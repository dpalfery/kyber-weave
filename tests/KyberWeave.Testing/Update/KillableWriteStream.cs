using KyberWeave.Data.Update;

namespace KyberWeave.Testing.Update;

/// <summary>
/// A write stream whose every chunk is one operation of the fault plan, so a kill can land in the middle of a file and
/// leave the bytes written so far, as a killed process does.
/// </summary>
internal sealed class KillableWriteStream(Stream inner, LatchingFaultFileSystem gate, string? path) : Stream
{
    private const int ChunkSize = 1000;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            gate.Enter("Write", path);
            int length = Math.Min(ChunkSize, buffer.Length);
            inner.Write(buffer[..length]);
            inner.Flush();
            buffer = buffer[length..];
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Permission-bit changes counted as operations of the fault plan.</summary>
internal sealed class KillableFileModes(IUpdateFileModes inner, LatchingFaultFileSystem gate) : IUpdateFileModes
{
    public UnixFileMode? Get(string path) => inner.Get(path);

    public void Set(string path, UnixFileMode mode)
    {
        gate.Enter("SetMode", path);
        inner.Set(path, mode);
    }
}
