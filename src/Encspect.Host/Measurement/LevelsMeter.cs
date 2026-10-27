using System.Numerics;
using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Host.Measurement;

// The levels of one stream (docs/design/spectral-analysis.md, "Levels"): sample peak, RMS, DC offset, clipping runs, digital silence and
// bit-depth usage of each measured channel, kept as exact integers where the samples are integers. The sum of squares and the sum are
// 128-bit: a block's squares are added in a 64-bit partial that is folded into the UInt128 before it can overflow (32-bit samples square to 2^62,
// so the fold comes every few samples, and 24-bit samples every 2^18). Floats are summed in double in a fixed order, after the stream
// measurer has replaced non-finite samples and clamped huge ones. A run, a silent stretch and a leading stretch end at a gap. The figures depend only
// on the samples and their positions, never on how a decoder cuts blocks.
internal sealed class LevelsMeter
{
    // The most samples of a channel given to AddIntegers or AddDoubles at once; the scratch arrays are this long.
    public const int MaxPiece = 16384;

    private readonly ChannelState[] _channels;
    private readonly bool _isFloat;
    private readonly SampleFormat _format;
    private readonly int _bits;
    private readonly int _high;
    private readonly int _low;
    private readonly int _chunk;
    private SilenceState _joint;
    private int[] _any = [];
    private int[] _flags = [];

    public LevelsMeter(int channels, SampleFormat format, int bitsPerSample)
    {
        _channels = new ChannelState[channels];
        for (int c = 0; c < channels; c++)
        {
            _channels[c] = new ChannelState();
        }

        _format = format;
        _isFloat = format != SampleFormat.Integer;
        _bits = Math.Clamp(bitsPerSample, 1, 32);
        _high = _bits >= 32 ? int.MaxValue : (1 << (_bits - 1)) - 1;
        _low = _bits >= 32 ? int.MinValue : -(1 << (_bits - 1));

        // The most squares a 64-bit partial can hold: (2^(2 bits - 2)) per square, 2^64 - 1 in all. From 31 bits on the fold would come
        // every few samples, so each square goes into the 128-bit sum on its own.
        _chunk = _bits >= 31 ? 0 : (int)Math.Min((1L << Math.Min(66 - (2 * _bits), 40)) - 1, MaxPiece);
    }

    // The bytes the working state needs beyond the per-channel records, for the memory bound.
    public static long EstimateBytes(int channels, SampleFormat format, bool isMeasuringSpectrum)
    {
        long bytes = 256L * channels;
        if (channels > 1)
        {
            bytes += 4L * MaxPiece;
        }

        if (format != SampleFormat.Integer)
        {
            bytes += 4L * MaxPiece;
            if (!isMeasuringSpectrum)
            {
                bytes += 8L * MaxPiece;
            }
        }

        return bytes;
    }

    public void AddIntegers(int channel, ReadOnlySpan<int> samples)
    {
        ChannelState state = _channels[channel];
        Scan(samples, out int low, out int high, out int bits, out long sum);
        state.Count += samples.Length;
        state.Sum += sum;
        state.Or |= bits;
        long peak = Math.Max(high, -(long)low);
        if (peak > state.PeakMagnitude)
        {
            state.PeakMagnitude = peak;
        }

        state.SumSquares += Squares(samples);
        if (high >= _high || low <= _low)
        {
            ScanClipping(state, samples);
        }
        else
        {
            state.RunLength = 0;
            state.RunSign = 0;
        }

        state.Silence.Add(samples);
        Combine(channel, samples);
    }

