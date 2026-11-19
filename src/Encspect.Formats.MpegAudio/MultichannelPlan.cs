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

    // The frame the parser's feed just read, for the decoder that is called for it next (the host decodes a frame in the call that hands it over, on the same thread): the base
    // channels' side information and the extension, so the decoder does not read them again. Session-owned like the plan itself.
    private Layer12Scale? _offeredBase;
    private Layer2Multichannel? _offeredExtension;
    private long _offeredFirstSample;

    public void Offer(long firstSample, Layer12Scale scale, Layer2Multichannel extension)
    {
        _offeredBase = scale;
        _offeredExtension = extension;
        _offeredFirstSample = firstSample;
    }

    // The offer for the frame at firstSample, taken once; false where none was made for it, and the decoder then reads the frame itself.
    public bool TryTake(long firstSample, out Layer12Scale scale, out Layer2Multichannel extension)
    {
        scale = _offeredBase!;
        extension = _offeredExtension!;
        bool isOffered = scale is not null && extension is not null && _offeredFirstSample == firstSample;
        _offeredBase = null;
        _offeredExtension = null;
        return isOffered;
    }
}
