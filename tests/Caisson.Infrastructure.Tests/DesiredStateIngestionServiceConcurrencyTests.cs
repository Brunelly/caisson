using Caisson.Domain.DesiredState;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Ingestion;
using Caisson.Infrastructure.Persistence.Queries;
using Caisson.Ingestion.Ingestion;
using Caisson.Ingestion.Observability;
using Caisson.Ingestion.Options;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Postgres-backed tests for story #62's shared idempotent <c>RunAsync</c> seam (NFR2/NFR3) and Q3's
/// partial-accept policy — mirroring <c>DiscoveryJobConcurrencyTests</c>'s pattern of exercising the real
/// orchestration-layer service against a real database, using a <see cref="FakeGitRepositoryProvider"/>
/// so no real Git repository is touched.
/// </summary>
public sealed class DesiredStateIngestionServiceConcurrencyTests : IClassFixture<PostgresFixture>
{
    private const string RepoUrl = "https://example.com/repo.git";

    private static string RackYaml(string rackSlug, int vlan = 100) => $"""
        rackSlug: {rackSlug}
        switches:
          - name: switch-a
            ports:
              - name: eth0
                accessVlan: {vlan}
        """;

    private const string InvalidRackYaml = """
        rackSlug: rack-invalid
        switches:
          - name: switch-a
            ports:
              - name: eth0
                accessVlan: 5000
        """;

    private readonly PostgresFixture _fixture;

    public DesiredStateIngestionServiceConcurrencyTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Two_concurrent_runs_for_the_same_commit_yield_one_started_and_one_replay()
    {
        await _fixture.MigrateAsync();
        var git = new FakeGitRepositoryProvider();
        git.NextCommit = new("sha-race", "author", DateTime.UtcNow, "message");
        git.SetFile("desired-state/racks/rack-a.yaml", RackYaml("rack-a"));

        await using var contextA = _fixture.CreateContext();
        await using var contextB = _fixture.CreateContext();

        var runA = Service(contextA, git).RunAsync(IngestionTriggerType.Poll, null, Guid.NewGuid(), default);
        var runB = Service(contextB, git).RunAsync(IngestionTriggerType.Webhook, null, Guid.NewGuid(), default);
        var results = await Task.WhenAll(runA, runB);

        results.Count(r => r.Disposition == IngestionRunDisposition.Started).Should().Be(1);
        results.Count(r => r.Disposition == IngestionRunDisposition.IdempotentReplay).Should().Be(1);
        results.Select(r => r.RunId).Distinct().Should().ContainSingle();

        await using var verify = _fixture.CreateContext();
        (await verify.DesiredStateIngestionRuns.CountAsync(r => r.CommitSha == "sha-race")).Should().Be(1);
    }

    [Fact]
    public async Task Duplicate_webhook_delivery_id_replays_the_same_run()
    {
        await _fixture.MigrateAsync();
        var git = new FakeGitRepositoryProvider();
        git.NextCommit = new("sha-delivery", "author", DateTime.UtcNow, "message");
        git.SetFile("desired-state/racks/rack-b.yaml", RackYaml("rack-b"));

        IngestionRunResult first, second;
        await using (var context = _fixture.CreateContext())
        {
            first = await Service(context, git).RunAsync(IngestionTriggerType.Webhook, "delivery-1", Guid.NewGuid(), default);
        }

        await using (var context = _fixture.CreateContext())
        {
            second = await Service(context, git).RunAsync(IngestionTriggerType.Webhook, "delivery-1", Guid.NewGuid(), default);
        }

        first.Disposition.Should().Be(IngestionRunDisposition.Started);
        second.Disposition.Should().Be(IngestionRunDisposition.IdempotentReplay);
        second.RunId.Should().Be(first.RunId);

        await using var verify = _fixture.CreateContext();
        (await verify.DesiredStateIngestionRuns.CountAsync(r => r.WebhookDeliveryId == "delivery-1")).Should().Be(1);
    }

