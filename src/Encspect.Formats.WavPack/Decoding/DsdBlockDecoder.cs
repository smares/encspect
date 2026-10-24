// Derived from libwavpack 5.6.0 (unpack_dsd.c), Copyright (c) 1998 - 2022 David Bryant, under the BSD 3-clause license.
// The license text is in THIRD-PARTY-NOTICES.md.

using Encspect.Core;

namespace Encspect.Formats.WavPack.Decoding;

// The decoding of one block of DSD audio (libwavpack 5.6.0 unpack_dsd.c): bytes of eight 1-bit samples, stored as they are,
// coded with a range coder over the probabilities of the byte that follows the previous bytes of the channel ("fast"), or
// with a range coder over bits whose probabilities adaptive filters predict ("high"). The block's CRC is worked out over
// the bytes. A block whose tables or data the decoder cannot use is muted to the byte 0x55, the silence of DSD.
internal sealed class DsdBlockDecoder
{
    private const int MaxHistoryBits = 5;
    private const int MaxBytesPerBin = 1280;
    private const int Precision = 20;
    private const int PrecisionUsed = 12;
    private const int TableBins = 256;
    private const int Up = 0x010000FE;
    private const int Down = 0x00010000;
    private const int Decay = 8;
    private const int ValueOne = 1 << Precision;
    private const int RateS = 20;

    private int[] _samples = [];
    private readonly byte[] _probabilities = new byte[(1 << MaxHistoryBits) * 256];
    private readonly ushort[] _summed = new ushort[(1 << MaxHistoryBits) * 256];
    private readonly byte[] _lookup = new byte[(1 << MaxHistoryBits) * MaxBytesPerBin];
    private readonly int[] _lookupStart = new int[1 << MaxHistoryBits];
    private readonly int[] _table = new int[TableBins];
    private readonly Filters[] _filters = [new(), new()];

    private sealed class Filters
    {
        public int Value;
        public int Filter0;
        public int Filter1;
        public int Filter2;
        public int Filter3;
        public int Filter4;
        public int Filter5;
        public int Filter6;
        public int Factor;
        public uint Byte;
    }

    private int _at;
    private int _bins;
    private uint _value;

    // One byte of eight samples per channel, interleaved for two channels.
    public ReadOnlySpan<int> Bytes => _samples;

    // The block's CRC (unpack_dsd_samples works it out over the bytes) and whether the block was muted.
    public (bool IsMuted, uint Crc) Decode(ReadOnlySpan<byte> block, in BlockHeader header, BlockContents contents, int count, CancellationToken cancellation)
    {
        bool isMonoData = (header.Flags & (BlockHeader.MonoFlag | BlockHeader.FalseStereoFlag)) != 0;
        int total = checked(count * (isMonoData ? 1 : 2));
        if (_samples.Length < total)
        {
            _samples = new int[total];
        }

        Span<int> output = _samples.AsSpan(0, total);
        ReadOnlySpan<byte> data = block.Slice(contents.Dsd.Start, contents.Dsd.Length);
        int mode = data[1];
        uint crc = uint.MaxValue;
        bool isOk;
        Prepare(data, isMonoData);
        if (mode == 0)
        {
            ReadOnlySpan<byte> stored = data[2..];
            int available = Math.Min(total, stored.Length);
            for (int i = 0; i < available; i++)
            {
                output[i] = stored[i];
                crc = unchecked(crc + (crc << 1) + stored[i]);
            }

            isOk = true;
        }
        else if (mode == 1)
        {
            isOk = RunFast(data, isMonoData, output, ref crc, cancellation);
        }
        else
        {
            isOk = RunHigh(data, isMonoData, output, ref crc, cancellation);
        }

        if (!isOk)
        {
            output.Fill(0x55);
        }

        return (!isOk, crc);
    }

    // What libwavpack's init_dsd_block does for the block's mode: reads the tables of the fast mode and the filters of the high mode,
    // and refuses what it cannot take. The mode and the size of the stored mode were checked when the sub-blocks were read.
    public void Prepare(ReadOnlySpan<byte> data, bool isMono)
    {
        switch (data[1])
        {
            case 1:
                PrepareFast(data);
                break;
            case 3:
                PrepareHigh(data, isMono);
                break;
        }
    }

