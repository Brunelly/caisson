using System.Security.Cryptography;
using System.Text;

namespace Caisson.Domain.DesiredState;

/// <summary>
/// One unknown/unsupported YAML section captured byte-for-byte during import so it can be re-emitted
/// unchanged on export (story #169, AC2). v1 captures only the reserved top-level <c>extensions</c> block
/// (Q1 answer); <see cref="AnchorPath"/> records where it was anchored (always <c>extensions</c> in v1) so a
/// later story can preserve more anchor positions without changing this shape.
/// </summary>
/// <param name="AnchorPath">The document location the block was captured from (e.g. <c>extensions</c>).</param>
/// <param name="RawYamlText">
/// The block's exact original text — original indentation and line endings untouched, never re-serialized —
/// so export can re-emit it verbatim (AC2: no unknown key dropped, renamed, re-ordered, or reformatted).
/// </param>
/// <param name="Checksum">
/// Lower-case hex SHA-256 of <paramref name="RawYamlText"/>'s UTF-8 bytes. The renderer verifies this before
/// re-emitting the block and rejects a mismatch, so a tampered/corrupted block is never silently written.
/// </param>
public sealed record PreservedYamlBlock(string AnchorPath, string RawYamlText, string Checksum)
{
    /// <summary>Creates a block, computing its <see cref="Checksum"/> from <paramref name="rawYamlText"/>.</summary>
    public static PreservedYamlBlock Create(string anchorPath, string rawYamlText)
    {
        ArgumentException.ThrowIfNullOrEmpty(anchorPath);
        ArgumentNullException.ThrowIfNull(rawYamlText);
        return new PreservedYamlBlock(anchorPath, rawYamlText, ComputeChecksum(rawYamlText));
    }

    /// <summary>Computes the lower-case hex SHA-256 of <paramref name="rawYamlText"/>'s UTF-8 bytes.</summary>
    public static string ComputeChecksum(string rawYamlText)
    {
        ArgumentNullException.ThrowIfNull(rawYamlText);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawYamlText));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Whether <see cref="Checksum"/> matches a freshly-computed hash of <see cref="RawYamlText"/>.</summary>
    public bool ChecksumMatches()
        => string.Equals(Checksum, ComputeChecksum(RawYamlText), StringComparison.OrdinalIgnoreCase);
}
