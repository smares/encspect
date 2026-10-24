using System.Buffers.Binary;
using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Mp4;

// What the container states about a track's speakers: QuickTime's channel layout atom (chan),
// Core Audio's AudioChannelLayout, and ISO/IEC 14496-12's channel layout box (chnl) with the speaker
// positions and channel configurations of ISO/IEC 23091-3. Both sit in the audio sample entry. The
// codec's own statement wins; the container's completes a stream whose codec states none.
public sealed partial class Mp4Parser
{
    private const uint UseChannelDescriptions = 0;
    private const uint UseChannelBitmap = 1U << 16;

    // Core Audio's layout tags (CoreAudioBaseTypes.h, AudioChannelLayoutTag) by their upper 16 bits,
    // with the channels in Apple's abbreviations, in order. Tags whose channels are not speakers
    // (Ambisonic B-format, mid/side) have discrete channels.
    private static readonly Dictionary<uint, (string Name, string Channels)> LayoutTags = new()
    {
        [100] = ("Mono", "C"),
        [101] = ("Stereo", "L R"),
        [102] = ("StereoHeadphones", "L R"),
        [103] = ("MatrixStereo", "Lt Rt"),
        [104] = ("MidSide", "- -"),
        [105] = ("XY", "L R"),
        [106] = ("Binaural", "L R"),
        [107] = ("Ambisonic_B_Format", "- - - -"),
        [108] = ("Quadraphonic", "L R Ls Rs"),
        [109] = ("Pentagonal", "L R Ls Rs C"),
        [110] = ("Hexagonal", "L R Ls Rs C Cs"),
        [111] = ("Octagonal", "L R Ls Rs C Cs Lw Rw"),
        [112] = ("Cube", "L R Rls Rrs Vhl Vhr Ltr Rtr"),
        [113] = ("MPEG_3_0_A", "L R C"),
        [114] = ("MPEG_3_0_B", "C L R"),
        [115] = ("MPEG_4_0_A", "L R C Cs"),
        [116] = ("MPEG_4_0_B", "C L R Cs"),
        [117] = ("MPEG_5_0_A", "L R C Ls Rs"),
        [118] = ("MPEG_5_0_B", "L R Ls Rs C"),
        [119] = ("MPEG_5_0_C", "L C R Ls Rs"),
        [120] = ("MPEG_5_0_D", "C L R Ls Rs"),
        [121] = ("MPEG_5_1_A", "L R C LFE Ls Rs"),
        [122] = ("MPEG_5_1_B", "L R Ls Rs C LFE"),
        [123] = ("MPEG_5_1_C", "L C R Ls Rs LFE"),
        [124] = ("MPEG_5_1_D", "C L R Ls Rs LFE"),
        [125] = ("MPEG_6_1_A", "L R C LFE Ls Rs Cs"),
        [126] = ("MPEG_7_1_A", "L R C LFE Ls Rs Lc Rc"),
        [127] = ("MPEG_7_1_B", "C Lc Rc L R Ls Rs LFE"),
        [128] = ("MPEG_7_1_C", "L R C LFE Ls Rs Rls Rrs"),
        [129] = ("Emagic_Default_7_1", "L R Ls Rs C LFE Lc Rc"),
        [130] = ("SMPTE_DTV", "L R C LFE Ls Rs Lt Rt"),
        [131] = ("ITU_2_1", "L R Cs"),
        [132] = ("ITU_2_2", "L R Ls Rs"),
        [133] = ("DVD_4", "L R LFE"),
        [134] = ("DVD_5", "L R LFE Cs"),
        [135] = ("DVD_6", "L R LFE Ls Rs"),
        [136] = ("DVD_10", "L R C LFE"),
        [137] = ("DVD_11", "L R C LFE Cs"),
        [138] = ("DVD_18", "L R Ls Rs LFE"),
        [139] = ("AudioUnit_6_0", "L R Ls Rs C Cs"),
        [140] = ("AudioUnit_7_0", "L R Ls Rs C Rls Rrs"),
        [141] = ("AAC_6_0", "C L R Ls Rs Cs"),
        [142] = ("AAC_6_1", "C L R Ls Rs Cs LFE"),
        [143] = ("AAC_7_0", "C L R Ls Rs Rls Rrs"),
        [144] = ("AAC_Octagonal", "C L R Ls Rs Rls Rrs Cs"),
        [148] = ("AudioUnit_7_0_Front", "L R Ls Rs C Lc Rc"),
        [149] = ("AC3_1_0_1", "C LFE"),
        [150] = ("AC3_3_0", "L C R"),
        [151] = ("AC3_3_1", "L C R Cs"),
        [152] = ("AC3_3_0_1", "L C R LFE"),
        [153] = ("AC3_2_1_1", "L R Cs LFE"),
        [154] = ("AC3_3_1_1", "L C R Cs LFE"),
        [155] = ("EAC_6_0_A", "L C R Ls Rs Cs"),
        [156] = ("EAC_7_0_A", "L C R Ls Rs Rls Rrs"),
        [157] = ("EAC3_6_1_A", "L C R Ls Rs LFE Cs"),
        [158] = ("EAC3_6_1_B", "L C R Ls Rs LFE Ts"),
        [159] = ("EAC3_6_1_C", "L C R Ls Rs LFE Vhc"),
        [160] = ("EAC3_7_1_A", "L C R Ls Rs LFE Rls Rrs"),
        [161] = ("EAC3_7_1_B", "L C R Ls Rs LFE Lc Rc"),
        [162] = ("EAC3_7_1_C", "L C R Ls Rs LFE Lsd Rsd"),
        [163] = ("EAC3_7_1_D", "L C R Ls Rs LFE Lw Rw"),
        [164] = ("EAC3_7_1_E", "L C R Ls Rs LFE Vhl Vhr"),
        [165] = ("EAC3_7_1_F", "L C R Ls Rs LFE Cs Ts"),
        [166] = ("EAC3_7_1_G", "L C R Ls Rs LFE Cs Vhc"),
        [167] = ("EAC3_7_1_H", "L C R Ls Rs LFE Ts Vhc"),
        [168] = ("DTS_3_1", "C L R LFE"),
        [169] = ("DTS_4_1", "C L R Cs LFE"),
        [170] = ("DTS_6_0_A", "Lc Rc L R Ls Rs"),
        [171] = ("DTS_6_0_B", "C L R Rls Rrs Ts"),
        [172] = ("DTS_6_0_C", "C Cs L R Rls Rrs"),
        [173] = ("DTS_6_1_A", "Lc Rc L R Ls Rs LFE"),
        [174] = ("DTS_6_1_B", "C L R Rls Rrs Ts LFE"),
        [175] = ("DTS_6_1_C", "C Cs L R Rls Rrs LFE"),
        [176] = ("DTS_7_0", "Lc C Rc L R Ls Rs"),
        [177] = ("DTS_7_1", "Lc C Rc L R Ls Rs LFE"),
        [178] = ("DTS_8_0_A", "Lc Rc L R Ls Rs Rls Rrs"),
        [179] = ("DTS_8_0_B", "Lc C Rc L R Ls Cs Rs"),
        [180] = ("DTS_8_1_A", "Lc Rc L R Ls Rs Rls Rrs LFE"),
        [181] = ("DTS_8_1_B", "Lc C Rc L R Ls Cs Rs LFE"),
        [182] = ("DTS_6_1_D", "C L R Ls Rs LFE Cs"),
        [183] = ("AAC_7_1_B", "C L R Ls Rs Rls Rrs LFE"),
        [184] = ("AAC_7_1_C", "C L R Ls Rs LFE Vhl Vhr"),
        [185] = ("WAVE_4_0_B", "L R Rls Rrs"),
        [186] = ("WAVE_5_0_B", "L R C Rls Rrs"),
        [187] = ("WAVE_5_1_B", "L R C LFE Rls Rrs"),
        [188] = ("WAVE_6_1", "L R C LFE Cs Ls Rs"),
        [189] = ("WAVE_7_1", "L R C LFE Rls Rrs Ls Rs"),
        [192] = ("Atmos_7_1_4", "L R C LFE Ls Rs Rls Rrs Vhl Vhr Ltr Rtr"),
        [193] = ("Atmos_9_1_6", "L R C LFE Ls Rs Rls Rrs Lw Rw Vhl Vhr Ltm Rtm Ltr Rtr"),
        [194] = ("Atmos_5_1_2", "L R C LFE Ls Rs Ltm Rtm"),
        [195] = ("Atmos_5_1_4", "L R C LFE Ls Rs Vhl Vhr Ltr Rtr"),
        [196] = ("Atmos_7_1_2", "L R C LFE Ls Rs Rls Rrs Ltm Rtm"),
    };

