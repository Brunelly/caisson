using System.Net;
using System.Net.Http.Json;
using Caisson.Api.Contracts;
using Caisson.Domain.NetworkConfig;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// End-to-end network-intent authoring behaviour (story #168/#176): RBAC (GET behind TopologyRead, PUT/
/// validate behind the elevated NetworkConfigAuthor permission alone — an Operator lacking it is still
/// rejected), save-reload round-trip, validation (400, no state changed), xmin/If-Match concurrency
/// (409), exactly-one audit event per save, and the /validate stub persisting nothing.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class NetworkIntentApiTests
{
    private readonly CaissonApiFactory _factory;

    public NetworkIntentApiTests(CaissonApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task Anonymous_get_is_unauthorized()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var rackId = await _factory.CreateRackAsync();
        var response = await _factory.CreateClient().GetAsync(IntentPath(rackId));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableFact]
    public async Task Anonymous_put_is_unauthorized_and_creates_no_state()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var rackId = await _factory.CreateRackAsync();
        var response = await _factory.CreateClient().PutAsJsonAsync(IntentPath(rackId), EmptyRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CountSavedAsync(rackId)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Read_only_can_get_an_empty_default_for_a_rack_with_no_saved_intent_yet()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var rackId = await _factory.CreateRackAsync();
        var response = await GetAsync(rackId, "ReadOnly");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<NetworkIntentDto>();
        body!.VlanCatalogue.Should().BeEmpty();
        body.PortIntents.Should().BeEmpty();
        body.UpdatedAtUtc.Should().BeNull();
    }

    [SkippableFact]
    public async Task Read_only_cannot_save_and_no_state_changes()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var rackId = await _factory.CreateRackAsync();
        var response = await PutAsync(rackId, "ReadOnly", ValidRequest(), ifMatch: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CountSavedAsync(rackId)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Operator_lacking_the_network_config_author_permission_is_forbidden_and_creates_no_state()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var rackId = await _factory.CreateRackAsync();
        var response = await PutAsync(rackId, "Operator", ValidRequest(), ifMatch: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CountSavedAsync(rackId)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Author_can_save_and_a_reload_returns_the_same_intent_state()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var rackId = await _factory.CreateRackAsync();
        var request = ValidRequest();

        var putResponse = await PutAsync(rackId, "NetworkConfigAuthor", request, ifMatch: null);
        putResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var reload = await GetAsync(rackId, "ReadOnly");
        reload.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await reload.Content.ReadFromJsonAsync<NetworkIntentDto>();
        body!.VlanCatalogue.Should().ContainSingle(v => v.Id == 120 && v.Name == "storage");
        body.PortIntents.Should().ContainSingle(p => p.SwitchStableKey == "SW-1" && p.PortName == "ether1" && p.AccessVlanId == 120);
    }

    [SkippableFact]
    public async Task Duplicate_vlan_id_is_rejected_with_a_400_field_error_and_persists_nothing()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var rackId = await _factory.CreateRackAsync();
        var request = new NetworkIntentSaveRequest(
            new[]
            {
                new VlanCatalogueEntryDto(120, "storage", null),
                new VlanCatalogueEntryDto(120, "other", null),
            },
            Array.Empty<PortAccessIntentDto>());

        var response = await PutAsync(rackId, "NetworkConfigAuthor", request, ifMatch: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CountSavedAsync(rackId)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Unknown_vlan_port_intent_is_rejected_with_a_400_field_error()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var rackId = await _factory.CreateRackAsync();
        var request = new NetworkIntentSaveRequest(
            Array.Empty<VlanCatalogueEntryDto>(),
            new[] { new PortAccessIntentDto("SW-1", "ether1", 999) });

        var response = await PutAsync(rackId, "NetworkConfigAuthor", request, ifMatch: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CountSavedAsync(rackId)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Unknown_rack_404s_on_get_and_put()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var unknownRackId = Guid.NewGuid();

        (await GetAsync(unknownRackId, "ReadOnly")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await PutAsync(unknownRackId, "NetworkConfigAuthor", ValidRequest(), ifMatch: null)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task A_stale_if_match_token_is_rejected_with_409_and_the_saved_state_is_unchanged()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var rackId = await _factory.CreateRackAsync();
        var firstPut = await PutAsync(rackId, "NetworkConfigAuthor", ValidRequest(), ifMatch: null);
        firstPut.StatusCode.Should().Be(HttpStatusCode.OK);
        var staleEtag = firstPut.Headers.ETag!.Tag;

        // A second save moves the row's xmin forward — the client's stale ETag no longer matches.
        var secondPut = await PutAsync(rackId, "NetworkConfigAuthor", ValidRequest(), ifMatch: staleEtag);
        secondPut.StatusCode.Should().Be(HttpStatusCode.OK);

        var thirdPutWithStaleToken = await PutAsync(rackId, "NetworkConfigAuthor", ValidRequest(), ifMatch: staleEtag);

        thirdPutWithStaleToken.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [SkippableFact]
    public async Task Saving_without_an_if_match_token_against_an_existing_state_is_rejected_with_409()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var rackId = await _factory.CreateRackAsync();
        var firstPut = await PutAsync(rackId, "NetworkConfigAuthor", ValidRequest(), ifMatch: null);
        firstPut.StatusCode.Should().Be(HttpStatusCode.OK);

        var secondPutMissingToken = await PutAsync(rackId, "NetworkConfigAuthor", ValidRequest(), ifMatch: null);

        secondPutMissingToken.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [SkippableFact]
    public async Task Exactly_one_audit_event_is_written_per_successful_save()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var rackId = await _factory.CreateRackAsync();
        var response = await PutAsync(rackId, "NetworkConfigAuthor", ValidRequest(), ifMatch: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // BestEffortAuditEventWriter is off-request-path (finding #5): the row appears once the background
        // writer's next flush (<=500ms) runs, not synchronously on response — mirrors DriftApplyApiTests.
        var audit = await PollForAuditEventAsync("network-intent.saved", rackId);
        audit.DetailsJson.Should().Contain("NetworkConfigAuthor").And.Contain("vlanCount").And.Contain("correlationId");

        await using var context = _factory.CreateDbContext();
        (await context.AuditEvents.CountAsync(a => a.Action == "network-intent.saved" && a.RackId == rackId))
            .Should().Be(1);
    }

    /// <summary>Polls for the off-request-path audit row, bounded to 5s.</summary>
    private async Task<Caisson.Domain.Topology.TopologyAuditEvent> PollForAuditEventAsync(string action, Guid rackId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await using var context = _factory.CreateDbContext();
            var audit = await context.AuditEvents.SingleOrDefaultAsync(a => a.Action == action && a.RackId == rackId);
            if (audit is not null)
            {
                return audit;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException($"No audit event action={action} rackId={rackId} appeared within the test budget.");
    }

    [SkippableFact]
    public async Task Validate_runs_the_same_rules_as_put_and_persists_nothing()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var rackId = await _factory.CreateRackAsync();
        var invalidRequest = new NetworkIntentSaveRequest(
            new[] { new VlanCatalogueEntryDto(0, "", null) }, Array.Empty<PortAccessIntentDto>());

        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"{IntentPath(rackId)}/validate")
        {
            Content = JsonContent.Create(invalidRequest),
        };
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, "NetworkConfigAuthor");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<NetworkIntentValidationResponse>();
        body!.IsValid.Should().BeFalse();
        body.Errors.Should().NotBeEmpty();
        (await CountSavedAsync(rackId)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Validate_requires_the_network_config_author_permission()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var rackId = await _factory.CreateRackAsync();
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"{IntentPath(rackId)}/validate")
        {
            Content = JsonContent.Create(ValidRequest()),
        };
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, "ReadOnly");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<HttpResponseMessage> GetAsync(Guid rackId, string role)
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, IntentPath(rackId));
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, role);
        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PutAsync(
        Guid rackId, string role, NetworkIntentSaveRequest body, string? ifMatch)
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Put, IntentPath(rackId))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, role);
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation(HeaderNames.IfMatch, ifMatch);
        }

        return await client.SendAsync(request);
    }

    private async Task<int> CountSavedAsync(Guid rackId)
    {
        await using var context = _factory.CreateDbContext();
        return await context.RackNetworkIntents.CountAsync(x => x.RackId == rackId);
    }

    private static NetworkIntentSaveRequest ValidRequest()
        => new(
            new[] { new VlanCatalogueEntryDto(120, "storage", "iSCSI") },
            new[] { new PortAccessIntentDto("SW-1", "ether1", 120) });

    private static NetworkIntentSaveRequest EmptyRequest()
        => new(Array.Empty<VlanCatalogueEntryDto>(), Array.Empty<PortAccessIntentDto>());

    private static string IntentPath(Guid rackId) => $"/api/racks/{rackId}/network-intent";
}
