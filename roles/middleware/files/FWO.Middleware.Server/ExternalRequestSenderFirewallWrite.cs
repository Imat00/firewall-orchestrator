using FWO.Data;
using FWO.Data.DeviceSpecificSettings;
using FWO.Data.Workflow;
using FWO.ExternalSystems.FirewallWrite;
using FWO.Logging;

namespace FWO.Middleware.Server
{
    /// <summary>
    /// Sending of the external requests writing an implementation task to the firewall with the firewall write library.
    /// </summary>
    public partial class ExternalRequestSender
    {
        private const string kFirewallWriteLogTitle = "Firewall Write Request";

        /// <summary>
        /// Creates the request handler taking over the result of a firewall write (settable for unit testing only).
        /// </summary>
        internal Func<ExternalRequestHandler>? HandlerFactory { get; init; }

        /// <summary>
        /// A request writing an implementation task carries no ticket system; one whose implementation task was deleted
        /// meanwhile has lost its reference as well.
        /// </summary>
        private static bool IsFirewallWriteRequest(ExternalRequest request)
        {
            return request.ImplTaskId != null || string.IsNullOrWhiteSpace(request.ExtTicketSystem);
        }

        /// <summary>
        /// Writes the implementation task of an open request to the firewall: success sets the request to done, an error
        /// that may pass is retried after some cycles up to the maximum number of attempts, any other error rejects it.
        /// An implementation task already finished otherwise, e.g. set to done by hand, is not written again.
        /// </summary>
        private async Task HandleFirewallWriteRequest(ExternalRequest request)
        {
            if (request.ExtRequestState != ExtStates.ExtReqInitialized.ToString() && request.ExtRequestState != ExtStates.ExtReqFailed.ToString())
            {
                Log.WriteDebug(kFirewallWriteLogTitle, $"{RequestInfo(request)} is {request.ExtRequestState}; a firewall write has no state to poll.");
                return;
            }
            if (request.WaitCycles > 0)
            {
                await CountDownWaitCycle(request);
                return;
            }
            using ExternalRequestHandler extReqHandler = HandlerFactory?.Invoke() ?? new(userConfig, apiConnection);
            (WfImplTask? implTask, bool isFinished) = await extReqHandler.LoadImplTaskOfRequest(request);
            if (implTask == null)
            {
                await DiscardRequestWithoutImplTask(request, extReqHandler);
                return;
            }
            if (isFinished)
            {
                request.LastMessage = $"Implementation task {implTask.Id} is already finished, nothing written.";
                request.ExtRequestState = ExtStates.ExtReqDone.ToString();
            }
            else if (!await WriteImplTask(request, implTask, extReqHandler))
            {
                return;
            }
            await UpdateRequestCreation(request);
            await FollowUp(request, extReqHandler);
        }

        /// <summary>
        /// Writes the implementation task and sets the resulting request state. Returns false if the write is retried
        /// later, as configured for its management; the request is then already stored.
        /// </summary>
        private async Task<bool> WriteImplTask(ExternalRequest request, WfImplTask implTask, ExternalRequestHandler extReqHandler)
        {
            Log.WriteInfo(kFirewallWriteLogTitle, $"Writing implementation task {implTask.Id} of {RequestInfo(request)}");
            request.Attempts++;
            ModifyFirewallResult result = await modifyFirewall(implTask);
            request.LastMessage = DescribeResult(result);
            if (result.Success)
            {
                request.ExtRequestState = ExtStates.ExtReqDone.ToString();
                return true;
            }
            Log.WriteWarning(kFirewallWriteLogTitle, $"{RequestInfo(request)}, attempt {request.Attempts}: {request.LastMessage}");
            GlobalDeviceSpecificSettings settings = await extReqHandler.GetRequestSettings(implTask.ManagementId);
            if (result.IsRetryable && request.Attempts < settings.RequestMaxAttempts)
            {
                request.ExtRequestState = ExtStates.ExtReqFailed.ToString();
                request.WaitCycles = request.Attempts * settings.RequestCyclesBetweenAttempts;
                await UpdateRequestCreation(request);
                return false;
            }
            request.ExtRequestState = ExtStates.ExtReqRejected.ToString();
            return true;
        }

        /// <summary>
        /// Takes over the finished request into the ticket. The firewall write is done already, so a failure here is
        /// only logged and does not repeat the write.
        /// </summary>
        private static async Task FollowUp(ExternalRequest request, ExternalRequestHandler extReqHandler)
        {
            try
            {
                await extReqHandler.HandleStateChange(request);
            }
            catch (Exception exception)
            {
                Log.WriteError(kFirewallWriteLogTitle, $"{RequestInfo(request)} is {request.ExtRequestState}, but follow-up processing failed.", exception);
            }
        }

        /// <summary>
        /// Closes a request whose implementation task no longer exists and continues the provisioning of the ticket.
        /// </summary>
        private static async Task DiscardRequestWithoutImplTask(ExternalRequest request, ExternalRequestHandler extReqHandler)
        {
            Log.WriteWarning(kFirewallWriteLogTitle, $"{RequestInfo(request)}: implementation task {request.ImplTaskId} no longer exists, the request is discarded.");
            request.ExtRequestState = ExtStates.ExtReqDiscarded.ToString();
            await extReqHandler.PatchState(request);
            await extReqHandler.StartProvisioning(request.TicketId);
        }

        private static string DescribeResult(ModifyFirewallResult result)
        {
            string steps = string.Join("; ", result.Steps.Select(step => $"{step.Operation} {step.Target}: {step.StatusCode}"));
            return steps == "" ? result.Message : $"{result.Message} ({steps})";
        }
    }
}
