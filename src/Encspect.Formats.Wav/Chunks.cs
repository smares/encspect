using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Wav;

// What the walker knows about a chunk by its ID: its node's name and description, the article, and the reader of its data.
internal sealed record ChunkSpec(string? Name, string Description, string? Topic, Walker.ChunkReader? Read)
{
    private static readonly Dictionary<string, ChunkSpec> Top = new(StringComparer.Ordinal)
    {
        ["fmt "] = new("Format Chunk", Explain.FormatChunk, Topics.WaveFormat, FormatChunk.Read),
        ["fact"] = new("Fact Chunk", Explain.FactChunk, Topics.WaveCompressed, Chunks.Fact),
        ["data"] = new("Data Chunk", Explain.DataChunk, Topics.PcmSamples, DataChunks.Read),
        ["ds64"] = new("Data Size 64 Chunk", Explain.Ds64Chunk, Topics.Rf64, Chunks.Ds64),
        ["bext"] = new("Broadcast Extension Chunk", Explain.BextChunk, Topics.Bext, Chunks.Bext),
        ["cue "] = new("Cue Points Chunk", Explain.CueChunk, Topics.CuePoints, Chunks.Cue),
        ["plst"] = new("Playlist Chunk", Explain.PlaylistChunk, Topics.CuePoints, Chunks.Playlist),
        ["LIST"] = new("List Chunk", Explain.ListChunk, Topics.RiffChunks, Chunks.List),
        ["smpl"] = new("Sample Chunk", Explain.SampleChunk, Topics.CuePoints, Chunks.Sample),
        ["inst"] = new("Instrument Chunk", Explain.InstrumentChunk, Topics.CuePoints, Chunks.Instrument),
        ["chna"] = new("Channel Allocation Chunk", Explain.ChnaChunk, Topics.Bext, Chunks.Chna),
        ["axml"] = new("XML Chunk", Explain.AxmlChunk, Topics.Bext, Chunks.Xml),
        ["iXML"] = new("iXML Chunk", Explain.IxmlChunk, Topics.Bext, Chunks.Xml),
        ["id3 "] = new("ID3v2 Chunk", Explain.Id3Chunk, Topics.Id3InChunks, Chunks.Id3),
        ["ID3 "] = new("ID3v2 Chunk", Explain.Id3Chunk, Topics.Id3InChunks, Chunks.Id3),
        ["JUNK"] = new("Junk Chunk", Explain.JunkChunk, Topics.RiffChunks, Chunks.Filler),
        ["PAD "] = new("Padding Chunk", Explain.PadChunk, Topics.RiffChunks, Chunks.Filler),
        ["levl"] = new("Peak Envelope Chunk", Explain.LevlChunk, Topics.Bext, null),
        ["link"] = new("Link Chunk", Explain.LinkChunk, Topics.Bext, null),
        ["dbmd"] = new("Dolby Metadata Chunk", Explain.DbmdChunk, Topics.Bext, null),
        ["qlty"] = new("Quality Chunk", Explain.QltyChunk, Topics.Bext, null),
        ["r64m"] = new("Marker Chunk", Explain.R64mChunk, Topics.Rf64, null),
        ["CSET"] = new("Character Set Chunk", Explain.CsetChunk, Topics.RiffChunks, Chunks.CharacterSet),
    };

    private static readonly Dictionary<string, ChunkSpec> Adtl = new(StringComparer.Ordinal)
    {
        ["labl"] = new("Label Chunk", Explain.LablChunk, Topics.CuePoints, Chunks.Label),
        ["note"] = new("Note Chunk", Explain.NoteChunk, Topics.CuePoints, Chunks.Label),
        ["ltxt"] = new("Labeled Text Chunk", Explain.LtxtChunk, Topics.CuePoints, Chunks.LabeledText),
        ["file"] = new("Embedded File Chunk", Explain.FileChunk, Topics.CuePoints, Chunks.EmbeddedFile),
    };

    private static readonly Dictionary<string, ChunkSpec> Wavl = new(StringComparer.Ordinal)
    {
        ["data"] = new("Data Chunk", Explain.DataChunk, Topics.PcmSamples, DataChunks.Read),
        ["slnt"] = new("Silence Chunk", Explain.SlntChunk, Topics.PcmSamples, Chunks.Silence),
    };

