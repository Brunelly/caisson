namespace Caisson.Infrastructure.LiveUpdates;

/// <summary>
/// Allocates a monotonic ordering sequence for a named stream (story #9, Q3). Discovery-job status
/// events use a per-job stream (<c>job:{jobId}</c>): because enqueue and run can happen on different API
/// instances, the production implementation issues a cluster-monotonic value via Redis <c>INCR</c>,
/// avoiding the crash-reclaim ordering anomaly an in-process counter would have. The default in-process
/// implementation is monotonic-per-process and is correct for single-instance/dev; client-side
/// <c>(jobId, seq)</c> de-dup remains the safety net either way.
/// <para>
/// Snapshot events do NOT use this — their <c>Seq</c> reuses the DB-monotonic per-rack snapshot version.
/// </para>
/// </summary>
public interface ITopologyEventSequencer
{
    /// <summary>Returns the next monotonic value for <paramref name="stream"/>. Never throws.</summary>
    ValueTask<long> NextAsync(string stream, CancellationToken cancellationToken = default);
}
