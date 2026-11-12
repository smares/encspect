using Encspect.Core;

namespace Encspect.Formats.Opus.Decoding;

// The decoder of Opus (RFC 6716 as RFC 8251 updates it, with the multistream mappings of RFC 7845): SILK, CELT and hybrid frames of one or more streams, decoded at 48 kHz to
// planar floats at full scale 1.0, with the header's output gain. The parser's entropy pass (OpusWalker) reads every frame into the buffers the session lends; the decoder
// adds the reconstruction. The code follows libopus 1.5.2 (BSD-3-Clause, notice in THIRD-PARTY-NOTICES.md) and RFC 6716's reference decoder. The stream stores no checksum over
// its audio, so the decoder runs only where a front end asks for the samples. A frame of no data (discontinuous transmission, or lost) and a gap are not concealed: the
// decoder starts over and withholds 80 ms, the pre-roll of RFC 7845, sections 4.2 and 4.6 (docs/design/lossy-decoders.md, decisions 4 and 5).
public sealed class OpusDecoder : IDecoder
{
    public string Id => OpusParser.CodecId;

    public bool DeclaresChecksums => false;

    // Coded audio at 6 kbps takes about 2,800 units a byte (mono) and silence needs far more per byte, which the floor of the budget covers for hours
    // (docs/design/lossy-decoders.md, "Per-byte allowance", decision 18).
    public long WorkPerInputByte => 8192;

    public IFrameDecoder Open(DecodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Setup.Configuration is not { } configuration || OpusHead.Read(configuration.Span) is not { } head)
        {
            throw new InvalidDataException("The stream gives no identification header to configure the decoder with");
        }

        if (!OpusDecodable.Check(head))
        {
            throw new InvalidDataException("The identification header is not one a decoder accepts");
        }

        return new FrameDecoder(context, head);
    }
}