    // The INFO chunks the specification lists, by name (Multimedia Programming Interface and Data Specifications 1.0, "INFO List Chunk").
    public static readonly IReadOnlyDictionary<string, string> InfoNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["IARL"] = "Archival Location",
        ["IART"] = "Artist",
        ["ICMS"] = "Commissioned",
        ["ICMT"] = "Comments",
        ["ICOP"] = "Copyright",
        ["ICRD"] = "Creation Date",
        ["ICRP"] = "Cropped",
        ["IDIM"] = "Dimensions",
        ["IDPI"] = "Dots Per Inch",
        ["IENG"] = "Engineer",
        ["IGNR"] = "Genre",
        ["IKEY"] = "Keywords",
        ["ILGT"] = "Lightness",
        ["IMED"] = "Medium",
        ["INAM"] = "Name",
        ["IPLT"] = "Palette Setting",
        ["IPRD"] = "Product",
        ["ISBJ"] = "Subject",
        ["ISFT"] = "Software",
        ["ISHP"] = "Sharpness",
        ["ISRC"] = "Source",
        ["ISRF"] = "Source Form",
        ["ITCH"] = "Technician",
    };

    private static readonly Dictionary<string, ChunkSpec> Info = InfoNames.ToDictionary(n => n.Key, n => new ChunkSpec($"{n.Value} Chunk", Explain.InfoListed, Topics.RiffInfo, Chunks.InfoText), StringComparer.Ordinal);

    private static readonly ChunkSpec UnlistedInfo = new(null, Explain.InfoUnlisted, Topics.RiffInfo, Chunks.InfoText);

    public static ChunkSpec Of(string id, string? listType)
    {
        if (listType is not null && id == "LIST")
        {
            return Top[id];
        }

        switch (listType)
        {
            case "INFO":
                return Info.TryGetValue(id, out ChunkSpec? info) ? info : UnlistedInfo;
            case "adtl" when Adtl.TryGetValue(id, out ChunkSpec? spec):
                return spec;
            case "wavl" when Wavl.TryGetValue(id, out ChunkSpec? silence):
                return silence;
            case not null:
                return new ChunkSpec(null, Explain.UnknownChunk, Topics.RiffChunks, null);
        }

        return Top.TryGetValue(id, out ChunkSpec? known) ? known : new ChunkSpec(null, Explain.UnknownChunk, Topics.RiffChunks, null);
    }
}

// The readers of the chunks that are not the format or the data.
internal static class Chunks
{
    // At most this many records of a table are listed as nodes, whatever count the chunk states.
    public const int MaxListed = 1024;

    // The cue point names kept to find a name twice and a label that names no cue point; past them those two checks are not made.
    public const int MaxCueNames = 65_536;

    public static void Fact(State s, Fields f, Node chunk, long length)
    {
        uint samples = f.Dword("Sample Length", Explain.FactSamples, v => NodeValue.Integer(v, "samples"), Topics.WaveCompressed);
        ulong value = samples;
        if (s.IsRf64 && samples == uint.MaxValue && s.Ds64 is { } ds64)
        {
            value = ds64.SampleCount;
            chunk.Children[^1].Value = NodeValue.Integer((long)Math.Min(value, long.MaxValue), "samples");
        }

        if (s.FactNode is null)
        {
            s.FactNode = chunk.Children[^1];
            s.FactSamples64 = value;
        }

        chunk.Value = NodeValue.Integer((long)Math.Min(value, long.MaxValue), "samples");
        if (f.Remaining > 0)
        {
            f.Bytes("Further Fields", f.Remaining, Explain.FactFurther);
        }
    }

