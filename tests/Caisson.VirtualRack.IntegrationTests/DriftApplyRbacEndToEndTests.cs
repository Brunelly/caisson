using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Caisson.Domain.DesiredState;
using Caisson.Domain.Drift.Apply;
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
/// Task #116: RBAC rejection for the apply endpoint (no job created, forbidden audit written — see
/// <c>ForbidLoggingAuthorizationResultHandler</c>) and the NFR5 concurrency/idempotency proof: two
/// concurrent applies for the SAME driftItemId must yield one job and exactly one device write.
/// </summary>
[Collection(VirtualRackCollection.Name)]
public sealed class DriftApplyRbacEndToEndTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly VirtualRackApiFactory _factory;
    private string _originPath = string.Empty;
    private string _mirrorPath = string.Empty;
    private string _branch = string.Empty;

    public DriftApplyRbacEndToEndTests(VirtualRackApiFactory factory) => _factory = factory;

    public Task InitializeAsync()
    {
        _originPath = Directory.CreateTempSubdirectory("caisson-drift-rbac-e2e-origin-").FullName;
        _mirrorPath = Path.Combine(Directory.CreateTempSubdirectory("caisson-drift-rbac-e2e-mirror-").FullName, "mirror.git");
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
    public async Task Operator_without_drift_apply_permission_is_forbidden_creates_no_job_and_is_audited()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var rackSlug = "vrack-rbac-" + Guid.NewGuid().ToString("N");
        var rackId = await _factory.CreateRackAsync(externalKey: rackSlug);

        CommitFile(
            $"desired-state/racks/{rackSlug}.yaml",
            DesiredStateYamlRenderer.RenderWithMismatchedVlan(rackSlug),
            "seed mismatched desired state");
        (await RunDesiredStateIngestionAsync()).Disposition.Should().Be(IngestionRunDisposition.Started);

        var snapshotId = await TriggerDiscoveryAndAwaitSucceededAsync(rackId);
        var latest = await PollUntilDriftReadyForSnapshotAsync(rackId, snapshotId);
        var driftItemId = latest.GetProperty("items").GetProperty("items").EnumerateArray()
            .Should().ContainSingle(i => i.GetProperty("driftType").GetString() == "AccessVlanMismatch").Subject
            .GetProperty("driftItemId").GetGuid();

        var correlationId = Guid.NewGuid();
        var response = await Send(
            HttpMethod.Post, $"/api/racks/{rackId}/drift/apply", "op", "Operator",
            new { driftItemId }, correlationId);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<CaissonDbContext>();
            var jobCount = await context.DriftApplyJobs.CountAsync(j => j.RackId == rackId && j.DriftItemId == driftItemId);
            jobCount.Should().Be(0, "a Forbidden result must never create a DriftApplyJob row");
        }

        var audit = await PollForAuditEventAsync("authorization.forbidden", correlationId);
        audit.RackId.Should().Be(rackId);
        audit.Result.Should().Be("403");
        using (var details = JsonDocument.Parse(audit.DetailsJson!))
        {
            details.RootElement.GetProperty("driftItemId").GetGuid().Should().Be(driftItemId);
            details.RootElement.GetProperty("correlationId").GetGuid().Should().Be(correlationId);
        }

        audit.DetailsJson.Should().NotContainAny("password", "sim-only-password", "vrack-switch", "vrack-bmc");
    }

    /// <summary>NFR5: at least one concurrency test asserts a single job claim and no duplicate device writes for one driftItemId.</summary>
    [SkippableFact]
    public async Task Two_concurrent_applies_for_the_same_drift_item_yield_one_job_and_exactly_one_device_write()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        _factory.ResetSwitchPortAccessVlanForTest(VirtualRackDefinition.CleanPort, VirtualRackDefinition.CleanVlan);
        var baselineSetCommandCount = _factory.ReceivedSwitchCommands.Count(c => c == "/interface/bridge/port/set");

        var rackSlug = "vrack-concurrency-" + Guid.NewGuid().ToString("N");
        var rackId = await _factory.CreateRackAsync(
            externalKey: rackSlug, scenario: VirtualRackApiFactory.RackScenario.DriftApplyCapable);

        CommitFile(
            $"desired-state/racks/{rackSlug}.yaml",
            DesiredStateYamlRenderer.RenderWithMismatchedVlan(rackSlug),
            "seed mismatched desired state");
        (await RunDesiredStateIngestionAsync()).Disposition.Should().Be(IngestionRunDisposition.Started);

        var snapshotId = await TriggerDiscoveryAndAwaitSucceededAsync(rackId);
        var latest = await PollUntilDriftReadyForSnapshotAsync(rackId, snapshotId);
        var driftItemId = latest.GetProperty("items").GetProperty("items").EnumerateArray()
            .Should().ContainSingle(i => i.GetProperty("driftType").GetString() == "AccessVlanMismatch").Subject
            .GetProperty("driftItemId").GetGuid();

        // Story #173 gate (AC4): apply is blocked until the exact candidate's PR is merged. Simulate that
        // merge for the ingested revision's canonical fingerprint so both concurrent applies clear the gate.
        await MergedPrLinkTestSeeder.SeedMergedPrForLatestRevisionAsync(_factory.Services, rackId);

        var firstCall = Send(HttpMethod.Post, $"/api/racks/{rackId}/drift/apply", "op1", "DriftApply", new { driftItemId });
        var secondCall = Send(HttpMethod.Post, $"/api/racks/{rackId}/drift/apply", "op2", "DriftApply", new { driftItemId });
        var responses = await Task.WhenAll(firstCall, secondCall);

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.Accepted);
        var jobIds = new List<Guid>();
        foreach (var response in responses)
        {
            jobIds.Add((await ReadJson(response)).GetProperty("jobId").GetGuid());
        }

        jobIds[0].Should().Be(jobIds[1], "the partial-unique-index-backed idempotent create (ADR 0032) must return the SAME jobId for both concurrent requests");

        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<CaissonDbContext>();
            var jobCount = await context.DriftApplyJobs.CountAsync(j => j.RackId == rackId && j.DriftItemId == driftItemId);
            jobCount.Should().Be(1, "exactly one DriftApplyJob row must exist for this driftItemId");
        }

        var jobDetail = await PollUntilApplyJobTerminalAsync(rackId, jobIds[0]);
        jobDetail.GetProperty("status").GetString().Should().Be("Completed");

        var afterSetCommandCount = _factory.ReceivedSwitchCommands.Count(c => c == "/interface/bridge/port/set");
        (afterSetCommandCount - baselineSetCommandCount).Should().Be(
            1, "two concurrent applies for the same driftItemId must produce exactly one device write, not two");
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

    private async Task<TopologyAuditEvent> PollForAuditEventAsync(string action, Guid correlationId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<CaissonDbContext>();
            var audit = await context.AuditEvents.SingleOrDefaultAsync(a => a.Action == action && a.CorrelationId == correlationId);
            if (audit is not null)
            {
                return audit;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"No audit event action={action} correlationId={correlationId} appeared within the test budget.");
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
