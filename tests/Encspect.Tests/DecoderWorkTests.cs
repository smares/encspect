using System.Buffers.Binary;
using System.Diagnostics;
using Encspect.Core;
using Encspect.Formats.Flac;
using Encspect.Formats.Flac.Decoding;
using Encspect.Formats.MonkeysAudio.Decoding;
using Encspect.Formats.WavPack.Decoding;
using Encspect.Host;
using Xunit.Abstractions;
using TrueHdDecoder = Encspect.Formats.TrueHd.Decoding.TrueHdDecoder;

namespace Encspect.Tests;

// Holds every decoder to the time per work unit that the budget's bounds assume (docs/design/decoders.md, "How a decoder that
// under-reports is caught"). A decoder charges the work it does (DecodeContext.AddWork); if it charges too little for a stage, a crafted file
// that asks for that stage costs far more time than the bound allows. The floor of the charge catches only a missing per-sample charge, so
// this is the test that catches a forgotten pass, tap or byte, for the cases it includes.
//
//   - What is timed is decoding alone: the analysis with decoding on minus the analysis with it off, in the same round, so the time of the
//     parse and the walk (very different between a FLAC reference with a large structure pass and a WavPack block of 8 bytes) is out.
//     For FLAC the "off" run is a run with a hollow decoder (it charges the fixed work and does nothing else): the parser lends its buffers and
//     the walk stores into them in both, so a faster walk cannot inflate the difference (the second speed pass in docs/design/decoders.md).
//   - A wrapper around the real decoder sums the units it charges, so the report is the real one.
//   - A round runs the reference off, the reference on, the case off and the case on, five rounds, and the time of a run is the median of
//     the rounds' differences; the case's time per unit may be at most twice the reference's, and decoding must be at least a fifth of the
//     case's analysis with decoding on, so a case the parse dominates cannot pass on a difference of two noisy numbers.
//   - The reference is FLAC frames of 4,096 samples of 16-bit stereo with linear predictors of order 8 (the shape of flac level 5), whose
//     content is pinned: a residual of a fixed-seed pseudo-random signal of about 7 bits, Rice parameter 6, no escapes.
//   - The absolute time per unit of the reference and of each case is printed, so that the history of the runs shows slow drift.
//   - A case that fails means a weight is too low; the fix is the weight, not the factor.
//
// It runs alone (the ComplexityTests collection) and only in the nightly run (the trait), which takes about 5 minutes per platform; a pull
// request and a push to main exclude it with the filter Category!=Nightly (scripts/ci/changes.py).
[Collection(nameof(ComplexityTests))]
[Trait("Category", "Nightly")]
public sealed class DecoderWorkTests(ITestOutputHelper output, DecoderWorkTests.CaseCache cache) : IClassFixture<DecoderWorkTests.CaseCache>
{
    // The built inputs (about 200 MB together) are kept for the cases of one run of the class and released with it, not for the life of the process.
    public sealed class CaseCache : IDisposable
    {
        public void Dispose()
        {
            lock (Built)
            {
                Built.Clear();
            }
        }
    }

    // Each run is sized to charge about this many units, about 0.3 s at 0.6 ns a unit.
    internal const long Target = 1L << 29;

    private const int Rounds = 5;

    // A case that comes near the factor is fixed by raising the charge of the stage it undercharges and measuring again, never by raising the factor.
    private const double Factor = 2.0;

    private const double MinimumDecodingShare = 0.2;

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (string name in Cases.Keys)
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void ATimePerUnitWithinTwiceTheFlacReferences(string name)
    {
        GC.KeepAlive(cache);
        Case work = Cases[name]();
        Case reference = Cases["flac-reference"]();

        Measurement result = Measure(reference, work);

        output.WriteLine($"{name}: {result.Case.NanosecondsPerUnit:F3} ns a unit over {result.Case.Units:N0} units; the reference {result.Reference.NanosecondsPerUnit:F3} ns over {result.Reference.Units:N0}; ratio {result.Ratio:F2}; decoding {result.Case.Share:P0} of the analysis with decoding on");
        Assert.True(result.Case.Units > Target / 8, $"The case charged only {result.Case.Units:N0} units.");
        Assert.True(result.Case.Share >= MinimumDecodingShare, $"Decoding is only {result.Case.Share:P0} of the analysis with decoding on, so the parse dominates the figure.");
        Assert.True(result.Ratio <= Factor, $"The case takes {result.Case.NanosecondsPerUnit:F3} ns a unit against the reference's {result.Reference.NanosecondsPerUnit:F3}: {result.Ratio:F2} times, at most {Factor} are allowed. A weight is too low.");
    }

    // The charge of the reference itself is what the shape of its frames says, so the case that every other is compared with is not itself
    // a free-running figure: 24 units a sample of each channel (16 and the 8 taps), 4,096 samples a frame.
    [Fact]
    public void TheReferenceChargesWhatItsFramesSay()
    {
        Case reference = Cases["flac-reference"]();

        (_, long units, Report report) = Run(reference, decode: true);

        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed" or "decode.limit");
        Assert.Equal(0, units % (4096 * 2 * 24));
        Assert.InRange(units, Target * 0.9, Target * 1.1);
    }

    // One round of the FLAC reference: its time per work unit, decoding less the hollow decoder's, for MeasurementWorkTests to take beside each of
    // its cases in the same round (a load that comes and goes then moves both). The built reference stays until ReleaseReference.
    internal static double ReferenceRoundNanosecondsPerUnit()
    {
        Case reference = Cases["flac-reference"]();
        (double off, _, _) = Run(reference, decode: true, isHollow: true);
        (double on, long units, _) = Run(reference, decode: true);
        return (on - off) * 1e9 / units;
    }

    internal static void ReleaseReference()
    {
        lock (Built)
        {
            Built.Clear();
        }
    }

    // ----- The measurement -----

    private sealed record Case(string Name, byte[] Data, bool LendsBuffers = true, Func<IDecoder>? Decoder = null);

    private readonly record struct Figures(double Seconds, long Units)
    {
        public double NanosecondsPerUnit => Seconds * 1e9 / Units;

        public double Share { get; init; }
    }

    private sealed record Measurement(Figures Case, Figures Reference)
    {
        public double Ratio => Case.NanosecondsPerUnit / Reference.NanosecondsPerUnit;
    }

