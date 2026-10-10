namespace Encspect.Formats.Mpeg;

// The 32-bit MPEG audio frame header (ISO/IEC 11172-3 section 2.4.1.3, ISO/IEC 13818-3 for the
// lower sample rates, and the Fraunhofer MPEG 2.5 extension):
// AAAAAAAA AAABBCCD EEEEFFGH IIJJKLMM, sync, version, layer, protection, bitrate index, sample
// rate index, padding, private, channel mode, mode extension, copyright, original, emphasis.
internal readonly record struct MpegHeader(uint Raw)
{
    private static readonly int[][] SampleRates =
    [
        [11025, 12000, 8000], // version bits 00: MPEG 2.5
        [],
        [22050, 24000, 16000], // 10: MPEG 2
        [44100, 48000, 32000], // 11: MPEG 1
    ];

    private static readonly int[][] Mpeg1Bitrates =
    [
        [],
        [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448],
        [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384],
        [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320],
    ];

    private static readonly int[][] Mpeg2Bitrates =
    [
        [],
        [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256],
        [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],
        [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],
    ];

    private static readonly string[] ChannelModes = ["Stereo", "Joint stereo", "Dual channel", "Mono"];
    private static readonly string[] Emphases = ["None", "50/15 µs", "Reserved", "CCITT J.17"];

    public int VersionBits => (int)(Raw >> 19) & 3;

    public int LayerBits => (int)(Raw >> 17) & 3;

    public bool IsProtected => ((Raw >> 16) & 1) == 0;

    public int BitrateIndex => (int)(Raw >> 12) & 15;

    public int SampleRateIndex => (int)(Raw >> 10) & 3;

    public int Padding => (int)(Raw >> 9) & 1;

    public bool IsPrivate => ((Raw >> 8) & 1) != 0;

    public int ChannelModeBits => (int)(Raw >> 6) & 3;

    public int ModeExtension => (int)(Raw >> 4) & 3;

    public bool IsCopyrighted => ((Raw >> 3) & 1) != 0;

    public bool IsOriginal => ((Raw >> 2) & 1) != 0;

    public int EmphasisBits => (int)Raw & 3;

    // Version 01, layer 00, bitrate index 1111 and sample rate index 11 are reserved.
    public bool IsValid =>
        (Raw & 0xFFE00000) == 0xFFE00000 && VersionBits != 1 && LayerBits != 0 && BitrateIndex != 15 && SampleRateIndex != 3;

    public int Layer => 4 - LayerBits;

    public bool IsMpeg1 => VersionBits == 3;

    public bool IsFreeFormat => BitrateIndex == 0;

    public bool IsMono => ChannelModeBits == 3;

    public int SampleRate => SampleRates[VersionBits][SampleRateIndex];

    public int Bitrate => (IsMpeg1 ? Mpeg1Bitrates : Mpeg2Bitrates)[Layer][BitrateIndex];

    public int SlotSize => Layer == 1 ? 4 : 1;

    // Layer III in MPEG 2 and 2.5 codes one granule per frame instead of two.
    public int Samples => Layer == 1 ? 384 : Layer == 3 && !IsMpeg1 ? 576 : 1152;

    public string VersionName => VersionBits switch
    {
        3 => "MPEG-1",
        2 => "MPEG-2",
        _ => "MPEG-2.5",
    };

    public string LayerName => Layer switch
    {
        1 => "I",
        2 => "II",
        _ => "III",
    };

    public string ChannelMode => ChannelModes[ChannelModeBits];

    public string Emphasis => Emphases[EmphasisBits];

    public string BitrateName => IsFreeFormat ? "free" : $"{Bitrate} kbps";

    public string ModeExtensionName
    {
        get
        {
            if (ChannelModeBits != 1)
            {
                return "Not used";
            }

            // Layer III: bit 1 MS stereo, bit 0 intensity stereo. Layers I and II: the subband
            // where intensity stereo starts.
            return Layer == 3
                ? ModeExtension switch
                {
                    0 => "None",
                    1 => "Intensity stereo",
                    2 => "MS stereo",
                    _ => "MS and intensity stereo",
                }
                : $"Bands {(ModeExtension + 1) * 4} to 31";
        }
    }

    public string Summary => $"{VersionName} Layer {LayerName}, {BitrateName}, {SampleRate} Hz, {ChannelMode}";

    public string StreamName => $"{VersionName} Layer {LayerName}, {SampleRate} Hz";

    // Frames of one stream share version, layer, sample rate and whether they are free format.
    public bool IsSameStream(MpegHeader other) =>
        VersionBits == other.VersionBits && LayerBits == other.LayerBits && SampleRateIndex == other.SampleRateIndex && IsFreeFormat == other.IsFreeFormat;

    // Frame length in bytes; a free-format frame's unpadded length comes from the stream.
    public int FrameLength(int freeLength = 0)
    {
        if (IsFreeFormat)
        {
            return freeLength + (Padding * SlotSize);
        }

        if (Layer == 1)
        {
            return ((12 * Bitrate * 1000 / SampleRate) + Padding) * 4;
        }

        int coefficient = Layer == 3 && !IsMpeg1 ? 72 : 144;
        return (coefficient * Bitrate * 1000 / SampleRate) + Padding;
    }

    // Bytes after the CRC that the CRC covers, or -1 when not known here. Layer III: the side
    // information (ISO/IEC 11172-3 2.4.1.7, 13818-3 2.4.1.7). Layer I: the bit allocation, 4 bits
    // per subband and channel up to the intensity stereo bound and per subband after it
    // (11172-3 2.4.2.3). Layer II needs the allocation tables and is not covered yet.
    public int CrcCoverage
    {
        get
        {
            if (Layer == 3)
            {
                return IsMpeg1 ? (IsMono ? 17 : 32) : (IsMono ? 9 : 17);
            }

            if (Layer == 1)
            {
                if (IsMono)
                {
                    return 16;
                }

                int bound = ChannelModeBits == 1 ? (ModeExtension + 1) * 4 : 32;
                return ((bound * 2) + (32 - bound)) * 4 / 8;
            }

            return -1;
        }
    }

    // ISO/IEC 11172-3 table 3-B.2: Layer II bitrates that are only allowed with or without a
    // single channel.
    public bool IsLayer2BitrateModeAllowed
    {
        get
        {
            if (!IsMpeg1 || Layer != 2 || IsFreeFormat)
            {
                return true;
            }

            return Bitrate switch
            {
                32 or 48 or 56 or 80 => IsMono,
                224 or 256 or 320 or 384 => !IsMono,
                _ => true,
            };
        }
    }
}
