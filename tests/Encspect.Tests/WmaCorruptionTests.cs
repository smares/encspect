using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Damaged ASF and Windows Media Audio files give findings, never an unhandled exception, and a report whose
// nodes lie where they say: the real files of every codec with a stretch of their data flipped, zeroed, cut or
// repeated at places chosen from a fixed seed (the fuzzer, tests/Encspect.Fuzz, does the same at scale).
public class WmaCorruptionTests
{
    public static TheoryData<string> Names => new()
    {
        "asf/external-ffmpeg-wmav2-stereo-44k-64k.wma",
        "asf/external-ffmpeg-wmav1-mono-22k-24k.wma",
        "asf/external-windows-wma92-cbr-stereo-44k-128.wma",
        "asf/external-windows-wma92-vbr-stereo-44k-q25.wma",
        "asf/external-windows-wma10pro-cbr-5.1-48k-384.wma",
        "asf/external-windows-wma10pro-vbr-stereo-44k-q10.wma",
        "asf/external-windows-wma10pro-cbr-stereo-44k-64.wma",
        "asf/external-windows-wma92lossless-stereo-44k-16.wma",
        "asf/external-windows-wmavoice-cbr-mono-8k-5.wma",
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void DamagedFilesGiveCleanReports(string name)
    {
        byte[] original = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name));
        var random = new Random(name.Sum(c => c) ^ 0x57A);
        var analyzer = Analyzer.CreateDefault();
        int dataStart = Math.Min(original.Length / 4, 1500);
        for (int round = 0; round < 120; round++)
        {
            byte[] data = (byte[])original.Clone();
            switch (round % 5)
            {
                case 0:
                    for (int i = 0; i < 1 + (round / 5); i++)
                    {
                        data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
                    }

                    break;
                case 1:
                    int start = random.Next(data.Length);
                    Array.Clear(data, start, Math.Min(random.Next(1, 400), data.Length - start));
                    break;
                case 2:
                    data = data[..random.Next(1, data.Length)];
                    break;
                case 3:
                    for (int i = 0; i < 40; i++)
                    {
                        data[random.Next(dataStart, data.Length)] = (byte)random.Next(256);
                    }

                    break;
                default:
                    data = [.. data, .. data.AsSpan(dataStart, Math.Min(900, data.Length - dataStart))];
                    break;
            }

            Report report = analyzer.Analyze(name, new MemoryStream(data));
            Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
            Assert.All(report.Findings, f => Assert.NotNull(f.Path));
            Assert.Equal(data.Length, report.Root.Length);
            try
            {
                TestFiles.CheckInsideParents(report.Root);
            }
            catch (Exception) when (Environment.GetEnvironmentVariable("WMA_SAVE") is { } dir)
            {
                File.WriteAllBytes(Path.Combine(dir, $"{Path.GetFileName(name)}-{round}.bin"), data);
                throw;
            }

            foreach (StreamInfo stream in report.Streams)
            {
                Assert.True(stream.Duration >= 0 && stream.Samples >= 0);
            }
        }
    }
}
