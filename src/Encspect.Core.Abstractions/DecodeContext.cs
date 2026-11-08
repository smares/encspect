namespace Encspect.Core;

// Gives a decoder the buffers its session lent the parser (a ref struct cannot be a type argument of Func).
public delegate SampleBuffers LentBuffersProvider();

// The context a decoder reports through; the host builds one for each stream it opens (IDecoder.Open).
public sealed class DecodeContext
{
    private readonly Action<Severity, string, string, Node?, int> _addFinding;
    private readonly Func<PcmFormat, IPcmSink> _declareOutput;
    private readonly Action<long> _addWork;
    private readonly LentBuffersProvider? _lent;
    private readonly Action<long, long, WithholdReason>? _withhold;

    public DecodeContext(DecodeSetup setup, CancellationToken cancellationToken, Action<Severity, string, string, Node?, int> addFinding,
        Func<PcmFormat, IPcmSink> declareOutput, Action<long> addWork, LentBuffersProvider? lent = null, Action<long, long, WithholdReason>? withhold = null)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(addFinding);
        ArgumentNullException.ThrowIfNull(declareOutput);
        ArgumentNullException.ThrowIfNull(addWork);

        Setup = setup;
        CancellationToken = cancellationToken;
        _addFinding = addFinding;
        _declareOutput = declareOutput;
        _addWork = addWork;
        _lent = lent;
        _withhold = withhold;
    }

    public DecodeSetup Setup { get; }

    // The decoding's token: it is canceled by the analysis's own token, by the front end's Stop and by the work budget
    // (AddWork), and canceling throws OperationCanceledException. The host checks it between frames; a decoder whose
    // single frame can take more than about 100 ms (Monkey's Audio at its highest levels) checks it inside the frame,
    // every few thousand samples and after each chunk it charges. The host tells the causes apart by its own state.
    public CancellationToken CancellationToken { get; }

    // Charges work units (DecodeWork) to the analysis's budget, before the decoder does the work. A frame of at most 2^27
    // units may be charged once; a decoder whose frames can be charged more charges per chunk and checks its token after
    // each charge. Past the budget this cancels CancellationToken at once, before the work it charged, so a decoder that
    // checks the token only between frames finishes the frame it has charged. Work charged in a frame that then fails
    // counts. Throws ArgumentOutOfRangeException for negative units, which reaches the host as decode.failed.
    public void AddWork(long units)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(units);

        _addWork(units);
    }

    // The buffers the session lent the parser for the frame being decoded, where its CodedFrame.IsWalked: the warm-up
    // samples and residuals its walk stored. Empty otherwise.
    public SampleBuffers LentBuffers => _lent is null ? default : _lent();

    // Reports output the decoder holds back: the samples per channel from firstSample (the stream's positions, as PcmBlock.Position)
    // that belong to units it read and decoded, but whose output depends on state a lost unit took with it (docs/design/lossy-decoders.md,
    // "Withheld output"). The measurements count those positions as withheld samples, with the reason, and only the rest of a jump between
    // blocks as lost. Spans come in stream order, each before the Write of the first block past it (or before the stream ends, where no
    // block follows); a span that overlaps the previous one counts once. A sink sees only the jump in positions. Zero samples is no span.
    // Throws ArgumentOutOfRangeException for a negative position or count or an undefined reason, which reaches the host as decode.failed.
    public void Withhold(long firstSample, long samples, WithholdReason reason)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstSample);
        ArgumentOutOfRangeException.ThrowIfNegative(samples);
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        if (samples > 0)
        {
            _withhold?.Invoke(firstSample, samples, reason);
        }
    }

    // Findings on the parser's nodes or on equal ones, which a decoder builds with its parser's node factories; null
    // points to the stream. Findings of one kind (one code) are reported once per stream with a count, pointing to the
    // first, with the message of the first.
    public void AddFinding(Severity severity, string code, string message, Node? node = null, int count = 1)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        _addFinding(severity, code, message, node, count);
    }

    // Called once, before the first block: the PCM the decoder will write, and where to write it. The sink is the
    // host's: it forwards to the front end's sink (DecodeOptions.Output) and to the stream's measurer (docs/design/spectral-analysis.md), and it
    // never throws. The host alone ends it (IPcmSink.End, with the samples it counted); a decoder never calls End, and a call
    // from it is ignored. A coding that decodes to something other than PCM (WavPack's DSD) does not call it.
    public IPcmSink DeclareOutput(PcmFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        return _declareOutput(format);
    }
}
