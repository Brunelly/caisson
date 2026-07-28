using Caisson.Infrastructure.Persistence.Shaping;
using FluentAssertions;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>DB-free tests of the opaque pagination <see cref="CursorCodec"/> (round-trip + rejection).</summary>
public sealed class CursorCodecTests
{
    [Fact]
    public void Round_trips_a_timestamp_and_id()
    {
        var ts = new DateTime(2026, 7, 28, 4, 5, 6, DateTimeKind.Utc);
        var id = Guid.NewGuid();

        var cursor = CursorCodec.Encode(ts, id);
        CursorCodec.TryDecode(cursor, out var decodedTs, out var decodedId).Should().BeTrue();

        decodedTs.Should().Be(ts);
        decodedId.Should().Be(id);
    }

    [Fact]
    public void Cursor_is_url_safe_base64()
    {
        var cursor = CursorCodec.Encode(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Guid.NewGuid());
        cursor.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-base64!!")]
    [InlineData("Zm9vYmFy")] // valid base64 ("foobar") but not a "ticks|guid" payload
    public void Rejects_invalid_cursors(string? cursor)
        => CursorCodec.TryDecode(cursor, out _, out _).Should().BeFalse();

    [Fact]
    public void Rejects_a_payload_with_a_non_numeric_timestamp()
    {
        // "abc|<guid>" base64url-encoded.
        var guid = Guid.NewGuid().ToString("N");
        var bytes = System.Text.Encoding.UTF8.GetBytes("abc|" + guid);
        var cursor = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        CursorCodec.TryDecode(cursor, out _, out _).Should().BeFalse();
    }
}
