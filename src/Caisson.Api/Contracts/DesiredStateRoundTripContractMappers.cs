using Caisson.Domain.DesiredState;
using Caisson.Domain.NetworkConfig;

namespace Caisson.Api.Contracts;

/// <summary>
/// The single wire↔domain (de)serialization point for the desired-state round-trip endpoints (story #169),
/// mirroring <see cref="NetworkIntentContractMappers"/>. Both the parse response and the render request go
/// through here, so the wire shape and the domain records can never drift.
/// </summary>
public static class DesiredStateRoundTripContractMappers
{
    /// <summary>Maps a parsed domain envelope onto the parse response DTO.</summary>
    public static DesiredStateRoundTripEnvelopeDto ToDto(DesiredStateRoundTripEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return new DesiredStateRoundTripEnvelopeDto(
            new SupportedDesiredStateModelDto(
                envelope.SupportedModel.RackSlug,
                envelope.SupportedModel.VlanCatalogue.Select(ToDto).ToList(),
                envelope.SupportedModel.PortIntents.Select(ToDto).ToList()),
            envelope.UnknownBlocks.Select(ToDto).ToList(),
            envelope.Warnings.Select(ToWarningCode).ToList(),
            envelope.SchemaVersion);
    }

    /// <summary>Maps a render request's supported subset onto the domain records the validator/renderer consume.</summary>
    public static (IReadOnlyList<VlanCatalogueEntry> VlanCatalogue, IReadOnlyList<PortAccessIntent> PortIntents)
        FromRequest(DesiredStateRenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var vlanCatalogue = (request.VlanCatalogue ?? Array.Empty<VlanCatalogueEntryDto>())
            .Select(v => new VlanCatalogueEntry(v.Id, v.Name, v.Description))
            .ToList();
        var portIntents = (request.PortIntents ?? Array.Empty<PortAccessIntentDto>())
            .Select(p => new PortAccessIntent(p.SwitchStableKey, p.PortName, p.AccessVlanId))
            .ToList();
        return (vlanCatalogue, portIntents);
    }

    /// <summary>Maps preserved blocks from a render request onto domain <see cref="PreservedYamlBlock"/> records.</summary>
    public static IReadOnlyList<PreservedYamlBlock> FromRequest(IReadOnlyList<PreservedYamlBlockDto>? blocks)
        => (blocks ?? Array.Empty<PreservedYamlBlockDto>())
            .Select(b => new PreservedYamlBlock(b.AnchorPath, b.RawYamlText, b.Checksum))
            .ToList();

    /// <summary>Maps warning codes from a render request onto domain enum values (ignoring unknown codes).</summary>
    public static IReadOnlyList<DesiredStateRoundTripWarningCode> WarningsFromRequest(IReadOnlyList<string>? warnings)
        => (warnings ?? Array.Empty<string>())
            .Select(TryParseWarning)
            .Where(w => w is not null)
            .Select(w => w!.Value)
            .ToList();

    /// <summary>Maps a domain warning code onto its stable wire string.</summary>
    public static string ToWarningCode(DesiredStateRoundTripWarningCode code) => code switch
    {
        DesiredStateRoundTripWarningCode.CommentsNotPreserved => "commentsNotPreserved",
        _ => code.ToString(),
    };

    private static DesiredStateRoundTripWarningCode? TryParseWarning(string code) => code switch
    {
        "commentsNotPreserved" => DesiredStateRoundTripWarningCode.CommentsNotPreserved,
        _ => null,
    };

    private static PreservedYamlBlockDto ToDto(PreservedYamlBlock block)
        => new(block.AnchorPath, block.RawYamlText, block.Checksum);

    private static VlanCatalogueEntryDto ToDto(VlanCatalogueEntry entry) => new(entry.Id, entry.Name, entry.Description);

    private static PortAccessIntentDto ToDto(PortAccessIntent intent)
        => new(intent.SwitchStableKey, intent.PortName, intent.AccessVlanId);
}