    // ISO/IEC 23091-3's ChannelConfiguration values 1 to 14 as speaker positions (the indexes of
    // Positions), in the configuration's channel order; 8 is two independent mono channels, -1 a channel without a position.
    // The specification is not freely available: the speaker sets are checked against MediaInfo's and GPAC's tables and the order of
    // 1 to 7, 11, 12, 13 and 14 against FFmpeg's aac_channel_layout_map (docs/development.md, "Tables").
    private static readonly int[]?[] Configurations =
    [
        null,
        [2],
        [0, 1],
        [2, 0, 1],
        [2, 0, 1, 10],
        [2, 0, 1, 4, 5],
        [2, 0, 1, 4, 5, 3],
        [2, 6, 7, 0, 1, 4, 5, 3],
        [-1, -1],
        [0, 1, 10],
        [0, 1, 4, 5],
        [2, 0, 1, 4, 5, 10, 3],
        [2, 0, 1, 4, 5, 8, 9, 3],
        [2, 6, 7, 0, 1, 13, 14, 8, 9, 10, 3, 26, 19, 17, 18, 23, 24, 25, 20, 21, 22, 29, 27, 28],
        [2, 0, 1, 4, 5, 3, 17, 18],
    ];

    // ISO/IEC 23091-3's speaker positions by index. The names are checked against the comments of FFmpeg's iso_channel_position and
    // MediaInfo's abbreviations (docs/development.md, "Tables").
    private static readonly string[] Positions =
    [
        "left front", "right front", "center front", "low frequency enhancement", "left surround", "right surround", "left front center",
        "right front center", "rear surround left", "rear surround right", "rear center", "left surround direct", "right surround direct",
        "left side surround", "right side surround", "left wide front", "right wide front", "left front vertical height",
        "right front vertical height", "center front vertical height", "left surround vertical height rear", "right surround vertical height rear",
        "center vertical height rear", "left vertical height side surround", "right vertical height side surround", "top center surround",
        "low frequency enhancement 2", "left front vertical bottom", "right front vertical bottom", "center front vertical bottom",
        "left vertical height surround", "right vertical height surround", "reserved", "reserved", "reserved", "reserved",
        "low frequency enhancement 3", "left edge of screen", "right edge of screen", "half-way between center of screen and left edge of screen",
        "half-way between center of screen and right edge of screen", "left back surround", "right back surround",
    ];

