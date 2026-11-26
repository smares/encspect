using Encspect.Core;

namespace Encspect.Formats.Aac;

// What the decoder takes from the parser about a stream (DecodeSetup.Shared): the configuration the raw data blocks are read with, and the speakers of the channels where
// the stream states them.
internal sealed record DecodeConfig(int ObjectType, int SampleRate, int SampleRateIndex, int FrameLength, bool IsSbrSignaled, bool IsPsSignaled, IReadOnlyList<Speaker>? Layout,
    string? Refusal = null, bool IsPsDenied = false, bool IsDownsampled = false, bool IsSbrDenied = false)
{
    // Samples a block decodes to, per channel: SBR doubles the core's.
    public int OutputLength => IsSbrSignaled ? 2 * FrameLength : FrameLength;
}

// The parser's side of the decoding of one stream of raw data blocks (docs/design/lossy-decoders.md, "The parser and decoder split"), for ADTS frames, ADIF blocks and the access
// units of a container alike: the walk of BlockReader stores each block's values in the store, this copies them into the buffers the session lends and hands the block over
// with its position, the gaps before it and the checks its frame failed. The session starts with the first block that is read to its end, since its channels are the
// block's channel streams.
internal sealed class DecodeFeed
{
    internal const string CodecId = "aac";

    private readonly AnalysisContext _context;
    private readonly Node _stream;
    private DecodeConfig _config;
    private readonly RecordStore _store = new();
    private readonly Queue<Held> _held = new();
    private IDecodeSession? _session;
    private long _next;
    private long _pendingLost;
    private bool _followsGap;
    private int _hold;
    private bool _hasTrim;
    private readonly bool _canDecode;
    private readonly int _channels;
    private bool _awaiting;
    private readonly List<Pending> _undecided = [];

    // canDecode: the stream's configuration is one the decoder takes. Where it is not, the session starts at once and the decoder refuses it (decode.not-possible), so every
    // AAC stream has a session whose verification says what was asked for; channels is then the configuration's.
    public DecodeFeed(AnalysisContext context, Node stream, DecodeConfig config, bool canDecode, int channels)
    {
        _context = context;
        _stream = stream;
        _config = config;
        _canDecode = canDecode;
        _channels = channels;
        _awaiting = canDecode && !config.IsDownsampled && ((!config.IsSbrSignaled && !config.IsSbrDenied) || (!config.IsPsSignaled && !config.IsPsDenied && channels == 1));
        if (!canDecode)
        {
            _session = Start(channels, config);
        }
    }

    // The store the block reader fills, or null where nothing is decoded: the session ended.
    public RecordStore? Store => !_canDecode || _session is { IsDecoding: false } ? null : _store;

    // The last blocks of the track that wait for the end trimming the container states (Matroska's DiscardPadding), which Trim must state before the blocks it reaches.
    public int Hold
    {
        set => _hold = value;
    }

    // The next block follows bytes that held no block (ADTS sync was lost).
    public void Gap()
    {
        if (_awaiting)
        {
            _undecided.Add(new Pending(PendingKind.Gap, null, string.Empty, null, false, null));
            return;
        }

        _followsGap = true;
    }

    // A block that was read to its end and walked into the store. frame: its bytes; nodeName and node: where findings about it point. isEvidence: the block counts for the decision
    // about implicit SBR and PS, as it does for the parser's tally (BlockLog.AddEvidence: not a block of a damaged frame or of one that fails its CRC); the blocks before the
    // first one that does wait, with their values, until it is known.
    public void Decode(MappedBytes frame, string nodeName, Node? node, bool failsChecksum, bool isEvidence = true)
    {
        if (!_canDecode)
        {
            return;
        }

        if (_awaiting)
        {
            if (!isEvidence)
            {
                _undecided.Add(new Pending(PendingKind.Block, frame, nodeName, node, failsChecksum, _store.IsOverflow ? null : Snapshot()));
                return;
            }

            Decide(true);
        }

        DecodeBlock(frame, nodeName, node, failsChecksum, null, false);
    }

    private void DecodeBlock(MappedBytes frame, string nodeName, Node? node, bool failsChecksum, int[]? replay, bool replaying)
    {
        bool overflow = replaying ? replay is null : _store.IsOverflow;
        long first = _next;
        _next += _config.OutputLength;

        if (_session is null && overflow)
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
            Enqueue(unit with { Record = replaying ? replay : overflow ? null : Snapshot() });
            return;
        }

