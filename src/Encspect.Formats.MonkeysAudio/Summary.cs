using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.MonkeysAudio;

// The checks over the whole stream, the statistics, the encoder, the verification and the stream info.
internal static class Summary
{
    private static readonly Speaker[][] Layouts = [[Speaker.FrontCenter], [Speaker.FrontLeft, Speaker.FrontRight]];

    // The speakers of one or two channels; the format states no positions for more.
    public static IReadOnlyList<Speaker>? Layout(int channels) => channels is 1 or 2 ? Layouts[channels - 1] : null;

    public static void Finish(State s)
    {
        ApeHeader h = s.Header;
        Node stream = s.Stream;
        Node? encoder = Encoder(s, h);
        if (encoder is not null)
        {
            s.AddLeading(encoder);
        }

        Node? writer = Writer(s, stream.Offset, stream.End);
        if (writer is not null)
        {
            s.AddLeading(writer);
        }

        ClaimRoles.CompleteVersion(encoder, s.Context.EncoderClaims);
        StreamVerification verification = Verify(s, h);
        StreamInfo? info = Statistics(s, h, encoder, writer, verification);
        if (info is not null)
        {
            s.Context.AddStream(info);
        }

        s.Findings.Flush(s.Context);
    }

    private static StreamVerification Verify(State s, ApeHeader h)
    {
        bool framesComplete = h.TotalFrames > 0 && s.Frames == h.TotalFrames && s.MisplacedFrames == 0 && s.CutFrames == 0 && s.ShortFrames == 0 && s.UnreadableFrames == 0 && !s.HasGap;
        bool md5Computed = s.ComputedMd5 is not null;
        bool md5Matches = md5Computed && h.Md5.Span.SequenceEqual(s.ComputedMd5);
        IDecodeSession session = s.Session ?? s.Context.StartDecoding(MonkeysAudioCodec.Id, new DecodeSetup { Stream = s.Stream, Channels = 1, BitsPerSample = 16 });
        List<ChecksumTally> tallies = [];
        if (h.HasDescriptor)
        {
            tallies.Add(new ChecksumTally
            {
                Name = ChecksumNames.DescriptorMd5,
                Covers = ChecksumCoverage.CodedData,
                CoversAllAudio = md5Computed && framesComplete,
                Stored = 1,
                Checked = md5Computed ? 1 : 0,
                Failed = md5Computed && !md5Matches ? 1 : 0,
            });
        }

        tallies.Add(new ChecksumTally
        {
            Name = h.HasStoredCrc ? ChecksumNames.FrameCrc : ChecksumNames.FrameChecksum,
            Covers = ChecksumCoverage.DecodedAudio,
            CoversAllAudio = framesComplete && (!h.HasStoredCrc || s.StoredCrcs == s.Frames),
            Stored = h.HasStoredCrc ? s.StoredCrcs : s.Frames,
        });
        return session.Finish(tallies);
    }

    private static Node? Encoder(State s, ApeHeader h)
    {
        Node Cover(string name, NodeValue? value, string description, string? topic = null) => new(name, s.Junk, s.Header.HasDescriptor ? ApeHeader.DescriptorLength : h.OldHeaderBytes, value) { Description = description, Topic = topic };
        if (!s.HasHeader)
        {
            return null;
        }

        bool hasProgram = h.ProgramVersion != 0;
        string name = hasProgram ? $"Monkey's Audio {ApeHeader.VersionText(h.ProgramVersion)}" : $"Monkey's Audio (file version {ApeHeader.VersionText(h.FileVersion)})";
        Node encoder = Cover("Encoder", name, Explain.Encoder, ClaimRoles.EncoderTopic);
        encoder.Add(Cover("Confidence", hasProgram ? "High" : "Medium", hasProgram ? Explain.ConfidenceHigh : Explain.ConfidenceMedium));
        encoder.Add(Cover("Evidence", hasProgram
            ? $"The descriptor's program version field states {ApeHeader.VersionText(h.ProgramVersion)}, the version of the program that wrote the file"
            : $"The file is in the format of Monkey's Audio {ApeHeader.VersionText(h.FileVersion)}; the file does not state which program wrote it", Explain.Evidence));
        return encoder;
    }

    private static Node? Writer(State s, long start, long end) =>
        WriterDetection.Detect(s.Context.EncoderClaims, (name, value, description) => new Node(name, start, end - start, value) { Description = description, Topic = ClaimRoles.TopicOf(name) },
            (claim, text, name) => s.Findings.Add(Severity.Info, "monkeys-audio.writer-claim-differs", claim.Source, _ => WriterDetection.Disagreement(claim, text, name)));

