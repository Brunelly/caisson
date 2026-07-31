using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Caisson.Domain.NetworkConfig.Preflight;

/// <summary>
/// Computes the stateless, content-bound <c>validationRunId</c> that makes warning acknowledgement
/// TOCTOU-safe (story #170, Q3 answer; NFR3 "no DB writes except audit"). It is a deterministic SHA-256
/// over <c>rackId + canonicalized(vlanCatalogue, portIntents) + observedSnapshotId</c>: identical input and
/// topology always yield the same id, and any candidate edit or topology-snapshot change yields a different
/// id. The PR endpoint re-derives it from the submitted model against the current latest snapshot and
/// compares — so no server-side row, expiry, or signature machinery is needed (ADR 0052). See ADR 0052.
/// </summary>
public static class ValidationRunToken
{
    /// <summary>
    /// Computes the deterministic validation-run id. Canonicalizes the payload by sorting the VLAN
    /// catalogue and port intents (so a pure reorder of equivalent content yields the same id) and
    /// length-prefixing every field (so no delimiter collision can make two distinct payloads hash alike).
    /// </summary>
    public static string Compute(
        Guid rackId,
        IReadOnlyList<VlanCatalogueEntry> vlanCatalogue,
        IReadOnlyList<PortAccessIntent> portIntents,
        Guid? observedSnapshotId)
    {
        ArgumentNullException.ThrowIfNull(vlanCatalogue);
        ArgumentNullException.ThrowIfNull(portIntents);

        var builder = new StringBuilder();
        Field(builder, "rack", rackId.ToString("N"));
        Field(builder, "snapshot", observedSnapshotId?.ToString("N"));

        foreach (var vlan in vlanCatalogue
            .OrderBy(v => v.Id)
            .ThenBy(v => v.Name, StringComparer.Ordinal)
            .ThenBy(v => v.Description, StringComparer.Ordinal))
        {
            Field(builder, "vlanId", vlan.Id.ToString(CultureInfo.InvariantCulture));
            Field(builder, "vlanName", vlan.Name);
            Field(builder, "vlanDesc", vlan.Description);
        }

        foreach (var port in portIntents
            .OrderBy(p => p.SwitchStableKey, StringComparer.Ordinal)
            .ThenBy(p => p.PortName, StringComparer.Ordinal)
            .ThenBy(p => p.AccessVlanId ?? int.MinValue))
        {
            Field(builder, "portSwitch", port.SwitchStableKey);
            Field(builder, "portName", port.PortName);
            Field(builder, "portVlan", port.AccessVlanId?.ToString(CultureInfo.InvariantCulture));
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Appends one framed <c>label=len:value;</c> field. A null value is framed distinctly from an empty
    /// string so "null description" and "empty description" cannot collide.
    /// </summary>
    private static void Field(StringBuilder builder, string label, string? value)
    {
        builder.Append(label).Append('=');
        if (value is null)
        {
            builder.Append("~;");
            return;
        }

        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
    }
}
