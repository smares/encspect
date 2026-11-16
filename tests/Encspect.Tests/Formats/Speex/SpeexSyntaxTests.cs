using Encspect.Formats.Speex;

namespace Encspect.Tests;

// The reading of a frame's parameters takes exactly the bits libspeex's tables say each submode takes (bits_per_frame of modes.c and modes_wb.c), and keeps the values it reads.
public class SpeexSyntaxTests
{
    private static byte[] Random(int bytes, int seed)
    {
        var random = new System.Random(seed);
        byte[] data = new byte[bytes];
        random.NextBytes(data);
        return data;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void A_narrowband_layer_takes_the_bits_of_its_submode(int submode)
    {
        for (int seed = 0; seed < 20; seed++)
        {
            byte[] data = Random(100, seed);
            var bits = new SpeexBits(data);
            int[] fields = new int[SpeexLayout.FrameInts];
            SpeexSyntax.ReadNarrowband(ref bits, submode, fields);
            Assert.Equal(SpeexFrames.NarrowbandBits[submode] - 5, bits.Position);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void A_wideband_layer_takes_the_bits_of_its_submode(int submode)
    {
        for (int seed = 0; seed < 20; seed++)
        {
            byte[] data = Random(60, seed);
            var bits = new SpeexBits(data);
            int[] fields = new int[SpeexLayout.FrameInts];
            SpeexSyntax.ReadHigh(ref bits, submode, fields);
            Assert.Equal(SpeexFrames.WidebandBits[submode] - 4, bits.Position);
        }
    }

    [Fact]
    public void The_walk_stores_the_submodes_and_the_parameters_of_a_frame()
    {
        // Narrowband submode 3: the frame header, then random parameters.
        byte[] data = Random(40, 7);
        data[0] = (byte)(0x18 | (data[0] & 0x07));
        var frames = new Encspect.Core.ChunkedList<FrameRecord>();
        int[] fields = new int[SpeexLayout.FrameInts * 2];

        PacketWalk walk = SpeexFrames.Walk(data, 0, 2, frames, 2, fields);

        Assert.True(walk.Frames >= 1);
        Assert.Equal(3, fields[SpeexLayout.NarrowbandSubmode]);
        Assert.Equal(FrameRecord.Absent, fields[SpeexLayout.WidebandSubmode]);
        Assert.InRange(fields[SpeexLayout.Narrowband + SpeexLayout.NarrowbandLsp], 0, 63);
    }
}
