using System.Globalization;
using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Aiff;

// What the walker knows about a chunk by its ID: its node's name and description, the article, and the reader of its data.
internal sealed record ChunkSpec(string? Name, string Description, string? Topic, Walker.ChunkReader? Read)
{
    private static readonly Dictionary<string, ChunkSpec> Known = new(StringComparer.Ordinal)
    {
        ["FVER"] = new("Format Version Chunk", Explain.FverChunk, Topics.Container, Chunks.FormatVersion),
        ["COMM"] = new("Common Chunk", Explain.CommChunk, Topics.Common, Chunks.Common),
        ["SSND"] = new("Sound Data Chunk", Explain.SsndChunk, Topics.SoundData, Chunks.SoundData),
        ["MARK"] = new("Marker Chunk", Explain.MarkChunk, Topics.Markers, Chunks.Markers),
        ["INST"] = new("Instrument Chunk", Explain.InstChunk, Topics.Markers, Chunks.Instrument),
        ["COMT"] = new("Comments Chunk", Explain.ComtChunk, Topics.Markers, Chunks.Comments),
        ["NAME"] = new("Name Chunk", Explain.NameChunk, Topics.Container, Chunks.Text),
        ["AUTH"] = new("Author Chunk", Explain.AuthChunk, Topics.Container, Chunks.Text),
        ["(c) "] = new("Copyright Chunk", Explain.CopyrightChunk, Topics.Container, Chunks.Text),
        ["ANNO"] = new("Annotation Chunk", Explain.AnnoChunk, Topics.Container, Chunks.Text),
        ["APPL"] = new("Application Specific Chunk", Explain.ApplChunk, Topics.Container, Chunks.Application),
        ["AESD"] = new("Audio Recording Chunk", Explain.AesdChunk, Topics.Container, Chunks.AudioRecording),
        ["MIDI"] = new("MIDI Data Chunk", Explain.MidiChunk, Topics.Container, Chunks.Midi),
        ["ID3 "] = new("ID3v2 Chunk", Explain.Id3Chunk, Topics.Id3InChunks, Chunks.Id3),
        ["id3 "] = new("ID3v2 Chunk", Explain.Id3Chunk, Topics.Id3InChunks, Chunks.Id3),
    };

    public static ChunkSpec Of(string id) => Known.TryGetValue(id, out ChunkSpec? spec) ? spec : new ChunkSpec(null, Explain.UnknownChunk, Topics.Container, null);
}

// The readers of the chunks.
internal static class Chunks
{
    // At most this many records of a table are listed as nodes, whatever count the chunk states.
    public const int MaxListed = 1024;

    // The references of loops and comments to markers kept to find one the Marker Chunk does not hold; past them the references are not checked.
    public const int MaxMarkerReferences = 65_536;

    // The time stamp of the format version of AIFF-C (section 3.0).
    public const uint AifcVersion1 = 0xA2805140;

    public static void FormatVersion(State s, Fields f, Node chunk, long length)
    {
        s.VersionChunks++;
        uint stamp = f.Dword("Timestamp", Explain.FverTimestamp, v => v == AifcVersion1 ? NodeValue.Text($"{v} (version 1 of AIFF-C)") : NodeValue.Integer(v), Topics.Container);
        if (s.VersionChunks == 1)
        {
            s.Version = stamp;
            s.VersionNode = chunk.Children[^1];
        }

        chunk.Value = NodeValue.Text(stamp == AifcVersion1 ? "AIFF-C version 1" : stamp.ToString(CultureInfo.InvariantCulture));
        if (f.Remaining > 0)
        {
            f.Bytes("Extra Data", f.Remaining, Explain.ExtraData);
        }
    }