    // QuickTime File Format, "Audio channel layout atom": a full box holding Core Audio's
    // AudioChannelLayout, big-endian: a layout tag, a channel bitmap that counts where the tag is
    // UseChannelBitmap, and channel descriptions that count where it is UseChannelDescriptions.
    private static void QuickTimeChannelLayout(State state, Node node, BoxFields f)
    {
        FullBox(node, f);
        uint tag = (uint)f.Field(node, "Layout Tag", 4, Explain.ChanLayoutTag, v => TagText((uint)v), topic: Topics.ChannelLayouts);
        uint bitmap = (uint)f.Field(node, "Channel Bitmap", 4, Explain.ChanBitmap, v => NodeValue.Hex((long)v, 8));
        uint count = f.U32(node, "Channel Descriptions", Explain.ChanDescriptionCount);
        var labels = new List<uint>();
        long descriptionsStart = f.Position;
        uint read = 0;
        for (uint i = 0; i < count && f.Remaining >= 20; i++, read++)
        {
            if (i % 4096 == 0)
            {
                state.Context.CancellationToken.ThrowIfCancellationRequested();
            }

            long start = f.Position;
            if (i >= MaxListedEntries)
            {
                // Past the descriptions shown, only the position moves on: a layout of that many channels is not stated.
                f.Skip(20);
                continue;
            }

            var description = new Node("Channel Description", start, 20) { Description = Explain.ChanDescription };
            uint label = (uint)f.Field(description, "Label", 4, Explain.ChanLabel, v => LabelText((uint)v));
            uint flags = (uint)f.Field(description, "Flags", 4, Explain.ChanFlags, v => NodeValue.Hex((long)v, 8));
            long at = f.Position;
            byte[] coordinates = f.TakeBytes(12);
            description.Add(new Node("Coordinates", at, 12, (flags & 3) == 0 ? "not used" : CoordinatesText(coordinates)) { Description = Explain.ChanCoordinates });
            description.Value = LabelText(label);
            node.Add(description);
            labels.Add(label);
        }

        MoreEntries(node, read, descriptionsStart + (MaxListedEntries * 20L), f.Position);

        if (read < count)
        {
            state.Findings.AddNow(Severity.Warning, "mp4.chan-descriptions-exceed-box",
                $"The chan box at 0x{node.Offset:X} declares {Counts.Of(count, "channel description", "channel descriptions")}, but holds room for {read}.", node);
        }

        IReadOnlyList<Speaker>? layout = tag switch
        {
            UseChannelDescriptions => labels.Count > 0 && read <= MaxListedEntries ? FromLabels(labels) : null,
            UseChannelBitmap => FromBitmap(bitmap),
            _ when (tag >> 16) is 147 or 0xFFFF => ChannelLayouts.Discrete((int)(tag & 0xFFFF)),
            _ when LayoutTags.TryGetValue(tag >> 16, out (string Name, string Channels) known) && known.Channels.Split(' ').Length == (tag & 0xFFFF) =>
                FromAbbreviations(known.Channels.Split(' ')),
            _ => null,
        };
        Stated(state, node, layout, "chan");
    }

