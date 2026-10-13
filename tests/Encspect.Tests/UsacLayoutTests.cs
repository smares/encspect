using Encspect.Core;
using Encspect.Formats.Aac;

namespace Encspect.Tests;

// The layout of a UsacConfig (ISO/IEC 23003-3 5.2) as AudioSpecificConfig reads it, and the exhale
// rule AacEncoder applies to it: hand-built configurations, each one bit pattern away from what
// exhale 1.2.2 writes (bitStreamWriter.cpp, createAudioConfig).
public class UsacLayoutTests
{
    // Single channel, channel pair and LFE elements by channel configuration, as exhale's
    // elementTypeConfig lists them.
    private static readonly string[] Stereo = ["CPE"];
    private static readonly string[] Surround71 = ["SCE", "CPE", "CPE", "CPE", "LFE"];

    [Fact]
    public void ExhaleWithoutSbrIsNamed()
    {
        (Node? encoder, _) = Detect(new Spec());
        Assert.NotNull(encoder);
        Assert.Equal("exhale", encoder.Value?.ToString());
        Assert.Equal("Medium", Child(encoder, "Confidence"));
        Assert.Contains("one loudness info extension of 9 bytes with one measurement", Child(encoder, "Evidence"));
        Assert.DoesNotContain("SBR", Child(encoder, "Evidence"));
    }

    [Fact]
    public void ExhaleWithSbrIsNamed()
    {
        (Node? encoder, _) = Detect(new Spec { FrameIndex = 3, LoudnessLength = 8 });
        Assert.NotNull(encoder);
        Assert.Contains("SBR defaults of start frequency 15", Child(encoder, "Evidence"));
    }

    [Fact]
    public void SevenOneWithoutConfigExtensionIsNamed()
    {
        (Node? encoder, _) = Detect(new Spec { ChannelConfiguration = 7, Elements = Surround71, Loudness = false });
        Assert.NotNull(encoder);
        Assert.Contains("and no configuration extension", Child(encoder, "Evidence"));
    }

    [Fact]
    public void SevenOneWithLoudnessIsNamed() =>
        Assert.NotNull(Detect(new Spec { ChannelConfiguration = 7, Elements = Surround71, FrameIndex = 3, LoudnessLength = 8 }).Encoder);

    public static TheoryData<string, Spec> NotExhale => new()
    {
        { "time-warped MDCT", new Spec { TimeWarp = true } },
        { "harmonic SBR", new Spec { FrameIndex = 3, LoudnessLength = 8, HarmonicSbr = true } },
        { "PVC", new Spec { FrameIndex = 3, LoudnessLength = 8, Pvc = true } },
        { "start frequency 14", new Spec { FrameIndex = 3, LoudnessLength = 8, StartFrequency = 14 } },
        { "stop frequency 11", new Spec { FrameIndex = 3, LoudnessLength = 8, StopFrequency = 11 } },
        { "default header extra 2", new Spec { FrameIndex = 3, LoudnessLength = 8, Extra2 = true } },
        { "MPEG Surround 2-1-2", new Spec { FrameIndex = 3, LoudnessLength = 8, StereoConfigIndex = 1 } },
        { "768 samples", new Spec { FrameIndex = 0 } },
        { "8:3 SBR", new Spec { FrameIndex = 2, LoudnessLength = 8 } },
        { "pre-roll element not first", new Spec { PreRollFirst = false } },
        { "no pre-roll element", new Spec { PreRoll = false } },
        { "pre-roll with a default length", new Spec { PreRollDefaultLength = true } },
        { "pre-roll with fragmentation", new Spec { PreRollFragmented = true } },
        { "fill element", new Spec { Fill = true } },
        { "stream ID and loudness", new Spec { StreamId = true } },
        { "two loudness extensions", new Spec { SecondLoudness = true } },
        { "loudness of 10 bytes", new Spec { LoudnessLength = 10 } },
        { "two measurements", new Spec { Measurements = 2, LoudnessLength = 10 } },
        { "stereo without config extension", new Spec { Loudness = false } },
    };

    [Theory]
    [MemberData(nameof(NotExhale))]
    public void OtherLayoutsAreNotNamed(string what, Spec spec)
    {
        (Node? encoder, AacConfig config) = Detect(spec);
        Assert.NotNull(config.UsacLayout);
        Assert.True(encoder is null, what);
    }

    [Fact]
    public void PreRollWithConfigLengthReadsNoConfiguration()
    {
        // Table 17 reads no configuration for an audio pre-roll element: the next element follows
        // directly, and the declared length is a warning.
        var findings = new List<string>();
        (Node? encoder, AacConfig config) = Detect(new Spec { PreRollConfigLength = 2 }, findings);
        Assert.Null(encoder);
        Assert.Contains("aac.usac-extension-config-length", findings);
        Assert.Equal(new[] { "EXT", "CPE" }, config.UsacLayout!.Elements.Select(e => e.Kind));
        Assert.Equal(new[] { 2L }, config.UsacLayout.ConfigExtensions.Select(e => e.Type));
    }

