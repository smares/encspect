using Encspect.Core;

namespace Encspect.Formats.Alac;

// The state of one analysis: the records the nodes are built from, the counts worked out as frames are read, and the findings. The codec parser hands
// the frames over one by one, and nothing here holds the packets after the call that walked them.
internal sealed class State
{
    private readonly WalkResult _walk = new();
    private readonly List<ElementRecord> _kept = new(Records.MaxDetailedElements);
    private readonly CancellationToken _cancellation;

    public State(AnalysisContext context)
    {
        Context = context;
        _cancellation = context.CancellationToken;
    }

    public AnalysisContext Context { get; }

    // The node of the stream; set before the frames are read.
    public Node Stream { get; set; } = null!;

    public Records Records { get; } = new();

    public FindingCollector Findings { get; } = new();

    public CodingTally Tally { get; } = new();

    public CodingRecorder Series { get; } = new();

    public StreamFigures? Figures { get; set; }

    public AlacCookie? Cookie => Records.Cookie;

    public Node? CookieNode { get; set; }

    // The cookie's bytes, which a decoder would take as its configuration.
    public MappedBytes? CookieBytes { get; set; }

    // The nodes before the frames: the writer and the statistics.
    public List<Node> Leading { get; } = [];

    // The frames listed, recorded or not; those past Records.MaxEntries are counted and walked but have no record.
    public long Listed { get; private set; }

    public long Unrecorded { get; private set; }

    // Counts over the frames.
    public long NotWalkedFrames { get; private set; }

    public long UnreadableFrames { get; private set; }

    public long PartialFrames { get; private set; }

    public long UncompressedFrames { get; private set; }

    public long Samples { get; private set; }

    // The largest packet, for the cookie's statement of it.
    public long Largest { get; private set; }

    public void AddFrame(MappedBytes packet)
    {
        long listed = Listed++;
        bool isRecorded = listed < Records.MaxEntries;
        int length = packet.Length;
        long offset = packet.FileStart;
        FrameFlags flags = FrameFlags.None;
        uint samples = 0;
        int elements = 0;
        int walked = 0;
        int detail = -1;
        Largest = Math.Max(Largest, length);
        IReadOnlyList<ByteRange> ranges = packet.FileRanges();
        if (ranges.Count > 1)
        {
            flags |= FrameFlags.HasRanges;
            if (isRecorded)
            {
                Records.Ranges[(int)listed] = ranges;
            }
        }

        if (Cookie is { CanWalk: true } cookie)
        {
            bool isDetailed = isRecorded && listed < Records.MaxDetailedFrames;
            FrameWalker.Walk(packet.Span, cookie, _walk, Tally, _kept, isDetailed ? Records.Store : null, _cancellation);
            flags |= _walk.Flags;
            samples = _walk.HasSamples ? _walk.Samples : 0;
            elements = _walk.Elements;
            walked = _walk.Walked;
            if (_walk.HasEnd && walked < length)
            {
                flags |= FrameFlags.HasTrailingData;
            }

            if (isDetailed)
            {
                detail = Records.Details.Count;
                Records.Details.Add(new FrameDetail([.. _kept], elements, _walk.RestStart, _walk.RestEnd));
            }

            Report(cookie, listed, isRecorded, offset, length, detail >= 0);
            PartialFrames += (flags & FrameFlags.Partial) != 0 ? 1 : 0;
            UncompressedFrames += (flags & FrameFlags.Uncompressed) != 0 ? 1 : 0;
            UnreadableFrames += (flags & FrameFlags.Unreadable) != 0 ? 1 : 0;
        }
        else
        {
            flags |= FrameFlags.NotWalked;
            NotWalkedFrames++;
        }

        Samples += samples;
        if (isRecorded)
        {
            Records.Frames.Add(new FrameRecord { Offset = offset, Length = length, Samples = samples, Flags = flags, Elements = elements, Walked = walked, Detail = detail });
            if (samples > 0 && Figures is { } figures)
            {
                figures.Add(offset, length * 8L, samples);
                Series.EndUnit(Tally);
            }
            else
            {
                CodingRecorder.Clear(Tally);
            }
        }
        else
        {
            Unrecorded++;
            CodingRecorder.Clear(Tally);
        }
    }

