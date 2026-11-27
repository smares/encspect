namespace Encspect.Formats.Aac.Decoding;

// What a block that predicts depends on, and whether that is still right after a loss (docs/design/aac-main-ltp-er.md, "Gaps and withheld output"). Two instances serve one
// decoder: one for gaps, which reset the state the prediction runs on, and one for blocks that fail their checksum, whose state the decoder keeps and cannot vouch for.
// - Main's predictors (ISO/IEC 13818-7 13.3.3): per channel a mask of the 30 reset groups that have not been reset since the loss. A block that uses prediction on a line of such a
//   group is wrong there, until the encoder resets the group; an eight-short block resets them all.
// - Long-term prediction (ISO/IEC 14496-3 4.6.7.3): the position up to which the decoder's output is not trustworthy, and whether the overlap, the aliased half the next block
//   adds to, is. A block whose lag reaches samples before that position, or that predicts from an overlap that is not right, is wrong where it uses the prediction.
// A block that is wrong, or follows one whose second half is, is withheld: its output lies beyond that position, which it extends to its own end.
internal sealed class DependencyTrust(int channels)
{
    internal const uint AllGroups = (1u << 30) - 1;

    private readonly uint[] _open = new uint[channels];
    private long _end;

    // The next block adds to an overlap that is not right.
    public bool IsOverlapUntrusted { get; private set; }

    // A loss: the predictors' state or the history before position is gone or doubtful. The overlap is too, where the loss is a gap.
    public void Lose(long end, bool isOverlapUntrusted)
    {
        Array.Fill(_open, AllGroups);
        _end = Math.Max(_end, end);
        IsOverlapUntrusted |= isOverlapUntrusted;
    }

    // Nothing is doubtful: the decoding starts over (a gap resets the state this instance tracks).
    public void Clear(long position)
    {
        Array.Clear(_open);
        _end = position;
        IsOverlapUntrusted = false;
    }

    // Whether the channel's prediction in a block at position depends on what is lost: groups the bands that use Main's prediction cover (a bit for each reset group), lag the
    // lag of long-term prediction where it is used (-1 where it is not).
    public bool Depends(int channel, uint groups, int lag, long position) =>
        (_open[channel] & groups) != 0 || (lag >= 0 && (IsOverlapUntrusted || (lag > 0 && position - lag < _end)));

    // After the block: it is withheld (untrusted), and whether it used something lost decides whether the next block's overlap is right.
    public void Settle(bool isUntrusted, bool depends, long end)
    {
        if (isUntrusted)
        {
            _end = Math.Max(_end, end);
        }

        IsOverlapUntrusted = depends;
    }

    // Predictor reset group 1 to 30 of the channel (13.3.3).
    public void Resynchronize(int channel, int group) => _open[channel] &= ~(1u << (group - 1));

    public void ResynchronizeAll(int channel) => _open[channel] = 0;
}
