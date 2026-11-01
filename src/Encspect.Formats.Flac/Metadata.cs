using System.Buffers.Binary;
using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Flac;

// Metadata blocks (RFC 9639, section 8): the header every block has and the fields of the seven types. A block's node is built when the block is
// read, so a block holds nothing the report needs to keep; blocks past MaxListedBlocks are counted, not listed.
internal static class Metadata
{
    public const int MaxListedBlocks = 1024;

    // The most of a block's body that is read into memory to build its nodes; a picture's data and a larger seek table are not read to their end.
    public const int MaxBodyBytes = 1 << 20;

    private const int ListedSeekPoints = 1024;
    private const int DetailedSeekPoints = 16;
    private const int ListedTracks = 128;
    private const int ListedIndexPoints = 16;
    private const int MaxText = 256;

    public const int SeekPointLength = 18;

    // block holds the 4-byte header and as much of the body as was read. declared is the body length the header states. wholeRanges, where the block's
    // bytes are not all in block (a block over MaxBodyBytes in an Ogg packet), are the file ranges of the whole block as far as it is present.
    public static Node Read(State s, MappedBytes block, bool isLast, int type, int declared, int available, int index, bool paddingIsZero, Func<Node, Node?>? parseComment, IReadOnlyList<ByteRange>? wholeRanges = null)
    {
        FieldMap map = wholeRanges is not { Count: > 0 } ? FieldMap.Of(block)
            : wholeRanges.Count == 1 ? FieldMap.At(wholeRanges[0].Offset)
            : FieldMap.Of(wholeRanges, 4 + available);
        ReadOnlySpan<byte> body = block.Span[4..];
        bool isPartial = body.Length < declared;
        IReadOnlyList<ByteRange> nodeRanges = wholeRanges ?? block.FileRanges();
        long nodeStart = wholeRanges is { Count: > 0 } ? wholeRanges[0].Offset : block.FileStart;
        long nodeEnd = wholeRanges is { Count: > 0 } ? wholeRanges[^1].End : block.FileEnd + (available - body.Length);
        var node = new Node("Metadata Block", nodeStart, nodeEnd - nodeStart, BlockTypes.Name(type))
        {
            Description = Explain.MetadataBlock,
            Topic = Topics.MetadataBlocks,
            Ranges = nodeRanges is { Count: > 1 } ranges ? ranges : null,
        };
        Node header = node.Add(map.Bytes("Block Header", 0, 4, null, Explain.BlockHeader, Topics.MetadataBlocks));
        header.Add(map.Bits("IsLastBlock", 0, 1, NodeValue.Boolean(isLast), Explain.IsLastBlock, Topics.MetadataBlocks));
        header.Add(map.Bits("Block Type", 1, 8, $"{BlockTypes.Name(type)} ({type})", Explain.BlockType, Topics.MetadataBlocks));
        header.Add(map.Bytes("Block Length", 1, 3, NodeValue.Integer(declared, "bytes"), Explain.BlockLength, Topics.MetadataBlocks));

        switch (type)
        {
            case BlockTypes.StreamInfo:
                StreamInfo(s, node, map, block, body, declared, index);
                break;
            case BlockTypes.Padding:
                node.Add(map.Bytes("Padding", 4, available, NodeValue.Integer(declared, "bytes"), Explain.Padding, Topics.MetadataBlocks));
                if (!paddingIsZero)
                {
                    s.Findings.Add(Severity.Warning, "flac.padding-not-zero", node, _ => $"The padding block at 0x{block.FileStart:X} holds bytes that are not 0, which the specification requires of padding.");
                }

                break;
            case BlockTypes.Application:
                Application(node, map, body, declared);
                break;
            case BlockTypes.SeekTable:
                SeekTable(s, node, map, body, declared, isPartial);
                break;
            case BlockTypes.VorbisComment:
                if (parseComment is not null)
                {
                    VorbisComment(s, node, block, declared, parseComment);
                }
                else
                {
                    node.Add(map.Bytes("Comment Data", 4, body.Length, NodeValue.Integer(declared, "bytes"), Explain.CommentData, Topics.VorbisComments));
                }

                break;
            case BlockTypes.CueSheet:
                CueSheet(s, node, map, body, declared, isPartial);
                break;
            case BlockTypes.Picture:
                Picture(s, node, map, body, declared, available);
                break;
            default:
                if (declared > 0)
                {
                    node.Add(map.Bytes("Block Data", 4, body.Length, NodeValue.Integer(declared, "bytes"), type == BlockTypes.Forbidden ? Explain.ForbiddenBlockData : Explain.ReservedBlockData, Topics.MetadataBlocks));
                }

                break;
        }

        return node;
    }