    public static void Common(State s, Fields f, Node chunk, long length)
    {
        s.CommonChunks++;
        int channels = f.Word("Channels", Explain.CommChannels, "channels", Topics.Common);
        uint frames = f.Dword("Sample Frames", Explain.CommFrames, "sample frames", Topics.Common);
        int size = f.Word("Sample Size", Explain.CommSampleSize, "bits", Topics.Common);
        long rateAt = f.Position;
        byte[] ten = f.Reader.ReadBytes(10);
        double? rate = Extended.Decode(ten);
        bool isRateValid = rate is > 0 and <= int.MaxValue;
        NodeValue shown = rate is null ? NodeValue.Text("not a finite number")
            : !isRateValid ? NodeValue.Text($"{rate.Value.ToString("R", CultureInfo.InvariantCulture)} (not a possible sample rate)")
            : rate == Math.Floor(rate.Value) ? NodeValue.Integer((long)rate.Value, "Hz") : NodeValue.Decimal(rate.Value, 4, "Hz");
        Node rateNode = f.Node.Add("Sample Rate", rateAt, 10, shown, Explain.CommRate, Topics.ExtendedRate);
        rateNode.Add("Sign and Exponent", rateAt, 2, NodeValue.Hex(Fields.Word(ten), 4), Explain.RateExponent, Topics.ExtendedRate);
        rateNode.Add("Mantissa", rateAt + 2, 8, NodeValue.Hex(System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(ten.AsSpan(2)), 16), Explain.RateMantissa, Topics.ExtendedRate);

        string? type = null;
        string? name = null;
        if (s.IsAifc)
        {
            if (f.Remaining >= 4)
            {
                type = f.FourCc("Compression Type", Explain.CommType, Topics.Compression);
                int count = f.Byte("Compression Name Length", Explain.CommNameLength, null, Topics.Compression);
                long textAt = f.Position;
                if (count > f.Remaining)
                {
                    throw new EndOfStreamException();
                }

                name = Fields.Clean(Encoding.Latin1.GetString(f.Reader.ReadBytes(count)));
                f.Node.Add("Compression Name", textAt, count, name, Explain.CommName, Topics.Compression);
                if (count % 2 == 0 && f.Remaining > 0)
                {
                    long padAt = f.Position;
                    f.Node.Add("Pad Byte", padAt, 1, NodeValue.Hex(f.Reader.ReadByte(), 2), Explain.StringPad);
                }
            }
            else
            {
                Node at = chunk.Children[1];
                s.Findings.Add(Severity.Warning, "aiff.compression-type-missing", at, _ =>
                    "The FORM is of type AIFC, whose Common Chunk holds a compression type and name after the sample rate, but this one ends with the sample rate; the samples are taken to be uncompressed.");
            }
        }

        if (f.Remaining > 0)
        {
            f.Bytes("Extra Data", f.Remaining, Explain.ExtraData);
        }

        var common = new CommonChunk(channels, frames, size, isRateValid ? rate!.Value : 0, isRateValid, type, name, chunk);
        chunk.Value = Summary(common);
        if (s.CommonChunks > 1)
        {
            return;
        }

        s.Common = common;
        Validate(s, common, chunk, rateNode);
    }

    private static string Summary(CommonChunk common)
    {
        string coding = AifcType.Of(common.Type)?.Codec ?? $"coding '{common.Type}'";
        string rate = common.IsRateValid ? $"{common.SampleRate.ToString("0.####", CultureInfo.InvariantCulture)} Hz" : "no valid rate";
        return $"{coding}, {Counts.Of(common.Channels, "channel", "channels")}, {rate}, {Counts.Of(common.SampleSize, "bit", "bits")}";
    }

