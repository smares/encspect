using System.Security.Cryptography;
using Builder = Encspect.Tests.TakBuilder;

namespace Encspect.Tests;

// An encoder of the coded samples of TAK 2.0 and 2.2, the inverse of the clean-room description the decoder follows (docs/design/tak-format.md; docs/development.md,
// "TAK decoding (clean-room)"), so that tests can round-trip samples the encoders at hand never write (every residual mode and escape, every prediction order, wasted bits,
// every difference order, the stereo modes and the paired entries of the channel table that no file uses) and craft damage. It makes arbitrary choices from a seeded generator
// instead of good ones: the filters' parameters are random, which costs bits and still decodes, since a value is predicted from the true values before it. It shares no code
// with the decoder; Takc's files and FFmpeg are what pin the description itself (crosscheck_tak_pcm.py).
internal static class TakEncoder
{
    internal sealed class Settings
    {
        public int Codec { get; init; } = 2;

        public int Bits { get; init; } = 16;

        public int Rate { get; init; } = 8000;

        // The stereo mode u(3) of codec 2 (section 6.1); null picks one at random.
        public int? StereoMode { get; init; }

        // Codec 4: the entries of the channel table as (channel, partner or -1, type); null writes no table (every channel alone).
        public (int Channel, int Partner, int Type)[]? Table { get; init; }

        public int? DifferenceOrder { get; init; }

        public int? WastedBits { get; init; }

        // 0 picks 1 to 8 at random.
        public int Subframes { get; init; }

        // The share of subframes that use prediction.
        public double Prediction { get; init; } = 0.7;

        // The share of residual blocks that are split into partitions where they can be.
        public double Partitions { get; init; } = 0.7;

        public bool StereoBit { get; init; }

        // Forces the residual mode of every block (null: chosen from those that fit).
        public int? ResidualMode { get; init; }

        // Forces the order index u(4) of every prediction (null: any that fits).
        public int? OrderIndex { get; init; }

        // Forces the filter mode's order of 16 (true), 8 (false) or random (null).
        public bool? LongFilter { get; init; }

        public bool WithoutContinuing { get; init; }
    }

    private static readonly int[] OrderTable = [4, 8, 12, 16, 24, 32, 48, 64, 80, 96, 128, 160, 192, 224, 256, 0];

    // The rows of the residual table by the description's pattern: rows 1 to 3 are their own, the others follow the formulas under the table.
    private static (int K, long T, long S, long A, long B) Row(int m)
    {
        switch (m)
        {
            case 1:
                return (1, 1, 1, 3, 8);
            case 2:
                return (2, 3, 1, 7, 6);
            case 3:
                return (3, 5, 2, 14, 13);
        }

        if (m % 2 == 1)
        {
            int j = (m - 1) / 2;
            return (j + 2, 11L << (j - 2), 1L << j, 7L << j, 25L << (j - 2));
        }

        int h = (m - 2) / 2;
        return (h + 2, 3L << (h - 1), 3L << (h - 1), 13L << (h - 1), 3L << (h + 2));
    }

    public static int PartitionLength(int rate)
    {
        int g = (((rate + 511) >> 9) + 3) / 4 * 4;
        return rate < 11025 ? g * 8 : rate < 22050 ? g * 4 : rate < 44100 ? g * 2 : g;
    }

    public static int Unit(int rate) => 2 * ((((rate + 511) >> 9) + 3) / 4 * 4);

    private sealed class Writer
    {
        private readonly Builder.BitWriter _bits = new();

        public void Put(long value, int count) => _bits.Put(value, count);

        public void Flag4(int value)
        {
            if (value == 0)
            {
                Put(0, 1);
                return;
            }

            Put(1, 1);
            Put(value - 1, 4);
        }

        public void Unary(int count, int limit)
        {
            for (int i = 0; i < count; i++)
            {
                Put(0, 1);
            }

            if (count < limit)
            {
                Put(1, 1);
            }
        }

        public byte[] ToArray() => _bits.ToArray();
    }

    // ----- Residual codes -----

    private static uint Zigzag(int value) => ((uint)value << 1) ^ (uint)(value >> 31);

