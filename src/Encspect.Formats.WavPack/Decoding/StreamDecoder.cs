using Encspect.Core;
using Syntax = Encspect.Formats.WavPack.Decode;

namespace Encspect.Formats.WavPack.Decoding;

// The decoding of one WavPack stream: the blocks the parser hands over, one by one in file order. The blocks from one with
// the initial flag to one with the final flag carry the channels of the same samples, so a block is decoded where it comes,
// its samples are kept in planes until the group's final block, and that block gives the group's samples to the sink and the
// MD5. The group is the unit the contract counts: Decode returns 0 for the blocks before the final one and the group's
// samples on it. libwavpack 5.6.0 (unpack_utils.c WavpackUnpackSamples, unpack.c, open_utils.c) is the reference; where it
// mutes a block and goes on, this decoder also does, and where it would read a stream it cannot make sense of it refuses the block.
internal sealed class StreamDecoder : IFrameDecoder
{
    // The samples of all the channels of one group a decoder holds: 2^24, 64 MiB of 32-bit values. A group past it is
    // decoded block by block, its CRCs compared, and its audio neither output nor hashed.
    internal const long MaxGroupSamples = 1 << 24;

    private const int DefaultMaxStreams = Syntax.OldMaxStreams;

    private readonly DecodeContext _context;
    private readonly int _channels;
    private readonly BlockContents _contents = new();
    private readonly PcmBlockDecoder _pcm = new();
    private readonly DsdBlockDecoder _dsd = new();

    // What the stream says about itself, settled by the first blocks as libwavpack settles it when it opens a file.
    private bool _hasAudio;
    private bool _isDsd;
    private bool _isFloat;
    private int _bytesPerSample;
    private int _sampleRate;
    private ChannelInfoValue? _channelInfo;
    private int _qmode;
    private int _layoutChannels;
    private byte[]? _reordering;
    private bool _isQmodeFrozen;
    private bool _sawHybrid;

    // The MD5 the file stores (the last block's), and the node of the sub-block that holds it.
    private byte[]? _storedMd5;
    private Node? _md5Node;

    private AudioDigest? _digest;
    private IPcmSink? _sink;

    // The group being gathered.
    private bool _inGroup;
    private long _groupPosition;
    private long _groupBlockIndex;
    private int _groupSamples;
    private int _groupChannels;
    private int _groupBlocks;
    private bool _isGroupOversized;
    private bool _isGroupFailed;
    private int[] _planes = [];

    private long _crcChecked;
    private long _crcFailed;
    private long _extraChecked;
    private long _extraFailed;
    private bool _isDigestIncomplete;
    private bool _wasOversizeReported;

    public StreamDecoder(DecodeContext context)
    {
        _context = context;
        _channels = context.Setup.Channels;
    }

    public int Decode(in CodedFrame frame)
    {
        ReadOnlySpan<byte> data = frame.Bytes.Span;
        if (data.Length < BlockHeader.Length)
        {
            throw new EndOfStreamException();
        }

        if (!BlockHeader.IsPlausible(data))
        {
            throw new InvalidDataException("The block does not start with a header libwavpack takes");
        }

        var header = new BlockHeader(data);
        if (frame.FollowsGap)
        {
            Abandon();
        }

        if (header.Version is < BlockHeader.MinVersion or > BlockHeader.MaxVersion)
        {
            throw new InvalidDataException($"The block has stream version 0x{header.Version:X3}, which libwavpack does not decode");
        }

        _contents.Read(data, header, _channelInfo is not null);
        Settle(frame, header);
        if (!header.HasAudio)
        {
            return 0;
        }

        return Block(frame, data, header);
    }

    // The facts the blocks give about the stream: the channel info and sample rate of the first ones, the qualify mode up to
    // the first block with audio, and the last MD5.
    private void Settle(in CodedFrame frame, in BlockHeader header)
    {
        if (_channelInfo is null && _contents.ChannelInfo is { } info)
        {
            _channelInfo = info;
        }

        if (_sampleRate == 0 && _contents.SampleRate is > 0 and { } rate)
        {
            _sampleRate = rate;
        }

        if (!_isQmodeFrozen && _contents.HasNewConfig)
        {
            _qmode = _contents.QualifyMode;
            _layoutChannels = _contents.LayoutChannels;
            _reordering = _contents.ReorderingLength > 0 ? _contents.Reordering.AsSpan(0, _contents.ReorderingLength).ToArray() : null;
        }

        if (_contents.Md5 is { } md5)
        {
            ReadOnlySpan<byte> data = frame.Bytes.Span;
            _storedMd5 = data.Slice(md5.Start, 16).ToArray();
            long at = frame.Bytes.OffsetOf(_contents.Md5Position);
            _md5Node = new Node(SubBlockIds.Name(md5.IsAlternate ? SubBlockIds.AltMd5Checksum : SubBlockIds.Md5Checksum), at, _contents.Md5HeaderLength + _contents.Md5Size);
        }

        if (header.HasAudio)
        {
            _sawHybrid |= header.IsHybrid;
        }
    }