    private static void StreamInfo(State s, Node node, FieldMap map, MappedBytes block, ReadOnlySpan<byte> body, int declared, int index)
    {
        if (declared != StreamInfoBlock.Length)
        {
            s.Findings.Add(Severity.Warning, "flac.streaminfo-length", node, _ =>
                $"The STREAMINFO block at 0x{block.FileStart:X} is {Counts.Of(declared, "byte", "bytes")} long; the specification fixes it at 34.");
        }

        if (body.Length < StreamInfoBlock.Length)
        {
            node.Add(map.Bytes("Block Data", 4, body.Length, NodeValue.Integer(declared, "bytes"), Explain.StreamInfoShort, Topics.StreamInfo));
            return;
        }

        StreamInfoBlock info = StreamInfoBlock.Parse(body);
        if (index != 0)
        {
            s.Findings.Add(Severity.Warning, "flac.streaminfo-misplaced", node, _ =>
                $"A STREAMINFO block is the metadata block number {index + 1}; the specification puts it first and allows one.");
        }
        else
        {
            MappedBytes slice = block.Slice(4, StreamInfoBlock.Length);
            s.SetStreamInfo(info, new MappedBytes(slice.Span.ToArray(), slice.FileRanges()));
        }

        Node unknown(string name, int start, int length, int value, string description) =>
            map.Bytes(name, start, length, value == 0 ? "unknown" : NodeValue.Integer(value, "bytes"), description, Topics.StreamInfo);
        node.Add(map.Bytes("Minimum Block Size", 4, 2, NodeValue.Integer(info.MinBlockSize, "samples"), Explain.MinBlockSize, Topics.StreamInfo));
        node.Add(map.Bytes("Maximum Block Size", 6, 2, NodeValue.Integer(info.MaxBlockSize, "samples"), Explain.MaxBlockSize, Topics.StreamInfo));
        node.Add(unknown("Minimum Frame Size", 8, 3, info.MinFrameSize, Explain.MinFrameSize));
        node.Add(unknown("Maximum Frame Size", 11, 3, info.MaxFrameSize, Explain.MaxFrameSize));
        node.Add(map.Bits("Sample Rate", (4 + 10) * 8, ((4 + 10) * 8) + 20, NodeValue.Integer(info.SampleRate, "Hz"), Explain.StreamInfoSampleRate, Topics.StreamInfo));
        node.Add(map.Bits("Channels", ((4 + 12) * 8) + 4, ((4 + 12) * 8) + 7, NodeValue.Integer(info.Channels, "channels"), Explain.StreamInfoChannels, Topics.StreamInfo));
        node.Add(map.Bits("Bits Per Sample", ((4 + 12) * 8) + 7, ((4 + 13) * 8) + 4, NodeValue.Integer(info.BitsPerSample, "bits"), Explain.StreamInfoBits, Topics.StreamInfo));
        node.Add(map.Bits("Total Samples", ((4 + 13) * 8) + 4, (4 + 18) * 8, info.TotalSamples == 0 ? "unknown" : NodeValue.Integer(info.TotalSamples, "samples"),
            info.TotalSamples == 0 ? Explain.TotalSamplesUnknown : Explain.TotalSamples, Topics.StreamInfo));
        bool isZero = StreamInfoBlock.IsMd5Unknown(body);
        Node md5 = node.Add(map.Bytes("MD5 Signature", 4 + StreamInfoBlock.Md5Offset, StreamInfoBlock.Md5Length, isZero ? "not stored" : Convert.ToHexStringLower(StreamInfoBlock.Md5(body)),
            isZero ? Explain.Md5NotStored : Explain.Md5, Topics.Verification));
        if (index == 0)
        {
            s.Md5Node = md5;
        }

        Check(s, node, block.FileStart, info);
    }

