using System.Text.Json;
using Caisson.Api.Contracts;
using Caisson.Api.Services;
using FluentAssertions;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// Pure unit tests for <see cref="PrMetadataComposer"/> (story #172, AC1): the title format and the body's
/// fenced machine-readable JSON block (rack/operator/timestamp/fingerprint/validation-run/counts/correlationId)
/// plus the human-readable summary.
/// </summary>
public sealed class PrMetadataComposerTests
{
    [Fact]
    public void Title_carries_the_rack_and_operator()
    {
        PrMetadataComposer.ComposeTitle("rack-a", "jdoe")
            .Should().Be("Rack rack-a: network desired-state update (jdoe)");
    }

    [Fact]
    public void Body_carries_a_parseable_json_evidence_block_and_a_human_summary()
    {
        var summary = new PrChangeSummary(1, 0, 0, 2, 0, 1, 4);
        var model = new PrBodyModel(
            "rack-a", "jdoe", new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc),
            "1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e0f1a2b",
            "run-123", new[] { "SAFETY_UPLINK_PORT" }, summary, "corr-9");

        var body = PrMetadataComposer.ComposeBody(model);

        body.Should().Contain("```json");
        body.Should().Contain("### Change summary");
        body.Should().Contain("SAFETY_UPLINK_PORT");

        var json = ExtractJson(body);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("rack").GetString().Should().Be("rack-a");
        root.GetProperty("operator").GetString().Should().Be("jdoe");
        root.GetProperty("timestampUtc").GetString().Should().Be("2026-07-31T12:00:00Z");
        root.GetProperty("candidateFingerprint").GetString().Should().Be(model.CandidateFingerprint);
        root.GetProperty("validationRunId").GetString().Should().Be("run-123");
        root.GetProperty("correlationId").GetString().Should().Be("corr-9");
        root.GetProperty("changeSummary").GetProperty("total").GetInt32().Should().Be(4);
    }

    private static string ExtractJson(string body)
    {
        const string fence = "```json";
        var start = body.IndexOf(fence, StringComparison.Ordinal) + fence.Length;
        var end = body.IndexOf("```", start, StringComparison.Ordinal);
        return body[start..end].Trim();
    }
}
