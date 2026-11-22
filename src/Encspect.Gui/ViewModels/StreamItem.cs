using System.Globalization;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Gui.ViewModels;

// One stream of the report in a line: "MPEG-1 Layer III · 44.1 kHz · Joint stereo · stereo (front
// left, front right) · 3:25 · 192 kbps VBR · LAME 3.100 (high) · written by FFmpeg (Lavf60.16.100) (low)".
public sealed class StreamItem(StreamInfo stream)
{
    public StreamInfo Stream { get; } = stream;

    public string Text
    {
        get
        {
            var parts = new List<string> { Stream.Codec };
            if (CodingText.Describe(Stream) is { } coding)
            {
                parts.Add(coding);
            }

            if (Stream.SampleRate > 0)
            {
                parts.Add(Format.SampleRate(Stream.SampleRate));
            }

            if (Stream.ChannelMode is { } mode)
            {
                parts.Add(mode);
            }
            else if (Stream.Channels > 0)
            {
                parts.Add(Format.Count(Stream.Channels, "channel", "channels"));
            }

            if (Stream.ChannelLayout is { } layout)
            {
                parts.Add(ChannelLayouts.Describe(layout));
            }

            if (Stream.MultilingualChannels > 0)
            {
                parts.Add(Format.Count(Stream.MultilingualChannels, "multilingual channel", "multilingual channels"));
            }

            if (Stream.BitsPerSample is { } bits)
            {
                parts.Add(bits.ToString(CultureInfo.InvariantCulture) + " bit");
            }

            if (Stream.Duration > 0)
            {
                parts.Add(Format.Duration(Stream.Duration));
            }

            if (Stream.LostSamples > 0)
            {
                parts.Add(Format.Count(Stream.LostSamples, "lost sample", "lost samples"));
            }

            if (Stream.AverageBitrate is { } bitrate)
            {
                parts.Add(Format.Bitrate(bitrate, Stream.IsVbr));
            }

            if (Stream.HasObjectAudio)
            {
                parts.Add(Stream.JocObjectCount is { } objects ? $"object audio ({Format.Count(objects, "object", "objects")})" : "object audio");
            }

            if (Stream.Protection is { } protection)
            {
                parts.Add(ProtectionText(protection));
            }

            if (Stream.Encoder is { } encoder)
            {
                parts.Add(Format.Encoder(encoder, Stream.EncoderConfidence));
            }

            if (Stream.Writer is { } writer)
            {
                parts.Add("written by " + Format.Encoder(writer, Stream.WriterConfidence));
            }

            if (Stream.Processor is { } processor)
            {
                parts.Add("processed by " + Format.Encoder(processor, Stream.ProcessorConfidence));
            }

            if (Stream.Claims.FirstOrDefault(c => c.Kind == StreamClaim.LossyPart) is { } lossyPart)
            {
                parts.Add($"lossy part of a {lossyPart.Value} (" + lossyPart.Confidence.ToString().ToLowerInvariant() + ")");
            }

            if (Stream.Correction is { } pairing)
            {
                parts.Add(FileSummary.CorrectionLine(pairing));
            }

            return string.Join(" · ", parts);
        }
    }

    // The evidence about the file's history that stands beside the coding: the processor claim and the spectral cutoff and the low bits unused, which read "not measured" until the audio
    // is decoded and measured; empty for a stream whose coding has no class.
    public IReadOnlyList<string> Evidence
    {
        get
        {
            if (CodingText.Describe(Stream) is null)
            {
                return [];
            }

            var lines = new List<string>();
            if (CodingText.Processor(Stream) is { } processor)
            {
                lines.Add("Processor: " + processor);
            }

            IReadOnlyList<MeasurementLine> measured = Stream.Measurements is { } m ? MeasurementText.Lines(m, Stream.Verification?.IsLossy == true) : [];
            foreach ((MeasurementFigure figure, string name) in new[] { (MeasurementFigure.SpectralCutoff, "Spectral Cutoff"), (MeasurementFigure.LowBitsUnused, "Low Bits Unused") })
            {
                lines.Add($"{name}: {measured.FirstOrDefault(l => l.Figure == figure)?.Text ?? "not measured"}");
            }

            return lines;
        }
    }

    // "encrypted: Common Encryption, AES-CTR (cenc); DRM systems: Widevine Content Protection, Microsoft PlayReady": the scheme, then the
    // DRM systems the file names; "protected, not encrypted:" where the scheme covers samples that are in the clear.
    internal static string ProtectionText(StreamProtection protection) =>
        (protection.IsEncrypted ? "encrypted: " : "protected, not encrypted: ") + protection.Scheme
        + (protection.Systems.Count > 0 ? "; DRM systems: " + string.Join(", ", protection.Systems) : string.Empty);
}
