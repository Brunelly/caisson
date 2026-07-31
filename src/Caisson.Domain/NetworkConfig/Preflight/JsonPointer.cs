using System.Globalization;

namespace Caisson.Domain.NetworkConfig.Preflight;

/// <summary>
/// Builds canonical RFC 6901 JSON Pointer field paths for <see cref="PreflightIssue.FieldPath"/> (story
/// #170, Q1 answer: "adopt JSON Pointer as canonical"). Each reference token is escaped per §3 —
/// <c>~</c> ⇒ <c>~0</c> and <c>/</c> ⇒ <c>~1</c> — so a token containing those characters (e.g. a
/// stacked-switch port name like <c>1/1/1</c>) round-trips unambiguously.
/// </summary>
public static class JsonPointer
{
    /// <summary>Escapes a single reference token per RFC 6901 §3 (order matters: <c>~</c> before <c>/</c>).</summary>
    public static string Escape(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }

    /// <summary>Builds a JSON Pointer from ordered reference tokens, escaping each and prefixing <c>/</c>.</summary>
    public static string Build(params string[] tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (tokens.Length == 0)
        {
            return string.Empty;
        }

        return "/" + string.Join('/', tokens.Select(Escape));
    }

    /// <summary>Builds a JSON Pointer whose second token is an array index.</summary>
    public static string Build(string collection, int index, string field)
        => Build(collection, index.ToString(CultureInfo.InvariantCulture), field);
}
