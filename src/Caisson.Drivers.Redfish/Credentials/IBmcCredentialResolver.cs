namespace Caisson.Drivers.Redfish.Credentials;

/// <summary>
/// Resolves a <c>BmcConnectionOptions.CredentialsRef</c> — an opaque secret-store reference — into the
/// concrete username/password the driver authenticates with. This is how the driver satisfies the ADR 0006
/// <c>CredentialsRef</c> deferral without changing the shape of <c>BmcConnectionOptions</c>. One resolved
/// credential serves both Redfish HTTP Basic auth and IPMI lanplus (realistic for iLO). Implementations
/// must never log or otherwise expose the resolved secret.
/// </summary>
public interface IBmcCredentialResolver
{
    /// <summary>Resolves <paramref name="credentialsRef"/> into credentials.</summary>
    /// <exception cref="BmcCredentialResolutionException">Thrown when the reference cannot be resolved.</exception>
    BmcCredentials Resolve(string credentialsRef);
}

/// <summary>Resolved BMC credentials. Held only in memory for the lifetime of a connection.</summary>
/// <param name="Username">The BMC/iLO account username.</param>
/// <param name="Password">The BMC/iLO account password.</param>
public sealed record BmcCredentials(string Username, string Password);

/// <summary>Raised when a <c>CredentialsRef</c> cannot be resolved. Its message never contains secret material.</summary>
public sealed class BmcCredentialResolutionException : Exception
{
    /// <summary>Creates the exception with a secret-free message.</summary>
    public BmcCredentialResolutionException(string message)
        : base(message)
    {
    }
}
