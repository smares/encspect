using Encspect.Core;

namespace Encspect.Formats.Asf;

public sealed partial class AsfParser
{
    // The checks that need the whole header and the packets: what the specification requires of the
    // file as a whole (sections 2.3, 3.1, 3.2, 8.2).
    private static void Check(State state)
    {
        FindingCollector findings = state.Findings;
        state.FlushUnknownObjects();
        Node file = state.File;
        Node anchor = state.HeaderNode ?? file;

        foreach (ExtensionRecord record in state.ExtensionRecords)
        {
            if (state.Stream(record.Number) is { } stream)
            {
                stream.Extension = record.Node;
                stream.DataBitrate = record.DataBitrate;
            }
            else
            {
                findings.AddNow(Severity.Warning, "asf.extended-stream-unknown",
                    $"The Extended Stream Properties Object at 0x{record.Node.Offset:X} extends stream {record.Number}, which no Stream Properties Object describes.", record.Node);
            }
        }

        foreach ((int number, uint bitrate, Node node) in state.PendingBitrates)
        {
            if (state.Stream(number) is { } stream)
            {
                stream.AverageBitrate = bitrate;
            }
            else
            {
                findings.AddNow(Severity.Warning, "asf.bitrate-record-unknown", $"The Stream Bitrate Properties Object has a record for stream {number}, which no Stream Properties Object describes.", node);
            }
        }

        if (state.HeaderNode is null)
        {
            return;
        }

        if (state.FilePropertiesNode is null)
        {
            findings.AddNow(Severity.Warning, "asf.missing-file-properties", "The Header Object holds no File Properties Object, which the specification requires (section 2.3).", state.HeaderNode);
        }

        if (state.HeaderExtensionCount == 0)
        {
            findings.AddNow(Severity.Warning, "asf.missing-header-extension", "The Header Object holds no Header Extension Object, which the specification requires (section 2.3).", state.HeaderNode);
        }
        else if (state.HeaderExtensionCount > 1)
        {
            findings.AddNow(Severity.Warning, "asf.duplicate-header-extension", "The Header Object holds more than one Header Extension Object; the specification allows one (section 3.4).", state.HeaderNode);
        }

        if (state.StreamPropertiesCount == 0)
        {
            findings.AddNow(Severity.Warning, "asf.no-streams", "The Header Object holds no Stream Properties Object, which the specification requires (section 2.3).", state.HeaderNode);
        }

        if (state.DataNode is null)
        {
            findings.AddNow(Severity.Warning, "asf.missing-data", "The file holds no Data Object, which the specification requires (section 2.2).", file);
        }

        if (state.FilePropertiesNode is not null)
        {
            if (state.IsBroadcast)
            {
                findings.AddNow(Severity.Info, "asf.broadcast", "The File Properties Object sets the broadcast flag: the file is being created, and its file size, packet count and durations are invalid (section 3.2).", state.FilePropertiesNode);
            }
            else if (state.FileSize != (ulong)state.Reader.Length)
            {
                findings.AddNow(Severity.Warning, "asf.file-size",
                    $"The File Properties Object gives the file size as {state.FileSize} bytes, but the file holds {state.Reader.Length}.", state.FileSizeNode!);
            }

            if (state.MinPacketSize != state.MaxPacketSize)
            {
                findings.AddNow(Severity.Warning, "asf.packet-size",
                    $"The File Properties Object gives different minimum ({state.MinPacketSize}) and maximum ({state.MaxPacketSize}) packet sizes; the specification makes them equal, and fixed-size packets are required (sections 3.2 and 8.2.13).", state.PacketSizeNode!);
            }
            else if (state.MaxPacketSize > 65535)
            {
                findings.AddNow(Severity.Warning, "asf.packet-size", $"The packet size is {state.MaxPacketSize} bytes; the specification asks for less than 64 KB (section 8.2.14).", state.PacketSizeNode!);
            }
        }

        if (state.IndexParametersNode is not null && state.IndexObjects == 0)
        {
            findings.AddNow(Severity.Warning, "asf.index-missing", "The header holds an Index Parameters Object, but the file holds no Index Object.", state.IndexParametersNode);
        }

        if (state.Figures is { } figures)
        {
            foreach ((int number, StreamFigures stream) in figures.Streams.OrderBy(s => s.Key))
            {
                if (state.Stream(number) is null)
                {
                    findings.AddNow(Severity.Info, "asf.payload-unknown-stream",
                        $"{Plural(stream.Payloads, "payload", "payloads")} belong to stream {number}, which no Stream Properties Object describes; readers ignore them (section 8.2.7).", state.DataNode!, 1);
                }
            }

            foreach (StreamEntry stream in state.Streams)
            {
                if (!figures.Streams.ContainsKey(stream.Number) && figures.PacketCount > 0)
                {
                    findings.AddNow(Severity.Info, "asf.stream-without-payloads", $"Stream {stream.Number} has a Stream Properties Object, but no payload belongs to it.", stream.Properties!);
                }
            }
        }
    }

