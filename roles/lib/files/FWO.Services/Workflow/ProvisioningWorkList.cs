using FWO.Data.DeviceSpecificSettings;
using FWO.Data.Workflow;
using FWO.Logging;

namespace FWO.Services.Workflow
{
    /// <summary>
    /// Way an entry of the provisioning work list is implemented.
    /// </summary>
    public enum ProvisioningWay
    {
        /// <summary>The whole request task is handed to the external ticket system of its management, e.g. Tufin SecureChange.</summary>
        ExternalTicketSystem,

        /// <summary>The implementation task is written directly to the firewall.</summary>
        FirewallWrite
    }

    /// <summary>
    /// One entry of the provisioning work list: a request task handed to an external ticket system, or an
    /// implementation task written to the firewall.
    /// </summary>
    /// <param name="Way">How the entry is implemented.</param>
    /// <param name="ReqTask">Request task of the entry.</param>
    /// <param name="ImplTask">Implementation task written to the firewall; null for an external ticket system.</param>
    /// <param name="ExternalTicketSystemId">External ticket system the request task is handed to; null for a firewall write.</param>
    /// <param name="IsFinished">Whether the task has reached the end state of the implementation phase, e.g. implemented,
    /// set to done by hand or rejected; a finished entry is not provisioned again.</param>
    public sealed record ProvisioningWorkItem(ProvisioningWay Way, WfReqTask ReqTask, WfImplTask? ImplTask, int? ExternalTicketSystemId, bool IsFinished);

    /// <summary>
    /// A request or implementation task nobody implements, as its implementation mode hands it to an external ticket
    /// system that does not take it over; reported with an alert.
    /// </summary>
    /// <param name="ReqTask">Request task concerned.</param>
    /// <param name="ImplTask">Implementation task concerned; null if the whole request task is concerned.</param>
    /// <param name="ManagementId">Management whose settings cause the gap.</param>
    /// <param name="Reason">Why the task is not provisioned.</param>
    public sealed record ProvisioningGap(WfReqTask ReqTask, WfImplTask? ImplTask, int ManagementId, string Reason);

    /// <summary>
    /// Effective device specific settings and states the provisioning work list is built from.
    /// </summary>
    public interface IProvisioningModeSource
    {
        /// <summary>Implementation mode of a management for rules or for objects.</summary>
        ProvisioningImplementationMode GetImplementationMode(int managementId, bool isRuleTask);

        /// <summary>External ticket system selected for a management; null if none is selected.</summary>
        int? GetExternalTicketSystemId(int managementId);

        /// <summary>Lowest end state of the implementation phase for a task type.</summary>
        int GetImplementationEndState(string taskType);
    }

    /// <summary>
    /// Ordered list of what a ticket provisions, built anew from the current tasks and settings each time, so it is
    /// never stored: a request task whose management hands it to an external ticket system as a whole, otherwise each
    /// implementation task whose target management is Automatic. Manual targets are left to a person and do not block
    /// the entries behind them. The entries are ordered by request task number, then implementation task number; they
    /// are provisioned one after another, the next one is the first entry not finished.
    /// Rules are not provisioned by firewall writes yet, only objects.
    /// </summary>
    public sealed class ProvisioningWorkList
    {
        private const string kLogTitle = "Provisioning";

        /// <summary>Entries in the order of provisioning, finished ones included.</summary>
        public List<ProvisioningWorkItem> Items { get; } = [];

        /// <summary>Tasks nobody implements, to be reported with an alert.</summary>
        public List<ProvisioningGap> Gaps { get; } = [];

        /// <summary>The entry to provision next; null if every entry is finished.</summary>
        public ProvisioningWorkItem? Next => Items.FirstOrDefault(item => !item.IsFinished);

