using Encspect.Core;

namespace Encspect.Formats.MonkeysAudio.Decoding;

// The decoder of Monkey's Audio files of versions 3.80 to 3.99 and 4.11 (which the SDK reads like 3.99), after the SDK 13.44's: the range decoders
// of 3.91 on, the predictors with their neural network filters, the undoing of the mid/side coding of channel pairs and the CRC over the decoded audio.
// The parser finds the frames, reads each one's CRC and special codes and hands the frame's bytes; this decoder reads the coded samples from there.
// Files before 3.93 use another core of the SDK (Old/UnMACOld.cpp), which OldFrameDecoder follows (docs/formats/monkeys-audio.md, "Decoding").
public sealed class MonkeysAudioDecoder : IDecoder
{
    // The first file version the SDK's current decoder reads; older files use the one of Old/UnMACOld.cpp, down to the oldest the parser reads.
    private const int FirstVersion = 3930;

    private const int FirstOldVersion = 3800;

    // Units of work a byte of the file may take. A sample of the highest level costs 1,032 units a channel (16 for the sample, 192 for the range
    // decoder, 48 for the predictor and 776 for the filters; 1,426 at 32 bits: 22, 192, 48 and 1,164) and the cheapest symbol of the range coder
    // takes 1.74 bits, so a legitimate file takes at most 1,032 / 0.2175 = 4,745 units a byte (6,556 at 32 bits), rounded up to 8,192
    // (docs/design/decoders.md, "The default bound").
    public const long PerInputByte = 8192;

    public string Id => MonkeysAudioCodec.Id;

    public long WorkPerInputByte => PerInputByte;

    public IFrameDecoder Open(DecodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        MappedBytes configuration = context.Setup.Configuration
            ?? throw new InvalidDataException("The stream has no header, so the format of its frames is not known");
        ReadOnlySpan<byte> bytes = configuration.Span;
        int version = ApeHeader.VersionOf(bytes);
        if (version is < FirstOldVersion or > 3990 && version != 4110)
        {
            throw new InvalidDataException($"The file is of version {ApeHeader.VersionText(version)}, whose frames are not decoded; the decoder takes versions 3.80 to 3.99 and 4.11");
        }

        ApeHeader header;
        long dataStart;
        if (version >= ApeHeader.FirstDescriptorVersion)
        {
            if (bytes.Length < ApeHeader.DescriptorLength + ApeHeader.HeaderLength)
            {
                throw new InvalidDataException("The stream's descriptor and header are cut");
            }

            header = ApeHeader.ReadDescriptor(bytes).WithHeader(bytes[ApeHeader.DescriptorLength..]);
            dataStart = header.DataOffset(configuration.FileStart);
        }
        else
        {
            if (bytes.Length < ApeHeader.OldHeaderLength)
            {
                throw new InvalidDataException("The stream's header is cut");
            }

            header = ApeHeader.ReadOld(bytes);
            dataStart = header.OldDataOffset(configuration.FileStart);
            if (header.CompressionLevel > 4000 && version < 3950)
            {
                throw new InvalidDataException($"The compression level {header.CompressionLevel} did not exist in files of version {ApeHeader.VersionText(version)}");
            }

            if (version < FirstVersion)
            {
                return OpenOld(context, header, dataStart, bytes);
            }
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

        return new FrameDecoder(context, header, dataStart);
    }

    // Files before 3.93, which the SDK reads with the decoder of Old/UnMACOld.cpp: what that decoder cannot decode is left undecoded here with its reason.
    private static OldFrameDecoder OpenOld(DecodeContext context, ApeHeader header, long dataStart, ReadOnlySpan<byte> configuration)
    {
        int version = header.FileVersion;
        string text = ApeHeader.VersionText(version);
        // CUnBitArrayOld::GenerateArray reads nothing in a file of 3.89 above 3.890, and CAPEDecompressOld refuses a file above 3920 (the file versions are the numbers the header stores: 3.92 is 3920).
        if (version is > 3890 and < 3900)
        {
            throw new InvalidDataException($"The file is of version {text} (file version {version}); the SDK's bit array for files before 3.90 reads no values from a file of a version above 3890, so the audio is not decoded");
        }

        if (version > 3920)
        {
            throw new InvalidDataException($"The file is of version {text} (file version {version}); the SDK's decoder of versions before 3.93 refuses a file of a version above 3920, so the audio is not decoded");
        }

        if (header.CompressionLevel is not (1000 or 2000 or 3000 or 4000))
        {
            throw new InvalidDataException($"The file states the compression level {header.CompressionLevel}; the SDK's decoder of versions before 3.93 reads the levels 1000 to 4000 and fails on any other");
        }

        if (header.Channels is not (1 or 2))
        {
            throw new InvalidDataException($"The header states {header.Channels} channels; the SDK's decoder of versions before 3.93 decodes samples for one or two channels only, so the audio is not decoded");
        }

        if (version >= 3900 && !header.HasStoredCrc)
        {
            throw new InvalidDataException($"The file is of version {text} and has no CRC flag; the SDK's decoder reads no checksum from such a file and gives silence, so the audio is not decoded");
        }

        if (header.BlocksPerFrame == 0)
        {
            throw new InvalidDataException("The file has no samples in a frame");
        }

        byte[] seekBits = [];
        if (version <= 3800)
        {
            long elements = header.SeekElementCount;
            ReadOnlySpan<byte> table = configuration[Math.Min(configuration.Length, header.OldHeaderBytes)..];
            if (table.Length < elements)
            {
                throw new InvalidDataException("The table of the bits the frames start at is not available");
            }

            seekBits = table[..(int)elements].ToArray();
        }

        return new OldFrameDecoder(context, header, dataStart, seekBits);
    }
}
