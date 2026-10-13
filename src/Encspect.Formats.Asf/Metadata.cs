using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Asf;

public sealed partial class AsfParser
{
    // The attributes whose text names the program that wrote the file: FFmpeg writes
    // WM/EncodingSettings (its muxer's version), the Windows Media Format SDK's writer
    // WMFSDKVersion, and Windows Media applications WM/ToolName and WM/ToolVersion.
    private static readonly HashSet<string> EncoderAttributes = new(StringComparer.Ordinal)
    {
        "WM/EncodingSettings", "WM/Tool", "WM/ToolName", "WM/ToolVersion", "WMFSDKVersion",
    };

    // ---- Content Description Object (section 3.10) ----

    private static void ParseContentDescription(State state, Node parent, ObjectHeader header, long end)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, "Content Description Object", Explain.ContentDescription, Topics.AsfMetadata, NodeKind.Tag);
        Body(state, node, f, 34, () =>
        {
            string[] names = ["Title", "Author", "Copyright", "Description", "Rating"];
            string[] descriptions = [Explain.Title, Explain.Author, Explain.Copyright, Explain.DescriptionField, Explain.Rating];
            var lengths = new ushort[5];
            for (int i = 0; i < 5; i++)
            {
                lengths[i] = f.Word($"{names[i]} Length", Explain.ContentDescriptionLength, "bytes");
            }

            string? first = null;
            for (int i = 0; i < 5; i++)
            {
                if (lengths[i] == 0)
                {
                    continue;
                }

                if (lengths[i] > f.Remaining)
                {
                    state.Findings.AddNow(Severity.Warning, "asf.object-truncated", $"The Content Description Object states {lengths[i]} bytes for the {names[i].ToLowerInvariant()}, but {f.Remaining} remain.", node.Children[i]);
                    break;
                }

                if ((lengths[i] & 1) != 0)
                {
                    state.Findings.AddNow(Severity.Warning, "asf.string-odd-length", $"The {names[i].ToLowerInvariant()} in the Content Description Object is {lengths[i]} bytes, not whole UTF-16 characters.", node.Children[i]);
                }

                string text = f.Unicode(names[i], lengths[i], descriptions[i]);
                first ??= text;
            }

            node.Value = first;
        });
    }

    // ---- Extended Content Description Object (3.11), Metadata Object (4.7), Metadata Library Object (4.8) ----

    private static void ParseExtendedContentDescription(State state, Node parent, ObjectHeader header, long end)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, "Extended Content Description Object", Explain.ExtendedContentDescription, Topics.AsfMetadata, NodeKind.Tag);
        Body(state, node, f, 26, () =>
        {
            ushort count = f.Word("Content Descriptors Count", Explain.ContentDescriptorsCount);
            node.Value = NodeValue.Integer(count, "descriptors", "descriptor");
            for (int i = 0; i < count; i++)
            {
                if (f.Remaining < 6)
                {
                    state.Findings.AddNow(Severity.Warning, "asf.descriptor-count", $"The Extended Content Description Object counts {count} descriptors, but its data holds {i}.", node.Children[0]);
                    break;
                }

                ReadDescriptor(state, f, node, DescriptorKind.Extended);
            }
        });
    }

    private static void ParseMetadata(State state, Node parent, ObjectHeader header, long end, bool library)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, library ? "Metadata Library Object" : "Metadata Object",
            library ? Explain.MetadataLibrary : Explain.Metadata, Topics.AsfMetadata, NodeKind.Tag);
        Body(state, node, f, 26, () =>
        {
            ushort count = f.Word("Description Records Count", Explain.DescriptionRecordsCount);
            node.Value = NodeValue.Integer(count, "records", "record");
            for (int i = 0; i < count; i++)
            {
                if (f.Remaining < 12)
                {
                    state.Findings.AddNow(Severity.Warning, "asf.descriptor-count", $"The {node.Name} counts {count} records, but its data holds {i}.", node.Children[0]);
                    break;
                }

                ReadDescriptor(state, f, node, library ? DescriptorKind.Library : DescriptorKind.Metadata);
            }
        });
    }

    private enum DescriptorKind
    {
        Extended,
        Metadata,
        Library,
    }

    // One attribute: a name and a typed value. The three objects lay them out differently (an
    // Extended Content Description descriptor has the name first and a 16-bit value length and a
    // 32-bit boolean; the Metadata and Metadata Library records have a stream number, a 32-bit value
    // length and a 16-bit boolean before the name, and the library a language index and the GUID
    // type).
    private static void ReadDescriptor(State state, Fields f, Node tag, DescriptorKind kind)
    {
        long at = f.Position;
        var item = new Node("Descriptor", at, 0);
        var d = new Fields(f.Reader, item);
        var problems = new List<(Severity Severity, string Code, string Message, Node? Node)>();
        ushort nameLength;
        ushort type;
        uint valueLength;
        string name;
        int stream = 0;
        if (kind == DescriptorKind.Extended)
        {
            nameLength = d.Word("Name Length", Explain.DescriptorNameLength, "bytes");
            name = ReadName(d, nameLength, problems);
            type = d.Word("Value Data Type", Explain.DescriptorDataType, v => DescriptorTypeValue(v));
            valueLength = d.Word("Value Length", Explain.DescriptorValueLength, "bytes");
        }
        else
        {
            ushort first = d.Word(kind == DescriptorKind.Library ? "Language List Index" : "Reserved", kind == DescriptorKind.Library ? Explain.LanguageListIndex : Explain.DescriptorReserved);
            stream = d.Word("Stream Number", Explain.DescriptorStreamNumber);
            nameLength = d.Word("Name Length", Explain.DescriptorNameLength, "bytes");
            type = d.Word("Data Type", Explain.DescriptorDataType, v => DescriptorTypeValue(v));
            valueLength = d.Dword("Data Length", Explain.DescriptorValueLength, "bytes");
            name = ReadName(d, nameLength, problems);
            if (kind == DescriptorKind.Metadata && first != 0)
            {
                problems.Add((Severity.Warning, "asf.metadata-reserved", $"A Metadata Object record at 0x{at:X} has the reserved field {first}; the specification requires 0.", item.Children[0]));
            }
        }

        if (valueLength > d.Remaining)
        {
            problems.Add((Severity.Warning, "asf.object-truncated", $"The value of \"{name}\" in the {tag.Name} states {valueLength} bytes, but {d.Remaining} remain.", null));
            valueLength = (uint)d.Remaining;
        }

        (NodeValue value, List<Node> extra) = ReadValue(state, d, name, type, valueLength, kind, problems);
        Node valueNode = item.Children[^1];
        foreach (Node child in extra)
        {
            valueNode.Add(child);
        }

        var shown = new Node(name.Length == 0 ? "Descriptor" : name, at, d.Position - at, value) { Description = Explain.Descriptor };
        foreach (Node child in item.Children)
        {
            shown.Add(child);
        }

        tag.Add(shown);
        foreach ((Severity severity, string code, string message, Node? node) in problems)
        {
            state.Findings.AddNow(severity, code, message, node ?? shown);
        }

        if (EncoderAttributes.Contains(name) && type == 0 && value.AsText() is { Length: > 0 } text)
        {
            state.Context.AddEncoderClaim(new EncoderClaim(text, valueNode, $"{tag.Name} descriptor {name}"));
        }

        _ = stream;
    }

    private static string ReadName(Fields d, ushort nameLength, List<(Severity Severity, string Code, string Message, Node? Node)> problems)
    {
        if (nameLength > d.Remaining)
        {
            throw new EndOfStreamException();
        }

        if ((nameLength & 1) != 0)
        {
            problems.Add((Severity.Warning, "asf.string-odd-length", $"A descriptor name at 0x{d.Position:X} is {nameLength} bytes, not whole UTF-16 characters.", null));
        }

        return d.Unicode("Name", nameLength, Explain.DescriptorName);
    }

    private static NodeValue DescriptorTypeValue(int type) => NodeValue.Text(type switch
    {
        0 => "0 (Unicode string)",
        1 => "1 (byte array)",
        2 => "2 (boolean)",
        3 => "3 (32-bit integer)",
        4 => "4 (64-bit integer)",
        5 => "5 (16-bit integer)",
        6 => "6 (GUID)",
        _ => $"{type} (unknown)",
    });

    private static (NodeValue Value, List<Node> Extra) ReadValue(State state, Fields d, string name, ushort type, uint length, DescriptorKind kind,
        List<(Severity Severity, string Code, string Message, Node? Node)> problems)
    {
        long at = d.Position;
        var extra = new List<Node>();
        NodeValue value;
        long expected = type switch
        {
            2 => kind == DescriptorKind.Extended ? 4 : 2,
            3 => 4,
            4 => 8,
            5 => 2,
            6 => 16,
            _ => -1,
        };
        if (expected >= 0 && length != expected)
        {
            problems.Add((Severity.Warning, "asf.descriptor-length",
                $"The value of \"{name}\" is {length} bytes, but a value of type {type} takes {expected}; it is shown as bytes.", null));
            type = 1;
        }

        switch (type)
        {
            case 0:
                if ((length & 1) != 0)
                {
                    problems.Add((Severity.Warning, "asf.string-odd-length", $"The value of \"{name}\" is {length} bytes, not whole UTF-16 characters.", null));
                }

                value = d.ReadUnicode(length);
                break;
            case 2:
                {
                    uint raw = kind == DescriptorKind.Extended ? d.Reader.ReadUInt32LittleEndian() : d.Reader.ReadUInt16LittleEndian();
                    if (raw > 1)
                    {
                        problems.Add((Severity.Warning, "asf.boolean-value", $"The boolean \"{name}\" is {raw}; the specification allows 0 and 1 (section 4.7).", null));
                    }

                    value = NodeValue.Boolean(raw != 0);
                    break;
                }

            case 3:
                value = NodeValue.Integer(d.Reader.ReadUInt32LittleEndian());
                break;
            case 4:
                {
                    ulong raw = d.Reader.ReadUInt64LittleEndian();
                    value = name == "WM/EncodingTime" ? NodeValue.Text(Fields.FileTime(raw))
                        : name == "Duration" ? Fields.Seconds(raw)
                        : raw <= long.MaxValue ? NodeValue.Integer((long)raw) : NodeValue.Hex(raw, 16);
                    break;
                }

            case 5:
                value = NodeValue.Integer(d.Reader.ReadUInt16LittleEndian());
                break;
            case 6:
                {
                    Span<byte> bytes = stackalloc byte[16];
                    d.Reader.Read(bytes);
                    value = AsfGuids.Text(new Guid(bytes));
                    break;
                }

            default:
                value = NodeValue.Integer(length, "bytes", "byte");
                ReadByteArray(state, d, name, length, extra, problems);
                break;
        }

        d.Node.Add(new Node("Value", at, length, value) { Description = Explain.DescriptorValue });
        return (value, extra);
    }

    // A byte array: attached pictures (WM/Picture) and ID3 tags are taken apart; the rest is skipped.
    private static void ReadByteArray(State state, Fields d, string name, uint length, List<Node> extra, List<(Severity Severity, string Code, string Message, Node? Node)> problems)
    {
        long at = d.Position;
        if (name == "WM/Picture" && length >= 9)
        {
            ReadPicture(d, at, length, extra, problems);
        }
        else if (name == "ID3" && length >= 10)
        {
            Span<byte> signature = stackalloc byte[3];
            d.Reader.Read(signature);
            d.Reader.Position = at;
            if (signature.SequenceEqual("ID3"u8))
            {
                var holder = new Node("ID3", at, length);
                if (state.Context.ParseTag("ID3v2", d.Reader.Slice(at, length), holder) is { } tag)
                {
                    extra.Add(tag);
                }
            }
        }

        d.Reader.Position = at + length;
    }

    // WM/Picture (the Windows Media attachment, which FFmpeg's asf_read_picture reads): the picture
    // type, the size of the picture data, a UTF-16 MIME type and a UTF-16 description, both ended by
    // a NUL, and the picture.
    private static void ReadPicture(Fields d, long at, uint length, List<Node> extra, List<(Severity Severity, string Code, string Message, Node? Node)> problems)
    {
        int read = (int)Math.Min(length, Fields.MaxText);
        byte[] bytes = d.Reader.ReadBytesAt(at, read);
        d.Reader.Position = at + length;
        int position = 5;
        int mimeEnd = IndexOfNul(bytes, position);
        if (mimeEnd < 0)
        {
            return;
        }

        int descriptionEnd = IndexOfNul(bytes, mimeEnd + 2);
        if (descriptionEnd < 0)
        {
            return;
        }

        uint size = BitConverter.ToUInt32(bytes, 1);
        string mime = Encoding.Unicode.GetString(bytes, position, mimeEnd - position);
        string description = Encoding.Unicode.GetString(bytes, mimeEnd + 2, descriptionEnd - mimeEnd - 2);
        int dataAt = descriptionEnd + 2;
        extra.Add(new Node("Picture Type", at, 1, NodeValue.Integer(bytes[0])) { Description = Explain.PictureType });
        extra.Add(new Node("Picture Data Size", at + 1, 4, NodeValue.Integer(size, "bytes")) { Description = Explain.PictureDataSize });
        extra.Add(new Node("MIME Type", at + position, mimeEnd + 2 - position, mime) { Description = Explain.PictureMimeType });
        extra.Add(new Node("Description", at + mimeEnd + 2, descriptionEnd - mimeEnd, description) { Description = Explain.PictureDescription });
        long dataLength = length - dataAt;
        extra.Add(new Node("Picture Data", at + dataAt, dataLength, NodeValue.Integer(dataLength, "bytes", "byte")) { Description = Explain.PictureData });
        if (size != dataLength)
        {
            problems.Add((Severity.Warning, "asf.picture-size", $"The attached picture states {size} bytes of picture data, but {dataLength} follow its description.", extra[1]));
        }
    }

    // The first UTF-16 NUL at an even distance from start, as the index of its first byte.
    private static int IndexOfNul(byte[] bytes, int start)
    {
        for (int i = start; i + 1 < bytes.Length; i += 2)
        {
            if (bytes[i] == 0 && bytes[i + 1] == 0)
            {
                return i;
            }
        }

        return -1;
    }

    // ---- Content Branding Object (section 3.13) ----

    private static void ParseContentBranding(State state, Node parent, ObjectHeader header, long end)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, "Content Branding Object", Explain.ContentBranding, Topics.AsfMetadata, NodeKind.Tag);
        Body(state, node, f, 40, () =>
        {
            uint type = f.Dword("Banner Image Type", Explain.BannerImageType, v => NodeValue.Text(v switch { 0 => "0 (none)", 1 => "1 (bitmap)", 2 => "2 (JPEG)", 3 => "3 (GIF)", _ => $"{v} (unknown)" }));
            uint size = f.Dword("Banner Image Data Size", Explain.BannerImageDataSize, "bytes");
            if (size > f.Remaining)
            {
                throw new EndOfStreamException();
            }

            if (size > 0)
            {
                f.Bytes("Banner Image Data", size, Explain.BannerImageData);
            }

            uint bannerLength = f.Dword("Banner Image URL Length", Explain.BannerImageUrlLength, "bytes");
            f.Ascii("Banner Image URL", Math.Min(bannerLength, f.Remaining), Explain.BannerImageUrl);
            uint copyrightLength = f.Dword("Copyright URL Length", Explain.CopyrightUrlLength, "bytes");
            f.Ascii("Copyright URL", Math.Min(copyrightLength, f.Remaining), Explain.CopyrightUrl);
            if (type == 0 && size != 0)
            {
                state.Findings.AddNow(Severity.Warning, "asf.branding-image", "The Content Branding Object gives no image type but states image data (section 3.13).", node.Children[3]);
            }
        });
    }

    // ---- Extended Stream Properties Object (section 4.1) ----

    private static void ParseExtendedStreamProperties(State state, Node parent, ObjectHeader header, long end, int depth)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, "Extended Stream Properties Object", Explain.ExtendedStreamProperties);
        Body(state, node, f, 88, () =>
        {
            var info = new ExtensionRecord { Node = node };
            info.Start = f.Qword("Start Time", Explain.ExtendedStartTime, "ms");
            info.End = f.Qword("End Time", Explain.ExtendedEndTime, "ms");
            info.DataBitrate = f.Dword("Data Bitrate", Explain.ExtendedDataBitrate, "bps");
            f.Dword("Buffer Size", Explain.ExtendedBufferSize, "ms");
            f.Dword("Initial Buffer Fullness", Explain.ExtendedInitialBufferFullness, "ms");
            f.Dword("Alternate Data Bitrate", Explain.ExtendedAlternateDataBitrate, "bps");
            f.Dword("Alternate Buffer Size", Explain.ExtendedAlternateBufferSize, "ms");
            f.Dword("Alternate Initial Buffer Fullness", Explain.ExtendedAlternateInitialBufferFullness, "ms");
            f.Dword("Maximum Object Size", Explain.MaximumObjectSize, "bytes");
            long flagsAt = f.Position;
            uint flags = f.Dword("Flags", Explain.ExtendedFlags, v => NodeValue.Hex(v, 8));
            Node flagsNode = node.Children[^1];
            Fields.Bits(flagsNode, "Reliable Flag", flagsAt, 0, 1, NodeValue.Boolean((flags & 1) != 0), Explain.ReliableFlag);
            Fields.Bits(flagsNode, "Seekable Flag", flagsAt, 1, 1, NodeValue.Boolean((flags & 2) != 0), Explain.ExtendedSeekableFlag);
            Fields.Bits(flagsNode, "No Cleanpoints Flag", flagsAt, 2, 1, NodeValue.Boolean((flags & 4) != 0), Explain.NoCleanpointsFlag);
            Fields.Bits(flagsNode, "Resend Live Cleanpoints Flag", flagsAt, 3, 1, NodeValue.Boolean((flags & 8) != 0), Explain.ResendLiveCleanpointsFlag);
            Fields.Bits(flagsNode, "Reserved", flagsAt, 4, 28, NodeValue.Hex(flags >> 4, 8), Explain.FlagsReserved);
            info.Number = f.Word("Stream Number", Explain.ExtendedStreamNumber);
            info.LanguageIndex = f.Word("Stream Language ID Index", Explain.StreamLanguageIdIndex);
            f.Qword("Average Time Per Frame", Explain.AverageTimePerFrame, v => NodeValue.Decimal(v / 10_000.0, 3, "ms"));
            ushort names = f.Word("Stream Name Count", Explain.StreamNameCount);
            ushort systems = f.Word("Payload Extension System Count", Explain.PayloadExtensionSystemCount);
            node.Value = $"stream {info.Number}";
            for (int i = 0; i < names; i++)
            {
                long at = f.Position;
                var entry = new Node("Stream Name", at, 0) { Description = Explain.StreamNameEntry };
                var n = new Fields(f.Reader, entry);
                n.Word("Language ID Index", Explain.StreamNameLanguage);
                ushort length = n.Word("Stream Name Length", Explain.StreamNameLength, "bytes");
                string text = n.Unicode("Stream Name", length, Explain.StreamName);
                var shown = new Node("Stream Name", at, f.Position - at, text) { Description = Explain.StreamNameEntry };
                foreach (Node child in entry.Children)
                {
                    shown.Add(child);
                }

                node.Add(shown);
            }

            for (int i = 0; i < systems; i++)
            {
                long at = f.Position;
                var entry = new Node("Payload Extension System", at, 0) { Description = Explain.PayloadExtensionSystem };
                var s = new Fields(f.Reader, entry);
                Guid id = s.Guid("Extension System ID", Explain.ExtensionSystemId, AsfGuids.ExtensionName);
                ushort size = s.Word("Extension Data Size", Explain.ExtensionDataSize, v => v == 0xFFFF ? NodeValue.Text("0xFFFF (variable)") : NodeValue.Integer(v, "bytes", "byte"));
                uint infoLength = s.Dword("Extension System Info Length", Explain.ExtensionSystemInfoLength, "bytes");
                if (infoLength > s.Remaining)
                {
                    throw new EndOfStreamException();
                }

                if (infoLength > 0)
                {
                    s.Bytes("Extension System Info", infoLength, Explain.ExtensionSystemInfo);
                }

                var shown = new Node("Payload Extension System", at, f.Position - at, AsfGuids.ExtensionName(id) ?? AsfGuids.Text(id)) { Description = Explain.PayloadExtensionSystem };
                foreach (Node child in entry.Children)
                {
                    shown.Add(child);
                }

                node.Add(shown);
                info.Extensions.Add((id, size, infoLength));
            }

            // An optional Stream Properties Object closes the object (section 4.1).
            if (f.Remaining >= ObjectHeaderSize)
            {
                long at = f.Position;
                if (ReadObjectHeader(state.Reader, at, end) is { } embedded && embedded.Id == AsfGuids.StreamProperties && embedded.Size >= ObjectHeaderSize)
                {
                    var entry = new StreamEntry { IsInExtension = true };
                    long embeddedEnd = Math.Min(embedded.End, end);
                    ParseStreamProperties(state, node, embedded, embeddedEnd, entry);
                    if (entry.Number != 0)
                    {
                        RegisterStream(state, entry, node.Children[^1]);
                    }

                    f.Reader.Position = embeddedEnd;
                }
            }

            if (f.Remaining > 0)
            {
                f.Bytes("Extra Data", f.Remaining, Explain.ExtraData);
            }

            state.ExtensionRecords.Add(info);
        });
    }
}

internal sealed class ExtensionRecord
{
    public Node Node { get; set; } = null!;

    public int Number { get; set; }

    public ulong Start { get; set; }

    public ulong End { get; set; }

    public uint DataBitrate { get; set; }

    public int LanguageIndex { get; set; }

    public List<(Guid Id, ushort Size, uint InfoLength)> Extensions { get; } = [];
}
