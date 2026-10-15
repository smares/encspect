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

    private readonly FrameBits _bits = FrameBits.ForAnalysis();
    private readonly FrameSyntax _syntax;
    private readonly byte[] _buffer = new byte[MaxFrame];
    private readonly Dictionary<int, Program> _programs = [];
    private readonly List<Program> _finished = [];
    private readonly AnalysisContext _context;
    private Program? _current;
    private int _currentType;
    private int _blocksOfSubstream0;
    private int _frameIndex;
    private long _frameOffset;
    private int _frameLength;
    private AccessUnit _unit = new();

    public Ac3Analysis(AnalysisContext context, Action<long, int, int>? recordMismatch = null)
    {
        _context = context;
        Records = new FrameRecords(Frames, Store, recordMismatch);
        _syntax = new FrameSyntax(_bits) { IssueFound = OnIssue, IssueCounted = issue => Findings.CountIfKnown(CodeOf(issue)) };
    }

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
    public void AddFrame(Region region, long offset, ReadOnlySpan<byte> bytes, SyncFrame header, bool cut = false, bool truncated = false)
    {
        int available = Math.Min(bytes.Length, header.FrameLength);
        bytes[..available].CopyTo(_buffer);
        int index = Frames.Count;
        _frameIndex = index;
        _frameOffset = offset;
        bool shortened = available < header.FrameLength;
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
        if (truncated)
        {
            Findings.AddNow(Severity.Warning, "ac3.frame-truncated", $"The last frame at 0x{offset:X} has {available} of its {header.FrameLength} bytes.", FrameAt());
        }

        if (cut)
        {
            Findings.Add(Severity.Warning, "ac3.frame-damaged", FrameAt, count =>
                $"{(count == 1 ? "A frame is" : $"{count} frames are")} cut short by the next frame's sync word, first at 0x{offset:X}: their header or data is damaged, or bytes are missing.");
        }

        if (crc1 == CrcState.Mismatch)
        {
            Findings.Add(Severity.Warning, "ac3.crc1-mismatch", () => new Node("CRC1", offset + 2, 2), count =>
                $"crc1, which covers the first 5/8 of a frame (A/52 7.10.1), fails in {Count(count)}, first at 0x{offset:X}; decoders that check it drop the frame.");
        }

        if (crc2 == CrcState.Mismatch)
        {
            Findings.Add(Severity.Warning, header.IsEac3 ? "ac3.crc-mismatch" : "ac3.crc2-mismatch", () => new Node("CRC2", offset + available - 2, 2), count =>
                header.IsEac3
                    ? $"The CRC that covers the whole frame (A/52 Annex E 3.2) fails in {Count(count)}, first at 0x{offset:X}; E-AC-3 decoders drop the frame."
                    : $"crc2, which covers the whole frame (A/52 7.10.1), fails in {Count(count)}, first at 0x{offset:X}; decoders that check it drop the frame.");
        }

        if (!header.IsDependent)
        {
            FinishUnit();
        }

        _unit.Add(offset, result);
        Program? program = Assign(region, index, header, result);
        program?.Add(index, offset, available, header, result, crc1 == CrcState.Mismatch || crc2 == CrcState.Mismatch);
    }

    public void AddGap(Region region, long start, long end, bool leading, bool trailing, BlockCache cache)
    {
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

            Findings.AddNow(Severity.Warning, "ac3.leading-junk", $"{length} bytes before the first frame are not audio{(zeros ? " (all zero bytes)" : string.Empty)}.", node);
        }
        else if (trailing)
        {
            Findings.AddNow(Severity.Warning, "ac3.trailing-data", $"{length} bytes after the last frame at 0x{start:X} are not a frame.", node);
        }
        else
        {
            Findings.AddNow(Severity.Warning, "ac3.sync-lost", $"Sync is lost for {length} bytes at 0x{start:X}.", node);
        }
    }

    private static string Count(int count) => count == 1 ? "1 frame" : $"{count} frames";

    private static string CodeOf(Issue issue) => issue switch
    {
        Issue.ReservedValue => "ac3.reserved-value",
        Issue.DialnormReserved => "ac3.dialnorm-reserved",
        Issue.LanguageCode => "ac3.language-code",
        Issue.BlockInconsistent => "ac3.block-inconsistent",
        Issue.BlockInvalid => "ac3.block-invalid",
        Issue.Overrun => "ac3.block-overrun",
        Issue.HeaderOverrun => "ac3.header-overrun",
        Issue.UndefinedBsid => "ac3.bsid-undefined",
        Issue.ReducedRate => "ac3.reduced-rate-not-read",
        Issue.Aht => "ac3.aht-not-read",
        Issue.EnhancedCoupling => "ac3.enhanced-coupling-not-read",
        Issue.MixDataOverrun => "ac3.mix-data-overrun",
        Issue.ChannelMapCount => "ac3.channel-map-count",
        Issue.EmdfTruncated => "ac3.emdf-truncated",
        Issue.EmdfLength => "ac3.emdf-length",
        Issue.EmdfPayloadSize => "ac3.emdf-payload-size",
        Issue.EmdfReserved => "ac3.emdf-reserved",
        Issue.EmdfVersion => "ac3.emdf-version-not-read",
        Issue.PayloadShort => "ac3.object-payload-short",
        Issue.JocReserved => "ac3.joc-reserved",
        Issue.ObjectAudioIncomplete => "ac3.object-audio-incomplete",
        Issue.EmdfLimit => "ac3.emdf-limit",
        _ => "ac3.auxiliary-length",
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
            case Issue.Aht:
                Findings.Add(Severity.Info, CodeOf(issue), Field, count =>
                    $"The frame codes channels with the adaptive hybrid transform (A/52 Annex E 3.4), whose mantissas the analyzer does not step over, so its audio blocks are read up to the first of them. First in {at} ({Times(count)}).");
                break;
            case Issue.EnhancedCoupling:
                Findings.Add(Severity.Info, CodeOf(issue), Field, count =>
                    $"The frame uses enhanced coupling (A/52 Annex E 3.5), whose parameters the analyzer does not read, so its audio blocks are read up to there. First in {at} ({Times(count)}).");
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
            Findings.Add(Severity.Warning, "ac3.extension-without-joc", () => Node.FromBits("HasExtensionTypeA", unit.ExtensionOffset, unit.ExtensionStart, unit.ExtensionEnd), count =>
                $"{Units(count)}: the additional bit stream information of a frame sets flag_ec3_extension_type_a, but no EMDF container in the frames holds a joint object coding payload (ETSI TS 103 420 8.3); the first is at 0x{unit.ExtensionOffset:X}.");
        }
        else if (unit.JocOffset >= 0 && unit.ExtensionOffset < 0)
        {
            Findings.Add(Severity.Warning, "ac3.joc-without-extension", () => Node.FromBits("Payload", unit.JocOffset, unit.JocStart, unit.JocEnd), count =>
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
    private Program? Assign(Region region, int index, SyncFrame header, FrameResult result)
    {
        long frameOffset = _frameOffset;
        int frameLength = _frameLength;
        Node FrameAt() => new("Frame", frameOffset, frameLength) { Kind = NodeKind.Frame };
        if (header.IsDependent)
        {
            if (_current is null)
            {
                Findings.Add(Severity.Warning, "ac3.dependent-without-independent", () => FrameAt(),
                    count => $"{Count(count)} of a dependent substream come before any independent substream they could belong to (A/52 Annex E 2.3.1.2); the first is at 0x{frameOffset:X}.");
                return null;
            }

            if (_currentType == 2)
            {
                Findings.Add(Severity.Warning, "ac3.dependent-after-converted", () => FrameAt(),
                    count => $"{Count(count)} of a dependent substream follow an independent substream of stream type 2, converted from AC-3, which may have none (A/52 Annex E 2.3.1.1); the first is at 0x{frameOffset:X}.");
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
            Findings.Add(Severity.Warning, "ac3.substream-blocks-differ", () => FrameAt(),
                count => $"{Count(count)} of independent substream {id} hold {blocks} audio blocks where substream 0's hold {expected} (A/52 Annex E 2.3.1.2 requires the same number); the first is at 0x{frameOffset:X}.");
        }

        var key = new ProgramKey(id, header.SampleRate, header.IsEac3, header.Acmod, header.Lfeon);
        if (!_programs.TryGetValue(id, out Program? program) || program.Key != key)
        {
            if (program is not null)
            {
                _finished.Add(program);
                Findings.AddNow(Severity.Info, "ac3.stream-changed",
                    $"The stream{(header.IsEac3 ? $" of independent substream {id}" : string.Empty)} changes at 0x{frameOffset:X} from {program.Key.Describe()} to {key.Describe()}.",
                    FrameAt());
            }

            program = new Program(key, index);
            _programs[id] = program;
        }

        program.Regions.Add(region);
        _current = program;
        return program;
    }

    // The streams, in the order their first frames appear, with each one's statistics in the first
    // region it spans.
    public void Finish(IReadOnlyList<EncoderClaim> claims)
    {
        FinishUnit();
        List<Program> programs = [.. _finished, .. _programs.Values];
        programs.Sort((a, b) => a.FirstFrame.CompareTo(b.FirstFrame));
        bool several = programs.Select(p => p.Key.Id).Distinct().Count() > 1 || programs.Any(p => p.Key.IsEac3);
        foreach (Program program in programs)
        {
            Cancellation.ThrowIfCancellationRequested();
            program.Close();
            AddStream(program, claims, several);
        }

        // The frames' nodes keep this analysis alive; the programs' per-frame figures are in the
        // streams' series now.
        _programs.Clear();
        _finished.Clear();
        _current = null;
        Findings.Flush(_context);
    }

    private void AddStream(Program program, IReadOnlyList<EncoderClaim> claims, bool named)
    {
        Region first = Regions.First(r => program.Regions.Contains(r));
        Node Cover(string name, NodeValue? value, NodeKind kind, string description, string? topic = null) =>
            new(name, first.Node!.Offset, first.Node.Length, value) { Kind = kind, Ranges = first.Node.Ranges, Description = description, Topic = topic };

        Node? encoder = Ac3Encoder.Detect(claims, (name, value, description) => Cover(name, value, NodeKind.Detail, description, name == "Encoder" ? Topics.EncoderDetection : null),
            (claim, text) => Findings.Add(Severity.Info, "ac3.encoder-claim", () => claim.Source, _ =>
                $"The {claim.Description} names \"{text}\"; nothing in the stream confirms or contradicts it."));
        if (encoder is not null)
        {
            first.Leading.Add(encoder);
        }

        Node statistics = program.Statistics(Cover, out Node bitrate);
        if (named)
        {
            statistics.Value = program.Key.IsEac3 ? $"independent substream {program.Key.Id}" : "AC-3";
        }

        first.Leading.Add(statistics);
        if (program.LayoutChanges > 0)
        {
            Findings.AddNow(Severity.Info, "ac3.dependent-layout-changed",
                $"The dependent substreams of {program.Name} add other channels in {program.LayoutChanges} of its frames than in its first; the report gives the first frame's layout.", statistics);
        }

        string? confidence = encoder?.Children.FirstOrDefault(c => c.Name == "Confidence")?.Value?.ToString();
        _context.AddStream(new StreamInfo
        {
            Nodes = [.. Regions.Where(r => program.Regions.Contains(r)).Select(r => r.Node!)],
            Codec = program.Codec,
            SampleRate = program.Key.SampleRate,
            Channels = program.Layout.Count,
            ChannelMode = Ac3Names.CodingMode(program.Key.Acmod),
            ChannelLayout = program.Layout,
            Samples = program.Figures.Samples,
            Duration = program.Figures.Duration,
            AverageBitrate = program.Figures.AverageBitrate,
            IsVbr = program.IsVbr,
            HasObjectAudio = program.HasObjectAudio,
            ObjectCount = program.ObjectCount,
            Encoder = encoder?.Value?.ToString(),
            EncoderConfidence = Enum.TryParse(confidence, out Confidence parsed) ? parsed : null,
            Series = program.Figures.Series(bitrate, Cancellation),
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

    internal readonly record struct ProgramKey(int Id, int SampleRate, bool IsEac3, int Acmod, bool Lfeon)
    {
        public string Describe() => $"{(IsEac3 ? "E-AC-3" : "AC-3")}, {SampleRate} Hz, {Ac3Names.CodingModeShort(Acmod)}{(Lfeon ? " + LFE" : string.Empty)}";
    }
}
