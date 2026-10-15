using Encspect.Core;

namespace Encspect.Formats.Mp4;

// The sample table (ISO/IEC 14496-12 8.5 to 8.7 and 8.9): the sample descriptions, the decoding
// times, the samples' chunks, sizes and the chunks' offsets, sync samples and sample groups. Entries
// are read in blocks into compact arrays, never more than the box holds, whatever count it declares.
public sealed partial class Mp4Parser
{
    private const int EntryBlock = 1 << 16;

    // Reads count entries of size bytes each, a block at a time, handing each to read with its
    // index; returns how many the box held.
    private static uint ReadEntries(State state, Node node, BoxFields f, uint count, int size, string what, Action<ReadOnlySpan<byte>, uint, long> read)
    {
        long room = f.Remaining / size;
        uint available = (uint)Math.Min(count, room);
        if (available < count)
        {
            state.Findings.AddNow(Severity.Warning, "mp4.entries-exceed-box",
                $"The {node.Name} box at 0x{node.Offset:X} declares {count} {what}, but holds room for {available}.", node);
        }

        var buffer = new byte[(int)Math.Min((long)available * size, EntryBlock - (EntryBlock % size))];
        uint index = 0;
        while (index < available)
        {
            state.Context.CancellationToken.ThrowIfCancellationRequested();
            int take = (int)Math.Min(available - index, buffer.Length / size);
            long at = f.Position;
            f.Fill(buffer.AsSpan(0, take * size));
            for (int i = 0; i < take; i++)
            {
                read(buffer.AsSpan(i * size, size), index++, at + ((long)i * size));
            }
        }

        return available;
    }

    private static void MoreEntries(Node node, uint count, long start, long end)
    {
        if (count > MaxListedEntries && end > start)
        {
            node.Add(new Node("More Entries", start, end - start, NodeValue.Integer(count - MaxListedEntries, "entries", "entry")) { Description = Explain.MoreEntries });
        }
    }

    private static uint BigEndian32(ReadOnlySpan<byte> b) => (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);

    // 8.5.2: the sample descriptions; an audio track's are audio sample entries (8.5.2.2, and the
    // sound sample descriptions of version 1 and 2 of the QuickTime File Format).
    private static void SampleDescription(State state, Node node, BoxHeader header, BoxFields f, int depth)
    {
        int version = FullBox(node, f).Version;
        uint count = f.U32(node, "Entries", Explain.SampleEntryCount);
        ReadChildren(state, node, f.Position, header.End, depth + 1, custom: (s, n, h, fields, d) => SampleEntryBox(s, n, h, fields, d, version),
            naming: h => new BoxName(h.Type, NodeKind.Detail, Explain.SampleEntry(h.Type), Topics.SampleTables));
        int read = node.Children.Count(c => c.Children.Any(k => k.Name == "Data Reference Index"));
        if (read != count)
        {
            state.Findings.AddNow(Severity.Warning, "mp4.sample-description-count",
                $"The stsd box at 0x{node.Offset:X} declares {count} sample descriptions and holds {read}.", node);
        }
    }

