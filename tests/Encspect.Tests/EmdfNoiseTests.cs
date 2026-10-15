using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Random bytes after the audio blocks (the unread bits of AHT frames, the unused bits and auxiliary data
// of plain ones) with the EMDF sync word planted at random bit positions are no container: the structural
// test accepts nothing here, so no object audio and no finding about a container is reported. Frames are
// the fixtures' own (ac3/aht.eac3, ac3/frame-exponents.eac3) with their tails replaced and the CRC written
// again.
public class EmdfNoiseTests
{
    private const int KeepBytes = 64;

    [Theory]
    [InlineData("aht.eac3", 1, true)]
    [InlineData("aht.eac3", 2, true)]
    [InlineData("aht.eac3", 3, true)]
    [InlineData("frame-exponents.eac3", 4, false)]
    [InlineData("frame-exponents.eac3", 5, false)]
    [InlineData("frame-exponents.eac3", 6, false)]
    public void RandomBytesWithPlantedSyncWordsAreNoContainers(string template, int seed, bool aht)
    {
        byte[] data = Noise(Path.Combine(Fixtures.FixtureDirectory, "ac3", template), new Random(seed), 2000);
        Report report = Analyzer.CreateDefault().Analyze("noise.eac3", new MemoryStream(data));

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.False(stream.HasObjectAudio);
        Assert.Null(stream.JocObjectCount);
        Assert.All(report.Findings, f => Assert.Equal("ac3.aht-not-read", f.Code));
        Assert.Equal(aht, report.Findings.Count > 0);
        Node statistics = stream.Nodes[0].Children.Single(c => c.Name == "Statistics");
        Assert.DoesNotContain(statistics.Children, c => c.Name is "EMDF Containers" or "Frames With Object Audio");
    }

    private static byte[] Noise(string path, Random random, int count)
    {
        byte[] file = File.ReadAllBytes(path);
        int length = (((file[2] & 7) << 8 | file[3]) + 1) * 2;
        byte[] frame = file[..length];
        var output = new MemoryStream();
        for (int i = 0; i < count; i++)
        {
            byte[] copy = (byte[])frame.Clone();
            for (int at = KeepBytes; at < copy.Length - 4; at++)
            {
                copy[at] = (byte)random.Next(256);
            }

            for (int plant = 0; plant < 40; plant++)
            {
                int bit = (KeepBytes * 8) + random.Next(((copy.Length - 4) * 8) - (KeepBytes * 8) - 16);
                for (int k = 0; k < 16; k++)
                {
                    bool one = ((0x5838 >> (15 - k)) & 1) != 0;
                    int index = (bit + k) >> 3;
                    byte mask = (byte)(0x80 >> ((bit + k) & 7));
                    copy[index] = one ? (byte)(copy[index] | mask) : (byte)(copy[index] & ~mask);
                }
            }

            int crc = Crc16(copy.AsSpan(2, copy.Length - 4));
            copy[^2] = (byte)(crc >> 8);
            copy[^1] = (byte)crc;
            output.Write(copy);
        }

        return output.ToArray();
    }

    // The CRC of A/52 7.10.1: polynomial x^16 + x^15 + x^2 + 1, from 0, so a frame ends in the CRC of the rest.
    private static int Crc16(ReadOnlySpan<byte> data)
    {
        int crc = 0;
        foreach (byte b in data)
        {
            crc ^= b << 8;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x8005) & 0xFFFF : (crc << 1) & 0xFFFF;
            }
        }

        return crc;
    }
}
