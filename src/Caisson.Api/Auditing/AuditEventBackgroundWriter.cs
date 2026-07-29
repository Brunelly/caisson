using System.Threading.Channels;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Caisson.Api.Auditing;

/// <summary>
/// Drains <see cref="AuditWriteRequest"/>s queued by <see cref="ChannelAuditEventWriter"/> and batches
/// them into the append-only audit table (finding #5), off the request path. Flushes every
/// <see cref="FlushInterval"/> (or once <see cref="MaxBatchSize"/> is reached), coalescing repeated
/// identical reads from the same principal+correlation id within one flush window, and drains any
/// remaining queued events on graceful shutdown so a host restart never silently loses them.
/// </summary>
public sealed class AuditEventBackgroundWriter : BackgroundService
{
    private const int MaxBatchSize = 200;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);

    private readonly ChannelReader<AuditWriteRequest> _reader;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuditEventBackgroundWriter> _logger;

    public AuditEventBackgroundWriter(
        ChannelReader<AuditWriteRequest> reader, IServiceScopeFactory scopeFactory, ILogger<AuditEventBackgroundWriter> logger)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<AuditWriteRequest>(MaxBatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            batch.Clear();
            await CollectBatchAsync(batch, stoppingToken);

            if (batch.Count > 0)
            {
                await FlushAsync(batch, CancellationToken.None);
            }
        }

        // Graceful shutdown: drain whatever is still queued (best-effort — anything enqueued after this
        // read completes, in the narrow window before the process actually exits, is still lost; audit is
        // deliberately eventually-consistent, not synchronously durable, per this finding's trade-off).
        var drained = new List<AuditWriteRequest>();
        while (_reader.TryRead(out var item))
        {
            drained.Add(item);
        }

        if (drained.Count > 0)
        {
            await FlushAsync(drained, CancellationToken.None);
        }
    }

    private async Task CollectBatchAsync(List<AuditWriteRequest> batch, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(FlushInterval);

        try
        {
            while (batch.Count < MaxBatchSize && await _reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false))
            {
                while (batch.Count < MaxBatchSize && _reader.TryRead(out var item))
                {
                    batch.Add(item);
                }
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // FlushInterval elapsed — flush whatever was collected so far.
        }
    }

    private async Task FlushAsync(List<AuditWriteRequest> batch, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<CaissonDbContext>();

            foreach (var request in Coalesce(batch))
            {
                context.AuditEvents.Add(new TopologyAuditEvent(
                    request.Id, request.OccurredAtUtc, request.ActorType, request.ActorId, request.Action,
                    request.TargetType, request.CorrelationId, request.Result,
                    rackId: request.RackId, snapshotId: null, targetId: request.TargetId));
            }

            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // The audit trail must never be able to bring down the writer loop or (transitively) the
            // request path that enqueued it — log and move on to the next batch.
            _logger.LogError(ex, "Failed to flush {Count} audit event(s); they are lost (eventually-consistent audit).", batch.Count);
        }
    }

    /// <summary>Collapses repeated identical reads from the same principal+correlation id within one flush window.</summary>
    private static IEnumerable<AuditWriteRequest> Coalesce(List<AuditWriteRequest> batch)
    {
        var seen = new HashSet<(string ActorId, Guid CorrelationId, string Action, string TargetType, string? TargetId)>();
        foreach (var request in batch)
        {
            var key = (request.ActorId, request.CorrelationId, request.Action, request.TargetType, request.TargetId);
            if (seen.Add(key))
            {
                yield return request;
            }
        }
    }
}
