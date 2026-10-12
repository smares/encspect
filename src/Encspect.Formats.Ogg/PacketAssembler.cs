namespace Encspect.Formats.Ogg;

// One packet's bytes on one page: a run of lacing values that ends with a value below 255 (the
// packet ends here) or at the end of the segment table (it continues on the stream's next page).
// Start is the run's offset in the page body. Packet is the index the packet is handed to the codec
// with, or -1 for bytes that are not handed on: the rest of a packet whose start was lost, or a
// packet larger than the parser reads.
internal readonly record struct Run(int Start, int Length, bool Ends, bool Continues, int Packet, RunKind Kind);

internal enum RunKind
{
    Packet,

    // A page continues a packet, but no packet was left unfinished: its start was lost.
    Orphan,

    // The packet grew larger than PacketAssembler.MaxPacket.
    TooLarge,
}

// What feeding one page changed besides its runs.
internal readonly record struct PageOutcome(bool PendingBefore, int CompletedBefore, bool ContinuationMissing, bool Orphaned, bool OrphanExpected,
    int TooLarge, bool EndsInsidePacket);

// Follows one logical stream's packets across its pages (RFC 3533, section 5, "Packet
// segmentation"): a lacing value of 255 continues the packet, a smaller one ends it, and a packet
// left unfinished at the end of a page continues on the next page of the stream, which sets the
// continued flag. The scan and the codec's packets both go through it, so the packets the codec
// gets are the ones the scan counted and reported on.
internal sealed class PacketAssembler
{
    // Larger packets are not read: a stream of lacing values of 255 would otherwise make the parser
    // gather the whole file into one packet.
    public const long MaxPacket = 1 << 26;

    private bool _pending;
    private long _pendingLength;
    private bool _pendingTooLarge;
    private bool _skipping;
    private bool _afterLoss;

    public int Completed { get; private set; }

    // A page of the stream is missing or unusable: a packet left unfinished cannot be completed,
    // and a page that continues it is expected to.
    public void Lose()
    {
        _pending = false;
        _pendingLength = 0;
        _pendingTooLarge = false;
        _skipping = false;
        _afterLoss = true;
    }

    // State as it was before a page, so a page node can show its runs as the scan saw them.
    public void Restore(bool pending, int completed)
    {
        _pending = pending;
        _pendingLength = 0;
        _pendingTooLarge = false;
        _skipping = false;
        _afterLoss = false;
        Completed = completed;
    }

    public PageOutcome Feed(bool continued, ReadOnlySpan<byte> lacing, List<Run> runs)
    {
        runs.Clear();
        bool pendingBefore = _pending;
        int completedBefore = Completed;
        bool continuationMissing = false;
        bool orphaned = false;
        bool orphanExpected = _afterLoss || _skipping;
        int tooLarge = 0;
        int index = 0;
        int position = 0;

        if (continued && !_pending && lacing.Length > 0)
        {
            // libogg skips such segments up to the end of the packet they belong to.
            int start = position;
            bool ends = false;
            while (index < lacing.Length)
            {
                byte value = lacing[index++];
                position += value;
                if (value < 255)
                {
                    ends = true;
                    break;
                }
            }

            orphaned = true;
            _skipping = !ends;
            runs.Add(new Run(start, position - start, ends, true, -1, RunKind.Orphan));
        }
        else if (!continued && _pending)
        {
            // The page says it starts a fresh packet, so the one left unfinished never ends.
            continuationMissing = true;
            _pending = false;
            _pendingLength = 0;
            _pendingTooLarge = false;
        }
        else if (!continued)
        {
            _skipping = false;
        }

        while (index < lacing.Length)
        {
            int start = position;
            bool continues = _pending;
            long length = 0;
            bool ends = false;
            while (index < lacing.Length)
            {
                byte value = lacing[index++];
                length += value;
                if (value < 255)
                {
                    ends = true;
                    break;
                }
            }

            position += (int)length;
            _pendingLength += length;
            bool large = _pendingTooLarge || _pendingLength > MaxPacket;
            if (ends)
            {
                runs.Add(new Run(start, (int)length, true, continues, large ? -1 : Completed, large ? RunKind.TooLarge : RunKind.Packet));
                if (large)
                {
                    tooLarge++;
                }
                else
                {
                    Completed++;
                }

                _pending = false;
                _pendingLength = 0;
                _pendingTooLarge = false;
            }
            else
            {
                runs.Add(new Run(start, (int)length, false, continues, large ? -1 : Completed, large ? RunKind.TooLarge : RunKind.Packet));
                _pending = true;
                _pendingTooLarge = large;
            }
        }

        _afterLoss = false;
        return new PageOutcome(pendingBefore, completedBefore, continuationMissing, orphaned, orphanExpected, tooLarge, _pending);
    }

    public bool IsPending => _pending;
}
