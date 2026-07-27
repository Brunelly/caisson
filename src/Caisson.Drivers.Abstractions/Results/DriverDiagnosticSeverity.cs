namespace Caisson.Drivers.Abstractions.Results;

/// <summary>The severity of a <see cref="DriverDiagnostic"/> attached to an otherwise successful result.</summary>
public enum DriverDiagnosticSeverity
{
    /// <summary>The item was read with a caveat, but discovery can proceed.</summary>
    Warning = 0,

    /// <summary>The item could not be read at all.</summary>
    Error,
}
