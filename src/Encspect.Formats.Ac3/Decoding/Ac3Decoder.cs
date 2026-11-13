using Encspect.Core;

namespace Encspect.Formats.Ac3.Decoding;

// The decoder of AC-3 (ATSC A/52:2018, sections 5 to 7 with Annex D's alternate bit stream syntax; E-AC-3 is a later step). The parser's entropy pass (FrameSyntax) reads the
// side information, the exponents, the bit allocation pointers and the mantissa codes of every audio block into the buffers the session lends; this decoder adds the
// reconstruction: the mantissas' dequantization with the dither of zero-bit mantissas, coupling, rematrixing and the inverse transforms with the window and the overlap. It
// applies no dynamic range control, dialogue normalization or downmix (decision 9 of docs/design/lossy-decoders.md): every coded channel is written in the coded order. The
// frame stores two CRCs over its coded data, which the parser compares, so the decoder declares no checksum over decoded audio and runs only where a front end asks for samples.
public sealed class Ac3Decoder : IDecoder
{
    public string Id => Ac3CodecParser.Ac3;

    public bool DeclaresChecksums => false;

    // The cheapest legitimate frames are those of 32 kbps (128 bytes of 1,536 samples); a stereo frame needs about 1,100 units a byte and a mono frame about 550. Frames of
    // more channels at that rate need more, which the floor of the budget covers (docs/design/lossy-decoders.md, "Per-byte allowance").
    public long WorkPerInputByte => 2048;

    public IFrameDecoder Open(DecodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Setup.Configuration is not { } configuration || configuration.Length < SyncFrame.Length)
        {
            throw new InvalidDataException("The stream gives no syncframe header to configure the decoder with");
        }

        SyncFrame header = SyncFrame.Read(configuration.Span);
        if (!header.IsValid || header.IsEac3 || header.Bsid > 8)
        {
            throw new InvalidDataException("The first frame is not an AC-3 syncframe of a bit stream ID up to 8");
        }

        return new FrameDecoder(context, header);
    }
}
