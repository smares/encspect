// Follows the Monkey's Audio SDK 13.44 (NewPredictor.cpp, Old/NewPredictorOld.cpp, ScaledFirstOrderFilter.h, RollBuffer.h, Interim.cpp), 3-clause BSD license:
// Copyright 2000-2026 Matthew T. Ashland. See THIRD-PARTY-NOTICES.md.
namespace Encspect.Formats.MonkeysAudio.Decoding;

// The filters a compression level runs after the range decoder, longest first as the decoder meets them: orders and shifts of the neural network
// filters (NewPredictor.cpp, the constructors of CPredictorDecompress3950toCurrent).
internal static class Levels
{
    public static (int Order, int Shift)[] Filters(int level) => level switch
    {
        1000 => [],
        2000 => [(16, 11)],
        3000 => [(64, 11)],
        4000 => [(256, 13), (32, 10)],
        5000 => [(1024 + 256, 15), (256, 13), (16, 11)],
        _ => throw new InvalidDataException($"The compression level {level} is none of 1000 to 5000"),
    };

    // The taps of all neural network filters of a level, the work of the filters for one sample of one channel.
    public static int Taps(int level)
    {
        int taps = 0;
        foreach ((int order, _) in Filters(level))
        {
            taps += order;
        }

        return taps;
    }
}

// The predictor of one channel of samples up to 24 bits (CPredictorDecompress3950toCurrent<int, short>): the neural network filters, then a first
// order adaptive filter over the value, a second over the other channel's, and a fixed first-order filter. isInterim is the mode for 24-bit files
// from 2019 to 2022 (Interim.h): the neural network filters round differently, and the first stage sums its products in 64 bits for samples of more
// than 16 bits (CPredictorDecompressInterim::DecompressValue, Interim.cpp).
internal sealed class Predictor32
{
    private const int Window = 256;
    private const int History = 8;

    private readonly NnFilter16[] _filters;
    private readonly bool _isWideFirstStage;
    private readonly bool _isNewDelta;
    private readonly int[] _predictionA = new int[Window + History];
    private readonly int[] _predictionB = new int[Window + History];
    private readonly int[] _adaptA = new int[Window + History];
    private readonly int[] _adaptB = new int[Window + History];
    private readonly int[] _ma = new int[8];
    private readonly int[] _mb = new int[8];
    private int _current;
    private int _index;
    private int _lastValueA;
    private int _stageA;
    private int _stageB;

    public Predictor32(int level, bool isInterim, int bitsPerSample, bool isNewDelta)
    {
        _isNewDelta = isNewDelta;
        _isWideFirstStage = isInterim && bitsPerSample > 16;
        (int Order, int Shift)[] filters = Levels.Filters(level);
        _filters = new NnFilter16[filters.Length];
        for (int i = 0; i < filters.Length; i++)
        {
            _filters[i] = new NnFilter16(filters[i].Order, filters[i].Shift, isInterim);
        }

        Flush();
    }

    public void Flush()
    {
        foreach (NnFilter16 filter in _filters)
        {
            filter.Flush();
        }

        Array.Clear(_ma);
        Array.Clear(_mb);
        Array.Clear(_predictionA, 0, History + 1);
        Array.Clear(_predictionB, 0, History + 1);
        Array.Clear(_adaptA, 0, History + 1);
        Array.Clear(_adaptB, 0, History + 1);
        _ma[0] = 360;
        _ma[1] = 317;
        _ma[2] = -109;
        _ma[3] = 98;
        _stageA = 0;
        _stageB = 0;
        _lastValueA = 0;
        _current = History;
        _index = 0;
    }

