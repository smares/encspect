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

    private static FrameList AllFrames(StreamState state) => new(state.Regions.SelectMany(r => r.Entries).Where(e => !e.IsGap).ToList());

    private static void AddStatistics(StreamState state)
    {
        FrameList all = AllFrames(state);
        foreach (RegionState region in state.Regions)
        {
            region.Statistics = Statistics(region.Entries, all);
        }

        foreach (Entry frame in all.Frames.Where(e => e.VbrFrame is not null))
        {
            CheckHeaderCounts(state.Findings, frame, all);
        }
    }

    private static Node? Statistics(List<Entry> entries, FrameList all)
    {
        List<Entry> audio = entries.Where(IsAudio).ToList();
        if (audio.Count == 0)
        {
            return null;
        }

        long start = audio[0].Offset;
        long length = audio[^1].End - start;
        long ticks = audio.Sum(Ticks);
        long bits = audio.Sum(e => e.Length * 8);
        Node node = new("Statistics", start, length, $"{FramesText(audio.Count)}, {DurationText(ticks)}");
        Add("Frames", Number(audio.Count));
        Add("Samples", Number(audio.Sum(e => (long)new MpegHeader(e.Header).Samples)));
        Add("Duration", DurationText(ticks));

        int?[] bitrates = audio.Select(BitrateOf).ToArray();
        Add("IsVbr", bitrates.Distinct().Count() > 1 ? "true" : "false");
        Add("Average Bitrate", TenthsText(RoundHalfEven((Int128)bits * Clock * 10, (Int128)ticks * 1000)) + " kbps");
        int[] fixedRates = bitrates.OfType<int>().Distinct().Order().ToArray();
        if (fixedRates.Length > 0)
        {
            Add("Minimum Bitrate", $"{Number(fixedRates[0])} kbps");
            Add("Maximum Bitrate", $"{Number(fixedRates[^1])} kbps");
        }

        var counts = bitrates.GroupBy(b => b).OrderBy(g => g.Key is null).ThenBy(g => g.Key).ToList();
        Node distribution = Add("Bitrate Distribution", counts.Count == 1 ? "1 bitrate" : $"{Number(counts.Count)} bitrates");
        foreach (var group in counts)
        {
            long tenths = RoundHalfEven(group.Count() * 1000L, audio.Count);
            distribution.Add(group.Key is { } kbps ? $"{Number(kbps)} kbps" : "free", start, length,
                $"{FramesText(group.Count())} ({TenthsText(tenths)}%)");
        }

        // Per second of audio, the frames that start in it: their bits over their duration, and
        // their mean reservoir fill.
        var seconds = new SortedDictionary<long, (long Bits, long Ticks, long Fill, int Count)>();
        long time = 0;
        foreach (Entry frame in audio)
        {
            seconds.TryGetValue(time / Clock, out var second);
            seconds[time / Clock] = (second.Bits + (frame.Length * 8), second.Ticks + Ticks(frame), second.Fill + frame.ReservoirFill, second.Count + 1);
            time += Ticks(frame);
        }

        Add("Bitrate Over Time", string.Join(',', seconds.Values.Select(s => Number(RoundHalfEven((Int128)s.Bits * Clock, (Int128)s.Ticks * 1000)))));
        if (audio.All(e => e.SideInfo is not null))
        {
            Add("Maximum Reservoir Fill", $"{Number(audio.Max(e => e.ReservoirFill))} bytes");
            Add("Reservoir Fill Over Time", string.Join(',', seconds.Values.Select(s => Number(RoundHalfEven(s.Fill, s.Count)))));
        }

        // Each header's figures cover the frames it describes; where those are not all of the
        // region's (files were joined), the node says which.
        var regionAudio = audio.Select(e => e.Offset).ToHashSet();
        long firstFrame = entries.First(e => !e.IsGap).Offset;
        foreach (Entry headerFrame in entries.Where(e => e.VbrFrame is not null))
        {
            AddHeaderComparison(headerFrame, all);
        }

        return node;

        Node Add(string name, string value) => node.Add(name, start, length, value);

        void AddHeaderComparison(Entry frame, FrameList frames)
        {
            VbrHeader header = VbrHeader.TryRead(frame.VbrFrame!, new MpegHeader(frame.Header))!;
            List<Entry> covered = Covered(frame, frames);
            List<Entry> inside = covered.Where(e => regionAudio.Contains(e.Offset)).ToList();
            (long from, long span) = inside.Count > 0 ? (inside[0].Offset, inside[^1].End - inside[0].Offset) : (node.Offset, node.Length);
            string? scope = frame.Offset == firstFrame && inside.Count == audio.Count ? null
                : $"{FramesText(covered.Count)} after the {header.Type} header at 0x{frame.Offset:X}";
            Node? counts = header.FrameCount is null && header.ByteCount is null ? null : node.Add(new Node("VBR Header", from, span, scope));
            if (header.FrameCount is { } claimedFrames)
            {
                counts!.Add("Claimed Frames", from, span, Number(claimedFrames));
                counts.Add("Counted Frames", from, span, Number(covered.Count));
            }

            if (header.ByteCount is { } claimedBytes)
            {
                counts!.Add("Claimed Bytes", from, span, Number(claimedBytes));
                counts.Add("Counted Bytes", from, span, Number(frame.Length + covered.Sum(e => e.Length)));
            }

            if (header.HasLameTag)
            {
                Node gapless = node.Add(new Node("Gapless", from, span, scope));
                gapless.Add("Encoder Delay", from, span, $"{Number(header.EncoderDelay)} samples");
                gapless.Add("Encoder Padding", from, span, $"{Number(header.EncoderPadding)} samples");
                long count = covered.Sum(e => (long)new MpegHeader(e.Header).Samples) - header.EncoderDelay - header.EncoderPadding;
                if (count >= 0)
                {
                    gapless.Add("Sample Count", from, span, Number(count));
                    gapless.Add("Duration", from, span, DurationText(count * (Clock / new MpegHeader(frame.Header).SampleRate)));
                }
            }
        }
    }

    // Xing's and Fraunhofer's encoders count the header's own frame, LAME does not; byte counts
    // include it, and LAME's leave out what is not its own, such as a tag added between frames.
    // A truncated last frame was written whole, so it counts.
    private static void CheckHeaderCounts(FindingCollector findings, Entry frame, FrameList all)
    {
        VbrHeader header = VbrHeader.TryRead(frame.VbrFrame!, new MpegHeader(frame.Header))!;
        List<Entry> covered = Covered(frame, all);
        long position = frame.Offset;
        Node node = FindingNode(frame);
        if (header.FrameCount is { } claimedFrames && claimedFrames != covered.Count && claimedFrames != covered.Count + 1)
        {
            findings.Add(Severity.Warning, "mpeg.vbr-frame-count-mismatch", node, count =>
                $"The {header.Type} header of {Frames(count)} claims a frame count the stream after it does not have, the first at 0x{position:X}: {Number(claimedFrames)} claimed, {Number(covered.Count)} counted.");
        }

        // The LAME tag's delay and padding count from the frames after its own, so a count that
        // includes the header (LAME 3.94 beta 1 writes one) moves the end of the music by a frame.
        if (header.HasLameTag && header.FrameCount is { } counted && counted == covered.Count + 1 && covered.Count > 0)
        {
            int frameSamples = new MpegHeader(covered[0].Header).Samples;
            findings.Add(Severity.Warning, "mpeg.vbr-frame-count-includes-header", node, count =>
                $"The Xing header of {Frames(count)} counts its own frame, though the LAME tag after it counts its delay and padding from the frames that follow; decoders that take the length from the count, such as ffmpeg and mpg123, play {Number(frameSamples)} samples more than were encoded. The first at 0x{position:X}.");
        }

        long bytes = frame.Length + covered.Sum(e => e.Length);
        if (header.ByteCount is { } claimedBytes && claimedBytes != bytes && claimedBytes != bytes - frame.Length)
        {
            findings.Add(Severity.Warning, "mpeg.vbr-byte-count-mismatch", node, count =>
                $"The {header.Type} header of {Frames(count)} claims a byte count the stream after it does not have, the first at 0x{position:X}: {Number(claimedBytes)} claimed, {Number(bytes)} counted.");
        }

        long samples = covered.Sum(e => (long)new MpegHeader(e.Header).Samples);
        if (header.HasLameTag && header.EncoderDelay + header.EncoderPadding > samples)
        {
            findings.Add(Severity.Warning, "mpeg.gapless-exceeds-stream", node, count =>
                $"The LAME tag of {Frames(count)} gives an encoder delay and padding of {Number(header.EncoderDelay + header.EncoderPadding)} samples, more than the {Number(samples)} of the stream after it; the first at 0x{position:X}.");
        }
    }

    // Whether the stream after a VBR header holds less than the header claims: its last frame is
    // truncated, or the counts are more than the frames and bytes there.
    private static bool IsCutShort(Entry frame, VbrHeader header, List<Entry> covered) =>
        covered.Any(e => e.IsTruncated) || header.FrameCount > covered.Count + 1 || header.ByteCount > frame.Length + covered.Sum(e => e.Length);

    // The frames a VBR header describes: from its frame to the end of its stream, the next VBR
    // header or a change of version, layer or sample rate, across gaps and tags.
    private static List<Entry> Covered(Entry headerFrame, FrameList all)
    {
        var header = new MpegHeader(headerFrame.Header);
        var covered = new List<Entry>();
        for (int i = all.IndexOf(headerFrame) + 1; i < all.Frames.Count; i++)
        {
            Entry frame = all.Frames[i];
            var next = new MpegHeader(frame.Header);
            if (frame.VbrFrame is not null || next.VersionBits != header.VersionBits || next.Layer != header.Layer
                || next.SampleRateIndex != header.SampleRateIndex)
            {
                break;
            }

            covered.Add(frame);
        }

        return covered;
    }

    // The frames of every region in file order, with each frame's index, so each VBR header's
    // frames are found without walking the list from the start.
    private sealed class FrameList
    {
        private readonly Dictionary<long, int> _index = [];

        public FrameList(List<Entry> frames)
        {
            Frames = frames;
            for (int i = 0; i < frames.Count; i++)
            {
                _index.TryAdd(frames[i].Offset, i);
            }
        }

        public List<Entry> Frames { get; }

        public int IndexOf(Entry frame) => _index[frame.Offset];
    }

    private static bool IsAudio(Entry entry) => !entry.IsGap && entry.VbrFrame is null && !entry.IsTruncated;

    private static int? BitrateOf(Entry frame)
    {
        var header = new MpegHeader(frame.Header);
        return header.IsFreeFormat ? null : header.Bitrate;
    }

    private static long Ticks(Entry frame)
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

    private static string DurationText(long ticks)
    {
        long ms = RoundHalfEven((Int128)ticks * 1000, Clock);
        return $"{Number(ms / 1000)}.{(ms % 1000).ToString("D3", CultureInfo.InvariantCulture)} s";
    }

    private static string TenthsText(long tenths) => $"{Number(tenths / 10)}.{Number(tenths % 10)}";

    private static string FramesText(int count) => count == 1 ? "1 frame" : $"{Number(count)} frames";

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
