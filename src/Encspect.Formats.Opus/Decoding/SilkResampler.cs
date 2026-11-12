using static Encspect.Formats.Opus.SilkMath;

namespace Encspect.Formats.Opus.Decoding;

// The resampler that takes SILK's output from its internal rate (8, 12 or 16 kHz) to 48 kHz (silk/resampler.c, resampler_private_IIR_FIR.c, resampler_private_up2_HQ.c and
// resampler_rom.c of libopus 1.5.2, BSD-3-Clause, notice in THIRD-PARTY-NOTICES.md): a 2x upsampler of two all-pass chains, then an interpolating FIR with 12 phases. Integer
// code, exact in every port.
internal sealed class Resampler
{
    private const int OrderFir12 = 8;
    private const int MaxBatchMs = 10;

    private readonly int[] _sIir = new int[6];
    private readonly short[] _sFir = new short[OrderFir12];
    private readonly short[] _delayBuf = new short[48];
    private int _batchSize;
    private int _invRatioQ16;
    private int _fsInKHz;
    private int _fsOutKHz;
    private int _inputDelay;

    public void Clear()
    {
        Array.Clear(_sIir);
        Array.Clear(_sFir);
        Array.Clear(_delayBuf);
        _batchSize = _invRatioQ16 = _fsInKHz = _fsOutKHz = _inputDelay = 0;
    }

    public void CopyFrom(Resampler other)
    {
        Array.Copy(other._sIir, _sIir, _sIir.Length);
        Array.Copy(other._sFir, _sFir, _sFir.Length);
        Array.Copy(other._delayBuf, _delayBuf, _delayBuf.Length);
        _batchSize = other._batchSize;
        _invRatioQ16 = other._invRatioQ16;
        _fsInKHz = other._fsInKHz;
        _fsOutKHz = other._fsOutKHz;
        _inputDelay = other._inputDelay;
    }

    // silk_resampler_init for the decoder, whose output rate is 48 kHz: no rate of SILK is half of it, so the interpolating path with the 2x upsampler always runs.
    public void Init(int fsHzIn, int fsHzOut)
    {
        Clear();
        _inputDelay = fsHzIn switch { 8000 => 0, 12000 => 4, _ => 7 };
        _fsInKHz = fsHzIn / 1000;
        _fsOutKHz = fsHzOut / 1000;
        _batchSize = _fsInKHz * MaxBatchMs;
        const int up2x = 1;
        _invRatioQ16 = ((fsHzIn << (14 + up2x)) / fsHzOut) << 2;
        while (Smulww(_invRatioQ16, fsHzOut) < (fsHzIn << up2x))
        {
            _invRatioQ16++;
        }
    }

    // silk_resampler: inLen samples (at least one millisecond) from input at inAt to inLen * 48 / fsIn at outAt.
    public void Process(short[] output, int outAt, short[] input, int inAt, int inLen)
    {
        int nSamples = _fsInKHz - _inputDelay;
        Array.Copy(input, inAt, _delayBuf, _inputDelay, nSamples);
        IirFir(output, outAt, _delayBuf, 0, _fsInKHz);
        IirFir(output, outAt + _fsOutKHz, input, inAt + nSamples, inLen - _fsInKHz);
        Array.Copy(input, inAt + inLen - _inputDelay, _delayBuf, 0, _inputDelay);
    }

    private void IirFir(short[] output, int outAt, short[] input, int inAt, int inLen)
    {
        short[] buf = new short[(2 * _batchSize) + OrderFir12];
        Array.Copy(_sFir, buf, OrderFir12);
        int indexIncrementQ16 = _invRatioQ16;
        int nSamplesIn;
        while (true)
        {
            nSamplesIn = Math.Min(inLen, _batchSize);
            Up2Hq(buf, OrderFir12, input, inAt, nSamplesIn);
            int maxIndexQ16 = nSamplesIn << (16 + 1);
            outAt = Interpol(output, outAt, buf, maxIndexQ16, indexIncrementQ16);
            inAt += nSamplesIn;
            inLen -= nSamplesIn;
            if (inLen > 0)
            {
                Array.Copy(buf, nSamplesIn << 1, buf, 0, OrderFir12);
            }
            else
            {
                break;
            }
        }

        Array.Copy(buf, nSamplesIn << 1, _sFir, 0, OrderFir12);
    }

    private static int Interpol(short[] output, int outAt, short[] buf, int maxIndexQ16, int indexIncrementQ16)
    {
        ReadOnlySpan<short> table = SilkTables.ResamplerFracFir12;
        for (int indexQ16 = 0; indexQ16 < maxIndexQ16; indexQ16 += indexIncrementQ16)
        {
            int tableIndex = Smulwb(indexQ16 & 0xFFFF, 12);
            int p = indexQ16 >> 16;
            int resQ15 = Smulbb(buf[p], table[(tableIndex * 4) + 0]);
            resQ15 = Smlabb(resQ15, buf[p + 1], table[(tableIndex * 4) + 1]);
            resQ15 = Smlabb(resQ15, buf[p + 2], table[(tableIndex * 4) + 2]);
            resQ15 = Smlabb(resQ15, buf[p + 3], table[(tableIndex * 4) + 3]);
            resQ15 = Smlabb(resQ15, buf[p + 4], table[((11 - tableIndex) * 4) + 3]);
            resQ15 = Smlabb(resQ15, buf[p + 5], table[((11 - tableIndex) * 4) + 2]);
            resQ15 = Smlabb(resQ15, buf[p + 6], table[((11 - tableIndex) * 4) + 1]);
            resQ15 = Smlabb(resQ15, buf[p + 7], table[((11 - tableIndex) * 4) + 0]);
            output[outAt++] = (short)Sat16(RshiftRound(resQ15, 15));
        }

        return outAt;
    }

    // silk_resampler_private_up2_HQ.
    private void Up2Hq(short[] output, int outAt, short[] input, int inAt, int len)
    {
        int[] s = _sIir;
        for (int k = 0; k < len; k++)
        {
            int in32 = input[inAt + k] << 10;
            int y = in32 - s[0];
            int x = Smulwb(y, 1746);
            int out321 = s[0] + x;
            s[0] = in32 + x;
            y = out321 - s[1];
            x = Smulwb(y, 14986);
            int out322 = s[1] + x;
            s[1] = out321 + x;
            y = out322 - s[2];
            x = Smlawb(y, y, 39083 - 65536);
            out321 = s[2] + x;
            s[2] = out322 + x;
            output[outAt + (2 * k)] = (short)Sat16(RshiftRound(out321, 10));
            y = in32 - s[3];
            x = Smulwb(y, 6854);
            out321 = s[3] + x;
            s[3] = in32 + x;
            y = out321 - s[4];
            x = Smulwb(y, 25769);
            out322 = s[4] + x;
            s[4] = out321 + x;
            y = out322 - s[5];
            x = Smlawb(y, y, 55542 - 65536);
            out321 = s[5] + x;
            s[5] = out322 + x;
            output[outAt + (2 * k) + 1] = (short)Sat16(RshiftRound(out321, 10));
        }
    }
}
