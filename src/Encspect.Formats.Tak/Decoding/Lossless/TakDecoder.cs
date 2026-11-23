using Encspect.Core;

namespace Encspect.Formats.Tak.Decoding.Lossless;

// The decoder of TAK (codec 2, and codec 4 for more than two channels), from the clean-room description docs/design/tak-format.md: the parser finds the frames and compares
// the two CRC-24s of each; this decoder reads the coded samples (the residual codes, the prediction filters, the decorrelation of channels) and adds the MD5 of the decoded audio
// where the stream stores one. The description covers the coding only; the container is the parser's (docs/development.md, "TAK decoding (clean-room)").
public sealed class TakDecoder : IDecoder
{
    // The MD5 object is the one checksum over decoded audio, so the host decodes also where nobody asks for samples.
    public bool DeclaresChecksums => true;

    public string Id => Summary.CodecId;

    // A frame of silence takes a few bytes for 16,384 samples of six channels (about 76,000 units a byte at its worst, DecoderChargeTests); derived in docs/design/decoders.md, "As built: the TAK decoder".
    public long WorkPerInputByte => 1 << 17;

    public IFrameDecoder Open(DecodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Setup.Shared is not DecodeShared setup)
        {
            throw new InvalidDataException("The stream has no stream info object, so the decoder does not know its format");
        }

        StreamInfoFields info = setup.Info;
        if (info.Codec is not (2 or 4))
        {
            throw new InvalidDataException($"The stream info states the codec {info.Codec}{(Layout.CodecName(info.Codec) is { } name ? $" ({name})" : string.Empty)}; the decoder takes 2 (TAK 2.0) and 4 (TAK 2.2)");
        }

        if (info.DataType != 0)
        {
            throw new InvalidDataException($"The stream info states the data type {info.DataType}; the decoder takes 0 (PCM)");
        }

        if (info.BitsPerSample is not (8 or 16 or 24))
        {
            throw new InvalidDataException($"The stream info states {info.BitsPerSample} bits per sample; the decoder takes 8, 16 and 24");
        }

        int limit = info.Codec == 2 ? 2 : 6;
        if (info.Channels > limit)
        {
            throw new InvalidDataException($"The stream info states {Counts.Of(info.Channels, "channel", "channels")}; the decoder takes up to {limit} for the codec {info.Codec}");
        }

        if (!Layout.IsFrameSizeValid(info.FrameSizeType, info.SampleRate))
        {
            throw new InvalidDataException("The stream info states a frame size that is not valid for its sample rate, so the frames' lengths are not known");
        }

        return new FrameDecoder(context, setup, Layout.FrameSamples(info.FrameSizeType, info.SampleRate));
    }
}
