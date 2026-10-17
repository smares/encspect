using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Mpeg;

// Statistics of each region's audio frames, and how they compare with what a VBR header claims.
// They count complete audio frames: not a frame that holds a VBR header, not a truncated last
// frame. Time runs on a clock of 1/14,112,000 s, ffmpeg's time base for MP3, which every MPEG
// sample rate divides, so frames of different sample rates add up exactly; figures are rounded
// half to even, as mp3guessenc's printf rounds them.
public sealed partial class MpegAudioParser
{
    private const long Clock = 14_112_000;

    private static void AddStatistics(StreamState state)
    {
        foreach (RegionState region in state.Regions)
        {
            region.Statistics = Statistics(state, region);
        }

        ChunkedList<Entry> frames = state.Build.Frames;
        for (int i = 0; i < frames.Count; i++)
        {
            state.CheckCancellation(i);
            if (frames[i].IsVbrHeader)
            {
                CheckHeaderCounts(state, i);
            }
        }
    }

    private static Node? Statistics(StreamState state, RegionState region)
    {
        if (region.AudioFrames == 0)
        {
            return null;
        }

        ChunkedList<Entry> frames = state.Build.Frames;
        int end = region.End ?? frames.Count;
        long ticks = 0;
        long bits = 0;
        long samples = 0;
        bool hasSideInfo = true;
        int maximumFill = 0;
        int free = 0;
        var fixedRates = new SortedDictionary<int, int>();
        for (int i = region.First; i < end; i++)
        {
            state.CheckCancellation(i - region.First);
            ref readonly Entry frame = ref frames[i];
            if (!IsAudio(frame))
            {
                continue;
            }

            var header = new MpegHeader(frame.Header);
            ticks += Ticks(frame);
            bits += frame.Length * 8;
            samples += header.Samples;
            hasSideInfo &= frame.HasSideInfo;
            maximumFill = Math.Max(maximumFill, frame.ReservoirFill);
            if (BitrateOf(frame) is { } kbps)
            {
                fixedRates[kbps] = fixedRates.GetValueOrDefault(kbps) + 1;
            }
            else
            {
                free++;
            }
        }

        int audio = region.AudioFrames;
        long start = region.AudioStart;
        long length = region.AudioEnd - start;
        Node node = new("Statistics", start, length, $"{FramesText(audio)}, {DurationText(ticks)}") { Kind = NodeKind.Statistics, Description = Explain.Statistics };
        Add("Frames", NodeValue.Integer(audio), Explain.StatFrames);
        Add("Samples", NodeValue.Integer(samples), Explain.StatSamples);
        Add("Duration", Duration(ticks), Explain.StatDuration);

        // Free format, without a bitrate, sorts last.
        var counts = fixedRates.Select(r => ((int?)r.Key, r.Value)).ToList();
        if (free > 0)
        {
            counts.Add((null, free));
        }

        Add("IsVbr", NodeValue.Boolean(counts.Count > 1), Explain.IsVbr, Topics.BitrateModes);
        Add("Average Bitrate", NodeValue.Decimal(RoundHalfEven((Int128)bits * Clock * 10, (Int128)ticks * 1000) / 10.0, 1, "kbps"), Explain.AverageBitrate);
        if (fixedRates.Count > 0)
        {
            Add("Minimum Bitrate", NodeValue.Integer(fixedRates.Keys.First(), "kbps"), Explain.MinimumBitrate);
            Add("Maximum Bitrate", NodeValue.Integer(fixedRates.Keys.Last(), "kbps"), Explain.MaximumBitrate);
        }

        region.Distribution = Add("Bitrate Distribution", NodeValue.Integer(counts.Count, "bitrates", "bitrate"), Explain.Distribution, Topics.BitrateModes);
        foreach ((int? rate, int count) in counts)
        {
            long tenths = RoundHalfEven(count * 1000L, audio);
            Node entry = region.Distribution.Add(rate is { } kbps ? $"{Number(kbps)} kbps" : "free", start, length,
                $"{FramesText(count)} ({TenthsText(tenths)}%)", Explain.DistributionEntry);
            AddFrameSizes(entry, region.Tally, rate);
        }

        // The bitrate and reservoir fill over time are series of the stream (AddStreams), which
        // the text tree shows after these nodes.
        if (hasSideInfo)
        {
            region.MaximumFill = Add("Maximum Reservoir Fill", NodeValue.Integer(maximumFill, "bytes"), Explain.MaximumFill, Topics.BitReservoir);
        }

        AddTally(node, region, start, length, bits / 8, ticks);

        // Each header's figures cover the frames it describes; where those are not all of the
        // region's (files were joined), the node says which.
        int firstFrame = FirstFrame(frames, region);
        for (int i = region.First; i < end; i++)
        {
            if (frames[i].IsVbrHeader)
            {
                AddHeaderComparison(i);
            }
        }

        return node;

        Node Add(string name, NodeValue value, string description, string? topic = null) => node.Add(name, start, length, value, description, topic);

        void AddHeaderComparison(int index)
        {
            Entry frame = frames[index];
            VbrHeader header = ReadVbrHeader(state.Build, frame);
            Coverage covered = Covered(state, index);
            (int inside, long from, long span) = AudioIn(state, region, covered);
            if (inside == 0)
            {
                (from, span) = (node.Offset, node.Length);
            }

            string? scope = index == firstFrame && inside == audio ? null
                : $"{FramesText(covered.Frames)} after the {header.Type} header at 0x{frame.Offset:X}";
            Node? counts = header.FrameCount is null && header.ByteCount is null ? null
                : node.Add(new Node("VBR Header", from, span, scope) { Description = Explain.HeaderCounts, Topic = Topics.XingHeader });
            if (header.FrameCount is { } claimedFrames)
            {
                counts!.Add("Claimed Frames", from, span, NodeValue.Integer(claimedFrames), Explain.ClaimedFrames);
                counts.Add("Counted Frames", from, span, NodeValue.Integer(covered.Frames), Explain.CountedFrames);
            }

            if (header.ByteCount is { } claimedBytes)
            {
                counts!.Add("Claimed Bytes", from, span, NodeValue.Integer(claimedBytes), Explain.ClaimedBytes);
                counts.Add("Counted Bytes", from, span, NodeValue.Integer(frame.Length + covered.Bytes), Explain.CountedBytes);
            }

            if (header.HasLameTag)
            {
                Node gapless = node.Add(new Node("Gapless", from, span, scope) { Description = Explain.Gapless, Topic = Topics.GaplessPlayback });
                gapless.Add("Encoder Delay", from, span, NodeValue.Integer(header.EncoderDelay, "samples"), Explain.EncoderDelay);
                gapless.Add("Encoder Padding", from, span, NodeValue.Integer(header.EncoderPadding, "samples"), Explain.EncoderPadding);
                long count = covered.Samples - header.EncoderDelay - header.EncoderPadding;
                if (count >= 0)
                {
                    gapless.Add("Sample Count", from, span, NodeValue.Integer(count), Explain.GaplessSamples);
                    gapless.Add("Duration", from, span, Duration(count * (Clock / new MpegHeader(frame.Header).SampleRate)), Explain.GaplessDuration);
                }
            }
        }
    }

