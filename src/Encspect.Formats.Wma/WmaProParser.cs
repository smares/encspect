using Encspect.Core;

namespace Encspect.Formats.Wma;

// Windows Media Audio Professional (the format tag 0x0162). Read at header level until the frames are read again by
// a clean-room rewrite: the stream is described by what the container and the codec-specific data state, and its packets are counted.
public sealed class WmaProParser : ICodecParser
{
    public const string CodecId = "wma-pro";

    public string Id => CodecId;

    public void Parse(CodecContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        WmaHeaderOnly.Parse(context, "Windows Media Audio Professional", Topics.WmaPro, "wma.pro-not-read",
            "The frames of Windows Media Audio Professional are not read: the stream is described by what the container states, and its packets are counted.", AddConfiguration, channelMaskInData: true);
    }

    private static void AddConfiguration(CodecConfiguration configuration, FindingCollector findings)
    {
        if (configuration.Data.Length >= Layout.Size)
        {
            Layout.Add(configuration.Parent, configuration.Data, Topics.WmaPro, lossless: false);
        }
    }
}
