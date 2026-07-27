namespace Caisson.Domain.ValueObjects;

/// <summary>
/// A MAC address stored in a single canonical form: lowercase hexadecimal, 12 characters, no
/// separators (e.g. <c>001b44113ab7</c>). Parsing accepts the common wire/display formats —
/// colon-, hyphen-, and dot-grouped, or bare — in any letter case, and normalizes them all to the
/// canonical form so equality, indexing and joins are reliable regardless of source. Presentation
/// formatting is a UI/API concern exposed via <see cref="ToDisplay"/>.
/// </summary>
public readonly record struct MacAddressValue
{
    /// <summary>Number of hexadecimal characters in a normalized MAC address.</summary>
    private const int HexLength = 12;

    private MacAddressValue(string value) => Value = value;

    /// <summary>The normalized value: lowercase, 12 hex characters, no separators.</summary>
    public string Value { get; }

    /// <summary>
    /// Parses <paramref name="input"/> in any accepted format, returning the normalized value.
    /// </summary>
    /// <exception cref="FormatException">Thrown when the input is not a valid MAC address.</exception>
    public static MacAddressValue Parse(string input)
    {
        if (!TryParse(input, out var mac))
        {
            throw new FormatException($"'{input}' is not a valid MAC address.");
        }

        return mac;
    }

    /// <summary>
    /// Attempts to parse <paramref name="input"/> in any accepted format. Separators (<c>:</c>,
    /// <c>-</c>, <c>.</c>) and surrounding whitespace are ignored; the remaining characters must be
    /// exactly 12 hexadecimal digits.
    /// </summary>
    public static bool TryParse(string? input, out MacAddressValue result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        Span<char> buffer = stackalloc char[HexLength];
        var count = 0;
        foreach (var c in input)
        {
            if (c is ':' or '-' or '.' || char.IsWhiteSpace(c))
            {
                continue;
            }

            if (count >= HexLength)
            {
                return false;
            }

            var lower = char.ToLowerInvariant(c);
            if (!IsHexDigit(lower))
            {
                return false;
            }

            buffer[count++] = lower;
        }

        if (count != HexLength)
        {
            return false;
        }

        result = new MacAddressValue(new string(buffer));
        return true;
    }

    /// <summary>Returns the colon-grouped display form, e.g. <c>00:1b:44:11:3a:b7</c>.</summary>
    public string ToDisplay()
    {
        return string.Create(17, Value, static (span, value) =>
        {
            var pos = 0;
            for (var i = 0; i < HexLength; i += 2)
            {
                if (i > 0)
                {
                    span[pos++] = ':';
                }

                span[pos++] = value[i];
                span[pos++] = value[i + 1];
            }
        });
    }

    /// <summary>Returns the canonical normalized value.</summary>
    public override string ToString() => Value ?? string.Empty;

    private static bool IsHexDigit(char c)
        => c is >= '0' and <= '9' or >= 'a' and <= 'f';
}
