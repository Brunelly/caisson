using System.Threading.Channels;

namespace Caisson.Ingestion.Ingestion;

/// <summary>A queued webhook-triggered ingestion request, drained by <c>DesiredStateIngestionRunner</c>.</summary>
public sealed record WebhookIngestionRequest(string? WebhookDeliveryId, Guid CorrelationId);

/// <summary>
/// Lets the webhook endpoint hand off work and return <c>202 Accepted</c> immediately, while the actual
/// ingestion runs reliably in-process on <c>DesiredStateIngestionRunner</c> (story #62, AC1) — more
/// robust than a raw detached <c>Task.Run</c>, which can be lost on host shutdown. Mirrors
/// <c>Caisson.Orchestration.Discovery.DiscoveryJobSignal</c>'s bounded, drop-oldest-write channel:
/// correctness never depends on this — the DB partial-unique indexes are the actual idempotency
/// guarantee, so a dropped notification only delays processing until the next poll tick picks up the
/// same (still-unprocessed) commit.
/// </summary>
public sealed class DesiredStateIngestionSignal
{
    private readonly Channel<WebhookIngestionRequest> _channel =
        Channel.CreateBounded<WebhookIngestionRequest>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    /// <summary>Enqueues a webhook-triggered ingestion request for the background drainer.</summary>
    public void Notify(WebhookIngestionRequest request) => _channel.Writer.TryWrite(request);

    /// <summary>The reader the drainer awaits.</summary>
    public ChannelReader<WebhookIngestionRequest> Reader => _channel.Reader;
}