    // Hands each audio stream's media objects to the codec parser of its format tag (the routes of
    // the host): "wma" for Windows Media Audio versions 1 and 2, "wma-pro", "wma-lossless" and
    // "wma-voice". A codec without a parser, an encrypted stream and spread audio, whose payloads are
    // scrambled, are findings.
    private static void Route(State state)
    {
        CheckEncryptedStreams(state);
        if (state.DataNode is null || state.Figures is null)
        {
            // Without a Data Object there is nothing to read, but a protected stream is still reported as such.
            foreach (StreamEntry stream in state.Streams)
            {
                AddProtectedStream(state, stream);
            }

            return;
        }

        AddWriterEvidence(state);
        foreach (StreamEntry stream in state.Streams)
        {
            Node anchor = stream.Properties ?? state.DataNode;
            if (!stream.IsAudio)
            {
                state.Findings.AddNow(Severity.Info, "asf.stream-not-analyzed", $"Stream {stream.Number} is a {stream.TypeName.ToLowerInvariant()} stream, which is not analyzed; the file's packets are reported as they are.", anchor);
                AddProtectedStream(state, stream);
                continue;
            }

            if (stream.Format is not { } format)
            {
                state.Findings.AddNow(Severity.Info, "asf.stream-not-analyzed", $"Stream {stream.Number} is an audio stream without a readable WAVEFORMATEX, so its coding is not analyzed.", anchor);
                AddProtectedStream(state, stream);
                continue;
            }

            string? codec = state.Routes.GetValueOrDefault(format.EffectiveTag);
            if (codec is null)
            {
                state.Findings.AddNow(Severity.Info, "asf.codec-not-analyzed",
                    $"Stream {stream.Number} is coded as {FormatTagName(format.EffectiveTag)} (format tag 0x{format.EffectiveTag:X4}), which has no parser here; its media objects are not analyzed.", anchor);
                AddProtectedStream(state, stream);
                continue;
            }

            if (ProtectionOf(state, stream) is { } protection)
            {
                RouteEncrypted(state, stream, codec, format, protection, anchor);
                continue;
            }

            if (stream.Spread is { Scrambles: true })
            {
                state.Findings.AddNow(Severity.Info, "asf.audio-spread",
                    $"Stream {stream.Number} spreads its audio over {stream.Spread.Span} packets (section 9.1.1), which scrambles the media objects; descrambling is not done, so the stream is not analyzed.", anchor);
                continue;
            }

            CodecConfiguration? configuration = ConfigurationOf(state, stream, format);
            if (configuration is null)
            {
                state.Findings.AddNow(Severity.Info, "asf.stream-not-analyzed", $"Stream {stream.Number}'s codec-specific data cannot be read.", anchor);
                continue;
            }

            bool routed = state.Context.ParsePackets(codec, state.DataNode, MediaObjects(state, stream.Number), configuration);
            if (!routed)
            {
                state.Findings.AddNow(Severity.Info, "asf.codec-not-analyzed",
                    $"Stream {stream.Number} is coded as {FormatTagName(format.EffectiveTag)}, but the analyzer has no parser for the {codec} coding.", anchor);
            }
        }
    }

