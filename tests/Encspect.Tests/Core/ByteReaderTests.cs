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
    public void PositionBeforeTheStartOfASliceCountsAsTruncation()
    {
        ByteReader slice = CreateReader(0, 1, 2, 3).Slice(2, 2);

        Assert.Throws<EndOfStreamException>(() => slice.Position = 1);
    }

    // A container's size field read from a damaged file must read as truncation.
    [Theory]
    [InlineData(-1, 2)]
    [InlineData(2, -1)]
    [InlineData(2, 3)]
    [InlineData(5, 0)]
    [InlineData(1, long.MaxValue)]
    [InlineData(1, 0xFFFFFF00)]
    public void SliceOutsideTheReaderCountsAsTruncation(long start, long length)
    {
        ByteReader reader = CreateReader(0, 1, 2, 3);

        Assert.Throws<EndOfStreamException>(() => reader.Slice(start, length));
    }

    [Fact]
    public void SliceOfASliceStaysWithinIt()
    {
        ByteReader slice = CreateReader(0, 1, 2, 3, 4, 5).Slice(1, 4);

        Assert.Equal([3, 4], slice.Slice(3, 2).ReadBytes(2));
        Assert.Throws<EndOfStreamException>(() => slice.Slice(0, 2));
        Assert.Throws<EndOfStreamException>(() => slice.Slice(4, 2));
    }

    [Fact]
    public void ReadFillsASpanAndStopsAtTheEnd()
    {
        ByteReader reader = CreateReader(1, 2, 3, 4, 5);
        Span<byte> buffer = stackalloc byte[3];

        reader.Read(buffer);

        Assert.Equal([1, 2, 3], buffer.ToArray());
        Assert.Equal(3L, reader.Position);
        try
        {
            reader.Read(buffer);
            Assert.Fail("Reading past the end did not throw.");
        }
        catch (EndOfStreamException)
        {
        }

        Assert.Equal(3L, reader.Position);
        reader.Read([]);
    }

    [Fact]
    public void ReadsWithoutAllocating()
    {
        ByteReader reader = CreateReader(new byte[128]);
        Span<byte> buffer = stackalloc byte[4];
        reader.Read(buffer);
        reader.Position = 0;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4; i++)
        {
            reader.Read(buffer);
            reader.ReadUInt16LittleEndian();
            reader.ReadUInt24BigEndian();
            reader.ReadUInt64BigEndian();
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void ReadsIntegersOfEveryWidthAndOrder()
    {
        ByteReader reader = CreateReader(
            0x34, 0x12,
            0x12, 0x34, 0x56,
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);

        Assert.Equal((ushort)0x1234, reader.ReadUInt16LittleEndian());
        Assert.Equal(0x123456u, reader.ReadUInt24BigEndian());
        Assert.Equal(0x0102030405060708ul, reader.ReadUInt64BigEndian());
        Assert.Equal(0x0102030405060708ul, reader.ReadUInt64LittleEndian());
        Assert.Equal(ulong.MaxValue, reader.ReadUInt64LittleEndian());
        Assert.Equal(0L, reader.Remaining);
    }

    [Fact]
    public void IntegersPastTheEndThrowAndLeaveThePosition()
    {
        ByteReader reader = CreateReader(1, 2, 3, 4, 5, 6, 7);
        reader.Position = 6;

        Assert.Throws<EndOfStreamException>(() => reader.ReadUInt16LittleEndian());
        reader.Position = 5;
        Assert.Throws<EndOfStreamException>(() => reader.ReadUInt24BigEndian());
        reader.Position = 0;
        Assert.Throws<EndOfStreamException>(() => reader.ReadUInt64BigEndian());
        Assert.Throws<EndOfStreamException>(() => reader.ReadUInt64LittleEndian());
        Assert.Equal(0L, reader.Position);
    }

    [Fact]
    public void EveryReadStopsOnceCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new ByteReader(new MemoryStream(new byte[16]), cancellation.Token);
        ByteReader slice = reader.Slice(4, 8);
        reader.ReadByte();

        cancellation.Cancel();

        Assert.Equal(cancellation.Token, slice.CancellationToken);
        Assert.Throws<OperationCanceledException>(() => reader.ReadByte());
        Assert.Throws<OperationCanceledException>(() => slice.ReadBytes(1));
        Assert.Throws<OperationCanceledException>(() => slice.ReadUInt32BigEndian());
        Assert.Throws<OperationCanceledException>(() => slice.Skip(1));
        Assert.Throws<OperationCanceledException>(() => slice.MatchesAt(4, "x"u8));
    }

    // Progress is the furthest offset read, in steps, also through slices and after jumping back.
    [Fact]
    public void ProgressReportsTheFurthestOffsetRead()
    {
        var reports = new List<long>();
        var reader = new ByteReader(new MemoryStream(new byte[2 << 20]), default, new SyncProgress(reports.Add));

        reader.Slice(0, 1 << 20).ReadBytes(1 << 20);
        reader.Position = 0;
        reader.ReadBytes(1 << 19);
        reader.Position = (2 << 20) - 1;
        reader.ReadByte();

        Assert.Equal([1 << 20, 2 << 20], reports);
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
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
