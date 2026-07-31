using Caisson.Domain.DesiredState.Diffing;
using FluentAssertions;
using Xunit;

namespace Caisson.Domain.Tests.DesiredState;

/// <summary>
/// Tests for <see cref="UnifiedDiffFormatter"/> (story #171, AC1): standard <c>@@</c> hunks with +/-/space
/// prefixes, identical inputs yield an empty diff, LF handling, and determinism (NFR3).
/// </summary>
public sealed class UnifiedDiffFormatterTests
{
    [Fact]
    public void Identical_inputs_yield_an_empty_diff()
    {
        const string text = "a\nb\nc\n";

        UnifiedDiffFormatter.Format(text, text).Should().BeEmpty();
    }

    [Fact]
    public void Emits_a_standard_hunk_header_with_line_prefixes()
    {
        var baseline = "line1\nline2\nline3\n";
        var candidate = "line1\nCHANGED\nline3\n";

        var diff = UnifiedDiffFormatter.Format(baseline, candidate);

        diff.Should().Contain("@@ -");
        diff.Should().Contain("-line2\n");
        diff.Should().Contain("+CHANGED\n");
        diff.Should().Contain(" line1\n"); // context line prefixed with a space
    }

    [Fact]
    public void Every_changed_line_carries_a_plus_or_minus_glyph()
    {
        var diff = UnifiedDiffFormatter.Format("x\n", "y\n");

        foreach (var line in diff.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            (line[0] is '@' or '+' or '-' or ' ').Should().BeTrue($"line '{line}' must start with a diff glyph");
        }
    }

    [Fact]
    public void Added_lines_appear_with_a_plus_prefix_inside_a_hunk()
    {
        var diff = UnifiedDiffFormatter.Format("a\n", "a\nb\n");

        diff.Should().StartWith("@@ -");
        diff.Should().Contain("+b\n");
        diff.Should().Contain(" a\n"); // the unchanged 'a' is context
    }

    [Fact]
    public void Repeated_calls_with_identical_inputs_are_byte_identical()
    {
        var baseline = "a\nb\nc\nd\ne\n";
        var candidate = "a\nB\nc\nd\nE\n";

        var first = UnifiedDiffFormatter.Format(baseline, candidate);
        var second = UnifiedDiffFormatter.Format(baseline, candidate);

        first.Should().Be(second);
        first.Should().NotBeEmpty();
    }

    [Fact]
    public void Context_lines_group_nearby_changes_into_one_hunk()
    {
        var baseline = "1\n2\n3\n4\n5\n6\n7\n";
        var candidate = "1\nX\n3\n4\n5\nY\n7\n";

        var diff = UnifiedDiffFormatter.Format(baseline, candidate, contextLines: 3);

        // With 3 context lines the two changes (lines 2 and 6) are within 2*context of each other -> one hunk.
        diff.Split("@@ -", StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(1);
    }
}
