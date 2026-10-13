using Encspect.Core;

namespace Encspect.Formats.Aac;

// AudioSpecificConfig (ISO/IEC 14496-3 1.6.2.1, Table 1.15) with GASpecificConfig (4.4.1, Table
// 4.1), the program config element (4.4.1.1, Table 4.2), ELDSpecificConfig (4.4.1.2, Table 4.3)
// and, at its top level, UsacConfig (ISO/IEC 23003-3 5.2, Table 13). Each field becomes a node at
// the file bits it came from.
internal static class AudioSpecificConfig
{
    private const int SyncSbr = 0x2B7;
    private const int SyncPs = 0x548;

    // The configuration, or null where it is too damaged to say what the stream is; findings say
    // what is wrong either way.
    public static AacConfig? Parse(MappedBytes data, Node node, Action<Severity, string, string, Node> report)
    {
        var fields = new BitFields(data);
        var state = new State();
        try
        {
            Read(fields, node, state, report);
        }
        catch (EndOfStreamException)
        {
            report(Severity.Warning, "aac.config-truncated", $"The AudioSpecificConfig ends inside a field, after {fields.Position} of its {fields.Length} bits.", node);
            state.IsCut = true;
        }

        if (state.ObjectType is not { } type || state.SampleRate is not { } rate || state.ChannelConfiguration is not { } configuration)
        {
            return null;
        }

        int remaining = fields.Length - fields.Position;
        if (!state.IsCut && !state.StoppedEarly && remaining >= 8)
        {
            node.Add(Node.FromBits("Unread Bits", data, fields.Position, fields.Length, NodeValue.Integer(remaining, "bits"), description: Explain.UnreadBits));
            report(Severity.Info, "aac.config-trailing-bits", $"The AudioSpecificConfig holds {remaining} bits after its last field.", node);
        }

        return new AacConfig
        {
            ObjectType = type,
            CoreSampleRate = rate,
            ChannelConfiguration = configuration,
            Channels = state.Channels ?? (configuration == 0 ? null : AacTables.Channels(configuration)),
            Layout = state.Layout,
            Sbr = state.Sbr,
            Ps = state.Ps,
            Signaling = state.Signaling,
            ExtensionSampleRate = state.ExtensionSampleRate,
            CoreFrameLength = state.FrameLength,
            UsacSampleRate = state.UsacSampleRate,
            UsacOutputFrameLength = state.UsacOutputFrameLength,
            UsacSbrRatio = state.UsacSbrRatio,
            UsacLayout = state.UsacLayout,
        };
    }

    private static void Read(BitFields f, Node node, State state, Action<Severity, string, string, Node> report)
    {
        int type = ObjectType(f, node, "Audio Object Type", Explain.ObjectType);
        int rate = SampleRate(f, node, "Sample Rate", Explain.SampleRate, report);
        int configuration = (int)f.Read(node, "Channel Configuration", 4, Explain.ChannelConfiguration, v => $"{v} ({AacTables.ChannelLayout((int)v)})", Topics.ChannelLayouts);
        state.SampleRate = rate;
        state.ChannelConfiguration = configuration;
        if (AacTables.Channels(configuration) is null && configuration != 0)
        {
            report(Severity.Warning, "aac.reserved-channel-configuration", $"The channel configuration {configuration} is reserved.", node);
        }

        // Hierarchical signaling: SBR (5) or PS (29) first, then the core's object type.
        if (type is 5 or 29)
        {
            state.Sbr = true;
            state.Ps = type == 29 ? true : null;
            state.Signaling = "hierarchical";
            state.ExtensionSampleRate = SampleRate(f, node, "Extension Sample Rate", Explain.ExtensionSampleRate, report, Topics.Sbr);
            type = ObjectType(f, node, "Core Audio Object Type", Explain.CoreObjectType);
            if (type == 22)
            {
                f.Read(node, "Extension Channel Configuration", 4, Explain.ExtensionChannelConfiguration);
            }
        }

        state.ObjectType = type;
        state.FrameLength = type is 23 or 39 ? 512 : 1024;
        switch (type)
        {
            case 1 or 2 or 3 or 4 or 6 or 7 or 17 or 19 or 20 or 21 or 22 or 23:
                GaSpecificConfig(f, node, state, type, configuration, report);
                break;
            case 39:
                EldSpecificConfig(f, node, state, configuration);
                break;
            case 42:
                UsacConfig(f, node, state, report);
                state.StoppedEarly = true;
                return;
            default:
                report(Severity.Info, "aac.object-type-not-analyzed",
                    $"The audio object type {type} ({AacTables.ObjectTypeName(type)}) is not analyzed; its configuration is shown up to its object type.", node);
                state.StoppedEarly = true;
                return;
        }

        if (type is 17 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27 or 39)
        {
            long epConfig = f.Read(node, "Error Protection Config", 2, Explain.ErrorProtectionConfig);
            if (epConfig is 2 or 3)
            {
                report(Severity.Info, "aac.error-protection-not-analyzed", "The ErrorProtectionSpecificConfig is not analyzed.", node);
                state.StoppedEarly = true;
                return;
            }
        }

        // 1.6.2.1: backward-compatible explicit signaling, after the core's configuration, where at
        // least 16 bits remain.
        if (state.Signaling is null && f.Remaining >= 16)
        {
            BackwardCompatibleSignaling(f, node, state, report);
        }
    }