    private static void Check(State s, Node node, long offset, in StreamInfoBlock info)
    {
        void Invalid(string problem, string text) =>
            s.Findings.Add(Severity.Warning, "flac.streaminfo-invalid", node, _ => $"The STREAMINFO block at 0x{offset:X} {text}.", problem);
        if (info.MinBlockSize < 16 || info.MaxBlockSize < 16)
        {
            Invalid("block size", $"states a block size below 16 samples ({info.MinBlockSize} to {info.MaxBlockSize}), the least the specification allows");
        }
        else if (info.MinBlockSize > info.MaxBlockSize)
        {
            Invalid("block sizes", $"states a minimum block size ({info.MinBlockSize}) above the maximum ({info.MaxBlockSize})");
        }

        if (info.MinFrameSize != 0 && info.MaxFrameSize != 0 && info.MinFrameSize > info.MaxFrameSize)
        {
            Invalid("frame sizes", $"states a minimum frame size ({info.MinFrameSize} bytes) above the maximum ({info.MaxFrameSize} bytes)");
        }

        if (info.SampleRate == 0)
        {
            Invalid("sample rate", "states a sample rate of 0 Hz");
        }

        if (info.BitsPerSample < 4)
        {
            Invalid("bits per sample", $"states {Counts.Of(info.BitsPerSample, "bit", "bits")} per sample, below the 4 the specification allows");
        }
    }

    private static void Application(Node node, FieldMap map, ReadOnlySpan<byte> body, int declared)
    {
        if (body.Length < 4)
        {
            node.Add(map.Bytes("Block Data", 4, body.Length, NodeValue.Integer(declared, "bytes"), Explain.ApplicationShort, Topics.MetadataBlocks));
            return;
        }

        bool isText = body[..4].ToArray().All(b => b is >= 0x20 and < 0x7F);
        string id = isText ? Encoding.ASCII.GetString(body[..4]) : "0x" + Convert.ToHexString(body[..4]);
        node.Add(map.Bytes("Application ID", 4, 4, id, Explain.ApplicationId, Topics.MetadataBlocks));
        node.Add(map.Bytes("Application Data", 8, body.Length - 4, NodeValue.Integer(declared - 4, "bytes"), Explain.ApplicationData, Topics.MetadataBlocks));
    }