    public static void Ds64(State s, Fields f, Node chunk, long length)
    {
        ulong riff = f.Qword("RIFF Size", Explain.Ds64RiffSize, "bytes", Topics.Rf64);
        ulong data = f.Qword("Data Size", Explain.Ds64DataSize, "bytes", Topics.Rf64);
        ulong samples = f.Qword("Sample Count", Explain.Ds64SampleCount, "samples", Topics.Rf64);

        // The three sizes are what the RF64 header and the data chunk need, so the first ds64 chunk that holds them counts even if it ends there.
        var table = new Dictionary<string, ulong>(StringComparer.Ordinal);
        if (s.Ds64 is null)
        {
            s.Ds64 = new Ds64(riff, data, samples, table);
        }

        if (f.Remaining < 4)
        {
            long held = length;
            s.Findings.Add(Severity.Warning, "wav.ds64-too-short", chunk, _ =>
                $"The ds64 chunk holds {Counts.Of(held, "byte", "bytes")}, but Tech 3306-2009 gives it the three 64-bit sizes and a 32-bit table length, 28 bytes at least; the table is taken to list no chunk.");
            return;
        }

        uint count = f.Dword("Table Length", Explain.Ds64TableLength, "entries", Topics.Rf64);
        Node? entries = null;
        long fit = f.Remaining / 12;
        long read = Math.Min(count, fit);
        if (count > 0)
        {
            entries = f.Node.Add(new Node("Table", f.Position, read * 12, NodeValue.Integer(count, "entries", "entry")) { Description = Explain.Ds64Table, Topic = Topics.Rf64 });
        }

        // Only a top-level chunk can use an entry, by its ID; an entry whose ID is no ID, or past the first MaxListed, can name no chunk the
        // walk reads, so a table of millions of entries costs no more than one of a thousand.
        for (long i = 0; i < read; i++)
        {
            long at = f.Position;
            byte[] idBytes = f.Reader.ReadBytes(4);
            uint low = f.ReadDword();
            uint high = f.ReadDword();
            ulong size = ((ulong)high << 32) | low;
            bool isId = Walker.IsFourCc(idBytes);
            if (i < MaxListed)
            {
                string id = System.Text.Encoding.Latin1.GetString(idBytes);
                entries!.Add(new Node($"Entry {i}", at, 12, $"{Fields.Clean(id)}, {size.ToString(CultureInfo.InvariantCulture)} bytes") { Description = Explain.Ds64Entry, Topic = Topics.Rf64 });
                if (isId)
                {
                    table.TryAdd(id, size);
                }
            }
        }

        if (count > fit)
        {
            Node target = entries ?? chunk;
            uint stated = count;
            long held = fit;
            s.Findings.Add(Severity.Warning, "wav.ds64-table-length", target, _ =>
                $"The ds64 chunk states {Counts.Of(stated, "table entry", "table entries")}, but its data holds room for {held}.");
        }

        if (f.Remaining > 0)
        {
            f.Bytes("Extra Data", f.Remaining, Explain.ExtraData);
        }
    }

    public static void Bext(State s, Fields f, Node chunk, long length)
    {
        string description = f.Text("Description", 256, Explain.BextDescription, Topics.Bext);
        f.Text("Originator", 32, Explain.BextOriginator, Topics.Bext);
        f.Text("Originator Reference", 32, Explain.BextOriginatorReference, Topics.Bext);
        f.Text("Origination Date", 10, Explain.BextDate, Topics.Bext);
        f.Text("Origination Time", 8, Explain.BextTime, Topics.Bext);
        long referenceAt = f.Position;
        uint low = f.ReadDword();
        uint high = f.ReadDword();
        ulong reference = ((ulong)high << 32) | low;
        f.Node.Add("Time Reference", referenceAt, 8, reference <= long.MaxValue ? NodeValue.Integer((long)reference, "samples") : NodeValue.Hex(reference, 16), Explain.BextTimeReference, Topics.Bext);
        int version = f.Word("Version", Explain.BextVersion, (string?)null, Topics.Bext);
        long umidAt = f.Position;
        byte[] umid = f.Reader.ReadBytes(64);
        Node umidNode = f.Node.Add("UMID", umidAt, 64, umid.Any(x => x != 0) ? Convert.ToHexStringLower(umid) : "not set", Explain.BextUmid, Topics.Bext);

        // Version 1 reserves 190 bytes, the room of the loudness values and the 180 below, and version 0 the 254 that include the UMID (Tech 3285
        // section 1.1); the finding points to the first of them that is not zero.
        Node? used = version < 1 && umid.Any(x => x != 0) ? umidNode : null;
        used ??= Loudness(f, "Integrated Loudness", Explain.BextLoudnessValue, "LUFS", false, version);
        used ??= Loudness(f, "Loudness Range", Explain.BextLoudnessRange, "LU", true, version);
        used ??= Loudness(f, "Maximum True Peak Level", Explain.BextMaxTruePeak, "dBTP", false, version);
        used ??= Loudness(f, "Maximum Momentary Loudness", Explain.BextMaxMomentary, "LUFS", false, version);
        used ??= Loudness(f, "Maximum Short-Term Loudness", Explain.BextMaxShortTerm, "LUFS", false, version);
        long reservedAt = f.Position;
        byte[] reserved = f.Reader.ReadBytes(180);
        Node reservedNode = f.Node.Add("Reserved", reservedAt, 180, NodeValue.Integer(180, "bytes"), Explain.BextReserved, Topics.Bext);
        used ??= reserved.Any(x => x != 0) ? reservedNode : null;
        if (version <= 2 && used is not null)
        {
            int reservedBytes = version switch { 0 => 254, 1 => 190, _ => 180 };
            int stated = version;
            s.Findings.Add(Severity.Info, "wav.bext-reserved-not-zero", used, _ =>
                $"The {reservedBytes} bytes of a version {stated} bext chunk that are reserved for extension should be zero (EBU Tech 3285, section 1.1 and the Reserved field), but not all are.");
        }

        if (version > 2)
        {
            int stated = version;
            s.Findings.Add(Severity.Info, "wav.bext-version-unknown", f.Node.Children.First(c => c.Name == "Version"), _ => $"The bext chunk is of version {stated}, which the analyzer does not know; versions 0 to 2 are specified.");
        }

        if (f.Remaining > 0)
        {
            f.Text("Coding History", f.Remaining, Explain.BextCodingHistory, Topics.Bext);
        }

        chunk.Value = description.Length == 0 ? NodeValue.Integer(length, "bytes", "byte") : description;
    }

