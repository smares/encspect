using Encspect.Core;

namespace Encspect.Formats.Wma;

// Windows Media Audio Voice (the format tag 0x000A). Identified only: the container's figures for the stream and its
// packets counted. The 46 bytes of codec-specific data and the frames are not read.
public sealed class WmaVoiceParser : ICodecParser
{
    public const string CodecId = "wma-voice";

    public string Id => CodecId;

    public void Parse(CodecContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        WmaHeaderOnly.Parse(context, "Windows Media Audio Voice", Topics.WmaVoice, "wma.voice-not-read",
            "Windows Media Audio Voice is identified only: the stream is described by what the container states, and its packets are counted; the codec-specific data and the frames are not read.", null);
    }
}
