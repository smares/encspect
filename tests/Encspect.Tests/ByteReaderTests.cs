using System.IO.Compression;
using Encspect.Core;

namespace Encspect.Tests;

public class ByteReaderTests
{
    private static ByteReader CreateReader(params byte[] bytes) => new(new MemoryStream(bytes));

    [Fact]
    public void ReadsBigEndianIntegers()
    {
        ByteReader reader = CreateReader(0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC);

        Assert.Equal((ushort)0x1234, reader.ReadUInt16BigEndian());
        Assert.Equal(0x56789ABCu, reader.ReadUInt32BigEndian());
    }

    [Fact]
    public void ReadsLittleEndianInteger()
    {
        ByteReader reader = CreateReader(0x78, 0x56, 0x34, 0x12);

        Assert.Equal(0x12345678u, reader.ReadUInt32LittleEndian());
    }

    [Fact]
    public void TracksPositionAndRemaining()
    {
        ByteReader reader = CreateReader(1, 2, 3, 4);

        Assert.Equal(4L, reader.Length);
        Assert.Equal(4L, reader.Remaining);

        reader.ReadByte();

        Assert.Equal(1L, reader.Position);
        Assert.Equal(3L, reader.Remaining);
    }

    [Fact]
    public void ReadBytesPastTheEndThrows()
    {
        ByteReader reader = CreateReader(1, 2, 3);

        Assert.Throws<EndOfStreamException>(() => reader.ReadBytes(4));
    }

    [Fact]
    public void ReadBytesWithHugeCountThrowsInsteadOfAllocating()
    {
        ByteReader reader = CreateReader(1, 2, 3);

        Assert.Throws<EndOfStreamException>(() => reader.ReadBytes(int.MaxValue));
    }

    [Fact]
    public void ReadByteAtTheEndThrows()
    {
        ByteReader reader = CreateReader();

        Assert.Throws<EndOfStreamException>(() => reader.ReadByte());
    }

    [Fact]
    public void RejectsNonSeekableStreams()
    {
        using var stream = new GZipStream(new MemoryStream(), CompressionMode.Decompress);

        Assert.Throws<ArgumentException>(() => new ByteReader(stream));
    }

    [Fact]
    public void SliceKeepsAbsolutePositionsAndStopsAtItsEnd()
    {
        ByteReader reader = CreateReader(0, 1, 2, 3, 4, 5, 6, 7);

        ByteReader slice = reader.Slice(2, 4);

        Assert.Equal((2L, 6L, 4L), (slice.Start, slice.End, slice.Length));
        Assert.Equal(2L, slice.Position);
        Assert.Equal([2, 3, 4, 5], slice.ReadBytes(4));
        Assert.Throws<EndOfStreamException>(() => slice.ReadByte());
    }

    [Fact]
    public void SliceRejectsPositionsBeforeItsStart()
    {
        ByteReader slice = CreateReader(0, 1, 2, 3).Slice(2, 2);

        Assert.Throws<ArgumentOutOfRangeException>(() => slice.Position = 1);
    }

    [Fact]
    public void SettingPositionPastTheEndCountsAsTruncation()
    {
        ByteReader reader = CreateReader(1, 2, 3);

        Assert.Throws<EndOfStreamException>(() => reader.Position = 4);
    }

    [Fact]
    public void MatchesAtComparesBytesAndTreatsOutOfRangeAsNoMatch()
    {
        ByteReader reader = CreateReader((byte)'x', (byte)'T', (byte)'A', (byte)'G');

        Assert.True(reader.MatchesAt(1, "TAG"u8));
        Assert.False(reader.MatchesAt(0, "TAG"u8));
        Assert.False(reader.MatchesAt(2, "TAG"u8));
        Assert.False(reader.MatchesAt(-1, "TAG"u8));
    }
}
