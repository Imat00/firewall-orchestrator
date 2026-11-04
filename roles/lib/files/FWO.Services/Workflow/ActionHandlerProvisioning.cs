using FWO.Data.Workflow;
using FWO.Logging;

namespace FWO.Services.Workflow;

/// <summary>State action "Create external requests" of the workflow action handler.</summary>
public partial class ActionHandler
{
    private const string kCreateExtRequestsLogTitle = "Create External Requests";

    /// <summary>
    /// Replaces the provisioning registered in the middleware server; used by tests.
    /// </summary>
    internal IExternalRequestProvisioning? ExternalRequestProvisioningOverride { get; set; }

    /// <summary>
    /// Starts the provisioning of the ticket: creates the external request for the next entry of its provisioning
    /// work list, i.e. hands a request task to its external ticket system or writes an implementation task to the
    /// firewall. In the request task scope only this task, a bundled task together with its bundle, is provisioned.
    /// Runs in the middleware server only, which processes the external requests.
    /// </summary>
    /// <param name="action">The executed action.</param>
    /// <param name="statefulObject">Ticket or request task the action is attached to.</param>
    /// <param name="scope">Scope of the stateful object.</param>
    public async Task CreateExtRequests(WfStateAction action, WfStatefulObject statefulObject, WfObjectScopes scope)
    {
        WfTicket? ticket = GetTicketForBundling(statefulObject, scope);
        if (ticket == null)
        {
            Log.WriteWarning(kCreateExtRequestsLogTitle, $"Action '{action.Name}' found no ticket request tasks.");
            return;
        }
        IExternalRequestProvisioning? provisioning = ExternalRequestProvisioningOverride
            ?? (useInMwServer ? ServiceProvider.Services?.GetService(typeof(IExternalRequestProvisioning)) as IExternalRequestProvisioning : null);
        if (provisioning == null)
        {
            Log.WriteWarning(kCreateExtRequestsLogTitle, $"Action '{action.Name}' is only executed in the middleware server; ticket {ticket.Id} is not provisioned.");
            return;
        }
        long? scopeReqTaskId = scope == WfObjectScopes.RequestTask && statefulObject is WfReqTask reqTask ? reqTask.Id : null;
        if (!await provisioning.StartProvisioning(apiConnection, wfHandler.userConfig, ticket.Id, scopeReqTaskId))
        {
            Log.WriteWarning(kCreateExtRequestsLogTitle, $"Action '{action.Name}' could not start the provisioning of ticket {ticket.Id}.");
        }
    }
}