    // ISO/IEC 14496-12 12.2.4: whether the stream carries channels or objects, then for channels a
    // ChannelConfiguration of ISO/IEC 23091-3 with a map of the channels it leaves out, or 0 and a
    // speaker position per channel of the sample entry (a position of 126 adds its azimuth and
    // elevation). Version 1 is laid out differently and is not read.
    private static void IsoChannelLayout(State state, Node node, BoxFields f)
    {
        int version = FullBox(node, f).Version;
        if (version != 0)
        {
            if (f.Remaining > 0)
            {
                node.Add(new Node("Data", f.Position, f.Remaining, NodeValue.Integer(f.Remaining, "bytes")) { Description = Explain.UnreadData });
            }

            state.Findings.AddNow(Severity.Info, "mp4.chnl-version-not-read", $"The chnl box at 0x{node.Offset:X} is of version {version}, which is not read; only version 0 is.", node);
            return;
        }

        int structure = (int)f.Field(node, "Stream Structure", 1, Explain.ChnlStreamStructure, v => (v & 3) switch
        {
            1 => $"{v} (channels)",
            2 => $"{v} (objects)",
            3 => $"{v} (channels and objects)",
            _ => $"{v} (neither)",
        });
        IReadOnlyList<Speaker>? layout = null;
        if ((structure & 1) != 0)
        {
            int defined = (int)f.Field(node, "Defined Layout", 1, Explain.ChnlDefinedLayout, v => DefinedText((int)v), topic: Topics.ChannelLayouts);
            if (defined == 0)
            {
                int channels = state.CurrentEntry?.Channels ?? 0;
                var positions = new List<int>();
                for (int i = 0; i < channels; i++)
                {
                    int position = (int)f.Field(node, "Speaker Position", 1, Explain.ChnlSpeakerPosition, v => PositionText((int)v));
                    positions.Add(position);
                    if (position == 126)
                    {
                        long at = f.Position;
                        short azimuth = (short)f.Take(2);
                        node.Add(new Node("Azimuth", at, 2, NodeValue.Integer(azimuth, "degrees")) { Description = Explain.ChnlAzimuth });
                        at = f.Position;
                        sbyte elevation = (sbyte)f.Take(1);
                        node.Add(new Node("Elevation", at, 1, NodeValue.Integer(elevation, "degrees")) { Description = Explain.ChnlElevation });
                    }
                }

                layout = positions.Count > 0 ? FromPositions(positions) : null;
            }
            else
            {
                ulong omitted = f.Field(node, "Omitted Channels", 8, Explain.ChnlOmittedChannels, v => NodeValue.Hex(v, 16));
                if (defined < Configurations.Length && Configurations[defined] is { } configuration)
                {
                    // The lowest bit stands for the configuration's first channel.
                    layout = FromPositions([.. configuration.Where((_, i) => i >= 64 || (omitted & (1UL << i)) == 0)]);
                }
            }
        }

        if ((structure & 2) != 0)
        {
            f.U8(node, "Object Count", Explain.ChnlObjectCount);
        }

        Stated(state, node, layout, "chnl");
    }

