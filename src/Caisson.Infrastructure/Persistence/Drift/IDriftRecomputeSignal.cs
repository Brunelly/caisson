namespace Caisson.Infrastructure.Persistence.Drift;

/// <summary>
/// The dependency-free seam the ingestion/persistence layers call to nudge a low-latency drift recompute
/// (story #64, AC4) — mirrors <c>Caisson.Infrastructure.LiveUpdates.ITopologyEventPublisher</c>'s shape so
/// those layers never reference <c>Caisson.Orchestration</c>'s bounded channel directly (Orchestration is
/// layered ABOVE both Infrastructure and Ingestion). The production implementation
/// (<c>Caisson.Orchestration.Drift.DriftRecomputeSignal</c>) enqueues onto a bounded, drop-oldest channel
/// drained by a background runner; <see cref="NoOpDriftRecomputeSignal"/> is the default when
/// Orchestration's drift wiring is not registered (e.g. isolated Infrastructure/Ingestion tests).
/// <para>
/// HARD CONTRACT: implementations MUST NEVER throw. Correctness never depends on this signal — it is
/// only a low-latency nudge; the periodic <c>DriftScheduler</c> sweep is the correctness backstop — so a
/// dropped or failed enqueue must never abort the ingestion path that triggered it (AC4).
/// </para>
/// </summary>
public interface IDriftRecomputeSignal
{
    /// <summary>Requests a drift recompute for the given rack as soon as a runner is free. Never throws.</summary>
    void Enqueue(Guid rackId);
}
