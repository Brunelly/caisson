using System.Globalization;
using System.Text;
using System.Text.Json;
using Caisson.Api.Contracts;
using Caisson.Domain.DesiredState.Diffing;

namespace Caisson.Api.Services;

/// <summary>The evidence carried in a PR title/body (story #172, AC1).</summary>
public sealed record PrBodyModel(
    string RackSlug,
    string OperatorSlug,
    DateTime TimestampUtc,
    string CandidateFingerprint,
    string ValidationRunId,
    IReadOnlyList<string> AcknowledgedWarningCodes,
    PrChangeSummary ChangeSummary,
    string CorrelationId);

/// <summary>
/// Composes the deterministic PR title and body for a rack desired-state change (story #172, AC1). The title
/// carries the rack + operator identifiers (<c>Rack {slug}: network desired-state update ({operator})</c>);
/// the body carries a machine-readable fenced JSON block (rack, operator, timestamp, fingerprint,
/// validation-run id, acknowledged warnings, structured change counts, correlation id) PLUS a human-readable
/// summary — so both a reviewer and downstream automation have the full evidence trail. Pure and
/// deterministic (no secrets, no wall-clock read).
/// </summary>
public static class PrMetadataComposer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>The PR title, e.g. <c>Rack rack-a: network desired-state update (jdoe)</c>.</summary>
    public static string ComposeTitle(string rackSlug, string operatorSlug)
        => $"Rack {rackSlug}: network desired-state update ({operatorSlug})";

    /// <summary>Builds the structured change counts from a semantic diff result.</summary>
    public static PrChangeSummary ToChangeSummary(SemanticDiffResult diff)
    {
        ArgumentNullException.ThrowIfNull(diff);

        int vAdd = 0, vRem = 0, vMod = 0, pAdd = 0, pRem = 0, pMod = 0;
        foreach (var change in diff.Changes)
        {
            var isVlan = change.Category == DesiredStateChangeCategory.Vlan;
            switch (change.Kind)
            {
                case DesiredStateChangeKind.Added when isVlan: vAdd++; break;
                case DesiredStateChangeKind.Removed when isVlan: vRem++; break;
                case DesiredStateChangeKind.Modified when isVlan: vMod++; break;
                case DesiredStateChangeKind.Added: pAdd++; break;
                case DesiredStateChangeKind.Removed: pRem++; break;
                case DesiredStateChangeKind.Modified: pMod++; break;
            }
        }

        return new PrChangeSummary(vAdd, vRem, vMod, pAdd, pRem, pMod, diff.Changes.Count);
    }

    /// <summary>Composes the full PR body: a fenced JSON evidence block followed by a human-readable summary.</summary>
    public static string ComposeBody(PrBodyModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var timestamp = model.TimestampUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var machine = new
        {
            rack = model.RackSlug,
            @operator = model.OperatorSlug,
            timestampUtc = timestamp,
            candidateFingerprint = model.CandidateFingerprint,
            validationRunId = model.ValidationRunId,
            acknowledgedWarningCodes = model.AcknowledgedWarningCodes,
            correlationId = model.CorrelationId,
            changeSummary = model.ChangeSummary,
        };

        var json = JsonSerializer.Serialize(machine, JsonOptions);
        var s = model.ChangeSummary;

        var body = new StringBuilder();
        body.Append("## Rack ").Append(model.RackSlug).Append(" — network desired-state update").Append("\n\n");
        body.Append("Submitted by **").Append(model.OperatorSlug).Append("** at ").Append(timestamp).Append(" (UTC).\n\n");
        body.Append("### Change summary\n\n");
        body.Append("- VLANs: ").Append(s.VlansAdded).Append(" added, ").Append(s.VlansRemoved)
            .Append(" removed, ").Append(s.VlansModified).Append(" modified\n");
        body.Append("- Ports: ").Append(s.PortsAdded).Append(" added, ").Append(s.PortsRemoved)
            .Append(" removed, ").Append(s.PortsModified).Append(" modified\n");
        body.Append("- Total changes: ").Append(s.Total).Append("\n\n");
        if (model.AcknowledgedWarningCodes.Count > 0)
        {
            body.Append("Acknowledged safety warnings: ")
                .Append(string.Join(", ", model.AcknowledgedWarningCodes)).Append("\n\n");
        }

        body.Append("### Machine-readable summary\n\n");
        body.Append("```json\n").Append(json).Append("\n```\n");

        return body.ToString();
    }
}
