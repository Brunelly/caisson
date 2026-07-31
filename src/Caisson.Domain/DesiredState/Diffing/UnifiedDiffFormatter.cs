using System.Globalization;
using System.Text;

namespace Caisson.Domain.DesiredState.Diffing;

/// <summary>
/// A pure, hand-rolled LCS-based unified-diff generator over two canonical-YAML strings (story #171, AC1;
/// answered design Q — diff the canonicalized YAML for reduced noise). Deliberately in-domain and
/// dependency-free (ADR 0053), AOT-clean, shareable, and fully deterministic — identical inputs always
/// yield the identical bytes, and identical inputs yield an EMPTY diff. Emits standard
/// <c>@@ -a,b +c,d @@</c> hunks with <c>'+'</c>/<c>'-'</c>/space line prefixes and a configurable number of
/// surrounding context lines. LF-only line splitting matches the renderer's LF-only output.
/// <para>
/// Canonical rack YAML is normally small (hundreds of lines), but <see cref="DesiredStateSchema"/> permits
/// up to 2048 ports / 4094 VLANs per rack, whose near-cap canonical YAML runs to tens of thousands of lines.
/// A dense O(n·m) LCS matrix over two such documents is a single ~1 GiB allocation and an authenticated
/// memory-exhaustion vector, so the edit script is computed with the fast dense matrix only while it stays
/// within <see cref="MaxFullMatrixCells"/> and switches to a memory-linear (Hirschberg) reconstruction — the
/// same O(n·m) time but an O(min(n,m)) working set — for larger inputs (story #171 security review, ADR 0053).
/// </para>
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
        => FormatCore(baseline, candidate, contextLines, Diff);

    /// <summary>
    /// Test seam that forces the memory-linear (Hirschberg) edit script regardless of input size, so the
    /// large-input path can be exercised without allocating multi-thousand-line documents.
    /// </summary>
    internal static string FormatWithLinearSpaceDiff(
        string baseline, string candidate, int contextLines = DefaultContextLines)
        => FormatCore(baseline, candidate, contextLines, DiffLinearSpace);

    private static string FormatCore(
        string baseline, string candidate, int contextLines, Func<string[], string[], List<DiffLine>> diff)
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
        var ops = diff(a, b);

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
    /// The dense LCS matrix ceiling, in cells: while <c>(n+1)·(m+1)</c> stays at or under this bound the fast
    /// full-matrix DP is used; above it (near-cap racks) the memory-linear reconstruction takes over so the
    /// working set never approaches the ~1 GiB square-matrix worst case. 4,000,000 cells ≈ a 16 MiB
    /// <c>int</c> matrix — well above realistic rack YAML yet a hard ceiling on per-request allocation.
    /// </summary>
    internal const long MaxFullMatrixCells = 4_000_000L;

    /// <summary>
    /// Computes a deterministic edit script over the two line arrays. Uses the fast dense-matrix LCS DP for
    /// the common (small) case and a memory-linear Hirschberg reconstruction once the dense matrix would
    /// exceed <see cref="MaxFullMatrixCells"/>. Both paths are pure and deterministic — identical inputs
    /// always produce an identical script (NFR3).
    /// </summary>
    private static List<DiffLine> Diff(string[] a, string[] b)
        => (long)(a.Length + 1) * (b.Length + 1) <= MaxFullMatrixCells
            ? DiffFullMatrix(a, b)
            : DiffLinearSpace(a, b);

    /// <summary>
    /// The dense O(n·m)-space LCS DP. Ties are broken delete-before-insert (<c>&gt;=</c>), so the same inputs
    /// always produce the same script (NFR3). Used only within the <see cref="MaxFullMatrixCells"/> budget.
    /// </summary>
    private static List<DiffLine> DiffFullMatrix(string[] a, string[] b)
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

    /// <summary>
    /// A memory-linear Hirschberg LCS reconstruction: O(n·m) time but only an O(min(n,m)) working set, so a
    /// near-cap baseline/candidate pair can never force the ~1 GiB dense-matrix allocation. Produces a valid,
    /// optimal, deterministic edit script (the same LCS length as the dense DP; the specific alignment may
    /// differ but is stable for identical inputs).
    /// </summary>
    private static List<DiffLine> DiffLinearSpace(string[] a, string[] b)
    {
        var result = new List<DiffLine>(a.Length + b.Length);
        Reconstruct(a, b, 0, a.Length, 0, b.Length, result);
        return result;
    }

    /// <summary>Recursively aligns <c>a[a0..a1)</c> against <c>b[b0..b1)</c>, appending ops in reading order.</summary>
    private static void Reconstruct(
        string[] a, string[] b, int a0, int a1, int b0, int b1, List<DiffLine> result)
    {
        var n = a1 - a0;
        var m = b1 - b0;

        if (n == 0)
        {
            for (var j = b0; j < b1; j++)
            {
                result.Add(new DiffLine(Op.Insert, a0, j, b[j]));
            }

            return;
        }

        if (m == 0)
        {
            for (var i = a0; i < a1; i++)
            {
                result.Add(new DiffLine(Op.Delete, i, b0, a[i]));
            }

            return;
        }

        if (n == 1)
        {
            ReconstructSingleBaselineLine(a, b, a0, b0, b1, result);
            return;
        }

        var aMid = a0 + (n / 2);
        var forward = ForwardLcsRow(a, b, a0, aMid, b0, b1);
        var backward = BackwardLcsRow(a, b, aMid, a1, b0, b1);

        // Split the candidate range at the column maximising forward[k] + backward[k]; the smallest such k
        // wins so the partition (and thus the whole script) is deterministic.
        var bestK = 0;
        var bestScore = -1;
        for (var k = 0; k <= m; k++)
        {
            var score = forward[k] + backward[k];
            if (score > bestScore)
            {
                bestScore = score;
                bestK = k;
            }
        }

        Reconstruct(a, b, a0, aMid, b0, b0 + bestK, result);
        Reconstruct(a, b, aMid, a1, b0 + bestK, b1, result);
    }

    /// <summary>Aligns a single baseline line against a candidate range: at most one <see cref="Op.Equal"/>.</summary>
    private static void ReconstructSingleBaselineLine(
        string[] a, string[] b, int a0, int b0, int b1, List<DiffLine> result)
    {
        var match = -1;
        for (var j = b0; j < b1; j++)
        {
            if (string.Equals(a[a0], b[j], StringComparison.Ordinal))
            {
                match = j;
                break;
            }
        }

        if (match < 0)
        {
            // No shared line: delete the baseline line first (mirroring the dense DP's delete-before-insert
            // tie-break), then insert every candidate line.
            result.Add(new DiffLine(Op.Delete, a0, b0, a[a0]));
            for (var j = b0; j < b1; j++)
            {
                result.Add(new DiffLine(Op.Insert, a0, j, b[j]));
            }

            return;
        }

        for (var j = b0; j < match; j++)
        {
            result.Add(new DiffLine(Op.Insert, a0, j, b[j]));
        }

        result.Add(new DiffLine(Op.Equal, a0, match, a[a0]));
        for (var j = match + 1; j < b1; j++)
        {
            result.Add(new DiffLine(Op.Insert, a0, j, b[j]));
        }
    }

    /// <summary>
    /// Forward LCS lengths of <c>a[a0..a1)</c> against every prefix of <c>b[b0..b1)</c>; index <c>k</c> is the
    /// LCS length against the first <c>k</c> candidate lines. O(m) working set.
    /// </summary>
    private static int[] ForwardLcsRow(string[] a, string[] b, int a0, int a1, int b0, int b1)
    {
        var m = b1 - b0;
        var prev = new int[m + 1];
        var curr = new int[m + 1];
        for (var i = a0; i < a1; i++)
        {
            for (var k = 1; k <= m; k++)
            {
                curr[k] = string.Equals(a[i], b[b0 + k - 1], StringComparison.Ordinal)
                    ? prev[k - 1] + 1
                    : Math.Max(prev[k], curr[k - 1]);
            }

            (prev, curr) = (curr, prev);
        }

        return prev;
    }

    /// <summary>
    /// Backward LCS lengths of <c>a[a0..a1)</c> against every suffix of <c>b[b0..b1)</c>; index <c>k</c> is the
    /// LCS length against the candidate lines from offset <c>k</c> onward. O(m) working set.
    /// </summary>
    private static int[] BackwardLcsRow(string[] a, string[] b, int a0, int a1, int b0, int b1)
    {
        var m = b1 - b0;
        var next = new int[m + 1];
        var curr = new int[m + 1];
        for (var i = a1 - 1; i >= a0; i--)
        {
            for (var k = m - 1; k >= 0; k--)
            {
                curr[k] = string.Equals(a[i], b[b0 + k], StringComparison.Ordinal)
                    ? next[k + 1] + 1
                    : Math.Max(next[k], curr[k + 1]);
            }

            (next, curr) = (curr, next);
        }

        return next;
    }

    /// <summary>Splits on LF only (matching the renderer's LF-only output); a trailing LF yields a trailing empty line.</summary>
    private static string[] SplitLines(string text) => text.Split('\n');
}
