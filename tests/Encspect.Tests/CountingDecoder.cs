using Encspect.Core;

namespace Encspect.Tests;

// Wraps a decoder and sums the work units it charges, passing every charge on to the host, so the report is the real one
// (docs/design/decoders.md, "How a decoder that under-reports is caught"). The wrapper replaces the registered decoder through the
// constructor of Analyzer that takes decoders.
internal sealed class CountingDecoder(IDecoder real) : IDecoder
{
    public long Units { get; private set; }

    public string Id => real.Id;

    public long WorkPerInputByte => real.WorkPerInputByte;

    public IFrameDecoder Open(DecodeContext context)
    {
        var counting = new DecodeContext(
            context.Setup,
            context.CancellationToken,
            context.AddFinding,
            context.DeclareOutput,
            units =>
            {
                Units += units;
                context.AddWork(units);
            },
            () => context.LentBuffers);
        return real.Open(counting);
    }
}
