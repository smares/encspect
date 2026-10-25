using System.Buffers.Binary;
using System.Globalization;

namespace Encspect.Core;

// Core Audio's way of naming the speakers of a stream (CoreAudioBaseTypes.h, AudioChannelLayout; Apple's Core Audio Format Specification, "Channel
// Layout Chunk"): a layout tag, a channel bitmap, or a label for each channel. QuickTime's chan atom and the CHAN chunk of AIFF files hold the same
// structure, so the tables are shared by the parsers of the containers that hold it.
public static class CoreAudioLayouts
{
    // kAudioChannelLayoutTag_UseChannelDescriptions and _UseChannelBitmap.
    public const uint UseChannelDescriptions = 0;

    public const uint UseChannelBitmap = 1U << 16;

    // The layout tag of a number of channels in the order they are in the file (kAudioChannelLayoutTag_DiscreteInOrder, high 16 bits 147) and the
    // tag of an unknown layout (0xFFFF).
    public const uint DiscreteInOrder = 147;

    public const uint UnknownLayout = 0xFFFF;

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


    public static bool TryGetTag(uint tag, out (string Name, string Channels) layout) => LayoutTags.TryGetValue(tag >> 16, out layout);

    // The channels a tag or bitmap or list of labels gives, or null where the layout is not one the tables know or the channels it names do not
    // number what the tag says (the low 16 bits of a layout tag).
    public static IReadOnlyList<Speaker>? Of(uint tag, uint bitmap, IReadOnlyList<uint>? labels) => tag switch
    {
        UseChannelDescriptions => labels is { Count: > 0 } ? FromLabels(labels) : null,
        UseChannelBitmap => FromBitmap(bitmap),
        _ when (tag >> 16) is DiscreteInOrder or UnknownLayout => ChannelLayouts.Discrete((int)(tag & 0xFFFF)),
        _ when LayoutTags.TryGetValue(tag >> 16, out (string Name, string Channels) known) && known.Channels.Split(' ').Length == (tag & 0xFFFF) =>
            FromAbbreviations(known.Channels.Split(' ')),
        _ => null,
    };

    // The number of channels a tag, bitmap or count of channel descriptions states: the descriptions' count for UseChannelDescriptions, the set bits of
    // the bitmap for UseChannelBitmap, the low 16 bits of any other tag.
    public static long Channels(uint tag, uint bitmap, uint descriptions) => tag switch
    {
        UseChannelDescriptions => descriptions,
        UseChannelBitmap => System.Numerics.BitOperations.PopCount(bitmap),
        _ => tag & 0xFFFF,
    };

    // Ls and Rs are the back pair, unless the layout also has the rear pair Rls and Rrs, which
    // makes them the side pair. Lt and Rt, a matrix-encoded stereo mix, play on the front pair.
    public static IReadOnlyList<Speaker> FromAbbreviations(string[] channels)
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
    public static IReadOnlyList<Speaker> FromBitmap(uint bitmap)
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
    public static IReadOnlyList<Speaker> FromLabels(IReadOnlyList<uint> labels)
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

    // The tag in the words a report shows: its name and channels in Apple's abbreviations, with the value.
    public static string TagText(uint tag)
    {
        string hex = $"0x{tag:X8}";
        return tag switch
        {
            UseChannelDescriptions => $"UseChannelDescriptions ({hex})",
            UseChannelBitmap => $"UseChannelBitmap ({hex})",
            _ when (tag >> 16) == DiscreteInOrder => $"DiscreteInOrder, {Counts.Of(tag & 0xFFFF, "channel", "channels")} ({hex})",
            _ when (tag >> 16) == UnknownLayout => $"Unknown, {Counts.Of(tag & 0xFFFF, "channel", "channels")} ({hex})",
            _ when LayoutTags.TryGetValue(tag >> 16, out (string Name, string Channels) known) => $"{known.Name}: {known.Channels.Replace("-", "discrete", StringComparison.Ordinal)} ({hex})",
            _ => $"not known ({hex})",
        };
    }

    public static string LabelText(uint label)
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
    public static string CoordinatesText(byte[] bytes) =>
        string.Join(", ", Enumerable.Range(0, 3).Select(i => BinaryPrimitives.ReadSingleBigEndian(bytes.AsSpan(i * 4, 4)).ToString("G6", CultureInfo.InvariantCulture)));
}
