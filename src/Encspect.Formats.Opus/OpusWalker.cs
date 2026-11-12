namespace Encspect.Formats.Opus;

// What the entropy pass of one Opus frame found.
internal struct FrameFacts
{
    public int Mode;
    public int Bandwidth;
    public bool IsEmpty;
    public bool IsOverrun;
    public bool HasRedundancy;
    public bool IsCeltToSilk;
    public int RedundancyBytes;
    public bool HasSilk;
    public bool HasCelt;
    public SilkFacts Silk;
    public CeltFacts Celt;
    public uint FinalRange;
}

// The entropy pass of one Opus stream: the frame by frame control flow of opus_decode_frame (opus_decoder.c of libopus 1.5.2, BSD-3-Clause, notice in
// THIRD-PARTY-NOTICES.md) that depends on the coded syntax alone, the SILK syntax (SilkSyntax) and the CELT syntax (CeltSyntax). It is the state a decoder
// carries between frames as far as it decides how the next frame is read: the mode and the redundancy of the frame before, and SILK's coding contexts.
//
// The record of a frame (integers, in order): 0 for a frame that codes data or 1 for one that does not (DTX, or a lost frame: a frame of at most one byte, which libopus
// conceals and the analyzer does not); the mode (0 SILK, 1 hybrid, 2 CELT), the bandwidth (0 narrowband to 4 fullband), the samples of the frame at 48 kHz and the stream's
// channels; and, for a frame with data: the control flags (bit 0: SILK is reset, bit 1: CELT is reset, bit 2: a silent CELT frame follows the SILK frame, bit 3: the mode
// changes without redundancy, so the mode before it is concealed for 5 ms, bits 4 to 5: that mode); the redundancy (0 or 1) and, if there is one, whether it comes before the
// frame (celt_to_silk) and its bytes; the SILK part (SilkSyntax) if the mode has one; the CELT frames in the order the decoder decodes them: the redundant frame where it
// comes before, the silent frame, the frame of the CELT part, the redundant frame where it comes after (CeltSyntax); the frame's final range coder state.
internal sealed class OpusWalker(bool disableInv)
{
    private readonly CeltSyntax _celt = new();
    private readonly SilkSyntax _silk = new();
    private int _previousMode = -1;
    private bool _previousRedundancy;

    public void Reset()
    {
        _silk.Reset();
        _previousMode = -1;
        _previousRedundancy = false;
    }

    // The 48 kHz sample rate's frame size of 5 ms and 2.5 ms.
    private const int F5 = 240;
    private const int F25 = 120;

    private static int LogOf(int samples) => samples switch { 120 => 0, 240 => 1, 480 => 2, _ => 3 };

    private static int EndBand(int bandwidth) => bandwidth switch { 0 => 13, 1 or 2 => 17, 3 => 19, _ => 21 };

