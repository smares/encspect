using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Mp4;

// Metadata (ISO/IEC 14496-12 8.11.1 meta) and Apple's iTunes metadata: an item list (ilst) whose
// items hold data atoms, each a type, a locale and the value, and freeform items ("----") named by a
// mean and a name atom, which carry Apple's gapless information (iTunSMPB), its loudness values
// (iTunNORM) and its encoder's settings (Encoding Params). An ID32 box (ID3 in MP4) goes to the
// ID3v2 tag parser; Nero writes chapters into a chpl box.
public sealed partial class Mp4Parser
{
    // ISO/IEC 14496-12 makes meta a full box; QuickTime's meta in udta is a plain container, which
    // shows as a handler box right at the start of its payload.
    private static void Meta(State state, Node node, BoxHeader header, BoxFields f, int depth)
    {
        if (!state.Reader.MatchesAt(f.Position + 4, "hdlr"u8))
        {
            FullBox(node, f);
        }

        string? outer = state.MetaHandler;
        state.MetaHandler = null;
        state.Keys.Clear();
        ReadChildren(state, node, f.Position, header.End, depth + 1);
        state.MetaHandler = outer;
    }

    // QuickTime File Format, "Metadata key atom": the keys an mdta item list refers to by index.
    private static void Keys(State state, Node node, BoxFields f)
    {
        FullBox(node, f);
        uint count = f.U32(node, "Entries", Explain.KeyEntries);
        for (uint i = 0; i < count && f.Remaining >= 8 && i < MaxListedEntries; i++)
        {
            long at = f.Position;
            uint size = (uint)f.Take(4);
            if (size < 8 || size - 8 > f.Remaining - 4)
            {
                state.Findings.AddNow(Severity.Warning, "mp4.key-size-invalid", $"The key at 0x{at:X} states a size of {size} bytes, which does not fit its box.", node);
                return;
            }

            string space = BoxFields.FourCcText((uint)f.Take(4));
            string key = Latin1OrUtf8(f.TakeBytes(size - 8));
            var entry = new Node("Key", at, size, key) { Description = Explain.Key };
            entry.Add(new Node("Key Size", at, 4, NodeValue.Integer(size, "bytes")) { Description = Explain.KeySize });
            entry.Add(new Node("Namespace", at + 4, 4, space) { Description = Explain.KeyNamespace });
            entry.Add(new Node("Value", at + 8, size - 8, key) { Description = Explain.KeyValue });
            node.Add(entry);
            state.Keys.Add(key);
        }
    }

    private static void ItemList(State state, Node node, BoxHeader header, BoxFields f, int depth)
    {
        int before = node.Children.Count;
        ReadChildren(state, node, f.Position, header.End, depth + 1, custom: Item,
            naming: h => new BoxName(ItemName(state, h), NodeKind.Detail, ItemDescription(state, h), Topics.ItunesMetadata));
        node.Value = NodeValue.Integer(node.Children.Count - before, "items", "item");
    }

    // An item's node is named for what it is; a name that its data leaves empty falls back to its type.
    private static string ItemName(State state, BoxHeader header)
    {
        string name = DataName(state, header);
        return name.Length > 0 ? name : header.Type;
    }

    private static string ItemDescription(State state, BoxHeader header) =>
        state.MetaHandler == "mdta" ? Explain.QuickTimeItem
        : header.Type == "----" ? Explain.Freeform(FreeformName(state, header))
        : Explain.Item(header.Type);

    private static string DataName(State state, BoxHeader header)
    {
        if (state.MetaHandler == "mdta")
        {
            uint index = BoxFields.FourCcValue(state.Reader.ReadBytesAt(header.Offset + 4, 4));
            return index >= 1 && index <= state.Keys.Count ? state.Keys[(int)index - 1] : header.Type;
        }

        return header.Type == "----" ? FreeformName(state, header) ?? header.Type : BoxNames.Item(header.Type) ?? header.Type;
    }

    // A freeform item is named by its name atom, which follows its mean atom inside it.
    private static string? FreeformName(State state, BoxHeader header)
    {
        long position = header.PayloadStart;
        while (position + 12 <= header.End && ReadHeader(state.Reader, position, header.End) is { } child && child.End <= header.End)
        {
            if (child.Type == "name" && child.Size > 12)
            {
                byte[] bytes = state.Reader.ReadBytesAt(child.Offset + 12, (int)Math.Min(child.Size - 12, 256));
                int end = Array.IndexOf(bytes, (byte)0);
                return Latin1OrUtf8(end < 0 ? bytes : bytes[..end]);
            }

            if (child.IsToEnd)
            {
                break;
            }

            position = child.End;
        }

        return null;
    }

