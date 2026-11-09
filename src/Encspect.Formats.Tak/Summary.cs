using Encspect.Core;

namespace Encspect.Formats.Tak;

// The stream node, the checks over the whole file, the statistics, the verification and the stream info.
internal static class Summary
{
    public const string CodecName = "TAK";

    // The codec ID a decoder would register under; none exists in 0.2, so the host reports that no decoder handles the coding.
    public const string CodecId = "tak";

    public static void Finish(State s, long start, long end)
    {
        Records r = s.Records;
        List<Node> body = s.Body;
        body.Add(Nodes.StreamId(r));
        foreach (ObjectRecord o in r.Objects)
        {
            body.Add(Nodes.Object(r, o));
        }

        if (r.AudioStart >= 0 && end > r.AudioStart)
        {
            body.Add(Nodes.AudioData(r.AudioStart, end - r.AudioStart));
        }
        else if (r.LeftoverOffset >= 0 && r.DataEnd > r.LeftoverOffset)
        {
            long length = r.DataEnd - r.LeftoverOffset;
            body.Add(new Node("Unread Data", r.LeftoverOffset, length, NodeValue.Integer(length, "bytes", "byte")) { Description = Explain.Unread, Topic = Topics.Metadata });
        }

        // The leading nodes grow after this (the statistics, the claims), so the children are put together when they are first asked for; the lists hold no context.
        List<Node> leading = s.Leading;
        Node stream = s.Stream = new Node(CodecName, start, Math.Max(0, end - start), null, () => [.. leading, .. body])
        {
            Kind = NodeKind.Stream,
            Description = Explain.Stream,
            Topic = Topics.Metadata,
        };
        s.Context.Root.Add(stream);
        Checks(s, stream, end);
        Statistics(s, stream, start, end);
        s.Findings.Flush(s.Context);
    }

