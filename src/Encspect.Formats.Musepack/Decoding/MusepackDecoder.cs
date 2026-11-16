using Encspect.Core;

namespace Encspect.Formats.Musepack.Decoding;

// The decoder of Musepack, stream versions 7 and 8 and 4 to 6 as libmpcdec 1.2.6 decodes them. The parser's entropy pass (Sv7Syntax, Sv8Syntax, Sv6Syntax) reads the scalefactors
// and quantized samples of every frame into the buffers the session lends; this decoder adds the reconstruction: requantization, mid/side stereo and the polyphase synthesis.
// The code follows libmpcdec r495 and 1.2.6 (BSD 3-clause, notice in THIRD-PARTY-NOTICES.md), in its floating-point build. The stream stores no checksum over its audio, so the
// decoder runs only where a front end asks for the samples (docs/design/lossy-decoders.md).
public sealed class MusepackDecoder : IDecoder
{
    public string Id => MusepackParser.CodecId;

    public bool DeclaresChecksums => false;

    // Constant-bitrate frames at the lowest rates need under 1,000 units a byte (docs/design/lossy-decoders.md, "Per-byte allowance"): a frame of 1,152 samples in two channels
    // costs about 120,000 units, and the cheapest coded frames take under 100 bytes.
    public long WorkPerInputByte => 2048;

    public IFrameDecoder Open(DecodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Setup.Configuration is not { } configuration || !MusepackConfiguration.TryRead(configuration.Span, out MusepackConfiguration setup))
        {
            throw new InvalidDataException("The stream gives no configuration to start the decoder with");
        }

        if (setup.IsRefused)
        {
            throw new InvalidDataException("libmpcdec refuses the stream: its header breaks a rule libmpcdec checks, so it decodes no sample");
        }

        if (setup.Channels is < 1 or > 2)
        {
            throw new InvalidDataException("libmpcdec decodes streams of 1 or 2 channels only");
        }

        return new FrameDecoder(context, setup);
    }
}