        if (replaying)
        {
            Give(unit, replay);
        }
        else
        {
            Give(unit, overflow ? default : _store.Seal());
        }
    }

    // The decision is made, and the blocks that waited for it are decoded.
    private void Decide(bool findImplicit)
    {
        _awaiting = false;
        if (findImplicit && !_store.IsOverflow)
        {
            FindImplicitSbr();
        }

        foreach (Pending pending in _undecided)
        {
            switch (pending.Kind)
            {
                case PendingKind.Gap:
                    Gap();
                    break;
                case PendingKind.Lost:
                    Lost(pending.Frame!, pending.NodeName, pending.Node);
                    break;
                default:
                    DecodeBlock(pending.Frame!, pending.NodeName, pending.Node, pending.FailsChecksum, pending.Record, true);
                    break;
            }
        }

        _undecided.Clear();
    }

    // The first block that can be read decides what the configuration left open (ffmpeg's rule: implicit signaling counts where the stream starts with it): SBR data in it makes the
    // stream HE-AAC with twice the core's rate, and the PS extension in a single channel's SBR data makes it HE-AAC v2 with two channels.
    private void FindImplicitSbr()
    {
        bool hasSbr = _config.IsSbrSignaled || (!_config.IsSbrDenied && (_store.Flags & BlockRecord.BlockHasSbrEvidence) != 0);
        if (hasSbr && !_config.IsSbrSignaled)
        {
            _config = _config with { IsSbrSignaled = true };
            _next *= 2;
        }

        if (hasSbr && !_config.IsPsSignaled && !_config.IsPsDenied && _store.Slots == 1 && (_store.Flags & BlockRecord.BlockHasPs) != 0)
        {
            _config = _config with { IsPsSignaled = true };
        }
    }

    // A block that is lost: cut by the data, or with syntax that breaks.
    public void Lost(MappedBytes frame, string nodeName, Node? node)
    {
        if (!_canDecode)
        {
            return;
        }

        if (_awaiting)
        {
            _undecided.Add(new Pending(PendingKind.Lost, frame, nodeName, node, false, null));
            return;
        }

        long first = _next;
        _next += _config.OutputLength;
        Lose(frame, nodeName, node, first);
    }

    // The container's end trimming: the blocks waiting are the ones it reaches. The positions are those of the coded stage.
    public void Trim(long endPosition)
    {
        if (_session is not null && _canDecode && !_hasTrim)
        {
            _hasTrim = true;
            _session.Trim(0, Math.Max(0, endPosition));
        }
    }

    // The end of the stream: the blocks held back are decoded, and the session ends with what the parser compared.
    public StreamVerification? Finish(IReadOnlyList<ChecksumTally> stored)
    {
        if (_awaiting)
        {
            // No block counted as evidence: the stream stays as its configuration says.
            Decide(false);
        }

        if (_session is null)
        {
            // No block could be read: the stream has its session all the same, whose blocks were all lost.
            _session = Start(_channels, _config);
            for (long i = 0; i < _pendingLost && _session.IsDecoding; i++)
            {
                _session.Decode(new CodedFrame(new MappedBytes(ReadOnlyMemory<byte>.Empty, 0), "Access Unit", i * _config.OutputLength, _config.OutputLength) { IsUnreadable = true, FollowsGap = i > 0, Node = _stream });
            }

            _pendingLost = 0;
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

        // A first block with no channel element says nothing about the channels: the configuration's do.
        int slots = _store.Slots > 0 ? _store.Slots : _channels;
        bool hasPs = _config.IsSbrSignaled && _config.IsPsSignaled && slots == 1;
        _config = _config with { Layout = hasPs ? ChannelLayouts.Stereo : _config.Layout is { } layout && layout.Count == _store.Slots ? layout : null, IsPsSignaled = hasPs };
        _session = Start(hasPs ? 2 : slots, _config);
        // The blocks before the first one that could be read were lost.
        long lost = _pendingLost;
        _pendingLost = 0;
        for (long i = 0; i < lost && _session.IsDecoding; i++)
        {
            _session.Decode(new CodedFrame(new MappedBytes(ReadOnlyMemory<byte>.Empty, frame.FileStart), nodeName, first - ((lost - i) * _config.OutputLength), _config.OutputLength)
            { IsUnreadable = true, FollowsGap = i > 0, Node = node });
        }
    }

    private IDecodeSession Start(int channels, DecodeConfig config) => _context.StartDecoding(CodecId, new DecodeSetup
    {
        Stream = _stream,
        Channels = Math.Clamp(channels, 1, 64),
        BitsPerSample = 32,
        IsLossy = true,
        Shared = config,
    });

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
            _session.Decode(new CodedFrame(unit.Frame, unit.NodeName, unit.First, _config.OutputLength) { IsUnreadable = true, FollowsGap = unit.FollowsGap, Node = unit.Node });
            return;
        }

        SampleBuffers lent = record.IsEmpty ? default : _session.Lend(BlockRecord.Stride, record.Length / BlockRecord.Stride);
        if (!lent.IsEmpty)
        {
            record.CopyTo(lent.Narrow);
        }

        _session.Decode(new CodedFrame(unit.Frame, unit.NodeName, unit.First, _config.OutputLength)
        {
            IsWalked = !lent.IsEmpty,
            FollowsGap = unit.FollowsGap,
            FailsChecksum = unit.FailsChecksum || unit.Frame.FailsChecksum,
            Node = unit.Node,
        });
    }

    private enum PendingKind { Block, Lost, Gap }

    private sealed record Pending(PendingKind Kind, MappedBytes? Frame, string NodeName, Node? Node, bool FailsChecksum, int[]? Record);

    private sealed record Held(MappedBytes Frame, string NodeName, Node? Node, long First, bool FollowsGap, bool FailsChecksum, bool IsLost, int[]? Record);
}
