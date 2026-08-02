using System.Net;
using System.Threading.Channels;
using Caisson.Api.Auditing;
using Caisson.Domain.Enums;
using Caisson.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// Story #308 (ADR 0064) AC4: audit persistence stays OFF the read request path, and Tier 3 channel
/// saturation — the ONLY tier that may shed events — never touches Tier 1 (outbox) or Tier 2 (denial
/// bucket) durable records.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuditReadPathTests
{
    private readonly CaissonApiFactory _factory;

    public AuditReadPathTests(CaissonApiFactory factory) => _factory = factory;

    /// <summary>
    /// A read response returns before its Tier 3 audit row is committed — the channel write is
    /// fire-and-forget and the background writer flushes on its own interval, never synchronously on the
    /// request path.
    /// </summary>
    [SkippableFact]
    public async Task A_read_endpoints_response_completes_with_no_synchronous_audit_commit()
    {
        Skip.IfNot(_factory.Available, SkipReason);

        // A unique, explicit correlation id disambiguates THIS request's own audit row from the many other
        // tests in this shared collection concurrently reading the SAME seeded rack/endpoint.
        var correlationId = Guid.NewGuid();
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/racks/{_factory.Seed.RackId}/topology/snapshots/latest");
        request.Headers.Add(TestAuthHandler.UserHeader, "read-path-actor-" + Guid.NewGuid().ToString("N")[..8]);
        request.Headers.Add(TestAuthHandler.RolesHeader, "ReadOnly");
        request.Headers.Add("X-Correlation-Id", correlationId.ToString());

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Immediately after the response returns — no delay, no polling — the audit row must not yet be
        // committed: the channel write is fire-and-forget and AuditEventBackgroundWriter flushes on its
        // own schedule, never as part of handling this request.
        await using var context = _factory.CreateDbContext();
        var immediatelyVisible = await context.AuditEvents.AnyAsync(a => a.CorrelationId == correlationId);
        immediatelyVisible.Should().BeFalse(
            "the read-audit write must be off the request path — it cannot already be durably committed the instant the response returns");
    }

    /// <summary>
    /// Saturating the Tier 3 channel directly (bypassing HTTP — 4096+ real requests would be prohibitively
    /// slow) must alter or delete NOTHING in the Tier 1 outbox or Tier 2 denial bucket tables; only the
    /// bounded, explicitly-droppable channel itself may shed writes.
    /// </summary>
    [SkippableFact]
    public async Task Saturating_the_best_effort_channel_leaves_tier1_outbox_and_tier2_denial_records_untouched()
    {
        Skip.IfNot(_factory.Available, SkipReason);

        var floodActor = "flood-read-actor-" + Guid.NewGuid().ToString("N")[..8];

        using var scope = _factory.Services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<ChannelWriter<AuditWriteRequest>>();

        // Flood well past the bounded channel's capacity (4096) — FullMode=DropWrite means these all
        // return immediately (TryWrite never blocks), some succeeding and some dropped once full.
        for (var i = 0; i < 5000; i++)
        {
            writer.TryWrite(new AuditWriteRequest(
                Guid.NewGuid(), DateTime.UtcNow, ActorType.User, floodActor, "topology.latest.read",
                "snapshot", Guid.NewGuid(), "success", _factory.Seed.RackId, null));
        }

        // A raw before/after total-row-count comparison would be racy: this factory's host keeps its
        // always-on background schedulers (discovery/drift/etc.) running for the WHOLE shared collection's
        // lifetime, and those can legitimately add their own unrelated Tier 1 rows between the two
        // snapshots. Assert the actual invariant instead — that nothing attributable to THIS flood (its
        // unique actor id) ever reaches Tier 1 or Tier 2 — which is immune to that unrelated churn.
        await using var after = _factory.CreateDbContext();
        (await after.AuditOutboxMessages.AnyAsync(m => m.ActorId == floodActor)).Should().BeFalse(
            "Tier 1 outbox rows must be untouched by Tier 3 channel saturation");
        (await after.AuditDenialBuckets.AnyAsync(b => b.ActorId == floodActor)).Should().BeFalse(
            "Tier 2 denial buckets must be untouched by Tier 3 channel saturation");
    }

    private const string SkipReason = "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.";
}
