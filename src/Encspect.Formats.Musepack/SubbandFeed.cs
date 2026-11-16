using Encspect.Core;

namespace Encspect.Formats.Musepack;

// The entropy pass and the feeding of the decoder for one stream (docs/design/lossy-decoders.md, "The parser and decoder split"): every frame is walked, whether or not
// anything is decoded, and where a session decodes the walked values are lent to it. A frame the walk cannot read, and one that is cut, is handed unreadable.
internal sealed class SubbandFeed
{
    // The most bytes of a frame the walkers hold. libmpcdec holds 4,352 (MAX_FRAME_SIZE); the largest frame its encoders write is about 5,600 bits per channel.
    public const int FrameWindowBytes = 8192;

    // The bytes of an SV8 audio packet the walker holds at once: it moves on when fewer than FrameWindowBytes are left in front of a frame.
    public const int PacketWindowBytes = 65536;

    // Zero bytes after the window, which reads past a frame's end land in before the walk notices (a resolution's 36 values of up to 19 bits).
    private const int Slack = 256;

    private readonly IDecodeSession? _session;
    private long _trimFirst = -1;
    private long _trimEnd = -1;

    public SubbandFeed(AnalysisContext context, MusepackConfiguration setup, Node stream, bool canDecode)
    {
        Setup = setup;
        if (canDecode)
        {
            _session = context.StartDecoding(MusepackParser.CodecId, new DecodeSetup
            {
                Stream = stream,
                Channels = Math.Min(setup.Channels, 2),
                BitsPerSample = 32,
                Configuration = new MappedBytes(setup.ToBytes(), 0),
                IsLossy = true,
            });
        }
    }

    public MusepackConfiguration Setup { get; }

    public SubbandState State { get; } = new();

    public MsbBits Bits { get; } = new();

    public byte[] Buffer { get; } = new byte[PacketWindowBytes + Slack];

    // The coding of each frame of the audio packet being walked (0 left/right, 1 mid/side), for the stereo coding series.
    public byte[] Codes { get; } = new byte[1 << 14];

    public bool IsDecoding => _session is { IsDecoding: true };

    // The stream's frames since the last frame the decoder was handed: set where one was lost.
    public bool PendingGap { get; private set; }

    // States the range of positions the stream's syntax trims to, when it changes.
    public void Trim(long first, long end)
    {
        end = Math.Max(first, end);
        if (_session is not null && (first != _trimFirst || end != _trimEnd))
        {
            (_trimFirst, _trimEnd) = (first, end);
            _session.Trim(first, end);
        }
    }

    // Loads the window with bytes already in MSB-first order and points the bit reader at its first bit; limit counts bits from the window's start. Zeroes the slack.
    public void Begin(int bytes, int position, int limit)
    {
        Buffer.AsSpan(bytes, Slack).Clear();
        Bits.Reset(Buffer, position, Math.Min(limit, bytes * 8));
    }

    // Hands a frame to the decoder: walked from the values the state holds, or unreadable. node names the frame in a finding the host builds for it.
    public void Feed(long firstSample, WalkFacts facts, bool isUnreadable, long fileStart, long fileEnd, string name = "Frame", int frames = 1)
    {
        int samples = frames * Sv7Parser.FrameSamples;
        if (_session is not { IsDecoding: true } session)
        {
            PendingGap |= isUnreadable;
            return;
        }

        MappedBytes bytes = MappedBytes.Opaque(ReadOnlyMemory<byte>.Empty, fileStart, fileEnd);
        if (isUnreadable)
        {
            session.Decode(new CodedFrame(bytes, name, firstSample, samples) { IsUnreadable = true });
            PendingGap = true;
            return;
        }

        SampleBuffers lent = session.Lend(LentLayout.PerChannel, 2);
        if (lent.IsEmpty)
        {
            session.Decode(new CodedFrame(bytes, name, firstSample, samples) { IsUnreadable = true });
            PendingGap = true;
            return;
        }

        LentLayout.Store(State, 0, lent.Channel(0));
        LentLayout.Store(State, 1, lent.Channel(1));
        session.Decode(new CodedFrame(bytes, name, firstSample, samples) { IsWalked = true, FollowsGap = PendingGap });
        PendingGap = false;
    }

    public StreamVerification? Finish() => _session?.Finish([]);
}
