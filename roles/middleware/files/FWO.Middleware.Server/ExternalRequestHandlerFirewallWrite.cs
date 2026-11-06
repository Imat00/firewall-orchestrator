using FWO.Basics;
using FWO.Data;
using FWO.Data.Modelling;
using FWO.Data.Workflow;
using FWO.Logging;
using FWO.Services;
using FWO.Services.Modelling;

namespace FWO.Middleware.Server
{
    /// <summary>
    /// Handling of the external requests writing an implementation task to the firewall, see <see cref="ExternalRequest.ImplTaskId"/>.
    /// </summary>
    public partial class ExternalRequestHandler
    {
        private const string kFirewallWriteLogTitle = "Firewall Write Request";
        private const string kFirewallWriteRequester = "Firewall write";

        /// <summary>
        /// Loads the implementation task a firewall write request references, with its elements.
        /// </summary>
        /// <param name="request">Request with implementation task.</param>
        /// <returns>The implementation task, null if it no longer exists; and whether it already reached the end state of
        /// the implementation phase, e.g. set to done by hand, so it must not be written again.</returns>
        public async Task<(WfImplTask? ImplTask, bool IsFinished)> LoadImplTaskOfRequest(ExternalRequest request)
        {
            WfTicket? ticket = await InitAndResolve(request.TicketId);
            (_, WfImplTask? implTask) = FindImplTask(ticket, request.ImplTaskId);
            if (implTask == null)
            {
                return (null, false);
            }
            return (implTask, implTask.StateId >= wfHandler.StateMatrix(implTask.TaskType).ImplementationLowestEndState);
        }

        /// <summary>
        /// Takes over the result of a finished firewall write: the implementation task gets the internal state of the
        /// request and a history entry, unless it already reached the end state of the implementation phase otherwise.
        /// A rejected write rejects the following request tasks as before; otherwise the next entry of the provisioning
        /// work list of the ticket follows. Nothing follows the write of a single implementation task started by its own
        /// action, and a rejection of it leaves the other tasks to the person who started it.
        /// </summary>
        private async Task HandleImplTaskStateChange(WfTicket ticket, ExternalRequest request)
        {
            bool rejected = request.ExtRequestState == ExtStates.ExtReqRejected.ToString();
            if (!rejected && request.ExtRequestState != ExtStates.ExtReqDone.ToString())
            {
                // a firewall write is finished at once, there is no intermediate state to show
                return;
            }
            (WfReqTask? reqTask, WfImplTask? implTask) = FindImplTask(ticket, request.ImplTaskId);
            if (reqTask != null && implTask != null)
            {
                await CompleteImplTask(ticket, reqTask, implTask, request, rejected);
            }
            else
            {
                Log.WriteWarning(kFirewallWriteLogTitle, $"Implementation task {request.ImplTaskId} of external request {request.Id} not found in ticket {ticket.Id}.");
            }
            await Acknowledge(request);
            if (rejected && !IsSingleImplTaskRequest(request))
            {
                await RejectFollowingTasks(ticket, request.TaskNumber);
                Log.WriteInfo($"External Request {request.Id} rejected", $"Reject Following Tasks for internal ticket {ticket.Id}");
            }
            else if (!rejected)
            {
                await StartProvisioning(ticket.Id, null, request);
            }
        }

        private async Task CompleteImplTask(WfTicket ticket, WfReqTask reqTask, WfImplTask implTask, ExternalRequest request, bool rejected)
        {
            if (implTask.StateId >= wfHandler.StateMatrix(implTask.TaskType).ImplementationLowestEndState)
            {
                Log.WriteInfo(kFirewallWriteLogTitle, $"Implementation task {implTask.Id} is already finished, its state {implTask.StateId} is kept.");
                return;
            }
            int? internalStateId = extStateHandler?.GetInternalStateId(request.ExtRequestState);
            wfHandler.SetTicketEnv(ticket);
            wfHandler.SetReqTaskEnv(reqTask);
            if (internalStateId == null || !wfHandler.TrySetImplTaskEnv(implTask))
            {
                Log.WriteError(kFirewallWriteLogTitle, $"Implementation task {implTask.Id}: no internal state for {request.ExtRequestState} or no state matrix for {implTask.TaskType}.");
                return;
            }
            wfHandler.ActImplTask.Stop ??= DateTime.Now;
            await wfHandler.PromoteImplTask(new WfStatefulObject { StateId = internalStateId.Value });
            if (rejected)
            {
                await LogImplTaskChange(reqTask, implTask, ModellingTypes.ChangeType.Reject, request.LastProcessingResponse ?? request.LastCreationResponse ?? "");
            }
            else
            {
                await LogImplTaskChange(reqTask, implTask, ModellingTypes.ChangeType.Implement);
            }
        }

        private static (WfReqTask? ReqTask, WfImplTask? ImplTask) FindImplTask(WfTicket? ticket, long? implTaskId)
        {
            if (ticket == null || implTaskId == null)
            {
                return (null, null);
            }
            foreach (WfReqTask reqTask in ticket.Tasks)
            {
                WfImplTask? implTask = reqTask.ImplementationTasks.FirstOrDefault(task => task.Id == implTaskId);
                if (implTask != null)
                {
                    return (reqTask, implTask);
                }
            }
            return (null, null);
        }

        /// <summary>
        /// Writes the history entry of an implementation task to the object of its request task, naming the management
        /// written to.
        /// </summary>
        private async Task LogImplTaskChange(WfReqTask reqTask, WfImplTask implTask, ModellingTypes.ChangeType changeType, string? comment = null)
        {
            (long objId, ModellingTypes.ModObjectType objType) = GetObject(reqTask);
            string managementName = wfHandler.Managements.FirstOrDefault(management => management.Id == implTask.ManagementId)?.Name
                ?? $"management {implTask.ManagementId}";
            await ModellingHandlerBase.LogChange(new LogChangeRequest
            {
                ChangeType = changeType,
                ObjectType = objType,
                ObjectId = objId,
                Text = $"{ConstructLogMessageText(changeType)} {implTask.Title} on {managementName}{(comment != null ? ", " + comment : "")}",
                ApiConnection = ApiConnection,
                UserConfig = UserConfig,
                ApplicationId = reqTask.Owners.FirstOrDefault()?.Owner.Id,
                DisplayMessageInUi = DefaultInit.DoNothing,
                Requester = kFirewallWriteRequester
            });
        }
    }
}
