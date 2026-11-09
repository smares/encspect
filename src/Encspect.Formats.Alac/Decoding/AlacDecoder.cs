// Follows Apple's ALAC reference implementation (macosforge/alac, ALACDecoder.cpp, Init), Apache License 2.0:
// Copyright (c) 2011 Apple Inc. See THIRD-PARTY-NOTICES.md.
using Encspect.Core;

namespace Encspect.Formats.Alac.Decoding;

// The decoder of Apple Lossless (Apple's ALAC reference implementation, macosforge/alac, ALACDecoder.cpp, dp_dec.c and matrix_dec.c, Apache License 2.0). It reads none
// of the coded syntax itself: the parser's walk stores each element's residuals, uncompressed samples and prediction parameters in the buffers the session lends
// (StoredLayout), or, for a frame the walk did not store, this decoder runs the same walk over the frame's bytes. What it adds is the reconstruction: the adaptive
// predictors, the shifted bytes and the undoing of the mixing of a channel pair. ALAC stores no checksum, so there is nothing to compare; the host opens the decoder
// only where a front end asks for measurements or output.
public sealed class AlacDecoder : IDecoder
{
    public string Id => AlacCodecParser.CodecId;

    public bool DeclaresChecksums => false;

    // The most a legitimate file charges for a byte: digital silence. Apple's own encoder codes a frame of 4,096 stereo samples of silence in 32 bytes, 7,164 units a byte
    // (28 units a sample with its filters of 4 coefficients at the weights before the tap weight of 4), and 6,999 for eight channels at 16 bits (docs/design/decoders.md,
    // "As built: ALAC").
    public long WorkPerInputByte => 8192;

    public IFrameDecoder Open(DecodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        MappedBytes configuration = context.Setup.Configuration
            ?? throw new InvalidDataException("The stream has no magic cookie, so the frame length, the bit depth and the channels of its frames are not known");
        AlacCookie cookie = AlacCookie.Read(configuration.Span);
        if (!cookie.IsComplete)
        {
            throw new InvalidDataException("The magic cookie is shorter than the 24 bytes of the configuration");
        }

        // The refusal of a compatible version above 0 is Apple's (ALACDecoder.cpp, Init). The others are Encspect's: they keep the decoder to what its buffers and the PCM
        // format hold, and a sample rate of 0 is no format (Apple's decoder ignores the rate).
        if (cookie.CompatibleVersion > 0)
        {
            throw new InvalidDataException($"The magic cookie states the compatible version {cookie.CompatibleVersion}, which Apple's decoder refuses");
        }

        if (!cookie.IsFrameLengthValid)
        {
            throw new InvalidDataException($"The magic cookie states a frame length of {cookie.FrameLength} samples, not 1 to {AlacCookie.MaxFrameLength}");
        }

        if (!cookie.IsBitDepthValid)
        {
            throw new InvalidDataException($"The magic cookie states a bit depth of {cookie.BitDepth}; Apple's decoder writes 16, 20, 24 and 32");
        }

        if (!cookie.AreChannelsValid)
        {
            throw new InvalidDataException($"The magic cookie states {cookie.Channels} channels, not 1 to {AlacCookie.MaxChannels}");
        }

        if (cookie.SampleRate == 0 || cookie.SampleRate > int.MaxValue)
        {
            throw new InvalidDataException($"The magic cookie states a sample rate of {cookie.SampleRate} Hz");
        }

        return new FrameDecoder(context, cookie);
    }
}
