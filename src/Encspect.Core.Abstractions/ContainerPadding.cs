namespace Encspect.Core;

// The end trimming a container states for a track's last block (Matroska's DiscardPadding, RFC 9559 section 5.1.3.5.7), read the same way by every codec
// parser: the padding is per block, so it can take no more samples than the frames of that block hold (CodecConfiguration.DiscardPaddingFrames), and a
// padding that asks for more is cut to them with the finding "<codec>.discard-padding-large".
public static class ContainerPadding
{
    // The padding in samples at the stream's rate, rounded to the nearest, halves up.
    public static long ToSamples(double seconds, int rate) => (long)Math.Floor((seconds * rate) + 0.5);

    public static string LargeMessage(long padding, long held, int frames) =>
        $"The DiscardPadding of the last block is {Count(padding)}, more than the {Count(held)} its {(frames == 1 ? "frame holds" : $"{frames} frames hold")} (RFC 9559, section 5.1.3.5.7); the end trimming is taken as {held}.";

    private static string Count(long samples) => samples == long.MaxValue ? "at least 9223372036854775807 samples" : samples == 1 ? "1 sample" : $"{samples} samples";
}
