using Encspect.Core;

namespace Encspect.Formats.Mp4;

// The boxes that describe the movie and its tracks (ISO/IEC 14496-12 8.2 to 8.7, 8.6.5 and 8.6.6).
public sealed partial class Mp4Parser
{
    // Tables list at most this many entries as nodes; the rest is one node. Every sample still gets
    // its node from the codec that reads it.
    private const int MaxListedEntries = 1000;

    private static bool ParseKnown(State state, Node node, BoxHeader header, BoxFields f, int depth)
    {
        switch (header.Type)
        {
            case "mvhd":
                MovieHeader(state, node, f);
                return true;
            case "tkhd":
                TrackHeader(state, node, f);
                return true;
            case "mdhd":
                MediaHeader(state, node, f);
                return true;
            case "hdlr":
                Handler(state, node, f);
                return true;
            case "smhd":
                FullBox(node, f);
                f.Fixed(node, "Balance", 2, 8, Explain.Balance);
                f.U16(node, "Reserved", Explain.Reserved);
                return true;
            case "dref":
                FullBox(node, f);
                f.U32(node, "Entries", Explain.DataEntries);
                ReadChildren(state, node, f.Position, f.End, depth + 1);
                return true;
            case "url " or "urn ":
                uint flags = FullBox(node, f).Flags;
                node.Value = (flags & 1) != 0 ? "media data in this file" : "media data elsewhere";
                if (f.Remaining > 0)
                {
                    f.CString(node, header.Type == "urn " ? "Name" : "Location", header.Type == "urn " ? Explain.DataName : Explain.DataLocation);
                }

                if (header.Type == "urn " && f.Remaining > 0)
                {
                    f.CString(node, "Location", Explain.DataLocation);
                }

                return true;
            case "elst":
                EditList(state, node, f);
                return true;
            case "stsd":
                SampleDescription(state, node, header, f, depth);
                return true;
            case "esds":
                ElementaryStreamDescriptor(state, node, f);
                return true;
            case "sbtd":
                SourceBitDepth(state, node, f);
                return true;
            case "dac3" or "dec3" or "ddts":
                CodecSpecificBox(state, node, f);
                return true;
            case "btrt":
                f.U32(node, "Buffer Size", Explain.BtrtBufferSize, unit: "bytes");
                f.U32(node, "Maximum Bitrate", Explain.BtrtMaxBitrate, unit: "bit/s");
                f.U32(node, "Average Bitrate", Explain.BtrtAverageBitrate, unit: "bit/s");
                return true;
            case "chan":
                QuickTimeChannelLayout(state, node, f);
                return true;
            case "chnl":
                IsoChannelLayout(state, node, f);
                return true;
            case "frma":
                node.Value = f.FourCc(node, "Format", Explain.OriginalFormat);
                return true;
            case "stts":
                TimeToSample(state, node, f);
                return true;
            case "ctts":
                CompositionOffsets(node, f);
                return true;
            case "stsc":
                SampleToChunk(state, node, f);
                return true;
            case "stsz":
                SampleSizes(state, node, f);
                return true;
            case "stz2":
                CompactSampleSizes(state, node, f);
                return true;
            case "stco" or "co64":
                ChunkOffsets(state, node, f, header.Type == "co64" ? 8 : 4);
                return true;
            case "stss":
                SyncSamples(node, f);
                return true;
            case "sgpd":
                SampleGroupDescription(state, node, f);
                return true;
            case "sbgp":
                SampleToGroup(node, f);
                return true;
            case "meta":
                Meta(state, node, header, f, depth);
                return true;
            case "keys":
                Keys(state, node, f);
                return true;
            case "ilst":
                ItemList(state, node, header, f, depth);
                return true;
            case "ID32":
                Id32(state, node, f);
                return true;
            case "chpl":
                NeroChapters(node, f);
                return true;
            case "mehd" or "trex" or "mfhd" or "tfhd" or "tfdt" or "trun" or "sidx" or "tfra" or "mfro":
                FragmentBox(state, node, header, f);
                return true;
            default:
                if (header.Type.StartsWith('©') && node.Name == header.Type)
                {
                    return QuickTimeText(state, node, header, f);
                }

                return false;
        }
    }

    // A box afconvert's M4A files carry in the audio sample entry: four bytes that are zero in every file seen, then the
    // bit depth of the source as text ("I16": 16-bit integers), which afinfo prints as "source bit depth". Not in ISO/IEC
    // 14496-12 or 14496-14, so it is read as the files show it (macOS 15, afconvert).
    private static void SourceBitDepth(State state, Node node, BoxFields f)
    {
        FullBox(node, f);
        if (f.Remaining > 0 && f.Remaining <= 16)
        {
            node.Value = Latin1OrUtf8(f.Bytes(node, "Source Bit Depth", f.Remaining, Explain.SourceBitDepth, b => Latin1OrUtf8(b)));
        }

        if (state.CurrentEntry is { } entry)
        {
            entry.SourceBitDepthNode ??= node;
        }
    }