    // The box's value and the sample entry's statement, of which the first box read counts.
    private static void Stated(State state, Node node, IReadOnlyList<Speaker>? layout, string box)
    {
        node.Value = layout is null ? "no layout stated that the analyzer knows" : ChannelLayouts.Describe(layout);
        if (layout is not null && state.CurrentEntry is { Layout: null } entry)
        {
            entry.Layout = layout;
            entry.LayoutNode = node;
            entry.LayoutBox = box;
        }
    }

    // After the codec has reported the track's streams: a stream whose codec states no layout gets
    // the container's, where the channel counts agree; a codec that states other speakers than the
    // container keeps its own, with a finding. An AAC stream whose access units carry parametric
    // stereo states stereo, signaled or not, so a mono one is mono.
    private static void CompareLayouts(State state, SampleEntry entry, int streamsBefore, string label)
    {
        if (entry.Layout is not { } stated || entry.LayoutNode is not { } node)
        {
            return;
        }

        foreach (StreamInfo stream in state.Context.Streams.Skip(streamsBefore).ToList())
        {
            if (stream.ChannelLayout is null && (stream.Channels == 0 || stream.Channels == stated.Count))
            {
                state.Context.UpdateStream(stream, stream with { ChannelLayout = stated, Channels = stated.Count });
                continue;
            }

            IReadOnlyList<Speaker>? coded = stream.ChannelLayout;
            if (coded is not null && ChannelLayouts.HaveSameSpeakers(stated, coded))
            {
                continue;
            }

            string codedText = coded is null ? $"{Counts.Of(stream.Channels, "channel", "channels")}" : ChannelLayouts.Describe(coded);
            state.Findings.AddNow(Severity.Warning, "mp4.channel-layout-differs",
                $"The {entry.LayoutBox} box of {label} states {ChannelLayouts.Describe(stated)}, but the {stream.Codec} stream states {codedText}"
                + "; players that follow the container place the channels differently from the coding. The report gives the stream's channels.",
                node);
        }
    }

    // Ls and Rs are the back pair, unless the layout also has the rear pair Rls and Rrs, which
    // makes them the side pair. Lt and Rt, a matrix-encoded stereo mix, play on the front pair.
    internal static IReadOnlyList<Speaker> FromAbbreviations(string[] channels)
    {
        bool hasRear = channels.Contains("Rls");
        return [.. channels.Select(c => c switch
        {
            "L" or "Lt" => Speaker.FrontLeft,
            "R" or "Rt" => Speaker.FrontRight,
            "C" => Speaker.FrontCenter,
            "LFE" => Speaker.LowFrequency,
            "Ls" => hasRear ? Speaker.SideLeft : Speaker.BackLeft,
            "Rs" => hasRear ? Speaker.SideRight : Speaker.BackRight,
            "Rls" => Speaker.BackLeft,
            "Rrs" => Speaker.BackRight,
            "Cs" => Speaker.BackCenter,
            "Lc" => Speaker.FrontLeftOfCenter,
            "Rc" => Speaker.FrontRightOfCenter,
            "Lsd" => Speaker.SideLeft,
            "Rsd" => Speaker.SideRight,
            "Lw" => Speaker.FrontLeftWide,
            "Rw" => Speaker.FrontRightWide,
            "Ts" => Speaker.TopCenter,
            "Vhl" => Speaker.TopFrontLeft,
            "Vhc" => Speaker.TopFrontCenter,
            "Vhr" => Speaker.TopFrontRight,
            "Ltm" => Speaker.TopSideLeft,
            "Rtm" => Speaker.TopSideRight,
            "Ltr" => Speaker.TopBackLeft,
            "Rtr" => Speaker.TopBackRight,
            _ => Speaker.Discrete,
        })];
    }

