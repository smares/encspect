using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Opus;

// The identification header (RFC 7845, section 5.1): "OpusHead", the version, the output channel
// count, the pre-skip, the input sample rate, the output gain and the channel mapping family; for
// a family other than 0 the stream count, the coupled stream count and the channel mapping table
// (section 5.1.1), or in family 3 a demixing matrix in its place (RFC 8486, section 3.2).
internal sealed record OpusHead(int Version, int Channels, int PreSkip, uint InputRate, short OutputGain, int Family, int Streams, int Coupled, byte[] Mapping,
    int Length)
{
    public const int MinimumLength = 19;

    // Families 0 and 1 have one, two, or up to eight channels in a fixed layout (section 5.1.1).
    public bool HasTable => Family != 0;

    // The streams the audio packets hold: one for family 0.
    public int StreamCount => Family == 0 ? 1 : Streams;

    // Null when the packet is too short for the fixed fields; Length then says how far it
    // reaches.
    public static OpusHead? Read(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < MinimumLength)
        {
            return null;
        }

        int family = packet[18];
        int channels = packet[9];
        int streams = 1;
        int coupled = channels == 2 ? 1 : 0;
        byte[] mapping = [];
        int length = MinimumLength;
        if (family != 0)
        {
            if (packet.Length < MinimumLength + 2)
            {
                return null;
            }

            streams = packet[19];
            coupled = packet[20];
            int table = family == 3 ? 2 * channels * (streams + coupled) : channels;
            length = Math.Min(packet.Length, MinimumLength + 2 + table);
            mapping = packet[(MinimumLength + 2)..length].ToArray();
        }

        return new OpusHead(packet[8], channels, BinaryPrimitives.ReadUInt16LittleEndian(packet[10..]), BinaryPrimitives.ReadUInt32LittleEndian(packet[12..]),
            BinaryPrimitives.ReadInt16LittleEndian(packet[16..]), family, streams, coupled, mapping, length);
    }

    private static string Count(int bytes) => bytes == 1 ? "1 byte" : $"{bytes} bytes";

    // The bytes the header should hold for its family.
    public int ExpectedLength => Family == 0 ? MinimumLength : MinimumLength + 2 + (Family == 3 ? 2 * Channels * (Streams + Coupled) : Channels);

    public Node ToNode(MappedBytes packet)
    {
        Node header = Node.FromMapped("Opus Head", packet, 0, Length, $"{Channels} {(Channels == 1 ? "channel" : "channels")}, pre-skip {PreSkip}",
            description: Explain.Head, topic: Topics.OpusPreSkip);
        header.Add(Node.FromMapped("Magic Signature", packet, 0, 8, "OpusHead", description: Explain.MagicHead));
        header.Add(Node.FromMapped("Version", packet, 8, 9, NodeValue.Integer(Version), description: Explain.Version));
        header.Add(Node.FromMapped("Channels", packet, 9, 10, NodeValue.Integer(Channels), description: Explain.Channels, topic: Topics.OpusChannelMapping));
        if (Layout is { } layout)
        {
            header.Add(Node.FromMapped("Channel Layout", packet, 9, 10, $"{layout.Name}: {string.Join(", ", layout.Speakers)}", description: Explain.ChannelLayout,
                topic: Topics.OpusChannelMapping));
        }
        header.Add(Node.FromMapped("Pre-skip", packet, 10, 12, NodeValue.Integer(PreSkip, "samples", "sample"), description: Explain.PreSkip, topic: Topics.OpusPreSkip));
        header.Add(Node.FromMapped("Input Sample Rate", packet, 12, 16, InputRate == 0 ? "0 (not given)" : NodeValue.Integer(InputRate, "Hz"), description: Explain.InputSampleRate));
        header.Add(Node.FromMapped("Output Gain", packet, 16, 18, NodeValue.Decimal(OutputGain / 256.0, 2, "dB", showsSign: true), description: Explain.OutputGain,
            topic: Topics.OpusPreSkip));
        header.Add(Node.FromMapped("Mapping Family", packet, 18, 19, FamilyText, description: Explain.MappingFamily, topic: Topics.OpusChannelMapping));
        if (Family != 0)
        {
            header.Add(Node.FromMapped("Stream Count", packet, 19, 20, NodeValue.Integer(Streams), description: Explain.StreamCount, topic: Topics.OpusChannelMapping));
            header.Add(Node.FromMapped("Coupled Count", packet, 20, 21, NodeValue.Integer(Coupled), description: Explain.CoupledCount, topic: Topics.OpusChannelMapping));
            if (Family == 3)
            {
                int columns = Streams + Coupled;
                header.Add(Node.FromMapped("Demixing Matrix", packet, 21, Length,
                    $"{Channels} {(Channels == 1 ? "row" : "rows")} by {columns} {(columns == 1 ? "column" : "columns")} of Q15 coefficients, {Count(Length - 21)}",
                    description: Explain.DemixingMatrix));
            }
            else if (Length > 21)
            {
                Node table = header.Add(Node.FromMapped("Channel Mapping", packet, 21, Length, string.Join(", ", Mapping), description: Explain.ChannelMapping,
                    topic: Topics.OpusChannelMapping));
                for (int c = 0; c < Mapping.Length; c++)
                {
                    table.Add(Node.FromMapped($"Channel {c}", packet, 21 + c, 22 + c, MappingEntry(c), description: Explain.ChannelMappingEntry));
                }
            }
        }

        return header;
    }

    private string FamilyText => Family switch
    {
        0 => "0 (mono or stereo)",
        1 => "1 (Vorbis channel order)",
        2 => "2 (ambisonics, RFC 8486)",
        3 => "3 (ambisonics with a demixing matrix, RFC 8486)",
        255 => "255 (no defined layout)",
        _ => $"{Family} (reserved)",
    };

    // The speakers of families 0 and 1 in channel order (RFC 7845, sections 5.1.1.1 and 5.1.1.2),
    // the Vorbis channel order; null for other families and counts.
    public (string Name, string[] Speakers)? Layout => Family switch
    {
        0 when Channels is 1 or 2 => Layouts[Channels - 1],
        1 when Channels is >= 1 and <= 8 => Layouts[Channels - 1],
        _ => null,
    };

    // The layout as speaker positions, in the same order: the rear pair of four to six channels is
    // the lone surround pair, the back pair; with seven and eight it is the side pair. Ambisonics
    // and family 255 name no speakers.
    public IReadOnlyList<Speaker>? Speakers => Family switch
    {
        0 when Channels is 1 or 2 => SpeakerLayouts[Channels - 1],
        1 when Channels is >= 1 and <= 8 => SpeakerLayouts[Channels - 1],
        _ => null,
    };

    private static readonly Speaker[][] SpeakerLayouts =
    [
        [Speaker.FrontCenter],
        [Speaker.FrontLeft, Speaker.FrontRight],
        [Speaker.FrontLeft, Speaker.FrontCenter, Speaker.FrontRight],
        [Speaker.FrontLeft, Speaker.FrontRight, Speaker.BackLeft, Speaker.BackRight],
        [Speaker.FrontLeft, Speaker.FrontCenter, Speaker.FrontRight, Speaker.BackLeft, Speaker.BackRight],
        [Speaker.FrontLeft, Speaker.FrontCenter, Speaker.FrontRight, Speaker.BackLeft, Speaker.BackRight, Speaker.LowFrequency],
        [Speaker.FrontLeft, Speaker.FrontCenter, Speaker.FrontRight, Speaker.SideLeft, Speaker.SideRight, Speaker.BackCenter, Speaker.LowFrequency],
        [Speaker.FrontLeft, Speaker.FrontCenter, Speaker.FrontRight, Speaker.SideLeft, Speaker.SideRight, Speaker.BackLeft, Speaker.BackRight, Speaker.LowFrequency],
    ];

    private static readonly (string Name, string[] Speakers)[] Layouts =
    [
        ("Mono", ["mono"]),
        ("Stereo", ["left", "right"]),
        ("Linear surround", ["left", "center", "right"]),
        ("Quadraphonic", ["front left", "front right", "rear left", "rear right"]),
        ("5.0", ["front left", "front center", "front right", "rear left", "rear right"]),
        ("5.1", ["front left", "front center", "front right", "rear left", "rear right", "LFE"]),
        ("6.1", ["front left", "front center", "front right", "side left", "side right", "rear center", "LFE"]),
        ("7.1", ["front left", "front center", "front right", "side left", "side right", "rear left", "rear right", "LFE"]),
    ];

    // Families 2 and 3 allow (1 + n)^2 + 2j channels for an ambisonic order n of 0 to 14 and j of
    // 0 or 1, j being a non-diegetic stereo pair (RFC 8486, section 3.3). Null for another count.
    public (int Order, bool HasStereo)? Ambisonics
    {
        get
        {
            for (int order = 0; order <= 14; order++)
            {
                int full = (order + 1) * (order + 1);
                if (Channels == full || Channels == full + 2)
                {
                    return (order, Channels == full + 2);
                }
            }

            return null;
        }
    }

    public bool IsAmbisonic => Family is 2 or 3;

    // What the summary calls the channels.
    public string ChannelMode => Layout is { } layout ? layout.Name
        : IsAmbisonic && Ambisonics is { } ambisonics ? $"Ambisonics, order {ambisonics.Order}{(ambisonics.HasStereo ? ", with non-diegetic stereo" : string.Empty)}"
        : $"{Channels} channels";

    // What decoded channel a table entry takes (section 5.1.1): the coupled streams give two
    // channels each, left then right, the others one each; 255 is silence.
    public string MappingEntry(int channel)
    {
        int index = Mapping[channel];
        string place = Place(channel) is { } name ? $"{name}: " : string.Empty;
        if (index == 255)
        {
            return $"{place}silence (255)";
        }

        if (index < 2 * Coupled)
        {
            return $"{place}coupled stream {index / 2}, {(index % 2 == 0 ? "left" : "right")} ({index})";
        }

        return index < Streams + Coupled ? $"{place}mono stream {index - Coupled} ({index})" : $"{place}{index}, beyond the {Streams + Coupled} decoded channels";
    }

    // Where an output channel plays: a speaker of family 1, or in family 2 an ambisonic channel
    // number (ACN, whose order is floor(sqrt(k)) and degree k - n(n + 1)) or the non-diegetic pair
    // after them (RFC 8486, section 3.1).
    private string? Place(int channel)
    {
        if (Family == 1 && Layout is { } layout)
        {
            return layout.Speakers[channel];
        }

        if (Family != 2 || Ambisonics is not { } ambisonics)
        {
            return null;
        }

        int full = (ambisonics.Order + 1) * (ambisonics.Order + 1);
        if (channel >= full)
        {
            return channel == full ? "non-diegetic left" : "non-diegetic right";
        }

        int n = (int)Math.Sqrt(channel);
        return $"ACN {channel} (order {n}, degree {channel - (n * (n + 1))})";
    }
}