    // Walks the frame of toc's packet that holds frame, writing its record. samples is the frame's duration at 48 kHz, channels the stream's (from the TOC).
    public FrameFacts Walk(IntWriter w, ReadOnlySpan<byte> frame, int mode, int bandwidth, int samples, int channels)
    {
        var facts = new FrameFacts { Mode = mode, Bandwidth = bandwidth };
        w.Add(frame.Length <= 1 ? 1 : 0);
        w.Add(mode);
        w.Add(bandwidth);
        w.Add(samples);
        w.Add(channels);
        if (frame.Length <= 1)
        {
            facts.IsEmpty = true;
            Reset();
            return facts;
        }

        int len = frame.Length;
        var dec = new RangeDecoder(frame);
        int flagsAt = w.Reserve(1);
        int previousMode = _previousMode;
        bool previousRedundancy = _previousRedundancy;
        bool transitionToCelt = previousMode >= 0 && mode == 2 && previousMode != 2 && !previousRedundancy;
        bool transitionFromCelt = previousMode >= 0 && mode != 2 && previousMode == 2;
        int flags = 0;
        if (mode != 2)
        {
            if (previousMode == 2)
            {
                flags |= 1;
                _silk.ResetDecoder();
            }

            int fsKHz = mode == 1 ? 16 : bandwidth switch { 0 => 8, 1 => 12, _ => 16 };
            facts.Silk = _silk.Walk(ref dec, w, channels, fsKHz, samples / 48);
            facts.HasSilk = true;
        }

        // The redundancy flags follow the SILK part (section 4.5.1).
        bool redundancy = false;
        bool celtToSilk = false;
        int redundancyBytes = 0;
        if (mode != 2 && dec.Tell() + 17 + (mode == 1 ? 20 : 0) <= 8 * len)
        {
            redundancy = mode == 1 ? dec.BitLogP(12) != 0 : true;
            if (redundancy)
            {
                celtToSilk = dec.BitLogP(1) != 0;
                redundancyBytes = mode == 1 ? (int)dec.UInt(256) + 2 : len - ((dec.Tell() + 7) >> 3);
                len -= redundancyBytes;
                if (len * 8 < dec.Tell())
                {
                    facts.IsOverrun = true;
                    return facts;
                }

                dec.Storage -= (uint)redundancyBytes;
            }
        }

        facts.HasRedundancy = redundancy;
        facts.IsCeltToSilk = celtToSilk;
        facts.RedundancyBytes = redundancyBytes;
        w.Add(redundancy ? 1 : 0);
        w.Add(celtToSilk ? 1 : 0);
        w.Add(redundancyBytes);

        // A mode change without redundancy conceals the mode before it for 5 ms (section 4.5.2): a CELT frame after a frame of another mode that did not end with a
        // redundant CELT frame, and a frame of another mode after a CELT frame. The inner call of libopus leaves the mode before it and no redundancy behind.
        bool transition = (transitionToCelt || transitionFromCelt) && !redundancy;
        int pm = previousMode;
        bool pr = previousRedundancy;
        if (transition)
        {
            int concealed = pr ? 2 : pm;
            flags |= 8 | (concealed << 4);
            pm = concealed;
            pr = false;
        }

        int start = mode != 2 ? 17 : 0;
        int end = EndBand(bandwidth);
        if (mode != 0 && pm >= 0 && mode != pm && !pr)
        {
            flags |= 2;
        }

        // After a hybrid frame a SILK frame fades the CELT part out with a silent frame of 2.5 ms.
        bool silenceFrame = mode == 0 && previousMode == 1 && !(redundancy && celtToSilk && previousRedundancy);
        if (silenceFrame)
        {
            flags |= 4;
        }

        w.Set(flagsAt, flags);
        uint redundantRange = 0;
        if (redundancy && celtToSilk)
        {
            facts.Celt = WalkRedundant(w, frame.Slice(len, redundancyBytes), channels, end, out redundantRange);
        }

        if (silenceFrame)
        {
            var silence = new RangeDecoder([0xFF, 0xFF]);
            _celt.Walk(ref silence, w, 2, 0, channels, 0, end, disableInv);
        }

        if (mode != 0)
        {
            int celtSamples = Math.Min(960, samples);
            facts.Celt = _celt.Walk(ref dec, w, len, LogOf(celtSamples), channels, start, end, disableInv);
            facts.HasCelt = true;
            if (facts.Celt.IsOverrun)
            {
                facts.IsOverrun = true;
            }
        }

        if (redundancy && !celtToSilk)
        {
            _ = WalkRedundant(w, frame.Slice(len, redundancyBytes), channels, end, out redundantRange);
        }

        facts.FinalRange = dec.Rng ^ redundantRange;
        w.Add(facts.FinalRange);
        _previousMode = mode;
        _previousRedundancy = redundancy && !celtToSilk;
        return facts;
    }

    private CeltFacts WalkRedundant(IntWriter w, ReadOnlySpan<byte> data, int channels, int end, out uint range)
    {
        var dec = new RangeDecoder(data);
        CeltFacts facts = _celt.Walk(ref dec, w, data.Length, LogOf(F5), channels, 0, end, disableInv);
        range = dec.Rng;
        return facts;
    }
}
