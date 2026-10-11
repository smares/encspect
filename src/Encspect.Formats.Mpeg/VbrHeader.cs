using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Mpeg;

// The header an encoder writes into the first frame of a Layer III stream: Xing or Info with the
// LAME tag after it, or Fraunhofer's VBRI. The frame is a valid frame with zero side information,
// so decoders that do not know it play a frame of silence.
//
// Xing and Info start where the side information would end without a CRC (LAME 3.100
// libmp3lame/VbrTag.c, GetVbrTag and lame_get_lametag_frame): a flags word, then the frame count,
// byte count, 100-byte TOC and quality its flags announce. The LAME tag follows the fields
// (Mp3 Info Tag revision 1): 36 bytes, from a 9-byte encoder string to a CRC-16 of every frame
// byte before that CRC. LAME before 3.90 wrote only a version string there. VBRI starts 36 bytes
// into the frame (ffmpeg 6.1 libavformat/mp3dec.c): version, delay, quality, byte and frame
// counts, and a TOC of byte counts.
//
// The header counts only when every byte between the frame header, or its CRC, and the header is
// zero, as mpg123 requires, so audio data that happens to hold the magic is not taken for it.
internal sealed class VbrHeader
{
    public const int LameTagSize = 36;
    private const int VbriOffset = 36;
    private const int VbriFixedSize = 26;
    private const uint GogoQuality = 0x474F474F;

    private static readonly string[] VbrMethods = ["Unknown", "CBR", "ABR", "VBR method 1 (rh)", "VBR method 2 (mtrh)", "VBR method 3 (mt)", "VBR method 4",
        "Reserved (7)", "CBR, two-pass", "ABR, two-pass"];

    private static readonly string[] GainNames = ["Not set", "Radio", "Audiophile"];

    private static readonly string[] GainOriginators = ["Not set", "Set by artist", "Set by user", "Determined automatically", "Simple RMS average"];

    private static readonly string[] StereoModes = ["Mono", "Stereo", "Dual channel", "Joint stereo", "Forced MS stereo", "Auto", "Intensity stereo", "Undefined"];

    private static readonly string[] SourceRates = ["32 kHz or lower", "44.1 kHz", "48 kHz", "Higher than 48 kHz"];

    private static readonly string[] Surround = ["None", "DPL", "DPL2", "Ambisonic"];

    private static readonly Dictionary<int, string> NamedPresets = new()
    {
        [1000] = "R3mix",
        [1001] = "Standard",
        [1002] = "Extreme",
        [1003] = "Insane",
        [1004] = "Standard fast",
        [1005] = "Extreme fast",
        [1006] = "Medium",
        [1007] = "Medium fast",
    };

    private static readonly SearchValues<byte> Printable = SearchValues.Create([0, .. Enumerable.Range(32, 95).Select(b => (byte)b)]);

    private readonly byte[] _frame;

    private VbrHeader(byte[] frame, int offset, string type)
    {
        _frame = frame;
        Offset = offset;
        Type = type;
    }

    // Xing, Info or VBRI.
    public string Type { get; }

    public bool IsVbri => Type == "VBRI";

    // Where the header starts, from the start of the frame.
    public int Offset { get; }

    // Where the header ends, cut at the frame's end.
    public int End { get; private set; }

    public bool IsTruncated { get; private set; }

    // The VBRI version, of which only 1 is known; ffmpeg ignores the header otherwise.
    public int? VbriVersion { get; private set; }

    // A VBRI TOC entry size other than the 1 to 4 bytes Fraunhofer's reader supports, with entries.
    public int? InvalidEntrySize { get; private set; }

    public uint? Flags { get; private set; }

    public uint? FrameCount { get; private set; }

    public uint? ByteCount { get; private set; }

    public int TocOffset { get; private set; } = -1;

    public int TocLength { get; private set; }

    public uint? Quality { get; private set; }

    // Gogo-no-coda writes "GOGO" where the quality belongs (as mp3guessenc reads it, tags.c).
    public bool HasGogoSignature => Quality == GogoQuality;

    public bool IsTocDecreasing { get; private set; }

    // Pre-3.90 LAME version string, without the tag fields.
    public string? EncoderString { get; private set; }

    public int LameOffset { get; private set; } = -1;

    public bool HasLameTag => LameOffset >= 0;

    public string LameEncoder { get; private set; } = string.Empty;

    public int StoredTagCrc { get; private set; }

    public int ComputedTagCrc { get; private set; }

