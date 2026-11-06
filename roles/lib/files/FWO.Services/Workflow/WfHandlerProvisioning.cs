using FWO.Data;
using FWO.Data.DeviceSpecificSettings;
using FWO.Data.Workflow;
using FWO.Logging;

namespace FWO.Services.Workflow
{
    /// <summary>
    /// Builds the provisioning work list of a ticket from the current implementation modes of the device specific
    /// settings, see <see cref="ProvisioningWorkList"/>.
    /// </summary>
    public partial class WfHandler
    {
        private const string kProvisioningLogTitle = "Provisioning";

        /// <summary>
        /// Builds the provisioning work list of a ticket with the device specific settings in effect now. Tasks nobody
        /// implements are reported with an alert. If the settings cannot be resolved, nothing is provisioned, as the
        /// defaults would write to firewalls that are left to an external ticket system or to a person.
        /// </summary>
        /// <param name="ticket">Ticket with its request and implementation tasks.</param>
        /// <param name="scopeTask">Request task in the request task scope: only this task, a bundled task together with
        /// its bundle once every member has reached the state of this task. Null in the ticket scope, where every
        /// request task of the ticket is provisioned.</param>
        /// <param name="release">Scope released by a button, whose implementation tasks on Manual targets are written as
        /// well; null without release.</param>
        /// <exception cref="InvalidOperationException">The device specific settings could not be resolved.</exception>
        public async Task<ProvisioningWorkList> BuildProvisioningWorkList(WfTicket ticket, WfReqTask? scopeTask = null, ProvisioningRelease? release = null)
        {
            SetTicketEnv(ticket);
            List<WfReqTask> ticketTasks = [.. ActTicket.Tasks];
            List<WfReqTask> reqTasks = scopeTask == null ? ticketTasks : TasksOfRequestTaskScope(ticketTasks, scopeTask);
            ProvisioningWorkList workList = await BuildWorkList(ticket, reqTasks, ReleasedReqTaskIds(ticketTasks, release));
            foreach (ProvisioningGap gap in workList.Gaps)
            {
                await ReportProvisioningGap(gap);
            }
            return workList;
        }

        /// <summary>
        /// The work list entry of a single implementation task, with the device specific settings in effect now; its
        /// bundle and the other tasks of the ticket do not matter. A gap of the task is reported with an alert.
        /// </summary>
        /// <param name="ticket">Ticket with its request and implementation tasks.</param>
        /// <param name="implTaskId">Implementation task to write.</param>
        /// <param name="byButton">True if a person pressed a button: the task is written on a Manual target as well.</param>
        /// <returns>The entry; null if the task is not in the ticket or not written by FWO.</returns>
        /// <exception cref="InvalidOperationException">The device specific settings could not be resolved.</exception>
        public async Task<ProvisioningWorkItem?> BuildProvisioningOfImplTask(WfTicket ticket, long implTaskId, bool byButton = false)
        {
            SetTicketEnv(ticket);
            WfReqTask? reqTask = ActTicket.Tasks.FirstOrDefault(task => task.ImplementationTasks.Any(implTask => implTask.Id == implTaskId));
            if (reqTask == null)
            {
                return null;
            }
            List<WfReqTask> reqTasks = [reqTask];
            HashSet<long> releasedReqTaskIds = byButton ? [reqTask.Id] : [];
            ProvisioningWorkList workList = await BuildWorkList(ticket, reqTasks, releasedReqTaskIds);
            foreach (ProvisioningGap gap in workList.Gaps.Where(gap => gap.ImplTask?.Id == implTaskId))
            {
                await ReportProvisioningGap(gap);
            }
            return workList.Items.FirstOrDefault(item => item.ImplTask?.Id == implTaskId);
        }

        private async Task<ProvisioningWorkList> BuildWorkList(WfTicket ticket, List<WfReqTask> reqTasks, HashSet<long> releasedReqTaskIds)
        {
            ResetAutoPlanCache();
            await EnsureAutoPlanSettings(kNoIds, ProvisioningManagementIds(reqTasks));
            if (AutoPlanSettingsOverride == null && (autoPlanResolveFailed || !autoPlanCache.ContainsKey((DeviceSpecificScopeType.Global, null))))
            {
                throw new InvalidOperationException($"The device specific settings could not be resolved; nothing of ticket {ticket.Id} is provisioned.");
            }
            return ProvisioningWorkList.Build(reqTasks, new ResolvedProvisioningModes(this), releasedReqTaskIds);
        }

