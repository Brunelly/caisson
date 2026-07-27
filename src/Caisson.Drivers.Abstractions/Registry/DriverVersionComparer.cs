using System.Globalization;

namespace Caisson.Drivers.Abstractions.Registry;

/// <summary>
/// Orders driver-version strings so the registry can pick the highest version when several drivers
/// share a (Vendor, Model, ConnectionKind) key (see ADR 0007). Kept deliberately dependency-free —
/// no NuGet.Versioning/SemVer package — so <c>Caisson.Drivers.Abstractions</c> stays AOT-compatible
/// and dependency-light (ADR 0006).
/// </summary>
/// <remarks>
/// Comparison rules:
/// <list type="bullet">
/// <item>The leading dotted-numeric core is compared segment-wise <b>numerically</b>, so
/// <c>"1.10.0"</c> sorts above <c>"1.9.0"</c>. Missing trailing segments are treated as <c>0</c>
/// (<c>"1.0"</c> equals <c>"1.0.0"</c>).</item>
/// <item>A <c>-prerelease</c> suffix ranks <b>below</b> the same release core, so <c>"2.0.0"</c> sorts
/// above <c>"2.0.0-rc1"</c>; two prereleases of the same core are compared ordinally.</item>
/// <item>If either value's core is not parseable as dotted numerics, both fall back to
/// <see cref="StringComparer.Ordinal"/> so resolution stays deterministic.</item>
/// </list>
/// </remarks>
internal sealed class DriverVersionComparer : IComparer<string>
{
    /// <summary>The shared, stateless instance.</summary>
    public static readonly DriverVersionComparer Instance = new();

    private DriverVersionComparer()
    {
    }

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        if (!TryParse(x, out var xCore, out var xPrerelease)
            || !TryParse(y, out var yCore, out var yPrerelease))
        {
            return StringComparer.Ordinal.Compare(x, y);
        }

        var coreComparison = CompareCores(xCore, yCore);
        if (coreComparison != 0)
        {
            return coreComparison;
        }

        return ComparePrereleases(xPrerelease, yPrerelease);
    }

    private static int CompareCores(IReadOnlyList<int> x, IReadOnlyList<int> y)
    {
        var length = Math.Max(x.Count, y.Count);
        for (var i = 0; i < length; i++)
        {
            // Missing trailing segments count as 0, so "1.0" == "1.0.0".
            var left = i < x.Count ? x[i] : 0;
            var right = i < y.Count ? y[i] : 0;
            var comparison = left.CompareTo(right);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static int ComparePrereleases(string? x, string? y)
    {
        // A release (no prerelease) outranks a prerelease of the same core.
        if (x is null && y is null)
        {
            return 0;
        }

        if (x is null)
        {
            return 1;
        }

        if (y is null)
        {
            return -1;
        }

        return StringComparer.Ordinal.Compare(x, y);
    }

    private static bool TryParse(string version, out int[] core, out string? prerelease)
    {
        core = Array.Empty<int>();
        prerelease = null;

        var corePart = version;
        var dashIndex = version.IndexOf('-', StringComparison.Ordinal);
        if (dashIndex >= 0)
        {
            corePart = version[..dashIndex];
            prerelease = version[(dashIndex + 1)..];
        }

        if (corePart.Length == 0)
        {
            return false;
        }

        var segments = corePart.Split('.');
        var parsed = new int[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out parsed[i]))
            {
                return false;
            }
        }

        core = parsed;
        return true;
    }
}
