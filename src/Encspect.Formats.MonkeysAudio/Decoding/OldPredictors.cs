// Follows the Monkey's Audio SDK 13.44 (Old/AntiPredictorOld.cpp, Old/AntiPredictorFastOld.cpp, Old/AntiPredictorNormalOld.cpp, Old/AntiPredictorHighOld.cpp,
// Old/AntiPredictorExtraHighOld.cpp), 3-clause BSD license:
// Copyright 2000-2026 Matthew T. Ashland. See THIRD-PARTY-NOTICES.md.
namespace Encspect.Formats.MonkeysAudio.Decoding;

// The predictors of files from version 3.80 to 3.92, which undo the prediction of a channel's whole array. They are resumable, so that the decoder charges its work
// and looks at the cancellation token between chunks: Begin starts an array, Run does the values [from, to) in order.
internal abstract class OldPredictor
{
    // Starts an array of count values: input holds them as the bit coding gave them, output is where the predicted values go (the same array for the fast level).
    public abstract void Begin(int[] input, int[] output, int count);

    public abstract void Run(int from, int to);

    // The predictor of a level (1000 fast to 4000 extra high) for files of the given version.
    public static OldPredictor Create(int level, int version) => level switch
    {
        1000 => new FastPredictor(),
        2000 => new NormalPredictor(),
        3000 => new HighPredictor(),
        4000 => new ExtraHighPredictor(version),
        _ => throw new InvalidDataException($"The compression level {level} did not exist in files before version 3.93, except 1000 to 4000"),
    };
}

// CAntiPredictorFast3320To3930: a stage of order 2 and then one of order 1, over the array in place.
internal sealed class FastPredictor : OldPredictor
{
    private int[] _input = [];
    private int[] _output = [];
    private int _count;
    private int _m;
    private int _ip2;
    private int _ip3;
    private int _op1;

    public override void Begin(int[] input, int[] output, int count)
    {
        _input = input;
        _output = output;
        _count = count;
        _m = 375;
        if (count >= 3)
        {
            _ip2 = input[1];
            _ip3 = input[0];
            _op1 = input[1];
            output[0] = input[0];
            output[1] = input[1];
        }
    }

    public override void Run(int from, int to)
    {
        int[] input = _input;
        int[] output = _output;
        if (_count < 3)
        {
            Array.Copy(input, from, output, from, to - from);
            return;
        }

        int m = _m;
        int ip2 = _ip2;
        int ip3 = _ip3;
        int op1 = _op1;
        for (int q = Math.Max(from, 2); q < to; q++)
        {
            int ip = input[q];
            int p = ip2 + ip2 - ip3;
            ip3 = ip2;
            ip2 = ip + ((p * m) >> 9);
            m += (ip ^ p) > 0 ? 1 : -1;
            op1 = output[q] = ip2 + op1;
        }

        _m = m;
        _ip2 = ip2;
        _ip3 = ip3;
        _op1 = op1;
    }
}

// The three stages every predictor from 3.80 on ends with (CAntiPredictorNormal3800To3929::AntiPredict), over an array whose first elements they take as they are.
internal sealed class ThreeStages
{
    private int _m2;
    private int _m3;
    private int _m4;
    private int _m5;
    private int _m6;
    private int _p2;
    private int _p3;
    private int _p4;
    private int _p7;
    private int _ipp2;
    private int _opp;
    private int _shift;

    // Starts after the first elements, which the output gets as sums of the array's. shift is the last stage's.
    public void Start(int[] input, int[] output, int first, int shift)
    {
        _shift = shift;
        _m2 = 64;
        _m3 = 115;
        _m4 = 64;
        _m5 = 740;
        _m6 = 0;
        _p4 = input[first - 1];
        _p3 = (input[first - 1] - input[first - 2]) << 1;
        _p2 = input[first - 1] + ((input[first - 3] - input[first - 2]) << 3);
        _ipp2 = input[first - 2];
        _p7 = (2 * input[first - 1]) - input[first - 2];
        _opp = input[first - 1];
        Array.Copy(input, output, first);
        for (int q = 1; q < first; q++)
        {
            output[q] += output[q - 1];
        }
    }

    // The stages on one value i: the first predicts from the last values and follows the sign of i, the second from the last outputs of the first and follows the sign of
    // the first's output, the last adds a share of the output before, previous.
    public int Step(int i, int previous)
    {
        int o = i + ((_p2 * _m2) + (_p3 * _m3) + (_p4 * _m4) >> 11);
        if (i > 0)
        {
            _m2 -= ((_p2 >> 30) & 2) - 1;
            _m3 -= ((_p3 >> 28) & 8) - 4;
            _m4 -= ((_p4 >> 28) & 8) - 4;
        }
        else if (i < 0)
        {
            _m2 += ((_p2 >> 30) & 2) - 1;
            _m3 += ((_p3 >> 28) & 8) - 4;
            _m4 += ((_p4 >> 28) & 8) - 4;
        }

        _p2 = o + ((_ipp2 - _p4) << 3);
        _p3 = (o - _p4) << 1;
        _ipp2 = _p4;
        _p4 = o;

        o += ((_p7 * _m5) - (_opp * _m6)) >> _shift;
        if (_p4 > 0)
        {
            _m5 -= ((_p7 >> 29) & 4) - 2;
            _m6 += ((_opp >> 30) & 2) - 1;
        }
        else if (_p4 < 0)
        {
            _m5 += ((_p7 >> 29) & 4) - 2;
            _m6 -= ((_opp >> 30) & 2) - 1;
        }

        _p7 = (2 * o) - _opp;
        _opp = o;
        return o + ((previous * 31) >> 5);
    }
}

