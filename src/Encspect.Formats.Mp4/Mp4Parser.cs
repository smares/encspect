using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Mp4;

// The ISO base media file format (ISO/IEC 14496-12) and the files built on it: MP4 (ISO/IEC
// 14496-14), M4A, M4B, 3GP and QuickTime .mov. The file is a run of boxes, each a 32-bit size, a
// four-character type and its payload; container boxes hold boxes. Every box becomes a node with its
// header and fields at their bytes. Each audio track's samples, found through its sample tables, go
// to the codec parser of their coding, AAC through the "aac" route with its AudioSpecificConfig and
// MPEG audio through "mpeg-audio"; the codec reports the stream. mdat is never read but where a
// codec reads its samples.
public sealed partial class Mp4Parser : IFormatParser
{
    // Boxes ISO/IEC 14496-12 and QuickTime place at the top level, and the only ones a run of
    // top-level boxes is followed through: anything else ends it, so a trailing tag is never taken
    // for a box.
    private static readonly HashSet<string> TopLevel =
    [
        "ftyp", "styp", "moov", "mdat", "free", "skip", "wide", "pnot", "uuid", "moof", "mfra", "sidx", "ssix", "prft", "emsg", "pdin", "meta", "junk",
    ];

    // Boxes nest at most this deep below the file's MP4 node, so the tree stays within Node.MaxDepth
    // with the fields and the codec's nodes under the deepest box.
    private const int MaxBoxDepth = 40;

    // The nodes of the tree in all, the boxes' and their fields' and entries'. A box that starts past the budget is still read for what the
    // file's tracks, samples, fragments and protection need, into the compact records of the parser, but makes no node of the tree: the rest of the
    // file is shown as one node with a finding. A valid file is read in full however many fragments it has.
    internal const int MaxNodes = 250_000;

    private readonly int _maxFragmentSamples;

    public Mp4Parser()
        : this(MaxFragmentSamples)
    {
    }

    // For the tests, which cannot write the millions of samples the cap is set for.
    internal Mp4Parser(int maxFragmentSamples) => _maxFragmentSamples = maxFragmentSamples;

    public string Name => "MP4";

    // An ftyp box at the start is certain (class 100); a run of top-level boxes that holds moov, mdat
    // or moof is sure (95). The boxes counted are those the run was followed through. Boxes have no
    // checksum.
    public SniffResult Sniff(SniffContext context)
    {
        ByteReader reader = context.Reader;
        long position = reader.Start;
        bool sawMedia = false;
        int boxes = 0;
        for (int count = 0; count < 8 && position + 8 <= reader.End; count++)
        {
            if (ReadHeader(reader, position, reader.End) is not { } header || !TopLevel.Contains(header.Type))
            {
                return sawMedia ? new SniffResult(95, reader.Start, boxes, End: position) : SniffResult.None;
            }

            if (count == 0 && header.Type == "ftyp")
            {
                return header.Size >= 16 ? new SniffResult(100, reader.Start, 1, End: Math.Min(header.End, reader.End)) : SniffResult.None;
            }

            sawMedia |= header.Type is "moov" or "mdat" or "moof";
            boxes++;
            if (header.Size == 0 || header.End >= reader.End)
            {
                position = reader.End;
                break;
            }

            position = header.End;
        }

        return sawMedia ? new SniffResult(95, reader.Start, boxes, End: position) : SniffResult.None;
    }

    // The end of the run of top-level boxes, which may lie past the end of a truncated file; null
    // where a box extends to the end of the file (size 0), which says nothing about a tag after it.
    public long? DeclaredEnd(SniffContext context)
    {
        ByteReader reader = context.Reader;
        long position = reader.Start;
        while (position + 8 <= reader.End)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (ReadHeader(reader, position, long.MaxValue) is not { } header || !TopLevel.Contains(header.Type))
            {
                break;
            }

            if (header.IsToEnd)
            {
                return null;
            }

            position = header.End;
        }