    [Fact]
    public async Task Valid_rack_file_is_materialised_and_run_succeeds()
    {
        await _fixture.MigrateAsync();
        var git = new FakeGitRepositoryProvider();
        git.NextCommit = new("sha-valid", "author", DateTime.UtcNow, "message");
        git.SetFile("desired-state/racks/rack-valid.yaml", RackYaml("rack-valid", vlan: 42));

        await using var context = _fixture.CreateContext();
        var result = await Service(context, git).RunAsync(IngestionTriggerType.Poll, null, Guid.NewGuid(), default);

        await using var verify = _fixture.CreateContext();
        var run = await verify.DesiredStateIngestionRuns.SingleAsync(r => r.Id == result.RunId);
        run.Status.Should().Be(IngestionRunStatus.Succeeded);

        var active = await verify.ActiveVersionForRackAsync("rack-valid");
        active.Should().NotBeNull();
        active!.CommitSha.Should().Be("sha-valid");

        var tree = await verify.ActiveVersionWithTreeAsync("rack-valid");
        tree!.Ports.Should().ContainSingle().Which.AccessVlan.Should().Be(42);

        // Story #63, AC1: the new revision-metadata fields are populated (author is nullable-tolerant —
        // FakeGitRepositoryProvider's NextCommit still carries a plain author name). Parsed rather than
        // substring-matched: Postgres's jsonb column re-canonicalizes on storage (different key order/
        // whitespace than the serializer's own output), so only the round-tripped VALUE is asserted.
        using (var payload = System.Text.Json.JsonDocument.Parse(active.DesiredStateJson))
        {
            payload.RootElement.GetProperty("rackSlug").GetString().Should().Be("rack-valid");
        }

        active.SchemaVersion.Should().Be(DesiredStateSchema.CurrentSchemaVersion);
        active.IngestedBy.Should().NotBeNullOrEmpty();
        active.AuthorName.Should().Be("author");

        // Story #63, AC5: the ingestion audit event is written in the same atomic save as the version.
        var audit = await verify.AuditEvents.SingleAsync(a => a.TargetId == active.Id.ToString());
        audit.Action.Should().Be("desired-state.revision.ingested");
        audit.TargetType.Should().Be("desired-state-version");
        audit.CorrelationId.Should().Be(run.CorrelationId);
    }

    [Fact]
    public async Task Invalid_rack_file_produces_validation_errors_and_run_is_validation_failed()
    {
        await _fixture.MigrateAsync();
        var git = new FakeGitRepositoryProvider();
        git.NextCommit = new("sha-invalid", "author", DateTime.UtcNow, "message");
        git.SetFile("desired-state/racks/rack-invalid.yaml", InvalidRackYaml);

        await using var context = _fixture.CreateContext();
        var result = await Service(context, git).RunAsync(IngestionTriggerType.Poll, null, Guid.NewGuid(), default);

        await using var verify = _fixture.CreateContext();
        var run = await verify.DesiredStateIngestionRuns.SingleAsync(r => r.Id == result.RunId);
        run.Status.Should().Be(IngestionRunStatus.ValidationFailed);
        run.ErrorCategory.Should().Be(IngestionErrorCategory.Validation);

        var errors = await verify.DesiredStateValidationErrors.Where(e => e.IngestionRunId == run.Id).ToListAsync();
        errors.Should().ContainSingle(e => e.Location == "/switches/0/ports/0/accessVlan");

        (await verify.ActiveVersionForRackAsync("rack-invalid")).Should().BeNull();
    }

