namespace Caisson.Infrastructure.Persistence.Drift;

/// <summary>
/// The default <see cref="IDriftRecomputeSignal"/> when Orchestration's drift wiring
/// (<c>Caisson.Orchestration.DependencyInjection.DriftServiceCollectionExtensions.AddCaissonDrift</c>) is
/// not registered — mirrors <c>NoOpTopologyEventPublisher</c>. Does nothing, so ingestion paths and any
/// non-drift host keep working unchanged.
/// </summary>
public sealed class NoOpDriftRecomputeSignal : IDriftRecomputeSignal
{
    /// <inheritdoc />
    public void Enqueue(Guid rackId)
    {
    }
}
