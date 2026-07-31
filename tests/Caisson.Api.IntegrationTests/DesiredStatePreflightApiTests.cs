using System.Net;
using System.Net.Http.Json;
using Caisson.Api.Contracts;
using Caisson.Domain.NetworkConfig.Preflight;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// End-to-end pre-flight validation behaviour (story #170): RBAC (NetworkConfigAuthor-gated; ReadOnly and
/// Operator-without-grant → 403 + audit), schema→semantic→safety results against the latest persisted
/// snapshot, deterministic validationRunId, side-effect-freedom (no DB writes except the audit), and the
/// no-500-for-validation-failures contract (NFR1). Mirrors <see cref="NetworkIntentApiTests"/>.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DesiredStatePreflightApiTests
{
    private readonly CaissonApiFactory _factory;

    public DesiredStatePreflightApiTests(CaissonApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task Anonymous_is_unauthorized()
    {
        Skip.IfNot(_factory.Available, SkipReason);

        var response = await _factory.CreateClient().PostAsJsonAsync(Path(Preflight.RackId), ValidRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableTheory]
    [InlineData("ReadOnly")]
    [InlineData("Operator")]
    public async Task Roles_without_the_author_permission_are_forbidden_and_audited(string role)
    {
        Skip.IfNot(_factory.Available, SkipReason);

        var response = await PostAsync(Preflight.RackId, role, ValidRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await PollForAuditEventAsync("authorization.forbidden", Preflight.RackId);
    }

    [SkippableFact]
    public async Task A_valid_candidate_against_known_topology_is_valid_and_pr_ready()
    {
        Skip.IfNot(_factory.Available, SkipReason);

        var response = await PostAsync(Preflight.RackId, "NetworkConfigAuthor", ValidRequest());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PreflightValidationResponse>();
        body!.IsValid.Should().BeTrue();
        body.CanCreatePr.Should().BeTrue();
        body.Errors.Should().BeEmpty();
        body.Warnings.Should().BeEmpty();
        body.ValidationRunId.Should().MatchRegex("^[0-9a-f]{64}$");
        body.TopologySnapshotId.Should().NotBeNull();
    }

    [SkippableFact]
    public async Task A_duplicate_vlan_returns_200_with_a_field_error_and_writes_no_intent()
    {
        Skip.IfNot(_factory.Available, SkipReason);

        var request = new PreflightValidateRequest(
            new[] { new VlanCatalogueEntryDto(10, "a", null), new VlanCatalogueEntryDto(10, "b", null) },
            Array.Empty<PortAccessIntentDto>());

        var response = await PostAsync(Preflight.RackId, "NetworkConfigAuthor", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PreflightValidationResponse>();
        body!.IsValid.Should().BeFalse();
        body.Errors.Should().Contain(e => e.Code == PreflightCodes.DuplicateVlanId);
        (await CountSavedIntentsAsync(Preflight.RackId)).Should().Be(0);
    }

    [SkippableFact]
    public async Task An_unknown_port_resolves_against_the_latest_snapshot_and_errors()
    {
        Skip.IfNot(_factory.Available, SkipReason);

        var request = new PreflightValidateRequest(
            new[] { new VlanCatalogueEntryDto(10, "data", null) },
            new[] { new PortAccessIntentDto(Preflight.SwitchStableKey, "ether-nope", 10) });

        var response = await PostAsync(Preflight.RackId, "NetworkConfigAuthor", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PreflightValidationResponse>();
        body!.Errors.Should().Contain(e => e.Code == PreflightCodes.PortNotFound);
    }

    [SkippableFact]
    public async Task A_change_to_an_uplink_port_is_a_non_blocking_safety_warning()
    {
        Skip.IfNot(_factory.Available, SkipReason);

        var response = await PostAsync(Preflight.RackId, "NetworkConfigAuthor", UplinkChangeRequest());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PreflightValidationResponse>();
        body!.IsValid.Should().BeTrue();
        body.CanCreatePr.Should().BeFalse();
        body.Warnings.Should().Contain(w => w.Code == PreflightCodes.UplinkPort && w.Severity == "warning");
    }

    [SkippableFact]
    public async Task The_validation_run_id_is_stable_across_repeated_calls()
    {
        Skip.IfNot(_factory.Available, SkipReason);

        var first = await (await PostAsync(Preflight.RackId, "NetworkConfigAuthor", ValidRequest()))
            .Content.ReadFromJsonAsync<PreflightValidationResponse>();
        var second = await (await PostAsync(Preflight.RackId, "NetworkConfigAuthor", ValidRequest()))
            .Content.ReadFromJsonAsync<PreflightValidationResponse>();

        second!.ValidationRunId.Should().Be(first!.ValidationRunId);
    }

    [SkippableFact]
    public async Task Every_run_is_audited_with_counts_only_and_no_intent_is_written()
    {
        Skip.IfNot(_factory.Available, SkipReason);

        var rackId = await _factory.CreateRackAsync();
        var emptyCandidate = new PreflightValidateRequest(
            Array.Empty<VlanCatalogueEntryDto>(), Array.Empty<PortAccessIntentDto>());
        var response = await PostAsync(rackId, "NetworkConfigAuthor", emptyCandidate);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var audit = await PollForAuditEventAsync("desired-state.preflight-validated", rackId);
        audit.DetailsJson.Should().Contain("errorCount").And.Contain("outcome").And.Contain("correlationId");
        audit.DetailsJson.Should().NotContain("vlanCatalogue").And.NotContain("ether");

        await using var context = _factory.CreateDbContext();
        (await context.AuditEvents.CountAsync(a =>
            a.Action == "desired-state.preflight-validated" && a.RackId == rackId)).Should().Be(1);
        (await CountSavedIntentsAsync(rackId)).Should().Be(0);
    }

    private SeededPreflight Preflight => _factory.Seed.Preflight;

    private PreflightValidateRequest ValidRequest()
        => new(
            new[] { new VlanCatalogueEntryDto(10, "data", null) },
            new[] { new PortAccessIntentDto(Preflight.SwitchStableKey, Preflight.AccessPortName, 10) });

    private PreflightValidateRequest UplinkChangeRequest()
        => new(
            new[] { new VlanCatalogueEntryDto(10, "data", null) },
            new[] { new PortAccessIntentDto(Preflight.SwitchStableKey, Preflight.UplinkPortName, 10) });

    private async Task<HttpResponseMessage> PostAsync(Guid rackId, string role, PreflightValidateRequest body)
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, Path(rackId))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, role);
        return await client.SendAsync(request);
    }

    private async Task<int> CountSavedIntentsAsync(Guid rackId)
    {
        await using var context = _factory.CreateDbContext();
        return await context.RackNetworkIntents.CountAsync(x => x.RackId == rackId);
    }

    private async Task<Caisson.Domain.Topology.TopologyAuditEvent> PollForAuditEventAsync(string action, Guid rackId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await using var context = _factory.CreateDbContext();
            var audit = await context.AuditEvents
                .Where(a => a.Action == action && a.RackId == rackId)
                .OrderByDescending(a => a.OccurredAtUtc)
                .FirstOrDefaultAsync();
            if (audit is not null)
            {
                return audit;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException($"No audit event action={action} rackId={rackId} appeared within the test budget.");
    }

    private static string Path(Guid rackId) => $"/api/racks/{rackId}/desired-state/preflight-validate";

    private const string SkipReason = "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.";
}
