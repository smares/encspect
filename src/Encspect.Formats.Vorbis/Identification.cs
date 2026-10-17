using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Vorbis;

// The identification header (Vorbis I specification, section 4.2.2): packet type 1, "vorbis",
// then vorbis_version, audio_channels, audio_sample_rate, the three bitrate hints, blocksize_0 and
// blocksize_1 as exponents of two in one byte (the first in the low four bits, as Vorbis packs
// least significant bits first), and the framing flag: 30 bytes in all.
internal sealed record Identification(uint Version, int Channels, uint Rate, int BitrateMaximum, int BitrateNominal, int BitrateMinimum,
    int Blocksize0Exponent, int Blocksize1Exponent, bool HasFraming)
{
    public const int Length = 30;

    public int Blocksize0 => 1 << Blocksize0Exponent;

    public int Blocksize1 => 1 << Blocksize1Exponent;

    // Section 4.2.2: both are 64 to 8192 and blocksize_0 is not larger than blocksize_1; libvorbis
    // 1.3.7 refuses the header otherwise (_vorbis_unpack_info).
    public bool AreBlocksizesValid =>
        Blocksize0Exponent is >= 6 and <= 13 && Blocksize1Exponent is >= 6 and <= 13 && Blocksize0Exponent <= Blocksize1Exponent;

    // What a decoder needs to count samples: a version it reads and block sizes it accepts. A
    // rate or channel count of 0 leaves the blocks countable but the stream without time.
    public bool IsUsable => Version == 0 && AreBlocksizesValid;

    public bool HasTime => IsUsable && Rate > 0 && Rate <= int.MaxValue;

    public static Identification? Read(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < Length)
        {
            return null;
        }

        return new Identification(
            BinaryPrimitives.ReadUInt32LittleEndian(packet[7..]),
            packet[11],
            BinaryPrimitives.ReadUInt32LittleEndian(packet[12..]),
            BinaryPrimitives.ReadInt32LittleEndian(packet[16..]),
            BinaryPrimitives.ReadInt32LittleEndian(packet[20..]),
            BinaryPrimitives.ReadInt32LittleEndian(packet[24..]),
            packet[28] & 0x0F,
            packet[28] >> 4,
            (packet[29] & 1) != 0);
    }

    // Section 4.2.2: the fields are hints, meaningful only when greater than zero.
    public string BitrateHint
    {
        get
        {
            bool max = BitrateMaximum > 0;
            bool nominal = BitrateNominal > 0;
            bool min = BitrateMinimum > 0;
            if (max && nominal && min && BitrateMaximum == BitrateNominal && BitrateNominal == BitrateMinimum)
            {
                return "All three equal: a fixed or tightly bounded, nearly fixed bitrate";
            }

            if (max || min)
            {
                return "A maximum or minimum: VBR within those limits";
            }

            return nominal ? "Only the nominal: VBR or ABR that averages it" : "None set: the encoder gives no hint";
        }
    }

    public Node ToNode(MappedBytes packet)
    {
        int end = Math.Min(packet.Length, Length);
        Node header = Node.FromMapped("Identification Header", packet, 0, end, $"{Channels} {(Channels == 1 ? "channel" : "channels")}, {Rate} Hz",
            description: Explain.Identification, topic: Topics.VorbisBlocks);
        header.Add(Node.FromMapped("Packet Type", packet, 0, 1, NodeValue.Integer(1), description: Explain.PacketTypeIdentification));
        header.Add(Node.FromMapped("Vorbis String", packet, 1, 7, "vorbis", description: Explain.VorbisString));
        header.Add(Node.FromMapped("Vorbis Version", packet, 7, 11, NodeValue.Integer(Version), description: Explain.Version));
        header.Add(Node.FromMapped("Channels", packet, 11, 12, NodeValue.Integer(Channels), description: Explain.Channels));
        if (VorbisParser.Speakers(Channels) is { } layout)
        {
            header.Add(Node.FromMapped("Channel Layout", packet, 11, 12, $"{VorbisParser.ChannelMode(Channels)}: {ChannelLayouts.Speakers(layout)}", description: Explain.ChannelLayout));
        }

        header.Add(Node.FromMapped("Sample Rate", packet, 12, 16, NodeValue.Integer(Rate, "Hz"), description: Explain.SampleRate));
        header.Add(Node.FromMapped("Bitrate Maximum", packet, 16, 20, Bitrate(BitrateMaximum), description: Explain.BitrateMaximum, topic: Topics.BitrateModes));
        header.Add(Node.FromMapped("Bitrate Nominal", packet, 20, 24, Bitrate(BitrateNominal), description: Explain.BitrateNominal, topic: Topics.BitrateModes));
        header.Add(Node.FromMapped("Bitrate Minimum", packet, 24, 28, Bitrate(BitrateMinimum), description: Explain.BitrateMinimum, topic: Topics.BitrateModes));
        header.Add(Node.FromMapped("Bitrate Hint", packet, 16, 28, BitrateHint, description: Explain.BitrateHint, topic: Topics.BitrateModes));
        header.Add(Node.FromBits("Blocksize 0", packet, 28 * 8, (28 * 8) + 4, Blocksize(Blocksize0Exponent), description: Explain.Blocksize0,
            order: BitOrder.LeastSignificantFirst));
        header.Add(Node.FromBits("Blocksize 1", packet, (28 * 8) + 4, 29 * 8, Blocksize(Blocksize1Exponent), description: Explain.Blocksize1,
            order: BitOrder.LeastSignificantFirst));
        header.Add(Node.FromBits("Framing Bit", packet, 29 * 8, (29 * 8) + 1, NodeValue.Boolean(HasFraming), description: Explain.FramingBit,
            order: BitOrder.LeastSignificantFirst));
        return header;
    }

    private static NodeValue Bitrate(int value) => value > 0 ? NodeValue.Integer(value, "bps") : $"{value} (not set)";

    private static NodeValue Blocksize(int exponent) => NodeValue.Integer(1L << exponent, "samples");
}
