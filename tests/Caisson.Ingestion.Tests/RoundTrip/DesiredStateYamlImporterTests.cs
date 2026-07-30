using System.Diagnostics;
using System.Text;
using Caisson.Domain.DesiredState;
using Caisson.Ingestion.RoundTrip;
using FluentAssertions;
using Xunit;

namespace Caisson.Ingestion.Tests.RoundTrip;

/// <summary>
/// Story #169, Task #183/#186 (AC2/AC3/AC4/NFR3): the importer must extract the supported model, capture the
/// <c>extensions</c> block byte-for-byte, warn on comments, reject syntax/schema/semantic errors fail-fast
/// with paths and no partial model, and bound resource use against malicious payloads.
/// </summary>
public sealed class DesiredStateYamlImporterTests
{
    private const string Header = """
        apiVersion: caisson.dev/v1alpha1
        kind: RackDesiredState
        metadata:
          rackSlug: rack-1
        """;

    private static string ValidDocument() => $"""
        {Header}
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
        """;

    [Fact]
    public void Imports_the_supported_model()
    {
        var result = DesiredStateYamlImporter.Import(ValidDocument());

        result.IsSuccess.Should().BeTrue();
        var model = result.Envelope!.SupportedModel;
        model.RackSlug.Should().Be("rack-1");
        model.VlanCatalogue.Should().ContainSingle(v => v.Id == 10 && v.Name == "storage" && v.Description == "iSCSI");
        model.PortIntents.Should().ContainSingle(p =>
            p.SwitchStableKey == "sw1" && p.PortName == "eth1" && p.AccessVlanId == 10);
        result.Envelope.SchemaVersion.Should().Be(DesiredStateSchema.CurrentSchemaVersion);
        result.Envelope.Warnings.Should().BeEmpty();
        result.Envelope.UnknownBlocks.Should().BeEmpty();
    }

    [Fact]
    public void Syntactically_invalid_yaml_yields_line_column_and_no_model()
    {
        var result = DesiredStateYamlImporter.Import("apiVersion: caisson.dev/v1alpha1\nspec: {unterminated");

        result.IsSuccess.Should().BeFalse();
        result.Envelope.Should().BeNull();
        result.Issues.Should().ContainSingle();
        result.Issues[0].Line.Should().NotBeNull();
        result.Issues[0].Column.Should().NotBeNull();
    }

    [Fact]
    public void Multi_document_input_is_rejected_fail_fast_rather_than_dropping_content()
    {
        // A '---'-separated stream would otherwise silently keep only the first document (lossy round-trip);
        // it must be rejected with an actionable error and line/column of the second document (AC4).
        var yaml = $"{ValidDocument()}\n---\n{ValidDocument()}";

        var result = DesiredStateYamlImporter.Import(yaml);

        result.IsSuccess.Should().BeFalse();
        result.Envelope.Should().BeNull();
        result.Issues.Should().ContainSingle();
        result.Issues[0].Message.Should().Contain("exactly one YAML document");
        result.Issues[0].Line.Should().NotBeNull();
    }

