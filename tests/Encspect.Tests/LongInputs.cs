using System.Buffers.Binary;
using System.Text;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Inputs built in memory whose lists of children run past two blocks of 1,024 entries, which no
// committed fixture does: one for each implementation of a long list in the formats. Repeating a
// fixture's frames or pages gives findings (a repeated header, a sequence gap); the lists are what
// these inputs are for.
internal static class LongInputs
{
    public static IReadOnlyList<string> Names { get; } =
    [
        "mpeg-layer3-lame", "mpeg-layer3-frames", "adts", "ac3", "eac3", "dts", "ogg-pages", "ogg-speex", "ogg-vorbis", "ogg-opus",
        "asf-wma", "asf-wma-pro", "asf-pcm", "wavpack", "musepack-sv8", "musepack-sv7", "musepack-sv5", "mp4-aac", "adif", "mp4-ac3", "mp4-eac3", "mp4-dts",
    ];

    public static byte[] Build(string name) => name switch
    {
        "mpeg-layer3-lame" => Repeat("mpeg/external-lame-cbr128-joint.mp3", 94),
        "mpeg-layer3-frames" => Layer3Frames(2060),
        "adts" => Repeat("adts/stereo.aac", 345),
        "ac3" => Repeat("ac3/external-ffmpeg-stereo-192k.ac3", 129),
        "eac3" => Repeat("ac3/external-ffmpeg-eac3-stereo-96k.eac3", 129),
        "dts" => Repeat("dts/external-ffmpeg-stereo-44k-256k.dts", 47),
        "ogg-pages" => UnknownOggPages(2060),
        "ogg-speex" => SpeexPackets(2060),
        "ogg-vorbis" => VorbisPackets(2060),
        "ogg-opus" => OpusPackets(2060),
        "asf-wma" => AsfWma("asf/external-ffmpeg-wmav2-stereo-44k-128k.wma", 0x0161, 2060),
        "asf-wma-pro" => AsfWma("asf/external-windows-wma10pro-cbr-stereo-44k-256.wma", 0x0162, 515),
        "asf-pcm" => AsfBuilder.Build(PcmStreamProperties(), new byte[160], 2060, 10),
        "wavpack" => WavPackTinyBlocks(2060),
        "musepack-sv8" => Sv8Packets(2060),
        "musepack-sv7" => Sv7Frames(2060),
        "musepack-sv5" => Sv5Frames(2060),
        "mp4-aac" => Mp4Samples(2060),
        "adif" => AdifBlocks(2060),
        "mp4-ac3" => Mp4Samples(2060, "ac-3", FirstAc3Frame("ac3/external-ffmpeg-mono-96k.ac3")),
        "mp4-eac3" => Mp4Samples(2060, "ec-3", FirstAc3Frame("ac3/external-ffmpeg-eac3-mono-48k.eac3")),
        "mp4-dts" => Mp4Samples(2060, "dtsc", FirstDtsFrame("dts/external-ffmpeg-mono-8k-64k.dts")),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    public static byte[] Repeat(string fixture, int times)
    {
        byte[] source = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, fixture));
        byte[] data = new byte[source.Length * times];
        for (int at = 0; at < data.Length; at += source.Length)
        {
            source.CopyTo(data, at);
        }

