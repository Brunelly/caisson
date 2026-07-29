using System.Security.Cryptography;
using System.Text;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology.Diffing;

namespace Caisson.Domain.Drift.Diffing;

/// <summary>
/// Computes a <see cref="DriftItem.DriftItemId"/> deterministically from a drift finding's identity
/// (story #64, AC1: "same inputs yield same output ... same IDs"). Pure and side-effect free: identical
/// inputs always hash to the identical <see cref="Guid"/>, which is what lets
/// <c>DriftComputationService</c> upsert items by id instead of minting a fresh row every recompute.
/// </summary>
/// <remarks>
/// Deliberately excludes the desired-revision/observed-snapshot identity — the same real-world drift
/// (same rack, type, subject, expected/actual) computed against two different revision/snapshot pairs
/// hashes to the SAME id, which is why <c>DriftItem</c>'s uniqueness is scoped to
/// <c>(DriftReportId, DriftItemId)</c> rather than global (see its type-level remarks).
/// </remarks>
public static class DeterministicGuid
{
    /// <summary>
    /// Hashes <c>rackId|driftType|subjectType|subjectKey|expectedValue|actualValue</c> (SHA-256, first 16
    /// bytes) into a stable <see cref="Guid"/>. <paramref name="expectedValue"/>/<paramref name="actualValue"/>
    /// are free-form and are percent-escaped before joining so a value embedding the literal <c>|</c>
    /// separator can never collide two distinct findings onto the same id (mirrors
    /// <see cref="StableKeys.EscapeSegment(string)"/>'s defence). <paramref name="subjectKey"/> is already
    /// an escaped, versioned key (<c>DriftSubjectKeys</c>) and is joined verbatim.
    /// </summary>
    public static Guid Compute(
        Guid rackId,
        DriftType driftType,
        DriftSubjectType subjectType,
        string subjectKey,
        string? expectedValue,
        string? actualValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(subjectKey);

        var canonical = string.Join(
            "|",
            rackId.ToString("N"),
            driftType.ToString(),
            subjectType.ToString(),
            subjectKey,
            StableKeys.EscapeSegment(expectedValue ?? string.Empty),
            StableKeys.EscapeSegment(actualValue ?? string.Empty));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new Guid(hash.AsSpan(0, 16));
    }
}
