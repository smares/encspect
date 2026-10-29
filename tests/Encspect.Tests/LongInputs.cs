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
        "asf-wma", "asf-pcm", "wavpack", "flac", "monkeys-audio", "tta", "musepack-sv8", "musepack-sv7", "musepack-sv5", "mp4-aac", "adif", "mp4-ac3", "mp4-eac3", "mp4-dts", "matroska", "truehd", "mlp", "wav", "aiff",
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
        "asf-pcm" => AsfBuilder.Build(PcmStreamProperties(), new byte[160], 2060, 10),
        "wavpack" => WavPackTinyBlocks(2060),
        "flac" => FlacBuilder.Stream(2060),
        "monkeys-audio" => MonkeysAudioBuilder.Silence(2060),
        "tta" => TtaBuilder.SmallFrames(2060),
        "musepack-sv8" => Sv8Packets(2060),
        "musepack-sv7" => Sv7Frames(2060),
        "musepack-sv5" => Sv5Frames(2060),
        "mp4-aac" => Mp4Samples(2060),
        "adif" => AdifBlocks(2060),
        "mp4-ac3" => Mp4Samples(2060, "ac-3", FirstAc3Frame("ac3/external-ffmpeg-mono-96k.ac3")),
        "mp4-eac3" => Mp4Samples(2060, "ec-3", FirstAc3Frame("ac3/external-ffmpeg-eac3-mono-48k.eac3")),
        "mp4-dts" => Mp4Samples(2060, "dtsc", FirstDtsFrame("dts/external-ffmpeg-mono-8k-64k.dts")),
        "matroska" => MatroskaBuilder.OpusBlocks(2060, 1000),
        "wav" => RiffChunks(2060),
        "aiff" => FormChunks(2060),
        "truehd" => Repeat("truehd/built-tiny-truehd-stereo.thd", 175),
        "mlp" => Repeat("truehd/built-tiny-mlp-stereo.mlp", 175),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    // A WAVE file whose format chunk is followed by many small chunks of a kind the parser names by their FourCC.
    private static byte[] RiffChunks(int count)
    {
        byte[] format = [1, 0, 1, 0, 0x40, 0x1F, 0, 0, 0x80, 0x3E, 0, 0, 2, 0, 16, 0];
        var body = new List<byte>(Encoding.ASCII.GetBytes("WAVEfmt "));
        body.AddRange(BitConverter.GetBytes(format.Length));
        body.AddRange(format);
        for (int i = 0; i < count; i++)
        {
            body.AddRange(Encoding.ASCII.GetBytes("abcd"));
            body.AddRange(BitConverter.GetBytes(4));
            body.AddRange(BitConverter.GetBytes(i));
        }

        byte[] file = new byte[8 + body.Count];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(file, 0);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), body.Count);
        body.CopyTo(file, 8);
        return file;
    }

    private static byte[] RiffChunk(string id, byte[] body)
    {
        byte[] chunk = new byte[8 + body.Length + (body.Length & 1)];
        Encoding.ASCII.GetBytes(id).CopyTo(chunk, 0);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4), body.Length);
        body.CopyTo(chunk, 8);
        return chunk;
    }

    private static byte[] RiffFile(string magic, params byte[][] chunks)
    {
        byte[] body = [.. "WAVE"u8, .. chunks.SelectMany(c => c)];
        byte[] file = new byte[8 + body.Length];
        Encoding.ASCII.GetBytes(magic).CopyTo(file, 0);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), body.Length);
        body.CopyTo(file, 8);
        return file;
    }

    private static readonly byte[] PcmFormat = RiffChunk("fmt ", [1, 0, 2, 0, 0x44, 0xAC, 0, 0, 0x10, 0xB1, 2, 0, 4, 0, 16, 0]);

    // A WAVE file of lists inside lists' place: lists LIST chunks of INFO type, each holding chunks empty chunks (the shape that multiplies the caps on
    // one level into millions of nodes), then the audio.
    public static byte[] WavNestedLists(int lists, int chunks)
    {
        byte[] inner = new byte[chunks * 8];
        for (int i = 0; i < chunks; i++)
        {
            Encoding.ASCII.GetBytes("abcd").CopyTo(inner, i * 8);
        }

        byte[] list = RiffChunk("LIST", [.. "xxxx"u8, .. inner]);
        var all = new List<byte[]> { PcmFormat };
        for (int i = 0; i < lists; i++)
        {
            all.Add(list);
        }

        all.Add(RiffChunk("data", new byte[16]));
        return RiffFile("RIFF", [.. all]);
    }

    // One cue chunk of the given number of cue points, named from 0.
    public static byte[] WavCueChunk(int points)
    {
        byte[] body = new byte[4 + (points * 24)];
        BinaryPrimitives.WriteInt32LittleEndian(body, points);
        for (int i = 0; i < points; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4 + (i * 24)), i);
            Encoding.ASCII.GetBytes("data").CopyTo(body, 4 + (i * 24) + 8);
        }

        return RiffChunk("cue ", body);
    }

    // A WAVE file with many cue chunks of the given number of cue points, or one chunk of that many.
    public static byte[] WavCueChunks(int chunks, int points)
    {
        byte[] cue = WavCueChunk(points);
        var all = new List<byte[]> { PcmFormat };
        for (int i = 0; i < chunks; i++)
        {
            all.Add(cue);
        }

        all.Add(RiffChunk("data", new byte[16]));
        return RiffFile("RIFF", [.. all]);
    }

    // An RF64 file whose ds64 chunk lists the given number of table entries, each of another four-character ID.
    public static byte[] WavDs64Table(int entries)
    {
        byte[] body = new byte[28 + (entries * 12)];
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(24), entries);
        for (int i = 0; i < entries; i++)
        {
            byte[] id = [(byte)('A' + (i % 26)), (byte)('a' + ((i / 26) % 26)), (byte)('a' + ((i / 676) % 26)), (byte)('a' + ((i / 17576) % 26))];
            id.CopyTo(body, 28 + (i * 12));
        }

        byte[] file = RiffFile("RF64", RiffChunk("ds64", body), PcmFormat, RiffChunk("data", new byte[16]));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), uint.MaxValue);
        return file;
    }

    // An AIFF file with the given number of Marker Chunks of the given number of markers (a marker of no name takes 8 bytes).
    public static byte[] AiffMarkerChunks(int chunks, int markers)
    {
        byte[] body = new byte[2 + (markers * 8)];
        BinaryPrimitives.WriteUInt16BigEndian(body, (ushort)markers);
        for (int i = 0; i < markers; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2 + (i * 8)), (ushort)(i + 1));
        }

        byte[] common = [.. "COMM"u8, 0, 0, 0, 18, 0, 1, 0, 0, 0, 0, 0, 16, 0x40, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0];
        var all = new List<byte>(common);
        for (int i = 0; i < chunks; i++)
        {
            all.AddRange("MARK"u8.ToArray());
            all.AddRange([0, 0, (byte)(body.Length >> 8), (byte)body.Length]);
            all.AddRange(body);
        }

        byte[] file = new byte[12 + all.Count];
        Encoding.ASCII.GetBytes("FORMxxxxAIFF").CopyTo(file, 0);
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(4), file.Length - 8);
        all.CopyTo(file, 12);
        return file;
    }

    // A WAVE file of 16-bit stereo PCM with the given number of bytes of samples.
    public static byte[] WavWithSamples(int bytes)
    {
        byte[] format = [1, 0, 2, 0, 0x44, 0xAC, 0, 0, 0x10, 0xB1, 2, 0, 4, 0, 16, 0];
        byte[] file = new byte[12 + 8 + format.Length + 8 + bytes];
        Encoding.ASCII.GetBytes("RIFFxxxxWAVEfmt ").CopyTo(file, 0);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), file.Length - 8);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(16), format.Length);
        format.CopyTo(file, 20);
        Encoding.ASCII.GetBytes("data").CopyTo(file, 20 + format.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(24 + format.Length), bytes);
        new Random(7).NextBytes(file.AsSpan(28 + format.Length));
        return file;
    }

    // A WAVE file whose 16-bit stereo samples are IEC 61937 bursts of the data type ATRAC-X low latency, which no codec parser reads: 128 IEC 60958
    // frames of 4 bytes each, a payload of 100 bytes.
    public static byte[] WavBursts(int count)
    {
        byte[] samples = new byte[count * 512];
        for (int i = 0; i < count; i++)
        {
            new byte[] { 0x72, 0xF8, 0x1F, 0x4E, 112, 0, 0x20, 0x03 }.CopyTo(samples, i * 512);
            for (int k = 0; k < 100; k++)
            {
                samples[(i * 512) + 8 + k] = (byte)(k + 1);
            }
        }

        byte[] file = WavWithSamples(samples.Length);
        samples.CopyTo(file, file.Length - samples.Length);
        return file;
    }

    // An AIFF file whose Common Chunk is followed by many small chunks of a kind the parser names by their FourCC.
    private static byte[] FormChunks(int count)
    {
        byte[] common = [0, 1, 0, 0, 0, 0, 0, 16, 0x40, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0];
        var body = new List<byte>(Encoding.ASCII.GetBytes("AIFFCOMM"));
        body.AddRange([0, 0, 0, (byte)common.Length]);
        body.AddRange(common);
        for (int i = 0; i < count; i++)
        {
            body.AddRange(Encoding.ASCII.GetBytes("abcd"));
            body.AddRange([0, 0, 0, 4]);
            body.AddRange([0, 0, (byte)(i >> 8), (byte)i]);
        }

        byte[] file = new byte[8 + body.Count];
        Encoding.ASCII.GetBytes("FORM").CopyTo(file, 0);
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(4), body.Count);
        body.CopyTo(file, 8);
        return file;
    }

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
