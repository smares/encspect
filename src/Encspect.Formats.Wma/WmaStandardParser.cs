using Encspect.Core;

namespace Encspect.Formats.Wma;

// Windows Media Audio versions 1 and 2 (the format tags 0x0160 and 0x0161). Read at header level until the
// frames are read again by a clean-room rewrite: the stream is described by what the container states, and its
// packets are counted.
public sealed class WmaStandardParser : ICodecParser
{
    public const string CodecId = "wma";

    public string Id => CodecId;

    public void Parse(CodecContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string name = context.Configuration?.Format?.FormatTag == 0x0160 ? "Windows Media Audio 1" : "Windows Media Audio 2";
        WmaHeaderOnly.Parse(context, name, Topics.WmaStandard, "wma.standard-not-read",
            "The frames of Windows Media Audio versions 1 and 2 are not read: the stream is described by what the container states, and its packets are counted.", null);
    }
}
