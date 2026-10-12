namespace Encspect.Core;

// Bytes [Offset, Offset + Length) of the file.
public readonly record struct ByteRange(long Offset, long Length)
{
    public long End => Offset + Length;
}
