using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;

namespace Encspect.Tests.Measurement;

// Reports are compared byte for byte on all six CI platforms and between the JIT and the NativeAOT build, so the arithmetic of the
// measurements must not depend on the machine (docs/design/spectral-analysis.md, "Determinism"): the hashes of the kernel's raw outputs
// are constants, and a platform that differs fails here before any golden. The kernels run here are the ones the analysis runs, compiled
// optimized (AggressiveOptimization), so the short input of a test does not run the tier-0 code the long analysis would leave.
// Run again with DOTNET_EnableHWIntrinsic=0 in CI's rest shard.
public class MeasurementDeterminismTests
{
    // The prototype's input: two integer-phase triangle tones and linear congruential noise, 511 frames of 4,096 points at hop 2,048.
    private static double[] Signal()
    {
        double[] noise = MeasurementSignals.Noise(1 << 20, 99);
        double[] tone1 = MeasurementSignals.Triangle(1 << 20, 100, 0.3);
        double[] tone2 = MeasurementSignals.Triangle(1 << 20, 37, 0.2);
        var signal = new double[1 << 20];
        for (int i = 0; i < signal.Length; i++)
        {
            signal[i] = (noise[i] * 0.05) + tone1[i] + tone2[i];
        }

        return signal;
    }

    private static string HashBits(IEnumerable<double> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (double value in values)
        {
            hash.AppendData(BitConverter.GetBytes(BitConverter.DoubleToInt64Bits(value)));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    // Every field of the levels of a stream, as text: integers are exact and the float sums are made in a fixed order, so the text is the same on
    // every platform.
    private static string LevelsText(LevelFigures levels)
    {
        var text = new System.Text.StringBuilder();
        text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{levels.ClippingBitsInUse},{levels.MinimumSilentStretch};");
        foreach (ChannelLevels c in levels.Channels.Prepend(levels.Stream))
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{c.SilentStretches},{c.SilentSamples},{c.LongestSilentStretch},{c.SamplePeakTenths},{c.RmsTenths},{c.DcOffsetMicropercent},{c.DcOffsetTenths},{c.ClippingRuns},{c.ClippedSamples},{c.LongestClippingRun},{c.SilentAtStart},{c.SilentAtEnd},{c.ZeroSamples},{c.LowBitsUnused},{c.HighestMagnitude},{c.HasOnly16BitValues},{c.HasOnly24BitValues};");
        }

        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())));
    }

    // The levels of three synthetic streams, integers at 16 and 24 bits (with runs, silence and a gap) and floats (NaN, huge values, full
    // scale), hash to constants on every platform, JIT and NativeAOT, with the vector paths on or off.
    [Fact]
    public void TheLevelsOfSyntheticStreamsHashToConstants()
    {
        int[] a = MeasurementSignals.ToIntegers(MeasurementSignals.Noise(100_000, 7).Select(v => v * 1.4).ToArray(), 16);
        int[] b = [.. a.Select(v => v / 5)];
        Array.Fill(a, 32767, 1000, 40);
        Array.Fill(b, 0, 5000, 9000);
        var format16 = new PcmFormat(44100, 2, 16, SampleFormat.Integer);
        var measurer = new StreamMeasurer(format16, new MeasureOptions { Include = MeasurementKinds.Levels }, CancellationToken.None, _ => true);
        foreach ((int from, int to, long position) in new[] { (0, 60_000, 0L), (60_000, 100_000, 70_000L) })
        {
            int[] planar = [.. a.AsSpan(from, to - from), .. b.AsSpan(from, to - from)];
            measurer.Write(new PcmBlock(position, to - from, 2, to - from, planar));
        }

        string integers16 = LevelsText(measurer.Result(MeasurementStatus.Complete, null).Levels!);

        int[] wide = MeasurementSignals.ToIntegers(MeasurementSignals.Noise(50_000, 8).Select(v => v * 2.0).ToArray(), 24);
        int[] shifted = [.. wide.Select(v => v & ~0xFF)];
        var measurer24 = new StreamMeasurer(new PcmFormat(48000, 2, 24, SampleFormat.Integer), new MeasureOptions { Include = MeasurementKinds.Levels }, CancellationToken.None, _ => true);
        measurer24.Write(new PcmBlock(0, 50_000, 2, 50_000, [.. wide, .. shifted]));
        string integers24 = LevelsText(measurer24.Result(MeasurementStatus.Complete, null).Levels!);

        float[] floats = [.. MeasurementSignals.Noise(40_000, 9).Select(v => (float)(v * 1.2))];
        floats[100] = float.NaN;
        floats[200] = 1e30f;
        Array.Fill(floats, 1.0f, 300, 10);
        Array.Fill(floats, 0f, 2000, 3000);
        var measurerFloat = new StreamMeasurer(new PcmFormat(44100, 1, 32, SampleFormat.Float32), new MeasureOptions { Include = MeasurementKinds.Levels }, CancellationToken.None, _ => true);
        measurerFloat.Write(new PcmBlock(0, 40_000, 1, 40_000, floats));
        string float32 = LevelsText(measurerFloat.Result(MeasurementStatus.Complete, null).Levels!);

        // Clipping at the extremes of the bits in use: 16-bit audio in 24-bit samples with runs at both extremes, and floats on the 16-bit grid with runs at both.
        int[] padded = [.. MeasurementSignals.ToIntegers(MeasurementSignals.Noise(30_000, 10).Select(v => v * 1.2).ToArray(), 16).Select(v => v << 8)];
        Array.Fill(padded, 0x7FFF00, 500, 30);
        Array.Fill(padded, -0x800000, 9000, 12);
        Array.Fill(padded, 0, 12000, 2000);
        var measurerPadded = new StreamMeasurer(new PcmFormat(48000, 1, 24, SampleFormat.Integer), new MeasureOptions { Include = MeasurementKinds.Levels }, CancellationToken.None, _ => true);
        measurerPadded.Write(new PcmBlock(0, 30_000, 1, 30_000, padded));
        string inUse24 = LevelsText(measurerPadded.Result(MeasurementStatus.Complete, null).Levels!);

        float[] grid = [.. MeasurementSignals.Noise(40_000, 11).Select(v => (float)(Math.Floor(v * 0.9 * 32768) / 32768))];
        Array.Fill(grid, 32767f / 32768f, 700, 20);
        Array.Fill(grid, -1f, 8000, 5);
        Array.Fill(grid, 0f, 12000, 1500);
        var measurerGrid = new StreamMeasurer(new PcmFormat(44100, 1, 32, SampleFormat.Float32), new MeasureOptions { Include = MeasurementKinds.Levels }, CancellationToken.None, _ => true);
        measurerGrid.Write(new PcmBlock(0, 40_000, 1, 40_000, grid));
        string grid16 = LevelsText(measurerGrid.Result(MeasurementStatus.Complete, null).Levels!);

        string all = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(integers16 + integers24 + float32 + inUse24 + grid16)));
        Assert.True(LevelsHash == all, $"LevelsHash = {all} (16 bits {integers16[..8]}, 24 bits {integers24[..8]}, floats {float32[..8]}, in use {inUse24[..8]}, grid {grid16[..8]})");
    }

    // The low bits unused per span of two lossyWAV-like streams (24 bits, three channels with a silent stretch and a gap; 16 bits with its blocks off the
    // grid of 32 by a late start): the counts, the changes, the grid and the times are integers and hash to a constant on every platform, JIT and
    // NativeAOT, with the vector paths on or off.
    [Fact]
    public void TheLowBitsPerSpanOfSyntheticStreamsHashToAConstant()
    {
        var text = new System.Text.StringBuilder();
        foreach ((int bits, int channels, long start, long gapAt) in new[] { (24, 3, 0L, 70_001L), (16, 2, 13L, 0L) })
        {
            var measurer = new StreamMeasurer(new PcmFormat(44100, channels, bits, SampleFormat.Integer), new MeasureOptions { Include = MeasurementKinds.Levels }, CancellationToken.None, _ => true);
            int[][] data = MeasurementSpanTests.Zeroed(channels, 120_000, 512, bits == 24 ? 50_000 : 4000, (uint)bits);
            Array.Clear(data[0], 30_000, 5000);
            for (int c = 0; c < channels; c++)
            {
                Array.Clear(data[c], 90_000, 3000);
            }

            foreach ((int from, int to, long position) in gapAt == 0 ? new[] { (0, 120_000, start) } : new[] { (0, 70_000, start), (70_000, 120_000, gapAt + 70_000) })
            {
                for (int at = from; at < to; at += 9999)
                {
                    int n = Math.Min(9999, to - at);
                    int[] planar = new int[channels * n];
                    for (int c = 0; c < channels; c++)
                    {
                        data[c].AsSpan(at, n).CopyTo(planar.AsSpan(c * n, n));
                    }

                    measurer.Write(new PcmBlock(position + (at - from), n, channels, n, planar));
                }
            }

            LowBitsSpanFigures spans = measurer.Result(MeasurementStatus.Complete, null).Levels!.Spans!;
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{spans.Spans},{spans.SilentSpans},{spans.ThinSpans},{spans.ShortSpans},{string.Join(' ', spans.Distribution)},{spans.SpansAboveLowBitsUnused},{spans.Changes},{spans.ChangeMoments},{spans.Grid},{spans.MomentsOnGrid},{spans.Times},{spans.TimesWithEqualCounts};");
        }

        string hash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())));
        Assert.True(LowBitsPerSpanHash == hash, $"LowBitsPerSpanHash = {hash} ({text})");
    }

    // The K-weighting coefficients of every rate the tests use (the derived ones come from DetMath's tangent and power).
    [Fact]
    public void TheDerivedKWeightingCoefficientsHashToAConstant()
    {
        var values = new List<double>();
        foreach (int rate in new[] { 8000, 11025, 16000, 22050, 32000, 44100, 48000, 88200, 96000, 176400, 192000, 384000 })
        {
            KWeighting k = KWeighting.Derive(rate);
            values.AddRange([k.B0, k.B1, k.B2, k.A1, k.A2, k.HighpassA1, k.HighpassA2]);
        }

        Assert.True(KWeightingHash == HashBits(values), "KWeightingHash = " + HashBits(values));
    }

    // The loudness meter's raw output on a program-like signal with an impulse followed by silence (the flush of the filter states), at three rates: the
    // figures, the series and the gating counts are exact integers and hash to a constant on every platform.
    [Fact]
    public void TheLoudnessOfSyntheticStreamsHashToAConstant()
    {
        var text = new System.Text.StringBuilder();
        foreach (int rate in new[] { 48000, 44100, 11025 })
        {
            double[] noise = MeasurementSignals.Noise(rate * 12, 21);
            var signal = new double[noise.Length];
            for (int i = 0; i < signal.Length; i++)
            {
                double t = (double)i / rate;
                signal[i] = noise[i] * (0.05 + (0.04 * ((i / (rate / 4)) % 5)));
            }

            signal[rate * 8] = 1.5;
            Array.Clear(signal, (rate * 8) + 1, rate * 2);
            LoudnessFigures l = LoudnessSignals.Measure([signal, signal.Select(v => -v * 0.5).ToArray()], rate, blockSize: 7777, kinds: MeasurementKinds.Loudness | MeasurementKinds.TruePeak).Loudness!;
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{rate}:{l.IntegratedTenths},{l.RangeTenths},{l.RangeLowTenths},{l.RangeHighTenths},{l.MaximumMomentaryTenths},{l.MaximumShortTermTenths},{l.Blocks},{l.BlocksAboveAbsoluteGate},{l.BlocksAboveRelativeGate};");
            text.Append(string.Join(',', l.ShortTerm!.Values.ToArray().Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))).Append(';');
        }

        string hash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())));
        Assert.True(LoudnessHash == hash, "LoudnessHash = " + hash);
    }

    // The K-weighted sums of squares of every granule and the maximum momentary energy, the raw doubles, for noise, an impulse and then silence: the
    // filter states fall under the flush threshold during the silence, and where they are set to zero decides the last bits of the sums.
    // The flush is at stream positions that are multiples of 4,096, so cutting the stream in other blocks must not change a bit.
    private static (string Hash, int Granules) LoudnessRawHash(int rate, int block)
    {
        double[] noise = MeasurementSignals.Noise(rate / 2, 41);
        var signal = new double[(rate * 5) / 2];
        for (int i = 0; i < noise.Length; i++)
        {
            signal[i] = noise[i] * 0.2;
        }

        signal[(rate / 2) + 100] = 1.0;
        var meter = new LoudnessMeter(rate, [100]) { GranuleTrace = [] };
        meter.Start(0);
        for (int start = 0; start < signal.Length; start += block)
        {
            int n = Math.Min(block, signal.Length - start);
            meter.AddChannel(0, signal.AsSpan(start, n));
            meter.EndPiece(n);
        }

        return (HashBits(meter.GranuleTrace!.Append(meter.RawMaximumMomentary)), meter.GranuleTrace!.Count);
    }

    [Fact]
    public void TheRawGranuleSumsOfAnImpulseAndSilenceDoNotDependOnTheBlocksAndHashToAConstant()
    {
        var hashes = new List<string>();
        foreach (int rate in new[] { 48000, 44100 })
        {
            (string reference, int granules) = LoudnessRawHash(rate, 4096);
            Assert.True(granules >= 120);
            foreach (int block in new[] { 777, 1000, 5000, 16384 })
            {
                Assert.True(reference == LoudnessRawHash(rate, block).Hash, $"the raw sums at {rate} Hz differ between blocks of 4096 and {block}");
            }

            hashes.Add(reference);
        }

        string all = string.Join('|', hashes);
        Assert.True(LoudnessRawHashes == all, "LoudnessRawHashes = " + all);
    }

    // The raw interpolated peaks (the maximum of the filter's outputs, the double itself) of a noise signal at the three oversamplings, cut in blocks of 777.
    [Fact]
    public void TheTruePeakOfSyntheticStreamsHashesToAConstant()
    {
        var peaks = new List<double>();
        foreach (int rate in new[] { 48000, 96000, 192000 })
        {
            double[] signal = MeasurementSignals.Noise(300_000, 31);
            var meter = new TruePeakMeter(1, rate);
            for (int start = 0; start < signal.Length; start += 777)
            {
                meter.AddChannel(0, signal.AsSpan(start, Math.Min(777, signal.Length - start)));
                peaks.Add(meter.RawPeak(0));
            }
        }

        Assert.True(TruePeakHash == HashBits(peaks), "TruePeakHash = " + HashBits(peaks));
    }

    [Fact]
    public void TheRawRowsOfASyntheticSignalHashToAConstant()
    {
        var plan = new FftPlan(4096);
        double[] signal = Signal();
        double[] rows = new double[512];
        var all = new List<double>();
        for (int start = 0; start + 4096 <= signal.Length; start += 2048)
        {
            plan.Rows(signal.AsSpan(start, 4096), plan.NewScratch(), plan.NewScratch(), rows);
            all.AddRange(rows);
        }

        Assert.Equal(511 * 512, all.Count);
        Assert.True(RowsHash == HashBits(all), "RowsHash = " + HashBits(all));
    }

    [Theory]
    [InlineData(1024, "")]
    [InlineData(32768, "")]
    public void TheTablesOfTheLargestAndSmallestSizesAreDeterministic(int size, string unused)
    {
        _ = unused;
        var plan = new FftPlan(size);
        double[] rows = new double[512];
        plan.Rows(MeasurementSignals.Noise(size, 5), plan.NewScratch(), plan.NewScratch(), rows);
        string expected = size == 1024 ? Rows1024Hash : Rows32768Hash;
        Assert.True(expected == HashBits(plan.Window.Concat(rows)), "hash(" + size + ") = " + HashBits(plan.Window.Concat(rows)));
    }

    [Fact]
    public void TheMathFunctionsHashToAConstant()
    {
        var values = new List<double>();
        foreach (int n in new[] { 1024, 4096, 32768 })
        {
            for (int k = 0; k < n; k += 7)
            {
                DetMath.SinCos(k, n, out double sin, out double cos);
                values.Add(sin);
                values.Add(cos);
            }
        }

        for (double x = 1e-300; x < 1e300; x *= 1.9)
        {
            values.Add(DetMath.Ln(x));
            values.Add(DetMath.Log10(x));
        }

        for (double x = -700; x < 700; x += 0.37)
        {
            values.Add(DetMath.Exp(x));
        }

        for (double x = -1.55; x < 1.55; x += 0.011)
        {
            values.Add(DetMath.Tan(x));
            values.Add(DetMath.Sin(x * 40));
            values.Add(DetMath.Cos(x * 40));
        }

        // The K-weighting derivation of step 5 uses these: tan(pi f0 / fs) and 10^(G / 20) and its power.
        foreach (int rate in new[] { 8000, 11025, 22050, 32000, 44100, 48000, 88200, 96000, 176400, 192000 })
        {
            values.Add(DetMath.Tan(DetMath.Pi * 1681.974450955533 / rate));
            values.Add(DetMath.Tan(DetMath.Pi * 38.13547087602444 / rate));
        }

        values.Add(DetMath.Pow(10, 3.999843853973347 / 20));
        values.Add(DetMath.Pow(DetMath.Pow(10, 3.999843853973347 / 20), 0.4996667741545416));
        Assert.True(MathHash == HashBits(values), "MathHash = " + HashBits(values));
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(4096)]
    [InlineData(32768)]
    public void TheVectorLoopAndTheScalarLoopAgreeBitForBit(int size)
    {
        // The vector path is taken where the hardware has it; the same plan with its vector loop turned off runs the scalar one.
        double[] samples = MeasurementSignals.Noise(size, 3);
        var vector = new FftPlan(size);
        var scalar = new FftPlan(size) { UsesVectors = false };
        double[] a = new double[512];
        double[] b = new double[512];
        vector.Rows(samples, vector.NewScratch(), vector.NewScratch(), a);
        scalar.Rows(samples, scalar.NewScratch(), scalar.NewScratch(), b);
        Assert.Equal(HashBits(a), HashBits(b));
    }

    // ---- No operation the platform defines for itself ----

    // Banned by name on every type that defines arithmetic: System.Math and MathF, double, float and Half (their static members and the
    // generic-math interfaces they implement), the vector types of System.Runtime.Intrinsics and System.Numerics, and Complex. By rule, not
    // by a short list: a member of one of these types with one of these names, or ending in Native or Estimate, or holding FusedMultiplyAdd.
    private static readonly HashSet<string> BannedMath =
    [
        "FusedMultiplyAdd", "Sin", "Cos", "Tan", "SinCos", "Sinh", "Cosh", "Tanh", "Asin", "Acos", "Atan", "Atan2", "Asinh", "Acosh", "Atanh",
        "SinPi", "CosPi", "TanPi", "SinCosPi", "AsinPi", "AcosPi", "AtanPi", "Atan2Pi", "Log", "Log2", "Log10", "LogP1", "Log2P1", "Log10P1",
        "Exp", "Exp2", "Exp10", "ExpM1", "Exp2M1", "Exp10M1", "Pow", "RootN", "Cbrt", "Hypot", "Lerp", "ScaleB", "ILogB", "BitIncrement",
        "BitDecrement", "ReciprocalEstimate", "ReciprocalSqrtEstimate", "MultiplyAddEstimate",
    ];

    private static readonly HashSet<string> ArithmeticTypes = ["System.Math", "System.MathF", "System.Double", "System.Single", "System.Half", "System.Numerics.Complex", "System.Runtime.InteropServices.NFloat"];

    private static bool DefinesArithmetic(string parent) =>
        ArithmeticTypes.Contains(parent)
        || parent.StartsWith("System.Runtime.Intrinsics.", StringComparison.Ordinal)
        || (parent.StartsWith("System.Numerics.", StringComparison.Ordinal) && !parent.StartsWith("System.Numerics.BitOperations", StringComparison.Ordinal));

    // The x86 and Arm intrinsic classes name their fused operations MultiplyAdd*, MultiplySubtract* and FusedMultiply* and their approximations
    // Reciprocal* (rcpps and rsqrtps differ between Intel and AMD).
    private static bool IsPlatformIntrinsic(string parent) =>
        parent.StartsWith("System.Runtime.Intrinsics.X86.", StringComparison.Ordinal) || parent.StartsWith("System.Runtime.Intrinsics.Arm.", StringComparison.Ordinal);

    internal static bool IsBanned(string parent, string name) =>
        (DefinesArithmetic(parent) && BannedMath.Contains(name))
        || (DefinesArithmetic(parent) && (name.EndsWith("Native", StringComparison.Ordinal) || name.EndsWith("Estimate", StringComparison.Ordinal) || name.Contains("FusedMultiplyAdd", StringComparison.Ordinal)))
        || (IsPlatformIntrinsic(parent) && (name.Contains("MultiplyAdd", StringComparison.Ordinal) || name.Contains("MultiplySubtract", StringComparison.Ordinal) || name.Contains("FusedMultiply", StringComparison.Ordinal) || name.Contains("Reciprocal", StringComparison.Ordinal) || name.StartsWith("HorizontalAdd", StringComparison.Ordinal) || name.StartsWith("HorizontalSubtract", StringComparison.Ordinal) || name.StartsWith("AddPairwise", StringComparison.Ordinal) || name.StartsWith("AddAcross", StringComparison.Ordinal) || name.StartsWith("DotProduct", StringComparison.Ordinal)))
        || (parent is "System.Numerics.Vector2" or "System.Numerics.Vector3" or "System.Numerics.Vector4" && name is "Length" or "LengthSquared" or "Distance" or "DistanceSquared")
        || parent.StartsWith("System.Numerics.Tensors", StringComparison.Ordinal)
        || ((parent.StartsWith("System.Runtime.Intrinsics.", StringComparison.Ordinal) || parent.StartsWith("System.Numerics.", StringComparison.Ordinal)) && name is "Sum" or "Dot")
        || parent == "System.Numerics.Complex"
        || parent is "System.Numerics.Vector`1" or "System.Numerics.Vector"
        || (parent == "System.MemoryExtensions" && name is "Sum" or "Max" or "Min")
        || (parent == "System.Buffers.ArrayPool`1" && name == "get_Shared");

    // The reductions of System.Linq.Enumerable. Whether one is banned depends on its signature: a sum, maximum, minimum or fold over integers is exact in
    // any order and stays allowed; one whose result, parameters or type arguments hold float, double or Half is a floating-point reduction.
    private static readonly HashSet<string> BannedLinq = ["Sum", "Average", "Max", "Min", "Aggregate", "MaxBy", "MinBy"];

    private static readonly System.Text.RegularExpressions.Regex FloatingType = new(@"\b(Double|Single)\b|System\.Half\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static bool IsBannedReduction(string parent, string name, Func<IEnumerable<string>> types) =>
        parent == "System.Linq.Enumerable" && BannedLinq.Contains(name) && types().Any(t => FloatingType.IsMatch(t));

    // Renders a signature's types in full (System.Func`2<Int32,Double>) so that a floating-point type anywhere in it shows.
    private sealed class FullNameProvider(MetadataReader md) : System.Reflection.Metadata.ISignatureTypeProvider<string, object?>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            reader.GetString(reader.GetTypeDefinition(handle).Namespace) + "." + reader.GetString(reader.GetTypeDefinition(handle).Name);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => TypeName(md, handle);

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[,]";

        public string GetByReferenceType(string elementType) => elementType;

        public string GetPointerType(string elementType) => elementType;

        public string GetPinnedType(string elementType) => elementType;

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetFunctionPointerType(MethodSignature<string> signature) => string.Empty;

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";

        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;

        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
    }

    // The types an Enumerable call involves: the return type and parameters of the method it names, and the type arguments of its instantiation.
    private static IEnumerable<string> ReductionTypes(MetadataReader md, MemberReference member, MethodSpecificationHandle? instantiation)
    {
        var provider = new FullNameProvider(md);
        MethodSignature<string> signature = member.DecodeMethodSignature(provider, null);
        var types = new List<string> { signature.ReturnType };
        types.AddRange(signature.ParameterTypes);
        if (instantiation is { } spec)
        {
            types.AddRange(md.GetMethodSpecification(spec).DecodeSignature(provider, null));
        }

        return types;
    }

    private static string TypeName(MetadataReader md, TypeReferenceHandle handle)
    {
        TypeReference type = md.GetTypeReference(handle);

        // A nested type (AdvSimd.Arm64, Sse41.X64) is its outer type's name and a plus sign before its own.
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return TypeName(md, (TypeReferenceHandle)type.ResolutionScope) + "+" + md.GetString(type.Name);
        }

        return md.GetString(type.Namespace) + "." + md.GetString(type.Name);
    }

    // The name of a generic type instance's definition (System.Numerics.Vector`1 for Vector<double>), which a member reference's parent is.
    private sealed class NameProvider(MetadataReader md) : System.Reflection.Metadata.ISignatureTypeProvider<string, object?>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => OwnerOf(reader, reader.GetTypeDefinition(handle));

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => TypeName(md, handle);

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => string.Empty;

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[,]";

        public string GetByReferenceType(string elementType) => elementType;

        public string GetPointerType(string elementType) => elementType;

        public string GetPinnedType(string elementType) => elementType;

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetFunctionPointerType(MethodSignature<string> signature) => string.Empty;

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;

        public string GetGenericMethodParameter(object? genericContext, int index) => string.Empty;

        public string GetGenericTypeParameter(object? genericContext, int index) => string.Empty;
    }

    private static string OwnerOf(MetadataReader md, TypeDefinition type)
    {
        while (!type.GetDeclaringType().IsNil)
        {
            type = md.GetTypeDefinition(type.GetDeclaringType());
        }

        return md.GetString(type.Namespace) + "." + md.GetString(type.Name);
    }

    // The scope and, transitively, every other type of the assembly that the scope's methods reach: a method call (a lossy decoder reaching a
    // helper in <project>.Decoding.Lossless), a method of a generic type instance of the assembly (Ring<float>.Push), and an interface call, which
    // reaches every type of the assembly that implements the interface (an interface of the assembly or one of Encspect's other assemblies).
    // Calls into other assemblies are scanned as their own scope.
    internal static Func<string, bool> WithCalledTypes(Assembly assembly, Func<string, bool> inScope)
    {
        using FileStream file = File.OpenRead(assembly.Location);
        using var pe = new PEReader(file);
        MetadataReader md = pe.GetMetadataReader();
        var names = new NameProvider(md);
        var included = new HashSet<string>();
        var calledInterfaces = new HashSet<string>();
        var implemented = new Dictionary<string, List<string>>();
        foreach (TypeDefinitionHandle handle in md.TypeDefinitions)
        {
            TypeDefinition type = md.GetTypeDefinition(handle);
            foreach (InterfaceImplementationHandle implementation in type.GetInterfaceImplementations())
            {
                EntityHandle contract = md.GetInterfaceImplementation(implementation).Interface;
                string name = contract.Kind switch
                {
                    HandleKind.TypeDefinition => OwnerOf(md, md.GetTypeDefinition((TypeDefinitionHandle)contract)),
                    HandleKind.TypeReference => TypeName(md, (TypeReferenceHandle)contract),
                    HandleKind.TypeSpecification => md.GetTypeSpecification((TypeSpecificationHandle)contract).DecodeSignature(names, null),
                    _ => string.Empty,
                };
                if (!implemented.TryGetValue(name, out List<string>? owners))
                {
                    implemented[name] = owners = [];
                }

                owners.Add(OwnerOf(md, type));
            }
        }

        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (TypeDefinitionHandle handle in md.TypeDefinitions)
            {
                TypeDefinition type = md.GetTypeDefinition(handle);
                string owner = OwnerOf(md, type);
                if (!(inScope(owner) || included.Contains(owner)))
                {
                    continue;
                }

                foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                {
                    MethodDefinition method = md.GetMethodDefinition(methodHandle);
                    if (method.RelativeVirtualAddress == 0)
                    {
                        continue;
                    }

                    foreach (EntityHandle token in BoundaryScanner.TokensIn(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!, md.GetString(method.Name)))
                    {
                        EntityHandle target = token.Kind == HandleKind.MethodSpecification ? md.GetMethodSpecification((MethodSpecificationHandle)token).Method : token;
                        string? called = null;
                        string? contract = null;
                        if (target.Kind == HandleKind.MethodDefinition)
                        {
                            TypeDefinition declaring = md.GetTypeDefinition(md.GetMethodDefinition((MethodDefinitionHandle)target).GetDeclaringType());
                            called = OwnerOf(md, declaring);
                            contract = (declaring.Attributes & System.Reflection.TypeAttributes.Interface) != 0 ? called : null;
                        }
                        else if (target.Kind == HandleKind.MemberReference)
                        {
                            MemberReference member = md.GetMemberReference((MemberReferenceHandle)target);
                            string parent = member.Parent.Kind switch
                            {
                                HandleKind.TypeSpecification => md.GetTypeSpecification((TypeSpecificationHandle)member.Parent).DecodeSignature(names, null),
                                HandleKind.TypeReference => TypeName(md, (TypeReferenceHandle)member.Parent),
                                _ => string.Empty,
                            };
                            called = member.Parent.Kind == HandleKind.TypeSpecification ? parent : null;
                            contract = parent.StartsWith("Encspect.", StringComparison.Ordinal) ? parent : null;
                        }

                        if (called is not null)
                        {
                            grew |= included.Add(called) && md.TypeDefinitions.Any(t => OwnerOf(md, md.GetTypeDefinition(t)) == called);
                        }

                        if (contract is not null && calledInterfaces.Add(contract) && implemented.TryGetValue(contract, out List<string>? owners))
                        {
                            foreach (string implementation in owners)
                            {
                                grew |= included.Add(implementation);
                            }
                        }
                    }
                }
            }
        }

        return owner => inScope(owner) || included.Contains(owner);
    }

    internal static IReadOnlyList<string> BannedCalls(Assembly assembly, Func<string, bool> inScope)
    {
        using FileStream file = File.OpenRead(assembly.Location);
        using var pe = new PEReader(file);
        MetadataReader md = pe.GetMetadataReader();
        var found = new List<string>();
        foreach (TypeDefinitionHandle handle in md.TypeDefinitions)
        {
            TypeDefinition type = md.GetTypeDefinition(handle);
            TypeDefinition outer = type;
            while (!outer.GetDeclaringType().IsNil)
            {
                outer = md.GetTypeDefinition(outer.GetDeclaringType());
            }

            string owner = md.GetString(outer.Namespace) + "." + md.GetString(outer.Name);
            if (!inScope(owner))
            {
                continue;
            }

            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = md.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                byte[] il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
                foreach (EntityHandle token in BoundaryScanner.TokensIn(il, md.GetString(method.Name)))
                {
                    MethodSpecificationHandle? instantiation = token.Kind == HandleKind.MethodSpecification ? (MethodSpecificationHandle)token : null;
                    EntityHandle target = instantiation is { } spec ? md.GetMethodSpecification(spec).Method : token;
                    if (target.Kind != HandleKind.MemberReference)
                    {
                        continue;
                    }

                    MemberReference member = md.GetMemberReference((MemberReferenceHandle)target);
                    string name = md.GetString(member.Name);
                    string parent = member.Parent.Kind switch
                    {
                        HandleKind.TypeReference => TypeName(md, (TypeReferenceHandle)member.Parent),
                        HandleKind.TypeSpecification => md.GetTypeSpecification((TypeSpecificationHandle)member.Parent).DecodeSignature(new NameProvider(md), null),
                        _ => string.Empty,
                    };
                    bool banned = IsBanned(parent, name) || IsBannedReduction(parent, name, () => ReductionTypes(md, member, instantiation));
                    if (banned)
                    {
                        found.Add($"{owner}::{md.GetString(method.Name)} calls {parent}.{name}");
                    }
                }
            }
        }

        return found;
    }

    [Fact]
    public void TheMeasurementCodeCallsNoLibraryMathNoFusedOrNativeOrEstimateOperationAndNoFloatingPointReduction()
    {
        Assembly host = typeof(FftPlan).Assembly;
        Func<string, bool> scope = owner => owner.StartsWith("Encspect.Host.Measurement.", StringComparison.Ordinal) || owner == "Encspect.Host.MeasurementText";
        IReadOnlyList<string> found = BannedCalls(host, WithCalledTypes(host, scope));
        Assert.Empty(found);

        // The scan reads something: the measurement namespace has methods that call Math.Sqrt-like members and the like.
        Assert.Contains(typeof(FftPlan).Assembly.GetTypes(), t => t.Namespace == "Encspect.Host.Measurement");

        // DetMath lives in the core, where the decoders reach it too.
        Assert.Empty(BannedCalls(typeof(DetMath).Assembly, owner => owner == "Encspect.Core.DetMath"));
    }

    // The nine lossy format assemblies, whole: a decoder and the parser code it shares compute under the same rules as the measurements
    // (docs/design/lossy-decoders.md, "Decoders fall under the determinism rules"). A lossless decoder in one of them lives in
    // <project>.Decoding.Lossless, which the scan leaves out so that it may use Vector<T>.
    internal static readonly string[] LossyAssemblies =
    [
        "Encspect.Formats.MpegAudio", "Encspect.Formats.Vorbis", "Encspect.Formats.Opus", "Encspect.Formats.Ac3", "Encspect.Formats.Musepack",
        "Encspect.Formats.Dts", "Encspect.Formats.Speex", "Encspect.Formats.Aac", "Encspect.Formats.Wma",
    ];

    internal static bool IsLossy(string owner) => !owner.Contains(".Decoding.Lossless.", StringComparison.Ordinal);

    [Fact]
    public void TheLossyFormatAssembliesCallNoLibraryMathNoFusedOrNativeOrEstimateOperationAndNoFloatingPointReduction()
    {
        var found = new List<string>();
        foreach (string name in LossyAssemblies)
        {
            Assembly assembly = Assembly.Load(name);
            Assert.NotEmpty(assembly.GetTypes());
            found.AddRange(BannedCalls(assembly, WithCalledTypes(assembly, IsLossy)).Select(f => $"{name}: {f}"));
        }

        // The core code those assemblies and the measurements call (readers, mapped bytes, the shared lists), whole (DetMath included).
        found.AddRange(BannedCalls(typeof(DetMath).Assembly, _ => true).Select(f => $"Encspect.Core.Abstractions: {f}"));

        Assert.True(found.Count == 0, string.Join('\n', found));
    }

    [Fact]
    public void TheScanFollowsCallsIntoHelpersOutsideItsScope()
    {
        Assembly tests = typeof(MeasurementDeterminismTests).Assembly;
        Func<string, bool> scope = owner => owner == "Encspect.Tests.Measurement.TransitiveCaller";
        Assert.Empty(BannedCalls(tests, scope));
        Assert.Contains(BannedCalls(tests, WithCalledTypes(tests, scope)), f => f.EndsWith("Math.Cos", StringComparison.Ordinal));

        Func<string, bool> generic = owner => owner == "Encspect.Tests.Measurement.TransitiveGenericCaller";
        Assert.Empty(BannedCalls(tests, generic));
        Assert.Contains(BannedCalls(tests, WithCalledTypes(tests, generic)), f => f.EndsWith("Math.Sin", StringComparison.Ordinal));

        Func<string, bool> viaInterface = owner => owner == "Encspect.Tests.Measurement.TransitiveInterfaceCaller";
        Assert.Empty(BannedCalls(tests, viaInterface));
        Assert.Contains(BannedCalls(tests, WithCalledTypes(tests, viaInterface)), f => f.EndsWith("Math.Tan", StringComparison.Ordinal));
    }

    [Fact]
    public void TheScanLeavesOutTheLosslessDecodersOfALossyProject()
    {
        Assert.True(IsLossy("Encspect.Formats.Wma.Decoding.Foo"));
        Assert.True(IsLossy("Encspect.Formats.Wma.Parser"));
        Assert.False(IsLossy("Encspect.Formats.Wma.Decoding.Lossless.Foo"));
        Assert.False(IsLossy("Encspect.Formats.Dts.Decoding.Lossless.Xll"));
    }

    [Fact]
    public void TheNegativeControlFindsEveryBannedCall()
    {
        IReadOnlyList<string> found = BannedCalls(typeof(MeasurementDeterminismTests).Assembly, owner => owner == "Encspect.Tests.Measurement.BannedSamples");

        foreach (string call in new[] { "Math.FusedMultiplyAdd", "Math.Sin", "Math.Pow", "Math.Log10", "Vector128.ConvertToInt64Native", "Vector128.MaxNative", "Vector128.FusedMultiplyAdd", "Math.ReciprocalEstimate", "NFloat.Sin", "Double.FusedMultiplyAdd", "Double.MultiplyAddEstimate", "Double.ReciprocalEstimate", "Double.Sin", "Double.Log10", "Double.Exp", "Double.Pow", "Single.Cos", "Single.Hypot", "Fma.MultiplyAdd", "Fma.MultiplyAddScalar", "Fma.MultiplySubtract", "Sse.Reciprocal", "Sse41.DotProduct", "Sse3.HorizontalAdd", "Avx.HorizontalAdd", "Arm64.AddPairwise", "Arm64.AddAcross", "Sse.ReciprocalSqrt", "AdvSimd.FusedMultiplyAdd", "AdvSimd.ReciprocalEstimate", "Vector3.Length", "Vector2.Distance", "Vector128.Sin", "Vector128.Cos", "Vector128.SinCos", "Vector128.Exp", "Vector128.Log", "Vector128.Log2", "Vector128.Hypot", "Vector128.Lerp", "Complex.Exp", "Complex..ctor", "ArrayPool`1.get_Shared", "Enumerable.Sum", "Vector128.Sum", "Vector128.Dot", "System.Numerics.Vector`1.op_Addition", "System.Numerics.Vector`1..ctor" })
        {
            Assert.Contains(found, f => f.EndsWith(call, StringComparison.Ordinal));
        }

        // The reductions are told apart by their signatures: each overload kind over floating-point values is found, integers are not.
        IReadOnlyList<string> reductions = BannedCalls(typeof(MeasurementDeterminismTests).Assembly, owner => owner is "Encspect.Tests.Measurement.BannedReductions" or "Encspect.Tests.Measurement.AllowedReductions");
        foreach (string method in new[] { "SumOfDoubles", "SumOfFloats", "SumBySelectorToDouble", "MaxOfDoubles", "MaxBySelectorToDouble", "MinOfHalves", "AverageOfIntegers", "AggregateOfDoubles", "MaxByDoubleKey", "MinOfFloatsBySelector" })
        {
            Assert.Contains(reductions, f => f.Contains($"BannedReductions::{method} calls System.Linq.Enumerable.", StringComparison.Ordinal));
        }

        Assert.DoesNotContain(reductions, f => f.Contains("AllowedReductions::", StringComparison.Ordinal));

        // The rule bans by name, so a self-test of the allowed side too: a plain Complex-free sum of element-wise members stays clean.
        Assert.False(IsBanned("System.Runtime.Intrinsics.Vector128", "Max"));
        Assert.False(IsBanned("System.Runtime.Intrinsics.Vector128", "Add"));
        Assert.False(IsBanned("System.Math", "Sqrt"));
        Assert.False(IsBanned("System.Runtime.Intrinsics.X86.Sse", "Add"));
        Assert.False(IsBanned("System.Numerics.Vector3", "Add"));
        Assert.False(IsBanned("System.Double", "IsNaN"));
        Assert.True(IsBanned("System.Double", "Cbrt"));
        Assert.True(IsBanned("System.Numerics.ITrigonometricFunctions`1", "Sin"));

        // Allowed: Sqrt, Floor, Abs, Min.
        Assert.DoesNotContain(found, f => f.EndsWith("Math.Sqrt", StringComparison.Ordinal) || f.EndsWith("Math.Floor", StringComparison.Ordinal) || f.EndsWith("Math.Min", StringComparison.Ordinal));
    }

    // The pinned constants. A platform or a change to the arithmetic that differs fails the tests above; the new values print in the message.
    private const string RowsHash = "f3b5f53bd0bc966cd84a42188592bd0e525b699b681ec7ebc06df6f0a3d1fc41";
    private const string Rows1024Hash = "7b9f9a01c21fcdb6c2e71db025b77d5ed5bacae23ff1b5aea23500e15f67bd83";
    private const string Rows32768Hash = "2dc1e2f59ef7e098f0fe240d9e135e79620e30793f6bacbfaaf2986babec6236";
    private const string LevelsHash = "a49171a81aeb8ad635eab6462075ed9b9cb95c07c78541b77b7ac741119e0a80";
    private const string LowBitsPerSpanHash = "ea09e332c5c4afca329c637317fa945f31ffb6e645748a782a29bfb7175e3018";
    private const string KWeightingHash = "f6048a6ea38eab1d0c9da05b4f9590e11f6090e6ad1942b442b98b24b94e44f6";
    private const string LoudnessHash = "436f003bd4cbd71d3728431622583423b26e3917f29dd4544a7be3bf929f9b12";
    private const string LoudnessRawHashes = "fbe68095d1d1bf5f4db95b77e1d2f3449f85cff5b687dcf8812f47d63fbfd44e|f82afd7a3245c2a833c86bd213284821b58ad0b680534ee7175b6cc3ac0c7eb7";
    private const string TruePeakHash = "0461b026bc6590cab1f7574b47c344005fa715234296d6fc59f01b111b7c612f";
    private const string MathHash = "6654c27b9dc93be9cabd9f70dad935929bf08d83c724028dff4dd2334949a8c1";
}