// CAntiPredictorNormal3800To3929.
internal sealed class NormalPredictor : OldPredictor
{
    private const int First = 4;
    private const int Minimum = 8;

    private readonly ThreeStages _stages = new();
    private int[] _input = [];
    private int[] _output = [];
    private bool _isPredicted;

    public override void Begin(int[] input, int[] output, int count)
    {
        _input = input;
        _output = output;
        _isPredicted = count >= Minimum;
        if (_isPredicted)
        {
            _stages.Start(input, output, First, 10);
        }
    }

    public override void Run(int from, int to)
    {
        int[] input = _input;
        int[] output = _output;
        if (!_isPredicted)
        {
            Array.Copy(input, from, output, from, to - from);
            return;
        }

        for (int q = Math.Max(from, First); q < to; q++)
        {
            output[q] = _stages.Step(input[q], output[q - 1]);
        }
    }
}

// CAntiPredictorHigh3800To3929: a filter of 16 taps over the last values of the array in front of the stages.
internal sealed class HighPredictor : OldPredictor
{
    private const int Taps = 16;
    private const int Minimum = 20;

    private readonly ThreeStages _stages = new();
    private readonly int[] _weights = new int[Taps];
    private int[] _input = [];
    private int[] _output = [];
    private bool _isPredicted;

    public override void Begin(int[] input, int[] output, int count)
    {
        _input = input;
        _output = output;
        Array.Clear(_weights);
        _isPredicted = count >= Minimum;
        if (_isPredicted)
        {
            _stages.Start(input, output, Taps, 10);
        }
    }

    public override void Run(int from, int to)
    {
        int[] input = _input;
        int[] output = _output;
        if (!_isPredicted)
        {
            Array.Copy(input, from, output, from, to - from);
            return;
        }

        int[] weights = _weights;
        for (int q = Math.Max(from, Taps); q < to; q++)
        {
            int i = input[q];
            int dot = OldVectors.HighTaps(input.AsSpan(q - Taps, Taps), weights, Math.Sign(i));
            i -= dot >> 9;
            input[q] = i;
            output[q] = _stages.Step(i, output[q - 1]);
        }
    }
}

// CAntiPredictorExtraHigh3800To3929: a filter of 128 taps (256 from version 3.83) over the 16-bit values of the array in front of the stages, and from 3.83 a filter of
// 8 taps in front of that.
internal sealed class ExtraHighPredictor : OldPredictor
{
    private readonly bool _isShort;
    private readonly int _taps;
    private readonly int _minimum;
    private readonly int _shift;
    private readonly ThreeStages _stages = new();
    private readonly short[] _weights = new short[256];
    private readonly int[] _fm = new int[9];
    private readonly int[] _fp = new int[9];
    private short[] _values = [];
    private short[] _adapt = [];
    private int[] _input = [];
    private int[] _output = [];
    private bool _isPredicted;

    public ExtraHighPredictor(int version)
    {
        _isShort = version < 3830;
        _taps = _isShort ? 128 : 256;
        _minimum = _isShort ? 134 : 262;
        _shift = _isShort ? 11 : 12;
    }

    public override void Begin(int[] input, int[] output, int count)
    {
        _input = input;
        _output = output;
        Array.Clear(_weights);
        Array.Clear(_fm);
        Array.Clear(_fp);
        _isPredicted = count >= _minimum;
        if (!_isPredicted)
        {
            return;
        }

        _stages.Start(input, output, _taps, _isShort ? 10 : 11);
        if (_values.Length < count)
        {
            _values = new short[count];
            _adapt = new short[count];
        }

        for (int q = 0; q < _taps; q++)
        {
            _adapt[q] = (short)(((input[q] >> 30) & 2) - 1);
            _values[q] = (short)input[q];
        }
    }

    public override void Run(int from, int to)
    {
        int[] input = _input;
        int[] output = _output;
        if (!_isPredicted)
        {
            Array.Copy(input, from, output, from, to - from);
            return;
        }

        short[] values = _values;
        short[] adapt = _adapt;
        int taps = _taps;
        Span<short> weights = _weights.AsSpan(0, taps);
        for (int q = Math.Max(from, taps); q < to; q++)
        {
            int ip = input[q];
            if (!_isShort)
            {
                ip -= EightTaps(ip) >> 9;
            }

            int original = ip;
            int dot = NnVectors.Dot(values.AsSpan(q - taps, taps), weights);
            if (original != 0)
            {
                NnVectors.AddOrSubtract(weights, adapt.AsSpan(q - taps, taps), original < 0);
            }

            ip -= dot >> _shift;
            values[q] = (short)ip;
            adapt[q] = (short)(((ip >> 30) & 2) - 1);
            output[q] = _stages.Step(ip, output[q - 1]);
        }
    }

    // The filter of 8 taps over the last eight values the array gave: the sum over the history, which then takes the value and adapts to its sign.
    private int EightTaps(int value)
    {
        int[] fp = _fp;
        int[] fm = _fm;
        fp[0] = value;
        int dot = 0;
        if (value == 0)
        {
            for (int j = 8; j >= 1; j--)
            {
                dot += fp[j] * fm[j];
                fp[j] = fp[j - 1];
            }
        }
        else if (value > 0)
        {
            for (int j = 8; j >= 1; j--)
            {
                dot += fp[j] * fm[j];
                fm[j] += ((fp[j] >> 30) & 2) - 1;
                fp[j] = fp[j - 1];
            }
        }
        else
        {
            for (int j = 8; j >= 1; j--)
            {
                dot += fp[j] * fm[j];
                fm[j] -= ((fp[j] >> 30) & 2) - 1;
                fp[j] = fp[j - 1];
            }
        }

        return dot;
    }
}
