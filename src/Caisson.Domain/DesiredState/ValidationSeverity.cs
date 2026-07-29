namespace Caisson.Domain.DesiredState;

/// <summary>Severity of a <see cref="DesiredStateValidationError"/> (story #62, data model).</summary>
public enum ValidationSeverity
{
    /// <summary>The rack file was rejected; no version was materialised for it.</summary>
    Error = 0,

    /// <summary>Advisory only; does not by itself prevent materialisation.</summary>
    Warning,
}
