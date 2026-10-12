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
    private int _frameIndex;
    private long _frameOffset;

    public Ac3Analysis(AnalysisContext context)
    {
        _context = context;
        _syntax = new FrameSyntax(_bits) { IssueFound = OnIssue };
    }

    public ChunkedList<Frame> Frames { get; } = new();

    public ByteStore Store { get; } = new();

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
        long store = Store.Add(_bits.Kept);
        var frame = new Frame(offset, available, header.Raw)
        {
            Store = store,
            KeptBits = (ushort)_bits.KeptBits,
            Flags = (byte)((int)crc1 | ((int)crc2 << 2) | (shortened ? 16 : 0) | (cut ? 32 : 0)),
        };
        Frames.Add(frame);

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

    private void OnIssue(Issue issue, string field, int start, int end, string detail)
    {
        long offset = _frameOffset;
        Node Field() => Node.FromBits(field, offset, start, end);
        string at = $"the frame at 0x{offset:X}";
        switch (issue)
        {
            case Issue.ReservedValue:
                Findings.Add(Severity.Warning, "ac3.reserved-value", Field, count =>
                    $"{field} holds a value A/52 reserves, first in {at} ({Times(count)}).");
                break;
            case Issue.DialnormReserved:
                Findings.Add(Severity.Warning, "ac3.dialnorm-reserved", Field, count =>
                    $"dialnorm is 0, which A/52 5.4.2.8 reserves; decoders read it as -31 dB. First in {at} ({Times(count)}).");
                break;
            case Issue.LanguageCode:
                Findings.Add(Severity.Info, "ac3.language-code", Field, count =>
                    $"langcod is not 0xFF, the value A/52:2018 5.4.2.12 gives the reserved field; earlier editions coded a language there. First in {at} ({Times(count)}).");
                break;
            case Issue.BlockInconsistent:
                Findings.Add(Severity.Warning, "ac3.block-inconsistent", Field, count =>
                    $"An audio block breaks a rule that A/52 7.10.2 lists among the errors a decoder may check for: {detail}. First in {at} ({Times(count)}).");
                break;
            case Issue.BlockInvalid:
                Findings.Add(Severity.Warning, "ac3.block-invalid", Field, count =>
                    $"An audio block holds what A/52 does not allow: {detail}. Decoders cannot decode the rest of such a frame, and its remaining audio blocks are not read. First in {at} ({Times(count)}).");
                break;
            case Issue.Overrun:
                Findings.Add(Severity.Warning, "ac3.block-overrun", Field, count =>
                    $"An audio block runs past the end of its frame: {detail} (A/52 7.10.2, check 21). First in {at} ({Times(count)}).");
                break;
            case Issue.UndefinedBsid:
                Findings.Add(Severity.Info, "ac3.bsid-undefined", Field, count =>
                    $"bsid 9 or 10 is not defined by A/52, whose Annex E 2.3.1.6 says decoders mute it; ffmpeg and liba52 decode it as AC-3 at half or a quarter of the sample rate, as the report does. Its audio blocks are not read. First in {at} ({Times(count)}).");
                break;
            case Issue.ReducedRate:
                Findings.Add(Severity.Info, "ac3.reduced-rate-not-read", Field, count =>
                    $"The frame is coded at a reduced sample rate (fscod2), for which A/52 does not specify the bit allocation, so its audio blocks are not read. First in {at} ({Times(count)}).");
                break;
            case Issue.Aht:
                Findings.Add(Severity.Info, "ac3.aht-not-read", Field, count =>
                    $"The frame codes channels with the adaptive hybrid transform (A/52 Annex E 3.4), whose mantissas the analyzer does not step over, so its audio blocks are read up to the first of them. First in {at} ({Times(count)}).");
                break;
            case Issue.EnhancedCoupling:
                Findings.Add(Severity.Info, "ac3.enhanced-coupling-not-read", Field, count =>
                    $"The frame uses enhanced coupling (A/52 Annex E 3.5), whose parameters the analyzer does not read, so its audio blocks are read up to there. First in {at} ({Times(count)}).");
                break;
            case Issue.MixDataOverrun:
                Findings.Add(Severity.Warning, "ac3.mix-data-overrun", Field, count =>
                    $"The mixing parameters run past the length mixdeflen gives: {detail}; decoders skip that length. First in {at} ({Times(count)}).");
                break;
            case Issue.ChannelMapCount:
                Findings.Add(Severity.Warning, "ac3.channel-map-count", Field, count =>
                    $"In a dependent substream {detail} (A/52 Annex E 2.3.1.8 requires them to match). First in {at} ({Times(count)}).");
                break;
            case Issue.AuxiliaryLength:
                Findings.Add(Severity.Warning, "ac3.auxiliary-length", Field, count =>
                    $"{detail} (A/52 5.4.4.2). First in {at} ({Times(count)}).");
                break;
        }
    }

    private static string Times(int count) => count == 1 ? "once" : $"{count} times";

    // Independent substreams (and AC-3 frames) start a program frame; dependent substreams join the
    // program of the independent frame before them.
    private Program? Assign(Region region, int index, SyncFrame header, FrameResult result)
    {
        if (header.IsDependent)
        {
            if (_current is null)
            {
                Findings.Add(Severity.Warning, "ac3.dependent-without-independent", () => new Node("Frame", Frames[index].Offset, Frames[index].Length) { Kind = NodeKind.Frame },
                    count => $"{Count(count)} of a dependent substream come before any independent substream they could belong to (A/52 Annex E 2.3.1.2); the first is at 0x{Frames[index].Offset:X}.");
                return null;
            }

            _current.Regions.Add(region);
            return _current;
        }

        int id = header.IsEac3 ? header.SubstreamId : 0;
        var key = new ProgramKey(id, header.SampleRate, header.IsEac3, header.Acmod, header.Lfeon);
        if (!_programs.TryGetValue(id, out Program? program) || program.Key != key)
        {
            if (program is not null)
            {
                _finished.Add(program);
                Findings.AddNow(Severity.Info, "ac3.stream-changed",
                    $"The stream{(header.IsEac3 ? $" of independent substream {id}" : string.Empty)} changes at 0x{Frames[index].Offset:X} from {program.Key.Describe()} to {key.Describe()}.",
                    new Node("Frame", Frames[index].Offset, Frames[index].Length) { Kind = NodeKind.Frame });
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
        List<Program> programs = [.. _finished, .. _programs.Values];
        programs.Sort((a, b) => a.FirstFrame.CompareTo(b.FirstFrame));
        bool several = programs.Select(p => p.Key.Id).Distinct().Count() > 1 || programs.Any(p => p.Key.IsEac3);
        foreach (Program program in programs)
        {
            Cancellation.ThrowIfCancellationRequested();
            program.Close();
            AddStream(program, claims, several);
        }

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
            Encoder = encoder?.Value?.ToString(),
            EncoderConfidence = Enum.TryParse(confidence, out Confidence parsed) ? parsed : null,
            Series = program.Figures.Series(bitrate, Cancellation),
        });
    }

    // A frame's node, with its fields built from the record when first read.
    public Node FrameNode(int index)
    {
        Frame frame = Frames[index];
        if (frame.IsGap)
        {
            return new Node("Gap", frame.Offset, frame.Length, NodeValue.Integer(frame.Length, "bytes")) { Description = Explain.Gap };
        }

        var header = new SyncFrame(frame.Header);
        ByteStore store = Store;
        return new Node("Frame", frame.Offset, frame.Length, header.Summary(header.FrameLength), () => FrameChildren(store, frame, header))
        {
            Kind = NodeKind.Frame,
            Description = header.IsEac3 ? Explain.Eac3Frame : Explain.Ac3Frame,
            Topic = Topics.FramesAndBsi,
        };
    }

    private static List<Node> FrameChildren(ByteStore store, Frame frame, SyncFrame header)
    {
        byte[] record = store.Read(frame.Store, (frame.KeptBits + 7) >> 3);
        var syntax = new FrameSyntax(FrameBits.ForNodes());
        return syntax.Nodes(record, frame.KeptBits, frame.Offset, frame.Length, header, Verdict(frame.Crc1, frame.IsTruncated), Verdict(frame.Crc2, frame.IsTruncated));
    }

    private static string Verdict(CrcState state, bool truncated) => state switch
    {
        CrcState.Verified => "verified",
        CrcState.Mismatch => "mismatch",
        _ => truncated ? "not checked: the frame is cut short" : "not checked",
    };

    internal enum CrcState
    {
        NotChecked,
        Verified,
        Mismatch,
    }

    // A frame or, with a zero header (never a valid one), a gap.
    [StructLayout(LayoutKind.Auto)]
    internal readonly struct Frame(long offset, int length, ulong header)
    {
        public long Offset => offset;

        public int Length => length;

        public ulong Header => header;

        public bool IsGap => header == 0;

        public long Store { get; init; }

        public ushort KeptBits { get; init; }

        public byte Flags { get; init; }

        public CrcState Crc1 => (CrcState)(Flags & 3);

        public CrcState Crc2 => (CrcState)((Flags >> 2) & 3);

        public bool IsTruncated => (Flags & 16) != 0;

        public static Frame Gap(long offset, long length) => new(offset, (int)Math.Min(length, int.MaxValue), 0);
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
