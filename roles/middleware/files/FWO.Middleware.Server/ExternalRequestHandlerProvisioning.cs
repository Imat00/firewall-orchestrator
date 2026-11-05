using FWO.Api.Client.Queries;
using FWO.Data;
using FWO.Data.DeviceSpecificSettings;
using FWO.Data.Workflow;
using FWO.Logging;
using FWO.Services.Workflow;
using System.Text.Json;

namespace FWO.Middleware.Server
{
    /// <summary>
    /// Provisioning of a ticket along its provisioning work list, see <see cref="ProvisioningWorkList"/>.
    /// </summary>
    public partial class ExternalRequestHandler
    {
        private const string kProvisioningLogTitle = "Provisioning";

        /// <summary>A firewall write is handled by no external ticket system.</summary>
        private static readonly ExternalTicketSystem kNoExternalTicketSystem = new();

        /// <summary>
        /// Creates the external request for the next entry of the provisioning work list of a ticket: a request task
        /// handed to its external ticket system, or an implementation task written to the firewall. The requests of a
        /// ticket run one after another, so nothing is created while a request of the ticket is still being processed.
        /// The work list is built anew from the current tasks and settings each time, so a second call creates nothing
        /// twice and a finished entry, e.g. set to done by hand, is skipped.
        /// </summary>
        /// <param name="ticketId">Ticket to provision.</param>
        /// <param name="scopeReqTaskId">Request task in the request task scope; null in the ticket scope.</param>
        /// <param name="previousRequest">Request just finished, if the provisioning continues after it; a group change it
        /// made may let the next request wait some cycles, see <see cref="GetWaitCycles"/>.</param>
        /// <returns>true if a request was created or nothing is to be done; false if the provisioning failed.</returns>
        public async Task<bool> StartProvisioning(long ticketId, long? scopeReqTaskId = null, ExternalRequest? previousRequest = null)
        {
            try
            {
                WfTicket? ticket = await InitAndResolve(ticketId);
                if (ticket == null)
                {
                    Log.WriteWarning(kProvisioningLogTitle, $"Ticket {ticketId} not found.");
                    return false;
                }
                if (await HasUnfinishedRequest(ticket.Id))
                {
                    Log.WriteInfo(kProvisioningLogTitle, $"Ticket {ticket.Id} still has an external request being processed; the next one follows when it is finished.");
                    return true;
                }
                WfReqTask? scopeTask = null;
                if (scopeReqTaskId != null)
                {
                    scopeTask = ticket.Tasks.FirstOrDefault(task => task.Id == scopeReqTaskId);
                    if (scopeTask == null)
                    {
                        Log.WriteWarning(kProvisioningLogTitle, $"Request task {scopeReqTaskId} not found in ticket {ticket.Id}.");
                        return false;
                    }
                }
                ProvisioningWorkList workList = await wfHandler.BuildProvisioningWorkList(ticket, scopeTask);
                return await CreateRequestForNextEntry(ticket, workList, previousRequest);
            }
            catch (Exception exception)
            {
                Log.WriteError(kProvisioningLogTitle, $"Provisioning of ticket {ticketId} failed: ", exception);
                return false;
            }
        }

        private async Task<bool> CreateRequestForNextEntry(WfTicket ticket, ProvisioningWorkList workList, ExternalRequest? previousRequest)
        {
            ProvisioningWorkItem? next = workList.Next;
            if (next == null)
            {
                Log.WriteInfo(kProvisioningLogTitle, $"Nothing left to provision for ticket {ticket.Id}.");
                return true;
            }
            if (next.WaitsForPlanning)
            {
                Log.WriteInfo(kProvisioningLogTitle, $"Request task {next.ReqTask.TaskNumber} of ticket {ticket.Id} is still being planned; " +
                    "its implementation tasks are written once it is planned.");
                return true;
            }
            if (next.Way == ProvisioningWay.FirewallWrite && next.ImplTask != null)
            {
                int waitCycles = GetWaitCycles(next.ImplTask.TaskType, previousRequest, kNoExternalTicketSystem);
                await CreateFirewallWriteRequest(ticket, next.ReqTask, next.ImplTask, waitCycles);
            }
            else
            {
                await CreateExternalTicketSystemRequest(ticket, workList, next, previousRequest);
            }
            Log.WriteInfo(kProvisioningLogTitle, $"Created the external request for request task {next.ReqTask.TaskNumber}" +
                $"{(next.ImplTask == null ? "" : $", implementation task {next.ImplTask.TaskNumber}")} of ticket {ticket.Id}.");
            return true;
        }

        /// <summary>
        /// A request without finish date is still being processed; same criterion as the unique index uidx_ext_request_one_active_per_ticket.
        /// </summary>
        private async Task<bool> HasUnfinishedRequest(long ticketId)
        {
            List<ExternalRequest> unfinishedRequests = await ApiConnection.SendQueryAsync<List<ExternalRequest>>(ExtRequestQueries.getUnfinishedRequestsOfTicket, new { ticketId });
            return unfinishedRequests.Count > 0;
        }

        /// <summary>
        /// Creates the request writing an implementation task to the firewall. It references the implementation task,
        /// which the sender loads itself, so the request carries no ticket system and no content.
        /// </summary>
        private async Task CreateFirewallWriteRequest(WfTicket ticket, WfReqTask reqTask, WfImplTask implTask, int waitCycles)
        {
            Dictionary<string, List<int>> extQueryVariables = [];
            if (implTask.ManagementId is int managementId)
            {
                extQueryVariables[ExternalVarKeys.ManagementId] = [managementId];
            }
            var variables = new
            {
                ownerId = reqTask.Owners.FirstOrDefault()?.Owner.Id ?? ticket.Tasks.FirstOrDefault()?.Owners.FirstOrDefault()?.Owner.Id,
                ticketId = ticket.Id,
                taskNumber = reqTask.TaskNumber,
                extTicketSystem = "",
                extTaskType = implTask.TaskType,
                extTaskContent = "",
                extQueryVariables = JsonSerializer.Serialize(extQueryVariables),
                extRequestState = ExtStates.ExtReqInitialized.ToString(),
                waitCycles,
                implTaskId = implTask.Id
            };
            await ApiConnection.SendQueryAsync<ReturnIdWrapper>(ExtRequestQueries.addExtRequest, variables);
        }

