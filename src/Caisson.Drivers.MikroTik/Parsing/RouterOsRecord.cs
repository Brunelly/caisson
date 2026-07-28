using System.Globalization;

namespace Caisson.Drivers.MikroTik.Parsing;

/// <summary>
/// A tolerant reader over one RouterOS <c>!re</c> attribute map (AC3). Every accessor supports
/// multi-key fallback so v6↔v7 field-name variance (e.g. <c>on-interface</c> vs <c>interface</c>) is
/// absorbed without branching in the mappers, and unknown/missing/renamed fields degrade to
/// <c>null</c> rather than throwing. Booleans accept RouterOS's <c>yes/no</c> as well as
/// <c>true/false</c> and <c>1/0</c>; surrounding whitespace is trimmed everywhere.
/// </summary>
public sealed class RouterOsRecord
{
    private readonly IReadOnlyDictionary<string, string> _attributes;

    /// <summary>Wraps a raw attribute map (the evidence for one reply row).</summary>
    public RouterOsRecord(IReadOnlyDictionary<string, string> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        _attributes = attributes;
    }

    /// <summary>The underlying raw key/value map, for evidence/diagnostics.</summary>
    public IReadOnlyDictionary<string, string> Raw => _attributes;

    /// <summary>Returns the first present, non-blank value among <paramref name="keys"/>, trimmed; else <c>null</c>.</summary>
    public string? GetString(params string[] keys)
    {
        foreach (var key in keys)
        {
            if (_attributes.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Parses the first present value among <paramref name="keys"/> as a boolean, accepting
    /// <c>yes/no</c>, <c>true/false</c> and <c>1/0</c> (case-insensitive). Missing or unrecognized
    /// values return <c>null</c> — never an exception.
    /// </summary>
    public bool? GetBool(params string[] keys)
    {
        var raw = GetString(keys);
        if (raw is null)
        {
            return null;
        }

        return raw.ToLowerInvariant() switch
        {
            "yes" or "true" or "1" => true,
            "no" or "false" or "0" => false,
            _ => null,
        };
    }

    /// <summary>Parses the first present value among <paramref name="keys"/> as an int, or <c>null</c> if absent/unparseable.</summary>
    public int? GetInt(params string[] keys)
    {
        var raw = GetString(keys);
        if (raw is not null
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return null;
    }
}