    // ETSI TS 102 366 F.4 and F.6: AC-3's and E-AC-3's configuration, which the codec parser reads
    // field by field (as the AudioSpecificConfig of an esds box).
    private static void CodecSpecificBox(State state, Node node, BoxFields f)
    {
        long start = f.Position;
        byte[] data = f.TakeBytes(f.Remaining);
        node.Value = NodeValue.Integer(data.Length, "bytes");
        if (state.CurrentEntry is { DecoderSpecificInfo: null } entry)
        {
            entry.DecoderSpecificInfo = new MappedBytes(data, start);
            entry.DecoderSpecificInfoNode = node;
        }
        else if (data.Length > 0)
        {
            node.Add(new Node("Data", start, data.Length, NodeValue.Integer(data.Length, "bytes")) { Description = Explain.UnreadData });
        }
    }

    // ISO/IEC 14496-12 4.2: version and flags open a full box.
    private static (int Version, uint Flags) FullBox(Node node, BoxFields f)
    {
        int version = f.U8(node, "Version", Explain.Version);
        uint flags = (uint)f.Field(node, "Flags", 3, Explain.Flags, v => NodeValue.Hex((long)v, 6));
        return (version, flags);
    }

    // 8.2.2.
    private static void MovieHeader(State state, Node node, BoxFields f)
    {
        int size = FullBox(node, f).Version == 1 ? 8 : 4;
        f.Time(node, "Creation Time", size, Explain.CreationTime);
        f.Time(node, "Modification Time", size, Explain.ModificationTime);
        uint timescale = f.U32(node, "Timescale", Explain.MovieTimescale, unit: "units per second");
        ulong duration = f.Duration(node, "Duration", size, timescale, Explain.MovieDuration);
        f.Fixed(node, "Rate", 4, 16, Explain.Rate);
        f.Fixed(node, "Volume", 2, 8, Explain.MovieVolume);
        f.Skip(10);
        f.Bytes(node, "Matrix", 36, Explain.Matrix);
        f.Skip(24);
        f.U32(node, "Next Track ID", Explain.NextTrackId);
        if (state.MovieHeader is null)
        {
            state.MovieTimescale = timescale;
            state.MovieDuration = duration;
            state.MovieHeader = node;
        }
    }

    // 8.3.2.
    private static void TrackHeader(State state, Node node, BoxFields f)
    {
        (int version, uint flags) = FullBox(node, f);
        int size = version == 1 ? 8 : 4;
        node.Add(Node.FromBits("IsEnabled", node.Children[^1].Offset + 2, 7, 8, NodeValue.Boolean((flags & 1) != 0), description: Explain.IsEnabled));
        f.Time(node, "Creation Time", size, Explain.CreationTime);
        f.Time(node, "Modification Time", size, Explain.ModificationTime);
        uint id = f.U32(node, "Track ID", Explain.TrackId);
        f.Skip(4);
        ulong duration = f.Duration(node, "Duration", size, state.MovieTimescale, Explain.TrackDuration);
        f.Skip(8);
        f.U16(node, "Layer", Explain.Layer);
        f.U16(node, "Alternate Group", Explain.AlternateGroup);
        f.Fixed(node, "Volume", 2, 8, Explain.TrackVolume);
        f.Skip(2);
        f.Bytes(node, "Matrix", 36, Explain.Matrix);
        f.Fixed(node, "Width", 4, 16, Explain.Width);
        f.Fixed(node, "Height", 4, 16, Explain.Height);
        if (state.Track is { TrackHeader: null } track)
        {
            track.Id = id;
            track.TrackDuration = duration;
            track.TrackHeader = node;
            node.Value = $"Track Header, track {id}";
        }
    }

    // 8.4.2: the media's timescale and duration, and its language as three letters packed into
    // 5 bits each, offset by 0x60 (ISO 639-2/T).
    private static void MediaHeader(State state, Node node, BoxFields f)
    {
        int size = FullBox(node, f).Version == 1 ? 8 : 4;
        f.Time(node, "Creation Time", size, Explain.CreationTime);
        f.Time(node, "Modification Time", size, Explain.ModificationTime);
        uint timescale = f.U32(node, "Timescale", Explain.MediaTimescale, unit: "units per second");
        ulong duration = f.Duration(node, "Duration", size, timescale, Explain.MediaDuration);
        // QuickTime takes a value below 0x400 for a Macintosh language code (0 is English).
        f.Field(node, "Language", 2, Explain.Language, v => v < 0x400 ? $"{v} (Macintosh language code)"
            : new string([(char)(((v >> 10) & 0x1F) + 0x60), (char)(((v >> 5) & 0x1F) + 0x60), (char)((v & 0x1F) + 0x60)]));
        f.U16(node, "Quality", Explain.Quality);
        if (state.Track is { MediaHeader: null } track)
        {
            track.Timescale = timescale;
            track.MediaDuration = duration;
            track.MediaHeader = node;
        }
    }