    // init_dsd_block_fast. The bytes are coded with the cumulative probabilities of the byte that comes after the previous byte
    // or bytes of the channel (the history), in a range coder of 32 bits.
    private void PrepareFast(ReadOnlySpan<byte> data)
    {
        int at = 2;
        int end = data.Length;
        if (at == end)
        {
            throw new InvalidDataException("The DSD block in the fast mode ends before its history bits");
        }

        int historyBits = data[at++];
        if (at == end || historyBits > MaxHistoryBits)
        {
            throw new InvalidDataException("The DSD block in the fast mode has no probabilities or more than 5 history bits");
        }

        int bins = 1 << historyBits;
        int maxProbability = data[at++];
        Span<byte> probabilities = _probabilities.AsSpan(0, bins * 256);
        probabilities.Clear();
        if (maxProbability < 0xFF)
        {
            int outAt = 0;
            while (outAt < probabilities.Length && at < end)
            {
                int code = data[at++];
                if (code > maxProbability)
                {
                    int zeros = code - maxProbability;
                    while (outAt < probabilities.Length && zeros-- > 0)
                    {
                        probabilities[outAt++] = 0;
                    }
                }
                else if (code != 0)
                {
                    probabilities[outAt++] = (byte)code;
                }
                else
                {
                    break;
                }
            }

            if (outAt < probabilities.Length || (at < end && data[at++] != 0))
            {
                throw new InvalidDataException("The probabilities of the DSD block in the fast mode are not valid");
            }
        }
        else if (end - at > probabilities.Length)
        {
            data.Slice(at, probabilities.Length).CopyTo(probabilities);
            at += probabilities.Length;
        }
        else
        {
            throw new InvalidDataException("The probabilities of the DSD block in the fast mode are not valid");
        }

        int totalSummed = 0;
        int lookupAt = 0;
        for (int bin = 0; bin < bins; bin++)
        {
            int sum = 0;
            for (int i = 0; i < 256; i++)
            {
                sum += probabilities[(bin * 256) + i];
                _summed[(bin * 256) + i] = (ushort)sum;
            }

            _lookupStart[bin] = -1;
            if (sum != 0)
            {
                totalSummed += sum;
                if (totalSummed > bins * MaxBytesPerBin)
                {
                    throw new InvalidDataException("The probabilities of the DSD block in the fast mode sum to more than the decoder's tables hold");
                }

                _lookupStart[bin] = lookupAt;
                for (int i = 0; i < 256; i++)
                {
                    for (int c = probabilities[(bin * 256) + i]; c-- > 0;)
                    {
                        _lookup[lookupAt++] = (byte)i;
                    }
                }
            }
        }

        if (end - at < 4)
        {
            throw new InvalidDataException("The DSD block in the fast mode ends before its range coder starts");
        }

        uint value = 0;
        for (int i = 0; i < 4; i++)
        {
            value = (value << 8) | data[at++];
        }

        (_at, _bins, _value) = (at, bins, value);
    }

    // decode_fast.
    private bool RunFast(ReadOnlySpan<byte> data, bool isMono, Span<int> output, ref uint crc, CancellationToken cancellation)
    {
        int at = _at;
        int end = data.Length;
        int bins = _bins;
        uint value = _value;
        Span<byte> probabilities = _probabilities.AsSpan(0, bins * 256);
        uint low = 0;
        uint high = 0xFFFFFFFF;
        int p0 = 0;
        int p1 = 0;
        for (int i = 0; i < output.Length; i++)
        {
            if ((i & 0xFFF) == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            uint total = _summed[(p0 * 256) + 255];
            if (total == 0)
            {
                return false;
            }

            uint mult = (high - low) / total;
            if (mult == 0)
            {
                if (end - at >= 4)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        value = (value << 8) | data[at++];
                    }
                }

                low = 0;
                high = 0xFFFFFFFF;
                mult = high / total;
                if (mult == 0)
                {
                    return false;
                }
            }

            uint index = (value - low) / mult;
            if (index >= total)
            {
                return false;
            }

            int code = _lookup[_lookupStart[p0] + (int)index];
            output[i] = code;
            if (code != 0)
            {
                low += _summed[(p0 * 256) + code - 1] * mult;
            }

            high = low + (probabilities[(p0 * 256) + code] * mult) - 1;
            crc = unchecked(crc + (crc << 1) + (uint)code);
            if (isMono)
            {
                p0 = code & (bins - 1);
            }
            else
            {
                p0 = p1;
                p1 = code & (bins - 1);
            }

            while (((high ^ low) & 0xFF000000) == 0 && at < end)
            {
                value = (value << 8) | data[at++];
                high = (high << 8) | 0xFF;
                low <<= 8;
            }
        }

