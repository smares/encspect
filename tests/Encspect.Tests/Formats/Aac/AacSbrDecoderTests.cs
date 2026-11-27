using Encspect.Formats.Aac;
using Encspect.Formats.Aac.Decoding;

namespace Encspect.Tests.Formats.Aac;

// The QMF banks and parametric stereo of the HE-AAC decoder, on signals whose result is known without a reference decoder (the PCM cross-checks against libxaac and FFmpeg are in
// docs/development.md, "AAC decoding").
public class AacSbrDecoderTests
{
    // A sinusoid through the 32-band analysis, then the 64-band synthesis with the upper half empty, comes out at twice the rate with the same amplitude (SbrElement.OutputScale).
    [Fact]
    public void AnalysisAndSynthesisKeepTheAmplitudeOfASinusoid()
    {
        var qmf = new SbrQmf();
        var synthesis = new SbrQmf();
        const int Slots = 64;
        float[] input = new float[Slots * 32];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = 0.5f * 32768f * (float)Math.Sin(2 * Math.PI * 1000 * i / 22050);
        }

        var output = new List<float>();
        float[] re = new float[64];
        float[] im = new float[64];
        float[] block = new float[64];
        for (int slot = 0; slot < Slots; slot++)
        {
            Array.Clear(re);
            Array.Clear(im);
            qmf.Analyze(input.AsSpan(slot * 32, 32), re.AsSpan(0, 32), im.AsSpan(0, 32));
            synthesis.Synthesize(re, im, block, 64);
            output.AddRange(block.Select(v => v * SbrElement.OutputScale));
        }

        double power = output.Skip(1500).Sum(v => (double)v * v) / (output.Count - 1500);
        Assert.InRange(Math.Sqrt(power), 0.5 / Math.Sqrt(2) * 0.97, 0.5 / Math.Sqrt(2) * 1.03);
    }

    // Without parametric stereo data the channels are the mono channel's, in the same slots (the hybrid analysis looks 6 slots ahead, into the slots the SBR buffer holds).
    [Fact]
    public void WithoutParametersBothChannelsAreTheMonoChannel()
    {
        var ps = new PsDecoder();
        var random = new Random(7);
        float[] ahead = new float[6 * 64];
        for (int frame = 0; frame < 4; frame++)
        {
            float[] re = new float[32 * 64];
            float[] im = new float[32 * 64];
            for (int i = 0; i < re.Length; i++)
            {
                re[i] = (float)random.NextDouble() - 0.5f;
                im[i] = (float)random.NextDouble() - 0.5f;
            }

            ps.Process(re, im, ahead, ahead, default, 64);
            for (int l = 0; l < 32; l++)
            {
                for (int band = 10; band < 64; band++)
                {
                    Assert.Equal(re[(l * 64) + band], ps.LeftRe[(l * 64) + band], 1e-6f);
                    Assert.Equal(re[(l * 64) + band], ps.RightRe[(l * 64) + band], 1e-6f);
                }
            }
        }
    }

    // An intensity difference of 7 steps (coarse, 20 bands) with full coherence gives the left channel the gain of the first scale factor and the right the last, as the tables define,
    // once the matrices have interpolated to it (the second frame).
    [Fact]
    public void AnIntensityDifferenceSetsTheChannelGains()
    {
        var ps = new PsDecoder();
        int[] data = new int[SbrRecord.PsIcc + (5 * SbrRecord.PsStride)];
        data[SbrRecord.PsHasHeader] = 1;
        data[SbrRecord.PsEnableIid] = 1;
        data[SbrRecord.PsIidMode] = 1;
        data[SbrRecord.PsEnableIcc] = 1;
        data[SbrRecord.PsIccMode] = 1;
        data[SbrRecord.PsFrameClass] = 0;
        data[SbrRecord.PsEnvelopes] = 1;
        data[SbrRecord.PsIid] = 7;
        float[] re = new float[32 * 64];
        float[] im = new float[32 * 64];
        for (int i = 0; i < re.Length; i++)
        {
            re[i] = (i % 3) - 1f;
            im[i] = (i % 5) - 2f;
        }

        float[] ahead = new float[6 * 64];
        ps.Process(re, im, ahead, ahead, data, 64);
        ps.Process(re, im, ahead, ahead, data, 64);

        // Alpha for a coherence of 1 is 0, so the matrices are diagonal: left = 1.4119827747 and right = 0.0794016272 times the input.
        for (int band = 12; band < 60; band++)
        {
            float input = re[(20 * 64) + band];
            Assert.Equal(1.4119827747f * input, ps.LeftRe[(20 * 64) + band], 1e-4f);
            Assert.Equal(0.0794016272f * input, ps.RightRe[(20 * 64) + band], 1e-4f);
        }
    }
}