    // 8.4.3: QuickTime puts a component type where ISO/IEC 14496-12 has pre_defined, and a Pascal
    // string where it has a null-terminated name.
    private static void Handler(State state, Node node, BoxFields f)
    {
        FullBox(node, f);
        string component = f.FourCc(node, "Component Type", Explain.ComponentType);
        string handler = f.FourCc(node, "Handler Type", Explain.HandlerType);
        f.Skip(Math.Min(12, f.Remaining));
        if (f.Remaining > 0)
        {
            long at = f.Position;
            byte first = (byte)f.Take(1);
            if (component == "mhlr" || component == "dhlr" || (first == f.Remaining && first > 0))
            {
                byte[] name = f.TakeBytes(Math.Min(first, f.Remaining));
                node.Add(new Node("Name", at, 1 + name.Length, Latin1OrUtf8(name)) { Description = Explain.HandlerName });
            }
            else
            {
                var rest = new BoxFields(f.Reader, at, f.End);
                rest.CString(node, "Name", Explain.HandlerName);
            }
        }

        node.Value = $"{handler} ({HandlerName(handler)})";
        if (node.Children.Count > 0 && state.Track is { Handler: null } track && component != "dhlr")
        {
            track.Handler = handler;
        }

        state.MetaHandler = handler;
    }

    private static string HandlerName(string handler) => handler switch
    {
        "soun" => "sound",
        "vide" => "video",
        "hint" => "hint",
        "meta" => "timed metadata",
        "text" or "sbtl" or "subt" => "text",
        "mdir" => "iTunes metadata",
        "mdta" => "QuickTime metadata",
        "ID32" => "ID3v2 metadata",
        "alis" => "alias data",
        "url " => "URL data",
        _ => "other",
    };

    // 8.6.6: each edit maps a stretch of the presentation (in the movie's timescale) to the media
    // (in the media's timescale) from media_time on; -1 is an empty edit, a pause.
    private static void EditList(State state, Node node, BoxFields f)
    {
        int size = FullBox(node, f).Version == 1 ? 8 : 4;
        uint count = f.U32(node, "Entries", Explain.EditEntries);
        var edits = new List<Edit>();
        for (uint i = 0; i < count; i++)
        {
            long start = f.Position;
            var edit = new Node("Edit", start, (2 * size) + 4) { Description = Explain.Edit };
            ulong duration = f.Duration(edit, "Segment Duration", size, state.MovieTimescale, Explain.SegmentDuration);
            long mediaTime = size == 8 ? (long)f.Take(8) : (int)(uint)f.Take(4);
            NodeValue shown = mediaTime switch
            {
                -1 => "-1 (empty edit)",
                < 0 => $"{mediaTime} (invalid)",
                _ => NodeValue.Integer(mediaTime, "units of the media's timescale"),
            };
            edit.Add(new Node("Media Time", start + size, size, shown) { Description = Explain.MediaTime });
            double rate = f.Fixed(edit, "Media Rate", 4, 16, Explain.MediaRate);
            if (i < MaxListedEntries)
            {
                node.Add(edit);
            }

            edits.Add(new Edit(duration, mediaTime, rate, edit));
        }

        if (count > MaxListedEntries)
        {
            node.Add(new Node("More Entries", node.Children[^1].End, f.Position - node.Children[^1].End, NodeValue.Integer(count - MaxListedEntries, "entries")) { Description = Explain.MoreEntries });
        }

        if (state.Track is { Edits: null } track)
        {
            track.Edits = edits;
            track.EditListNode = node;
        }
    }

    // QuickTime's user data text: a 16-bit length, a language code, then the text.
    private static bool QuickTimeText(State state, Node node, BoxHeader header, BoxFields f)
    {
        if (f.Remaining < 4)
        {
            return false;
        }

        int length = f.U16(node, "Text Length", Explain.TextLength, unit: "bytes");
        f.U16(node, "Language", Explain.TextLanguage);
        string text = Latin1OrUtf8(f.Bytes(node, "Text", Math.Min(length, f.Remaining), Explain.Text, b => Latin1OrUtf8(b)));
        node.Value = text;
        // ©swr is the "name and version number of the software (or hardware) that generated this movie" (QuickTime File Format, user data
        // atoms), and no Apple specification defines ©too (ffmpeg writes its muxer version there): both are the writer's, unless the text is a libavcodec encoder ID, which names the encoder.
        if (header.Type is "©swr" or "©too" && text.Length > 0)
        {
            state.Context.AddEncoderClaim(new EncoderClaim(text, node, $"QuickTime {header.Type} atom", ClaimRoles.Of(ClaimRole.Writer, text)));
        }

        return true;
    }
}
