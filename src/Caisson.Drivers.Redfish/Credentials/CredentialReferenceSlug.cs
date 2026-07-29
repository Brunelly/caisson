using System.Text;
using System.Text.RegularExpressions;

namespace Caisson.Drivers.Redfish.Credentials;

/// <summary>
/// Normalizes an opaque <c>CredentialsRef</c> into the environment-variable slug used by
/// <see cref="EnvBmcCredentialResolver"/> (and the driver factory's TLS-trust lookup): the reference
/// upper-cased with every non-alphanumeric character replaced by <c>_</c>. Shared so the credential and
/// TLS-config variable names can never drift apart. Mirrors the MikroTik driver's equivalent. Public (not
/// <c>internal</c>) so <c>Caisson.Orchestration</c> — the one layer permitted to reference the concrete
/// driver projects — can reuse <see cref="Normalize"/> and <see cref="Validate"/> for configuration-wide
/// collision detection.
/// </summary>
public static partial class CredentialReferenceSlug
{
    /// <summary>
    /// The only <c>CredentialsRef</c> shape accepted: 1–64 characters, each an ASCII letter, digit, or
    /// underscore. Restricting the charset up front makes <see cref="Normalize"/> collide only on case —
    /// the one ambiguity <see cref="Validate"/> cannot close by construction and that a configuration-wide
    /// collision check must catch instead.
    /// </summary>
    [GeneratedRegex("^[A-Za-z0-9_]{1,64}$")]
    private static partial Regex AllowedPattern();

    public static string Normalize(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var builder = new StringBuilder(reference.Length);
        foreach (var c in reference)
        {
            builder.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Validates that <paramref name="reference"/> matches the strict <c>^[A-Za-z0-9_]{1,64}$</c> charset —
    /// in particular, rejecting an empty reference outright rather than letting it normalize to an empty
    /// slug that silently falls back to the global (non-per-device) credential.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="reference"/> does not match the allowed shape.</exception>
    public static void Validate(string reference, string deviceKey)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (!AllowedPattern().IsMatch(reference))
        {
            throw new ArgumentException(
                $"Device '{deviceKey}' has an invalid CredentialsRef '{reference}'. A CredentialsRef must be " +
                "non-empty and match ^[A-Za-z0-9_]{1,64}$.");
        }
    }
}
