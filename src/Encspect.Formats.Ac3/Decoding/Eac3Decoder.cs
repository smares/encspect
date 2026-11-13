using Encspect.Core;

namespace Encspect.Formats.Ac3.Decoding;

// The decoder of E-AC-3's independent substreams (ATSC A/52:2018 Annex E, ETSI TS 102 366 Annex E), on the foundation of AC-3's: its transforms, window, mantissa dequantization and
// coupling. The parser's entropy pass (FrameSyntax) reads each audio block's side information, exponents, bit allocation and mantissa codes, including the adaptive hybrid
// transform's vectors and gain adaptive values, enhanced coupling's parameters and spectral extension's coordinates, into the buffers the session lends; this decoder adds the
// reconstruction: the inverse DCT across the six blocks of the adaptive hybrid transform, standard and enhanced coupling, rematrixing, spectral extension with its noise blend,
// and the inverse transforms with the window and the overlap. It applies no dynamic range control, dialogue normalization or downmix. Transient pre-noise processing (3.7) is
// applied with the Hanning windows 3.7.2 names, and the dependent substreams' channels join the program's (3.8).
public sealed class Eac3Decoder : IDecoder
{
    public string Id => Ac3CodecParser.Eac3;

    public bool DeclaresChecksums => false;

    // The cheapest legitimate frames of a stereo program are those of 32 kbps (a frame of 1,536 samples in 192 bytes); spectral extension and the adaptive hybrid transform are
    // charged on top of what a frame's samples cost (docs/design/decoders.md, "Per-byte allowance").
    public long WorkPerInputByte => 2048;

    public IFrameDecoder Open(DecodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Setup.Configuration is not { } configuration || configuration.Length < SyncFrame.Length)
        {
            throw new InvalidDataException("The stream gives no syncframe header to configure the decoder with");
        }

        SyncFrame header = SyncFrame.Read(configuration.Span);
        if (!header.IsValid || !header.IsEac3 || header.IsDependent || header.Fscod == 3)
        {
            throw new InvalidDataException("The first frame is not an E-AC-3 independent substream frame at a full sample rate");
        }

        return new Eac3FrameDecoder(context, header);
    }
}
