using System.Numerics;

namespace Encspect.Formats.Opus;

// What the entropy pass of one CELT frame found, for the report and for the decoder's control flow.
internal struct CeltFacts
{
    public bool IsSilence;
    public bool IsTransient;
    public bool IsIntra;
    public bool HasPostfilter;
    public bool IsAntiCollapse;
    public bool IsDualStereo;
    public int Spread;
    public int Intensity;
    public int CodedBands;
    public int Start;
    public int End;
    public int Channels;
    public int LogFrame;

    // The bands the stereo frame codes with an angle (mid/side), as two channels on their own (dual stereo) and as intensity stereo, over [Start, End).
    public int AngleBands;
    public int DualBands;
    public int IntensityBands;

    // The frame needs more bits than it holds (celt_decode_with_ec returns OPUS_INTERNAL_ERROR, and the packet is refused).
    public bool IsOverrun;

    public uint FinalRange;
}

// The syntax of a CELT frame (RFC 6716, section 4.3): the range coded header, the energy, the allocation, the shape of every band and the final energy bits, read with the
// code of celt_decoder.c, quant_bands.c, rate.c, bands.c, cwrs.c and laplace.c of libopus 1.5.2 (BSD-3-Clause, notice in THIRD-PARTY-NOTICES.md). The allocation is integer
// arithmetic the syntax needs to know how many bits each band takes. The walk stores the integers the reconstruction needs (the record below) and no float.
//
// The record of a frame (integers, in order): flags (bit 0 silence, 1 transient, 2 intra, 3 anti-collapse, 4 dual stereo), LM, start, end, channels; postfilter pitch, gain
// (the coded index plus 1, 0 for none) and tapset; spread, intensity; tf_res for each band; the coarse energy steps of each band and channel; fine_quant of each band;
// the fine energy bits of each band and channel that has some; the bits of each band (for the anti-collapse depth); the number of band integers, then the bands'
// events (CeltBands: per band the folding source, then for every partition either a split (1, the angle, which half comes first) or a leaf (0, the pulse count and the pulses,
// or 0, 0 and how the band is filled)); the final energy bits of each band and channel (-1 where none); the collapse mask of each band and channel; the final range.
internal sealed class CeltSyntax
{
    public const int Bands = 21;
    public const int ShortMdct = 120;
    public const int MaxLm = 3;
    private const int Bitres = 3;
    private const int MaxFineBits = 8;
    private const int FineOffset = 21;
    private const int QThetaOffset = 4;
    private const int QThetaOffsetTwoPhase = 16;
    private const int LogMaxPseudo = 6;
    private const int AllocSteps = 6;
    private const int SpreadAggressive = 3;

    private static readonly byte[] SmallEnergyICdf = [2, 1, 0];
    private static readonly byte[] TrimICdf = [126, 124, 119, 109, 87, 41, 19, 9, 4, 2, 0];
    private static readonly byte[] SpreadICdf = [25, 23, 2, 0];
    private static readonly byte[] TapsetICdf = [2, 1, 0];
    private static readonly byte[] Log2FracTable = [0, 8, 13, 16, 19, 21, 23, 24, 26, 27, 28, 29, 30, 31, 32, 32, 33, 34, 34, 35, 36, 36, 37, 37];
    private static readonly byte[] BitInterleaveTable = [0, 1, 1, 1, 2, 3, 3, 3, 2, 3, 3, 3, 2, 3, 3, 3];
    private static readonly byte[] BitDeinterleaveTable = [0x00, 0x03, 0x0C, 0x0F, 0x30, 0x33, 0x3C, 0x3F, 0xC0, 0xC3, 0xCC, 0xCF, 0xF0, 0xF3, 0xFC, 0xFF];
    private static readonly short[] Exp2Table8 = [16384, 17866, 19483, 21247, 23170, 25267, 27554, 30048];

    // The pairs of the 48 kHz mode's band edges (eband5ms), band widths and bands per frame size are looked up from the tables.
    private static ReadOnlySpan<short> EBands => CeltTables.EBand5Ms;

    private readonly int[] _cap = new int[Bands];
    private readonly int[] _offsets = new int[Bands];
    private readonly int[] _fineQuant = new int[Bands];
    private readonly int[] _fineQuantEnergy = new int[Bands];
    private readonly int[] _pulses = new int[Bands];
    private readonly int[] _finePriority = new int[Bands];
    private readonly int[] _tfRes = new int[Bands];
    private readonly byte[] _collapse = new byte[2 * Bands];
    private readonly int[] _bits1 = new int[Bands];
    private readonly int[] _bits2 = new int[Bands];
    private readonly int[] _thresh = new int[Bands];
    private readonly int[] _trimOffset = new int[Bands];
    private readonly int[] _pulseScratch = new int[256];

    // The band loop's own state (struct band_ctx of bands.c).
    private int _band;
    private int _intensity;
    private int _tfChange;
    private int _remainingBits;
    private bool _disableInv;

    internal static int GetPulses(int i) => i < 8 ? i : (8 + (i & 7)) << ((i >> 3) - 1);

    private static int Ilog(uint v) => 32 - BitOperations.LeadingZeroCount(v);

    private static int CeltUdiv(int n, int d) => (int)((uint)n / (uint)d);

    private static int CeltSudiv(int n, int d) => n < 0 ? -(int)((uint)-n / (uint)d) : (int)((uint)n / (uint)d);

    private static int FracMul16(int a, int b) => (16384 + ((short)a * (short)b)) >> 15;

    internal static uint Isqrt32(uint val)
    {
        uint g = 0;
        int bshift = (Ilog(val) - 1) >> 1;
        uint b = 1u << bshift;
        do
        {
            uint t = ((g << 1) + b) << bshift;
            if (t <= val)
            {
                g += b;
                val -= t;
            }

            b >>= 1;
            bshift--;
        }
        while (bshift >= 0);
        return g;
    }

