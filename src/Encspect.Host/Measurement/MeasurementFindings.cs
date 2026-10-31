using System.Globalization;
using Encspect.Core;

namespace Encspect.Host.Measurement;

// Turns what a measurer saw into the stream's record and the host's measure.* findings. None of them is about the audio but
// measure.non-finite-samples, which is about a file no reader can play as written; a figure is never a finding.
internal static class MeasurementFindings
{
    public delegate void AddFinding(Severity severity, string code, string message, Node? node, int count);

    public static StreamMeasurements Finish(StreamMeasurer measurer, DecodeRun run, bool decodingEndedEarly, bool hasFramesNotDecoded, AddFinding add)
    {
        MeasurementStatus status = MeasurementStatus.Complete;
        string? reason = null;
        if (measurer.Failure is { } failure)
        {
            status = MeasurementStatus.Failed;
            add(Severity.Error, "measure.failed", $"Measuring the stream failed: {failure.GetType().Name}: {failure.Message}", null, 1);
        }
        else if (measurer.HasFormatChanged)
        {
            status = MeasurementStatus.FormatChanged;
            add(Severity.Info, "measure.format-changed", $"The channels or sample format of a decoded block differed from the declared output after {Counts.Of(measurer.StopPosition, "sample", "samples")}; measuring stopped there.", null, 1);
        }
        else if (measurer.HasPositionJumped)
        {
            status = MeasurementStatus.Stopped;
            add(Severity.Info, "measure.positions-jump", $"The positions of the decoded blocks went back at sample {measurer.StopPosition.ToString("N0", CultureInfo.InvariantCulture)}; measuring stopped there.", null, 1);
        }
        else if (measurer.IsLimited)
        {
            status = MeasurementStatus.Stopped;
            long tenths = measurer.SampleRate > 0 ? measurer.Samples * 10 / measurer.SampleRate : 0;
            string basis = run.Options.Measure?.MaxWork is null ? " that the file's size allows" : string.Empty;
            add(Severity.Info, "measure.limit", $"Measuring stopped after {(tenths / 10).ToString("N0", CultureInfo.InvariantCulture)}.{tenths % 10} s of the stream: its work passed the budget of {run.MeasureBudget.ToString("N0", CultureInfo.InvariantCulture)} work units{basis}.", null, 1);
        }
        else if (measurer.IsCanceled || decodingEndedEarly)
        {
            status = MeasurementStatus.Stopped;
        }
        else if (hasFramesNotDecoded)
        {
            status = MeasurementStatus.Incomplete;
        }

        if (measurer.Channels > MeasurementLimits.MaxChannels)
        {
            add(Severity.Info, "measure.channels-limited", $"The stream has {measurer.Channels.ToString("N0", CultureInfo.InvariantCulture)} channels; the first {MeasurementLimits.MaxChannels} were measured.", null, 1);
        }

        if (measurer.NonFiniteSamples > 0)
        {
            add(Severity.Warning, "measure.non-finite-samples", $"The stream holds {Counts.Of(measurer.NonFiniteSamples, "float sample", "float samples")} that are NaN or infinite; they were measured as 0.", null, 1);
        }

        if (measurer.Failure is not null)
        {
            reason = "measuring failed";
        }

        // The figures are made once, here, and a bug in making them is the stream's measure.failed like any other.
        try
        {
            return measurer.Result(status, reason);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failed(ex, null, add, measurer);
        }
    }

    // The record of a measurement that failed: no figures, the failure as a finding.
    public static StreamMeasurements Failed(Exception failure, PcmFormat? format, AddFinding add, StreamMeasurer? measurer = null)
    {
        add(Severity.Error, "measure.failed", $"Measuring the stream failed: {failure.GetType().Name}: {failure.Message}", null, 1);
        return new StreamMeasurements
        {
            Status = MeasurementStatus.Failed,
            Reason = "measuring failed",
            Samples = measurer?.Samples ?? 0,
            SampleRate = measurer?.SampleRate ?? format?.SampleRate ?? 0,
            Channels = measurer?.Channels ?? format?.Channels ?? 0,
            ChannelsMeasured = measurer?.ChannelsMeasured ?? Math.Min(format?.Channels ?? 0, MeasurementLimits.MaxChannels),
        };
    }

    // The name a codec ID has in text for a person.
    public static string DisplayName(string codecId) => codecId switch
    {
        "tta" => "TTA",
        "alac" => "ALAC",
        "flac" => "FLAC",
        "wavpack" => "WavPack",
        "monkeys-audio" => "Monkey's Audio",
        "truehd" => "Dolby TrueHD",
        "mlp" => "MLP",
        _ => codecId,
    };
}
