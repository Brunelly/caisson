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

    // ---- Tier 2 durable-first-N + bounded overflow (story #308, ADR 0064) ---------------------------------

    /// <summary>AC3: a burst from one principal is bounded — first N verbatim, the rest one flushed aggregate.</summary>
    [SkippableFact]
    public async Task A_denial_flood_from_one_principal_yields_exactly_N_verbatim_rows_plus_a_bounded_aggregate()
    {
        Skip.IfNot(_factory.Available, SkipReason);

        var actor = "flood-actor-" + Guid.NewGuid().ToString("N")[..8];
        const int burstSize = 30; // >> the default DenialFirstN of 5
        const int expectedFirstN = 5;

        var client = _factory.CreateClient();
        var responses = await Task.WhenAll(Enumerable.Range(0, burstSize).Select(_ => SendForbiddenRequestAsync(client, actor)));
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Forbidden);

        // The first N are durable and queryable immediately — no polling needed for these.
        await using (var context = _factory.CreateDbContext())
        {
            var verbatim = await context.AuditEvents
                .Where(a => a.Action == "authorization.forbidden" && a.ActorId == actor)
                .ToListAsync();
            verbatim.Should().HaveCount(expectedFirstN);
            verbatim.Should().OnlyContain(a => a.Result == "403");
        }

        // The overflow is flushed asynchronously (bounded by the sped-up DenialFlushIntervalSeconds).
        var aggregateCount = await PollForOverflowCountAsync(actor, burstSize - expectedFirstN);
        aggregateCount.Should().Be(burstSize - expectedFirstN);
    }

    /// <summary>NFR2: a flood from one principal must never evict another principal's records (Tier 1 or Tier 2).</summary>
    [SkippableFact]
    public async Task One_principals_denial_flood_does_not_evict_another_principals_first_n_records()
    {
        Skip.IfNot(_factory.Available, SkipReason);

        var floodActor = "flood-actor-" + Guid.NewGuid().ToString("N")[..8];
        var quietActor = "quiet-actor-" + Guid.NewGuid().ToString("N")[..8];
        var client = _factory.CreateClient();

        // The quiet principal is denied exactly once, BEFORE the flood.
        var quietResponse = await SendForbiddenRequestAsync(client, quietActor);
        quietResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var floodResponses = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => SendForbiddenRequestAsync(client, floodActor)));
        floodResponses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Forbidden);

        await using var context = _factory.CreateDbContext();
        (await context.AuditEvents.CountAsync(a => a.Action == "authorization.forbidden" && a.ActorId == quietActor))
            .Should().Be(1, "the quiet principal's own durable denial must survive another principal's flood");
    }

    /// <summary>Bucket key + denial details must never carry the raw path, query string, or any secret/token.</summary>
    [SkippableFact]
    public async Task Denial_records_contain_no_raw_query_string_or_secrets()
    {
        Skip.IfNot(_factory.Available, SkipReason);

        var actor = "secret-check-actor-" + Guid.NewGuid().ToString("N")[..8];
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, LatestPath() + "?token=super-secret-value&password=hunter2");
        request.Headers.Add(TestAuthHandler.UserHeader, actor);
        request.Headers.Add(TestAuthHandler.RolesHeader, "SomeUnrecognisedRole");
        request.Headers.Add("Authorization", "Bearer fake-bearer-token-value");

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await using var context = _factory.CreateDbContext();
        var audit = await context.AuditEvents.SingleAsync(a => a.Action == "authorization.forbidden" && a.ActorId == actor);
        audit.DetailsJson.Should().NotContain("super-secret-value");
        audit.DetailsJson.Should().NotContain("hunter2");
        audit.DetailsJson.Should().NotContain("fake-bearer-token-value");
        audit.DetailsJson.Should().NotContain("?token=");
        audit.DetailsJson.Should().Contain("GET "); // the stable "{method} {routeTemplate}" bucket key
    }

    private Task<HttpResponseMessage> SendForbiddenRequestAsync(HttpClient client, string actor)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, LatestPath());
        request.Headers.Add(TestAuthHandler.UserHeader, actor);
        request.Headers.Add(TestAuthHandler.RolesHeader, "SomeUnrecognisedRole");
        return client.SendAsync(request);
    }

    /// <summary>
    /// Sums the overflow aggregates for <paramref name="actor"/>, polling until the total reaches
    /// <paramref name="expectedTotal"/> (or the budget runs out, returning whatever it last saw so the
    /// caller's assertion reports the real number).
    /// <para>
    /// It must NOT stop at the first aggregate row it sees. The flush interval is deliberately sped up to
    /// one second here, so a flush can easily land part-way through a burst and split the overflow across
    /// several aggregate rows — that is normal, correct behaviour (each row carries its own batch id), but
    /// returning on the first row would read a partial total and fail intermittently for no real reason.
    /// </para>
    /// </summary>
    private async Task<long> PollForOverflowCountAsync(string actor, long expectedTotal)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        long total = 0;
        var sawAny = false;

        while (DateTime.UtcNow < deadline)
        {
            await using (var context = _factory.CreateDbContext())
            {
                var aggregates = await context.AuditEvents
                    .Where(a => a.Action == "authorization.forbidden.overflow" && a.ActorId == actor)
                    .ToListAsync();

                if (aggregates.Count > 0)
                {
                    sawAny = true;
                    total = aggregates.Sum(a =>
                    {
                        using var details = JsonDocument.Parse(a.DetailsJson!);
                        return details.RootElement.GetProperty("count").GetInt64();
                    });

                    if (total >= expectedTotal)
                    {
                        return total;
                    }
                }
            }

            await Task.Delay(200);
        }

        return sawAny
            ? total
            : throw new TimeoutException($"No authorization.forbidden.overflow aggregate for actorId={actor} appeared within the test budget.");
    }

    /// <summary>Polls for the off-request-path (BestEffortAuditEventWriter) audit row, bounded to 10s.</summary>
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

    private const string SkipReason = "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.";
}
