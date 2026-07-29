using Caisson.Infrastructure.Persistence.Shaping;
using FluentAssertions;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// DB-free tests of the opaque pagination <see cref="CursorCodec"/> (round-trip + rejection). Finding
/// #21: the cursor now carries a truncated HMAC bound to the rack id and endpoint it was issued for — a
/// forged, tampered, or cross-endpoint/cross-rack cursor is rejected the same clean way a malformed one
/// is (a <c>TryDecode</c> <c>false</c>, never a throw), and the existing round-trip tests still pass
/// against the fixed development key this process falls back to (no ASPNETCORE_ENVIRONMENT=Production).
/// </summary>
public sealed class CursorCodecTests
{
    private static readonly Guid RackId = Guid.NewGuid();
    private const string Endpoint = "topology.snapshots.history";

    [Fact]
    public void Round_trips_a_timestamp_and_id()
    {
        var ts = new DateTime(2026, 7, 28, 4, 5, 6, DateTimeKind.Utc);
        var id = Guid.NewGuid();

        var cursor = CursorCodec.Encode(ts, id, RackId, Endpoint);
        CursorCodec.TryDecode(cursor, RackId, Endpoint, out var decodedTs, out var decodedId).Should().BeTrue();

        decodedTs.Should().Be(ts);
        decodedId.Should().Be(id);
    }

    [Fact]
    public void Cursor_is_url_safe_base64()
    {
        var cursor = CursorCodec.Encode(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Guid.NewGuid(), RackId, Endpoint);
        cursor.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-base64!!")]
    [InlineData("Zm9vYmFy")] // valid base64 ("foobar") but not a "ticks|guid|mac" payload
    public void Rejects_invalid_cursors(string? cursor)
        => CursorCodec.TryDecode(cursor, RackId, Endpoint, out _, out _).Should().BeFalse();

    [Fact]
    public void Rejects_a_payload_with_a_non_numeric_timestamp()
    {
        // "abc|<guid>|<mac>" base64url-encoded — well-formed shape, garbage timestamp.
        var guid = Guid.NewGuid().ToString("N");
        var bytes = System.Text.Encoding.UTF8.GetBytes($"abc|{guid}|0123456789abcdef0123456789abcdef");
        var cursor = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        CursorCodec.TryDecode(cursor, RackId, Endpoint, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_cursor_with_a_tampered_mac()
    {
        var cursor = CursorCodec.Encode(DateTime.UtcNow, Guid.NewGuid(), RackId, Endpoint);
        var tampered = cursor[..^4] + (cursor[^4] == 'A' ? 'B' : 'A') + cursor[(cursor.Length - 3)..];

        CursorCodec.TryDecode(tampered, RackId, Endpoint, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_cursor_replayed_against_a_different_rack()
    {
        var cursor = CursorCodec.Encode(DateTime.UtcNow, Guid.NewGuid(), RackId, Endpoint);

        CursorCodec.TryDecode(cursor, Guid.NewGuid(), Endpoint, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_cursor_replayed_against_a_different_endpoint()
    {
        var cursor = CursorCodec.Encode(DateTime.UtcNow, Guid.NewGuid(), RackId, Endpoint);

        CursorCodec.TryDecode(cursor, RackId, "audit.list", out _, out _).Should().BeFalse();
    }
}
