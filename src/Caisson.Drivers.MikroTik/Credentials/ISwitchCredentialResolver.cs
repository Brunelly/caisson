namespace Caisson.Drivers.MikroTik.Credentials;

/// <summary>
/// Resolves a <c>SwitchConnectionOptions.CredentialsRef</c> — an opaque secret-store reference — into
/// the concrete username/password the driver authenticates with. This is how the driver satisfies the
/// ADR 0006 <c>CredentialsRef</c> deferral without changing the shape of <c>SwitchConnectionOptions</c>.
/// Implementations must never log or otherwise expose the resolved secret.
/// </summary>
public interface ISwitchCredentialResolver
{
    /// <summary>Resolves <paramref name="credentialsRef"/> into credentials.</summary>
    /// <exception cref="CredentialResolutionException">Thrown when the reference cannot be resolved.</exception>
    SwitchCredentials Resolve(string credentialsRef);
}

/// <summary>Resolved switch credentials. Held only in memory for the lifetime of a connection.</summary>
/// <param name="Username">The RouterOS API username.</param>
/// <param name="Password">The RouterOS API password.</param>
public sealed record SwitchCredentials(string Username, string Password);

/// <summary>Raised when a <c>CredentialsRef</c> cannot be resolved. Its message never contains secret material.</summary>
public sealed class CredentialResolutionException : Exception
{
    /// <summary>Creates the exception with a secret-free message.</summary>
    public CredentialResolutionException(string message)
        : base(message)
    {
    }
}
