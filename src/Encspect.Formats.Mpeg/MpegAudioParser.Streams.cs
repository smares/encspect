using Encspect.Core;

namespace Encspect.Formats.Mpeg;

// The streams of the file: frames of one version, layer, sample rate and free format or not, in
// file order across gaps and tags, so a stream that an embedded tag splits into two regions is one
// stream, and a change of those parameters starts the next (MpegHeader.IsSameStream).
public sealed partial class MpegAudioParser
{
    private static void AddStreams(StreamState state, ParseContext context)
    {
        var frames = new List<(RegionState Region, Entry Frame)>();
        MpegHeader? previous = null;
        foreach (RegionState region in state.Regions)
        {
            foreach (Entry entry in region.Entries.Where(e => !e.IsGap))
            {
                var header = new MpegHeader(entry.Header);
                if (previous is { } last && !header.IsSameStream(last))
                {
                    context.AddStream(Stream(state, frames));
                    frames.Clear();
                }

                frames.Add((region, entry));
                previous = header;
            }
        }

        if (frames.Count > 0)
        {
            context.AddStream(Stream(state, frames));
        }
    }

    private static StreamInfo Stream(StreamState state, List<(RegionState Region, Entry Frame)> frames)
    {
        var header = new MpegHeader(frames[0].Frame.Header);
        List<(RegionState Region, Entry Frame)> audio = frames.Where(f => IsAudio(f.Frame)).ToList();
        long ticks = audio.Sum(f => Ticks(f.Frame));
        long bits = audio.Sum(f => f.Frame.Length * 8);
        List<RegionState> regions = frames.Select(f => f.Region).Distinct().ToList();
        Node? encoder = regions.Select(r => r.Encoder).FirstOrDefault(e => e is not null);
        string? confidence = encoder?.Children.FirstOrDefault(c => c.Name == "Confidence")?.Value?.ToString();

        return new StreamInfo
        {
            Nodes = regions.Select(r => r.Node!).ToList(),
            Codec = $"{header.VersionName} Layer {header.LayerName}",
            SampleRate = header.SampleRate,
            Channels = header.IsMono ? 1 : 2,
            ChannelMode = header.ChannelMode,
            Samples = audio.Sum(f => (long)new MpegHeader(f.Frame.Header).Samples),
            Duration = ticks / (double)Clock,
            AverageBitrate = ticks > 0 ? bits * (double)Clock / (ticks * 1000.0) : null,
            IsVbr = audio.Select(f => BitrateOf(f.Frame)).Distinct().Count() > 1,
            Encoder = encoder?.Value?.ToString(),
            EncoderConfidence = Enum.TryParse(confidence, out Confidence parsed) ? parsed : null,
            Series = Series(state, audio),
        };
    }

    // Per frame, the bitrate its header gives (a free-format frame's from its length) and the bit
    // reservoir fill when it starts; per second of audio, the bits of the frames that start in it
    // over their duration and their mean reservoir fill, rounded half to even. The reservoir fill
    // is there when every frame is Layer III.
    private static List<Series> Series(StreamState state, List<(RegionState Region, Entry Frame)> audio)
    {
        if (audio.Count == 0)
        {
            return [];
        }

        var offsets = new double[audio.Count];
        var bitrates = new double[audio.Count];
        var fills = new double[audio.Count];
        var seconds = new List<(long Bits, long Ticks, long Fill, int Count)>();
        long time = 0;
        for (int i = 0; i < audio.Count; i++)
        {
            state.CheckCancellation(i);
            Entry frame = audio[i].Frame;
            var header = new MpegHeader(frame.Header);
            offsets[i] = frame.Offset;
            bitrates[i] = header.IsFreeFormat ? frame.Length * 8.0 * header.SampleRate / header.Samples / 1000 : header.Bitrate;
            fills[i] = frame.ReservoirFill;

            // Frames last less than a second, so every second has a frame that starts in it.
            int second = (int)(time / Clock);
            while (seconds.Count <= second)
            {
                seconds.Add(default);
            }

            (long b, long t, long f, int c) = seconds[second];
            seconds[second] = (b + (frame.Length * 8), t + Ticks(frame), f + frame.ReservoirFill, c + 1);
            time += Ticks(frame);
        }

        double[] starts = Enumerable.Range(0, seconds.Count).Select(s => (double)s).ToArray();
        Node? distribution = audio[0].Region.Distribution;
        var series = new List<Series>
        {
            new("Bitrate", "kbps", SeriesAxis.FrameOffset, offsets, bitrates),
            new("Bitrate Over Time", "kbps", SeriesAxis.Seconds, starts,
                seconds.Select(s => s.Ticks > 0 ? (double)RoundHalfEven((Int128)s.Bits * Clock, (Int128)s.Ticks * 1000) : 0).ToArray(), distribution),
        };

        if (audio.All(f => f.Frame.SideInfo is not null))
        {
            series.Add(new("Reservoir Fill", "bytes", SeriesAxis.FrameOffset, offsets, fills));
            series.Add(new("Reservoir Fill Over Time", "bytes", SeriesAxis.Seconds, starts,
                seconds.Select(s => s.Count > 0 ? (double)RoundHalfEven(s.Fill, s.Count) : 0).ToArray(), audio[0].Region.MaximumFill));
        }

        return series;
    }
}
