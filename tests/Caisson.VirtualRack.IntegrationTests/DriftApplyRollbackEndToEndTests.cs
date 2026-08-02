using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Caisson.Domain.DesiredState;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Drift;
using Caisson.Infrastructure.Persistence.Ingestion;
using Caisson.Ingestion.Git;
using Caisson.Ingestion.Ingestion;
using Caisson.Ingestion.Observability;
using Caisson.Ingestion.Options;
using Caisson.VirtualRack.Fixtures;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caisson.VirtualRack.IntegrationTests;

/// <summary>
/// Task #115: auto-rollback via a scripted withheld-confirmation driver, proven at the ORCHESTRATION
/// layer — see ADR 0035 for why this is a distinct layer from the already-proven driver-level rollback
/// (<c>SetAccessVlanIntegrationTests</c>, not duplicated here) and for the terminal-status-shape and
/// severity decisions this suite relies on. The rack is created under
/// <see cref="VirtualRackApiFactory.RackScenario.WithheldRollback"/>, whose switch device resolves to the
/// additively-registered <c>ScriptedWithheldMutatingDriver</c> (write) and <c>MockWithheldReadDriverFactory</c>
/// (read, a real pass-through) instead of the real MikroTik factories.
/// </summary>
[Collection(VirtualRackCollection.Name)]
public sealed class DriftApplyRollbackEndToEndTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly VirtualRackApiFactory _factory;
    private string _originPath = string.Empty;
    private string _mirrorPath = string.Empty;
    private string _branch = string.Empty;

    public DriftApplyRollbackEndToEndTests(VirtualRackApiFactory factory) => _factory = factory;

    public Task InitializeAsync()
    {
        _originPath = Directory.CreateTempSubdirectory("caisson-drift-rollback-e2e-origin-").FullName;
        _mirrorPath = Path.Combine(Directory.CreateTempSubdirectory("caisson-drift-rollback-e2e-mirror-").FullName, "mirror.git");
        Repository.Init(_originPath);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        TryDeleteDirectory(_originPath);
        TryDeleteDirectory(Path.GetDirectoryName(_mirrorPath));
        return Task.CompletedTask;
    }

    [SkippableFact]
    public async Task Withheld_confirmation_auto_rolls_back_makes_one_device_call_and_the_next_discovery_still_shows_the_drift()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        // The write-capable simulator is shared across every device-mutating test in this collection
        // (sequential, unspecified order) — force ether1 back to its baseline PVID before seeding (ADR 0035 §7).
        _factory.ResetSwitchPortAccessVlanForTest(VirtualRackDefinition.CleanPort, VirtualRackDefinition.CleanVlan);
        var callCountBefore = _factory.WithheldDriverCallCount;

        var rackSlug = "vrack-rollback-" + Guid.NewGuid().ToString("N");
        var rackId = await _factory.CreateRackAsync(
            externalKey: rackSlug, scenario: VirtualRackApiFactory.RackScenario.WithheldRollback);

        CommitFile(
            $"desired-state/racks/{rackSlug}.yaml",
            DesiredStateYamlRenderer.RenderWithMismatchedVlan(rackSlug),
            "seed mismatched desired state");
        (await RunDesiredStateIngestionAsync()).Disposition.Should().Be(IngestionRunDisposition.Started);

        var firstSnapshotId = await TriggerDiscoveryAndAwaitSucceededAsync(rackId);
        var latest = await PollUntilDriftReadyForSnapshotAsync(rackId, firstSnapshotId);
        var mismatch = latest.GetProperty("items").GetProperty("items").EnumerateArray()
            .Should().ContainSingle(i => i.GetProperty("driftType").GetString() == "AccessVlanMismatch").Subject;
        var driftItemId = mismatch.GetProperty("driftItemId").GetGuid();

        // Story #173 gate (AC4): apply is blocked until the exact candidate's PR is merged. Simulate that
        // merge for the ingested revision's canonical fingerprint so the withheld-confirmation rollback path runs.
        await MergedPrLinkTestSeeder.SeedMergedPrForLatestRevisionAsync(_factory.Services, rackId);

        var correlationId = Guid.NewGuid();
        var applyResponse = await Send(
            HttpMethod.Post, $"/api/racks/{rackId}/drift/apply", "op", "Operator,DriftApply",
            new { driftItemId }, correlationId);
        applyResponse.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.Accepted);
        var jobId = (await ReadJson(applyResponse)).GetProperty("jobId").GetGuid();

        var jobDetail = await PollUntilApplyJobTerminalAsync(rackId, jobId);

        // The domain models rollback as Failed + a device reason code — there is no distinct RolledBack
        // status (ADR 0035 §4, DriftApplyOrchestrator.FinalizeFromDeviceOutcomeAsync).
        jobDetail.GetProperty("status").GetString().Should().Be("Failed");
        jobDetail.GetProperty("deviceReasonCode").GetString().Should().Be("AutoRolledBack");

        // Exactly one device call — no retry double-write for a rejected/rolled-back outcome.
        (_factory.WithheldDriverCallCount - callCountBefore).Should().Be(1);

        // Independent proof, straight from the real simulator: the port is back at its ORIGINAL VLAN, not
        // the desired one — this is what distinguishes rollback from success.
        _factory.GetSwitchPortAccessVlan(VirtualRackDefinition.CleanPort).Should().Be(VirtualRackDefinition.CleanVlan);

        // A fresh discovery (via the real, pass-through MockWithheldReadDriverFactory) observes the SAME
        // reverted VLAN, and the drift item is STILL present — the port never actually reached the desired
        // state, so nothing was resolved.
        var secondSnapshotId = await TriggerDiscoveryAndAwaitSucceededAsync(rackId);
        var latestAfterRollback = await PollUntilDriftReadyForSnapshotAsync(rackId, secondSnapshotId);
        var mismatchAfterRollback = latestAfterRollback.GetProperty("items").GetProperty("items").EnumerateArray()
            .Should().ContainSingle(i => i.GetProperty("driftType").GetString() == "AccessVlanMismatch").Subject;
        mismatchAfterRollback.GetProperty("actualValue").GetString().Should().Be(VirtualRackDefinition.CleanVlan.ToString());
        mismatchAfterRollback.GetProperty("expectedValue").GetString().Should().Be(DesiredStateYamlRenderer.MismatchedVlan.ToString());

        // Rollback audit: before/attempted/rolled-back VLANs, timestamps, correlationId, and the actual
        // terminal outcome the code ships (result=Failed; there is no distinct "Rollback" outcome value —
        // ADR 0035 flags this as the story-#68-AC5-wording vs. shipped-code discrepancy).
        var failedAudit = await PollForAuditEventAsync("drift.apply.job.failed", jobId.ToString());
        failedAudit.RackId.Should().Be(rackId);
        failedAudit.CorrelationId.Should().Be(correlationId);
        failedAudit.Result.Should().Be("Failed");
        failedAudit.OccurredAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        using (var details = JsonDocument.Parse(failedAudit.DetailsJson!))
        {
            var root = details.RootElement;
            root.GetProperty("deviceReasonCode").GetString().Should().Be("AutoRolledBack");
            root.GetProperty("deviceConfirmed").GetBoolean().Should().BeFalse();
            root.GetProperty("desiredVlanId").GetInt32().Should().Be(DesiredStateYamlRenderer.MismatchedVlan, "the ATTEMPTED VLAN");
            root.GetProperty("beforeState").GetString().Should().Contain(VirtualRackDefinition.CleanVlan.ToString(), "the VLAN before the attempt");
            root.GetProperty("afterState").GetString().Should().Contain(VirtualRackDefinition.CleanVlan.ToString(), "the ROLLED-BACK VLAN — equal to before, proving the revert");
        }
    }

    private async Task<Guid> TriggerDiscoveryAndAwaitSucceededAsync(Guid rackId)
    {
        var trigger = await Send(HttpMethod.Post, $"/api/racks/{rackId}/discovery-jobs", "op", "Operator", new { mode = "OnDemand" });
        trigger.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobId = (await ReadJson(trigger)).GetProperty("jobId").GetGuid();

        JsonElement detail = default;
        var terminal = false;
        for (var i = 0; i < 60 && !terminal; i++)
        {
            await Task.Delay(500);
            var response = await Send(HttpMethod.Get, $"/api/discovery-jobs/{jobId}", "op", "Operator");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            detail = await ReadJson(response);
            terminal = detail.GetProperty("status").GetString() is "Succeeded" or "Failed" or "Canceled";
        }

        terminal.Should().BeTrue("the background discovery runner must drive the job to a terminal state within the poll budget");
        detail.GetProperty("status").GetString().Should().Be("Succeeded");
        return detail.GetProperty("resultSnapshotId").GetGuid();
    }

    private async Task<JsonElement> PollUntilDriftReadyForSnapshotAsync(Guid rackId, Guid snapshotId)
    {
        for (var i = 0; i < 60; i++)
        {
            var response = await Send(HttpMethod.Get, $"/api/racks/{rackId}/drift/latest", "ro", "ReadOnly");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var doc = await ReadJson(response);
                if (doc.GetProperty("report").GetProperty("observedSnapshotId").GetGuid() == snapshotId)
                {
                    return doc;
                }
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Drift for snapshot '{snapshotId}' was not computed within the poll budget.");
    }

    private async Task<JsonElement> PollUntilApplyJobTerminalAsync(Guid rackId, Guid jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var response = await Send(HttpMethod.Get, $"/api/racks/{rackId}/jobs/{jobId}", "ro", "ReadOnly");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var doc = await ReadJson(response);
            var status = doc.GetProperty("status").GetString();
            if (status is "Completed" or "Failed" or "StaleDrift" or "Canceled")
            {
                return doc;
            }

            await Task.Delay(150);
        }

        throw new TimeoutException($"Drift-apply job '{jobId}' did not reach a terminal state within the test budget.");
    }

    private async Task<TopologyAuditEvent> PollForAuditEventAsync(string action, string targetId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<CaissonDbContext>();
            var audit = await context.AuditEvents.SingleOrDefaultAsync(a => a.Action == action && a.TargetId == targetId);
            if (audit is not null)
            {
                return audit;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"No audit event action={action} targetId={targetId} appeared within the test budget.");
    }

    private async Task<IngestionRunResult> RunDesiredStateIngestionAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CaissonDbContext>();
        var git = new LibGit2SharpRepositoryProvider(_originPath, _mirrorPath, NullLogger<LibGit2SharpRepositoryProvider>.Instance);
        var options = Microsoft.Extensions.Options.Options.Create(new GitIngestionOptions
        {
            Enabled = true,
            RepoUrl = _originPath,
            Branch = _branch,
            PathGlob = "desired-state/racks/*.yaml",
        });

        var service = new DesiredStateIngestionService(
            context, git, new GuidTopologyIdGenerator(), TimeProvider.System, options, new GitIngestionMetrics(),
            new NoOpDriftRecomputeSignal(),
            new Caisson.Infrastructure.Persistence.Auditing.MandatoryAuditOutbox(),
            NullLogger<DesiredStateIngestionService>.Instance);

        return await service.RunAsync(IngestionTriggerType.Poll, webhookDeliveryId: null, Guid.NewGuid(), default);
    }

    private Task<HttpResponseMessage> Send(
        HttpMethod method, string path, string? user, string? roles, object? body = null, Guid? correlationId = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (user is not null)
        {
            request.Headers.Add(TestAuthHandler.UserHeader, user);
        }

        if (roles is not null)
        {
            request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        }

        if (correlationId is { } cid)
        {
            request.Headers.Add("X-Correlation-Id", cid.ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return _factory.CreateClient().SendAsync(request);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(text, Json);
    }

    private void CommitFile(string relativePath, string content, string message)
    {
        var fullPath = Path.Combine(_originPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);

        using var repo = new Repository(_originPath);
        Commands.Stage(repo, "*");
        var signature = new Signature("Caisson Test", "test@example.com", DateTimeOffset.UtcNow);
        repo.Commit(message, signature, signature);
        _branch = repo.Head.FriendlyName;
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup only.
        }
    }
}