        /// <summary>
        /// Device specific settings in effect for a management, e.g. the response timeout and retries of its external
        /// requests. Without management, or if the settings cannot be resolved, the global settings or the defaults apply.
        /// </summary>
        /// <param name="managementId">Management of the request task or implementation task.</param>
        public async Task<GlobalDeviceSpecificSettings> GetManagementSettings(int? managementId)
        {
            List<int> managementIds = managementId is int id && id > 0 ? [id] : kNoIds;
            await EnsureAutoPlanSettings(kNoIds, managementIds);
            return GetResolvedSettings(DeviceSpecificScopeType.Management, managementId);
        }

        /// <summary>
        /// The request tasks a release by a button covers: the whole ticket, or the request task it was pressed on with
        /// its bundle. Empty without release or if the request task no longer exists.
        /// </summary>
        private HashSet<long> ReleasedReqTaskIds(List<WfReqTask> ticketTasks, ProvisioningRelease? release)
        {
            if (release == null)
            {
                return [];
            }
            if (release.ScopeTaskNumber == null)
            {
                return [.. ticketTasks.Select(task => task.Id)];
            }
            WfReqTask? scopeTask = ticketTasks.FirstOrDefault(task => task.TaskNumber == release.ScopeTaskNumber);
            return scopeTask == null ? [] : [.. TasksOfRequestTaskScope(ticketTasks, scopeTask).Select(task => task.Id)];
        }

        private static List<int> ProvisioningManagementIds(List<WfReqTask> reqTasks)
        {
            IEnumerable<int?> reqTaskManagementIds = reqTasks.Select(task => task.ManagementId);
            IEnumerable<int?> implTaskManagementIds = reqTasks.SelectMany(task => task.ImplementationTasks).Select(implTask => implTask.ManagementId);
            return [.. reqTaskManagementIds.Concat(implTaskManagementIds).OfType<int>().Where(id => id > 0).Distinct()];
        }

        /// <summary>
        /// Logs a task nobody implements and raises a workflow alert for it.
        /// </summary>
        private async Task ReportProvisioningGap(ProvisioningGap gap)
        {
            string implTaskText = gap.ImplTask == null ? "" : $", implementation task {gap.ImplTask.Id}";
            string description = $"Ticket {gap.ReqTask.TicketId}, request task {gap.ReqTask.Id}{implTaskText}: not provisioned, {gap.Reason}";
            Log.WriteWarning(kProvisioningLogTitle, description);
            if (apiConnection == null)
            {
                return;
            }
            await AlertHelper.SetAlert(apiConnection, kProvisioningLogTitle, description, kAlertSource, AlertCode.WorkflowAlert,
                new AlertHelper.AdditionalAlertData { MgmtId = gap.ManagementId, CompareDesc = true });
        }

        /// <summary>
        /// Gives the work list the settings resolved for the current run and the end states of the state matrices.
        /// </summary>
        private sealed class ResolvedProvisioningModes(WfHandler handler) : IProvisioningModeSource
        {
            public ProvisioningImplementationMode GetImplementationMode(int managementId, bool isRuleTask)
            {
                return handler.GetResolvedSettings(DeviceSpecificScopeType.Management, managementId).GetImplementationMode(isRuleTask);
            }

            public ExternalTicketSystemKind GetExternalTicketSystem(int managementId)
            {
                return handler.GetResolvedSettings(DeviceSpecificScopeType.Management, managementId).RerouteToExternalTicketSystem;
            }

            public int GetImplementationEndState(string taskType)
            {
                return handler.StateMatrix(taskType).ImplementationLowestEndState;
            }

            public int? GetPlanningEndState(string taskType)
            {
                return handler.StateMatrix(taskType).PlanningLowestEndState;
            }
        }
    }
}
