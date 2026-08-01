using Caisson.Api.Options;
using Caisson.Domain.Auditing;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Caisson.Api.Auditing;

/// <summary>
/// The Tier 1 (mandatory-durable) outbox drain (story #308, ADR 0064): claims due <c>audit_outbox</c> rows
/// with the codebase's established <c>FOR UPDATE SKIP LOCKED</c> lease (<see cref="AuditOutboxQueries.ClaimDueAsync"/>),
/// then dispatches each claimed row — inserting its projection into <c>topology_audit_event</c> (same id,
/// <c>ON CONFLICT DO NOTHING</c>) and marking it <see cref="AuditOutboxStatus.Dispatched"/> — in ONE
/// transaction per row, so a crash between the insert and the status flip leaves the row <c>Pending</c>
/// (re-dispatched after lease expiry, never duplicated) rather than ever risking an audit event without a
/// matching Dispatched row or vice versa.
/// <para>
/// Dispatch is per-ROW, not per-batch: one poisoned row's permanent failure must never block or falsely
/// exhaust the attempt budget of the other, healthy rows claimed in the same tick.
/// </para>
/// Cloned from <c>GitPullRequestStatusPoller</c> for shape: <see cref="PeriodicTimer"/> + injected
/// <see cref="TimeProvider"/>, options-gated, a fresh <see cref="IServiceScopeFactory.CreateAsyncScope"/>
/// per tick, per-tick exception isolation so one poisoned row (or a transient DB outage) never crashes the
/// host, and an internal <see cref="TickAsync"/> so tests can drive it deterministically without the timer.
/// </summary>
public sealed class AuditOutboxDispatcher : BackgroundService
{
    /// <summary>Bounds the sanitized, stable failure code recorded on a poisoned row — never a raw exception message.</summary>
    private const string UnknownFailureCode = "DispatchFailed";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly IOptions<AuditDurabilityOptions> _options;
    private readonly AuditOutboxMetrics _metrics;
    private readonly ILogger<AuditOutboxDispatcher> _logger;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    public AuditOutboxDispatcher(
        IServiceScopeFactory scopeFactory,
        TimeProvider time,
        IOptions<AuditDurabilityOptions> options,
        AuditOutboxMetrics metrics,
        ILogger<AuditOutboxDispatcher> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Audit outbox dispatcher started instanceId={InstanceId}.", _instanceId);
        var period = TimeSpan.FromSeconds(_options.Value.OutboxPollIntervalSeconds);
        using var timer = new PeriodicTimer(period, _time);

        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audit outbox dispatcher tick failed; will retry next period.");
            }
        }
        while (await WaitAsync(timer, stoppingToken));

        _logger.LogInformation("Audit outbox dispatcher stopped instanceId={InstanceId}.", _instanceId);
    }

    /// <summary>Runs one dispatch tick; internal so tests can drive it deterministically without the timer.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CaissonDbContext>();

        var now = _time.GetUtcNow().UtcDateTime;
        var leaseUntil = now.AddSeconds(_options.Value.OutboxLeaseSeconds);

        var claimed = await AuditOutboxQueries.ClaimDueAsync(
            context, now, leaseUntil, _instanceId, _options.Value.OutboxBatchSize, cancellationToken);

        if (claimed.Count == 0)
        {
            return;
        }

        _metrics.RecordClaimed(claimed.Count);

        foreach (var id in claimed)
        {
            await DispatchOneAsync(context, id, cancellationToken);
        }
    }

    private async Task DispatchOneAsync(CaissonDbContext context, Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            await AuditOutboxQueries.ProjectToAuditEventAsync(context, id, cancellationToken);

            var message = await context.AuditOutboxMessages.SingleAsync(m => m.Id == id, cancellationToken);
            message.MarkDispatched(_time.GetUtcNow().UtcDateTime);
            await context.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            _metrics.RecordDispatched();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Isolate this row's failure from every other row claimed in this tick (never abort the batch,
            // never let one bad row starve the rest of their own attempt budget).
            context.ChangeTracker.Clear();
            await HandleDispatchFailureAsync(context, id, ex, cancellationToken);
        }
    }

    private async Task HandleDispatchFailureAsync(
        CaissonDbContext context, Guid id, Exception failure, CancellationToken cancellationToken)
    {
        var failureCode = ClassifyFailure(failure);
        var message = await context.AuditOutboxMessages.SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (message is null)
        {
            // Already dispatched/removed by a concurrent instance since the claim — nothing to reconcile.
            return;
        }

        if (message.AttemptCount >= _options.Value.OutboxMaxAttempts)
        {
            message.MarkPoisoned(failureCode);
            await context.SaveChangesAsync(cancellationToken);
            _metrics.RecordPoisoned(failureCode);
            _logger.LogError(
                failure,
                "Audit outbox row {Id} poisoned after {AttemptCount} attempts, failureCode={FailureCode}.",
                id, message.AttemptCount, failureCode);
            return;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var backoff = ComputeBackoff(message.AttemptCount, _options.Value);
        message.ReleaseForRetry(now.Add(backoff));
        await context.SaveChangesAsync(cancellationToken);
        _metrics.RecordRetried();
        _logger.LogWarning(
            failure,
            "Audit outbox row {Id} dispatch failed (attempt {AttemptCount}), releasing for retry in {BackoffSeconds}s, failureCode={FailureCode}.",
            id, message.AttemptCount, backoff.TotalSeconds, failureCode);
    }

    /// <summary>Exponential backoff by attempt count, bounded by <see cref="AuditDurabilityOptions.OutboxRetryMaxDelaySeconds"/>.</summary>
    private static TimeSpan ComputeBackoff(int attemptCount, AuditDurabilityOptions options)
    {
        var exponent = Math.Max(0, attemptCount - 1);
        var seconds = options.OutboxRetryBaseDelaySeconds * Math.Pow(2, exponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, options.OutboxRetryMaxDelaySeconds));
    }

    /// <summary>
    /// Maps a dispatch failure to a stable, sanitized code — NEVER the raw exception message (which could
    /// embed connection strings or other sensitive detail; the outbox row's own payload is already
    /// scrubbed, but the exception text describing the failure is not).
    /// </summary>
    private static string ClassifyFailure(Exception ex) => ex switch
    {
        PostgresException pg => ClassifyPostgresError(pg.SqlState),
        DbUpdateException { InnerException: PostgresException pg } => ClassifyPostgresError(pg.SqlState),
        TimeoutException => "Timeout",
        _ => UnknownFailureCode,
    };

    private static string ClassifyPostgresError(string? sqlState) => sqlState switch
    {
        PostgresErrorCodes.ForeignKeyViolation => "ForeignKeyViolation",
        PostgresErrorCodes.UniqueViolation => "UniqueViolation",
        PostgresErrorCodes.CheckViolation => "CheckViolation",
        PostgresErrorCodes.StringDataRightTruncation => "DataTooLong",
        _ => "PostgresError",
    };

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
