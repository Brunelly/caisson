using Caisson.Domain.DesiredState;
using Caisson.Ingestion.Schema;
using FluentAssertions;
using Xunit;

namespace Caisson.Ingestion.Tests;

/// <summary>
/// Story #62, AC2: every validation case the acceptance criteria calls out, asserting the full
/// <see cref="DesiredStateValidationIssue"/> shape (file path + location + message, and line/column for
/// syntax errors) — not just "it failed".
/// </summary>
public sealed class DesiredStateValidatorTests
{
    private const string FilePath = "desired-state/racks/rack-1.yaml";

    private static DesiredStateValidationResult ParseAndValidate(string yaml, string filePath = FilePath)
    {
        var parsed = DesiredStateYamlParser.Parse(filePath, yaml);
        parsed.IsSuccess.Should().BeTrue("the fixture YAML must be syntactically valid for this test");
        return DesiredStateValidator.Validate(filePath, parsed.Root!);
    }

    [Fact]
    public void Valid_document_materialises_with_no_issues()
    {
        const string yaml = """
            rackSlug: rack-1
            switches:
              - name: switch-a
                ports:
                  - name: eth0
                    accessVlan: 100
                    description: uplink
                    neighbor:
                      systemName: leaf-1
                      portId: Ethernet1
            """;

        var result = ParseAndValidate(yaml);

        result.IsValid.Should().BeTrue();
        result.Issues.Should().BeEmpty();
        result.Document!.RackSlug.Should().Be("rack-1");
        result.Document.Switches.Should().ContainSingle().Which.Ports.Should().ContainSingle();
        var port = result.Document.Switches[0].Ports[0];
        port.AccessVlan.Should().Be(100);
        port.Description.Should().Be("uplink");
        port.NeighborSystemName.Should().Be("leaf-1");
        port.NeighborPortId.Should().Be("Ethernet1");
    }

    [Fact]
    public void Out_of_range_vlan_is_rejected_with_location()
    {
        const string yaml = """
            rackSlug: rack-1
            switches:
              - name: switch-a
                ports:
                  - name: eth0
                    accessVlan: 5000
            """;

        var result = ParseAndValidate(yaml);

        result.IsValid.Should().BeFalse();
        result.Issues.Should().ContainSingle(i =>
            i.Location == "/switches/0/ports/0/accessVlan"
            && i.FilePath == FilePath
            && i.Message.Contains("5000", StringComparison.Ordinal));
    }

