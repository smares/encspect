namespace Encspect.Core;

// Gives a decoder the buffers its session lent the parser (a ref struct cannot be a type argument of Func).
public delegate SampleBuffers LentBuffersProvider();

// The context a decoder reports through; the host builds one for each stream it opens (IDecoder.Open).
public sealed class DecodeContext
{
    private readonly Action<Severity, string, string, Node?, int> _addFinding;
    private readonly Func<PcmFormat, IPcmSink> _declareOutput;
    private readonly LentBuffersProvider? _lent;

    public DecodeContext(DecodeSetup setup, CancellationToken cancellationToken, Action<Severity, string, string, Node?, int> addFinding,
        Func<PcmFormat, IPcmSink> declareOutput, LentBuffersProvider? lent = null)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(addFinding);
        ArgumentNullException.ThrowIfNull(declareOutput);

        Setup = setup;
        CancellationToken = cancellationToken;
        _addFinding = addFinding;
        _declareOutput = declareOutput;
        _lent = lent;
    }

    public DecodeSetup Setup { get; }

    // The analysis's token: canceling throws OperationCanceledException. The host checks it between frames; a decoder
    // whose single frame can take more than about 100 ms (Monkey's Audio at its highest levels) checks it every few
    // thousand samples inside the frame.
    public CancellationToken CancellationToken { get; }

    // The buffers the session lent the parser for the frame being decoded, where its CodedFrame.IsWalked: the warm-up
    // samples and residuals its walk stored. Empty otherwise.
    public SampleBuffers LentBuffers => _lent is null ? default : _lent();

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
    // host's: it forwards to the front end's sink (DecodeOptions.Output) and later to the host's PCM analyses, and it
    // never throws. A coding that decodes to something other than PCM (WavPack's DSD) does not call it.
    public IPcmSink DeclareOutput(PcmFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        return _declareOutput(format);
    }
}
