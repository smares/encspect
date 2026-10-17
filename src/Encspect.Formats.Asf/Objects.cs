using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Asf;

public sealed partial class AsfParser
{
    // Top-level objects, and objects in the header, are read up to these counts; a file of millions
    // of tiny objects is no ASF file anyone wrote.
    private const int MaxTopLevelObjects = 10_000;
    private const int MaxChildObjects = 10_000;

    // Entries of a list inside an object (codecs, encryption records) become nodes up to this count; the rest of
    // the object is left as it is, with a finding.
    private const int MaxListEntries = 1000;

    // Objects nest at most this deep (header, extension, extended stream properties, stream
    // properties), so the tree stays well within Node.MaxDepth.
    private const int MaxObjectDepth = 8;

    private static void ParseFile(ParseContext context, ByteReader reader, IReadOnlyDictionary<int, string> routes)
    {
        var state = new State(context, reader) { Routes = routes };
        Node file = context.Root.Add(new Node("ASF", reader.Start, reader.Length) { Description = Explain.File, Topic = Topics.AsfContainer });
        state.File = file;

        long position = reader.Start;
        int count = 0;
        while (position + ObjectHeaderSize <= reader.End)
        {
            reader.CancellationToken.ThrowIfCancellationRequested();
            if (count++ >= MaxTopLevelObjects)
            {
                Node rest = file.Add(new Node("Unparsed Data", position, reader.End - position, NodeValue.Integer(reader.End - position, "bytes")) { Description = Explain.UnparsedData });
                state.Findings.AddNow(Severity.Warning, "asf.too-many-objects", $"The file holds more than {MaxTopLevelObjects} top-level objects; the rest, from 0x{position:X}, is not read.", rest);
                position = reader.End;
                break;
            }

            ObjectHeader header = ReadObjectHeader(reader, position, reader.End)!.Value;
            bool toEnd = header.Id == AsfGuids.Data && header.Size == 0;
            if (header.Size < ObjectHeaderSize && !toEnd)
            {
                Node rest = file.Add(new Node("Unparsed Data", position, reader.End - position, NodeValue.Integer(reader.End - position, "bytes")) { Description = Explain.UnparsedData });
                state.Findings.AddNow(Severity.Warning, "asf.object-size",
                    $"The object at 0x{position:X} states a size of {header.Size} bytes, less than the 24 bytes of its GUID and size; the {reader.End - position} bytes from there are not read.", rest);
                position = reader.End;
                break;
            }

            long end = toEnd ? reader.End : Math.Min(header.End, reader.End);
            ParseTopLevel(state, header, end, toEnd);
            if (!toEnd && header.End > reader.End && file.Children.Count > 0)
            {
                state.Findings.AddNow(Severity.Warning, "asf.object-truncated",
                    $"The {AsfGuids.ObjectName(header.Id) ?? "object"} at 0x{position:X} states a size of {header.Size} bytes, but the file ends {header.End - reader.End} bytes sooner: the file is cut short.", file.Children[^1]);
            }

            position = end;
        }

        if (position < reader.End)
        {
            Node trailing = file.Add(new Node("Trailing Data", position, reader.End - position, NodeValue.Integer(reader.End - position, "bytes")) { Description = Explain.TrailingData });
            state.Findings.AddNow(Severity.Warning, "asf.trailing-data", $"{reader.End - position} bytes after the last object at 0x{position:X} are not an object.", trailing);
        }

        Check(state);
        Route(state);
        file.Value = Describe(state);
        state.Findings.Flush(context);
    }

    private static string Describe(State state)
    {
        if (state.Streams.Count == 0)
        {
            return "no streams";
        }

        var kinds = state.Streams.GroupBy(s => s.TypeName.ToLowerInvariant()).Select(g => g.Count() == 1 ? g.Key : $"{g.Count()} {g.Key}").ToList();
        return $"{state.Streams.Count} {(state.Streams.Count == 1 ? "stream" : "streams")} ({string.Join(", ", kinds)})";
    }

    private static void ParseTopLevel(State state, ObjectHeader header, long end, bool sizeUnknown)
    {
        Node file = state.File;
        if (header.Id == AsfGuids.Header && state.HeaderNode is null)
        {
            ParseHeaderObject(state, header, end);
        }
        else if (header.Id == AsfGuids.Data)
        {
            ParseDataObject(state, header, end, sizeUnknown);
        }
        else if (header.Id == AsfGuids.SimpleIndex)
        {
            ParseSimpleIndex(state, header, end);
        }
        else if (header.Id == AsfGuids.Index)
        {
            ParseIndex(state, header, end);
        }
        else if (header.Id == AsfGuids.MediaObjectIndex)
        {
            ParseIndex(state, header, end);
        }
        else if (header.Id == AsfGuids.TimecodeIndex)
        {
            ParseIndex(state, header, end);
        }
        else
        {
            Node node = ParseUnknown(state, file, header, end);
            if (header.Id == AsfGuids.Header)
            {
                state.Findings.AddNow(Severity.Warning, "asf.duplicate-header", $"A second Header Object starts at 0x{header.Offset:X}; the specification allows one, at the start of the file.", node);
            }
            else
            {
                state.NoteUnknownObject("top-level", header, node, "the file", "section 2.2");
            }
        }
    }

    // An object this analyzer does not know, with its fields and its data as bytes.
    private static Node ParseUnknown(State state, Node parent, ObjectHeader header, long end)
    {
        (Node node, Fields fields) = Begin(state, parent, header, end, AsfGuids.ObjectName(header.Id) ?? "Unknown Object", Explain.UnknownObject);
        node.Value = NodeValue.Integer(end - header.Offset, "bytes");
        if (fields.Remaining > 0)
        {
            fields.Bytes("Object Data", fields.Remaining, Explain.UnknownObjectData);
        }

        return node;
    }

    // Starts an object's node with its GUID and size, and returns the fields after them.
    private static (Node Node, Fields Fields) Begin(State state, Node parent, ObjectHeader header, long end, string name, string description, string? topic = null,
        NodeKind kind = NodeKind.Detail)
    {
        var node = parent.Add(new Node(name, header.Offset, end - header.Offset) { Kind = kind, Description = description, Topic = topic ?? Topics.AsfContainer });
        var fields = new Fields(state.Reader.Slice(header.Offset, end - header.Offset), node);
        fields.Guid("Object ID", Explain.ObjectId, AsfGuids.ObjectName);
        fields.Qword("Object Size", Explain.ObjectSize, "bytes");
        return (node, fields);
    }

    // Runs the body of an object. Fields that run past the object's end are a finding, and what was
    // read stays in the tree.
    private static void Body(State state, Node node, Fields fields, long minimum, Action body, bool coversRest = true)
    {
        if (node.Length < minimum)
        {
            state.Findings.AddNow(Severity.Warning, "asf.object-size",
                $"The {node.Name} at 0x{node.Offset:X} is {node.Length} bytes; the specification gives at least {minimum}.", node);
        }

        try
        {
            body();
        }
        catch (EndOfStreamException)
        {
            state.Findings.AddNow(Severity.Warning, "asf.object-truncated",
                $"The {node.Name} at 0x{node.Offset:X} is {node.Length} bytes, too few for its fields; they are read as far as they go.", node);
        }

        if (coversRest && fields.Remaining > 0 && node.Children.Count > 0 && node.Children[^1].End < node.End)
        {
            // Bytes the fields do not cover (the end of an object that is longer than its fields).
            long start = node.Children[^1].End;
            node.Add(new Node("Extra Data", start, node.End - start, NodeValue.Integer(node.End - start, "bytes", "byte")) { Description = Explain.ExtraData });
        }
    }

    private static void TooManyEntries(State state, Node node, Fields f, string what, string from)
    {
        state.Findings.AddNow(Severity.Warning, "asf.too-many-entries",
            $"The {node.Name} counts {what}; the first {MaxListEntries} are read, and the rest of the object, {from} 0x{f.Position:X}, is not.", node);
    }

