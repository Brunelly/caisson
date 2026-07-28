namespace Caisson.Drivers.Redfish.Credentials;

/// <summary>
/// The default <see cref="IBmcCredentialResolver"/>: resolves a <c>CredentialsRef</c> from environment
/// variables, which is how CI (GitHub Actions secrets) and local runs supply BMC credentials without
/// persisting them. For a reference <c>ref</c> it reads <c>CAISSON_BMC_{REF}_USERNAME</c>/<c>_PASSWORD</c>
/// (the reference upper-cased with non-alphanumeric characters replaced by <c>_</c>), falling back to the
/// global <c>CAISSON_BMC_USERNAME</c>/<c>_PASSWORD</c> pair. The secret is read on demand and never logged.
/// </summary>
public sealed class EnvBmcCredentialResolver : IBmcCredentialResolver
{
    private const string Prefix = "CAISSON_BMC";

    private readonly Func<string, string?> _readEnvironment;

    /// <summary>Creates a resolver backed by process environment variables.</summary>
    public EnvBmcCredentialResolver()
        : this(Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>Creates a resolver backed by a custom lookup — used by tests to avoid mutating the process environment.</summary>
    public EnvBmcCredentialResolver(Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(readEnvironment);
        _readEnvironment = readEnvironment;
    }

    /// <inheritdoc />
    public BmcCredentials Resolve(string credentialsRef)
    {
        ArgumentNullException.ThrowIfNull(credentialsRef);

        var slug = CredentialReferenceSlug.Normalize(credentialsRef);
        var username = Read($"{Prefix}_{slug}_USERNAME") ?? Read($"{Prefix}_USERNAME");
        var password = Read($"{Prefix}_{slug}_PASSWORD") ?? Read($"{Prefix}_PASSWORD");

        if (username is null || password is null)
        {
            // The message intentionally names only the reference, never the resolved secret.
            throw new BmcCredentialResolutionException(
                $"No credentials found for reference '{credentialsRef}'. Set {Prefix}_{slug}_USERNAME/_PASSWORD " +
                $"or the global {Prefix}_USERNAME/_PASSWORD environment variables.");
        }

        return new BmcCredentials(username, password);
    }

    private string? Read(string name)
    {
        var value = _readEnvironment(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
