using Encspect.Core;

namespace Encspect.Formats.Aac;

// What the decoder takes from the parser about a stream (DecodeSetup.Shared): the configuration the raw data blocks are read with, and the speakers of the channels where
// the stream states them.
internal sealed record DecodeConfig(int ObjectType, int SampleRate, int SampleRateIndex, int FrameLength, bool IsSbrSignaled, bool IsPsSignaled, IReadOnlyList<Speaker>? Layout);

// The parser's side of the decoding of one stream of raw data blocks (docs/design/lossy-decoders.md, "The parser and decoder split"), for ADTS frames, ADIF blocks and the access
// units of a container alike: the walk of BlockReader stores each block's values in the store, this copies them into the buffers the session lends and hands the block over
// with its position, the gaps before it and the checks its frame failed. The session starts with the first block that is read to its end, since its channels are the
// block's channel streams.
internal sealed class DecodeFeed
{
    internal const string CodecId = "aac";

    private readonly AnalysisContext _context;
    private readonly Node _stream;
    private readonly DecodeConfig _config;
    private readonly RecordStore _store = new();
    private readonly Queue<Held> _held = new();
    private IDecodeSession? _session;
    private long _next;
    private long _pendingLost;
    private bool _followsGap;
    private int _hold;
    private bool _hasTrim;
    private bool _isDeclined;

    public DecodeFeed(AnalysisContext context, Node stream, DecodeConfig config)
    {
        _context = context;
        _stream = stream;
        _config = config;
    }

    // The store the block reader fills, or null where nothing is decoded: the session ended.
    public RecordStore? Store => _isDeclined || _session is { IsDecoding: false } ? null : _store;

    // The last blocks of the track that wait for the end trimming the container states (Matroska's DiscardPadding), which Trim must state before the blocks it reaches.
    public int Hold
    {
        set => _hold = value;
    }

    // The next block follows bytes that held no block (ADTS sync was lost).
    public void Gap() => _followsGap = true;

    // A block that was read to its end and walked into the store. frame: its bytes; nodeName and node: where findings about it point.
    public void Decode(MappedBytes frame, string nodeName, Node? node, bool failsChecksum)
    {
        if (_isDeclined)
        {
            return;
        }

        long first = _next;
        _next += _config.FrameLength;
        if (_session is null && (_store.Flags & (BlockRecord.BlockHasSbr | BlockRecord.BlockHasPs)) != 0)
        {
            // The first block that can be read holds SBR data: the stream is HE-AAC signaled implicitly, which is decoded in a later step, so no session starts.
            _isDeclined = true;
            _pendingLost = 0;
            return;
        }

        if (_session is null && _store.IsOverflow)
        {
            Lose(frame, nodeName, node, first);
            return;
        }

        StartSession(frame, nodeName, node, first);
        if (!_session!.IsDecoding)
        {
            return;
        }

        var unit = new Held(frame, nodeName, node, first, _followsGap, failsChecksum, false, null);
        _followsGap = false;
        if (_hold > 0)
        {
            Enqueue(unit with { Record = _store.IsOverflow ? null : Snapshot() });
            return;
        }

        Give(unit, _store.IsOverflow ? default : _store.Seal());
    }

    // A block that is lost: cut by the data, or with syntax that breaks.
    public void Lost(MappedBytes frame, string nodeName, Node? node)
    {
        if (_isDeclined)
        {
            return;
        }

        long first = _next;
        _next += _config.FrameLength;
        Lose(frame, nodeName, node, first);
    }

    // The container's end trimming: the blocks waiting are the ones it reaches. The positions are those of the coded stage.
    public void Trim(long endPosition)
    {
        if (_session is not null && !_hasTrim)
        {
            _hasTrim = true;
            _session.Trim(0, Math.Max(0, endPosition));
        }
    }

    // The end of the stream: the blocks held back are decoded, and the session ends with what the parser compared.
    public StreamVerification? Finish(IReadOnlyList<ChecksumTally> stored)
    {
        if (_session is null)
        {
            return null;
        }

        while (_held.Count > 0)
        {
            Give(_held.Dequeue());
        }

        return _session.Finish(stored);
    }

    private void Lose(MappedBytes frame, string nodeName, Node? node, long first)
    {
        if (_session is null)
        {
            _pendingLost++;
            _followsGap = true;
            return;
        }

        var unit = new Held(frame, nodeName, node, first, _followsGap, false, true, null);
        _followsGap = true;
        if (_hold > 0)
        {
            Enqueue(unit);
            return;
        }

        Give(unit);
    }

    private void StartSession(MappedBytes frame, string nodeName, Node? node, long first)
    {
        if (_session is not null)
        {
            return;
        }

        _session = _context.StartDecoding(CodecId, new DecodeSetup
        {
            Stream = _stream,
            Channels = Math.Max(1, _store.Slots),
            BitsPerSample = 32,
            IsLossy = true,
            Shared = _config with { Layout = _config.Layout is { } layout && layout.Count == _store.Slots ? layout : null },
        });
        // The blocks before the first one that could be read were lost.
        long lost = _pendingLost;
        _pendingLost = 0;
        for (long i = 0; i < lost && _session.IsDecoding; i++)
        {
            _session.Decode(new CodedFrame(new MappedBytes(ReadOnlyMemory<byte>.Empty, frame.FileStart), nodeName, first - ((lost - i) * _config.FrameLength), _config.FrameLength)
                { IsUnreadable = true, FollowsGap = i > 0, Node = node });
        }
    }

    private void Enqueue(Held unit)
    {
        _held.Enqueue(unit);
        if (_held.Count > _hold)
        {
            Give(_held.Dequeue());
        }
    }

    private int[] Snapshot() => _store.Seal().ToArray();

    private void Give(Held unit) => Give(unit, unit.Record);

    private void Give(Held unit, ReadOnlySpan<int> record)
    {
        if (!_session!.IsDecoding)
        {
            return;
        }

        if (unit.IsLost)
        {
            _session.Decode(new CodedFrame(unit.Frame, unit.NodeName, unit.First, _config.FrameLength) { IsUnreadable = true, FollowsGap = unit.FollowsGap, Node = unit.Node });
            return;
        }

        SampleBuffers lent = record.IsEmpty ? default : _session.Lend(BlockRecord.Stride, record.Length / BlockRecord.Stride);
        if (!lent.IsEmpty)
        {
            record.CopyTo(lent.Narrow);
        }

        _session.Decode(new CodedFrame(unit.Frame, unit.NodeName, unit.First, _config.FrameLength)
        {
            IsWalked = !lent.IsEmpty,
            FollowsGap = unit.FollowsGap,
            FailsChecksum = unit.FailsChecksum || unit.Frame.FailsChecksum,
            Node = unit.Node,
        });
    }

    private sealed record Held(MappedBytes Frame, string NodeName, Node? Node, long First, bool FollowsGap, bool FailsChecksum, bool IsLost, int[]? Record);
}
