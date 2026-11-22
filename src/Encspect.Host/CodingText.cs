using Encspect.Core;

namespace Encspect.Host;

// How a stream's coding class reads in the text output and the front ends: "lossless coding", "lossy coding", "hybrid coding (lossless with its correction file)".
// The words say what the class is a class of, the coding, and the hybrid's state follows the pairing's status; a class never states where the audio came from.
public static class CodingText
{
    public const string Topic = Topics.CodingClass;

    public const string Description = "How the coding reconstructs audio from the coded stream, from the coding's definition: lossless, lossy, or hybrid (a lossy stream with a correction file that makes it lossless). It is a property of the coding and says nothing about where the audio came from.";

    // The text after "Coding: ", or null for a stream whose coding has no class.
    public static string? Describe(StreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return stream.CodingClass switch
        {
            CodingClass.Lossless => "lossless coding",
            CodingClass.Lossy => "lossy coding",
            CodingClass.Hybrid => stream.Correction?.Status switch
            {
                CorrectionStatus.Consistent => "hybrid coding (lossless with its correction file)",
                CorrectionStatus.NotWritten => "hybrid coding (lossy, no correction file written)",
                null => "hybrid coding",
                _ => "hybrid coding (lossy without its correction file)",
            },
            _ => null,
        };
    }

    // The word JSON and CSV write.
    public static string? Word(CodingClass? codingClass) => codingClass switch
    {
        CodingClass.Lossless => "lossless",
        CodingClass.Lossy => "lossy",
        CodingClass.Hybrid => "hybrid",
        _ => null,
    };

    // The processor claim as the line beside the coding reads, "lossyWAV (medium)".
    public static string? Processor(StreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return stream.Processor is { } processor ? stream.ProcessorConfidence is { } confidence ? $"{processor} ({confidence.ToString().ToLowerInvariant()})" : processor : null;
    }

    public const string ProcessorDescription = "The software that changed the audio before it was encoded, from a mark it left in the file, with how sure the analysis is. It is a claim with its evidence in the Processor node, not a verdict about the audio.";

    // The measured figures that stand beside the coding line, ahead of the other measurements.
    public static bool IsBesideCoding(MeasurementFigure figure) => figure is MeasurementFigure.SpectralCutoff or MeasurementFigure.LowBitsUnused;
}