    private static void BackwardCompatibleSignaling(BitFields f, Node node, State state, Action<Severity, string, string, Node> report)
    {
        if (f.Look(11) != SyncSbr)
        {
            return;
        }

        int start = f.Position;
        var extension = new Node("Sync Extension", 0, 0);
        f.Read(extension, "Sync Extension Type", 11, Explain.SyncExtensionType, v => NodeValue.Hex(v, 3));
        int extensionType = ObjectType(f, extension, "Extension Audio Object Type", Explain.ExtensionObjectType);
        state.Signaling = "backward compatible";
        if (extensionType == 5)
        {
            state.Sbr = f.Flag(extension, "HasSbr", Explain.HasSbr, Topics.Sbr);
            if (state.Sbr == true)
            {
                state.ExtensionSampleRate = SampleRate(f, extension, "Extension Sample Rate", Explain.ExtensionSampleRate, report, Topics.Sbr);
                if (f.Remaining >= 12 && f.Look(11) == SyncPs)
                {
                    f.Read(extension, "Sync Extension Type", 11, Explain.SyncExtensionType, v => NodeValue.Hex(v, 3));
                    state.Ps = f.Flag(extension, "HasPs", Explain.HasPs, Topics.ParametricStereo);
                }
            }
            else
            {
                state.Ps = false;
            }
        }
        else if (extensionType == 22)
        {
            if (f.Flag(extension, "HasSbr", Explain.HasSbr, Topics.Sbr))
            {
                SampleRate(f, extension, "Extension Sample Rate", Explain.ExtensionSampleRate, report, Topics.Sbr);
            }

            f.Read(extension, "Extension Channel Configuration", 4, Explain.ExtensionChannelConfiguration);
        }
        else
        {
            report(Severity.Warning, "aac.unknown-sync-extension",
                $"The sync extension names the audio object type {extensionType} ({AacTables.ObjectTypeName(extensionType)}), which it does not define.", node);
        }

        string summary = state.Sbr == true ? (state.Ps == true ? "SBR and PS present" : "SBR present") : "SBR absent";
        node.Add(f.Span("Sync Extension", start, summary, Explain.SyncExtension, extension, Topics.Sbr));
    }

    private static int ObjectType(BitFields f, Node node, string name, string description)
    {
        int start = f.Position;
        int type = (int)f.Take(5);
        if (type == 31)
        {
            type = 32 + (int)f.Take(6);
        }

        node.Add(f.Span(name, start, $"{type} ({AacTables.ObjectTypeName(type)})", description, topic: Topics.ObjectTypes));
        return type;
    }

    private static int SampleRate(BitFields f, Node node, string name, string description, Action<Severity, string, string, Node> report, string? topic = null)
    {
        int start = f.Position;
        int index = (int)f.Take(4);
        if (index == 15)
        {
            int rate = (int)f.Take(24);
            node.Add(f.Span(name, start, NodeValue.Integer(rate, "Hz"), description, topic: topic));
            return rate;
        }

        Node field = f.Span(name, start, AacTables.SampleRate(index) is { } known ? NodeValue.Integer(known, "Hz") : $"{index} (reserved)", description, topic: topic);
        node.Add(field);
        if (AacTables.SampleRate(index) is not { } value)
        {
            report(Severity.Warning, "aac.reserved-sample-rate", $"The sampling frequency index {index} is reserved.", field);
            return 0;
        }

        return value;
    }