    // How the header protects a stream: its Encrypted Content Flag (section 3.3), the Content Encryption Object and the Extended Content Encryption
    // Object, which cover the whole file, and the records of the Advanced Content Encryption Object that name the stream or 0. Whatever protects it,
    // its media objects are taken as encrypted: a stream the flag does not mark may still have payloads that the file's object encrypts. The Digital
    // Signature Object protects nothing.
    private static StreamProtection? ProtectionOf(State state, StreamEntry stream)
    {
        List<ProtectionObject> objects = [.. state.Protections.Where(p => p.Covers(stream.Number))];
        if (!stream.IsEncrypted && objects.Count == 0)
        {
            return null;
        }

        return new StreamProtection
        {
            Scheme = objects.Count > 0 ? objects[0].Scheme : ProtectionSchemes.StreamFlag,
            IsEncrypted = true,
            Systems = [.. objects.SelectMany(p => p.Systems).Distinct()],
        };
    }

    // An encrypted stream is described by its WAVEFORMATEX and codec-specific data, with no media object handed to the codec parser: ciphertext
    // is no valid coding.
    private static void RouteEncrypted(State state, StreamEntry stream, string codec, WaveFormat format, StreamProtection protection, Node anchor)
    {
        state.Findings.AddNow(Severity.Info, "asf.codec-not-analyzed",
            $"Stream {stream.Number} is encrypted, so its media objects cannot be analyzed; the stream is described by its header.", anchor);
        CodecConfiguration? configuration = ConfigurationOf(state, stream, format);
        int before = state.Context.Streams.Count;
        if (configuration is not null)
        {
            state.Context.ParsePackets(codec, state.DataNode!, [], configuration with { AreSamplesEncrypted = true });
        }

        if (state.Context.Streams.Count == before)
        {
            AddProtectedStream(state, stream);
            return;
        }

        for (int i = before; i < state.Context.Streams.Count; i++)
        {
            state.Context.UpdateStream(state.Context.Streams[i], state.Context.Streams[i] with { Protection = protection });
        }
    }

    // The stream of a protected stream that no codec parser describes (a coding without a parser, video, a stream without a readable
    // WAVEFORMATEX): the header's own account of it, so that its protection is reported all the same.
    private static void AddProtectedStream(State state, StreamEntry stream)
    {
        if (ProtectionOf(state, stream) is not { } protection)
        {
            return;
        }

        Node parent = state.DataNode ?? stream.Properties!;
        string name = stream.Format is { } format && stream.IsAudio ? FormatTagName(format.EffectiveTag) : stream.TypeName;
        Node node = parent.Add(new Node($"Stream {stream.Number}", parent.Offset, parent.Length, NodeValue.Text("frames not read: encrypted"))
        {
            Kind = NodeKind.Stream,
            Description = Explain.EncryptedStream,
            Topic = Topics.AsfContentProtection,
        });
        state.Context.AddStream(new StreamInfo
        {
            Nodes = [node],
            Codec = name,
            SampleRate = stream.Format?.SamplesPerSecond ?? 0,
            Channels = stream.Format?.Channels ?? 0,
            Protection = protection,
        });
    }

    // Records of the Advanced Content Encryption Object that name a stream the header does not describe.
    private static void CheckEncryptedStreams(State state)
    {
        foreach (ProtectionObject protection in state.Protections)
        {
            foreach ((int number, Node node) in protection.Streams)
            {
                if (number != 0 && state.Stream(number) is null)
                {
                    state.Findings.AddNow(Severity.Info, "asf.encrypted-object-unknown-stream",
                        $"An Encrypted Object Record names stream {number}, which no Stream Properties Object describes; the record protects nothing in this file.", node);
                }
            }
        }
    }