    // A loudness field of the bext chunk (EBU Tech 3285 section 2.4): the value times 100 in a 16-bit integer, 0x7FFF where unused, and
    // ignored when outside the valid range. Returns the node when the bytes are reserved in this version (before version 2) and not zero.
    private static Node? Loudness(Fields f, string name, string description, string unit, bool isRange, int version)
    {
        long at = f.Position;
        short raw = unchecked((short)f.ReadWord());
        NodeValue value;
        if (version < 2)
        {
            value = raw == 0 ? NodeValue.Text("not set") : NodeValue.Integer(raw);
        }
        else if (raw == 0x7FFF)
        {
            value = NodeValue.Text("not used");
        }
        else if (isRange ? raw is < 0 or > 9999 : raw is < -9999 or > 9999)
        {
            value = NodeValue.Text($"{raw} (out of range, ignored)");
        }
        else
        {
            value = NodeValue.Decimal(raw / 100.0, 2, unit);
        }

        Node field = f.Node.Add(name, at, 2, value, description, Topics.Bext);
        return version < 2 && raw != 0 ? field : null;
    }

    public static void Cue(State s, Fields f, Node chunk, long length)
    {
        uint count = f.Dword("Cue Point Count", Explain.CueCount, "cue points", Topics.CuePoints);
        long fit = f.Remaining / 24;
        var names = new HashSet<uint>();
        s.CueIdentifiers ??= [];
        bool isDuplicate = false;
        Node? table = count > 0 ? f.Node.Add(new Node("Cue Points", f.Position, Math.Min(count, fit) * 24, NodeValue.Integer(count, "cue points", "cue point")) { Description = Explain.CueTable, Topic = Topics.CuePoints }) : null;
        for (long i = 0; i < Math.Min(count, fit); i++)
        {
            long at = f.Position;
            uint name = f.ReadDword();
            uint position = f.ReadDword();
            string fccChunk = System.Text.Encoding.Latin1.GetString(f.Reader.ReadBytes(4));
            uint chunkStart = f.ReadDword();
            uint blockStart = f.ReadDword();
            uint sampleOffset = f.ReadDword();
            if (names.Count < MaxCueNames)
            {
                isDuplicate |= !names.Add(name);
            }
            else
            {
                s.IsCueNamesIncomplete = true;
            }

            if (s.CueIdentifiers.Count < MaxCueNames)
            {
                s.CueIdentifiers.Add(name);
            }
            else
            {
                s.IsCueNamesIncomplete = true;
            }

            if (i < MaxListed)
            {
                var cue = new Node($"Cue Point {i}", at, 24, NodeValue.Integer(sampleOffset, "samples")) { Description = Explain.CuePoint, Topic = Topics.CuePoints };
                cue.Add("Name", at, 4, NodeValue.Integer(name), Explain.CueName, Topics.CuePoints);
                cue.Add("Position", at + 4, 4, NodeValue.Integer(position, "samples"), Explain.CuePosition, Topics.CuePoints);
                cue.Add("Chunk ID", at + 8, 4, fccChunk, Explain.CueChunkId, Topics.CuePoints);
                cue.Add("Chunk Start", at + 12, 4, NodeValue.Integer(chunkStart, "bytes", "byte"), Explain.CueChunkStart, Topics.CuePoints);
                cue.Add("Block Start", at + 16, 4, NodeValue.Integer(blockStart, "bytes", "byte"), Explain.CueBlockStart, Topics.CuePoints);
                cue.Add("Sample Offset", at + 20, 4, NodeValue.Integer(sampleOffset, "samples"), Explain.CueSampleOffset, Topics.CuePoints);
                table!.Add(cue);
            }
        }

        if (count > fit)
        {
            Node target = table ?? chunk;
            uint stated = count;
            long held = fit;
            s.Findings.Add(Severity.Warning, "wav.cue-count-mismatch", target, _ =>
                $"The cue chunk states {Counts.Of(stated, "cue point", "cue points")}, but its data holds room for {held}.");
        }

        if (isDuplicate)
        {
            s.Findings.Add(Severity.Warning, "wav.cue-name-repeated", table ?? chunk, _ => "Two cue points of the cue chunk have the same name, which the specification wants unique.");
        }

        if (f.Remaining > 0)
        {
            f.Bytes("Extra Data", f.Remaining, Explain.ExtraData);
        }
    }