    // The tag CRC as ffmpeg 6.1 writes it: over the frame's first 190 bytes, with the CRC field
    // zero, wherever the tag starts. Only in an MPEG-1 stereo frame are those the bytes before it.
    public int FfmpegTagCrc { get; private set; }

    public bool IsTagCrcValid => StoredTagCrc == ComputedTagCrc;

    public uint MusicLength { get; private set; }

    public int StoredMusicCrc { get; private set; }

    public int EncoderDelay { get; private set; }

    public int EncoderPadding { get; private set; }

    // The previous file of a gapless (--nogap) sequence continues into this one.
    public bool IsNoGapContinuation { get; private set; }

    public static int XingOffset(MpegHeader header) => header.IsMpeg1 ? (header.IsMono ? 21 : 36) : (header.IsMono ? 13 : 21);

    // frame holds the frame's bytes, as far as the file has them.
    public static VbrHeader? TryRead(byte[] frame, MpegHeader header)
    {
        if (header.Layer != 3)
        {
            return null;
        }

        int dataStart = header.IsProtected ? 6 : 4;
        if (Matches(frame, VbriOffset, "VBRI") && IsZero(frame, dataStart, VbriOffset))
        {
            var vbri = new VbrHeader(frame, VbriOffset, "VBRI");
            vbri.ReadVbri();
            return vbri;
        }

        int offset = XingOffset(header);
        foreach (string type in new[] { "Xing", "Info" })
        {
            if (Matches(frame, offset, type) && IsZero(frame, dataStart, offset))
            {
                var xing = new VbrHeader(frame, offset, type);
                xing.ReadXing();
                return xing;
            }
        }

        return null;
    }

    // LAME 3.100 CRC_update_lookup: CRC-16, polynomial 0x8005 reflected, initial value 0.
    public static int LameCrc(ReadOnlySpan<byte> data, int crc = 0)
    {
        foreach (byte b in data)
        {
            crc = (crc >> 8) ^ LameCrcTable[(crc ^ b) & 0xFF];
        }

        return crc;
    }

    // The CRC of a range from running CRCs: with init 0 the CRC is linear, so after n more bytes
    // a running CRC c becomes Shift(c, n) XOR the CRC of those bytes alone, as in zlib's
    // crc32_combine. Shift applies the operator of one zero byte n times by squaring.
    public static int LameCrcShift(int crc, long bytes)
    {
        for (int bit = 0; bytes != 0; bit++, bytes >>= 1)
        {
            if ((bytes & 1) != 0)
            {
                crc = Apply(ZeroBytePowers.Value[bit], crc);
            }
        }

        return crc;
    }

    // ZeroBytePowers[k] is the operator of 2^k zero bytes, as the images of the 16 unit vectors;
    // built on first use, after the CRC table it is made from.
    private static readonly Lazy<int[][]> ZeroBytePowers = new(BuildZeroBytePowers);

    private static int[][] BuildZeroBytePowers()
    {
        var powers = new int[63][];
        powers[0] = Enumerable.Range(0, 16).Select(bit => LameCrc([0], 1 << bit)).ToArray();
        for (int k = 1; k < powers.Length; k++)
        {
            int[] half = powers[k - 1];
            powers[k] = half.Select(column => Apply(half, column)).ToArray();
        }

        return powers;
    }

    private static int Apply(int[] matrix, int vector)
    {
        int result = 0;
        for (int bit = 0; vector != 0; bit++, vector >>= 1)
        {
            if ((vector & 1) != 0)
            {
                result ^= matrix[bit];
            }
        }

        return result;
    }

    private static readonly int[] LameCrcTable = Enumerable.Range(0, 256).Select(value =>
    {
        for (int bit = 0; bit < 8; bit++)
        {
            value = (value & 1) != 0 ? (value >> 1) ^ 0xA001 : value >> 1;
        }

        return value;
    }).ToArray();

    private static bool Matches(byte[] frame, int offset, string magic) =>
        offset + 4 <= frame.Length && Encoding.ASCII.GetString(frame, offset, 4) == magic;

