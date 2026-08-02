using Caisson.Api.Auditing;
using Caisson.Api.Options;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Caisson.Infrastructure.Tests.Auditing;

/// <summary>
/// Proves the Tier 2 overflow flush's PARTIAL-failure and duplicate-key recovery behaviour against real
/// PostgreSQL (story #308, ADR 0064). Each aggregate INSERT autocommits on its own, so a flush batch can
/// be half-committed when a later row fails; and the same bucket key can legitimately appear twice in one
/// batch (evicted under capacity pressure, then re-saturated). Both cases land in the failure path, which
/// is precisely where a bug is most expensive — the recovery code is the last thing standing between a
/// transient fault and a lost or double-counted security signal.
/// </summary>
public sealed class AuditDenialFlushPartialFailureTests : IClassFixture<PostgresFixture>
{
    private const string OverflowAction = "authorization.forbidden.overflow";

    private readonly PostgresFixture _fixture;

    public AuditDenialFlushPartialFailureTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_committed_aggregate_is_not_flushed_again_under_a_new_batch_id_when_a_later_one_fails()
    {
        await _fixture.MigrateAsync();

        var committedActor = "committed-" + Guid.NewGuid().ToString("N")[..8];
        var failingActor = "failing-" + Guid.NewGuid().ToString("N")[..8];

        // A rack that does not exist YET: topology_audit_event's FK on rack_id makes this bucket's insert
        // fail on the first tick. Creating the rack between ticks makes the failure transient, so the
        // second tick commits everything and the assertion never depends on flush ordering.
        var missingRackId = Guid.NewGuid();

        // Capacity 1 forces the committed bucket out into the urgent queue, which is always flushed FIRST —
        // so it is deterministically the one that commits before the failing bucket is reached.
        var options = Options.Create(new AuditDurabilityOptions { DenialMaxActiveBuckets = 1 });
        var accumulator = new DenialOverflowAccumulator(options, NullLogger<DenialOverflowAccumulator>.Instance);

        var t0 = DateTime.UtcNow;
        var committedKey = new DenialBucketKey(committedActor, "GET /api/a", "403", t0.Date);
        var failingKey = new DenialBucketKey(failingActor, "GET /api/b", "403", t0.Date);

        accumulator.MarkSaturated(committedKey, ActorType.User, rackId: null, t0.AddMinutes(5), t0);
        accumulator.TryIncrementIfSaturated(committedKey, t0);
        accumulator.TryIncrementIfSaturated(committedKey, t0); // 3 denials so far for the committed bucket
        accumulator.MarkSaturated(failingKey, ActorType.User, missingRackId, t0.AddMinutes(5), t0.AddSeconds(1));

        // One more denial for the committed bucket arrives DURING the flush — after DetachGeneration has
        // taken the batch, so it starts a brand-new Entry with a brand-new batch id in the fresh
        // generation. This is the case that turns a naive "merge everything back" into over-counting: the
        // already-committed tally gets folded into an entry carrying a DIFFERENT id, so replaying it is
        // invisible to ON CONFLICT (id) DO NOTHING.
        var raced = false;
        void RaceOneDenialIntoTheFreshGeneration()
        {
            if (raced)
            {
                return;
            }

            raced = true;
            accumulator.MarkSaturated(committedKey, ActorType.User, rackId: null, t0.AddMinutes(5), t0.AddSeconds(2));
        }

        var service = new AuditDenialFlushService(
            new FakeScopeFactory(_fixture, RaceOneDenialIntoTheFreshGeneration), accumulator, TimeProvider.System,
            options, NullLogger<AuditDenialFlushService>.Instance);

        await service.TickAsync(default);

        // The failure becomes transient: the second tick can now commit what the first could not.
        await using (var seed = _fixture.CreateContext())
        {
            seed.Racks.Add(new Rack(missingRackId, "rack-" + missingRackId.ToString("N"), "Late Rack", DateTime.UtcNow));
            await seed.SaveChangesAsync();
        }

        await service.TickAsync(default);

        // Four denials happened for this bucket: three before the flush, one racing it. The durable
        // aggregate rows must add up to exactly that — no more.
        (await TotalOverflowCountAsync(committedActor)).Should().Be(
            4,
            "an aggregate that already committed must not be replayed under a different batch id — " +
            "ON CONFLICT (id) DO NOTHING cannot dedupe that, so the denial count is inflated");

        (await TotalOverflowCountAsync(failingActor)).Should().Be(
            1, "the aggregate that genuinely failed must be retried and land exactly once");
    }

