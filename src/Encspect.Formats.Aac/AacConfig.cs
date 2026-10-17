using Encspect.Core;

namespace Encspect.Formats.Aac;

// What a stream's configuration says, from an AudioSpecificConfig (ISO/IEC 14496-3 1.6.2.1) or an
// ADTS header: the core coding, and SBR and PS where they are signaled explicitly; and, once its
// raw data blocks are read, whether they hold SBR data and the PS extension, which implicit
// signaling leaves to them.
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

    // The core's speakers where a program config element or USAC's channel positions give them;
    // otherwise the channel configuration's (Table 1.19).
    public IReadOnlyList<Speaker>? Layout { get; init; }

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

    // How the UsacConfig lays out its elements and configuration extensions, where it was read to
    // its end; encoders differ in it (AacEncoder).
    public UsacLayout? UsacLayout { get; init; }

    // Whether the access units hold SBR data, and SBR data with the parametric stereo extension;
    // null where they were not read.
    public bool? SbrFound { get; init; }

    public bool? PsFound { get; init; }

    // SBR as decoders apply it: signaled, or found where the configuration says nothing about it.
    public bool HasSbr => Sbr == true || (Sbr is null && SbrFound == true);

    // Decoders apply PS only to a core of one channel (fdk-aac's sbrdecoder.cpp, ffmpeg's aacdec).
    public bool IsPsPossible => Channels == 1;

    // PS likewise, where SBR is present and the core has one channel.
    public bool HasPs => IsPsPossible && (Ps == true || (HasSbr && Ps is null && PsFound == true));

    // SBR found that the configuration does not signal, and PS likewise.
    public bool IsSbrImplicit => Sbr is null && SbrFound == true;

    public bool IsPsImplicit => IsPsPossible && HasSbr && Ps is null && PsFound == true;

    public bool IsUsac => ObjectType == 42;

    // Whether each access unit is a raw_data_block whose first element ID can be read.
    public bool HasRawDataBlocks => AacTables.HasRawDataBlock(ObjectType);

    // With implicit SBR, fdk-aac (sbrdecoder.cpp, "assume dual rate SBR") and ffmpeg double any core
    // rate; faad2 doubles only a core rate of up to 24 kHz, which the implicit SBR finding says.
    public int OutputSampleRate => UsacSampleRate ?? (Sbr == true ? ExtensionSampleRate ?? CoreSampleRate * 2
        : IsSbrImplicit ? CoreSampleRate * 2 : CoreSampleRate);

    public int OutputFrameLength => UsacOutputFrameLength
        ?? (CoreSampleRate > 0 ? (int)((long)CoreFrameLength * OutputSampleRate / CoreSampleRate) : CoreFrameLength);

    public int? OutputChannels => HasPs ? 2 : Channels;

    // With PS, signaled or found, a mono core gives left and right.
    public IReadOnlyList<Speaker>? OutputLayout => HasPs ? ChannelLayouts.Stereo
        : Layout ?? (ChannelConfiguration == 0 ? null : AacTables.Layout(ChannelConfiguration));

    // Whether implicit signaling could still add SBR or PS that the access units were not read for:
    // SBR to a GA core that signals nothing about it, PS to an SBR stream of one channel.
    public bool ImplicitPossible =>
        HasRawDataBlocks && SbrFound is null && (Sbr is null || (Sbr == true && Ps is null && Channels == 1));

    public string Codec
    {
        get
        {
            if (IsUsac)
            {
                return "xHE-AAC (USAC)";
            }

            string core = AacTables.ObjectTypeName(ObjectType);
            if (HasSbr)
            {
                if (ObjectType != 2)
                {
                    return HasPs ? $"{core} with SBR and PS" : $"{core} with SBR";
                }

                return HasPs ? "HE-AAC v2" : Ps is null && Channels == 1 && PsFound is null ? "HE-AAC (no PS signaled explicitly)" : "HE-AAC";
            }

            return Sbr is null && HasRawDataBlocks && SbrFound is null ? $"{core} (no SBR signaled explicitly)" : core;
        }
    }

    public string? ChannelMode => HasPs ? "Parametric stereo"
        : ChannelConfiguration == 0 ? (Channels is { } count ? $"{count} channels" : null)
        : AacTables.ChannelMode(ChannelConfiguration);
}
