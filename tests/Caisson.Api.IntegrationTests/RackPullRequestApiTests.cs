using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Caisson.Api.Contracts;
using Caisson.Api.Security;
using Caisson.Domain.Git;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using FluentAssertions;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// End-to-end tests for the rack-scoped PR status read endpoints (story #173, Task #213/#215): auth (401),
/// rack-access denial with zero metadata, the no-link representation, the populated status DTO, and the
/// newest-first transition history.
/// </summary>
public sealed class RackPullRequestApiTests : IAsyncLifetime
{
    private readonly PostgresHarness _postgres = new();

    public Task InitializeAsync() => _postgres.InitializeAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [SkippableFact]
    public async Task Anonymous_request_is_unauthorized()
    {
        Skip.IfNot(_postgres.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var rackId = await SeedRackAsync();
        await using var host = NewHost();

        var response = await host.CreateClient().GetAsync($"/api/racks/{rackId}/git/pull-request");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableFact]
    public async Task Accessible_rack_without_a_pr_returns_a_no_link_representation()
    {
        Skip.IfNot(_postgres.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var rackId = await SeedRackAsync();
        await using var host = NewHost();

        var response = await GetAsync(host, $"/api/racks/{rackId}/git/pull-request", "ReadOnly");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<PullRequestStatusDto>();
        dto!.HasPullRequest.Should().BeFalse();
        dto.CanApply.Should().BeFalse();
        dto.GateReasonCode.Should().Be(GitPrGateReasonCodes.NoPrLinked);
    }

    [SkippableFact]
    public async Task Merged_pr_status_is_returned_with_can_apply_true()
    {
        Skip.IfNot(_postgres.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var rackId = await SeedRackAsync();
        await SeedStatusAsync(rackId, GitPullRequestStatus.Merged, GitPullRequestChecksConclusion.Success, prNumber: 7);
        await using var host = NewHost();

        var response = await GetAsync(host, $"/api/racks/{rackId}/git/pull-request", "ReadOnly");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<PullRequestStatusDto>();
        dto!.HasPullRequest.Should().BeTrue();
        dto.State.Should().Be("Merged");
        dto.ChecksConclusion.Should().Be("Success");
        dto.PullRequestNumber.Should().Be(7);
        dto.PullRequestUrl.Should().Be("https://gh/pr/7");
        dto.CanApply.Should().BeTrue();
        dto.GateReasonCode.Should().Be(GitPrGateReasonCodes.Allowed);
    }

    [SkippableFact]
    public async Task Open_pr_status_reports_pr_not_merged()
    {
        Skip.IfNot(_postgres.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var rackId = await SeedRackAsync();
        await SeedStatusAsync(rackId, GitPullRequestStatus.Open, GitPullRequestChecksConclusion.Pending, prNumber: 8);
        await using var host = NewHost();

        var response = await GetAsync(host, $"/api/racks/{rackId}/git/pull-request", "ReadOnly");

        var dto = await response.Content.ReadFromJsonAsync<PullRequestStatusDto>();
        dto!.CanApply.Should().BeFalse();
        dto.GateReasonCode.Should().Be(GitPrGateReasonCodes.PrNotMerged);
    }

    [SkippableFact]
    public async Task Denied_rack_access_returns_no_pr_metadata()
    {
        Skip.IfNot(_postgres.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var rackId = await SeedRackAsync();
        await SeedStatusAsync(rackId, GitPullRequestStatus.Merged, GitPullRequestChecksConclusion.Success, prNumber: 9);
        await using var host = NewHost(denyRackId: rackId);

        var response = await GetAsync(host, $"/api/racks/{rackId}/git/pull-request", "ReadOnly");

        // A denied rack is indistinguishable from a missing one (CheckRackAccessAsync → 404), and no PR
        // metadata (url / pr number) leaks in the body.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("https://gh/pr/9");
    }

    [SkippableFact]
    public async Task Events_endpoint_returns_transitions_newest_first()
    {
        Skip.IfNot(_postgres.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var rackId = await SeedRackAsync();
        await SeedAuditAsync(rackId, GitPrStatusAuditActions.StatusChanged, "Open", "Merged", occurredOffsetSeconds: -10);
        await SeedAuditAsync(rackId, GitPrStatusAuditActions.ChecksChanged, "Open", "Open", occurredOffsetSeconds: 0);
        await using var host = NewHost();

        var response = await GetAsync(host, $"/api/racks/{rackId}/git/pull-request/events", "ReadOnly");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<PagedResult<PrStatusEventDto>>();
        page!.Items.Should().HaveCount(2);
        page.Items[0].Action.Should().Be(GitPrStatusAuditActions.ChecksChanged, "newest first");
        page.Items[1].Action.Should().Be(GitPrStatusAuditActions.StatusChanged);
        page.Items[1].NewState.Should().Be("Merged");
    }

    private static async Task<HttpResponseMessage> GetAsync(ApiHost host, string path, string role)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, role);
        return await host.CreateClient().SendAsync(request);
    }

    private async Task<Guid> SeedRackAsync()
    {
        var rackId = Guid.NewGuid();
        await using var context = _postgres.CreateContext();
        context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "PR Status Test Rack", DateTime.UtcNow));
        await context.SaveChangesAsync();
        return rackId;
    }

    private async Task SeedStatusAsync(
        Guid rackId, GitPullRequestStatus state, GitPullRequestChecksConclusion checks, int prNumber)
    {
        await using var context = _postgres.CreateContext();
        var fingerprint = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))[..64];
        var linkId = Guid.NewGuid();
        var link = new GitPullRequestLink(
            linkId, rackId, "octo", "repo", "caisson/a", fingerprint, "tester", DateTime.UtcNow, Guid.NewGuid().ToString());
        link.MarkPublished(prNumber, $"https://gh/pr/{prNumber}", "commitsha", DateTime.UtcNow);
        if (state != GitPullRequestStatus.Open)
        {
            link.UpdateStatus(state, DateTime.UtcNow);
        }

        var record = new GitPullRequestStatusRecord(
            Guid.NewGuid(), linkId, rackId, "octo", "repo", prNumber, $"https://gh/pr/{prNumber}", DateTime.UtcNow);
        record.ApplyObservation(state, "sha1", checks, checks == GitPullRequestChecksConclusion.Success ? 0 : null, "{}", DateTime.UtcNow);

        context.GitPullRequestLinks.Add(link);
        context.GitPullRequestStatuses.Add(record);
        await context.SaveChangesAsync();
    }