    // An item: its data atoms, and for a freeform item its mean and name.
    private static void Item(State state, Node node, BoxHeader header, BoxFields f, int depth)
    {
        string? mean = null;
        string? name = null;
        ReadChildren(state, node, f.Position, header.End, depth + 1, custom: (s, child, h, fields, d) =>
        {
            switch (h.Type)
            {
                case "mean":
                    FullBox(child, fields);
                    mean = fields.CString(child, "Value", Explain.MeanValue);
                    child.Value = mean;
                    break;
                case "name":
                    FullBox(child, fields);
                    name = fields.CString(child, "Value", Explain.NameValue);
                    child.Value = name;
                    break;
                case "data":
                    NodeValue? value = Data(s, node, child, fields, header.Type, name);
                    if (node.Value is null || node.Value == BoxNames.Describe(header.Type))
                    {
                        node.Value = value;
                    }

                    break;
                default:
                    if (fields.Remaining > 0)
                    {
                        child.Add(new Node("Data", fields.Position, fields.Remaining, NodeValue.Integer(fields.Remaining, "bytes")) { Description = Explain.UnreadData });
                    }

                    break;
            }
        });

        if (header.Type == "----" && name is not null)
        {
            node.Value = node.Value is null ? $"{mean}:{name}" : node.Value;
        }
    }

    // Apple's iTunes metadata format, "Data atom": a type (one byte of type set, then three of the
    // well-known type), a locale and the value.
    private static NodeValue? Data(State state, Node item, Node node, BoxFields f, string itemType, string? freeform)
    {
        uint type = (uint)f.Field(node, "Data Type", 4, Explain.DataType, v => $"{v & 0xFFFFFF} ({DataTypeName((int)(v & 0xFFFFFF))})") & 0xFFFFFF;
        f.U32(node, "Locale", Explain.Locale);
        long at = f.Position;
        byte[] bytes = f.TakeBytes(f.Remaining);
        NodeValue value = Value((int)type, bytes, itemType, freeform);
        Node shown = node.Add(new Node("Value", at, bytes.Length, value) { Description = Explain.DataValue });
        node.Value = value;
        string? text = value.AsText();
        if (itemType == "©too" && text is { Length: > 0 })
        {
            // No specification at hand defines ©too as the encoder, and ffmpeg's muxer writes its own version into it: the writer's.
            state.Context.AddEncoderClaim(new EncoderClaim(text, item, "MP4 ©too atom (encoding tool)", ClaimRole.Writer));
        }

        if (freeform == "iTunSMPB" && text is not null)
        {
            Smpb(state, shown, bytes, at);
        }
        else if (freeform == "Encoding Params")
        {
            EncodingParamsAtom(state, shown, bytes, at);
        }

        return value;
    }

    private static string DataTypeName(int type) => type switch
    {
        0 => "implicit",
        1 => "UTF-8",
        2 => "UTF-16",
        3 => "S/JIS",
        4 => "UTF-8 sort",
        5 => "UTF-16 sort",
        13 => "JPEG",
        14 => "PNG",
        21 => "signed integer",
        22 => "unsigned integer",
        23 => "32-bit float",
        24 => "64-bit float",
        27 => "BMP",
        28 => "QuickTime metadata atom",
        65 => "8-bit signed integer",
        66 => "16-bit signed integer",
        67 => "32-bit signed integer",
        70 => "point",
        71 => "dimensions",
        72 => "rectangle",
        74 => "64-bit signed integer",
        75 => "8-bit unsigned integer",
        76 => "16-bit unsigned integer",
        77 => "32-bit unsigned integer",
        78 => "64-bit unsigned integer",
        79 => "affine transform",
        _ => "reserved",
    };

