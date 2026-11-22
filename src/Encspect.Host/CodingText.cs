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

    public const string ProcessorDescription = "The software that changed the audio before it was encoded, from a mark it left in the file or from how the samples behave, with how sure the analysis is. It is a claim with its evidence in the Processor node, not a verdict about the audio.";

    public const string ProcessorTopic = ClaimRoles.ProcessorTopic;

    // The measured figures that stand beside the coding line, ahead of the other measurements.
    public static bool IsBesideCoding(MeasurementFigure figure) => figure is MeasurementFigure.SpectralCutoff or MeasurementFigure.LowBitsUnused;

    // One line of the evidence beside the coding line.
    public sealed record EvidenceLine(string Name, string Text, string Description, string? Topic);

    // The evidence about the file's history that stands beside the coding line, for the text report and the GUI alike: the processor claim, then the
    // spectral cutoff and the low bits unused with the basis they were measured on. Where a figure is not there, the GUI (placeholders) says why in every case,
    // from the stream's state: "decode the audio" where decoding can still run, "no decoder for this coding" where none exists, the measurer's own reason,
    // "not defined for floating-point samples". The text report states an absence once, under "Measured:", and adds only the one that line does not say
    // (floating-point samples); without measurements it leaves the figures out.
    public static IReadOnlyList<EvidenceLine> Evidence(StreamInfo stream, IReadOnlyList<MeasurementLine>? lines, bool placeholders)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var evidence = new List<EvidenceLine>();
        if (Processor(stream) is { } processor)
        {
            evidence.Add(new EvidenceLine("Processor", processor, ProcessorDescription, ProcessorTopic));
        }

        StreamMeasurements? m = stream.Measurements;
        if (m is null)
        {
            if (placeholders)
            {
                string text = stream.Verification switch
                {
                    { Decoding: DecodeStatus.NotRequested } => "not measured (decode the audio to measure it)",
                    null or { Decoding: DecodeStatus.NoDecoder } => "not measured (no decoder for this coding)",
                    { Decoding: DecodeStatus.NotPossible } => "not measured (the stream cannot be decoded)",
                    _ => "not measured",
                };
                evidence.Add(new EvidenceLine("Spectral Cutoff", text, MeasurementText.SpectralCutoffDescription, MeasurementText.SpectralCutoffTopic));
                evidence.Add(new EvidenceLine("Low Bits Unused", text, MeasurementText.LowBitsUnusedDescription, MeasurementText.BitDepthUsageTopic));
            }

            return evidence;
        }

        string basis = Basis(m);
        foreach ((MeasurementFigure figure, string name, string description, string topic) in new[]
        {
            (MeasurementFigure.SpectralCutoff, "Spectral Cutoff", MeasurementText.SpectralCutoffDescription, MeasurementText.SpectralCutoffTopic),
            (MeasurementFigure.LowBitsUnused, "Low Bits Unused", MeasurementText.LowBitsUnusedDescription, MeasurementText.BitDepthUsageTopic),
        })
        {
            MeasurementLine? line = lines?.FirstOrDefault(l => l.Figure == figure);
            if (line is not null && m.Samples > 0)
            {
                evidence.Add(new EvidenceLine(name, $"{line.Text}, {basis}", description, topic));
            }
            else if (Absence(m, figure, placeholders) is { } absence)
            {
                evidence.Add(new EvidenceLine(name, absence, description, topic));
            }
        }

        return evidence;
    }

    private static string Basis(StreamMeasurements m)
    {
        string basis = "measured from the decoded audio";
        basis += m.Status switch
        {
            MeasurementStatus.Stopped => ", measuring stopped early",
            MeasurementStatus.FormatChanged => ", measuring stopped where the format of the decoded blocks changed",
            MeasurementStatus.Incomplete => ", some frames were not decoded or delivered in part",
            _ => string.Empty,
        };
        return m.Gaps > 0 || m.WithheldSamples > 0 ? basis + ", with gaps or withheld samples counted under Measured" : basis;
    }

    // Null where the text report has nothing to add to what "Measured:" says.
    private static string? Absence(StreamMeasurements m, MeasurementFigure figure, bool placeholders)
    {
        if (figure == MeasurementFigure.LowBitsUnused && m.Status is not (MeasurementStatus.NotPossible or MeasurementStatus.Failed) && m.Levels is { } levels && levels.Format != SampleFormat.Integer)
        {
            return "not defined for floating-point samples";
        }

        if (!placeholders)
        {
            return null;
        }

        return m.Status switch
        {
            MeasurementStatus.NotPossible => m.Reason is { } reason ? $"not measured ({reason})" : "not measured",
            MeasurementStatus.Failed => "not measured (the measurement failed)",
            _ when m.Samples == 0 => "not measured (nothing was measured before the measuring stopped)",
            _ when figure == MeasurementFigure.LowBitsUnused && m.Levels is null => "not measured (levels were not asked for)",
            _ when figure == MeasurementFigure.SpectralCutoff && m.Spectrum is null => "not measured (the spectrum was not asked for)",
            _ => "not measured",
        };
    }
}