        /// <summary>
        /// Hands a request task to Tufin SecureChange as configured for its management in the device specific settings
        /// (connection, ticket templates, response timeout and retries), together with the request tasks it is bundled with.
        /// </summary>
        /// <exception cref="InvalidOperationException">The management has no URL or no credential for Tufin SecureChange.</exception>
        private async Task CreateExternalTicketSystemRequest(WfTicket ticket, ProvisioningWorkList workList, ProvisioningWorkItem start, ExternalRequest? previousRequest)
        {
            actSystem = ExternalTicketSystem.FromSecureChangeSettings(await GetRequestSettings(start.ReqTask.ManagementId));
            if (string.IsNullOrWhiteSpace(actSystem.Url) || actSystem.CredentialId == null)
            {
                throw new InvalidOperationException($"The Tufin SecureChange connection of management {start.ReqTask.ManagementId} has no URL or no credential.");
            }
            await ApplySecureChangeTemplates(ticket, start.ReqTask);
            (List<WfReqTask> contentTasks, List<WfReqTask> handledTasks) = await BundleRequestTasks(workList, start);
            await CreateExtRequest(ticket, contentTasks, handledTasks, GetWaitCycles(start.ReqTask.TaskType, previousRequest, actSystem));
        }

        /// <summary>
        /// Bundles the open entries directly following the start entry into one request, as long as they are handed to
        /// the same external ticket system with the same connection, have the same task type and the same ticket templates.
        /// Only rule tasks are bundled. A rule change of the same connection on another gateway of the same management
        /// is merged into the task before (if the system bundles gateways); other tasks join as tasks of their own if
        /// bundling is switched on, up to the maximum number of tasks of the system. The first entry that does not fit
        /// ends the bundle; finished entries and implementation tasks left to a person do not interrupt it.
        /// </summary>
        /// <returns>The tasks forming the content of the request and all request tasks it handles.</returns>
        private async Task<(List<WfReqTask> ContentTasks, List<WfReqTask> HandledTasks)> BundleRequestTasks(ProvisioningWorkList workList, ProvisioningWorkItem start)
        {
            List<WfReqTask> contentTasks = [];
            List<WfReqTask> handledTasks = [start.ReqTask];
            WfReqTask currentTask = start.ReqTask;
            if (IsBundledTaskType(start.ReqTask.TaskType))
            {
                foreach (ProvisioningWorkItem further in OpenEntriesAfter(workList, start))
                {
                    if (!await CanBundle(further, start))
                    {
                        break;
                    }
                    if (actSystem.BundleGateways() && actSystem.TaskTypesToBundleGateways().Contains(start.ReqTask.TaskType)
                        && IsSameRuleOnDiffGw(currentTask, further.ReqTask))
                    {
                        currentTask.Elements.AddRange(further.ReqTask.GetRuleElements().ConvertAll(element => element.ToReqElement()));
                        handledTasks.Add(further.ReqTask);
                        continue;
                    }
                    // the current task and the further one must fit into the request
                    if (!UserConfig.ModRolloutBundleTasks || contentTasks.Count + 2 > actSystem.MaxBundledTasks())
                    {
                        break;
                    }
                    contentTasks.Add(currentTask);
                    currentTask = new(further.ReqTask);
                    handledTasks.Add(further.ReqTask);
                }
            }
            contentTasks.Add(currentTask);
            return (contentTasks, handledTasks);
        }

        /// <summary>
        /// Device specific settings in effect for a management, e.g. the response timeout and retries of its external
        /// requests.
        /// </summary>
        /// <param name="managementId">Management of the request task or implementation task.</param>
        public async Task<GlobalDeviceSpecificSettings> GetRequestSettings(int? managementId)
        {
            return await wfHandler.GetManagementSettings(managementId);
        }

        private static IEnumerable<ProvisioningWorkItem> OpenEntriesAfter(ProvisioningWorkList workList, ProvisioningWorkItem start)
        {
            return workList.Items.SkipWhile(item => item != start).Skip(1).Where(item => !item.IsFinished);
        }

        /// <summary>
        /// Whether a further entry joins the request of the start entry: one request goes to one SecureChange server with
        /// one login and one set of ticket templates.
        /// </summary>
        private async Task<bool> CanBundle(ProvisioningWorkItem further, ProvisioningWorkItem start)
        {
            if (further.Way != ProvisioningWay.ExternalTicketSystem || further.ExternalTicketSystem != start.ExternalTicketSystem
                || further.ReqTask.TaskType != start.ReqTask.TaskType
                || !scTemplates.HaveSameTemplates(further.ReqTask.ManagementId, start.ReqTask.ManagementId))
            {
                return false;
            }
            GlobalDeviceSpecificSettings furtherSettings = await GetRequestSettings(further.ReqTask.ManagementId);
            return furtherSettings.ScUrl == actSystem.Url && furtherSettings.ScCredential == actSystem.CredentialId;
        }

        private static bool IsBundledTaskType(string taskType)
        {
            return taskType == WfTaskType.access.ToString() || IsRuleChangeOrDelete(taskType);
        }
    }
}
