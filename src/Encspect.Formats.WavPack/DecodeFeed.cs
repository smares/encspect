using Encspect.Core;

namespace Encspect.Formats.WavPack;

// Hands the blocks to the decoding session as they are read, and declares the checksums the stream stores. The session starts
// at the first block with audio that passes libwavpack's checks, the block it settles the stream's channels with (it skips the
// ones before it); blocks without audio that come before are kept, a few of them, and handed over first, since they may
// carry the channel info or the new config. Nothing is kept for the decoder otherwise: it works on a block while the parser holds it.
internal sealed class DecodeFeed
{
    // libwavpack gives up on a file whose first 16 blocks hold no audio.
    private const int MaxHeld = 16;
    private const int MaxHeldBytes = 1 << 16;

    private readonly List<(long Offset, byte[] Bytes, BlockHeader Header, bool IsRefused)> _held = [];
    private readonly BlockContents _probe = new();
    private bool _channelsAreKnown;
    private int _heldBytes;
    private long _firstPosition = -1;

    public IDecodeSession? Session { get; private set; }

    public void Offer(State s, long offset, in BlockHeader header, ReadOnlyMemory<byte> bytes, BlockStatus status)
    {
        // The decoder's own reading of the block says whether libwavpack refuses it, so the parser and the decoder cannot differ.
        bool isRefused = (status & (BlockStatus.Truncated | BlockStatus.Malformed)) == 0 && Refuses(header, bytes.Span, Session is not null || !header.HasAudio || s.IsConfigured);
        if (isRefused)
        {
            status |= BlockStatus.Refused;
        }

        bool isUnreadable = (status & (BlockStatus.Truncated | BlockStatus.Malformed | BlockStatus.Refused)) != 0;
        if (header.HasAudio && isUnreadable)
        {
            s.UnreadableBlocks++;
        }

        bool followsGap = header.HasAudio && s.FollowsGap;
        if (header.HasAudio)
        {
            s.FollowsGap = false;
        }

        if (Session is null)
        {
            if (!header.HasAudio)
            {
                Hold(offset, bytes, header, isRefused);
                return;
            }

            // The block libwavpack opens the file with, even where it refuses it: the decoder then refuses the stream, as libwavpack
            // refuses to open it.
            if (!s.IsConfigured)
            {
                return;
            }

            Session = Start(s, header, offset, bytes);
            foreach ((long heldOffset, byte[] heldBytes, BlockHeader heldHeader, bool isHeldRefused) in _held)
            {
                Hand(s, heldOffset, heldHeader, heldBytes, isHeldRefused ? BlockStatus.Refused : BlockStatus.None, false);
            }

            _held.Clear();
            _heldBytes = 0;
        }

        Hand(s, offset, header, bytes, status, followsGap);
    }

    private void Hold(long offset, ReadOnlyMemory<byte> bytes, in BlockHeader header, bool isRefused)
    {
        if (_held.Count < MaxHeld && _heldBytes + bytes.Length <= MaxHeldBytes)
        {
            _held.Add((offset, bytes.ToArray(), header, isRefused));
            _heldBytes += bytes.Length;
        }
    }

    // Whether libwavpack refuses the block (its version, and the sub-blocks the decoder reads). A block the decoder gets settles the
    // stream's channel info for the ones after it, as the decoder does, so the same blocks are read the same way.
    private bool Refuses(in BlockHeader header, ReadOnlySpan<byte> bytes, bool reachesDecoder)
    {
        if (header.Version is < BlockHeader.MinVersion or > BlockHeader.MaxVersion)
        {
            return true;
        }

        try
        {
            _probe.Read(bytes, header, _channelsAreKnown);
        }
        catch (InvalidDataException)
        {
            return true;
        }

        _channelsAreKnown |= reachesDecoder && _probe.ChannelInfo is not null;
        return false;
    }

    // The stream's figures as the first block that settles them gives them, and that block's bytes for the decoder to open with.
    private static IDecodeSession Start(State s, in BlockHeader first, long offset, ReadOnlyMemory<byte> bytes)
    {
        bool isDsd = first.IsDsd;
        bool isFloat = first.IsFloat;
        return s.Context.StartDecoding(WavPackCodecParser.CodecId, new DecodeSetup
        {
            Stream = s.Stream,
            Channels = s.ChannelInfo?.Value.Channels ?? (first.IsMono ? 1 : 2),
            BitsPerSample = isDsd ? 8 : isFloat ? 32 : first.BytesPerSample * 8,
            IsDsd = isDsd,
            Samples = s.DeclaredTotal,
            Configuration = new MappedBytes(bytes.ToArray(), offset),
            IsLossy = first.IsHybrid || s.HasLossyBlocks,
        });
    }

    private void Hand(State s, long offset, in BlockHeader header, ReadOnlyMemory<byte> bytes, BlockStatus status, bool followsGap)
    {
        if (Session is not { IsDecoding: true } session)
        {
            return;
        }

        _firstPosition = _firstPosition < 0 ? Math.Max(0, s.FirstIndex) : _firstPosition;
        var frame = new CodedFrame(new MappedBytes(bytes, offset), header.HasAudio ? "Block" : "Metadata Block", header.BlockIndex - _firstPosition, (int)header.Samples)
        {
            IsDamaged = (status & (BlockStatus.ChecksumInvalid | BlockStatus.ChecksumMissing | BlockStatus.Oversized)) != 0,
            IsUnreadable = (status & (BlockStatus.Truncated | BlockStatus.Malformed | BlockStatus.Refused)) != 0,
            FollowsGap = followsGap,
        };
        session.Decode(frame);
    }

    // Ends the session with the checksums the stream stores and returns what it says about the stream.
    public StreamVerification Finish(State s, bool isCorrection)
    {
        long audioBlocks = s.AudioBlocks;
        bool matches = s.DeclaredTotal is not { } declared || declared == s.CountedSamples;
        bool coversAll = !isCorrection && audioBlocks > 0 && !s.HasGap && matches && s.UnreadableBlocks == 0 && s.BrokenSets == 0;
        // A block that says it ends with a checksum and holds none fails the check as libwavpack makes it.
        long checksums = s.ChecksumValid + s.ChecksumInvalid + s.ChecksumMissing;
        IDecodeSession session = Session ?? s.Context.StartDecoding(WavPackCodecParser.CodecId, new DecodeSetup { Stream = s.Stream, Channels = 1, BitsPerSample = 16 });
        return session.Finish(
        [
            new ChecksumTally { Name = ChecksumNames.BlockChecksum, Covers = ChecksumCoverage.CodedData, CoversAllAudio = coversAll && s.AudioChecksummed == audioBlocks, Stored = checksums, Checked = checksums, Failed = s.ChecksumInvalid + s.ChecksumMissing },
            new ChecksumTally { Name = ChecksumNames.BlockCrc, Covers = ChecksumCoverage.DecodedAudio, CoversAllAudio = coversAll, Stored = audioBlocks },
            new ChecksumTally { Name = ChecksumNames.ExtraCrc, Covers = ChecksumCoverage.DecodedAudio, Stored = s.WvxBlocks },
            new ChecksumTally { Name = ChecksumNames.Md5, Covers = ChecksumCoverage.DecodedAudio, CoversAllAudio = true, Stored = s.Md5Count > 0 ? 1 : 0 },
        ]);
    }
}
