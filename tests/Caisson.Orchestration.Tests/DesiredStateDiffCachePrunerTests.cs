using Caisson.Domain.DesiredState;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence;
using Caisson.Orchestration.Drift;
using Caisson.Orchestration.Options;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Caisson.Orchestration.Tests;

/// <summary>
/// Postgres-backed tests of <see cref="DesiredStateDiffCachePruner"/> (story #171, Task #197): a single
/// deterministic <c>TickAsync</c> deletes only rows whose <c>ExpiresAtUtc</c> has passed and preserves live
/// rows, driven by a controllable <see cref="TimeProvider"/> (mirrors <see cref="DriftRetentionPrunerTests"/>).
/// </summary>
public sealed class DesiredStateDiffCachePrunerTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public DesiredStateDiffCachePrunerTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Tick_deletes_only_expired_rows_and_keeps_live_rows()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        var now = new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc);

        var expiredId = await SeedCacheAsync(rackId, createdAt: now.AddHours(-2), expiresAt: now.AddHours(-1));
        var liveId = await SeedCacheAsync(rackId, createdAt: now.AddMinutes(-30), expiresAt: now.AddHours(1));
        var neverExpiresId = await SeedCacheAsync(rackId, createdAt: now.AddHours(-5), expiresAt: null);

        var pruner = CreatePruner(now);
        var pruned = await pruner.TickAsync(default);

        pruned.Should().Be(1);
        await using var verify = _fixture.CreateContext();
        var remaining = await verify.DesiredStateCandidateDiffCaches
            .Where(c => c.RackId == rackId).Select(c => c.Id).ToListAsync();
        remaining.Should().BeEquivalentTo(new[] { liveId, neverExpiresId });
        (await verify.DesiredStateCandidateDiffCaches.AnyAsync(c => c.Id == expiredId)).Should().BeFalse();
    }

    [Fact]
    public async Task Tick_with_no_expired_rows_deletes_nothing()
    {
        await _fixture.MigrateAsync();
        var rackId = await SeedRackAsync();
        var now = new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc);

        await SeedCacheAsync(rackId, createdAt: now, expiresAt: now.AddHours(1));

        var pruned = await CreatePruner(now).TickAsync(default);

        pruned.Should().Be(0);
    }

    private DesiredStateDiffCachePruner CreatePruner(DateTime nowUtc)
    {
        var services = new ServiceCollection();
        services.AddDbContext<CaissonDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        var provider = services.BuildServiceProvider();

        return new DesiredStateDiffCachePruner(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FixedTimeProvider(nowUtc),
            MsOptions.Create(new DesiredStateDiffCacheOptions { PruneBatchSize = 500 }),
            NullLogger<DesiredStateDiffCachePruner>.Instance);
    }

    private async Task<Guid> SeedRackAsync()
    {
        var rackId = Guid.NewGuid();
        await using var context = _fixture.CreateContext();
        context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "Seed Rack", DateTime.UtcNow));
        await context.SaveChangesAsync();
        return rackId;
    }

    private async Task<Guid> SeedCacheAsync(Guid rackId, DateTime createdAt, DateTime? expiresAt)
    {
        var id = Guid.NewGuid();
        await using var context = _fixture.CreateContext();
        context.DesiredStateCandidateDiffCaches.Add(new DesiredStateCandidateDiffCache(
            id, rackId, Guid.NewGuid(), Hex(), Hex(), "@@ -1,1 +1,1 @@\n-a\n+b\n", "{\"baselineCommitSha\":null,\"changes\":[]}",
            "tester", createdAt, expiresAt));
        await context.SaveChangesAsync();
        return id;
    }

    private static string Hex() => (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTime now) => _now = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
