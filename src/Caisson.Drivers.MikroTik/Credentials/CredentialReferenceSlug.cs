using System.Text;

namespace Caisson.Drivers.MikroTik.Credentials;

/// <summary>
/// Normalizes an opaque <c>CredentialsRef</c> into the environment-variable slug used by
/// <see cref="EnvSwitchCredentialResolver"/> (and the driver factory's TLS-trust lookup): the reference
/// upper-cased with every non-alphanumeric character replaced by <c>_</c>. Shared so the credential and
/// TLS-config variable names can never drift apart.
/// </summary>
internal static class CredentialReferenceSlug
{
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
}