    [Fact]
    public async Task Partial_accept_one_valid_one_invalid_rack_marks_run_partially_succeeded()
    {
        await _fixture.MigrateAsync();
        var git = new FakeGitRepositoryProvider();
        git.NextCommit = new("sha-partial", "author", DateTime.UtcNow, "message");
        git.SetFile("desired-state/racks/rack-partial-ok.yaml", RackYaml("rack-partial-ok"));
        git.SetFile("desired-state/racks/rack-partial-bad.yaml", InvalidRackYaml.Replace("rack-invalid", "rack-partial-bad"));

        await using var context = _fixture.CreateContext();
        var result = await Service(context, git).RunAsync(IngestionTriggerType.Poll, null, Guid.NewGuid(), default);

        await using var verify = _fixture.CreateContext();
        var run = await verify.DesiredStateIngestionRuns.SingleAsync(r => r.Id == result.RunId);
        run.Status.Should().Be(IngestionRunStatus.PartiallySucceeded);

        (await verify.ActiveVersionForRackAsync("rack-partial-ok")).Should().NotBeNull();
        (await verify.ActiveVersionForRackAsync("rack-partial-bad")).Should().BeNull();
    }

    [Fact]
    public async Task A_later_commit_that_invalidates_one_rack_leaves_its_previous_version_active()
    {
        // AC3 scenario 2: a commit modifies only one rack file; only that rack receives a new active
        // version, and a LATER commit that breaks a different, previously-valid rack leaves that rack's
        // earlier version untouched while unrelated racks still update.
        await _fixture.MigrateAsync();
        var rackKeptSlug = "rack-kept-" + Guid.NewGuid().ToString("N");
        var rackChangedSlug = "rack-changed-" + Guid.NewGuid().ToString("N");
        var git = new FakeGitRepositoryProvider();

        git.NextCommit = new("sha-first", "author", DateTime.UtcNow, "message");
        git.SetFile($"desired-state/racks/{rackKeptSlug}.yaml", RackYaml(rackKeptSlug, vlan: 10));
        git.SetFile($"desired-state/racks/{rackChangedSlug}.yaml", RackYaml(rackChangedSlug, vlan: 20));

        Guid firstKeptVersionId;
        Guid firstChangedVersionId;
        await using (var context = _fixture.CreateContext())
        {
            await Service(context, git).RunAsync(IngestionTriggerType.Poll, null, Guid.NewGuid(), default);
        }

        await using (var verify = _fixture.CreateContext())
        {
            firstKeptVersionId = (await verify.ActiveVersionForRackAsync(rackKeptSlug))!.Id;
            firstChangedVersionId = (await verify.ActiveVersionForRackAsync(rackChangedSlug))!.Id;
        }

        // Second commit: rack-kept is untouched (same content); rack-changed's file becomes invalid.
        git.NextCommit = new("sha-second", "author", DateTime.UtcNow, "message");
        git.SetFile(
            $"desired-state/racks/{rackChangedSlug}.yaml",
            InvalidRackYaml.Replace("rack-invalid", rackChangedSlug));

        await using (var context = _fixture.CreateContext())
        {
            await Service(context, git).RunAsync(IngestionTriggerType.Poll, null, Guid.NewGuid(), default);
        }

        await using var finalVerify = _fixture.CreateContext();
        var keptActive = await finalVerify.ActiveVersionForRackAsync(rackKeptSlug);
        var changedActive = await finalVerify.ActiveVersionForRackAsync(rackChangedSlug);

        keptActive!.Id.Should().Be(firstKeptVersionId, "the unaffected rack's active version must not change");
        changedActive!.Id.Should().Be(
            firstChangedVersionId,
            "the invalidated rack keeps its previous valid version active — the second commit's " +
            "invalid file only produces validation-error rows, never a new version");
    }