    [Fact]
    public void Non_integer_vlan_is_rejected()
    {
        const string yaml = """
            rackSlug: rack-1
            switches:
              - name: switch-a
                ports:
                  - name: eth0
                    accessVlan: not-a-number
            """;

        var result = ParseAndValidate(yaml);

        result.IsValid.Should().BeFalse();
        result.Issues.Should().ContainSingle(i =>
            i.Location == "/switches/0/ports/0/accessVlan" && i.Message.Contains("integer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unknown_top_level_field_is_rejected()
    {
        const string yaml = """
            rackSlug: rack-1
            switches: []
            unexpectedField: true
            """;

        var result = ParseAndValidate(yaml);

        result.IsValid.Should().BeFalse();
        result.Issues.Should().ContainSingle(i =>
            i.Location == "/unexpectedField" && i.Message.Contains("Unknown field", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_port_field_is_rejected()
    {
        const string yaml = """
            rackSlug: rack-1
            switches:
              - name: switch-a
                ports:
                  - name: eth0
                    accessVlan: 10
                    bogusField: nope
            """;

        var result = ParseAndValidate(yaml);

        result.IsValid.Should().BeFalse();
        result.Issues.Should().ContainSingle(i => i.Location == "/switches/0/ports/0/bogusField");
    }

    [Fact]
    public void Syntactically_invalid_yaml_reports_file_line_and_column()
    {
        const string yaml = "rackSlug: rack-1\nswitches: [this is not valid: yaml::: {{{";

        var parsed = DesiredStateYamlParser.Parse(FilePath, yaml);

        parsed.IsSuccess.Should().BeFalse();
        parsed.Error!.FilePath.Should().Be(FilePath);
        parsed.Error.Line.Should().NotBeNull();
        parsed.Error.Column.Should().NotBeNull();
        parsed.Error.Message.Should().Contain("YAML parse error");
    }

    [Fact]
    public void Oversized_document_is_rejected_before_full_parse_with_no_hang()
    {
        var hugeDescription = new string('a', DesiredStateSchema.MaxYamlDocumentBytes + 1);
        var yaml = $"rackSlug: rack-1\nswitches: []\n# {hugeDescription}\n";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var parsed = DesiredStateYamlParser.Parse(FilePath, yaml);
        stopwatch.Stop();

        parsed.IsSuccess.Should().BeFalse();
        parsed.Error!.Message.Should().Contain("bytes");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Too_many_switches_is_rejected()
    {
        var switches = string.Join(
            '\n',
            Enumerable.Range(0, DesiredStateSchema.MaxSwitchesPerRack + 1)
                .Select(i => $"  - name: switch-{i}\n    ports: []"));
        var yaml = $"rackSlug: rack-1\nswitches:\n{switches}\n";

        var result = ParseAndValidate(yaml);

        result.IsValid.Should().BeFalse();
        result.Issues.Should().ContainSingle(i => i.Location == "/switches" && i.Message.Contains("exceeding"));
    }

    [Fact]
    public void Too_many_ports_is_rejected()
    {
        var ports = string.Join(
            '\n',
            Enumerable.Range(0, DesiredStateSchema.MaxPortsPerRack + 1)
                .Select(i => $"      - name: eth{i}\n        accessVlan: 10"));
        var yaml = $"rackSlug: rack-1\nswitches:\n  - name: switch-a\n    ports:\n{ports}\n";

        var result = ParseAndValidate(yaml);

        result.IsValid.Should().BeFalse();
        result.Issues.Should().ContainSingle(i => i.Location == "/switches" && i.Message.Contains("port"));
    }

    [Fact]
    public void Duplicate_switch_names_are_rejected()
    {
        const string yaml = """
            rackSlug: rack-1
            switches:
              - name: switch-a
                ports: []
              - name: switch-a
                ports: []
            """;

        var result = ParseAndValidate(yaml);

        result.IsValid.Should().BeFalse();
        result.Issues.Should().ContainSingle(i => i.Message.Contains("Duplicate switch name", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_port_names_on_the_same_switch_are_rejected()
    {
        const string yaml = """
            rackSlug: rack-1
            switches:
              - name: switch-a
                ports:
                  - name: eth0
                    accessVlan: 10
                  - name: eth0
                    accessVlan: 20
            """;

        var result = ParseAndValidate(yaml);

        result.IsValid.Should().BeFalse();
        result.Issues.Should().ContainSingle(i => i.Message.Contains("Duplicate port name", StringComparison.Ordinal));
    }

    [Fact]
    public void RackSlug_mismatched_with_file_name_is_rejected()
    {
        const string yaml = """
            rackSlug: some-other-rack
            switches: []
            """;

        var result = ParseAndValidate(yaml);

        result.IsValid.Should().BeFalse();
        result.Issues.Should().ContainSingle(i => i.Location == "/rackSlug" && i.Message.Contains("does not match"));
    }

    [Fact]
    public void Description_over_the_length_bound_is_rejected()
    {
        var yaml = $"""
            rackSlug: rack-1
            switches:
              - name: switch-a
                ports:
                  - name: eth0
                    accessVlan: 10
                    description: "{new string('a', DesiredStateSchema.MaxDescriptionLength + 1)}"
            """;

        var result = ParseAndValidate(yaml);

        result.IsValid.Should().BeFalse();
        result.Issues.Should().ContainSingle(i => i.Location == "/switches/0/ports/0/description");
    }

    [Fact]
    public void Errors_accumulate_rather_than_failing_on_the_first_problem()
    {
        const string yaml = """
            rackSlug: rack-1
            switches:
              - name: switch-a
                ports:
                  - name: eth0
                    accessVlan: 5000
                  - name: eth1
                    accessVlan: not-a-number
            """;

        var result = ParseAndValidate(yaml);

        result.IsValid.Should().BeFalse();
        result.Issues.Should().HaveCount(2);
    }
}
