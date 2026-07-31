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
}
