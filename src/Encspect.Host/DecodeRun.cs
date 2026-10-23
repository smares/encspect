using System.Runtime.ExceptionServices;
using Encspect.Core;

namespace Encspect.Host;

// The decoding state of one analysis, shared by its sessions: the work budget and the count against it, the token that
// ends the decoding (linked to the analysis's token and the front end's stop, canceled by the budget), the front end's
// stop, and the first exception a front end's sink or output callback threw, which the analysis rethrows once the format
// parser has returned. Analyze disposes it when the analysis ends, so that the linked token leaves the registrations of a
// long-lived token of the caller.
internal sealed class DecodeRun : IDisposable
{
    private readonly CancellationTokenSource _linked;
    private readonly long _fileLength;
    private ExceptionDispatchInfo? _sinkFailure;
    private long _work;
    private long _perInputByte;

    public DecodeRun(DecodeOptions options, long fileLength, CancellationToken analysis)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxWork is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxWork cannot be negative.");
        }

        Options = options;
        _fileLength = fileLength;
        _linked = CancellationTokenSource.CreateLinkedTokenSource(analysis, options.Stop);
    }

    public DecodeOptions Options { get; }

    // What the decoders get as DecodeContext.CancellationToken: canceled by the analysis, the stop and the budget.
    public CancellationToken Token => _linked.Token;

    // The larger of 2^35 and the file's size times the largest allowance among the decoders that started a session, so
    // it can grow during an analysis; a stream already stopped under the smaller figure stays stopped.
    public long Budget => Options.MaxWork ?? Math.Max(DecodeOptions.MinimumWork, _perInputByte > 0 && _fileLength > long.MaxValue / _perInputByte ? long.MaxValue : _fileLength * _perInputByte);

    public bool IsOutOfBudget => _work > Budget;

    public bool IsStopRequested => Options.Stop.IsCancellationRequested;

    public bool HasSinkFailure => _sinkFailure is not null;

    public void AllowPerInputByte(long unitsPerByte) => _perInputByte = Math.Max(_perInputByte, unitsPerByte);

    // Work units, saturating (a crafted file can state absurd counts). Past the budget the token is canceled at once, before
    // the work the decoder charged.
    public void AddWork(long units)
    {
        _work = units > long.MaxValue - _work ? long.MaxValue : _work + units;
        if (IsOutOfBudget)
        {
            _linked.Cancel();
        }
    }

    public void Dispose() => _linked.Dispose();

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
