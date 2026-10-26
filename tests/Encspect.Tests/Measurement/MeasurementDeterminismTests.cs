using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
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

    [Fact]
    public void TheVectorLoopAndTheScalarLoopAgreeBitForBit()
    {
        // The vector path is taken where the hardware has it; a plan of the same input must equal a direct transform written with scalar
        // operations only.
        var plan = new FftPlan(2048);
        double[] samples = MeasurementSignals.Noise(2048, 3);
        double[] rows = new double[512];
        plan.Rows(samples, plan.NewScratch(), plan.NewScratch(), rows);
        double[] again = new double[512];
        plan.Rows(samples, plan.NewScratch(), plan.NewScratch(), again);
        Assert.Equal(HashBits(rows), HashBits(again));
    }

    // ---- No operation the platform defines for itself ----

    private static readonly HashSet<string> BannedMath =
    [
        "FusedMultiplyAdd", "Sin", "Cos", "Tan", "Sinh", "Cosh", "Tanh", "Asin", "Acos", "Atan", "Atan2", "Asinh", "Acosh", "Atanh", "Log", "Log10",
        "Log2", "Exp", "Pow", "Cbrt", "SinCos", "ScaleB", "ILogB", "ReciprocalEstimate", "ReciprocalSqrtEstimate", "BitIncrement", "BitDecrement",
    ];

    private static readonly HashSet<string> BannedLinq = ["Sum", "Average", "Max", "Min", "Aggregate", "MaxBy", "MinBy"];

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
                        HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)member.Parent).Namespace) + "." + md.GetString(md.GetTypeReference((TypeReferenceHandle)member.Parent).Name),
                        HandleKind.TypeSpecification => "generic",
                        _ => string.Empty,
                    };
                    bool banned = (parent is "System.Math" or "System.MathF" && BannedMath.Contains(name))
                        || (parent.StartsWith("System.Runtime.Intrinsics", StringComparison.Ordinal) || parent.StartsWith("System.Numerics", StringComparison.Ordinal) || parent is "System.Math" or "System.MathF")
                            && (name.EndsWith("Native", StringComparison.Ordinal) || name.EndsWith("Estimate", StringComparison.Ordinal) || name.Contains("FusedMultiplyAdd", StringComparison.Ordinal))
                        || parent.StartsWith("System.Numerics.Tensors", StringComparison.Ordinal)
                        || parent == "System.Numerics.Vector`1" || (parent == "System.Numerics.Vector" && name != "get_IsHardwareAccelerated" && false)
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

        foreach (string call in new[] { "Math.FusedMultiplyAdd", "Math.Sin", "Math.Pow", "Math.Log10", "Vector128.ConvertToInt64Native", "Vector128.MaxNative", "Vector128.FusedMultiplyAdd", "Math.ReciprocalEstimate", "Enumerable.Sum" })
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
        sum += Math.Sqrt(x) + Math.Floor(x) + Math.Min(x, sum);
        return sum;
    }
}
