using System.Text;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// A report written every way the front ends write it, with the explanations (descriptions and topics),
// which Fixtures.ToJson leaves out: what two readings of one report, or of two analyses of one file,
// are compared by. What is written is hashed as it goes (the length and a SHA-256), since a long report
// written on eight threads at once would otherwise need gigabytes.
internal static class ReportRendering
{
    public static string Json(Report report)
    {
        using var hash = new HashingStream();
        ReportJsonWriter.Write([report], hash, ReportFilter.All, explain: true);
        return hash.Result();
    }

    public static string Text(Report report)
    {
        using var hash = new HashingStream();
        using (var writer = new StreamWriter(hash, new UTF8Encoding(false), 1 << 16, leaveOpen: true))
        {
            ReportTextWriter.Write(report, writer, showOffsets: true, ReportFilter.All, explain: true);
        }

        return hash.Result();
    }

    public static string Csv(Report report)
    {
        using var hash = new HashingStream();
        using (var writer = new StreamWriter(hash, new UTF8Encoding(false), 1 << 16, leaveOpen: true))
        {
            ReportCsvWriter.Write([report], writer);
        }

        return hash.Result();
    }

    public static string All(Report report) => $"{Json(report)} {Text(report)} {Csv(report)}";

    // Writer 0 writes JSON, 1 the text, 2 CSV.
    public static string Write(Report report, int writer) => (writer % 3) switch
    {
        0 => Json(report),
        1 => Text(report),
        _ => Csv(report),
    };

    // A report written to a string by writer 0 (JSON), 1 (text) or 2 (CSV), for a failure's message only.
    public static string Render(Report report, int writer)
    {
        using var stream = new MemoryStream();
        using (var text = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16, leaveOpen: true))
        {
            switch (writer % 3)
            {
                case 0:
                    text.Flush();
                    ReportJsonWriter.Write([report], stream, ReportFilter.All, explain: true);
                    break;
                case 1:
                    ReportTextWriter.Write(report, text, showOffsets: true, ReportFilter.All, explain: true);
                    break;
                default:
                    ReportCsvWriter.Write([report], text);
                    break;
            }
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // What the hashes of two renderings cannot say: written again now, the report and a reference
    // (another analysis of the same file) are compared by all three writers, and the first line that
    // differs is named. When they agree now, the difference came and went with a collection or a race.
    public static string Differences(Report report, Report reference)
    {
        for (int writer = 0; writer < 3; writer++)
        {
            string difference = FirstDifference(Render(reference, writer), Render(report, writer));
            if (difference.Length > 0)
            {
                return $"Written again now, writer {writer} differs from a fresh analysis: {difference}";
            }
        }

        return "Written again now, the report equals a fresh analysis: the difference did not persist (a collection or a race).";
    }

    // The first line where two texts differ, with its number and both lines (cut to 160 characters), or
    // an empty string when they are equal.
    public static string FirstDifference(string expected, string actual)
    {
        int at = 0;
        while (at < expected.Length && at < actual.Length && expected[at] == actual[at])
        {
            at++;
        }

        if (at == expected.Length && at == actual.Length)
        {
            return string.Empty;
        }

        int line = 1 + expected.AsSpan(0, at).Count('\n');
        int start = expected.LastIndexOf('\n', Math.Max(at - 1, 0)) + 1;
        string Line(string text)
        {
            int begin = Math.Min(start, text.Length);
            int end = text.IndexOf('\n', begin);
            string value = text[begin..(end < 0 ? text.Length : end)];
            return value.Length > 160 ? value[..160] + "..." : value;
        }

        return $"line {line}, offset {at}: expected \"{Line(expected)}\", got \"{Line(actual)}\".";
    }

    private sealed class HashingStream : Stream
    {
        private readonly System.Security.Cryptography.IncrementalHash _hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        private long _length;

        public string Result() => $"{_length}:{Convert.ToHexString(_hash.GetHashAndReset())}";

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _hash.AppendData(buffer);
            _length += buffer.Length;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    public static void Collect()
    {
        for (int i = 0; i < 2; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    // A thread that forces collections of all generations until disposed, so that the nodes held weakly
    // are dropped while other threads read them. The longer the pause between collections, the fewer
    // blocks are built again; a collection every few milliseconds makes threads meet in the list most
    // often, but a report of costly nodes written meanwhile would only build the same blocks again.
    public sealed class Collector : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _thread;

        public Collector(int maximumPauseMilliseconds = 4)
        {
            _thread = new Thread(() =>
            {
                var random = new Random(3);
                while (!_stop.IsCancellationRequested)
                {
                    GC.Collect(random.Next(3), GCCollectionMode.Forced, blocking: true, compacting: false);
                    Thread.Sleep(random.Next(maximumPauseMilliseconds));
                }
            })
            { IsBackground = true };
            _thread.Start();
        }

        public void Dispose()
        {
            _stop.Cancel();
            _thread.Join();
            _stop.Dispose();
        }
    }
}
