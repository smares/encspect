namespace Encspect.Core;

// The sample buffers a decode session lends the parser for one frame (IDecodeSession.Lend): channel c holds its samples
// from c * Stride on, in the 32-bit plane and, where the parser asked for it, in the 64-bit plane too (the side channel
// of a 32-bit FLAC stream needs 33 bits, RFC 9639, section 9.2.3). They belong to the session, are valid until the next
// Lend, and are empty where nothing is decoded or the frame is past the host's limit.
public readonly ref struct SampleBuffers
{
    public SampleBuffers(Span<int> narrow, Span<long> wide, int channels, int stride)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channels);
        ArgumentOutOfRangeException.ThrowIfNegative(stride);

        Narrow = narrow;
        Wide = wide;
        Channels = channels;
        Stride = stride;
    }

    public Span<int> Narrow { get; }

    public Span<long> Wide { get; }

    public int Channels { get; }

    // The samples per channel the buffers were lent for.
    public int Stride { get; }

    public bool IsEmpty => Narrow.IsEmpty && Wide.IsEmpty;

    public Span<int> Channel(int channel) => Narrow.Slice(channel * Stride, Stride);

    public Span<long> WideChannel(int channel) => Wide.Slice(channel * Stride, Stride);
}