    [Fact]
    public void Out_of_range_vlan_is_rejected_with_its_yaml_path()
    {
        var yaml = $"""
            {Header}
            spec:
              vlans:
                - vlanId: 1
                  name: a
                - vlanId: 2
                  name: b
                - vlanId: 9000
                  name: c
            """;

        var result = DesiredStateYamlImporter.Import(yaml);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Path == "spec.vlans[2].vlanId");
    }

    [Fact]
    public void Duplicate_vlan_id_is_rejected()
    {
        var yaml = $"""
            {Header}
            spec:
              vlans:
                - vlanId: 10
                  name: a
                - vlanId: 10
                  name: b
            """;

        var result = DesiredStateYamlImporter.Import(yaml);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Path == "spec.vlans[1].vlanId");
    }

    [Fact]
    public void Port_referencing_absent_vlan_is_rejected_with_switch_and_port_path()
    {
        var yaml = $"""
            {Header}
            spec:
              vlans:
                - vlanId: 10
                  name: a
              switches:
                - name: sw1
                  ports:
                    - name: eth1
                      accessVlan: 999
            """;

        var result = DesiredStateYamlImporter.Import(yaml);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Path == "spec.switches[0].ports[0].accessVlan");
    }

    [Fact]
    public void Unknown_top_level_key_other_than_extensions_is_rejected()
    {
        var yaml = $"""
            {Header}
            spec:
              vlans: []
              switches: []
            surprise:
              foo: bar
            """;

        var result = DesiredStateYamlImporter.Import(yaml);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Path == "surprise");
    }

    [Fact]
    public void Unknown_port_key_is_rejected()
    {
        var yaml = $"""
            {Header}
            spec:
              vlans:
                - vlanId: 10
                  name: a
              switches:
                - name: sw1
                  ports:
                    - name: eth1
                      accessVlan: 10
                      description: not-supported-in-v1
            """;

        var result = DesiredStateYamlImporter.Import(yaml);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Path == "spec.switches[0].ports[0].description");
    }

    [Fact]
    public void Comments_outside_extensions_produce_a_warning()
    {
        var yaml = $"""
            {Header}
            spec:
              vlans:
                - vlanId: 10 # the storage vlan
                  name: storage
              switches: []
            """;

        var result = DesiredStateYamlImporter.Import(yaml);

        result.IsSuccess.Should().BeTrue();
        result.Envelope!.Warnings.Should().Contain(DesiredStateRoundTripWarningCode.CommentsNotPreserved);
    }

    [Fact]
    public void Hash_inside_a_quoted_scalar_is_not_treated_as_a_comment()
    {
        var yaml = $"""
            {Header}
            spec:
              vlans:
                - vlanId: 10
                  name: storage
                  description: "value # not a comment"
              switches: []
            """;

        var result = DesiredStateYamlImporter.Import(yaml);

        result.IsSuccess.Should().BeTrue();
        result.Envelope!.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Extensions_block_is_captured_byte_for_byte_with_matching_checksum()
    {
        var extensions = "extensions:\r\n  l3:\r\n        oddIndent: kept\r\n  # comment kept in the opaque block\n";
        var yaml = ValidDocument() + "\n" + extensions;

        var result = DesiredStateYamlImporter.Import(yaml);

        result.IsSuccess.Should().BeTrue();
        var block = result.Envelope!.UnknownBlocks.Should().ContainSingle().Subject;
        block.AnchorPath.Should().Be("extensions");
        block.RawYamlText.Should().Be(extensions);
        block.ChecksumMatches().Should().BeTrue();
        // A comment that lives ONLY inside the opaque extensions bytes is preserved, so it must NOT warn.
        result.Envelope.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Round_trip_preserves_the_extensions_block_exactly()
    {
        var extensions = "extensions:\n  l3:\n    routers:\n      - 10.0.0.1\n";
        var yaml = ValidDocument() + "\n" + extensions;

        var imported = DesiredStateYamlImporter.Import(yaml);
        imported.IsSuccess.Should().BeTrue();

        var rendered = DesiredStateYamlRenderer.Render(
            imported.Envelope!.SupportedModel, imported.Envelope.UnknownBlocks, imported.Envelope.Warnings);

        rendered.Yaml.Should().EndWith(extensions);

        // Re-importing the rendered document yields the same model + same preserved block (fixed point).
        var reimported = DesiredStateYamlImporter.Import(rendered.Yaml);
        reimported.IsSuccess.Should().BeTrue();
        reimported.Envelope!.UnknownBlocks.Should().ContainSingle()
            .Which.RawYamlText.Should().Be(extensions);
        DesiredStateYamlRenderer.Render(
                reimported.Envelope.SupportedModel, reimported.Envelope.UnknownBlocks).Yaml
            .Should().Be(rendered.Yaml);
    }

    [Fact]
    public void Oversized_document_is_rejected_before_full_parse()
    {
        var huge = new string('x', DesiredStateSchema.MaxYamlDocumentBytes + 1024);
        var yaml = $"{Header}\nspec:\n  vlans:\n    - vlanId: 10\n      name: \"{huge}\"";

        var stopwatch = Stopwatch.StartNew();
        var result = DesiredStateYamlImporter.Import(yaml);
        stopwatch.Stop();

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().ContainSingle(i => i.Message.Contains("bytes"));
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Billion_laughs_payload_is_bounded_and_does_not_hang()
    {
        // Small in raw bytes, exponential when naively expanded — the byte cap + node budget must contain it.
        var sb = new StringBuilder();
        sb.AppendLine("apiVersion: caisson.dev/v1alpha1");
        sb.AppendLine("kind: RackDesiredState");
        sb.AppendLine("a: &a [\"x\",\"x\",\"x\",\"x\",\"x\",\"x\",\"x\",\"x\",\"x\"]");
        sb.AppendLine("b: &b [*a,*a,*a,*a,*a,*a,*a,*a,*a]");
        sb.AppendLine("c: &c [*b,*b,*b,*b,*b,*b,*b,*b,*b]");
        sb.AppendLine("d: &d [*c,*c,*c,*c,*c,*c,*c,*c,*c]");
        sb.AppendLine("e: &e [*d,*d,*d,*d,*d,*d,*d,*d,*d]");

        var stopwatch = Stopwatch.StartNew();
        var result = DesiredStateYamlImporter.Import(sb.ToString());
        stopwatch.Stop();

        // It is rejected (unknown keys / structure), never a hang or OOM.
        result.IsSuccess.Should().BeFalse();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Missing_required_field_is_rejected_with_no_model()
    {
        var yaml = $"""
            {Header}
            spec:
              vlans:
                - name: no-id-here
            """;

        var result = DesiredStateYamlImporter.Import(yaml);

        result.IsSuccess.Should().BeFalse();
        result.Envelope.Should().BeNull();
        result.Issues.Should().Contain(i => i.Path == "spec.vlans[0].vlanId");
    }
}
