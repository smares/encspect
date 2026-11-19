using Encspect.Core;

namespace Encspect.Formats.WavPack;

// What the decoder of a legacy stream needs from the parser, handed over as DecodeSetup.Shared: the header, where the bitstream lies, the audio's layout and the parts
// of the tree the decoder's findings point to (it builds nodes equal in name, offset and length). The decoder reads the bytes the parser feeds it and nothing else.
internal sealed class LegacyDecodeInfo
{
    public required LegacyHeader Header { get; init; }

    public required bool IsRaw { get; init; }

    // The channels, the rate and the bits of a sample the decoder outputs (16 or 24, by the header's 3-byte flag).
    public required int Channels { get; init; }

    public required int OutputBits { get; init; }

    // The bits of a sample the stored WAV header states (16 for a raw file).
    public required int SourceBits { get; init; }

    public required long SampleRate { get; init; }

    // The samples of each channel the stream holds: the header's count in version 3, the data chunk's in versions 1 and 2.
    public required long Samples { get; init; }

    // The Bitstream node's place, the end of the data the parser feeds (the end of the stream's window), and where the parser placed the end of the bitstream: where a
    // trailer begins, or the end of the data.
    public required long BitstreamAt { get; init; }

    public required long BitstreamEnd { get; init; }

    public required long DataEnd { get; init; }

    public required bool HasTrailer { get; init; }

    // Why libwavpack's open refuses the file, or null where it opens it; the decoder refuses a stream for the same reasons.
    public string? Refusal { get; init; }
}

internal static partial class Legacy
{
    // The bytes of the bitstream handed to the decoder at a time.
    internal const int FeedLength = 1 << 18;

    // Starts the decoding session of the stream, feeds it the bytes from the bitstream's start to the end of the data and ends it with the checksum the stream stores.
    // Nothing is read where decoding is not asked for.
    private static StreamVerification Decode(ParseContext context, ByteReader reader, LegacyDecodeInfo info, Node stream, Node bitstream, IReadOnlyList<ChecksumTally> checksums)
    {
        IDecodeSession session = context.StartDecoding(WavPackCodecParser.CodecId, new DecodeSetup
        {
            Stream = stream,
            Channels = info.Channels,
            BitsPerSample = info.OutputBits,
            Samples = info.Samples,
            Shared = info,
            IsLossy = info.Header.Bits != 0,
        });
        if (session.IsDecoding)
        {
            byte[] buffer = new byte[FeedLength];
            long position = info.BitstreamAt;
            do
            {
                int length = (int)Math.Min(FeedLength, info.DataEnd - position);
                reader.Position = position;
                reader.Read(buffer.AsSpan(0, length));
                bool isDecoding = session.Decode(new CodedFrame(new MappedBytes(buffer.AsMemory(0, length), position), "Bitstream", 0, 0) { Node = bitstream });
                position += length;
                if (!isDecoding)
                {
                    break;
                }
            }
            while (position < info.DataEnd);
        }

        return session.Finish(checksums);
    }
}