    // The decoder of a FLAC case in the run that stands for "decoding off": it charges the fixed work of every sample, as the host requires, and
    // does nothing else, so the parser lends its buffers and the walk stores into them as it does when the decoder works.
    private sealed class HollowDecoder(IDecoder real) : IDecoder
    {
        public string Id => real.Id;

        public long WorkPerInputByte => real.WorkPerInputByte;

        public bool DeclaresChecksums => real.DeclaresChecksums;

        public IFrameDecoder Open(DecodeContext context) => new Frames(context);

        private sealed class Frames(DecodeContext context) : IFrameDecoder
        {
            public int Decode(in CodedFrame frame)
            {
                context.AddWork(DecodeWork.ForSample(CanonicalPcm.BytesPerSample(context.Setup.BitsPerSample)) * context.Setup.Channels * frame.Samples);
                return frame.Samples;
            }

            public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end) => [];
        }
    }

    private static (double Seconds, long Units, Report Report) Run(Case work, bool decode, bool isHollow = false)
    {
        IDecoder real = work.Decoder?.Invoke() ?? DecoderOf(work.Name);
        var counting = new CountingDecoder(isHollow ? new HollowDecoder(real) : real);
        IEnumerable<IFormatParser> formats = DefaultParsers.Formats.Select(p => p is FlacParser && !work.LendsBuffers ? new FlacParser { LendsBuffers = false } : p);
        var analyzer = new Analyzer(DefaultParsers.Tags, formats, DefaultParsers.Codecs, [counting]);
        // A decoder with no checksum decodes only for a front end that wants the samples: the sink of a PCM case takes them and does nothing.
        var options = new AnalysisOptions { Decoding = decode ? new DecodeOptions { MaxWork = long.MaxValue, Output = real.DeclaresChecksums ? null : _ => new NullSink() } : null };
        using var stream = new MemoryStream(work.Data, writable: false);

        long start = Stopwatch.GetTimestamp();
        Report report = analyzer.Analyze(work.Name + ExtensionOf(work.Name), stream, options);
        double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        return (seconds, counting.Units, report);
    }

    internal sealed class NullSink : IPcmSink
    {
        public void Write(PcmBlock block)
        {
        }

        public void End(long samples)
        {
        }
    }

    private static IDecoder DecoderOf(string name) => name switch
    {
        _ when name.StartsWith("pcm", StringComparison.Ordinal) => new Encspect.Formats.Wav.Decoding.PcmDecoder(),
        _ when name.StartsWith("flac", StringComparison.Ordinal) => new FlacDecoder(),
        _ when name.StartsWith("monkeys-audio", StringComparison.Ordinal) => new MonkeysAudioDecoder(),
        _ when name.StartsWith("truehd", StringComparison.Ordinal) => TrueHdDecoder.TrueHd,
        _ when name.StartsWith("mlp", StringComparison.Ordinal) => TrueHdDecoder.Mlp,
        _ => new WavPackDecoder(),
    };

    private static string ExtensionOf(string name) => name switch
    {
        _ when name.StartsWith("flac", StringComparison.Ordinal) => ".flac",
        _ when name.StartsWith("monkeys-audio", StringComparison.Ordinal) => ".ape",
        _ when name.StartsWith("truehd", StringComparison.Ordinal) => ".thd",
        _ when name.StartsWith("mlp", StringComparison.Ordinal) => ".mlp",
        _ when name.StartsWith("pcm-aiff", StringComparison.Ordinal) => ".aiff",
        _ when name.StartsWith("pcm", StringComparison.Ordinal) => ".wav",
        _ => ".wv",
    };

    private static Measurement Measure(Case reference, Case work)
    {
        // One analysis of each for the JIT and the caches, then the rounds.
        Run(reference, decode: true);
        Run(work, decode: true);

        var referenceDiffs = new List<double>();
        var caseDiffs = new List<double>();
        var caseOns = new List<double>();
        long referenceUnits = 0, caseUnits = 0;
        for (int round = 0; round < Rounds; round++)
        {
            GC.Collect();
            (double referenceOff, _, _) = Run(reference, decode: true, isHollow: true);
            (double referenceOn, long referenceCharged, Report referenceReport) = Run(reference, decode: true);
            (double caseOff, _, _) = IsFlac(work) ? Run(work, decode: true, isHollow: true) : Run(work, decode: false);
            (double caseOn, long caseCharged, Report caseReport) = Run(work, decode: true);

            Assert.DoesNotContain(referenceReport.Findings, f => f.Code is "parse.failed" or "decode.failed" or "decode.limit");
            Assert.DoesNotContain(caseReport.Findings, f => f.Code is "parse.failed" or "decode.failed" or "decode.limit");
            referenceDiffs.Add(referenceOn - referenceOff);
            caseDiffs.Add(caseOn - caseOff);
            caseOns.Add(caseOn);
            referenceUnits = referenceCharged;
            caseUnits = caseCharged;
        }

        double caseDiff = Median(caseDiffs);
        return new Measurement(
            new Figures(caseDiff, caseUnits) { Share = caseDiff / Median(caseOns) },
            new Figures(Median(referenceDiffs), referenceUnits));
    }

    private static bool IsFlac(Case work) => work.LendsBuffers && work.Name.StartsWith("flac", StringComparison.Ordinal);

    private static double Median(List<double> values)
    {
        double[] sorted = [.. values.Order()];
        return sorted[sorted.Length / 2];
    }

    // ----- The cases -----

    private static readonly Dictionary<string, Func<Case>> Cases = BuildCases();

    private static readonly Dictionary<string, Case> Built = [];

    private static Dictionary<string, Func<Case>> BuildCases()
    {
        var cases = new Dictionary<string, Func<Case>>();

        void Add(string name, Func<Case> build) => cases[name] = () =>
        {
            lock (Built)
            {
                if (!Built.TryGetValue(name, out Case? built))
                {
                    Built[name] = built = build();
                }

                return built;
            }
        };

        // FLAC. The reference first.
        Add("flac-reference", () => new Case("flac-reference", FlacCases.Reference()));
        Add("flac-constant-8-channels-32-bits", () => new Case("flac-constant-8-channels-32-bits", FlacCases.Constant8Channels32Bits()));
        Add("flac-verbatim-32-bits", () => new Case("flac-verbatim-32-bits", FlacCases.Verbatim32Bits()));
        Add("flac-order-32-in-64-bits", () => new Case("flac-order-32-in-64-bits", FlacCases.Order32In64Bits()));
        Add("flac-fixed-order-4", () => new Case("flac-fixed-order-4", FlacCases.FixedOrder4()));
        Add("flac-not-walked-escapes-and-long-unary-codes", () => new Case("flac-not-walked-escapes-and-long-unary-codes", FlacCases.EscapesAndLongUnaryCodes(), LendsBuffers: false));

        // Monkey's Audio: every level, the widest samples, the flagged frames, and the cheapest symbol. Each case repeats the frames an encoder wrote.
        foreach ((string label, string file) in new[]
        {
            ("fast", "external-level-fast-16-stereo.ape"),
            ("normal", "external-level-normal-16-stereo.ape"),
            ("high", "external-level-high-16-stereo.ape"),
            ("extra-high", "external-level-extra-high-16-stereo.ape"),
            ("insane", "external-level-insane-16-stereo.ape"),
            ("insane-mono-8-bit", "external-insane-8-mono.ape"),
            ("insane-24-bit", "external-insane-24-stereo.ape"),
            // A frame of the full 1,179,648 samples that the highest level uses: the frame of 24-bit audio is held in memory until its CRC has passed.
            ("insane-24-bit-full-frames", "external-full-frame-insane-24-mono.ape"),
            ("insane-32-bit", "external-insane-32-stereo.ape"),
            ("insane-pseudo-stereo", "external-pseudo-stereo-insane.ape"),
            ("insane-cheapest-symbol", "external-quiet-insane.ape"),
            ("normal-6-channels", "external-normal-16-5.1.ape"),
            ("normal-24-bit-8-channels", "external-normal-24-7.1.ape"),
            ("normal-float", "external-normal-float-stereo.ape"),
        })
        {
            Add("monkeys-audio-" + label, () => new Case("monkeys-audio-" + label, MonkeysAudioCases.Repeated(file)));
        }

        // Frames flagged silent charge the fixed work of their samples and no more, at the level whose coded frames cost the most.
        Add("monkeys-audio-insane-silent-frames", () => new Case("monkeys-audio-insane-silent-frames", MonkeysAudioCases.Repeated("external-silent-stereo.ape", level: 5000)));

        // WavPack: the crafted worst cases, all 16 decorrelation passes.
        Add("wavpack-term-17-zero-run", () => new Case("wavpack-term-17-zero-run", WavPackCases.ZeroRuns(terms: [17], channels: 1)));
        Add("wavpack-term-17", () => new Case("wavpack-term-17", WavPackCases.Passes([17], channels: 2)));
        Add("wavpack-term-18", () => new Case("wavpack-term-18", WavPackCases.Passes([18], channels: 2)));
        Add("wavpack-terms-1-to-8", () => new Case("wavpack-terms-1-to-8", WavPackCases.Passes([1, 2, 3, 4, 5, 6, 7, 8], channels: 2)));
        Add("wavpack-terms-minus-1-to-minus-3", () => new Case("wavpack-terms-minus-1-to-minus-3", WavPackCases.Passes([-1, -2, -3], channels: 2)));
        Add("wavpack-terms-mono", () => new Case("wavpack-terms-mono", WavPackCases.Passes([1, 2, 3, 4, 5, 6, 7, 8, 17, 18], channels: 1)));
        Add("wavpack-hybrid-error-limit-search", () => new Case("wavpack-hybrid-error-limit-search", WavPackCases.HybridSearch()));
        Add("wavpack-floats-shift-31-exponent-255", () => new Case("wavpack-floats-shift-31-exponent-255", WavPackCases.CraftedFloats()));
        Add("wavpack-muted-blocks", () => new Case("wavpack-muted-blocks", WavPackCases.MutedBlocks()));
        Add("wavpack-sets-that-never-complete", () => new Case("wavpack-sets-that-never-complete", WavPackCases.SetsThatNeverComplete()));
        Add("wavpack-many-small-dsd-blocks", () => new Case("wavpack-many-small-dsd-blocks", WavPackCases.SmallDsdBlocks()));
        Add("wavpack-dsd-fast-5-history-bits", () => new Case("wavpack-dsd-fast-5-history-bits", WavPackCases.DsdFast()));

        // WavPack with few passes, so that the work of a sample is not hidden by 16 passes: the cases that hold the 16 units a PCM sample.
        Add("wavpack-0-passes-8-bit-zero-run", () => new Case("wavpack-0-passes-8-bit-zero-run", WavPackCases.FewPasses(0, 1)));
        Add("wavpack-0-passes-16-bit-zero-run", () => new Case("wavpack-0-passes-16-bit-zero-run", WavPackCases.FewPasses(0, 2)));
        Add("wavpack-0-passes-32-bit-zero-run", () => new Case("wavpack-0-passes-32-bit-zero-run", WavPackCases.FewPasses(0, 4)));
        Add("wavpack-1-pass-8-bit-zero-run", () => new Case("wavpack-1-pass-8-bit-zero-run", WavPackCases.FewPasses(1, 1)));
        Add("wavpack-1-pass-16-bit-zero-run", () => new Case("wavpack-1-pass-16-bit-zero-run", WavPackCases.FewPasses(1, 2)));
        Add("wavpack-1-pass-32-bit-zero-run", () => new Case("wavpack-1-pass-32-bit-zero-run", WavPackCases.FewPasses(1, 4)));

        // WavPack: the committed fixtures the encoder wrote, repeated block for block.
        foreach ((string label, string file) in new[]
        {
            ("floats", "external-lossless-float-stereo-md5.wv"),
            ("32-bit-integers", "external-lossless-32-stereo-md5.wv"),
            ("24-bit-integers", "external-lossless-24-stereo-md5.wv"),
            ("dsd-high", "external-dsd-64-high-md5.wv"),
            ("dsd-fast", "external-dsd-64-md5.wv"),
            ("hybrid-b22", "external-hybrid-b22.wv"),
            ("hybrid-b2", "external-hybrid-b2.wv"),
            ("mode-fast", "external-mode-fast.wv"),
            ("mode-very-high", "external-mode-very-high.wv"),
            ("mode-extra-6", "external-mode-extra-6.wv"),
        })
        {
            Add("wavpack-fixture-" + label, () => new Case("wavpack-fixture-" + label, WavPackCases.Repeated(file)));
        }

        // TrueHD and MLP: the encoder's fixtures repeated, and crafted units of the stages' worst cases (192 kHz, 160 samples a unit).
        foreach ((string label, string file) in new[]
        {
            ("fixture-stereo-192k", "external-truehd-stereo-192k-24.thd"),
            ("fixture-51-48k", "external-truehd-51-48k-24.thd"),
            ("fixture-51-96k", "external-truehd-51-96k-24.thd"),
            ("fixture-cholesky", "external-truehd-51-48k-24-cholesky.thd"),
            ("fixture-mono-48k", "external-truehd-mono-48k-16.thd"),
        })
        {
            Add("truehd-" + label, () => new Case("truehd-" + label, TrueHdCases.Repeated(file)));
        }

        Add("mlp-fixture-51-48k", () => new Case("mlp-fixture-51-48k", TrueHdCases.Repeated("external-mlp-51-48k-24.mlp")));
        Add("mlp-fixture-stereo-192k", () => new Case("mlp-fixture-stereo-192k", TrueHdCases.Repeated("external-mlp-stereo-192k-24.mlp")));
        Add("truehd-crafted-no-filters-no-matrices", () => new Case("truehd-crafted-no-filters-no-matrices", TrueHdCases.Crafted(0, 0, 0)));
        Add("truehd-crafted-fir-8", () => new Case("truehd-crafted-fir-8", TrueHdCases.Crafted(8, 0, 0)));
        Add("truehd-crafted-fir-4-iir-4", () => new Case("truehd-crafted-fir-4-iir-4", TrueHdCases.Crafted(4, 4, 0)));
        Add("truehd-crafted-8-matrices", () => new Case("truehd-crafted-8-matrices", TrueHdCases.Crafted(0, 0, 8)));
        Add("truehd-crafted-fir-4-iir-4-8-matrices", () => new Case("truehd-crafted-fir-4-iir-4-8-matrices", TrueHdCases.Crafted(4, 4, 8)));

        // PCM: the samples of a WAV or AIFF file unpacked, one case of integers (16-bit little-endian, and 24-bit big-endian, which takes the three-byte path with
        // the swap), one of floats and one of A-law, each of pseudo-random bytes so that no path is cheaper for its content.
        Add("pcm-integers-16-bit-stereo", () => new Case("pcm-integers-16-bit-stereo", PcmCases.Wav(tag: 1, channels: 2, bits: 16)));
        Add("pcm-aiff-integers-24-bit-6-channels", () => new Case("pcm-aiff-integers-24-bit-6-channels", PcmCases.Aiff(channels: 6, bits: 24)));
        Add("pcm-floats-32-bit-stereo", () => new Case("pcm-floats-32-bit-stereo", PcmCases.Wav(tag: 3, channels: 2, bits: 32)));
        Add("pcm-floats-64-bit-mono", () => new Case("pcm-floats-64-bit-mono", PcmCases.Wav(tag: 3, channels: 1, bits: 64)));
        Add("pcm-a-law-mono", () => new Case("pcm-a-law-mono", PcmCases.Wav(tag: 6, channels: 1, bits: 8)));

        return cases;
    }

    // WAV and AIFF files of pseudo-random samples that charge about Target units.
    internal static class PcmCases
    {
        private static byte[] Samples(int channels, int bits, long frames)
        {
            var bytes = new byte[frames * channels * (bits / 8)];
            new Random(20261027).NextBytes(bytes);
            return bytes;
        }

        // The frames that make Target units at DecodeWork.ForSample of the decoder's output width.
        private static long Frames(int channels, int outputBits, long target) => target / (DecodeWork.ForSample(CanonicalPcm.BytesPerSample(outputBits)) * channels);

        public static byte[] Wav(int tag, int channels, int bits, long target = Target)
        {
            long frames = Frames(channels, tag is 6 or 7 ? 16 : bits, target);
            byte[] samples = Samples(channels, bits, frames);
            var file = new List<byte>();
            file.AddRange("RIFF"u8.ToArray());
            file.AddRange(BitConverter.GetBytes((uint)(4 + 24 + 8 + samples.Length)));
            file.AddRange("WAVEfmt "u8.ToArray());
            file.AddRange(BitConverter.GetBytes(16u));
            file.AddRange(BitConverter.GetBytes((ushort)tag));
            file.AddRange(BitConverter.GetBytes((ushort)channels));
            file.AddRange(BitConverter.GetBytes(44100u));
            file.AddRange(BitConverter.GetBytes((uint)(44100 * channels * (bits / 8))));
            file.AddRange(BitConverter.GetBytes((ushort)(channels * (bits / 8))));
            file.AddRange(BitConverter.GetBytes((ushort)bits));
            file.AddRange("data"u8.ToArray());
            file.AddRange(BitConverter.GetBytes((uint)samples.Length));
            file.AddRange(samples);
            return [.. file];
        }

        public static byte[] Aiff(int channels, int bits, long target = Target)
        {
            long frames = Frames(channels, bits, target);
            byte[] samples = Samples(channels, bits, frames);
            var file = new List<byte>();
            void Be32(uint value) => file.AddRange([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
            file.AddRange("FORM"u8.ToArray());
            Be32((uint)(4 + 8 + 18 + 8 + 8 + samples.Length));
            file.AddRange("AIFFCOMM"u8.ToArray());
            Be32(18);
            file.AddRange([(byte)(channels >> 8), (byte)channels]);
            Be32((uint)frames);
            file.AddRange([(byte)(bits >> 8), (byte)bits]);
            file.AddRange([0x40, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0]);
            file.AddRange("SSND"u8.ToArray());
            Be32((uint)(8 + samples.Length));
            Be32(0);
            Be32(0);
            file.AddRange(samples);
            return [.. file];
        }
    }

    // A reader and writer of bits, most significant first, for the FLAC frames.
    private sealed class BitWriter
    {
        private byte[] _bytes = new byte[1024];
        private long _bit;

        public void Write(ulong value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                if (_bit >> 3 >= _bytes.Length)
                {
                    Array.Resize(ref _bytes, _bytes.Length * 2);
                }

                if (((value >> i) & 1) != 0)
                {
                    _bytes[_bit >> 3] |= (byte)(0x80 >> (int)(_bit & 7));
                }

                _bit++;
            }
        }

        public void Zeros(int count) => Write(0, count);

        public byte[] ToArray() => _bytes[..(int)((_bit + 7) >> 3)];
    }

    internal static class FlacCases
    {
        private const int Rate = 44100;

        private static byte[] Stream(int blockSize, int channels, int bits, long total, IEnumerable<byte[]> frames) =>
            [.. FlacBuilder.Marker, .. FlacBuilder.Block(0, FlacBuilder.StreamInfo(blockSize, Rate, channels, bits, total), isLast: true), .. frames.SelectMany(f => f)];

        // A Rice code of the signed value with parameter k.
        private static void Rice(BitWriter w, int value, int k)
        {
            uint folded = value >= 0 ? (uint)value << 1 : ((uint)(-(long)value) << 1) - 1;
            uint quotient = folded >> k;
            w.Zeros((int)quotient);
            w.Write(1, 1);
            w.Write(folded & ((1u << k) - 1), k);
        }

        private static void Residual(BitWriter w, Random random, int count, int amplitude, int k)
        {
            w.Write(0, 2);
            w.Write(0, 4);
            w.Write((ulong)k, 4);
            for (int i = 0; i < count; i++)
            {
                Rice(w, random.Next(-amplitude, amplitude), k);
            }
        }

        private static void Lpc(BitWriter w, Random random, int order, int sampleBits, int precision, int shift, int coefficient, int count, int amplitude, int k)
        {
            w.Write(0, 1);
            w.Write((ulong)(32 + order - 1), 6);
            w.Write(0, 1);
            for (int i = 0; i < order; i++)
            {
                w.Write((ulong)(random.Next(-100, 100) & ((1L << sampleBits) - 1)), sampleBits);
            }

            w.Write((ulong)(precision - 1), 4);
            w.Write((ulong)shift, 5);
            for (int i = 0; i < order; i++)
            {
                w.Write((ulong)(coefficient & ((1 << precision) - 1)), precision);
            }

            Residual(w, random, count - order, amplitude, k);
        }

        // 4,096 samples of 16-bit stereo with a linear predictor of order 8 (5 bits of precision, a shift of 3, every coefficient 1) and a residual of
        // about 7 bits in one partition of Rice parameter 6; 2^29 units are 2,731 frames, 22 MB.
        public static byte[] Reference(long target = Target)
        {
            const int block = 4096;
            const long unitsPerFrame = block * 2 * (16 + 8);
            int frames = (int)(target / unitsPerFrame);
            var random = new Random(20261023);
            return Stream(block, 2, 16, (long)frames * block, Enumerable.Range(0, frames).Select(number =>
            {
                var w = new BitWriter();
                for (int c = 0; c < 2; c++)
                {
                    Lpc(w, random, 8, 16, 5, 3, 1, block, 64, 6);
                }

                return FlacBuilder.FrameOf(number, block, Rate, 1, 16, w.ToArray());
            }));
        }

        // Constant subframes of 8 channels at 32 bits: 22 units a sample of each channel for 5 bytes of coded data a frame channel, the most bytes
        // of MD5 per unit.
        public static byte[] Constant8Channels32Bits(long target = Target)
        {
            const int block = 65535;
            int frames = (int)(target / (block * 8 * 22));
            return Stream(block, 8, 32, (long)frames * block, Enumerable.Range(0, frames).Select(number => FlacBuilder.ConstantFrame(number, block, Rate, 8, 32, number)));
        }

        // Verbatim subframes at 32 bits: the MD5 of every byte of the stream and the walk's store of every sample, with no predictor at all. 2^27 units are 18 MB.
        public static byte[] Verbatim32Bits(long target = 1L << 27)
        {
            const int block = 4096;
            int frames = (int)(target / (block * 2 * (22 + 8)));
            var random = new Random(7);
            return Stream(block, 2, 32, (long)frames * block, Enumerable.Range(0, frames).Select(number =>
            {
                var w = new BitWriter();
                for (int c = 0; c < 2; c++)
                {
                    w.Write(0, 1);
                    w.Write(1, 6);
                    w.Write(0, 1);
                    for (int i = 0; i < block; i++)
                    {
                        w.Write((ulong)(uint)random.Next(), 32);
                    }
                }

                return FlacBuilder.FrameOf(number, block, Rate, 1, 32, w.ToArray());
            }));
        }

        // Left/side stereo at 32 bits, so the side channel has 33 bits, with predictors of order 32 whose sums do not fit 32 bits: the 64-bit path,
        // 22 plus 2 for each of 32 taps, a sample of each channel.
        public static byte[] Order32In64Bits(long target = 1L << 28)
        {
            const int block = 4096;
            int frames = (int)(target / (block * 2 * (22 + (2 * 32))));
            var random = new Random(32);
            return Stream(block, 2, 32, (long)frames * block, Enumerable.Range(0, frames).Select(number =>
            {
                var w = new BitWriter();
                for (int c = 0; c < 2; c++)
                {
                    int sampleBits = c == 1 ? 33 : 32;
                    w.Write(0, 1);
                    w.Write(63, 6);
                    w.Write(0, 1);
                    for (int i = 0; i < 32; i++)
                    {
                        w.Write((ulong)((long)random.Next(-100000, 100000) & ((1L << sampleBits) - 1)), sampleBits);
                    }

                    w.Write(14, 4);
                    w.Write(10, 5);
                    for (int i = 0; i < 32; i++)
                    {
                        w.Write((ulong)((i == 0 ? 600 : 5) & 0x7FFF), 15);
                    }

                    Residual(w, random, block - 32, 4096, 12);
                }

                return FlacBuilder.FrameOf(number, block, Rate, 8, 32, w.ToArray());
            }));
        }

        // Fixed predictors of order 4: 16 units a sample of each channel and 4 taps, over a residual of a bit or two a sample so that the walk,
        // which is out of the figure but in the time of the analysis, does not take most of it.
        public static byte[] FixedOrder4(long target = Target)
        {
            const int block = 4096;
            int frames = (int)(target / (block * 2 * (16 + 4)));
            var random = new Random(4);
            return Stream(block, 2, 16, (long)frames * block, Enumerable.Range(0, frames).Select(number =>
            {
                var w = new BitWriter();
                for (int c = 0; c < 2; c++)
                {
                    w.Write(0, 1);
                    w.Write(12, 6);
                    w.Write(0, 1);
                    for (int i = 0; i < 4; i++)
                    {
                        w.Write((ulong)(random.Next(-100, 100) & 0xFFFF), 16);
                    }

                    Residual(w, random, block - 4, 2, 1);
                }

                return FlacBuilder.FrameOf(number, block, Rate, 1, 16, w.ToArray());
            }));
        }

        // Frames the walk does not store (FlacParser.LendsBuffers is false), so the decoder reads them: 16 units a byte of the frame. A fixed
        // predictor of order 1 and two partitions, the first escaped to 14 bits a residual, the second a Rice code of parameter 0 whose unary
        // codes run to 64 bits.
        public static byte[] EscapesAndLongUnaryCodes(long target = 1L << 28)
        {
            const int block = 4096;
            var random = new Random(1);
            var sample = new BitWriter();
            for (int c = 0; c < 2; c++)
            {
                sample.Write(0, 1);
                sample.Write(9, 6);
                sample.Write(0, 1);
                sample.Write(0, 16);
                sample.Write(0, 2);
                sample.Write(1, 4);
                sample.Write(15, 4);
                sample.Write(14, 5);
                for (int i = 1; i < block / 2; i++)
                {
                    sample.Write((ulong)random.Next(0, 1 << 14), 14);
                }

                sample.Write(0, 4);
                for (int i = 0; i < block / 2; i++)
                {
                    sample.Zeros(63);
                    sample.Write(1, 1);
                }
            }

            byte[] subframes = sample.ToArray();
            long unitsPerFrame = (block * 2 * (16 + 1)) + (16L * (subframes.Length + 14));
            int frames = (int)(target / unitsPerFrame);
            return Stream(block, 2, 16, (long)frames * block, Enumerable.Range(0, frames).Select(number => FlacBuilder.FrameOf(number, block, Rate, 1, 16, subframes)));
        }
    }

    // Files of copies of one frame of a fixture, sized to charge about Target units: a frame decodes by itself, so the copies are a file of as much audio.
    internal static class MonkeysAudioCases
    {
        public static byte[] Repeated(string fixture, long target = Target, int frame = 0, ushort? level = null)
        {
            (byte[] header, byte[] headerData, List<byte[]> frames) = MonkeysAudioBuilder.Take(fixture);
            uint blocks = MonkeysAudioBuilder.BlocksOf(fixture, frame);
            byte[] probe = MonkeysAudioBuilder.Build(header, headerData, [frames[frame], frames[frame]], blocks, blocks, level);
            long perCopy = Math.Max(1, Units(probe) / 2);
            int copies = (int)Math.Clamp(target / perCopy, 2, 1 << 20);
            return MonkeysAudioBuilder.Build(header, headerData, [.. Enumerable.Repeat(frames[frame], copies)], blocks, blocks, level);
        }

        public static long Units(byte[] file)
        {
            var counting = new CountingDecoder(new MonkeysAudioDecoder());
            var analyzer = new Analyzer(DefaultParsers.Tags, DefaultParsers.Formats, DefaultParsers.Codecs, [counting]);
            Report report = analyzer.Analyze("probe.ape", new MemoryStream(file), new AnalysisOptions { Decoding = new DecodeOptions { MaxWork = long.MaxValue } });
            Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed" or "decode.limit");
            return counting.Units;
        }
    }

    internal static class WavPackCases
    {
        internal const uint BaseFlags = 0x14BC1805;

        private static long WavPackPassWork(int passes) => passes * StreamDecoder.WorkPerPass;

        private static readonly byte[] MonoHeader = File.ReadAllBytes(WavPackBuilder.FixturePath("external-lossless-16-mono.wv"))[..32];

        internal static byte[] Header(uint flags, uint samples, long index)
        {
            byte[] header = (byte[])MonoHeader.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)index);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), samples);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), flags);
            return header;
        }

        internal static byte[] Block(uint flags, uint samples, long index, params byte[][] subBlocks) =>
            WavPackBuilder.Join(Header(flags, samples, index), [.. subBlocks, WavPackBuilder.Sub(0x2F, new byte[4])]);

        internal static byte[] Terms(int[] terms, int passes)
        {
            byte[] data = new byte[passes];
            for (int i = 0; i < passes; i++)
            {
                data[i] = (byte)((terms[i % terms.Length] + 5) | (2 << 5));
            }

            return WavPackBuilder.Sub(0x02, data);
        }

        // The bits of a run of zeros of this many words (a word is a sample of a channel) in the entropy coder, as 8 bytes or more, LSB first:
        // the length in ones, a zero, and the length's low bits.
        internal static byte[] ZeroRun(long words)
        {
            int count = 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)words);
            var bits = new List<bool>();
            bits.AddRange(Enumerable.Repeat(true, count));
            bits.Add(false);
            for (int i = 0; i < count - 1; i++)
            {
                bits.Add(((words >> i) & 1) != 0);
            }

            byte[] bytes = new byte[Math.Max(8, (bits.Count + 7) / 8 + 8)];
            bytes = new byte[(bytes.Length + 1) & ~1];
            for (int i = 0; i < bits.Count; i++)
            {
                if (bits[i])
                {
                    bytes[i / 8] |= (byte)(1 << (i % 8));
                }
            }

            return bytes;
        }

        // Bytes with a zero in every bit 7: the unary part of a word is at most 7 ones, so the entropy decoder neither runs out of code nor reads an
        // escape, and the values are not all zero.
        internal static byte[] Noise(Random random, int length)
        {
            byte[] bytes = new byte[length];
            random.NextBytes(bytes);
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] &= 0x7F;
            }

            return bytes;
        }

        internal static byte[] EntropyVariables(int channels, int log)
        {
            byte[] data = new byte[channels == 1 ? 6 : 12];
            for (int i = 0; i < data.Length / 2; i++)
            {
                data[2 * i + 1] = (byte)log;
            }

            return WavPackBuilder.Sub(0x05, data);
        }

        internal static uint Flags(int channels) => channels == 1 ? BaseFlags : (BaseFlags & ~4u);

        // The review's worst case: mono blocks of 196,607 samples whose bitstream codes one zero run (8 bytes), 16 passes of the terms.
        public static byte[] ZeroRuns(int[] terms, int channels, long target = Target)
        {
            const uint samples = 196_607;
            int blocks = (int)(target / ((long)samples * channels * (WavPackPassWork(16) + 32))) + 1;
            var all = new List<byte[]>();
            for (int i = 0; i < blocks; i++)
            {
                all.Add(Block(Flags(channels), samples, i * (long)samples, Terms(terms, 16), EntropyVariables(channels, 0), WavPackBuilder.Sub(0x0A, ZeroRun(samples * channels))));
            }

            return WavPackBuilder.Concat(all);
        }

        // 16 passes of these terms over noise, blocks of 4,096 samples. The units of a block are the fixed work of its samples, 16 a byte of the
        // bitstream and 8 a pass.
        public static byte[] Passes(int[] terms, int channels, long target = Target)
        {
            const uint samples = 4096;
            long unitsPerBlock = samples * channels * (32 + WavPackPassWork(16)) + (16L * samples * channels * 12 / 8);
            int blocks = (int)(target / unitsPerBlock);
            var random = new Random(terms.Sum() + channels);
            var all = new List<byte[]>();
            for (int i = 0; i < blocks; i++)
            {
                all.Add(Block(Flags(channels), samples, i * (long)samples, Terms(terms, 16), EntropyVariables(channels, 7), WavPackBuilder.Sub(0x0A, Noise(random, (int)(samples * channels * 12 / 8)))));
            }

            return WavPackBuilder.Concat(all);
        }

        // Hybrid blocks with a bitrate accumulator of 0, so the error limit is 1 and the search for a value reads a bit a step down from medians of
        // 2^24: about 20 bits a sample, 16 units a byte of them.
        public static byte[] HybridSearch(long target = Target)
        {
            const uint samples = 4096;
            long unitsPerBlock = samples * (32 + (16 * 30 / 8));
            int blocks = (int)((target / 2) / unitsPerBlock);
            var random = new Random(24);
            var all = new List<byte[]>();
            for (int i = 0; i < blocks; i++)
            {
                all.Add(Block(Flags(1) | 8, samples, i * (long)samples, WavPackBuilder.Sub(0x06, new byte[2]), EntropyVariables(1, 24), WavPackBuilder.Sub(0x0A, Noise(random, (int)(samples * 30 / 8 + 1) & ~1))));
            }

            return WavPackBuilder.Concat(all);
        }

        // Floats with a shift of 31 and a maximum exponent of 255 over zero runs: one pass of term 1 with weight 1024 and a decorrelation sample of
        // 1 makes every sample 1, which the shift turns into int.MinValue (negated, still negative) and then 0 after one more shift, where libwavpack's loop
        // counts the exponent down one step at a time.
        public static byte[] CraftedFloats(long target = Target)
        {
            const uint samples = 196_607;
            int blocks = (int)(target / ((long)samples * (22 + 16 + 16 + 8))) + 1;
            uint flags = (Flags(1) & ~3u) | 3 | 0x80u;
            var all = new List<byte[]>();
            for (int i = 0; i < blocks; i++)
            {
                all.Add(Block(flags, samples, i * (long)samples,
                    WavPackBuilder.Sub(0x02, [(byte)((1 + 5) | (2 << 5))]),
                    WavPackBuilder.Sub(0x03, [127]),
                    WavPackBuilder.Sub(0x04, [0, 1]),
                    EntropyVariables(1, 0),
                    WavPackBuilder.Sub(0x08, [0, 31, 255, 0]),
                    WavPackBuilder.Sub(0x0A, ZeroRun(samples))));
            }

            return WavPackBuilder.Concat(all);
        }

        // Blocks whose bitstream ends after a few words: each is muted, and charged its samples all the same.
        public static byte[] MutedBlocks(long target = Target)
        {
            const uint samples = 196_607;
            int blocks = (int)(target / ((long)samples * (WavPackPassWork(16) + 32))) + 1;
            var all = new List<byte[]>();
            for (int i = 0; i < blocks; i++)
            {
                all.Add(Block(Flags(1), samples, i * (long)samples, Terms([17], 16), EntropyVariables(1, 7), WavPackBuilder.Sub(0x0A, new byte[16])));
            }

            return WavPackBuilder.Concat(all);
        }

        // Mono blocks of 196,607 samples that are each a set of their own in a stream of 100 channels: no set completes. Every block is charged.
        public static byte[] SetsThatNeverComplete(long target = Target)
        {
            const uint samples = 196_607;
            int blocks = (int)(target / ((long)samples * (WavPackPassWork(16) + 32))) + 1;
            var all = new List<byte[]>();
            for (int i = 0; i < blocks; i++)
            {
                var parts = new List<byte[]>();
                if (i == 0)
                {
                    parts.Add(WavPackBuilder.Sub(0x0D, [99, 99, 0, 0, 0, 0]));
                }

                parts.AddRange([Terms([17], 16), EntropyVariables(1, 0), WavPackBuilder.Sub(0x0A, ZeroRun(samples))]);
                all.Add(Block(Flags(1), samples, i * (long)samples, [.. parts]));
            }

            return WavPackBuilder.Concat(all);
        }

        // DSD blocks of about 400 bytes whose fast-mode tables cost 57,000 steps to set up and which state 100 bytes of audio: the setup is the
        // block's cost. The table of each of the 32 bins holds one byte value with probability 1 and a run of zeros.
        public static byte[] SmallDsdBlocks(long target = Target)
        {
            const uint samples = 100;
            byte[] dsd = [.. new byte[] { 4, 1, 5, 2 }, .. Enumerable.Range(0, 32).SelectMany(_ => new byte[] { 1, 255, 4 }), 0, 0, 0, 0, 0, .. new byte[300]];
            long unitsPerBlock = 57_344 + (16L * dsd.Length) + (samples * (13 + 32));
            int blocks = (int)(target / unitsPerBlock);
            uint flags = Flags(1) | 0x80000000;
            var all = new List<byte[]>();
            for (int i = 0; i < blocks; i++)
            {
                all.Add(Block(flags, samples, i * (long)samples, WavPackBuilder.Sub(0x0E, dsd)));
            }

            return WavPackBuilder.Concat(all);
        }

        // Mono DSD blocks in the fast mode with 5 history bits: a table of 1 for every byte value in every bin, then bytes the range decoder
        // reads as it goes.
        public static byte[] DsdFast(long target = Target)
        {
            const uint samples = 50_000;
            Random random = new(5);
            long unitsPerBlock = 57_344 + (16L * (samples + 8300)) + (samples * (13 + 32));
            int blocks = (int)(target / unitsPerBlock);
            uint flags = Flags(1) | 0x80000000;
            var all = new List<byte[]>();
            for (int i = 0; i < blocks; i++)
            {
                byte[] payload = new byte[samples + 8];
                random.NextBytes(payload);
                byte[] dsd = [.. new byte[] { 4, 1, 5, 255 }, .. Enumerable.Repeat((byte)1, 32 * 256), .. payload];
                all.Add(Block(flags, samples, i * (long)samples, WavPackBuilder.Sub(0x0E, dsd)));
            }

            return WavPackBuilder.Concat(all);
        }

        // Mono blocks of 196,607 samples of this many bytes whose bitstream codes one zero run, with this many passes of term 17.
        public static byte[] FewPasses(int passes, int bytes, long target = Target)
        {
            const uint samples = 196_607;
            long perSample = DecodeWork.ForSample(bytes) + StreamDecoder.WorkPerPcmSample + (passes * StreamDecoder.WorkPerPass);
            int blocks = (int)(target / ((long)samples * perSample)) + 1;
            uint flags = (Flags(1) & ~3u) | (uint)(bytes - 1);
            var all = new List<byte[]>();
            for (int i = 0; i < blocks; i++)
            {
                var parts = new List<byte[]>();
                if (passes > 0)
                {
                    parts.Add(Terms([17], passes));
                }

                parts.Add(EntropyVariables(1, 0));
                parts.Add(WavPackBuilder.Sub(0x0A, ZeroRun(samples)));
                all.Add(Block(flags, samples, i * (long)samples, [.. parts]));
            }

            return WavPackBuilder.Concat(all);
        }

        // The blocks of a fixture, repeated until their units come to the target. The units of one pass are counted by decoding it once.
        public static byte[] Repeated(string file)
        {
            byte[] bytes = File.ReadAllBytes(WavPackBuilder.FixturePath(file));
            List<byte[]> blocks = WavPackBuilder.Split(bytes);
            var once = new Case("wavpack-once", WavPackBuilder.Concat(blocks));
            (_, long units, _) = Run(once, decode: true);
            int copies = (int)Math.Max(1, (Target + units - 1) / units);
            return WavPackBuilder.Concat(Enumerable.Repeat(blocks, copies).SelectMany(b => b));
        }
    }

    internal static class TrueHdCases
    {
        // The units of a fixture, repeated until they come to the target: each pass starts with a major sync and its restart header, so the passes join.
        public static byte[] Repeated(string file)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "truehd", file));
            return RepeatedTo(bytes, file.EndsWith(".mlp", StringComparison.Ordinal) ? "mlp-once" : "truehd-once");
        }

        private static byte[] RepeatedTo(byte[] once, string name)
        {
            (_, long units, _) = Run(new Case(name, once), decode: true);
            int copies = (int)Math.Max(1, (Target + units - 1) / units);
            byte[] data = new byte[once.Length * copies];
            for (int i = 0; i < copies; i++)
            {
                once.CopyTo(data, i * once.Length);
            }

            return data;
        }

        // A stereo unit of 160 samples at 192 kHz in one block that restarts the substream: the encoder's major sync and restart header, then parameters
        // of the stages' worst cases for every channel: FIR and IIR filters of the given orders (a channel allows 8 taps in all), matrices with every
        // coefficient present, and lsbCount raw bits a sample (codebook 0). Repeated until the units come to the target, since each unit restarts; with repeat
        // false it is the one unit.
        public static byte[] Crafted(int firOrder, int iirOrder, int matrices, int lsbCount = 8, bool repeat = true)
        {
            byte[] fixture = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "truehd", "external-truehd-stereo-192k-24.thd"));
            const int headerBits = 133;
            const int substreamStart = 34;
            var w = new BitWriter();
            w.Write(1, 1);
            w.Write(1, 1);
            for (int i = 0; i < headerBits; i++)
            {
                int at = (substreamStart * 8) + 2 + i;
                w.Write((ulong)((fixture[at >> 3] >> (7 - (at & 7))) & 1), 1);
            }

            w.Write(0, 1);
            w.Write(1, 1);
            w.Write(160, 9);
            w.Write(1, 1);
            w.Write((ulong)matrices, 4);
            for (int j = 0; j < matrices; j++)
            {
                w.Write((ulong)(j & 1), 4);
                w.Write(0, 4);
                w.Write(0, 1);
                for (int input = 0; input < 4; input++)
                {
                    w.Write(1, 1);
                    w.Write(1, 2);
                }
            }

            w.Write(1, 1);
            w.Write(0, 8);
            w.Write(1, 1);
            w.Write(0, 8);
            for (int c = 0; c < 2; c++)
            {
                w.Write(1, 1);
                WriteFilter(w, firOrder);
                WriteFilter(w, iirOrder);
                w.Write(0, 1);
                w.Write(0, 2);
                w.Write((ulong)lsbCount, 5);
            }

            for (int i = 0; i < 160 * 2 * lsbCount; i++)
            {
                w.Write((ulong)(((i * 2654435761L) >> 7) & 1), 1);
            }

            w.Write(1, 1);
            byte[] bits = w.ToArray();
            int padded = ((bits.Length + 1) / 2) * 2;
            byte[] body = new byte[padded];
            bits.CopyTo(body, 0);
            byte parity = 0xA9;
            foreach (byte b in body)
            {
                parity ^= b;
            }

            byte[] substream = [.. body, parity, Encspect.Formats.TrueHd.Checks.Substream(body)];
            byte[] unit = [.. fixture[..substreamStart], .. substream];
            int words = unit.Length / 2;
            unit[0] = (byte)((unit[0] & 0xF0) | (words >> 8));
            unit[1] = (byte)words;
            int end = substream.Length / 2;
            unit[32] = (byte)((unit[32] & 0xF0) | (end >> 8));
            unit[33] = (byte)end;
            TrueHdFindingTests.Refresh(unit, 0, unit.Length, hasSync: true);
            return repeat ? RepeatedTo(unit, "truehd-once") : unit;
        }

        private static void WriteFilter(BitWriter w, int order)
        {
            if (order == 0)
            {
                w.Write(0, 1);
                return;
            }

            w.Write(1, 1);
            w.Write((ulong)order, 4);
            w.Write(3, 4);
            w.Write(2, 5);
            w.Write(0, 3);
            for (int k = 0; k < order; k++)
            {
                w.Write(1, 2);
            }

            w.Write(0, 1);
        }
    }
}
