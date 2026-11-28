using Encspect.Core;

namespace Encspect.Formats.Wma.Decoding;

// The decoder of Windows Media Audio versions 1 and 2: the parser's walk (StdFrameReader) reads every frame's syntax into the buffers the session lends, and this decoder adds the
// reconstruction that docs/design/wma-standard-decoding.md describes (written without code from FFmpeg's decoder, whose own sources this one never saw): the dequantization with
// the exponents of both codings, noise substitution, mid/side stereo, the inverse transform and the windows and overlap of blocks of different sizes. It computes in single
// precision for the transform and in double precision for the scalar setup, with DetMath for every power and trigonometric function, so its output is the same on every platform.
// The stream stores no checksum over its audio, so the decoder runs only where a front end asks for the samples. Microsoft's decoder is the reference for the output, and
// docs/formats/asf-wma.md lists where this one cannot follow it (the noise generator) or follows FFmpeg's rule where the description leaves a point open.
public sealed class WmaStandardDecoder : IDecoder
{
    public string Id => StdDecodeFeed.CodecId;

    public bool DeclaresChecksums => false;

    public IFrameDecoder Open(DecodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Setup.Shared is not StdParameters parameters)
        {
            throw new InvalidDataException("The parser gave no parameters to decode Windows Media Audio with");
        }

        if (parameters.Unsupported is { } reason)
        {
            throw new InvalidDataException(reason);
        }

        return new StdFrameDecoder(context, parameters);
    }
}
