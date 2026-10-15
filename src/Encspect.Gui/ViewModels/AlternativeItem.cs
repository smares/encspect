using Encspect.Core;

namespace Encspect.Gui.ViewModels;

// A format besides the one analyzed that the data also matches, and the evidence for it.
public sealed class AlternativeItem(FormatCandidate candidate)
{
    public string Format { get; } = candidate.Format;

    public string Evidence { get; } = $"Analyze as {candidate.Format}: {candidate.Evidence.Describe()}";
}
