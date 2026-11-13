using Encspect.Core;

namespace Encspect.Formats.Ac3.Decoding;

// The decoder of one E-AC-3 program (an independent substream and the dependent substreams after it, Annex E 2.3.1.2): each substream is decoded by its own Eac3Substream, and their
// channels are merged into the program's layout by the channel locations they state (3.8): a dependent substream's channel at a location the program has replaces it, and one at
// another location extends the layout. The blocks of the substreams reach the merger at different times (a block with enhanced coupling waits for the next frame's first block), so
// a block is written when every substream of its unit has delivered it, in order. A gap, which one decoder notes for all the substreams, resets them and withholds the first block.
internal sealed class Eac3FrameDecoder : IFrameDecoder
{
    private const int BlockSamples = 256;

    private readonly DecodeContext _context;
    private readonly int _rate;
    private readonly Dictionary<int, Eac3Substream> _substreams = [];
    private readonly SortedDictionary<long, Block> _slots = [];
    private IPcmSink? _sink;
    private List<Program.Token>? _layout;
    private int _channels;
    private long _withheldUntil;
    private bool _lostBefore;

    // The samples of one block of the program (planar, 256 for each channel), and which substreams of its unit are to deliver theirs and have.
    private sealed class Block(int channels)
    {
        public float[] Samples { get; } = new float[channels * BlockSamples];
        public int Expected { get; set; }
        public int Got { get; set; }
    }

    public Eac3FrameDecoder(DecodeContext context, SyncFrame header)
    {
        _context = context;
        _rate = header.SampleRate;
    }

    public int Decode(in CodedFrame frame)
    {
        SampleBuffers lent = _context.LentBuffers;
        if (!frame.IsWalked || lent.IsEmpty)
        {
            _context.AddFinding(Severity.Info, "decode.frame-too-large", $"The frame at 0x{frame.Bytes.FileStart:X} could hold more values than the host lends buffers for, so it is not decoded.");
            _lostBefore = true;
            return 0;
        }

        _context.CancellationToken.ThrowIfCancellationRequested();
        var r = new IntReader(lent.Narrow);
        int layoutCount = r.Next();
        var layout = new List<Program.Token>(layoutCount);
        for (int i = 0; i < layoutCount; i++)
        {
            layout.Add((Program.Token)r.Next());
        }

        if (_layout is null)
        {
            _layout = layout;
            _channels = layoutCount;
        }

        if (frame.FollowsGap || _lostBefore)
        {
            foreach (Eac3Substream substream in _substreams.Values)
            {
                substream.FlushPending();
            }

            WriteReady(force: true);
            _pre?.Flush();
            _lostBefore = false;
            foreach (Eac3Substream substream in _substreams.Values)
            {
                substream.Reset();
            }

            _context.Withhold(frame.FirstSample, BlockSamples, WithholdReason.AfterGap);
            _withheldUntil = Math.Max(_withheldUntil, frame.FirstSample + BlockSamples);
        }

        int substreams = r.Next();
        int blocks = frame.Samples / BlockSamples;
        var present = new HashSet<int>();
        // Each substream's records are read in turn; the slots of the unit's blocks expect every substream that has a frame in it.
        for (int i = 0; i < blocks; i++)
        {
            BlockAt(frame.FirstSample + ((long)i * BlockSamples)).Expected = 0;
        }

        for (int i = 0; i < substreams; i++)
        {
            int id = r.Next();
            int count = r.Next();
            int[] slots = [.. r.Take(count).ToArray()];
            int acmod = r.Next();
            bool lfe = r.Next() != 0;
            int unitBlocks = r.Next();
            if (unitBlocks != blocks)
            {
                throw new InvalidDataException("A dependent substream has another number of blocks than the independent one");
            }

            if (!_substreams.TryGetValue(id, out Eac3Substream? substream) || substream.Acmod != acmod || substream.HasLfe != lfe)
            {
                substream?.FlushPending();
                substream = new Eac3Substream(_context, acmod, lfe, (first, planar, stride, offset) => Deliver(id, first, planar, stride, offset));
                _substreams[id] = substream;
            }

            present.Add(id);
            for (int b = 0; b < blocks; b++)
            {
                BlockAt(frame.FirstSample + ((long)b * BlockSamples)).Expected |= 1 << id;
            }

            _slotsOf[id] = slots;
            substream.DecodeUnit(ref r, frame.FirstSample, blocks);
            foreach ((int channel, int location, int length) in substream.Transients)
            {
                if (channel < slots.Length && slots[channel] >= 0)
                {
                    (_pre ??= NewPreNoise()).Add(slots[channel], frame.FirstSample + location, length);
                }
            }
        }

        // A substream with no frame in this unit has lost its overlap.
        foreach ((int id, Eac3Substream substream) in _substreams)
        {
            if (!present.Contains(id))
            {
                substream.FlushPending();
                substream.Reset();
            }
        }

        WriteReady(force: false);
        long first = Math.Max(frame.FirstSample, _withheldUntil);
        return (int)Math.Max(0, frame.Samples - (first - frame.FirstSample));
    }