    private static NodeValue Value(int type, byte[] bytes, string itemType, string? freeform)
    {
        switch (type)
        {
            case 1 or 4:
                return Latin1OrUtf8(bytes);
            case 2 or 5:
                return Encoding.BigEndianUnicode.GetString(bytes);
            case 3:
                return Encoding.Latin1.GetString(bytes);
            case 13 or 14 or 27:
                return $"{DataTypeName(type)} image, {bytes.Length} bytes";
            case 21 or 65 or 66 or 67 or 74 when bytes.Length is 1 or 2 or 3 or 4 or 8:
                return NodeValue.Integer(Signed(bytes));
            case 22 or 75 or 76 or 77 or 78 when bytes.Length is 1 or 2 or 3 or 4 or 8:
                ulong unsigned = Unsigned(bytes);
                return unsigned <= long.MaxValue ? NodeValue.Integer((long)unsigned) : unsigned.ToString(CultureInfo.InvariantCulture);
            case 23 when bytes.Length == 4:
                float single = BinaryPrimitives.ReadSingleBigEndian(bytes);
                return float.IsFinite(single) ? NodeValue.Decimal(single, 3) : single.ToString(CultureInfo.InvariantCulture);
            case 24 when bytes.Length == 8:
                double real = BinaryPrimitives.ReadDoubleBigEndian(bytes);
                return double.IsFinite(real) ? NodeValue.Decimal(real, 3) : real.ToString(CultureInfo.InvariantCulture);
            case 70 or 71 when bytes.Length == 8:
                return $"{BinaryPrimitives.ReadSingleBigEndian(bytes).ToString(CultureInfo.InvariantCulture)}, {BinaryPrimitives.ReadSingleBigEndian(bytes.AsSpan(4)).ToString(CultureInfo.InvariantCulture)}";
            case 72 when bytes.Length == 16:
                return string.Join(", ", Enumerable.Range(0, 4).Select(i => BinaryPrimitives.ReadSingleBigEndian(bytes.AsSpan(4 * i)).ToString(CultureInfo.InvariantCulture)));
            case 0:
                if (itemType is "trkn" or "disk" && bytes.Length >= 6)
                {
                    int number = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2));
                    int total = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4));
                    return total > 0 ? $"{number} of {total}" : NodeValue.Integer(number);
                }

                if (itemType == "gnre" && bytes.Length == 2)
                {
                    int genre = BinaryPrimitives.ReadUInt16BigEndian(bytes);
                    return $"{genre} (ID3v1 genre {genre - 1})";
                }

                if (freeform == "Encoding Params")
                {
                    return NodeValue.Integer(bytes.Length, "bytes");
                }

                break;
        }

        return bytes.Length <= 32 ? $"0x{Convert.ToHexString(bytes)}" : NodeValue.Integer(bytes.Length, "bytes");
    }

    private static long Signed(byte[] bytes)
    {
        long value = (sbyte)bytes[0];
        for (int i = 1; i < bytes.Length; i++)
        {
            value = (value << 8) | bytes[i];
        }

        return value;
    }

    private static ulong Unsigned(byte[] bytes)
    {
        ulong value = 0;
        foreach (byte b in bytes)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    // iTunSMPB, as Apple's encoder writes it: space-separated hexadecimal words, the second the
    // encoder delay, the third the padding and the fourth the samples between them, at the output
    // sample rate.
    private static void Smpb(State state, Node value, byte[] bytes, long at)
    {
        var words = new List<(int Start, int Length)>();
        for (int i = 0; i < bytes.Length;)
        {
            while (i < bytes.Length && bytes[i] == (byte)' ')
            {
                i++;
            }

            int start = i;
            while (i < bytes.Length && bytes[i] != (byte)' ' && bytes[i] != 0)
            {
                i++;
            }

            if (i > start)
            {
                words.Add((start, i - start));
            }

            if (i < bytes.Length && bytes[i] == 0)
            {
                break;
            }
        }

        long? Word(int index, string name, string description)
        {
            if (index >= words.Count)
            {
                return null;
            }

            (int start, int length) = words[index];
            string text = Encoding.ASCII.GetString(bytes, start, length);
            if (!long.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long number) || length > 16)
            {
                return null;
            }

            value.Add(new Node(name, at + start, length, NodeValue.Integer(number, "samples", "sample")) { Description = description, Topic = Topics.EditListsAndPriming });
            return number;
        }

        long? delay = Word(1, "Encoder Delay", Explain.SmpbDelay);
        long? padding = Word(2, "Padding", Explain.SmpbPadding);
        long? samples = Word(3, "Original Samples", Explain.SmpbSamples);
        if (delay is null || padding is null || samples is null)
        {
            state.Findings.AddNow(Severity.Warning, "mp4.itunsmpb-invalid", "The iTunSMPB atom does not hold the hexadecimal words of an encoder delay, padding and sample count.", value);
            return;
        }

        state.Smpb ??= new GaplessInfo(delay.Value, padding.Value, samples.Value, value);
    }

    // Apple's encoder writes its settings as pairs of a four-character key and a 32-bit value: vers,
    // acbf (the bitrate control mode of Core Audio's AudioConverter: 0 constant, 1 long-term
    // average, 2 variable constrained, 3 variable), brat (the bitrate) and cdcv (the codec version).
    private static void EncodingParamsAtom(State state, Node value, byte[] bytes, long at)
    {
        int? mode = null;
        long? bitrate = null;
        for (int i = 0; i + 8 <= bytes.Length; i += 8)
        {
            string key = BoxFields.FourCcText(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i)));
            uint number = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i + 4));
            NodeValue shown = key switch
            {
                "acbf" => $"{number} ({BitrateMode((int)number)})",
                "brat" => NodeValue.Integer(number, "bit/s"),
                "cdcv" => NodeValue.Hex(number, 8),
                _ => NodeValue.Integer(number),
            };
            var pair = new Node(key switch { "vers" => "Version", "acbf" => "Bitrate Mode", "brat" => "Bitrate", "cdcv" => "Codec Version", _ => key }, at + i, 8, shown)
            {
                Description = key switch
                {
                    "vers" => Explain.ParamsVersion,
                    "acbf" => Explain.ParamsMode,
                    "brat" => Explain.ParamsBitrate,
                    "cdcv" => Explain.ParamsCodecVersion,
                    _ => Explain.ParamsOther,
                },
                Topic = key is "acbf" or "brat" ? Topics.BitrateModes : null,
            };
            pair.Add(new Node("Key", at + i, 4, key) { Description = Explain.ParamsKey });
            pair.Add(new Node("Value", at + i + 4, 4, NodeValue.Integer(number)) { Description = Explain.ParamsValue });
            value.Add(pair);
            if (key == "acbf")
            {
                mode = (int)number;
            }
            else if (key == "brat")
            {
                bitrate = number;
            }
        }

        state.EncodingParams ??= new EncodingParams(mode, bitrate, value);
    }

    private static string BitrateMode(int mode) => mode switch
    {
        0 => "CBR",
        1 => "ABR",
        2 => "constrained VBR",
        3 => "VBR",
        _ => "unknown",
    };

    // ID3 in MP4 (id3v2 in ISO base media files, as mp4ra registers it): a full box, a 15-bit
    // language, then a whole ID3v2 tag, which the ID3v2 tag parser reads.
    private static void Id32(State state, Node node, BoxFields f)
    {
        FullBox(node, f);
        f.Field(node, "Language", 2, Explain.Id3Language, v =>
            new string([(char)(((v >> 10) & 0x1F) + 0x60), (char)(((v >> 5) & 0x1F) + 0x60), (char)((v & 0x1F) + 0x60)]));
        if (f.Remaining > 0)
        {
            state.Context.ParseTag("ID3v2", state.Reader.Slice(f.Position, f.Remaining), node);
        }
    }

    // Nero's chapter list: a full box, four more bytes in version 1, a chapter count, then each
    // chapter's start in 100-nanosecond units and its title (as ffmpeg reads it).
    private static void NeroChapters(Node node, BoxFields f)
    {
        int version = FullBox(node, f).Version;
        if (version != 0)
        {
            f.U32(node, "Reserved", Explain.ChaptersReserved);
        }

        int count = f.U8(node, "Chapters", Explain.ChapterCount);
        for (int i = 0; i < count && f.Remaining >= 9; i++)
        {
            long at = f.Position;
            ulong start = f.Take(8);
            int length = (int)f.Take(1);
            string title = Latin1OrUtf8(f.TakeBytes(Math.Min(length, f.Remaining)));
            TimeSpan time = TimeSpan.FromTicks((long)Math.Min(start, (ulong)TimeSpan.MaxValue.Ticks));
            var chapter = new Node("Chapter", at, f.Position - at, $"{time.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture)} {title}") { Description = Explain.Chapter };
            chapter.Add(new Node("Start", at, 8, NodeValue.Decimal(start / 1e7, 3, "s")) { Description = Explain.ChapterStart });
            chapter.Add(new Node("Title Length", at + 8, 1, NodeValue.Integer(length, "bytes")) { Description = Explain.ChapterTitleLength });
            chapter.Add(new Node("Title", at + 9, f.Position - at - 9, title) { Description = Explain.ChapterTitle });
            node.Add(chapter);
        }
    }
}