    // The code in the mode's row as (value, count) fields; null where the row has no way to write it.
    private static List<(long Value, int Count)>? CodeFields(uint code, int mode)
    {
        (int k, long t, long s, long a, long b) = Row(mode);
        long limit = 1L << k;
        if (code < t)
        {
            return [(code, k)];
        }

        if (code < limit)
        {
            return [(code, k), (0, 1)];
        }

        long low = t;
        long high = limit - 1;
        long w = code + t;
        if (w < a && w - limit >= low && w - limit <= high)
        {
            return [(w - limit, k), (1, 1)];
        }

        for (int d = 0; d <= 8; d++)
        {
            w = code + t - (s * d);
            long v = w - limit;
            if (w >= a && v >= low && v <= high)
            {
                List<(long, int)> fields = [(v, k), (1, 1)];
                for (int i = 0; i < d; i++)
                {
                    fields.Add((0, 1));
                }

                fields.Add((1, 1));
                return fields;
            }
        }

        List<(long, int)> escape = [];
        long first = Math.Max(a, low + limit);
        long last = high + limit;
        long at = code - b;
        if (at >= first && at <= last)
        {
            escape.Add((at - limit, k));
            escape.Add((1, 1));
            for (int i = 0; i < 9; i++)
            {
                escape.Add((0, 1));
            }

            escape.Add((0, 3));
            return escape;
        }

        long rest = code - b - first;
        if (rest < s)
        {
            return null;
        }

        w = first + (rest % s);
        long m = (code - b - w) / s;
        if (w > last || m < 1)
        {
            return null;
        }

        long u = m - 1;
        int e = Math.Max(1, 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)u));
        if (e > 29)
        {
            return null;
        }

        escape.Add((w - limit, k));
        escape.Add((1, 1));
        for (int i = 0; i < 9; i++)
        {
            escape.Add((0, 1));
        }

        if (e < 7)
        {
            escape.Add((e, 3));
        }
        else
        {
            escape.Add((7, 3));
            escape.Add((e - 7, 5));
        }

        escape.Add((u, e));
        return escape;
    }

    private static long CostOf(List<(long Value, int Count)> fields) => fields.Sum(f => f.Count);

    // The modes that can write every code of a run, with their cost in bits; mode 0 only for a run of zeros.
    private static List<(int Mode, long Cost)> Fitting(IEnumerable<uint> codes)
    {
        uint[] all = [.. codes];
        List<(int, long)> modes = [];
        if (all.All(c => c == 0))
        {
            modes.Add((0, 0));
        }

        for (int mode = 1; mode <= 50; mode++)
        {
            long cost = 0;
            bool fits = true;
            foreach (uint code in all)
            {
                List<(long Value, int Count)>? fields = CodeFields(code, mode);
                if (fields is null)
                {
                    fits = false;
                    break;
                }

                cost += CostOf(fields);
            }

            if (fits)
            {
                modes.Add((mode, cost));
            }
        }

        return modes;
    }

    private static int PickMode(IEnumerable<uint> codes, Settings settings, Random random)
    {
        List<(int Mode, long Cost)> fitting = Fitting(codes);
        if (settings.ResidualMode is { } forced)
        {
            if (fitting.All(f => f.Mode != forced))
            {
                throw new InvalidOperationException($"Mode {forced} cannot write the codes");
            }

            return forced;
        }

        long best = fitting.Min(f => f.Cost);
        List<(int Mode, long Cost)> near = [.. fitting.Where(f => f.Cost <= (2 * best) + 64)];
        return near[random.Next(near.Count)].Mode;
    }

    private static void WriteRun(Writer w, uint[] codes, int mode)
    {
        if (mode == 0)
        {
            return;
        }

        foreach (uint code in codes)
        {
            foreach ((long value, int count) in CodeFields(code, mode)!)
            {
                w.Put(value, count);
            }
        }
    }

    // A residual block of the values, split into partitions where that is possible and the settings and the dice say so.
    private static void WriteBlock(Writer w, int[] values, Settings settings, Random random)
    {
        uint[] codes = [.. values.Select(Zigzag)];
        int p = PartitionLength(settings.Rate);
        int quotient = values.Length / p;
        int rest = values.Length - (quotient * p);
        int count = rest < p / 2 ? quotient : quotient + 1;
        int last = rest < p / 2 ? rest + p : rest;
        if (count is >= 2 and <= 128 && random.NextDouble() < settings.Partitions)
        {
            w.Put(1, 1);
            int[] modes = new int[count];
            int[][] runs = new int[count][];
            for (int i = 0, at = 0; i < count; i++)
            {
                int length = i == count - 1 ? last : p;
                runs[i] = [.. Enumerable.Range(at, length)];
                at += length;
            }

            // Every partition's mode is one that fits it; the changes between neighbors are written with the shortest of the description's codes, or the new-mode code at random.
            for (int i = 0; i < count; i++)
            {
                modes[i] = PickMode(runs[i].Select(j => codes[j]), settings, random);
            }

            w.Put(modes[0], 6);
            for (int i = 1; i < count; i++)
            {
                int delta = modes[i] - modes[i - 1];
                bool isNew = random.Next(6) == 0;
                if (!isNew && delta == 0)
                {
                    w.Unary(0, 6);
                }
                else if (!isNew && delta == -1)
                {
                    w.Unary(1, 6);
                }
                else if (!isNew && delta == 1)
                {
                    w.Unary(2, 6);
                }
                else if (!isNew && Math.Abs(delta) is >= 2 and <= 4)
                {
                    w.Unary(Math.Abs(delta) + 1, 6);
                    w.Put(delta < 0 ? 1 : 0, 1);
                }
                else
                {
                    w.Unary(6, 6);
                    w.Put(modes[i], 6);
                }
            }

            for (int i = 0; i < count; i++)
            {
                WriteRun(w, [.. runs[i].Select(j => codes[j])], modes[i]);
            }

            return;
        }

        w.Put(0, 1);
        int mode = PickMode(codes, settings, random);
        w.Put(mode, 6);
        WriteRun(w, codes, mode);
    }

    // ----- Differences -----

    // The channel's values as the sequence the integration of the description turns back into them.
    private static int[] Differences(int[] values, int order)
    {
        int[] d = [.. values];
        unchecked
        {
            for (int pass = 1; pass <= order; pass++)
            {
                for (int i = d.Length - 1; i >= pass; i--)
                {
                    d[i] -= d[i - 1];
                }
            }
        }

        return d;
    }

    // ----- Prediction -----

    private static int Clip14(int v) => Math.Max(-8192, Math.Min(8191, v));

    private static short[] Coefficients(int[] k, int f)
    {
        int n = k.Length;
        int[] t = new int[n];
        unchecked
        {
            if (n > 0)
            {
                t[0] = 64 * k[0];
            }

            for (int i = 1; i < n; i++)
            {
                int[] before = (int[])t.Clone();
                for (int j = 0; j < i; j++)
                {
                    t[j] = before[j] + (((k[i] * before[i - 1 - j]) + 256) >> 9);
                }

                t[i] = 64 * k[i];
            }

            int s = 15 - f;
            short[] c = new short[n];
            for (int d = 1; d <= n; d++)
            {
                c[d - 1] = (short)(-((t[d - 1] + (1 << (s - 1))) >> s));
            }

            return c;
        }
    }

    // The subframe of the values at [at, at + length) of a channel's sequence. previous: the length of the subframe before it in the channel, or 0 for the first.
    private static void WriteSubframe(Writer w, int[] v, int at, int length, bool isFirst, int previous, Settings settings, Random random)
    {
        List<int> indexes = [];
        for (int i = 0; i < OrderTable.Length; i++)
        {
            int n = OrderTable[i];
            if ((settings.OrderIndex is null || settings.OrderIndex == i) && (n <= length || (!isFirst && n <= previous)))
            {
                indexes.Add(i);
            }
        }

        if (indexes.Count == 0 || random.NextDouble() >= settings.Prediction)
        {
            w.Put(0, 1);
            WriteBlock(w, v[at..(at + length)], settings, random);
            return;
        }

        w.Put(1, 1);
        int index = indexes[random.Next(indexes.Count)];
        int order = OrderTable[index];
        w.Put(index, 4);
        bool canWarm = order <= length;
        bool canContinue = !isFirst && order <= previous;
        bool isContinued = canContinue && (!canWarm || (!settings.WithoutContinuing && random.Next(2) == 0));
        if (!isFirst)
        {
            w.Put(isContinued ? 1 : 0, 1);
        }

        int warm = 0;
        if (!isContinued)
        {
            int h = random.Next(3);
            w.Put(h, 2);
            WriteBlock(w, Differences(v[at..(at + order)], h), settings, random);
            warm = order;
        }

        int ds = random.Next(4) == 0 ? random.Next(0, 17) : 0;
        w.Flag4(ds);
        int z = 6 + random.Next(2);
        w.Put(z - 6, 1);
        int f = 10;
        if (random.Next(2) == 0)
        {
            w.Put(0, 1);
        }
        else
        {
            int drop = random.Next(1, 8);
            w.Put(1, 1);
            w.Put(drop - 1, 3);
            f = 10 - drop;
        }

        // The parameters, as the raw fields and as k in units of 1/512.
        int[] k = new int[order];
        if (order > 0)
        {
            int scale = 10 - z;
            k[0] = random.Next(-200, 200);
            k[1] = random.Next(-200, 200);
            w.Put(k[0], 10);
            w.Put(k[1], 10);
            int FieldValue(int bits) => random.Next(-(1 << (bits - 1)) / 3, (1 << (bits - 1)) / 3 + 1);
            int v2 = FieldValue(z);
            int v3 = FieldValue(z);
            k[2] = v2 << scale;
            k[3] = v3 << scale;
            w.Put(v2, z);
            w.Put(v3, z);
            if (order > 4)
            {
                int y = random.Next(2);
                w.Put(y, 1);
                for (int i = 4; i < order; i += 4)
                {
                    int drop = random.Next(4);
                    int q = z - y - drop;
                    w.Put(drop, 2);
                    for (int j = 0; j < 4; j++)
                    {
                        int raw = FieldValue(q);
                        k[i + j] = raw << scale;
                        w.Put(raw, q);
                    }
                }
            }
        }

        short[] c = Coefficients(k, f);
        int first = at + warm;
        int[] residuals = new int[length - warm];
        unchecked
        {
            for (int p = first; p < at + length; p++)
            {
                int a = 1 << (f - 1);
                for (int d = 1; d <= order; d++)
                {
                    a += c[d - 1] * (short)(v[p - d] >> ds);
                }

                residuals[p - first] = (Clip14(a >> f) << ds) - v[p];
            }
        }

        WriteBlock(w, residuals, settings, random);
    }

    // ----- Channels -----

    private sealed class ChannelData
    {
        public int Wasted;
        public int Order;
        public int[] Values = [];
    }

    private static int TrailingZeros(int[] samples, int bits)
    {
        int zeros = bits;
        foreach (int s in samples)
        {
            if (s != 0)
            {
                zeros = Math.Min(zeros, System.Numerics.BitOperations.TrailingZeroCount(s));
            }
        }

        return zeros;
    }

    private static void WriteChannel(Writer w, ChannelData channel, int bits, Settings settings, Random random)
    {
        int n = channel.Values.Length;
        w.Flag4(channel.Wasted);
        w.Put(channel.Values[0], bits - channel.Wasted);
        w.Put(channel.Order, 2);
        int unit = Unit(settings.Rate);
        int room = (n - 2) / unit;
        int count = settings.Subframes > 0 ? settings.Subframes : random.Next(1, 9);
        count = Math.Min(count, room + 1);
        w.Put(count - 1, 3);
        int[] boundaries = new int[count];
        List<int> pool = [.. Enumerable.Range(1, Math.Max(room, 0))];
        for (int i = 1; i < count; i++)
        {
            int pick = random.Next(pool.Count);
            boundaries[i] = pool[pick];
            pool.RemoveAt(pick);
        }

        Array.Sort(boundaries, 1, count - 1);
        for (int i = 1; i < count; i++)
        {
            w.Put(boundaries[i], 6);
        }

        int position = 1;
        int previous = 0;
        for (int i = 0; i < count; i++)
        {
            int length = i < count - 1 ? (boundaries[i + 1] - boundaries[i]) * unit : n - 1 - (boundaries[i] * unit);
            WriteSubframe(w, channel.Values, position, length, i == 0, previous, settings, random);
            position += length;
            previous = length;
        }
    }

    // ----- Decorrelation, undone: from the values the decoder ends with to the ones it starts from -----

    private sealed class Decorrelation
    {
        public int Mode;     // 1 add, 2 subtract, 3 mid/side, 4 scaled, 5 filter
        public int X;
        public int Y;
        public int Shift;
        public int Gain;
        public short[] G = [];
        public int[] Coefficients = [];
        public bool Head;
        public bool Tail;
    }

    private static void Undo(Decorrelation d, int[][] values)
    {
        int[] x = values[d.X];
        int[] y = values[d.Y];
        int n = x.Length;
        unchecked
        {
            switch (d.Mode)
            {
                case 1:
                    for (int i = 1; i < n; i++)
                    {
                        y[i] -= x[i];
                    }

                    break;
                case 2:
                    for (int i = 1; i < n; i++)
                    {
                        x[i] = y[i] - x[i];
                    }

                    break;
                case 3:
                    for (int i = 1; i < n; i++)
                    {
                        int b = y[i] - x[i];
                        x[i] += b >> 1;
                        y[i] = b;
                    }

                    break;
                case 4:
                    for (int i = 1; i < n; i++)
                    {
                        x[i] = ((((d.Gain * (y[i] >> d.Shift)) + 128) >> 8) << d.Shift) - x[i];
                    }

                    break;
                default:
                    int k = n - 1;
                    int m = d.G.Length;
                    int h = m / 2;
                    int[] original = [.. x];
                    for (int j = 0; j < k; j++)
                    {
                        if (j < h)
                        {
                            if (d.Head)
                            {
                                x[j + 1] = original[j + 1] - y[j + 1];
                            }
                        }
                        else if (j <= k - h)
                        {
                            int a = 512;
                            for (int i = 0; i < m; i++)
                            {
                                a += d.G[i] * (short)(y[j - h + i + 1] >> d.Shift);
                            }

                            x[j + 1] = (Clip14(a >> 10) << d.Shift) - original[j + 1];
                        }
                        else if (d.Tail)
                        {
                            x[j + 1] = original[j + 1] - y[j + 1];
                        }
                    }

                    break;
            }
        }
    }

    private static Decorrelation Choose(int mode, int x, int y, Settings settings, Random random)
    {
        var d = new Decorrelation { Mode = mode, X = x, Y = y };
        if (mode == 4)
        {
            d.Shift = random.Next(0, 4) == 0 ? random.Next(0, 17) : 0;
            d.Gain = random.Next(-512, 512);
        }
        else if (mode == 5)
        {
            d.Shift = random.Next(0, 4) == 0 ? random.Next(0, 17) : 0;
            bool isLong = settings.LongFilter ?? random.Next(2) == 0;
            d.G = new short[isLong ? 16 : 8];
            d.Head = random.Next(2) == 0;
            d.Tail = random.Next(2) == 0;
        }

        return d;
    }

    // The fields of the mode, with the widths a filter's coefficients use.
    private static void WriteDecorrelation(Writer w, Decorrelation d)
    {
        if (d.Mode == 4)
        {
            w.Flag4(d.Shift);
            w.Put(d.Gain, 10);
        }
        else if (d.Mode == 5)
        {
            w.Flag4(d.Shift);
            w.Put(d.G.Length == 16 ? 1 : 0, 1);
            w.Put(d.Head ? 1 : 0, 1);
            w.Put(d.Tail ? 1 : 0, 1);
            for (int i = 0; i < d.G.Length; i += 4)
            {
                int drop = d.Coefficients[i / 4];
                w.Put(drop, 3);
                for (int j = 0; j < 4; j++)
                {
                    w.Put(d.G[i + j], 14 - drop);
                }
            }
        }
    }

    // ----- Frames -----

    // The coded data of one frame (the bytes between its header's CRC-24 and its data CRC-24), from the planar samples of its channels.
    public static byte[] Frame(int[][] pcm, Settings settings, Random random)
    {
        int channels = pcm.Length;
        int n = pcm[0].Length;
        int bits = settings.Bits;
        var w = new Writer();
        if (n < 16)
        {
            foreach (int[] channel in pcm)
            {
                foreach (int sample in channel)
                {
                    w.Put(sample, bits);
                }
            }

            return w.ToArray();
        }

        var data = new ChannelData[channels];
        for (int c = 0; c < channels; c++)
        {
            int wasted = settings.WastedBits ?? random.Next(0, Math.Min(Math.Min(TrailingZeros(pcm[c], bits), bits - 1), 16) + 1);
            int order = settings.DifferenceOrder ?? random.Next(4);
            int[] shifted = [.. pcm[c].Select(s => s >> wasted)];
            data[c] = new ChannelData { Wasted = wasted, Order = order, Values = Differences(shifted, order) };
        }

        int[][] values = [.. data.Select(d => d.Values)];
        if (settings.Codec == 2)
        {
            Decorrelation? d = null;
            int mode = 0;
            if (channels == 2)
            {
                mode = settings.StereoMode ?? random.Next(n - 1 >= 256 ? 8 : 6);
                if (mode != 0)
                {
                    (int mx, int my, int kind) = mode switch
                    {
                        1 => (0, 1, 1),
                        2 => (0, 1, 2),
                        3 => (0, 1, 3),
                        4 => (1, 0, 4),
                        5 => (0, 1, 4),
                        6 => (1, 0, 5),
                        _ => (0, 1, 5),
                    };
                    d = Choose(kind, mx, my, settings, random);
                    Randomize(d, random);
                    Undo(d, values);
                }
            }

            for (int c = 0; c < channels; c++)
            {
                WriteChannel(w, data[c], bits, settings, random);
            }

            if (channels == 2)
            {
                w.Put(settings.StereoBit ? 1 : 0, 1);
                if (settings.StereoBit)
                {
                    w.Put(random.Next(64), 6);
                }

                w.Put(mode, 3);
                if (d is not null)
                {
                    WriteDecorrelation(w, d);
                }
            }

            return w.ToArray();
        }

        (int Channel, int Partner, int Type)[] table = settings.Table ?? [.. Enumerable.Range(0, channels).Select(c => (c, -1, 0))];
        List<Decorrelation?> decorrelations = [];
        foreach ((int channel, int partner, int type) in table)
        {
            if (partner < 0)
            {
                decorrelations.Add(null);
                continue;
            }

            (int x, int y, int kind) = type switch
            {
                0 => (partner, channel, 1),
                1 => (partner, channel, 3),
                2 => (channel, partner, 4),
                _ => (channel, partner, 5),
            };
            Decorrelation d = Choose(kind, x, y, settings, random);
            Randomize(d, random);
            decorrelations.Add(d);
        }

        for (int i = decorrelations.Count - 1; i >= 0; i--)
        {
            if (decorrelations[i] is { } d)
            {
                Undo(d, values);
            }
        }

        if (settings.Table is null)
        {
            w.Put(0, 1);
        }
        else
        {
            w.Put(1, 1);
            w.Put(table.Length - 1, 4);
            foreach ((int channel, int partner, int type) in table)
            {
                w.Put(channel, 4);
                w.Put(partner >= 0 ? 1 : 0, 1);
                if (partner >= 0)
                {
                    w.Put(type, 2);
                    w.Put(partner, 4);
                }
            }
        }

        for (int i = 0; i < table.Length; i++)
        {
            (int channel, int partner, int type) = table[i];
            if (partner >= 0 && type == 1)
            {
                WriteChannel(w, data[partner], bits, settings, random);
            }

            WriteChannel(w, data[channel], bits, settings, random);
            if (decorrelations[i] is { } d)
            {
                WriteDecorrelation(w, d);
            }
        }

        return w.ToArray();
    }

    // The scaling gain and the filter's coefficients are drawn once the mode is known, so that the same values are used to undo it and to write it.
    private static void Randomize(Decorrelation d, Random random)
    {
        if (d.Mode != 5)
        {
            return;
        }

        d.Coefficients = new int[d.G.Length / 4];
        for (int i = 0; i < d.G.Length; i += 4)
        {
            int drop = random.Next(0, 8);
            d.Coefficients[i / 4] = drop;
            int width = 14 - drop;
            for (int j = 0; j < 4; j++)
            {
                d.G[i + j] = (short)random.Next(-(1 << (width - 1)) / 4, (1 << (width - 1)) / 4 + 1);
            }
        }
    }

    // ----- Files -----

    // The samples per frame of the frame size types the tests use (4 to 9 are sizes).
    public static int FrameLength(int sizeType) => sizeType switch { 4 => 4096, 5 => 8192, 6 => 16384, 7 => 512, 8 => 1024, 9 => 2048, _ => throw new ArgumentOutOfRangeException(nameof(sizeType)) };

    // The MD5 of the samples as a WAVE file's data chunk holds them: interleaved, little-endian, 8-bit samples as unsigned bytes.
    public static byte[] Md5Of(int[][] pcm, int bits)
    {
        int width = bits / 8;
        byte[] bytes = new byte[pcm[0].Length * pcm.Length * width];
        int at = 0;
        for (int i = 0; i < pcm[0].Length; i++)
        {
            foreach (int[] channel in pcm)
            {
                int value = bits == 8 ? channel[i] + 128 : channel[i];
                for (int b = 0; b < width; b++)
                {
                    bytes[at++] = (byte)(value >> (8 * b));
                }
            }
        }

        return MD5.HashData(bytes);
    }

    internal sealed record Built(byte[] Data, int[] Offsets, int[] Lengths, IReadOnlyList<byte[]> Coded);

    // A TAK file of the samples: the stream info, the last frame info, the encoder info, an End object, the frames (the first an info frame) and, with md5, the MD5 object.
    // damage may change a frame's coded data before its CRC-24s are computed.
    public static Built Build(int[][] pcm, Settings settings, int seed, int sizeType = 7, bool md5 = false, Func<int, byte[], byte[]>? damage = null, int[]? speakers = null)
    {
        int total = pcm[0].Length;
        int length = FrameLength(sizeType);
        int count = Math.Max(1, (total + length - 1) / length);
        var random = new Random(seed);
        List<byte[]> coded = [];
        for (int i = 0; i < count; i++)
        {
            int from = i * length;
            int samples = Math.Min(length, total - from);
            byte[] data = Frame([.. pcm.Select(c => c[from..(from + samples)])], settings, random);
            coded.Add(damage is null ? data : damage(i, data));
        }

        return Assemble(settings, pcm.Length, total, sizeType, coded, md5 ? Md5Of(pcm, settings.Bits) : null, speakers);
    }

    public static byte[] File(int[][] pcm, Settings settings, int seed, int sizeType = 7, bool md5 = false, Func<int, byte[], byte[]>? damage = null, int[]? speakers = null) =>
        Build(pcm, settings, seed, sizeType, md5, damage, speakers).Data;

    // The file around frames' coded data, however they were made.
    public static Built Assemble(Settings settings, int channels, long total, int sizeType, IReadOnlyList<byte[]> coded, byte[]? md5 = null, int[]? speakers = null)
    {
        int length = FrameLength(sizeType);
        byte[] infoBits = Builder.StreamInfoBits(settings.Codec, 2, sizeType, total, settings.Rate, settings.Bits, channels, 0, speakers);
        List<byte[]> frames = [];
        for (int i = 0; i < coded.Count; i++)
        {
            int samples = (int)Math.Min(length, total - ((long)i * length));
            frames.Add(Builder.Frame(i, 0, isLast: i == coded.Count - 1, isInfo: i == 0, samples: Math.Max(samples, 1), streamInfoBits: infoBits, coded: coded[i]));
        }

        byte[] audio = [.. frames.SelectMany(f => f)];
        byte[] streamInfo = Builder.StreamInfo(settings.Codec, 2, sizeType, total, settings.Rate, settings.Bits, channels, 0, speakers);
        byte[] lastInfo = Builder.LastFrameInfo(audio.Length - frames[^1].Length, frames[^1].Length);
        byte[] prefix = [.. "tBaK"u8, .. streamInfo, .. lastInfo, .. Builder.EncoderInfo(), .. md5 is null ? Array.Empty<byte>() : Builder.Object(6, md5), .. Builder.End()];
        int[] offsets = new int[frames.Count];
        for (int i = 1; i < offsets.Length; i++)
        {
            offsets[i] = offsets[i - 1] + frames[i - 1].Length;
        }

        return new Built([.. prefix, .. audio], [.. offsets.Select(o => o + prefix.Length)], [.. frames.Select(f => f.Length)], coded);
    }
}
