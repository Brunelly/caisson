using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Caisson.Infrastructure.Persistence;
using Caisson.Orchestration.Discovery;
using Caisson.VirtualRack.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caisson.VirtualRack.IntegrationTests;

/// <summary>
/// Story #11's cohesive virtual-rack harness (Task #58, deliverable 1): boots the simulated switch + BMC,
/// drives a discovery job through the REAL <c>RouterOsSwitchDriver</c>/<c>RedfishBmcDriver</c> and the
/// real orchestration/persistence/query path (no fake drivers — see <see cref="VirtualRackApiFactory"/>),
/// and diffs discovered-vs-expected against <see cref="ExpectedTopologyBuilder"/>. Also covers AC3
/// (Redfish auth failure, unreachable switch) with the real orchestration error vocabulary and a
/// no-secret-leak assertion. Gated on <see cref="VirtualRackApiFactory.Available"/> (Postgres);
/// self-skips (not fails) when Docker/Postgres is unavailable, matching every other DB-backed suite.
/// </summary>
[Collection(VirtualRackCollection.Name)]
public sealed class VirtualRackEndToEndTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly VirtualRackApiFactory _factory;

    public VirtualRackEndToEndTests(VirtualRackApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task Happy_path_discovers_correlates_persists_and_queries_the_virtual_rack()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var rackId = await _factory.CreateRackAsync(scenario: VirtualRackApiFactory.RackScenario.Happy);

        var trigger = await Send(HttpMethod.Post, $"/api/racks/{rackId}/discovery-jobs", "op", "Operator",
            new { mode = "OnDemand" });
        trigger.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobId = (await ReadJson(trigger)).GetProperty("jobId").GetGuid();

        var detail = await PollUntilTerminalAsync(jobId);
        detail.GetProperty("status").GetString().Should().Be("Succeeded");
        var snapshotId = detail.GetProperty("resultSnapshotId").GetGuid();

        var graphResponse = await Send(HttpMethod.Get, $"/api/racks/{rackId}/topology/snapshots/latest/graph", "ro", "ReadOnly");
        graphResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var graph = await ReadJson(graphResponse);

        var servers = graph.GetProperty("servers").EnumerateArray().ToList();
        servers.Should().HaveCount(1, "the virtual rack has exactly one server");
        var nics = servers.SelectMany(s => s.GetProperty("nics").EnumerateArray()).ToList();
        nics.Should().HaveCount(3, "the virtual rack's server has three NICs: clean, ambiguous, unmapped");

        var unmappedPortNames = graph.GetProperty("unmappedPorts").EnumerateArray()
            .Select(p => p.GetProperty("portName").GetString()).ToList();
        unmappedPortNames.Should().Contain(VirtualRackDefinition.UnmappedPort, "AC2: at least one switch port has no matching NIC");

        // Deep fidelity check: reconstruct what was actually PERSISTED (full reason-code evidence, not just
        // the single collapsed column the graph endpoint exposes) and diff against the same expectation the
        // fixtures library verifies against the raw correlation engine. Unmapped-port reason codes are never
        // persisted (see PersistedTopologyReader), so that one dimension is relaxed to an existence check.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CaissonDbContext>();
        var actual = await PersistedTopologyReader.LoadAsync(db, snapshotId);

        var expected = ExpectedTopologyBuilder.Build();
        var persistedExpected = expected with
        {
            UnmappedPorts = expected.UnmappedPorts
                .Select(p => p with { ReasonCodes = Array.Empty<Caisson.Domain.Enums.ReasonCode>() })
                .ToList(),
        };

        var diff = TopologyDiff.Compare(actual, persistedExpected);
        diff.Should().BeEmpty(
            "the persisted topology must match the ground truth exactly, including LldpConsistent for the clean NIC — " +
            "a diff of: " + string.Join(" | ", diff));
    }

    [SkippableFact]
    public async Task Bmc_auth_failure_fails_the_job_with_BmcDiscoveryFailed_and_leaks_no_secrets()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres.");
        var rackId = await _factory.CreateRackAsync(scenario: VirtualRackApiFactory.RackScenario.BmcAuthFailure);

        var trigger = await Send(HttpMethod.Post, $"/api/racks/{rackId}/discovery-jobs", "op", "Operator",
            new { mode = "OnDemand" });
        trigger.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobId = (await ReadJson(trigger)).GetProperty("jobId").GetGuid();

        var detail = await PollUntilTerminalAsync(jobId);
        detail.GetProperty("status").GetString().Should().Be("Failed");
        detail.GetProperty("errorCode").GetString().Should().Be(DiscoveryErrorCodes.BmcDiscoveryFailed);

        AssertNoSecretLeak(detail);
    }

    [SkippableFact]
    public async Task Switch_unreachable_fails_the_job_with_SwitchDiscoveryFailed_within_bounded_timeout()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres.");
        var rackId = await _factory.CreateRackAsync(scenario: VirtualRackApiFactory.RackScenario.SwitchUnreachable);

        var trigger = await Send(HttpMethod.Post, $"/api/racks/{rackId}/discovery-jobs", "op", "Operator",
            new { mode = "OnDemand" });
        trigger.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobId = (await ReadJson(trigger)).GetProperty("jobId").GetGuid();

        var detail = await PollUntilTerminalAsync(jobId);
        detail.GetProperty("status").GetString().Should().Be("Failed");
        detail.GetProperty("errorCode").GetString().Should().Be(DiscoveryErrorCodes.SwitchDiscoveryFailed);

        AssertNoSecretLeak(detail);
    }

    private static void AssertNoSecretLeak(JsonElement detail)
    {
        var text = detail.GetRawText();
        text.Should().NotContain("credentialsRef");
        text.Should().NotContain("kv://");
        text.Should().NotContain("sim-only-password");
    }

    /// <summary>Polls the job detail endpoint to a terminal state, bounded to 30s (AC3's "bounded timeout").</summary>
    private async Task<JsonElement> PollUntilTerminalAsync(Guid jobId)
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
}
