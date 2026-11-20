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

    // The correction file's bitstream (a version 3 hybrid file's .wvc has no header: all of its bytes are the bitstream) in the host's reader over it, or null where the
    // stream is decoded without one. HasCorrection is libwavpack's wvc_flag.
    public ByteReader? CorrectionReader { get; init; }

    public long CorrectionAt { get; init; }

    public long CorrectionEnd { get; init; }

    public bool HasCorrection => CorrectionReader is not null;

    // What the decoder found of the correction bitstream once the samples were decoded: how many of its bytes the last bit read lies in (-1 where it did not get so far), and
    // whether it ended before the samples did. The parser places the node of the bytes that were not reached by it; this is the one thing the decoder gives back.
    public long CorrectionRead { get; set; } = -1;

    public bool IsCorrectionShort { get; set; }

    // What the decoder tells the feed, so that the correction bytes are handed over by its demand and not by a share of the file: how many it has taken from the bitstream
    // (those of 0xFF past the end included), whether it waits for more before it can go on, and whether it has ended (all samples decoded, or the main bitstream ended).
    public long CorrectionTaken { get; set; }

    public bool IsCorrectionStarved { get; set; }

    public bool IsDecoded { get; set; }
}

internal static partial class Legacy
{
    // The bytes of the bitstream handed to the decoder at a time.
    internal const int FeedLength = 1 << 18;

    // The correction bytes the decoder is kept supplied with beyond those it has taken: a window, so that neither the decoder's buffer nor the feed's grows with the correction
    // file, whatever its length and however fast the stream's words use it.
    private const int CorrectionWindow = 1 << 20;

    // Starts the decoding session of the stream, feeds it the bytes from the bitstream's start to the end of the data and ends it with the checksum the stream stores. A
    // correction bitstream is fed by the decoder's demand: each frame carries the bytes up to a window beyond those the decoder has taken, and while the decoder waits for more
    // it is given frames of correction bytes alone before the next main bytes. Nothing is read where decoding is not asked for.
    private static StreamVerification Decode(ParseContext context, ByteReader reader, LegacyDecodeInfo info, Node stream, Node bitstream, IReadOnlyList<ChecksumTally> checksums)
    {
        IDecodeSession session = context.StartDecoding(WavPackCodecParser.CodecId, new DecodeSetup
        {
            Stream = stream,
            Channels = info.Channels,
            BitsPerSample = info.OutputBits,
            Samples = info.Samples,
            Shared = info,
            IsLossy = info.Header.Bits != 0 && !info.HasCorrection,
            HasCorrection = info.HasCorrection,
        });
        if (session.IsDecoding)
        {
            byte[] buffer = new byte[FeedLength];
            byte[] corrections = [];
            long correctionPosition = info.CorrectionAt;
            MappedBytes? Correction()
            {
                if (!info.HasCorrection)
                {
                    return null;
                }

                long target = Math.Min(info.CorrectionEnd, info.CorrectionAt + info.CorrectionTaken + CorrectionWindow);
                int length = (int)Math.Max(0, target - correctionPosition);
                if (corrections.Length < length)
                {
                    corrections = new byte[length];
                }

                info.CorrectionReader!.Position = correctionPosition;
                info.CorrectionReader.Read(corrections.AsSpan(0, length));
                var bytes = new MappedBytes(corrections.AsMemory(0, length), correctionPosition);
                correctionPosition += length;
                return bytes;
            }

            long position = info.BitstreamAt;
            bool isDecoding = true;
            do
            {
                int length = (int)Math.Min(FeedLength, info.DataEnd - position);
                reader.Position = position;
                reader.Read(buffer.AsSpan(0, length));
                position += length;
                isDecoding = session.Decode(new CodedFrame(new MappedBytes(buffer.AsMemory(0, length), position - length), "Bitstream", 0, 0) { Node = bitstream, Correction = info.IsDecoded ? null : Correction() });

                // The decoder waits for correction bytes it has not been given: they come before the next main bytes, so that the main bitstream is not held meanwhile.
                while (isDecoding && info.IsCorrectionStarved && correctionPosition < info.CorrectionEnd)
                {
                    isDecoding = session.Decode(new CodedFrame(new MappedBytes(ReadOnlyMemory<byte>.Empty, position), "Bitstream", 0, 0) { Node = bitstream, Correction = Correction() });
                }
            }
            while (isDecoding && position < info.DataEnd);
        }

        return session.Finish(checksums);
    }
}
