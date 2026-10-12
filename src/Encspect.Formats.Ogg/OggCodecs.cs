namespace Encspect.Formats.Ogg;

// A codec as an Ogg stream's first packet names it: the codec ID the packets are routed by, and
// its name.
internal sealed record OggCodec(string Id, string Name, byte[] Magic);

// The codecs Ogg mappings define, by the start of their identification header: Speex
// ("Speex   ", speex_header.h), Vorbis ("\x01vorbis", Vorbis I specification, section 4.2.1),
// Opus ("OpusHead", RFC 7845, section 5.1), FLAC ("\x7FFLAC", the FLAC Ogg mapping), Theora
// ("\x80theora", Theora specification, section 6.1), Skeleton ("fishead\0", the Ogg Skeleton 4
// specification), CELT ("CELT    "), Kate ("\x80kate\0\0\0"), Dirac ("BBCD\0"), Ogg PCM
// ("PCM     ") and VP8 ("OVP80"). Only the ID matters to the container: whether a codec parser
// reads the packets is the host's to say.
internal static class OggCodecs
{
    public static IReadOnlyList<OggCodec> All { get; } =
    [
        new("speex", "Speex", "Speex   "u8.ToArray()),
        new("vorbis", "Vorbis", [0x01, .. "vorbis"u8]),
        new("opus", "Opus", "OpusHead"u8.ToArray()),
        new("flac", "FLAC", [0x7F, .. "FLAC"u8]),
        new("theora", "Theora", [0x80, .. "theora"u8]),
        new("skeleton", "Skeleton", "fishead\0"u8.ToArray()),
        new("celt", "CELT", "CELT    "u8.ToArray()),
        new("kate", "Kate", [0x80, .. "kate\0\0\0"u8]),
        new("dirac", "Dirac", "BBCD\0"u8.ToArray()),
        new("pcm", "PCM", "PCM     "u8.ToArray()),
        new("vp8", "VP8", "OVP80"u8.ToArray()),
    ];

    // The longest magic, which is as much of the first packet as identification needs.
    public const int MagicLength = 8;

    public static OggCodec? Identify(ReadOnlySpan<byte> start)
    {
        foreach (OggCodec codec in All)
        {
            if (start.StartsWith(codec.Magic))
            {
                return codec;
            }
        }

        return null;
    }
}
