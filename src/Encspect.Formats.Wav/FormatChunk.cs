using System.Numerics;
using Encspect.Core;

namespace Encspect.Formats.Wav;

// The fmt chunk: a WAVEFORMATEX (Multimedia Programming Interface and Data Specifications 1.0, "WAVE Format Chunk"; Microsoft's WAVEFORMATEX
// documentation), and for the extensible form a WAVEFORMATEXTENSIBLE. The figures after cbSize depend on the format tag; the structures of
// the tags the specifications give are read, the rest are bytes.
internal static class FormatChunk
{
    public static void Read(State s, Fields f, Node chunk, long length)
    {
        s.FormatChunks++;
        WaveFormat? format = ReadFormat(s, f, chunk, length);
        if (s.FormatChunks > 1)
        {
            s.Findings.Add(Severity.Warning, "wav.format-repeated", chunk, n => $"The file holds {n + 1} fmt chunks; the first one describes the audio and the others are read for their fields only.");
            return;
        }

        s.Format = format;
        s.FormatChunkNode = chunk;
        if (format is not null)
        {
            chunk.Value = Summary(format);
            Validate(s, format, chunk);
        }
    }

    private static string Summary(WaveFormat format)
    {
        string name = format.CodingName ?? "unlisted coding";
        string bits = format.Bits > 0 ? $", {Counts.Of(format.AudioBits, "bit", "bits")}" : string.Empty;
        return $"{name}, {Counts.Of(format.Channels, "channel", "channels")}, {format.SampleRate} Hz{bits}";
    }

    private static WaveFormat ReadFormat(State s, Fields f, Node chunk, long length)
    {
        int tag = f.Word("Format Tag", Explain.FormatTag, v => NodeValue.Text(FormatTags.Show(v)), Topics.WaveFormat);
        int channels = f.Word("Channels", Explain.Channels, "channels");
        uint rate = f.Dword("Samples Per Second", Explain.SamplesPerSecond, "Hz");
        uint average = f.Dword("Average Bytes Per Second", Explain.AverageBytesPerSecond, "bytes/s");
        int align = f.Word("Block Alignment", Explain.BlockAlignment, "bytes");

        // The common fields of WAVEFORMAT are 14 bytes; wBitsPerSample, which PCM and the formats that follow WAVEFORMATEX add, is the 15th and 16th.
        int bits = 0;
        if (f.Remaining >= 2)
        {
            bits = f.Word("Bits Per Sample", Explain.BitsPerSample, "bits");
        }
        else
        {
            s.Findings.Add(Severity.Warning, "wav.format-short", chunk, _ =>
                $"The fmt chunk holds {Counts.Of(length, "byte", "bytes")}, fewer than the 16 of a PCMWAVEFORMAT: wBitsPerSample, which PCM and the formats that follow WAVEFORMATEX add after the 14 bytes of common fields, is missing, so the bits per sample are not known.");
        }

        int extra = 0;
        int? valid = null;
        uint? mask = null;
        Guid? sub = null;
        Node? data = null;
        if (f.Remaining >= 2)
        {
            long cbAt = f.Position;
            extra = f.Word("Codec Specific Data Size", Explain.CodecSpecificDataSize, "bytes");
            if (extra > f.Remaining)
            {
                int stated = extra;
                long held = f.Remaining;
                Node size = chunk.Children[^1];
                s.Findings.Add(Severity.Warning, "wav.format-extra-size", size, _ =>
                    $"The fmt chunk's cbSize states {Counts.Of(stated, "byte", "bytes")} of format-specific data, but only {Counts.Of(held, "byte", "bytes")} follow; they are read as far as they go.");
                extra = (int)held;
            }

            long extraEnd = f.Position + extra;
            if (tag == FormatTags.Extensible)
            {
                if (extra >= 22)
                {
                    // The union of wValidBitsPerSample, wSamplesPerBlock and wReserved: valid bits cannot exceed the container, so without a
                    // container size the word is the samples per block of a compressed format.
                    valid = bits == 0
                        ? f.Word("Samples Per Block", Explain.ExtensibleSamplesPerBlock, "samples", Topics.Extensible)
                        : f.Word("Valid Bits Per Sample", Explain.ValidBitsPerSample, "bits", Topics.Extensible);
                    mask = f.Dword("Channel Mask", Explain.ChannelMask, v => NodeValue.Hex(v, 8), Topics.Extensible);
                    if (mask != 0)
                    {
                        long maskAt = f.Position - 4;
                        string speakers = ChannelLayouts.Speakers(MaskSpeakers(mask.Value));
                        chunk.Children[^1].Add("Speakers", maskAt, 4, speakers, Explain.MaskSpeakers, Topics.ChannelLayouts);
                    }

                    sub = f.Guid("Sub-Format", Explain.SubFormat, g => FormatTags.TagOfSubFormat(g) is { } t ? FormatTags.NameOf(t) ?? "unlisted coding" : FormatTags.NamedSubFormat(g)?.Name);
                    if (extra > 22)
                    {
                        data = f.Bytes("Codec Specific Data", extra - 22, Explain.CodecSpecificData, Topics.WaveCompressed);
                    }
                }
                else if (extra > 0)
                {
                    Node size = chunk.Children[^1];
                    int held = extra;
                    s.Findings.Add(Severity.Warning, "wav.extensible-incomplete", size, _ =>
                        $"The format tag is 0xFFFE (WAVEFORMATEXTENSIBLE), whose cbSize is at least 22 for the valid bits, the channel mask and the sub-format, but it states {Counts.Of(held, "byte", "bytes")}.");
                    data = f.Bytes("Codec Specific Data", extra, Explain.CodecSpecificData);
                }
                else
                {
                    s.Findings.Add(Severity.Warning, "wav.extensible-incomplete", chunk.Children[^1], _ =>
                        "The format tag is 0xFFFE (WAVEFORMATEXTENSIBLE), whose cbSize is at least 22 for the valid bits, the channel mask and the sub-format, but it states 0.");
                }
            }
            else if (extra > 0)
            {
                data = ReadExtra(f, tag, extra);
            }

            if (f.Position < extraEnd)
            {
                f.Bytes("Codec Specific Data", extraEnd - f.Position, Explain.CodecSpecificData, Topics.WaveCompressed);
            }
        }

        if (f.Remaining > 0)
        {
            f.Bytes("Extra Data", f.Remaining, Explain.ExtraData);
        }

        return new WaveFormat(tag, channels, rate, average, align, bits, extra, (int)length) { ValidBits = valid, ChannelMask = mask, SubFormat = sub, CodecData = data };
    }