    // The findings of one walked frame. Cases of one code are counted and reported once, at the first; the messages and the node builders use copies of
    // what they need, since the walk's result is reused for the next frame.
    private void Report(AlacCookie cookie, long listed, bool isRecorded, long offset, int length, bool isDetailed)
    {
        WalkResult w = _walk;
        Records records = Records;
        Node stream = Stream;
        int channels = cookie.Channels;
        int read = w.ChannelsRead;
        int elements = w.Elements;
        Func<Node> At(int element) => !isRecorded ? () => stream : isDetailed && element >= 0 && element < elements ? () => Nodes.Element(records, (int)listed, element) : () => Nodes.Frame(records, (int)listed);
        static string More(int count, string one, string many) => count > 1 ? $"; {Counts.Of(count - 1, "more frame", "more frames")} {Counts.Agree(count - 1, one, many)}" : string.Empty;

        if (w.Fault != WalkFault.None)
        {
            int at = w.FaultElement;
            int tag = w.FaultTag;
            bool inElement = at < elements;
            string place = inElement ? $"element {at + 1}" : $"the {Counts.Of(elements, "element", "elements")} before it";
            switch (w.Fault)
            {
                case WalkFault.Truncated:
                    Findings.Add(Severity.Warning, "alac.frame-overrun", At(at), count =>
                        $"The frame at 0x{offset:X} needs more bits than its packet of {Counts.Of(length, "byte", "bytes")} holds: the walk runs out of data {(inElement ? "inside " + place : "after " + place)}, with {read} of the cookie's {Counts.Of(channels, "channel", "channels")} read{More(count, "does the same", "do the same")}.");
                    break;
                case WalkFault.UnknownElement:
                    Findings.Add(Severity.Warning, "alac.element-unknown", At(at), count =>
                        $"Element {at + 1} of the frame at 0x{offset:X} has the tag {tag} ({ElementTags.Name(tag)}), for which ALAC defines no syntax; the reference decoder refuses the frame, so the rest of it is not read{More(count, "has such an element", "have such elements")}.");
                    break;
                case WalkFault.InvalidHeader:
                    Findings.Add(Severity.Warning, "alac.element-invalid", At(at), count =>
                        $"Element {at + 1} of the frame at 0x{offset:X} states a header the reference decoder cannot use (3 bytes shifted, or a sample width of no bits or of more than 32), so the rest of the frame is not read{More(count, "does the same", "do the same")}.");
                    break;
                case WalkFault.Tuning:
                    int kb = cookie.Kb;
                    Findings.Add(Severity.Warning, "alac.tuning-invalid", At(at), count =>
                        $"Element {at + 1} of the frame at 0x{offset:X} is compressed, but the cookie states the maximum Golomb parameter (kb) {kb}, for which the reference code defines no residual code (it uses the parameter as a 32-bit mask, and 0 reads no bits), so the rest of the frame is not read{More(count, "does the same", "do the same")}.");
                    break;
                default:
                    Findings.Add(Severity.Warning, "alac.residual-invalid", At(at), count =>
                        $"In element {at + 1} of the frame at 0x{offset:X} a run of zero residuals goes past the samples of the element, which the reference decoder refuses{More(count, "does the same", "do the same")}.");
                    break;
            }
        }

        if (w.IsEndMissing)
        {
            Findings.Add(Severity.Warning, "alac.end-missing", At(-1), count =>
                $"The frame at 0x{offset:X} ends after its last channel without the end tag that Apple's encoder writes; the reference decoder stops there{More(count, "does the same", "do the same")}.");
        }

        if (w.HasEnd && w.Walked < length)
        {
            int walked = w.Walked;
            Findings.Add(Severity.Warning, "alac.frame-underrun", () => isRecorded ? Nodes.FrameChild(records, (int)listed, "Trailing Data") : stream, count =>
                $"The frame at 0x{offset:X} ends after {Counts.Of(walked, "byte", "bytes")}, but its packet holds {length}; {Counts.Of(length - walked, "byte", "bytes")} follow the end tag{More(count, "does the same", "do the same")}.");
        }

        if (w.ExceedingElement >= 0)
        {
            Findings.Add(Severity.Warning, "alac.channels-exceed", At(w.ExceedingElement), count =>
                $"The elements of the frame at 0x{offset:X} hold {Counts.Of(read, "channel", "channels")}, but the cookie states {channels}; the reference decoder stops at the cookie's channels{More(count, "does the same", "do the same")}.");
        }
        else if (w.HasEnd && read < channels)
        {
            Findings.Add(Severity.Warning, "alac.channels-missing", At(-1), count =>
                $"The elements of the frame at 0x{offset:X} hold {Counts.Of(read, "channel", "channels")}, but the cookie states {channels}; the reference decoder fills the others with zeros{More(count, "does the same", "do the same")}.");
        }

        if (w.ReservedElement >= 0)
        {
            int element = w.ReservedElement;
            Findings.Add(Severity.Warning, "alac.element-reserved-bits", At(element), count =>
                $"The 12 reserved bits of element {element + 1} of the frame at 0x{offset:X} are not 0, which the reference decoder refuses{More(count, "does the same", "do the same")}.");
        }

        if (w.ShiftedEscapeElement >= 0)
        {
            int element = w.ShiftedEscapeElement;
            Findings.Add(Severity.Info, "alac.escape-shifted", At(element), count =>
                $"Element {element + 1} of the frame at 0x{offset:X} is uncompressed and also states bytes shifted; Apple's encoder writes 0 there, and the walk follows the reference decoder, which reads no shifted bits{More(count, "does the same", "do the same")}.");
        }

        if (w.DifferingElement >= 0)
        {
            int element = w.DifferingElement;
            long first = w.Samples;
            Findings.Add(Severity.Warning, "alac.element-samples-differ", At(element), count =>
                $"Element {element + 1} of the frame at 0x{offset:X} has a sample count that differs from the first element's {first}; the frame has one count{More(count, "does the same", "do the same")}.");
        }

        if (w.LongElement >= 0)
        {
            int element = w.LongElement;
            long samples = w.LongElementSamples;
            long frameLength = cookie.FrameLength;
            Findings.Add(Severity.Warning, "alac.partial-frame-too-long", At(element), count =>
                $"Element {element + 1} of the frame at 0x{offset:X} states {Counts.Of(samples, "sample", "samples")}, more than the frame length of {frameLength} in the cookie, which the reference decoder's buffers hold{More(count, "does the same", "do the same")}.");
        }
    }
}