    // 4.4.1, Table 4.1.
    private static void GaSpecificConfig(BitFields f, Node node, State state, int type, int configuration, Action<Severity, string, string, Node> report)
    {
        int full = type == 23 ? 512 : 1024;
        int shortened = type == 23 ? 480 : 960;
        bool isShort = f.Read(node, "Frame Length", 1, Explain.FrameLength, v => NodeValue.Integer(v == 0 ? full : shortened, "samples")) != 0;
        state.FrameLength = isShort ? shortened : full;
        if (f.Flag(node, "DependsOnCoreCoder", Explain.DependsOnCoreCoder))
        {
            f.Read(node, "Core Coder Delay", 14, Explain.CoreCoderDelay, v => NodeValue.Integer(v, "samples"));
        }

        bool hasExtension = f.Flag(node, "HasExtension", Explain.HasExtension);
        if (configuration == 0)
        {
            var pce = new Node("Program Config Element", 0, 0);
            int start = f.Position;

            // The element's findings wait for its node, which exists only once it is read: the
            // scratch node it is read into is not in the tree.
            var findings = new List<(Severity Severity, string Code, string Message)>();
            int channels = ProgramConfigElement.Read(f, pce, 0, (severity, code, message, _) => findings.Add((severity, code, message)), out IReadOnlyList<Speaker> layout);
            Node element = node.Add(f.Span("Program Config Element", start, NodeValue.Integer(channels, "channels"), Explain.ProgramConfigElement, pce, Topics.ChannelLayouts));
            foreach ((Severity severity, string code, string message) in findings)
            {
                report(severity, code, message, element);
            }

            state.Channels = channels;
            state.Layout = layout;
        }

        if (type is 6 or 20)
        {
            f.Read(node, "Layer Number", 3, Explain.LayerNumber);
        }

        if (hasExtension)
        {
            if (type == 22)
            {
                f.Read(node, "Number of Subframes", 5, Explain.SubframeCount);
                f.Read(node, "Layer Length", 11, Explain.LayerLength);
            }

            if (type is 17 or 19 or 20 or 23)
            {
                f.Flag(node, "HasSectionDataResilience", Explain.SectionResilience);
                f.Flag(node, "HasScalefactorDataResilience", Explain.ScalefactorResilience);
                f.Flag(node, "HasSpectralDataResilience", Explain.SpectralResilience);
            }

            if (f.Flag(node, "HasExtension3", Explain.HasExtension3))
            {
                report(Severity.Info, "aac.extension3", "The GASpecificConfig sets extensionFlag3, whose contents the specification leaves to a later version.", node);
                state.StoppedEarly = true;
            }
        }
    }

    // 4.4.1.2, Table 4.3, with ld_sbr_header (Table 4.5) and sbr_header (Table 4.63).
    private static void EldSpecificConfig(BitFields f, Node node, State state, int configuration)
    {
        bool isShort = f.Read(node, "Frame Length", 1, Explain.FrameLength, v => NodeValue.Integer(v == 0 ? 512 : 480, "samples")) != 0;
        state.FrameLength = isShort ? 480 : 512;
        f.Flag(node, "HasSectionDataResilience", Explain.SectionResilience);
        f.Flag(node, "HasScalefactorDataResilience", Explain.ScalefactorResilience);
        f.Flag(node, "HasSpectralDataResilience", Explain.SpectralResilience);
        if (f.Flag(node, "HasLowDelaySbr", Explain.HasLowDelaySbr, Topics.Sbr))
        {
            bool dualRate = f.Read(node, "Low Delay SBR Sampling Rate", 1, Explain.LowDelaySbrRate, v => v == 0 ? "downsampled SBR (core rate)" : "dual-rate SBR") != 0;
            f.Flag(node, "HasLowDelaySbrCrc", Explain.HasLowDelaySbrCrc);
            int headers = configuration switch
            {
                1 or 2 => 1,
                3 => 2,
                4 => 3,
                5 or 6 => 4,
                7 => 5,
                _ => 0,
            };
            for (int i = 0; i < headers; i++)
            {
                int start = f.Position;
                var header = new Node("SBR Header", 0, 0);
                SbrHeader(f, header);
                node.Add(f.Span("SBR Header", start, null, Explain.SbrHeader, header, Topics.Sbr));
            }

            state.Sbr = true;
            state.Ps = false;
            state.Signaling = "low delay SBR";
            state.ExtensionSampleRate = dualRate ? state.SampleRate * 2 : state.SampleRate;
        }
        else
        {
            state.Sbr = false;
        }

        while (true)
        {
            int start = f.Position;
            var extension = new Node("Extension", 0, 0);
            long kind = f.Read(extension, "Type", 4, Explain.EldExtensionType);
            if (kind == 0)
            {
                node.Add(f.Span("ELD Extension Terminator", start, null, Explain.EldTerminator));
                break;
            }

            long length = f.Read(extension, "Length", 4, Explain.EldExtensionLength);
            if (length == 15)
            {
                long more = f.Read(extension, "Additional Length", 8, Explain.EldExtensionLength);
                length += more;
                if (more == 255)
                {
                    length += f.Read(extension, "Further Length", 16, Explain.EldExtensionLength);
                }
            }

            f.Skip(length * 8);
            node.Add(f.Span("ELD Extension", start, NodeValue.Integer(length, "bytes"), Explain.EldExtension, extension));
        }
    }

