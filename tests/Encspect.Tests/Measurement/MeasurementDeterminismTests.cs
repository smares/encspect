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
        foreach (ChannelLevels c in levels.Channels.Prepend(levels.Stream))
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{c.SamplePeakTenths},{c.RmsTenths},{c.DcOffsetMicropercent},{c.DcOffsetTenths},{c.ClippingRuns},{c.ClippedSamples},{c.LongestClippingRun},{c.SilentAtStart},{c.SilentAtEnd},{c.ZeroSamples},{c.LowBitsUnused},{c.HighestMagnitude},{c.HasOnly16BitValues},{c.HasOnly24BitValues};");
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

        string all = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(integers16 + integers24 + float32)));
        Assert.True(LevelsHash == all, $"LevelsHash = {all} (16 bits {integers16[..8]}, 24 bits {integers24[..8]}, floats {float32[..8]})");
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

    private static readonly HashSet<string> BannedMath =
    [
        "FusedMultiplyAdd", "Sin", "Cos", "Tan", "Sinh", "Cosh", "Tanh", "Asin", "Acos", "Atan", "Atan2", "Asinh", "Acosh", "Atanh", "Log", "Log10",
        "Log2", "Exp", "Pow", "Cbrt", "SinCos", "ScaleB", "ILogB", "ReciprocalEstimate", "ReciprocalSqrtEstimate", "BitIncrement", "BitDecrement",
    ];

    private static readonly HashSet<string> BannedLinq = ["Sum", "Average", "Max", "Min", "Aggregate", "MaxBy", "MinBy"];

    private static string TypeName(MetadataReader md, TypeReferenceHandle handle)
    {
        TypeReference type = md.GetTypeReference(handle);
        return md.GetString(type.Namespace) + "." + md.GetString(type.Name);
    }

    // The name of a generic type instance's definition (System.Numerics.Vector`1 for Vector<double>), which a member reference's parent is.
    private sealed class NameProvider(MetadataReader md) : System.Reflection.Metadata.ISignatureTypeProvider<string, object?>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => string.Empty;

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
                    EntityHandle target = token.Kind == HandleKind.MethodSpecification ? md.GetMethodSpecification((MethodSpecificationHandle)token).Method : token;
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
                    bool banned = (parent is "System.Math" or "System.MathF" && BannedMath.Contains(name))
                        || (parent.StartsWith("System.Runtime.Intrinsics", StringComparison.Ordinal) || parent.StartsWith("System.Numerics", StringComparison.Ordinal) || parent is "System.Math" or "System.MathF")
                            && (name.EndsWith("Native", StringComparison.Ordinal) || name.EndsWith("Estimate", StringComparison.Ordinal) || name.Contains("FusedMultiplyAdd", StringComparison.Ordinal))
                        || parent.StartsWith("System.Numerics.Tensors", StringComparison.Ordinal)
                        || (parent.StartsWith("System.Runtime.Intrinsics", StringComparison.Ordinal) || parent.StartsWith("System.Numerics", StringComparison.Ordinal)) && name is "Sum" or "Dot"
                        || parent is "System.Numerics.Vector`1" or "System.Numerics.Vector"
                        || (parent == "System.Linq.Enumerable" && BannedLinq.Contains(name))
                        || (parent == "System.MemoryExtensions" && name is "Sum" or "Max" or "Min");
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
        IReadOnlyList<string> found = BannedCalls(host, owner => owner.StartsWith("Encspect.Host.Measurement.", StringComparison.Ordinal) || owner == "Encspect.Host.MeasurementText");
        Assert.Empty(found);

        // The scan reads something: the measurement namespace has methods that call Math.Sqrt-like members and the like.
        Assert.Contains(typeof(FftPlan).Assembly.GetTypes(), t => t.Namespace == "Encspect.Host.Measurement");
    }

    [Fact]
    public void TheNegativeControlFindsEveryBannedCall()
    {
        IReadOnlyList<string> found = BannedCalls(typeof(MeasurementDeterminismTests).Assembly, owner => owner == "Encspect.Tests.Measurement.BannedSamples");

        foreach (string call in new[] { "Math.FusedMultiplyAdd", "Math.Sin", "Math.Pow", "Math.Log10", "Vector128.ConvertToInt64Native", "Vector128.MaxNative", "Vector128.FusedMultiplyAdd", "Math.ReciprocalEstimate", "Enumerable.Sum", "Vector128.Sum", "Vector128.Dot", "System.Numerics.Vector`1.op_Addition", "System.Numerics.Vector`1..ctor" })
        {
            Assert.Contains(found, f => f.EndsWith(call, StringComparison.Ordinal));
        }

        // Allowed: Sqrt, Floor, Abs, Min.
        Assert.DoesNotContain(found, f => f.EndsWith("Math.Sqrt", StringComparison.Ordinal) || f.EndsWith("Math.Floor", StringComparison.Ordinal) || f.EndsWith("Math.Min", StringComparison.Ordinal));
    }

    // The pinned constants. A platform or a change to the arithmetic that differs fails the tests above; the new values print in the message.
    private const string RowsHash = "f3b5f53bd0bc966cd84a42188592bd0e525b699b681ec7ebc06df6f0a3d1fc41";
    private const string Rows1024Hash = "7b9f9a01c21fcdb6c2e71db025b77d5ed5bacae23ff1b5aea23500e15f67bd83";
    private const string Rows32768Hash = "2dc1e2f59ef7e098f0fe240d9e135e79620e30793f6bacbfaaf2986babec6236";
    private const string LevelsHash = "f92d52741857da7606e0932e860091e47898d604dbfb1bbd97ffde473c022fbc";
    private const string KWeightingHash = "f6048a6ea38eab1d0c9da05b4f9590e11f6090e6ad1942b442b98b24b94e44f6";
    private const string LoudnessHash = "436f003bd4cbd71d3728431622583423b26e3917f29dd4544a7be3bf929f9b12";
    private const string TruePeakHash = "0461b026bc6590cab1f7574b47c344005fa715234296d6fc59f01b111b7c612f";
    private const string MathHash = "6654c27b9dc93be9cabd9f70dad935929bf08d83c724028dff4dd2334949a8c1";
}

// Calls the tests' negative control finds; never run.
internal static class BannedSamples
{
    public static double Banned(double x, System.Runtime.Intrinsics.Vector128<double> v, System.Runtime.Intrinsics.Vector128<double> w, double[] values)
    {
        double sum = Math.FusedMultiplyAdd(x, x, x) + Math.Sin(x) + Math.Pow(x, x) + Math.Log10(x) + Math.ReciprocalEstimate(x) + values.Sum();
        sum += System.Runtime.Intrinsics.Vector128.ConvertToInt64Native(v)[0];
        sum += System.Runtime.Intrinsics.Vector128.MaxNative(v, w)[0];
        sum += System.Runtime.Intrinsics.Vector128.FusedMultiplyAdd(v, w, v)[0];
        sum += System.Runtime.Intrinsics.Vector128.Sum(v) + System.Runtime.Intrinsics.Vector128.Dot(v, w);
        var wide = new System.Numerics.Vector<double>(x);
        sum += (wide + wide)[0];
        sum += Math.Sqrt(x) + Math.Floor(x) + Math.Min(x, sum);
        return sum;
    }
}
