using Caisson.Domain.DesiredState;
using Caisson.Domain.NetworkConfig;
using Caisson.Ingestion.RoundTrip;
using FluentAssertions;
using Xunit;

namespace Caisson.Ingestion.Tests.RoundTrip;

/// <summary>
/// Unit tests for <see cref="CandidateFingerprint"/> (story #172, Q1): the fingerprint is the lowercase
/// 64-hex SHA-256 of the candidate's canonical YAML, so it is stable across collection reordering and changes
/// when the semantic content changes.
/// </summary>
public sealed class CandidateFingerprintTests
{
    private const string RackSlug = "rack-a";

    [Fact]
    public void Compute_returns_a_lowercase_64_hex_digest()
    {
        var model = new SupportedDesiredStateModel(
            RackSlug,
            new[] { new VlanCatalogueEntry(10, "data", null) },
            new[] { new PortAccessIntent("sw1", "ether1", 10) });

        CandidateFingerprint.Compute(model).Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Reordered_collections_yield_the_same_fingerprint()
    {
        var ordered = new SupportedDesiredStateModel(
            RackSlug,
            new[] { new VlanCatalogueEntry(10, "data", null), new VlanCatalogueEntry(20, "voice", null) },
            new[] { new PortAccessIntent("sw1", "ether1", 10), new PortAccessIntent("sw1", "ether2", 20) });

        var reordered = new SupportedDesiredStateModel(
            RackSlug,
            new[] { new VlanCatalogueEntry(20, "voice", null), new VlanCatalogueEntry(10, "data", null) },
            new[] { new PortAccessIntent("sw1", "ether2", 20), new PortAccessIntent("sw1", "ether1", 10) });

        CandidateFingerprint.Compute(ordered).Should().Be(CandidateFingerprint.Compute(reordered));
    }

    [Fact]
    public void Changed_content_yields_a_different_fingerprint()
    {
        var baseModel = new SupportedDesiredStateModel(
            RackSlug,
            new[] { new VlanCatalogueEntry(10, "data", null) },
            new[] { new PortAccessIntent("sw1", "ether1", 10) });

        var changed = new SupportedDesiredStateModel(
            RackSlug,
            new[] { new VlanCatalogueEntry(10, "voice", null) },
            new[] { new PortAccessIntent("sw1", "ether1", 10) });

        CandidateFingerprint.Compute(baseModel).Should().NotBe(CandidateFingerprint.Compute(changed));
    }

    [Fact]
    public void Fingerprint_is_stable_across_repeated_computations()
    {
        var model = new SupportedDesiredStateModel(
            RackSlug,
            new[] { new VlanCatalogueEntry(10, "data", null) },
            new[] { new PortAccessIntent("sw1", "ether1", 10) });

        CandidateFingerprint.Compute(model).Should().Be(CandidateFingerprint.Compute(model));
    }

    /// <summary>
    /// The story #173 (ADR 0062) alignment invariant: the canonical fingerprint ingestion stamps on a
    /// <c>DesiredStateVersion</c> — projecting the materialised document via <see cref="BaselineIntentProjection"/>
    /// and hashing through <see cref="CandidateFingerprint"/> — equals the fingerprint a PR candidate for the
    /// same rack-slug + per-port access-VLAN model is stamped with. This is what lets the merged-apply gate
    /// match an ingested <c>DesiredStateVersion.CandidateFingerprint</c> to a merged
    /// <c>GitPullRequestLink.CandidateFingerprint</c> in the real ingestion→PR→merge→apply pipeline. If the
    /// projection or the primitive ever drifts, the gate silently fails closed in production — exactly the
    /// regression this test guards.
    /// <para>
    /// The M1 ingestion schema carries no VLAN catalogue (ADR 0053), so both sides are compared over the
    /// synthesised <c>vlan-{id}</c> catalogue the projection produces; a candidate authored with different VLAN
    /// names is the separate, documented vlan-catalogue-persistence follow-up, not this fingerprint alignment.
    /// </para>
    /// </summary>
    [Fact]
    public void Ingestion_projection_fingerprint_matches_the_pr_candidate_fingerprint()
    {
        // The persisted materialised document shape BaselineIntentProjection consumes (rackSlug + switches[].ports[]).
        const string desiredStateJson =
            """{"rackSlug":"rack-a","switches":[{"name":"sw1","ports":[{"name":"ether1","accessVlan":10},{"name":"ether2","accessVlan":20}]}]}""";

        // Ingestion side: project the document and fingerprint it (exactly what
        // DesiredStateIngestionService.ComputeCandidateFingerprint does).
        var projected = BaselineIntentProjection.Project(RackSlug, desiredStateJson);
        var ingestedRevisionFingerprint = CandidateFingerprint.Compute(projected);

        // PR side: a candidate for the same rack-slug + port-access model (with the synthesised VLAN catalogue
        // an ingested revision can carry) is stamped with the same fingerprint.
        var prCandidate = new SupportedDesiredStateModel(
            RackSlug,
            new[] { new VlanCatalogueEntry(10, "vlan-10", null), new VlanCatalogueEntry(20, "vlan-20", null) },
            new[] { new PortAccessIntent("sw1", "ether1", 10), new PortAccessIntent("sw1", "ether2", 20) });
        var prLinkFingerprint = CandidateFingerprint.Compute(prCandidate);

        ingestedRevisionFingerprint.Should().MatchRegex("^[0-9a-f]{64}$");
        ingestedRevisionFingerprint.Should().Be(prLinkFingerprint);
    }
}
