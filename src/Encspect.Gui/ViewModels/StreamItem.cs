using System.Globalization;
using Encspect.Core;

namespace Encspect.Gui.ViewModels;

// One stream of the report in a line: "MPEG-1 Layer III · 44.1 kHz · Joint stereo · stereo (front
// left, front right) · 3:25 · 192 kbps VBR · LAME 3.100 (high)".
public sealed class StreamItem(StreamInfo stream)
{
    public StreamInfo Stream { get; } = stream;

    public string Text
    {
        get
        {
            var parts = new List<string> { Stream.Codec };
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

            if (Stream.AverageBitrate is { } bitrate)
            {
                parts.Add(Format.Bitrate(bitrate, Stream.IsVbr));
            }

            if (Stream.Encoder is { } encoder)
            {
                parts.Add(Format.Encoder(encoder, Stream.EncoderConfidence));
            }

            return string.Join(" · ", parts);
        }
    }
}