    public int Decompress(long inputA, long inputB)
    {
        if (_index == Window)
        {
            Array.Copy(_predictionA, _current - History, _predictionA, 0, History);
            Array.Copy(_predictionB, _current - History, _predictionB, 0, History);
            Array.Copy(_adaptA, _current - History, _adaptA, 0, History);
            Array.Copy(_adaptB, _current - History, _adaptB, 0, History);
            _current = History;
            _index = 0;
        }

        int a = (int)inputA;
        int b = (int)inputB;
        for (int i = _filters.Length - 1; i >= 0; i--)
        {
            a = _filters[i].Decompress(a, _isNewDelta);
        }

        int c = _current;
        int[] pa = _predictionA;
        int[] pb = _predictionB;
        pa[c] = _lastValueA;
        pa[c - 1] = pa[c] - pa[c - 1];
        pb[c] = b - ((_stageB * 31) >> 5);
        _stageB = b;
        pb[c - 1] = pb[c] - pb[c - 1];

        int currentA;
        if (_isWideFirstStage)
        {
            long wideA = ((long)pa[c] * _ma[0]) + ((long)pa[c - 1] * _ma[1]) + ((long)pa[c - 2] * _ma[2]) + ((long)pa[c - 3] * _ma[3]);
            long wideB = ((long)pb[c] * _mb[0]) + ((long)pb[c - 1] * _mb[1]) + ((long)pb[c - 2] * _mb[2]) + ((long)pb[c - 3] * _mb[3]) + ((long)pb[c - 4] * _mb[4]);
            currentA = a + (int)((wideA + (wideB >> 1)) >> 10);
        }
        else
        {
            int predictionA = (pa[c] * _ma[0]) + (pa[c - 1] * _ma[1]) + (pa[c - 2] * _ma[2]) + (pa[c - 3] * _ma[3]);
            int predictionB = (pb[c] * _mb[0]) + (pb[c - 1] * _mb[1]) + (pb[c - 2] * _mb[2]) + (pb[c - 3] * _mb[3]) + (pb[c - 4] * _mb[4]);
            currentA = a + ((predictionA + (predictionB >> 1)) >> 10);
        }

        _adaptA[c] = pa[c] != 0 ? ((pa[c] >> 30) & 2) - 1 : 0;
        _adaptA[c - 1] = pa[c - 1] != 0 ? ((pa[c - 1] >> 30) & 2) - 1 : 0;
        _adaptB[c] = pb[c] != 0 ? ((pb[c] >> 30) & 2) - 1 : 0;
        _adaptB[c - 1] = pb[c - 1] != 0 ? ((pb[c - 1] >> 30) & 2) - 1 : 0;

        int direction = (a < 0 ? 1 : 0) - (a > 0 ? 1 : 0);
        int[] aa = _adaptA;
        int[] ab = _adaptB;
        _ma[0] += aa[c] * direction;
        _ma[1] += aa[c - 1] * direction;
        _ma[2] += aa[c - 2] * direction;
        _ma[3] += aa[c - 3] * direction;
        _mb[0] += ab[c] * direction;
        _mb[1] += ab[c - 1] * direction;
        _mb[2] += ab[c - 2] * direction;
        _mb[3] += ab[c - 3] * direction;
        _mb[4] += ab[c - 4] * direction;

        _stageA = currentA + ((_stageA * 31) >> 5);
        _lastValueA = currentA;
        _current++;
        _index++;
        return _stageA;
    }
}

// The same for samples of 32 bits (CPredictorDecompress3950toCurrent<int64, int>), whose sums are 64 bits wide.
internal sealed class Predictor64
{
    private const int Window = 256;
    private const int History = 8;

    private readonly NnFilter32[] _filters;
    private readonly long[] _predictionA = new long[Window + History];
    private readonly long[] _predictionB = new long[Window + History];
    private readonly long[] _adaptA = new long[Window + History];
    private readonly long[] _adaptB = new long[Window + History];
    private readonly long[] _ma = new long[8];
    private readonly long[] _mb = new long[8];
    private int _current;
    private int _index;
    private long _lastValueA;
    private int _stageA;
    private int _stageB;

    public Predictor64(int level)
    {
        (int Order, int Shift)[] filters = Levels.Filters(level);
        _filters = new NnFilter32[filters.Length];
        for (int i = 0; i < filters.Length; i++)
        {
            _filters[i] = new NnFilter32(filters[i].Order, filters[i].Shift, false);
        }

        Flush();
    }

    public void Flush()
    {
        foreach (NnFilter32 filter in _filters)
        {
            filter.Flush();
        }

        Array.Clear(_ma);
        Array.Clear(_mb);
        Array.Clear(_predictionA, 0, History + 1);
        Array.Clear(_predictionB, 0, History + 1);
        Array.Clear(_adaptA, 0, History + 1);
        Array.Clear(_adaptB, 0, History + 1);
        _ma[0] = 360;
        _ma[1] = 317;
        _ma[2] = -109;
        _ma[3] = 98;
        _stageA = 0;
        _stageB = 0;
        _lastValueA = 0;
        _current = History;
        _index = 0;
    }

