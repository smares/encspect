using System.Buffers.Binary;
using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Speex;

// The Speex header packet (speex_header.h, struct SpeexHeader; written by speex_init_header and
// speex_header_to_packet in libspeex): the string "Speex   ", a 20-byte version string, then
// thirteen 32-bit little-endian integers, 80 bytes in all.
internal sealed record SpeexHeader(string Version, int VersionId, int HeaderSize, int Rate, int Mode, int ModeBitstreamVersion, int Channels,
    int Bitrate, int FrameSize, int Vbr, int FramesPerPacket, int ExtraHeaders, int Reserved1, int Reserved2)
{
    public const int Length = 80;

    // The encoder's lookahead of each mode, which speexenc and ffmpeg subtract from the granule
    // positions (SPEEX_GET_LOOKAHEAD of an encoder in libspeex 1.2.1).
    public static ReadOnlySpan<int> Lookahead => [40, 143, 349];

    public static string[] ModeNames { get; } = ["Narrowband", "Wideband", "Ultra-wideband"];

    public bool IsModeValid => Mode is >= 0 and <= 2;

    // The decoder's view: nb_channels out of range is clamped (speex_packet_to_header).
    public int DecodedChannels => Math.Clamp(Channels, 1, 2);

    // The frames a packet is counted with: at least one, and at most 64, far above the 10 speexenc
    // writes, so a corrupt count neither makes the frame walk follow a packet of mode-0 frames
    // forever nor the stream last for centuries; the header's value gets a finding.
    public int FramesPerPacketCounted => Math.Clamp(FramesPerPacket, 1, 64);

    // Null when the packet is too short for the fixed fields.
    public static SpeexHeader? Read(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < Length)
        {
            return null;
        }

        // A C string: what follows the first NUL is not part of it.
        ReadOnlySpan<byte> field = packet[8..28];
        int nul = field.IndexOf((byte)0);
        string version = Encoding.Latin1.GetString(nul < 0 ? field : field[..nul]);
        Span<int> f = stackalloc int[13];
        for (int i = 0; i < f.Length; i++)
        {
            f[i] = BinaryPrimitives.ReadInt32LittleEndian(packet[(28 + (4 * i))..]);
        }

        return new SpeexHeader(version, f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12]);
    }

    public Node ToNode(MappedBytes packet)
    {
        Node header = Node.FromMapped("Speex Header", packet, 0, Math.Min(packet.Length, Length), $"{ModeName}, {Rate} Hz");
        header.Add(Node.FromMapped("Speex String", packet, 0, 8, "Speex   "));
        header.Add(Node.FromMapped("Speex Version", packet, 8, 28, Version));
        header.Add(Node.FromMapped("Version ID", packet, 28, 32, NodeValue.Integer(VersionId)));
        header.Add(Node.FromMapped("Header Size", packet, 32, 36, NodeValue.Integer(HeaderSize, "bytes", "byte")));
        header.Add(Node.FromMapped("Sample Rate", packet, 36, 40, NodeValue.Integer(Rate, "Hz")));
        header.Add(Node.FromMapped("Mode", packet, 40, 44, IsModeValid ? ModeName : $"{Mode} (undefined)"));
        header.Add(Node.FromMapped("Mode Bitstream Version", packet, 44, 48, NodeValue.Integer(ModeBitstreamVersion)));
        header.Add(Node.FromMapped("Channels", packet, 48, 52, NodeValue.Integer(Channels)));
        header.Add(Node.FromMapped("Bitrate", packet, 52, 56, Bitrate == -1 ? "-1 (not given)" : NodeValue.Integer(Bitrate, "bps")));
        header.Add(Node.FromMapped("Frame Size", packet, 56, 60, NodeValue.Integer(FrameSize, "samples", "sample")));
        header.Add(Node.FromMapped("IsVbr", packet, 60, 64, Vbr is 0 or 1 ? NodeValue.Boolean(Vbr == 1) : NodeValue.Integer(Vbr)));
        header.Add(Node.FromMapped("Frames Per Packet", packet, 64, 68, NodeValue.Integer(FramesPerPacket)));
        header.Add(Node.FromMapped("Extra Headers", packet, 68, 72, NodeValue.Integer(ExtraHeaders)));
        header.Add(Node.FromMapped("Reserved 1", packet, 72, 76, NodeValue.Integer(Reserved1)));
        header.Add(Node.FromMapped("Reserved 2", packet, 76, 80, NodeValue.Integer(Reserved2)));
        return header;
    }

    public string ModeName => IsModeValid ? ModeNames[Mode] : "Undefined mode";
}
