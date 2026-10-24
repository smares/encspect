using Encspect.Core;

namespace Encspect.Formats.TrueHd;

// The codec parser of the access units a container hands over as packets, one unit to a packet (MP4's mlpa samples, Matroska's A_TRUEHD and A_MLP
// blocks). The routes themselves are a later step; this is the parser they will reach by the codec IDs "truehd" and "mlp".
public sealed class TrueHdCodecParser(string id) : ICodecParser
{
    public const string TrueHdId = "truehd";
    public const string MlpId = "mlp";

    public static TrueHdCodecParser TrueHd { get; } = new(TrueHdId);

    public static TrueHdCodecParser Mlp { get; } = new(MlpId);

    public string Id => id;

    public void Parse(CodecContext context)
    {
        throw new NotSupportedException("Containers do not route to the TrueHD codec parser yet.");
    }
}
