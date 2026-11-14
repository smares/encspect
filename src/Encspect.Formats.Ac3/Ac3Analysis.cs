using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Encspect.Core;

namespace Encspect.Formats.Ac3;

// One analysis of AC-3 and E-AC-3 frames, whether a raw file's (Ac3Parser) or a container's
// packets (Ac3CodecParser): each frame is read field by field once (FrameSyntax), checked against
// its CRCs, and kept as a compact record with the fields its nodes are built from; findings of a
// kind are counted once per file; the frames are grouped into programs (an independent substream
// and its dependent substreams, Annex E 2.3.1.2), and each program's run of one configuration is a
// stream with its statistics and series.
internal sealed class Ac3Analysis
{
    public const int MaxFrame = 4096;

    public const int MaxStreams = 64;

    private readonly FrameBits _bits = FrameBits.ForAnalysis();
    private readonly FrameSyntax _syntax;
    private readonly byte[] _buffer = new byte[MaxFrame];
    // A stream is a run of the frames of one independent substream (id 0 for AC-3) with one configuration; a change
    // ends it and starts another, and a returning configuration starts a new one. The number of streams is capped so that
    // a file which changes all the time cannot grow the report: a frame that would start a stream past the cap is in no
    // stream (ac-3.streams-limited), and so is every later frame of its substream, which never gets a stream. The other
    // substreams keep theirs, as they play at the same time.
    private readonly Dictionary<int, Program?> _active = [];
    private readonly Dictionary<int, ProgramKey> _lastKeys = [];
    private readonly List<Program> _programs = [];
    private bool _isIgnoring;
    private readonly AnalysisContext _context;
    private Program? _current;
    private int _currentType;
    private int _blocksOfSubstream0;
    private int _frameIndex;
    private long _frameOffset;
    private int _frameLength;
    private AccessUnit _unit = new();
    private readonly IntWriter _writer = new();
    private readonly Node? _streamNode;
    private int _gaps;

    // streamNode: the node findings of a decoding point to where the stream has none of its own yet (a raw file's root, a container's track).
    public Ac3Analysis(AnalysisContext context, Action<long, int, int>? recordMismatch = null, Node? streamNode = null)
    {
        _context = context;
        _streamNode = streamNode;
        Records = new FrameRecords(Frames, Store, recordMismatch);
        _syntax = new FrameSyntax(_bits) { IssueFound = OnIssue, IssueCounted = issue => Findings.CountIfKnown(CodeOf(issue)), Lent = _writer };
    }

    // Makes the node of a region that has none when its stream starts a session: the session's stream node must be the one StreamInfo.Nodes names first, or the host
    // finds no measurements for the stream (a container's region node is otherwise made after its frames are read).
    public Func<SyncFrame, Node>? RegionNodeFactory { get; set; }

    public ChunkedList<Frame> Frames { get; } = new();

    public ByteStore Store { get; } = new();

    // What frame nodes are built from, apart from the analysis, whose syntax and bit reader change
    // with every frame.
    public FrameRecords Records { get; }

    public List<Region> Regions { get; } = [];

    public Findings Findings { get; } = new();

    public CancellationToken Cancellation => _context.CancellationToken;

    // What reading the last frame found.
    public FrameResult LastResult => _syntax.Result;

