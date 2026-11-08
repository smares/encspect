using System.Buffers.Binary;
using System.Text;
using Encspect.Core;

namespace Encspect.Tests;

// The reader of lossyWAV's fact chunk text: the bounds that keep a damaged or hostile body from becoming a long name or a long walk.
public class FactTextTests
{
    private static byte[] Chunk(string id, byte[] body)
    {
        byte[] size = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Length);
        return [.. Encoding.ASCII.GetBytes(id), .. size, .. body, .. (body.Length % 2 == 1 ? new byte[] { 0 } : [])];
    }

    [Fact]
    public void AVersionOfAtMost64CharactersIsRecognized()
    {
        string version = "lossyWAV " + new string('9', 64 - "lossyWAV ".Length);
        FactText text = FactText.Read(Encoding.ASCII.GetBytes($"{version} @ d, --quality 0"))!;
        Assert.Equal(version, text.Version);
        Assert.True(text.IsRecognized);
    }

    [Fact]
    public void AVersionOfMoreThan64CharactersIsNotRecognized()
    {
        string version = "lossyWAV " + new string('9', 65 - "lossyWAV ".Length);
        FactText text = FactText.Read(Encoding.ASCII.GetBytes($"{version} @ d, --quality 0"))!;
        Assert.False(text.IsRecognized);
        Assert.Null(text.Version);
    }

    [Fact]
    public void ControlAndC1BytesBecomeTheReplacementCharacter()
    {
        byte[] body = [.. "lossyWAV 1 @ "u8, 0x01, 0x7F, 0x80, 0x9F, 0xA0, .. ", x"u8];
        FactText text = FactText.Read(body)!;
        Assert.Equal("���� ", text.Time);
    }

    [Fact]
    public void ThePartsKeepTheirBytePositions()
    {
        FactText text = FactText.Read("lossyWAV 1.4.2 @ 08/11/2026 08:37:19, --quality standard"u8)!;
        Assert.Equal((0, 17, 38), (text.VersionAt, text.TimeAt, text.SettingsAt));
    }

    [Fact]
    public void FindStopsAtTheDataAndAfterThe32ndChunk()
    {
        byte[] text = "lossyWAV 1.4.2 @ d, --quality 0\r\n\0\0"u8.ToArray();
        byte[] filler = Chunk("junk", [1, 2, 3, 4]);

        Assert.True(FactText.Find([.. filler, .. Chunk("fact", text)], 0, out int at, out int length));
        Assert.Equal((8 + 8 + 4, text.Length), (at, length));

        Assert.False(FactText.Find([.. Chunk("data", [1, 2]), .. Chunk("fact", text)], 0, out _, out _));

        byte[] atTheLimit = [.. Enumerable.Repeat(filler, 31).SelectMany(c => c), .. Chunk("fact", text)];
        Assert.True(FactText.Find(atTheLimit, 0, out _, out _));
        byte[] pastTheLimit = [.. Enumerable.Repeat(filler, 32).SelectMany(c => c), .. Chunk("fact", text)];
        Assert.False(FactText.Find(pastTheLimit, 0, out _, out _));
    }
}
