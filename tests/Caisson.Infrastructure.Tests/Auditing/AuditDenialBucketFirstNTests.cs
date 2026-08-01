using Caisson.Api.Auditing;
using Caisson.Api.Options;
using Caisson.Domain.Enums;
using Caisson.Infrastructure.Persistence.Queries;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Caisson.Infrastructure.Tests.Auditing;

/// <summary>
/// Proves the Tier 2 (durable-first-N) bucket contract against real PostgreSQL (story #308, ADR 0064):
/// the bucket-key upsert is idempotent, and concurrent cold requests — even from different "replicas"
/// (separate <see cref="CaissonDbContext"/> instances/writer instances) — serialize on the locked bucket
/// row so the first-N guarantee is GLOBAL, never per-instance.
/// </summary>
public sealed class AuditDenialBucketFirstNTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public AuditDenialBucketFirstNTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Upsert_first_sights_the_bucket_row_and_a_second_upsert_is_a_no_op()
    {
        await _fixture.MigrateAsync();
        var now = DateTime.UtcNow;
        var windowStart = now.Date;

        await using var context = _fixture.CreateContext();
        var inserted1 = await AuditDenialBucketQueries.UpsertBucketAsync(
            context, Guid.NewGuid(), "actor-1", ActorType.User, "GET /api/test", "403", windowStart, windowStart.AddMinutes(5), now, default);
        var inserted2 = await AuditDenialBucketQueries.UpsertBucketAsync(
            context, Guid.NewGuid(), "actor-1", ActorType.User, "GET /api/test", "403", windowStart, windowStart.AddMinutes(5), now, default);

        inserted1.Should().Be(1);
        inserted2.Should().Be(0);

        (await context.AuditDenialBuckets.CountAsync(b => b.ActorId == "actor-1" && b.Endpoint == "GET /api/test"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_denials_across_two_replicas_yield_exactly_N_verbatim_rows_globally()
    {
        await _fixture.MigrateAsync();
        await ResetAsync();

        const int firstN = 5;
        const int totalRequests = 20;
        var actorId = "flood-" + Guid.NewGuid().ToString("N")[..8];
        var options = Options.Create(new AuditDurabilityOptions { DenialFirstN = firstN, DenialWindowSeconds = 300 });

        // Two independent "replicas": each has its OWN DenialOverflowAccumulator (in-memory saturation
        // cache never shared across processes in production). Each call gets its OWN fresh
        // CaissonDbContext, exactly like one HTTP request gets its own scoped context in production — a
        // DbContext is not thread-safe, so concurrent requests must never share one.
        var accumulators = new[]
        {
            new DenialOverflowAccumulator(options, NullLogger<DenialOverflowAccumulator>.Instance),
            new DenialOverflowAccumulator(options, NullLogger<DenialOverflowAccumulator>.Instance),
        };

        async Task RecordOneAsync(int i)
        {
            await using var context = _fixture.CreateContext();
            var writer = new AuthorizationDenialAuditWriter(
                context, accumulators[i % 2], TimeProvider.System, options, NullLogger<AuthorizationDenialAuditWriter>.Instance);
            await writer.RecordDenialAsync(
                ActorType.User, actorId, "GET /api/test", "403", rackId: null, Guid.NewGuid(), detailsJson: null, default);
        }

        await Task.WhenAll(Enumerable.Range(0, totalRequests).Select(RecordOneAsync));

        await using var verify = _fixture.CreateContext();
        var verbatim = await verify.AuditEvents
            .Where(a => a.Action == "authorization.forbidden" && a.ActorId == actorId)
            .ToListAsync();
        verbatim.Should().HaveCount(firstN);

        var bucket = await verify.AuditDenialBuckets.SingleAsync(b => b.ActorId == actorId);
        bucket.DurableCount.Should().Be(firstN);
    }

    private async Task ResetAsync()
    {
        await using var context = _fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM audit_denial_bucket;");
    }
}