    private async Task SeedAuditAsync(
        Guid rackId, string action, string previousState, string newState, int occurredOffsetSeconds)
    {
        await using var context = _postgres.CreateContext();
        var details = JsonSerializer.Serialize(new
        {
            rackId,
            prNumber = 7,
            previousState,
            newState,
            previousChecks = "Pending",
            newChecks = "Success",
        });
        context.AuditEvents.Add(new TopologyAuditEvent(
            Guid.NewGuid(),
            DateTime.UtcNow.AddSeconds(occurredOffsetSeconds),
            Caisson.Domain.Enums.ActorType.System,
            "system",
            action,
            GitPrStatusAuditActions.TargetType,
            Guid.NewGuid(),
            "success",
            rackId,
            null,
            null,
            details));
        await context.SaveChangesAsync();
    }

    private ApiHost NewHost(Guid? denyRackId = null) => new(_postgres.ConnectionString, denyRackId);

    private sealed class ApiHost : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly Guid? _denyRackId;

        public ApiHost(string connectionString, Guid? denyRackId)
        {
            _connectionString = connectionString;
            _denyRackId = denyRackId;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Caisson", _connectionString);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll(typeof(DbContextOptions<CaissonDbContext>));
                services.RemoveAll(typeof(DbContextOptions));
                services.AddDbContext<CaissonDbContext>(options => options.UseNpgsql(_connectionString));

                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

                if (_denyRackId is { } denied)
                {
                    services.RemoveAll(typeof(IRackAccessPolicy));
                    services.AddSingleton<IRackAccessPolicy>(new DenyRackAccessPolicy(denied));
                }
            });
        }
    }

    private sealed class DenyRackAccessPolicy : IRackAccessPolicy
    {
        private readonly Guid _deniedRackId;

        public DenyRackAccessPolicy(Guid deniedRackId) => _deniedRackId = deniedRackId;

        public Task<bool> CanReadAsync(ClaimsPrincipal user, Guid rackId, CancellationToken cancellationToken)
            => Task.FromResult(rackId != _deniedRackId);
    }
}
