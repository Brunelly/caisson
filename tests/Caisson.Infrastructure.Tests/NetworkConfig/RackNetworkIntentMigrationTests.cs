using System.Text.Json;
using Caisson.Domain.NetworkConfig;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Tests;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Caisson.Infrastructure.Tests.NetworkConfig;

/// <summary>
/// Postgres-backed tests for the <see cref="RackNetworkIntent"/> table/EF-configuration invariants
/// (story #176): the jsonb column round-trips, the unique index enforces a single saved state per rack
/// (story Q3), the rack FK is restrictive (a rack with saved intent cannot be deleted), and the xmin
/// concurrency token actually rejects a stale concurrent update. Mirrors
/// <see cref="DriftApplyJobConcurrencyTests"/>'s shape.
/// </summary>
public sealed class RackNetworkIntentMigrationTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public RackNetworkIntentMigrationTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Migration_creates_the_expected_table_and_unique_index()
    {
        await _fixture.MigrateAsync();

        var tables = await _fixture.GetTableNamesAsync();
        var indexes = await _fixture.GetIndexNamesAsync();

        tables.Should().Contain("rack_network_intent");
        indexes.Should().Contain("ux_rack_network_intent_rack_id");
    }

    [Fact]
    public async Task Saved_intent_round_trips_its_jsonb_payload()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        const string payload = "{\"vlanCatalogue\":[{\"id\":120,\"name\":\"storage\",\"description\":null}],\"portIntents\":[]}";

        await using (var context = _fixture.CreateContext())
        {
            context.RackNetworkIntents.Add(
                new RackNetworkIntent(Guid.NewGuid(), rackId, payload, "author@example.com", DateTime.UtcNow));
            await context.SaveChangesAsync();
        }

        await using var verify = _fixture.CreateContext();
        var saved = await verify.RackNetworkIntents.AsNoTracking().SingleAsync(x => x.RackId == rackId);

        // The jsonb column type is free to reformat whitespace/key order on storage — compare parsed
        // values, not the raw string, for a genuine round-trip assertion.
        using var actual = JsonDocument.Parse(saved.IntentJson);
        actual.RootElement.GetProperty("vlanCatalogue")[0].GetProperty("id").GetInt32().Should().Be(120);
        actual.RootElement.GetProperty("vlanCatalogue")[0].GetProperty("name").GetString().Should().Be("storage");
        actual.RootElement.GetProperty("portIntents").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_second_row_for_the_same_rack_violates_the_unique_index()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();

        await using (var context = _fixture.CreateContext())
        {
            context.RackNetworkIntents.Add(
                new RackNetworkIntent(Guid.NewGuid(), rackId, "{}", "author@example.com", DateTime.UtcNow));
            await context.SaveChangesAsync();
        }

        await using var second = _fixture.CreateContext();
        second.RackNetworkIntents.Add(
            new RackNetworkIntent(Guid.NewGuid(), rackId, "{}", "author@example.com", DateTime.UtcNow));

        var act = async () => await second.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Deleting_a_rack_with_saved_network_intent_is_restricted()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();

        await using (var context = _fixture.CreateContext())
        {
            context.RackNetworkIntents.Add(
                new RackNetworkIntent(Guid.NewGuid(), rackId, "{}", "author@example.com", DateTime.UtcNow));
            await context.SaveChangesAsync();
        }

        await using var context2 = _fixture.CreateContext();
        var rack = await context2.Racks.SingleAsync(r => r.Id == rackId);
        context2.Racks.Remove(rack);

        var act = async () => await context2.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    /// <summary>
    /// The story's optimistic-concurrency "version/etag" IS the row's xmin (never a hand-rolled version
    /// int): loading the same row through two separate contexts, updating both, and saving the second
    /// after the first already succeeded must throw — the second context's captured original xmin no
    /// longer matches what is now in the table.
    /// </summary>
    [Fact]
    public async Task A_stale_concurrent_update_is_rejected_via_the_xmin_token()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();

        await using (var seed = _fixture.CreateContext())
        {
            seed.RackNetworkIntents.Add(
                new RackNetworkIntent(Guid.NewGuid(), rackId, "{}", "author@example.com", DateTime.UtcNow));
            await seed.SaveChangesAsync();
        }

        await using var contextA = _fixture.CreateContext();
        await using var contextB = _fixture.CreateContext();
        var intentA = await contextA.RackNetworkIntents.SingleAsync(x => x.RackId == rackId);
        var intentB = await contextB.RackNetworkIntents.SingleAsync(x => x.RackId == rackId);

        intentA.Update("{\"v\":1}", "a@example.com", DateTime.UtcNow);
        await contextA.SaveChangesAsync();

        intentB.Update("{\"v\":2}", "b@example.com", DateTime.UtcNow);
        var act = async () => await contextB.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    private async Task<Guid> SeedRackAsync()
    {
        var rackId = Guid.NewGuid();
        await using var context = _fixture.CreateContext();
        context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "Test Rack", DateTime.UtcNow));
        await context.SaveChangesAsync();
        return rackId;
    }
}
