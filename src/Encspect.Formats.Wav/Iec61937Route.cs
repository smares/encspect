using Encspect.Core;

namespace Encspect.Formats.Wav;

// Hands the bursts of a chain to the codec parser and reports what the bursts say about the file.
internal static class Iec61937Route
{
    // The codec ID standing for a chain of bursts whose coding the analyzer has no parser for.
    public const string Unanalyzed = "iec-61937";

    // The container node of the bursts under the data chunk and the walk; null when the codec parser does not exist.
    private static (BurstRun Run, Node Container) Walk(State s, DataChunk data, BurstChain chain)
    {
        var container = new Node("IEC 61937 Bursts", chain.First, data.Start + data.Available - chain.First, NodeValue.Integer(0, "bursts", "burst"))
        {
            Description = Explain.BurstsContainer,
            Topic = Topics.Iec61937,
        };
        data.Node.Add(container);
        return (new BurstRun(s, s.Context.Reader.Slice(data.Start, data.Available), chain, container), container);
    }

    private static void Close(BurstRun run, Node container) =>
        container.Value = NodeValue.Integer(run.Bursts, "bursts", "burst");

    // Reads the bursts and gives their payloads to the codec parser as packets. True when the codec parser took them.
    public static bool ParseBursts(State s, WaveFormat format, DataChunk data, BurstChain chain, string codec)
    {
        (BurstRun run, Node container) = Walk(s, data, chain);
        bool isRouted = s.Context.ParsePackets(codec, data.Node, run.Payloads());
        if (!isRouted)
        {
            // No codec parser: the walk still reads the bursts for their findings.
            foreach (MappedBytes _ in run.Payloads())
            {
            }
        }

        Close(run, container);
        return isRouted;
    }

    // What the container states against what the bursts and the codec parser found: the file is a carrier, the stream the coding's.
    public static void Compare(State s, WaveFormat format, DataChunk data, StreamInfo stream)
    {
        BurstChain chain = s.Burst!;
        Node container = data.Node.Children.First(c => c.Name == "IEC 61937 Bursts");
        long bursts = container.Value?.AsInteger() ?? 0;
        int bits = format.Bits;
        int channels = format.Channels;
        long rate = format.SampleRate;
        string name = chain.Type.Name;
        s.Findings.Add(Severity.Info, "wav.iec61937-in-pcm", container, _ =>
            $"The data holds {Counts.Of(bursts, "data-burst", "data-bursts")} of IEC 61937 with {name} frames, as S/PDIF passthrough writes them, not samples; the fmt chunk describes the carrier ({bits} bits, {Counts.Of(channels, "channel", "channels")} at {rate} Hz), and the stream is read as {stream.Codec}.");

        // The link runs at the sampling rate of the coding, and at four times it for E-AC-3 (IEC 61937-1 6.1.5; Microsoft, "Representing Formats for IEC 61937 Transmissions").
        long expected = chain.Type.Codec == "e-ac-3" ? 4L * stream.SampleRate : stream.SampleRate;
        if (stream.SampleRate > 0 && rate > 0 && rate != expected)
        {
            int content = stream.SampleRate;
            s.Findings.Add(Severity.Warning, "wav.iec61937-rate-mismatch", s.FormatChunkNode ?? s.Container, _ =>
                $"The fmt chunk states {rate} Hz, but the {stream.Codec} stream has a sampling rate of {content} Hz and its bursts take a link rate of {expected} Hz.");
        }
    }

    // Bursts of a coding with no codec parser here (MPEG audio, AAC, ATRAC, WMA Professional, MAT): the stream is described by what the bursts state.
    public static void AddUnanalyzed(State s, WaveFormat format, DataChunk data, BurstChain chain)
    {
        (BurstRun run, Node container) = Walk(s, data, chain);
        foreach (MappedBytes _ in run.Payloads())
        {
        }

        Close(run, container);
        ParseContext context = s.Context;
        BurstType type = chain.Type;
        // The repetition period counts IEC 60958 frames, so the length follows from the link rate; the sampling rate of the coding and the number of its
        // samples are not known, since the link runs at twice the rate for some data types.
        long linkFrames = run.Bursts * (long)type.Repetition;
        long rate = format.SampleRate;
        double duration = rate > 0 ? linkFrames / (double)rate : 0;
        double? bitrate = duration > 0 ? run.PayloadBytes * 8 / duration / 1000 : null;
        Node node = data.Node;
        Node Cover(string name, NodeValue? value, string description, string? topic = null) =>
            new(name, node.Offset, node.Length, value) { Description = description, Topic = topic };

        var statistics = new Node("Statistics", node.Offset, node.Length) { Kind = NodeKind.Statistics, Description = Explain.Statistics };
        statistics.Add(Cover("Codec", type.Name, Explain.StatBurstCoding, Topics.Iec61937));
        statistics.Add(Cover("Bursts", NodeValue.Integer(run.Bursts, "bursts", "burst"), Explain.StatBursts, Topics.Iec61937));
        statistics.Add(Cover("Duration", NodeValue.Decimal(duration, 3, "s"), Explain.StatDuration));
        statistics.Add(Cover("Link Rate", NodeValue.Integer(rate, "Hz"), Explain.StatLinkRate, Topics.Iec61937));
        if (bitrate is not null)
        {
            statistics.Add(Cover("Average Bitrate", NodeValue.Decimal(bitrate.Value, 1, "kbps"), Explain.StatBitrate));
        }

        node.Add(statistics);
        string coding = type.Name;
        long count = run.Bursts;
        s.Findings.Add(Severity.Info, "wav.iec61937-payload-not-analyzed", container, _ =>
            $"The data holds {Counts.Of(count, "data-burst", "data-bursts")} of IEC 61937 with {coding} frames, which the analyzer has no parser for; the stream is described by what the bursts state.");
        context.AddStream(new StreamInfo
        {
            Nodes = [node],
            Codec = type.Name,
            SampleRate = 0,
            Channels = 0,
            Samples = 0,
            Duration = duration,
            AverageBitrate = bitrate,
        });
    }
}