    private static void SbrHeader(BitFields f, Node node)
    {
        f.Read(node, "bs_amp_res", 1, Explain.SbrAmpRes);
        f.Read(node, "bs_start_freq", 4, Explain.SbrStartFreq);
        f.Read(node, "bs_stop_freq", 4, Explain.SbrStopFreq);
        f.Read(node, "bs_xover_band", 3, Explain.SbrXoverBand);
        f.Read(node, "bs_reserved", 2, Explain.SbrReserved);
        bool extra1 = f.Flag(node, "HasHeaderExtra1", Explain.SbrHeaderExtra1);
        bool extra2 = f.Flag(node, "HasHeaderExtra2", Explain.SbrHeaderExtra2);
        if (extra1)
        {
            f.Read(node, "bs_freq_scale", 2, Explain.SbrFreqScale);
            f.Read(node, "bs_alter_scale", 1, Explain.SbrAlterScale);
            f.Read(node, "bs_noise_bands", 2, Explain.SbrNoiseBands);
        }

        if (extra2)
        {
            f.Read(node, "bs_limiter_bands", 2, Explain.SbrLimiterBands);
            f.Read(node, "bs_limiter_gains", 2, Explain.SbrLimiterGains);
            f.Read(node, "bs_interpol_freq", 1, Explain.SbrInterpolFreq);
            f.Read(node, "bs_smoothing_mode", 1, Explain.SbrSmoothingMode);
        }
    }