    private static void SampleEntryBox(State state, Node node, BoxHeader header, BoxFields f, int depth, int stsdVersion)
    {
        f.Bytes(node, "Reserved", 6, Explain.SampleEntryReserved);
        f.U16(node, "Data Reference Index", Explain.DataReferenceIndex);
        var entry = new SampleEntry(header.Type, node);
        node.Value = $"{header.Type} ({FormatName(header.Type)})";
        state.Track?.Entries.Add(entry);
        if (state.Track is { Handler: not null and not "soun" } || f.Remaining < 20)
        {
            if (f.Remaining > 0)
            {
                node.Add(new Node("Data", f.Position, f.Remaining, NodeValue.Integer(f.Remaining, "bytes")) { Description = Explain.SampleEntryData });
            }

            return;
        }

        int version = f.U16(node, "Version", Explain.SoundVersion);
        f.U16(node, "Revision Level", Explain.RevisionLevel);
        f.FourCc(node, "Vendor", Explain.Vendor);
        entry.Channels = f.U16(node, "Channels", Explain.Channels);
        entry.SampleSize = f.U16(node, "Sample Size", Explain.SampleSize, unit: "bits");
        f.Field(node, "Compression ID", 2, Explain.CompressionId, v => NodeValue.Integer((short)(ushort)v));
        f.U16(node, "Packet Size", Explain.PacketSize);
        entry.SampleRate = f.Fixed(node, "Sample Rate", 4, 16, Explain.EntrySampleRate, "Hz", isUnsigned: true);

        // QuickTime File Format, "Sound sample description (version 1 and 2)"; ISO/IEC 14496-12's own
        // version 1 keeps version 0's layout, so as in ffmpeg, the QuickTime layout applies where the
        // file is QuickTime's or the stsd box is of version 0.
        bool quickTime = state.IsQuickTime || state.HasQuickTimeBrand || (stsdVersion == 0 && version > 0);
        if (quickTime && version == 1 && f.Remaining >= 16)
        {
            f.U32(node, "Samples Per Packet", Explain.SamplesPerPacket);
            f.U32(node, "Bytes Per Packet", Explain.BytesPerPacket);
            f.U32(node, "Bytes Per Frame", Explain.BytesPerFrame);
            f.U32(node, "Bytes Per Sample", Explain.BytesPerSample);
        }
        else if (quickTime && version == 2 && f.Remaining >= 36)
        {
            f.U32(node, "Structure Size", Explain.StructureSize, unit: "bytes");
            long at = f.Position;
            double rate = BitConverter.Int64BitsToDouble((long)f.Take(8));
            node.Add(new Node("Audio Sample Rate", at, 8, double.IsFinite(rate) ? NodeValue.Decimal(rate, 3, "Hz") : "not a number") { Description = Explain.AudioSampleRate });
            entry.SampleRate = double.IsFinite(rate) ? rate : 0;
            entry.Channels = (int)Math.Min(f.U32(node, "Audio Channels", Explain.AudioChannels), int.MaxValue);
            f.Field(node, "Always 0x7F000000", 4, Explain.AlwaysValue, v => NodeValue.Hex((long)v, 8));
            f.U32(node, "Bits Per Channel", Explain.BitsPerChannel);
            f.Field(node, "Format Flags", 4, Explain.FormatFlags, v => NodeValue.Hex((long)v, 8));
            f.U32(node, "Bytes Per Audio Packet", Explain.BytesPerAudioPacket);
            f.U32(node, "Frames Per Audio Packet", Explain.FramesPerAudioPacket);
        }

        SampleEntry? outer = state.CurrentEntry;
        state.CurrentEntry = entry;
        ReadChildren(state, node, f.Position, header.End, depth + 1);
        state.CurrentEntry = outer;
    }

    private static string FormatName(string format) => format switch
    {
        "mp4a" => "MPEG-4 audio",
        ".mp3" => "MPEG-1 Layer III",
        "alac" => "Apple Lossless",
        "fLaC" => "FLAC",
        "Opus" => "Opus",
        "ac-3" => "AC-3",
        "ec-3" => "E-AC-3",
        "ac-4" => "AC-4",
        "dtsc" or "dtsh" or "dtsl" or "dtse" => "DTS",
        "lpcm" => "linear PCM",
        "twos" or "sowt" or "in24" or "in32" or "fl32" or "fl64" or "raw " => "PCM",
        "samr" => "AMR",
        "sawb" => "AMR-WB",
        "enca" => "encrypted audio",
        "mha1" or "mhm1" => "MPEG-H 3D Audio",
        _ => "other",
    };

    // ISO/IEC 14496-14 5.6 (esds) with ISO/IEC 14496-1 7.2.6.5 (ES_Descriptor), 7.2.6.6
    // (DecoderConfigDescriptor) and 7.2.6.7 (DecoderSpecificInfo). A descriptor's size takes one to
    // four bytes, seven bits each, the top bit set on all but the last.
    private static void ElementaryStreamDescriptor(State state, Node node, BoxFields f)
    {
        FullBox(node, f);
        while (f.Remaining >= 2)
        {
            Descriptor(state, node, f, 0);
        }
    }

