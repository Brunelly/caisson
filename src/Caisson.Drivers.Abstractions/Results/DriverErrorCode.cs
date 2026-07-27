namespace Caisson.Drivers.Abstractions.Results;

/// <summary>
/// The taxonomy of call-level failures a driver can report, shared across switch and BMC drivers
/// (see ADR 0006). This is distinct from <see cref="Caisson.Domain.Enums.ReasonCode"/>, which
/// annotates per-item correlation ambiguity rather than a failed driver call.
/// </summary>
public enum DriverErrorCode
{
    /// <summary>No more specific error code applies.</summary>
    Unknown = 0,

    /// <summary>The connection attempt did not complete within the configured timeout.</summary>
    ConnectionTimeout,

    /// <summary>The device actively refused the connection.</summary>
    ConnectionRefused,

    /// <summary>The device could not be reached (e.g. no route, DNS failure).</summary>
    DeviceUnreachable,

    /// <summary>The device rejected the supplied credentials.</summary>
    AuthenticationFailed,

    /// <summary>The credentials were valid but lacked permission for the requested operation.</summary>
    AuthorizationDenied,

    /// <summary>The device responded in a way that violated the expected protocol.</summary>
    ProtocolError,

    /// <summary>The device response could not be parsed into the expected structure.</summary>
    ParseError,

    /// <summary>The driver does not support the requested operation.</summary>
    UnsupportedOperation,
}
