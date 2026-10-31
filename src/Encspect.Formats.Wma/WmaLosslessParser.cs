using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Wma;

public sealed class WmaLosslessParser : ICodecParser
{
    public const string CodecId = "wma-lossless";

    public string Id => CodecId;

    public void Parse(CodecContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        WmaHeaderOnly.Parse(context, "Windows Media Audio Lossless", Topics.WmaLossless, "wma.lossless-not-read", "not read", (c, f) =>
        {
            if (c.Data.Length >= 18) { CodecData.Add(c.Parent, c.Data, Topics.WmaLossless, true); }
        }, channelMaskInData: true);
    }
}

internal static class LosslessExplain
{
    public const string HasLpcFlags = "Set if each subframe carries a flag for an LPC filter.";
}
