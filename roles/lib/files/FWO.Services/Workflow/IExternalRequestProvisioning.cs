using FWO.Api.Client;
using FWO.Config.Api;

namespace FWO.Services.Workflow
{
    /// <summary>
    /// Starts the provisioning of a ticket through external requests. Implemented by the middleware server, which
    /// processes the external requests; the state action "Create external requests" only runs there.
    /// </summary>
    public interface IExternalRequestProvisioning
    {
        /// <summary>
        /// Creates the external request for the next entry of the provisioning work list of a ticket, unless a request
        /// of the ticket is still being processed or nothing is left to provision.
        /// </summary>
        /// <param name="apiConnection">Api connection of the executing state action.</param>
        /// <param name="userConfig">Configuration of the executing state action.</param>
        /// <param name="ticketId">Ticket to provision.</param>
        /// <param name="scopeReqTaskId">Request task in the request task scope; null in the ticket scope.</param>
        /// <param name="byButton">True if a person pressed a button: implementation tasks on Manual targets in the scope
        /// are written as well, see <see cref="ProvisioningRelease"/>.</param>
        /// <returns>false if the provisioning failed.</returns>
        Task<bool> StartProvisioning(ApiConnection apiConnection, UserConfig userConfig, long ticketId, long? scopeReqTaskId, bool byButton = false);
    }
}