    // Reads one frame of length bytes from bytes, which hold available of them.
    public void AddFrame(Region region, long offset, ReadOnlySpan<byte> bytes, SyncFrame header, bool cut = false, bool truncated = false, bool inSample = false)
    {
        if (!header.IsDependent)
        {
            // The previous access unit is complete: its configuration, dependent substreams included, decides its program.
            CommitUnit();
        }

        int available = Math.Min(bytes.Length, header.FrameLength);
        bytes[..available].CopyTo(_buffer);
        int index = Frames.Count;
        _frameIndex = index;
        _frameOffset = offset;
        bool shortened = available < header.FrameLength;
        DecodeFeed? feed = PrepareFeed(region, offset, header);
        // A dependent frame's records go to the unit of the program in progress (E-AC-3: Annex E 2.3.1.2); a dependent frame after an AC-3 core is not decoded, but its CRC joins the core's crc2 tally.
        DecodeFeed? dependentFeed = header.IsDependent ? (_pending.Count > 0 && !_pending[0].Header.IsDependent ? _pending[0].Feed : _current?.Feed) : null;
        _writer.IsEnabled = feed is { Session.IsDecoding: true } || dependentFeed is { Session.IsDecoding: true };
        CrcState crc1 = CrcState.NotChecked;
        CrcState crc2 = CrcState.NotChecked;
        if (!shortened)
        {
            if (!header.IsEac3)
            {
                int words = header.FrameSizeWords;
                int fiveEighths = ((words >> 1) + (words >> 3)) * 2;
                crc1 = Crc16.Compute(_buffer.AsSpan(2, fiveEighths - 2)) == 0 ? CrcState.Verified : CrcState.Mismatch;
            }

            crc2 = Crc16.Compute(_buffer.AsSpan(2, available - 2)) == 0 ? CrcState.Verified : CrcState.Mismatch;
        }

        _syntax.Analyze(_buffer, available, header);
        FrameResult result = _syntax.Result;
        // The record's prefix: the length where the frame is shorter than its header says, and
        // the header where the kept bits do not start with it.
        ReadOnlySpan<byte> kept = _bits.Kept;
        bool headerKept = kept.Length >= SyncFrame.Length && kept[..SyncFrame.Length].SequenceEqual(_buffer.AsSpan(0, SyncFrame.Length));
        Span<byte> prefix = stackalloc byte[2 + SyncFrame.Length];
        int used = 0;
        if (shortened)
        {
            BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)available);
            used = 2;
        }

        if (!headerKept)
        {
            BinaryPrimitives.WriteUInt64BigEndian(prefix[used..], header.Raw);
            used += SyncFrame.Length;
        }

        uint store = Store.Add(prefix[..used], kept);
        int flags = (int)crc1 | ((int)crc2 << 2) | (shortened ? Frame.Shortened : 0) | (cut ? Frame.Cut : 0) | (headerKept ? Frame.HeaderKept : 0);
        Frames.Add(Frame.Of(offset, store, _bits.KeptBits, flags));
        _frameLength = available;

        Node FrameAt() => new("Frame", offset, available) { Kind = NodeKind.Frame };
        if (truncated && inSample)
        {
            // Any sample of a container can end inside its frame, so these are counted.
            int have = available;
            int need = header.FrameLength;
            Findings.Add(Severity.Warning, "ac-3.frame-truncated", FrameAt, count =>
                $"{(count == 1 ? "A sample ends inside its frame" : $"{count} samples end inside their frames")}, first the frame at 0x{offset:X} with {have} of its {need} bytes.");
        }
        else if (truncated)
        {
            Findings.AddNow(Severity.Warning, "ac-3.frame-truncated", $"The last frame at 0x{offset:X} has {available} of its {Counts.Of(header.FrameLength, "byte", "bytes")}.", FrameAt());
        }

        if (cut)
        {
            Findings.Add(Severity.Warning, "ac-3.frame-damaged", FrameAt, count =>
                $"{(count == 1 ? "A frame is" : $"{count} frames are")} cut short by the next frame's sync word, first at 0x{offset:X}: their header or data is damaged, or bytes are missing.");
        }

        if (crc1 == CrcState.Mismatch)
        {
            Findings.Add(Severity.Warning, "ac-3.crc1-mismatch", () => new Node("CRC1", offset + 2, 2), count =>
                $"crc1, which covers the first 5/8 of a frame (A/52 7.10.1), fails in {Count(count)}, first at 0x{offset:X}; decoders that check it drop the frame.");
        }

        if (crc2 == CrcState.Mismatch)
        {
            Findings.Add(Severity.Warning, header.IsEac3 ? "ac-3.crc-mismatch" : "ac-3.crc2-mismatch", () => new Node("CRC2", offset + available - 2, 2), count =>
                header.IsEac3
                    ? $"The CRC that covers the whole frame (A/52 Annex E 3.2) fails in {Count(count)}, first at 0x{offset:X}; E-AC-3 decoders drop the frame."
                    : $"crc2, which covers the whole frame (A/52 7.10.1), fails in {Count(count)}, first at 0x{offset:X}; decoders that check it drop the frame.");
        }

        if (!header.IsDependent)
        {
            FinishUnit();
        }

        _unit.Add(offset, result);
        bool isReadable = !shortened && !cut && result.Stop == StopReason.None && (header.IsEac3 || header.Bsid <= 8);
        bool keeps = _writer.IsEnabled || (feed is not null && feed.Session.IsDecoding);
        _pending.Add(new PendingFrame(region, index, offset, available, header, result.Clone(), crc1, crc2, shortened, cut, isReadable, feed, _buffer.AsSpan(0, SyncFrame.Length).ToArray(),
            keeps ? _buffer.AsSpan(0, available).ToArray() : [], _writer.IsEnabled ? _writer.Written.ToArray() : []));
        if (_pending.Count >= MaxPendingFrames)
        {
            CommitUnit();
        }
    }

    // The most frames of an access unit held back for their program's decision; a unit with more is decided on these (its dependent substream IDs repeat, which is reported).
    private const int MaxPendingFrames = 16;

    // The held frames keep copies of their bytes (only where a decoding is running) and records: AddFrame is given a span of its caller's buffer, which the caller refills with
    // the next frame (a raw file's read buffer, a container's packet), so a range into it cannot outlive the call, and the parser has no mapping of the input to point into. The cost
    // against main's structure pass is within noise (a repeated 11 MB AC-3 file 1.92 s on main, 1.89 s here; 9.8 MB of E-AC-3 1.82 s and 1.91 s; the bound was 1.5).
    private readonly List<PendingFrame> _pending = [];

    private sealed record PendingFrame(Region Region, int Index, long Offset, int Available, SyncFrame Header, FrameResult Result, CrcState Crc1, CrcState Crc2, bool Shortened, bool Cut,
        bool IsReadable, DecodeFeed? Feed, byte[] HeaderBytes, byte[] Bytes, int[] Records, bool DependentsLost = false);

    // Decides the program of the access unit held back (an independent frame and the dependent frames after it): the dependent substreams' configuration is part of the program's key, so
    // a dependent substream that appears, disappears or changes starts a new stream as an independent one does.
    private void CommitUnit(bool endsAtGap = false)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        PendingFrame[] frames = [.. _pending];
        _pending.Clear();
        // A program frame holds each dependent substream ID once (Annex E 2.3.1.2): a repeated ID is left out of the figures and of the configuration.
        string dependents = string.Join(';', frames.Where(f => f.Header.IsDependent).DistinctBy(f => f.Header.SubstreamId).Select(f =>
            $"{f.Header.SubstreamId}:{(f.Result.HasChannelMap ? f.Result.ChannelMap.ToString("X4", System.Globalization.CultureInfo.InvariantCulture) : "-")}:{f.Header.Acmod}:{(f.Header.Lfeon ? 1 : 0)}"));
        bool lost = false;
        if (endsAtGap && !frames[0].Header.IsDependent && _active.TryGetValue(frames[0].Header.IsEac3 ? frames[0].Header.SubstreamId : 0, out Program? current) && current is not null
            && current.Key.Dependents.Length > 0 && current.Key.Independent() == new ProgramKey(current.Key.Id, frames[0].Header.SampleRate, frames[0].Header.IsEac3, frames[0].Header.Acmod, frames[0].Header.Lfeon))
        {
            // A program frame cut by a gap or the end of the data has lost dependent frames, not changed its configuration: it stays in its program, which counts its samples lost.
            HashSet<string> wanted = [.. current.Key.Dependents.Split(';')];
            if (dependents.Length == 0 || !dependents.Split(';').ToHashSet().SetEquals(wanted))
            {
                lost = wanted.IsSupersetOf(dependents.Length == 0 ? [] : dependents.Split(';'));
                dependents = lost ? current.Key.Dependents : dependents;
            }
        }

        // A dependent frame of another block count or rate than its independent frame makes the program frame lost too.
        bool differs = !frames[0].Header.IsDependent && frames.Any(f => f.Header.IsDependent && (f.Header.Blocks != frames[0].Header.Blocks || f.Header.SampleRate != frames[0].Header.SampleRate));
        for (int i = 0; i < frames.Length; i++)
        {
            PendingFrame frame = frames[i];
            // The program frame lost is counted in the program's lost samples as well as in the decoder's, so the stream's verdict does not vouch for it.
            PendingFrame given = (lost || differs) && i == 0 ? frame with { IsReadable = false, DependentsLost = true } : frame;
            if (frame.Header.IsDependent && !frames[0].Header.IsDependent && (frame.Header.Blocks != frames[0].Header.Blocks || frame.Header.SampleRate != frames[0].Header.SampleRate))
            {
                // A dependent frame of another length or rate cannot be merged with its independent frame (Annex E 2.3.1.2): the program frame is not decoded.
                SyncFrame dependentHeader = frame.Header;
                SyncFrame independentHeader = frames[0].Header;
                long at = frame.Offset;
                Findings.Add(Severity.Warning, "ac-3.dependent-format-differs", () => new Node("Frame", at, frame.Available) { Kind = NodeKind.Frame }, count =>
                    $"{Count(count)} of a dependent substream {Counts.Agree(count, "has", "have")} {dependentHeader.Blocks} audio blocks at {dependentHeader.SampleRate} Hz where the independent frame before {Counts.Agree(count, "has", "have")} {independentHeader.Blocks} at {independentHeader.SampleRate} Hz (A/52 Annex E 2.3.1.2 gives a program frame one block count and sample rate); the first is at 0x{at:X}, and the program frame is not decoded.");
                given = frame with { IsReadable = false };
            }

            Process(given, frame.Header.IsDependent ? string.Empty : dependents);
        }
    }

    private void Process(PendingFrame p, string dependents)
    {
        SyncFrame header = p.Header;
        FrameResult result = p.Result;
        _frameIndex = p.Index;
        _frameOffset = p.Offset;
        _frameLength = p.Available;
        long offset = p.Offset;
        int available = p.Available;
        Node FrameAt() => new("Frame", offset, available) { Kind = NodeKind.Frame };
        Program? program = Assign(p.Region, p.Index, header, result, dependents);
        if (program is null)
        {
            return;
        }

        DecodeFeed? feed = p.Feed;
        if (!header.IsDependent && program.FirstFrame == p.Index && program.Feed is null)
        {
            if (feed is not null && _programs.Any(o => o != program && ReferenceEquals(o.Feed, feed)))
            {
                // Only the dependent substreams differ from the program this feed belongs to: the new program is a new decoding session.
                p.Region.Node ??= RegionNodeFactory?.Invoke(header);
                feed = new DecodeFeed(_context, p.Region.Node ?? _streamNode ?? throw new InvalidOperationException("A decoding needs a node for the stream"), header, p.HeaderBytes, offset) { GapsSeen = _gaps };
            }

            program.Feed = feed;
        }

        if (!header.IsDependent && dependents.Length > 0 && program.Feed is { IsEac3: false } core)
        {
            core.HasDependents = true;
        }

        if (program.Feed is { } fed && !header.IsDependent)
        {
            fed.Feed(p.Records, offset, p.Bytes, header, p.IsReadable, p.Crc1, p.Crc2);
        }
        else if (header.IsDependent && program == _current && program.Feed is { } dependentFeed)
        {
            dependentFeed.AddDependent(p.Records, header, result, p.Bytes, p.IsReadable, p.Crc2);
        }

        program.Cover(p.Region, offset, available);
        if (program.Add(p.Index, offset, available, header, result, p.Crc1 == CrcState.Mismatch || p.Crc2 == CrcState.Mismatch,
                p.Shortened || p.Cut || p.DependentsLost || result.Stop is StopReason.Invalid or StopReason.Overrun, p.Shortened || p.Cut) && !Findings.CountIfKnown("ac-3.dependent-excess"))
        {
            int id = header.SubstreamId;
            Findings.Add(Severity.Warning, "ac-3.dependent-excess", FrameAt, count =>
                $"{(count == 1 ? "1 dependent frame is" : $"{count} dependent frames are")} left out of the figures and statistics after a dependent substream ID repeats within a program frame (A/52 Annex E 2.3.1.2 allows each ID once, at most 8, after an independent frame); the first is of substream {id} at 0x{offset:X}.");
        }
    }

    // The program in progress follows a gap: what lies between its frames is no frame.
    public void NoteGap() => _gaps++;

    // The feed a frame goes to: the one of the program it continues, or a new one (with its session) where it starts a program, as Assign will decide. Dependent frames,
    // E-AC-3 at a reduced sample rate and the frames of bsid 9 and 10 start none.
    private DecodeFeed? PrepareFeed(Region region, long offset, SyncFrame header)
    {
        if (header.IsDependent)
        {
            return null;
        }

        int id = header.IsEac3 ? header.SubstreamId : 0;
        var key = new ProgramKey(id, header.SampleRate, header.IsEac3, header.Acmod, header.Lfeon);
        DecodeFeed? feed;
        if (_lastKeys.TryGetValue(id, out ProgramKey last) && last.Independent() == key)
        {
            feed = _active.TryGetValue(id, out Program? program) ? program?.Feed : null;
            if (feed is not null && feed.GapsSeen != _gaps)
            {
                feed.PendingGap = true;
                feed.GapsSeen = _gaps;
            }
        }
        else if (_programs.Count < MaxStreams && (header.IsEac3 ? header.Fscod != 3 : header.Bsid <= 8))
        {
            region.Node ??= RegionNodeFactory?.Invoke(header);
            feed = new DecodeFeed(_context, region.Node ?? _streamNode ?? throw new InvalidOperationException("A decoding needs a node for the stream"), header, _buffer, offset)
            {
                GapsSeen = _gaps,
            };
        }
        else
        {
            feed = null;
        }

        return feed;
    }

    public void AddGap(Region region, long start, long end, bool leading, bool trailing, BlockCache cache)
    {
        CommitUnit(endsAtGap: true);
        // The dependent frames after a gap belong to an independent frame the gap took.
        _current = null;
        _gaps += !leading && !trailing ? 1 : 0;
        Frames.Add(Frame.Gap(start, end - start));
        var node = new Node("Gap", start, end - start, NodeValue.Integer(end - start, "bytes")) { Description = Explain.Gap };
        long length = end - start;
        if (leading)
        {
            bool zeros = true;
            for (long at = start; at < end && zeros; at++)
            {
                zeros = cache.ByteAt(at) == 0;
            }

            Findings.AddNow(Severity.Warning, "ac-3.leading-junk", $"{Counts.Of(length, "byte", "bytes")} before the first frame {Counts.Agree(length, "is", "are")} not audio{(zeros ? " (all zero bytes)" : string.Empty)}.", node);
        }
        else if (trailing)
        {
            Findings.AddNow(Severity.Warning, "ac-3.trailing-data", $"{Counts.Of(length, "byte", "bytes")} after the last frame at 0x{start:X} {Counts.Agree(length, "is", "are")} not a frame.", node);
        }
        else
        {
            Findings.AddNow(Severity.Warning, "ac-3.sync-lost", $"Sync is lost for {Counts.Of(length, "byte", "bytes")} at 0x{start:X}.", node);
        }
    }

    private static string Count(int count) => count == 1 ? "1 frame" : $"{count} frames";

    private static string CodeOf(Issue issue) => issue switch
    {
        Issue.ReservedValue => "ac-3.reserved-value",
        Issue.DialnormReserved => "ac-3.dialnorm-reserved",
        Issue.LanguageCode => "ac-3.language-code",
        Issue.BlockInconsistent => "ac-3.block-inconsistent",
        Issue.BlockInvalid => "ac-3.block-invalid",
        Issue.Overrun => "ac-3.block-overrun",
        Issue.HeaderOverrun => "ac-3.header-overrun",
        Issue.UndefinedBsid => "ac-3.bsid-undefined",
        Issue.ReducedRate => "ac-3.reduced-rate-not-read",
        Issue.MixDataOverrun => "ac-3.mix-data-overrun",
        Issue.ChannelMapCount => "ac-3.channel-map-count",
        Issue.EmdfTruncated => "ac-3.emdf-truncated",
        Issue.EmdfLength => "ac-3.emdf-length",
        Issue.EmdfPayloadSize => "ac-3.emdf-payload-size",
        Issue.EmdfReserved => "ac-3.emdf-reserved",
        Issue.EmdfVersion => "ac-3.emdf-version-not-read",
        Issue.PayloadShort => "ac-3.object-payload-short",
        Issue.JocReserved => "ac-3.joc-reserved",
        Issue.ObjectAudioIncomplete => "ac-3.object-audio-incomplete",
        Issue.EmdfLimit => "ac-3.emdf-limit",
        Issue.DownmixModeReserved => "ac-3.dmixmod-reserved",
        Issue.MantissaNoValue => "ac-3.mantissa-code-invalid",
        _ => "ac-3.auxiliary-length",
    };

    private void OnIssue(Issue issue, string field, int start, int end, string detail)
    {
        long offset = _frameOffset;
        Node Field() => Node.FromBits(field, offset, start, end);
        string at = $"the frame at 0x{offset:X}";
        switch (issue)
        {
            case Issue.ReservedValue:
                Findings.Add(Severity.Warning, CodeOf(issue), Field, count =>
                    $"{field} holds a value A/52 reserves, first in {at} ({Times(count)}).");
                break;
            case Issue.DialnormReserved:
                Findings.Add(Severity.Warning, CodeOf(issue), Field, count =>
                    $"dialnorm is 0, which A/52 5.4.2.8 reserves; decoders read it as -31 dB. First in {at} ({Times(count)}).");
                break;
            case Issue.LanguageCode:
                Findings.Add(Severity.Info, CodeOf(issue), Field, count =>
                    $"langcod is not 0xFF, the value A/52:2018 5.4.2.12 gives the reserved field; earlier editions coded a language there. First in {at} ({Times(count)}).");
                break;
            case Issue.BlockInconsistent:
                Findings.Add(Severity.Warning, CodeOf(issue), Field, count =>
                    $"An audio block breaks a rule that A/52 7.10.2 lists among the errors a decoder may check for: {detail}. First in {at} ({Times(count)}).");
                break;
            case Issue.BlockInvalid:
                Findings.Add(Severity.Warning, CodeOf(issue), Field, count =>
                    $"An audio block holds what A/52 does not allow: {detail}. Decoders cannot decode the rest of such a frame, and its remaining audio blocks are not read. First in {at} ({Times(count)}).");
                break;
            case Issue.Overrun:
                Findings.Add(Severity.Warning, CodeOf(issue), Field, count =>
                    $"An audio block runs past the end of its frame: {detail} (A/52 7.10.2, check 21). First in {at} ({Times(count)}).");
                break;
            case Issue.HeaderOverrun:
                Findings.Add(Severity.Warning, CodeOf(issue), Field, count =>
                    $"The frame is too short for its own fields: {detail}, so its audio blocks are not read (A/52 7.10.2, check 21). First in {at} ({Times(count)}).");
                break;
            case Issue.UndefinedBsid:
                Findings.Add(Severity.Info, CodeOf(issue), Field, count =>
                    $"bsid 9 or 10 is not defined by A/52, whose Annex E 2.3.1.6 says decoders mute it; ffmpeg and liba52 decode it as AC-3 at half or a quarter of the sample rate, as the report does. Its audio blocks are not read. First in {at} ({Times(count)}).");
                break;
            case Issue.ReducedRate:
                Findings.Add(Severity.Info, CodeOf(issue), Field, count =>
                    $"The frame is coded at a reduced sample rate (fscod2), for which A/52 does not specify the bit allocation, so its audio blocks are not read. First in {at} ({Times(count)}).");
                break;
            case Issue.MixDataOverrun:
                Findings.Add(Severity.Warning, CodeOf(issue), Field, count =>
                    $"The mixing parameters run past the length mixdeflen gives: {detail}; decoders skip that length. First in {at} ({Times(count)}).");
                break;
            case Issue.ChannelMapCount:
                Findings.Add(Severity.Warning, CodeOf(issue), Field, count =>
                    $"In a dependent substream {detail} (A/52 Annex E 2.3.1.8 requires them to match). First in {at} ({Times(count)}).");
                break;
            case Issue.EmdfTruncated:
                Findings.Add(Severity.Warning, CodeOf(issue), Field, count =>
                    $"An EMDF container is cut short: {detail} (ETSI TS 102 366 H.2.2.1.2). First in {at} ({Times(count)}).");
                break;
            case Issue.EmdfLength:
                Findings.Add(Severity.Warning, CodeOf(issue), Field, count =>
                    $"An EMDF container does not end where emdf_container_length says: {detail} (ETSI TS 102 366 H.2.2.1.2). First in {at} ({Times(count)}).");
                break;
            case Issue.EmdfPayloadSize:
                Findings.Add(Severity.Warning, CodeOf(issue), Field, count =>
                    $"An EMDF payload runs past its container: {detail} (ETSI TS 102 366 H.2.2.2.5). The rest of the container is not read. First in {at} ({Times(count)}).");
                break;
            case Issue.EmdfReserved:
                Findings.Add(Severity.Warning, CodeOf(issue), Field, count =>
                    $"An EMDF container holds what ETSI TS 102 366 Annex H does not allow: {detail}. First in {at} ({Times(count)}).");
                break;
            case Issue.EmdfVersion:
                Findings.Add(Severity.Info, CodeOf(issue), Field, count =>
                    $"An EMDF container has a version Annex H does not define ({detail}), so its payloads are not read. First in {at} ({Times(count)}).");
                break;
            case Issue.PayloadShort:
                Findings.Add(Severity.Warning, CodeOf(issue), Field, count =>
                    $"An object audio payload is too short for its first fields: {detail}. First in {at} ({Times(count)}).");
                break;
            case Issue.JocReserved:
                Findings.Add(Severity.Warning, CodeOf(issue), Field, count =>
                    $"A joint object coding header holds a reserved or out-of-range value: {detail}. First in {at} ({Times(count)}).");
                break;
            case Issue.ObjectAudioIncomplete:
                Findings.Add(Severity.Warning, CodeOf(issue), Field, count =>
                    $"An EMDF container has only one of the two payloads of object audio: {detail}; ETSI TS 103 420 8.2 carries the JOC and the OAMD payload together. First in {at} ({Times(count)}).");
                break;
            case Issue.DownmixModeReserved:
                Findings.Add(Severity.Info, CodeOf(issue), Field, count =>
                    $"dmixmod is 3, which A/52 Annex D Table D2.2 reserves (E-AC-3's mixing metadata has the same field and no table of its own). The decoder should still reproduce audio and may read it as not indicated; encoders use it to indicate Dolby Pro Logic II, as ffmpeg's ac3defs.h notes, and Dolby's own files write it. First in {at} ({Times(count)}).");
                break;
            case Issue.MantissaNoValue:
                Findings.Add(Severity.Warning, CodeOf(issue), Field, count =>
                    $"An audio block holds a mantissa code that its quantizer has no value for: {detail}. The decoder limits it to the quantizer's highest value. First in {at} ({Times(count)}).");
                break;
            case Issue.EmdfLimit:
                Findings.Add(Severity.Info, CodeOf(issue), Field, count =>
                    $"An EMDF container goes beyond what the analyzer reads: {detail}, so the rest of the container is not read. First in {at} ({Times(count)}).");
                break;
            case Issue.AuxiliaryLength:
                Findings.Add(Severity.Warning, CodeOf(issue), Field, count =>
                    $"{detail} (A/52 5.4.4.2). First in {at} ({Times(count)}).");
                break;
        }
    }

    // The extension flag and the JOC payload are judged over an access unit, an independent frame and
    // the dependent frames after it: ETSI TS 103 420 8.3.1 puts the flag in the substream that holds the
    // container, and ffmpeg reads it from either. A unit whose frames were not all read to their end
    // may hold a container in the bits that were not.
    private void FinishUnit()
    {
        AccessUnit unit = _unit;
        _unit = new();
        if (unit.ExtensionOffset >= 0 && unit.JocOffset < 0 && unit.AllRead)
        {
            Findings.Add(Severity.Warning, "ac-3.extension-without-joc", () => Node.FromBits("HasExtensionTypeA", unit.ExtensionOffset, unit.ExtensionStart, unit.ExtensionEnd), count =>
                $"{Units(count)}: the additional bit stream information of a frame sets flag_ec3_extension_type_a, but no EMDF container in the frames holds a joint object coding payload (ETSI TS 103 420 8.3); the first is at 0x{unit.ExtensionOffset:X}.");
        }
        else if (unit.JocOffset >= 0 && unit.ExtensionOffset < 0)
        {
            Findings.Add(Severity.Warning, "ac-3.joc-without-extension", () => Node.FromBits("Payload", unit.JocOffset, unit.JocStart, unit.JocEnd), count =>
                $"{Units(count)}: a frame holds a joint object coding payload, but none of the frames sets flag_ec3_extension_type_a in its additional bit stream information (ETSI TS 103 420 8.3.1); the first is at 0x{unit.JocOffset:X}.");
        }
    }

    private static string Units(int count) => count == 1 ? "1 access unit (an independent frame and its dependent frames)" : $"{count} access units (independent frames and their dependent frames)";

    private sealed class AccessUnit
    {
        public long ExtensionOffset { get; private set; } = -1;

        public int ExtensionStart { get; private set; }

        public int ExtensionEnd { get; private set; }

        public long JocOffset { get; private set; } = -1;

        public int JocStart { get; private set; }

        public int JocEnd { get; private set; }

        public bool AllRead { get; private set; } = true;

        public void Add(long offset, FrameResult result)
        {
            if (result.ExtensionFlag == 1 && ExtensionOffset < 0)
            {
                (ExtensionOffset, ExtensionStart, ExtensionEnd) = (offset, result.ExtensionStart, result.ExtensionEnd);
            }

            if (result.HasJoc && JocOffset < 0)
            {
                (JocOffset, JocStart, JocEnd) = (offset, result.JocStart, result.JocEnd);
            }

            AllRead &= result.Stop == StopReason.None;
        }
    }

    private static string Times(int count) => count == 1 ? "once" : $"{count} times";

    // Independent substreams (and AC-3 frames) start a program frame; dependent substreams join the
    // program of the independent frame before them.
    private Program? Assign(Region region, int index, SyncFrame header, FrameResult result, string dependents)
    {
        long frameOffset = _frameOffset;
        int frameLength = _frameLength;
        Node FrameAt() => new("Frame", frameOffset, frameLength) { Kind = NodeKind.Frame };
        if (header.IsDependent)
        {
            if (_current is null)
            {
                if (_isIgnoring)
                {
                    Findings.CountIfKnown("ac-3.streams-limited");
                    return null;
                }

                Findings.Add(Severity.Warning, "ac-3.dependent-without-independent", () => FrameAt(),
                    count => $"{Count(count)} of a dependent substream {Counts.Agree(count, "comes", "come")} before any independent frame they could belong to, or after a gap that took it (A/52 Annex E 2.3.1.2); the first is at 0x{frameOffset:X}.");
                return null;
            }

            if (_currentType == 2)
            {
                Findings.Add(Severity.Warning, "ac-3.dependent-after-converted", () => FrameAt(),
                    count => $"{Count(count)} of a dependent substream {Counts.Agree(count, "follows", "follow")} an independent substream of stream type 2, converted from AC-3, which may have none (A/52 Annex E 2.3.1.1); the first is at 0x{frameOffset:X}.");
            }

            _current.Regions.Add(region);
            return _current;
        }

        int id = header.IsEac3 ? header.SubstreamId : 0;
        _currentType = header.IsEac3 ? header.StreamType : 0;
        if (id == 0)
        {
            _blocksOfSubstream0 = header.Blocks;
        }
        else if (_blocksOfSubstream0 != 0 && header.Blocks != _blocksOfSubstream0)
        {
            int blocks = header.Blocks;
            int expected = _blocksOfSubstream0;
            Findings.Add(Severity.Warning, "ac-3.substream-blocks-differ", () => FrameAt(),
                count => $"{Count(count)} of independent substream {id} hold {blocks} audio blocks where substream 0's hold {expected} (A/52 Annex E 2.3.1.2 requires the same number); the first is at 0x{frameOffset:X}.");
        }

        var key = new ProgramKey(id, header.SampleRate, header.IsEac3, header.Acmod, header.Lfeon, dependents);
        Program? program;
        if (!_lastKeys.TryGetValue(id, out ProgramKey lastKey) || lastKey != key)
        {
            bool changed = _active.ContainsKey(id);
            _lastKeys[id] = key;
            if (_programs.Count < MaxStreams)
            {
                program = new Program(key, index);
                _programs.Add(program);
            }
            else
            {
                program = null;
            }

            _active[id] = program;

            if (changed && !Findings.CountIfKnown("ac-3.stream-changed"))
            {
                string substream = header.IsEac3 ? $" of independent substream {id}" : string.Empty;
                string from = lastKey.Describe();
                string to = key.Describe();
                Findings.Add(Severity.Info, "ac-3.stream-changed", FrameAt,
                    count => $"The stream{substream} changes at 0x{frameOffset:X} from {from} to {to}{(count > 1 ? $"; the streams change {Counts.Of(count, "time", "times")} in the file" : string.Empty)}.");
            }
        }
        else
        {
            program = _active[id];
        }

        if (program is null)
        {
            if (!Findings.CountIfKnown("ac-3.streams-limited"))
            {
                Findings.Add(Severity.Warning, "ac-3.streams-limited", FrameAt,
                    count => $"{Count(count)}, dependent frames included, are in no stream: the analyzer keeps at most {MaxStreams} streams, one per run of an independent substream with one configuration, and these frames start another; the first is at 0x{frameOffset:X}.");
            }

            _current = null;
            _isIgnoring = true;
            return null;
        }

        _isIgnoring = false;
        program.Regions.Add(region);
        _current = program;
        return program;
    }

    // The streams, in the order their first frames appear, with each one's statistics in the first
    // region it spans.
    public void Finish(IReadOnlyList<EncoderClaim> claims)
    {
        CommitUnit(endsAtGap: true);
        FinishUnit();
        List<Program> programs = _programs;
        int ids = programs.Select(p => p.Key.Id).Distinct().Count();
        bool several = ids > 1 || programs.Any(p => p.Key.IsEac3);
        // The independent substreams of an E-AC-3 stream play at the same time (Annex E 2.3.1.2), so a summary
        // counts the longest once. Where a substream changes its configuration, its programs follow one
        // another and the others run beside them: no one number says that, so none is given. A container reads
        // the number as "these play together" and replaces it with the group of its track.
        int? group = programs.Count > 1 && ids == programs.Count ? 1 : null;
        foreach (Program program in programs)
        {
            Cancellation.ThrowIfCancellationRequested();
            program.Close();
            AddStream(program, claims, several, group);
        }

        // The frames' nodes keep this analysis alive; the programs' per-frame figures are in the
        // streams' series now.
        _programs.Clear();
        _active.Clear();
        _lastKeys.Clear();
        _current = null;
        Findings.Flush(_context);
    }

    private void AddStream(Program program, IReadOnlyList<EncoderClaim> claims, bool named, int? group)
    {
        Region first = program.FirstRegion!;
        // The nodes of a program cover its own frames, so that finding the node of a frame does not
        // pass through every program's nodes. Where the region's bytes are not contiguous (a container's
        // samples), the region's ranges stay.
        (long coverStart, long coverLength) = first.Node!.Ranges is null && program.SpanEnd > program.SpanStart
            ? (program.SpanStart, program.SpanEnd - program.SpanStart) : (first.Node.Offset, first.Node.Length);
        Node Cover(string name, NodeValue? value, NodeKind kind, string description, string? topic = null) =>
            new(name, coverStart, coverLength, value) { Kind = kind, Ranges = first.Node.Ranges, Description = description, Topic = topic };

        Node? encoder = Ac3Encoder.Detect(claims, (name, value, description) => Cover(name, value, NodeKind.Detail, description, ClaimRoles.TopicOf(name)),
            (claim, text) => Findings.Add(Severity.Info, "ac-3.encoder-claim", () => claim.Source, _ =>
                $"The {claim.Description} names \"{text}\"; nothing in the stream confirms or contradicts it."));
        if (encoder is not null)
        {
            first.Leading.Add(encoder);
        }

        Node? writer = WriterDetection.Detect(claims, (name, value, description) => Cover(name, value, NodeKind.Detail, description, ClaimRoles.TopicOf(name)),
            (claim, text, name) => Findings.Add(Severity.Info, "ac-3.writer-claim-differs", () => claim.Source, _ => WriterDetection.Disagreement(claim, text, name)),
            format: new WriterDetection.StreamFormat(KnownWriters.Ac3, "AC-3", (claim, message, node) => Findings.Add(Severity.Info, "ac-3.writer-other-format", () => node, _ => message)));
        if (writer is not null)
        {
            first.Leading.Add(writer);
        }

        ClaimRoles.CompleteVersion(encoder, claims);

        Node statistics = program.Statistics(Cover, out Node bitrate, out CodingNodes coding);
        if (named)
        {
            statistics.Value = program.Key.IsEac3 ? $"independent substream {program.Key.Id}" : "AC-3";
        }

        first.Leading.Add(statistics);
        (string? encoderName, Confidence? encoderConfidence) = ClaimRoles.Resolved(encoder);
        (string? writerName, Confidence? writerConfidence) = ClaimRoles.Resolved(writer);
        List<Series> series = program.Figures.Series(bitrate, Cancellation);
        IReadOnlyList<CategorySeries> categories = program.Recorder.Build(series.Count > 0 ? series[0].X : Array.Empty<double>(), series.Count > 0 ? series[0] : null,
            program.Figures.Time, coding, series, Cancellation);
        _context.AddStream(new StreamInfo
        {
            Nodes = [.. Regions.Where(r => program.Regions.Contains(r)).Select(r => r.Node!)],
            Codec = program.Codec,
            SampleRate = program.Key.SampleRate,
            Channels = program.Layout.Count,
            ChannelMode = Ac3Names.CodingModeShort(program.Key.Acmod),
            ChannelLayout = program.Layout,
            Samples = program.Figures.Samples,
            LostSamples = program.LostSamples,
            Duration = program.Figures.Duration,
            AverageBitrate = program.Figures.AverageBitrate,
            IsVbr = program.Figures.IsVariable,
            HasObjectAudio = program.HasObjectAudio,
            JocObjectCount = program.JocObjectCount,
            Encoder = encoderName,
            EncoderConfidence = encoderConfidence,
            Writer = writerName,
            WriterConfidence = writerConfidence,
            Series = series,
            CategorySeries = categories,
            PlaybackGroup = group,
            Verification = program.Feed?.Finish(program.LostSamples),
        });
    }

    internal enum CrcState
    {
        NotChecked,
        Verified,
        Mismatch,
    }

    // A frame's record, 16 bytes: its offset, where its kept bits lie in the store, and a word of
    // the kept bits' count and flags. In the store, a prefix comes before the kept bits: the length
    // where the frame is shorter than its header says, and the header (the frame's first 8 bytes)
    // where the kept bits do not start with it. A gap keeps its length where a frame keeps its
    // store position.
    [StructLayout(LayoutKind.Auto)]
    internal readonly struct Frame(long offset, uint store, uint packed)
    {
        public const int Shortened = 16;
        public const int Cut = 32;
        public const int HeaderKept = 64;
        private const uint GapFlag = 1u << 31;

        public long Offset => offset;

        public uint Store => store;

        public bool IsGap => (packed & GapFlag) != 0;

        public int KeptBits => (int)(packed & 0xFFFF);

        private int Flags => (int)(packed >> 16) & 0xFF;

        public CrcState Crc1 => (CrcState)(Flags & 3);

        public CrcState Crc2 => (CrcState)((Flags >> 2) & 3);

        public bool IsShortened => (Flags & Shortened) != 0;

        public bool IsHeaderKept => (Flags & HeaderKept) != 0;

        public static Frame Of(long offset, uint store, int keptBits, int flags) => new(offset, store, (uint)keptBits | ((uint)flags << 16));

        public static Frame Gap(long offset, long length) => new(offset, (uint)Math.Min(length, uint.MaxValue), GapFlag);
    }

    internal sealed class Region(int first)
    {
        public int First => first;

        public int? End { get; set; }

        public Node? Node { get; set; }

        public List<Node> Leading { get; } = [];
    }

    internal readonly record struct ProgramKey(int Id, int SampleRate, bool IsEac3, int Acmod, bool Lfeon, string Dependents = "")
    {
        public ProgramKey Independent() => this with { Dependents = string.Empty };

        public string Describe() => $"{(IsEac3 ? "E-AC-3" : "AC-3")}, {SampleRate} Hz, {Ac3Names.CodingModeShort(Acmod)}{(Lfeon ? " + LFE" : string.Empty)}{(Dependents.Length == 0 ? string.Empty : ", " + DescribeDependents())}";

        // Each dependent substream as it was keyed (ID:channel map or -:coding mode:LFE), written out.
        private string DescribeDependents() => string.Join(", ", Dependents.Split(';').Select(entry =>
        {
            string[] parts = entry.Split(':');
            string map = parts[1] == "-" ? "no channel map" : $"channel map 0x{parts[1]}";
            return $"dependent substream {parts[0]} ({map}, {Ac3Names.CodingModeShort(int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture))}{(parts[3] == "1" ? " + LFE" : string.Empty)})";
        }));
    }
}
