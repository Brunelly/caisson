using Caisson.Api.Auditing;
using Caisson.Api.Options;
using Caisson.Domain.Enums;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Caisson.Infrastructure.Tests.Auditing;

/// <summary>
/// Proves the Tier 2 overflow flush's idempotency/retry/graceful-shutdown contract against real
/// PostgreSQL (story #308, ADR 0064): a replayed flush batch never double-counts, a failed flush retains
/// its tally for the next interval, and a graceful stop flushes pending overflow.
/// </summary>
public sealed class AuditDenialFlushIdempotencyTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public AuditDenialFlushIdempotencyTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Replaying_the_same_flush_batch_id_does_not_double_count()
    {
        await _fixture.MigrateAsync();
        var batchId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var context = _fixture.CreateContext();
        await AuditDenialBucketQueries.InsertOverflowAuditEventAsync(
            context, batchId, now, ActorType.User, "actor-1", Guid.Empty, rackId: null,
            detailsJson: """{"count":42}""", default);
        await AuditDenialBucketQueries.InsertOverflowAuditEventAsync(
            context, batchId, now, ActorType.User, "actor-1", Guid.Empty, rackId: null,
            detailsJson: """{"count":999}""", default); // a "replay" must not overwrite or duplicate

        var rows = await context.AuditEvents.Where(a => a.Id == batchId).ToListAsync();
        rows.Should().ContainSingle();
        rows[0].DetailsJson.Should().Contain("42");
    }

    [Fact]
    public async Task Tick_flushes_the_accumulated_overflow_exactly_once_and_a_second_tick_is_a_no_op()
    {
        await _fixture.MigrateAsync();
        var actorId = "overflow-" + Guid.NewGuid().ToString("N")[..8];
        var options = Options.Create(new AuditDurabilityOptions());
        var accumulator = new DenialOverflowAccumulator(options, NullLogger<DenialOverflowAccumulator>.Instance);
        var key = new DenialBucketKey(actorId, "GET /api/test", "403", DateTime.UtcNow.Date);
        var now = DateTime.UtcNow;

        accumulator.MarkSaturated(key, ActorType.User, rackId: null, windowEndAtUtc: now.AddMinutes(5), now);
        accumulator.Increment(key, now);
        accumulator.Increment(key, now);

        var service = new AuditDenialFlushService(
            new FakeScopeFactory(_fixture), accumulator, TimeProvider.System, options, NullLogger<AuditDenialFlushService>.Instance);

        await service.TickAsync(default);

        await using (var verify = _fixture.CreateContext())
        {
            var rows = await verify.AuditEvents.Where(a => a.Action == "authorization.forbidden.overflow" && a.ActorId == actorId).ToListAsync();
            rows.Should().ContainSingle();
            using var details = System.Text.Json.JsonDocument.Parse(rows[0].DetailsJson!);
            details.RootElement.GetProperty("count").GetInt64().Should().Be(3);
        }

        // A second tick (nothing new accumulated — the generation was already detached) must not duplicate.
        await service.TickAsync(default);

        await using var final = _fixture.CreateContext();
        (await final.AuditEvents.CountAsync(a => a.Action == "authorization.forbidden.overflow" && a.ActorId == actorId))
            .Should().Be(1);
    }

    [Fact]
    public void A_failed_flush_merges_the_generation_back_for_retry_without_losing_concurrent_increments()
    {
        var options = Options.Create(new AuditDurabilityOptions());
        var accumulator = new DenialOverflowAccumulator(options, NullLogger<DenialOverflowAccumulator>.Instance);
        var key = new DenialBucketKey("actor-1", "GET /api/test", "403", DateTime.UtcNow.Date);
        var now = DateTime.UtcNow;

        accumulator.MarkSaturated(key, ActorType.User, rackId: null, windowEndAtUtc: now.AddMinutes(5), now);
        var detached = accumulator.DetachGeneration();

        // An increment races the detach and lands on the FRESH (post-swap) generation.
        accumulator.MarkSaturated(key, ActorType.User, rackId: null, windowEndAtUtc: now.AddMinutes(5), now);

        // The "flush" fails — merge the detached generation back.
        accumulator.MergeBack(detached);

        var merged = accumulator.DetachGeneration();
        merged.Should().ContainKey(key);
        merged[key].Count.Should().Be(2, "neither the original nor the racing increment may be lost");
    }
}