    // Core Audio's channel bitmap: bits 0 to 17 in the order of the WAVE channel mask, then the top
    // middle and top rear positions (21, 23, 24, 25 and 26); a channel per set bit, lowest first.
    internal static IReadOnlyList<Speaker> FromBitmap(uint bitmap)
    {
        var speakers = new List<Speaker>();
        for (int bit = 0; bit < 32; bit++)
        {
            if ((bitmap & (1U << bit)) == 0)
            {
                continue;
            }

            speakers.Add(bit switch
            {
                < 18 => (Speaker)(bit + 1),
                21 => Speaker.TopSideLeft,
                23 => Speaker.TopSideRight,
                24 => Speaker.TopBackLeft,
                25 => Speaker.TopBackCenter,
                26 => Speaker.TopBackRight,
                _ => Speaker.Discrete,
            });
        }

        return speakers;
    }

    // Core Audio's channel labels: 1 to 18 follow the WAVE channel mask (CoreAudioBaseTypes.h),
    // where LeftSurround is the back left; with the rear surround labels 33 and 34 it is the side
    // left. Labels without a speaker of their own (unused, discrete, Ambisonic, headphones) are
    // discrete.
    internal static IReadOnlyList<Speaker> FromLabels(List<uint> labels)
    {
        bool hasRear = labels.Contains(33U) || labels.Contains(34U);
        return [.. labels.Select(label => label switch
        {
            5 when hasRear => Speaker.SideLeft,
            6 when hasRear => Speaker.SideRight,
            >= 1 and <= 18 => (Speaker)label,
            33 => Speaker.BackLeft,
            34 => Speaker.BackRight,
            35 => Speaker.FrontLeftWide,
            36 => Speaker.FrontRightWide,
            37 => Speaker.LowFrequency2,
            38 => Speaker.FrontLeft,
            39 => Speaker.FrontRight,
            42 => Speaker.FrontCenter,
            44 => Speaker.BackCenter,
            49 => Speaker.TopSideLeft,
            51 => Speaker.TopSideRight,
            52 => Speaker.TopBackLeft,
            53 => Speaker.TopBackCenter,
            54 => Speaker.TopBackRight,
            _ => Speaker.Discrete,
        })];
    }

    // ISO/IEC 23091-3 positions: the surround pair (4 and 5) is the back pair, unless the rear
    // pair (8 and 9) is also present, which makes it the side pair; positions the report names no
    // speaker for are discrete.
    internal static IReadOnlyList<Speaker> FromPositions(IReadOnlyList<int> positions)
    {
        bool hasRear = positions.Contains(8) || positions.Contains(9);
        return [.. positions.Select(p => p switch
        {
            0 => Speaker.FrontLeft,
            1 => Speaker.FrontRight,
            2 => Speaker.FrontCenter,
            3 => Speaker.LowFrequency,
            4 => hasRear ? Speaker.SideLeft : Speaker.BackLeft,
            5 => hasRear ? Speaker.SideRight : Speaker.BackRight,
            6 => Speaker.FrontLeftOfCenter,
            7 => Speaker.FrontRightOfCenter,
            8 => Speaker.BackLeft,
            9 => Speaker.BackRight,
            10 => Speaker.BackCenter,
            11 or 13 => Speaker.SideLeft,
            12 or 14 => Speaker.SideRight,
            15 => Speaker.FrontLeftWide,
            16 => Speaker.FrontRightWide,
            17 => Speaker.TopFrontLeft,
            18 => Speaker.TopFrontRight,
            19 => Speaker.TopFrontCenter,
            20 => Speaker.TopBackLeft,
            21 => Speaker.TopBackRight,
            22 => Speaker.TopBackCenter,
            23 => Speaker.TopSideLeft,
            24 => Speaker.TopSideRight,
            25 => Speaker.TopCenter,
            26 => Speaker.LowFrequency2,
            27 => Speaker.BottomFrontLeft,
            28 => Speaker.BottomFrontRight,
            29 => Speaker.BottomFrontCenter,
            _ => Speaker.Discrete,
        })];
    }

