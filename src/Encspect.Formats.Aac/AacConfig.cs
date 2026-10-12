namespace Encspect.Formats.Aac;

// What a stream's configuration says, from an AudioSpecificConfig (ISO/IEC 14496-3 1.6.2.1) or an
// ADTS header: the core coding, and SBR and PS where they are signaled explicitly. Implicit
// signaling, an SBR or PS extension found only in the fill elements of the access units, needs the
// spectral data of every channel element parsed and is not checked yet.
internal sealed record AacConfig
{
    // The object type of the core coding: the one after SBR (5) or PS (29) in hierarchical
    // signaling.
    public required int ObjectType { get; init; }

    public required int CoreSampleRate { get; init; }

    public required int ChannelConfiguration { get; init; }

    // The core's channels, from the channel configuration or a program config element; null where
    // neither says.
    public int? Channels { get; init; }

    // True where SBR or PS is signaled present, false where signaled absent, null where the
    // configuration says nothing (implicit signaling remains possible).
    public bool? Sbr { get; init; }

    public bool? Ps { get; init; }

    // "hierarchical" (object type 5 or 29 first) or "backward compatible" (the sync extension
    // 0x2B7 after the core's configuration), for explicit signaling.
    public string? Signaling { get; init; }

    public int? ExtensionSampleRate { get; init; }

    // Samples per access unit at the core's sample rate.
    public required int CoreFrameLength { get; init; }

    // USAC (object type 42): the output rate and samples per access unit, which UsacConfig gives.
    public int? UsacSampleRate { get; init; }

    public int? UsacOutputFrameLength { get; init; }

    public int? UsacSbrRatio { get; init; }

    public bool IsUsac => ObjectType == 42;

    // Whether each access unit is a raw_data_block whose first element ID can be read.
    public bool HasRawDataBlocks => AacTables.HasRawDataBlock(ObjectType);

    public int OutputSampleRate => UsacSampleRate ?? (Sbr == true ? ExtensionSampleRate ?? CoreSampleRate * 2 : CoreSampleRate);

    public int OutputFrameLength => UsacOutputFrameLength
        ?? (CoreSampleRate > 0 ? (int)((long)CoreFrameLength * OutputSampleRate / CoreSampleRate) : CoreFrameLength);

    public int? OutputChannels => Ps == true ? 2 : Channels;

    // Whether implicit signaling could still add SBR or PS: SBR to a GA core that signals nothing
    // about it, PS to an SBR stream of one channel.
    public bool ImplicitPossible =>
        HasRawDataBlocks && (Sbr is null || (Sbr == true && Ps is null && Channels == 1));

    public string Codec
    {
        get
        {
            if (IsUsac)
            {
                return "xHE-AAC (USAC)";
            }

            string core = AacTables.ObjectTypeName(ObjectType);
            if (Sbr == true)
            {
                if (ObjectType != 2)
                {
                    return Ps == true ? $"{core} with SBR and PS" : $"{core} with SBR";
                }

                return Ps == true ? "HE-AAC v2" : Ps is null && Channels == 1 ? "HE-AAC (no PS signaled explicitly)" : "HE-AAC";
            }

            return Sbr is null && HasRawDataBlocks ? $"{core} (no SBR signaled explicitly)" : core;
        }
    }

    public string? ChannelMode => Ps == true ? "Parametric stereo"
        : ChannelConfiguration == 0 ? (Channels is { } count ? $"{count} channels" : null)
        : AacTables.ChannelMode(ChannelConfiguration);
}
