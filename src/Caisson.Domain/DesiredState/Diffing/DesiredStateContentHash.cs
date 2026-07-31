using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Caisson.Domain.DesiredState.Diffing;

/// <summary>
/// Computes the stable content hash of a canonical desired-state YAML document, used to key the diff cache
/// and to stamp candidate/baseline identities (story #171, AC2; NFR2 rack-scoped cache key). Reuses the
/// same canonicalize → length-prefix → <see cref="SHA256.HashData(byte[])"/> → 64-hex discipline as
/// <c>ValidationRunToken</c>: the single canonical string is length-prefixed so no field-boundary collision
/// is possible, then hashed to a lowercase 64-hex digest. Pure and deterministic — identical canonical YAML
/// always yields the identical digest, which is what makes the cache lookup content-addressable (AC2).
/// </summary>
public static class DesiredStateContentHash
{
    /// <summary>
    /// Computes the lowercase 64-hex SHA-256 digest of <paramref name="canonicalYaml"/>. The input is
    /// length-prefixed before hashing so the digest is unambiguous even if callers ever concatenate fields.
    /// </summary>
    public static string Compute(string canonicalYaml)
    {
        ArgumentNullException.ThrowIfNull(canonicalYaml);

        var framed = new StringBuilder()
            .Append(canonicalYaml.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(canonicalYaml)
            .ToString();

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(framed));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
