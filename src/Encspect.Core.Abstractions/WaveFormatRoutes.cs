using System.Buffers.Binary;

namespace Encspect.Core;

// Which codec parser reads the audio a WAVEFORMATEX describes, by its format tag or, for the extensible format, the tag or named sub-format GUID of its coding.
// WAV, ASF and Matroska's A_MS/ACM route through the same tables; each container hands over what its own layout allows.
public static class WaveFormatRoutes
{
    public const int Pcm = 0x0001;
    public const int IeeeFloat = 0x0003;
    public const int ALaw = 0x0006;
    public const int MuLaw = 0x0007;
    public const int DtsMicrosoft = 0x0008;
    public const int Mpeg = 0x0050;
    public const int MpegLayer3 = 0x0055;
    public const int Ac3 = 0x2000;
    public const int Dts = 0x2001;
    public const int Extensible = 0xFFFE;

    // Sub-format GUIDs of the codings that have no format tag, with the codec parser each goes to. The values are those of Microsoft's headers:
    // MEDIASUBTYPE_DOLBY_AC3 and MEDIASUBTYPE_MPEG2_AUDIO (ksuuids.h, named as SubFormat values by "Multiple channel audio data and WAVE
    // files") and MFAudioFormat_Dolby_DDPlus (mfapi.h, "Audio Subtype GUIDs").
    private static readonly (Guid Id, string Name, string CodecId)[] NamedSubFormats =
    [
        (new Guid("e06d802c-db46-11cf-b4d1-00805f6cbbea"), "AC-3", "ac-3"),
        (new Guid("a7fb87af-2d02-42fb-a4d4-05cd93843bdd"), "E-AC-3", "e-ac-3"),
        (new Guid("e06d802b-db46-11cf-b4d1-00805f6cbbea"), "MPEG-2 audio", "mpeg-audio"),
    ];

    // The base of every sub-format GUID: the format tag in the first 32 bits and these twelve bytes after them (ksmedia.h, and the
    // WAVEFORMATEXTENSIBLE sub-format of EBU Tech 3306-2009 Annex A.2).
    private static readonly byte[] SubFormatBase = [0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];

    // Whether the GUID is a format tag in a sub-format GUID's clothes, and which tag.
    public static int? TagOfSubFormat(Guid guid)
    {
        Span<byte> bytes = stackalloc byte[16];
        guid.TryWriteBytes(bytes);
        return bytes[4..].SequenceEqual(SubFormatBase) ? (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[..4]) : null;
    }

    // The name and codec parser of a sub-format GUID that is no format tag but is one Microsoft's headers name, or null.
    public static (string Name, string CodecId)? NamedSubFormat(Guid guid)
    {
        foreach ((Guid id, string name, string codecId) in NamedSubFormats)
        {
            if (id == guid)
            {
                return (name, codecId);
            }
        }

        return null;
    }

    // The codec parser of the audio frames a coding stores as a stream of its own (MPEG audio, AC-3 and DTS), or null: AC-3 under 0x2000 (mmreg.h
    // calls the tag WAVE_FORMAT_DVM) is the convention of the files FFmpeg's muxer writes, not a definition of the header.
    public static string? CodecId(int tag) => tag switch
    {
        Mpeg or MpegLayer3 => "mpeg-audio",
        Ac3 => "ac-3",
        Dts or DtsMicrosoft => "dts",
        _ => null,
    };

    // The codec parser of a Windows Media Audio coding (ASF specification, section 11.1; 0x000A is Windows Media Audio Voice), or null.
    public static string? WindowsMediaCodecId(int tag) => tag switch
    {
        0x0160 or 0x0161 => "wma",
        0x0162 => "wma-pro",
        0x0163 => "wma-lossless",
        0x000A => "wma-voice",
        _ => null,
    };

    // How the samples of a linear coding are laid out, or null where the tag is none of them or the widths do not fit it: PCM (8 bits unsigned, else signed),
    // IEEE floating point (32 or 64 bits, the container's width the stated one), and A-law and mu-law (8 bits). `bits` is the container's width of a sample
    // and `statedBits` the wBitsPerSample the header states.
    public static PcmEncoding? PcmEncodingOf(int tag, int bits, int statedBits) => tag switch
    {
        Pcm => bits == 8 ? PcmEncoding.UnsignedInteger : PcmEncoding.SignedInteger,
        IeeeFloat when bits is 32 or 64 && statedBits == bits => PcmEncoding.Float,
        ALaw when statedBits == 8 => PcmEncoding.ALaw,
        MuLaw when statedBits == 8 => PcmEncoding.MuLaw,
        _ => null,
    };

    // The speakers of a stream's channels: the assignment of the channel mask of an extensible format (a mask that names fewer speakers than there are channels
    // leaves the rest without a position, one that names more has its extra bits ignored), else, for the plain WAVEFORMATEX, front left and right for two
    // channels and the front center for one (ksmedia.h, KSAUDIO_SPEAKER_MONO).
    public static IReadOnlyList<Speaker>? StreamLayout(int channels, bool isExtensible, uint? mask)
    {
        if (channels is < 1 or > 64)
        {
            return null;
        }

        if (isExtensible && mask is { } assigned and not 0)
        {
            return ChannelLayouts.FromWaveMask(assigned, channels);
        }

        return channels switch { 1 => ChannelLayouts.Mono, 2 => ChannelLayouts.Stereo, _ => null };
    }

    // How the samples of a linear coding stored in a WAVEFORMATEX are laid out, which WAV and Matroska's A_MS/ACM read the same way: a sample takes the whole bytes
    // wBitsPerSample rounds up to (12 and 20 bits are read in 16 and 24-bit containers), and a block alignment that is stated is one sample frame of them. Null with
    // the reason where the tag is no linear coding, the width does not fit it, there are no channels or the alignment is not one sample frame.
    public static PcmCoding? PcmCodingOf(int tag, int channels, int bits, int blockAlign, long sampleRate, bool isBigEndian, IReadOnlyList<Speaker>? layout, out string why)
    {
        why = string.Empty;
        int bytes = (bits + 7) / 8;
        if (channels < 1 || bytes < 1)
        {
            why = channels < 1 ? "states no channels" : "states no sample width";
            return null;
        }

        long frameBytes = (long)bytes * channels;
        if (blockAlign != 0 && blockAlign != frameBytes)
        {
            why = $"states a block alignment of {blockAlign} bytes, which is not one sample frame of {frameBytes} bytes ({Counts.Of(bytes, "byte", "bytes")} for each of {Counts.Of(channels, "channel", "channels")})";
            return null;
        }

        if (PcmEncodingOf(tag, bytes * 8, bits) is not { } encoding)
        {
            why = $"states {Counts.Of(bits, "bit", "bits")} per sample, which is no sample width the coding {WaveFormatNames.Show(tag)} is read with";
            return null;
        }

        return new PcmCoding
        {
            Encoding = encoding,
            IsBigEndian = isBigEndian,
            ContainerBits = bytes * 8,
            SampleRate = (int)Math.Min(sampleRate, int.MaxValue),
            ChannelLayout = layout,
        };
    }
}