    // ISO/IEC 23003-3 5.2, Tables 13 to 16 and 22: the output rate, the frame lengths and SBR ratio
    // (Table 72, coreSbrFrameLengthIndex), the channel configuration and the elements' types and
    // core settings. A channel pair element with an MPEG Surround 2-1-2 configuration and anything
    // after it is shown as unread.
    private static void UsacConfig(BitFields f, Node node, State state, Action<Severity, string, string, Node> report)
    {
        int usacStart = f.Position;
        var usac = new Node("UsacConfig", 0, 0);
        try
        {
            int start = f.Position;
            int index = (int)f.Take(5);
            int rate;
            if (index == 31)
            {
                rate = (int)f.Take(24);
                usac.Add(f.Span("Sample Rate", start, NodeValue.Integer(rate, "Hz"), Explain.UsacSampleRate));
            }
            else
            {
                rate = AacTables.UsacSampleRate(index) ?? 0;
                usac.Add(f.Span("Sample Rate", start, rate > 0 ? NodeValue.Integer(rate, "Hz") : $"{index} (reserved)", Explain.UsacSampleRate));
            }

            state.UsacSampleRate = rate;
            long frameIndex = f.Read(usac, "Core SBR Frame Length Index", 3, Explain.CoreSbrFrameLengthIndex, topic: Topics.Sbr, describe: v => v switch
            {
                0 => "0 (768 samples, no SBR)",
                1 => "1 (1024 samples, no SBR)",
                2 => "2 (768 core samples, 8:3 SBR, 2048 output samples)",
                3 => "3 (1024 core samples, 2:1 SBR, 2048 output samples)",
                4 => "4 (1024 core samples, 4:1 SBR, 4096 output samples)",
                _ => $"{v} (reserved)",
            });
            (int core, int output, int sbrRatio) = frameIndex switch
            {
                0 => (768, 768, 0),
                1 => (1024, 1024, 0),
                2 => (768, 2048, 2),
                3 => (1024, 2048, 3),
                4 => (1024, 4096, 1),
                _ => (1024, 1024, 0),
            };
            state.FrameLength = core;
            state.UsacOutputFrameLength = output;
            state.UsacSbrRatio = sbrRatio;
            state.Sbr = sbrRatio > 0;
            if (frameIndex > 4)
            {
                report(Severity.Warning, "aac.usac-reserved-frame-length", $"The coreSbrFrameLengthIndex {frameIndex} is reserved.", node);
            }

            int channelIndex = (int)f.Read(usac, "Channel Configuration", 5, Explain.UsacChannelConfiguration, v => $"{v} ({AacTables.ChannelLayout((int)v)})", Topics.ChannelLayouts);
            state.Layout = AacTables.UsacLayout(channelIndex);
            state.Channels = state.Layout?.Count;
            if (channelIndex == 0)
            {
                int channelStart = f.Position;
                var channels = new Node("UsacChannelConfig", 0, 0);
                long count = f.Escaped(channels, "Output Channels", Explain.OutputChannels, 5, 8, 16);
                var positions = new List<int>();
                for (long i = 0; i < count; i++)
                {
                    positions.Add((int)f.Read(channels, "Output Channel Position", 5, Explain.OutputChannelPosition));
                }

                state.Channels = (int)count;
                state.Layout = AacTables.UsacPositions(positions);
                node.Add(f.Span("UsacChannelConfig", channelStart, NodeValue.Integer(count, "channels"), Explain.UsacChannelConfig, channels));
            }

            int decoderStart = f.Position;
            var decoder = new Node("UsacDecoderConfig", 0, 0);
            long elements = f.Escaped(decoder, "Elements", Explain.UsacElements, 4, 8, 16, add: 1);
            var kinds = new List<string>();
            bool plainPreRollFirst = false;
            bool harmonicSbrOrPvc = false;
            for (long i = 0; i < elements; i++)
            {
                int elementStart = f.Position;
                var element = new Node("Element", 0, 0);
                long kind = f.Read(element, "Element Type", 2, Explain.UsacElementType, v => v switch { 0 => "SCE", 1 => "CPE", 2 => "LFE", _ => "EXT" });
                bool stop = false;
                if (kind is 0 or 1)
                {
                    f.Flag(element, "HasTimeWarpedMdct", Explain.TimeWarpedMdct);
                    f.Flag(element, "HasNoiseFilling", Explain.NoiseFilling);
                    if (sbrRatio > 0)
                    {
                        harmonicSbrOrPvc |= f.Flag(element, "HasHarmonicSbr", Explain.HarmonicSbr, Topics.Sbr);
                        f.Flag(element, "HasInterTes", Explain.InterTes, Topics.Sbr);
                        harmonicSbrOrPvc |= f.Flag(element, "HasPvc", Explain.Pvc, Topics.Sbr);
                        f.Read(element, "Default Start Frequency", 4, Explain.DefaultStartFreq, topic: Topics.Sbr);
                        f.Read(element, "Default Stop Frequency", 4, Explain.DefaultStopFreq, topic: Topics.Sbr);
                        bool extra1 = f.Flag(element, "HasDefaultHeaderExtra1", Explain.DefaultHeaderExtra1);
                        bool extra2 = f.Flag(element, "HasDefaultHeaderExtra2", Explain.DefaultHeaderExtra2);
                        if (extra1)
                        {
                            f.Read(element, "Default Frequency Scale", 2, Explain.DefaultFreqScale);
                            f.Read(element, "Default Alter Scale", 1, Explain.DefaultAlterScale);
                            f.Read(element, "Default Noise Bands", 2, Explain.DefaultNoiseBands);
                        }

                        if (extra2)
                        {
                            f.Read(element, "Default Limiter Bands", 2, Explain.DefaultLimiterBands);
                            f.Read(element, "Default Limiter Gains", 2, Explain.DefaultLimiterGains);
                            f.Read(element, "Default Interpolated Frequency", 1, Explain.DefaultInterpolFreq);
                            f.Read(element, "Default Smoothing Mode", 1, Explain.DefaultSmoothingMode);
                        }

                        if (kind == 1)
                        {
                            long stereo = f.Read(element, "Stereo Config Index", 2, Explain.StereoConfigIndex);
                            stop = stereo > 0;
                        }
                    }
                }
                else if (kind == 3)
                {
                    long extensionType = f.Escaped(element, "Extension Element Type", Explain.ExtElementType, 4, 8, 16);
                    element.Children[^1].Value = $"{extensionType} ({extensionType switch { 0 => "fill", 1 => "MPEG Surround", 2 => "SAOC", 3 => "audio pre-roll", 4 => "DRC", _ => "reserved" }})";
                    long length = f.Escaped(element, "Configuration Length", Explain.ExtConfigLength, 4, 8, 16);
                    bool hasDefaultLength = f.Flag(element, "HasDefaultLength", Explain.HasDefaultLength);
                    if (hasDefaultLength)
                    {
                        f.Escaped(element, "Default Length", Explain.DefaultLength, 8, 16, 0, add: 1);
                    }

                    bool fragmented = f.Flag(element, "HasPayloadFragmentation", Explain.PayloadFragmentation);
                    f.Skip(length * 8);
                    plainPreRollFirst |= i == 0 && extensionType == 3 && length == 0 && !hasDefaultLength && !fragmented;
                    kinds.Add($"EXT{extensionType}");
                }

                string name = kind switch { 0 => "SCE", 1 => "CPE", 2 => "LFE", _ => "EXT" };
                if (kind != 3)
                {
                    kinds.Add(name);
                }

                decoder.Add(f.Span("Element", elementStart, name, Explain.UsacElement, element));
                if (stop)
                {
                    report(Severity.Info, "aac.usac-config-not-analyzed",
                        "The USAC configuration is read up to a channel pair element with MPEG Surround 2-1-2 stereo, whose configuration is not analyzed yet.", node);
                    state.StoppedEarly = true;
                    usac.Add(f.Span("UsacDecoderConfig", decoderStart, NodeValue.Integer(elements, "elements"), Explain.UsacDecoderConfig, decoder));
                    node.Add(f.Span("UsacConfig", usacStart, $"{rate} Hz, {output} samples per frame", Explain.UsacConfig, usac, Topics.Usac));
                    return;
                }
            }

            usac.Add(f.Span("UsacDecoderConfig", decoderStart, NodeValue.Integer(elements, "elements"), Explain.UsacDecoderConfig, decoder));
            var extensionTypes = new List<long>();
            if (f.Flag(usac, "HasConfigExtension", Explain.HasConfigExtension))
            {
                int extensionStart = f.Position;
                var extensions = new Node("UsacConfigExtension", 0, 0);
                long count = f.Escaped(extensions, "Extensions", Explain.ConfigExtensionCount, 2, 4, 8, add: 1);
                for (long i = 0; i < count; i++)
                {
                    long extensionType = f.Escaped(extensions, "Extension Type", Explain.ConfigExtensionType, 4, 8, 16);
                    extensionTypes.Add(extensionType);
                    extensions.Children[^1].Value = $"{extensionType} ({extensionType switch { 0 => "fill", 2 => "loudness info", 7 => "stream ID", _ => "reserved" }})";
                    long length = f.Escaped(extensions, "Extension Length", Explain.ConfigExtensionLength, 4, 8, 16);
                    f.Skip(length * 8);
                }

                usac.Add(f.Span("UsacConfigExtension", extensionStart, NodeValue.Integer(count, "extensions"), Explain.UsacConfigExtension, extensions));
            }

            Node config = node.Add(f.Span("UsacConfig", usacStart, $"{rate} Hz, {output} samples per frame", Explain.UsacConfig, usac, Topics.Usac));
            state.UsacLayout = new UsacLayout(config, kinds, plainPreRollFirst, harmonicSbrOrPvc, extensionTypes);
        }
        catch (EndOfStreamException)
        {
            if (f.Position > usacStart)
            {
                node.Add(f.Span("UsacConfig", usacStart, "cut short", Explain.UsacConfig, usac, Topics.Usac));
            }

            throw;
        }
    }

    private sealed class State
    {
        public int? ObjectType { get; set; }

        public int? SampleRate { get; set; }

        public int? ChannelConfiguration { get; set; }

        public int? Channels { get; set; }

        public IReadOnlyList<Speaker>? Layout { get; set; }

        public bool? Sbr { get; set; }

        public bool? Ps { get; set; }

        public string? Signaling { get; set; }

        public int? ExtensionSampleRate { get; set; }

        public int FrameLength { get; set; } = 1024;

        public int? UsacSampleRate { get; set; }

        public int? UsacOutputFrameLength { get; set; }

        public int? UsacSbrRatio { get; set; }

        public UsacLayout? UsacLayout { get; set; }

        public bool IsCut { get; set; }

        // Whether reading stopped at a structure that is not analyzed, so the bits after it are not
        // reported as unread.
        public bool StoppedEarly { get; set; }
    }
}
