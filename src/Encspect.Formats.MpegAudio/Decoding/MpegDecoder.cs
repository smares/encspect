using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.MpegAudio.Decoding;

// The decoder of MPEG audio, Layers I, II and III (MPEG-1, MPEG-2, MPEG-2.5, free format). The parser's entropy pass reads the scalefactors, Huffman codes and sample
// codes of every frame into the buffers the session lends (Layer3Walker, Layer12Scale); this decoder adds the reconstruction: requantization, stereo processing, the
// antialias butterflies, the inverse transforms and the polyphase synthesis. The code follows minimp3 (CC0 1.0, third_party/minimp3). The stream stores no checksum over
// its audio, so the decoder runs only where a front end asks for the samples (docs/design/lossy-decoders.md).
public sealed class MpegDecoder : IDecoder
{
    public string Id => MpegAudioParser.CodecId;

    public bool DeclaresChecksums => false;

    // The cheapest legitimate frames are those of 8 kbps MPEG-2.5 at 8 kHz, which need about 600 units a byte (docs/design/lossy-decoders.md, "Per-byte allowance").
    public long WorkPerInputByte => 2048;

    public IFrameDecoder Open(DecodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Setup.Configuration is not { } configuration || configuration.Length < 4)
        {
            throw new InvalidDataException("The stream gives no frame header to configure the decoder with");
        }

        var header = new MpegHeader(BinaryPrimitives.ReadUInt32BigEndian(configuration.Span));
        if (!header.IsValid)
        {
            throw new InvalidDataException("The stream's first frame header is not valid");
        }

        return new FrameDecoder(context, header, context.Setup.Channels, context.Setup.Shared as MultichannelPlan);
    }
}
