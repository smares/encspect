using Encspect.Core;

namespace Encspect.Formats.Wma;

// Windows Media Audio Voice (the format tag 0x000A). Read at header level until the frames are read again by a
// clean-room rewrite: the stream is described by what the container states, and its packets are counted.
public sealed class WmaVoiceParser : ICodecParser
{
    public const string CodecId = "wma-voice";

    public string Id => CodecId;

    public void Parse(CodecContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        WmaHeaderOnly.Parse(context, "Windows Media Audio Voice", Topics.WmaVoice, "wma.voice-not-read",
            "The frames of Windows Media Audio Voice are not read: the stream is described by what the container states, and its packets are counted.", null, channelMaskInData: true);
    }
}
