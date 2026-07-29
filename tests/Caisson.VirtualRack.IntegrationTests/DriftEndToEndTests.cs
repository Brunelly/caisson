using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Caisson.Domain.DesiredState;
using Caisson.Infrastructure.Persistence.Drift;
using Caisson.Infrastructure.Persistence.Ingestion;
using Caisson.Ingestion.Git;
using Caisson.Ingestion.Ingestion;
using Caisson.Ingestion.Observability;
using Caisson.Ingestion.Options;
using Caisson.Infrastructure.Persistence;
using Caisson.VirtualRack.Fixtures;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caisson.VirtualRack.IntegrationTests;

/// <summary>
/// Story #64's simulation-first invariant, end-to-end (Task #87): aligns a virtual rack's
/// <c>Rack.ExternalKey</c> with <see cref="DesiredStateYamlRenderer.RackSlug"/> (independently random by
/// default, ADR 0029), ingests a desired-state variant with a deliberately mismatched
/// <see cref="VirtualRackDefinition.CleanPort"/> access VLAN via the real
/// <see cref="DesiredStateIngestionService"/>, runs a real discovery job through the virtual-rack
/// harness's live RouterOS/Redfish simulators, and asserts — via the real
/// <c>GET /api/racks/{rackId}/drift/latest</c> API, not a direct DB read — exactly one
/// <c>AccessVlanMismatch</c> item AND one non-actionable <c>UnknownTopologyMapping</c> item for the
/// fixture's already-seeded ambiguous NIC (AC2's orthogonality: the ambiguity item never suppresses the
/// certain port-level mismatch, and vice versa).
/// </summary>
[Collection(VirtualRackCollection.Name)]
public sealed class DriftEndToEndTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly VirtualRackApiFactory _factory;
    private string _originPath = string.Empty;
    private string _mirrorPath = string.Empty;
    private string _branch = string.Empty;

    public DriftEndToEndTests(VirtualRackApiFactory factory) => _factory = factory;

    public Task InitializeAsync()
    {
        _originPath = Directory.CreateTempSubdirectory("caisson-drift-e2e-origin-").FullName;
        _mirrorPath = Path.Combine(Directory.CreateTempSubdirectory("caisson-drift-e2e-mirror-").FullName, "mirror.git");
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
    public async Task Real_discovery_against_a_mismatched_desired_revision_yields_exactly_the_expected_drift()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        // Align the rack's stable ExternalKey with the desired-state rackSlug so DriftEngine's natural-key
        // join (RackSlug == ExternalKey, ADR 0029) actually bridges this rack's desired and observed sides.
        var rackId = await _factory.CreateRackAsync(externalKey: DesiredStateYamlRenderer.RackSlug);

        CommitFile(
            $"desired-state/racks/{DesiredStateYamlRenderer.RackSlug}.yaml",
            DesiredStateYamlRenderer.RenderWithMismatchedVlan(),
            "seed mismatched desired state");
        var ingestionResult = await RunDesiredStateIngestionAsync();
        ingestionResult.Disposition.Should().Be(IngestionRunDisposition.Started);

        var trigger = await Send(HttpMethod.Post, $"/api/racks/{rackId}/discovery-jobs", "op", "Operator", new { mode = "OnDemand" });
        trigger.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobId = (await ReadJson(trigger)).GetProperty("jobId").GetGuid();

        var jobDetail = await PollUntilJobTerminalAsync(jobId);
        jobDetail.GetProperty("status").GetString().Should().Be("Succeeded");
        var snapshotId = jobDetail.GetProperty("resultSnapshotId").GetGuid();

        // The real snapshot-persisted event hook (TopologySnapshotIngestionService) enqueues this rack
        // through the production IDriftRecomputeSignal → DriftRecomputeRunner → DriftComputationService —
        // no manual drift trigger is needed; poll the read API until that async recompute lands.
        var latest = await PollUntilDriftReadyForSnapshotAsync(rackId, snapshotId);

        var report = latest.GetProperty("report");
        report.GetProperty("observedSnapshotId").GetGuid().Should().Be(snapshotId);
        report.GetProperty("hasAmbiguities").GetBoolean().Should().BeTrue();

        var items = latest.GetProperty("items").GetProperty("items").EnumerateArray().ToList();

        // Exactly one certain, actionable port-level finding — the deliberately mismatched clean port.
        var mismatch = items.Should().ContainSingle(i => i.GetProperty("driftType").GetString() == "AccessVlanMismatch").Subject;
        mismatch.GetProperty("actionable").GetBoolean().Should().BeTrue();
        mismatch.GetProperty("subjectKey").GetString().Should().Contain(VirtualRackDefinition.CleanPort);
        mismatch.GetProperty("expectedValue").GetString().Should().Be("99");
        mismatch.GetProperty("actualValue").GetString().Should().Be(VirtualRackDefinition.CleanVlan.ToString());

        // Exactly one non-actionable ambiguity item for the fixture's already-seeded AmbiguousNic
        // (learned on both AmbiguousPortA/B with conflicting VLANs) — distinct from the separately
        // unmapped NIC's own ambiguity item, identified by its normalized MAC in the subject key.
        var normalizedAmbiguousMac = VirtualRackDefinition.AmbiguousMac.Value;
        var ambiguityItems = items.Where(i => i.GetProperty("driftType").GetString() == "UnknownTopologyMapping").ToList();
        var ambiguity = ambiguityItems.Should()
            .ContainSingle(i => i.GetProperty("subjectKey").GetString()!.Contains(normalizedAmbiguousMac))
            .Subject;
        ambiguity.GetProperty("actionable").GetBoolean().Should().BeFalse("AC2: an ambiguous NIC must never be presented as actionable");
        ambiguity.GetProperty("subjectType").GetString().Should().Be("ServerNic");
        var candidatePorts = ambiguity.GetProperty("details").GetProperty("candidatePorts").EnumerateArray()
            .Select(p => p.GetString()).ToList();
        candidatePorts.Should().Contain(p => p!.Contains(VirtualRackDefinition.AmbiguousPortA));
        candidatePorts.Should().Contain(p => p!.Contains(VirtualRackDefinition.AmbiguousPortB));
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

    /// <summary>Polls the discovery job detail endpoint to a terminal state, bounded to 30s.</summary>
    private async Task<JsonElement> PollUntilJobTerminalAsync(Guid jobId)
    {
        JsonElement detail = default;
        var terminal = false;
        for (var i = 0; i < 60 && !terminal; i++)
        {
            await Task.Delay(500);
            var response = await Send(HttpMethod.Get, $"/api/discovery-jobs/{jobId}", "op", "Operator");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            detail = await ReadJson(response);
            var status = detail.GetProperty("status").GetString();
            terminal = status is "Succeeded" or "Failed" or "Canceled";
        }

        terminal.Should().BeTrue("the background runner must drive the job to a terminal state within the poll budget");
        return detail;
    }

    /// <summary>
    /// Polls the drift latest-report API until the event-triggered recompute for
    /// <paramref name="snapshotId"/> lands (bounded to 30s) — correctness never depends on when
    /// <see cref="DriftRecomputeRunner"/> drains the queue, only that it eventually does.
    /// </summary>
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

    private Task<HttpResponseMessage> Send(
        HttpMethod method, string path, string? user, string? roles, object? body = null)
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
