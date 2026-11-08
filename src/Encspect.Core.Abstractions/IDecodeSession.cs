namespace Encspect.Core;

// The parser's side of a decoding: one stream, fed frame by frame while the parser reads them.
public interface IDecodeSession
{
    // False where nothing is decoded (not asked for, no decoder, stopped): the parser then builds no CodedFrame.
    bool IsDecoding { get; }

    // Lends the parser the buffers its walk over the next frame's entropy-coded data fills with warm-up samples and
    // residuals, so the decoder reads only what the walk does not store (CodedFrame.IsWalked) and the data is read once.
    // samples: the most per channel the frame can hold. needsWide: also lend 64-bit planes. The buffers are the session's
    // own, grown to the largest frame seen; empty where IsDecoding is false, or where the frame would pass the host's
    // limit of 2^24 samples over all channels, and the parser then walks without storing and hands the frame not walked.
    SampleBuffers Lend(int samples, int channels, bool needsWide = false);

    // Hands the next frame to the decoder, in stream order. Returns IsDecoding afterward: false once the budget, the
    // front end's stop, a sink's exception or a decoder failure ended the session.
    bool Decode(in CodedFrame frame);

    // States the range of positions the stream's own syntax trims to (Opus's pre-skip and end trimming, Vorbis's and Speex's granule
    // positions, Musepack's header, WMA Professional's frames), at the coded stage as CodedFrame.FirstSample: the positions before
    // firstPosition and from endPosition on are Trimmed Samples, which the measurements count apart from lost and withheld samples and
    // leave out of their figures (docs/design/lossy-decoders.md, decision 8). The sink's PCM stays untrimmed. A parser calls it before the
    // Decode of the first frame that the leading trim reaches and of the first that the trailing trim reaches; a trim stated later only
    // affects blocks written after it. Not calling it trims nothing. Once a block past the end has been written the end can only stay or shrink. Throws ArgumentOutOfRangeException for a negative firstPosition or an endPosition before it, whether or not anything is decoded; that is a parser bug (parse.failed).
    void Trim(long firstPosition, long endPosition);

    // Ends the session, once. stored lists every checksum the stream stores, of either kind, with Stored set, and for
    // those over coded data what the parser compared. The host fills in the decoder's results for those over decoded
    // audio and returns the verification the parser puts on its StreamInfo. Further calls return the same verification.
    StreamVerification Finish(IReadOnlyList<ChecksumTally> stored);
}
