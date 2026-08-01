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
/// Task #114: the apply-success path through the REAL write driver, closing the observed-state loop.
/// <c>AddCaissonDriftApply</c> already registers the real <c>RouterOsSwitchMutatingDriverFactory</c> by
/// default (no driver override needed here, unlike <see cref="DriftApplyRollbackEndToEndTests"/>) — this
/// test only needs a rack created under <see cref="VirtualRackApiFactory.RackScenario.DriftApplyCapable"/>
/// (the stateful, write-capable simulator seeded by <c>RouterOsProfileRenderer.RenderStateful</c>) and an
/// Operator+DriftApply principal (ADR 0032: Admin does not imply DriftApply). Ports the flow proven by
/// mcp-tooling's <c>DriftApplyRunner</c> scenario 1 into a tracked xUnit test in this repo.
/// </summary>
[Collection(VirtualRackCollection.Name)]
public sealed class DriftApplyEndToEndTests : IAsyncLifetime
{
    /// <summary>Mirrors VirtualRackApiFactory's simulator credential — the secret-guard assertion below checks for this literal.</summary>
    private const string SimulatorPassword = "sim-only-password";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly VirtualRackApiFactory _factory;
    private string _originPath = string.Empty;
    private string _mirrorPath = string.Empty;
    private string _branch = string.Empty;

    public DriftApplyEndToEndTests(VirtualRackApiFactory factory) => _factory = factory;

    public Task InitializeAsync()
    {
        _originPath = Directory.CreateTempSubdirectory("caisson-drift-apply-e2e-origin-").FullName;
        _mirrorPath = Path.Combine(Directory.CreateTempSubdirectory("caisson-drift-apply-e2e-mirror-").FullName, "mirror.git");
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
    public async Task Apply_success_drives_the_real_write_driver_corrects_the_device_and_closes_the_loop()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        // The write-capable simulator is shared across every device-mutating test in this collection
        // (sequential, unspecified order) — force ether1 back to its baseline PVID before seeding, so this
        // test's "before" assumption holds regardless of what an earlier test left behind.
        _factory.ResetSwitchPortAccessVlanForTest(VirtualRackDefinition.CleanPort, VirtualRackDefinition.CleanVlan);

        var rackSlug = "vrack-apply-" + Guid.NewGuid().ToString("N");
        var rackId = await _factory.CreateRackAsync(
            externalKey: rackSlug, scenario: VirtualRackApiFactory.RackScenario.DriftApplyCapable);

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
        // merge for the ingested revision's canonical fingerprint so the real device-write loop can run.
        await MergedPrLinkTestSeeder.SeedMergedPrForLatestRevisionAsync(_factory.Services, rackId);

        var correlationId = Guid.NewGuid();
        var applyResponse = await Send(
            HttpMethod.Post, $"/api/racks/{rackId}/drift/apply", "op", "Operator,DriftApply",
            new { driftItemId }, correlationId);
        applyResponse.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.Accepted);
        var jobId = (await ReadJson(applyResponse)).GetProperty("jobId").GetGuid();

        var jobDetail = await PollUntilApplyJobTerminalAsync(rackId, jobId);
        jobDetail.GetProperty("status").GetString().Should().Be("Completed");
        jobDetail.GetProperty("deviceReasonCode").GetString().Should().Be("Applied");
        jobDetail.GetProperty("beforeState").GetString().Should().NotBeNullOrEmpty();
        jobDetail.GetProperty("afterState").GetString().Should().NotBeNullOrEmpty();

        // INDEPENDENT device proof: read the real in-process simulator's port state directly — not the
        // job's own bookkeeping — to prove the device was actually mutated.
        _factory.GetSwitchPortAccessVlan(VirtualRackDefinition.CleanPort).Should().Be(DesiredStateYamlRenderer.MismatchedVlan);

        // CLOSE THE LOOP: a fresh discovery job observes the corrected VLAN and a drift recompute no
        // longer reports the AccessVlanMismatch item for this port (AC4).
        var secondSnapshotId = await TriggerDiscoveryAndAwaitSucceededAsync(rackId);
        var latestAfterApply = await PollUntilDriftReadyForSnapshotAsync(rackId, secondSnapshotId);
        latestAfterApply.GetProperty("items").GetProperty("items").EnumerateArray()
            .Should().NotContain(i => i.GetProperty("driftType").GetString() == "AccessVlanMismatch",
                "the applied port must no longer drift once the next observed snapshot reflects the corrected VLAN");

        // AUDIT COMPLETENESS: BestEffortAuditEventWriter is off-request-path (<=500ms background flush), so
        // both the creation and terminal rows are polled rather than asserted synchronously.
        var created = await PollForAuditEventAsync("drift.apply.job.created", jobId.ToString());
        created.RackId.Should().Be(rackId);
        created.CorrelationId.Should().Be(correlationId);
        using (var createdDetails = JsonDocument.Parse(created.DetailsJson!))
        {
            createdDetails.RootElement.GetProperty("permission").GetString().Should().Be("DriftApply");
            createdDetails.RootElement.GetProperty("driftItemId").GetGuid().Should().Be(driftItemId);
            createdDetails.RootElement.GetProperty("correlationId").GetGuid().Should().Be(correlationId);
        }

        var completed = await PollForAuditEventAsync("drift.apply.job.completed", jobId.ToString());
        completed.RackId.Should().Be(rackId);
        completed.CorrelationId.Should().Be(correlationId);
        completed.Result.Should().Be("Completed");
        completed.ActorId.Should().NotBeNullOrEmpty("the audit record must capture actor identity");
        completed.OccurredAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        using (var completedDetails = JsonDocument.Parse(completed.DetailsJson!))
        {
            var root = completedDetails.RootElement;
            root.GetProperty("switchDeviceKey").GetString().Should().Be(VirtualRackDefinition.SwitchId);
            root.GetProperty("portName").GetString().Should().Be(VirtualRackDefinition.CleanPort);
            root.GetProperty("desiredVlanId").GetInt32().Should().Be(DesiredStateYamlRenderer.MismatchedVlan);
            root.GetProperty("deviceReasonCode").GetString().Should().Be("Applied");
            root.GetProperty("deviceConfirmed").GetBoolean().Should().BeTrue();
            root.GetProperty("beforeState").GetString().Should().Contain(VirtualRackDefinition.CleanVlan.ToString());
            root.GetProperty("afterState").GetString().Should().Contain(DesiredStateYamlRenderer.MismatchedVlan.ToString());
        }

        // SECRET GUARD: audit detailsJson must never carry device credentials.
        created.DetailsJson.Should().NotContain(SimulatorPassword);
        completed.DetailsJson.Should().NotContain(SimulatorPassword);
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

    /// <summary>Polls the drift latest-report API until the event-triggered recompute for <paramref name="snapshotId"/> lands, bounded to 30s.</summary>
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

    /// <summary>Polls a drift-apply job's detail endpoint to a terminal state, bounded to 20s (RunnerPollSeconds=1 keeps this fast).</summary>
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

    /// <summary>Polls the AuditEvents table directly (no generic audit read API exists) for a persisted row, bounded to 10s.</summary>
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
