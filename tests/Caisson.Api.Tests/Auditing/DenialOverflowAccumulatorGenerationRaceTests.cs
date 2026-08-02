using System.Collections.Concurrent;
using Caisson.Api.Auditing;
using Caisson.Api.Options;
using Caisson.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caisson.Api.Tests.Auditing;

/// <summary>
/// Guards the ONE invariant the Tier 2 overflow accumulator exists to provide (story #308, ADR 0064): a
/// denial that takes the no-DB flood path must always be counted SOMEWHERE. The accumulator is allowed to
/// count it in the generation the flush service just detached or in the fresh one — that choice is
/// invisible to the caller and both are eventually persisted — but it must never report "counted" while
/// the increment landed in neither.
/// <para>
/// That is precisely what a generation swap racing a saturation check can cause if the two are separate
/// reads of <c>_active</c>: the check observes the old dictionary, the swap lands, and the increment then
/// looks the key up in the NEW, empty one, finds nothing, and silently does nothing — while the caller has
/// already returned down the no-DB path. The denial is counted nowhere and the request is over.
/// </para>
/// </summary>
public sealed class DenialOverflowAccumulatorGenerationRaceTests
{
    private static readonly DenialBucketKey Key = new("flood-actor", "POST /api/racks/apply", "403", DateTime.UtcNow.Date);

    [Fact]
    public async Task A_denial_is_never_counted_nowhere_when_a_flush_swaps_the_generation_underneath_it()
    {
        var accumulator = new DenialOverflowAccumulator(
            global::Microsoft.Extensions.Options.Options.Create(new AuditDurabilityOptions()),
            NullLogger<DenialOverflowAccumulator>.Instance);
        var now = DateTime.UtcNow;
        var windowEnd = now.AddMinutes(5);

        // Every Entry the accumulator ever hands out. Counts are summed at the END, off the live objects,
        // so an increment that lands on an already-detached entry still counts — the flush service reading
        // its own snapshot slightly late is a separate, ADR-0064-accepted window, not what this guards.
        var everDetached = new ConcurrentBag<DenialOverflowAccumulator.Entry>();
        long claimedByHotPath = 0;
        long establishedByDurablePath = 0;

        void Detach()
        {
            foreach (var entry in accumulator.DetachGeneration().Values)
            {
                everDetached.Add(entry);
            }
        }

        // Stands in for the writer's cold path, which re-establishes saturation after a DB round trip.
        // Each call is itself one denial. Kept on the flushing thread so it can never land in a generation
        // that was already detached AND enumerated — isolating this test to the hot-path race alone.
        void EstablishSaturation()
        {
            accumulator.MarkSaturated(Key, ActorType.User, rackId: null, windowEnd, now);
            Interlocked.Increment(ref establishedByDurablePath);
        }

        EstablishSaturation();

        using var stopFlushing = new CancellationTokenSource();
        var flusher = Task.Run(() =>
        {
            while (!stopFlushing.Token.IsCancellationRequested)
            {
                Detach();
                EstablishSaturation();
            }
        });

        var floodTasks = Enumerable.Range(0, Environment.ProcessorCount * 2)
            .Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < 50_000; i++)
                {
                    // A true result is a PROMISE that this denial has been counted — the caller returns
                    // immediately down the no-DB path and never touches the database again. A false result
                    // is fine: in production it simply sends the request to the durable path instead.
                    if (accumulator.TryIncrementIfSaturated(Key, now))
                    {
                        Interlocked.Increment(ref claimedByHotPath);
                    }
                }
            }))
            .ToArray();

        await Task.WhenAll(floodTasks);
        await stopFlushing.CancelAsync();
        await flusher;
        Detach(); // whatever the flusher left behind

        everDetached.Sum(e => e.Count).Should().Be(
            Interlocked.Read(ref claimedByHotPath) + Interlocked.Read(ref establishedByDurablePath),
            "every denial the hot path CLAIMED to have counted must actually have landed on a generation — " +
            "reporting 'counted' while incrementing nothing silently erases a security record");
    }
}
