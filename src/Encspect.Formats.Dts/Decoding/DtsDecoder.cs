using Encspect.Core;

namespace Encspect.Formats.Dts.Decoding;

// The decoder of the DTS core (ETSI TS 102 114 V1.6.1, docs/design/lossy-decoders.md; the high-frequency vectors' table D.10 is format data, docs/development.md): the entropy pass of the parser (SubframeWalker) reads every
// subframe's side information and quantization indexes into the buffers the session lends, and this decoder adds the reconstruction: inverse quantization, subband prediction, joint
// intensity coding, sum/difference decoding of the front and surround pairs, the 32-band synthesis filter bank the header selects and the interpolation of the LFE channel. It outputs
// the core's channels at the core's sampling frequency in single precision floats, full scale 1.0, and no extension. The stream stores no checksum over its audio, so it runs only where a
// front end asks for the samples.
public sealed class DtsDecoder : IDecoder
{
    public string Id => DecodeFeed.CodecId;

    public bool DeclaresChecksums => false;

    // A 5.1 frame costs (22 + 112) x 5 + (22 + 8) + 5 x 5 = 725 units a sample. The smallest frame the syntax allows at 512 samples (NBLKS 15, FSIZE 95: 96 bytes) is about 371,000
    // units, 3,900 a byte, and a 5.1 stream at 48 kHz and 64 kbps (RATE 3) needs about 34.8 million units a second against 8,000 bytes: 4,350 a byte. 8,192 covers both; a frame of
    // more samples in fewer bytes (valid syntax, no encoder writes one) can exceed it and stops with decode.limit.
    public long WorkPerInputByte => 8192;

    public IFrameDecoder Open(DecodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Setup.Shared is not DecodeShared shared || shared.Positions.Count != context.Setup.Channels || context.Setup.Channels is < 1 or > 6)
        {
            throw new InvalidDataException("The stream gives no configuration to start the decoder with");
        }

        // The arrangements 10 to 15 and the user defined ones are valid streams whose channel pairs the decoder does not know (docs/formats/dts.md): reported once, not for every frame.
        if (shared.Amode > 9)
        {
            throw new InvalidDataException($"The channel arrangement {shared.Amode} is not decoded");
        }

        return new FrameDecoder(context, shared);
    }
}