    // Samples already made safe by the stream measurer: finite and at most 1,024 times full scale.
    public void AddDoubles(int channel, ReadOnlySpan<double> samples)
    {
        if (_flags.Length < samples.Length)
        {
            _flags = new int[Math.Max(MaxPiece, samples.Length)];
        }

        ChannelState state = _channels[channel];
        Span<int> flags = _flags.AsSpan(0, samples.Length);
        double low = state.Low;
        double high = state.High;
        double sum = state.SumDouble;
        double squares = state.SumSquaresDouble;
        bool only16 = state.Only16;
        bool only24 = state.Only24;
        long runLength = state.RunLength;
        int runSign = state.RunSign;
        for (int i = 0; i < samples.Length; i++)
        {
            double v = samples[i];
            low = v < low ? v : low;
            high = v > high ? v : high;
            sum += v;
            squares += v * v;
            flags[i] = v != 0 ? 1 : 0;
            if (only24)
            {
                double y = v * 8388608.0;
                if (y != Math.Floor(y) || y < -8388608.0 || y > 8388607.0)
                {
                    only24 = false;
                    only16 = false;
                }
                else if (only16)
                {
                    double z = v * 32768.0;
                    only16 = z == Math.Floor(z) && z >= -32768.0 && z <= 32767.0;
                }
            }

            int sign = v >= 1.0 ? 1 : v <= -1.0 ? -1 : 0;
            if (sign != 0 && sign == runSign)
            {
                runLength++;
            }
            else
            {
                runSign = sign;
                runLength = sign != 0 ? 1 : 0;
            }

            if (runLength >= MeasurementLimits.MinimumClippingRun)
            {
                if (runLength == MeasurementLimits.MinimumClippingRun)
                {
                    state.Runs++;
                    state.Clipped += MeasurementLimits.MinimumClippingRun;
                }
                else
                {
                    state.Clipped++;
                }

                if (runLength > state.Longest)
                {
                    state.Longest = runLength;
                }
            }
        }

        state.Count += samples.Length;
        state.Low = low;
        state.High = high;
        state.SumDouble = sum;
        state.SumSquaresDouble = squares;
        state.Only16 = only16;
        state.Only24 = only24;
        state.RunLength = runLength;
        state.RunSign = runSign;
        state.Silence.Add(flags);
        Combine(channel, flags);
    }

    // Called after every measured channel has had the same samples added: the sample frames in which all of them are 0.
    public void EndPiece(int length)
    {
        if (_channels.Length > 1)
        {
            _joint.Add(_any.AsSpan(0, length));
        }
    }

    // A gap in the positions: no run and no silent stretch goes across it.
    public void Gap()
    {
        foreach (ChannelState state in _channels)
        {
            state.RunLength = 0;
            state.RunSign = 0;
            state.Silence.Gap();
        }

        _joint.Gap();
    }

