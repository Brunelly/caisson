using System.Text;

namespace Caisson.Drivers.MikroTik.Credentials;

/// <summary>
/// The default <see cref="ISwitchCredentialResolver"/>: resolves a <c>CredentialsRef</c> from
/// environment variables, which is how CI (GitHub Actions secrets) and local runs supply switch
/// credentials without persisting them. For a reference <c>ref</c> it reads
/// <c>CAISSON_SWITCH_{REF}_USERNAME</c>/<c>_PASSWORD</c> (the reference upper-cased with non-alphanumeric
/// characters replaced by <c>_</c>), falling back to the global <c>CAISSON_SWITCH_USERNAME</c>/
/// <c>_PASSWORD</c> pair. The secret is read on demand and never logged.
/// </summary>
public sealed class EnvSwitchCredentialResolver : ISwitchCredentialResolver
{
    private const string Prefix = "CAISSON_SWITCH";

    private readonly Func<string, string?> _readEnvironment;

    /// <summary>Creates a resolver backed by process environment variables.</summary>
    public EnvSwitchCredentialResolver()
        : this(Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>Creates a resolver backed by a custom lookup — used by tests to avoid mutating the process environment.</summary>
    public EnvSwitchCredentialResolver(Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(readEnvironment);
        _readEnvironment = readEnvironment;
    }

    /// <inheritdoc />
    public SwitchCredentials Resolve(string credentialsRef)
    {
        ArgumentNullException.ThrowIfNull(credentialsRef);

        var slug = Normalize(credentialsRef);
        var username = Read($"{Prefix}_{slug}_USERNAME") ?? Read($"{Prefix}_USERNAME");
        var password = Read($"{Prefix}_{slug}_PASSWORD") ?? Read($"{Prefix}_PASSWORD");

        if (username is null || password is null)
        {
            // The message intentionally names only the reference, never the resolved secret.
            throw new CredentialResolutionException(
                $"No credentials found for reference '{credentialsRef}'. Set {Prefix}_{slug}_USERNAME/_PASSWORD " +
                $"or the global {Prefix}_USERNAME/_PASSWORD environment variables.");
        }

        return new SwitchCredentials(username, password);
    }

    private string? Read(string name)
    {
        var value = _readEnvironment(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string Normalize(string reference)
    {
        var builder = new StringBuilder(reference.Length);
        foreach (var c in reference)
        {
            builder.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        }

        return builder.ToString();
    }
}
