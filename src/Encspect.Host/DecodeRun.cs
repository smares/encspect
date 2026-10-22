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

    // Bytes of the canonical layout, saturating (the product of three ints can pass a long only with absurd channel counts,
    // but a crafted file may state them).
    public void Count(long samples, int channels, int bytesPerSample)
    {
        UInt128 bytes = (UInt128)(ulong)samples * (ulong)channels * (ulong)bytesPerSample;
        UInt128 total = (UInt128)(ulong)_pcmBytes + bytes;
        _pcmBytes = total > long.MaxValue ? long.MaxValue : (long)total;
    }

    public void Capture(Exception ex) => _sinkFailure ??= ExceptionDispatchInfo.Capture(ex);

    public void ThrowIfSinkFailed() => _sinkFailure?.Throw();

    // The sink a decoder gets: it forwards to the front end's and never throws.
    public GuardedSink OpenSink(DecodedStream stream)
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

    // The decoder writes to it; the host alone ends it (EndByHost), so the front end's sink sees exactly one End, with the
    // samples the host counted. An End from the decoder is ignored.
    internal sealed class GuardedSink(DecodeRun run, IPcmSink? target) : IPcmSink
    {
        private bool _isEnded;

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
        }

        public void EndByHost(long samples)
        {
            if (_isEnded || target is null || run.HasSinkFailure)
            {
                return;
            }

            _isEnded = true;

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