    private static void SeekTable(State s, Node node, FieldMap map, ReadOnlySpan<byte> body, int declared, bool isPartial)
    {
        long count = declared / SeekPointLength;
        node.Add(map.Bytes("Seek Points", 4, body.Length, NodeValue.Integer(count, "seek points", "seek point"), Explain.SeekPoints, Topics.SeekTable));
        if (declared % SeekPointLength != 0)
        {
            s.Findings.Add(Severity.Warning, "flac.seek-table-length", node, _ =>
                $"The seek table at 0x{node.Offset:X} is {Counts.Of(declared, "byte", "bytes")} long, which is not a whole number of 18-byte seek points.");
        }

        ulong previous = 0;
        bool hasPrevious = false;
        bool sawPlaceholder = false;
        bool isUnordered = false;
        bool isMisplaced = false;
        int whole = body.Length / SeekPointLength;
        for (int i = 0; i < whole; i++)
        {
            ReadOnlySpan<byte> point = body.Slice(i * SeekPointLength, SeekPointLength);
            ulong sample = BinaryPrimitives.ReadUInt64BigEndian(point);
            bool isPlaceholder = sample == ulong.MaxValue;
            if (isPlaceholder)
            {
                sawPlaceholder = true;
            }
            else
            {
                isMisplaced |= sawPlaceholder;
                isUnordered |= hasPrevious && sample <= previous;
                previous = sample;
                hasPrevious = true;
            }

            if (i >= ListedSeekPoints)
            {
                continue;
            }

            int start = 4 + (i * SeekPointLength);
            Node pointNode = map.Bytes("Seek Point", start, SeekPointLength, isPlaceholder ? "placeholder" : NodeValue.Integer((long)Math.Min(sample, long.MaxValue), "samples"),
                isPlaceholder ? Explain.SeekPlaceholder : Explain.SeekPoint, Topics.SeekTable);
            if (i < DetailedSeekPoints)
            {
                pointNode.Add(map.Bytes("Sample Number", start, 8, isPlaceholder ? "placeholder" : NodeValue.Integer((long)Math.Min(sample, long.MaxValue), "samples"), Explain.SeekSampleNumber, Topics.SeekTable));
                pointNode.Add(map.Bytes("Stream Offset", start + 8, 8, NodeValue.Integer((long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(point[8..]), long.MaxValue), "bytes"), Explain.SeekOffset, Topics.SeekTable));
                pointNode.Add(map.Bytes("Frame Samples", start + 16, 2, NodeValue.Integer(BinaryPrimitives.ReadUInt16BigEndian(point[16..]), "samples"), Explain.SeekFrameSamples, Topics.SeekTable));
            }

            node.Add(pointNode);
        }

        if (whole > ListedSeekPoints)
        {
            int start = 4 + (ListedSeekPoints * SeekPointLength);
            node.Add(map.Bytes("Further Seek Points", start, (whole - ListedSeekPoints) * SeekPointLength, NodeValue.Integer(whole - ListedSeekPoints, "seek points", "seek point"), Explain.FurtherSeekPoints, Topics.SeekTable));
        }

        if (isUnordered)
        {
            s.Findings.Add(Severity.Warning, "flac.seek-table-unordered", node, _ =>
                $"The seek table at 0x{node.Offset:X} has seek points that do not rise in sample number, which the specification requires.");
        }

        if (isMisplaced)
        {
            s.Findings.Add(Severity.Warning, "flac.seek-table-placeholder", node, _ =>
                $"The seek table at 0x{node.Offset:X} has a seek point after a placeholder; placeholders belong at the end.");
        }
    }

    private static void VorbisComment(State s, Node node, MappedBytes block, int declared, Func<Node, Node?> parseComment)
    {
        Node? tag = parseComment(node);
        if (tag is null)
        {
            s.Findings.Add(Severity.Info, "flac.comment-not-read", node, _ => $"The Vorbis comment at 0x{block.FileStart + 4:X} was not read as a tag.");
        }
        else if (s.CommentTag is null)
        {
            s.CommentTag = tag;
        }

        if (tag is not null && tag.End < block.FileStart + 4 + declared && block.FileRanges().Count <= 1)
        {
            long rest = block.FileStart + 4 + declared - tag.End;
            s.Findings.Add(Severity.Warning, "flac.comment-trailing-bytes", tag, _ =>
                $"{Counts.Of(rest, "byte", "bytes")} {Counts.Agree(rest, "follows", "follow")} the last comment of the Vorbis comment block at 0x{block.FileStart:X}; FLAC has no framing bit, so they belong to no comment.");
        }
    }

