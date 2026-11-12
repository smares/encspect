using System.Numerics;

namespace Encspect.Formats.Opus;

// The range decoder of RFC 6716, section 4.1 (entdec.c and entcode.c of libopus 1.5.2, BSD-3-Clause, notice in THIRD-PARTY-NOTICES.md). The entropy pass (SILK and CELT syntax)
// reads every symbol through it; the decoder reads none. The state after a frame, Rng, is the final range coder state that a conforming decoder shares with the encoder
// (RFC 6716, section 6).
internal ref struct RangeDecoder
{
    private const int SymBits = 8;
    private const int CodeBits = 32;
    private const uint SymMax = (1u << SymBits) - 1;
    private const uint CodeTop = 1u << (CodeBits - 1);
    private const uint CodeBot = CodeTop >> SymBits;
    private const int CodeExtra = ((CodeBits - 2) % SymBits) + 1;
    private const int UintBits = 8;
    private const int WindowSize = 32;

    private readonly ReadOnlySpan<byte> _buffer;
    private uint _endOffset;
    private uint _endWindow;
    private int _endBits;
    private uint _offset;
    private uint _value;
    private uint _extent;
    private int _remainder;

    public RangeDecoder(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        Storage = (uint)buffer.Length;
        _endOffset = 0;
        _endWindow = 0;
        _endBits = 0;
        BitsTotal = CodeBits + 1 - (((CodeBits - CodeExtra) / SymBits) * SymBits);
        _offset = 0;
        Rng = 1u << CodeExtra;
        _remainder = ReadByte();
        _value = Rng - 1 - (uint)(_remainder >> (SymBits - CodeExtra));
        HasError = false;
        Normalize();
    }

    // The bytes the decoder may read; a hybrid packet shrinks it by the bytes of a redundant frame (opus_decoder.c, "dec.storage -= redundancy_bytes").
    public uint Storage { get; set; }

    // The range, whose final value is the decoder's final range coder state.
    public uint Rng { get; private set; }

    public int BitsTotal { get; private set; }

    public bool HasError { get; private set; }

    private int ReadByte() => _offset < Storage && _offset < (uint)_buffer.Length ? _buffer[(int)_offset++] : 0;

    private int ReadByteFromEnd()
    {
        if (_endOffset < Storage && Storage - (_endOffset + 1) < (uint)_buffer.Length)
        {
            _endOffset++;
            return _buffer[(int)(Storage - _endOffset)];
        }

        return 0;
    }

    private void Normalize()
    {
        while (Rng <= CodeBot)
        {
            BitsTotal += SymBits;
            Rng <<= SymBits;
            int sym = _remainder;
            _remainder = ReadByte();
            sym = ((sym << SymBits) | _remainder) >> (SymBits - CodeExtra);
            _value = ((_value << SymBits) + (SymMax & ~(uint)sym)) & (CodeTop - 1);
        }
    }

    private static int Ilog(uint value) => 32 - BitOperations.LeadingZeroCount(value);

    // The bits used so far, rounded up (ec_tell).
    public int Tell() => BitsTotal - Ilog(Rng);

    // The bits used so far in eighths of a bit (ec_tell_frac, the portable version).
    public uint TellFrac()
    {
        uint bits = (uint)BitsTotal << 3;
        int l = Ilog(Rng);
        uint r = Rng >> (l - 16);
        for (int i = 3; i-- > 0;)
        {
            r = (r * r) >> 15;
            int b = (int)(r >> 16);
            l = (l << 1) | b;
            r >>= b;
        }

        return bits - (uint)l;
    }

    // Claims the bits of a frame that ended with the symbol "silence" (celt_decoder.c: dec->nbits_total += tell - ec_tell).
    public void AddBits(int bits) => BitsTotal += bits;

    public uint Decode(uint total)
    {
        _extent = Rng / total;
        uint s = _value / _extent;
        return total - Math.Min(s + 1, total);
    }

    public uint DecodeBin(int bits)
    {
        _extent = Rng >> bits;
        uint s = _value / _extent;
        return (1u << bits) - Math.Min(s + 1u, 1u << bits);
    }

    public void Update(uint low, uint high, uint total)
    {
        uint s = unchecked(_extent * (total - high));
        _value -= s;
        Rng = low > 0 ? unchecked(_extent * (high - low)) : Rng - s;
        Normalize();
    }

    public int BitLogP(int logp)
    {
        uint r = Rng;
        uint d = _value;
        uint s = r >> logp;
        int ret = d < s ? 1 : 0;
        if (ret == 0)
        {
            _value = d - s;
        }

        Rng = ret != 0 ? s : r - s;
        Normalize();
        return ret;
    }

    public int ICdf(scoped ReadOnlySpan<byte> icdf, int ftb)
    {
        uint s = Rng;
        uint d = _value;
        uint r = s >> ftb;
        int ret = -1;
        uint t;
        do
        {
            t = s;
            s = unchecked(r * icdf[++ret]);
        }
        while (d < s);
        _value = d - s;
        Rng = t - s;
        Normalize();
        return ret;
    }

    public uint UInt(uint total)
    {
        total--;
        int ftb = Ilog(total);
        if (ftb > UintBits)
        {
            ftb -= UintBits;
            uint ft = (total >> ftb) + 1;
            uint s = Decode(ft);
            Update(s, s + 1, ft);
            uint t = (s << ftb) | Bits(ftb);
            if (t <= total)
            {
                return t;
            }

            HasError = true;
            return total;
        }
        else
        {
            total++;
            uint s = Decode(total);
            Update(s, s + 1, total);
            return s;
        }
    }

    public uint Bits(int bits)
    {
        uint window = _endWindow;
        int available = _endBits;
        if ((uint)available < (uint)bits)
        {
            do
            {
                window |= (uint)ReadByteFromEnd() << available;
                available += SymBits;
            }
            while (available <= WindowSize - SymBits);
        }

        uint ret = window & ((1u << bits) - 1u);
        window >>= bits;
        available -= bits;
        _endWindow = window;
        _endBits = available;
        BitsTotal += bits;
        return ret;
    }

    // ec_laplace_decode (laplace.c): a signed integer with the probability of 0 and the decay a band's model states.
    public int Laplace(uint fs, int decay)
    {
        const int minP = 1;
        const int nMin = 16;
        int val = 0;
        uint fm = DecodeBin(15);
        uint fl = 0;
        if (fm >= fs)
        {
            val++;
            fl = fs;
            fs = (((32768 - (minP * 2 * nMin) - fs) * (uint)(16384 - decay)) >> 15) + minP;
            while (fs > minP && fm >= fl + (2 * fs))
            {
                fs *= 2;
                fl += fs;
                fs = (uint)(((fs - (2 * minP)) * (long)decay) >> 15);
                fs += minP;
                val++;
            }

            if (fs <= minP)
            {
                int di = (int)((fm - fl) >> 1);
                val += di;
                fl += (uint)(2 * di * minP);
            }

            if (fm < fl + fs)
            {
                val = -val;
            }
            else
            {
                fl += fs;
            }
        }

        Update(fl, Math.Min(fl + fs, 32768), 32768);
        return val;
    }
}