    private static bool IsZero(byte[] frame, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            if (frame[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private bool Fits(int position, int size)
    {
        if (position + size <= _frame.Length)
        {
            return true;
        }

        IsTruncated = true;
        return false;
    }

    private uint UInt32At(int position) => BinaryPrimitives.ReadUInt32BigEndian(_frame.AsSpan(position));

    private int UInt16At(int position) => BinaryPrimitives.ReadUInt16BigEndian(_frame.AsSpan(position));

    private void ReadXing()
    {
        int position = Offset + 4;
        End = Math.Min(position, _frame.Length);
        if (!Fits(position, 4))
        {
            End = _frame.Length;
            return;
        }

        Flags = UInt32At(position);
        position += 4;
        End = position;
        if ((Flags & 1) != 0)
        {
            FrameCount = Fits(position, 4) ? UInt32At(position) : null;
            position += 4;
        }

        if ((Flags & 2) != 0 && !IsTruncated)
        {
            ByteCount = Fits(position, 4) ? UInt32At(position) : null;
            position += 4;
        }

        if ((Flags & 4) != 0 && !IsTruncated)
        {
            TocOffset = position;
            TocLength = Math.Min(100, _frame.Length - position);
            Fits(position, 100);
            for (int i = 1; i < TocLength; i++)
            {
                IsTocDecreasing |= _frame[position + i] < _frame[position + i - 1];
            }

            position += 100;
        }

        if ((Flags & 8) != 0 && !IsTruncated)
        {
            Quality = Fits(position, 4) ? UInt32At(position) : null;
            position += 4;
        }

        End = Math.Min(position, _frame.Length);
        if (IsTruncated || position + 9 > _frame.Length || !IsEncoderString(_frame.AsSpan(position, 9)))
        {
            return;
        }

        string text = Encoding.ASCII.GetString(_frame, position, 9).TrimEnd('\0');
        if (IsOldLame(text))
        {
            int end = position;
            while (end < _frame.Length && end - position < 20 && _frame[end] is >= 32 and < 127)
            {
                end++;
            }

            EncoderString = Encoding.ASCII.GetString(_frame, position, end - position);
            End = end;
        }
        else if (position + LameTagSize <= _frame.Length)
        {
            ReadLame(position, text);
        }
    }

    // Printable ASCII, padded with zero bytes, not empty.
    private static bool IsEncoderString(ReadOnlySpan<byte> data) =>
        data[0] != 0 && data.IndexOfAnyExcept(Printable) < 0;

    // "LAME3.xx" before 3.90; "LAME3.100" and later continue with a third digit.
    private static bool IsOldLame(string text) =>
        text.StartsWith("LAME3.", StringComparison.Ordinal) && text.Length >= 8 && char.IsAsciiDigit(text[6]) && char.IsAsciiDigit(text[7])
        && text[6] < '9' && (text.Length == 8 || !char.IsAsciiDigit(text[8]));

    // LAME before 3.95 computed ReplayGain against an 83 dB reference (mpg123, parse.c, and LAME's
    // ChangeLog of 2004-01-13: "ReplayGain ->89dB").
    private bool HasOldGainReference =>
        LameEncoder.StartsWith("LAME3.", StringComparison.Ordinal) && LameEncoder.Length >= 8 && char.IsAsciiDigit(LameEncoder[6])
        && char.IsAsciiDigit(LameEncoder[7]) && (LameEncoder.Length == 8 || !char.IsAsciiDigit(LameEncoder[8]))
        && int.Parse(LameEncoder.AsSpan(6, 2), CultureInfo.InvariantCulture) < 95;

    private void ReadLame(int position, string encoder)
    {
        LameOffset = position;
        LameEncoder = encoder;
        End = position + LameTagSize;
        IsNoGapContinuation = (_frame[position + 19] & 0x80) != 0;
        EncoderDelay = (_frame[position + 21] << 4) | (_frame[position + 22] >> 4);
        EncoderPadding = ((_frame[position + 22] & 15) << 8) | _frame[position + 23];
        MusicLength = UInt32At(position + 28);
        StoredMusicCrc = UInt16At(position + 32);
        StoredTagCrc = UInt16At(position + 34);
        ComputedTagCrc = LameCrc(_frame.AsSpan(0, position + 34));
        if (_frame.Length >= 190)
        {
            byte[] first = _frame[..190];
            if (position + 34 < 190)
            {
                first[position + 34] = 0;
                first[Math.Min(position + 35, 189)] = 0;
            }

            FfmpegTagCrc = LameCrc(first);
        }
        else
        {
            FfmpegTagCrc = -1;
        }
    }

    private void ReadVbri()
    {
        int position = Offset + 4;
        if (!Fits(position, VbriFixedSize - 4))
        {
            End = _frame.Length;
            return;
        }

        FrameCount = UInt32At(position + 10);
        ByteCount = UInt32At(position + 6);
        int entries = UInt16At(position + 14);
        int entrySize = UInt16At(position + 18);
        TocOffset = Offset + VbriFixedSize;
        int length = entrySize is >= 1 and <= 4 ? entries * entrySize : 0;
        VbriVersion = UInt16At(position);
        if (entrySize is < 1 or > 4 && entries > 0)
        {
            InvalidEntrySize = entrySize;
        }

        Fits(TocOffset, length);
        TocLength = Math.Min(length, _frame.Length - TocOffset);
        End = TocOffset + TocLength;
    }

    // The frame's nodes after its header and CRC: the zero side information before the header,
    // the header and the LAME tag, and what follows them up to the frame's end.
    public List<Node> ToNodes(long frameOffset, int dataStart, int? musicCrc)
    {
        var nodes = new List<Node>();
        if (Offset > dataStart)
        {
            nodes.Add(new Node("Side Info", frameOffset + dataStart, Offset - dataStart));
        }

        nodes.Add(IsVbri ? VbriNode(frameOffset) : XingNode(frameOffset));
        if (EncoderString is not null)
        {
            int start = End - EncoderString.Length;
            nodes.Add(new Node("Encoder String", frameOffset + start, EncoderString.Length, EncoderString));
        }

        if (HasLameTag)
        {
            nodes.Add(LameNode(frameOffset, musicCrc));
        }

        if (_frame.Length > End)
        {
            nodes.Add(new Node("Unused", frameOffset + End, _frame.Length - End, $"{_frame.Length - End} bytes"));
        }

        return nodes;
    }

    private Node XingNode(long frameOffset)
    {
        int xingEnd = HasLameTag ? LameOffset : EncoderString is not null ? End - EncoderString.Length : End;
        Node node = Field("Xing", Offset, xingEnd - Offset, Type);
        if (Flags is not { } flags)
        {
            return node;
        }

        int position = Offset + 4;
        Node flagsNode = node.Add(Field("Flags", position, 4, $"0x{flags:X8}"));
        flagsNode.Add(Field("HasFrameCount", position, 4, Bool((flags & 1) != 0)));
        flagsNode.Add(Field("HasByteCount", position, 4, Bool((flags & 2) != 0)));
        flagsNode.Add(Field("HasToc", position, 4, Bool((flags & 4) != 0)));
        flagsNode.Add(Field("HasQuality", position, 4, Bool((flags & 8) != 0)));
        position += 4;
        if (FrameCount is { } frames)
        {
            node.Add(Field("Frame Count", position, 4, Number(frames)));
        }

        position += (flags & 1) != 0 ? 4 : 0;
        if (ByteCount is { } bytes)
        {
            node.Add(Field("Byte Count", position, 4, Number(bytes)));
        }

        if (TocOffset >= 0)
        {
            Node toc = node.Add(Field("TOC", TocOffset, TocLength, $"{TocLength} entries"));
            for (int i = 0; i < TocLength; i++)
            {
                toc.Add(Field($"Entry {i}", TocOffset + i, 1, Number(_frame[TocOffset + i])));
            }
        }

        if (Quality is { } quality)
        {
            node.Add(Field("Quality", xingEnd - 4, 4, HasGogoSignature ? "\"GOGO\", which Gogo writes instead of a quality" : Number(quality)));
        }

        return node;

        Node Field(string name, int offset, int length, string? value) => new(name, frameOffset + offset, length, value);
    }

    private Node LameNode(long frameOffset, int? musicCrc)
    {
        int p = LameOffset;
        byte[] b = _frame;
        Node node = Field("LAME Tag", 0, LameTagSize, LameEncoder);
        node.Add(Field("Encoder", 0, 9, LameEncoder));
        int method = b[p + 9] & 15;
        node.Add(Field("Revision", 9, 1, Number(b[p + 9] >> 4)));
        node.Add(Field("VBR Method", 9, 1, method < VbrMethods.Length ? VbrMethods[method] : $"Reserved ({Number(method)})"));
        node.Add(Field("Lowpass", 10, 1, b[p + 10] == 0 ? "Unknown" : $"{Number(b[p + 10] * 100)} Hz"));
        uint peak = UInt32At(p + 11);
        // LAME 3.94 and later store the peak as a fixed-point fraction of full scale, 1.0 = 2^23.
        node.Add(Field("Peak", 11, 4, peak == 0 ? "Unknown" : (peak / (double)(1 << 23)).ToString("F6", CultureInfo.InvariantCulture)));
        AddGain("Radio Replay Gain", 15);
        AddGain("Audiophile Replay Gain", 17);

        byte flags = b[p + 19];
        Node encodingFlags = node.Add(Field("Encoding Flags", 19, 1, null));
        encodingFlags.Add(Field("UsesNsPsytune", 19, 1, Bool((flags & 0x10) != 0)));
        encodingFlags.Add(Field("UsesNsSafeJoint", 19, 1, Bool((flags & 0x20) != 0)));
        encodingFlags.Add(Field("IsNoGapContinued", 19, 1, Bool((flags & 0x40) != 0)));
        encodingFlags.Add(Field("IsNoGapContinuation", 19, 1, Bool((flags & 0x80) != 0)));
        node.Add(Field("ATH Type", 19, 1, Number(flags & 15)));

        // ABR stores its target, CBR its bitrate, VBR its minimum (VbrTag.c, PutLameVBR).
        string bitrateName = method is 2 or 9 ? "Average Bitrate" : method is >= 3 and <= 6 ? "Minimum Bitrate" : "Bitrate";
        byte bitrate = b[p + 20];
        node.Add(Field(bitrateName, 20, 1, bitrate switch
        {
            0 => "Unknown",
            255 => "255 kbps or more",
            _ => $"{Number(bitrate)} kbps",
        }));

        node.Add(Field("Encoder Delay", 21, 2, $"{Number((b[p + 21] << 4) | (b[p + 22] >> 4))} samples"));
        node.Add(Field("Encoder Padding", 22, 2, $"{Number(((b[p + 22] & 15) << 8) | b[p + 23])} samples"));

        byte misc = b[p + 24];
        node.Add(Field("Noise Shaping", 24, 1, Number(misc & 3)));
        node.Add(Field("Stereo Mode", 24, 1, StereoModes[(misc >> 2) & 7]));
        node.Add(Field("HasUnwiseSettings", 24, 1, Bool((misc & 0x20) != 0)));
        node.Add(Field("Source Sample Rate", 24, 1, SourceRates[misc >> 6]));

        // A signed number of 1.5 dB steps.
        int gain = (sbyte)b[p + 25];
        int tenths = Math.Abs(gain) * 15;
        node.Add(Field("MP3 Gain", 25, 1, $"{(gain < 0 ? "-" : "+")}{Number(tenths / 10)}.{Number(tenths % 10)} dB"));

        int presetSurround = UInt16At(p + 26);
        int surround = (presetSurround >> 11) & 7;
        node.Add(Field("Surround", 26, 2, surround < Surround.Length ? Surround[surround] : $"Reserved ({Number(surround)})"));
        Node preset = node.Add(Field("Preset", 26, 2, PresetName(presetSurround & 0x7FF)));
        if ((presetSurround & 0x7FF) == 0 && AltPreset(method, b[p + 10], flags, bitrate) is { } altPreset)
        {
            preset.Add(Field("Settings Match", 26, 2,
                $"{altPreset}; LAME 3.90 to 3.92 leave the preset out, but its VBR method, lowpass, ATH type and nspsytune flags are the ones this tag holds"));
        }

        node.Add(Field("Music Length", 28, 4, $"{Number(MusicLength)} bytes"));
        Node musicCrcNode = node.Add(Field("Music CRC", 32, 2, $"0x{StoredMusicCrc:X4}"));
        if (musicCrc is { } computed)
        {
            musicCrcNode.Add(Field("Computed", 32, 2, $"0x{computed:X4}"));
        }

        Node tagCrc = node.Add(Field("Tag CRC", 34, 2, $"0x{StoredTagCrc:X4}"));
        tagCrc.Add(Field("Computed", 34, 2, $"0x{ComputedTagCrc:X4}"));
        return node;

        // A ReplayGain field: 3 bits name, 3 bits originator, a sign bit and 9 bits of tenths of a dB.
        void AddGain(string name, int offset)
        {
            int value = UInt16At(p + offset);
            if (value == 0)
            {
                node.Add(Field(name, offset, 2, "Not set"));
                return;
            }

            int adjustment = value & 0x1FF;
            string sign = (value & 0x200) != 0 && adjustment != 0 ? "-" : "+";
            Node gain = node.Add(Field(name, offset, 2, $"{sign}{Number(adjustment / 10)}.{Number(adjustment % 10)} dB"));
            int gainName = value >> 13;
            int originator = (value >> 10) & 7;
            gain.Add(Field("Name", offset, 2, gainName < GainNames.Length ? GainNames[gainName] : $"Reserved ({Number(gainName)})"));
            gain.Add(Field("Originator", offset, 2, originator < GainOriginators.Length ? GainOriginators[originator] : $"Reserved ({Number(originator)})"));
            if (originator == 3 && HasOldGainReference)
            {
                gain.Add(Field("Reference Level", offset, 2, "83 dB, as LAME before 3.95 computed it; later versions use 89 dB, so this gain is 6 dB lower than theirs"));
            }
        }

        Node Field(string name, int offset, int length, string? value) => new(name, frameOffset + p + offset, length, value);
    }

    // The --alt-preset whose settings LAME 3.90 to 3.92 wrote, where the tag holds no preset (3.93
    // added it). Each preset sets nspsytune; the VBR ones and insane also nssafejoint, a fixed
    // lowpass (whatever the sample rate) and their ATH type, the ABR and CBR ones ATH type 2,
    // which nspsytune alone does not set. Checked on encodes of the 3.90, 3.91 and 3.92 releases;
    // 3.90.2 and 3.90.3, HydrogenAudio's releases of the 3.90 line, are not covered.
    private string? AltPreset(int method, byte lowpass, byte flags, byte bitrate)
    {
        if (LameEncoder.TrimEnd(' ') is not ("LAME3.90" or "LAME3.91" or "LAME3.92") || (flags & 0x10) == 0)
        {
            return null;
        }

        bool safeJoint = (flags & 0x20) != 0;
        int ath = flags & 15;
        return (method, lowpass, ath) switch
        {
            (3, 190, 4) when safeJoint => "--alt-preset standard",
            (4, 190, 4) when safeJoint => "--alt-preset fast standard",
            (3, 195, 2) when safeJoint => "--alt-preset extreme",
            (4, 195, 2) when safeJoint => "--alt-preset fast extreme",
            (1, 205, 2) when safeJoint && bitrate == 255 => "--alt-preset insane",
            (2, _, 2) when bitrate is > 0 and < 255 => $"--alt-preset {Number(bitrate)}",
            (1, _, 2) when bitrate is > 0 and < 255 => $"--alt-preset cbr {Number(bitrate)}",
            _ => null,
        };
    }

    private static string PresetName(int value) => value switch
    {
        0 => "None",
        >= 8 and <= 320 => $"{Number(value)} kbps",
        >= 410 and <= 500 when value % 10 == 0 => $"V{Number((500 - value) / 10)}",
        _ => NamedPresets.TryGetValue(value, out string? name) ? name : $"Unknown ({Number(value)})",
    };

    private Node VbriNode(long frameOffset)
    {
        Node node = new("VBRI", frameOffset + Offset, End - Offset);
        if (FrameCount is null)
        {
            return node;
        }

        int p = Offset + 4;
        int entrySize = UInt16At(p + 18);
        int scale = UInt16At(p + 16);
        node.Add(Field("Version", 0, 2, Number(UInt16At(p))));
        node.Add(Field("Delay", 2, 2, Number(UInt16At(p + 2))));
        node.Add(Field("Quality", 4, 2, Number(UInt16At(p + 4))));
        node.Add(Field("Byte Count", 6, 4, Number(ByteCount!.Value)));
        node.Add(Field("Frame Count", 10, 4, Number(FrameCount.Value)));
        node.Add(Field("TOC Entries", 14, 2, Number(UInt16At(p + 14))));
        node.Add(Field("TOC Scale", 16, 2, Number(scale)));
        node.Add(Field("TOC Entry Size", 18, 2, $"{Number(entrySize)} bytes"));
        node.Add(Field("Frames Per TOC Entry", 20, 2, Number(UInt16At(p + 20))));
        int count = entrySize is >= 1 and <= 4 ? TocLength / entrySize : 0;
        Node toc = node.Add(new Node("TOC", frameOffset + TocOffset, TocLength, $"{Number(count)} entries"));
        for (int i = 0; i < count; i++)
        {
            int at = TocOffset + (i * entrySize);
            long value = 0;
            for (int j = 0; j < entrySize; j++)
            {
                value = (value << 8) | _frame[at + j];
            }

            toc.Add(new Node($"Entry {i}", frameOffset + at, entrySize, $"{Number(value * scale)} bytes"));
        }

        return node;

        Node Field(string name, int offset, int length, string value) => new(name, frameOffset + p + offset, length, value);
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