    public static void Playlist(State s, Fields f, Node chunk, long length)
    {
        uint count = f.Dword("Segment Count", Explain.PlaylistCount, "segments", Topics.CuePoints);
        long fit = f.Remaining / 12;
        Node? table = count > 0 ? f.Node.Add(new Node("Segments", f.Position, Math.Min(count, fit) * 12, NodeValue.Integer(count, "segments", "segment")) { Description = Explain.PlaylistTable, Topic = Topics.CuePoints }) : null;
        for (long i = 0; i < Math.Min(count, fit); i++)
        {
            long at = f.Position;
            uint name = f.ReadDword();
            uint segmentLength = f.ReadDword();
            uint loops = f.ReadDword();
            if (i < MaxListed)
            {
                var segment = new Node($"Segment {i}", at, 12, NodeValue.Integer(segmentLength, "samples")) { Description = Explain.PlaylistSegment, Topic = Topics.CuePoints };
                segment.Add("Cue Point Name", at, 4, NodeValue.Integer(name), Explain.PlaylistName, Topics.CuePoints);
                segment.Add("Length", at + 4, 4, NodeValue.Integer(segmentLength, "samples"), Explain.PlaylistLength, Topics.CuePoints);
                segment.Add("Loops", at + 8, 4, NodeValue.Integer(loops, "times"), Explain.PlaylistLoops, Topics.CuePoints);
                table!.Add(segment);
            }
        }

        if (count > fit)
        {
            Node target = table ?? chunk;
            uint stated = count;
            long held = fit;
            s.Findings.Add(Severity.Warning, "wav.playlist-count-mismatch", target, _ =>
                $"The playlist chunk states {Counts.Of(stated, "segment", "segments")}, but its data holds room for {held}.");
        }

        if (f.Remaining > 0)
        {
            f.Bytes("Extra Data", f.Remaining, Explain.ExtraData);
        }
    }

