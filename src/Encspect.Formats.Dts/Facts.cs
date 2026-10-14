using Encspect.Core;

namespace Encspect.Formats.Dts;

// What reading a unit found, for the statistics, the streams and the checks against a container's
// configuration: the fields the analysis keeps after the unit's own record is made.
internal sealed class UnitFacts
{
    public bool HasCore { get; set; }

    public CoreHeader Core { get; set; }

    public StreamForm Form { get; set; }

    // Fields after those of CoreHeader (5.4.2).
    public int EncoderRevision { get; set; }

    public int Pcmr { get; set; }

    public bool IsSumDifferenceFront { get; set; }

    public bool IsSumDifferenceSurround { get; set; }

    // The dialog normalization gain in dB, or null where VERNUM carries none.
    public int? DialogNormalization { get; set; }

    // The primary audio coding header was read to its end.
    public bool HasAudioHeader { get; set; }

    public int PrimaryChannels { get; set; }

    public int Subframes { get; set; }

    // The core extension found by its sync word and distance: 0 none, XCh, X96 or XXCH.
    public CoreExtension Extension { get; set; }

    public int XxchChannels { get; set; }

    public uint XxchSpeakerMask { get; set; }

    public int XxchCoreMask { get; set; }

    public List<SubstreamFacts> Substreams { get; } = [];

    // The core frame's size in bytes of the bit-packed stream (FSIZE + 1), or the substream core's.
    public int CorePackedLength { get; set; }

    // Frames whose header or substream CRC failed.
    public bool HasCrcMismatch { get; set; }

    // The components that name the stream: those of the core and its extension, and of the first asset of the first extension
    // substream only, as FFmpeg's decoder reads a stream (dcadec.c, assets[0]).
    public int ProfileComponents
    {
        get
        {
            int mask = HasCore ? Tables.CoreSubstreamCore : 0;
            mask |= Extension switch { CoreExtension.Xch => Tables.CoreSubstreamXch, CoreExtension.X96 => Tables.CoreSubstreamX96, CoreExtension.Xxch => Tables.CoreSubstreamXxch, _ => 0 };
            return mask | (Substreams.FirstOrDefault()?.Assets.FirstOrDefault()?.Components ?? 0);
        }
    }

    // The components Table E-2 lists, as Tables.CoreSubstreamCore and the like, that this unit holds.
    public int Components
    {
        get
        {
            int mask = HasCore ? Tables.CoreSubstreamCore : 0;
            mask |= Extension switch { CoreExtension.Xch => Tables.CoreSubstreamXch, CoreExtension.X96 => Tables.CoreSubstreamX96, CoreExtension.Xxch => Tables.CoreSubstreamXxch, _ => 0 };
            foreach (SubstreamFacts substream in Substreams)
            {
                foreach (AssetFacts asset in substream.Assets)
                {
                    mask |= asset.Components;
                }
            }

            return mask;
        }
    }
}

internal enum CoreExtension : byte
{
    None,
    Xch,
    X96,
    Xxch,
}

internal sealed class SubstreamFacts
{
    public int Index { get; set; }

    public bool HasStaticFields { get; set; }

    // The frame's duration in seconds from the reference clock (7.5.2, nuExSSFrameDurationCode), or 0 without static fields.
    public double Duration { get; set; }

    public bool IsCrcValid { get; set; }

    public List<AssetFacts> Assets { get; } = [];
}

internal sealed class AssetFacts
{
    public int Index { get; set; }

    // The coding mode (Table 7-14) and the components in Table E-2's bits for the extension substream.
    public int CodingMode { get; set; }

    public int Components { get; set; }

    public bool HasStaticFields { get; set; }

    public int Channels { get; set; }

    public int BitResolution { get; set; }

    public int MaxSampleRate { get; set; }

    // The loudspeaker activity mask where the asset has one, else 0.
    public int SpeakerMask { get; set; }

    public bool HasSpeakerMask { get; set; }

    // The core component of the asset's core frame, read like a core frame: its header, if the core is in the substream.
    public CoreHeader? Core { get; set; }

    // XLL's common header: samples per frame of the first frequency band, or 0.
    public int XllSamples { get; set; }

    // LBR's decoder initialization: its sample rate, speaker mask and bit rate, or 0.
    public int LbrSampleRate { get; set; }

    public int LbrSpeakerMask { get; set; }

    public int LbrBitRate { get; set; }

    public int LbrFlags { get; set; }

    // The sizes of the asset's components in bytes, by their bit in Table 7-15.
    public Dictionary<int, int> ComponentSizes { get; set; } = [];
}
