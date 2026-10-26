using System.Buffers;
using Encspect.Core;
using Encspect.Formats.Dts;
using Encspect.Formats.Mpeg;

namespace Encspect.Tests;

// The searches of the DTS sniffer and the MPEG frame walk, each held to the one it replaced.
public class ScanSpeedPathTests
{
    private static readonly SearchValues<byte> OldSyncStarts = SearchValues.Create([0x7F, 0xFE, 0x1F, 0xFF, 0x64, 0x58]);

    // Data in which the first bytes of the DTS syncs are common: runs of each, the sync words themselves, and noise.
    private static byte[] Adversarial(Random rng, int length)
    {
        byte[] data = new byte[length];
        int kind = rng.Next(5);
        for (int i = 0; i < length; i++)
        {
            data[i] = kind switch
            {
                0 => (byte)rng.Next(256),
                1 => new byte[] { 0xFF, 0x7F, 0xFE, 0x1F, 0x64, 0x58 }[rng.Next(6)],
                2 => rng.Next(40) == 0 ? new byte[] { 0xFF, 0x7F, 0xFE, 0x1F, 0x64, 0x58 }[rng.Next(6)] : (byte)0,
                3 => 0xFF,
                _ => (byte)(rng.Next(8) == 0 ? rng.Next(256) : 0x80 + rng.Next(8)),
            };
        }

        return data;
    }

    [Fact]
    public void TheTableSearchFindsTheFirstSyncStartAsSearchValuesDid()
    {
        var rng = new Random(11);
        for (int iteration = 0; iteration < 3000; iteration++)
        {
            byte[] data = Adversarial(rng, rng.Next(0, 300));
            int index = rng.Next(0, data.Length + 1);
            int count = rng.Next(0, data.Length - index + 1);
            Assert.Equal(data.AsSpan(index, count).IndexOfAny(OldSyncStarts), BlockCache.FirstInTable(data, index, count, DtsParser.SyncStarts));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    public void TheBlockCacheFindsTheSameOffsetsAcrossBlockEdgesAndInASlice(int prefix)
    {
        var rng = new Random(12 + prefix);
        foreach (int size in new[] { 1, 100, 65535, 65536, 65537, 131072, 200000 })
        {
            byte[] data = new byte[prefix + size + 9];
            // Sparse candidates, so that searches cross the edges of the 64 KiB blocks, with some at the last byte of a block.
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = 0x80;
            }

            foreach (int at in new[] { 65535, 65536, 131071, 131072, 5, size - 1 }.Where(a => a >= 0 && a < size))
            {
                data[prefix + at] = 0x1F;
            }

            for (int i = 0; i < 5; i++)
            {
                data[prefix + rng.Next(size)] = 0x64;
            }

            ByteReader reader = new ByteReader(new MemoryStream(data)).Slice(prefix, size);
            var cache = new BlockCache(reader);
            for (int i = 0; i < 300; i++)
            {
                long from = prefix + rng.Next(-3, size + 3);
                long limit = prefix + rng.Next(0, size + 10);
                long expected = -1;
                for (long p = Math.Max(from, prefix); p < Math.Min(limit, prefix + size); p++)
                {
                    if (DtsParser.SyncStarts[data[p]])
                    {
                        expected = p;
                        break;
                    }
                }

                Assert.Equal(expected, cache.IndexOfAny(DtsParser.SyncStarts, from, limit));
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(300)]
    public void TheMpegBlockReaderSearchesAndReadsWordsAsByteByByteDid(int prefix)
    {
        var rng = new Random(13 + prefix);
        foreach (int size in new[] { 4, 5, 100, 65535, 65536, 65537, 140000 })
        {
            byte[] data = new byte[prefix + size + 7];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = rng.Next(20) == 0 ? (byte)0xFF : (byte)rng.Next(256);
            }

            ByteReader reader = new ByteReader(new MemoryStream(data)).Slice(prefix, size);
            var blocks = new BlockReader(reader);
            for (int i = 0; i < 400; i++)
            {
                long from = prefix + rng.Next(-3, size + 3);
                long limit = prefix + rng.Next(0, size + 10);
                long expected = -1;
                for (long p = Math.Max(from, prefix); p < Math.Min(limit, prefix + size); p++)
                {
                    if (data[p] == 0xFF)
                    {
                        expected = p;
                        break;
                    }
                }

                Assert.Equal(expected, blocks.IndexOf(0xFF, from, limit));

                long at = prefix + rng.Next(-2, size + 2);
                bool fits = at >= prefix && at + 4 <= prefix + size;
                Assert.Equal(fits, blocks.TryReadUInt32(at, out uint word));
                if (fits)
                {
                    Assert.Equal(((uint)data[at] << 24) | ((uint)data[at + 1] << 16) | ((uint)data[at + 2] << 8) | data[at + 3], word);
                }
            }
        }
    }
}
