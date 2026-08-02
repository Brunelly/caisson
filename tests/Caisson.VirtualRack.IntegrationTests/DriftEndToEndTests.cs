using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Caisson.Domain.DesiredState;
using Caisson.Domain.Enums;
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

        var snapshotId = await TriggerDiscoveryAndAwaitSucceededAsync(rackId);

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
        mismatch.GetProperty("expectedValue").GetString().Should().Be(DesiredStateYamlRenderer.MismatchedVlan.ToString());
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

    /// <summary>
    /// Task #113: severity is deterministic (asserts the value <c>DriftSeverityRules</c> actually ships —
    /// High — NOT the story's illustrative "Medium"; production severity code is never changed to match a
    /// proof story's example, see ADR 0035), the item exposes stable switch/port details, and — the
    /// concrete NFR1 "all generated IDs deterministic" proof — recomputing drift a SECOND time against
    /// unchanged desired/observed inputs (a second real discovery run against the same simulator state)
    /// yields the SAME content-hashed driftItemId (ADR 0029's upsert-by-id).
    /// </summary>
    [SkippableFact]
    public async Task Drift_item_severity_and_subject_details_are_deterministic_and_the_driftItemId_recurs_across_recompute()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var rackSlug = "vrack-severity-" + Guid.NewGuid().ToString("N");
        var rackId = await _factory.CreateRackAsync(externalKey: rackSlug);

        CommitFile(
            $"desired-state/racks/{rackSlug}.yaml",
            DesiredStateYamlRenderer.RenderWithMismatchedVlan(rackSlug),
            "seed mismatched desired state");
        (await RunDesiredStateIngestionAsync()).Disposition.Should().Be(IngestionRunDisposition.Started);

        var firstSnapshotId = await TriggerDiscoveryAndAwaitSucceededAsync(rackId);
        var latest1 = await PollUntilDriftReadyForSnapshotAsync(rackId, firstSnapshotId);
        var mismatch1 = latest1.GetProperty("items").GetProperty("items").EnumerateArray()
            .Should().ContainSingle(i => i.GetProperty("driftType").GetString() == "AccessVlanMismatch").Subject;

        mismatch1.GetProperty("severity").GetString().Should().Be(
            nameof(DriftSeverity.High),
            "DriftSeverityRules deterministically maps AccessVlanMismatch to High — this proof story asserts " +
            "whatever the code ships, it never changes production severity to match the story text's example");
        var details1 = mismatch1.GetProperty("details");
        details1.GetProperty("switchName").GetString().Should().Be(VirtualRackDefinition.SwitchId);
        details1.GetProperty("portName").GetString().Should().Be(VirtualRackDefinition.CleanPort);
        var driftItemId1 = mismatch1.GetProperty("driftItemId").GetGuid();
        driftItemId1.Should().NotBeEmpty();

        var secondSnapshotId = await TriggerDiscoveryAndAwaitSucceededAsync(rackId);
        secondSnapshotId.Should().NotBe(firstSnapshotId, "each discovery run persists a NEW immutable snapshot even when observed state is unchanged");

        var latest2 = await PollUntilDriftReadyForSnapshotAsync(rackId, secondSnapshotId);
        var mismatch2 = latest2.GetProperty("items").GetProperty("items").EnumerateArray()
            .Should().ContainSingle(i => i.GetProperty("driftType").GetString() == "AccessVlanMismatch").Subject;
        mismatch2.GetProperty("driftItemId").GetGuid().Should().Be(
            driftItemId1, "the SAME real-world drift (same rack/type/subject/expected/actual) must hash to the SAME DriftItemId across recomputes (ADR 0029)");
    }

    /// <summary>
    /// Task #113: proves the UI's actual read data path with contract-level backend assertions (per the
    /// story's answered question — Playwright stays nightly/on-main, ADR 0016/existing angular-e2e-smoke).
    /// The Angular drift UI (story #67) reads a single report via BOTH <c>GET .../drift/latest</c> and
    /// <c>GET .../drift/reports/{{driftReportId}}</c> (there is no bare <c>GET .../drift/reports</c>
    /// collection endpoint — the list route is <c>GET .../drift/history</c>, which returns report
    /// SUMMARIES only, no per-item fields) — both expose the same switch/port/before-after item contract.
    /// Also asserts the harness-supplied correlationId reaches the persisted discovery-job audit trail.
    /// </summary>
    [SkippableFact]
    public async Task Drift_report_read_api_exposes_switch_port_and_before_after_vlans_and_the_harness_correlation_id_reaches_the_discovery_audit_trail()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var rackSlug = "vrack-uicontract-" + Guid.NewGuid().ToString("N");
        var rackId = await _factory.CreateRackAsync(externalKey: rackSlug);
        var correlationId = Guid.NewGuid();

        CommitFile(
            $"desired-state/racks/{rackSlug}.yaml",
            DesiredStateYamlRenderer.RenderWithMismatchedVlan(rackSlug),
            "seed mismatched desired state");
        (await RunDesiredStateIngestionAsync()).Disposition.Should().Be(IngestionRunDisposition.Started);

        var trigger = await Send(
            HttpMethod.Post, $"/api/racks/{rackId}/discovery-jobs", "op", "Operator", new { mode = "OnDemand" }, correlationId);
        trigger.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobId = (await ReadJson(trigger)).GetProperty("jobId").GetGuid();

        var jobDetail = await PollUntilJobTerminalAsync(jobId);
        jobDetail.GetProperty("status").GetString().Should().Be("Succeeded");
        var snapshotId = jobDetail.GetProperty("resultSnapshotId").GetGuid();

        var latest = await PollUntilDriftReadyForSnapshotAsync(rackId, snapshotId);
        var driftReportId = latest.GetProperty("report").GetProperty("driftReportId").GetGuid();
        var mismatchFromLatest = latest.GetProperty("items").GetProperty("items").EnumerateArray()
            .Should().ContainSingle(i => i.GetProperty("driftType").GetString() == "AccessVlanMismatch").Subject;

        var reportResponse = await Send(HttpMethod.Get, $"/api/racks/{rackId}/drift/reports/{driftReportId}", "ro", "ReadOnly");
        reportResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var reportDoc = await ReadJson(reportResponse);
        var mismatchFromReport = reportDoc.GetProperty("items").GetProperty("items").EnumerateArray()
            .Should().ContainSingle(i => i.GetProperty("driftType").GetString() == "AccessVlanMismatch").Subject;

        mismatchFromReport.GetProperty("driftItemId").GetGuid().Should().Be(mismatchFromLatest.GetProperty("driftItemId").GetGuid());
        mismatchFromReport.GetProperty("subjectKey").GetString().Should().Contain(VirtualRackDefinition.CleanPort);
        mismatchFromReport.GetProperty("expectedValue").GetString().Should().Be(DesiredStateYamlRenderer.MismatchedVlan.ToString());
        mismatchFromReport.GetProperty("actualValue").GetString().Should().Be(VirtualRackDefinition.CleanVlan.ToString());
        var details = mismatchFromReport.GetProperty("details");
        details.GetProperty("switchName").GetString().Should().Be(VirtualRackDefinition.SwitchId);
        details.GetProperty("portName").GetString().Should().Be(VirtualRackDefinition.CleanPort);

        var audit = await PollForAuditEventAsync("discovery.job.triggered", jobId.ToString());
        audit.CorrelationId.Should().Be(correlationId, "the harness's X-Correlation-Id header must propagate through CorrelationIdMiddleware onto the persisted discovery-job audit trail");
        audit.RackId.Should().Be(rackId);
    }

    private async Task<Guid> TriggerDiscoveryAndAwaitSucceededAsync(Guid rackId, Guid? correlationId = null)
    {
        var trigger = await Send(
            HttpMethod.Post, $"/api/racks/{rackId}/discovery-jobs", "op", "Operator", new { mode = "OnDemand" }, correlationId);
        trigger.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobId = (await ReadJson(trigger)).GetProperty("jobId").GetGuid();

        var jobDetail = await PollUntilJobTerminalAsync(jobId);
        jobDetail.GetProperty("status").GetString().Should().Be("Succeeded");
        return jobDetail.GetProperty("resultSnapshotId").GetGuid();
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

    /// <summary>Honoured by the real CorrelationIdMiddleware (echoes it back, stamps every audit/log line for the request).</summary>
    private const string CorrelationIdHeader = "X-Correlation-Id";

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
            request.Headers.Add(CorrelationIdHeader, cid.ToString());
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
