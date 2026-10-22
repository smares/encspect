using System.Runtime.ExceptionServices;
using Encspect.Core;

namespace Encspect.Host;

// The decoding state of one analysis, shared by its sessions: the PCM budget, the front end's stop, and the first
// exception a front end's sink or output callback threw, which the analysis rethrows once the format parser has returned.
internal sealed class DecodeRun
{
    private ExceptionDispatchInfo? _sinkFailure;
    private long _pcmBytes;

    public DecodeRun(DecodeOptions options, long fileLength)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxPcmBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxPcmBytes cannot be negative.");
        }

        Options = options;
        Budget = options.MaxPcmBytes ?? Math.Max(DecodeOptions.MinimumBudget, fileLength > long.MaxValue / DecodeOptions.BudgetPerInputByte ? long.MaxValue : fileLength * DecodeOptions.BudgetPerInputByte);
    }

    public DecodeOptions Options { get; }

    public long Budget { get; }

    public bool IsOutOfBudget => _pcmBytes > Budget;

    public bool IsStopRequested => Options.Stop.IsCancellationRequested;

    public bool HasSinkFailure => _sinkFailure is not null;

    // Bytes of the canonical layout, saturating.
    public void Count(long samples, int channels, int bytesPerSample)
    {
        long bytes = samples * channels * bytesPerSample;
        _pcmBytes = _pcmBytes > long.MaxValue - bytes ? long.MaxValue : _pcmBytes + bytes;
    }

    public void Capture(Exception ex) => _sinkFailure ??= ExceptionDispatchInfo.Capture(ex);

    public void ThrowIfSinkFailed() => _sinkFailure?.Throw();

    // The sink a decoder gets: it forwards to the front end's and never throws.
    public IPcmSink OpenSink(DecodedStream stream)
    {
        IPcmSink? target = null;
        if (Options.Output is { } output && !HasSinkFailure)
        {
            try
            {
                target = output(stream);
            }
            catch (Exception ex)
            {
                Capture(ex);
            }
        }

        return new GuardedSink(this, target);
    }

    private sealed class GuardedSink(DecodeRun run, IPcmSink? target) : IPcmSink
    {
        public void Write(PcmBlock block)
        {
            if (target is null || run.HasSinkFailure)
            {
                return;
            }

            try
            {
                target.Write(block);
            }
            catch (Exception ex)
            {
                run.Capture(ex);
            }
        }

        public void End(long samples)
        {
            if (target is null || run.HasSinkFailure)
            {
                return;
            }

            try
            {
                target.End(samples);
            }
            catch (Exception ex)
            {
                run.Capture(ex);
            }
        }
    }
}
