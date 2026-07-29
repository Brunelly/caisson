using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Caisson.Domain.Topology;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>RBAC matrix (AC4): 401 anonymous, 403 unrecognised role, 200 for each read role.</summary>
[Collection(ApiCollection.Name)]
public sealed class RbacTests
{
    private readonly CaissonApiFactory _factory;

    public RbacTests(CaissonApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task Anonymous_request_is_unauthorized()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var client = _factory.CreateClient();
        var response = await client.GetAsync(LatestPath());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableTheory]
    [InlineData("Admin")]
    [InlineData("Operator")]
    [InlineData("ReadOnly")]
    [InlineData("ServiceAccount")]
    public async Task Each_read_role_can_read(string role)
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, LatestPath());
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, role);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [SkippableFact]
    public async Task Authenticated_without_a_recognised_role_is_forbidden()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, LatestPath());
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, "SomeUnrecognisedRole");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>Task #116: the forbidden-audit gap — a 403 never reaches a controller, so this is the ONLY place that write can happen.</summary>
    [SkippableFact]
    public async Task Forbidden_result_persists_an_authorization_forbidden_audit_event_with_rackId_and_correlationId()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var correlationId = Guid.NewGuid();
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, LatestPath());
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, "SomeUnrecognisedRole");
        request.Headers.Add("X-Correlation-Id", correlationId.ToString());

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var audit = await PollForAuditEventAsync("authorization.forbidden", correlationId);
        audit.Result.Should().Be("403");
        audit.RackId.Should().Be(_factory.Seed.RackId);
        audit.CorrelationId.Should().Be(correlationId);
    }

    /// <summary>A malformed body must never turn a 403 into a 500 — the best-effort driftItemId peek swallows the parse failure.</summary>
    [SkippableFact]
    public async Task Forbidden_drift_apply_with_a_malformed_body_still_returns_403_and_the_audit_event_has_no_driftItemId()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var rackId = await _factory.CreateRackAsync();
        var correlationId = Guid.NewGuid();
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/racks/{rackId}/drift/apply")
        {
            Content = new StringContent("{ not valid json", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, "Operator"); // lacks DriftApply
        request.Headers.Add("X-Correlation-Id", correlationId.ToString());

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a malformed body must never turn a 403 into a 500");

        var audit = await PollForAuditEventAsync("authorization.forbidden", correlationId);
        audit.RackId.Should().Be(rackId);
        using var details = JsonDocument.Parse(audit.DetailsJson!);
        details.RootElement.GetProperty("driftItemId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>Also proves an ABSENT body (no Content-Type at all) never crashes the handler.</summary>
    [SkippableFact]
    public async Task Forbidden_drift_apply_with_no_body_still_returns_403()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var rackId = await _factory.CreateRackAsync();
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/racks/{rackId}/drift/apply");
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, "Operator");

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "an absent body must never turn a 403 into a 500");
    }

    /// <summary>The narrowly-scoped body peek (DriftApply policy + JSON body only) recovers driftItemId when the body IS well-formed.</summary>
    [SkippableFact]
    public async Task Forbidden_drift_apply_with_a_valid_body_persists_the_driftItemId_on_the_audit_event()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");

        var rackId = await _factory.CreateRackAsync();
        var driftItemId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/racks/{rackId}/drift/apply")
        {
            Content = JsonContent.Create(new { driftItemId }),
        };
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, "Operator");
        request.Headers.Add("X-Correlation-Id", correlationId.ToString());

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var audit = await PollForAuditEventAsync("authorization.forbidden", correlationId);
        using var details = JsonDocument.Parse(audit.DetailsJson!);
        details.RootElement.GetProperty("driftItemId").GetGuid().Should().Be(driftItemId);
    }

    /// <summary>Polls for the off-request-path (ChannelAuditEventWriter) audit row, bounded to 10s.</summary>
    private async Task<TopologyAuditEvent> PollForAuditEventAsync(string action, Guid correlationId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var context = _factory.CreateDbContext();
            var audit = await context.AuditEvents.SingleOrDefaultAsync(a => a.Action == action && a.CorrelationId == correlationId);
            if (audit is not null)
            {
                return audit;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"No audit event action={action} correlationId={correlationId} appeared within the test budget.");
    }

    private string LatestPath()
        => $"/api/racks/{_factory.Seed.RackId}/topology/snapshots/latest";
}
