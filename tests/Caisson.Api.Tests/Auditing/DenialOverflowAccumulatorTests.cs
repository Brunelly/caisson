using Caisson.Api.Auditing;
using Caisson.Api.Options;
using Caisson.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Caisson.Api.Tests.Auditing;

/// <summary>
/// Pure, DB-free unit tests for <see cref="DenialOverflowAccumulator"/> (story #308, ADR 0064): known-
/// saturation lookup, lock-free increment, the atomic generation swap used by the flush service, and
/// capacity-pressure eviction.
/// </summary>
public sealed class DenialOverflowAccumulatorTests
{
    private static readonly DenialBucketKey Key = new("actor-1", "GET /api/test", "403", DateTime.UtcNow.Date);

    [Fact]
    public void A_fresh_bucket_is_not_known_saturated()
    {
        var accumulator = New();
        accumulator.IsKnownSaturated(Key).Should().BeFalse();
    }

    [Fact]
    public void MarkSaturated_makes_the_bucket_known_saturated_with_count_one()
    {
        var accumulator = New();
        var now = DateTime.UtcNow;

        accumulator.MarkSaturated(Key, ActorType.User, rackId: null, windowEndAtUtc: now.AddMinutes(5), now);

        accumulator.IsKnownSaturated(Key).Should().BeTrue();
        var generation = accumulator.DetachGeneration();
        generation[Key].Count.Should().Be(1);
    }

    [Fact]
    public void Increment_is_lock_free_and_accumulates_concurrently()
    {
        var accumulator = New();
        var now = DateTime.UtcNow;
        accumulator.MarkSaturated(Key, ActorType.User, rackId: null, windowEndAtUtc: now.AddMinutes(5), now);

        Parallel.For(0, 1000, _ => accumulator.Increment(Key, now));

        var generation = accumulator.DetachGeneration();
        generation[Key].Count.Should().Be(1001); // the initial MarkSaturated count + 1000 increments
    }

    [Fact]
    public void Increment_on_an_unknown_bucket_is_a_safe_no_op()
    {
        var accumulator = New();
        accumulator.Increment(Key, DateTime.UtcNow); // no MarkSaturated call first

        accumulator.DetachGeneration().Should().BeEmpty();
    }

    [Fact]
    public void DetachGeneration_swaps_in_a_fresh_dictionary_so_subsequent_marks_start_a_new_generation()
    {
        var accumulator = New();
        var now = DateTime.UtcNow;
        accumulator.MarkSaturated(Key, ActorType.User, rackId: null, windowEndAtUtc: now.AddMinutes(5), now);

        var first = accumulator.DetachGeneration();
        first.Should().ContainKey(Key);

        // Nothing carries over: the bucket must be re-marked (a fresh DB check) to be known-saturated again.
        accumulator.IsKnownSaturated(Key).Should().BeFalse();
    }

    [Fact]
    public void MergeBack_folds_counts_and_keeps_the_earliest_first_seen_and_latest_last_seen()
    {
        var accumulator = New();
        var t0 = DateTime.UtcNow;
        var t1 = t0.AddSeconds(5);

        accumulator.MarkSaturated(Key, ActorType.User, rackId: null, windowEndAtUtc: t0.AddMinutes(5), t0);
        var detached = accumulator.DetachGeneration();

        // A racing increment lands on the fresh (post-detach) generation.
        accumulator.MarkSaturated(Key, ActorType.User, rackId: null, windowEndAtUtc: t0.AddMinutes(5), t1);
        accumulator.Increment(Key, t1);

        accumulator.MergeBack(detached);

        var merged = accumulator.DetachGeneration();
        merged[Key].Count.Should().Be(3); // 1 (original) + 1 (re-mark) + 1 (increment)
        merged[Key].FirstSeenAtUtc.Should().Be(t0);
        merged[Key].LastSeenAtUtc.Should().Be(t1);
    }

    [Fact]
    public void Exceeding_max_active_buckets_evicts_the_oldest_bucket_for_urgent_flush()
    {
        var accumulator = new DenialOverflowAccumulator(
            global::Microsoft.Extensions.Options.Options.Create(new AuditDurabilityOptions { DenialMaxActiveBuckets = 2 }),
            NullLogger<DenialOverflowAccumulator>.Instance);
        var now = DateTime.UtcNow;
        var keyA = new DenialBucketKey("actor-a", "GET /a", "403", now.Date);
        var keyB = new DenialBucketKey("actor-b", "GET /b", "403", now.Date);
        var keyC = new DenialBucketKey("actor-c", "GET /c", "403", now.Date);

        accumulator.MarkSaturated(keyA, ActorType.User, null, now.AddMinutes(5), now);
        accumulator.MarkSaturated(keyB, ActorType.User, null, now.AddMinutes(5), now.AddMilliseconds(1));
        accumulator.MarkSaturated(keyC, ActorType.User, null, now.AddMinutes(5), now.AddMilliseconds(2)); // triggers eviction

        // The oldest (A) is evicted into the urgent-flush queue — NEVER silently discarded — while B/C remain active.
        var urgent = accumulator.DrainUrgentFlush();
        urgent.Should().ContainSingle(kv => kv.Key.Equals(keyA));

        var generation = accumulator.DetachGeneration();
        generation.Should().ContainKey(keyB);
        generation.Should().ContainKey(keyC);
        generation.Should().NotContainKey(keyA);
    }

    private static DenialOverflowAccumulator New()
        => new(global::Microsoft.Extensions.Options.Options.Create(new AuditDurabilityOptions()), NullLogger<DenialOverflowAccumulator>.Instance);
}
