using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Fuzz;

// Mutates the fixtures for a fixed time and analyzes every mutant, so each CI run tries inputs the
// deterministic corrupt-file tests do not. An exception, a hang, a parser exception that the host
// caught (parse.failed, or children that could not be built) or a report whose nodes leave the
// file fails the run; the input is saved, and the seed and iteration reproduce it.
internal static class Program
{
    private const int MaxCorpusFileSize = 256 * 1024;
    private static readonly TimeSpan PerInputLimit = TimeSpan.FromSeconds(20);

    // Writing a report twice around a collection costs several times the rest of an iteration, so
    // only every this many iterations does it; keyed on the iteration, so a replay of one runs it.
    private const int CollectionCheckInterval = 16;

    // Signatures every parser anchors on; dropping them at random spots makes structure checks run.
    private static readonly byte[][] Tokens =
    [
        "ID3"u8.ToArray(), "3DI"u8.ToArray(), "TAG"u8.ToArray(), "TAG+"u8.ToArray(), "APETAGEX"u8.ToArray(),
        "LYRICSBEGIN"u8.ToArray(), "LYRICSEND"u8.ToArray(), "LYRICS200"u8.ToArray(), "Xing"u8.ToArray(),
        "Info"u8.ToArray(), "VBRI"u8.ToArray(), "LAME"u8.ToArray(), [0xFF, 0xFB], [0xFF, 0xF3], [0xFF, 0xE3], [0xFF, 0xFD],
        [0xFF, 0xF1], [0xFF, 0xF9], [0xFF, 0xF0], "ADIF"u8.ToArray(), "ftyp"u8.ToArray(), "moov"u8.ToArray(), "mdat"u8.ToArray(), "trak"u8.ToArray(),
        "stsz"u8.ToArray(), "stco"u8.ToArray(), "co64"u8.ToArray(), "stsc"u8.ToArray(), "stts"u8.ToArray(), "esds"u8.ToArray(), "moof"u8.ToArray(),
        "trun"u8.ToArray(), "ilst"u8.ToArray(), "meta"u8.ToArray(), "udta"u8.ToArray(), "----"u8.ToArray(), "data"u8.ToArray(), "ID32"u8.ToArray(),
        [0x2B, 0x7], [0x56, 0xE5], [0x05, 0x80, 0x80, 0x80],
        "OggS\0"u8.ToArray(), "Speex   "u8.ToArray(), [0x01, .. "vorbis"u8], "OpusHead"u8.ToArray(), [0xFF, 0xFF, 0xFF, 0xFF],
        new Guid("75B22630-668E-11CF-A6D9-00AA0062CE6C").ToByteArray(), new Guid("75B22636-668E-11CF-A6D9-00AA0062CE6C").ToByteArray(),
        new Guid("8CABDCA1-A947-11CF-8EE4-00C00C205365").ToByteArray(), new Guid("B7DC0791-A9B7-11CF-8EE6-00C00C205365").ToByteArray(),
        new Guid("5FBF03B5-A92E-11CF-8EE3-00C00C205365").ToByteArray(), new Guid("33000890-E5B1-11CF-89F4-00A0C90349CB").ToByteArray(),
        new Guid("D6E229D3-35DA-11D1-9034-00A0C90349BE").ToByteArray(), new Guid("14E6A5CB-C672-4332-8399-A96952065B5A").ToByteArray(),
        [0x62, 0x01, 0x00, 0x00], [0x61, 0x01, 0x00, 0x00], [0xE0, 0x00],
        "MPCK"u8.ToArray(), "MP+\x07"u8.ToArray(), "SH"u8.ToArray(), "AP"u8.ToArray(), "SE\x03"u8.ToArray(), "ST"u8.ToArray(),
        "wvpk"u8.ToArray(), [0x10, 0x04], [0x02, 0x04], [0x2F, 0x02], [0x2F, 0x03], [0x8A], [0x21, 0x16], [0x25, 0x01], [0x2A, 0x00], [0x0D, 0x01],
        [0x7F, 0xFE, 0x80, 0x01], [0xFE, 0x7F, 0x01, 0x80], [0x1F, 0xFF, 0xE8, 0x00, 0x07, 0xF0], [0xFF, 0x1F, 0x00, 0xE8, 0xF0, 0x07],
        [0x64, 0x58, 0x20, 0x25], [0x58, 0x64, 0x25, 0x20], [0x5A, 0x5A, 0x5A, 0x5A], [0x1D, 0x95, 0xF2, 0x62], [0x47, 0x00, 0x4A, 0x03],
        [0x65, 0x5E, 0x31, 0x5E], [0x0A, 0x80, 0x19, 0x21], [0x41, 0xA2, 0x95, 0x47], [0x02, 0xB0, 0x92, 0x61], "ddts"u8.ToArray(), "dtsc"u8.ToArray(),
        "dtsh"u8.ToArray(), "dtsl"u8.ToArray(), "dtse"u8.ToArray(),
    ];

    private static readonly uint[] Sizes = [0, 1, 0x7F, 0x80, 0x7F7F7F7F, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, 10, 128];

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        uint r = (uint)i << 24;
        for (int bit = 0; bit < 8; bit++)
        {
            r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
        }

