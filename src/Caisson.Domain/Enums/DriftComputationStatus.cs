namespace Caisson.Domain.Enums;

/// <summary>The terminal outcome of a drift computation run captured on its <c>DriftReport</c>.</summary>
public enum DriftComputationStatus
{
    /// <summary>The engine ran to completion and the report's items reflect its full output.</summary>
    Succeeded = 0,

    /// <summary>The engine or persistence step failed; the report carries an <c>ErrorSummary</c> and no items.</summary>
    Failed,
}