    private static void Checks(State s, Node stream, long end)
    {
        Records r = s.Records;
        Func<Node> Of(ObjectRecord o) => () => s.Body.First(n => n.Offset == o.Offset && n.Name == Nodes.NameOf(o));

        if (r.Start > 0)
        {
            long before = r.Start;
            s.Findings.Add(Severity.Info, "tak.leading-tag", stream, _ =>
                $"{Counts.Of(before, "byte", "bytes")} of tags {Counts.Agree(before, "precedes", "precede")} the stream ID. TAK's readme says its decoder throws an error on tags prepended to the file.");
        }

        if (!r.IsStreamInfoFirst)
        {
            if (r.StreamInfoObject is null)
            {
                s.Findings.Add(Severity.Warning, "tak.stream-info-missing", r.Objects.Count > 0 ? Of(r.Objects[0]) : () => stream, _ =>
                    "The metadata holds no stream info object, so the format of the audio is not read.");
            }
            else
            {
                ObjectRecord first = r.Objects[0];
                string name = Layout.ObjectName(first.Id).ToLowerInvariant();
                s.Findings.Add(Severity.Warning, "tak.stream-info-not-first", Of(first), _ =>
                    $"The first metadata object is {(first.IsKnown ? "the " + name + " object" : "an unknown object")}, not the stream info, which the SDK's decoder requires first and otherwise signals damaged metadata.");
            }
        }

        foreach (ObjectRecord o in r.Objects)
        {
            if (!o.IsKnown)
            {
                int id = o.Id;
                long size = o.Size;
                s.Findings.Add(Severity.Info, "tak.object-unknown", Of(o), count =>
                    $"The metadata object with ID {id} at 0x{o.Offset:X} is not one takStream.pas defines, so it is skipped by its size of {Counts.Of(size, "byte", "bytes")}{(count > 1 ? $"; {Counts.Of(count - 1, "more object", "more objects")} {Counts.Agree(count - 1, "is", "are")} unknown" : string.Empty)}.");
            }

            if (o.IsReserved)
            {
                s.Findings.Add(Severity.Info, "tak.object-reserved-bit", Of(o), count =>
                    $"The reserved bit of the object header at 0x{o.Offset:X}, which takStream.pas says is 0, is set{(count > 1 ? $"; {Counts.Of(count - 1, "more object", "more objects")} {Counts.Agree(count - 1, "has", "have")} it set" : string.Empty)}.");
            }

            if (!o.IsSizeValid && !o.IsCut)
            {
                long size = o.Size;
                string rule = o.Id switch
                {
                    Layout.End => "no data",
                    Layout.StreamInfo => $"{Layout.StreamInfoSizeMin} to {Layout.StreamInfoSizeMax} bytes",
                    Layout.WaveData => $"{Layout.WaveDataFixed} bytes and the header and footer sizes it states ({o.HeaderSize} and {o.FooterSize})",
                    Layout.EncoderInfo => $"{Layout.EncoderInfoSize} bytes",
                    Layout.Md5 => $"{Layout.Md5Size} bytes",
                    _ => $"{Layout.LastFrameInfoSize} bytes",
                };
                string name = Layout.ObjectName(o.Id);
                s.Findings.Add(Severity.Warning, "tak.object-size-invalid", Of(o), _ =>
                    $"The {name.ToLowerInvariant()} object at 0x{o.Offset:X} states {Counts.Of(size, "byte", "bytes")}, which breaks takStream.pas: it needs {rule}; its fields are not read.", o.Id.ToString());
            }
        }

        ObjectRecord[] failed = [.. r.Objects.Where(o => o.IsCompared && !o.IsCrcValid)];
        if (failed.Length > 0)
        {
            ObjectRecord first = failed[0];
            uint stored = first.StoredCrc;
            uint computed = first.ComputedCrc;
            string name = Layout.ObjectName(first.Id).ToLowerInvariant();
            s.Findings.Add(Severity.Warning, "tak.object-crc-mismatch", () => Nodes.CrcField(r, first), count =>
                $"The CRC-24 of the {name} object at 0x{first.Offset:X} is {stored:x6}, but its data gives {computed:x6}{(count > 1 ? $"; {Counts.Of(count - 1, "more object", "more objects")} {Counts.Agree(count - 1, "differs", "differ")}" : string.Empty)}.", failed.Length);
        }

        if (r.IsMetadataCut)
        {
            ObjectRecord? cut = r.Objects.FirstOrDefault(o => o.IsCut);
            long length = r.DataEnd - r.Start;
            if (cut is not null)
            {
                long stated = cut.Size;
                long held = cut.Held;
                string name = Layout.ObjectName(cut.Id).ToLowerInvariant();
                s.Findings.Add(Severity.Warning, "tak.metadata-cut", Of(cut), _ =>
                    $"The data ends inside the {name} object at 0x{cut.Offset:X}, which states {Counts.Of(stated, "byte", "bytes")}; {held} of them {Counts.Agree(held, "is", "are")} there. The stream has no frame.");
            }
            else
            {
                s.Findings.Add(Severity.Warning, "tak.metadata-cut", stream, _ =>
                    $"The data ends after {Counts.Of(length, "byte", "bytes")}, in the metadata: no End object and no frame follow the last object.");
            }
        }

        if (r.IsLimitReached)
        {
            s.Findings.Add(Severity.Warning, "tak.objects-limit", stream, _ =>
                $"The metadata holds more than {Records.MaxObjects} objects; the walk stops there and the rest is not read.");
        }

        if (r.IsEndMissing)
        {
            s.Findings.Add(Severity.Warning, "tak.end-missing", stream, _ =>
                $"The metadata has no End object: the first frame, with a header whose CRC-24 holds, begins at 0x{r.AudioStart:X}.");
        }

        if (r.DeclaredEnd is { } declared)
        {
            if (declared > r.DataEnd)
            {
                long missing = declared - r.DataEnd;
                s.Findings.Add(Severity.Warning, "tak.audio-cut", stream, _ =>
                    $"The last frame info states that the last frame ends at 0x{declared:X}, but the data ends {Counts.Of(missing, "byte", "bytes")} before: the file is cut in the audio.");
            }
            else if (declared < r.DataEnd)
            {
                long extra = r.DataEnd - declared;
                Node trailing = s.Context.Root.Add(new Node("Trailing Data", declared, extra, NodeValue.Integer(extra, "bytes", "byte")) { Description = Explain.Trailing, Topic = Topics.Metadata });
                s.Findings.Add(Severity.Warning, "tak.data-after-frames", () => trailing, _ =>
                    $"{Counts.Of(extra, "byte", "bytes")} at 0x{declared:X} {Counts.Agree(extra, "follows", "follow")} the last frame the last frame info states and {Counts.Agree(extra, "is", "are")} no tag; {Counts.Agree(extra, "it is", "they are")} not read.");
            }
        }

        if (r.Stream is not { } info)
        {
            return;
        }

        Node StreamField(string field) => () => Nodes.Object(r, r.StreamInfoObject!).Children.FirstOrDefault(c => c.Name == field) ?? Nodes.Object(r, r.StreamInfoObject!);
        if (Layout.CodecName(info.Codec) is null)
        {
            int value = info.Codec;
            s.Findings.Add(Severity.Info, "tak.value-undefined", StreamField("Codec"), _ => $"The codec {value} is not one Takc's readme lists (0 to 4).", "codec");
        }

        if (info.DataType != 0)
        {
            int value = info.DataType;
            s.Findings.Add(Severity.Info, "tak.value-undefined", StreamField("Data Type"), _ => $"The data type {value} is not defined: takStream.pas defines 0, PCM.", "data-type");
        }

        if (info.BitsPerSampleCode > 16)
        {
            int value = info.BitsPerSampleCode;
            s.Findings.Add(Severity.Info, "tak.value-undefined", StreamField("Bits Per Sample"), _ => $"The bits per sample code {value} is outside 0 to 16 (8 to 24 bits).", "sample-bits");
        }

        if (info.SampleRate > Layout.SampleRateMax)
        {
            int value = info.SampleRate;
            s.Findings.Add(Severity.Info, "tak.value-undefined", StreamField("Sample Rate"), _ => $"The sample rate {value} Hz is outside 6000 to {Layout.SampleRateMax} Hz.", "sample-rate");
        }

        if (info.HasExtension && (info.ValidBitsPerSample > info.BitsPerSample))
        {
            int value = info.ValidBitsPerSample;
            int bits = info.BitsPerSample;
            s.Findings.Add(Severity.Info, "tak.value-undefined", StreamField("Valid Bits Per Sample"), _ => $"The valid bits per sample, {value}, exceed the {bits} bits of a sample.", "valid-bits");
        }

        if (r.HasEncoderInfo && r.Evaluation == 3)
        {
            s.Findings.Add(Severity.Info, "tak.value-undefined", () => Nodes.Object(r, r.EncoderInfoObject!).Children.First(c => c.Name == "Evaluation"), _ =>
                "The evaluation level 3 is not defined (0 Standard, 1 Extra, 2 Max).", "evaluation");
        }

        for (int i = 0; i < info.Speakers.Length; i++)
        {
            if (Layout.SpeakerName(info.Speakers[i]) is null)
            {
                int channel = i + 1;
                int value = info.Speakers[i];
                s.Findings.Add(Severity.Info, "tak.value-undefined", () => Nodes.Object(r, r.StreamInfoObject!).Children.First(c => c.Name == "Speaker Assignment").Children[channel - 1], _ =>
                    $"The speaker code {value} of channel {channel} is not defined (0 to 18).", "speaker");
            }
        }

        int type = info.FrameSizeType;
        if (type >= Layout.FrameSizeTypes)
        {
            s.Findings.Add(Severity.Warning, "tak.frame-size-type-reserved", StreamField("Frame Size Type"), _ =>
                $"The frame size type {type} is reserved (takStream.pas defines 0 to 9; 10 to 14 are reserved and 15, \"read size following\", is not implemented), so the frame length is not worked out.");
        }
        else if (!Layout.IsFrameSizeValid(type, info.SampleRate))
        {
            int rate = info.SampleRate;
            int samples = Layout.FrameSamples(type, rate);
            string name = Layout.FrameSizeTypeName(type);
            s.Findings.Add(Severity.Warning, "tak.frame-size-invalid", StreamField("Frame Size Type"), _ =>
                $"The frame size type {name} comes to {Counts.Of(samples, "sample", "samples")} at {rate} Hz, more than a frame may hold (a time type at most {Layout.MaxFrameSamples} samples, a size type at most 250 ms), so the SDK's decoder rejects the stream info.");
        }

        if (r.WaveDataObject is { } wave)
        {
            if (wave.WavHeader is { } header)
            {
                HeaderData.Findings(s, header, () => Nodes.Object(r, wave).Children.First(c => c.Name == header.Name));
            }
        }
    }

