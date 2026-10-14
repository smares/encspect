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
    // are dropped while other threads read them.
    public sealed class Collector : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _thread;

        public Collector()
        {
            _thread = new Thread(() =>
            {
                var random = new Random(3);
                while (!_stop.IsCancellationRequested)
                {
                    GC.Collect(random.Next(3), GCCollectionMode.Forced, blocking: true, compacting: false);
                    Thread.Sleep(random.Next(4));
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