        return position > reader.Start ? position : null;
    }

    // Tags an ID3v2.4 SEEK frame points to inside data without a declared end (no ftyp or moov to
    // give one) are already in the report. Such a tag breaks the box structure, so the boxes are
    // read up to the first one, and the bytes between and after them are reported as not read.
    public void Parse(ParseContext context)
    {
        ByteReader window = context.Reader;
        List<Node> tags = [.. context.EmbeddedTags.Where(t => t.Offset >= window.Start && t.Offset < window.End).OrderBy(t => t.Offset)];
        ByteReader reader = tags.Count > 0 ? window.Slice(window.Start, tags[0].Offset - window.Start) : window;
        ParseBoxes(context, reader, _maxFragmentSamples);
        long position = reader.End;
        foreach (Node? tag in tags.Append<Node?>(null))
        {
            long end = Math.Min(tag?.Offset ?? window.End, window.End);
            if (end > position)
            {
                Node data = context.Root.Add(new Node("Data", position, end - position, NodeValue.Integer(end - position, "bytes")) { Description = Explain.AfterEmbeddedTag });
                context.AddFinding(Severity.Warning, "mp4.data-after-tag",
                    $"{Counts.Of(end - position, "byte", "bytes")} at 0x{position:X} follow an ID3v2 tag inside the MP4 data, which breaks its boxes; they are not read.", data);
            }

            position = Math.Max(position, tag?.End ?? position);
        }
    }

    private static void ParseBoxes(ParseContext context, ByteReader reader, int maxFragmentSamples)
    {
        var state = new State(context, reader) { SampleBytesLeft = reader.Length, MaxFragmentSamples = maxFragmentSamples };
        Node file = context.Root.Add(new Node("MP4", reader.Start, reader.Length) { Description = Explain.File, Topic = Topics.IsoBaseMediaFormat });
        state.File = file;
        long end = ReadChildren(state, file, reader.Start, reader.End, 0, topLevel: true);
        if (end < reader.End)
        {
            // What the boxes after it say is not known, so it may say that samples are protected.
            state.DoesEveryTrackHaveProtectingEvidence = true;
            Node data = file.Add(new Node("Trailing Data", end, reader.End - end, NodeValue.Integer(reader.End - end, "bytes")) { Description = Explain.TrailingData });
            state.Findings.AddNow(Severity.Warning, "mp4.trailing-data", $"{Counts.Of(reader.End - end, "byte", "bytes")} after the last box at 0x{end:X} are not a box.", data);
        }

        Check(state, file);
        RouteTracks(state);
        file.Value = Describe(state);
        state.Findings.Flush(context);
    }

    private static string Describe(State state) =>
        state.MajorBrand is { } brand ? $"{brand.Trim()} ({BrandName(brand)})" : state.IsQuickTime ? "QuickTime (no ftyp box)" : "no ftyp box";

    private static string BrandName(string brand) => brand switch
    {
        "M4A " => "Apple audio",
        "M4B " => "Apple audio book",
        "M4P " => "Apple protected audio",
        "M4V " => "Apple video",
        "mp41" => "MP4 version 1",
        "mp42" => "MP4 version 2",
        "isom" => "ISO base media",
        "iso2" or "iso4" or "iso5" or "iso6" or "iso8" or "iso9" => "ISO base media",
        "dash" => "MPEG-DASH",
        "qt  " => "QuickTime",
        "3gp4" or "3gp5" or "3gp6" or "3gp7" => "3GPP",
        "3g2a" => "3GPP2",
        "F4A " => "Flash audio",
        _ => "other brand",
    };

    // A box's size, type and, for a uuid box, its user type; null where the header does not fit or
    // states a size smaller than itself.
    internal static BoxHeader? ReadHeader(ByteReader reader, long position, long end)
    {
        if (position + 8 > Math.Min(end, reader.End))
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[16];
        reader.Position = position;
        reader.Read(bytes[..8]);
        long size = BoxFields.FourCcValue(bytes);
        string type = BoxFields.FourCcText(BoxFields.FourCcValue(bytes[4..]));
        int headerLength = 8;
        bool toEnd = false;
        bool large = size == 1;
        if (large)
        {
            if (position + 16 > reader.End)
            {
                return null;
            }

            reader.Read(bytes[8..16]);
            ulong wide = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
            if (wide > long.MaxValue / 2)
            {
                return null;
            }

            size = (long)wide;
            headerLength = 16;
        }
        else if (size == 0)
        {
            toEnd = true;
            size = Math.Min(end, reader.End) - position;
        }

        if (type == "uuid")
        {
            headerLength += 16;
        }

        return size < headerLength && !toEnd ? null : new BoxHeader(position, size, type, headerLength, toEnd, large);
    }

    // Reads a box's payload, where the box's type alone does not say how (a sample entry, an item).
    private delegate void PayloadParser(State state, Node node, BoxHeader header, BoxFields fields, int depth);

    private delegate BoxName BoxNaming(BoxHeader header);

    // Reads the boxes in [start, end) as children of parent and returns where they end: end, or the
    // start of bytes that are no box.
    private static long ReadChildren(State state, Node parent, long start, long end, int depth, bool topLevel = false, PayloadParser? custom = null,
        BoxNaming? naming = null)
    {
        string? outerParent = state.ParentType;
        state.ParentType = topLevel ? null : parent.Name == UserDataName ? "udta" : parent.Name;
        try
        {
            return ReadChildrenOf(state, parent, start, end, depth, topLevel, custom, naming);
        }
        finally
        {
            state.ParentType = outerParent;
        }
    }

    private static long ReadChildrenOf(State state, Node parent, long start, long end, int depth, bool topLevel, PayloadParser? custom, BoxNaming? naming)
    {
        ByteReader reader = state.Reader;
        long position = start;
        while (position < end)
        {
            state.Context.CancellationToken.ThrowIfCancellationRequested();
            if (end - position < 8)
            {
                if (topLevel)
                {
                    return position;
                }

                // QuickTime File Format, "User data list": the list of a udta box is optionally ended by a 32-bit zero.
                if (state.ParentType == "udta" && end - position == 4 && state.Reader.ReadBytesAt(position, 4).All(b => b == 0))
                {
                    parent.Add(new Node("Terminator", position, 4, NodeValue.Integer(0)) { Description = Explain.UserDataTerminator });
                    return end;
                }

                Node rest = parent.Add(new Node("Unused Bytes", position, end - position, NodeValue.Integer(end - position, "bytes")) { Description = Explain.UnusedBytes });
                state.Findings.AddNow(Severity.Warning, "mp4.box-header-truncated", $"{Counts.Bytes(end - position)} at 0x{position:X} {(end - position == 1 ? "is" : "are")} too few for a box header.", rest);
                return end;
            }

            BoxHeader? read = ReadHeader(reader, position, end);
            if (read is not { } header || (topLevel && !TopLevel.Contains(header.Type) && position > start))
            {
                if (topLevel)
                {
                    return position;
                }

                Node rest = parent.Add(new Node("Unused Bytes", position, end - position, NodeValue.Integer(end - position, "bytes")) { Description = Explain.UnusedBytes });
                state.Findings.AddNow(Severity.Warning, "mp4.box-size-invalid",
                    $"The box at 0x{position:X} states a size smaller than its header, or a 64-bit size beyond any file, so the {Counts.Of(end - position, "byte", "bytes")} from there {Counts.Agree(end - position, "is", "are")} not read as boxes.", rest);
                return end;
            }

            long boxEnd = header.End;
            bool cut = boxEnd > end;
            if (cut)
            {
                boxEnd = end;
            }

            // A header that its parent's end cuts off is no box: its size fields cannot be shown.
            if (boxEnd - position < header.HeaderLength)
            {
                if (topLevel)
                {
                    return position;
                }

                Node rest = parent.Add(new Node("Unused Bytes", position, end - position, NodeValue.Integer(end - position, "bytes")) { Description = Explain.UnusedBytes });
                state.Findings.AddNow(Severity.Warning, "mp4.box-header-truncated", $"{Counts.Of(end - position, "byte", "bytes")} at 0x{position:X} are too few for the {header.Type} box's header.", rest);
                return end;
            }

            // Named from the header as the parent clips it: a udta box is a tag for the text atoms it holds, not for those past its parent's end.
            BoxHeader clipped = header with { Size = boxEnd - position };
            BoxName name = naming?.Invoke(clipped) ?? Naming(state, clipped);
            Node node = ParseBox(state, parent, clipped, depth, header.Size, custom, name);
            if (header.IsToEnd && !topLevel)
            {
                state.Findings.AddNow(Severity.Warning, "mp4.box-to-end",
                    $"The {header.Type} box at 0x{position:X} has size 0, which ISO/IEC 14496-12 allows only for the last box of the file; it is read to the end of its parent.", node);
            }

            if (cut)
            {
                if (topLevel && header.End > reader.End)
                {
                    state.Findings.AddNow(Severity.Warning, "mp4.truncated", $"The {header.Type} box at 0x{position:X} declares {Counts.Of(header.Size, "byte", "bytes")}, but the file ends after {reader.End - position}.", node);
                }
                else
                {
                    state.Findings.AddNow(Severity.Warning, "mp4.box-overruns-parent", $"The {header.Type} box at 0x{position:X} declares {Counts.Of(header.Size, "byte", "bytes")}, {header.End - end} more than its parent {parent.Name} holds.", node);
                }
            }

            position = boxEnd;
        }

        return position;
    }

    // The name of a udta box that holds QuickTime's text atoms, a tag of the container like an item list.
    private const string UserDataName = "QuickTime User Data";

    // An item list is the file's iTunes or QuickTime metadata, a tag of the container, and so is a user data box that holds text atoms;
    // every other box is named by its type.
    private static BoxName Naming(State state, BoxHeader header)
    {
        if (header.Type == "ilst")
        {
            return new(state.MetaHandler == "mdta" ? "QuickTime Metadata" : "iTunes Metadata", NodeKind.Tag, Explain.Box(header.Type), Topics.ItunesMetadata);
        }

        if (header.Type == "udta" && HoldsTextAtom(state, header))
        {
            return new(UserDataName, NodeKind.Tag, Explain.UserDataTag, Topics.QuickTimeUserData);
        }

        if (state.ParentType == "udta" && BoxNames.UserDataText(header.Type) is { } field)
        {
            return new(field, NodeKind.Detail, Explain.UserDataText(header.Type), Topics.QuickTimeUserData);
        }

        return new(header.Type, NodeKind.Detail, Explain.Box(header.Type), Explain.BoxTopic(header.Type));
    }

    // Whether a text atom the specification lists is among the boxes of the udta box, found by their headers alone.
    private static bool HoldsTextAtom(State state, BoxHeader header)
    {
        long position = header.PayloadStart;
        for (int i = 0; ReadHeader(state.Reader, position, header.End) is { } child && child.End <= header.End; i++)
        {
            if ((i & 0x3FF) == 0)
            {
                state.Context.CancellationToken.ThrowIfCancellationRequested();
            }

            if (BoxNames.UserDataText(child.Type) is not null)
            {
                return true;
            }

            if (child.IsToEnd)
            {
                break;
            }

            position = child.End;
        }

        return false;
    }

    // The box's node with its header fields, then its payload by type.
    private static Node ParseBox(State state, Node parent, BoxHeader header, int depth, long declared, PayloadParser? custom, BoxName name)
    {
        var node = new Node(name.Name, header.Offset, header.Size, BoxNames.Describe(header.Type)) { Kind = name.Kind, Description = name.Description, Topic = name.Topic };
        bool wasDetaching = state.Findings.IsDetaching;
        bool isDetached = wasDetaching || state.Nodes >= MaxNodes;
        if (!isDetached)
        {
            parent.Add(node);
        }

        Node? outerNote = state.Findings.CurrentNote;
        if (isDetached)
        {
            if (!wasDetaching)
            {
                state.Findings.CurrentNote = ShowNotShown(state, parent, header);
            }

            state.DetachedBoxes++;
            state.Findings.IsDetaching = true;
        }

        try
        {
            ReadBox(state, node, header, depth, declared, custom);
        }
        finally
        {
            state.Findings.IsDetaching = wasDetaching;
        }

        if (isDetached)
        {
            state.Findings.MarkDetached(node, state.Findings.CurrentNote!);
            state.Findings.CurrentNote = outerNote;
        }
        else
        {
            state.Nodes += CountOwn(node);
        }

        return node;
    }

    // The one node that stands for the boxes of a parent that are read and not shown, once for each parent, and the finding once.
    private static Node ShowNotShown(State state, Node parent, BoxHeader header)
    {
        if (parent.Children.Count > 0 && parent.Children[^1].Name == "Boxes Not Shown")
        {
            return parent.Children[^1];
        }

        Node note = parent.Add(new Node("Boxes Not Shown", header.Offset, Math.Max(0, parent.End - header.Offset), "read, not shown") { Description = Explain.BoxesNotShown });
        if (!state.Findings.IsNodeLimitReported)
        {
            state.Findings.IsNodeLimitReported = true;
            state.Findings.AddNow(Severity.Warning, "mp4.node-limit",
                $"The tree holds more than {MaxNodes} nodes; the boxes from the {header.Type} box at 0x{header.Offset:X} on are read for the tracks, samples and protection they describe, but are not shown as nodes, and the checks of their children are not made.", note);
        }

        return note;
    }

    // The nodes a box holds besides the boxes inside it, which count when they are read.
    private static int CountOwn(Node node)
    {
        int count = 1;
        foreach (Node child in node.Children)
        {
            if (!(child.Children.Count >= 2 && child.Children[0].Name == "Size" && child.Children[1].Name == "Type"))
            {
                count += CountOwn(child);
            }
        }

        return count;
    }

    private static Node ReadBox(State state, Node node, BoxHeader header, int depth, long declared, PayloadParser? custom)
    {
        long position = header.Offset;
        node.Add(new Node("Size", position, 4, header.IsToEnd ? "0 (to the end of the file)" : header.IsLarge ? "1 (a 64-bit size follows)" : NodeValue.Integer(declared, "bytes")) { Description = Explain.Size });
        node.Add(new Node("Type", position + 4, 4, header.Type) { Description = Explain.Type });
        if (header.IsLarge)
        {
            node.Add(new Node("Large Size", position + 8, 8, NodeValue.Integer(declared, "bytes")) { Description = Explain.LargeSize });
        }

        if (header.Type == "uuid")
        {
            long at = header.Offset + header.HeaderLength - 16;
            byte[] user = state.Reader.ReadBytesAt(at, 16);
            var userType = new Guid(user, bigEndian: true);
            node.Add(new Node("User Type", at, 16, userType.ToString()) { Description = Explain.UserType });
            if (userType == PiffSampleEncryption || userType == PiffTrackEncryption)
            {
                MarkProtecting(state);
            }
        }

        if (depth >= MaxBoxDepth)
        {
            if (header.Size > header.HeaderLength)
            {
                node.Add(new Node("Payload", header.PayloadStart, header.End - header.PayloadStart, NodeValue.Integer(header.End - header.PayloadStart, "bytes")) { Description = Explain.Payload });
            }

            state.Findings.AddNow(Severity.Warning, "mp4.nesting-too-deep", $"Boxes nest more than {Counts.Of(MaxBoxDepth, "level", "levels")} deep at 0x{header.Offset:X}; the boxes inside are not read.", node);
            return node;
        }

        var fields = new BoxFields(state.Reader, header.PayloadStart, header.End);
        try
        {
            (custom ?? ParsePayload)(state, node, header, fields, depth);
        }
        catch (EndOfStreamException)
        {
            state.Findings.Add(Severity.Warning, "mp4.box-fields-truncated", node, count =>
                $"{(count == 1 ? $"The {header.Type} box at 0x{header.Offset:X} ends" : $"{count} boxes end")} inside a field{(count == 1 ? string.Empty : $", first the {header.Type} box at 0x{header.Offset:X}")}.");
        }

        return node;
    }

    private static void ParsePayload(State state, Node node, BoxHeader header, BoxFields f, int depth)
    {
        switch (header.Type)
        {
            case "moov" or "trak" or "mdia" or "minf" or "stbl" or "dinf" or "edts" or "mvex" or "moof" or "traf" or "mfra" or "sinf" or "schi" or "tref" or "wave" or "udta":
                Container(state, node, header, f, depth);
                break;
            case "ftyp" or "styp":
                FileType(state, node, header, f);
                break;
            case "mdat":
                state.Mdats.Add((header.PayloadStart, header.End, node));
                state.HasPageAlignedMdat |= state.FreeEnd == header.Offset && header.PayloadStart % 4096 == 0;
                if (depth == 0)
                {
                    state.FirstMdat ??= header.Offset;
                }

                if (header.End > header.PayloadStart)
                {
                    node.Add(new Node("Data", header.PayloadStart, header.End - header.PayloadStart, NodeValue.Integer(header.End - header.PayloadStart, "bytes")) { Description = Explain.MediaData });
                }

                break;
            case "free" or "skip" or "wide" or "junk":
                if (header.Type == "free" && depth == 0)
                {
                    state.FreeEnd = header.End;
                }

                if (header.End > header.PayloadStart)
                {
                    node.Add(new Node("Data", header.PayloadStart, header.End - header.PayloadStart, NodeValue.Integer(header.End - header.PayloadStart, "bytes")) { Description = Explain.FreeData });
                }

                break;
            default:
                if (!ParseKnown(state, node, header, f, depth) && f.Remaining > 0)
                {
                    node.Add(new Node("Data", f.Position, f.Remaining, NodeValue.Integer(f.Remaining, "bytes")) { Description = Explain.UnreadData });
                }

                break;
        }
    }

    private static void Container(State state, Node node, BoxHeader header, BoxFields f, int depth)
    {
        Track? outerTrack = state.Track;
        FragmentTrack? outerFragment = state.Fragment;
        (long Offset, Node Node)? outerMoof = state.Moof;
        bool outerIgnored = state.IsMovieIgnored;
        FragmentTrack? fragment = null;
        if (header.Type == "trak")
        {
            // The tracks of a moov box that is not the movie are shown, and their boxes count for every track.
            state.Track = state.IsMovieIgnored ? null : new Track(node);
            if (state.Track is not null)
            {
                state.Tracks.Add(state.Track);
            }
        }
        else if (header.Type == "moov")
        {
            if (depth > 0)
            {
                state.Findings.AddNow(Severity.Warning, "mp4.box-misplaced",
                    $"The moov box at 0x{header.Offset:X} lies in a {state.ParentType} box, where ISO/IEC 14496-12 8.2.1 does not place it; it is shown but is not the movie, and its tracks are not analyzed.", node);
                state.IsMovieIgnored = true;
                state.Track = null;
            }
            else
            {
                state.Moov ??= header.Offset;
                state.MoovNode ??= node;
                if (state.MoovCount++ > 0)
                {
                    state.Findings.AddNow(Severity.Warning, "mp4.duplicate-box", $"The file holds a second moov box at 0x{header.Offset:X}; players read the first, so its tracks are not analyzed.", node);
                    state.IsMovieIgnored = true;
                    state.Track = null;
                }
            }
        }
        else if (header.Type == "traf")
        {
            fragment = state.Fragment = new FragmentTrack(node, state.Moof) { IsInTrack = state.Track is not null, IsMisplaced = outerFragment is not null };
            if (outerFragment is not null)
            {
                state.Findings.AddNow(Severity.Warning, "mp4.box-misplaced",
                    $"The traf box at 0x{header.Offset:X} lies in another traf box, where ISO/IEC 14496-12 8.8.6 does not place it; its samples are placed as a fragment of their own, and what it says about protection counts for every track.", node);
            }
        }
        else if (header.Type == "moof")
        {
            if (state.OpenMoofs++ > 0)
            {
                state.Findings.AddNow(Severity.Warning, "mp4.box-misplaced",
                    $"The moof box at 0x{header.Offset:X} lies in another moof box, where ISO/IEC 14496-12 8.8.4 does not place it.", node);
            }

            state.Moof = (header.Offset, node);
            state.Fragments++;
        }

        bool outerSinf = state.InSinf;
        if (header.Type == "sinf")
        {
            if (state.CurrentEntry is { } protectedEntry)
            {
                state.InSinf = true;
                protectedEntry.Protection ??= new EntryProtection(node);
            }
            else
            {
                // Outside a sample entry a sinf box is not that entry's protection, but it says that a scheme protects the track or file it lies in.
                MarkProtectionScheme(state);
            }
        }

        ReadChildren(state, node, f.Position, header.End, depth + 1);
        state.InSinf = outerSinf;
        if (!state.Findings.IsDetaching)
        {
            CheckChildren(state, node, header.Type);
        }

        if (header.Type is "trak" or "moov")
        {
            state.Track = outerTrack;
            state.IsMovieIgnored = outerIgnored;
        }
        else if (fragment is not null)
        {
            FinishFragment(state, fragment);
            state.Fragment = outerFragment;
        }
        else if (header.Type == "moof")
        {
            // The moof box stays the current one after it ends (a track fragment outside any moof box follows the last), unless it lay in another.
            state.OpenMoofs--;
            if (state.OpenMoofs > 0)
            {
                state.Moof = outerMoof;
            }
        }
    }

    // ISO/IEC 14496-12 4.3 (ftyp): the major brand, its version and the compatible brands.
    private static void FileType(State state, Node node, BoxHeader header, BoxFields f)
    {
        string major = f.FourCc(node, "Major Brand", Explain.MajorBrand);
        f.U32(node, "Minor Version", Explain.MinorVersion);
        var brands = new List<string>();
        while (f.Remaining >= 4)
        {
            brands.Add(f.FourCc(node, "Compatible Brand", Explain.CompatibleBrand));
        }

        if (header.Type == "ftyp")
        {
            if (state.MajorBrand is not null)
            {
                state.Findings.AddNow(Severity.Warning, "mp4.duplicate-box", $"The file holds a second ftyp box at 0x{header.Offset:X}.", node);
                return;
            }

            if (header.Offset != state.Reader.Start)
            {
                state.Findings.AddNow(Severity.Warning, "mp4.ftyp-not-first", $"The ftyp box at 0x{header.Offset:X} is not the first box, as ISO/IEC 14496-12 4.3 requires.", node);
            }

            state.MajorBrand = major;
            state.IsQuickTime = major == "qt  ";
            state.HasQuickTimeBrand = major == "qt  " || brands.Contains("qt  ");
        }

        node.Value = $"{major.Trim()} ({BrandName(major)})";
    }

    // Whether some of the box's own children were read and not shown: a node it holds stands for them (bytes too few for a box header may follow it).
    private static bool HasHiddenChild(Node node) => node.Children.Any(c => c.Name == "Boxes Not Shown");

    // Boxes a parent requires once (ISO/IEC 14496-12, the "Quantity: Exactly one" of each box).
    private static void CheckChildren(State state, Node node, string type)
    {
        string[] required = type switch
        {
            "moov" => ["mvhd"],
            "trak" => ["tkhd", "mdia"],
            "mdia" => ["mdhd", "hdlr", "minf"],
            "stbl" => ["stsd", "stts", "stsc"],
            "traf" => ["tfhd"],
            "moof" => ["mfhd"],
            _ => [],
        };
        // A box that has a hidden child may have the required box among those; the duplicates among the shown ones are certain.
        bool hasHiddenChild = HasHiddenChild(node);
        foreach (string child in hasHiddenChild ? [] : required)
        {
            if (!node.Children.Any(c => c.Name == child))
            {
                state.Findings.Add(Severity.Warning, $"mp4.missing-{child}", node, count =>
                    $"{(count == 1 ? $"The {type} box at 0x{node.Offset:X} has" : $"{count} {type} boxes have")} no {child} box, which ISO/IEC 14496-12 requires{(count == 1 ? string.Empty : $"; the first is at 0x{node.Offset:X}")}.");
            }
        }

        if (type == "stbl" && !hasHiddenChild)
        {
            if (!node.Children.Any(c => c.Name is "stsz" or "stz2"))
            {
                state.Findings.Add(Severity.Warning, "mp4.missing-stsz", node, _ => $"The stbl box at 0x{node.Offset:X} has neither an stsz nor an stz2 box, so its samples have no sizes.");
            }

            if (!node.Children.Any(c => c.Name is "stco" or "co64"))
            {
                state.Findings.Add(Severity.Warning, "mp4.missing-stco", node, _ => $"The stbl box at 0x{node.Offset:X} has neither an stco nor a co64 box, so its chunks have no offsets.");
            }
        }

        string[] unique = type switch
        {
            "moov" => ["mvhd", "mvex"],
            "trak" => ["tkhd", "mdia", "edts"],
            "mdia" => ["mdhd", "hdlr", "minf"],
            "minf" => ["smhd", "dinf", "stbl"],
            "stbl" => ["stsd", "stts", "ctts", "stsc", "stsz", "stz2", "stco", "co64", "stss"],
            "edts" => ["elst"],
            "traf" => ["tfhd", "tfdt"],
            "moof" => ["mfhd"],
            _ => [],
        };
        foreach (string child in unique)
        {
            if (node.Children.Where(c => c.Name == child).Skip(1).FirstOrDefault() is { } second)
            {
                state.Findings.AddNow(Severity.Warning, "mp4.duplicate-box",
                    $"The {type} box at 0x{node.Offset:X} holds a second {child} box at 0x{second.Offset:X}; ISO/IEC 14496-12 allows one, and readers take the first.", second);
            }
        }
    }

    // Layout findings once every box is read.
    private static void Check(State state, Node file)
    {
        if (state.Moov is null)
        {
            state.Findings.AddNow(Severity.Warning, "mp4.moov-missing", "The file has no moov box, so it has no tracks and no sample tables.", file);
        }
        else if (state.FirstMdat is { } mdat && mdat < state.Moov && state.Fragments == 0)
        {
            state.Findings.AddNow(Severity.Info, "mp4.moov-after-mdat",
                "The moov box follows the mdat box, so a player must read to the end of the file before it can play it (not optimized for streaming, no fast start).", state.MoovNode!);
        }
    }

    internal static string Latin1OrUtf8(byte[] bytes) =>
        System.Text.Unicode.Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : Encoding.Latin1.GetString(bytes);
}

// size: the bytes the box takes in its parent, IsToEnd for size 0 and IsLarge for a 64-bit size.
internal readonly record struct BoxHeader(long Offset, long Size, string Type, int HeaderLength, bool IsToEnd, bool IsLarge)
{
    public long End => Offset + Size;

    public long PayloadStart => Offset + HeaderLength;
}

// How a box's node is named and described: by its type, or for an item of an item list by what the
// item is.
internal readonly record struct BoxName(string Name, NodeKind Kind, string Description, string? Topic);
