namespace Caisson.Orchestration.RackDefinitions;

/// <summary>
/// Resolves the desired-state <see cref="RackDefinition"/> for a rack. Fail-closed: throws
/// <see cref="RackDefinitionMissingException"/> when the rack has no config-bound definition (AC/ADR 0013).
/// </summary>
public interface IRackDefinitionProvider
{
    /// <summary>Loads the definition for <paramref name="rackId"/>.</summary>
    /// <exception cref="RackDefinitionMissingException">Thrown when no definition exists for the rack.</exception>
    Task<RackDefinition> GetAsync(Guid rackId, CancellationToken cancellationToken);
}

/// <summary>Thrown when a rack has no config-bound discovery definition (fail-closed).</summary>
public sealed class RackDefinitionMissingException : Exception
{
    /// <summary>Creates the exception for a rack with no definition.</summary>
    public RackDefinitionMissingException(Guid rackId)
        : base($"No discovery definition is configured for rack '{rackId}'.")
    {
        RackId = rackId;
    }

    /// <summary>The rack that has no definition.</summary>
    public Guid RackId { get; }
}