    // The figures after cbSize that the specifications give for a format tag (RIFFNEW.pdf, the WAVE type of each; mmreg.h for MPEGLAYER3WAVEFORMAT).
    private static Node? ReadExtra(Fields f, int tag, int extra)
    {
        long start = f.Position;
        long end = start + extra;
        switch (tag)
        {
            case FormatTags.MicrosoftAdpcm when extra >= 4:
                f.Word("Samples Per Block", Explain.SamplesPerBlock, "samples", Topics.WaveCompressed);
                int count = f.Word("Coefficient Sets", Explain.CoefficientSets, "sets", Topics.WaveCompressed);
                long setsLength = Math.Min((long)count * 4, end - f.Position);
                Node sets = f.Node.Add(new Node("Coefficients", f.Position, setsLength, NodeValue.Integer(count, "sets", "set")) { Description = Explain.Coefficients, Topic = Topics.WaveCompressed });
                for (int i = 0; i < setsLength / 4; i++)
                {
                    if (i >= 64)
                    {
                        f.Reader.Skip(4);
                        continue;
                    }

                    long at = f.Position;
                    short first = unchecked((short)f.ReadWord());
                    short second = unchecked((short)f.ReadWord());
                    sets.Add($"Coefficient Set {i}", at, 4, $"{first}, {second}", Explain.CoefficientSet, Topics.WaveCompressed);
                }

                return null;
            case FormatTags.ImaAdpcm or FormatTags.Gsm610 when extra >= 2:
                f.Word("Samples Per Block", Explain.SamplesPerBlock, "samples", Topics.WaveCompressed);
                return null;
            case FormatTags.Mpeg when extra >= 22:
                f.Word("Layer", Explain.MpegLayer, v => NodeValue.Text(MpegLayers(v)), Topics.WaveCompressed);
                f.Dword("Bitrate", Explain.MpegBitrate, v => v == 0 ? NodeValue.Text("0 (variable or not standard)") : NodeValue.Integer(v, "bit/s"), Topics.WaveCompressed);
                f.Word("Mode", Explain.MpegMode, v => NodeValue.Text(MpegModes(v)), Topics.WaveCompressed);
                f.Word("Mode Extension", Explain.MpegModeExtension, v => NodeValue.Hex(v, 4), Topics.WaveCompressed);
                f.Word("Emphasis", Explain.MpegEmphasis, v => NodeValue.Text(MpegEmphasis(v)), Topics.WaveCompressed);
                f.Word("Flags", Explain.MpegFlags, v => NodeValue.Hex(v, 4), Topics.WaveCompressed);
                f.Dword("Presentation Time Stamp Low", Explain.PtsLow, v => NodeValue.Integer(v), Topics.WaveCompressed);
                f.Dword("Presentation Time Stamp High", Explain.PtsHigh, v => NodeValue.Integer(v), Topics.WaveCompressed);
                return null;
            case FormatTags.MpegLayer3 when extra >= 12:
                f.Word("Identifier", Explain.Mp3Id, v => NodeValue.Text(v switch { 0 => "0 (unknown)", 1 => "1 (MPEG)", 2 => "2 (constant frame size)", _ => v.ToString(System.Globalization.CultureInfo.InvariantCulture) }), Topics.WaveCompressed);
                f.Dword("Flags", Explain.Mp3Flags, v => NodeValue.Text(v switch { 0 => "0 (padding as in ISO)", 1 => "1 (padding on)", 2 => "2 (padding off)", _ => $"0x{v:X8}" }), Topics.WaveCompressed);
                f.Word("Block Size", Explain.Mp3BlockSize, "bytes", Topics.WaveCompressed);
                f.Word("Frames Per Block", Explain.Mp3FramesPerBlock, "frames", Topics.WaveCompressed);
                f.Word("Codec Delay", Explain.Mp3CodecDelay, "samples", Topics.WaveCompressed);
                return null;
            default:
                return f.Bytes("Codec Specific Data", extra, Explain.CodecSpecificData, Topics.WaveCompressed);
        }
    }

