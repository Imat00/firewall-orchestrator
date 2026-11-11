using FWO.Api.Client;
using FWO.Api.Client.Queries;
using FWO.Config.Api;
using FWO.Data;
using FWO.Data.DeviceSpecificSettings;
using FWO.Data.Workflow;

namespace FWO.ExternalSystems.FirewallWrite
{
    /// <summary>Loads the target of an implementation task.</summary>
    public interface IFirewallWriteDataSource
    {
        /// <summary>Returns management, gateway and the device specific settings in effect for the task's target.</summary>
        /// <exception cref="FirewallWriteException">Thrown if the task has no management or the management or gateway does not exist.</exception>
        Task<FirewallWriteContext> LoadContext(WfImplTask implTask);
    }

    /// <summary>
    /// Loads the target through the API: the management with its export credentials and gateways, and the device
    /// specific settings resolved along Global, device type, management and gateway.
    /// </summary>
    public sealed class ApiFirewallWriteDataSource(ApiConnection apiConnection) : IFirewallWriteDataSource
    {
        /// <summary>
        /// The management of the gateway of a task stored without management, e.g. a rule task created before the
        /// management was stored with it.
        /// </summary>
        private async Task<int> ManagementOfGateway(WfImplTask implTask)
        {
            if (implTask.DeviceId is not int gatewayId)
            {
                throw new FirewallWriteException(FirewallWriteErrorKind.InvalidTaskData, $"Implementation task {implTask.Id} has no management.");
            }
            List<Device> gateways = await apiConnection.SendQueryAsync<List<Device>>(DeviceQueries.getRulePositioningTarget, new { gatewayId });
            int managementId = gateways.FirstOrDefault()?.Management?.Id ?? 0;
            if (managementId <= 0)
            {
                throw new FirewallWriteException(FirewallWriteErrorKind.InvalidTaskData,
                    $"Implementation task {implTask.Id} has no management, and its gateway {gatewayId} has none either.");
            }
            return managementId;
        }

        /// <inheritdoc/>
        public async Task<FirewallWriteContext> LoadContext(WfImplTask implTask)
        {
            ArgumentNullException.ThrowIfNull(implTask);
            int managementId = implTask.ManagementId ?? await ManagementOfGateway(implTask);

            List<Management> managements = await apiConnection.SendQueryAsync<List<Management>>(DeviceQueries.getManagementById, new { id = managementId });
            Management management = managements.FirstOrDefault()
                ?? throw new FirewallWriteException(FirewallWriteErrorKind.InvalidTaskData,
                    $"Management {managementId} of implementation task {implTask.Id} not found.");
            Device? gateway = null;
            if (implTask.DeviceId != null)
            {
                gateway = management.Devices.FirstOrDefault(device => device.Id == implTask.DeviceId)
                    ?? throw new FirewallWriteException(FirewallWriteErrorKind.InvalidTaskData,
                        $"Gateway {implTask.DeviceId} of implementation task {implTask.Id} does not belong to management {management.Id}.");
            }

            DeviceSpecificScopePath path = gateway == null
                ? DeviceSpecificScopePath.ForManagement(management)
                : DeviceSpecificScopePath.ForGateway(management, gateway);
            List<DeviceSpecificScopePath> paths = [path];
            List<GlobalDeviceSpecificSettings> settings = await new DeviceSpecificSettingsManager(apiConnection).LoadEffectiveSettingsAsync(paths);
            FirewallGatewayRulebases rulebases = gateway == null ? FirewallGatewayRulebases.None : await LoadRulebases(gateway);
            Dictionary<string, string> ruleLayerUids = await LoadRuleLayers(implTask, management);
            return new FirewallWriteContext
            {
                Management = management,
                Gateway = gateway,
                Settings = settings[0],
                Rulebases = rulebases,
                RuleLayerUids = ruleLayerUids
            };
        }

        /// <summary>
        /// Loads the layers of the existing rules a rule_modify or rule_delete task names from the imported configuration,
        /// only for vendors that address rules in their layer; other tasks name no existing rule.
        /// </summary>
        private async Task<Dictionary<string, string>> LoadRuleLayers(WfImplTask implTask, Management management)
        {
            FirewallVendor vendor = FirewallVendors.FromDeviceTypeId(management.DeviceType.Id);
            if (!WfTaskBase.IsRuleChangeOrDelete(implTask.TaskType) || vendor == FirewallVendor.Unsupported || !FirewallVendorProfile.For(vendor).AddressesRulesInImportedLayer)
            {
                return [];
            }
            int managementId = management.Id;
            List<string> ruleUids = implTask.GetRuleUids();
            if (ruleUids.Count == 0)
            {
                return [];
            }
            FirewallRuleLayerQueryResult result = await apiConnection.SendQueryAsync<FirewallRuleLayerQueryResult>(RuleQueries.getRuleLayers,
                new { managementId, ruleUids, ruleLimit = FirewallRuleLayers.kMaxRules + 1, linkLimit = FirewallRuleLayers.kMaxSectionLinks + 1 });
            return FirewallRuleLayers.Resolve(result);
        }

        /// <summary>
        /// Loads the package name and the current rulebase links of the gateway; the management query leaves both out. The
        /// package name is copied to the gateway, where it takes precedence over the package derived from the links.
        /// </summary>
        private async Task<FirewallGatewayRulebases> LoadRulebases(Device gateway)
        {
            List<Device> devices = await apiConnection.SendQueryAsync<List<Device>>(DeviceQueries.getGatewayRulebaseLinks, new { gwId = gateway.Id });
            Device? linked = devices.FirstOrDefault();
            if (linked == null)
            {
                return FirewallGatewayRulebases.None;
            }
            gateway.Package = linked.Package;
            return FirewallGatewayRulebases.FromLinks(linked.RulebaseLinks);
        }
    }
}
