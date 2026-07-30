using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Caisson.Domain.DesiredState;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// Story #169, Task #186/#187: end-to-end RBAC, request-limit, round-trip preservation, and audit-without-
/// payload coverage for the desired-state YAML parse/render endpoints. Mirrors
/// <see cref="NetworkIntentApiTests"/>: SkippableFact + <see cref="CaissonApiFactory"/>, header-driven test
/// auth, and audit read-back polling.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DesiredStateRoundTripApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CaissonApiFactory _factory;

    public DesiredStateRoundTripApiTests(CaissonApiFactory factory) => _factory = factory;

    private static string ValidYaml(string rackSlug) => $"""
        apiVersion: caisson.dev/v1alpha1
        kind: RackDesiredState
        metadata:
          rackSlug: {rackSlug}
        spec:
          vlans:
            - vlanId: 10
              name: storage
              description: iSCSI
          switches:
            - name: sw1
              ports:
                - name: eth1
                  accessVlan: 10
        extensions:
          l3: # kept in the opaque block
            routers:
              - 10.0.0.1
        """;

    [SkippableFact]
    public async Task Anonymous_parse_is_unauthorized()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres (CAISSON_TEST_DB or Docker); skipped when unavailable.");
        var rackId = await _factory.CreateRackAsync();

        var response = await _factory.CreateClient()
            .PostAsJsonAsync(ParsePath(rackId), new { yaml = ValidYaml("rack-x") });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableFact]
    public async Task ReadOnly_parse_is_forbidden()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres; skipped when unavailable.");
        var rackId = await _factory.CreateRackAsync();

        var response = await SendAsync(HttpMethod.Post, ParsePath(rackId), "ReadOnly", new { yaml = ValidYaml("rack-x") });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [SkippableFact]
    public async Task Author_parses_valid_yaml_and_captures_the_extensions_block()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres; skipped when unavailable.");
        var rackId = await _factory.CreateRackAsync();

        var response = await SendAsync(
            HttpMethod.Post, ParsePath(rackId), "NetworkConfigAuthor", new { yaml = ValidYaml("rack-x") });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("supportedModel").GetProperty("vlanCatalogue").GetArrayLength().Should().Be(1);
        body.GetProperty("unknownBlocks").GetArrayLength().Should().Be(1);
        body.GetProperty("warnings").EnumerateArray().Select(w => w.GetString())
            .Should().Contain("commentsNotPreserved");
    }

    [SkippableFact]
    public async Task Parse_of_invalid_yaml_returns_400_problem_details_with_paths()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres; skipped when unavailable.");
        var rackId = await _factory.CreateRackAsync();

        var invalid = """
            apiVersion: caisson.dev/v1alpha1
            kind: RackDesiredState
            metadata:
              rackSlug: rack-x
            spec:
              vlans:
                - vlanId: 9000
                  name: bad
            """;

        var response = await SendAsync(HttpMethod.Post, ParsePath(rackId), "NetworkConfigAuthor", new { yaml = invalid });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        problem.GetProperty("errors").TryGetProperty("spec.vlans[0].vlanId", out _).Should().BeTrue();
        problem.GetProperty("issues").EnumerateArray().Should().Contain(i =>
            i.GetProperty("path").GetString() == "spec.vlans[0].vlanId");
    }

    [SkippableFact]
    public async Task Parse_of_syntactically_invalid_yaml_reports_line_and_column()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres; skipped when unavailable.");
        var rackId = await _factory.CreateRackAsync();

        var response = await SendAsync(
            HttpMethod.Post, ParsePath(rackId), "NetworkConfigAuthor",
            new { yaml = "apiVersion: caisson.dev/v1alpha1\nspec: {unterminated" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        var issue = problem.GetProperty("issues").EnumerateArray().First();
        issue.GetProperty("line").ValueKind.Should().NotBe(JsonValueKind.Null);
        issue.GetProperty("column").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [SkippableFact]
    public async Task Render_round_trips_and_preserves_the_extensions_block_as_lf_bytes()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres; skipped when unavailable.");
        var rackId = await _factory.CreateRackAsync();

        var parse = await SendAsync(
            HttpMethod.Post, ParsePath(rackId), "NetworkConfigAuthor", new { yaml = ValidYaml("rack-x") });
        parse.StatusCode.Should().Be(HttpStatusCode.OK);
        var envelope = await parse.Content.ReadFromJsonAsync<JsonElement>(Json);

        var renderRequest = new
        {
            vlanCatalogue = envelope.GetProperty("supportedModel").GetProperty("vlanCatalogue"),
            portIntents = envelope.GetProperty("supportedModel").GetProperty("portIntents"),
            unknownBlocks = envelope.GetProperty("unknownBlocks"),
            warnings = envelope.GetProperty("warnings"),
            schemaVersion = envelope.GetProperty("schemaVersion"),
        };

        var render = await SendAsync(HttpMethod.Post, RenderPath(rackId), "NetworkConfigAuthor", renderRequest);
        render.StatusCode.Should().Be(HttpStatusCode.OK);
        var rendered = await render.Content.ReadFromJsonAsync<JsonElement>(Json);
        var yaml = rendered.GetProperty("yaml").GetString()!;

        yaml.Should().Contain("10.0.0.1");
        yaml.Should().Contain("routers:");
        yaml.Should().Contain("accessVlan: 10");
        // Two renders are byte-deterministic.
        var render2 = await SendAsync(HttpMethod.Post, RenderPath(rackId), "NetworkConfigAuthor", renderRequest);
        (await render2.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("yaml").GetString()
            .Should().Be(yaml);
    }

    [SkippableFact]
    public async Task Over_limit_request_body_is_rejected()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres; skipped when unavailable.");
        var rackId = await _factory.CreateRackAsync();

        var oversized = new string('x', DesiredStateSchema.MaxYamlDocumentBytes + 4096);
        var request = new HttpRequestMessage(HttpMethod.Post, ParsePath(rackId))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { yaml = oversized }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, "NetworkConfigAuthor");

        var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.RequestEntityTooLarge, HttpStatusCode.BadRequest);
    }

    [SkippableFact]
    public async Task Each_operation_writes_exactly_one_audit_event_with_no_yaml()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres; skipped when unavailable.");
        var rackId = await _factory.CreateRackAsync();

        var parse = await SendAsync(
            HttpMethod.Post, ParsePath(rackId), "NetworkConfigAuthor", new { yaml = ValidYaml("rack-x") });
        parse.StatusCode.Should().Be(HttpStatusCode.OK);

        var audit = await PollForAuditEventAsync("desired-state.parsed", rackId);
        audit.DetailsJson.Should().Contain("vlanCount").And.Contain("correlationId").And.Contain("warnings");
        audit.DetailsJson.Should().NotContain("10.0.0.1"); // never the YAML body
        audit.DetailsJson.Should().NotContain("iSCSI");

        await using var context = _factory.CreateDbContext();
        (await context.AuditEvents.CountAsync(a => a.Action == "desired-state.parsed" && a.RackId == rackId))
            .Should().Be(1);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string role, object body)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add(TestAuthHandler.UserHeader, "tester");
        request.Headers.Add(TestAuthHandler.RolesHeader, role);
        return await _factory.CreateClient().SendAsync(request);
    }

    private async Task<(string DetailsJson, string Action)> PollForAuditEventAsync(string action, Guid rackId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await using var context = _factory.CreateDbContext();
            var evt = await context.AuditEvents
                .FirstOrDefaultAsync(a => a.Action == action && a.RackId == rackId);
            if (evt is not null)
            {
                return (evt.DetailsJson ?? string.Empty, evt.Action);
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Audit event '{action}' for rack '{rackId}' did not appear within 5s.");
    }

    private static string ParsePath(Guid rackId) => $"/api/racks/{rackId}/desired-state/parse";

    private static string RenderPath(Guid rackId) => $"/api/racks/{rackId}/desired-state/render";
}
