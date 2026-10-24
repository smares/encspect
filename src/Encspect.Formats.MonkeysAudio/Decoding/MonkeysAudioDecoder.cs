using Encspect.Core;

namespace Encspect.Formats.MonkeysAudio.Decoding;

// The decoder of Monkey's Audio files of version 3.99 (and 4.11, which the SDK reads the same way), after the SDK 13.42's: the range decoder, the
// predictors with their neural network filters, the undoing of the mid/side coding of channel pairs and the CRC over the decoded audio. The parser
// finds the frames, reads each one's CRC and special codes and hands the frame's bytes; this decoder reads the coded samples from there. Files of
// earlier versions use other coding of the samples and are not decoded (docs/formats/monkeys-audio.md, "Decoding").
public sealed class MonkeysAudioDecoder : IDecoder
{
    // Units of work a byte of the file may take. A sample of the highest level costs 1,032 units a channel (16 for the sample, 192 for the range
    // decoder, 48 for the predictor and 776 for the filters) and the cheapest symbol of the range coder takes 1.74 bits, so a legitimate file
    // takes at most 1,032 / 0.2175 = 4,745 units a byte, rounded up to 8,192 (docs/design/decoders.md, "The default bound").
    public const long PerInputByte = 8192;

    public string Id => MonkeysAudioCodec.Id;

    public long WorkPerInputByte => PerInputByte;

    public IFrameDecoder Open(DecodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        MappedBytes configuration = context.Setup.Configuration
            ?? throw new InvalidDataException("The stream has no header, so the format of its frames is not known");
        ReadOnlySpan<byte> bytes = configuration.Span;
        if (bytes.Length < ApeHeader.DescriptorLength + ApeHeader.HeaderLength || ApeHeader.VersionOf(bytes) < ApeHeader.FirstDescriptorVersion)
        {
            throw new InvalidDataException($"The file is of version {ApeHeader.VersionText(ApeHeader.VersionOf(bytes))}, whose frames are not decoded; the decoder takes version 3.99 files");
        }

        ApeHeader header = ApeHeader.ReadDescriptor(bytes).WithHeader(bytes[ApeHeader.DescriptorLength..]);
        if (header.FileVersion != 3990 && header.FileVersion != 4110)
        {
            throw new InvalidDataException($"The file is of version {ApeHeader.VersionText(header.FileVersion)}, whose frames are not decoded; the decoder takes version 3.99 files");
        }

        if (!ApeHeader.IsKnownLevel(header.CompressionLevel))
        {
            throw new InvalidDataException($"The compression level {header.CompressionLevel} is none of 1000 (fast) to 5000 (insane)");
        }

        if (header.BitsPerSample is not (8 or 16 or 24 or 32))
        {
            throw new InvalidDataException($"The header states {header.BitsPerSample} bits per sample; the decoder writes 8, 16, 24 or 32");
        }

        if (header.Channels is < 1 or > ApeHeader.MaximumChannels)
        {
            throw new InvalidDataException($"The header states {header.Channels} channels; the decoder takes 1 to 32");
        }

        if (header.BlocksPerFrame == 0 || header.BlocksPerFrame > header.MaximumBlocksPerFrame)
        {
            throw new InvalidDataException($"The header states {header.BlocksPerFrame} samples in a frame; the decoder takes 1 to {header.MaximumBlocksPerFrame}");
        }

        return new FrameDecoder(context, header, header.DataOffset(configuration.FileStart));
    }
}