    private readonly Dictionary<int, int[]> _slotsOf = [];

    private Block BlockAt(long first)
    {
        if (!_slots.TryGetValue(first, out Block? slot))
        {
            slot = new Block(_channels);
            _slots[first] = slot;
        }

        return slot;
    }

    // A block of 256 samples a substream finished: its channels go to the program's channels by the slots the unit gave.
    private void Deliver(int id, long first, float[] planar, int stride, int offset)
    {
        Block slot = BlockAt(first);
        int[] slots = _slotsOf[id];
        for (int c = 0; c < slots.Length; c++)
        {
            if (slots[c] >= 0)
            {
                Array.Copy(planar, (c * stride) + offset, slot.Samples, slots[c] * BlockSamples, BlockSamples);
            }
        }

        slot.Got |= 1 << id;
    }

    // Writes the blocks at the front that every expected substream has delivered (all of them, where force), as one run of consecutive blocks.
    private void WriteReady(bool force)
    {
        var run = new List<(long First, Block Slot)>();
        foreach ((long first, Block slot) in _slots)
        {
            if (!force && (slot.Got & slot.Expected) != slot.Expected)
            {
                break;
            }

            run.Add((first, slot));
        }

        foreach ((long first, _) in run)
        {
            _slots.Remove(first);
        }

        int at = 0;
        while (at < run.Count)
        {
            int end = at + 1;
            while (end < run.Count && run[end].First == run[end - 1].First + BlockSamples)
            {
                end++;
            }

            Write(run, at, end);
            at = end;
        }
    }

    private void Write(List<(long First, Block Slot)> run, int from, int to)
    {
        int skipBlocks = 0;
        while (from + skipBlocks < to && run[from + skipBlocks].First + BlockSamples <= _withheldUntil)
        {
            skipBlocks++;
        }

        int blocks = to - from - skipBlocks;
        if (blocks <= 0)
        {
            return;
        }

        int length = blocks * BlockSamples;
        var planar = new float[_channels * length];
        for (int b = 0; b < blocks; b++)
        {
            float[] samples = run[from + skipBlocks + b].Slot.Samples;
            for (int c = 0; c < _channels; c++)
            {
                Array.Copy(samples, c * BlockSamples, planar, (c * length) + (b * BlockSamples), BlockSamples);
            }
        }

        (_pre ??= NewPreNoise()).Append(run[from + skipBlocks].First, planar, length);
    }

    private PreNoise? _pre;

    private PreNoise NewPreNoise() => new(_channels, Output, _context.AddWork);

    private void Output(long first, float[] planar, int length)
    {
        _sink ??= _context.DeclareOutput(new PcmFormat(_rate, _channels, 32, SampleFormat.Float32) { ChannelLayout = Program.SpeakersOf(_layout!) });
        _sink.Write(new PcmBlock(first, length, _channels, length, planar));
    }

    public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end)
    {
        foreach (Eac3Substream substream in _substreams.Values)
        {
            substream.FlushPending();
        }

        WriteReady(force: true);
        _pre?.Flush();
        return [];
    }
}
