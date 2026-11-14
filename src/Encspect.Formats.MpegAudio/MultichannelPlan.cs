using Encspect.Core;

namespace Encspect.Formats.MpegAudio;

// What the parser tells the decoder about a stream whose frames carry the MPEG-2 multichannel extension (ISO/IEC 13818-3 2.5) in full: the speakers it outputs, in
// the order of Layer2Multichannel.Layout, and the configuration every frame must state again (docs/design/lossy-decoders.md, "MPEG-2 multichannel").
internal sealed class MultichannelPlan(IReadOnlyList<Speaker> layout, (bool, int, bool, int) key, bool hasCenter, int surround, bool hasLfe)
{
    public IReadOnlyList<Speaker> Layout { get; } = layout;

    public (bool, int, bool, int) Key { get; } = key;

    public bool HasCenter { get; } = hasCenter;

    public int Surround { get; } = surround;

    public bool HasLfe { get; } = hasLfe;

    public int Channels => Layout.Count;

    // The channels of the full-bandwidth programs: all but the LFE.
    public int FullChannels => Layout.Count - (HasLfe ? 1 : 0);
}