        /// <summary>
        /// Builds the work list of the given request tasks with their implementation tasks.
        /// </summary>
        /// <param name="reqTasks">Request tasks in the scope of the provisioning.</param>
        /// <param name="modes">Effective settings and states.</param>
        public static ProvisioningWorkList Build(IEnumerable<WfReqTask> reqTasks, IProvisioningModeSource modes)
        {
            ProvisioningWorkList workList = new();
            foreach (WfReqTask reqTask in reqTasks.OrderBy(task => task.TaskNumber))
            {
                workList.AddRequestTask(reqTask, modes);
            }
            return workList;
        }

        private void AddRequestTask(WfReqTask reqTask, IProvisioningModeSource modes)
        {
            bool isRuleTask = WfHandler.IsRuleTaskType(reqTask.TaskType);
            if (!isRuleTask && !WfObjectTaskHelper.IsManagementObjectTask(reqTask.TaskType))
            {
                Log.WriteDebug(kLogTitle, $"Request task {reqTask.Id} ({reqTask.TaskType}) has no target to provision.");
                return;
            }
            if (TryAddExternalTicketSystemEntry(reqTask, isRuleTask, modes))
            {
                return;
            }
            foreach (WfImplTask implTask in reqTask.ImplementationTasks.OrderBy(task => task.TaskNumber))
            {
                AddImplTask(reqTask, implTask, isRuleTask, modes);
            }
        }

        /// <summary>
        /// Adds the request task as a whole if the implementation mode of its management is external ticket system.
        /// Without a selected system the task is reported instead. Returns whether the request task is handled this way.
        /// </summary>
        private bool TryAddExternalTicketSystemEntry(WfReqTask reqTask, bool isRuleTask, IProvisioningModeSource modes)
        {
            if (reqTask.ManagementId is not int managementId || managementId <= 0
                || modes.GetImplementationMode(managementId, isRuleTask) != ProvisioningImplementationMode.RerouteToExternalTicketSystem)
            {
                return false;
            }
            bool isFinished = reqTask.StateId >= modes.GetImplementationEndState(reqTask.TaskType);
            int? systemId = modes.GetExternalTicketSystemId(managementId);
            if (systemId == null)
            {
                if (!isFinished)
                {
                    Gaps.Add(new(reqTask, null, managementId, NoExternalTicketSystemSelectedReason(managementId)));
                }
                return true;
            }
            Items.Add(new(ProvisioningWay.ExternalTicketSystem, reqTask, null, systemId, isFinished));
            return true;
        }

        private void AddImplTask(WfReqTask reqTask, WfImplTask implTask, bool isRuleTask, IProvisioningModeSource modes)
        {
            if (isRuleTask)
            {
                Log.WriteInfo(kLogTitle, $"Implementation task {implTask.Id} of request task {reqTask.Id} is not provisioned: rules are not written to firewalls yet.");
                return;
            }
            if (implTask.ManagementId is not int managementId || managementId <= 0)
            {
                Log.WriteDebug(kLogTitle, $"Implementation task {implTask.Id} of request task {reqTask.Id} has no target management to provision.");
                return;
            }
            bool isFinished = implTask.StateId >= modes.GetImplementationEndState(implTask.TaskType);
            switch (modes.GetImplementationMode(managementId, false))
            {
                case ProvisioningImplementationMode.Automatic:
                    Items.Add(new(ProvisioningWay.FirewallWrite, reqTask, implTask, null, isFinished));
                    break;
                case ProvisioningImplementationMode.RerouteToExternalTicketSystem when !isFinished:
                    Gaps.Add(new(reqTask, implTask, managementId, $"target management {managementId} is set to external ticket system, " +
                        "but the implementation task can only be written to the firewall or implemented manually."));
                    break;
                default:
                    Log.WriteDebug(kLogTitle, $"Implementation task {implTask.Id} of request task {reqTask.Id} is implemented manually.");
                    break;
            }
        }

        private static string NoExternalTicketSystemSelectedReason(int managementId)
        {
            return $"management {managementId} is set to external ticket system, but no external ticket system is selected for it.";
        }
    }
}