    private static NodeValue TagText(uint tag)
    {
        string hex = $"0x{tag:X8}";
        return tag switch
        {
            UseChannelDescriptions => $"UseChannelDescriptions ({hex})",
            UseChannelBitmap => $"UseChannelBitmap ({hex})",
            _ when (tag >> 16) == 147 => $"DiscreteInOrder, {Counts.Of(tag & 0xFFFF, "channel", "channels")} ({hex})",
            _ when (tag >> 16) == 0xFFFF => $"Unknown, {Counts.Of(tag & 0xFFFF, "channel", "channels")} ({hex})",
            _ when LayoutTags.TryGetValue(tag >> 16, out (string Name, string Channels) known) => $"{known.Name}: {known.Channels.Replace("-", "discrete", StringComparison.Ordinal)} ({hex})",
            _ => $"not known ({hex})",
        };
    }

    private static NodeValue LabelText(uint label)
    {
        string name = label switch
        {
            0 => "Unused",
            1 => "Left",
            2 => "Right",
            3 => "Center",
            4 => "LFEScreen",
            5 => "LeftSurround",
            6 => "RightSurround",
            7 => "LeftCenter",
            8 => "RightCenter",
            9 => "CenterSurround",
            10 => "LeftSurroundDirect",
            11 => "RightSurroundDirect",
            12 => "TopCenterSurround",
            13 => "VerticalHeightLeft",
            14 => "VerticalHeightCenter",
            15 => "VerticalHeightRight",
            16 => "TopBackLeft",
            17 => "TopBackCenter",
            18 => "TopBackRight",
            33 => "RearSurroundLeft",
            34 => "RearSurroundRight",
            35 => "LeftWide",
            36 => "RightWide",
            37 => "LFE2",
            38 => "LeftTotal",
            39 => "RightTotal",
            40 => "HearingImpaired",
            41 => "Narration",
            42 => "Mono",
            43 => "DialogCentricMix",
            44 => "CenterSurroundDirect",
            45 => "Haptic",
            49 => "LeftTopMiddle",
            51 => "RightTopMiddle",
            52 => "LeftTopRear",
            53 => "CenterTopRear",
            54 => "RightTopRear",
            100 => "UseCoordinates",
            >= 200 and <= 209 => "Ambisonic, mid/side, X-Y or binaural",
            301 => "HeadphonesLeft",
            302 => "HeadphonesRight",
            304 => "ClickTrack",
            305 => "ForeignLanguage",
            400 => "Discrete",
            >= 0x10000 and < 0x20000 => $"Discrete_{label & 0xFFFF}",
            >= 0x20000 and < 0x30000 => $"HOA_ACN_{label & 0xFFFF}",
            0xFFFFFFFF => "Unknown",
            _ => "not known",
        };
        return $"{name} ({label.ToString(CultureInfo.InvariantCulture)})";
    }

    // Three big-endian 32-bit floats: left/right, back/front and down/up, or azimuth, elevation and
    // distance (Core Audio's AudioChannelDescription).
    private static string CoordinatesText(byte[] bytes) =>
        string.Join(", ", Enumerable.Range(0, 3).Select(i => BinaryPrimitives.ReadSingleBigEndian(bytes.AsSpan(i * 4, 4)).ToString("G6", CultureInfo.InvariantCulture)));

    private static NodeValue DefinedText(int value) => value switch
    {
        0 => "0 (a speaker position per channel)",
        _ when value < Configurations.Length && Configurations[value] is { } configuration => $"{value} ({ChannelLayouts.Describe(FromPositions(configuration))})",
        _ => $"{value} (not known)",
    };

    private static NodeValue PositionText(int value) => value switch
    {
        < 43 => $"{value} ({Positions[value]})",
        126 => $"{value} (explicit position)",
        127 => $"{value} (unknown)",
        _ => $"{value} (reserved)",
    };
}
