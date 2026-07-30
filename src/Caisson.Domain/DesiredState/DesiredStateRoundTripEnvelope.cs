using System.Collections.Generic;
using Caisson.Domain.NetworkConfig;

namespace Caisson.Domain.DesiredState;

/// <summary>
/// A typed round-trip warning code (story #169, AC3). Warnings are non-fatal facts the UI/API surface to the
/// operator; they never block a parse or render. Codes are stable identifiers safe to log and audit.
/// </summary>
public enum DesiredStateRoundTripWarningCode
{
    /// <summary>The imported YAML contained comments, which are not preserved in v1 (AC3).</summary>
    CommentsNotPreserved,
}

/// <summary>
/// The v1 UI-supported subset of a rack desired-state document (story #169): the VLAN catalogue and the
/// per-port access-VLAN intents, plus the rack slug carried in <c>metadata.rackSlug</c>. Reuses the existing
/// <see cref="VlanCatalogueEntry"/>/<see cref="PortAccessIntent"/> authoring records verbatim so the authoring
/// screen (#168) and this pipeline can never disagree about the model shape.
/// </summary>
/// <param name="RackSlug">The rack slug rendered into / parsed from <c>metadata.rackSlug</c>.</param>
/// <param name="VlanCatalogue">The rack's VLAN catalogue (rendered as <c>spec.vlans</c>).</param>
/// <param name="PortIntents">
/// The per-port access-VLAN intents (rendered as <c>spec.switches[].ports[]</c>, grouped by
/// <see cref="PortAccessIntent.SwitchStableKey"/>). Intents with a <c>null</c> <see cref="PortAccessIntent.AccessVlanId"/>
/// are "no intent" and are omitted from the rendered document.
/// </param>
public sealed record SupportedDesiredStateModel(
    string RackSlug,
    IReadOnlyList<VlanCatalogueEntry> VlanCatalogue,
    IReadOnlyList<PortAccessIntent> PortIntents);

/// <summary>
/// The full result of importing a desired-state YAML document (story #169, data-model change): the extracted
/// UI-supported model, every unknown section captured byte-for-byte for lossless re-emission, any non-fatal
/// warnings, and the schema version. On a successful parse the whole envelope is returned; on any error the
/// importer returns an accumulated issue list instead and NO partial model (AC4).
/// </summary>
/// <param name="SupportedModel">The extracted UI-supported subset.</param>
/// <param name="UnknownBlocks">Unknown/unsupported sections captured verbatim (v1: the reserved <c>extensions</c> block).</param>
/// <param name="Warnings">Non-fatal warning codes (e.g. <see cref="DesiredStateRoundTripWarningCode.CommentsNotPreserved"/>).</param>
/// <param name="SchemaVersion">The desired-state payload schema version (<see cref="DesiredStateSchema.CurrentSchemaVersion"/>).</param>
public sealed record DesiredStateRoundTripEnvelope(
    SupportedDesiredStateModel SupportedModel,
    IReadOnlyList<PreservedYamlBlock> UnknownBlocks,
    IReadOnlyList<DesiredStateRoundTripWarningCode> Warnings,
    int SchemaVersion);
