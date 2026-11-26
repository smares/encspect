using Encspect.Core;

namespace Encspect.Formats.Aac.Decoding;

// The decoder of AAC LC (ISO/IEC 14496-3, subpart 4) at the core's rate: the parser's entropy pass (BlockReader) reads every raw data block's elements into the buffers the
// session lends, and this decoder adds the reconstruction: inverse quantization with the scalefactors, mid/side and intensity stereo, noise substitution, temporal noise
// shaping, and the filter bank (inverse MDCT, windows, overlap-add). The reconstruction follows libxaac (github.com/ittiam-systems/libxaac at 2fbadd5, Apache 2.0, Copyright
// (C) The Android Open Source Project; the notice is in THIRD-PARTY-NOTICES.md) where the specification leaves details to the decoder: the order of the tools, noise
// substitution's generator and its correlation with mid/side stereo, and the TNS filter ranges. It computes in single precision, where libxaac computes in fixed point.
// The stream stores no checksum over its audio, so the decoder runs only where a front end asks for the samples. HE-AAC (SBR and parametric stereo) is decoded on top of the
// core (SbrElement, PsDecoder); downsampled SBR, AAC Main, LTP and SSR, ER AAC, USAC and frames of 960 samples are not (docs/formats/aac.md).
public sealed class AacDecoder : IDecoder
{
    public string Id => DecodeFeed.CodecId;

    public bool DeclaresChecksums => false;

    // Silence is the cheapest legitimate case: 6 bytes for 1,024 samples of stereo, which cost 1,024 * 2 * 22 units and the floor of the stages (docs/design/lossy-decoders.md,
    // "Per-byte allowance").
    public long WorkPerInputByte => 8192;

    public IFrameDecoder Open(DecodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Setup.Shared is not DecodeConfig config)
        {
            throw new InvalidDataException("The parser gave no configuration to decode AAC with");
        }

        if (config.Refusal is { } refusal)
        {
            throw new InvalidDataException(refusal);
        }

        if (config.ObjectType != 2 || config.FrameLength != 1024)
        {
            throw new InvalidDataException("Only AAC LC with frames of 1,024 samples is decoded");
        }

        if (config.IsDownsampled)
        {
            throw new InvalidDataException("Downsampled SBR (the extension rate equals the core's) is not decoded");
        }

        if (config.SampleRate <= 0)
        {
            throw new InvalidDataException("The sample rate is not known");
        }

        return new FrameDecoder(context, config);
    }
}