    // Xing's and Fraunhofer's encoders count the header's own frame, LAME does not; byte counts
    // include it, and LAME's leave out what is not its own, such as a tag added between frames.
    // A truncated last frame was written whole, so it counts.
    private static void CheckHeaderCounts(StreamState state, int index)
    {
        FindingCollector findings = state.Findings;
        Entry frame = state.Build.Frames[index];
        VbrHeader header = ReadVbrHeader(state.Build, frame);
        Coverage covered = Covered(state, index);
        long position = frame.Offset;
        Node node = FindingNode(frame);
        if (header.FrameCount is { } claimedFrames && claimedFrames != covered.Frames && claimedFrames != covered.Frames + 1)
        {
            findings.Add(Severity.Warning, "mpeg.vbr-frame-count-mismatch", node, count =>
                $"The {header.Type} header of {Frames(count)} claims a frame count the stream after it does not have, the first at 0x{position:X}: {Number(claimedFrames)} claimed, {Number(covered.Frames)} counted.");
        }

        // The LAME tag's delay and padding count from the frames after its own, so a count that
        // includes the header (LAME 3.94 beta 1 writes one) moves the end of the music by a frame.
        if (header.HasLameTag && header.FrameCount is { } counted && counted == covered.Frames + 1 && covered.Frames > 0)
        {
            int frameSamples = new MpegHeader(state.Build.Frames[covered.FirstFrame].Header).Samples;
            findings.Add(Severity.Warning, "mpeg.vbr-frame-count-includes-header", node, count =>
                $"The Xing header of {Frames(count)} counts its own frame, though the LAME tag after it counts its delay and padding from the frames that follow; decoders that take the length from the count, such as ffmpeg and mpg123, play {Number(frameSamples)} samples more than were encoded. The first at 0x{position:X}.");
        }

        long bytes = frame.Length + covered.Bytes;
        if (header.ByteCount is { } claimedBytes && claimedBytes != bytes && claimedBytes != bytes - frame.Length)
        {
            findings.Add(Severity.Warning, "mpeg.vbr-byte-count-mismatch", node, count =>
                $"The {header.Type} header of {Frames(count)} claims a byte count the stream after it does not have, the first at 0x{position:X}: {Number(claimedBytes)} claimed, {Number(bytes)} counted.");
        }

        long samples = covered.Samples;
        if (header.HasLameTag && header.EncoderDelay + header.EncoderPadding > samples)
        {
            findings.Add(Severity.Warning, "mpeg.gapless-exceeds-stream", node, count =>
                $"The LAME tag of {Frames(count)} gives an encoder delay and padding of {Number(header.EncoderDelay + header.EncoderPadding)} samples, more than the {Number(samples)} of the stream after it; the first at 0x{position:X}.");
        }
    }

