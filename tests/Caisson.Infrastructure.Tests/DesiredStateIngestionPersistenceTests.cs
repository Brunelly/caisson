using Caisson.Domain.DesiredState;
using Caisson.Infrastructure.Persistence.Queries;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Story #62 persistence proofs (AC1/AC3/NFR2/NFR3/NFR7): append-only enforcement for the four
/// IAppendOnly desired-state tables, the two partial-unique indexes rejecting duplicate inserts at the
/// DB level, and <see cref="LatestDesiredStateVersionQueries"/> resolving newest-per-rack correctly —
/// including the partial-accept scenario where one rack's active version is untouched by another
/// rack's new version.
/// </summary>
public sealed class DesiredStateIngestionPersistenceTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public DesiredStateIngestionPersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Mutating_a_persisted_desired_state_version_is_rejected()
    {
        await _fixture.MigrateAsync();
        var (_, versionId) = await SeedRunAndVersionAsync("rack-1");

        await using var context = _fixture.CreateContext();
        var version = await context.DesiredStateVersions.SingleAsync(v => v.Id == versionId);
        context.Entry(version).Property(v => v.ContentHash).CurrentValue = "tampered";

        var act = async () => await context.SaveChangesAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Deleting_a_persisted_desired_state_version_is_rejected()
    {
        await _fixture.MigrateAsync();
        var (_, versionId) = await SeedRunAndVersionAsync("rack-2");

        await using var context = _fixture.CreateContext();
        var version = await context.DesiredStateVersions.SingleAsync(v => v.Id == versionId);
        context.DesiredStateVersions.Remove(version);

        var act = async () => await context.SaveChangesAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Mutating_a_persisted_desired_port_intent_is_rejected()
    {
        await _fixture.MigrateAsync();
        var (_, versionId) = await SeedRunAndVersionAsync("rack-3");
        var portId = await SeedTreeAsync(versionId);

        await using var context = _fixture.CreateContext();
        var port = await context.DesiredPortIntents.SingleAsync(p => p.Id == portId);
        context.Entry(port).Property(p => p.AccessVlan).CurrentValue = 999;

        var act = async () => await context.SaveChangesAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Deleting_a_persisted_validation_error_is_rejected()
    {
        await _fixture.MigrateAsync();
        var runId = await SeedRunAsync();
        var errorId = Guid.NewGuid();

        await using (var context = _fixture.CreateContext())
        {
            context.DesiredStateValidationErrors.Add(new DesiredStateValidationError(
                errorId, runId, DateTime.UtcNow, "rack-x", "desired-state/racks/rack-x.yaml", "/switches/0/ports/0/accessVlan",
                "accessVlan out of range"));
            await context.SaveChangesAsync();
        }

        await using var deleteContext = _fixture.CreateContext();
        var error = await deleteContext.DesiredStateValidationErrors.SingleAsync(e => e.Id == errorId);
        deleteContext.DesiredStateValidationErrors.Remove(error);

        var act = async () => await deleteContext.SaveChangesAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Duplicate_commit_sha_for_a_live_or_processed_run_is_rejected_at_the_db_level()
    {
        await _fixture.MigrateAsync();
        await SeedRunAsync(commitSha: "shared-sha", status: IngestionRunStatus.Succeeded);

        await using var context = _fixture.CreateContext();
        var run = new DesiredStateIngestionRun(
            Guid.NewGuid(), IngestionTriggerType.Poll, DateTime.UtcNow, "https://example.com/repo.git",
            "main", Guid.NewGuid());
        run.RecordCommit("shared-sha", "author", DateTime.UtcNow, "message");
        context.DesiredStateIngestionRuns.Add(run);

        var act = async () => await context.SaveChangesAsync();

        var exception = await act.Should().ThrowAsync<DbUpdateException>();
        exception.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Duplicate_webhook_delivery_id_is_rejected_at_the_db_level()
    {
        await _fixture.MigrateAsync();

        await using (var context = _fixture.CreateContext())
        {
            var first = new DesiredStateIngestionRun(
                Guid.NewGuid(), IngestionTriggerType.Webhook, DateTime.UtcNow, "https://example.com/repo.git",
                "main", Guid.NewGuid(), webhookDeliveryId: "delivery-1");
            context.DesiredStateIngestionRuns.Add(first);
            await context.SaveChangesAsync();
        }

        await using var context2 = _fixture.CreateContext();
        var second = new DesiredStateIngestionRun(
            Guid.NewGuid(), IngestionTriggerType.Webhook, DateTime.UtcNow, "https://example.com/repo.git",
            "main", Guid.NewGuid(), webhookDeliveryId: "delivery-1");
        context2.DesiredStateIngestionRuns.Add(second);

        var act = async () => await context2.SaveChangesAsync();

        var exception = await act.Should().ThrowAsync<DbUpdateException>();
        exception.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task An_infra_failed_run_does_not_block_reprocessing_the_same_commit()
    {
        await _fixture.MigrateAsync();
        await SeedRunAsync(commitSha: "retriable-sha", status: IngestionRunStatus.Failed);

        await using var context = _fixture.CreateContext();
        var retry = new DesiredStateIngestionRun(
            Guid.NewGuid(), IngestionTriggerType.Poll, DateTime.UtcNow, "https://example.com/repo.git",
            "main", Guid.NewGuid());
        retry.RecordCommit("retriable-sha", "author", DateTime.UtcNow, "message");
        context.DesiredStateIngestionRuns.Add(retry);

        var act = async () => await context.SaveChangesAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Latest_version_query_returns_the_newest_row_per_rack()
    {
        await _fixture.MigrateAsync();
        var (_, olderVersionId) = await SeedRunAndVersionAsync("rack-newest", createdOffset: TimeSpan.FromMinutes(-10));
        var (_, newerVersionId) = await SeedRunAndVersionAsync("rack-newest", createdOffset: TimeSpan.Zero);

        await using var context = _fixture.CreateContext();
        var active = await context.ActiveVersionForRackAsync("rack-newest");

        active.Should().NotBeNull();
        active!.Id.Should().Be(newerVersionId);
        active.Id.Should().NotBe(olderVersionId);
    }

    [Fact]
    public async Task Partial_accept_leaves_the_other_racks_active_version_intact()
    {
        await _fixture.MigrateAsync();
        var (_, rackAVersionId) = await SeedRunAndVersionAsync("rack-a");
        var (_, rackBVersionId) = await SeedRunAndVersionAsync("rack-b");

        // A later commit only produces a new version for rack-a (rack-b failed validation and is
        // untouched — Q3's partial-accept policy).
        var (_, rackANewVersionId) = await SeedRunAndVersionAsync("rack-a");

        await using var context = _fixture.CreateContext();
        var activeA = await context.ActiveVersionForRackAsync("rack-a");
        var activeB = await context.ActiveVersionForRackAsync("rack-b");

        activeA!.Id.Should().Be(rackANewVersionId);
        activeA.Id.Should().NotBe(rackAVersionId);
        activeB!.Id.Should().Be(rackBVersionId);
    }

    [Fact]
    public async Task Latest_version_per_rack_returns_exactly_one_row_per_rack_slug()
    {
        await _fixture.MigrateAsync();
        var rackXSlug = "rack-x-" + Guid.NewGuid().ToString("N");
        var rackYSlug = "rack-y-" + Guid.NewGuid().ToString("N");
        await SeedRunAndVersionAsync(rackXSlug, createdOffset: TimeSpan.FromMinutes(-5));
        var (_, rackXNewest) = await SeedRunAndVersionAsync(rackXSlug, createdOffset: TimeSpan.Zero);
        var (_, rackY) = await SeedRunAndVersionAsync(rackYSlug);

        await using var context = _fixture.CreateContext();
        // The class fixture shares one database across every test method, so filter to just the rack
        // slugs this test seeded before asserting "exactly one row per rack slug".
        var latest = (await context.LatestVersionPerRackAsync())
            .Where(v => v.RackSlug == rackXSlug || v.RackSlug == rackYSlug)
            .ToList();

        latest.Should().HaveCount(2);
        latest.Should().Contain(v => v.Id == rackXNewest);
        latest.Should().Contain(v => v.Id == rackY);
    }

    [Fact]
    public async Task Active_version_with_tree_loads_the_full_rack_switch_port_graph()
    {
        await _fixture.MigrateAsync();
        var (_, versionId) = await SeedRunAndVersionAsync("rack-tree");
        await SeedTreeAsync(versionId);

        await using var context = _fixture.CreateContext();
        var tree = await context.ActiveVersionWithTreeAsync("rack-tree");

        tree.Should().NotBeNull();
        tree!.Rack.DesiredStateVersionId.Should().Be(versionId);
        tree.Switches.Should().ContainSingle();
        tree.Ports.Should().ContainSingle();
    }

    private async Task<Guid> SeedRunAsync(
        string? commitSha = null, IngestionRunStatus status = IngestionRunStatus.Succeeded)
    {
        await using var context = _fixture.CreateContext();
        var run = new DesiredStateIngestionRun(
            Guid.NewGuid(), IngestionTriggerType.Poll, DateTime.UtcNow, "https://example.com/repo.git",
            "main", Guid.NewGuid());
        if (commitSha is not null)
        {
            run.RecordCommit(commitSha, "author", DateTime.UtcNow, "message");
        }

        switch (status)
        {
            case IngestionRunStatus.Succeeded:
                run.Succeed(DateTime.UtcNow);
                break;
            case IngestionRunStatus.Failed:
                run.Fail(DateTime.UtcNow, IngestionErrorCategory.Network, "network error");
                break;
        }

        context.DesiredStateIngestionRuns.Add(run);
        await context.SaveChangesAsync();
        return run.Id;
    }

    private async Task<(Guid RunId, Guid VersionId)> SeedRunAndVersionAsync(
        string rackSlug, TimeSpan createdOffset = default)
    {
        // Each call seeds its own run (a real commit can touch several racks in one run, but a fresh,
        // globally-unique commit SHA per call keeps these class-fixture-shared-database tests from
        // colliding with each other on the ux_desired_state_ingestion_run_commit_sha index).
        var commitSha = "sha-" + Guid.NewGuid().ToString("N");
        var runId = await SeedRunAsync(commitSha);

        await using var context = _fixture.CreateContext();
        var version = new DesiredStateVersion(
            Guid.NewGuid(), rackSlug, commitSha, runId, DateTime.UtcNow.Add(createdOffset), "hash-" + commitSha);
        context.DesiredStateVersions.Add(version);
        await context.SaveChangesAsync();
        return (runId, version.Id);
    }

    private async Task<Guid> SeedTreeAsync(Guid versionId)
    {
        await using var context = _fixture.CreateContext();
        var rack = new DesiredRackIntent(Guid.NewGuid(), versionId, "rack-tree", "rack-tree");
        var switchIntent = new DesiredSwitchIntent(Guid.NewGuid(), rack.Id, "sw-a", "rack-tree|sw-a");
        var port = new DesiredPortIntent(Guid.NewGuid(), switchIntent.Id, "eth0", "rack-tree|sw-a|eth0", 100);

        context.DesiredRackIntents.Add(rack);
        context.DesiredSwitchIntents.Add(switchIntent);
        context.DesiredPortIntents.Add(port);
        await context.SaveChangesAsync();
        return port.Id;
    }
}
