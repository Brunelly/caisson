using System.Globalization;
using System.Text;

namespace Caisson.Domain.DesiredState.Diffing;

/// <summary>
/// A pure, hand-rolled LCS-based unified-diff generator over two canonical-YAML strings (story #171, AC1;
/// answered design Q — diff the canonicalized YAML for reduced noise). Deliberately in-domain and
/// dependency-free (ADR 0053): canonical rack YAML is small (hundreds of lines), so the O(n·m) LCS is safe
/// and this keeps the diff logic AOT-clean, shareable, and fully deterministic — identical inputs always
/// yield the identical bytes, and identical inputs yield an EMPTY diff. Emits standard
/// <c>@@ -a,b +c,d @@</c> hunks with <c>'+'</c>/<c>'-'</c>/space line prefixes and a configurable number of
/// surrounding context lines. LF-only line splitting matches the renderer's LF-only output.
/// </summary>
public static class UnifiedDiffFormatter
{
    /// <summary>The default number of unchanged context lines emitted around each change (git's default).</summary>
    public const int DefaultContextLines = 3;

    private enum Op
    {
        Equal,
        Delete,
        Insert,
    }

    private readonly record struct DiffLine(Op Op, int AIndex, int BIndex, string Text);

    /// <summary>
    /// Formats the unified diff transforming <paramref name="baseline"/> into <paramref name="candidate"/>.
    /// Returns the empty string when the two documents are identical.
    /// </summary>
    /// <param name="baseline">The baseline canonical-YAML document.</param>
    /// <param name="candidate">The candidate canonical-YAML document.</param>
    /// <param name="contextLines">The number of unchanged context lines to keep around each change (default 3).</param>
    public static string Format(string baseline, string candidate, int contextLines = DefaultContextLines)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        if (contextLines < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contextLines), contextLines, "Context lines cannot be negative.");
        }

        if (string.Equals(baseline, candidate, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var a = SplitLines(baseline);
        var b = SplitLines(candidate);
        var ops = Diff(a, b);

        // Collect the positions of changed (non-Equal) ops; nothing changed => empty diff.
        var changes = new List<int>();
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i].Op != Op.Equal)
            {
                changes.Add(i);
            }
        }

        if (changes.Count == 0)
        {
            return string.Empty;
        }

        // Group changes whose surrounding context windows touch/overlap into a single hunk.
        var groups = new List<(int Start, int End)>();
        var groupStart = changes[0];
        var groupEnd = changes[0];
        for (var k = 1; k < changes.Count; k++)
        {
            if (changes[k] - groupEnd - 1 <= contextLines * 2)
            {
                groupEnd = changes[k];
            }
            else
            {
                groups.Add((groupStart, groupEnd));
                groupStart = changes[k];
                groupEnd = changes[k];
            }
        }

        groups.Add((groupStart, groupEnd));

        var output = new StringBuilder();
        foreach (var (start, end) in groups)
        {
            EmitHunk(output, ops, start, end, contextLines);
        }

        return output.ToString();
    }

    private static void EmitHunk(StringBuilder output, IReadOnlyList<DiffLine> ops, int changeStart, int changeEnd, int context)
    {
        var from = Math.Max(0, changeStart - context);
        var to = Math.Min(ops.Count - 1, changeEnd + context);

        var aStart = 0;
        var bStart = 0;
        var aCount = 0;
        var bCount = 0;
        var aStartSet = false;
        var bStartSet = false;
        var body = new StringBuilder();

        for (var i = from; i <= to; i++)
        {
            var line = ops[i];
            switch (line.Op)
            {
                case Op.Equal:
                    if (!aStartSet)
                    {
                        aStart = line.AIndex + 1;
                        aStartSet = true;
                    }

                    if (!bStartSet)
                    {
                        bStart = line.BIndex + 1;
                        bStartSet = true;
                    }

                    aCount++;
                    bCount++;
                    body.Append(' ').Append(line.Text).Append('\n');
                    break;

                case Op.Delete:
                    if (!aStartSet)
                    {
                        aStart = line.AIndex + 1;
                        aStartSet = true;
                    }

                    aCount++;
                    body.Append('-').Append(line.Text).Append('\n');
                    break;

                case Op.Insert:
                    if (!bStartSet)
                    {
                        bStart = line.BIndex + 1;
                        bStartSet = true;
                    }

                    bCount++;
                    body.Append('+').Append(line.Text).Append('\n');
                    break;
            }
        }

        // git uses a 0 start line for an empty side (e.g. adding to an empty file: "@@ -0,0 +1,3 @@").
        var aHeaderStart = aCount == 0 ? 0 : aStart;
        var bHeaderStart = bCount == 0 ? 0 : bStart;

        output
            .Append("@@ -")
            .Append(aHeaderStart.ToString(CultureInfo.InvariantCulture))
            .Append(',')
            .Append(aCount.ToString(CultureInfo.InvariantCulture))
            .Append(" +")
            .Append(bHeaderStart.ToString(CultureInfo.InvariantCulture))
            .Append(',')
            .Append(bCount.ToString(CultureInfo.InvariantCulture))
            .Append(" @@\n")
            .Append(body);
    }

    /// <summary>
    /// Computes a deterministic edit script via a standard longest-common-subsequence DP over the two line
    /// arrays. Ties are broken delete-before-insert (<c>&gt;=</c>), so the same inputs always produce the
    /// same script (NFR3).
    /// </summary>
    private static List<DiffLine> Diff(string[] a, string[] b)
    {
        var n = a.Length;
        var m = b.Length;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var result = new List<DiffLine>(n + m);
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(a[x], b[y], StringComparison.Ordinal))
            {
                result.Add(new DiffLine(Op.Equal, x, y, a[x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                result.Add(new DiffLine(Op.Delete, x, y, a[x]));
                x++;
            }
            else
            {
                result.Add(new DiffLine(Op.Insert, x, y, b[y]));
                y++;
            }
        }

        while (x < n)
        {
            result.Add(new DiffLine(Op.Delete, x, y, a[x]));
            x++;
        }

        while (y < m)
        {
            result.Add(new DiffLine(Op.Insert, x, y, b[y]));
            y++;
        }

        return result;
    }

    /// <summary>Splits on LF only (matching the renderer's LF-only output); a trailing LF yields a trailing empty line.</summary>
    private static string[] SplitLines(string text) => text.Split('\n');
}
