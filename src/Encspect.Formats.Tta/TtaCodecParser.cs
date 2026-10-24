using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Tta;

// TTA frames a container stores one to a packet (Matroska's A_TTA1, whose codec mapping says each frame is kept intact, CRC-32 included, and the
// header and the seek table are dropped): the coded samples and the CRC-32 over them. The header comes from the track: the CodecPrivate holds
// it (the mapping's "TTA Header Structure"), or, when a muxer writes none (FFmpeg's does not), the container states the sample rate, the
// channels and the bits per sample, and the frames then show no sample count.
public sealed class TtaCodecParser : ICodecParser
{
    public const string CodecId = "tta";

    public string Id => CodecId;

    public void Parse(CodecContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Packets is not { } packets)
        {
            context.AddFinding(Severity.Info, "tta.payload-not-analyzed", "TTA handed over as one payload is not analyzed; only packets, one for each frame, are.", context.Parent);
            return;
        }

        var state = new State(context, isContained: true) { Stream = context.Parent };
        state.Records.DataEnd = long.MaxValue;
        if (context.Configuration is { } configuration)
        {
            Configure(state, configuration);
        }

        long start = -1;
        long end = 0;
        int index = 0;
        foreach (MappedBytes packet in packets)
        {
            if (index % 4096 == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
            }

            start = start < 0 ? packet.FileStart : start;
            end = Math.Max(end, packet.FileEnd);
            Add(state, packet, index++);
        }

        if (state.Header is { SampleRate: > 0 } header && header.FrameCount != state.Listed)
        {
            long expected = header.FrameCount;
            long found = state.Listed;
            state.Findings.Add(Severity.Warning, "tta.frame-count-differs", state.Stream, _ =>
                $"The header's {Counts.Of(header.Samples, "sample", "samples")} at {header.SampleRate} Hz call for {Counts.Of(expected, "frame", "frames")}, but the container holds {found}.");
        }

        if (start < 0)
        {
            start = context.Parent.Offset;
            end = context.Parent.End;
        }

        Summary.Finish(state, start, end);
    }

    private static void Configure(State s, CodecConfiguration configuration)
    {
        MappedBytes data = configuration.Data;
        if (data.Length >= TtaHeader.Length && TtaHeader.HasSignature(data.Span))
        {
            TtaHeader header = TtaHeader.Read(data.Span);
            s.Records.Header = header;
            s.SampleRate = header.SampleRate;
            s.Channels = header.Channels;
            s.BitsPerSample = header.BitsPerSample;
            s.HeaderBytes = data.Slice(0, TtaHeader.Length);
            s.HeaderNode = configuration.Parent.Add(Nodes.Header(FieldMap.Of(data.Slice(0, TtaHeader.Length)), TtaHeader.Length, header, isConfiguration: true));
            Summary.HeaderFindings(s, header);
            Compare(s, header, configuration);
            if (data.Length > TtaHeader.Length)
            {
                // The mapping's "TTA Header Structure" may be read to include the seek table, which Matroska drops; whatever follows the 22 bytes is shown, not read.
                int extra = data.Length - TtaHeader.Length;
                Node rest = configuration.Parent.Add(FieldMap.Of(data.Slice(TtaHeader.Length, extra)).Bytes("Extra Data", 0, extra, NodeValue.Integer(extra, "bytes", "byte"), Explain.ConfigurationExtra));
                s.Findings.Add(Severity.Info, "tta.configuration-extra", rest, _ =>
                    $"The configuration the container stores for the track holds {Counts.Of(extra, "byte", "bytes")} after the {TtaHeader.Length} of the TTA1 header; {Counts.Agree(extra, "it is", "they are")} not read.");
            }

            return;
        }

        if (data.Length > 0)
        {
            int length = data.Length;
            s.Findings.Add(Severity.Warning, "tta.header-invalid", configuration.Parent, _ =>
                $"The configuration the container stores for the track is {Counts.Of(length, "byte", "bytes")} that do not begin with a TTA1 header, so the figures of the track come from the container.");
        }

        if (configuration.Format is { } format)
        {
            s.SampleRate = Math.Max(0, format.SampleRate);
            s.Channels = format.Channels;
            s.BitsPerSample = format.BitsPerSample;
        }
    }

    // The sample rate, channels and bits per sample the container states for the track against those of the header it stores.
    private static void Compare(State s, TtaHeader header, CodecConfiguration configuration)
    {
        if (configuration.Format is not { } format
            || (format.SampleRate == header.SampleRate && format.Channels == header.Channels && format.BitsPerSample == header.BitsPerSample))
        {
            return;
        }

        s.Findings.Add(Severity.Warning, "tta.configuration-differs", s.HeaderNode!, _ =>
            $"The container states {format.SampleRate} Hz, {Counts.Of(format.Channels, "channel", "channels")} and {Counts.Of(format.BitsPerSample, "bit", "bits")} per sample for the track, but the TTA1 header it stores for it gives "
            + $"{header.SampleRate} Hz, {Counts.Of(header.Channels, "channel", "channels")} and {Counts.Of(header.BitsPerSample, "bit", "bits")} per sample; the header's figures are used.");
    }

    private static void Add(State s, MappedBytes packet, int index)
    {
        ReadOnlySpan<byte> data = packet.Span;
        var entry = new Entry { Offset = packet.FileStart, Size = (uint)packet.Length };
        if (packet.FollowsGap)
        {
            s.HasGap = true;
        }

        if (data.Length < 4)
        {
            entry.Status = EntryStatus.TooSmall;
        }
        else
        {
            entry.StoredCrc = BinaryPrimitives.ReadUInt32LittleEndian(data[^4..]);
            entry.ComputedCrc = Crc32.Of(data[..^4]);
        }

        IReadOnlyList<ByteRange> ranges = packet.FileRanges();
        if (ranges.Count > 1 && index < Records.MaxEntries)
        {
            s.Records.Ranges[index] = ranges;
        }

        s.AddEntry(entry);
    }
}
