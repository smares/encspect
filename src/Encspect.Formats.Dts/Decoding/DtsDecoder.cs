using Encspect.Core;

namespace Encspect.Formats.Dts.Decoding;

// The decoder of the DTS core (ETSI TS 102 114 V1.6.1, written from the specification alone, docs/design/lossy-decoders.md): the entropy pass of the parser (SubframeWalker) reads every
// subframe's side information and quantization indexes into the buffers the session lends, and this decoder adds the reconstruction: inverse quantization, subband prediction, joint
// intensity coding, sum/difference decoding of the front and surround pairs, the 32-band synthesis filter bank the header selects and the interpolation of the LFE channel. It outputs
// the core's channels at the core's sampling frequency in single precision floats, full scale 1.0, and no extension. The stream stores no checksum over its audio, so it runs only where a
// front end asks for the samples.
public sealed class DtsDecoder : IDecoder
{
    public string Id => DecodeFeed.CodecId;

    public bool DeclaresChecksums => false;

    // The smallest frames (96 bytes for 256 samples of up to six channels) cost about 256 x 6 x 56 units: 900 a byte, under the default of 2,048 with room for the worst case of
    // an extension-less 5.1 frame at the lowest rates.
    public long WorkPerInputByte => 2048;

    public IFrameDecoder Open(DecodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Setup.Shared is not DecodeShared shared || shared.Positions.Count != context.Setup.Channels || context.Setup.Channels is < 1 or > 6)
        {
            throw new InvalidDataException("The stream gives no configuration to start the decoder with");
        }

        return new FrameDecoder(context, shared);
    }
}
