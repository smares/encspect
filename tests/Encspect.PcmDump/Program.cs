using System.Text;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.PcmDump;

// Writes the PCM that Encspect's decoders deliver, for the cross-check scripts (scripts/fixtures/compare_pcm.py): the library with an Output
// sink, nothing more. For each file and each decoded stream it writes <number>-<name>.s<index>.pcm, the samples in the canonical layout of
// CanonicalPcm (interleaved, little-endian, floats as IEEE 754 singles), and one <number>-<name>.pcm.json that says what the files hold: the stream's
// format and the blocks as runs of [position, samples], untrimmed and at the coded stage, so that a position a decoder withheld or lost is a
// jump between runs. Not shipped (docs/design/lossy-decoders.md, "The PCM cross-checks").
internal static class Program
{
    private static int Main(string[] args)
    {
        bool isHelp = args.Length > 0 && args[0] is "-h" or "--help";
        if (args.Length < 2 || isHelp)
        {
            Console.Error.WriteLine("usage: PcmDump <output folder> <audio file>...");
            return isHelp ? 0 : 2;
        }

        Directory.CreateDirectory(args[0]);
        int failures = 0;
        int input = 0;
        foreach (string path in args.Skip(1))
        {
            try
            {
                Dump(args[0], path, input++);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"{path}: {ex.Message}");
                failures++;
            }
        }

        return failures == 0 ? 0 : 1;
    }

    private sealed class DumpSink(string pcmPath, PcmFormat format, int index, string codec, Node node) : IPcmSink, IDisposable
    {
        private readonly FileStream _file = File.Create(pcmPath);
        private readonly List<long[]> _runs = [];
        private byte[] _buffer = [];

        public Node Node => node;

        public int Index { get; set; } = index;

        public void Write(PcmBlock block)
        {
            int bytes = checked((int)CanonicalPcm.ByteCount(block, format.BitsPerSample));
            if (_buffer.Length < bytes)
            {
                _buffer = new byte[bytes];
            }

            CanonicalPcm.Write(block, format.BitsPerSample, _buffer);
            _file.Write(_buffer, 0, bytes);
            if (block.Length == 0)
            {
                return;
            }

            if (_runs.Count > 0 && _runs[^1][0] + _runs[^1][1] == block.Position)
            {
                _runs[^1][1] += block.Length;
            }
            else
            {
                _runs.Add([block.Position, block.Length]);
            }
        }

        public void End(long samples)
        {
        }

        public void Dispose() => _file.Dispose();

        public void WriteJson(Utf8JsonWriter writer, int reportIndex)
        {
            writer.WriteStartObject();
            writer.WriteNumber("index", reportIndex);
            writer.WriteString("codec", codec);
            writer.WriteNumber("sampleRate", format.SampleRate);
            writer.WriteNumber("channels", format.Channels);
            writer.WriteString("format", format.Format == SampleFormat.Integer ? "integer" : format.Format == SampleFormat.Float32 ? "float32" : "float64");
            writer.WriteNumber("bitsPerSample", format.BitsPerSample);
            if (format.ChannelLayout is { } layout)
            {
                writer.WriteStartArray("channelLayout");
                foreach (Speaker speaker in layout)
                {
                    writer.WriteStringValue(ChannelLayouts.Name(speaker));
                }

                writer.WriteEndArray();
            }

            writer.WriteString("pcm", Path.GetFileName(pcmPath));
            writer.WriteNumber("samples", _runs.Sum(r => r[1]));
            writer.WriteStartArray("runs");
            foreach (long[] run in _runs)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(run[0]);
                writer.WriteNumberValue(run[1]);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }

    private static void Dump(string folder, string path, int ordinal)
    {
        // Two inputs of the same name in different folders must not overwrite each other: the output names carry the input's number (ordinal).
        string name = Path.GetFileName(path);
        string stem = $"{ordinal:D4}-{name}";
        var sinks = new List<DumpSink>();
        try
        {
            var options = new AnalysisOptions
            {
                Decoding = new DecodeOptions
                {
                    Output = stream =>
                    {
                        var sink = new DumpSink(Path.Combine(folder, $"{stem}.s{sinks.Count}.pcm"), stream.Format, sinks.Count, stream.CodecId, stream.Stream);
                        sinks.Add(sink);
                        return sink;
                    },
                },
            };

            using FileStream input = File.OpenRead(path);
            Report report = Analyzer.CreateDefault().Analyze(name, input, options);
            foreach (DumpSink sink in sinks)
            {
                sink.Dispose();
            }

            using var json = new FileStream(Path.Combine(folder, stem + ".pcm.json"), FileMode.Create);
            using (var writer = new Utf8JsonWriter(json, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("file", path);
                writer.WriteStartArray("streams");
                foreach (DumpSink sink in sinks)
                {
                    int reportIndex = report.Streams.ToList().FindIndex(s => s.Nodes.Contains(sink.Node));
                    sink.WriteJson(writer, reportIndex);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            Console.WriteLine($"{name}: {sinks.Count} decoded {(sinks.Count == 1 ? "stream" : "streams")}");
        }
        finally
        {
            foreach (DumpSink sink in sinks)
            {
                sink.Dispose();
            }
        }
    }
}
