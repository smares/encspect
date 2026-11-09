namespace Encspect.Core;

// A claim about a stream that the analysis makes from evidence of different strength, held as data so that the reports and front ends read it without the
// node tree. A parser makes it from the bytes; the host may raise its confidence when an independent signal agrees (the file name's extension, for
// ExtensionSignals) and nothing lowers it. A claim the bytes speak against is held unasserted: it is not shown, and lets the host note a disagreeing signal.
public sealed class StreamClaim
{
    // The stream is the lossy part of a pair whose other part, a correction file, makes it lossless (OptimFROG DualStream).
    public const string LossyPart = "lossy-part";

    public StreamClaim(string kind, string value, bool isAsserted, Confidence confidence, string evidence)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(evidence);
        Kind = kind;
        Value = value;
        IsAsserted = isAsserted;
        Confidence = confidence;
        Evidence = evidence;
    }

    public string Kind { get; }

    // What the claim says, in words ("Probably the lossy part of a DualStream").
    public string Value { get; }

    public bool IsAsserted { get; }

    public Confidence Confidence { get; private set; }

    // What points to it, naming each signal that agrees.
    public string Evidence { get; private set; }

    // Raises the confidence to `confidence` (never lowers it) and adds the signal to the evidence.
    public void Raise(Confidence confidence, string signal)
    {
        ArgumentException.ThrowIfNullOrEmpty(signal);
        if (confidence > Confidence)
        {
            Confidence = confidence;
        }

        Evidence = $"{Evidence}, and {signal}";
    }
}
