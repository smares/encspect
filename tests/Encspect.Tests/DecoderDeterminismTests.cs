using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The lossy decoders compute in floating point and fall under the determinism rules of the measurements (docs/design/lossy-decoders.md, "Decoders fall
// under the determinism rules"): their raw output hashes to the same value on every platform and in the NativeAOT build. The decoders of the formats
// bring their own cases when they land (each fixture's pcmSha256 in its .decode.json golden); these tests hold the machinery with a scripted float
// decoder written the way the rules ask: tables from DetMath rounded to float, a generator with a fixed seed that restarts after a gap, an element-wise
// Vector128 kernel with a scalar reference it equals bit for bit, and a recursive filter flushed at the start of each unit.
public class DecoderDeterminismTests
{
    private const int UnitSamples = 100;

    // The scripted decoder's output for the stream below, on every platform. A change to DetMath or to the rules the decoder follows fails here first; the
    // new value prints in the message.
    private const string ScriptedHash = "40d554ddf5ab8d91ceda8773302f51d99e3e8633f72a4d2fd5fd90a186fa4fcb";

    private const string ScriptedGappedHash = "f2fee6d7ff59e2a75325456bdde2e98d0491f97e4fa57fc3dc57ec62711f0d73";

    internal sealed class ScriptedFloatDecoder(bool usesVectors = true) : IDecoder
    {
        public string Id => "test";

        public bool DeclaresChecksums => false;

        public IFrameDecoder Open(DecodeContext context) => new Frames(context, usesVectors);

        private sealed class Frames : IFrameDecoder
        {
            private const int WindowLength = 128;

            private readonly DecodeContext _context;
            private readonly bool _usesVectors;
            private readonly float[] _window = new float[WindowLength];
            private readonly float[] _state = new float[2];
            private readonly float[] _noise = new float[UnitSamples];
            private readonly float[] _planar = new float[2 * UnitSamples];
            private IPcmSink? _sink;
            private uint _seed = Seed;

            private const uint Seed = 0x2545F491;

            public Frames(DecodeContext context, bool usesVectors)
            {
                _context = context;
                _usesVectors = usesVectors;

                // Tables are computed once in double with DetMath and rounded to float.
                for (int i = 0; i < WindowLength; i++)
                {
                    _window[i] = (float)DetMath.Sin(DetMath.Pi * (i + 0.5) / WindowLength);
                }
            }

            public int Decode(in CodedFrame frame)
            {
                _sink ??= _context.DeclareOutput(new PcmFormat(48000, 2, 32, SampleFormat.Float32));
                _context.AddWork(DecodeRouteTests.Charge(_context.Setup, frame.Samples));

                // After a gap the generator restarts at its seed and the recursive state is reset.
                if (frame.FollowsGap)
                {
                    _seed = Seed;
                    Array.Clear(_state);
                }

                // The start of each unit flushes the recursive state below 1e-30.
                for (int c = 0; c < _state.Length; c++)
                {
                    if (Math.Abs(_state[c]) < 1e-30f)
                    {
                        _state[c] = 0;
                    }
                }

                for (int i = 0; i < UnitSamples; i++)
                {
                    _seed = unchecked((_seed * 1664525u) + 1013904223u);
                    _noise[i] = ((int)((_seed >> 8) & 0xFFFF) - 32768) * (1f / 32768f) * _window[i % WindowLength];
                }

                for (int c = 0; c < 2; c++)
                {
                    Span<float> plane = _planar.AsSpan(c * UnitSamples, UnitSamples);
                    Filter(_noise, ref _state[c], plane);
                    if (_usesVectors)
                    {
                        ScaleVector(plane, 0.5f + (0.25f * c));
                    }
                    else
                    {
                        ScaleScalar(plane, 0.5f + (0.25f * c));
                    }
                }

                _sink.Write(new PcmBlock(frame.FirstSample, UnitSamples, 2, UnitSamples, _planar));
                return UnitSamples;
            }

            public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end) => [];
        }
    }

    // A one-pole recursive filter, in the order of the samples.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Filter(ReadOnlySpan<float> input, ref float state, Span<float> output)
    {
        float z = state;
        for (int i = 0; i < input.Length; i++)
        {
            z = input[i] + (0.875f * z);
            output[i] = z;
        }

        state = z;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void ScaleScalar(Span<float> samples, float gain)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (samples[i] * gain) + 0.0009765625f;
        }
    }

    // Element-wise: each lane computes what the scalar loop computes for its sample, a multiply and then an add, never fused.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void ScaleVector(Span<float> samples, float gain)
    {
        Vector128<float> g = Vector128.Create(gain);
        Vector128<float> offset = Vector128.Create(0.0009765625f);
        int i = 0;
        for (; i + Vector128<float>.Count <= samples.Length; i += Vector128<float>.Count)
        {
            Vector128<float> v = Vector128.Create<float>(samples.Slice(i, Vector128<float>.Count));
            ((v * g) + offset).CopyTo(samples.Slice(i, Vector128<float>.Count));
        }

        for (; i < samples.Length; i++)
        {
            samples[i] = (samples[i] * gain) + 0.0009765625f;
        }
    }

    private static (Report Report, string Hash) Run(IDecoder decoder, Func<int, CodedFrame, CodedFrame>? shape = null, bool measures = false)
    {
        var sink = new PcmHashSink();
        var options = new AnalysisOptions
        {
            Decoding = new DecodeOptions
            {
                Output = stream =>
                {
                    sink.Format(stream.Format);
                    return sink;
                },
                Measure = measures ? new MeasureOptions() : null,
            },
        };
        var parser = new DecodeRouteTests.FrameParser { BitsPerSample = 32, IsLossy = true, Shape = shape, Samples = 400 };
        Report report = new Analyzer([], [parser], [], [decoder]).Analyze("sample.bin", new MemoryStream(new byte[64]), options);
        return (report, sink.Hash());
    }

    private static string Json(Report report)
    {
        using var stream = new MemoryStream();
        ReportJsonWriter.Write([report], stream);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public void AScriptedFloatDecoderDeliversTheSameBitsOnEveryPlatform()
    {
        (Report report, string hash) = Run(new ScriptedFloatDecoder());

        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed");
        Assert.True(hash == ScriptedHash, hash);
    }

    [Fact]
    public void TheVectorKernelAndItsScalarReferenceAgreeBitForBit()
    {
        (_, string vector) = Run(new ScriptedFloatDecoder(usesVectors: true));
        (_, string scalar) = Run(new ScriptedFloatDecoder(usesVectors: false));
        Assert.Equal(scalar, vector);

        // Lengths that leave a scalar tail.
        var random = new Random(7);
        foreach (int length in new[] { 1, 3, 4, 5, 97, 100, 1023 })
        {
            float[] a = Enumerable.Range(0, length).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray();
            float[] b = [.. a];
            ScaleVector(a, 0.7f);
            ScaleScalar(b, 0.7f);
            Assert.Equal(b.Select(BitConverter.SingleToInt32Bits), a.Select(BitConverter.SingleToInt32Bits));
        }
    }

    [Fact]
    public void AGapRestartsTheGeneratorAndTheStateAndPinsWhereItLies()
    {
        (Report gapped, string hash) = Run(new ScriptedFloatDecoder(), (i, f) => i == 2 ? f with { IsUnreadable = true } : f);
        (_, string whole) = Run(new ScriptedFloatDecoder());

        Assert.NotEqual(whole, hash);
        Assert.DoesNotContain(gapped.Findings, f => f.Code is "decode.failed" or "parse.failed");
        Assert.True(hash == ScriptedGappedHash, hash);
    }

    [Fact]
    public void TheFiguresOfTheScriptedStreamAreTheSameWhenItIsMeasured()
    {
        (Report first, _) = Run(new ScriptedFloatDecoder(), measures: true);
        (Report second, _) = Run(new ScriptedFloatDecoder(usesVectors: false), measures: true);

        Assert.Equal(MeasurementStatus.Complete, first.Streams[0].Measurements!.Status);
        Assert.False(first.Streams[0].Measurements!.HasClampedSamples);
        Assert.Equal(Json(first), Json(second));
    }

    [Fact]
    public void ThePcmHashIsPartOfALossyDecodeGoldenAndOnlyOfThat()
    {
        (Report report, string hash) = Run(new ScriptedFloatDecoder());

        string lossy = DecoderFixtureTests.Golden(report, hash);
        string lossless = DecoderFixtureTests.Golden(report);

        Assert.Contains($"\"pcmSha256\": \"{hash}\",\n  \"findings\"", lossy);
        Assert.DoesNotContain("pcmSha256", lossless);
        Assert.Equal(lossless.Replace("  \"findings\"", $"  \"pcmSha256\": \"{hash}\",\n  \"findings\""), lossy);
    }

    [Fact]
    public void TheHashDoesNotDependOnHowABlockIsCutAndPinsAJump()
    {
        float[] samples = Enumerable.Range(0, 200).Select(i => i * 0.001f).ToArray();
        var whole = new PcmHashSink();
        var cut = new PcmHashSink();
        var jumped = new PcmHashSink();
        foreach (PcmHashSink sink in new[] { whole, cut, jumped })
        {
            sink.Format(new PcmFormat(48000, 1, 32, SampleFormat.Float32));
        }

        whole.Write(new PcmBlock(0, 200, 1, 200, samples));
        cut.Write(new PcmBlock(0, 70, 1, 70, samples.AsSpan(0, 70)));
        cut.Write(new PcmBlock(70, 130, 1, 130, samples.AsSpan(70, 130)));
        jumped.Write(new PcmBlock(0, 70, 1, 70, samples.AsSpan(0, 70)));
        jumped.Write(new PcmBlock(80, 130, 1, 130, samples.AsSpan(70, 130)));

        string hash = whole.Hash();
        Assert.Equal(hash, cut.Hash());
        Assert.NotEqual(hash, jumped.Hash());
    }
}
