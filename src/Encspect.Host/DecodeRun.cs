using System.Runtime.ExceptionServices;
using Encspect.Core;
using Encspect.Host.Measurement;

namespace Encspect.Host;

// The decoding state of one analysis, shared by its sessions: the work budget and the count against it, the token that
// ends the decoding (linked to the analysis's token and the front end's stop, canceled by the budget), the front end's
// stop, and the first exception a front end's sink or output callback threw, which the analysis rethrows once the format
// parser has returned. Analyze disposes it when the analysis ends, so that the linked token leaves the registrations of a
// long-lived token of the caller.
internal sealed class DecodeRun : IDisposable
{
    private readonly CancellationTokenSource _linked;
    private ExceptionDispatchInfo? _sinkFailure;
    private long _work;
    private long _allowance;
    private bool _isCrossed;
    private long _measureWork;
    private bool _isMeasureCrossed;
    private long _measureReserved;
    private readonly Dictionary<Node, StreamMeasurements> _records = new(ReferenceEqualityComparer.Instance);

    public DecodeRun(DecodeOptions options, CancellationToken analysis)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxWork is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxWork cannot be negative.");
        }

        Options = options;
        _linked = CancellationTokenSource.CreateLinkedTokenSource(analysis, options.Stop);
    }

    public DecodeOptions Options { get; }

    // What the decoders get as DecodeContext.CancellationToken: canceled by the analysis, the stop and the budget.
    public CancellationToken Token => _linked.Token;

    // The larger of 2^35 and the allowance the sessions have earned: each session adds the bytes of every unit it is handed times its own decoder's
    // units per byte (IDecoder.WorkPerInputByte), so a coding's allowance applies to its own bytes only. It grows during an analysis; a stream already
    // stopped under the smaller figure stays stopped.
    public long Budget => Options.MaxWork ?? Math.Max(DecodeOptions.MinimumWork, _allowance);

    // Sticky: a canceled token cannot be un-canceled, so a budget that grows after it was crossed (a later session of a decoder with a larger
    // allowance) must not read as not crossed while the decoders of the other sessions hold a canceled token.
    public bool IsOutOfBudget => _isCrossed || _work > Budget;

    // The most state the measurements of one analysis hold at once, in bytes (docs/design/spectral-analysis.md, "Memory").
    public const long MeasureMemoryBound = 64L << 20;

    public bool IsMeasuring => Options.Measure is not null;

    // The measurements' own budget, counted apart from the decoders' so that measuring never changes how far a stream decodes.
    public long MeasureBudget => Options.Measure?.MaxWork ?? Math.Max(DecodeOptions.MinimumWork, _allowance);

    // Charges the work of a block before it is done; false once the budget is passed (the block is not measured).
    public bool ChargeMeasure(long units)
    {
        if (_isMeasureCrossed)
        {
            return false;
        }

        _measureWork = units > long.MaxValue - _measureWork ? long.MaxValue : _measureWork + units;
        if (_measureWork > MeasureBudget)
        {
            _isMeasureCrossed = true;
            return false;
        }

        return true;
    }

    public bool TryReserveMeasureMemory(long bytes)
    {
        if (bytes > MeasureMemoryBound - _measureReserved)
        {
            return false;
        }

        _measureReserved += bytes;
        return true;
    }

    public void ReleaseMeasureMemory(long bytes) => _measureReserved -= bytes;

    public void AddRecord(Node stream, StreamMeasurements record) => _records.TryAdd(stream, record);

    // The streams of a file whose configuration changes share one node (a parser's session names the node of the stream it starts in, and several streams start in the one region), so the
    // record of a session is also kept by the verification its parser put on its stream: that object is the session's own.
    private readonly Dictionary<StreamVerification, StreamMeasurements> _recordsBySession = new(ReferenceEqualityComparer.Instance);

    public void LinkRecord(StreamVerification verification, StreamMeasurements record) => _recordsBySession.TryAdd(verification, record);

    public StreamMeasurements? RecordFor(StreamInfo stream) =>
        stream.Verification is { } verification && _recordsBySession.TryGetValue(verification, out StreamMeasurements? own) ? own
        : stream.Nodes.Count > 0 && _records.TryGetValue(stream.Nodes[0], out StreamMeasurements? record) ? record : null;

    public bool IsStopRequested => Options.Stop.IsCancellationRequested;

    public bool HasSinkFailure => _sinkFailure is not null;

    // A session's allowance for the bytes of a unit it is handed, before the unit is decoded; saturating.
    public void Allow(long bytes, long unitsPerByte)
    {
        long units = unitsPerByte > 0 && bytes > long.MaxValue / unitsPerByte ? long.MaxValue : bytes * unitsPerByte;
        _allowance = units > long.MaxValue - _allowance ? long.MaxValue : _allowance + units;
    }

    // Work units, saturating (a crafted file can state absurd counts). Past the budget the token is canceled at once, before
    // the work the decoder charged.
    public void AddWork(long units)
    {
        _work = units > long.MaxValue - _work ? long.MaxValue : _work + units;
        if (IsOutOfBudget)
        {
            _isCrossed = true;
            _linked.Cancel();
        }
    }

    public void Dispose() => _linked.Dispose();

    // A sink failure ends the decoding of every session, inside a long frame too.
    public void Capture(Exception ex)
    {
        if (_sinkFailure is null)
        {
            _sinkFailure = ExceptionDispatchInfo.Capture(ex);
            _linked.Cancel();
        }
    }

    public void ThrowIfSinkFailed() => _sinkFailure?.Throw();

    // The sink a decoder gets: it forwards to the front end's and to the stream's measurer, and never throws.
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

        StreamMeasurer? measurer = null;
        string? refusal = null;
        bool isMemoryLimited = false;
        Exception? openFailure = null;
        long reserved = 0;
        if (Options.Measure is { } measure)
        {
            PcmFormat format = stream.Format;
            if (format.SampleRate <= 0)
            {
                refusal = "the sample rate is not stated";
            }
            else if (format.Channels < 1)
            {
                refusal = "the stream has no channels";
            }
            else
            {
                // A bug in estimating or in making the measurer must not reach the decoder that called DeclareOutput: it becomes measure.failed.
                try
                {
                    measure.Probe?.Invoke("estimate");
                    reserved = StreamMeasurer.EstimateBytes(format, measure);
                    if (TryReserveMeasureMemory(reserved))
                    {
                        measure.Probe?.Invoke("open");
                        measurer = new StreamMeasurer(format, measure, Token, ChargeMeasure);
                    }
                    else
                    {
                        reserved = 0;
                        isMemoryLimited = true;
                        refusal = "the memory bound of the measurements allows no further stream";
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    openFailure = ex;
                    ReleaseMeasureMemory(reserved);
                    reserved = 0;
                }
            }
        }

        return new GuardedSink(this, target, measurer, refusal, reserved) { IsMemoryLimited = isMemoryLimited, OpenFailure = openFailure, Format = stream.Format };
    }

    // The decoder writes to it; the host alone ends it (EndByHost), so the front end's sink sees exactly one End, with the
    // samples the host counted. An End from the decoder is ignored.
    internal sealed class GuardedSink(DecodeRun run, IPcmSink? target, StreamMeasurer? measurer, string? refusal, long reserved) : IPcmSink
    {
        private bool _isEnded;
        private StreamMeasurer? _measurer = measurer;

        public StreamMeasurer? Measurer => _measurer;

        // Called when the record is made: the measurer's working state is freed whenever the parser lets go of the session.
        public void DropMeasurer() => _measurer = null;

        // Why the stream is not measured though measuring was asked for (a rate of 0 Hz, the memory bound), or null.
        public string? Refusal => refusal;

        public long Reserved => reserved;

        public bool IsMemoryLimited { get; init; }

        // The exception of making the measurer, and the format the stream declared (for the record of a stream that has no measurer).
        public Exception? OpenFailure { get; init; }

        public PcmFormat? Format { get; init; }

        // The samples per channel the decoder wrote, whatever became of the frame they belonged to.
        public long Received { get; private set; }

        public void Write(PcmBlock block)
        {
            Received += block.Length;
            _measurer?.Write(block);
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