// A caller in scope and the helper it calls, which is out of scope until the closure follows the call; never run.
internal static class TransitiveCaller
{
    public static double Call(double x) => TransitiveCallee.Helper(x);
}

internal static class TransitiveCallee
{
    public static double Helper(double x) => Math.Cos(x);
}

internal sealed class TransitiveGeneric<T>
{
    public double Helper(double x) => Math.Sin(x);
}

internal static class TransitiveGenericCaller
{
    public static double Call(double x) => new TransitiveGeneric<float>().Helper(x);
}

internal interface ITransitiveContract
{
    double Run(double x);
}

internal sealed class TransitiveImplementation : ITransitiveContract
{
    public double Run(double x) => Math.Tan(x);
}

internal static class TransitiveInterfaceCaller
{
    public static double Call(ITransitiveContract contract, double x) => contract.Run(x);
}

// Calls the tests' negative control finds; never run.
internal static class BannedSamples
{
    public static double Banned(double x, System.Runtime.Intrinsics.Vector128<double> v, System.Runtime.Intrinsics.Vector128<double> w, double[] values)
    {
        double sum = (double)System.Runtime.InteropServices.NFloat.Sin(new System.Runtime.InteropServices.NFloat(x)) + Math.FusedMultiplyAdd(x, x, x) + Math.Sin(x) + Math.Pow(x, x) + Math.Log10(x) + Math.ReciprocalEstimate(x) + values.Sum();
        sum += System.Runtime.Intrinsics.Vector128.ConvertToInt64Native(v)[0];
        sum += System.Runtime.Intrinsics.Vector128.MaxNative(v, w)[0];
        sum += System.Runtime.Intrinsics.Vector128.FusedMultiplyAdd(v, w, v)[0];
        sum += System.Runtime.Intrinsics.Vector128.Sum(v) + System.Runtime.Intrinsics.Vector128.Dot(v, w);
        sum += double.FusedMultiplyAdd(x, x, x) + double.MultiplyAddEstimate(x, x, x) + double.ReciprocalEstimate(x) + double.Sin(x) + double.Log10(x) + double.Exp(x) + double.Pow(x, x) + float.Cos((float)x) + float.Hypot((float)x, (float)x);
        sum += System.Runtime.Intrinsics.Vector128.Sin(v)[0] + System.Runtime.Intrinsics.Vector128.Cos(v)[0] + System.Runtime.Intrinsics.Vector128.SinCos(v).Sin[0] + System.Runtime.Intrinsics.Vector128.Exp(v)[0];
        sum += System.Runtime.Intrinsics.Vector128.Log(v)[0] + System.Runtime.Intrinsics.Vector128.Log2(v)[0] + System.Runtime.Intrinsics.Vector128.Hypot(v, w)[0] + System.Runtime.Intrinsics.Vector128.Lerp(v, w, v)[0];
        sum += System.Numerics.Complex.Exp(new System.Numerics.Complex(x, x)).Real;
        double[] rented = System.Buffers.ArrayPool<double>.Shared.Rent(4);
        sum += rented.Length;
        var wide = new System.Numerics.Vector<double>(x);
        sum += (wide + wide)[0];
        var f = System.Runtime.Intrinsics.Vector128.Create((float)x);
        sum += System.Runtime.Intrinsics.X86.Fma.MultiplyAdd(f, f, f)[0] + System.Runtime.Intrinsics.X86.Fma.MultiplyAddScalar(f, f, f)[0] + System.Runtime.Intrinsics.X86.Fma.MultiplySubtract(f, f, f)[0];
        sum += System.Runtime.Intrinsics.X86.Sse41.DotProduct(f, f, 0xFF)[0] + System.Runtime.Intrinsics.X86.Sse3.HorizontalAdd(f, f)[0] + System.Runtime.Intrinsics.X86.Avx.HorizontalAdd(System.Runtime.Intrinsics.Vector256.Create(f, f), System.Runtime.Intrinsics.Vector256.Create(f, f))[0];
        sum += System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.AddPairwise(f, f)[0] + System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.AddAcross(System.Runtime.Intrinsics.Vector128.Create((byte)1))[0];
        sum += System.Runtime.Intrinsics.X86.Sse.Reciprocal(f)[0] + System.Runtime.Intrinsics.X86.Sse.ReciprocalSqrt(f)[0];
        sum += System.Runtime.Intrinsics.Arm.AdvSimd.FusedMultiplyAdd(f, f, f)[0] + System.Runtime.Intrinsics.Arm.AdvSimd.ReciprocalEstimate(f)[0];
        sum += new System.Numerics.Vector3((float)x).Length() + System.Numerics.Vector2.Distance(new System.Numerics.Vector2(1), new System.Numerics.Vector2(2));
        sum += Math.Sqrt(x) + Math.Floor(x) + Math.Min(x, sum);
        return sum;
    }
}

