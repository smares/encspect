using Encspect.Core;

namespace Encspect.Formats.Speex.Decoding;

// The decoder of Speex in narrowband, wideband and ultra-wideband, with in-band stereo: libspeex 1.2.1's fixed-point decoder with the perceptual enhancer on, bit for bit
// (the code follows its BSD-licensed sources, which carry their notice in each file; THIRD-PARTY-NOTICES.md has the text). The parser's entropy pass (SpeexSyntax) reads the
// parameters of every frame into the buffers the session lends; this decoder adds the reconstruction. The stream stores no checksum over its audio, so the decoder runs only where
// a front end asks for the samples (docs/design/lossy-decoders.md).
public sealed class SpeexDecoder : IDecoder
{
    public string Id => SpeexParser.CodecId;

    public bool DeclaresChecksums => false;

    // The cheapest legitimate packets, the 2.15 kbps vocoder mode and ultra-wideband at about 4 kbps, need about 4,000 to 6,100 units a byte at the weights of FrameDecoder.SynthesisUnits (docs/design/lossy-decoders.md,
    // "Per-byte allowance").
    public long WorkPerInputByte => 8192;

    public IFrameDecoder Open(DecodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Setup.Configuration is not { } configuration || SpeexHeader.Read(configuration.Span) is not { } header)
        {
            throw new InvalidDataException("The stream gives no Speex header to start the decoder with");
        }

        if (!header.IsModeValid)
        {
            throw new InvalidDataException("libspeex refuses the stream: its mode is none of narrowband, wideband and ultra-wideband");
        }

        if (header.VersionId > 1 || header.ModeBitstreamVersion != 4)
        {
            throw new InvalidDataException("libspeex refuses the stream: its header names a bit-stream version the library does not decode");
        }

        if (header.Rate <= 0)
        {
            throw new InvalidDataException("The header gives no sample rate, so the decoded samples have none");
        }

        return new FrameDecoder(context, header);
    }
}
