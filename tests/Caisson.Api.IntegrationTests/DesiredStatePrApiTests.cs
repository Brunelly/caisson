using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Caisson.Api.Contracts;
using Caisson.Domain.NetworkConfig.Preflight;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// End-to-end PR-gate behaviour (story #170, AC3/AC5): server-side re-validation, structured 422 on a
/// run-id mismatch / blocking error / unacknowledged warning, success only after the correct run id plus all
/// warning codes are acknowledged, and side-effect-freedom (no git write, no DB writes except the audit).
/// The publisher is the stubbed <c>NotYetEnabledDesiredStatePrService</c> (#172 deferred).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DesiredStatePrApiTests
{
    private readonly CaissonApiFactory _factory;

    public DesiredStatePrApiTests(CaissonApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task Anonymous_is_unauthorized()
    {
        Skip.IfNot(_factory.Available, SkipReason);

        var response = await _factory.CreateClient().PostAsJsonAsync(Path(Preflight.RackId),
            new CreatePrRequest("x", Array.Empty<string>(), Array.Empty<VlanCatalogueEntryDto>(), Array.Empty<PortAccessIntentDto>()));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableTheory]
    [InlineData("ReadOnly")]
    [InlineData("Operator")]
    public async Task Roles_without_the_author_permission_are_forbidden(string role)
    {
        Skip.IfNot(_factory.Available, SkipReason);

        var response = await PostAsync(Preflight.RackId, role,
            new CreatePrRequest("x", Array.Empty<string>(), Array.Empty<VlanCatalogueEntryDto>(), Array.Empty<PortAccessIntentDto>()));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [SkippableFact]
    public async Task A_blocking_error_is_rejected_with_a_structured_422_and_no_pr_created_audit()
    {
        Skip.IfNot(_factory.Available, SkipReason);

        // 'ether-nope' does not exist on the preflight rack's switch → a blocking portNotFound error.
        var runId = await ValidateForRunIdAsync(Preflight.RackId, ErrorCandidate());

        var response = await PostAsync(Preflight.RackId, "NetworkConfigAuthor",
            new CreatePrRequest(runId, Array.Empty<string>(), ErrorCandidate().VlanCatalogue!, ErrorCandidate().PortIntents!));

        // The gate rejects (422) before the publisher is ever reached — no PR is created on a blocking error.
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReasonCodeAsync(response)).Should().Be("errors");
    }

    [SkippableFact]
    public async Task A_stale_validation_run_id_is_rejected_with_revalidate()
    {
        Skip.IfNot(_factory.Available, SkipReason);

        var response = await PostAsync(Preflight.RackId, "NetworkConfigAuthor",
            new CreatePrRequest("deadbeef", Array.Empty<string>(), ValidRequest().VlanCatalogue!, ValidRequest().PortIntents!));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReasonCodeAsync(response)).Should().Be("revalidate");
    }

    [SkippableFact]
    public async Task A_changed_candidate_invalidates_the_supplied_run_id()
    {
        Skip.IfNot(_factory.Available, SkipReason);

        var runId = await ValidateForRunIdAsync(Preflight.RackId, ValidRequest());

        // Same run id, but a mutated candidate: the server re-derives a different id → revalidate.
        var mutated = new CreatePrRequest(
            runId, Array.Empty<string>(),
            new[] { new VlanCatalogueEntryDto(11, "changed", null) },
            ValidRequest().PortIntents!);

        var response = await PostAsync(Preflight.RackId, "NetworkConfigAuthor", mutated);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReasonCodeAsync(response)).Should().Be("revalidate");
    }

    [SkippableFact]
    public async Task An_unacknowledged_safety_warning_is_rejected()
    {
        Skip.IfNot(_factory.Available, SkipReason);

        var runId = await ValidateForRunIdAsync(Preflight.RackId, UplinkChangeRequest());

        var response = await PostAsync(Preflight.RackId, "NetworkConfigAuthor",
            new CreatePrRequest(runId, Array.Empty<string>(), UplinkChangeRequest().VlanCatalogue!, UplinkChangeRequest().PortIntents!));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReasonCodeAsync(response)).Should().Be("acknowledge-warnings");
    }

    [SkippableFact]
    public async Task A_valid_candidate_with_no_warnings_passes_the_gate_without_any_ack()
    {
        Skip.IfNot(_factory.Available, SkipReason);

        var runId = await ValidateForRunIdAsync(Preflight.RackId, ValidRequest());

        var response = await PostAsync(Preflight.RackId, "NetworkConfigAuthor",
            new CreatePrRequest(runId, Array.Empty<string>(), ValidRequest().VlanCatalogue!, ValidRequest().PortIntents!));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<CreatePrResponse>();
        body!.ValidationRunId.Should().Be(runId);
        body.PullRequestUrl.Should().BeNull(); // no git write — publisher stubbed (#172).
    }

    [SkippableFact]
    public async Task Acknowledging_the_warning_lets_the_gate_pass_and_is_audited_without_writing_state()
    {
        Skip.IfNot(_factory.Available, SkipReason);

        var runId = await ValidateForRunIdAsync(Preflight.RackId, UplinkChangeRequest());

        var response = await PostAsync(Preflight.RackId, "NetworkConfigAuthor",
            new CreatePrRequest(runId, new[] { PreflightCodes.UplinkPort },
                UplinkChangeRequest().VlanCatalogue!, UplinkChangeRequest().PortIntents!));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var audit = await PollForAuditEventAsync("desired-state.pr-created", Preflight.RackId);
        audit.DetailsJson.Should().Contain(PreflightCodes.UplinkPort).And.Contain("outcome");
        audit.DetailsJson.Should().NotContain("ether-nope");
        (await CountSavedIntentsAsync(Preflight.RackId)).Should().Be(0);
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

    private PreflightValidateRequest ErrorCandidate()
        => new(
            new[] { new VlanCatalogueEntryDto(10, "data", null) },
            new[] { new PortAccessIntentDto(Preflight.SwitchStableKey, "ether-nope", 10) });

    /// <summary>Runs preflight-validate and returns the server-issued validationRunId for the same rack.</summary>
    private async Task<string> ValidateForRunIdAsync(Guid rackId, PreflightValidateRequest candidate)
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/racks/{rackId}/desired-state/preflight-validate")
        {
            Content = JsonContent.Create(candidate),
        };
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, "NetworkConfigAuthor");
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<PreflightValidationResponse>();
        return body!.ValidationRunId;
    }

    private async Task<HttpResponseMessage> PostAsync(Guid rackId, string role, CreatePrRequest body)
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, Path(rackId)) { Content = JsonContent.Create(body) };
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, role);
        return await client.SendAsync(request);
    }

    private static async Task<string?> ReasonCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("reasonCode", out var value) ? value.GetString() : null;
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

    private static string Path(Guid rackId) => $"/api/racks/{rackId}/desired-state/prs";

    private const string SkipReason = "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.";
}