        return data;
    }

    // The first syncframe of an AC-3 or E-AC-3 file, which the sample entries of MP4 hold one apiece.
    private static byte[] FirstAc3Frame(string fixture)
    {
        byte[] source = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, fixture));
        int length = Encspect.Formats.Ac3.SyncFrame.Read(source).FrameLength;
        return source[..length];
    }

    // The first core frame of a DTS file in 16-bit big-endian words: up to the second sync word.
    private static byte[] FirstDtsFrame(string fixture)
    {
        byte[] source = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, fixture));
        ReadOnlySpan<byte> sync = [0x7F, 0xFE, 0x80, 0x01];
        if (!source.AsSpan().StartsWith(sync)) { throw new InvalidDataException(fixture); }
        int second = source.AsSpan(4).IndexOf(sync) + 4;
        return source[..second];
    }

    // An ADIF file: the header of the fixture and its raw data blocks repeated.
    private static byte[] AdifBlocks(int blocks)
    {
        byte[] source = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "adts", "adif.aac"));
        Report report = Analyzer.CreateDefault().Analyze("adif.aac", new MemoryStream(source));
        Node first = report.Root.Children.Single(c => c.Name == "ADIF").Children.First(c => c.Name == "Raw Data Block");
        int header = (int)first.Offset;
        byte[] body = source[header..];
        int perCopy = report.Root.Children.Single(c => c.Name == "ADIF").Children.Count(c => c.Name == "Raw Data Block");
        int copies = (blocks + perCopy - 1) / perCopy;
        var data = new byte[header + (body.Length * copies)];
        source.AsSpan(0, header).CopyTo(data);
        for (int i = 0; i < copies; i++)
        {
            body.CopyTo(data, header + (i * body.Length));
        }

        return data;
    }

    // MPEG-1 Layer III stereo frames at 32 kbps and 44100 Hz, 104 bytes each.
    private static byte[] Layer3Frames(int count)
    {
        byte[] frame = [0xFF, 0xFB, 0x10, 0x00, 0x00, 0x00, 0x01, 0x90, .. new byte[96]];
        return Enumerable.Repeat(frame, count).SelectMany(b => b).ToArray();
    }

    private static byte[] UnknownOggPages(int pages)
    {
        var builder = new OggBuilder().Page(7, 0, 2, "unknown codec"u8.ToArray());
        byte[] packet = new byte[20];
        for (int i = 1; i < pages; i++)
        {
            builder.Page(7, i, i == pages - 1 ? (byte)4 : (byte)0, packet);
        }

        return builder.Bytes;
    }

    private static byte[] SpeexPackets(int packets)
    {
        // A narrowband frame of submode 7 and the terminator, 63 bytes.
        byte[] frame = [0x38, .. new byte[60], 0x07, 0x80];
        var builder = new OggBuilder()
            .Page(9, 0, 2, OggBuilder.SpeexHeader(8000, 0))
            .Page(9, 0, 0, OggBuilder.VorbisComment("Encoded with Speex 1.2.1"));
        for (int done = 0; done < packets; done += 250)
        {
            builder.Page(9, ((done + 250) * 160L) - 40, done + 250 == packets ? (byte)4 : (byte)0, [.. Enumerable.Repeat(frame, 250)]);
        }

        return builder.Bytes;
    }

    private static byte[] VorbisPackets(int packets)
    {
        byte[] identification = new byte[30];
        "\u0001vorbis"u8.CopyTo(identification);
        identification[11] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(identification.AsSpan(12), 44100);
        identification[28] = 0xB8;
        identification[29] = 1;
        byte[] comment = [0x03, .. "vorbis"u8, .. OggBuilder.VorbisComment("Encspect"), 0x01];
        var builder = new OggBuilder()
            .Page(5, 0, 2, identification)
            .Page(5, 0, 0, comment, MinimalVorbisSetup);
        byte[] block = [0x0E];
        for (int done = 0; done < packets; done += 250)
        {
            builder.Page(5, (done + 249) * 1024L, done + 250 == packets ? (byte)4 : (byte)0, [.. Enumerable.Repeat(block, 250)]);
        }

        return builder.Bytes;
    }

    private static byte[] OpusPackets(int packets)
    {
        byte[] head = [.. "OpusHead"u8, 1, 1, 0x38, 0x01, 0x80, 0xBB, 0, 0, 0, 0, 0];
        var builder = new OggBuilder()
            .Page(6, 0, 2, head)
            .Page(6, 0, 0, [.. "OpusTags"u8, .. OggBuilder.VorbisComment("Encspect")]);
        byte[] packet = [0xF8, 0, 0, 0];
        for (int done = 0; done < packets; done += 250)
        {
            builder.Page(6, (done + 250) * 960L, done + 250 == packets ? (byte)4 : (byte)0, [.. Enumerable.Repeat(packet, 250)]);
        }

        return builder.Bytes;
    }

    // The setup header of make_vorbis_opus_fixtures.py's minimal codec.
    private static readonly byte[] MinimalVorbisSetup =
    [
        0x05, 0x76, 0x6F, 0x72, 0x62, 0x69, 0x73, 0x00, 0x42, 0x43, 0x56, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x80, 0x62, 0x40, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
    ];

    private static byte[] AsfWma(string fixture, int formatTag, int packets)
    {
        (byte[] properties, byte[] mediaObject) = AsfBuilder.Take(fixture, formatTag);
        return AsfBuilder.Build(properties, mediaObject, packets, 46);
    }

    // A Stream Properties Object of 16-bit mono PCM at 8 kHz, whose payloads no codec parser reads.
    private static byte[] PcmStreamProperties() => AsfBuilder.StreamProperties(1, 1, 1, 8000, 16000, 2, 16, []);

    // Blocks of mono 16-bit audio at 44.1 kHz, 128 samples each, in one set apiece.
    private static byte[] WavPackTinyBlocks(int count)
    {
        byte[] header = new byte[32];
        "wvpk"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 0x410);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 128);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), 1 | 4 | 0x800 | 0x1000 | (9u << 23) | 0x10000000);
        byte[][] parts = [WavPackBuilder.Sub(0x0A, new byte[4]), WavPackBuilder.Sub(0x2F, new byte[4])];
        var data = new List<byte>(count * 44);
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)(count * 128L));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)(i * 128L));
            data.AddRange(WavPackBuilder.Join(header, parts));
        }

        return [.. data];
    }

    // An SV7 stream: a header with mid/side, max band 1 and no true gapless, then frames of 400 zero bits.
    private static byte[] Sv7Frames(int frames)
    {
        var words = new List<uint> { 0x072B504D, (uint)frames, (1u << 30) | (1u << 24) | (10u << 20), 0, 0, 0 };
        long bit = 192;
        void Write(uint value, int count)
        {
            for (int i = count - 1; i >= 0; i--, bit++)
            {
                while (words.Count <= bit >> 5)
                {
                    words.Add(0);
                }

                if (((value >> i) & 1) != 0)
                {
                    words[(int)(bit >> 5)] |= 1u << (31 - (int)(bit & 31));
                }
            }
        }

        Write(116, 8);
        for (int i = 0; i < frames; i++)
        {
            Write(400, 20);
            bit += 400;
        }

        Write(0, 11);
        return words.SelectMany(BitConverter.GetBytes).ToArray()[..(int)(((bit + 31) >> 5) << 2)];
    }

    // An SV8 stream: a stream header, ReplayGain, then audio packets of one byte and the stream end.
    private static byte[] Sv8Packets(int packets)
    {
        byte[] body = [8, 0, 0, 0x1F, 0x1F];
        uint crc = 0xFFFFFFFF;
        foreach (byte b in body)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
        }

        crc ^= 0xFFFFFFFF;
        byte[] sh = [(byte)'S', (byte)'H', 12, (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc, .. body];
        byte[] rg = [(byte)'R', (byte)'G', 12, 1, 0, 0, 0, 0, 0, 0, 0, 0];
        var data = new List<byte>("MPCK"u8.ToArray());
        data.AddRange(sh);
        data.AddRange(rg);
        for (int i = 0; i < packets; i++)
        {
            data.AddRange(new byte[] { (byte)'A', (byte)'P', 4, 0 });
        }

        data.AddRange("SE\x03"u8.ToArray());
        return [.. data];
    }

    // An SV5 stream: a header with a variable bitrate, mid/side, max band 0 and one frame per length, then
    // frames of 32 zero bits.
    private static byte[] Sv5Frames(int frames)
    {
        long bits = 64 + (frames * 52L);
        var data = new byte[(int)(bits >> 5) << 2];
        uint[] words = new uint[data.Length / 4];
        words[0] = (1u << 21) | (5u << 11) | 1u;
        words[1] = (uint)frames;
        for (long i = 0, bit = 64; i < frames; i++, bit += 52)
        {
            for (int b = 0; b < 20; b++)
            {
                long position = bit + b;
                if (((32 >> (19 - b)) & 1) != 0 && position >> 5 < words.Length)
                {
                    words[position >> 5] |= 1u << (31 - (int)(position & 31));
                }
            }
        }

        Buffer.BlockCopy(words, 0, data, 0, data.Length);
        return data;
    }

    // An M4A file of count silent AAC LC stereo access units of 100 bytes in one chunk after moov, or
    // with another sample entry (AC-3, E-AC-3, DTS) and the same bytes as every sample.
    private static byte[] Mp4Samples(int count, string entry = "mp4a", byte[]? sample = null)
    {
        static byte[] Box(string type, params byte[][] parts)
        {
            byte[] payload = parts.SelectMany(p => p).ToArray();
            return [.. BigEndian(8 + payload.Length), .. Encoding.ASCII.GetBytes(type), .. payload];
        }

        static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

        byte[] unit = sample ?? [0x21, 0x00, 0x03, 0x20, 0x64, 0x1C, .. new byte[94]];
        byte[] Moov(int mdatPayload)
        {
            byte[] esds = Box("esds", [0, 0, 0, 0, 0x03, 25, 0, 1, 0, 0x04, 17, 0x40, 0x15, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x05, 2, 0x12, 0x10, 0x06, 1, 2]);
            byte[] mp4a = Box(entry, new byte[6], [0, 1], new byte[8], [0, 2, 0, 16, 0, 0, 0, 0, 0xAC, 0x44, 0, 0], entry == "mp4a" ? esds : []);
            byte[] stsz = Box("stsz", [0, 0, 0, 0], BigEndian(0), BigEndian(count), Enumerable.Range(0, count).SelectMany(_ => BigEndian(unit.Length)).ToArray());
            byte[] stbl = Box("stbl",
                Box("stsd", [0, 0, 0, 0], BigEndian(1), mp4a),
                Box("stts", [0, 0, 0, 0], BigEndian(1), BigEndian(count), BigEndian(1024)),
                Box("stsc", [0, 0, 0, 0], BigEndian(1), BigEndian(1), BigEndian(count), BigEndian(1)),
                stsz,
                Box("stco", [0, 0, 0, 0], BigEndian(1), BigEndian(mdatPayload)));
            byte[] mdia = Box("mdia",
                Box("mdhd", [0, 0, 0, 0], new byte[8], BigEndian(44100), BigEndian(count * 1024), [0x55, 0xC4, 0, 0]),
                Box("hdlr", [0, 0, 0, 0], new byte[4], Encoding.ASCII.GetBytes("soun"), new byte[13]),
                Box("minf", Box("smhd", new byte[8]), Box("stbl", stbl[8..])));
            byte[] tkhd = Box("tkhd", [0, 0, 0, 3], new byte[8], BigEndian(1), new byte[4], BigEndian(count * 1024 / 44100 * 1000), new byte[60]);
            byte[] mvhd = Box("mvhd", [0, 0, 0, 0], new byte[8], BigEndian(1000), BigEndian(count * 1024 / 44100 * 1000), new byte[80]);
            return Box("moov", mvhd, Box("trak", tkhd, mdia));
        }

        byte[] ftyp = Box("ftyp", Encoding.ASCII.GetBytes("M4A "), new byte[4], Encoding.ASCII.GetBytes("M4A isom"));
        int moovLength = Moov(0).Length;
        byte[] moov = Moov(ftyp.Length + moovLength + 8);
        byte[] mdat = [.. BigEndian(8 + (count * unit.Length)), .. "mdat"u8, .. Enumerable.Repeat(unit, count).SelectMany(u => u)];
        return [.. ftyp, .. moov, .. mdat];
    }
}
