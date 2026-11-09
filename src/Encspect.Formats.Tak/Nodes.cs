using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Tak;

// The nodes of the report built from the records of the walk.
internal static class Nodes
{
    public static Node StreamId(Records r) => new("Stream ID", r.Start, Layout.SignatureLength, "tBaK") { Description = Explain.StreamId, Topic = Topics.Metadata };

    private static Node Bits(string name, long at, int first, int end, NodeValue? value, string description, string? topic = Topics.Metadata) =>
        Node.FromBits(name, at, first, end, value, NodeKind.Detail, description, topic, BitOrder.LeastSignificantFirst);

    private static Node Bytes(string name, long at, long length, NodeValue? value, string description, string? topic = Topics.Metadata) =>
        new(name, at, length, value) { Description = description, Topic = topic };

    private static string DescriptionOf(int id) => id switch
    {
        Layout.End => Explain.EndObject,
        Layout.StreamInfo => Explain.StreamInfoObject,
        Layout.SeekTable => Explain.SeekTableObject,
        Layout.WaveData => Explain.WaveDataObject,
        Layout.EncoderInfo => Explain.EncoderInfoObject,
        Layout.Padding => Explain.PaddingObject,
        Layout.Md5 => Explain.Md5Object,
        Layout.LastFrameInfo => Explain.LastFrameInfoObject,
        _ => Explain.UnknownObject,
    };

    public static string NameOf(ObjectRecord o) => o.IsKnown ? Layout.ObjectName(o.Id) + " Object" : "Unknown Object";

    // The node of a metadata object: its header, its fields and, where it has one, its CRC-24.
    public static Node Object(Records r, ObjectRecord o)
    {
        long length = Layout.ObjectHeaderLength + o.Held;
        var node = new Node(NameOf(o), o.Offset, length, NodeValue.Integer(length, "bytes", "byte")) { Description = DescriptionOf(o.Id), Topic = Topics.Metadata };
        node.Add(Bits("Object ID", o.Offset, 0, 7, NodeValue.Integer(o.Id), Explain.ObjectId));
        node.Add(Bits("Reserved Bit", o.Offset, 7, 8, NodeValue.Integer(o.IsReserved ? 1 : 0), Explain.ObjectReserved));
        node.Add(Bytes("Object Size", o.Offset + 1, 3, NodeValue.Integer(o.Size, "bytes", "byte"), Explain.ObjectSize));
        long at = o.DataOffset;
        long dataEnd = at + o.Held - (o.IsCompared || (Layout.HasCrc(o.Id) && o.Held >= Layout.CrcLength && !o.IsCut) ? Layout.CrcLength : 0);
        switch (o.Id)
        {
            case Layout.StreamInfo when r.StreamInfoObject == o && r.Stream is { } info && o.IsSizeValid && !o.IsCut:
                AddStreamInfo(node, at, info);
                break;
            case Layout.EncoderInfo when r.EncoderInfoObject == o:
                AddEncoderInfo(node, at, r);
                break;
            case Layout.WaveData when o.IsSizeValid && !o.IsCut:
                AddWaveData(node, at, o);
                break;
            case Layout.Md5 when r.Md5Object == o && r.Md5 is { } md5:
                node.Add(Bytes("MD5", at, 16, Convert.ToHexStringLower(md5), Explain.Md5Digest, Topics.Metadata));
                break;
            case Layout.LastFrameInfo when r.LastFrameObject == o:
                node.Add(Bytes("Last Frame Position", at, 5, NodeValue.Integer(r.LastFramePosition, "bytes", "byte"), Explain.LastFramePosition));
                node.Add(Bytes("Last Frame Size", at + 5, 3, NodeValue.Integer(r.LastFrameSize, "bytes", "byte"), Explain.LastFrameSize));
                break;
            case Layout.Padding:
                if (o.Held > 0)
                {
                    node.Add(Bytes("Padding Data", at, o.Held, NodeValue.Integer(o.Held, "bytes", "byte"), Explain.PaddingData));
                }

                break;
            case Layout.End:
                break;
            default:
                long rest = dataEnd - at;
                if (rest > 0)
                {
                    node.Add(Bytes(o.Id == Layout.SeekTable ? "Seek Table Data" : "Object Data", at, rest, NodeValue.Integer(rest, "bytes", "byte"),
                        o.Id == Layout.SeekTable ? Explain.SeekTableData : Explain.UnknownData));
                }

                break;
        }

        if (Layout.HasCrc(o.Id) && o.IsCompared)
        {
            long crcAt = at + o.Size - Layout.CrcLength;
            node.Add(Bytes("CRC-24", crcAt, Layout.CrcLength, NodeValue.Hex(o.StoredCrc, 6), Explain.ObjectCrc, Topics.Crc));
            node.Add(Bytes("Computed CRC-24", crcAt, Layout.CrcLength, NodeValue.Hex(o.ComputedCrc, 6), Explain.ObjectComputedCrc, Topics.Crc));
            node.Add(Bytes("IsCrc24Valid", crcAt, Layout.CrcLength, NodeValue.Boolean(o.IsCrcValid), Explain.ObjectIsCrcValid, Topics.Crc));
        }

        return node;
    }