    public static void Sample(State s, Fields f, Node chunk, long length)
    {
        f.Dword("Manufacturer", Explain.SmplManufacturer, v => NodeValue.Hex(v, 8), Topics.CuePoints);
        f.Dword("Product", Explain.SmplProduct, (string?)null, Topics.CuePoints);
        f.Dword("Sample Period", Explain.SmplPeriod, "ns", Topics.CuePoints);
        f.Dword("MIDI Unity Note", Explain.SmplUnityNote, (string?)null, Topics.CuePoints);
        f.Dword("MIDI Pitch Fraction", Explain.SmplPitchFraction, v => NodeValue.Hex(v, 8), Topics.CuePoints);
        f.Dword("SMPTE Format", Explain.SmplSmpteFormat, v => NodeValue.Text(v switch { 0 => "0 (no offset)", 24 => "24 frames per second", 25 => "25 frames per second", 29 => "29 (30 frames per second with frame dropping)", 30 => "30 frames per second", _ => v.ToString(CultureInfo.InvariantCulture) }), Topics.CuePoints);
        f.Dword("SMPTE Offset", Explain.SmplSmpteOffset, v => NodeValue.Hex(v, 8), Topics.CuePoints);
        uint loops = f.Dword("Sample Loop Count", Explain.SmplLoopCount, "loops", Topics.CuePoints);
        uint samplerBytes = f.Dword("Sampler Data Size", Explain.SmplDataSize, "bytes", Topics.CuePoints);
        long fit = f.Remaining / 24;
        Node? table = loops > 0 ? f.Node.Add(new Node("Sample Loops", f.Position, Math.Min(loops, fit) * 24, NodeValue.Integer(loops, "loops", "loop")) { Description = Explain.SmplLoops, Topic = Topics.CuePoints }) : null;
        for (long i = 0; i < Math.Min(loops, fit); i++)
        {
            long at = f.Position;
            uint id = f.ReadDword();
            uint type = f.ReadDword();
            uint start = f.ReadDword();
            uint end = f.ReadDword();
            uint fraction = f.ReadDword();
            uint playCount = f.ReadDword();
            if (i < MaxListed)
            {
                var loop = new Node($"Sample Loop {i}", at, 24, NodeValue.Text(type switch { 0 => "forward", 1 => "alternating", 2 => "backward", _ => $"type {type}" })) { Description = Explain.SmplLoop, Topic = Topics.CuePoints };
                loop.Add("Identifier", at, 4, NodeValue.Integer(id), Explain.SmplLoopId, Topics.CuePoints);
                loop.Add("Type", at + 4, 4, NodeValue.Text(type switch { 0 => "0 (forward)", 1 => "1 (alternating forward and backward)", 2 => "2 (backward)", >= 3 and <= 31 => $"{type} (reserved)", _ => $"{type} (sampler specific)" }), Explain.SmplLoopType, Topics.CuePoints);
                loop.Add("Start", at + 8, 4, NodeValue.Integer(start, "samples"), Explain.SmplLoopStart, Topics.CuePoints);
                loop.Add("End", at + 12, 4, NodeValue.Integer(end, "samples"), Explain.SmplLoopEnd, Topics.CuePoints);
                loop.Add("Fraction", at + 16, 4, NodeValue.Hex(fraction, 8), Explain.SmplLoopFraction, Topics.CuePoints);
                loop.Add("Play Count", at + 20, 4, playCount == 0 ? NodeValue.Text("0 (infinite sustain loop)") : NodeValue.Integer(playCount, "times"), Explain.SmplLoopPlayCount, Topics.CuePoints);
                table!.Add(loop);
            }
        }

        if (loops > fit || samplerBytes > f.Remaining)
        {
            Node target = table ?? chunk;
            uint stated = loops;
            s.Findings.Add(Severity.Warning, "wav.sample-chunk-size", target, _ =>
                $"The sample chunk states {Counts.Of(stated, "loop", "loops")} and {Counts.Of(samplerBytes, "byte", "bytes")} of sampler data, which its data does not hold.");
        }

        if (f.Remaining > 0)
        {
            f.Bytes("Sampler Data", f.Remaining, Explain.SmplSamplerData, Topics.CuePoints);
        }
    }

    public static void Instrument(State s, Fields f, Node chunk, long length)
    {
        f.Byte("Unshifted Note", Explain.InstNote, null, Topics.CuePoints);
        f.SignedByte("Fine Tune", Explain.InstFineTune, "cents");
        f.SignedByte("Gain", Explain.InstGain, "dB");
        f.Byte("Low Note", Explain.InstLowNote, null, Topics.CuePoints);
        f.Byte("High Note", Explain.InstHighNote, null, Topics.CuePoints);
        f.Byte("Low Velocity", Explain.InstLowVelocity, null, Topics.CuePoints);
        f.Byte("High Velocity", Explain.InstHighVelocity, null, Topics.CuePoints);
        if (f.Remaining > 0)
        {
            f.Bytes("Extra Data", f.Remaining, Explain.ExtraData);
        }
    }

