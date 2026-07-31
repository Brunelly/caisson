using Caisson.Domain.DesiredState;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence.Queries;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Postgres-backed tests for the impact-preview diff cache (story #171, Task #197): persist/read, jsonb
/// summary + text diff round-trip, unique rack-scoped cache-key enforcement, and the rack-scoped query
/// helpers (a candidate id from another rack must not resolve — NFR2).
/// </summary>
public sealed class DesiredStateDiffCacheTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public DesiredStateDiffCacheTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Persists_and_round_trips_the_jsonb_summary_and_text_diff()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        var baselineRevisionId = Guid.NewGuid();
        var candidateSha = Hex();
        const string diff = "@@ -1,2 +1,2 @@\n vlans:\n-  - id: 10\n+  - id: 20\n";
        const string summaryJson = "{\"baselineCommitSha\":\"abc123\",\"changes\":[{\"kind\":\"Added\",\"category\":\"Vlan\"}]}";

        var id = Guid.NewGuid();
        await using (var context = _fixture.CreateContext())
        {
            context.DesiredStateCandidateDiffCaches.Add(new DesiredStateCandidateDiffCache(
                id, rackId, baselineRevisionId, candidateSha, Hex(), diff, summaryJson, "tester",
                new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)));
            await context.SaveChangesAsync();
        }

        await using var verify = _fixture.CreateContext();
        var row = await verify.DesiredStateCandidateDiffCaches.SingleAsync(c => c.Id == id);
        row.RawUnifiedDiff.Should().Be(diff); // text column preserves the diff byte-for-byte
        row.CandidateSha256.Should().Be(candidateSha);
        row.ExpiresAtUtc.Should().Be(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));

        // The jsonb column normalizes whitespace/key order, so assert on the parsed value, not the raw text.
        using var parsed = System.Text.Json.JsonDocument.Parse(row.StructuredSummaryJson);
        parsed.RootElement.GetProperty("baselineCommitSha").GetString().Should().Be("abc123");
        parsed.RootElement.GetProperty("changes").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Enforces_the_unique_rack_baseline_candidate_key()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        var baselineRevisionId = Guid.NewGuid();
        var candidateSha = Hex();

        await using var context = _fixture.CreateContext();
        context.DesiredStateCandidateDiffCaches.Add(Row(rackId, baselineRevisionId, candidateSha));
        await context.SaveChangesAsync();

        context.DesiredStateCandidateDiffCaches.Add(Row(rackId, baselineRevisionId, candidateSha));
        var act = async () => await context.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task FindAsync_returns_the_cached_row_on_a_hit_and_null_on_a_miss()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        var baselineRevisionId = Guid.NewGuid();
        var candidateSha = Hex();

        await using (var context = _fixture.CreateContext())
        {
            context.DesiredStateCandidateDiffCaches.Add(Row(rackId, baselineRevisionId, candidateSha));
            await context.SaveChangesAsync();
        }

        await using var query = _fixture.CreateContext();
        (await query.FindAsync(rackId, baselineRevisionId, candidateSha)).Should().NotBeNull();
        (await query.FindAsync(rackId, baselineRevisionId, Hex())).Should().BeNull();
        (await query.FindAsync(Guid.NewGuid(), baselineRevisionId, candidateSha)).Should().BeNull();
    }

    [Fact]
    public async Task FindByIdForRackAsync_is_rack_scoped_and_does_not_leak_across_racks()
    {
        await _fixture.MigrateAsync();
        var rackA = await SeedRackAsync();
        var rackB = await SeedRackAsync();
        var row = Row(rackA, Guid.NewGuid(), Hex());

        await using (var context = _fixture.CreateContext())
        {
            context.DesiredStateCandidateDiffCaches.Add(row);
            await context.SaveChangesAsync();
        }

        await using var query = _fixture.CreateContext();
        (await query.FindByIdForRackAsync(rackA, row.Id)).Should().NotBeNull();
        (await query.FindByIdForRackAsync(rackB, row.Id)).Should().BeNull();
    }

    private static DesiredStateCandidateDiffCache Row(Guid rackId, Guid baselineRevisionId, string candidateSha)
        => new(
            Guid.NewGuid(), rackId, baselineRevisionId, candidateSha, Hex(),
            "@@ -1,1 +1,1 @@\n-a\n+b\n", "{\"baselineCommitSha\":null,\"changes\":[]}", "tester",
            DateTime.UtcNow, DateTime.UtcNow.AddDays(1));

    private async Task<Guid> SeedRackAsync()
    {
        var rackId = Guid.NewGuid();
        await using var context = _fixture.CreateContext();
        context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "Seed Rack", DateTime.UtcNow));
        await context.SaveChangesAsync();
        return rackId;
    }

    private static string Hex() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
}
