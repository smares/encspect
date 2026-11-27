using Encspect.Core;

namespace Encspect.Formats.Wav;

// The hand-over of the data chunk of a Windows Media Audio file to the codec parsers ASF and Matroska's A_MS/ACM route to: the data holds the coded blocks
// one after another, each the format's block alignment (the superframe size those parsers read as a packet), with the last one cut short where the data ends.
// The parsers take the WAVEFORMATEX and the codec-specific data after it, as they do from those containers.
internal static class WindowsMediaRoute
{
    public static bool Run(State s, WaveFormat format, DataChunk data, string codec)
    {
        if (format.BlockAlign < 1)
        {
            string name = format.CodingName ?? "this coding";
            s.Findings.Add(Severity.Info, "wav.coding-not-analyzed", data.Node, _ =>
                $"The coding is {name}, whose blocks the fmt chunk's block alignment gives, and it states none, so the data cannot be split for the analyzer's parser; the audio is reported by the figures the file states.");
            return true;
        }

        Node parent = format.CodecData ?? s.FormatChunkNode ?? data.Node;
        byte[] specific = format.CodecData is { } node ? s.Reader.ReadBytesAt(node.Offset, (int)Math.Min(node.Length, 1 << 16)) : [];
        long? samples = s.FactNode is not null ? (long)Math.Min(s.FactSamples64, long.MaxValue) : null;
        var configuration = new CodecConfiguration(new MappedBytes(specific, format.CodecData?.Offset ?? parent.Offset), parent)
        {
            Format = new AudioFormat(format.CodingTag, format.Channels, (int)Math.Min(format.SampleRate, int.MaxValue), (int)Math.Min(format.BytesPerSecond, int.MaxValue), format.BlockAlign, format.Bits)
            {
                ChannelMask = format.ChannelMask,
                ValidBitsPerSample = format.ValidBits,
            },
            DurationSamples = samples,
            Duration = samples is { } count && format.SampleRate > 0 ? count / (double)format.SampleRate : null,
        };
        return s.Context.ParsePackets(codec, data.Node, Blocks(s, data, format.BlockAlign), configuration);
    }

    private static IEnumerable<MappedBytes> Blocks(State s, DataChunk data, int blockAlign)
    {
        ByteReader reader = s.Reader;
        for (long done = 0; done < data.Available; done += blockAlign)
        {
            reader.CancellationToken.ThrowIfCancellationRequested();
            int length = (int)Math.Min(blockAlign, data.Available - done);
            // A last block shorter than the block alignment is a cut one (the data chunk ends inside it).
            yield return new MappedBytes(reader.ReadBytesAt(data.Start + done, length), data.Start + done) { IsCut = length < blockAlign };
        }
    }
}
