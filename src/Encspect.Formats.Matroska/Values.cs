using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Matroska;

// How an element's value is shown: typed, with its unit, and enumerations with the names the specification gives them.
internal static class Values
{
    private static readonly HashSet<string> Booleans =
    [
        "FlagEnabled", "FlagDefault", "FlagForced", "FlagHearingImpaired", "FlagVisualImpaired", "FlagTextDescriptions", "FlagOriginal",
        "FlagCommentary", "FlagLacing", "EditionFlagDefault", "EditionFlagOrdered", "EditionFlagHidden", "ChapterFlagHidden", "ChapterFlagEnabled", "TagDefault",
        "TagDefaultBogus", "CodecDecodeAll", "TrickTrackFlag",
    ];

    private static readonly Dictionary<string, string> Units = new()
    {
        ["TimestampScale"] = "ns",
        ["Timestamp"] = "ticks",
        ["Duration"] = "ticks",
        ["CueTime"] = "ticks",
        ["CueDuration"] = "ticks",
        ["CueRefTime"] = "ticks",
        ["BlockDuration"] = "ticks",
        ["ReferenceBlock"] = "ticks",
        ["DefaultDuration"] = "ns",
        ["DefaultDecodedFieldDuration"] = "ns",
        ["CodecDelay"] = "ns",
        ["SeekPreRoll"] = "ns",
        ["DiscardPadding"] = "ns",
        ["ChapterTimeStart"] = "ns",
        ["ChapterTimeEnd"] = "ns",
        ["SamplingFrequency"] = "Hz",
        ["OutputSamplingFrequency"] = "Hz",
        ["BitDepth"] = "bits",
        ["BitsPerChannel"] = "bits",
        ["PixelWidth"] = "pixels",
        ["PixelHeight"] = "pixels",
        ["PixelCropBottom"] = "pixels",
        ["PixelCropTop"] = "pixels",
        ["PixelCropLeft"] = "pixels",
        ["PixelCropRight"] = "pixels",
        ["EBMLMaxIDLength"] = "bytes",
        ["EBMLMaxSizeLength"] = "bytes",
        ["PrevSize"] = "bytes",
    };

    public static NodeValue? Of(ElementDefinition definition, ulong u, long s, double d, string? text, byte[]? bytes, long length)
    {
        string name = definition.Name;
        switch (definition.Type)
        {
            case ElementType.Unsigned:
                return Unsigned(name, u);
            case ElementType.Signed:
                return NodeValue.Integer(s, Units.GetValueOrDefault(name));
            case ElementType.Float:
                return double.IsFinite(d) ? NodeValue.Decimal(d, name is "SamplingFrequency" or "OutputSamplingFrequency" or "Duration" ? 1 : 3, Units.GetValueOrDefault(name)) : NodeValue.Text(d.ToString(CultureInfo.InvariantCulture));
            case ElementType.Date:
                return NodeValue.Text(Date(s));
            case ElementType.String or ElementType.Utf8:
                return NodeValue.Text(text ?? string.Empty);
            case ElementType.Binary:
                if (bytes is not null && name == "CRC-32" && bytes.Length == 4)
                {
                    return NodeValue.Hex(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes), 8);
                }

                if (bytes is not null && name == "SeekID" && bytes.Length is >= 1 and <= 4)
                {
                    uint id = (uint)Ebml.Unsigned(bytes);
                    return NodeValue.Text($"0x{id:X} ({Schema.Find(id)?.Name ?? "unknown"})");
                }

                if (bytes is not null && bytes.Length > 0)
                {
                    return NodeValue.Text("0x" + Convert.ToHexString(bytes));
                }

                return NodeValue.Integer(length, "bytes", "byte");
            default:
                return null;
        }
    }

    private static NodeValue Unsigned(string name, ulong value)
    {
        if (Booleans.Contains(name) && value <= 1)
        {
            return NodeValue.Boolean(value == 1);
        }

        if (name.EndsWith("UID", StringComparison.Ordinal) || name == "AttachmentLink" || value > long.MaxValue)
        {
            return NodeValue.Hex(value, 16);
        }

        string? named = Named(name, value);
        if (named is not null)
        {
            return NodeValue.Text($"{value} ({named})");
        }

        return NodeValue.Integer((long)value, Units.GetValueOrDefault(name));
    }

    // The names of the enumerations the analysis shows: the track types (RFC 9559 section 5.1.4.1.3, Table 2), the content encodings
    // (section 5.1.4.1.31, Tables 21 to 26), the display units (Table 10) and the tag target types (Table 33).
    public static string? Named(string element, ulong value) => element switch
    {
        "TrackType" => TrackTypeName(value),
        "ContentEncodingType" => value switch { 0 => "compression", 1 => "encryption", _ => null },
        "ContentCompAlgo" => value switch { 0 => "zlib", 1 => "bzlib", 2 => "lzo1x", 3 => "header stripping", _ => null },
        "ContentEncAlgo" => value switch { 0 => "not encrypted", 1 => "DES", 2 => "3DES", 3 => "Twofish", 4 => "Blowfish", 5 => "AES", _ => null },
        "AESSettingsCipherMode" => value switch { 1 => "AES-CTR", 2 => "AES-CBC", _ => null },
        "ContentEncodingScope" => ScopeName(value),
        "DisplayUnit" => value switch { 0 => "pixels", 1 => "centimeters", 2 => "inches", 3 => "display aspect ratio", 4 => "unknown", _ => null },
        "TargetTypeValue" => value switch
        {
            70 => "COLLECTION",
            60 => "EDITION / ISSUE / VOLUME / OPUS / SEASON / SEQUEL",
            50 => "ALBUM / OPERA / CONCERT / MOVIE / EPISODE",
            40 => "PART / SESSION",
            30 => "TRACK / SONG / CHAPTER",
            20 => "SUBTRACK / MOVEMENT / SCENE",
            10 => "SHOT",
            _ => null,
        },
        _ => null,
    };

    public static string? TrackTypeName(ulong value) => value switch
    {
        1 => "video",
        2 => "audio",
        3 => "complex",
        16 => "logo",
        17 => "subtitle",
        18 => "buttons",
        32 => "control",
        33 => "metadata",
        _ => null,
    };

    private static string? ScopeName(ulong value)
    {
        var names = new List<string>();
        if ((value & 1) != 0)
        {
            names.Add("frames");
        }

        if ((value & 2) != 0)
        {
            names.Add("CodecPrivate");
        }

        if ((value & 4) != 0)
        {
            names.Add("next encoding");
        }

        return names.Count == 0 || value > 7 ? null : string.Join(", ", names);
    }

    // A Date element: nanoseconds from 2001-01-01T00:00:00 UTC (RFC 8794 section 7.6).
    public static string Date(long nanoseconds)
    {
        try
        {
            DateTime epoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return epoch.AddTicks(nanoseconds / 100).ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return $"{nanoseconds} ns after 2001-01-01T00:00:00Z";
        }
    }
}
