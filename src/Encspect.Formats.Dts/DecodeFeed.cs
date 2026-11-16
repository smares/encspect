using Encspect.Core;

namespace Encspect.Formats.Dts;

// The feeding of the decoder for one stream (docs/design/lossy-decoders.md, "The parser and decoder split"): every core frame is walked whether or not anything is decoded, and
// where a session decodes, the walked values are lent to it. A frame the walk cannot read, one that is cut and one whose subframes do not hold the samples the header counts is
// handed unreadable. The stream's frames are those of one configuration, so they have one number of channels.
internal sealed class DecodeFeed
{
    public const string CodecId = "dts";

    private readonly IDecodeSession _session;
    private long _next;
    private bool _pendingGap;

    // The stream's channels: the primary channels its arrangement has (or the unit's header counts, for a user defined arrangement) and the LFE channel where the key has one.
    public DecodeFeed(AnalysisContext context, Node stream, int amode, int primary, bool hasLfe, int sampleRate)
    {
        Channels = primary + (hasLfe ? 1 : 0);
        Positions = [.. Layouts.Arrangement(amode)];
        if (Positions.Count != primary)
        {
            Positions = [.. Enumerable.Repeat(Position.None, primary)];
        }

        if (hasLfe)
        {
            Positions.Add(Position.Lfe);
        }

        _session = context.StartDecoding(CodecId, new DecodeSetup
        {
            Stream = stream,
            Channels = Channels,
            BitsPerSample = 32,
            IsLossy = true,
            Shared = new DecodeShared(sampleRate, Positions),
        });
    }

    public int Channels { get; }

    private List<Position> Positions { get; }

    public bool IsDecoding => _session.IsDecoding;

    // The lent planes for a frame of this audio header, empty where nothing is decoded or the frame has other channels than the stream.
    public SampleBuffers Lend(CoreAudioHeader a)
    {
        int channels = a.Channels + (a.Lff > 0 ? 1 : 0);
        return !_session.IsDecoding || channels != Channels ? default : _session.Lend(LentLayout.Stride(a.Subframes), channels);
    }

    // A frame was lost before the next one: it follows a gap.
    public void Gap() => _pendingGap = true;

    // Hands the unit to the decoder: walked from the values the planes hold, or unreadable. samples counts at the core rate.
    public void Frame(long offset, int length, int samples, bool isWalked)
    {
        long first = _next;
        _next += samples;
        if (!_session.IsDecoding)
        {
            _pendingGap |= !isWalked;
            return;
        }

        MappedBytes bytes = MappedBytes.Opaque(ReadOnlyMemory<byte>.Empty, offset, offset + length);
        if (!isWalked)
        {
            _session.Decode(new CodedFrame(bytes, "Frame", first, samples) { IsUnreadable = true });
            _pendingGap = true;
            return;
        }

        _session.Decode(new CodedFrame(bytes, "Frame", first, samples) { IsWalked = true, FollowsGap = _pendingGap });
        _pendingGap = false;
    }

    public StreamVerification? Finish() => _session.Finish([]);
}

// What the parser tells the decoder that no frame carries: the core's sampling frequency and the speakers of the channels it outputs, in order.
internal sealed record DecodeShared(int SampleRate, IReadOnlyList<Position> Positions);
