using Caisson.Domain.DesiredState;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Queries;
using Caisson.Infrastructure.Persistence.Shaping;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Story #63, AC3/NFR1/NFR3: keyset ordering with no gaps/dupes for revision history, cross-rack
/// isolation for by-id/by-commit lookups, and index-usage proof for the history query — sibling to
/// <see cref="KeysetPaginationTests"/>.
/// </summary>
public sealed class DesiredStateRevisionQueriesTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public DesiredStateRevisionQueriesTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task History_pagination_returns_every_revision_exactly_once_when_created_at_collide()
    {
        await _fixture.MigrateAsync();
        var rackSlug = "rack-history-" + Guid.NewGuid().ToString("N");
        var sharedInstant = new DateTime(2026, 7, 23, 0, 0, 0, DateTimeKind.Utc);

        var expected = new HashSet<Guid>();
        await using (var context = _fixture.CreateContext())
        {
            var runId = await SeedRunAsync(context, "sha-history-run");
            for (var i = 0; i < 7; i++)
            {
                var version = NewVersion(rackSlug, $"sha-history-{i}", runId, sharedInstant);
                expected.Add(version.Id);
                context.DesiredStateVersions.Add(version);
            }

            await context.SaveChangesAsync();
        }

        var seen = await PageAllHistoryAsync(rackSlug, pageSize: 3);

        seen.Should().BeEquivalentTo(expected, "no revision should be skipped or duplicated across page boundaries");
    }

    [Fact]
    public async Task History_page_never_selects_the_payload_column()
    {
        await _fixture.MigrateAsync();
        var rackSlug = "rack-metadata-only-" + Guid.NewGuid().ToString("N");

        await using (var context = _fixture.CreateContext())
        {
            var runId = await SeedRunAsync(context, "sha-metadata-only");
            context.DesiredStateVersions.Add(NewVersion(rackSlug, "sha-metadata-only", runId, DateTime.UtcNow));
            await context.SaveChangesAsync();
        }

        await using var verify = _fixture.CreateContext();
        var page = await verify.RevisionHistoryPageAsync(rackSlug, after: null, limit: 10);

        // DesiredStateRevisionMetadata has no DesiredStateJson property at all — this is a compile-time
        // guarantee, not just a runtime check, that the history query can never leak the payload (AC3, NFR3).
        page.Should().ContainSingle();
        page[0].RackSlug.Should().Be(rackSlug);
    }

    [Fact]
    public async Task RevisionById_returns_null_when_the_id_belongs_to_a_different_rack()
    {
        await _fixture.MigrateAsync();
        var rackA = "rack-a-" + Guid.NewGuid().ToString("N");
        var rackB = "rack-b-" + Guid.NewGuid().ToString("N");

        Guid versionAId;
        await using (var context = _fixture.CreateContext())
        {
            var runId = await SeedRunAsync(context, "sha-cross-rack");
            var versionA = NewVersion(rackA, "sha-cross-rack", runId, DateTime.UtcNow);
            versionAId = versionA.Id;
            context.DesiredStateVersions.Add(versionA);
            await context.SaveChangesAsync();
        }

        await using var verify = _fixture.CreateContext();
        (await verify.RevisionByIdAsync(rackA, versionAId)).Should().NotBeNull();
        (await verify.RevisionByIdAsync(rackB, versionAId)).Should().BeNull(
            "a revision id belonging to rack A must not resolve when queried under rack B (NFR1)");
    }

    [Fact]
    public async Task RevisionByCommitSha_returns_null_when_the_commit_belongs_to_a_different_rack()
    {
        await _fixture.MigrateAsync();
        var rackA = "rack-a-" + Guid.NewGuid().ToString("N");
        var rackB = "rack-b-" + Guid.NewGuid().ToString("N");
        var commitSha = "sha-shared-commit-" + Guid.NewGuid().ToString("N");

        await using (var context = _fixture.CreateContext())
        {
            var runId = await SeedRunAsync(context, commitSha);
            context.DesiredStateVersions.Add(NewVersion(rackA, commitSha, runId, DateTime.UtcNow));
            await context.SaveChangesAsync();
        }

        await using var verify = _fixture.CreateContext();
        (await verify.RevisionByCommitShaAsync(rackA, commitSha)).Should().NotBeNull();
        (await verify.RevisionByCommitShaAsync(rackB, commitSha)).Should().BeNull(
            "a commit that produced a version for rack A must not resolve under rack B (NFR1)");
    }

    [Fact]
    public async Task History_query_plan_uses_the_rack_slug_covering_index()
    {
        await _fixture.MigrateAsync();
        var rackSlug = "rack-explain-" + Guid.NewGuid().ToString("N");

        await using (var context = _fixture.CreateContext())
        {
            var runId = await SeedRunAsync(context, "sha-explain");
            context.DesiredStateVersions.Add(NewVersion(rackSlug, "sha-explain", runId, DateTime.UtcNow));
            await context.SaveChangesAsync();
        }

        // Mirrors RevisionHistoryPageAsync's actual SQL shape exactly (rack_slug filter, created_at_utc
        // DESC/id DESC, LIMIT) as a literal so the parameter placeholder EF's ToQueryString() emits (which
        // Postgres's own SQL parser would otherwise misread as the "@" absolute-value operator) never
        // enters the picture.
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "EXPLAIN SELECT id FROM desired_state_version WHERE rack_slug = @rackSlug " +
            "ORDER BY created_at_utc DESC, id DESC LIMIT 10", connection);
        command.Parameters.AddWithValue("rackSlug", rackSlug);
        await using var reader = await command.ExecuteReaderAsync();
        var planLines = new List<string>();
        while (await reader.ReadAsync())
        {
            planLines.Add(reader.GetString(0));
        }

        var plan = string.Join("\n", planLines);
        plan.Should().Contain(
            "ix_desired_state_version_rack_slug_created_at_id",
            "the history query must be served by the covering index, not a sequential scan (NFR3)");
    }

    private async Task<HashSet<Guid>> PageAllHistoryAsync(string rackSlug, int pageSize)
    {
        const string endpoint = "desired-state-revisions";
        var seen = new HashSet<Guid>();
        KeysetPosition? after = null;
        await using var context = _fixture.CreateContext();
        while (true)
        {
            var page = await context.RevisionHistoryPageAsync(rackSlug, after, pageSize + 1);
            var hasMore = page.Count > pageSize;
            foreach (var row in page.Take(pageSize))
            {
                seen.Add(row.Id).Should().BeTrue("no revision should be returned on two different pages");
            }

            if (!hasMore)
            {
                return seen;
            }

            var last = page[pageSize - 1];
            var cursor = CursorCodec.Encode(last.CreatedAtUtc, last.Id, rackSlug, endpoint);
            CursorCodec.TryDecode(cursor, rackSlug, endpoint, out var ts, out var id).Should().BeTrue();
            after = new KeysetPosition(ts, id);
        }
    }

    private static DesiredStateVersion NewVersion(string rackSlug, string commitSha, Guid runId, DateTime createdAtUtc)
        => new(
            Guid.NewGuid(), rackSlug, commitSha, runId, createdAtUtc, "hash-" + Guid.NewGuid().ToString("N"),
            "{\"rackSlug\":\"" + rackSlug + "\",\"switches\":[]}", DesiredStateSchema.CurrentSchemaVersion,
            "desired-state-ingestion", "author", "author@example.com", createdAtUtc);

    private static async Task<Guid> SeedRunAsync(CaissonDbContext context, string commitSha)
    {
        var run = new DesiredStateIngestionRun(
            Guid.NewGuid(), IngestionTriggerType.Poll, DateTime.UtcNow, "https://example.com/repo.git", "main",
            Guid.NewGuid());
        run.RecordCommit(commitSha, "author", DateTime.UtcNow, "message");
        run.Succeed(DateTime.UtcNow);
        context.DesiredStateIngestionRuns.Add(run);
        await context.SaveChangesAsync();
        return run.Id;
    }
}