    public static void Chna(State s, Fields f, Node chunk, long length)
    {
        f.Word("Track Count", Explain.ChnaTracks, "tracks", Topics.Bext);
        int uids = f.Word("UID Count", Explain.ChnaUids, "UIDs", Topics.Bext);
        long fit = f.Remaining / 40;
        Node? table = fit > 0 ? f.Node.Add(new Node("Track UIDs", f.Position, fit * 40, NodeValue.Integer(fit, "entries", "entry")) { Description = Explain.ChnaTable, Topic = Topics.Bext }) : null;
        for (long i = 0; i < fit; i++)
        {
            long at = f.Position;
            int index = f.ReadWord();
            string uid = AsciiField(f.Reader.ReadBytes(12));
            string trackRef = AsciiField(f.Reader.ReadBytes(14));
            string packRef = AsciiField(f.Reader.ReadBytes(11));
            byte pad = f.Reader.ReadByte();
            if (i < MaxListed)
            {
                var entry = new Node($"Track UID {i}", at, 40, index == 0 && uid.Length == 0 ? "unused" : uid) { Description = Explain.ChnaEntry, Topic = Topics.Bext };
                entry.Add("Track Index", at, 2, NodeValue.Integer(index), Explain.ChnaIndex, Topics.Bext);
                entry.Add("UID", at + 2, 12, uid, Explain.ChnaUid, Topics.Bext);
                entry.Add("Track Format Reference", at + 14, 14, trackRef, Explain.ChnaTrackRef, Topics.Bext);
                entry.Add("Pack Format Reference", at + 28, 11, packRef, Explain.ChnaPackRef, Topics.Bext);
                entry.Add("Pad", at + 39, 1, NodeValue.Hex(pad, 2), Explain.ChnaPad);
                table!.Add(entry);
            }
        }

        if (f.Remaining > 0 || uids > fit)
        {
            int stated = uids;
            long held = fit;
            s.Findings.Add(Severity.Warning, "wav.chna-size", table ?? chunk, _ =>
                $"The chna chunk's data should be 4 bytes and 40 for each of its ID structures, at least as many as the {Counts.Of(stated, "UID", "UIDs")} it states; it holds {Counts.Of(held, "structure", "structures")}.");
            if (f.Remaining > 0)
            {
                f.Bytes("Extra Data", f.Remaining, Explain.ExtraData);
            }
        }
    }

    private static string AsciiField(byte[] bytes)
    {
        int end = Array.IndexOf(bytes, (byte)0);
        return Fields.Clean(System.Text.Encoding.Latin1.GetString(bytes, 0, end < 0 ? bytes.Length : end));
    }

