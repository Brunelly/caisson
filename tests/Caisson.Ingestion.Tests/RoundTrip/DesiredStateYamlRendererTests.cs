using System.Globalization;
using System.Text;
using Caisson.Domain.DesiredState;
using Caisson.Domain.NetworkConfig;
using Caisson.Ingestion.RoundTrip;
using FluentAssertions;
using Xunit;

namespace Caisson.Ingestion.Tests.RoundTrip;

/// <summary>
/// Story #169, Task #184/#186 (AC1/NFR1): the hand-written renderer must be deterministic (byte-identical
/// repeat renders), locale-independent, sort lists by the defined keys (never insertion order), omit
/// null-intent ports, quote ambiguous scalars, and re-emit preserved extensions blocks byte-for-byte.
/// </summary>
public sealed class DesiredStateYamlRendererTests
{
    private static readonly string GoldenPath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "RoundTrip", "canonical-rack.yaml");

    /// <summary>The canonical sample model — VLANs and ports deliberately in NON-sorted insertion order.</summary>
    private static SupportedDesiredStateModel SampleModel() => new(
        "rack-1",
        new[]
        {
            new VlanCatalogueEntry(20, "mgmt", null),
            new VlanCatalogueEntry(10, "storage", "iSCSI"),
            new VlanCatalogueEntry(30, "public", "DMZ zone"),
        },
        new[]
        {
            new PortAccessIntent("sw2", "eth3", 20),
            new PortAccessIntent("sw1", "eth2", 10),
            new PortAccessIntent("sw1", "eth1", 20),
            new PortAccessIntent("sw1", "eth10", null), // null intent => omitted entirely
        });

    [Fact]
    public void Renders_the_committed_golden_file_byte_for_byte()
    {
        var golden = ReadGolden();

        var result = DesiredStateYamlRenderer.Render(SampleModel());

        result.Yaml.Should().Be(golden);
    }

    [Fact]
    public void Repeated_renders_are_byte_identical()
    {
        var first = DesiredStateYamlRenderer.Render(SampleModel()).Yaml;
        var second = DesiredStateYamlRenderer.Render(SampleModel()).Yaml;

        second.Should().Be(first);
    }

    [Fact]
    public void Output_is_lf_only_with_exactly_one_terminal_newline()
    {
        var yaml = DesiredStateYamlRenderer.Render(SampleModel()).Yaml;

        yaml.Should().NotContain("\r");
        yaml.Should().EndWith("\n");
        yaml.Should().NotEndWith("\n\n");
    }

    [Fact]
    public void Insertion_order_does_not_affect_output()
    {
        var reordered = new SupportedDesiredStateModel(
            "rack-1",
            new[]
            {
                new VlanCatalogueEntry(30, "public", "DMZ zone"),
                new VlanCatalogueEntry(10, "storage", "iSCSI"),
                new VlanCatalogueEntry(20, "mgmt", null),
            },
            new[]
            {
                new PortAccessIntent("sw1", "eth1", 20),
                new PortAccessIntent("sw2", "eth3", 20),
                new PortAccessIntent("sw1", "eth10", null),
                new PortAccessIntent("sw1", "eth2", 10),
            });

        DesiredStateYamlRenderer.Render(reordered).Yaml
            .Should().Be(DesiredStateYamlRenderer.Render(SampleModel()).Yaml);
    }

    [Fact]
    public void Render_is_locale_independent()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");
            var underTr = DesiredStateYamlRenderer.Render(SampleModel()).Yaml;

            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            var underInvariant = DesiredStateYamlRenderer.Render(SampleModel()).Yaml;

            underTr.Should().Be(underInvariant);
            underTr.Should().Be(ReadGolden());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void Empty_catalogue_and_no_intents_emit_empty_sequences()
    {
        var yaml = DesiredStateYamlRenderer.Render(
            new SupportedDesiredStateModel("rack-1", Array.Empty<VlanCatalogueEntry>(), Array.Empty<PortAccessIntent>())).Yaml;

        yaml.Should().Contain("vlans: []");
        yaml.Should().Contain("switches: []");
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("~")]
    [InlineData("no")]
    [InlineData("0100")]
    [InlineData("0x1f")]
    [InlineData("123")]
    [InlineData("1.5")]
    public void Ambiguous_scalar_names_are_quoted(string ambiguous)
    {
        var yaml = DesiredStateYamlRenderer.Render(
            new SupportedDesiredStateModel(
                "rack-1",
                new[] { new VlanCatalogueEntry(10, ambiguous, null) },
                Array.Empty<PortAccessIntent>())).Yaml;

        yaml.Should().Contain($"name: \"{ambiguous}\"");
    }

    [Fact]
    public void Values_with_colon_or_hash_are_quoted()
    {
        var yaml = DesiredStateYamlRenderer.Render(
            new SupportedDesiredStateModel(
                "rack-1",
                new[] { new VlanCatalogueEntry(10, "storage", "a: b # c") },
                Array.Empty<PortAccessIntent>())).Yaml;

        yaml.Should().Contain("description: \"a: b # c\"");
    }

    [Fact]
    public void Invalid_model_is_refused_via_the_shared_validator()
    {
        var invalid = new SupportedDesiredStateModel(
            "rack-1",
            new[] { new VlanCatalogueEntry(9000, "bad", null) }, // out of range
            Array.Empty<PortAccessIntent>());

        var act = () => DesiredStateYamlRenderer.Render(invalid);

        act.Should().Throw<DesiredStateRenderException>()
            .Which.Errors.Should().ContainSingle(e => e.Field == "vlanCatalogue[0].id");
    }

    [Fact]
    public void Preserved_extensions_block_is_re_emitted_verbatim_after_the_supported_sections()
    {
        var raw = "extensions:\r\n  l3:\r\n      weirdIndent: kept\r\n  # inside comment kept\n";
        var block = PreservedYamlBlock.Create("extensions", raw);

        var yaml = DesiredStateYamlRenderer.Render(SampleModel(), new[] { block }).Yaml;

        yaml.Should().EndWith(raw);
        yaml.Should().Contain("\r\n"); // block CRLF preserved verbatim even though generated part is LF-only
        yaml.IndexOf("extensions:", StringComparison.Ordinal)
            .Should().BeGreaterThan(yaml.IndexOf("switches:", StringComparison.Ordinal));
    }

    [Fact]
    public void Checksum_mismatch_in_a_preserved_block_is_rejected()
    {
        var tampered = new PreservedYamlBlock("extensions", "extensions:\n  l3: kept\n", "deadbeef");

        var act = () => DesiredStateYamlRenderer.Render(SampleModel(), new[] { tampered });

        act.Should().Throw<DesiredStateRenderException>();
    }

    [Fact]
    public void Warnings_are_carried_through()
    {
        var warnings = new[] { DesiredStateRoundTripWarningCode.CommentsNotPreserved };

        var result = DesiredStateYamlRenderer.Render(SampleModel(), warnings: warnings);

        result.Warnings.Should().ContainSingle().Which.Should().Be(DesiredStateRoundTripWarningCode.CommentsNotPreserved);
    }

    private static string ReadGolden()
        => File.ReadAllText(GoldenPath, Encoding.UTF8).Replace("\r\n", "\n");
}