    private static StreamInfo? Statistics(State s, ApeHeader h, Node? encoder, Node? writer, StreamVerification verification)
    {
        Node stream = s.Stream;
        Node Cover(string name, NodeValue? value, string description, string? topic = null) => new(name, stream.Offset, stream.Length, value) { Description = description, Topic = topic };
        var statistics = new Node("Statistics", stream.Offset, stream.Length) { Kind = NodeKind.Statistics, Description = Explain.Statistics };
        long samples = h.TotalFrames == 0 ? 0 : h.TotalBlocks;
        int rate = (int)Math.Min(h.SampleRate, int.MaxValue);
        double duration = rate > 0 ? samples / (double)rate : 0;
        IReadOnlyList<Speaker>? layout = Layout(h.Channels);
        statistics.Add(Cover("Frames", NodeValue.Integer(s.Frames, "frames", "frame"), Explain.StatFrames, Topics.Frames));
        statistics.Add(Cover("Samples", NodeValue.Integer(samples, "samples", "sample"), Explain.StatSamples));
        statistics.Add(Cover("Duration", NodeValue.Decimal(duration, 3, "s"), Explain.StatDuration));
        if (rate > 0)
        {
            statistics.Add(Cover("Sample Rate", NodeValue.Integer(rate, "Hz"), Explain.StatRate));
        }

        if (h.Channels > 0)
        {
            statistics.Add(Cover("Channels", NodeValue.Integer(h.Channels, "channels", "channel"), Explain.StatChannels, Topics.MidSideStereo));
            if (layout is not null)
            {
                statistics.Add(Cover("Channel Layout", ChannelLayouts.Describe(layout), Explain.StatLayout, Topics.Channels));
            }
        }

        if (h.BitsPerSample > 0)
        {
            statistics.Add(Cover("Bits Per Sample", NodeValue.Integer(h.BitsPerSample, "bits"), Explain.StatBits));
        }

        statistics.Add(Cover("Compression Level", $"{ApeHeader.LevelName(h.CompressionLevel)} ({h.CompressionLevel})", Explain.StatLevel, Topics.Prediction));
        if (s.SilentFrames > 0)
        {
            statistics.Add(Cover("Silent Frames", NodeValue.Integer(s.SilentFrames, "frames", "frame"), Explain.StatSilentFrames, Topics.Frames));
        }

        if (s.PseudoStereoFrames > 0)
        {
            statistics.Add(Cover("Pseudo-Stereo Frames", NodeValue.Integer(s.PseudoStereoFrames, "frames", "frame"), Explain.StatPseudoStereoFrames, Topics.Frames));
        }

        statistics.Add(h.HasStoredCrc
            ? Cover("Stored Frame CRCs", NodeValue.Integer(s.StoredCrcs, "frames", "frame"), Explain.StatStoredCrcs, Topics.Crc)
            : Cover("Stored Frame Checksums", NodeValue.Integer(s.Frames, "frames", "frame"), Explain.StatStoredChecksums, Topics.Crc));
        if (h.HasDescriptor)
        {
            string md5 = s.ComputedMd5 is null ? "not compared" : h.Md5.Span.SequenceEqual(s.ComputedMd5) ? "matches" : "differs";
            statistics.Add(Cover("Descriptor MD5", md5, Explain.StatMd5, Topics.DescriptorMd5));
        }

        StreamFigures figures = s.Figures ?? new StreamFigures(rate);
        Node Covered(string name, NodeValue? value, string description) => Cover(name, value, description);
        figures.AddTo(statistics, Covered, "Frame", "frames", true, out _);
        Node? anchor = statistics.Children.FirstOrDefault(c => c.Name == "Average Bitrate");
        s.AddLeading(statistics);

        string? channelMode = h.Channels switch
        {
            1 => "Mono",
            2 => "Mid/side stereo",
            > 2 => "Multichannel",
            _ => null,
        };
        return new StreamInfo
        {
            Nodes = [stream],
            Codec = "Monkey's Audio",
            SampleRate = rate,
            Channels = h.Channels,
            ChannelMode = channelMode,
            ChannelLayout = layout,
            BitsPerSample = h.BitsPerSample > 0 ? h.BitsPerSample : null,
            Samples = samples,
            Duration = duration,
            AverageBitrate = figures.AverageBitrate,
            IsVbr = figures is { Units: > 0 } && figures.MaximumBitrate > figures.MinimumBitrate,
            Encoder = encoder?.Value?.ToString(),
            EncoderConfidence = ClaimRoles.Resolved(encoder).Confidence ?? (h.ProgramVersion != 0 ? Confidence.High : Confidence.Medium),
            Writer = ClaimRoles.Resolved(writer).Name,
            WriterConfidence = ClaimRoles.Resolved(writer).Confidence,
            Verification = verification,
            Series = figures.Series(anchor, s.Context.CancellationToken),
        };
    }

    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
