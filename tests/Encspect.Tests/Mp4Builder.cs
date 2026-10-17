using System.Text;

namespace Encspect.Tests;

// MP4 files built in memory for the tests that need many boxes, entries or samples: a movie of one audio track whose sample tables
// and boxes the caller chooses, in front of or behind the media data.
internal static class Mp4Builder
{
    public static byte[] U32(long value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    public static byte[] Box(string type, params byte[][] parts)
    {
        int length = 8 + parts.Sum(p => p.Length);
        byte[] result = new byte[length];
        U32(length).CopyTo(result, 0);
        Encoding.ASCII.GetBytes(type).CopyTo(result, 4);
        int at = 8;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result, at);
            at += part.Length;
        }

        return result;
    }

    public static byte[] FullBox(string type, params byte[][] parts) => Box(type, [[0, 0, 0, 0], .. parts]);

    public static byte[] Join(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    public static byte[] Ftyp() => Box("ftyp", "M4A "u8.ToArray(), new byte[4], "M4A isom"u8.ToArray());

    // The sample entry of Apple Lossless, which no parser reads: the track's samples are located and counted and nothing is decoded.
    public static byte[] AlacEntry(params byte[][] children) =>
        Box("alac", [new byte[6], [0, 1], new byte[8], [0, 2, 0, 16, 0, 0, 0, 0, 0xAC, 0x44, 0, 0], .. children]);

    // An AAC LC stereo entry at 44.1 kHz with its esds box.
    public static byte[] AacEntry(string format = "mp4a", params byte[][] children)
    {
        byte[] esds = FullBox("esds", [0x03, 25, 0, 1, 0, 0x04, 17, 0x40, 0x15, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x05, 2, 0x12, 0x10, 0x06, 1, 2]);
        return Box(format, [new byte[6], [0, 1], new byte[8], [0, 2, 0, 16, 0, 0, 0, 0, 0xAC, 0x44, 0, 0], esds, .. children]);
    }

    // The sinf box of a Common Encryption entry (cenc) whose tenc box says that no sample is protected by default.
    public static byte[] ClearSinf() =>
        Box("sinf", Box("frma", "mp4a"u8.ToArray()), FullBox("schm", "cenc"u8.ToArray(), U32(0x10000)), Box("schi", FullBox("tenc", [0, 0, 0, 0], new byte[16])));

    public static byte[] EmptyTables() => Join(FullBox("stts", U32(0)), FullBox("stsc", U32(0)), FullBox("stsz", new byte[8]), FullBox("stco", U32(0)));

    // A silent AAC LC stereo access unit of 100 bytes: a channel pair element with a common window, max_sfb 0 and a global gain of 100 in each channel.
    public static byte[] AacUnit() => [0x21, 0x00, 0x03, 0x20, 0x64, 0x1C, .. new byte[94]];

    // A trak box with a sound handler over the given sample table boxes; ids and durations are small, as the tests do not look at them.
    public static byte[] Trak(int id, byte[] entry, params byte[][] tables)
    {
        byte[] stbl = Box("stbl", [FullBox("stsd", U32(1), entry), .. tables]);
        byte[] mdia = Box("mdia",
            FullBox("mdhd", new byte[8], U32(44100), U32(0), [0x55, 0xC4, 0, 0]),
            FullBox("hdlr", new byte[4], "soun"u8.ToArray(), new byte[13]),
            Box("minf", FullBox("smhd", new byte[4]), stbl));
        return Box("trak", FullBox("tkhd", new byte[8], U32(id), new byte[4], U32(0), new byte[60]), mdia);
    }

    public static byte[] Mvhd() => FullBox("mvhd", new byte[8], U32(1000), U32(0), new byte[80]);

    public static byte[] Stts(long count, long delta) => FullBox("stts", U32(1), U32(count), U32(delta));

    public static byte[] Stsc(long samplesPerChunk) => FullBox("stsc", U32(1), U32(1), U32(samplesPerChunk), U32(1));

    // A constant sample size, with no table behind the count.
    public static byte[] ConstantSizes(long size, long count) => FullBox("stsz", U32(size), U32(count));

    public static byte[] Sizes(IEnumerable<int> sizes)
    {
        int[] list = [.. sizes];
        return FullBox("stsz", U32(0), U32(list.Length), Join([.. list.Select(s => U32(s))]));
    }

    public static byte[] ChunkOffsets(IEnumerable<long> offsets)
    {
        long[] list = [.. offsets];
        return FullBox("stco", U32(list.Length), Join([.. list.Select(o => U32(o))]));
    }

    // ftyp, a moov box of one audio track and then the media data, with the offsets the sample tables hold laid out by build(offset of the mdat payload).
    public static byte[] FileWithMoov(Func<long, byte[]> trak, byte[] mdatPayload, params byte[][] after)
    {
        byte[] ftyp = Ftyp();
        byte[] Moov(long payload) => Box("moov", Mvhd(), trak(payload));
        long moovLength = Moov(0).Length;
        byte[] moov = Moov(ftyp.Length + moovLength + 8);
        return Join(ftyp, moov, Box("mdat", mdatPayload), Join(after));
    }
}