    // Whether the stream after a VBR header holds less than the header claims: its last frame is
    // truncated, or the counts are more than the frames and bytes there.
    private static bool IsCutShort(in Entry frame, VbrHeader header, Coverage covered) =>
        covered.HasTruncated || header.FrameCount > covered.Frames + 1 || header.ByteCount > frame.Length + covered.Bytes;

    // The frames a VBR header describes: from its frame to the end of its stream, the next VBR
    // header or a change of version, layer or sample rate, across gaps and tags. Each header's
    // frames end where the next header's start, so finding them all reads every record once.
    private static Coverage Covered(StreamState state, int index)
    {
        if (state.Coverages.TryGetValue(index, out Coverage known))
        {
            return known;
        }

        ChunkedList<Entry> frames = state.Build.Frames;
        var header = new MpegHeader(frames[index].Header);
        int count = 0;
        long bytes = 0;
        long samples = 0;
        bool hasTruncated = false;
        int first = -1;
        int last = index;
        for (int i = index + 1; i < frames.Count; i++)
        {
            state.CheckCancellation(i - index);
            ref readonly Entry frame = ref frames[i];
            if (frame.IsGap)
            {
                continue;
            }

            var next = new MpegHeader(frame.Header);
            if (frame.IsVbrHeader || next.VersionBits != header.VersionBits || next.Layer != header.Layer
                || next.SampleRateIndex != header.SampleRateIndex)
            {
                break;
            }

            first = first < 0 ? i : first;
            last = i;
            count++;
            bytes += frame.Length;
            samples += next.Samples;
            hasTruncated |= frame.IsTruncated;
        }

        return state.Coverages[index] = new Coverage(first, last + 1, count, bytes, samples, hasTruncated);
    }

    // How many of a region's complete audio frames a header describes, and the bytes from the first
    // to the last of them.
    private static (int Frames, long Offset, long Length) AudioIn(StreamState state, RegionState region, Coverage covered)
    {
        ChunkedList<Entry> frames = state.Build.Frames;
        int count = 0;
        long start = 0;
        long end = 0;
        for (int i = Math.Max(covered.FirstFrame, region.First); covered.Frames > 0 && i < Math.Min(covered.End, region.End ?? frames.Count); i++)
        {
            ref readonly Entry frame = ref frames[i];
            if (IsAudio(frame))
            {
                start = count == 0 ? frame.Offset : start;
                end = frame.End;
                count++;
            }
        }

        return (count, start, end - start);
    }

    // The index of a region's first frame.
    private static int FirstFrame(ChunkedList<Entry> frames, RegionState region)
    {
        int i = region.First;
        while (i < (region.End ?? frames.Count) && frames[i].IsGap)
        {
            i++;
        }

        return i;
    }

    private static VbrHeader ReadVbrHeader(BuildContext build, in Entry frame) =>
        VbrHeader.TryRead(build.Store.Span(frame.Data, frame.DataLength).ToArray(), new MpegHeader(frame.Header))!;

    // The frames a VBR header describes: the records FirstFrame to End, gaps among them not
    // counted; FirstFrame is -1 where there are none.
    private readonly record struct Coverage(int FirstFrame, int End, int Frames, long Bytes, long Samples, bool HasTruncated);

    private static bool IsAudio(in Entry entry) => !entry.IsGap && !entry.IsVbrHeader && !entry.IsTruncated;

    private static int? BitrateOf(in Entry frame)
    {
        var header = new MpegHeader(frame.Header);
        return header.IsFreeFormat ? null : header.Bitrate;
    }

    private static long Ticks(in Entry frame)
    {
        var header = new MpegHeader(frame.Header);
        return header.Samples * (Clock / header.SampleRate);
    }

    // For a non-negative numerator and a positive denominator.
    private static long RoundHalfEven(Int128 numerator, Int128 denominator)
    {
        (Int128 quotient, Int128 rest) = Int128.DivRem(numerator, denominator);
        if (rest * 2 > denominator || (rest * 2 == denominator && quotient % 2 != 0))
        {
            quotient++;
        }

        return (long)quotient;
    }

    private static string DurationText(long ticks) => Duration(ticks).ToString();

    // In seconds, rounded half to even to the millisecond.
    private static NodeValue Duration(long ticks) => NodeValue.Decimal(RoundHalfEven((Int128)ticks * 1000, Clock) / 1000.0, 3, "s");

    private static string TenthsText(long tenths) => $"{Number(tenths / 10)}.{Number(tenths % 10)}";

    private static string FramesText(int count) => count == 1 ? "1 frame" : $"{Number(count)} frames";

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