// Reductions the scan must find, one method each so that the negative control names the overload it covers; never run.
internal static class BannedReductions
{
    public static double SumOfDoubles(double[] values) => values.Sum();

    public static float SumOfFloats(float[] values) => values.Sum();

    public static double SumBySelectorToDouble(int[] values) => values.Sum(v => v * 0.5);

    public static double MaxOfDoubles(List<double> values) => values.Max();

    public static double MaxBySelectorToDouble(int[] values) => values.Max(v => v * 0.5);

    public static Half MinOfHalves(Half[] values) => values.Min();

    public static double AverageOfIntegers(int[] values) => values.Average();

    public static double AggregateOfDoubles(int[] values) => values.Aggregate(0.0, (sum, v) => sum + v);

    public static int MaxByDoubleKey(int[] values) => values.MaxBy(v => v * 0.5);

    public static float MinOfFloatsBySelector(int[] values) => values.Min(v => (float)v);
}

// Reductions over integers, which are exact in any order and stay allowed; never run.
internal static class AllowedReductions
{
    public static int Sum(int[] values) => values.Sum();

    public static long SumBySelector(int[] values) => values.Sum(v => (long)v);

    public static long Max(List<long> values) => values.Max();

    public static int MinBySelector(string[] values) => values.Min(v => v.Length);

    public static int Aggregate(int[] values) => values.Aggregate(0, (sum, v) => sum + v);

    public static string MaxByKey(string[] values) => values.MaxBy(v => v.Length)!;
}