    private static void CueSheet(State s, Node node, FieldMap map, ReadOnlySpan<byte> body, int declared, bool isPartial)
    {
        const int Header = 396;
        if (body.Length < Header)
        {
            node.Add(map.Bytes("Block Data", 4, body.Length, NodeValue.Integer(declared, "bytes"), Explain.CueSheetShort, Topics.CueSheet));
            s.Findings.Add(Severity.Warning, "flac.cuesheet-invalid", node, _ => $"The cue sheet at 0x{node.Offset:X} is {Counts.Of(declared, "byte", "bytes")} long, less than the 396 bytes of its fixed fields.", "short");
            return;
        }

        ReadOnlySpan<byte> catalogue = body[..128];
        int end = catalogue.IndexOf((byte)0);
        string number = Printable(end < 0 ? catalogue : catalogue[..end]);
        node.Add(map.Bytes("Media Catalog Number", 4, 128, number.Length == 0 ? "none" : number, Explain.CueCatalog, Topics.CueSheet));
        node.Add(map.Bytes("Lead-in", 4 + 128, 8, NodeValue.Integer((long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(body[128..]), long.MaxValue), "samples"), Explain.CueLeadIn, Topics.CueSheet));
        node.Add(map.Bits("IsCompactDisc", (4 + 136) * 8, ((4 + 136) * 8) + 1, NodeValue.Boolean((body[136] & 0x80) != 0), Explain.CueIsCompactDisc, Topics.CueSheet));
        int trackCount = body[395];
        bool isCompactDisc = (body[136] & 0x80) != 0;
        node.Add(map.Bytes("Track Count", 4 + 395, 1, NodeValue.Integer(trackCount, "tracks"), Explain.CueTrackCount, Topics.CueSheet));
        int at = Header;
        long lastNumber = -1;
        bool isInvalid = false;
        bool hasReserved = (body[136] & 0x7F) != 0 || body[137..395].ContainsAnyExcept((byte)0);
        for (int t = 0; t < trackCount; t++)
        {
            if (at + 36 > body.Length)
            {
                isInvalid = true;
                break;
            }

            ReadOnlySpan<byte> track = body[at..];
            int indexes = track[35];
            int length = 36 + (indexes * 12);
            if (at + length > body.Length)
            {
                isInvalid = true;
                break;
            }

            int number0 = track[8];
            lastNumber = number0;
            hasReserved |= (track[21] & 0x3F) != 0 || track[22..35].ContainsAnyExcept((byte)0);
            for (int i = 0; i < indexes && !hasReserved; i++)
            {
                hasReserved |= track.Slice(36 + (i * 12) + 9, 3).ContainsAnyExcept((byte)0);
            }
            if (t < ListedTracks)
            {
                bool isAudio = (track[21] & 0x80) == 0;
                Node trackNode = map.Bytes("Track", 4 + at, length, NodeValue.Text($"{number0}"), Explain.CueTrack, Topics.CueSheet);
                trackNode.Add(map.Bytes("Track Offset", 4 + at, 8, NodeValue.Integer((long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(track), long.MaxValue), "samples"), Explain.CueTrackOffset, Topics.CueSheet));
                trackNode.Add(map.Bytes("Track Number", 4 + at + 8, 1, NodeValue.Integer(number0), Explain.CueTrackNumber, Topics.CueSheet));
                ReadOnlySpan<byte> isrc = track.Slice(9, 12);
                trackNode.Add(map.Bytes("ISRC", 4 + at + 9, 12, isrc.ContainsAnyExcept((byte)0) ? Printable(isrc) : "none", Explain.CueIsrc, Topics.CueSheet));
                trackNode.Add(map.Bits("IsAudio", (4 + at + 21) * 8, ((4 + at + 21) * 8) + 1, NodeValue.Boolean(isAudio), Explain.CueIsAudio, Topics.CueSheet));
                trackNode.Add(map.Bits("HasPreEmphasis", ((4 + at + 21) * 8) + 1, ((4 + at + 21) * 8) + 2, NodeValue.Boolean((track[21] & 0x40) != 0), Explain.CuePreEmphasis, Topics.CueSheet));
                trackNode.Add(map.Bytes("Index Point Count", 4 + at + 35, 1, NodeValue.Integer(indexes, "index points", "index point"), Explain.CueIndexCount, Topics.CueSheet));
                for (int i = 0; i < Math.Min(indexes, ListedIndexPoints); i++)
                {
                    int p = at + 36 + (i * 12);
                    Node point = map.Bytes("Index Point", 4 + p, 12, NodeValue.Text($"{body[p + 8]}"), Explain.CueIndexPoint, Topics.CueSheet);
                    point.Add(map.Bytes("Index Offset", 4 + p, 8, NodeValue.Integer((long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(body[p..]), long.MaxValue), "samples"), Explain.CueIndexOffset, Topics.CueSheet));
                    point.Add(map.Bytes("Index Number", 4 + p + 8, 1, NodeValue.Integer(body[p + 8]), Explain.CueIndexNumber, Topics.CueSheet));
                    trackNode.Add(point);
                }

                node.Add(trackNode);
            }

            at += length;
        }

        if (isInvalid || (!isPartial && at != body.Length))
        {
            s.Findings.Add(Severity.Warning, "flac.cuesheet-invalid", node, _ =>
                $"The cue sheet at 0x{node.Offset:X} does not hold the {Counts.Of(trackCount, "track", "tracks")} it counts in its {Counts.Of(declared, "byte", "bytes")}.", "tracks");
        }
        else if (trackCount > 0 && lastNumber is not (170 or 255))
        {
            s.Findings.Add(Severity.Warning, "flac.cuesheet-invalid", node, _ =>
                $"The last track of the cue sheet at 0x{node.Offset:X} is number {lastNumber}; the specification ends a cue sheet with the lead-out track, 170 for a compact disc and 255 for any other medium.", "lead-out");
        }
        else if (trackCount > 0 && lastNumber != (isCompactDisc ? 170 : 255))
        {
            // flac 1.4.3 writes track 170 with the compact disc flag clear for a cue sheet imported with --cuesheet (the encoder-written fixture), so this
            // is information and not a warning.
            s.Findings.Add(Severity.Info, "flac.cuesheet-lead-out-medium", node, _ =>
                $"The lead-out track of the cue sheet at 0x{node.Offset:X} is number {lastNumber}, but the compact disc flag is {(isCompactDisc ? "set" : "not set")}, for which the specification gives {(isCompactDisc ? 170 : 255)}.");
        }
        else if (trackCount == 0)
        {
            s.Findings.Add(Severity.Warning, "flac.cuesheet-invalid", node, _ => $"The cue sheet at 0x{node.Offset:X} counts no track, so it lacks the lead-out track every cue sheet ends with.", "lead-out");
        }

        if (hasReserved)
        {
            s.Findings.Add(Severity.Info, "flac.cuesheet-reserved", node, _ => $"The cue sheet at 0x{node.Offset:X} sets bits or bytes that the specification reserves as 0.");
        }
    }

