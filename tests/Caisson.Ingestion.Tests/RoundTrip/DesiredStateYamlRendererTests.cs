using System.Globalization;
using System.Text;
using Caisson.Domain.DesiredState;
using Caisson.Domain.NetworkConfig;
using Caisson.Ingestion.RoundTrip;
using FluentAssertions;
using Xunit;
using YamlDotNet.RepresentationModel;

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

    [Theory]
    [InlineData("Rack-1")]        // uppercase
    [InlineData("rack_1")]        // underscore
    [InlineData("rack.1")]        // dot
    [InlineData("-rack")]         // leading hyphen
    [InlineData("rack-")]         // trailing hyphen
    public void Non_slug_rack_slug_is_refused_rather_than_emitting_an_unparseable_document(string badSlug)
    {
        // Regression for the AC1/AC2 gap: /render must never emit a metadata.rackSlug that /parse (which
        // enforces DesiredStateSchema.IsValidRackSlug) would reject. rackSlug comes from the rack's
        // ExternalKey, which is only length-bounded — a non-DNS-label key must be refused here.
        var model = new SupportedDesiredStateModel(
            badSlug, Array.Empty<VlanCatalogueEntry>(), Array.Empty<PortAccessIntent>());

        var act = () => DesiredStateYamlRenderer.Render(model);

        act.Should().Throw<DesiredStateRenderException>()
            .Which.Errors.Should().ContainSingle(e => e.Field == "metadata.rackSlug");
    }

    [Fact]
    public void Over_length_rack_slug_is_refused()
    {
        var tooLong = new string('a', DesiredStateSchema.MaxRackSlugLength + 1);
        var model = new SupportedDesiredStateModel(
            tooLong, Array.Empty<VlanCatalogueEntry>(), Array.Empty<PortAccessIntent>());

        var act = () => DesiredStateYamlRenderer.Render(model);

        act.Should().Throw<DesiredStateRenderException>()
            .Which.Errors.Should().ContainSingle(e => e.Field == "metadata.rackSlug");
    }

    [Fact]
    public void Rendered_document_for_a_slug_shaped_key_re_imports_cleanly()
    {
        // The export→re-import round-trip guarantee (AC2): a rendered document must be accepted by the importer.
        var rendered = DesiredStateYamlRenderer.Render(SampleModel()).Yaml;

        var reimported = DesiredStateYamlImporter.Import(rendered);

        reimported.IsSuccess.Should().BeTrue();
        reimported.Envelope!.SupportedModel.RackSlug.Should().Be("rack-1");
    }

    [Fact]
    public void Preserved_block_with_a_non_extensions_anchor_is_refused()
    {
        var wrongAnchor = PreservedYamlBlock.Create("spec", "spec:\n  injected: true\n");

        var act = () => DesiredStateYamlRenderer.Render(SampleModel(), new[] { wrongAnchor });

        act.Should().Throw<DesiredStateRenderException>()
            .Which.Errors.Should().ContainSingle(e => e.Field == "extensions:spec");
    }

    [Fact]
    public void Emitted_key_order_matches_the_schema_constants()
    {
        // Makes the ADR-0049 "renderer can never drift" guarantee real for the hand-written emitter: the
        // emitted key order at every level is pinned to the DesiredStateYamlSchema.*KeyOrder constants, so any
        // reordering in the literal emitter fails here.
        var model = new SupportedDesiredStateModel(
            "rack-1",
            new[] { new VlanCatalogueEntry(10, "storage", "iSCSI") },
            new[] { new PortAccessIntent("sw1", "eth1", 10) });

        var root = LoadRoot(DesiredStateYamlRenderer.Render(model).Yaml);

        KeysOf(root).Should().Equal(DesiredStateYamlSchema.TopLevelKeyOrder.Where(k => k != "extensions"));
        KeysOf(Child(root, "metadata")).Should().Equal(DesiredStateYamlSchema.MetadataKeyOrder);
        KeysOf(Child(root, "spec")).Should().Equal(DesiredStateYamlSchema.SpecKeyOrder);

        var vlan = (YamlMappingNode)((YamlSequenceNode)Child(Child(root, "spec"), "vlans")).Children[0];
        KeysOf(vlan).Should().Equal(DesiredStateYamlSchema.VlanKeyOrder); // vlanId, name, description

        var sw = (YamlMappingNode)((YamlSequenceNode)Child(Child(root, "spec"), "switches")).Children[0];
        KeysOf(sw).Should().Equal(DesiredStateYamlSchema.SwitchKeyOrder);

        var port = (YamlMappingNode)((YamlSequenceNode)Child(sw, "ports")).Children[0];
        KeysOf(port).Should().Equal(DesiredStateYamlSchema.SupportedPortKeyOrder); // name, accessVlan
    }

    private static YamlMappingNode LoadRoot(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static IEnumerable<string> KeysOf(YamlNode node)
        => ((YamlMappingNode)node).Children.Keys.Cast<YamlScalarNode>().Select(k => k.Value!);

    private static YamlNode Child(YamlNode node, string key)
        => ((YamlMappingNode)node).Children[new YamlScalarNode(key)];

    private static string ReadGolden()
        => File.ReadAllText(GoldenPath, Encoding.UTF8).Replace("\r\n", "\n");
}
