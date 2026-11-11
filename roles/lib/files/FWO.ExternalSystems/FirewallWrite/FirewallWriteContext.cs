using FWO.Data;
using FWO.Data.DeviceSpecificSettings;

namespace FWO.ExternalSystems.FirewallWrite
{
    /// <summary>
    /// Target of a firewall write: the management with its export credentials, the gateway if the task names one, and the
    /// device specific settings in effect for the most specific of both.
    /// </summary>
    public sealed class FirewallWriteContext
    {
        public required Management Management { get; init; }

        public Device? Gateway { get; init; }

        /// <summary>
        /// Gateways a new rule is installed on when the install-on setting is gateways in path; empty means the gateway of
        /// the task.
        /// </summary>
        public List<Device> InstallOnGateways { get; init; } = [];

        /// <summary>The gateways a new rule is installed on: <see cref="InstallOnGateways"/>, else the gateway of the task.</summary>
        public List<Device> EffectiveInstallOnGateways => InstallOnGateways.Count > 0 || Gateway == null ? InstallOnGateways : [Gateway];

        public required GlobalDeviceSpecificSettings Settings { get; init; }

        /// <summary>Policy package and first access layer of the gateway from its imported rulebase links.</summary>
        public FirewallGatewayRulebases Rulebases { get; init; } = FirewallGatewayRulebases.None;

        /// <summary>Layer uid of each existing rule of the task by rule uid, from the imported configuration.</summary>
        public IReadOnlyDictionary<string, string> RuleLayerUids { get; init; } = new Dictionary<string, string>();

        public int DeviceTypeId => Management.DeviceType.Id;

        public FirewallVendor Vendor => FirewallVendors.FromDeviceTypeId(DeviceTypeId);

        /// <summary>
        /// Returns the endpoint setting of an operation: the configured value, or the default shipped for the device type.
        /// Empty if neither exists, i.e. the device type does not support the operation.
        /// </summary>
        public string GetEndpoint(FirewallWriteOperation operation)
        {
            string configured = Settings.FwWriteEndpoints.GetValueOrDefault(operation, "");
            return string.IsNullOrWhiteSpace(configured)
                ? FirewallWriteDefaults.GetEndpoint(DeviceTypeId, operation) ?? ""
                : configured;
        }

        /// <summary>Returns the body template of an operation: the configured value, or the default shipped for the device type.</summary>
        public string GetTemplate(FirewallWriteOperation operation)
        {
            string configured = Settings.FwWriteTemplates.GetValueOrDefault(operation, "");
            return string.IsNullOrWhiteSpace(configured)
                ? FirewallWriteDefaults.GetTemplate(DeviceTypeId, operation) ?? ""
                : configured;
        }
    }
}
