namespace Caisson.Drivers.MikroTik.Tests;

/// <summary>
/// A duplex-looking stream that serves a fixed reply buffer to reads and discards writes, so the real
/// <c>RouterOsApiClient</c> command path can be driven without a socket.
/// </summary>
internal sealed class OneWayReplyStream : Stream
{
    private readonly byte[] _reply;
    private int _readPosition;

    public OneWayReplyStream(byte[] reply) => _reply = reply;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _reply.Length;

    public override long Position { get; set; }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count)
    {
        var available = Math.Min(count, _reply.Length - _readPosition);
        Array.Copy(_reply, _readPosition, buffer, offset, available);
        _readPosition += available;
        return available;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var available = Math.Min(buffer.Length, _reply.Length - _readPosition);
        _reply.AsSpan(_readPosition, available).CopyTo(buffer.Span);
        _readPosition += available;
        return ValueTask.FromResult(available);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