    [Fact]
    public async Task A_bucket_appearing_twice_in_one_batch_does_not_blow_up_the_failure_recovery_path()
    {
        await _fixture.MigrateAsync();

        var duplicatedActor = "duplicated-" + Guid.NewGuid().ToString("N")[..8];
        var otherActor = "other-" + Guid.NewGuid().ToString("N")[..8];
        var missingRackId = Guid.NewGuid();

        var options = Options.Create(new AuditDurabilityOptions { DenialMaxActiveBuckets = 1 });
        var accumulator = new DenialOverflowAccumulator(options, NullLogger<DenialOverflowAccumulator>.Instance);

        var t0 = DateTime.UtcNow;
        var duplicatedKey = new DenialBucketKey(duplicatedActor, "GET /api/dup", "403", t0.Date);
        var otherKey = new DenialBucketKey(otherActor, "GET /api/other", "403", t0.Date);

        // EvictOldest queues a key for urgent flush and REMOVES it from _active; a later MarkSaturated
        // re-adds the very same key. DrainUrgentFlush() and DetachGeneration() then both yield it, so the
        // same bucket key is in the batch twice — with two distinct Entry objects and two distinct batch
        // ids. Its insert fails (unknown rack), which is what drives the batch into the recovery path.
        accumulator.MarkSaturated(duplicatedKey, ActorType.User, missingRackId, t0.AddMinutes(5), t0);
        accumulator.MarkSaturated(otherKey, ActorType.User, missingRackId, t0.AddMinutes(5), t0.AddSeconds(1));
        accumulator.MarkSaturated(duplicatedKey, ActorType.User, missingRackId, t0.AddMinutes(5), t0.AddSeconds(2));

        var service = new AuditDenialFlushService(
            new FakeScopeFactory(_fixture), accumulator, TimeProvider.System, options,
            NullLogger<AuditDenialFlushService>.Instance);

        // The recovery path must survive its own inputs. If it throws, ExecuteAsync logs a generic "tick
        // failed" and the ENTIRE batch -- every principal's counts, not just the duplicated one -- is gone.
        var tick = async () => await service.TickAsync(default);
        await tick.Should().NotThrowAsync(
            "the failure-recovery path must tolerate the same bucket key appearing twice in one batch; " +
            "throwing from inside the catch loses every count in the batch");

        // Nothing may be silently discarded: with the fault cleared, the retry must persist all of it.
        await using (var seed = _fixture.CreateContext())
        {
            seed.Racks.Add(new Rack(missingRackId, "rack-" + missingRackId.ToString("N"), "Late Rack", DateTime.UtcNow));
            await seed.SaveChangesAsync();
        }

        await service.TickAsync(default);

        (await TotalOverflowCountAsync(duplicatedActor)).Should().Be(
            2, "both denials for the duplicated bucket must survive the failed batch and be flushed");
        (await TotalOverflowCountAsync(otherActor)).Should().Be(
            1, "an unrelated principal's count must not be collateral damage of the duplicate key");
    }

    /// <summary>Sums the <c>count</c> field of every durable overflow aggregate row for one actor.</summary>
    private async Task<long> TotalOverflowCountAsync(string actorId)
    {
        await using var context = _fixture.CreateContext();
        var rows = await context.AuditEvents
            .Where(a => a.Action == OverflowAction && a.ActorId == actorId)
            .Select(a => a.DetailsJson)
            .ToListAsync();

        return rows.Sum(json =>
        {
            using var document = System.Text.Json.JsonDocument.Parse(json!);
            return document.RootElement.GetProperty("count").GetInt64();
        });
    }
}