    private static void Validate(State s, CommonChunk common, Node chunk, Node rateNode)
    {
        Node At(string name) => chunk.Children.FirstOrDefault(c => c.Name == name) ?? chunk;
        if (common.Channels == 0)
        {
            s.Findings.Add(Severity.Warning, "aiff.channels-zero", At("Channels"), _ => "The Common Chunk states 0 channels.");
        }

        if (!common.IsRateValid)
        {
            s.Findings.Add(Severity.Warning, "aiff.sample-rate-invalid", rateNode, _ => "The sample rate in the Common Chunk is not a positive finite number that fits 32 bits; the rate is taken to be 0.");
        }

        AifcType? type = AifcType.Of(common.Type);
        if (type is { IsLinear: true, FixedBits: null } && common.SampleSize is < 1 or > 32)
        {
            int bits = common.SampleSize;
            s.Findings.Add(Severity.Warning, "aiff.sample-size-invalid", At("Sample Size"), _ => $"The sample size is {Counts.Of(bits, "bit", "bits")}, but linear samples take 1 to 32 bits.");
        }

        if (type is { FixedBits: { } fixedBits } && common.SampleSize != fixedBits)
        {
            int bits = common.SampleSize;
            s.Findings.Add(Severity.Warning, "aiff.sample-size-mismatch", At("Sample Size"), _ => $"The compression type {common.Type} has samples of {Counts.Of(fixedBits, "bit", "bits")}, but the sample size is {bits}.");
        }
    }

    public static void SoundData(State s, Fields f, Node chunk, long length)
    {
        uint offset = f.Dword("Offset", Explain.SsndOffset, "bytes", Topics.SoundData);
        uint blockSize = f.Dword("Block Size", Explain.SsndBlockSize, "bytes", Topics.SoundData);
        long dataLength = f.Remaining;
        if (s.Sound is not null)
        {
            return;
        }

        if (dataLength > 0)
        {
            f.Node.Add(new Node("Sound Data", f.Position, dataLength, NodeValue.Integer(dataLength, "bytes", "byte")) { Description = Explain.SsndData, Topic = Topics.SoundData });
        }

        s.FirstSoundNode = chunk;
        s.Sound = new SoundChunk(chunk, f.Reader.Start, length, offset, blockSize, s.ChunkSizeIsUnknown);
    }

    public static void Markers(State s, Fields f, Node chunk, long length)
    {
        int count = f.Word("Marker Count", Explain.MarkCount, "markers", Topics.Markers);
        s.MarkerIds ??= [];
        Node? table = count > 0 ? f.Node.Add(new Node("Markers", f.Position, f.Remaining, NodeValue.Integer(count, "markers", "marker")) { Description = Explain.MarkTable, Topic = Topics.Markers }) : null;
        int listed = 0;
        bool isDuplicate = false;
        bool isIdZero = false;
        for (int i = 0; i < count && f.Remaining >= 7; i++)
        {
            long at = f.Position;
            short id = unchecked((short)f.ReadWord());
            uint position = f.ReadDword();
            int nameLength = f.Reader.ReadByte();
            long total = 1 + nameLength;
            total += total % 2;
            if (total - 1 > f.Remaining)
            {
                f.Reader.Position = at;
                break;
            }

            string name = Fields.Clean(Encoding.Latin1.GetString(f.Reader.ReadBytes(nameLength)));
            f.Reader.Skip(total - 1 - nameLength);
            isDuplicate |= !s.MarkerIds.Add(id);
            isIdZero |= id <= 0;
            listed++;
            if (i < MaxListed)
            {
                var marker = new Node($"Marker {i}", at, 6 + total, name.Length == 0 ? NodeValue.Integer(position, "sample frames", "sample frame") : name) { Description = Explain.Marker, Topic = Topics.Markers };
                marker.Add("Marker ID", at, 2, NodeValue.Integer(id), Explain.MarkId, Topics.Markers);
                marker.Add("Position", at + 2, 4, NodeValue.Integer(position, "sample frames", "sample frame"), Explain.MarkPosition, Topics.Markers);
                marker.Add("Name", at + 6, total, name, Explain.MarkName, Topics.Markers);
                table!.Add(marker);
            }
        }

        if (listed < count)
        {
            Node target = table ?? chunk;
            int stated = count;
            int held = listed;
            s.Findings.Add(Severity.Warning, "aiff.marker-count-mismatch", target, _ => $"The Marker Chunk states {Counts.Of(stated, "marker", "markers")}, but its data holds {held}.");
        }

        if (isDuplicate)
        {
            s.Findings.Add(Severity.Warning, "aiff.marker-id-repeated", table ?? chunk, _ => "Two markers have the same ID, which the specification wants unique.");
        }

        if (isIdZero)
        {
            s.Findings.Add(Severity.Warning, "aiff.marker-id-not-positive", table ?? chunk, _ => "A marker has an ID that is not above zero, which the specification requires.");
        }

        if (f.Remaining > 0)
        {
            f.Bytes("Extra Data", f.Remaining, Explain.ExtraData);
        }
    }

