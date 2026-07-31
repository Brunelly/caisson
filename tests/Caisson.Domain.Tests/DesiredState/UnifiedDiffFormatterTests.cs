using System.Text;
using System.Text.RegularExpressions;
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

    // --- Memory-linear (Hirschberg) path: guards the story #171 security fix that avoids the ~1 GiB dense
    //     LCS matrix for near-cap racks. The linear reconstruction must produce a valid, optimal, deterministic
    //     diff for all shapes, and the auto-dispatch must use it once the dense matrix would exceed budget.

    [Theory]
    [InlineData("line1\nline2\nline3\n", "line1\nCHANGED\nline3\n")]
    [InlineData("a\nb\nc\nd\ne\n", "a\nB\nc\nd\nE\n")]
    [InlineData("a\nb\nc\n", "a\nx\nb\nc\n")]
    [InlineData("a\nb\nc\nd\n", "a\nd\n")]
    [InlineData("1\n2\n3\n4\n5\n", "1\n2\n3\n4\n5\n6\n7\n")]
    [InlineData("a\nb\nc\n", "a\nb\nc\n")]
    public void Linear_space_diff_reconstructs_the_candidate(string baseline, string candidate)
    {
        var diff = UnifiedDiffFormatter.FormatWithLinearSpaceDiff(baseline, candidate);

        ApplyUnifiedDiff(baseline, diff).Should().Be(candidate);
    }

    [Fact]
    public void Linear_space_diff_is_deterministic()
    {
        var baseline = "a\nb\nc\nd\ne\nf\n";
        var candidate = "a\nX\nc\nd\nY\nf\n";

        var first = UnifiedDiffFormatter.FormatWithLinearSpaceDiff(baseline, candidate);
        var second = UnifiedDiffFormatter.FormatWithLinearSpaceDiff(baseline, candidate);

        first.Should().Be(second);
    }

    [Fact]
    public void Large_inputs_auto_dispatch_to_the_linear_space_path_and_stay_correct()
    {
        // (2101 + 1)^2 exceeds MaxFullMatrixCells (4,000,000), so Format never allocates the dense square
        // matrix — it reconstructs via the memory-linear path instead. Every 10th line differs.
        var baseline = BuildLines(2100, mutate: false);
        var candidate = BuildLines(2100, mutate: true);

        var auto = UnifiedDiffFormatter.Format(baseline, candidate);
        var forcedLinear = UnifiedDiffFormatter.FormatWithLinearSpaceDiff(baseline, candidate);

        auto.Should().Be(forcedLinear);
        auto.Should().NotBeEmpty();
        ApplyUnifiedDiff(baseline, auto).Should().Be(candidate);
    }

    private static string BuildLines(int count, bool mutate)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            var text = mutate && i > 0 && i % 10 == 0 ? $"line{i}-CHANGED" : $"line{i}";
            builder.Append(text).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Applies a unified diff to <paramref name="baseline"/> to reconstruct the candidate — an oracle that
    /// accepts any valid diff (not just a byte-specific one), so it validates the linear-space alignment
    /// without over-fitting to a particular edit script.
    /// </summary>
    private static string ApplyUnifiedDiff(string baseline, string diff)
    {
        if (diff.Length == 0)
        {
            return baseline;
        }

        var header = new Regex(@"^@@ -(\d+),(\d+) \+(\d+),(\d+) @@$");
        var baseLines = baseline.Split('\n');
        var lines = diff.Split('\n');
        var output = new List<string>();
        var cursor = 0;

        for (var i = 0; i < lines.Length;)
        {
            var match = header.Match(lines[i]);
            if (!match.Success)
            {
                i++;
                continue;
            }

            var aStart = int.Parse(match.Groups[1].Value);
            var aCount = int.Parse(match.Groups[2].Value);
            var aStartIdx = aCount == 0 ? aStart : aStart - 1;
            for (; cursor < aStartIdx; cursor++)
            {
                output.Add(baseLines[cursor]);
            }

            for (i++; i < lines.Length && !header.IsMatch(lines[i]); i++)
            {
                var body = lines[i];
                if (body.Length == 0)
                {
                    continue; // the trailing empty element from the diff's final LF
                }

                switch (body[0])
                {
                    case ' ':
                        output.Add(baseLines[cursor]);
                        cursor++;
                        break;
                    case '-':
                        cursor++;
                        break;
                    case '+':
                        output.Add(body[1..]);
                        break;
                }
            }
        }

        for (; cursor < baseLines.Length; cursor++)
        {
            output.Add(baseLines[cursor]);
        }

        return string.Join('\n', output);
    }
}