        return true;
    }

    // init_dsd_block_high and init_ptable.
    private void PrepareHigh(ReadOnlySpan<byte> data, bool isMono)
    {
        int at = 2;
        int end = data.Length;
        int channels = isMono ? 1 : 2;
        if (end - at < (isMono ? 13 : 20))
        {
            throw new InvalidDataException("The DSD block in the high mode is shorter than its tables");
        }

        int rateI = data[at++];
        int rateS = data[at++];
        if (rateS != RateS)
        {
            throw new InvalidDataException($"The DSD block in the high mode has a rate step of {rateS}, not {RateS}");
        }

        InitTable(_table, rateI, rateS);
        for (int channel = 0; channel < channels; channel++)
        {
            Filters f = _filters[channel];
            f.Filter1 = data[at++] << (Precision - 8);
            f.Filter2 = data[at++] << (Precision - 8);
            f.Filter3 = data[at++] << (Precision - 8);
            f.Filter4 = data[at++] << (Precision - 8);
            f.Filter5 = data[at++] << (Precision - 8);
            f.Filter6 = 0;
            f.Filter0 = 0;
            f.Value = 0;
            f.Byte = 0;
            int factor = data[at] | ((data[at + 1] << 8) & 0xFF00);
            at += 2;
            f.Factor = (int)((uint)factor << 16) >> 16;
        }

        uint value = 0;
        for (int i = 0; i < 4; i++)
        {
            value = (value << 8) | data[at++];
        }

        (_at, _value) = (at, value);
    }

    // decode_high.
    private bool RunHigh(ReadOnlySpan<byte> data, bool isMono, Span<int> output, ref uint crc, CancellationToken cancellation)
    {
        int at = _at;
        int end = data.Length;
        uint value = _value;
        uint high = 0xFFFFFFFF;
        uint low = 0;
        int[] table = _table;
        int channels = isMono ? 1 : 2;
        Filters s0 = _filters[0];
        Filters s1 = _filters[1];
        int samples = output.Length / channels;
        int outAt = 0;
        for (int n = 0; n < samples; n++)
        {
            if ((n & 0xFFF) == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            s0.Value = s0.Filter1 - s0.Filter5 + ((s0.Filter6 * s0.Factor) >> 2);
            if (!isMono)
            {
                s1.Value = s1.Filter1 - s1.Filter5 + ((s1.Filter6 * s1.Factor) >> 2);
            }

            for (int bit = 0; bit < 8; bit++)
            {
                DecodeBit(s0, table, ref low, ref high, ref value, data, ref at, end);
                if (!isMono)
                {
                    DecodeBit(s1, table, ref low, ref high, ref value, data, ref at, end);
                }
            }

            output[outAt] = (int)(s0.Byte & 0xFF);
            crc = unchecked(crc + (crc << 1) + (s0.Byte & 0xFF));
            outAt++;
            s0.Factor -= (s0.Factor + 512) >> 10;
            if (!isMono)
            {
                output[outAt] = (int)(s1.Byte & 0xFF);
                crc = unchecked(crc + (crc << 1) + (s1.Byte & 0xFF));
                outAt++;
                s1.Factor -= (s1.Factor + 512) >> 10;
            }
        }

        return true;
    }

    // One bit of one channel: the range coder decides it with the probability the channel's filters predict, and the filters adapt.
    private static void DecodeBit(Filters s, int[] table, ref uint low, ref uint high, ref uint value, ReadOnlySpan<byte> data, ref int at, int end)
    {
        ref int p = ref table[(s.Value >> (Precision - PrecisionUsed)) & (TableBins - 1)];
        uint split = unchecked(low + (((high - low) >> 8) * (uint)(p >> 16)));
        if (value <= split)
        {
            high = split;
            p += (Up - p) >> Decay;
            s.Filter0 = -1;
        }
        else
        {
            low = split + 1;
            p += (Down - p) >> Decay;
            s.Filter0 = 0;
        }

        while (((high ^ low) & 0xFF000000) == 0 && at < end)
        {
            value = (value << 8) | data[at++];
            high = (high << 8) | 0xFF;
            low <<= 8;
        }

        s.Value += s.Filter6 * 8;
        s.Byte = (s.Byte << 1) | (uint)(s.Filter0 & 1);
        s.Factor += (((s.Value ^ s.Filter0) >> 31) | 1) & ((s.Value ^ (s.Value - (s.Filter6 * 16))) >> 31);
        s.Filter1 += ((s.Filter0 & ValueOne) - s.Filter1) >> 6;
        s.Filter2 += ((s.Filter0 & ValueOne) - s.Filter2) >> 4;
        s.Filter3 += (s.Filter2 - s.Filter3) >> 4;
        s.Filter4 += (s.Filter3 - s.Filter4) >> 4;
        s.Value = (s.Filter4 - s.Filter5) >> 4;
        s.Filter5 += s.Value;
        s.Filter6 += (s.Value - s.Filter6) >> 3;
        s.Value = s.Filter1 - s.Filter5 + ((s.Filter6 * s.Factor) >> 2);
    }

    // init_ptable. The loops that move a value towards DOWN by a shifted fraction of the distance stop moving it once
    // the fraction is 0, so they end after a few thousand steps whatever count the rate asks for.
    private static void InitTable(int[] table, int rateI, int rateS)
    {
        int value = 0x808000;
        int rate = rateI << 8;
        value = Approach(value, (rate + 128) >> 8);
        for (int i = 0; i < TableBins / 2; i++)
        {
            table[i] = value;
            table[TableBins - 1 - i] = 0x100FFFF - value;
            if (value > 0x010000)
            {
                rate += (rate * rateS + 128) >> 8;
                value = Approach(value, (rate + 64) >> 7);
            }
        }
    }

    private static int Approach(int value, int count)
    {
        for (int c = count; c-- > 0;)
        {
            int step = (Down - value) >> Decay;
            if (step == 0)
            {
                break;
            }

            value += step;
        }

        return value;
    }
}