    public static void Instrument(State s, Fields f, Node chunk, long length)
    {
        f.SignedByte("Base Note", Explain.InstBaseNote, null);
        f.SignedByte("Detune", Explain.InstDetune, "cents");
        f.SignedByte("Low Note", Explain.InstLowNote, null);
        f.SignedByte("High Note", Explain.InstHighNote, null);
        f.SignedByte("Low Velocity", Explain.InstLowVelocity, null);
        f.SignedByte("High Velocity", Explain.InstHighVelocity, null);
        f.SignedWord("Gain", Explain.InstGain, "dB");
        Loop(s, f, "Sustain Loop", Explain.InstSustain);
        Loop(s, f, "Release Loop", Explain.InstRelease);
        if (f.Remaining > 0)
        {
            f.Bytes("Extra Data", f.Remaining, Explain.ExtraData);
        }
    }

    private static void Loop(State s, Fields f, string name, string description)
    {
        long at = f.Position;
        ushort mode = f.ReadWord();
        short begin = unchecked((short)f.ReadWord());
        short end = unchecked((short)f.ReadWord());
        var loop = new Node(name, at, 6, NodeValue.Text(mode switch { 0 => "no looping", 1 => "forward looping", 2 => "forward and backward looping", _ => $"mode {mode}" })) { Description = description, Topic = Topics.Markers };
        loop.Add("Play Mode", at, 2, NodeValue.Text(mode switch { 0 => "0 (no looping)", 1 => "1 (forward looping)", 2 => "2 (forward and backward looping)", _ => mode.ToString(CultureInfo.InvariantCulture) }), Explain.LoopMode, Topics.Markers);
        loop.Add("Begin Marker", at + 2, 2, NodeValue.Integer(begin), Explain.LoopBegin, Topics.Markers);
        loop.Add("End Marker", at + 4, 2, NodeValue.Integer(end), Explain.LoopEnd, Topics.Markers);
        f.Node.Add(loop);
        if (mode != 0)
        {
            AddReference(s, begin, loop);
            AddReference(s, end, loop);
        }
    }

    private static void AddReference(State s, int marker, Node node)
    {
        if (s.MarkerReferences.Count < MaxMarkerReferences)
        {
            s.MarkerReferences.Add((marker, node));
        }
    }

