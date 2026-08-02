using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Caisson.Api.Auditing;
using Caisson.Api.Options;
using Caisson.Domain.Enums;
using Caisson.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Caisson.Api.Tests.Auditing;

/// <summary>
/// Guards the failure path of the Tier 2 (durable-first-N) denial writer (story #308, ADR 0064). The
/// swallow itself is deliberate and must stay — a denial-audit failure must never turn a 403 into a 500 —
/// but the durable first-N guarantee is contingent on the database being available, so when it is lost the
/// loss has to be OBSERVABLE: an Error-level log plus a failure counter to alert on, never a quiet warning
/// nobody has a dashboard for.
/// <para>
/// DB-free by construction: the context is pointed at a closed local port, so the write fails on connect
/// without any Postgres (or container) being involved.
/// </para>
/// </summary>
public sealed class AuthorizationDenialAuditWriterFailureTests
{
    [Fact]
    public async Task A_failed_denial_persistence_is_logged_at_error_and_counted_never_silently_dropped()
    {
        var metrics = new AuthorizationDenialAuditMetrics();
        using var failures = new CounterCollector(AuthorizationDenialAuditMetrics.MeterName);
        var logger = new LevelCapturingLogger<AuthorizationDenialAuditWriter>();

        await using var context = UnreachableContext();
        var writer = new AuthorizationDenialAuditWriter(
            context,
            new DenialOverflowAccumulator(
                global::Microsoft.Extensions.Options.Options.Create(new AuditDurabilityOptions()),
                new LevelCapturingLogger<DenialOverflowAccumulator>()),
            TimeProvider.System,
            global::Microsoft.Extensions.Options.Options.Create(new AuditDurabilityOptions()),
            metrics,
            logger);

        // Must not propagate: a 403 can never become a 500 because auditing failed.
        var record = async () => await writer.RecordDenialAsync(
            ActorType.User, "actor-1", "POST /api/racks/apply", "403", rackId: null,
            correlationId: Guid.NewGuid(), detailsJson: null, CancellationToken.None);
        await record.Should().NotThrowAsync();

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error,
            "a lost first-N denial record is a security-signal loss — Warning is not a level anyone alerts on");
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning,
            "the failure must be reported once, at Error — not downgraded to a warning");

        failures.Total.Should().Be(1,
            "the loss must increment a failure metric so a silently-lost denial audit is observable");
    }

    /// <summary>A context pointed at a closed loopback port, so every command fails fast on connect.</summary>
    private static CaissonDbContext UnreachableContext()
        => new(new DbContextOptionsBuilder<CaissonDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=caisson_unreachable;Username=none;Password=none;Timeout=1;Command Timeout=1")
            .Options);

    /// <summary>Sums every <see cref="long"/> measurement published by one meter.</summary>
    private sealed class CounterCollector : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _total;

        public CounterCollector(string meterName)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, meterName, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _total, value));
            _listener.Start();
        }

        public long Total => Interlocked.Read(ref _total);

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>An <see cref="ILogger{T}"/> that captures the LEVEL of every entry, not just its text.</summary>
    private sealed class LevelCapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyCollection<(LogLevel Level, string Message)> Entries => _entries.ToArray();

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _entries.Enqueue((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
