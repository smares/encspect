using Encspect.Core;

namespace Encspect.Formats.Wma;

// The parser's side of the decoding of one stream of Windows Media Audio versions 1 and 2 (docs/design/lossy-decoders.md, "The parser and decoder split"): the walk of
// StdFrameReader stores each valid frame's values in the writer, and this copies them into the buffers the session lends and hands the frame over with its position at the
// coded stage (frame i at i times the frame length, the frames the parser counts as lost included). Frames the parser could not read are handed over unreadable. The
// decoder's output of frame i is the audio that the overlap of its blocks completes, which lies two frames before it in the signal: the first two frames' output is the
// start the stream's own syntax trims, and the end of the stream adds the part of the last block's overlap that the blocks no longer change.
internal sealed class StdDecodeFeed
{
    internal const string CodecId = "wma";

    private readonly StdParameters _parameters;
    private readonly Node _stream;
    private readonly IDecodeSession _session;
    private long _next;
    private long _tailFrame;
    private bool _isLastValid;

    public StdDecodeFeed(AnalysisContext context, Node stream, StdParameters parameters)
    {
        _parameters = parameters;
        _stream = stream;
        _session = context.StartDecoding(CodecId, new DecodeSetup
        {
            Stream = stream,
            Channels = parameters.Channels,
            BitsPerSample = 32,
            IsLossy = true,
            Shared = parameters,
        });
        _session.Trim(DroppedFrames(parameters) * parameters.FrameLength, long.MaxValue);
    }

    // The frames whose output the stream's own syntax trims: two with the bit reservoir, one without it (Microsoft's decoder, docs/design/wma-standard-decoding.md, "The output of a stream").
    private static long DroppedFrames(StdParameters parameters) => parameters.UsesBitReservoir ? 2 : 1;

    public bool IsDecoding => _session.IsDecoding;

    // The values of the frame being read, or null where nothing is decoded.
    public StdRecordWriter? Writer => _session.IsDecoding ? _writer : null;

    private readonly StdRecordWriter _writer = new();

    // A valid frame whose values the walk stored in the writer. bytes and the bits in them: where the frame lies; node: the parser's node of the frame, where findings point.
    public void Frame(ReadOnlyMemory<byte> bytes, int startBit, int bitCount, Node? node, bool failsChecksum = false)
    {
        int length = _parameters.FrameLength;
        long first = _next++ * length;
        _isLastValid = true;
        _tailFrame = _next;
        if (!_session.IsDecoding)
        {
            return;
        }

        int from = Math.Clamp(startBit >> 3, 0, bytes.Length);
        int to = Math.Clamp((startBit + bitCount + 7) >> 3, from, bytes.Length);
        SampleBuffers lent = _session.Lend(_writer.Count, 1);
        if (!lent.IsEmpty)
        {
            _writer.Values.CopyTo(lent.Narrow);
        }

        Node at = node ?? _stream;
        _session.Decode(new CodedFrame(new MappedBytes(bytes.Slice(from, to - from), at.Offset), "Frame", first, length) { IsWalked = !lent.IsEmpty, FailsChecksum = failsChecksum, Node = at });
    }

    // Frames the parser counts as lost: an invalid frame, and those that were declared after it or could not be finished. isUnfinished: the frame the last superframe
    // announces and the file does not hold whole, which follows the last frame without taking away what that frame's overlap completes.
    public void Lost(long count, bool isUnfinished = false)
    {
        int length = _parameters.FrameLength;
        for (long i = 0; i < count; i++)
        {
            long first = _next++ * length;
            _isLastValid &= isUnfinished;
            if (_session.IsDecoding)
            {
                _session.Decode(new CodedFrame(new MappedBytes(ReadOnlyMemory<byte>.Empty, _stream.Offset), "Frame", first, length) { IsUnreadable = true, Node = _stream });
            }
        }
    }

    // The end of the stream: the part of the last block's overlap that is complete follows the last frame (at most half a frame), where the last frame was valid and
    // at least two frames were handed over; blockSize and nextSize are the size indexes of the last block and of the one after it.
    public StreamVerification Finish(int blockSize, int nextSize)
    {
        int length = _parameters.FrameLength;
        long tail = _parameters.UsesBitReservoir && _isLastValid && _tailFrame >= 2 ? (length - Math.Min(length >> blockSize, length >> nextSize)) / 2 : 0;
        long start = DroppedFrames(_parameters) * length;
        _session.Trim(start, Math.Max(start, (_tailFrame * length) + tail));
        if (tail > 0 && _session.IsDecoding)
        {
            SampleBuffers lent = _session.Lend(1, 1);
            if (!lent.IsEmpty)
            {
                lent.Narrow[0] = -1;
                _session.Decode(new CodedFrame(new MappedBytes(ReadOnlyMemory<byte>.Empty, _stream.Offset), "Frame", _tailFrame * length, (int)tail) { IsWalked = true, Node = _stream });
            }
        }

        return _session.Finish([]);
    }
}
