namespace Caisson.Domain.NetworkConfig.Preflight;

/// <summary>
/// The stable, machine-readable issue codes surfaced by <see cref="PreflightValidator"/> (story #170,
/// NFR1: "stable identifiers"). Codes are the automation/UI contract — they never change once shipped and
/// are documented in <c>docs/adding-a-validation-rule.md</c>. Grouped by stage: <c>schema.*</c> (M1 schema
/// bounds), <c>semantic.*</c> (rack-scoped uniqueness + topology resolution), <c>safety.*</c> (non-blocking
/// guardrails).
/// </summary>
public static class PreflightCodes
{
    // -- schema.* — M1 desired-state schema bounds, sourced from NetworkIntentValidator/DesiredStateSchema.
    /// <summary>VLAN id outside the schema's [MinVlan, MaxVlan] range.</summary>
    public const string VlanIdRange = "schema.vlanIdRange";

    /// <summary>VLAN name is missing/blank.</summary>
    public const string VlanNameRequired = "schema.vlanNameRequired";

    /// <summary>VLAN name exceeds the schema length bound.</summary>
    public const string VlanNameLength = "schema.vlanNameLength";

    /// <summary>VLAN description exceeds the schema length bound.</summary>
    public const string VlanDescriptionLength = "schema.vlanDescriptionLength";

    /// <summary>Port intent is missing its switch stable key.</summary>
    public const string SwitchKeyRequired = "schema.switchKeyRequired";

    /// <summary>Port intent is missing its port name.</summary>
    public const string PortNameRequired = "schema.portNameRequired";

    /// <summary>A validator field that could not be mapped (defensive; should not occur).</summary>
    public const string SchemaInvalid = "schema.invalid";

    // -- semantic.* — rack-scoped uniqueness + resolvable references (AC2).
    /// <summary>A VLAN id appears more than once in the catalogue (every member of the group is reported).</summary>
    public const string DuplicateVlanId = "semantic.duplicateVlanId";

    /// <summary>The same switch/port has more than one identical access-VLAN intent in the payload.</summary>
    public const string DuplicatePortIntent = "semantic.duplicatePortIntent";

    /// <summary>The same switch/port is assigned conflicting access VLANs in the payload.</summary>
    public const string PortVlanConflict = "semantic.portVlanConflict";

    /// <summary>A port intent references a VLAN absent from the catalogue.</summary>
    public const string VlanNotInCatalogue = "semantic.vlanNotInCatalogue";

    /// <summary>A port intent references a switch not present in the rack topology inventory.</summary>
    public const string SwitchNotFound = "semantic.switchNotFound";

    /// <summary>A port intent references a port not present on the resolved switch.</summary>
    public const string PortNotFound = "semantic.portNotFound";

    /// <summary>No topology snapshot exists for the rack, so port intents cannot be resolved.</summary>
    public const string TopologyUnavailable = "semantic.topologyUnavailable";

    // -- safety.* — non-blocking guardrails (AC3).
    /// <summary>A change targets a port classified as an uplink.</summary>
    public const string UplinkPort = "safety.uplinkPort";

    /// <summary>A change targets a port classified as management.</summary>
    public const string ManagementPort = "safety.managementPort";
}
