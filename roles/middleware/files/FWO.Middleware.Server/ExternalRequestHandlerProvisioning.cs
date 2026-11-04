using FWO.Api.Client.Queries;
using FWO.Data;
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
                await CreateExternalTicketSystemRequest(ticket, next.ReqTask, next.ExternalTicketSystemId, previousRequest);
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
        /// Hands a request task to the external ticket system selected for its management in the device specific settings.
        /// </summary>
        private async Task CreateExternalTicketSystemRequest(WfTicket ticket, WfReqTask reqTask, int? externalTicketSystemId, ExternalRequest? previousRequest)
        {
            List<ExternalTicketSystem> extTicketSystems = JsonSerializer.Deserialize<List<ExternalTicketSystem>>(UserConfig.ExtTicketSystems) ?? [];
            actSystem = extTicketSystems.FirstOrDefault(system => system.Id == externalTicketSystemId)
                ?? throw new InvalidOperationException($"No matching external ticket system found for id {externalTicketSystemId}.");
            actInternalWork = false;
            await ApplySecureChangeTemplates(ticket, reqTask);
            // a request task handed over alone is both the content and the only handled task of the request
            List<WfReqTask> handedOverTasks = [reqTask];
            await CreateExtRequest(ticket, handedOverTasks, handedOverTasks, GetWaitCycles(reqTask.TaskType, previousRequest, actSystem));
        }
    }
}
