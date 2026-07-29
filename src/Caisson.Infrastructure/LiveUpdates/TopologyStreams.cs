using System.Globalization;

namespace Caisson.Infrastructure.LiveUpdates;

/// <summary>
/// Names the per-subject sequence streams handed to <see cref="ITopologyEventSequencer"/>. The
/// cluster-monotonic ordering guarantee (story #9, ADR 0014) relies on every publisher of a given
/// subject using the identical stream key, so the key has a single source of truth here — mirroring
/// how <see cref="TopologyGroups"/> single-sources the SignalR group names.
/// </summary>
public static class TopologyStreams
{
    /// <summary>The seq stream carrying discovery-job status events for a single job.</summary>
    public static string ForJob(Guid jobId)
        => "job:" + jobId.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>The seq stream carrying drift-apply-job status events for a single job (story #65).</summary>
    public static string ForDriftApplyJob(Guid jobId)
        => "drift-apply-job:" + jobId.ToString("N", CultureInfo.InvariantCulture);
}