    [Fact]
    public void FillWithConfigLengthReadsNoConfiguration()
    {
        var findings = new List<string>();
        (_, AacConfig config) = Detect(new Spec { Fill = true, FillConfigLength = 3 }, findings);
        Assert.Contains("aac.usac-extension-config-length", findings);
        Assert.Equal(new[] { "EXT", "CPE", "EXT" }, config.UsacLayout!.Elements.Select(e => e.Kind));
        Assert.Equal(new[] { 2L }, config.UsacLayout.ConfigExtensions.Select(e => e.Type));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 2)]
    [InlineData(3, 0)]
    public void Mps212ConfigIsReadToItsEnd(int stereoConfigIndex, int shaping)
    {
        var findings = new List<string>();
        (_, AacConfig config) = Detect(new Spec { FrameIndex = 3, LoudnessLength = 8, StereoConfigIndex = stereoConfigIndex, Shaping = shaping, Fill = true }, findings);
        UsacLayout layout = config.UsacLayout!;
        Assert.Equal(stereoConfigIndex, layout.Elements[1].StereoConfigIndex);
        Assert.Equal(new[] { "EXT", "CPE", "EXT" }, layout.Elements.Select(e => e.Kind));
        Assert.Equal(0L, layout.Elements[2].ExtensionType);
        Assert.Equal(new[] { 2L }, layout.ConfigExtensions.Select(e => e.Type));
        Assert.True(layout.ConfigExtensions[0].SingleLoudnessMeasurement);
        Assert.Empty(findings);
    }

    [Fact]
    public void UsacChannelConfigSitsInUsacConfig()
    {
        var node = new Node("AudioSpecificConfig", 0, 0);
        AudioSpecificConfig.Parse(new MappedBytes(Build(new Spec { ChannelConfiguration = 0, Positions = [0, 1] }), 0), node, (_, _, _, _) => { });
        Node usac = Assert.Single(node.Children, c => c.Name == "UsacConfig");
        Assert.Contains(usac.Children, c => c.Name == "UsacChannelConfig");
        Assert.DoesNotContain(node.Children, c => c.Name == "UsacChannelConfig");
    }

    private static string? Child(Node node, string name) => node.Children.FirstOrDefault(c => c.Name == name)?.Value?.ToString();

    private static (Node? Encoder, AacConfig Config) Detect(Spec spec, List<string>? findings = null)
    {
        var node = new Node("AudioSpecificConfig", 0, 0);
        AacConfig? config = AudioSpecificConfig.Parse(new MappedBytes(Build(spec), 0), node, (_, code, _, _) => findings?.Add(code));
        Assert.NotNull(config);
        Node? encoder = AacEncoder.Detect(null, [], (name, value, description) => new Node(name, 0, 0, value) { Description = description },
            (_, _, _) => { }, config.UsacLayout);
        return (encoder, config);
    }

    // An AudioSpecificConfig of object type 42 at 44100 Hz.
    private static byte[] Build(Spec spec)
    {
        var bits = new BitWriter();
        bits.Put(31, 5).Put(42 - 32, 6).Put(4, 4).Put(spec.ChannelConfiguration is > 0 and < 8 ? spec.ChannelConfiguration : 0, 4);
        bits.Put(4, 5).Put(spec.FrameIndex, 3).Put(spec.ChannelConfiguration, 5);
        if (spec.ChannelConfiguration == 0)
        {
            bits.Put(spec.Positions.Length, 5);
            foreach (int position in spec.Positions)
            {
                bits.Put(position, 5);
            }
        }

        bool sbr = spec.FrameIndex >= 2;
        var elements = new List<Action>();
        void PreRoll()
        {
            bits.Put(3, 2).Put(3, 4).Put(spec.PreRollConfigLength, 4).Put(spec.PreRollDefaultLength ? 1 : 0, 1);
            if (spec.PreRollDefaultLength)
            {
                bits.Put(10, 8);
            }

            bits.Put(spec.PreRollFragmented ? 1 : 0, 1);
        }

        if (spec.PreRoll && spec.PreRollFirst)
        {
            elements.Add(PreRoll);
        }

        foreach (string kind in spec.Elements)
        {
            elements.Add(() =>
            {
                bits.Put(kind switch { "SCE" => 0, "CPE" => 1, _ => 2 }, 2);
                if (kind == "LFE")
                {
                    return;
                }

                bits.Put(spec.TimeWarp ? 1 : 0, 1).Put(1, 1);
                if (!sbr)
                {
                    return;
                }

                bits.Put(spec.HarmonicSbr ? 1 : 0, 1).Put(0, 1).Put(spec.Pvc ? 1 : 0, 1).Put(spec.StartFrequency, 4).Put(spec.StopFrequency, 4);
                bits.Put(0, 1).Put(spec.Extra2 ? 1 : 0, 1);
                if (spec.Extra2)
                {
                    bits.Put(0, 6);
                }

                if (kind == "CPE")
                {
                    bits.Put(spec.StereoConfigIndex, 2);
                    if (spec.StereoConfigIndex > 0)
                    {
                        // Mps212Config: bsFreqRes 2, bsFixedGainDMX, bsTempShapeConfig, bsDecorrConfig,
                        // bsHighRateMode, bsPhaseCoding, bsOttBandsPhasePresent with bsOttBandsPhase.
                        bits.Put(2, 3).Put(0, 3).Put(spec.Shaping, 2).Put(0, 2).Put(1, 1).Put(1, 1).Put(1, 1).Put(5, 5);
                        if (spec.StereoConfigIndex > 1)
                        {
                            bits.Put(4, 5).Put(1, 1);
                        }

                        if (spec.Shaping == 2)
                        {
                            bits.Put(1, 1);
                        }
                    }
                }
            });
        }

        if (spec.PreRoll && !spec.PreRollFirst)
        {
            elements.Add(PreRoll);
        }

        if (spec.Fill)
        {
            elements.Add(() => bits.Put(3, 2).Put(0, 4).Put(spec.FillConfigLength, 4).Put(0, 1).Put(0, 1));
        }

        bits.Put(elements.Count - 1, 4);
        foreach (Action element in elements)
        {
            element();
        }

        var extensions = new List<(int Type, byte[] Payload)>();
        if (spec.StreamId)
        {
            extensions.Add((7, [0x00, 0x80]));
        }

        if (spec.Loudness)
        {
            extensions.Add((2, LoudnessInfo(spec.LoudnessLength, spec.Measurements)));
        }

        if (spec.SecondLoudness)
        {
            extensions.Add((2, LoudnessInfo(spec.LoudnessLength, spec.Measurements)));
        }

        bits.Put(extensions.Count > 0 ? 1 : 0, 1);
        if (extensions.Count > 0)
        {
            bits.Put(extensions.Count - 1, 2);
            foreach ((int type, byte[] payload) in extensions)
            {
                bits.Put(type, 4).Put(payload.Length, 4);
                foreach (byte b in payload)
                {
                    bits.Put(b, 8);
                }
            }
        }

        return bits.ToArray();
    }

    // loudnessInfoSet() (ISO/IEC 23003-4) as exhale writes it: no album loudness, one loudnessInfo()
    // with a sample peak and the given number of program loudness measurements, zero-padded.
    private static byte[] LoudnessInfo(int length, int measurements)
    {
        var bits = new BitWriter();
        bits.Put(0, 6).Put(1, 6).Put(0, 6).Put(0, 7).Put(1, 1).Put(1218, 12).Put(0, 1).Put(measurements, 4);
        for (int i = 0; i < measurements; i++)
        {
            bits.Put(1, 4).Put(156, 8).Put(2, 4).Put(3, 2);
        }

        bits.Put(0, 1);
        byte[] payload = bits.ToArray();
        Assert.True(payload.Length <= length);
        return [.. payload, .. new byte[length - payload.Length]];
    }

    public sealed record Spec
    {
        public int FrameIndex { get; init; } = 1;

        public int ChannelConfiguration { get; init; } = 2;

        public int[] Positions { get; init; } = [];

        public string[] Elements { get; init; } = Stereo;

        public bool PreRoll { get; init; } = true;

        public bool PreRollFirst { get; init; } = true;

        public int PreRollConfigLength { get; init; }

        public bool PreRollDefaultLength { get; init; }

        public bool PreRollFragmented { get; init; }

        public bool Fill { get; init; }

        public int FillConfigLength { get; init; }

        public bool TimeWarp { get; init; }

        public bool HarmonicSbr { get; init; }

        public bool Pvc { get; init; }

        public int StartFrequency { get; init; } = 15;

        public int StopFrequency { get; init; } = 9;

        public bool Extra2 { get; init; }

        public int StereoConfigIndex { get; init; }

        public int Shaping { get; init; }

        public bool Loudness { get; init; } = true;

        public int LoudnessLength { get; init; } = 9;

        public int Measurements { get; init; } = 1;

        public bool SecondLoudness { get; init; }

        public bool StreamId { get; init; }
    }

    private sealed class BitWriter
    {
        private readonly List<bool> bits = [];

        public BitWriter Put(long value, int width)
        {
            for (int i = width - 1; i >= 0; i--)
            {
                bits.Add(((value >> i) & 1) != 0);
            }

            return this;
        }

        public byte[] ToArray()
        {
            var bytes = new byte[(bits.Count + 7) / 8];
            for (int i = 0; i < bits.Count; i++)
            {
                if (bits[i])
                {
                    bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
                }
            }

            return bytes;
        }
    }
}
