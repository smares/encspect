using Encspect.Core;

namespace Encspect.Formats.WavPack.Decoding;

// The decoder of WavPack's lossless and hybrid streams, DSD included (libwavpack 5.6.0). It reads the sub-blocks of each block
// with the parser's code (BlockContents), decodes the bitstream, and adds what the parser does not do: the entropy decoding, the
// decorrelation passes, the restoring of floats and large integers, the DSD decoders, and the checks of the CRCs and the MD5 over
// the decoded audio.
public sealed class WavPackDecoder : IDecoder
{
    public string Id => WavPackCodecParser.CodecId;

    public IFrameDecoder Open(DecodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Setup.Channels > 4096)
        {
            throw new InvalidDataException($"The stream has {context.Setup.Channels} channels; WavPack allows 4096");
        }

        // The block libwavpack opens the file with: whatever it refuses to open a file for, the stream cannot be decoded.
        MappedBytes block = context.Setup.Configuration
            ?? throw new InvalidDataException("The stream has no block with audio that passes the checks libwavpack opens a file with, so there is nothing to decode.");
        ReadOnlySpan<byte> data = block.Span;
        if (data.Length < BlockHeader.Length || !BlockHeader.IsPlausible(data))
        {
            throw new InvalidDataException("The first block with audio does not start with a header libwavpack takes.");
        }

        var header = new BlockHeader(data);
        if (header.Version is < BlockHeader.MinVersion or > BlockHeader.MaxVersion)
        {
            throw new InvalidDataException($"The first block with audio has stream version 0x{header.Version:X3}, which libwavpack does not decode.");
        }

        try
        {
            var contents = new BlockContents();
            contents.Read(data, header, channelsAreKnown: false);
            if (header.IsDsd)
            {
                new DsdBlockDecoder().Prepare(data.Slice(contents.Dsd.Start, contents.Dsd.Length), header.IsMonoData);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"The first block with audio cannot be decoded: {char.ToLowerInvariant(ex.Message[0])}{ex.Message[1..]}.", ex);
        }

        return new StreamDecoder(context);
    }
}