    // ---- Header Object (section 3.1) ----

    private static void ParseHeaderObject(State state, ObjectHeader header, long end)
    {
        (Node node, Fields fields) = Begin(state, state.File, header, end, "Header Object", Explain.HeaderObject);
        state.HeaderNode = node;
        uint declared = 0;
        Body(state, node, fields, HeaderObjectMinimum, () =>
        {
            declared = fields.Dword("Number of Header Objects", Explain.NumberOfHeaderObjects);
            byte reserved1 = fields.Byte("Reserved1", Explain.Reserved1);
            byte reserved2 = fields.Byte("Reserved2", Explain.Reserved2);
            if (reserved1 != 1)
            {
                state.Findings.AddNow(Severity.Info, "asf.header-reserved1", $"The Header Object's Reserved1 is {reserved1}; the specification sets it to 1 and says parsers may ignore it.", node.Children[^2]);
            }

            if (reserved2 != 2)
            {
                state.Findings.AddNow(Severity.Warning, "asf.header-reserved2",
                    $"The Header Object's Reserved2 is {reserved2}; the specification sets it to 2 and says an application should fail to source the content if it is not.", node.Children[^1]);
            }
        }, coversRest: false);

        if (node.Length >= HeaderObjectMinimum)
        {
            int before = state.ChildCount;
            ReadObjects(state, node, header.Offset + HeaderObjectMinimum, end, inExtension: false, depth: 0);
            state.HeaderObjects = state.ChildCount - before;
            node.Value = NodeValue.Integer(state.HeaderObjects, "objects", "object");
            if (declared != state.HeaderObjects)
            {
                state.Findings.AddNow(Severity.Warning, "asf.header-object-count",
                    $"The Header Object counts {declared} objects, but it holds {state.HeaderObjects} (the Header Extension Object counts as one, section 3.1).", node.Children.FirstOrDefault(c => c.Name == "Number of Header Objects") ?? node);
            }
        }
    }

    // The objects between start and end, in a Header Object or a Header Extension Object.
    private static void ReadObjects(State state, Node parent, long start, long end, bool inExtension, int depth)
    {
        ByteReader reader = state.Reader;
        long position = start;
        int count = 0;
        while (position < end)
        {
            reader.CancellationToken.ThrowIfCancellationRequested();
            if (end - position < ObjectHeaderSize)
            {
                Node rest = parent.Add(new Node("Unparsed Data", position, end - position, NodeValue.Integer(end - position, "bytes")) { Description = Explain.UnparsedData });
                state.Findings.AddNow(Severity.Warning, "asf.object-truncated", $"{end - position} bytes at 0x{position:X} are too few for an object's GUID and size.", rest);
                return;
            }

            if (count++ >= MaxChildObjects)
            {
                Node rest = parent.Add(new Node("Unparsed Data", position, end - position, NodeValue.Integer(end - position, "bytes")) { Description = Explain.UnparsedData });
                state.Findings.AddNow(Severity.Warning, "asf.too-many-objects", $"The {parent.Name} holds more than {MaxChildObjects} objects; the rest, from 0x{position:X}, is not read.", rest);
                return;
            }

            ObjectHeader header = ReadObjectHeader(reader, position, end)!.Value;
            if (header.Size < ObjectHeaderSize)
            {
                Node rest = parent.Add(new Node("Unparsed Data", position, end - position, NodeValue.Integer(end - position, "bytes")) { Description = Explain.UnparsedData });
                state.Findings.AddNow(Severity.Warning, "asf.object-size",
                    $"The object at 0x{position:X} in the {parent.Name} states a size of {header.Size} bytes, less than the 24 bytes of its GUID and size; the {end - position} bytes from there are not read.", rest);
                return;
            }

            long childEnd = Math.Min(header.End, end);
            state.ChildCount++;
            state.HeaderGuids.Add(header.Id);
            ParseHeaderChild(state, parent, header, childEnd, inExtension, depth);
            if (header.End > end && parent.Children.Count > 0)
            {
                state.Findings.AddNow(Severity.Warning, "asf.object-overruns",
                    $"The {AsfGuids.ObjectName(header.Id) ?? "object"} at 0x{position:X} states a size of {header.Size} bytes, {header.End - end} more than the {parent.Name} that holds it has left; it is read up to the end of the {parent.Name}.",
                    parent.Children[^1]);
            }

            position = childEnd;
        }
    }

    private static void ParseHeaderChild(State state, Node parent, ObjectHeader header, long end, bool inExtension, int depth)
    {
        Guid id = header.Id;
        if (depth >= MaxObjectDepth)
        {
            ParseUnknown(state, parent, header, end);
            return;
        }

        if (id == AsfGuids.FileProperties)
        {
            ParseFileProperties(state, parent, header, end);
        }
        else if (id == AsfGuids.StreamProperties)
        {
            ParseStreamProperties(state, parent, header, end, embedded: null);
        }
        else if (id == AsfGuids.HeaderExtension)
        {
            ParseHeaderExtension(state, parent, header, end, depth);
        }
        else if (id == AsfGuids.CodecList)
        {
            ParseCodecList(state, parent, header, end);
        }
        else if (id == AsfGuids.ScriptCommand)
        {
            ParseScriptCommand(state, parent, header, end);
        }
        else if (id == AsfGuids.Marker)
        {
            ParseMarker(state, parent, header, end);
        }
        else if (id == AsfGuids.BitrateMutualExclusion)
        {
            ParseMutualExclusion(state, parent, header, end, "Bitrate Mutual Exclusion Object", Explain.BitrateMutualExclusion, 42);
        }
        else if (id == AsfGuids.AdvancedMutualExclusion)
        {
            ParseMutualExclusion(state, parent, header, end, "Advanced Mutual Exclusion Object", Explain.AdvancedMutualExclusion, 42);
        }
        else if (id == AsfGuids.ErrorCorrection)
        {
            ParseErrorCorrection(state, parent, header, end);
        }
        else if (id == AsfGuids.ContentDescription)
        {
            ParseContentDescription(state, parent, header, end);
        }
        else if (id == AsfGuids.ExtendedContentDescription)
        {
            ParseExtendedContentDescription(state, parent, header, end);
        }
        else if (id == AsfGuids.ContentBranding)
        {
            ParseContentBranding(state, parent, header, end);
        }
        else if (id == AsfGuids.StreamBitrateProperties)
        {
            ParseStreamBitrateProperties(state, parent, header, end);
        }
        else if (id == AsfGuids.ContentEncryption)
        {
            ParseContentEncryption(state, parent, header, end);
        }
        else if (id == AsfGuids.ExtendedContentEncryption)
        {
            ParseExtendedContentEncryption(state, parent, header, end);
        }
        else if (id == AsfGuids.DigitalSignature)
        {
            ParseDigitalSignature(state, parent, header, end);
        }
        else if (id == AsfGuids.Padding)
        {
            ParsePadding(state, parent, header, end);
        }
        else if (id == AsfGuids.ExtendedStreamProperties)
        {
            ParseExtendedStreamProperties(state, parent, header, end, depth);
        }
        else if (id == AsfGuids.GroupMutualExclusion)
        {
            ParseGroupMutualExclusion(state, parent, header, end);
        }
        else if (id == AsfGuids.StreamPrioritization)
        {
            ParseStreamPrioritization(state, parent, header, end);
        }
        else if (id == AsfGuids.BandwidthSharing)
        {
            ParseBandwidthSharing(state, parent, header, end);
        }
        else if (id == AsfGuids.LanguageList)
        {
            ParseLanguageList(state, parent, header, end);
        }
        else if (id == AsfGuids.Metadata)
        {
            ParseMetadata(state, parent, header, end, library: false);
        }
        else if (id == AsfGuids.MetadataLibrary)
        {
            ParseMetadata(state, parent, header, end, library: true);
        }
        else if (id == AsfGuids.IndexParameters)
        {
            ParseIndexParameters(state, parent, header, end, "Index Parameters Object", Explain.IndexParameters, Explain.IndexEntryTimeInterval);
        }
        else if (id == AsfGuids.MediaObjectIndexParameters)
        {
            ParseIndexParameters(state, parent, header, end, "Media Object Index Parameters Object", Explain.MediaObjectIndexParameters, Explain.IndexEntryCountInterval);
        }
        else if (id == AsfGuids.TimecodeIndexParameters)
        {
            ParseIndexParameters(state, parent, header, end, "Timecode Index Parameters Object", Explain.TimecodeIndexParameters, Explain.IndexEntryCountIntervalIgnored);
        }
        else if (id == AsfGuids.Compatibility && inExtension)
        {
            ParseCompatibility(state, parent, header, end);
        }
        else if (id == AsfGuids.AdvancedContentEncryption)
        {
            ParseAdvancedContentEncryption(state, parent, header, end);
        }
        else if (id == AsfGuids.IndexPlaceholder && inExtension)
        {
            ParseIndexPlaceholder(state, parent, header, end);
        }
        else
        {
            Node node = ParseUnknown(state, parent, header, end);
            state.NoteUnknownObject(parent.Name, header, node, $"the {parent.Name}", "section 3.1");
        }
    }