    private static void Descriptor(State state, Node parent, BoxFields f, int depth)
    {
        long start = f.Position;
        int tag = (int)f.Take(1);
        long size = 0;
        int lengthBytes = 0;
        while (lengthBytes < 4)
        {
            int b = (int)f.Take(1);
            lengthBytes++;
            size = (size << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
            {
                break;
            }
        }

        long bodyStart = f.Position;
        long bodyEnd = bodyStart + size;
        string name = tag switch
        {
            3 => "ES_Descriptor",
            4 => "DecoderConfigDescriptor",
            5 => "DecoderSpecificInfo",
            6 => "SLConfigDescriptor",
            0x14 => "ProfileLevelIndicationIndexDescriptor",
            _ => $"Descriptor 0x{tag:X2}",
        };
        if (bodyEnd > f.End)
        {
            state.Findings.AddNow(Severity.Warning, "mp4.descriptor-overruns",
                $"The {name} at 0x{start:X} declares {size} bytes, {bodyEnd - f.End} more than its box holds.", parent);
            bodyEnd = f.End;
        }

        var node = new Node(name, start, bodyEnd - start)
        {
            Description = tag switch
            {
                3 => Explain.EsDescriptor,
                4 => Explain.DecoderConfigDescriptor,
                5 => Explain.DecoderSpecificInfo,
                6 => Explain.SlConfigDescriptor,
                _ => Explain.OtherDescriptor,
            },
            Topic = tag == 5 ? Topics.AudioSpecificConfig : null,
        };
        parent.Add(node);
        node.Add(new Node("Tag", start, 1, NodeValue.Hex(tag, 2)) { Description = Explain.DescriptorTag });
        node.Add(new Node("Length", start + 1, lengthBytes, NodeValue.Integer(size, "bytes")) { Description = Explain.DescriptorLength });
        var body = new BoxFields(f.Reader, bodyStart, bodyEnd);
        try
        {
            switch (tag)
            {
                case 3:
                    body.U16(node, "ES ID", Explain.EsId);
                    int flags = (int)body.Field(node, "Flags", 1, Explain.EsFlags, v => NodeValue.Hex((long)v, 2));
                    if ((flags & 0x80) != 0)
                    {
                        body.U16(node, "Depends On ES ID", Explain.DependsOnEsId);
                    }

                    if ((flags & 0x40) != 0)
                    {
                        int length = body.U8(node, "URL Length", Explain.UrlLength);
                        body.Bytes(node, "URL", length, Explain.Url, b => Latin1OrUtf8(b));
                    }

                    if ((flags & 0x20) != 0)
                    {
                        body.U16(node, "OCR ES ID", Explain.OcrEsId);
                    }

                    SubDescriptors(state, node, body, depth);
                    break;
                case 4:
                    int type = (int)body.Field(node, "Object Type Indication", 1, Explain.ObjectTypeIndication, v => $"0x{v:X2} ({ObjectTypeName((int)v)})");
                    uint streamType = (uint)body.Field(node, "Stream Type", 1, Explain.StreamType,
                        v => $"{v >> 2} ({((v >> 2) == 5 ? "audio" : "other")}){((v & 2) != 0 ? ", upstream" : string.Empty)}{((v & 1) == 0 ? ", reserved bit 0" : string.Empty)}");
                    body.Field(node, "Buffer Size", 3, Explain.DecoderBufferSize, unit: "bytes");
                    uint max = (uint)body.Field(node, "Maximum Bitrate", 4, Explain.DecoderMaxBitrate, unit: "bit/s", topic: Topics.BitrateModes);
                    uint average = (uint)body.Field(node, "Average Bitrate", 4, Explain.DecoderAverageBitrate, topic: Topics.BitrateModes, describe: v => v == 0 ? "0 (variable bitrate)" : NodeValue.Integer((long)v, "bit/s"));
                    node.Value = $"0x{type:X2} ({ObjectTypeName(type)})";
                    if (state.CurrentEntry is { ObjectType: null } entry)
                    {
                        entry.ObjectType = type;
                        entry.StreamTypeByte = (int)streamType;
                        entry.MaxBitrate = max;
                        entry.AverageBitrate = average;
                    }

                    SubDescriptors(state, node, body, depth);
                    break;
                case 5:
                    byte[] data = body.TakeBytes(body.Remaining);
                    node.Value = NodeValue.Integer(data.Length, "bytes");
                    if (state.CurrentEntry is { DecoderSpecificInfo: null } owner)
                    {
                        owner.DecoderSpecificInfo = new MappedBytes(data, bodyStart);
                        owner.DecoderSpecificInfoNode = node;
                    }
                    else if (data.Length > 0)
                    {
                        node.Add(new Node("Data", bodyStart, data.Length, NodeValue.Integer(data.Length, "bytes")) { Description = Explain.DescriptorData });
                    }

                    break;
                case 6:
                    body.Field(node, "Predefined", 1, Explain.SlPredefined, v => v switch { 0 => "0 (custom)", 1 => "1 (null)", 2 => "2 (MP4 files)", _ => $"{v} (reserved)" });
                    if (body.Remaining > 0)
                    {
                        node.Add(new Node("Data", body.Position, body.Remaining, NodeValue.Integer(body.Remaining, "bytes")) { Description = Explain.DescriptorData });
                    }

                    break;
                default:
                    if (body.Remaining > 0)
                    {
                        node.Add(new Node("Data", body.Position, body.Remaining, NodeValue.Integer(body.Remaining, "bytes")) { Description = Explain.DescriptorData });
                    }

                    break;
            }
        }
        catch (EndOfStreamException)
        {
            state.Findings.AddNow(Severity.Warning, "mp4.descriptor-truncated", $"The {name} at 0x{start:X} ends inside a field.", node);
        }

        f.Skip(bodyEnd - f.Position);
    }

    private static void SubDescriptors(State state, Node node, BoxFields body, int depth)
    {
        while (body.Remaining >= 2)
        {
            if (depth >= 4)
            {
                node.Add(new Node("Data", body.Position, body.Remaining, NodeValue.Integer(body.Remaining, "bytes")) { Description = Explain.DescriptorData });
                return;
            }

            Descriptor(state, node, body, depth + 1);
        }
    }

    // ISO/IEC 14496-1 Table 5 (objectTypeIndication), the audio values.
    private static string ObjectTypeName(int type) => type switch
    {
        0x40 => "MPEG-4 Audio",
        0x66 => "MPEG-2 AAC Main",
        0x67 => "MPEG-2 AAC LC",
        0x68 => "MPEG-2 AAC SSR",
        0x69 => "MPEG-2 Audio",
        0x6B => "MPEG-1 Audio",
        0xA5 => "AC-3",
        0xA6 => "E-AC-3",
        0xA9 => "DTS",
        0xAD => "Opus",
        0xDD => "Vorbis",
        0xE1 => "13K Voice",
        _ => "other",
    };

    // 8.6.1.2: runs of samples with the same duration.
    private static void TimeToSample(State state, Node node, BoxFields f)
    {
        FullBox(node, f);
        uint count = f.U32(node, "Entries", Explain.TimeToSampleEntries);
        var entries = new List<(uint Count, uint Delta)>();
        long start = f.Position;
        ulong samples = 0;
        ulong duration = 0;
        ReadEntries(state, node, f, count, 8, "entries", (b, i, at) =>
        {
            uint samplesInRun = BigEndian32(b);
            uint delta = BigEndian32(b[4..]);
            entries.Add((samplesInRun, delta));
            samples += samplesInRun;
            duration += (ulong)samplesInRun * delta;
            if (i < MaxListedEntries)
            {
                var entry = new Node("Entry", at, 8, $"{samplesInRun} × {delta}") { Description = Explain.TimeToSampleEntry };
                entry.Add(new Node("Sample Count", at, 4, NodeValue.Integer(samplesInRun)) { Description = Explain.RunSampleCount });
                entry.Add(new Node("Sample Delta", at + 4, 4, NodeValue.Integer(delta)) { Description = Explain.SampleDelta });
                node.Add(entry);
            }
        });
        MoreEntries(node, count, start + (MaxListedEntries * 8L), f.Position);
        node.Value = $"{samples} samples, {BoxFields.DurationText(duration, state.Track?.Timescale ?? 0)}";
        if (state.Track is { TimeToSample: null } track)
        {
            track.TimeToSample = entries;
            track.TimeToSampleNode = node;
        }
    }

    // 8.6.1.3: offsets from decoding to composition time, unsigned in version 0, signed in 1.
    private static void CompositionOffsets(Node node, BoxFields f)
    {
        int version = FullBox(node, f).Version;
        uint count = f.U32(node, "Entries", Explain.CompositionEntries);
        long start = f.Position;
        for (uint i = 0; i < count && f.Remaining >= 8 && i < MaxListedEntries; i++)
        {
            long at = f.Position;
            uint samples = (uint)f.Take(4);
            uint offset = (uint)f.Take(4);
            var entry = new Node("Entry", at, 8, $"{samples} × {(version == 1 ? (int)offset : offset)}") { Description = Explain.CompositionEntry };
            entry.Add(new Node("Sample Count", at, 4, NodeValue.Integer(samples)) { Description = Explain.CompositionRunCount });
            entry.Add(new Node("Sample Offset", at + 4, 4, NodeValue.Integer(version == 1 ? (int)offset : offset)) { Description = Explain.CompositionOffset });
            node.Add(entry);
        }

        MoreEntries(node, count, f.Position, Math.Min(f.End, start + (count * 8L)));
    }

    // 8.7.4: which chunks hold how many samples, from each first chunk to the next entry's.
    private static void SampleToChunk(State state, Node node, BoxFields f)
    {
        FullBox(node, f);
        uint count = f.U32(node, "Entries", Explain.ChunkEntries);
        var entries = new List<(uint, uint, uint)>();
        long start = f.Position;
        ReadEntries(state, node, f, count, 12, "entries", (b, i, at) =>
        {
            uint first = BigEndian32(b);
            uint samples = BigEndian32(b[4..]);
            uint description = BigEndian32(b[8..]);
            entries.Add((first, samples, description));
            if (i < MaxListedEntries)
            {
                var entry = new Node("Entry", at, 12, $"from chunk {first}: {samples} samples, description {description}") { Description = Explain.ChunkEntry };
                entry.Add(new Node("First Chunk", at, 4, NodeValue.Integer(first)) { Description = Explain.FirstChunk });
                entry.Add(new Node("Samples Per Chunk", at + 4, 4, NodeValue.Integer(samples)) { Description = Explain.SamplesPerChunk });
                entry.Add(new Node("Sample Description Index", at + 8, 4, NodeValue.Integer(description)) { Description = Explain.SampleDescriptionIndex });
                node.Add(entry);
            }
        });
        MoreEntries(node, count, start + (MaxListedEntries * 12L), f.Position);
        if (state.Track is { SampleToChunk: null } track)
        {
            track.SampleToChunk = entries;
            track.SampleToChunkNode = node;
        }
    }

    // 8.7.3.2: one size for every sample, or a size per sample.
    private static void SampleSizes(State state, Node node, BoxFields f)
    {
        FullBox(node, f);
        uint size = f.U32(node, "Sample Size", Explain.ConstantSampleSize, unit: "bytes");
        uint count = f.U32(node, "Sample Count", Explain.SampleCount);
        node.Value = size != 0 ? $"{count} samples of {size} bytes" : NodeValue.Integer(count, "samples", "sample");
        int[]? sizes = null;
        if (size == 0)
        {
            sizes = new int[Math.Min(count, f.Remaining / 4)];
            long start = f.Position;
            ReadEntries(state, node, f, count, 4, "sample sizes", (b, i, at) =>
            {
                uint value = BigEndian32(b);
                sizes[i] = (int)Math.Min(value, int.MaxValue);
                if (i < MaxListedEntries)
                {
                    node.Add(new Node("Entry Size", at, 4, NodeValue.Integer(value, "bytes")) { Description = Explain.EntrySize });
                }
            });
            MoreEntries(node, count, start + (MaxListedEntries * 4L), f.Position);
        }

        if (state.Track is { SampleSizeNode: null } track)
        {
            track.SampleSize = size;
            track.SampleCount = sizes is null ? count : (uint)sizes.Length;
            track.Sizes = sizes;
            track.SampleSizeNode = node;
        }
    }

    // 8.7.3.3: sizes of 4, 8 or 16 bits per sample.
    private static void CompactSampleSizes(State state, Node node, BoxFields f)
    {
        FullBox(node, f);
        f.Skip(3);
        int field = f.U8(node, "Field Size", Explain.FieldSize, unit: "bits");
        uint count = f.U32(node, "Sample Count", Explain.SampleCount);
        node.Value = NodeValue.Integer(count, "samples", "sample");
        if (field is not (4 or 8 or 16))
        {
            state.Findings.AddNow(Severity.Warning, "mp4.stz2-field-size", $"The stz2 box at 0x{node.Offset:X} has a field size of {field} bits; ISO/IEC 14496-12 allows 4, 8 and 16.", node);
            return;
        }

        long room = f.Remaining * 8 / field;
        uint available = (uint)Math.Min(count, room);
        if (available < count)
        {
            state.Findings.AddNow(Severity.Warning, "mp4.entries-exceed-box", $"The stz2 box at 0x{node.Offset:X} declares {count} sample sizes, but holds room for {available}.", node);
        }

        byte[] data = f.TakeBytes(((long)available * field) + 7 >> 3);
        var sizes = new int[available];
        for (int i = 0; i < available; i++)
        {
            sizes[i] = field switch
            {
                4 => (data[i / 2] >> (i % 2 == 0 ? 4 : 0)) & 0xF,
                8 => data[i],
                _ => (data[2 * i] << 8) | data[(2 * i) + 1],
            };
        }

        node.Add(new Node("Entries", f.Position - data.Length, data.Length, NodeValue.Integer(available, "entries", "entry")) { Description = Explain.CompactEntries });
        if (state.Track is { SampleSizeNode: null } track)
        {
            track.SampleCount = available;
            track.Sizes = sizes;
            track.SampleSizeNode = node;
        }
    }

    // 8.7.5: each chunk's file offset, 32 or 64 bits.
    private static void ChunkOffsets(State state, Node node, BoxFields f, int size)
    {
        FullBox(node, f);
        uint count = f.U32(node, "Entries", Explain.ChunkOffsetEntries);
        var offsets = new long[Math.Min(count, f.Remaining / size)];
        long start = f.Position;
        ReadEntries(state, node, f, count, size, "chunk offsets", (b, i, at) =>
        {
            long offset = size == 4 ? BigEndian32(b) : (long)Math.Min(((ulong)BigEndian32(b) << 32) | BigEndian32(b[4..]), long.MaxValue);
            offsets[i] = offset;
            if (i < MaxListedEntries)
            {
                node.Add(new Node("Chunk Offset", at, size, NodeValue.Hex(offset, 8)) { Description = Explain.ChunkOffset });
            }
        });
        MoreEntries(node, count, start + (MaxListedEntries * (long)size), f.Position);
        node.Value = NodeValue.Integer(offsets.Length, "chunks", "chunk");
        if (state.Track is { ChunkOffsetNode: null } track)
        {
            track.ChunkOffsets = offsets;
            track.ChunkOffsetNode = node;
        }
    }

    // 8.6.2: the samples a decoder can start at; without this box every sample is one.
    private static void SyncSamples(Node node, BoxFields f)
    {
        FullBox(node, f);
        uint count = f.U32(node, "Entries", Explain.SyncSampleEntries);
        node.Value = NodeValue.Integer(count, "sync samples", "sync sample");
        long start = f.Position;
        for (uint i = 0; i < count && i < MaxListedEntries && f.Remaining >= 4; i++)
        {
            f.U32(node, "Sample Number", Explain.SyncSampleNumber);
        }

        MoreEntries(node, count, start + (MaxListedEntries * 4L), Math.Min(f.End, start + (count * 4L)));
    }

    // 8.9.3: sample group descriptions; for the roll and pre-roll groups of audio (10.1 and 10.3),
    // the roll distance in samples, negative for samples a decoder must decode before.
    private static void SampleGroupDescription(State state, Node node, BoxFields f)
    {
        int version = FullBox(node, f).Version;
        string grouping = f.FourCc(node, "Grouping Type", Explain.GroupingType);
        uint defaultLength = version == 1 ? f.U32(node, "Default Length", Explain.DefaultLength, unit: "bytes") : 0;
        if (version >= 2)
        {
            f.U32(node, "Default Sample Description Index", Explain.DefaultSampleDescriptionIndex);
        }

        uint count = f.U32(node, "Entries", Explain.GroupDescriptionEntries);
        node.Value = $"{grouping} ({GroupingName(grouping)})";
        for (uint i = 0; i < count && i < MaxListedEntries && f.Remaining > 0; i++)
        {
            long length = version == 1 && defaultLength == 0 ? f.U32(node, "Description Length", Explain.DescriptionLength, unit: "bytes") : defaultLength;
            long at = f.Position;
            if (grouping is "roll" or "prol" && (length == 2 || version == 0))
            {
                int distance = (short)(ushort)f.Take(2);
                var entry = new Node("Entry", at, 2, NodeValue.Integer(distance, "samples")) { Description = Explain.RollEntry };
                entry.Add(new Node("Roll Distance", at, 2, NodeValue.Integer(distance, "samples")) { Description = Explain.RollDistance });
                node.Add(entry);
                state.Track?.Rolls.Add((grouping, distance, entry));
            }
            else if (length > 0)
            {
                f.Bytes(node, "Entry", Math.Min(length, f.Remaining), Explain.GroupDescription);
            }
            else
            {
                break;
            }
        }
    }

    private static string GroupingName(string grouping) => grouping switch
    {
        "roll" => "audio roll recovery",
        "prol" => "audio pre-roll",
        "rap " => "random access point",
        "sync" => "sync samples",
        "tele" => "temporal level",
        "seig" => "encryption",
        _ => "other",
    };

    // 8.9.2: runs of samples and the group description each belongs to.
    private static void SampleToGroup(Node node, BoxFields f)
    {
        int version = FullBox(node, f).Version;
        node.Value = f.FourCc(node, "Grouping Type", Explain.GroupingType);
        if (version == 1)
        {
            f.U32(node, "Grouping Type Parameter", Explain.GroupingTypeParameter);
        }

        uint count = f.U32(node, "Entries", Explain.SampleToGroupEntries);
        long start = f.Position;
        for (uint i = 0; i < count && i < MaxListedEntries && f.Remaining >= 8; i++)
        {
            long at = f.Position;
            uint samples = (uint)f.Take(4);
            uint index = (uint)f.Take(4);
            var entry = new Node("Entry", at, 8, $"{samples} samples in group {index}") { Description = Explain.SampleToGroupEntry };
            entry.Add(new Node("Sample Count", at, 4, NodeValue.Integer(samples)) { Description = Explain.GroupSampleCount });
            entry.Add(new Node("Group Description Index", at + 4, 4, NodeValue.Integer(index)) { Description = Explain.GroupDescriptionIndex });
            node.Add(entry);
        }

        MoreEntries(node, count, start + (MaxListedEntries * 8L), Math.Min(f.End, start + (count * 8L)));
    }
}
