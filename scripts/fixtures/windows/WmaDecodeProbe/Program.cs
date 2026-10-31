// Decodes WMA files with Microsoft's decoders on a Windows runner and writes one JSON record per file.
//
//   wma-decode-probe --out <dir> [--list <file with one path per line>] [--timeout <seconds>] <file or folder>...
//
// Each file is handled by three child processes of this program (Source Reader, decoder DMO, and the
// DMO again without timestamps), so that a decoder that crashes on a damaged file is recorded and the
// run goes on.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace WmaDecodeProbe;

public static class Program
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        WriteIndented = false,
    };

    public static int Main(string[] args)
    {
        if (args.Length >= 5 && args[0] == "--child")
        {
            return Child(args[1], args[2], args[3], int.Parse(args[4]));
        }

        string? outDir = null, list = null;
        int timeout = 120;
        var inputs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":
                    outDir = args[++i];
                    break;
                case "--list":
                    list = args[++i];
                    break;
                case "--timeout":
                    timeout = int.Parse(args[++i]);
                    break;
                default:
                    inputs.Add(args[i]);
                    break;
            }
        }

        if (outDir == null)
        {
            Console.Error.WriteLine("usage: wma-decode-probe --out <dir> [--list <file>] [--timeout <s>] <file or folder>...");
            return 2;
        }

        if (list != null)
        {
            inputs.AddRange(File.ReadAllLines(list).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
        }

        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                foreach (var f in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext is ".wma" or ".asf")
                    {
                        files.Add(Path.GetFullPath(f));
                    }
                }
            }
            else if (File.Exists(input))
            {
                files.Add(Path.GetFullPath(input));
            }
            else
            {
                Console.Error.WriteLine("not found: " + input);
            }
        }

        Directory.CreateDirectory(Path.Combine(outDir, "records"));
        var decoders = new SortedDictionary<string, string?>();
        var modules = new SortedDictionary<string, string?>();
        var used = new HashSet<string>();
        int written = 0;
        WriteEnvironment(outDir, decoders, modules);
        string root = Path.GetFullPath(inputs.FirstOrDefault(Directory.Exists) ?? ".");
        foreach (var file in files)
        {
            string name = Path.GetFileName(file);
            string recordName = used.Add(name) ? name : name + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file)))[..8];
            var record = new JsonObject
            {
                ["file"] = name,
                ["path"] = Path.GetRelativePath(root, file).Replace('\\', '/'),
                ["bytes"] = new FileInfo(file).Length,
                ["sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant(),
                ["sourceReader"] = RunChild("sr", file, timeout, modules),
                ["direct"] = RunChild("dmo", file, timeout, modules),
                ["directWithoutTimestamps"] = RunChild("dmo-nots", file, timeout, modules),
            };
            var direct = record["direct"]?["decoder"];
            if (direct != null)
            {
                decoders[direct["dll"]?.ToString() ?? direct["clsid"]?.ToString() ?? "?"] = direct["dllVersion"]?.ToString();
            }

            File.WriteAllText(Path.Combine(outDir, "records", recordName + ".json"), record.ToJsonString(Options) + "\n", new UTF8Encoding(false));
            Console.WriteLine(Summary(name, record));
            written++;
        }

        WriteEnvironment(outDir, decoders, modules);
        Console.WriteLine($"{written} records written to {Path.Combine(outDir, "records")}");
        return written > 0 ? 0 : 1;
    }

    private static string Summary(string name, JsonObject record)
    {
        string Part(string key)
        {
            var node = record[key];
            if (node == null)
            {
                return "none";
            }

            if (node["crashed"] != null)
            {
                return "crashed " + node["exitCode"];
            }

            if (node["stalled"] != null)
            {
                return key == "sourceReader"
                    ? $"stalled after {node["calls"]} reads (last timestamp {node["lastTimestamp"]}, in ReadSample {node["inReadSample"]})"
                    : $"stalled after {node["objectCount"] ?? 0} objects, {node["totalSamples"] ?? 0} samples";
            }

            if (node["timedOut"] != null)
            {
                return "timed out";
            }

            if (key == "sourceReader")
            {
                return node["opened"]?.GetValue<bool>() == true
                    ? $"{node["totalSamples"]} samples, {node["errorCount"]} errors"
                    : "open " + node["openHresult"];
            }

            return node["decoder"] != null ? $"{node["totalSamples"]} samples, {node["objectCount"]} objects" : (node["error"]?.ToString() ?? node["asf"]?["error"]?.ToString() ?? "no decoder");
        }

        return $"{name}: reader {Part("sourceReader")}; decoder {Part("direct")}; without timestamps {Part("directWithoutTimestamps")}";
    }

    private static JsonNode RunChild(string mode, string file, int timeoutSeconds, SortedDictionary<string, string?> modules)
    {
        string json = Path.Combine(Path.GetTempPath(), "wma-probe-" + Guid.NewGuid().ToString("N") + ".json");
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add(typeof(Program).Assembly.Location);
        }

        foreach (var a in new[] { "--child", mode, file, json, Math.Max(5, timeoutSeconds - 10).ToString() })
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            process.Kill(true);
            process.WaitForExit();
            return new JsonObject { ["timedOut"] = true, ["timeoutSeconds"] = timeoutSeconds };
        }

        process.WaitForExit();
        JsonNode? node = null;
        if (File.Exists(json))
        {
            try
            {
                node = JsonNode.Parse(File.ReadAllText(json));
            }
            catch (JsonException)
            {
            }

            File.Delete(json);
        }

        if (node is JsonObject obj)
        {
            if (obj["modules"] is JsonArray mods)
            {
                foreach (var m in mods)
                {
                    modules[m?["name"]?.ToString() ?? "?"] = m?["version"]?.ToString();
                }

                obj.Remove("modules");
            }

            return obj;
        }

        string tail = stderr.Result + stdout.Result;
        return new JsonObject
        {
            ["crashed"] = true,
            ["exitCode"] = "0x" + ((uint)process.ExitCode).ToString("X8"),
            ["output"] = tail.Length > 2000 ? tail[^2000..] : tail,
        };
    }

    // Runs the probe on a thread of its own and waits for the budget: a call into Windows that does not
    // return leaves a record of what was seen up to then (calls, last timestamp, whether it is inside
    // ReadSample and since when) instead of a bare time-out.
    private static int Child(string mode, string file, string jsonPath, int budgetSeconds)
    {
        var record = new Dictionary<string, object?>();
        var worker = new Thread(() =>
        {
            try
            {
                if (mode == "sr")
                {
                    SourceReaderProbe.Run(file, record);
                }
                else if (mode == "asf")
                {
                    DmoProbe.WalkOnly(file, record);
                }
                else
                {
                    DmoProbe.Run(file, record, withTimestamps: mode != "dmo-nots");
                }
            }
            catch (Exception e)
            {
                lock (SourceReaderProbe.Gate)
                {
                    record["exception"] = e.GetType().Name + ": " + e.Message;
                    record["trace"] = e.ToString();
                }
            }
        }) { IsBackground = true };
        worker.Start();
        var waited = Stopwatch.StartNew();
        bool finished = worker.Join(budgetSeconds * 1000);
        string json;
        lock (SourceReaderProbe.Gate)
        {
            if (!finished)
            {
                record["stalled"] = true;
                record["stalledAfterMs"] = waited.ElapsedMilliseconds;
            }

            record["modules"] = Versions.LoadedModules();
            json = "";
            for (int attempt = 0; attempt < 5 && json.Length == 0; attempt++)
            {
                try
                {
                    json = JsonSerializer.Serialize(record, Options);
                }
                catch (Exception e)
                {
                    // Serializing is done under the lock the probes write under, so this is a bug of the
                    // probe or of a type it serializes: say so in the record and try again.
                    record["serializeError"] = e.GetType().Name + ": " + e.Message;
                    Thread.Sleep(50);
                }
            }
        }

        File.WriteAllText(jsonPath, json, new UTF8Encoding(false));
        // The decoders and Media Foundation may keep threads alive; the record is written, so leave now.
        Environment.Exit(0);
        return 0;
    }

    private static void WriteEnvironment(string outDir, SortedDictionary<string, string?> decoders, SortedDictionary<string, string?> modules)
    {
        string? Reg(string name)
        {
            try
            {
                return Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", name, null)?.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        var dlls = new SortedDictionary<string, string?>();
        foreach (var d in new[] { "mfplat.dll", "mfreadwrite.dll", "mf.dll", "mfcore.dll", "wmadmod.dll", "wmspdmod.dll", "wmvcore.dll", "wmasf.dll", "msdmo.dll", "mfasfsrcsnk.dll", "wmcodecdspps.dll" })
        {
            dlls[d] = Versions.Of(Path.Combine(Environment.SystemDirectory, d));
        }

        var env = new JsonObject
        {
            ["windows"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ["productName"] = Reg("ProductName"),
            ["displayVersion"] = Reg("DisplayVersion"),
            ["currentBuild"] = Reg("CurrentBuildNumber"),
            ["updateBuildRevision"] = Reg("UBR"),
            ["osVersion"] = Environment.OSVersion.Version.ToString(),
            ["architecture"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            ["runnerImageOS"] = Environment.GetEnvironmentVariable("ImageOS"),
            ["runnerImageVersion"] = Environment.GetEnvironmentVariable("ImageVersion"),
            ["dotnet"] = Environment.Version.ToString(),
            ["systemDlls"] = JsonSerializer.SerializeToNode(dlls),
            ["decodersUsed"] = JsonSerializer.SerializeToNode(decoders),
            ["loadedModules"] = JsonSerializer.SerializeToNode(modules),
        };
        File.WriteAllText(Path.Combine(outDir, "environment.json"), env.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
    }
}