    // ---- File Properties Object (section 3.2) ----

    private static void ParseFileProperties(State state, Node parent, ObjectHeader header, long end)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, "File Properties Object", Explain.FileProperties);
        if (state.FilePropertiesCount++ > 0)
        {
            state.Findings.AddNow(Severity.Warning, "asf.duplicate-file-properties", $"The Header Object holds a second File Properties Object at 0x{header.Offset:X}; the first one counts.", node);
        }

        Body(state, node, f, 104, () =>
        {
            Guid fileId = f.Guid("File ID", Explain.FileId);
            Node fileIdNode = node.Children[^1];
            ulong fileSize = f.Qword("File Size", Explain.FileSize, "bytes");
            Node fileSizeNode = node.Children[^1];
            ulong creation = f.Qword("Creation Date", Explain.CreationDate, v => Fields.FileTime(v));
            ulong packets = f.Qword("Data Packets Count", Explain.DataPacketsCount);
            Node packetsNode = node.Children[^1];
            ulong play = f.Qword("Play Duration", Explain.PlayDuration, Fields.Seconds);
            ulong send = f.Qword("Send Duration", Explain.SendDuration, Fields.Seconds);
            ulong preroll = f.Qword("Preroll", Explain.Preroll, "ms");
            long flagsAt = f.Position;
            uint flags = f.Dword("Flags", Explain.FilePropertiesFlags, v => NodeValue.Hex(v, 8));
            Node flagsNode = node.Children[^1];
            Fields.Bits(flagsNode, "Broadcast Flag", flagsAt, 0, 1, NodeValue.Boolean((flags & 1) != 0), Explain.BroadcastFlag);
            Fields.Bits(flagsNode, "Seekable Flag", flagsAt, 1, 1, NodeValue.Boolean((flags & 2) != 0), Explain.SeekableFlag);
            Fields.Bits(flagsNode, "Reserved", flagsAt, 2, 30, NodeValue.Hex(flags >> 2, 8), Explain.FlagsReserved);
            uint min = f.Dword("Minimum Data Packet Size", Explain.MinimumPacketSize, "bytes");
            Node minNode = node.Children[^1];
            uint max = f.Dword("Maximum Data Packet Size", Explain.MaximumPacketSize, "bytes");
            uint bitrate = f.Dword("Maximum Bitrate", Explain.MaximumBitrate, "bps");

            if (state.FilePropertiesCount == 1)
            {
                state.FilePropertiesNode = node;
                state.FileId = fileId;
                state.FileIdNode = fileIdNode;
                state.FileSize = fileSize;
                state.FileSizeNode = fileSizeNode;
                state.CreationDate = creation;
                state.PacketCount = packets;
                state.PacketCountNode = packetsNode;
                state.PlayDuration = play;
                state.SendDuration = send;
                state.Preroll = preroll;
                state.Flags = flags;
                state.MinPacketSize = min;
                state.MaxPacketSize = max;
                state.PacketSizeNode = minNode;
                state.MaxBitrate = bitrate;
            }

            if ((flags & ~3u) != 0)
            {
                state.Findings.AddNow(Severity.Info, "asf.file-flags-reserved", $"The File Properties Object sets reserved flag bits (0x{flags & ~3u:X8}); the specification sets them to 0.", flagsNode);
            }
        });

        node.Value = node.Length >= 104 ? $"{state.PlayDuration / 10_000_000.0:F3} s" : null;
    }

    // ---- Stream Properties Object (section 3.3) ----

    private static void ParseStreamProperties(State state, Node parent, ObjectHeader header, long end, StreamEntry? embedded)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, "Stream Properties Object", Explain.StreamProperties);
        var entry = embedded ?? new StreamEntry();
        entry.Properties = node;
        entry.IsInExtension = embedded is not null;
        state.StreamPropertiesCount++;

        Body(state, node, f, 78, () =>
        {
            entry.Type = f.Guid("Stream Type", Explain.StreamType, AsfGuids.StreamTypeName);
            entry.ErrorCorrectionType = f.Guid("Error Correction Type", Explain.StreamErrorCorrectionType,
                g => g == AsfGuids.NoErrorCorrection ? "No error correction" : g == AsfGuids.AudioSpread ? "Audio spread" : null);
            f.Qword("Time Offset", Explain.TimeOffset, v => NodeValue.Decimal(v / 10_000_000.0, 3, "s"));
            uint typeLength = f.Dword("Type-Specific Data Length", Explain.TypeSpecificDataLength, "bytes");
            Node typeLengthNode = node.Children[^1];
            uint errorLength = f.Dword("Error Correction Data Length", Explain.ErrorCorrectionDataLength, "bytes");
            Node errorLengthNode = node.Children[^1];
            long flagsAt = f.Position;
            ushort flags = f.Word("Flags", Explain.StreamFlags, v => NodeValue.Hex(v, 4));
            Node flagsNode = node.Children[^1];
            entry.Flags = flags;
            entry.Number = flags & 0x7F;
            Fields.Bits(flagsNode, "Stream Number", flagsAt, 0, 7, NodeValue.Integer(entry.Number), Explain.StreamNumber);
            Fields.Bits(flagsNode, "Reserved", flagsAt, 7, 8, NodeValue.Hex((flags >> 7) & 0xFF, 2), Explain.StreamFlagsReserved);
            Fields.Bits(flagsNode, "Encrypted Content Flag", flagsAt, 15, 1, NodeValue.Boolean(entry.IsEncrypted), Explain.EncryptedContentFlag);
            f.Dword("Reserved", Explain.StreamReserved, v => NodeValue.Hex(v, 8));
            node.Value = $"stream {entry.Number}, {entry.TypeName.ToLowerInvariant()}";

            long remaining = f.Remaining;
            long typeBytes = Math.Min(typeLength, remaining);
            if (typeLength > remaining)
            {
                state.Findings.AddNow(Severity.Warning, "asf.stream-data-length",
                    $"Stream {entry.Number} states {typeLength} bytes of type-specific data, but the object holds {remaining} after its fixed fields.", typeLengthNode);
            }

            if (typeBytes > 0)
            {
                ParseTypeSpecificData(state, f, entry, typeBytes);
            }

            long errorBytes = Math.Min(errorLength, f.Remaining);
            if (errorLength > f.Remaining)
            {
                state.Findings.AddNow(Severity.Warning, "asf.stream-data-length",
                    $"Stream {entry.Number} states {errorLength} bytes of error correction data, but the object holds {f.Remaining} after its type-specific data.", errorLengthNode);
            }

            if (errorBytes > 0)
            {
                ParseErrorCorrectionData(state, f, entry, errorBytes);
            }
        });

        if (embedded is null)
        {
            RegisterStream(state, entry, node);
        }
    }

    // Records a stream; a stream that a Stream Properties Object and an Extended Stream Properties
    // Object both describe is one stream.
    private static void RegisterStream(State state, StreamEntry entry, Node node)
    {
        if (entry.Number == 0)
        {
            state.Findings.AddNow(Severity.Warning, "asf.stream-number", $"The Stream Properties Object at 0x{node.Offset:X} gives the stream number 0, which is not a stream (section 3.3).", node);
            return;
        }

        if (state.StreamsByNumber.TryGetValue(entry.Number, out StreamEntry? existing))
        {
            // Mutually exclusive streams of one number cannot be told apart; the first keeps it.
            state.Findings.AddNow(Severity.Warning, "asf.stream-number-reused", $"Stream number {entry.Number} is described by a second Stream Properties Object at 0x{node.Offset:X}; the first at 0x{existing.Properties!.Offset:X} counts.", node);
            return;
        }

        state.Streams.Add(entry);
        state.StreamsByNumber[entry.Number] = entry;
    }

    private static void ParseTypeSpecificData(State state, Fields f, StreamEntry entry, long length)
    {
        long start = f.Position;
        var data = new Node("Type-Specific Data", start, length, NodeValue.Integer(length, "bytes", "byte"))
        {
            Description = entry.IsAudio ? Explain.AudioTypeSpecificData : Explain.TypeSpecificData,
            Topic = entry.IsAudio ? Topics.WaveFormat : Topics.AsfContainer,
        };
        f.Node.Add(data);
        entry.TypeSpecific = data;
        if (!entry.IsAudio)
        {
            f.Reader.Skip(length);
            return;
        }

        ByteReader slice = f.Reader.Slice(start, length);
        var wave = new Fields(slice, data);
        try
        {
            entry.Format = ReadWaveFormat(state, wave, entry, length);
        }
        catch (EndOfStreamException)
        {
            state.Findings.AddNow(Severity.Warning, "asf.waveformat-truncated",
                $"Stream {entry.Number} holds {length} bytes of type-specific data, too few for a WAVEFORMATEX (16 bytes, section 9.1).", data);
        }

        f.Reader.Position = start + length;
    }

    // WAVEFORMATEX (section 9.1) and, for the extensible form, WAVEFORMATEXTENSIBLE.
    private static WaveFormat ReadWaveFormat(State state, Fields f, StreamEntry entry, long length)
    {
        ushort tag = f.Word("Format Tag", Explain.FormatTag, v => FormatTagValue(v), Topics.WaveFormat);
        ushort channels = f.Word("Channels", Explain.Channels);
        uint rate = f.Dword("Samples Per Second", Explain.SamplesPerSecond, "Hz");
        uint average = f.Dword("Average Bytes Per Second", Explain.AverageBytesPerSecond, "bytes/s");
        ushort align = f.Word("Block Alignment", Explain.BlockAlignment, "bytes");
        ushort bits = f.Word("Bits Per Sample", Explain.BitsPerSample, "bits");
        int extra = 0;
        int? valid = null;
        uint? mask = null;
        Guid? sub = null;
        if (length >= 18)
        {
            extra = f.Word("Codec Specific Data Size", Explain.CodecSpecificDataSize, "bytes");
            if (extra > f.Remaining)
            {
                state.Findings.AddNow(Severity.Warning, "asf.waveformat-extra",
                    $"Stream {entry.Number}'s WAVEFORMATEX states {extra} bytes of codec-specific data, but {f.Remaining} follow; they are read as far as they go.", f.Node.Children[^1]);
                extra = (int)f.Remaining;
            }

            if (tag == 0xFFFE && extra >= 22)
            {
                // WAVEFORMATEXTENSIBLE: the valid bits, the channel mask and the sub-format GUID,
                // whose first 32 bits are the format tag of the coding.
                long extensionStart = f.Position;
                valid = f.Word("Valid Bits Per Sample", Explain.ValidBitsPerSample, "bits");
                mask = f.Dword("Channel Mask", Explain.ChannelMask, v => NodeValue.Hex(v, 8), Topics.ChannelLayouts);
                sub = f.Guid("Sub-Format", Explain.SubFormat);
                if (extra > 22)
                {
                    long restLength = extra - 22;
                    Node rest = f.Bytes("Codec Specific Data", restLength, Explain.CodecSpecificData);
                    entry.CodecSpecific = rest;
                    entry.CodecSpecificOffset = rest.Offset;
                    entry.CodecSpecificLength = (int)restLength;
                }

                _ = extensionStart;
            }
            else if (extra > 0)
            {
                Node codec = f.Bytes("Codec Specific Data", extra, Explain.CodecSpecificData);
                entry.CodecSpecific = codec;
                entry.CodecSpecificOffset = codec.Offset;
                entry.CodecSpecificLength = extra;
            }
        }

        if (f.Remaining > 0)
        {
            f.Bytes("Extra Data", f.Remaining, Explain.ExtraData);
        }

        return new WaveFormat(tag, channels, (int)Math.Min(rate, int.MaxValue), (int)Math.Min(average, int.MaxValue), align, bits, extra, valid, mask, sub);
    }

    internal static NodeValue FormatTagValue(int tag) => NodeValue.Text($"0x{tag:X4} ({FormatTagName(tag)})");

    internal static string FormatTagName(int tag) => tag switch
    {
        0x0001 => "PCM",
        0x0003 => "IEEE float",
        0x000A => "Windows Media Audio Voice",
        0x0050 => "MPEG audio layer I and II",
        0x0055 => "MPEG audio layer III",
        0x0160 => "Windows Media Audio 1",
        0x0161 => "Windows Media Audio 2",
        0x0162 => "Windows Media Audio Professional",
        0x0163 => "Windows Media Audio Lossless",
        0x7A21 => "GSM-AMR, fixed bitrate",
        0x7A22 => "GSM-AMR, variable bitrate",
        0xFFFE => "extensible",
        _ => "unknown",
    };

    // Section 9.1.1: the error correction data of audio, spread across packets.
    private static void ParseErrorCorrectionData(State state, Fields f, StreamEntry entry, long length)
    {
        long start = f.Position;
        var data = new Node("Error Correction Data", start, length, NodeValue.Integer(length, "bytes", "byte"))
        {
            Description = entry.ErrorCorrectionType == AsfGuids.AudioSpread ? Explain.SpreadAudio : Explain.ErrorCorrectionData,
            Topic = Topics.AsfContainer,
        };
        f.Node.Add(data);
        if (entry.ErrorCorrectionType != AsfGuids.AudioSpread || length < 8)
        {
            f.Reader.Skip(length);
            return;
        }

        var spread = new Fields(f.Reader.Slice(start, length), data);
        try
        {
            byte span = spread.Byte("Span", Explain.Span, "packets");
            ushort packet = spread.Word("Virtual Packet Length", Explain.VirtualPacketLength, "bytes");
            ushort chunk = spread.Word("Virtual Chunk Length", Explain.VirtualChunkLength, "bytes");
            ushort silence = spread.Word("Silence Data Length", Explain.SilenceDataLength, "bytes");
            entry.Spread = new SpreadAudio(span, packet, chunk, silence);
            if (silence > spread.Remaining)
            {
                state.Findings.AddNow(Severity.Warning, "asf.spread-silence", $"Stream {entry.Number}'s spread audio data states {silence} bytes of silence data, but {spread.Remaining} follow.", data.Children[^1]);
                silence = (ushort)spread.Remaining;
            }

            if (silence > 0)
            {
                spread.Bytes("Silence Data", silence, Explain.SilenceData);
            }

            if (spread.Remaining > 0)
            {
                spread.Bytes("Extra Data", spread.Remaining, Explain.ExtraData);
            }
        }
        catch (EndOfStreamException)
        {
            state.Findings.AddNow(Severity.Warning, "asf.object-truncated", $"Stream {entry.Number}'s error correction data holds {length} bytes, too few for the spread audio fields.", data);
        }

        f.Reader.Position = start + length;
    }

    // ---- Header Extension Object (section 3.4) ----

    private static void ParseHeaderExtension(State state, Node parent, ObjectHeader header, long end, int depth)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, "Header Extension Object", Explain.HeaderExtension);
        state.HeaderExtensionNode = node;
        state.HeaderExtensionCount++;
        Body(state, node, f, 46, () =>
        {
            Guid reserved = f.Guid("Reserved Field 1", Explain.ReservedField1);
            ushort reserved2 = f.Word("Reserved Field 2", Explain.ReservedField2);
            uint size = f.Dword("Header Extension Data Size", Explain.HeaderExtensionDataSize, "bytes");
            if (reserved != AsfGuids.Reserved1)
            {
                state.Findings.AddNow(Severity.Info, "asf.extension-reserved", $"The Header Extension Object's Reserved Field 1 is {AsfGuids.Text(reserved)}; the specification sets it to ASF_Reserved_1.", node.Children[^3]);
            }

            if (reserved2 != 6)
            {
                state.Findings.AddNow(Severity.Info, "asf.extension-reserved", $"The Header Extension Object's Reserved Field 2 is {reserved2}; the specification recommends 6.", node.Children[^2]);
            }

            long dataStart = f.Position;
            long available = end - dataStart;
            if (size != available)
            {
                state.Findings.AddNow(Severity.Warning, "asf.extension-size",
                    $"The Header Extension Object states {size} bytes of data, but its size leaves {available}; the specification makes the two equal (section 3.4).", node.Children[^1]);
            }

            if (size != 0 && size < ObjectHeaderSize)
            {
                state.Findings.AddNow(Severity.Warning, "asf.extension-size", $"The Header Extension Object states {size} bytes of data; the specification allows 0 or at least 24.", node.Children[^1]);
            }

            long dataEnd = Math.Min(end, dataStart + size);
            int before = state.ChildCount;
            ReadObjects(state, node, dataStart, dataEnd, inExtension: true, depth + 1);
            state.ChildCount = before;
            node.Value = NodeValue.Integer(node.Children.Count(c => c.Name.EndsWith("Object", StringComparison.Ordinal)), "objects", "object");
            if (dataEnd < end)
            {
                node.Add(new Node("Extra Data", dataEnd, end - dataEnd, NodeValue.Integer(end - dataEnd, "bytes", "byte")) { Description = Explain.ExtraData });
            }
        }, coversRest: false);
    }

    // ---- Codec List Object (section 3.5) ----

    private static void ParseCodecList(State state, Node parent, ObjectHeader header, long end)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, "Codec List Object", Explain.CodecList);
        state.CodecListNode = node;
        Body(state, node, f, 44, () =>
        {
            Guid reserved = f.Guid("Reserved", Explain.CodecListReserved);
            uint count = f.Dword("Codec Entries Count", Explain.CodecEntriesCount);
            if (reserved != AsfGuids.Reserved2)
            {
                state.Findings.AddNow(Severity.Info, "asf.codec-list-reserved", $"The Codec List Object's reserved GUID is {AsfGuids.Text(reserved)}; the specification sets it to ASF_Reserved_2.", node.Children[^2]);
            }

            node.Value = NodeValue.Integer(count, "codecs", "codec");
            for (uint i = 0; i < count; i++)
            {
                if (i == MaxListEntries)
                {
                    TooManyEntries(state, node, f, $"{count} codec entries", "from");
                    break;
                }

                if (f.Remaining < 10)
                {
                    state.Findings.AddNow(Severity.Warning, "asf.codec-list-count", $"The Codec List Object counts {count} entries, but its data holds {i}.", node.Children[^1]);
                    break;
                }

                long at = f.Position;
                var entry = new Node("Codec Entry", at, 0) { Description = Explain.CodecEntry };
                var e = new Fields(f.Reader, entry);
                ushort type = e.Word("Type", Explain.CodecType, v => NodeValue.Text(v switch { 1 => "Video codec", 2 => "Audio codec", 0xFFFF => "Unknown codec", _ => $"0x{v:X4}" }));
                ushort nameLength = e.Word("Codec Name Length", Explain.CodecNameLength, "characters");
                string name = e.Unicode("Codec Name", nameLength * 2L, Explain.CodecName);
                ushort descriptionLength = e.Word("Codec Description Length", Explain.CodecDescriptionLength, "characters");
                string description = e.Unicode("Codec Description", descriptionLength * 2L, Explain.CodecDescription);
                ushort infoLength = e.Word("Codec Information Length", Explain.CodecInformationLength, "bytes");
                if (infoLength > 0)
                {
                    e.Bytes("Codec Information", infoLength, Explain.CodecInformation);
                }

                entry = new Node("Codec Entry", at, f.Position - at, name) { Description = Explain.CodecEntry };
                foreach (Node child in e.Node.Children)
                {
                    entry.Add(child);
                }

                node.Add(entry);
                state.Codecs.Add((name, description, type));
            }
        });
    }

    // ---- Script Command Object (3.6) and Marker Object (3.7) ----

    private static void ParseScriptCommand(State state, Node parent, ObjectHeader header, long end)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, "Script Command Object", Explain.ScriptCommand);
        Body(state, node, f, 44, () =>
        {
            f.Guid("Reserved", Explain.ScriptReserved);
            ushort commands = f.Word("Commands Count", Explain.CommandsCount);
            ushort typeCount = f.Word("Command Types Count", Explain.CommandTypesCount);
            var types = new List<string>();
            for (int i = 0; i < typeCount; i++)
            {
                ushort length = f.Word("Command Type Name Length", Explain.CommandTypeNameLength, "characters");
                types.Add(f.Unicode("Command Type Name", length * 2L, Explain.CommandTypeName));
            }

            for (int i = 0; i < commands; i++)
            {
                if (i == 1000)
                {
                    f.Bytes("Further Commands", f.Remaining, Explain.FurtherEntries);
                    break;
                }

                long at = f.Position;
                var entry = new Node("Command", at, 0) { Description = Explain.Command };
                var c = new Fields(f.Reader, entry);
                uint time = c.Dword("Presentation Time", Explain.CommandPresentationTime, "ms");
                ushort typeIndex = c.Word("Type Index", Explain.CommandTypeIndex);
                ushort length = c.Word("Command Name Length", Explain.CommandNameLength, "characters");
                string name = c.Unicode("Command Name", length * 2L, Explain.CommandName);
                string typeName = typeIndex < types.Count ? types[typeIndex] : "?";
                var shown = new Node("Command", at, f.Position - at, $"{typeName}: {name} at {time} ms") { Description = Explain.Command };
                foreach (Node child in entry.Children)
                {
                    shown.Add(child);
                }

                node.Add(shown);
                if (typeIndex >= types.Count)
                {
                    state.Findings.AddNow(Severity.Warning, "asf.command-type-index", $"A script command at 0x{at:X} names command type {typeIndex}, but the object lists {types.Count}.", shown);
                }
            }

            node.Value = NodeValue.Integer(commands, "commands", "command");
        });
    }

    private static void ParseMarker(State state, Node parent, ObjectHeader header, long end)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, "Marker Object", Explain.Marker);
        Body(state, node, f, 48, () =>
        {
            f.Guid("Reserved", Explain.MarkerReserved);
            uint count = f.Dword("Markers Count", Explain.MarkersCount);
            f.Word("Reserved", Explain.MarkerReservedWord);
            ushort nameLength = f.Word("Name Length", Explain.MarkerNameLength, "bytes");
            f.Unicode("Name", nameLength, Explain.MarkerName);
            node.Value = NodeValue.Integer(count, "markers", "marker");
            for (uint i = 0; i < count; i++)
            {
                if (i == 1000)
                {
                    f.Bytes("Further Markers", f.Remaining, Explain.FurtherEntries);
                    break;
                }

                long at = f.Position;
                var entry = new Node("Marker", at, 0) { Description = Explain.MarkerEntry };
                var m = new Fields(f.Reader, entry);
                m.Qword("Offset", Explain.MarkerOffset, "bytes");
                m.Qword("Presentation Time", Explain.MarkerPresentationTime, Fields.Seconds);
                ushort entryLength = m.Word("Entry Length", Explain.MarkerEntryLength, "bytes");
                m.Dword("Send Time", Explain.MarkerSendTime, "ms");
                m.Dword("Flags", Explain.MarkerFlags, v => NodeValue.Hex(v, 8));
                uint descriptionLength = m.Dword("Marker Description Length", Explain.MarkerDescriptionLength, "characters");
                string description = m.Unicode("Marker Description", descriptionLength * 2L, Explain.MarkerDescription);
                var shown = new Node("Marker", at, f.Position - at, description) { Description = Explain.MarkerEntry };
                foreach (Node child in entry.Children)
                {
                    shown.Add(child);
                }

                node.Add(shown);
                long expected = 4 + 4 + 4 + (descriptionLength * 2L);
                if (entryLength != expected)
                {
                    state.Findings.AddNow(Severity.Warning, "asf.marker-entry-length",
                        $"A marker at 0x{at:X} states an entry length of {entryLength} bytes; its send time, flags, description length and description take {expected} (section 3.7).", shown);
                }
            }
        });
    }

    // ---- Mutual exclusion objects (3.8, 4.2, 4.3), prioritization (4.4), bandwidth sharing (4.5) ----

    private static void ParseMutualExclusion(State state, Node parent, ObjectHeader header, long end, string name, string description, long minimum)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, name, description);
        Body(state, node, f, minimum, () =>
        {
            Guid type = f.Guid("Exclusion Type", Explain.ExclusionType, AsfGuids.ExclusionName);
            ushort count = f.Word("Stream Numbers Count", Explain.StreamNumbersCount);
            var numbers = new List<int>();
            long at = f.Position;
            for (int i = 0; i < count; i++)
            {
                numbers.Add(f.Reader.ReadUInt16LittleEndian());
            }

            if (count > 0)
            {
                node.Add("Stream Numbers", at, count * 2L, string.Join(", ", numbers), Explain.StreamNumbersList);
            }

            node.Value = $"{AsfGuids.ExclusionName(type) ?? "unknown"}: streams {string.Join(", ", numbers)}";
        });
    }

    private static void ParseGroupMutualExclusion(State state, Node parent, ObjectHeader header, long end)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, "Group Mutual Exclusion Object", Explain.GroupMutualExclusion);
        Body(state, node, f, 42, () =>
        {
            Guid type = f.Guid("Exclusion Type", Explain.ExclusionType, AsfGuids.ExclusionName);
            ushort records = f.Word("Record Count", Explain.RecordCount);
            node.Value = $"{AsfGuids.ExclusionName(type) ?? "unknown"}: {records} records";
            for (int i = 0; i < records; i++)
            {
                long at = f.Position;
                ushort count = f.Reader.ReadUInt16LittleEndian();
                var numbers = new List<int>();
                for (int j = 0; j < count; j++)
                {
                    numbers.Add(f.Reader.ReadUInt16LittleEndian());
                }

                node.Add("Record", at, f.Position - at, $"streams {string.Join(", ", numbers)}", Explain.ExclusionRecord);
            }
        });
    }

    private static void ParseStreamPrioritization(State state, Node parent, ObjectHeader header, long end)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, "Stream Prioritization Object", Explain.StreamPrioritization);
        Body(state, node, f, 26, () =>
        {
            ushort count = f.Word("Priority Records Count", Explain.PriorityRecordsCount);
            node.Value = NodeValue.Integer(count, "records", "record");
            for (int i = 0; i < count; i++)
            {
                long at = f.Position;
                ushort stream = f.Reader.ReadUInt16LittleEndian();
                ushort flags = f.Reader.ReadUInt16LittleEndian();
                var record = node.Add("Priority Record", at, 4, $"stream {stream}{((flags & 1) != 0 ? ", mandatory" : string.Empty)}", Explain.PriorityRecord);
                record.Add("Stream Number", at, 2, NodeValue.Integer(stream), Explain.PriorityStreamNumber);
                Node flagsNode = record.Add("Priority Flags", at + 2, 2, NodeValue.Hex(flags, 4), Explain.PriorityFlags);
                Fields.Bits(flagsNode, "Mandatory Flag", at + 2, 0, 1, NodeValue.Boolean((flags & 1) != 0), Explain.MandatoryFlag);
            }
        });
    }

    private static void ParseBandwidthSharing(State state, Node parent, ObjectHeader header, long end)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, "Bandwidth Sharing Object", Explain.BandwidthSharing);
        Body(state, node, f, 50, () =>
        {
            Guid type = f.Guid("Sharing Type", Explain.SharingType,
                g => g == AsfGuids.BandwidthSharingExclusive ? "Exclusive" : g == AsfGuids.BandwidthSharingPartial ? "Partial" : null);
            f.Dword("Data Bitrate", Explain.SharingDataBitrate, "bps");
            f.Dword("Buffer Size", Explain.SharingBufferSize, "bits");
            ushort count = f.Word("Stream Numbers Count", Explain.StreamNumbersCount);
            var numbers = new List<int>();
            long at = f.Position;
            for (int i = 0; i < count; i++)
            {
                numbers.Add(f.Reader.ReadUInt16LittleEndian());
            }

            if (count > 0)
            {
                node.Add("Stream Numbers", at, count * 2L, string.Join(", ", numbers), Explain.StreamNumbersList);
            }

            node.Value = $"{(type == AsfGuids.BandwidthSharingExclusive ? "exclusive" : type == AsfGuids.BandwidthSharingPartial ? "partial" : "unknown")}: streams {string.Join(", ", numbers)}";
        });
    }

    // ---- Error Correction Object (3.9), Padding Object (3.17), Compatibility Object (4.12) ----

    private static void ParseErrorCorrection(State state, Node parent, ObjectHeader header, long end)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, "Error Correction Object", Explain.ErrorCorrectionObject);
        Body(state, node, f, 44, () =>
        {
            Guid type = f.Guid("Error Correction Type", Explain.ErrorCorrectionObjectType,
                g => g == AsfGuids.NoErrorCorrection ? "No error correction" : g == AsfGuids.AudioSpread ? "Audio spread" : null);
            uint length = f.Dword("Error Correction Data Length", Explain.ErrorCorrectionDataLength, "bytes");
            if (length > f.Remaining)
            {
                state.Findings.AddNow(Severity.Warning, "asf.object-truncated", $"The Error Correction Object states {length} bytes of data, but {f.Remaining} follow.", node.Children[^1]);
                length = (uint)f.Remaining;
            }

            if (length > 0)
            {
                f.Bytes("Error Correction Data", length, Explain.ErrorCorrectionData);
            }

            node.Value = AsfGuids.Describe(type, type == AsfGuids.NoErrorCorrection ? "no error correction" : type == AsfGuids.AudioSpread ? "audio spread" : null);
        });
    }

    private static void ParseIndexPlaceholder(State state, Node parent, ObjectHeader header, long end)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, "Index Placeholder Object", Explain.IndexPlaceholder);
        Body(state, node, f, 24, () =>
        {
            long length = f.Remaining;
            if (length > 0)
            {
                f.Bytes("Data", length, Explain.IndexPlaceholderData);
            }

            node.Value = NodeValue.Integer(length, "bytes", "byte");
        });
    }

    private static void ParsePadding(State state, Node parent, ObjectHeader header, long end)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, "Padding Object", Explain.PaddingObject);
        Body(state, node, f, 24, () =>
        {
            long length = f.Remaining;
            if (length > 0)
            {
                f.Bytes("Padding Data", length, Explain.PaddingData);
            }

            node.Value = NodeValue.Integer(length, "bytes", "byte");
        });
    }

    private static void ParseCompatibility(State state, Node parent, ObjectHeader header, long end)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, "Compatibility Object", Explain.CompatibilityObject);
        Body(state, node, f, 26, () =>
        {
            f.Byte("Profile", Explain.CompatibilityProfile);
            f.Byte("Mode", Explain.CompatibilityMode);
        });
    }

    // ---- Encryption and signature objects (3.14, 3.15, 3.16, 4.13): their fields are read, nothing is decrypted or verified ----

    private static void ParseContentEncryption(State state, Node parent, ObjectHeader header, long end)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, "Content Encryption Object", Explain.ContentEncryption, Topics.AsfContentProtection);
        state.Protections.Add(new ProtectionObject(ProtectionSchemes.DrmVersion1, node, [], [], IsWholeFile: true));
        Body(state, node, f, 40, () =>
        {
            uint secret = f.Dword("Secret Data Length", Explain.SecretDataLength, "bytes");
            f.Bytes("Secret Data", Math.Min(secret, f.Remaining), Explain.SecretData);
            uint protectionLength = f.Dword("Protection Type Length", Explain.ProtectionTypeLength, "bytes");
            string protection = f.Ascii("Protection Type", Math.Min(protectionLength, f.Remaining), Explain.ProtectionType);
            Node protectionNode = node.Children[^1];
            uint keyLength = f.Dword("Key ID Length", Explain.KeyIdLength, "bytes");
            f.Ascii("Key ID", Math.Min(keyLength, f.Remaining), Explain.KeyId);
            uint urlLength = f.Dword("License URL Length", Explain.LicenseUrlLength, "bytes");
            f.Ascii("License URL", Math.Min(urlLength, f.Remaining), Explain.LicenseUrl);
            node.Value = protection;
            if (protection != "DRM")
            {
                state.Findings.AddNow(Severity.Warning, "asf.protection-type", $"The Content Encryption Object's protection type is \"{protection}\"; section 3.14 sets it to \"DRM\".", protectionNode);
            }
        });
        state.Findings.AddNow(Severity.Info, "asf.content-encrypted", "The file is protected by Microsoft Digital Rights Manager version 1; its streams are encrypted and are reported as the file holds them.", node);
    }

    private static void ParseExtendedContentEncryption(State state, Node parent, ObjectHeader header, long end)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, "Extended Content Encryption Object", Explain.ExtendedContentEncryption, Topics.AsfContentProtection);
        state.Protections.Add(new ProtectionObject(ProtectionSchemes.RightsManager7, node, [], [], IsWholeFile: true));
        Body(state, node, f, 24, () =>
        {
            uint size = f.Dword("Data Size", Explain.EncryptionDataSize, "bytes");
            f.Bytes("Data", Math.Min(size, f.Remaining), Explain.EncryptionData);
        });
        state.Findings.AddNow(Severity.Info, "asf.content-encrypted", "The file is protected by Windows Media Rights Manager 7; its streams are encrypted and are reported as the file holds them.", node);
    }

    // Section 3.16: a signature of the header between the File Properties Object and this object. It protects nothing, so no stream is protected by it.
    private static void ParseDigitalSignature(State state, Node parent, ObjectHeader header, long end)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, "Digital Signature Object", Explain.DigitalSignature, Topics.AsfContentProtection);
        Body(state, node, f, 32, () =>
        {
            uint type = f.Dword("Signature Type", Explain.SignatureType);
            Node typeNode = node.Children[^1];
            uint size = f.Dword("Signature Data Length", Explain.SignatureDataLength, "bytes");
            f.Bytes("Signature Data", Math.Min(size, f.Remaining), Explain.SignatureData);
            node.Value = NodeValue.Integer(Math.Min(size, node.Length), "bytes", "byte");
            if (type != 2)
            {
                state.Findings.AddNow(Severity.Warning, "asf.signature-type", $"The Digital Signature Object's signature type is {type}; section 3.16 sets it to 2.", typeNode);
            }
        });
    }

    private static void ParseAdvancedContentEncryption(State state, Node parent, ObjectHeader header, long end)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, "Advanced Content Encryption Object", Explain.AdvancedContentEncryption, Topics.AsfContentProtection);
        int recordsBefore = state.Protections.Count;
        bool isRead = false;
        var unreadSystems = new List<string>();
        Body(state, node, f, 26, () =>
        {
            ushort count = f.Word("Content Encryption Records Count", Explain.ContentEncryptionRecordsCount);
            node.Value = NodeValue.Integer(count, "records", "record");
            int recordNodes = 0;
            bool stopped = false;
            for (int i = 0; i < count && !stopped; i++)
            {
                if (i == MaxListEntries)
                {
                    TooManyEntries(state, node, f, $"{count} content encryption records", "from");
                    return;
                }

                long at = f.Position;
                var entry = new Node("Content Encryption Record", at, 0) { Description = Explain.ContentEncryptionRecord };
                var r = new Fields(f.Reader, entry);
                var streams = new List<(int Number, Node Node)>();
                var pending = new List<(ushort Type, ushort Length, Node Node)>();
                string? system = null;

                // Section 4.13 says only that the length of a stream number "should" be 2 and defines no other type. An object record that
                // cannot be read as a stream number leaves the record's scope unknown, and a record that protects nothing it names would
                // pass ciphertext on as clear data, so the record is taken to cover every stream, as an object that cannot be read is.
                bool coversAll = false;

                // The record as far as it was read is kept, with the streams it names, whether it ends here for want of bytes or entries.
                void Attach(long stop)
                {
                    var shown = new Node("Content Encryption Record", at, stop - at) { Description = Explain.ContentEncryptionRecord };
                    foreach (Node child in entry.Children)
                    {
                        shown.Add(child);
                    }

                    node.Add(shown);
                    foreach ((ushort type, ushort length, Node idTypeNode) in pending)
                    {
                        state.Findings.AddNow(type == 1 ? Severity.Warning : Severity.Info, type == 1 ? "asf.encrypted-object-id-length" : "asf.encrypted-object-id-type",
                            type == 1
                                ? $"An Encrypted Object Record gives a stream number in {length} bytes; section 4.13 recommends 2 for type 1, a WORD. Which stream it names is not known, so the record is taken to cover every stream."
                                : $"An Encrypted Object Record has the ID type 0x{type:X4}; section 4.13 defines only 1, a stream number, so which object it covers is not known. The record is taken to cover every stream.", idTypeNode);
                    }

                    state.Protections.Add(new ProtectionObject(ProtectionSchemes.AdvancedEncryption, shown, [system!], streams, IsWholeFile: coversAll));
                }

                try
                {
                    Guid id = r.Guid("System ID", Explain.EncryptionSystemId, AsfGuids.EncryptionSystemName);
                    system = AsfGuids.EncryptionSystemName(id) ?? AsfGuids.Text(id);
                    r.Dword("System Version", Explain.EncryptionSystemVersion);
                    ushort objects = r.Word("Encrypted Object Record Count", Explain.EncryptedObjectRecordCount);
                    for (int j = 0; j < objects; j++)
                    {
                        if (++recordNodes > MaxListEntries)
                        {
                            // The record so far is shown, and the rest of the object is Extra Data.
                            TooManyEntries(state, node, f, $"{objects} encrypted object records in record {i + 1} of {count}", "from");
                            Attach(r.Position);
                            unreadSystems.Add(system!);
                            stopped = true;
                            break;
                        }

                        long recordAt = r.Position;
                        var objectRecord = new Node("Encrypted Object Record", recordAt, 0) { Description = Explain.EncryptedObjectRecord };
                        var o = new Fields(r.Reader, objectRecord);
                        ushort idType = o.Word("Encrypted Object ID Type", Explain.EncryptedObjectIdType);
                        ushort idLength = o.Word("Encrypted Object ID Length", Explain.EncryptedObjectIdLength, "bytes");
                        Node idTypeNode = objectRecord.Children[^2];
                        bool isStream = idType == 1 && idLength == 2;
                        if (isStream)
                        {
                            streams.Add((o.Word("Encrypted Object ID", Explain.EncryptedObjectStreamNumber), objectRecord.Children[^1]));
                        }
                        else
                        {
                            o.Bytes("Encrypted Object ID", idLength, Explain.EncryptedObjectId);
                        }

                        var shownRecord = new Node("Encrypted Object Record", recordAt, r.Position - recordAt) { Description = Explain.EncryptedObjectRecord };
                        foreach (Node child in objectRecord.Children)
                        {
                            shownRecord.Add(child);
                        }

                        entry.Add(shownRecord);
                        if (!isStream)
                        {
                            coversAll = true;
                            pending.Add((idType, idLength, idTypeNode));
                        }
                    }

                    if (stopped)
                    {
                        return;
                    }

                    uint dataSize = r.Dword("Data Size", Explain.EncryptionDataSize, "bytes");
                    if (dataSize > r.Remaining)
                    {
                        state.Findings.AddNow(Severity.Warning, "asf.encryption-data-size", $"A content encryption record of the Advanced Content Encryption Object gives {dataSize} bytes of data, but {r.Remaining} remain in the object.", entry.Children[^1]);
                    }

                    r.Bytes("Data", Math.Min(dataSize, r.Remaining), Explain.EncryptionData);
                    Attach(f.Position);
                }
                catch (EndOfStreamException) when (system is not null)
                {
                    Attach(r.Position);
                    unreadSystems.Add(system!);
                    throw;
                }
            }

            isRead = !stopped;
        });
        if (!isRead)
        {
            // The records were not all read: the streams the unread ones name are not known, so every stream is taken to be covered.
            state.Protections.Add(new ProtectionObject(ProtectionSchemes.AdvancedEncryption, node, [.. unreadSystems], [], IsWholeFile: true));
        }

        var added = state.Protections.Skip(recordsBefore).ToList();
        string covered = !isRead
            ? "the object could not be read in full, so every stream is taken to be encrypted and is reported as the file holds it."
            : added.Any(p => p.IsWholeFile) ? "a record names its objects in a way that is not a stream number, so every stream is taken to be encrypted and is reported as the file holds it."
            : added.Any(p => p.Streams.Count > 0) ? "the streams that its records name (0 names them all) are encrypted and are reported as the file holds them."
            : "no record names a stream, so the object protects none.";
        state.Findings.AddNow(Severity.Info, added.Count > 0 ? "asf.content-encrypted" : "asf.no-content-encryption-record",
            added.Count > 0
                ? $"The file is protected by Windows Media DRM for network devices; {covered}"
                : "The Advanced Content Encryption Object holds no content encryption record, so it protects no stream.", node);
    }

    // ---- Stream Bitrate Properties Object (3.12) ----

    private static void ParseStreamBitrateProperties(State state, Node parent, ObjectHeader header, long end)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, "Stream Bitrate Properties Object", Explain.StreamBitrateProperties);
        Body(state, node, f, 26, () =>
        {
            ushort count = f.Word("Bitrate Records Count", Explain.BitrateRecordsCount);
            node.Value = NodeValue.Integer(count, "records", "record");
            for (int i = 0; i < count; i++)
            {
                long at = f.Position;
                ushort flags = f.Reader.ReadUInt16LittleEndian();
                uint bitrate = f.Reader.ReadUInt32LittleEndian();
                int number = flags & 0x7F;
                Node record = node.Add("Bitrate Record", at, 6, $"stream {number}: {bitrate} bps", Explain.BitrateRecord);
                Node flagsNode = record.Add("Flags", at, 2, NodeValue.Hex(flags, 4), Explain.BitrateRecordFlags);
                Fields.Bits(flagsNode, "Stream Number", at, 0, 7, NodeValue.Integer(number), Explain.BitrateRecordStreamNumber);
                Fields.Bits(flagsNode, "Reserved", at, 7, 9, NodeValue.Hex(flags >> 7, 4), Explain.BitrateRecordReserved);
                record.Add("Average Bitrate", at + 2, 4, NodeValue.Integer(bitrate, "bps"), Explain.AverageBitrate);
                state.PendingBitrates.Add((number, bitrate, record));
            }
        });
    }

    // ---- Language List Object (4.6) ----

    private static void ParseLanguageList(State state, Node parent, ObjectHeader header, long end)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, "Language List Object", Explain.LanguageList);
        Body(state, node, f, 26, () =>
        {
            ushort count = f.Word("Language ID Records Count", Explain.LanguageRecordsCount);
            node.Value = NodeValue.Integer(count, "languages", "language");
            for (int i = 0; i < count; i++)
            {
                long at = f.Position;
                byte length = f.Reader.ReadByte();
                string text = f.ReadUnicode(length);
                Node record = node.Add("Language ID Record", at, 1 + (long)length, text, Explain.LanguageRecord);
                record.Add("Language ID Length", at, 1, NodeValue.Integer(length, "bytes", "byte"), Explain.LanguageIdLength);
                record.Add("Language ID", at + 1, length, text, Explain.LanguageId);
                state.Languages.Add(text);
            }
        });
    }

    // ---- Index Parameters Objects (4.9, 4.10, 4.11) ----

    private static void ParseIndexParameters(State state, Node parent, ObjectHeader header, long end, string name, string description, string intervalDescription)
    {
        (Node node, Fields f) = Begin(state, parent, header, end, name, description);
        bool timecode = header.Id == AsfGuids.TimecodeIndexParameters;
        bool media = header.Id == AsfGuids.MediaObjectIndexParameters;
        List<(ushort Stream, ushort Type)> kept;
        if (timecode)
        {
            state.TimecodeIndexParametersNode = node;
            kept = state.TimecodeIndexSpecifiers;
        }
        else if (media)
        {
            state.MediaIndexParametersNode = node;
            kept = state.MediaIndexSpecifiers;
        }
        else
        {
            state.IndexParametersNode = node;
            kept = state.IndexSpecifiers;
        }

        Body(state, node, f, 34, () =>
        {
            uint interval = f.Dword(header.Id == AsfGuids.IndexParameters ? "Index Entry Time Interval" : "Index Entry Count Interval", intervalDescription,
                header.Id == AsfGuids.IndexParameters ? "ms" : null);
            ushort count = f.Word("Index Specifiers Count", Explain.IndexSpecifiersCount);
            node.Value = NodeValue.Integer(count, "specifiers", "specifier");
            if (interval == 0 && header.Id != AsfGuids.TimecodeIndexParameters)
            {
                state.Findings.AddNow(Severity.Warning, "asf.index-interval", $"The {name} states an interval of 0; the specification does not allow it.", node.Children[^2]);
            }

            if (count == 0)
            {
                state.Findings.AddNow(Severity.Warning, "asf.index-specifiers", $"The {name} lists no index specifiers; the specification needs at least one.", node.Children[^1]);
            }

            for (int i = 0; i < count; i++)
            {
                long at = f.Position;
                ushort stream = f.Reader.ReadUInt16LittleEndian();
                ushort type = f.Reader.ReadUInt16LittleEndian();
                Node specifier = node.Add("Index Specifier", at, 4, $"stream {stream}, {IndexTypeName(type)}", Explain.IndexSpecifier);
                specifier.Add("Stream Number", at, 2, NodeValue.Integer(stream), Explain.IndexSpecifierStream);
                specifier.Add("Index Type", at + 2, 2, NodeValue.Text($"{type} ({IndexTypeName(type)})"), Explain.IndexType);
                kept.Add((stream, type));
                if (timecode && type == 1)
                {
                    state.Findings.AddNow(Severity.Warning, "asf.index-specifiers", $"The {name} lists index type 1, which section 4.11 says is not a valid value for a timecode index.", specifier.Children[^1]);
                }
            }
        });
    }

    internal static string IndexTypeName(int type) => type switch
    {
        1 => "nearest past data packet",
        2 => "nearest past media object",
        3 => "nearest past cleanpoint",
        0xFF => "frame number offset",
        _ => "unknown",
    };

    internal static string Plural(long count, string singular, string plural) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural)}";
}
