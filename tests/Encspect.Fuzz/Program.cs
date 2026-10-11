using System.Diagnostics;
using System.Globalization;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Fuzz;

// Mutates the fixtures for a fixed time and analyzes every mutant, so each CI run tries inputs the
// deterministic corrupt-file tests do not. An exception, a hang or a report whose nodes leave the
// file fails the run; the input is saved, and the seed and iteration reproduce it.
internal static class Program
{
    private const int MaxCorpusFileSize = 256 * 1024;
    private static readonly TimeSpan PerInputLimit = TimeSpan.FromSeconds(20);

    // Signatures every parser anchors on; dropping them at random spots makes structure checks run.
    private static readonly byte[][] Tokens =
    [
        "ID3"u8.ToArray(), "3DI"u8.ToArray(), "TAG"u8.ToArray(), "TAG+"u8.ToArray(), "APETAGEX"u8.ToArray(),
        "LYRICSBEGIN"u8.ToArray(), "LYRICSEND"u8.ToArray(), "LYRICS200"u8.ToArray(), "Xing"u8.ToArray(),
        "Info"u8.ToArray(), "VBRI"u8.ToArray(), "LAME"u8.ToArray(), [0xFF, 0xFB], [0xFF, 0xF3], [0xFF, 0xE3], [0xFF, 0xFD],
    ];

    private static readonly uint[] Sizes = [0, 1, 0x7F, 0x80, 0x7F7F7F7F, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, 10, 128];

    private static int Main(string[] args)
    {
        string fixtures = Option(args, "--fixtures") ?? "tests/fixtures";
        string output = Option(args, "--out") ?? "fuzz-failures";
        double seconds = double.Parse(Option(args, "--seconds") ?? "60", CultureInfo.InvariantCulture);
        int seed = int.Parse(Option(args, "--seed") ?? Environment.TickCount.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        long? only = Option(args, "--iteration") is { } i ? long.Parse(i, CultureInfo.InvariantCulture) : null;

        byte[][] corpus = Directory.EnumerateFiles(fixtures, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f) is ".mp3" or ".mp2" or ".mp1" or ".bin" && new FileInfo(f).Length <= MaxCorpusFileSize)
            .Order(StringComparer.Ordinal)
            .Select(File.ReadAllBytes)
            .ToArray();
        if (corpus.Length == 0)
        {
            Console.Error.WriteLine($"No fixtures under {fixtures}.");
            return 2;
        }

        Console.WriteLine($"Seed {seed}, {corpus.Length} fixtures, {seconds} s.");
        var analyzer = Analyzer.CreateDefault();
        var clock = Stopwatch.StartNew();
        long iteration = only ?? 0;
        for (; only is null ? clock.Elapsed.TotalSeconds < seconds : iteration == only; iteration++)
        {
            // Each iteration has its own generator, so --iteration replays one input without the others.
            var random = new Random(Mix(seed, iteration));
            byte[] input = Mutate(corpus, random);
            string? failure = Check(analyzer, input);
            if (failure is not null)
            {
                Directory.CreateDirectory(output);
                string path = Path.Combine(output, $"seed{seed}-iteration{iteration}.bin");
                File.WriteAllBytes(path, input);
                Console.Error.WriteLine($"Iteration {iteration}: {failure}");
                Console.Error.WriteLine($"Input saved to {path}; replay with --seed {seed} --iteration {iteration}.");
                return 1;
            }
        }

        Console.WriteLine($"{iteration - (only ?? 0)} inputs, no failures.");
        return 0;
    }

    // HashCode.Combine is seeded per process, which would make a failure impossible to replay.
    private static int Mix(int seed, long iteration)
    {
        ulong z = ((ulong)(uint)seed << 32 ^ (ulong)iteration) + 0x9E3779B97F4A7C15;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return (int)(z ^ (z >> 31));
    }

    private static string? Option(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string? Check(Analyzer analyzer, byte[] input)
    {
        var task = Task.Run(() => analyzer.Analyze("fuzz", new MemoryStream(input)));
        try
        {
            if (!task.Wait(PerInputLimit))
            {
                return $"analysis did not finish within {PerInputLimit.TotalSeconds} s ({input.Length} bytes).";
            }
        }
        catch (AggregateException ex)
        {
            return $"{ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}\n{ex.InnerException?.StackTrace}";
        }

        Report report = task.Result;
        if (report.Root.Length != input.Length)
        {
            return $"the root covers {report.Root.Length} bytes of {input.Length}.";
        }

        long previousEnd = 0;
        foreach (Node region in report.Root.Children)
        {
            if (region.Offset < previousEnd)
            {
                return $"region {region.Name} at {region.Offset} overlaps the previous one, which ends at {previousEnd}.";
            }

            previousEnd = region.End;
        }

        return OutsideParent(report.Root) ?? (report.Findings.Any(f => f.Node is null) ? "a finding has no node." : null);
    }

    private static string? OutsideParent(Node parent)
    {
        foreach (Node child in parent.Children)
        {
            if (child.Offset < parent.Offset || child.End > parent.End)
            {
                return $"{child.Name} [{child.Offset}, {child.End}) is outside {parent.Name} [{parent.Offset}, {parent.End}).";
            }

            if (OutsideParent(child) is { } failure)
            {
                return failure;
            }
        }

        return null;
    }

    private static byte[] Mutate(byte[][] corpus, Random random)
    {
        var data = new List<byte>(corpus[random.Next(corpus.Length)]);
        int steps = 1 + random.Next(6);
        for (int step = 0; step < steps; step++)
        {
            int at = data.Count == 0 ? 0 : Position(data.Count, random);
            switch (random.Next(8))
            {
                case 0 when data.Count > 0:
                    data[Math.Min(at, data.Count - 1)] ^= (byte)(1 << random.Next(8));
                    break;
                case 1 when data.Count > 0:
                    data[Math.Min(at, data.Count - 1)] = (byte)random.Next(256);
                    break;
                case 2:
                    data.RemoveRange(at, Math.Min(data.Count - at, 1 + random.Next(512)));
                    break;
                case 3:
                    data.InsertRange(at, Enumerable.Range(0, 1 + random.Next(64)).Select(_ => (byte)random.Next(256)));
                    break;
                case 4 when data.Count > 0:
                    int start = random.Next(data.Count);
                    data.InsertRange(at, data.GetRange(start, Math.Min(data.Count - start, 1 + random.Next(4096))));
                    break;
                case 5:
                    byte[] other = corpus[random.Next(corpus.Length)];
                    int from = random.Next(other.Length + 1);
                    data.RemoveRange(at, data.Count - at);
                    data.AddRange(other.AsSpan(from).ToArray());
                    break;
                case 6:
                    uint size = Sizes[random.Next(Sizes.Length)];
                    byte[] bytes = random.Next(2) == 0 ? BitConverter.GetBytes(size) : [.. BitConverter.GetBytes(size).Reverse()];
                    Overwrite(data, at, bytes);
                    break;
                default:
                    Overwrite(data, at, Tokens[random.Next(Tokens.Length)]);
                    break;
            }
        }

        return [.. data];
    }

    // Tags sit at both ends of a file, so half the positions fall within 1 KiB of an end.
    private static int Position(int count, Random random) => random.Next(4) switch
    {
        0 => random.Next(Math.Min(count, 1024)),
        1 => count - random.Next(Math.Min(count, 1024)),
        _ => random.Next(count + 1),
    };

    private static void Overwrite(List<byte> data, int at, byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            if (at + i < data.Count)
            {
                data[at + i] = bytes[i];
            }
            else
            {
                data.Add(bytes[i]);
            }
        }
    }
}