        return r;
    }).ToArray();

    private static int Main(string[] args)
    {
        string fixtures = Option(args, "--fixtures") ?? "tests/fixtures";
        string output = Option(args, "--out") ?? "fuzz-failures";
        double seconds = double.Parse(Option(args, "--seconds") ?? "60", CultureInfo.InvariantCulture);
        int seed = int.Parse(Option(args, "--seed") ?? Environment.TickCount.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        long? only = Option(args, "--iteration") is { } i ? long.Parse(i, CultureInfo.InvariantCulture) : null;

        byte[][] corpus = Directory.EnumerateFiles(fixtures, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f) is ".mp3" or ".mp2" or ".mp1" or ".bin" or ".m4a" or ".mp4" or ".mov" or ".aac" or ".ogg" or ".spx" or ".opus" or ".mpc" or ".ac3" or ".eac3" or ".wma" or ".asf" or ".wv" or ".wvc" or ".dts" or ".dtshd" or ".mka" or ".mkv" or ".webm" && new FileInfo(f).Length <= MaxCorpusFileSize)
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
            string? failure = Check(analyzer, input, checksCollection: only is not null || iteration % CollectionCheckInterval == 0);
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

    private static string? Check(Analyzer analyzer, byte[] input, bool checksCollection)
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
        if (report.Findings.FirstOrDefault(f => f.Code == "parse.failed") is { } failed)
        {
            return failed.Message;
        }

        if (report.Root.Length != input.Length)
        {
            return $"the root covers {report.Root.Length} bytes of {input.Length}.";
        }

        // Top-level nodes own their bytes; a node that is not contiguous owns only its ranges, so
        // another may sit in its gaps.
        long previousEnd = 0;
        foreach ((Node region, ByteRange range) in report.Root.Children.SelectMany(c => RangesOf(c).Select(r => (c, r))).OrderBy(p => p.r.Offset))
        {
            if (range.Offset < previousEnd)
            {
                return $"region {region.Name} at {range.Offset} overlaps the previous one, which ends at {previousEnd}.";
            }

            previousEnd = range.End;
        }

        // A finding's node must be a node of the tree, which the host found a path to.
        if (report.Findings.FirstOrDefault(f => f.Node is not null && f.Path is null) is { } lost)
        {
            return $"the node of {lost.Code} ({lost.Node!.Name} at {lost.Node.Offset}, {lost.Node.Length} bytes) is not in the tree.";
        }

        if (report.Streams.Any(s => s.NodePaths.Any(p => p is null)))
        {
            return "a stream's node is not in the tree.";
        }

        return OutsideParent(report.Root) ?? (report.Findings.Any(f => f.Node is null) ? "a finding has no node." : null) ?? (checksCollection ? ChangesAfterCollection(report) : null);
    }

    // The nodes of long lists are held weakly and built again after a collection: the report written
    // before and after one must be the same. A collection of generation 1 is enough, since the
    // nodes were built a moment ago.
    private static string? ChangesAfterCollection(Report report)
    {
        try
        {
            string before = Written(report);
            GC.Collect(1, GCCollectionMode.Forced, blocking: true);
            return Written(report) == before ? null : "the report written again after a collection differs.";
        }
        catch (Exception ex)
        {
            return $"writing the report failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
        }
    }

    private static string Written(Report report)
    {
        using var stream = new MemoryStream();
        ReportJsonWriter.Write([report], stream, ReportFilter.All, explain: true);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream.ToArray()));
    }

    private static string? OutsideParent(Node parent)
    {
        foreach (Node child in parent.Children)
        {
            if (child is { Name: "Error", Value: { } value } && value.ToString() is var error && error.StartsWith("The children could not be built", StringComparison.Ordinal))
            {
                return $"{error} (under {parent.Name} at {parent.Offset})";
            }

            if (child.Offset < parent.Offset || child.End > parent.End)
            {
                return $"{child.Name} [{child.Offset}, {child.End}) is outside {parent.Name} [{parent.Offset}, {parent.End}).";
            }

            // A field of a packet that spans pages must not point into a page header between them.
            if (parent.Ranges is not null && RangesOf(child).FirstOrDefault(r => r.Length > 0 && !RangesOf(parent).Any(p => p.Offset <= r.Offset && r.End <= p.End)) is { Length: > 0 } stray)
            {
                return $"{child.Name} holds [{stray.Offset}, {stray.End}), which lies in a gap between the ranges of {parent.Name}.";
            }

            if (OutsideParent(child) is { } failure)
            {
                return failure;
            }
        }

        return null;
    }

    private static IReadOnlyList<ByteRange> RangesOf(Node node) => node.Ranges ?? [new ByteRange(node.Offset, node.Length)];

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

        // Ogg pages that fail their CRC are dropped before their packets reach a codec, so half the
        // time the mutant's pages get their CRCs back and the mutations reach Speex and the comments.
        if (random.Next(2) == 0)
        {
            FixOggCrcs(data);
        }

        return [.. data];
    }

    // Every "OggS" with version 0 whose header, segment table and body fit gets the CRC its bytes
    // give (RFC 3533: polynomial 0x04C11DB7, initial value 0, unreflected, CRC field zeroed).
    private static void FixOggCrcs(List<byte> data)
    {
        byte[] bytes = [.. data];
        for (int at = 0; at + 27 <= bytes.Length; at++)
        {
            if (bytes[at] != 'O' || bytes[at + 1] != 'g' || bytes[at + 2] != 'g' || bytes[at + 3] != 'S' || bytes[at + 4] != 0)
            {
                continue;
            }

            int segments = bytes[at + 26];
            if (at + 27 + segments > bytes.Length)
            {
                continue;
            }

            int length = 27 + segments + bytes.AsSpan(at + 27, segments).ToArray().Sum(b => b);
            if (at + length > bytes.Length)
            {
                continue;
            }

            bytes.AsSpan(at + 22, 4).Clear();
            uint crc = 0;
            foreach (byte b in bytes.AsSpan(at, length))
            {
                crc = (crc << 8) ^ CrcTable[(crc >> 24) ^ b];
            }

            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 22, 4), crc);
        }

        for (int i = 0; i < bytes.Length; i++)
        {
            data[i] = bytes[i];
        }
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