    // The writer of the file, from the way it lays out the header. The Windows Media Format SDK's writer and
    // Media Foundation's ASF sink each write the header in a habit of their own, which the files the project
    // encoded with them show (the SDK's and Media Foundation's files of tests/fixtures/asf, wmvcore 12.0.26100).
    // The objects are the specification's (Codec List, Compatibility, Language List, Metadata, Padding), but
    // that a writer chooses them is a habit, not a rule, so the evidence is medium. It names the writer, which
    // drives whatever codec it was asked for; a name the file states (WMFSDKVersion) is a low claim by itself.
    private static void AddWriterEvidence(State state)
    {
        if (state.HeaderNode is not { } header || state.HeaderExtensionNode is not { } extension)
        {
            return;
        }

        bool codecList = state.HeaderGuids.Contains(AsfGuids.CodecList);
        bool languageList = state.HeaderGuids.Contains(AsfGuids.LanguageList);
        bool extendedStream = state.HeaderGuids.Contains(AsfGuids.ExtendedStreamProperties);
        bool metadata = state.HeaderGuids.Contains(AsfGuids.Metadata);
        bool padding = state.HeaderGuids.Contains(AsfGuids.Padding);
        bool bitrates = state.HeaderGuids.Contains(AsfGuids.StreamBitrateProperties);
        bool writerObject = state.HeaderGuids.Contains(AsfGuids.Compatibility);

        if (state.SdkVersion is { } version && writerObject && codecList)
        {
            state.Context.AddEncoderClaim(new EncoderClaim($"Windows Media Format SDK {version}", state.SdkVersionNode ?? extension,
                $"ASF header: WMFSDKVersion {version}, a Codec List Object and a Compatibility Object in the Header Extension Object, the layout of the Windows Media Format SDK's writer{WriterSettings(state)}")
            {
                Confidence = Confidence.Medium,
                IsLayout = true,
            });
        }
        else if (state.MediaFoundationVersion is { } foundation && state.SdkVersion is null && !codecList && !writerObject && languageList && extendedStream && metadata && padding && bitrates)
        {
            state.Context.AddEncoderClaim(new EncoderClaim($"Media Foundation {foundation}", state.MediaFoundationVersionNode ?? extension,
                $"ASF header: MediaFoundationVersion {foundation}, a Language List, Extended Stream Properties and Metadata Object in the Header Extension Object, a Padding Object, and no Codec List Object or WMFSDKVersion, the layout of Media Foundation's ASF sink{WriterSettings(state)}")
            {
                Confidence = Confidence.Medium,
                IsLayout = true,
            });
        }

        _ = header;
    }

    // What the writer says about its settings, in the Codec List Object (its text for people) and the IsVBR
    // attribute. It is the writer's statement, not something read from the audio.
    private static string WriterSettings(State state)
    {
        var parts = new List<string>();
        foreach ((string name, string description, int type) in state.Codecs)
        {
            if (type == 2 && (name.Length > 0 || description.Length > 0))
            {
                parts.Add($"codec list \"{name}\", \"{description}\"");
                break;
            }
        }

        if (state.IsVbr is { } isVbr)
        {
            parts.Add($"IsVBR {(isVbr ? "true" : "false")}");
        }

        return parts.Count == 0 ? string.Empty : $". The writer states {string.Join("; ", parts)}";
    }

    // The play duration less the preroll (section 3.2), where the file states them.
    private static double? DurationOf(State state)
    {
        if (state.FilePropertiesNode is null || state.IsBroadcast || state.PlayDuration == 0)
        {
            return null;
        }

        double seconds = (state.PlayDuration / 10_000_000.0) - (state.Preroll / 1000.0);
        return seconds > 0 ? seconds : null;
    }

    private static CodecConfiguration? ConfigurationOf(State state, StreamEntry stream, WaveFormat format)
    {
        Node parent = stream.CodecSpecific ?? stream.TypeSpecific ?? stream.Properties!;
        byte[] data = [];
        long offset = stream.CodecSpecific?.Offset ?? parent.Offset;
        if (stream.CodecSpecificLength > 0)
        {
            data = state.Reader.ReadBytesAt(stream.CodecSpecificOffset, stream.CodecSpecificLength);
        }

        var mapped = new MappedBytes(data, offset);
        return new CodecConfiguration(mapped, parent)
        {
            Duration = DurationOf(state),
            IsVbr = state.IsVbr,
            Format = new AudioFormat(format.EffectiveTag, format.Channels, format.SamplesPerSecond, format.AverageBytesPerSecond, format.BlockAlign, format.BitsPerSample)
            {
                ChannelMask = format.ChannelMask,
                ValidBitsPerSample = format.ValidBits,
                StreamNumber = stream.Number,
            },
        };
    }

    // The play duration the File Properties Object gives, less the preroll, in seconds, or null where
    // there is none or it is invalid (a broadcast).
    private static double? DeclaredDuration(State state)
    {
        if (state.FilePropertiesNode is null || state.IsBroadcast || state.PlayDuration == 0)
        {
            return null;
        }

        double seconds = (state.PlayDuration / 10_000_000.0) - (state.Preroll / 1000.0);
        return seconds > 0 ? seconds : null;
    }
}