    [Fact]
    public async Task Unchanged_file_content_does_not_materialise_a_new_version()
    {
        await _fixture.MigrateAsync();
        var rackSlug = "rack-unchanged-" + Guid.NewGuid().ToString("N");
        var git = new FakeGitRepositoryProvider();
        git.NextCommit = new("sha-a", "author", DateTime.UtcNow, "message");
        git.SetFile($"desired-state/racks/{rackSlug}.yaml", RackYaml(rackSlug));

        Guid firstVersionId;
        await using (var context = _fixture.CreateContext())
        {
            await Service(context, git).RunAsync(IngestionTriggerType.Poll, null, Guid.NewGuid(), default);
        }

        await using (var verify = _fixture.CreateContext())
        {
            firstVersionId = (await verify.ActiveVersionForRackAsync(rackSlug))!.Id;
        }

        // A second commit re-submits the SAME file content unchanged.
        git.NextCommit = new("sha-b", "author", DateTime.UtcNow, "message");
        await using (var context = _fixture.CreateContext())
        {
            var result = await Service(context, git).RunAsync(IngestionTriggerType.Poll, null, Guid.NewGuid(), default);
            var run = await context.DesiredStateIngestionRuns.SingleAsync(r => r.Id == result.RunId);
            run.Status.Should().Be(IngestionRunStatus.Succeeded);
        }

        await using var finalVerify = _fixture.CreateContext();
        (await finalVerify.ActiveVersionForRackAsync(rackSlug))!.Id.Should().Be(firstVersionId);
        (await finalVerify.DesiredStateVersions.CountAsync(v => v.RackSlug == rackSlug)).Should().Be(1);

        // Story #63, AC5: an unchanged-content replay must not double-write the ingestion audit event.
        (await finalVerify.AuditEvents.CountAsync(
            a => a.Action == "desired-state.revision.ingested" && a.TargetId == firstVersionId.ToString())).Should().Be(1);
    }

    [Fact]
    public async Task A_commit_fetch_failure_persists_a_failed_run_with_no_commit_sha()
    {
        await _fixture.MigrateAsync();
        var git = new FakeGitRepositoryProvider { FetchException = new InvalidOperationException("network unreachable") };

        await using var context = _fixture.CreateContext();
        var result = await Service(context, git).RunAsync(IngestionTriggerType.Poll, null, Guid.NewGuid(), default);

        await using var verify = _fixture.CreateContext();
        var run = await verify.DesiredStateIngestionRuns.SingleAsync(r => r.Id == result.RunId);
        run.Status.Should().Be(IngestionRunStatus.Failed);
        run.ErrorCategory.Should().Be(IngestionErrorCategory.Network);
        run.CommitSha.Should().BeNull();
    }

    [Fact]
    public async Task An_infra_failed_run_does_not_block_a_subsequent_successful_run_for_the_same_commit()
    {
        await _fixture.MigrateAsync();
        var rackSlug = "rack-retry-" + Guid.NewGuid().ToString("N");
        var git = new FakeGitRepositoryProvider
        {
            NextCommit = new("sha-retry", "author", DateTime.UtcNow, "message"),
            FetchException = new InvalidOperationException("transient network blip"),
        };
        git.SetFile($"desired-state/racks/{rackSlug}.yaml", RackYaml(rackSlug));

        await using (var context = _fixture.CreateContext())
        {
            var failed = await Service(context, git).RunAsync(IngestionTriggerType.Poll, null, Guid.NewGuid(), default);
            failed.Disposition.Should().Be(IngestionRunDisposition.Started);
        }

        git.FetchException = null;
        await using (var context = _fixture.CreateContext())
        {
            var retried = await Service(context, git).RunAsync(IngestionTriggerType.Poll, null, Guid.NewGuid(), default);
            var run = await context.DesiredStateIngestionRuns.SingleAsync(r => r.Id == retried.RunId);
            run.Status.Should().Be(IngestionRunStatus.Succeeded);
        }

        await using var verify = _fixture.CreateContext();
        (await verify.ActiveVersionForRackAsync(rackSlug)).Should().NotBeNull();
    }

    private static DesiredStateIngestionService Service(CaissonDbContext context, FakeGitRepositoryProvider git)
        => new(
            context,
            git,
            new GuidTopologyIdGenerator(),
            TimeProvider.System,
            Options.Create(new GitIngestionOptions { Enabled = true, RepoUrl = RepoUrl }),
            new GitIngestionMetrics(),
            new Caisson.Infrastructure.Persistence.Drift.NoOpDriftRecomputeSignal(),
            NullLogger<DesiredStateIngestionService>.Instance);
}
