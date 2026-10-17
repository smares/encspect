using System.Buffers.Binary;

namespace Encspect.Formats.Dts;

// The static fields of an extension substream header (7.5.2, 7.5.3.2) as the last header that carried them gave them. When
// bStaticFieldsPresent is 0 the header omits them and they keep their values, as FFmpeg's dca_exss.c does by keeping its parser state
// between frames. The analysis holds one per extension substream index; a unit whose header omits the fields uses the one in force and
// its record keeps a copy, so the node pass reads the unit as the analysis did.
internal sealed class StaticState
{
    public const int Bytes = 27;

    public bool IsMixEnabled { get; set; }

    public int MixConfigurations { get; set; }

    public int[] MixChannels { get; } = new int[4];

    public int FrameDurationCycles { get; set; }

    // The reference clock in Hz, or 0 where its code is unused.
    public int ClockRate { get; set; }

    // The first asset's fields: the only asset a header without static fields has.
    public int BitResolution { get; set; }

    public int MaxSampleRate { get; set; }

    public int Channels { get; set; }

    public bool IsOneToOne { get; set; }

    public bool HasEmbeddedStereo { get; set; }

    public bool HasEmbeddedSixChannel { get; set; }

    public bool HasSpeakerMask { get; set; }

    public int SpeakerMask { get; set; }

    public double Duration => ClockRate > 0 ? FrameDurationCycles / (double)ClockRate : 0;

    public void WriteTo(Span<byte> bytes)
    {
        bytes[0] = (byte)(IsMixEnabled ? 1 : 0);
        bytes[1] = (byte)MixConfigurations;
        for (int i = 0; i < 4; i++)
        {
            bytes[2 + i] = (byte)MixChannels[i];
        }

        BinaryPrimitives.WriteInt32BigEndian(bytes[6..], FrameDurationCycles);
        BinaryPrimitives.WriteInt32BigEndian(bytes[10..], ClockRate);
        bytes[14] = (byte)BitResolution;
        BinaryPrimitives.WriteInt32BigEndian(bytes[15..], MaxSampleRate);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[19..], (ushort)Channels);
        bytes[21] = (byte)((IsOneToOne ? 1 : 0) | (HasEmbeddedStereo ? 2 : 0) | (HasEmbeddedSixChannel ? 4 : 0) | (HasSpeakerMask ? 8 : 0));
        BinaryPrimitives.WriteInt32BigEndian(bytes[22..], SpeakerMask);
    }

    public static StaticState ReadFrom(ReadOnlySpan<byte> bytes)
    {
        var state = new StaticState
        {
            IsMixEnabled = bytes[0] != 0,
            MixConfigurations = bytes[1],
            FrameDurationCycles = BinaryPrimitives.ReadInt32BigEndian(bytes[6..]),
            ClockRate = BinaryPrimitives.ReadInt32BigEndian(bytes[10..]),
            BitResolution = bytes[14],
            MaxSampleRate = BinaryPrimitives.ReadInt32BigEndian(bytes[15..]),
            Channels = BinaryPrimitives.ReadUInt16BigEndian(bytes[19..]),
            IsOneToOne = (bytes[21] & 1) != 0,
            HasEmbeddedStereo = (bytes[21] & 2) != 0,
            HasEmbeddedSixChannel = (bytes[21] & 4) != 0,
            HasSpeakerMask = (bytes[21] & 8) != 0,
            SpeakerMask = BinaryPrimitives.ReadInt32BigEndian(bytes[22..]),
        };
        for (int i = 0; i < 4; i++)
        {
            state.MixChannels[i] = bytes[2 + i];
        }

        return state;
    }
}
