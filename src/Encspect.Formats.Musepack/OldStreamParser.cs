using Encspect.Core;

namespace Encspect.Formats.Musepack;

// Stream versions 4 to 6, before SV7 (libmpcdec 1.2.6 streaminfo_read_header_sv6, mpc_decoder.c).
// Their header has no magic number: the first 32-bit little-endian word holds a bitrate, the
// intensity and mid/side stereo flags, the stream version, the max band and the block size; the
// frame count follows, in 16 bits in version 4 and 32 in 5 and 6, and the frames start 48 or 64
// bits in, each a 20-bit length and its data, with 11 bits after the last in version 6. A header
// alone is too weak to tell such a stream from other data, so the stream is recognized only where
// its frames, followed by their lengths, end in the last word of the data. Only the header is
// reported; the frames are not analyzed.
internal static class OldStreamParser
{
    private const int MinimumBytes = 8;

    private readonly record struct OldHeader(int Bitrate, bool IntensityStereo, bool MidSide, int Version, int MaxBand, int BlockSize, long Frames)
    {
        public long DataBit => Version == 4 ? 48 : 64;

        public static OldHeader Read(WordStream w)
        {
            int version = (int)w.Read(11, 10);
            return new OldHeader((int)w.Read(0, 9), w.Read(9, 1) == 1, w.Read(10, 1) == 1, version, (int)w.Read(21, 5), (int)w.Read(26, 6),
                version == 4 ? w.Read(32, 16) : w.Read(32, 32));
        }
    }

    // Whether the data is such a stream: a header of version 4 to 6 whose frames end in the data's
    // last word.
    public static bool Sniff(ByteReader reader)
    {
        if (reader.Length < MinimumBytes || reader.Length % 4 != 0)
        {
            return false;
        }

        var words = new WordStream(reader, reader.Start, reader.End);
        OldHeader header = OldHeader.Read(words);
        if (header.Version is < 4 or > 6 || header.Frames == 0)
        {
            return false;
        }

        long bit = header.DataBit;
        for (long i = 0; i < header.Frames; i++)
        {
            if (i % 4096 == 0)
            {
                reader.CancellationToken.ThrowIfCancellationRequested();
            }

            if (bit + 20 > words.BitLength)
            {
                return false;
            }

            bit += 20 + words.Read(bit, 20) + (header.Version == 6 && i == header.Frames - 1 ? 11 : 0);
        }

        return bit <= words.BitLength && words.Layout.WordEnd(bit) == reader.End;
    }

    public static void Parse(ByteReader reader, Node parent, AnalysisContext context)
    {
        var words = new WordStream(reader, reader.Start, reader.End);
        WordLayout w = words.Layout;
        OldHeader h = OldHeader.Read(words);
        string name = $"Musepack SV{h.Version}";
        var stream = new Node(name, reader.Start, reader.Length) { Kind = NodeKind.Stream, Description = Explain.OldStream, Topic = Topics.SubbandCoding };
        // Version 4's 16-bit frame count lies in the second word's high bytes, its frames start in the
        // low ones.
        long start = reader.Start;
        bool split = h.Version == 4;
        var header = stream.Add(new Node("Header", start, 8)
        {
            Description = Explain.Sv4Header,
            Topic = Topics.SubbandCoding,
            Ranges = split ? [new ByteRange(start, 4), new ByteRange(start + 6, 2)] : null,
        });
        Node bitrate = header.Add(w.Field("Bitrate", 0, 9, NodeValue.Integer(h.Bitrate, "kbps"), Explain.Sv4Bitrate, Topics.BitrateModes));
        Node intensity = header.Add(w.Field("Intensity Stereo", 9, 10, NodeValue.Boolean(h.IntensityStereo), Explain.Sv4IntensityStereo, Topics.IntensityStereo));
        header.Add(w.Field("Mid/Side Stereo", 10, 11, NodeValue.Boolean(h.MidSide), Explain.Sv4MidSide, Topics.MidSideStereo));
        Node version = header.Add(w.Field("Stream Version", 11, 21, NodeValue.Integer(h.Version), Explain.Sv4StreamVersion));
        header.Add(w.Field("Max Band", 21, 26, NodeValue.Integer(h.MaxBand), Explain.Sv4MaxBand, Topics.SubbandCoding));
        Node blockSize = header.Add(w.Field("Block Size", 26, 32, NodeValue.Integer(h.BlockSize), Explain.Sv4BlockSize));
        header.Add(w.Field("Frame Count", 32, h.Version == 4 ? 48 : 64, NodeValue.Integer(h.Frames, "frames", "frame"), Explain.Sv4FrameCount));
        IReadOnlyList<ByteRange> data = split ? [new ByteRange(start + 4, 2), new ByteRange(start + 8, reader.Length - 8)] : [new ByteRange(start + 8, reader.Length - 8)];
        data = data.Where(r => r.Length > 0).ToList();
        if (data.Count > 0)
        {
            stream.Add(new Node("Audio Data", data[0].Offset, data[^1].End - data[0].Offset, NodeValue.Integer(data.Sum(r => r.Length), "bytes"))
            {
                Description = Explain.AudioData,
                Ranges = data.Count > 1 ? data : null,
            });
        }

        parent.Add(stream);
        context.AddFinding(Severity.Info, "musepack.version-not-analyzed",
            $"The stream is Musepack SV{h.Version}, which the analyzer reads only as far as its header; its frames are not analyzed.", version);
        if (h.Bitrate != 0)
        {
            context.AddFinding(Severity.Info, "musepack.constant-bitrate", $"The header gives a constant bitrate of {h.Bitrate} kbps; libmpcdec 1.2.6 refuses such streams.", bitrate);
        }

        if (h.IntensityStereo)
        {
            context.AddFinding(Severity.Info, "musepack.intensity-stereo", "The stream uses intensity stereo; libmpcdec 1.2.6 refuses such streams.", intensity);
        }

        if (h.BlockSize != 1)
        {
            context.AddFinding(Severity.Info, "musepack.block-size", $"The header's block size is {h.BlockSize}; libmpcdec 1.2.6 decodes only 1.", blockSize);
        }

        // libmpcdec 1.2.6: "used by all files up to SV7".
        context.AddStream(new StreamInfo
        {
            Nodes = [stream],
            Codec = name,
            SampleRate = 44100,
            Channels = 2,
            ChannelLayout = ChannelLayouts.Stereo,
            IsVbr = h.Bitrate == 0,
        });
    }
}