    private static IReadOnlyList<Speaker>? LayoutOf(StreamInfoFields info, uint? wavMask)
    {
        if (info.HasSpeakerAssignment && Layout.SpeakerMask(info.Speakers) is { } assigned and not 0 && System.Numerics.BitOperations.PopCount(assigned) == info.Channels)
        {
            return ChannelLayouts.FromWaveMask(assigned, info.Channels);
        }

        if (wavMask is { } mask and not 0)
        {
            return ChannelLayouts.FromWaveMask(mask, info.Channels);
        }

        return info.Channels switch { 1 => ChannelLayouts.Mono, 2 => ChannelLayouts.Stereo, _ => null };
    }

    private static void Statistics(State s, Node stream, long start, long end)
    {
        Records r = s.Records;
        Node Cover(string name, NodeValue? value, string description, string? topic = null) => new(name, start, Math.Max(0, end - start), value) { Description = description, Topic = topic };

        Node? encoder = null;
        if (r.Stream is not null)
        {
            encoder = EncoderNode(r, Cover);
            s.Leading.Add(encoder);
        }

        Node? writer = WriterDetection.Detect(s.Context.EncoderClaims, (name, value, description) => Cover(name, value, description, ClaimRoles.TopicOf(name)),
            (claim, text, name) => s.Findings.Add(Severity.Info, "tak.writer-claim-differs", claim.Source, _ => WriterDetection.Disagreement(claim, text, name)));
        if (writer is not null)
        {
            s.Leading.Add(writer);
        }

        Node? processor = ProcessorDetection.Detect(s.Context.EncoderClaims, (name, value, description) => Cover(name, value, description, ClaimRoles.TopicOf(name)));
        if (processor is not null)
        {
            s.Leading.Add(processor);
        }

        ClaimRoles.CompleteVersion(encoder, s.Context.EncoderClaims);
        ObjectRecord[] compared = [.. r.Objects.Where(o => o.IsCompared)];
        long verified = compared.Count(o => o.IsCrcValid);
        long failed = compared.Length - verified;
        StreamInfoFields? info = r.Stream;
        var tallies = new List<ChecksumTally>
        {
            new() { Name = ChecksumNames.ObjectCrc24, Covers = ChecksumCoverage.Header, Stored = compared.Length, Checked = compared.Length, Failed = failed },
        };

        long? frameCount = info is { Samples: > 0 } && Layout.FrameSamples(info.FrameSizeType, info.SampleRate) is > 0 and int length ? (info.Samples + length - 1) / length : null;
        if (frameCount is { } frames)
        {
            // Every frame stores both; they are declared and not compared, since the frames are not walked.
            tallies.Add(new ChecksumTally { Name = ChecksumNames.FrameHeaderCrc24, Covers = ChecksumCoverage.Header, Stored = frames });
            tallies.Add(new ChecksumTally { Name = ChecksumNames.FrameDataCrc24, Covers = ChecksumCoverage.CodedData, CoversAllAudio = true, Stored = frames });
        }

        if (r.Md5 is not null)
        {
            tallies.Add(new ChecksumTally { Name = ChecksumNames.Md5, Covers = ChecksumCoverage.DecodedAudio, CoversAllAudio = true, Stored = 1 });
        }

        if (info is null)
        {
            StreamVerification none = s.Context.StartDecoding(CodecId, new DecodeSetup { Stream = stream, Channels = 1, BitsPerSample = 16 }).Finish(tallies);
            s.Context.AddStream(new StreamInfo { Nodes = [stream], Codec = CodecName, Verification = none });
            return;
        }

        IDecodeSession session = s.Context.StartDecoding(CodecId, new DecodeSetup { Stream = stream, Channels = info.Channels, BitsPerSample = info.BitsPerSample, Samples = info.Samples });
        StreamVerification verification = session.Finish(tallies);

        var statistics = new Node("Statistics", start, Math.Max(0, end - start)) { Kind = NodeKind.Statistics, Description = Explain.Statistics };
        long rate = info.SampleRate;
        double duration = rate > 0 ? info.Samples / (double)rate : 0;
        uint? mask = r.WaveDataObject?.WavHeader?.Format?.Mask;
        IReadOnlyList<Speaker>? layout = LayoutOf(info, mask);
        string preset = r.HasEncoderInfo ? Layout.PresetName(r.Preset, r.Evaluation) : $"-p{info.Profile}";
        statistics.Add(Cover("Codec", Layout.CodecName(info.Codec) ?? $"undefined ({info.Codec})", Explain.StatCodec));
        statistics.Add(Cover("Profile", NodeValue.Integer(info.Profile), Explain.StatProfile, Topics.Presets));
        statistics.Add(Cover("Preset", preset, Explain.StatPreset, Topics.Presets));
        int frameLength = Layout.FrameSamples(info.FrameSizeType, info.SampleRate);
        if (info.FrameSizeType < Layout.FrameSizeTypes)
        {
            statistics.Add(Cover("Frame Length", NodeValue.Integer(frameLength, "samples", "sample"), Explain.StatFrameLength));
        }

        statistics.Add(Cover("Samples", NodeValue.Integer(info.Samples, "samples", "sample"), Explain.StatSamples));
        statistics.Add(Cover("Duration", NodeValue.Decimal(duration, 3, "s"), Explain.StatDuration));
        statistics.Add(Cover("Sample Rate", NodeValue.Integer(rate, "Hz"), Explain.StatRate));
        statistics.Add(Cover("Channels", NodeValue.Integer(info.Channels, "channels", "channel"), Explain.StatChannels));
        if (layout is not null)
        {
            statistics.Add(Cover("Channel Layout", ChannelLayouts.Describe(layout), Explain.StatLayout, Topics.Channels));
        }

        statistics.Add(Cover("Bits Per Sample", NodeValue.Integer(info.BitsPerSample, "bits", "bit"), Explain.StatBits));
        long audioBytes = r.AudioStart >= 0 ? Math.Max(0, Math.Min(end, r.DataEnd) - r.AudioStart) : 0;
        double? bitrate = duration > 0 && audioBytes > 0 ? audioBytes * 8 / duration / 1000 : null;
        statistics.Add(Cover("Average Bitrate", bitrate is { } average ? NodeValue.Decimal(average, 1, "kbps") : "none", Explain.AverageBitrate));
        statistics.Add(Cover("Verified Header Checksums", NodeValue.Integer(verified, "headers", "header"), Explain.StatHeaderChecksums, Topics.Crc));
        if (failed > 0)
        {
            statistics.Add(Cover("Failed Header Checksums", NodeValue.Integer(failed, "headers", "header"), Explain.StatHeaderChecksumsFailed, Topics.Crc));
        }

        s.Leading.Insert(0, statistics);
        s.Context.AddStream(new StreamInfo
        {
            Nodes = [stream],
            Codec = CodecName,
            SampleRate = (int)Math.Min(rate, int.MaxValue),
            Channels = info.Channels,
            ChannelMode = info.Channels == 1 ? "Mono" : info.Channels == 2 ? "Stereo" : null,
            ChannelLayout = layout,
            BitsPerSample = info.BitsPerSample,
            Samples = info.Samples,
            Duration = duration,
            AverageBitrate = bitrate,
            Encoder = ClaimRoles.Resolved(encoder).Name,
            EncoderConfidence = ClaimRoles.Resolved(encoder).Confidence,
            Writer = ClaimRoles.Resolved(writer).Name,
            WriterConfidence = ClaimRoles.Resolved(writer).Confidence,
            Processor = ClaimRoles.Resolved(processor).Name,
            ProcessorConfidence = ClaimRoles.Resolved(processor).Confidence,
            Verification = verification,
        });
    }

    private static Node EncoderNode(Records r, Func<string, NodeValue?, string, string?, Node> cover)
    {
        if (r.HasEncoderInfo)
        {
            int v = r.EncoderVersion;
            string version = $"{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}";
            Node high = cover("Encoder", $"TAK {version}", Explain.Encoder, ClaimRoles.EncoderTopic);
            high.Add(cover("Confidence", "High", Explain.ConfidenceHigh, null));
            high.Add(cover("Evidence", $"The encoder info object states version {version}", Explain.Evidence, null));
            return high;
        }

        Node medium = cover("Encoder", "TAK 1.0", Explain.Encoder, ClaimRoles.EncoderTopic);
        medium.Add(cover("Confidence", "Medium", Explain.ConfidenceMedium, null));
        medium.Add(cover("Evidence", "The file has no encoder info object, which the SDK's decoder reads as version 1.0.0; the stream info is the only evidence", Explain.Evidence, null));
        return medium;
    }
}