    private void Combine(int channel, ReadOnlySpan<int> values)
    {
        if (_channels.Length == 1)
        {
            return;
        }

        if (_any.Length < values.Length)
        {
            _any = new int[Math.Max(MaxPiece, values.Length)];
        }

        if (channel == 0)
        {
            values.CopyTo(_any);
            return;
        }

        for (int i = 0; i < values.Length; i++)
        {
            _any[i] |= values[i];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Scan(ReadOnlySpan<int> samples, out int low, out int high, out int bits, out long sum)
    {
        int lo = int.MaxValue;
        int hi = int.MinValue;
        int or = 0;
        long total = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            int v = samples[i];
            lo = v < lo ? v : lo;
            hi = v > hi ? v : hi;
            or |= v;
            total += v;
        }

        low = lo;
        high = hi;
        bits = or;
        sum = total;
    }

    private UInt128 Squares(ReadOnlySpan<int> samples)
    {
        UInt128 total = 0;
        if (_chunk == 0)
        {
            foreach (int v in samples)
            {
                total += (ulong)((long)v * v);
            }

            return total;
        }

        for (int at = 0; at < samples.Length; at += _chunk)
        {
            total += PartialSquares(samples.Slice(at, Math.Min(_chunk, samples.Length - at)));
        }

        return total;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong PartialSquares(ReadOnlySpan<int> samples)
    {
        ulong total = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            long v = samples[i];
            total += (ulong)(v * v);
        }

        return total;
    }

    // Runs of the same extreme code, carried across pieces; a run is counted when it reaches the minimum length and grows from there.
    private void ScanClipping(ChannelState state, ReadOnlySpan<int> samples)
    {
        long runLength = state.RunLength;
        int runSign = state.RunSign;
        for (int i = 0; i < samples.Length; i++)
        {
            int v = samples[i];
            int sign = v == _high ? 1 : v == _low ? -1 : 0;
            if (sign != 0 && sign == runSign)
            {
                runLength++;
            }
            else
            {
                runSign = sign;
                runLength = sign != 0 ? 1 : 0;
            }

            if (runLength >= MeasurementLimits.MinimumClippingRun)
            {
                if (runLength == MeasurementLimits.MinimumClippingRun)
                {
                    state.Runs++;
                    state.Clipped += MeasurementLimits.MinimumClippingRun;
                }
                else
                {
                    state.Clipped++;
                }

                if (runLength > state.Longest)
                {
                    state.Longest = runLength;
                }
            }
        }

        state.RunLength = runLength;
        state.RunSign = runSign;
    }

    // The figures. Called once, when the stream has ended; null where no sample was measured.
    public LevelFigures? Build()
    {
        long count = _channels[0].Count;
        if (count == 0)
        {
            return null;
        }

        var channels = new List<ChannelLevels>(_channels.Length);
        foreach (ChannelState state in _channels)
        {
            channels.Add(_isFloat ? FloatLevels(state) : IntegerLevels(state));
        }

        SilenceState joint = _channels.Length == 1 ? _channels[0].Silence : _joint;
        int rms = _isFloat ? RmsOfFloats() : RmsOfIntegers(count);
        int orBits = 0;
        long highest = 0;
        int peak = MeasurementLimits.NoLevel;
        ChannelLevels? dc = null;
        long runs = 0;
        long clipped = 0;
        long longest = 0;
        bool only16 = _isFloat;
        bool only24 = _isFloat;
        for (int c = 0; c < channels.Count; c++)
        {
            ChannelState state = _channels[c];
            ChannelLevels levels = channels[c];
            orBits |= state.Or;
            highest = Math.Max(highest, state.PeakMagnitude);
            peak = Math.Max(peak, levels.SamplePeakTenths);
            if (dc is null || Math.Abs(levels.DcOffsetMicropercent) > Math.Abs(dc.DcOffsetMicropercent))
            {
                dc = levels;
            }

            runs += levels.ClippingRuns;
            clipped += levels.ClippedSamples;
            longest = Math.Max(longest, levels.LongestClippingRun);
            only16 &= levels.HasOnly16BitValues;
            only24 &= levels.HasOnly24BitValues;
        }

        ChannelLevels stream = new()
        {
            SamplePeakTenths = peak,
            RmsTenths = rms,
            DcOffsetMicropercent = dc!.DcOffsetMicropercent,
            DcOffsetTenths = dc.DcOffsetTenths,
            ClippingRuns = runs,
            ClippedSamples = clipped,
            LongestClippingRun = longest,
            SilentAtStart = joint.Leading,
            SilentAtEnd = joint.Trailing,
            SilentSamples = joint.Total,
            LowBitsUnused = !_isFloat && orBits != 0 ? BitOperations.TrailingZeroCount(orBits) : null,
            HighestMagnitude = highest,
            HasOnly16BitValues = only16,
            HasOnly24BitValues = only24,
        };
        return new LevelFigures { Format = _format, BitsPerSample = _isFloat ? 0 : _bits, Stream = stream, Channels = channels };
    }

    private ChannelLevels IntegerLevels(ChannelState state)
    {
        double fullScale = DetMath.PowerOfTwo(_bits - 1);
        long fullScaleCode = 1L << (_bits - 1);
        UInt128 magnitude = state.Sum < 0 ? (UInt128)(-state.Sum) : (UInt128)state.Sum;
        UInt128 numerator = magnitude * 100_000_000UL;
        UInt128 denominator = (UInt128)(ulong)state.Count * (ulong)fullScaleCode;
        long micro = (long)Math.Min((ulong)((numerator + numerator + denominator) / (denominator + denominator)), long.MaxValue);
        double ratio = ToDouble(magnitude) / ((double)state.Count * fullScale);
        double peak = (double)state.PeakMagnitude / fullScale;
        double meanSquare = ToDouble(state.SumSquares) / ((double)state.Count * fullScale * fullScale);
        return new ChannelLevels
        {
            SamplePeakTenths = state.PeakMagnitude == 0 ? MeasurementLimits.NoLevel : Tenths(peak * peak),
            RmsTenths = state.SumSquares == 0 ? MeasurementLimits.NoLevel : Tenths(meanSquare),
            DcOffsetMicropercent = state.Sum < 0 ? -micro : micro,
            DcOffsetTenths = magnitude == 0 ? MeasurementLimits.NoLevel : Tenths(ratio * ratio),
            ClippingRuns = state.Runs,
            ClippedSamples = state.Clipped,
            LongestClippingRun = state.Longest,
            SilentAtStart = state.Silence.Leading,
            SilentAtEnd = state.Silence.Trailing,
            SilentSamples = state.Silence.Total,
            LowBitsUnused = state.Or != 0 ? BitOperations.TrailingZeroCount(state.Or) : null,
            HighestMagnitude = state.PeakMagnitude,
        };
    }

    private static ChannelLevels FloatLevels(ChannelState state)
    {
        double peak = Math.Max(state.High, -state.Low);
        double mean = state.SumDouble / state.Count;
        double magnitude = Math.Abs(mean);
        long micro = (long)Math.Floor((magnitude * 1e8) + 0.5);
        return new ChannelLevels
        {
            SamplePeakTenths = peak > 0 ? Tenths(peak * peak) : MeasurementLimits.NoLevel,
            RmsTenths = state.SumSquaresDouble > 0 ? Tenths(state.SumSquaresDouble / state.Count) : MeasurementLimits.NoLevel,
            DcOffsetMicropercent = mean < 0 ? -micro : micro,
            DcOffsetTenths = magnitude > 0 ? Tenths(magnitude * magnitude) : MeasurementLimits.NoLevel,
            ClippingRuns = state.Runs,
            ClippedSamples = state.Clipped,
            LongestClippingRun = state.Longest,
            SilentAtStart = state.Silence.Leading,
            SilentAtEnd = state.Silence.Trailing,
            SilentSamples = state.Silence.Total,
            HasOnly16BitValues = state.Only16,
            HasOnly24BitValues = state.Only24,
        };
    }

    private int RmsOfIntegers(long count)
    {
        UInt128 squares = 0;
        foreach (ChannelState state in _channels)
        {
            squares += state.SumSquares;
        }

        double fullScale = DetMath.PowerOfTwo(_bits - 1);
        return squares == 0 ? MeasurementLimits.NoLevel : Tenths(ToDouble(squares) / ((double)count * _channels.Length * fullScale * fullScale));
    }

    private int RmsOfFloats()
    {
        double squares = 0;
        foreach (ChannelState state in _channels)
        {
            squares += state.SumSquaresDouble;
        }

        return squares > 0 ? Tenths(squares / ((double)_channels[0].Count * _channels.Length)) : MeasurementLimits.NoLevel;
    }

    // A power under 1e-300 would be a subnormal number of no use; it reads as -3,000 dB.
    private static int Tenths(double power) => DetMath.Tenths(Math.Max(power, 1e-300));

    // A UInt128 as a double by four exact 32-bit parts added from the top: rounded the same way on every platform.
    internal static double ToDouble(UInt128 value)
    {
        ulong high = (ulong)(value >> 64);
        ulong low = (ulong)value;
        return (((double)(uint)(high >> 32) * 79228162514264337593543950336.0) + ((double)(uint)high * 18446744073709551616.0)) + (((double)(uint)(low >> 32) * 4294967296.0) + (double)(uint)low);
    }

    private sealed class ChannelState
    {
        public long Count;
        public Int128 Sum;
        public UInt128 SumSquares;
        public int Or;
        public long PeakMagnitude;
        public double Low = double.MaxValue;
        public double High = double.MinValue;
        public double SumDouble;
        public double SumSquaresDouble;
        public bool Only16 = true;
        public bool Only24 = true;
        public long RunLength;
        public int RunSign;
        public long Runs;
        public long Clipped;
        public long Longest;
        public SilenceState Silence;
    }

    // Stretches of samples that are exactly 0 (flags that are 0 for a float), as a struct whose methods change it in place.
    private struct SilenceState
    {
        private bool _isLeadingClosed;

        public long Leading;
        public long Trailing;
        public long Total;

        public void Add(ReadOnlySpan<int> values)
        {
            int first = values.IndexOfAnyExcept(0);
            if (first < 0)
            {
                Total += values.Length;
                Trailing += values.Length;
                if (!_isLeadingClosed)
                {
                    Leading += values.Length;
                }

                return;
            }

            if (!_isLeadingClosed)
            {
                Leading += first;
                _isLeadingClosed = true;
            }

            Trailing = values.Length - 1 - values.LastIndexOfAnyExcept(0);
            Total += values.Count(0);
        }

        public void Gap()
        {
            _isLeadingClosed = true;
            Trailing = 0;
        }
    }
}
