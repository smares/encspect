namespace Encspect.Cli;

// Wraps stdout so that a failed write can be told apart from a file that cannot be read, which
// throws IOException too.
internal sealed class GuardedStream : Stream
{
    private readonly Stream _inner;

    public GuardedStream(Stream inner)
    {
        _inner = inner;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        try
        {
            _inner.Write(buffer);
        }
        catch (IOException ex)
        {
            throw new WriteFailedException(ex);
        }
    }

    public override void WriteByte(byte value) => Write([value]);

    public override void Flush()
    {
        try
        {
            _inner.Flush();
        }
        catch (IOException ex)
        {
            throw new WriteFailedException(ex);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public sealed class WriteFailedException(IOException inner) : Exception(inner.Message, inner);
}