    public static Node CrcField(Records r, ObjectRecord o) => Object(r, o).Children.First(c => c.Name == "CRC-24");

    private static void AddStreamInfo(Node node, long at, StreamInfoFields info)
    {
        node.Add(Bits("Codec", at, 0, 6, Layout.CodecName(info.Codec) ?? $"undefined ({info.Codec})", Explain.Codec));
        node.Add(Bits("Profile", at, 6, 10, NodeValue.Integer(info.Profile), Explain.Profile, Topics.Presets));
        Node type = node.Add(Bits("Frame Size Type", at, 10, 14, Layout.FrameSizeTypeName(info.FrameSizeType), Explain.FrameSizeType));
        if (info.FrameSizeType < Layout.FrameSizeTypes)
        {
            type.Add(Bits("Frame Length", at, 10, 14, NodeValue.Integer(Layout.FrameSamples(info.FrameSizeType, info.SampleRate), "samples", "sample"), Explain.FrameLength));
        }

        node.Add(Bits("Samples", at, 14, 49, NodeValue.Integer(info.Samples, "samples", "sample"), Explain.SampleCount));
        node.Add(Bits("Data Type", at, 49, 52, info.DataType == 0 ? "PCM" : $"reserved ({info.DataType})", Explain.DataType));
        node.Add(Bits("Sample Rate", at, 52, 70, NodeValue.Integer(info.SampleRate, "Hz"), Explain.SampleRate));
        node.Add(Bits("Bits Per Sample", at, 70, 75, NodeValue.Integer(info.BitsPerSample, "bits", "bit"), Explain.BitsPerSample));
        node.Add(Bits("Channels", at, 75, 79, NodeValue.Integer(info.Channels, "channels", "channel"), Explain.Channels));
        node.Add(Bits("HasExtension", at, 79, 80, NodeValue.Boolean(info.HasExtension), Explain.HasExtension));
        if (!info.HasExtension)
        {
            return;
        }

        node.Add(Bits("Valid Bits Per Sample", at, 80, 85, NodeValue.Integer(info.ValidBitsPerSample, "bits", "bit"), Explain.ValidBits));
        node.Add(Bits("HasSpeakerAssignment", at, 85, 86, NodeValue.Boolean(info.HasSpeakerAssignment), Explain.HasSpeakerAssignment));
        if (!info.HasSpeakerAssignment)
        {
            return;
        }

        Node speakers = node.Add(Bits("Speaker Assignment", at, 86, 86 + (6 * info.Channels), NodeValue.Integer(info.Channels, "channels", "channel"), Explain.SpeakerAssignment, Topics.Channels));
        for (int i = 0; i < info.Channels; i++)
        {
            int speaker = info.Speakers[i];
            speakers.Add(Bits($"Channel {i + 1}", at, 86 + (6 * i), 92 + (6 * i), Layout.SpeakerName(speaker) ?? $"undefined ({speaker})", Explain.Speaker, Topics.Channels));
        }
    }

    private static void AddEncoderInfo(Node node, long at, Records r)
    {
        int v = r.EncoderVersion;
        node.Add(Bytes("Encoder Version", at, 3, $"{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}", Explain.EncoderVersion, ClaimRoles.EncoderTopic));
        node.Add(Bits("Preset", at + 3, 0, 4, NodeValue.Integer(r.Preset), Explain.Preset, Topics.Presets));
        node.Add(Bits("Evaluation", at + 3, 4, 6, Layout.EvaluationName(r.Evaluation) ?? $"undefined ({r.Evaluation})", Explain.Evaluation, Topics.Presets));
        node.Add(Bits("Reserved", at + 3, 6, 8, NodeValue.Integer(r.EncoderReserved), Explain.EncoderReserved));
    }

    private static void AddWaveData(Node node, long at, ObjectRecord o)
    {
        node.Add(Bytes("Header Size", at, 3, NodeValue.Integer(o.HeaderSize, "bytes", "byte"), Explain.HeaderSize));
        node.Add(Bytes("Footer Size", at + 3, 3, NodeValue.Integer(o.FooterSize, "bytes", "byte"), Explain.FooterSize));
        if (o.WavHeader is { } header)
        {
            node.Add(HeaderData.Node(header, Explain.HeaderData, isHeader: true));
        }

        if (o.WavFooter is { } footer)
        {
            node.Add(HeaderData.Node(footer, Explain.TerminatingData, isHeader: false));
        }
    }

    public static Node AudioData(long offset, long length) =>
        new("Audio Data", offset, length, NodeValue.Integer(length, "bytes", "byte")) { Description = Explain.AudioData, Topic = Topics.Metadata };
}
