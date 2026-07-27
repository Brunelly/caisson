using System.Text;

namespace Caisson.Drivers.MikroTik.Transport;

/// <summary>
/// The MikroTik RouterOS binary API wire protocol at the sentence level. A <em>sentence</em> is a
/// sequence of length-prefixed <em>words</em> terminated by a zero-length word; a word's length is
/// encoded as a 1–5 byte variable-length prefix. This type owns only the framing — it has no notion of
/// commands, replies or the allowlist (those live in <see cref="RouterOsApiClient"/>).
/// </summary>
/// <remarks>
/// Length prefix encoding (see the RouterOS API documentation):
/// <c>&lt; 0x80</c> → 1 byte; <c>&lt; 0x4000</c> → 2 bytes with <c>0x8000</c>; <c>&lt; 0x200000</c> →
/// 3 bytes with <c>0xC00000</c>; <c>&lt; 0x10000000</c> → 4 bytes with <c>0xE0000000</c>; otherwise a
/// <c>0xF0</c> marker followed by 4 big-endian length bytes.
/// </remarks>
internal static class RouterOsSentence
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Writes <paramref name="words"/> as one framed sentence, followed by its zero-length terminator.</summary>
    public static async Task WriteAsync(Stream stream, IReadOnlyList<string> words, CancellationToken cancellationToken)
    {
        var buffer = new List<byte>(64);
        foreach (var word in words)
        {
            var bytes = Utf8.GetBytes(word);
            WriteLength(buffer, bytes.Length);
            buffer.AddRange(bytes);
        }

        // Empty word terminates the sentence.
        WriteLength(buffer, 0);

        await stream.WriteAsync(buffer.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one framed sentence, returning its words (excluding the zero-length terminator). An empty
    /// list means the peer sent a bare terminator.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var words = new List<string>();
        while (true)
        {
            var length = await ReadLengthAsync(stream, cancellationToken).ConfigureAwait(false);
            if (length == 0)
            {
                return words;
            }

            var bytes = await ReadExactAsync(stream, length, cancellationToken).ConfigureAwait(false);
            words.Add(Utf8.GetString(bytes));
        }
    }

    private static void WriteLength(List<byte> buffer, int length)
    {
        if (length < 0x80)
        {
            buffer.Add((byte)length);
        }
        else if (length < 0x4000)
        {
            buffer.Add((byte)((length >> 8) | 0x80));
            buffer.Add((byte)length);
        }
        else if (length < 0x200000)
        {
            buffer.Add((byte)((length >> 16) | 0xC0));
            buffer.Add((byte)(length >> 8));
            buffer.Add((byte)length);
        }
        else if (length < 0x10000000)
        {
            buffer.Add((byte)((length >> 24) | 0xE0));
            buffer.Add((byte)(length >> 16));
            buffer.Add((byte)(length >> 8));
            buffer.Add((byte)length);
        }
        else
        {
            buffer.Add(0xF0);
            buffer.Add((byte)(length >> 24));
            buffer.Add((byte)(length >> 16));
            buffer.Add((byte)(length >> 8));
            buffer.Add((byte)length);
        }
    }

    private static async Task<int> ReadLengthAsync(Stream stream, CancellationToken cancellationToken)
    {
        int first = (await ReadExactAsync(stream, 1, cancellationToken).ConfigureAwait(false))[0];

        if ((first & 0x80) == 0x00)
        {
            return first;
        }

        if ((first & 0xC0) == 0x80)
        {
            var rest = await ReadExactAsync(stream, 1, cancellationToken).ConfigureAwait(false);
            return ((first & 0x3F) << 8) | rest[0];
        }

        if ((first & 0xE0) == 0xC0)
        {
            var rest = await ReadExactAsync(stream, 2, cancellationToken).ConfigureAwait(false);
            return ((first & 0x1F) << 16) | (rest[0] << 8) | rest[1];
        }

        if ((first & 0xF0) == 0xE0)
        {
            var rest = await ReadExactAsync(stream, 3, cancellationToken).ConfigureAwait(false);
            return ((first & 0x0F) << 24) | (rest[0] << 16) | (rest[1] << 8) | rest[2];
        }

        if ((first & 0xF8) == 0xF0)
        {
            var rest = await ReadExactAsync(stream, 4, cancellationToken).ConfigureAwait(false);
            return (rest[0] << 24) | (rest[1] << 16) | (rest[2] << 8) | rest[3];
        }

        throw new RouterOsApiException("Invalid RouterOS API word-length prefix.");
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException("The RouterOS connection was closed mid-sentence.");
            }

            read += n;
        }

        return buffer;
    }
}
