using Encspect.Core;
using MatroskaCrc = Encspect.Formats.Matroska.Crc32;
using MusepackCrc = Encspect.Formats.Musepack.Crc32;

namespace Encspect.Tests;

public class Crc32Tests
{
    [Fact]
    public void GivesTheStandardCheckValue()
    {
        Assert.Equal(0xCBF43926u, Crc32.Of("123456789"u8));
        Assert.Equal(0u, Crc32.Of([]));
    }

    [Fact]
    public void PiecesGiveTheCrcOfTheWholeAndTheRegisterIsTheRawState()
    {
        byte[] data = new byte[1000];
        new Random(32).NextBytes(data);
        uint state = Crc32.Initial;
        for (int at = 0; at < data.Length; at += 77)
        {
            state = Crc32.Update(state, data.AsSpan(at, Math.Min(77, data.Length - at)));
        }

        Assert.Equal(Crc32.Of(data), Crc32.Finish(state));
        Assert.Equal(~state, Crc32.Finish(state));
    }

    [Fact]
    public void AgreesWithTheCopiesTheFormatsKeep()
    {
        var random = new Random(9);
        for (int length = 0; length < 200; length += 7)
        {
            byte[] data = new byte[length];
            random.NextBytes(data);
            Assert.Equal(MatroskaCrc.Of(data), Crc32.Of(data));
            Assert.Equal(MusepackCrc.Compute(data), Crc32.Of(data));
            Assert.Equal(MatroskaCrc.Update(0xFFFFFFFF, data), Crc32.Update(Crc32.Initial, data));
        }
    }
}
