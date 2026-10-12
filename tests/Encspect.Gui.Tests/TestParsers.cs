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

    public int Sniff(SniffContext context) => context.Reader.Length >= 5 && context.Reader.MatchesAt(context.Reader.Start, "BLOCK"u8) ? 100 : 0;

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
