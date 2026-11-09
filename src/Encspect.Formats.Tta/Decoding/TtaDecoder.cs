using Encspect.Core;

namespace Encspect.Formats.Tta.Decoding;

// The decoder of TTA1 (audio format 1). The parser finds the frames and compares each one's CRC-32; this decoder reads the adaptive Rice codes of the frame and adds
// the reconstruction: the adaptive filter, the fixed first-order predictor and the undoing of the channel decorrelation. The format description of the TTA authors
// covers the container only; the stages follow a clean-room description of the authors' codec (docs/development.md, "TTA decoding"). The stream stores no checksum
// over the decoded audio, so the decoder runs only where a front end asks for the samples.
public sealed class TtaDecoder : IDecoder
{
    public string Id => TtaCodecParser.CodecId;

    public bool DeclaresChecksums => false;

    public IFrameDecoder Open(DecodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        MappedBytes configuration = context.Setup.Configuration
            ?? throw new InvalidDataException("The stream has no TTA1 header, so the number of samples in its last frame is not known");
        if (configuration.Length < TtaHeader.Length || !TtaHeader.HasSignature(configuration.Span))
        {
            throw new InvalidDataException("The configuration is not a TTA1 header");
        }

        TtaHeader header = TtaHeader.Read(configuration.Span);
        if (header.AudioFormat == TtaHeader.FormatEncrypted)
        {
            throw new InvalidDataException("The stream is stored with a password (audio format 2), which the decoder does not take");
        }

        if (header.AudioFormat != TtaHeader.FormatSimple)
        {
            throw new InvalidDataException($"The header states the audio format {header.AudioFormat}; the decoder takes format 1");
        }

        if (header.BitsPerSample is < 1 or > 24)
        {
            throw new InvalidDataException($"The header states {header.BitsPerSample} bits per sample; the decoder takes 1 to 24");
        }

        if (header.Channels < 1 || header.SampleRate == 0)
        {
            throw new InvalidDataException("The header states no channels or no sample rate");
        }

        return new FrameDecoder(context, header);
    }
}