    public static void Xml(State s, Fields f, Node chunk, long length)
    {
        if (length > 0)
        {
            f.Text("XML Text", length, Explain.XmlText, Topics.Bext);
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

    public static void Filler(State s, Fields f, Node chunk, long length)
    {
        if (length > 0)
        {
            f.Bytes("Filler", length, Explain.Filler);
        }
    }

    public static void CharacterSet(State s, Fields f, Node chunk, long length)
    {
        f.Word("Code Page", Explain.CsetCodePage, v => v == 0 ? NodeValue.Text("0 (ISO 8859-1)") : NodeValue.Integer(v), Topics.RiffChunks);
        f.Word("Country Code", Explain.CsetCountry, (string?)null, Topics.RiffChunks);
        f.Word("Language Code", Explain.CsetLanguage, (string?)null, Topics.RiffChunks);
        f.Word("Dialect", Explain.CsetDialect, (string?)null, Topics.RiffChunks);
        if (f.Remaining > 0)
        {
            f.Bytes("Extra Data", f.Remaining, Explain.ExtraData);
        }
    }

    public static void List(State s, Fields f, Node chunk, long length)
    {
        long bodyStart = f.Reader.Start;
        string listType = f.FourCc("List Type", Explain.ListType, Topics.RiffChunks);
        chunk.Value = listType;
        long end = bodyStart + length;
        if (s.Nesting >= Walker.MaxNesting)
        {
            s.Findings.Add(Severity.Warning, "wav.list-nesting", chunk, _ => $"LIST chunks nest more than {Walker.MaxNesting} deep; the inner ones are not read.");
            f.Bytes("Chunks", f.Remaining, Explain.ListContents);
            return;
        }

        s.Nesting++;
        string? outer = s.ListType;
        Node? outerList = s.CurrentList;
        s.ListType = listType;
        s.CurrentList = chunk;
        try
        {
            long stop = Walker.Chunks(s, chunk, bodyStart + 4, end, listType, s.Nesting);
            if (stop < end)
            {
                long extra = end - stop;
                Node bytes = chunk.Add(new Node("Extra Data", stop, extra, NodeValue.Integer(extra, "bytes", "byte")) { Description = Explain.ExtraData });
                s.Findings.Add(Severity.Warning, "wav.list-extra-bytes", bytes, _ => $"The list holds {Counts.Of(extra, "byte", "bytes")} after its last chunk, too few for another.");
            }
        }
        finally
        {
            s.Nesting--;
            s.ListType = outer;
            s.CurrentList = outerList;
        }
    }

    public static void InfoText(State s, Fields f, Node chunk, long length)
    {
        if (length == 0)
        {
            return;
        }

        string value = f.ReadText(length);
        Node text = chunk.Add(new Node("Text", f.Reader.Start, length, value) { Description = Explain.InfoText, Topic = Topics.RiffInfo });
        chunk.Value = value;
        if (chunk.Name == "Software Chunk" && value.Trim().Length > 0)
        {
            s.Context.AddEncoderClaim(new EncoderClaim(value, text, "LIST INFO chunk ISFT (Software)", ClaimRole.Writer));
        }
    }

    public static void Label(State s, Fields f, Node chunk, long length)
    {
        uint name = f.Dword("Cue Point Name", Explain.LabelName, (string?)null, Topics.CuePoints);
        s.LabelReferences.Add((name, chunk));
        if (f.Remaining > 0)
        {
            string text = f.Text("Text", f.Remaining, Explain.LabelText, Topics.CuePoints);
            chunk.Value = text;
        }
    }

    public static void LabeledText(State s, Fields f, Node chunk, long length)
    {
        uint name = f.Dword("Cue Point Name", Explain.LabelName, (string?)null, Topics.CuePoints);
        s.LabelReferences.Add((name, chunk));
        f.Dword("Sample Length", Explain.LtxtLength, "samples", Topics.CuePoints);
        f.Dword("Purpose", Explain.LtxtPurpose, v => NodeValue.Text(PurposeText(v, s.IsBigEndian)), Topics.CuePoints);
        f.Word("Country", Explain.LtxtCountry, (string?)null, Topics.CuePoints);
        f.Word("Language", Explain.LtxtLanguage, (string?)null, Topics.CuePoints);
        f.Word("Dialect", Explain.LtxtDialect, (string?)null, Topics.CuePoints);
        f.Word("Code Page", Explain.LtxtCodePage, (string?)null, Topics.CuePoints);
        if (f.Remaining > 0)
        {
            string text = f.Text("Text", f.Remaining, Explain.LabelText, Topics.CuePoints);
            chunk.Value = text;
        }
    }

    private static string PurposeText(uint value, bool isBigEndian)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (isBigEndian)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        }
        else
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        }

        return Walker.IsFourCc(bytes) ? System.Text.Encoding.Latin1.GetString(bytes) : $"0x{value:X8}";
    }

    public static void EmbeddedFile(State s, Fields f, Node chunk, long length)
    {
        uint name = f.Dword("Cue Point Name", Explain.LabelName, (string?)null, Topics.CuePoints);
        s.LabelReferences.Add((name, chunk));
        f.Dword("Media Type", Explain.FileMediaType, v => NodeValue.Text(PurposeText(v, s.IsBigEndian)), Topics.CuePoints);
        if (f.Remaining > 0)
        {
            f.Bytes("File Data", f.Remaining, Explain.FileData, Topics.CuePoints);
        }
    }

    public static void Silence(State s, Fields f, Node chunk, long length)
    {
        uint samples = f.Dword("Sample Count", Explain.SlntSamples, "samples", Topics.PcmSamples);
        s.SilentSamples += samples;
        chunk.Value = NodeValue.Integer(samples, "samples");
    }
}
