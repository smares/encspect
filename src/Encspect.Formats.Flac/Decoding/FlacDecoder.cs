using Encspect.Core;

namespace Encspect.Formats.Flac.Decoding;

// The decoder of FLAC (RFC 9639): it reads nothing of the coded syntax itself. The parser's walk stores each frame's warm-up samples and
// residuals in the buffers the session lends (and the description of every subframe after them), or, for a frame the walk did not store,
// this decoder runs the same walk over the frame's bytes. What it adds is the reconstruction: the predictors, the wasted bits, the undoing
// of left/side, side/right and mid/side coding, and the MD5 of the samples that STREAMINFO stores.
public sealed class FlacDecoder : IDecoder
{
    // The largest frame the specification allows, in samples over all channels: 65,535 samples of 8 channels.
    internal const long MaxSamplesPerFrame = 65535L * 8;

    public string Id => FlacCodecParser.CodecId;

    public IFrameDecoder Open(DecodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        MappedBytes configuration = context.Setup.Configuration
            ?? throw new InvalidDataException("The stream has no STREAMINFO block, so the sample rate, channels and bit depth of its frames are not known.");
        if (configuration.Length < StreamInfoBlock.Length)
        {
            throw new InvalidDataException("The STREAMINFO block is shorter than the 34 bytes it needs.");
        }

        StreamInfoBlock info = StreamInfoBlock.Parse(configuration.Span);
        if (info.BitsPerSample < 4)
        {
            throw new InvalidDataException($"STREAMINFO states {info.BitsPerSample} bits per sample; the specification allows 4 to 32.");
        }

        return new FrameDecoder(context, info, configuration);
    }
}