    private static void Picture(State s, Node node, FieldMap map, ReadOnlySpan<byte> body, int declared, int available)
    {
        int at = 0;
        string? problem = null;
        if (body.Length < 8)
        {
            problem = "ends before the picture type and the length of the MIME type";
        }
        else
        {
            node.Add(map.Bytes("Picture Type", 4, 4, PictureType(BinaryPrimitives.ReadUInt32BigEndian(body)), Explain.PictureType, Topics.Picture));
            uint mimeLength = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
            if (mimeLength > body.Length - 12)
            {
                problem = "ends inside the MIME type or the length of the description";
            }
            else
            {
                int mime = (int)mimeLength;
                node.Add(map.Bytes("MIME Type", 8, 4 + mime, Limit(Encoding.ASCII.GetString(body.Slice(8, mime))), Explain.PictureMime, Topics.Picture));
                at = 8 + mime;
                uint descriptionLength = BinaryPrimitives.ReadUInt32BigEndian(body[at..]);
                if (descriptionLength > body.Length - at - 24)
                {
                    problem = "ends inside the description or the fields after it";
                    at += 4;
                }
                else
                {
                    int text = (int)descriptionLength;
                    string description = Encoding.UTF8.GetString(body.Slice(at + 4, text));
                    node.Add(map.Bytes("Description", 4 + at, 4 + text, description.Length == 0 ? "none" : Limit(description), Explain.PictureDescription, Topics.Picture));
                    at += 4 + text;
                    node.Add(map.Bytes("Width", 4 + at, 4, NodeValue.Integer(BinaryPrimitives.ReadUInt32BigEndian(body[at..]), "pixels", "pixel"), Explain.PictureWidth, Topics.Picture));
                    node.Add(map.Bytes("Height", 8 + at, 4, NodeValue.Integer(BinaryPrimitives.ReadUInt32BigEndian(body[(at + 4)..]), "pixels", "pixel"), Explain.PictureHeight, Topics.Picture));
                    node.Add(map.Bytes("Color Depth", 12 + at, 4, NodeValue.Integer(BinaryPrimitives.ReadUInt32BigEndian(body[(at + 8)..]), "bits"), Explain.PictureDepth, Topics.Picture));
                    uint colors = BinaryPrimitives.ReadUInt32BigEndian(body[(at + 12)..]);
                    node.Add(map.Bytes("Indexed Colors", 16 + at, 4, colors == 0 ? "not indexed" : NodeValue.Integer(colors, "colors", "color"), Explain.PictureColors, Topics.Picture));
                    uint dataLength = BinaryPrimitives.ReadUInt32BigEndian(body[(at + 16)..]);
                    node.Add(map.Bytes("Data Length", 20 + at, 4, NodeValue.Integer(dataLength, "bytes"), Explain.PictureDataLength, Topics.Picture));
                    at += 20;
                    node.Add(map.Bytes("Picture Data", 4 + at, available - at, NodeValue.Integer(declared - at, "bytes"), Explain.PictureData, Topics.Picture));
                    if ((long)at + dataLength != declared)
                    {
                        int fields = at;
                        s.Findings.Add(Severity.Warning, "flac.picture-invalid", node, _ =>
                            $"The picture block at 0x{node.Offset:X} states {Counts.Of(dataLength, "byte", "bytes")} of picture data after {Counts.Of(fields, "byte", "bytes")} of fields, but is {Counts.Of(declared, "byte", "bytes")} long.", "length");
                    }
                }
            }
        }

        if (problem is not null)
        {
            node.Add(map.Bytes("Block Data", 4 + at, body.Length - at, NodeValue.Integer(declared - at, "bytes"), Explain.PictureShort, Topics.Picture));
            string reason = problem;
            s.Findings.Add(Severity.Warning, "flac.picture-invalid", node, _ => $"The picture block at 0x{node.Offset:X} {reason}.", "fields");
        }
    }

    private static string PictureType(uint type) => type switch
    {
        0 => "other",
        1 => "32x32 pixel file icon",
        2 => "other file icon",
        3 => "front cover",
        4 => "back cover",
        5 => "leaflet page",
        6 => "media label",
        7 => "lead artist",
        8 => "artist",
        9 => "conductor",
        10 => "band or orchestra",
        11 => "composer",
        12 => "lyricist",
        13 => "recording location",
        14 => "during recording",
        15 => "during performance",
        16 => "movie or video screen capture",
        17 => "a bright colored fish",
        18 => "illustration",
        19 => "band or artist logotype",
        20 => "publisher or studio logotype",
        _ => "reserved",
    };

    private static string Limit(string text)
    {
        string printable = new([.. text.Select(c => char.IsControl(c) ? '?' : c)]);
        return printable.Length > MaxText ? printable[..MaxText] + "..." : printable;
    }

    private static string Printable(ReadOnlySpan<byte> bytes) => Limit(Encoding.ASCII.GetString(bytes));
}
