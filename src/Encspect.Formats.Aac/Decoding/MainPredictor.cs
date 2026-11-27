using System.Runtime.CompilerServices;

namespace Encspect.Formats.Aac.Decoding;

// The bank of backward-adaptive predictors of AAC Main, one per spectral line up to Table 62's limit (ISO/IEC 13818-7 clause 13): a second-order lattice predictor adapted by
// LMS from the reconstructed values of the two frames before. The specification fixes the arithmetic (13.3.2.2 to 13.3.2.4): every operation in IEEE single precision with round
// to nearest even, the six state values of a predictor stored truncated to their upper 16 bits, the estimate rounded to a 7-bit mantissa by its own half-up function, and b / VAR
// from two tables built by its procedure. Plain float operations, which neither .NET nor the hardware fuse, reproduce it on every platform.
internal sealed class MainPredictor
{
    // The adaptation constant alpha (13.3.2.1) and the attenuation factors a and b: all exact in a float.
    private const float Alpha = 0.90625f;
    private const float Attenuation = 0.953125f;

    // A reconstructed value beyond this takes it, so that the squares and the sums of the states stay finite whatever the stream codes: the largest dequantized value is about 2^56
    // (an escape of 8,191 at scalefactor 255), and no stream that follows the specification reaches this.
    private const float ValueLimit = 1.152921504606846976e18f;

    // Below this a state is zero (docs/design/lossy-decoders.md, "Determinism"): the exponent table makes b / VAR zero below VAR 2, so what it drops is lost in the estimate's
    // 7-bit rounding.
    private const float FlushLimit = 1e-30f;

    private static readonly float[] Mantissas = new float[128];
    private static readonly float[] Exponents = new float[256];

    private readonly float[] _state;
    private readonly int _lines;

    static MainPredictor()
    {
        float one = BitConverter.Int32BitsToSingle(0x3F800000);
        for (int i = 0; i < 128; i++)
        {
            float divisor = BitConverter.Int32BitsToSingle(0x3F800000 + (i << 16));
            Mantissas[i] = RoundEven(Attenuation / divisor);
        }

        for (int i = 0; i < 256; i++)
        {
            float value = BitConverter.Int32BitsToSingle(i << 23);
            Exponents[i] = value > one ? one / value : 0f;
        }
    }

    public MainPredictor(int lines)
    {
        _lines = lines;
        _state = new float[lines * 6];
        ResetAll();
    }

    public int Lines => _lines;

    // The state of a predictor at the start of decoding (13.3.3): r0 = r1 = 0, COR1 = COR2 = 0, VAR1 = VAR2 = 1.
    public void ResetAll()
    {
        for (int i = 0; i < _lines; i++)
        {
            Initialize(i);
        }
    }

    // The predictors of one reset group (Table 63): group 1 holds the lines 0, 30, 60 and so on.
    public void ResetGroup(int group)
    {
        for (int i = group - 1; i < _lines; i += 30)
        {
            Initialize(i);
        }
    }

    public void Flush()
    {
        float[] state = _state;
        for (int i = 0; i < state.Length; i++)
        {
            if (Math.Abs(state[i]) < FlushLimit)
            {
                state[i] = 0;
            }
        }
    }

    private void Initialize(int line)
    {
        int at = line * 6;
        _state[at] = 0;
        _state[at + 1] = 0;
        _state[at + 2] = 0;
        _state[at + 3] = 0;
        _state[at + 4] = 1;
        _state[at + 5] = 1;
    }

    // One block of one channel: modes holds per line 0 for a line that takes its transmitted value, 1 for a line whose prediction is used (13.3.2.1: the estimate is added to the
    // transmitted prediction error) and 2 for a line of noise substitution (4.6.13.4: the predictor is reset and updated with a value of zero, and the spectrum keeps the noise).
    // Every predictor runs whatever its mode, so that its coefficients follow the signal.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Process(Span<float> spectrum, ReadOnlySpan<byte> modes)
    {
        float[] state = _state;
        float[] mantissas = Mantissas;
        float[] exponents = Exponents;
        for (int i = 0; i < _lines; i++)
        {
            int at = i * 6;
            float r0 = state[at];
            float r1 = state[at + 1];
            float cor1 = state[at + 2];
            float cor2 = state[at + 3];
            float var1 = state[at + 4];
            float var2 = state[at + 5];
            byte mode = modes[i];
            float x;
            if (mode == 2)
            {
                r0 = 0;
                r1 = 0;
                cor1 = 0;
                cor2 = 0;
                var1 = 1;
                var2 = 1;
                x = 0;
            }
            else
            {
                x = spectrum[i];
            }

            int bits1 = BitConverter.SingleToInt32Bits(var1);
            int bits2 = BitConverter.SingleToInt32Bits(var2);
            float k1 = cor1 * (mantissas[(bits1 >> 16) & 0x7F] * exponents[(bits1 >> 23) & 0xFF]);
            float k2 = cor2 * (mantissas[(bits2 >> 16) & 0x7F] * exponents[(bits2 >> 23) & 0xFF]);
            float estimate1 = k1 * r0;
            float estimate2 = k2 * r1;
            if (mode == 1)
            {
                float reconstructed = RoundInfinity(estimate1 + estimate2) + x;
                x = Math.Abs(reconstructed) <= ValueLimit ? reconstructed : reconstructed > 0 ? ValueLimit : reconstructed < 0 ? -ValueLimit : 0;
                spectrum[i] = x;
            }

            float e1 = x - estimate1;
            state[at] = Truncate(Attenuation * x);
            state[at + 1] = Truncate(Attenuation * (r0 - (k1 * x)));
            state[at + 2] = Truncate((Alpha * cor1) + (r0 * x));
            state[at + 3] = Truncate((Alpha * cor2) + (r1 * e1));
            state[at + 4] = Truncate((Alpha * var1) + (0.5f * ((r0 * r0) + (x * x))));
            state[at + 5] = Truncate((Alpha * var2) + (0.5f * ((r1 * r1) + (e1 * e1))));
        }
    }

    // The upper 16 bits of a float's storage word (13.3.2.2).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Truncate(float value) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(value) & unchecked((int)0xFFFF0000));

    // flt_round_inf of 13.3.2.3: the 7-bit mantissa, rounded half away from zero, by the specification's float arithmetic.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float RoundInfinity(float value)
    {
        int bits = BitConverter.SingleToInt32Bits(value);
        float truncated = BitConverter.Int32BitsToSingle(bits & unchecked((int)0xFFFF0000));
        if ((bits & 0x8000) == 0)
        {
            return truncated;
        }

        int exponent = bits & unchecked((int)0xFF800000);
        float one = BitConverter.Int32BitsToSingle(exponent);
        float next = BitConverter.Int32BitsToSingle(exponent | 0x10000);
        return truncated + next - one;
    }

    // flt_round_even of 13.3.2.4, for the table of b / VAR: the value to 8 significant bits, with the specification's own code (its second correction cannot fire, so ties go up;
    // no b / VAR of the table is a tie).
    private static float RoundEven(float value)
    {
        int exponent = ((BitConverter.SingleToInt32Bits(value) >> 23) & 0xFF) - 126;
        float scale = 1 << (8 - exponent);
        float scaled = value * scale;
        int a = (int)scaled;
        if (scaled - a >= 0.5f)
        {
            a++;
        }

        if (scaled - a == 0.5f)
        {
            a &= -2;
        }

        return a / scale;
    }
}
