using Encspect.Core;
using Encspect.Host;

namespace Encspect.Gui.Tests;

// A format of files that start with "BLOCK": parsing one waits until the analysis is canceled, so
// tests can hold a run in the middle and see what canceling does.
internal sealed class BlockingParser : IFormatParser
{
    private int _started;
    private int _canceled;

    public static byte[] File => "BLOCK and some bytes"u8.ToArray();

    public string Name => "Blocking";

    public int Started => Volatile.Read(ref _started);

    public int Canceled => Volatile.Read(ref _canceled);

    public SniffResult Sniff(SniffContext context) => context.Reader.Length >= 5 && context.Reader.MatchesAt(context.Reader.Start, "BLOCK"u8) ? new(100, context.Reader.Start) : SniffResult.None;

    public void Parse(ParseContext context)
    {
        Interlocked.Increment(ref _started);
        try
        {
            context.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            context.CancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _canceled);
            throw;
        }
    }

    public Analyzer Analyzer() => new(DefaultParsers.Tags, [this, .. DefaultParsers.Formats], DefaultParsers.Codecs);
}

// A format of files that start with "BOOM", whose parser runs out of memory, which the host does
// not turn into a finding but passes on.
internal sealed class ThrowingParser : IFormatParser
{
    public const string Message = "Out of memory in a test.";

    public static byte[] File => "BOOM and some bytes"u8.ToArray();

    public string Name => "Throwing";

    public SniffResult Sniff(SniffContext context) => context.Reader.Length >= 4 && context.Reader.MatchesAt(context.Reader.Start, "BOOM"u8) ? new(100, context.Reader.Start) : SniffResult.None;

    public void Parse(ParseContext context) => throw new OutOfMemoryException(Message);

    public Analyzer Analyzer() => new(DefaultParsers.Tags, [this, .. DefaultParsers.Formats], DefaultParsers.Codecs);
}

// The default format parser, counting the files it parses, so tests see which files a run
// analyzed rather than took from the cache.
internal sealed class CountingParser(IFormatParser inner) : IFormatParser
{
    private readonly System.Collections.Concurrent.ConcurrentBag<long> _lengths = [];

    public string Name => inner.Name;

    // The lengths of the files parsed.
    public IReadOnlyCollection<long> Lengths => _lengths;

    public SniffResult Sniff(SniffContext context) => inner.Sniff(context);

    public bool HasDeclaredEnd => true;

    public long? DeclaredEnd(SniffContext context) => inner.DeclaredEnd(context);

    public void Parse(ParseContext context)
    {
        _lengths.Add(context.Reader.Length);
        inner.Parse(context);
    }

    public static CountingParser Mpeg() => new(DefaultParsers.Formats[0]);

    public Analyzer Analyzer() => new(DefaultParsers.Tags, [this], DefaultParsers.Codecs);
}