    public int Decompress(long inputA, long inputB)
    {
        if (_index == Window)
        {
            Array.Copy(_predictionA, _current - History, _predictionA, 0, History);
            Array.Copy(_predictionB, _current - History, _predictionB, 0, History);
            Array.Copy(_adaptA, _current - History, _adaptA, 0, History);
            Array.Copy(_adaptB, _current - History, _adaptB, 0, History);
            _current = History;
            _index = 0;
        }

        long a = inputA;
        int b = (int)inputB;
        for (int i = _filters.Length - 1; i >= 0; i--)
        {
            a = _filters[i].Decompress(a, true);
        }

        int c = _current;
        long[] pa = _predictionA;
        long[] pb = _predictionB;
        pa[c] = _lastValueA;
        pa[c - 1] = pa[c] - pa[c - 1];
        pb[c] = b - (((long)_stageB * 31) >> 5);
        _stageB = b;
        pb[c - 1] = pb[c] - pb[c - 1];

        long predictionA = (pa[c] * _ma[0]) + (pa[c - 1] * _ma[1]) + (pa[c - 2] * _ma[2]) + (pa[c - 3] * _ma[3]);
        long predictionB = (pb[c] * _mb[0]) + (pb[c - 1] * _mb[1]) + (pb[c - 2] * _mb[2]) + (pb[c - 3] * _mb[3]) + (pb[c - 4] * _mb[4]);
        long currentA = a + ((predictionA + (predictionB >> 1)) >> 10);

        _adaptA[c] = pa[c] != 0 ? ((pa[c] >> 30) & 2) - 1 : 0;
        _adaptA[c - 1] = pa[c - 1] != 0 ? ((pa[c - 1] >> 30) & 2) - 1 : 0;
        _adaptB[c] = pb[c] != 0 ? ((pb[c] >> 30) & 2) - 1 : 0;
        _adaptB[c - 1] = pb[c - 1] != 0 ? ((pb[c - 1] >> 30) & 2) - 1 : 0;

        long direction = (a < 0 ? 1 : 0) - (a > 0 ? 1 : 0);
        long[] aa = _adaptA;
        long[] ab = _adaptB;
        _ma[0] += aa[c] * direction;
        _ma[1] += aa[c - 1] * direction;
        _ma[2] += aa[c - 2] * direction;
        _ma[3] += aa[c - 3] * direction;
        _mb[0] += ab[c] * direction;
        _mb[1] += ab[c - 1] * direction;
        _mb[2] += ab[c - 2] * direction;
        _mb[3] += ab[c - 3] * direction;
        _mb[4] += ab[c - 4] * direction;

        _stageA = (int)(currentA + (((long)_stageA * 31) >> 5));
        _lastValueA = currentA;
        _current++;
        _index++;
        return _stageA;
    }
}

// The predictor of files from 3.93 to 3.94 (CPredictorDecompress3930to3950): the neural network filters, then one adaptive filter of four taps over
// the last values, without the other channel's values and the first-order filters of later versions.
internal sealed class Predictor3930
{
    private const int Window = 256;
    private const int History = 8;

    private readonly NnFilter16[] _filters;
    private readonly int[] _buffer = new int[Window + History];
    private readonly int[] _m = new int[8];
    private int _at;
    private int _index;
    private int _lastValue;

    public Predictor3930(int level)
    {
        // Extra high is the highest level the encoders of these versions had (NewPredictorOld.cpp: anything above throws).
        if (level > 4000)
        {
            throw new InvalidDataException($"The compression level {level} did not exist in files of version 3.93 and 3.94");
        }

        (int Order, int Shift)[] filters = Levels.Filters(level);
        _filters = new NnFilter16[filters.Length];
        for (int i = 0; i < filters.Length; i++)
        {
            _filters[i] = new NnFilter16(filters[i].Order, filters[i].Shift, false);
        }

        Flush();
    }

    public void Flush()
    {
        foreach (NnFilter16 filter in _filters)
        {
            filter.Flush();
        }

        Array.Clear(_buffer, 0, History + 1);
        Array.Clear(_m);
        _m[0] = 360;
        _m[1] = 317;
        _m[2] = -109;
        _m[3] = 98;
        _at = History;
        _lastValue = 0;
        _index = 0;
    }

    public int Decompress(long input)
    {
        if (_index == Window)
        {
            Array.Copy(_buffer, Window, _buffer, 0, History);
            _at = History;
            _index = 0;
        }

        int value = (int)input;
        for (int i = _filters.Length - 1; i >= 0; i--)
        {
            value = _filters[i].Decompress(value, false);
        }

        int[] b = _buffer;
        int at = _at;
        int p1 = b[at - 1];
        int p2 = b[at - 1] - b[at - 2];
        int p3 = b[at - 2] - b[at - 3];
        int p4 = b[at - 3] - b[at - 4];
        b[at] = value + (((p1 * _m[0]) + (p2 * _m[1]) + (p3 * _m[2]) + (p4 * _m[3])) >> 9);

        int direction = (value < 0 ? 1 : 0) - (value > 0 ? 1 : 0);
        _m[0] += (((p1 >> 30) & 2) - 1) * direction;
        _m[1] += (((p2 >> 30) & 2) - 1) * direction;
        _m[2] += (((p3 >> 30) & 2) - 1) * direction;
        _m[3] += (((p4 >> 30) & 2) - 1) * direction;

        int result = b[at] + ((_lastValue * 31) >> 5);
        _lastValue = result;
        _index++;
        _at++;
        return result;
    }
}