    private int Block(in CodedFrame frame, ReadOnlySpan<byte> data, in BlockHeader header)
    {
        if (!_hasAudio)
        {
            _hasAudio = true;
            _isDsd = header.IsDsd;
            _isFloat = header.IsFloat;
            _bytesPerSample = _isDsd ? 1 : header.BytesPerSample;
            if (_sampleRate == 0)
            {
                _sampleRate = header.SampleRate != 0 ? header.SampleRate : 44100;
            }

            _isQmodeFrozen = true;
        }

        if (header.IsInitial)
        {
            Abandon();
            _inGroup = true;
            _groupPosition = Math.Max(0, frame.FirstSample);
            _groupBlockIndex = header.BlockIndex;
            _groupSamples = (int)header.Samples;
            _groupChannels = 0;
            _groupBlocks = 0;
            _isGroupOversized = false;
            _isGroupFailed = false;
        }
        else if (!_inGroup)
        {
            // libwavpack reads on to the next block that starts a group.
            return 0;
        }

        _groupBlocks++;
        bool isMono = (header.Flags & BlockHeader.MonoFlag) != 0;
        int blockChannels = isMono ? 1 : 2;
        if (header.IsDsd != _isDsd)
        {
            _isGroupFailed = true;
            throw new InvalidDataException("The block is DSD where the first block of the stream is not, or is not where it is");
        }

        if (_groupChannels + blockChannels > _channels)
        {
            _isGroupFailed = true;
            throw new InvalidDataException($"The blocks of the group hold more than the {Counts.Of(_channels, "channel", "channels")} of the stream");
        }

        int samples = _groupSamples;
        bool isMuted = header.BlockIndex > _groupBlockIndex || header.Samples < samples;
        long needed = (long)(_groupChannels + blockChannels) * samples;
        if (needed > MaxGroupSamples)
        {
            _isGroupOversized = true;
        }

        uint blockCrc = header.Crc;
        bool isCrcValid;
        bool hasExtra = false;
        bool isExtraValid = true;
        ReadOnlySpan<int> decoded;
        int width = (header.Flags & (BlockHeader.MonoFlag | BlockHeader.FalseStereoFlag)) != 0 ? 1 : 2;
        try
        {
            if (isMuted)
            {
                isCrcValid = uint.MaxValue == blockCrc;
                decoded = MutedSamples(samples * width);
            }
            else if (_isDsd)
            {
                (bool isDsdMuted, uint crc) = _dsd.Decode(data, header, _contents, samples, _context.CancellationToken);
                isCrcValid = crc == blockCrc;
                decoded = _dsd.Bytes;
                isMuted = isDsdMuted;
            }
            else
            {
                BlockOutcome outcome = _pcm.Decode(data, header, _contents, samples, _context.CancellationToken);
                isCrcValid = outcome.IsCrcValid;
                hasExtra = outcome.HasExtra;
                isExtraValid = outcome.IsExtraCrcValid;
                decoded = _pcm.Samples;
                isMuted = outcome.IsMuted;
            }
        }
        catch (InvalidDataException)
        {
            _isGroupFailed = true;
            throw;
        }

        _crcChecked++;
        if (!isCrcValid)
        {
            _crcFailed++;
            _context.AddFinding(Severity.Warning, "wavpack.audio-crc-mismatch",
                $"The decoded audio of the block at 0x{frame.Bytes.FileStart:X} does not have the CRC its header stores{(isMuted ? " (the block's data ends early or holds samples libwavpack takes for corrupt, so it is muted)" : string.Empty)}.",
                Nodes.BlockCrc(frame.Bytes, blockCrc));
        }

        if (hasExtra)
        {
            _extraChecked++;
            if (!isExtraValid)
            {
                _extraFailed++;
                _context.AddFinding(Severity.Warning, "wavpack.audio-crc-mismatch",
                    $"The extra bits of the block at 0x{frame.Bytes.FileStart:X} do not have the CRC their bitstream stores.",
                    Nodes.BlockCrc(frame.Bytes, blockCrc));
            }
        }

        if (!_isGroupOversized)
        {
            Keep(decoded, width, blockChannels, samples, header);
        }

        _groupChannels += blockChannels;
        int maxStreams = _channelInfo?.Streams ?? DefaultMaxStreams;
        if (!header.IsFinal && _groupBlocks < maxStreams && _groupChannels < _channels)
        {
            return 0;
        }

        return Complete(frame);
    }

    private int[] _muted = [];

    private ReadOnlySpan<int> MutedSamples(int count)
    {
        if (_muted.Length < count)
        {
            _muted = new int[count];
        }

        _muted.AsSpan(0, count).Fill(_isDsd ? 0x55 : 0);
        return _muted.AsSpan(0, count);
    }

    // Puts the block's channels into the group's planes.
    private void Keep(ReadOnlySpan<int> decoded, int width, int blockChannels, int samples, in BlockHeader header)
    {
        int needed = (_groupChannels + blockChannels) * samples;
        if (_planes.Length < needed)
        {
            int[] grown = new int[needed];
            _planes.AsSpan(0, _groupChannels * samples).CopyTo(grown);
            _planes = grown;
        }

        Span<int> first = _planes.AsSpan(_groupChannels * samples, samples);
        if (width == 1)
        {
            decoded[..samples].CopyTo(first);
            if (blockChannels == 2)
            {
                decoded[..samples].CopyTo(_planes.AsSpan((_groupChannels + 1) * samples, samples));
            }

            return;
        }

        Span<int> second = _planes.AsSpan((_groupChannels + 1) * samples, samples);
        for (int i = 0; i < samples; i++)
        {
            first[i] = decoded[2 * i];
            second[i] = decoded[(2 * i) + 1];
        }
    }

    // The group's final block: the samples go to the sink and the MD5.
    private int Complete(in CodedFrame frame)
    {
        _inGroup = false;
        int samples = _groupSamples;
        if (_isGroupFailed)
        {
            _isDigestIncomplete = true;
            return 0;
        }

        if (_groupChannels != _channels)
        {
            _isDigestIncomplete = true;
            throw new InvalidDataException($"The blocks of the group hold {Counts.Of(_groupChannels, "channel", "channels")}, but the stream has {_channels}");
        }

        if (_isGroupOversized)
        {
            _isDigestIncomplete = true;
            if (!_wasOversizeReported)
            {
                _wasOversizeReported = true;
                _context.AddFinding(Severity.Info, "wavpack.md5-group-too-large",
                    $"The group of blocks at sample {_groupBlockIndex} holds more samples over all its channels than the {MaxGroupSamples} the decoder keeps at once, so its blocks were decoded one at a time and their CRCs compared, and the MD5 of the audio is not compared.",
                    _md5Node);
            }

            return samples;
        }

        ReadOnlySpan<int> planes = _planes.AsSpan(0, _channels * samples);
        if (!_isDsd)
        {
            if (_sink is null)
            {
                _sink = _context.DeclareOutput(new PcmFormat(_sampleRate, _channels, _isFloat ? 32 : _bytesPerSample * 8, _isFloat ? SampleFormat.Float32 : SampleFormat.Integer)
                {
                    ChannelLayout = Syntax.Layout(_channelInfo?.Mask ?? (uint)(0x5 - Math.Min(_channels, 2)), _channels),
                });
            }

            _sink.Write(_isFloat
                ? new PcmBlock(_groupPosition, samples, _channels, samples, System.Runtime.InteropServices.MemoryMarshal.Cast<int, float>(planes))
                : new PcmBlock(_groupPosition, samples, _channels, samples, planes));
        }

        // The MD5 sits in the last block, so every group is hashed as it comes; a lossy stream has no MD5 to compare.
        if (!_context.Setup.IsLossy && !_sawHybrid)
        {
            _digest ??= new AudioDigest(_channels, _bytesPerSample, _qmode, _isDsd, UsesReordering() ? _reordering : null);
            _digest.Append(planes, samples);
        }

        return samples;
    }

    private bool UsesReordering() => (_qmode & AudioDigest.QmodeReorderedChannels) != 0 && _reordering is not null && _layoutChannels <= _channels;

    // A group that does not end: its samples are missing from the stream.
    private void Abandon()
    {
        if (_inGroup)
        {
            _inGroup = false;
            _isDigestIncomplete = true;
        }
    }

    public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end)
    {
        Abandon();
        var results = new List<ChecksumResult> { new(ChecksumNames.BlockCrc, _crcChecked, _crcFailed) };
        if (_extraChecked > 0)
        {
            results.Add(new ChecksumResult(ChecksumNames.ExtraCrc, _extraChecked, _extraFailed));
        }

        if (_storedMd5 is not { } stored)
        {
            return results;
        }

        if (_context.Setup.IsLossy || _sawHybrid)
        {
            if (end.IsComplete)
            {
                _context.AddFinding(Severity.Info, "wavpack.md5-needs-correction-file",
                    "The file stores the MD5 of the lossless audio, which a hybrid file without its correction file (.wvc) does not decode to, so it is not compared.", _md5Node);
            }

            return results;
        }

        if (!end.IsComplete || _isDigestIncomplete || _digest is null)
        {
            return results;
        }

        Span<byte> computed = stackalloc byte[Md5.HashLength];
        _digest.Finish(computed);
        bool matches = computed.SequenceEqual(stored);
        if (!matches)
        {
            _context.AddFinding(Severity.Warning, "wavpack.md5-mismatch",
                $"The MD5 of the decoded audio is {Convert.ToHexStringLower(computed)}, but the file stores {Convert.ToHexStringLower(stored)}: the audio differs from what the encoder read.", _md5Node);
        }

        results.Add(new ChecksumResult(ChecksumNames.Md5, 1, matches ? 0 : 1));
        return results;
    }
}
