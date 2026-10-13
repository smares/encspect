using Encspect.Core;

namespace Encspect.Formats.Aac;

// The shape of a UsacConfig read to its end (ISO/IEC 23003-3 5.2): the coreSbrFrameLengthIndex,
// its elements in order and its configuration extensions. Config is the UsacConfig node.
internal sealed record UsacLayout(Node Config, int CoreSbrFrameLengthIndex, IReadOnlyList<UsacElementLayout> Elements,
    IReadOnlyList<UsacExtensionLayout> ConfigExtensions);

// One element of UsacDecoderConfig(): its kind ("SCE", "CPE", "LFE" or "EXT"), for a single
// channel or channel pair element its core settings and, with SBR, its SbrConfig defaults and the
// stereoConfigIndex; for an extension element its UsacExtElementConfig().
internal sealed record UsacElementLayout
{
    public required string Kind { get; init; }

    public bool TimeWarpedMdct { get; init; }

    public bool NoiseFilling { get; init; }

    public bool HarmonicSbr { get; init; }

    public bool InterTes { get; init; }

    public bool Pvc { get; init; }

    public long? StartFrequency { get; init; }

    public long? StopFrequency { get; init; }

    public bool DefaultHeaderExtra2 { get; init; }

    public long StereoConfigIndex { get; init; }

    public long ExtensionType { get; init; } = -1;

    public long ConfigLength { get; init; }

    public bool HasDefaultLength { get; init; }

    public bool Fragmented { get; init; }

    public int Channels => Kind switch { "CPE" => 2, "SCE" or "LFE" => 1, _ => 0 };
}

// One UsacConfigExtension() entry: its type, its length in bytes and, for loudness info, whether
// it holds one loudnessInfo() with one measurement (AudioSpecificConfig.IsSingleLoudnessMeasurement).
internal sealed record UsacExtensionLayout(long Type, long Length, bool SingleLoudnessMeasurement);