    public static void Comments(State s, Fields f, Node chunk, long length)
    {
        int count = f.Word("Comment Count", Explain.ComtCount, "comments", Topics.Markers);
        Node? table = count > 0 ? f.Node.Add(new Node("Comments", f.Position, f.Remaining, NodeValue.Integer(count, "comments", "comment")) { Description = Explain.ComtTable, Topic = Topics.Markers }) : null;
        int listed = 0;
        for (int i = 0; i < count && f.Remaining >= 8; i++)
        {
            long at = f.Position;
            uint stamp = f.ReadDword();
            short marker = unchecked((short)f.ReadWord());
            int textLength = f.ReadWord();
            long total = textLength + (textLength % 2);
            if (total > f.Remaining)
            {
                f.Reader.Position = at;
                break;
            }

            string text = Fields.Clean(Encoding.Latin1.GetString(f.Reader.ReadBytes(textLength)));
            f.Reader.Skip(total - textLength);
            listed++;
            if (marker > 0)
            {
                AddReference(s, marker, table!);
            }

            if (i < MaxListed)
            {
                var comment = new Node($"Comment {i}", at, 8 + total, text) { Description = Explain.Comment, Topic = Topics.Markers };
                comment.Add("Time Stamp", at, 4, NodeValue.Text(MacTime(stamp)), Explain.ComtTimeStamp, Topics.Markers);
                comment.Add("Marker ID", at + 4, 2, marker == 0 ? NodeValue.Text("0 (no marker)") : NodeValue.Integer(marker), Explain.ComtMarker, Topics.Markers);
                comment.Add("Text Length", at + 6, 2, NodeValue.Integer(textLength, "bytes", "byte"), Explain.ComtLength, Topics.Markers);
                comment.Add("Text", at + 8, total, text, Explain.ComtText, Topics.Markers);
                table!.Add(comment);
            }
        }

        if (listed < count)
        {
            Node target = table ?? chunk;
            int stated = count;
            int held = listed;
            s.Findings.Add(Severity.Warning, "aiff.comment-count-mismatch", target, _ => $"The Comments Chunk states {Counts.Of(stated, "comment", "comments")}, but its data holds {held}.");
        }

        if (f.Remaining > 0)
        {
            f.Bytes("Extra Data", f.Remaining, Explain.ExtraData);
        }
    }

    // The seconds since January 1, 1904 that the Macintosh counts (sections 3.0 and 7.0), as a date.
    private static string MacTime(uint seconds)
    {
        if (seconds == 0)
        {
            return "0 (not set)";
        }

        DateTime time = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
        return time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
    }

    public static void Text(State s, Fields f, Node chunk, long length)
    {
        if (length > 0)
        {
            string text = f.Text("Text", length, Explain.TextData, Topics.Container);
            chunk.Value = text;
        }
    }

    public static void Application(State s, Fields f, Node chunk, long length)
    {
        string signature = f.FourCc("Application Signature", Explain.ApplSignature, Topics.Container);
        chunk.Value = signature;
        if (signature is "pdos" or "stoc" && f.Remaining > 0)
        {
            long at = f.Position;
            int count = f.Reader.ReadByte();
            if (count > f.Remaining)
            {
                throw new EndOfStreamException();
            }

            string app = Fields.Clean(Encoding.Latin1.GetString(f.Reader.ReadBytes(count)));
            f.Node.Add("Application Name", at, 1 + count, app, Explain.ApplName, Topics.Container);
            chunk.Value = $"{signature}, {app}";
            if (count % 2 == 0 && f.Remaining > 0)
            {
                long padAt = f.Position;
                f.Node.Add("Pad Byte", padAt, 1, NodeValue.Hex(f.Reader.ReadByte(), 2), Explain.StringPad);
            }
        }

        if (f.Remaining > 0)
        {
            f.Bytes("Data", f.Remaining, Explain.ApplData);
        }
    }

    public static void AudioRecording(State s, Fields f, Node chunk, long length)
    {
        f.Bytes("Channel Status Data", Math.Min(24, f.Remaining), Explain.AesdData);
        if (f.Remaining > 0)
        {
            f.Bytes("Extra Data", f.Remaining, Explain.ExtraData);
        }
    }

    public static void Midi(State s, Fields f, Node chunk, long length)
    {
        if (length > 0)
        {
            f.Bytes("MIDI Data", length, Explain.MidiData);
        }
    }

    public static void Id3(State s, Fields f, Node chunk, long length)
    {
        if (length == 0)
        {
            return;
        }

        // The tag starts at the start of the chunk's data and ends inside it; the tag parser says what it finds.
        Node? tag = s.Context.ParseTag("ID3v2", f.Reader.Slice(f.Reader.Start, length), chunk);
        if (tag is not null)
        {
            chunk.Value = NodeValue.Text("ID3v2 tag");
        }
    }
}