    internal static int BitexactCos(int x)
    {
        int tmp = (4096 + (x * x)) >> 13;
        int x2 = tmp;
        x2 = (32767 - x2) + FracMul16(x2, -7651 + FracMul16(x2, 8277 + FracMul16(-626, x2)));
        return 1 + x2;
    }

    internal static int BitexactLog2Tan(int isin, int icos)
    {
        int lc = Ilog((uint)icos);
        int ls = Ilog((uint)isin);
        icos <<= 15 - lc;
        isin <<= 15 - ls;
        return ((ls - lc) * (1 << 11)) + FracMul16(isin, FracMul16(isin, -2597) + 7932) - FracMul16(icos, FracMul16(icos, -2597) + 7932);
    }

    private static int Bits2Pulses(int band, int lm, int bits)
    {
        lm++;
        ReadOnlySpan<byte> bitsTable = CeltTables.CacheBits50;
        int at = CeltTables.CacheIndex50[(lm * Bands) + band];
        int lo = 0;
        int hi = bitsTable[at];
        bits--;
        for (int i = 0; i < LogMaxPseudo; i++)
        {
            int mid = (lo + hi + 1) >> 1;
            if (bitsTable[at + mid] >= bits)
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        return bits - (lo == 0 ? -1 : bitsTable[at + lo]) <= bitsTable[at + hi] - bits ? lo : hi;
    }

    private static int Pulses2Bits(int band, int lm, int pulses)
    {
        lm++;
        int at = CeltTables.CacheIndex50[(lm * Bands) + band];
        return pulses == 0 ? 0 : CeltTables.CacheBits50[at + pulses] + 1;
    }

    private static void InitCaps(Span<int> cap, int lm, int c)
    {
        for (int i = 0; i < Bands; i++)
        {
            int n = (EBands[i + 1] - EBands[i]) << lm;
            cap[i] = (CeltTables.CacheCaps50[(Bands * ((2 * lm) + c - 1)) + i] + 64) * c * n >> 2;
        }
    }

    // Walks one frame whose range decoder is positioned at its first symbol (the start of the frame, or after the SILK part of a hybrid frame). len is the frame's
    // bytes, which the budgets are counted in (the shrunken length of a hybrid frame with a redundant frame behind it). disableInv: the stream's decoder has one
    // output channel (celt_decoder.c, "disable_inv = channels == 1"), which then never inverts the side of an intensity band.
    public CeltFacts Walk(ref RangeDecoder dec, IntWriter w, int len, int lm, int channels, int start, int end, bool disableInv)
    {
        _disableInv = disableInv;
        int m = 1 << lm;
        int c = channels;
        var facts = new CeltFacts { Start = start, End = end, Channels = c, LogFrame = lm };

        int flagsAt = w.Reserve(1);
        w.Add(lm);
        w.Add(start);
        w.Add(end);
        w.Add(c);

        int totalBits = len * 8;
        int tell = dec.Tell();
        bool silence;
        if (tell >= totalBits)
        {
            silence = true;
        }
        else if (tell == 1)
        {
            silence = dec.BitLogP(15) != 0;
        }
        else
        {
            silence = false;
        }

        if (silence)
        {
            tell = len * 8;
            dec.AddBits(tell - dec.Tell());
        }

        int postfilterPitch = 0;
        int postfilterGain = 0;
        int postfilterTapset = 0;
        if (start == 0 && tell + 16 <= totalBits)
        {
            if (dec.BitLogP(1) != 0)
            {
                int octave = (int)dec.UInt(6);
                postfilterPitch = (16 << octave) + (int)dec.Bits(4 + octave) - 1;
                int qg = (int)dec.Bits(3);
                if (dec.Tell() + 2 <= totalBits)
                {
                    postfilterTapset = dec.ICdf(TapsetICdf, 2);
                }

                postfilterGain = qg + 1;
                facts.HasPostfilter = true;
            }

            tell = dec.Tell();
        }

        bool isTransient = false;
        if (lm > 0 && tell + 3 <= totalBits)
        {
            isTransient = dec.BitLogP(3) != 0;
            tell = dec.Tell();
        }

        bool intra = tell + 3 <= totalBits && dec.BitLogP(3) != 0;
        w.Add(postfilterPitch);
        w.Add(postfilterGain);
        w.Add(postfilterTapset);

        // The spread is written after it is read; its place is kept now so that the fixed fields stay together.
        int spreadAt = w.Reserve(2);
        for (int i = start; i < end; i++)
        {
            _tfRes[i] = 0;
        }

        // The tf, coarse energy and spread come in the order of the stream.
        int tfAt = w.Reserve(end - start);
        int coarseAt = w.Reserve((end - start) * c);
        ReadCoarseEnergy(ref dec, w, coarseAt, start, end, intra, c, lm);
        ReadTf(ref dec, start, end, isTransient, lm);
        for (int i = start; i < end; i++)
        {
            w.Set(tfAt + i - start, _tfRes[i]);
        }

        tell = dec.Tell();
        int spread = 2;
        if (tell + 4 <= totalBits)
        {
            spread = dec.ICdf(SpreadICdf, 5);
        }

        InitCaps(_cap, lm, c);
        int dynallocLogp = 6;
        int totalBitsFrac = totalBits << Bitres;
        int tellFrac = (int)dec.TellFrac();
        for (int i = start; i < end; i++)
        {
            int width = (c * (EBands[i + 1] - EBands[i])) << lm;
            int quanta = Math.Min(width << Bitres, Math.Max(6 << Bitres, width));
            int loopLogp = dynallocLogp;
            int boost = 0;
            while (tellFrac + (loopLogp << Bitres) < totalBitsFrac && boost < _cap[i])
            {
                int flag = dec.BitLogP(loopLogp);
                tellFrac = (int)dec.TellFrac();
                if (flag == 0)
                {
                    break;
                }

                boost += quanta;
                totalBitsFrac -= quanta;
                loopLogp = 1;
            }

            _offsets[i] = boost;
            if (boost > 0)
            {
                dynallocLogp = Math.Max(2, dynallocLogp - 1);
            }
        }

        int allocTrim = tellFrac + (6 << Bitres) <= totalBitsFrac ? dec.ICdf(TrimICdf, 7) : 5;
        int bits = ((len * 8) << Bitres) - (int)dec.TellFrac() - 1;
        int antiCollapseRsv = isTransient && lm >= 2 && bits >= ((lm + 2) << Bitres) ? 1 << Bitres : 0;
        bits -= antiCollapseRsv;
        int intensity = 0;
        bool dualStereo = false;
        int balance;
        int codedBands = ComputeAllocation(ref dec, start, end, allocTrim, ref intensity, ref dualStereo, bits, out balance, c, lm);

        // The fine energy: the bits of every band with fine bits, read after the allocation.
        int fineQuantAt = w.Reserve(end - start);
        for (int i = start; i < end; i++)
        {
            w.Set(fineQuantAt + i - start, _fineQuant[i]);
        }

        for (int i = start; i < end; i++)
        {
            if (_fineQuant[i] <= 0)
            {
                continue;
            }

            for (int ch = 0; ch < c; ch++)
            {
                w.Add((int)dec.Bits(_fineQuant[i]));
            }
        }

        for (int i = start; i < end; i++)
        {
            w.Add(_pulses[i]);
        }

        for (int i = 0; i < c * Bands; i++)
        {
            _collapse[i] = 0;
        }

        int bandsAt = w.Reserve(1);
        int bandsStart = w.Count;
        QuantAllBands(ref dec, w, start, end, isTransient ? m : 0, spread, dualStereo, intensity, (len * (8 << Bitres)) - antiCollapseRsv, balance, lm, codedBands, c, m);
        w.Set(bandsAt, w.Count - bandsStart);

        bool antiCollapseOn = false;
        if (antiCollapseRsv > 0)
        {
            antiCollapseOn = dec.Bits(1) != 0;
        }

        // unquant_energy_finalise
        int bitsLeft = (len * 8) - dec.Tell();
        int finAt = w.Reserve((end - start) * c);
        for (int i = 0; i < (end - start) * c; i++)
        {
            w.Set(finAt + i, -1);
        }

        for (int prio = 0; prio < 2; prio++)
        {
            for (int i = start; i < end && bitsLeft >= c; i++)
            {
                if (_fineQuant[i] >= MaxFineBits || _finePriority[i] != prio)
                {
                    continue;
                }

                for (int ch = 0; ch < c; ch++)
                {
                    w.Set(finAt + ((i - start) * c) + ch, (int)dec.Bits(1));
                    bitsLeft--;
                }
            }
        }

        for (int i = start; i < end; i++)
        {
            for (int ch = 0; ch < c; ch++)
            {
                w.Add(_collapse[(i * c) + ch]);
            }
        }

        w.Set(spreadAt, spread);
        w.Set(spreadAt + 1, intensity);
        w.Set(flagsAt, (silence ? 1 : 0) | (isTransient ? 2 : 0) | (intra ? 4 : 0) | (antiCollapseOn ? 8 : 0) | (dualStereo ? 16 : 0));

        facts.IsSilence = silence;
        facts.IsTransient = isTransient;
        facts.IsIntra = intra;
        facts.IsAntiCollapse = antiCollapseOn;
        facts.IsDualStereo = dualStereo;
        facts.Spread = spread;
        facts.Intensity = intensity;
        facts.CodedBands = codedBands;
        facts.IsOverrun = dec.Tell() > 8 * len;
        facts.FinalRange = dec.Rng;
        w.Add(dec.Rng);
        if (c == 2)
        {
            for (int i = start; i < end; i++)
            {
                if (i >= intensity)
                {
                    facts.IntensityBands++;
                }
                else if (dualStereo)
                {
                    facts.DualBands++;
                }
                else
                {
                    facts.AngleBands++;
                }
            }
        }

        return facts;
    }

    // unquant_coarse_energy (quant_bands.c): the steps of every band and channel.
    private static void ReadCoarseEnergy(ref RangeDecoder dec, IntWriter w, int at, int start, int end, bool intra, int c, int lm)
    {
        int model = ((lm * 2) + (intra ? 1 : 0)) * 42;
        int budget = (int)dec.Storage * 8;
        for (int i = start; i < end; i++)
        {
            for (int ch = 0; ch < c; ch++)
            {
                int tell = dec.Tell();
                int qi;
                if (budget - tell >= 15)
                {
                    int pi = 2 * Math.Min(i, 20);
                    qi = dec.Laplace((uint)CeltTables.EProbModel[model + pi] << 7, CeltTables.EProbModel[model + pi + 1] << 6);
                }
                else if (budget - tell >= 2)
                {
                    qi = dec.ICdf(SmallEnergyICdf, 2);
                    qi = (qi >> 1) ^ -(qi & 1);
                }
                else if (budget - tell >= 1)
                {
                    qi = -dec.BitLogP(1);
                }
                else
                {
                    qi = -1;
                }

                w.Set(at + ((i - start) * c) + ch, qi);
            }
        }
    }

    // tf_decode (celt_decoder.c).
    private void ReadTf(ref RangeDecoder dec, int start, int end, bool isTransient, int lm)
    {
        int budget = (int)dec.Storage * 8;
        int tell = dec.Tell();
        int logp = isTransient ? 2 : 4;
        bool tfSelectRsv = lm > 0 && tell + logp + 1 <= budget;
        budget -= tfSelectRsv ? 1 : 0;
        int tfChanged = 0;
        int curr = 0;
        for (int i = start; i < end; i++)
        {
            if (tell + logp <= budget)
            {
                curr ^= dec.BitLogP(logp);
                tell = dec.Tell();
                tfChanged |= curr;
            }

            _tfRes[i] = curr;
            logp = isTransient ? 4 : 5;
        }

        int tfSelect = 0;
        int t = isTransient ? 4 : 0;
        if (tfSelectRsv && CeltTables.TfSelect[(lm * 8) + t + 0 + tfChanged] != CeltTables.TfSelect[(lm * 8) + t + 2 + tfChanged])
        {
            tfSelect = dec.BitLogP(1);
        }

        for (int i = start; i < end; i++)
        {
            _tfRes[i] = CeltTables.TfSelect[(lm * 8) + t + (2 * tfSelect) + _tfRes[i]];
        }
    }

    // clt_compute_allocation and interp_bits2pulses (rate.c), decoding side.
    private int ComputeAllocation(ref RangeDecoder dec, int start, int end, int allocTrim, ref int intensity, ref bool dualStereo, int total, out int balanceOut, int c, int lm)
    {
        total = Math.Max(total, 0);
        int len = Bands;
        int skipStart = start;
        int skipRsv = total >= 1 << Bitres ? 1 << Bitres : 0;
        total -= skipRsv;
        int intensityRsv = 0;
        int dualStereoRsv = 0;
        if (c == 2)
        {
            intensityRsv = Log2FracTable[end - start];
            if (intensityRsv > total)
            {
                intensityRsv = 0;
            }
            else
            {
                total -= intensityRsv;
                dualStereoRsv = total >= 1 << Bitres ? 1 << Bitres : 0;
                total -= dualStereoRsv;
            }
        }

        for (int j = start; j < end; j++)
        {
            _thresh[j] = Math.Max(c << Bitres, (3 * (EBands[j + 1] - EBands[j]) << lm << Bitres) >> 4);
            _trimOffset[j] = c * (EBands[j + 1] - EBands[j]) * (allocTrim - 5 - lm) * (end - j - 1) * (1 << (lm + Bitres)) >> 6;
            if ((EBands[j + 1] - EBands[j]) << lm == 1)
            {
                _trimOffset[j] -= c << Bitres;
            }
        }

        ReadOnlySpan<byte> alloc = CeltTables.BandAllocation;
        int nbAllocVectors = 11;
        int lo = 1;
        int hi = nbAllocVectors - 1;
        do
        {
            int done = 0;
            int psum = 0;
            int mid = (lo + hi) >> 1;
            for (int j = end; j-- > start;)
            {
                int n = EBands[j + 1] - EBands[j];
                int bitsj = c * n * alloc[(mid * len) + j] << lm >> 2;
                if (bitsj > 0)
                {
                    bitsj = Math.Max(0, bitsj + _trimOffset[j]);
                }

                bitsj += _offsets[j];
                if (bitsj >= _thresh[j] || done != 0)
                {
                    done = 1;
                    psum += Math.Min(bitsj, _cap[j]);
                }
                else if (bitsj >= c << Bitres)
                {
                    psum += c << Bitres;
                }
            }

            if (psum > total)
            {
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }
        while (lo <= hi);
        hi = lo--;
        for (int j = start; j < end; j++)
        {
            int n = EBands[j + 1] - EBands[j];
            int bits1j = c * n * alloc[(lo * len) + j] << lm >> 2;
            int bits2j = hi >= nbAllocVectors ? _cap[j] : c * n * alloc[(hi * len) + j] << lm >> 2;
            if (bits1j > 0)
            {
                bits1j = Math.Max(0, bits1j + _trimOffset[j]);
            }

            if (bits2j > 0)
            {
                bits2j = Math.Max(0, bits2j + _trimOffset[j]);
            }

            if (lo > 0)
            {
                bits1j += _offsets[j];
            }

            bits2j += _offsets[j];
            if (_offsets[j] > 0)
            {
                skipStart = j;
            }

            bits2j = Math.Max(0, bits2j - bits1j);
            _bits1[j] = bits1j;
            _bits2[j] = bits2j;
        }

        return InterpBits2Pulses(ref dec, start, end, skipStart, total, out balanceOut, skipRsv, ref intensity, intensityRsv, ref dualStereo, dualStereoRsv, c, lm);
    }

    private int InterpBits2Pulses(ref RangeDecoder dec, int start, int end, int skipStart, int total, out int balanceOut, int skipRsv, ref int intensity, int intensityRsv,
        ref bool dualStereo, int dualStereoRsv, int c, int lm)
    {
        int allocFloor = c << Bitres;
        bool stereo = c > 1;
        int logM = lm << Bitres;
        int lo = 0;
        int hi = 1 << AllocSteps;
        for (int i = 0; i < AllocSteps; i++)
        {
            int mid = (lo + hi) >> 1;
            int psum = 0;
            bool done = false;
            for (int j = end; j-- > start;)
            {
                int tmp = _bits1[j] + (int)(mid * (long)_bits2[j] >> AllocSteps);
                if (tmp >= _thresh[j] || done)
                {
                    done = true;
                    psum += Math.Min(tmp, _cap[j]);
                }
                else if (tmp >= allocFloor)
                {
                    psum += allocFloor;
                }
            }

            if (psum > total)
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        int psumFinal = 0;
        bool doneFinal = false;
        for (int j = end; j-- > start;)
        {
            int tmp = _bits1[j] + (int)(lo * (long)_bits2[j] >> AllocSteps);
            if (tmp < _thresh[j] && !doneFinal)
            {
                tmp = tmp >= allocFloor ? allocFloor : 0;
            }
            else
            {
                doneFinal = true;
            }

            tmp = Math.Min(tmp, _cap[j]);
            _pulses[j] = tmp;
            psumFinal += tmp;
        }

        int codedBands;
        for (codedBands = end; ; codedBands--)
        {
            int j = codedBands - 1;
            if (j <= skipStart)
            {
                total += skipRsv;
                break;
            }

            int left = total - psumFinal;
            int percoeff = CeltUdiv(left, EBands[codedBands] - EBands[start]);
            left -= (EBands[codedBands] - EBands[start]) * percoeff;
            int rem = Math.Max(left - (EBands[j] - EBands[start]), 0);
            int bandWidth = EBands[codedBands] - EBands[j];
            int bandBits = _pulses[j] + (percoeff * bandWidth) + rem;
            if (bandBits >= Math.Max(_thresh[j], allocFloor + (1 << Bitres)))
            {
                if (dec.BitLogP(1) != 0)
                {
                    break;
                }

                psumFinal += 1 << Bitres;
                bandBits -= 1 << Bitres;
            }

            psumFinal -= _pulses[j] + intensityRsv;
            if (intensityRsv > 0)
            {
                intensityRsv = Log2FracTable[j - start];
            }

            psumFinal += intensityRsv;
            if (bandBits >= allocFloor)
            {
                psumFinal += allocFloor;
                _pulses[j] = allocFloor;
            }
            else
            {
                _pulses[j] = 0;
            }
        }

        if (intensityRsv > 0)
        {
            intensity = start + (int)dec.UInt((uint)(codedBands + 1 - start));
        }
        else
        {
            intensity = 0;
        }

        if (intensity <= start)
        {
            total += dualStereoRsv;
            dualStereoRsv = 0;
        }

        if (dualStereoRsv > 0)
        {
            dualStereo = dec.BitLogP(1) != 0;
        }
        else
        {
            dualStereo = false;
        }

        int left2 = total - psumFinal;
        int percoeff2 = CeltUdiv(left2, EBands[codedBands] - EBands[start]);
        left2 -= (EBands[codedBands] - EBands[start]) * percoeff2;
        for (int j = start; j < codedBands; j++)
        {
            _pulses[j] += percoeff2 * (EBands[j + 1] - EBands[j]);
        }

        for (int j = start; j < codedBands; j++)
        {
            int tmp = Math.Min(left2, EBands[j + 1] - EBands[j]);
            _pulses[j] += tmp;
            left2 -= tmp;
        }

        int balance = 0;
        int jj;
        for (jj = start; jj < codedBands; jj++)
        {
            int n0 = EBands[jj + 1] - EBands[jj];
            int n = n0 << lm;
            int bit = _pulses[jj] + balance;
            int excess;
            if (n > 1)
            {
                excess = Math.Max(bit - _cap[jj], 0);
                _pulses[jj] = bit - excess;
                int den = (c * n) + ((c == 2 && n > 2 && !dualStereo && jj < intensity) ? 1 : 0);
                int nClogN = den * (CeltTables.LogN400[jj] + logM);
                int offset = (nClogN >> 1) - (den * FineOffset);
                if (n == 2)
                {
                    offset += den << Bitres >> 2;
                }

                if (_pulses[jj] + offset < den * 2 << Bitres)
                {
                    offset += nClogN >> 2;
                }
                else if (_pulses[jj] + offset < den * 3 << Bitres)
                {
                    offset += nClogN >> 3;
                }

                _fineQuant[jj] = Math.Max(0, _pulses[jj] + offset + (den << (Bitres - 1)));
                _fineQuant[jj] = CeltUdiv(_fineQuant[jj], den) >> Bitres;
                if (c * _fineQuant[jj] > (_pulses[jj] >> Bitres))
                {
                    _fineQuant[jj] = _pulses[jj] >> (stereo ? 1 : 0) >> Bitres;
                }

                _fineQuant[jj] = Math.Min(_fineQuant[jj], MaxFineBits);
                _finePriority[jj] = _fineQuant[jj] * (den << Bitres) >= _pulses[jj] + offset ? 1 : 0;
                _pulses[jj] -= c * _fineQuant[jj] << Bitres;
            }
            else
            {
                excess = Math.Max(0, bit - (c << Bitres));
                _pulses[jj] = bit - excess;
                _fineQuant[jj] = 0;
                _finePriority[jj] = 1;
            }

            if (excess > 0)
            {
                int extraFine = Math.Min(excess >> ((stereo ? 1 : 0) + Bitres), MaxFineBits - _fineQuant[jj]);
                _fineQuant[jj] += extraFine;
                int extraBits = extraFine * c << Bitres;
                _finePriority[jj] = extraBits >= excess - balance ? 1 : 0;
                excess -= extraBits;
            }

            balance = excess;
        }

        balanceOut = balance;
        for (; jj < end; jj++)
        {
            _fineQuant[jj] = _pulses[jj] >> (stereo ? 1 : 0) >> Bitres;
            _pulses[jj] = 0;
            _finePriority[jj] = _fineQuant[jj] < 1 ? 1 : 0;
        }

        return codedBands;
    }

    // quant_all_bands (bands.c), decoding side: what its integers decide. The float work (folding, rotations, the stereo mix) is the decoder's.
    private void QuantAllBands(ref RangeDecoder dec, IntWriter w, int start, int end, int shortBlocksM, int spread, bool dualStereo, int intensity, int totalBits, int balance,
        int lm, int codedBands, int c, int m)
    {
        int b0 = shortBlocksM != 0 ? m : 1;
        int normOffset = m * EBands[start];
        int lowbandOffset = 0;
        bool updateLowband = true;
        _intensity = intensity;
        for (int i = start; i < end; i++)
        {
            _band = i;
            int n = (m * EBands[i + 1]) - (m * EBands[i]);
            int tell = (int)dec.TellFrac();
            if (i != start)
            {
                balance -= tell;
            }

            int remainingBits = totalBits - tell - 1;
            _remainingBits = remainingBits;
            int b;
            if (i <= codedBands - 1)
            {
                int currBalance = CeltSudiv(balance, Math.Min(3, codedBands - i));
                b = Math.Max(0, Math.Min(16383, Math.Min(remainingBits + 1, _pulses[i] + currBalance)));
            }
            else
            {
                b = 0;
            }

            if (((m * EBands[i]) - n >= m * EBands[start] || i == start + 1) && (updateLowband || lowbandOffset == 0))
            {
                lowbandOffset = i;
            }

            _tfChange = _tfRes[i];
            int effectiveLowband = -1;
            uint xCm;
            uint yCm;
            if (lowbandOffset != 0 && (spread != SpreadAggressive || b0 > 1 || _tfChange < 0))
            {
                effectiveLowband = Math.Max(0, (m * EBands[lowbandOffset]) - normOffset - n);
                int foldStart = lowbandOffset;
                while (m * EBands[--foldStart] > effectiveLowband + normOffset)
                {
                }

                int foldEnd = lowbandOffset - 1;
                while (++foldEnd < i && m * EBands[foldEnd] < effectiveLowband + normOffset + n)
                {
                }

                xCm = 0;
                yCm = 0;
                for (int foldI = foldStart; foldI < foldEnd; foldI++)
                {
                    xCm |= _collapse[(foldI * c) + 0];
                    yCm |= _collapse[(foldI * c) + c - 1];
                }
            }
            else
            {
                xCm = yCm = (1u << b0) - 1;
            }

            w.Add(effectiveLowband);
            bool hasLowband = effectiveLowband != -1;
            if (dualStereo && i == intensity)
            {
                dualStereo = false;
            }

            if (dualStereo)
            {
                xCm = QuantBand(ref dec, w, n, b / 2, b0, hasLowband, lm, xCm);
                yCm = QuantBand(ref dec, w, n, b / 2, b0, hasLowband, lm, yCm);
            }
            else if (c == 2)
            {
                xCm = QuantBandStereo(ref dec, w, n, b, b0, hasLowband, lm, xCm | yCm);
                yCm = xCm;
            }
            else
            {
                xCm = QuantBand(ref dec, w, n, b, b0, hasLowband, lm, xCm | yCm);
                yCm = xCm;
            }

            _collapse[(i * c) + 0] = (byte)xCm;
            _collapse[(i * c) + c - 1] = (byte)yCm;
            balance += _pulses[i] + tell;
            updateLowband = b > (n << Bitres);
        }
    }

    // quant_band_n1 (bands.c): the sign of a band of one coefficient, for each channel.
    private uint QuantBandN1(ref RangeDecoder dec, IntWriter w, bool stereo)
    {
        for (int ch = 0; ch < (stereo ? 2 : 1); ch++)
        {
            int sign = 0;
            if (_remainingBits >= 1 << Bitres)
            {
                sign = (int)dec.Bits(1);
                _remainingBits -= 1 << Bitres;
            }

            w.Add(sign);
        }

        return 1;
    }

    private uint QuantBand(ref RangeDecoder dec, IntWriter w, int n, int b, int bBlocks, bool hasLowband, int lm, uint fill)
    {
        int nB = n;
        int b0;
        int timeDivide = 0;
        int recombine = 0;
        int tfChange = _tfChange;
        nB = CeltUdiv(nB, bBlocks);
        if (n == 1)
        {
            return QuantBandN1(ref dec, w, stereo: false);
        }

        if (tfChange > 0)
        {
            recombine = tfChange;
        }

        for (int k = 0; k < recombine; k++)
        {
            fill = (uint)(BitInterleaveTable[fill & 0xF] | (BitInterleaveTable[fill >> 4] << 2));
        }

        bBlocks >>= recombine;
        nB <<= recombine;
        while ((nB & 1) == 0 && tfChange < 0)
        {
            fill |= fill << bBlocks;
            bBlocks <<= 1;
            nB >>= 1;
            timeDivide++;
            tfChange++;
        }

        b0 = bBlocks;
        uint cm = QuantPartition(ref dec, w, n, b, bBlocks, hasLowband, lm, fill);
        bBlocks = b0;
        for (int k = 0; k < timeDivide; k++)
        {
            bBlocks >>= 1;
            cm |= cm >> bBlocks;
        }

        for (int k = 0; k < recombine; k++)
        {
            cm = BitDeinterleaveTable[cm & 0xF];
        }

        bBlocks <<= recombine;
        cm &= (1u << bBlocks) - 1;
        return cm;
    }

    private uint QuantBandStereo(ref RangeDecoder dec, IntWriter w, int n, int b, int bBlocks, bool hasLowband, int lm, uint fill)
    {
        if (n == 1)
        {
            return QuantBandN1(ref dec, w, stereo: true);
        }

        uint origFill = fill;
        ComputeTheta(ref dec, n, ref b, bBlocks, bBlocks, lm, stereo: true, ref fill, out int inv, out int imid, out int iside, out int delta, out int itheta, out int qalloc);
        _ = imid;
        _ = iside;
        w.Add(itheta);
        w.Add(inv);
        uint cm;
        if (n == 2)
        {
            int mbits = b;
            int sbits = 0;
            if (itheta != 0 && itheta != 16384)
            {
                sbits = 1 << Bitres;
            }

            mbits -= sbits;
            _remainingBits -= qalloc + sbits;
            int sign = 0;
            if (sbits != 0)
            {
                sign = (int)dec.Bits(1);
            }

            w.Add(sign);
            cm = QuantBand(ref dec, w, n, mbits, bBlocks, hasLowband, lm, origFill);
        }
        else
        {
            int mbits = Math.Max(0, Math.Min(b, (b - delta) / 2));
            int sbits = b - mbits;
            _remainingBits -= qalloc;
            int rebalance = _remainingBits;
            bool midFirst = mbits >= sbits;
            w.Add(midFirst ? 1 : 0);
            if (midFirst)
            {
                cm = QuantBand(ref dec, w, n, mbits, bBlocks, hasLowband, lm, fill);
                rebalance = mbits - (rebalance - _remainingBits);
                if (rebalance > 3 << Bitres && itheta != 0)
                {
                    sbits += rebalance - (3 << Bitres);
                }

                cm |= QuantBand(ref dec, w, n, sbits, bBlocks, false, lm, fill >> bBlocks);
            }
            else
            {
                cm = QuantBand(ref dec, w, n, sbits, bBlocks, false, lm, fill >> bBlocks);
                rebalance = sbits - (rebalance - _remainingBits);
                if (rebalance > 3 << Bitres && itheta != 16384)
                {
                    mbits += rebalance - (3 << Bitres);
                }

                cm |= QuantBand(ref dec, w, n, mbits, bBlocks, hasLowband, lm, fill);
            }
        }

        return cm;
    }

    private uint QuantPartition(ref RangeDecoder dec, IntWriter w, int n, int b, int bBlocks, bool hasLowband, int lm, uint fill)
    {
        int i = _band;
        int b0 = bBlocks;
        int cacheAt = CeltTables.CacheIndex50[((lm + 1) * Bands) + i];
        ReadOnlySpan<byte> cache = CeltTables.CacheBits50;
        uint cm;
        if (lm != -1 && b > cache[cacheAt + cache[cacheAt]] + 12 && n > 2)
        {
            n >>= 1;
            lm -= 1;
            if (bBlocks == 1)
            {
                fill = (fill & 1) | (fill << 1);
            }

            bBlocks = (bBlocks + 1) >> 1;
            ComputeTheta(ref dec, n, ref b, bBlocks, b0, lm, stereo: false, ref fill, out _, out _, out _, out int delta, out int itheta, out int qalloc);
            if (b0 > 1 && (itheta & 0x3fff) != 0)
            {
                if (itheta > 8192)
                {
                    delta -= delta >> (4 - lm);
                }
                else
                {
                    delta = Math.Min(0, delta + (n << Bitres >> (5 - lm)));
                }
            }

            int mbits = Math.Max(0, Math.Min(b, (b - delta) / 2));
            int sbits = b - mbits;
            _remainingBits -= qalloc;
            int rebalance = _remainingBits;
            bool midFirst = mbits >= sbits;
            w.Add(1);
            w.Add(itheta);
            w.Add(midFirst ? 1 : 0);
            if (midFirst)
            {
                cm = QuantPartition(ref dec, w, n, mbits, bBlocks, hasLowband, lm, fill);
                rebalance = mbits - (rebalance - _remainingBits);
                if (rebalance > 3 << Bitres && itheta != 0)
                {
                    sbits += rebalance - (3 << Bitres);
                }

                cm |= QuantPartition(ref dec, w, n, sbits, bBlocks, hasLowband, lm, fill >> bBlocks) << (b0 >> 1);
            }
            else
            {
                cm = QuantPartition(ref dec, w, n, sbits, bBlocks, hasLowband, lm, fill >> bBlocks) << (b0 >> 1);
                rebalance = sbits - (rebalance - _remainingBits);
                if (rebalance > 3 << Bitres && itheta != 16384)
                {
                    mbits += rebalance - (3 << Bitres);
                }

                cm |= QuantPartition(ref dec, w, n, mbits, bBlocks, hasLowband, lm, fill);
            }
        }
        else
        {
            int q = Bits2Pulses(i, lm, b);
            int currBits = Pulses2Bits(i, lm, q);
            _remainingBits -= currBits;
            while (_remainingBits < 0 && q > 0)
            {
                _remainingBits += currBits;
                q--;
                currBits = Pulses2Bits(i, lm, q);
                _remainingBits -= currBits;
            }

            if (q != 0)
            {
                int k = GetPulses(q);
                w.Add(0);
                w.Add(k);
                cm = DecodePulses(ref dec, w, n, k, bBlocks);
            }
            else
            {
                uint cmMask = (1u << bBlocks) - 1;
                fill &= cmMask;
                w.Add(0);
                w.Add(0);
                if (fill == 0)
                {
                    cm = 0;
                    w.Add(0);
                }
                else if (!hasLowband)
                {
                    cm = cmMask;
                    w.Add(1);
                }
                else
                {
                    cm = fill;
                    w.Add(2);
                }
            }
        }

        return cm;
    }

    // compute_theta (bands.c), decoding side.
    private void ComputeTheta(ref RangeDecoder dec, int n, ref int b, int bBlocks, int b0, int lm, bool stereo, ref uint fill, out int inv, out int imid, out int iside, out int delta,
        out int itheta, out int qalloc)
    {
        int i = _band;
        int pulseCap = CeltTables.LogN400[i] + (lm * (1 << Bitres));
        int offset = (pulseCap >> 1) - (stereo && n == 2 ? QThetaOffsetTwoPhase : QThetaOffset);
        int qn = ComputeQn(n, b, offset, pulseCap, stereo);
        if (stereo && i >= _intensity)
        {
            qn = 1;
        }

        itheta = 0;
        inv = 0;
        int tell = (int)dec.TellFrac();
        if (qn != 1)
        {
            if (stereo && n > 2)
            {
                int p0 = 3;
                int x;
                int x0 = qn / 2;
                int ft = (p0 * (x0 + 1)) + x0;
                int fs = (int)dec.Decode((uint)ft);
                if (fs < (x0 + 1) * p0)
                {
                    x = fs / p0;
                }
                else
                {
                    x = x0 + 1 + (fs - ((x0 + 1) * p0));
                }

                dec.Update((uint)(x <= x0 ? p0 * x : x - 1 - x0 + ((x0 + 1) * p0)), (uint)(x <= x0 ? p0 * (x + 1) : x - x0 + ((x0 + 1) * p0)), (uint)ft);
                itheta = x;
            }
            else if (b0 > 1 || stereo)
            {
                itheta = (int)dec.UInt((uint)(qn + 1));
            }
            else
            {
                int half = qn >> 1;
                int ft = (half + 1) * (half + 1);
                int fm = (int)dec.Decode((uint)ft);
                int fs;
                int fl;
                if (fm < (half * (half + 1) >> 1))
                {
                    itheta = (int)((Isqrt32((8 * (uint)fm) + 1) - 1) >> 1);
                    fs = itheta + 1;
                    fl = itheta * (itheta + 1) >> 1;
                }
                else
                {
                    itheta = (int)(((2 * (qn + 1)) - (int)Isqrt32((8 * (uint)(ft - fm - 1)) + 1)) >> 1);
                    fs = qn + 1 - itheta;
                    fl = ft - ((qn + 1 - itheta) * (qn + 2 - itheta) >> 1);
                }

                dec.Update((uint)fl, (uint)(fl + fs), (uint)ft);
            }

            itheta = CeltUdiv(itheta * 16384, qn);
        }
        else if (stereo)
        {
            if (b > 2 << Bitres && _remainingBits > 2 << Bitres)
            {
                inv = dec.BitLogP(2);
            }
            else
            {
                inv = 0;
            }

            if (_disableInv)
            {
                inv = 0;
            }

            itheta = 0;
        }

        qalloc = (int)dec.TellFrac() - tell;
        b -= qalloc;
        if (itheta == 0)
        {
            imid = 32767;
            iside = 0;
            fill &= (1u << bBlocks) - 1;
            delta = -16384;
        }
        else if (itheta == 16384)
        {
            imid = 0;
            iside = 32767;
            fill &= ((1u << bBlocks) - 1) << bBlocks;
            delta = 16384;
        }
        else
        {
            imid = BitexactCos(itheta);
            iside = BitexactCos(16384 - itheta);
            delta = FracMul16((n - 1) << 7, BitexactLog2Tan(iside, imid));
        }

    }

    private static int ComputeQn(int n, int b, int offset, int pulseCap, bool stereo)
    {
        int n2 = (2 * n) - 1;
        if (stereo && n == 2)
        {
            n2--;
        }

        int qb = CeltSudiv(b + (n2 * offset), n2);
        qb = Math.Min(b - pulseCap - (4 << Bitres), qb);
        qb = Math.Min(8 << Bitres, qb);
        int qn;
        if (qb < (1 << Bitres >> 1))
        {
            qn = 1;
        }
        else
        {
            qn = Exp2Table8[qb & 0x7] >> (14 - (qb >> Bitres));
            qn = (qn + 1) >> 1 << 1;
        }

        return qn;
    }

    // decode_pulses and cwrsi (cwrs.c): the pulse vector of a band, stored in index order; the return is the collapse mask (extract_collapse_mask, vq.c).
    private uint DecodePulses(ref RangeDecoder dec, IntWriter w, int n, int k, int bBlocks)
    {
        uint index = dec.UInt(PvqV(n, k));
        Span<int> y = _pulseScratch.AsSpan(0, n);
        Cwrsi(n, k, index, y);
        for (int j = 0; j < n; j++)
        {
            w.Add(y[j]);
        }

        if (bBlocks <= 1)
        {
            return 1;
        }

        int n0 = CeltUdiv(n, bBlocks);
        uint mask = 0;
        for (int i = 0; i < bBlocks; i++)
        {
            uint tmp = 0;
            for (int j = 0; j < n0; j++)
            {
                tmp |= (uint)y[(i * n0) + j];
            }

            mask |= (tmp != 0 ? 1u : 0u) << i;
        }

        return mask;
    }

    private static readonly int[] PvqRows = [0, 176, 351, 525, 698, 870, 1041, 1131, 1178, 1207, 1226, 1240, 1248, 1254, 1257];

    private static uint PvqU(int n, int k) => CeltTables.PvqUData[PvqRows[Math.Min(n, k)] + Math.Max(n, k)];

    private static uint PvqV(int n, int k) => unchecked(PvqU(n, k) + PvqU(n, k + 1));

    private static uint PvqRow(int row, int column) => CeltTables.PvqUData[PvqRows[row] + column];

    private static void Cwrsi(int n, int k, uint index, Span<int> y)
    {
        int at = 0;
        uint p;
        int s;
        int k0;
        while (n > 2)
        {
            uint q;
            if (k >= n)
            {
                int row = PvqRows[n];
                p = CeltTables.PvqUData[row + k + 1];
                s = index >= p ? -1 : 0;
                index -= p & (uint)s;
                k0 = k;
                q = CeltTables.PvqUData[row + n];
                if (q > index)
                {
                    k = n;
                    do
                    {
                        p = PvqRow(--k, n);
                    }
                    while (p > index);
                }
                else
                {
                    for (p = CeltTables.PvqUData[row + k]; p > index; p = CeltTables.PvqUData[row + k])
                    {
                        k--;
                    }
                }

                index -= p;
                y[at++] = (k0 - k + s) ^ s;
            }
            else
            {
                p = PvqRow(k, n);
                q = PvqRow(k + 1, n);
                if (p <= index && index < q)
                {
                    index -= p;
                    y[at++] = 0;
                }
                else
                {
                    s = index >= q ? -1 : 0;
                    index -= q & (uint)s;
                    k0 = k;
                    do
                    {
                        p = PvqRow(--k, n);
                    }
                    while (p > index);
                    index -= p;
                    y[at++] = (k0 - k + s) ^ s;
                }
            }

            n--;
        }

        p = (uint)((2 * k) + 1);
        s = index >= p ? -1 : 0;
        index -= p & (uint)s;
        k0 = k;
        k = (int)((index + 1) >> 1);
        if (k != 0)
        {
            index -= (uint)((2 * k) - 1);
        }

        y[at++] = (k0 - k + s) ^ s;
        s = -(int)index;
        y[at] = (k + s) ^ s;
    }
}