    private static string MpegLayers(int flags)
    {
        var names = new List<string>();
        if ((flags & 1) != 0)
        {
            names.Add("Layer I");
        }

        if ((flags & 2) != 0)
        {
            names.Add("Layer II");
        }

        if ((flags & 4) != 0)
        {
            names.Add("Layer III");
        }

        return $"0x{flags:X4} ({(names.Count == 0 ? "none" : string.Join(", ", names))})";
    }

    private static string MpegModes(int flags)
    {
        var names = new List<string>();
        if ((flags & 1) != 0)
        {
            names.Add("stereo");
        }

        if ((flags & 2) != 0)
        {
            names.Add("joint stereo");
        }

        if ((flags & 4) != 0)
        {
            names.Add("dual channel");
        }

        if ((flags & 8) != 0)
        {
            names.Add("single channel");
        }

        return $"0x{flags:X4} ({(names.Count == 0 ? "none" : string.Join(", ", names))})";
    }

    private static string MpegEmphasis(int value) => value switch
    {
        1 => "1 (no emphasis)",
        2 => "2 (50/15 ms emphasis)",
        3 => "3 (reserved)",
        4 => "4 (CCITT J.17)",
        _ => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    // The speakers of a channel mask in the order of its bits, which is the order of the channels in the data. The bits past the eighteen
    // positions of the WAVE channel mask are the EBU's additions (Tech 3306-2009 section 3) and SPEAKER_ALL; they stand for channels without a position.
    public static List<Speaker> MaskSpeakers(uint mask)
    {
        var speakers = new List<Speaker>();
        for (int bit = 0; bit < 32; bit++)
        {
            if ((mask & (1u << bit)) != 0)
            {
                speakers.Add(bit < 18 ? (Speaker)(bit + 1) : Speaker.Discrete);
            }
        }

        return speakers;
    }

    // The speakers of the channels of a stream: with fewer channels than the mask has bits the extra, most significant bits are ignored, and with
    // more the remaining channels are assigned to no speaker (Microsoft, "Multiple channel audio data and WAVE files", "Details about dwChannelMask").
    public static IReadOnlyList<Speaker> Layout(uint mask, int channels)
    {
        List<Speaker> speakers = MaskSpeakers(mask);
        if (speakers.Count > channels)
        {
            speakers.RemoveRange(channels, speakers.Count - channels);
        }

        while (speakers.Count < channels)
        {
            speakers.Add(Speaker.Discrete);
        }

        return speakers;
    }

    private static void Validate(State s, WaveFormat format, Node chunk)
    {
        Node At(string name) => chunk.Children.FirstOrDefault(c => c.Name == name) ?? chunk;
        int coding = format.CodingTag;
        if (format.Channels == 0)
        {
            s.Findings.Add(Severity.Warning, "wav.channels-zero", At("Channels"), _ => "The fmt chunk states 0 channels.");
        }

        if (format.SampleRate == 0)
        {
            s.Findings.Add(Severity.Warning, "wav.sample-rate-zero", At("Samples Per Second"), _ => "The fmt chunk states a sample rate of 0.");
        }

        if (FormatTags.IsLinear(coding) && format.Bits == 0 && format.FormatSize >= 16)
        {
            s.Findings.Add(Severity.Warning, "wav.bits-per-sample-zero", At("Bits Per Sample"), _ => "The fmt chunk states 0 bits per sample for a coding that stores its samples as they are.");
        }

        if (coding == FormatTags.IeeeFloat && format.Bits is not (32 or 64) && format.Bits != 0)
        {
            int bits = format.Bits;
            s.Findings.Add(Severity.Warning, "wav.float-bits", At("Bits Per Sample"), _ => $"The format is IEEE float with {Counts.Of(bits, "bit", "bits")} per sample; floating-point samples take 32 or 64 bits.");
        }

        if (coding is FormatTags.ALaw or FormatTags.MuLaw && format.Bits is not 8 && format.Bits != 0)
        {
            int bits = format.Bits;
            s.Findings.Add(Severity.Warning, "wav.companded-bits", At("Bits Per Sample"), _ => $"The format is companded with {Counts.Of(bits, "bit", "bits")} per sample; the companded formats take 8.");
        }

        if (FormatTags.IsLinear(coding) && format.Bits > 0 && format.Bits % 8 == 0 && format.Channels > 0)
        {
            long expectedAlign = (long)format.Channels * format.Bits / 8;
            if (format.BlockAlign != expectedAlign)
            {
                int stated = format.BlockAlign;
                s.Findings.Add(Severity.Warning, "wav.block-align-mismatch", At("Block Alignment"), _ =>
                    $"The block alignment is {Counts.Of(stated, "byte", "bytes")}, but {Counts.Of(format.Channels, "channel", "channels")} of {Counts.Of(format.Bits, "bit", "bits")} take {Counts.Of(expectedAlign, "byte", "bytes")} per sample frame.");
            }

            long expectedRate = format.SampleRate * format.Channels * format.Bits / 8;
            if (format.BytesPerSecond != expectedRate)
            {
                long stated = format.BytesPerSecond;
                s.Findings.Add(Severity.Warning, "wav.average-bytes-mismatch", At("Average Bytes Per Second"), _ =>
                    $"The average data rate is {Counts.Of(stated, "byte", "bytes")} per second, but the sample rate, channels and bits give {expectedRate}.");
            }
        }

        if (format.IsExtensible && format.Bits > 0 && format.Bits % 8 != 0)
        {
            int bits = format.Bits;
            s.Findings.Add(Severity.Warning, "wav.extensible-container-bits", At("Bits Per Sample"), _ =>
                $"The container size of an extensible format is {Counts.Of(bits, "bit", "bits")}, but WAVEFORMATEXTENSIBLE wants a whole number of bytes: its containers are byte-aligned.");
        }

        if (format.IsExtensible && format.Bits > 0 && format.ValidBits is { } valid && FormatTags.IsLinear(coding) && valid > format.Bits)
        {
            int bits = format.Bits;
            s.Findings.Add(Severity.Warning, "wav.valid-bits-exceed-container", At("Valid Bits Per Sample"), _ =>
                $"The valid bits per sample ({valid}) are more than the {Counts.Of(bits, "bit", "bits")} of the container.");
        }

        if (format.IsExtensible && format.ChannelMask is { } mask && mask != 0 && (mask & 0x80000000) == 0 && BitOperations.PopCount(mask) != format.Channels)
        {
            int positions = BitOperations.PopCount(mask);
            int channels = format.Channels;
            bool isMore = positions > channels;
            s.Findings.Add(Severity.Info, "wav.channel-mask-mismatch", At("Channel Mask"), _ => isMore
                ? $"The channel mask names {Counts.Of(positions, "speaker", "speakers")}, but the file has {Counts.Of(channels, "channel", "channels")}; the extra bits, the most significant, are ignored."
                : $"The channel mask names {Counts.Of(positions, "speaker", "speakers")}, but the file has {Counts.Of(channels, "channel", "channels")}; the remaining channels are assigned to no speaker.", isMore ? "more" : "fewer");
        }
    }
}
