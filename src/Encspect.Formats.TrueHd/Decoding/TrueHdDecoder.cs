using Encspect.Core;

namespace Encspect.Formats.TrueHd.Decoding;

// The decoder of Dolby TrueHD and MLP (docs/design/truehd-format.md): it reads the syntax of every access unit with the parser's reader, then
// rebuilds the audio of the substreams the analyzer reads (0 to 2): the prediction filters, the noise channels, the primitive matrices and the
// output shifts, with the lossless check of every presentation compared at each restart header. It outputs the presentation of the highest
// substream. One instance serves one codec ID, the way the parser's two codec IDs do.
public sealed class TrueHdDecoder(string id) : IDecoder
{
    public static TrueHdDecoder TrueHd { get; } = new(TrueHdCodecParser.TrueHdId);

    public static TrueHdDecoder Mlp { get; } = new(TrueHdCodecParser.MlpId);

    public string Id => id;

    public IFrameDecoder Open(DecodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        DecodeSetup setup = context.Setup;
        if (setup.Channels is < 1 or > 8)
        {
            throw new InvalidDataException($"The stream has {Counts.Of(setup.Channels, "channel", "channels")}, but a presentation has 1 to 8");
        }

        return new StreamDecoder(context);
    }
}
