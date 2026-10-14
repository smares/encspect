using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Dts;

// What makes frames one stream: the same sampling frequency, channel arrangement, LFE flag and
// extension, in the same form (ETSI TS 102 114 Annex E.4 lists the parameters that stay constant
// in a stream for seamless playback), and the same extension substream assets. A change starts a new stream.
internal readonly record struct StreamKey(bool HasCore, int SampleRate, int Amode, bool HasLfe, CoreExtension Extension, StreamForm Form, int Substreams)
{
    public string Describe()
    {
        string channels = HasCore ? $"{Tables.ArrangementShort(Amode)}{(HasLfe ? " + LFE" : string.Empty)}" : "extension substream only";
        return $"{(HasCore ? "DTS" : "DTS-HD")}, {SampleRate} Hz, {channels}{(Extension == CoreExtension.None ? string.Empty : $", with {Extension.ToString().ToUpperInvariant()}")}";
    }

    public static StreamKey Of(UnitFacts facts)
    {
        int substreams = 0;
        foreach (SubstreamFacts substream in facts.Substreams)
        {
            foreach (AssetFacts asset in substream.Assets)
            {
                substreams = HashCode.Combine(substreams, asset.Components, asset.Channels, asset.MaxSampleRate, asset.SpeakerMask, asset.CodingMode);
            }
        }

        if (facts.HasCore)
        {
            return new StreamKey(true, facts.Core.SampleRate, facts.Core.Amode, facts.Core.Lff is 1 or 2, facts.Extension, facts.Form, substreams);
        }

        AssetFacts? first = facts.Substreams.SelectMany(s => s.Assets).FirstOrDefault();
        return new StreamKey(false, RateOf(first), -1, false, CoreExtension.None, StreamForm.BigEndian16, substreams);
    }

    // The sampling frequency of an asset: LBR's own, else the highest its descriptor names.
    public static int RateOf(AssetFacts? asset) => asset is null ? 0 : asset.LbrSampleRate > 0 ? asset.LbrSampleRate : asset.MaxSampleRate;
}

// One stream: the units with one configuration, with the figures its statistics and graphs are made from.
internal sealed class DtsStream(StreamKey key, int firstFrame)
{
    private readonly HashSet<int> _nominal = [];
    private readonly HashSet<int> _revisions = [];
    private readonly List<int> _dialnorms = [];
    private readonly HashSet<int> _resolutions = [];
    private readonly HashSet<int> _sizes = [];
    private readonly HashSet<int> _constructions = [];
    private int _componentUnion;
    private int _profileUnion;
    private int _extensionRate;
    private int _assetChannels;
    private int _assets;
    private AssetFacts? _firstAsset;
    private int _firstAssetUnits;
    private IReadOnlyList<Speaker>? _layout;

    public StreamKey Key => key;

    public int FirstFrame => firstFrame;

    public HashSet<DtsAnalysis.Region> Regions { get; } = [];

    public StreamFigures Figures { get; } = new(key.SampleRate);

    private int CoreFrames { get; set; }

    private int SubstreamFrames { get; set; }

    private int TerminationFrames { get; set; }

    private int XchFrames { get; set; }

    private int X96Frames { get; set; }

    private int XxchFrames { get; set; }

    private int DynamicRangeFrames { get; set; }

    private int TimeStampFrames { get; set; }

    private int AuxiliaryFrames { get; set; }

    private int CrcFrames { get; set; }

    private int CrcErrors { get; set; }

    public bool HasLossless => (_componentUnion & Tables.ExtensionXll) != 0;

    public bool IsVbr => HasLossless || _sizes.Count > 1 || _nominal.Count > 1;

    public IReadOnlyList<Speaker> Layout => _layout ?? [];

    public int Channels => Layout.Count;

    // The sampling frequency the stream delivers: the highest the core and the extensions state (X96 doubles the core's, an asset names
    // its own); the core's is key.SampleRate.
    public int DeliveredRate => Math.Max(key.SampleRate, _extensionRate);

    public string Codec => Profile.Name(_profileUnion);

    // The bits per sample the stream states: the source PCM resolution of the core, else the asset's.
    public int? BitsPerSample => _resolutions.Count == 1 && _resolutions.First() > 0 ? _resolutions.First() : _firstAsset?.BitResolution is > 0 and var bits ? bits : null;

    public string? ChannelMode => key.HasCore ? Tables.ArrangementShort(key.Amode) : null;

    public void Add(long offset, int length, int samples, UnitFacts facts, UnitLayout layout)
    {
        Figures.Add(offset, length, samples);
        if (!layout.IsCut && !layout.IsTruncated)
        {
            _sizes.Add(length);
        }

        _componentUnion |= facts.Components;
        _profileUnion |= facts.ProfileComponents;
        if (facts.HasCore)
        {
            CoreFrames++;
            CoreHeader core = facts.Core;
            TerminationFrames += core.IsNormalFrame ? 0 : 1;
            _nominal.Add(core.Rate);
            DynamicRangeFrames += core.HasDynamicRange ? 1 : 0;
            TimeStampFrames += core.HasTimeStamp ? 1 : 0;
            AuxiliaryFrames += core.HasAuxiliaryData ? 1 : 0;
            CrcFrames += core.HasCrc ? 1 : 0;
            if (facts.HasAudioHeader)
            {
                _revisions.Add(facts.EncoderRevision);
                _resolutions.Add(Tables.SourceResolution(facts.Pcmr));
                if (facts.DialogNormalization is { } gain && !_dialnorms.Contains(gain))
                {
                    _dialnorms.Add(gain);
                }
            }

            switch (facts.Extension)
            {
                case CoreExtension.Xch:
                    XchFrames++;
                    break;
                case CoreExtension.X96:
                    X96Frames++;
                    _extensionRate = Math.Max(_extensionRate, core.SampleRate * 2);
                    break;
                case CoreExtension.Xxch:
                    XxchFrames++;
                    break;
            }
        }

        if (facts.Substreams.Count > 0)
        {
            SubstreamFrames++;
            foreach (AssetFacts asset in facts.Substreams.SelectMany(s => s.Assets))
            {
                _extensionRate = Math.Max(_extensionRate, StreamKey.RateOf(asset));
            }

            AssetFacts? asset0 = facts.Substreams[0].Assets.FirstOrDefault();
            if (asset0 is not null && _firstAsset is null)
            {
                _firstAsset = asset0;
                _assets = facts.Substreams[0].Assets.Count;
                _assetChannels = asset0.Channels;
            }

            _firstAssetUnits++;
        }

        int construction = Tables.StreamConstructionOf(facts.Components);
        if (construction > 0)
        {
            _constructions.Add(construction);
        }

        CrcErrors += facts.HasCrcMismatch ? 1 : 0;
        _layout ??= LayoutOf(facts);
    }

    // The speakers of the first unit's channels: the core's arrangement and its extension channels, or the first asset's speaker
    // mask where the extension substream gives one, which names every channel of the stream.
    private IReadOnlyList<Speaker> LayoutOf(UnitFacts facts)
    {
        AssetFacts? asset = facts.Substreams.SelectMany(s => s.Assets).FirstOrDefault();
        if (asset is { HasSpeakerMask: true, SpeakerMask: not 0 })
        {
            return Layouts.Speakers(Layouts.Mask(asset.SpeakerMask));
        }

        if (!facts.HasCore)
        {
            return asset is { LbrSpeakerMask: not 0 } ? Layouts.Speakers(Layouts.Mask(asset.LbrSpeakerMask)) : asset is { Channels: > 0 } ? ChannelLayouts.Discrete(asset.Channels) : [];
        }

        int amode = facts.Core.Amode;
        List<Position> positions = Layouts.Arrangement(amode);
        if (positions.Count == 0)
        {
            positions = [.. Enumerable.Repeat(Position.None, Math.Max(1, facts.PrimaryChannels))];
        }

        if (facts.Extension == CoreExtension.Xch)
        {
            positions.Add(Position.CenterSurround);
        }
        else if (facts.Extension == CoreExtension.Xxch && Tables.ArrangementChannels(amode) <= 5)
        {
            positions.AddRange(Layouts.XxchMask(facts.XxchSpeakerMask));
        }

        if (facts.Core.Lff is 1 or 2)
        {
            positions.Add(Position.Lfe);
        }

        return Layouts.Speakers(positions);
    }

    public Node Statistics(Func<string, NodeValue?, NodeKind, string, string?, Node> cover, out Node bitrate)
    {
        Node statistics = Figures.Statistics((name, value, kind, description) => cover(name, value, kind, description, null), "Frame", "Frames", out bitrate);
        Node Add(string name, NodeValue value, string description, string? topic = null) =>
            statistics.Add(cover(name, value, NodeKind.Detail, description, topic));
        NodeValue Counted(int count) => NodeValue.Integer(count, "frames", "frame");

        Add("Core Frames", Counted(CoreFrames), Explain.StatCoreFrames, Topics.CoreFrames);
        Add("Extension Substream Frames", Counted(SubstreamFrames), Explain.StatSubstreamFrames, Topics.ExtensionSubstream);
        if (key.HasCore)
        {
            Add("Termination Frames", Counted(TerminationFrames), Explain.StatTermination, Topics.CoreFrames);
        }

        if (key.SampleRate > 0)
        {
            Add("Sampling Frequency", NodeValue.Integer(key.SampleRate, "Hz"), Explain.StatSampleRate, Topics.CoreFrames);
        }

        if (_extensionRate > key.SampleRate)
        {
            Add("Extension Sampling Frequency", NodeValue.Integer(_extensionRate, "Hz"), Explain.StatExtensionRate, Topics.CoreExtensions);
        }

        if (_nominal.Count > 0)
        {
            Add("Nominal Bitrate", string.Join(", ", _nominal.Order().Select(RateText)), Explain.StatNominalBitrate, Topics.BitrateModes);
        }

        if (_revisions.Count > 0)
        {
            Add("Encoder Revision", string.Join(", ", _revisions.Order().Select(r => r.ToString(CultureInfo.InvariantCulture))), Explain.StatRevisions, Topics.CoreFrames);
        }

        if (_dialnorms.Count > 0)
        {
            Add("Dialogue Normalization", string.Join(", ", _dialnorms.Select(d => $"{d} dB")), Explain.StatDialnorm, Topics.DialogueNormalization);
        }

        if (_resolutions.Count > 0)
        {
            Add("Source PCM Resolution", string.Join(", ", _resolutions.Order().Select(r => r == 0 ? "invalid" : $"{r} bits")), Explain.StatResolution, Topics.CoreFrames);
        }

        if (key.HasCore)
        {
            Node extensions = Add("Frames With Extension", Counted(XchFrames + X96Frames + XxchFrames), Explain.StatExtensions, Topics.CoreExtensions);
            extensions.Add(cover("XCh", Counted(XchFrames), NodeKind.Detail, Explain.StatExtensions, Topics.CoreExtensions));
            extensions.Add(cover("X96", Counted(X96Frames), NodeKind.Detail, Explain.StatExtensions, Topics.CoreExtensions));
            extensions.Add(cover("XXCH", Counted(XxchFrames), NodeKind.Detail, Explain.StatExtensions, Topics.CoreExtensions));
            Add("Frames With Dynamic Range Coefficients", Counted(DynamicRangeFrames), Explain.StatFlags, Topics.DynamicRange);
            Add("Frames With Time Stamps", Counted(TimeStampFrames), Explain.StatFlags, Topics.CoreFrames);
            Add("Frames With Auxiliary Data", Counted(AuxiliaryFrames), Explain.StatFlags, Topics.CoreFrames);
            Add("Frames With CRC", Counted(CrcFrames), Explain.StatFlags, Topics.Crc);
        }

        Add("Frames Failing a CRC", Counted(CrcErrors), Explain.StatCrcErrors, Topics.Crc);
        Add("Coding Components", ComponentsText(), Explain.StatComponents, Topics.ExtensionSubstream);
        if (_firstAsset is not null)
        {
            Add("Audio Assets", NodeValue.Integer(_assets, "assets", "asset"), Explain.StatAssets, Topics.ExtensionSubstream);
        }

        Add("HasExtensionSubstream", NodeValue.Boolean(SubstreamFrames > 0), Explain.HasExtensionSubstream, Topics.ExtensionSubstream);
        Add("IsLossless", NodeValue.Boolean(HasLossless), Explain.IsLossless, Topics.ExtensionSubstream);
        Node profile = Add("Profile", Codec, Explain.Profile, Topics.ExtensionSubstream);
        profile.Add(cover("Evidence", ProfileEvidence(), NodeKind.Detail, Explain.Evidence, null));
        return statistics;
    }

    private NodeValue ComponentsText()
    {
        string names = Tables.ComponentNames(_componentUnion);
        int construction = Tables.StreamConstructionOf(_componentUnion);
        return construction > 0 ? $"{names} (StreamConstruction {construction})" : names;
    }

    private string ProfileEvidence()
    {
        if ((_profileUnion & Tables.ExtensionLbr) != 0)
        {
            return "the first asset of the extension substream has the low bit rate component (LBR, mask 0x100)";
        }

        if ((_profileUnion & Tables.ExtensionXll) != 0)
        {
            return "the first asset of the extension substream has the lossless component (XLL, mask 0x200)";
        }

        if ((_profileUnion & (Tables.ExtensionXbr | Tables.ExtensionXxch | Tables.ExtensionX96)) != 0)
        {
            return "the first asset of the extension substream has XBR, XXCH or X96 (mask 0x020, 0x040 or 0x080)";
        }

        if ((_profileUnion & Tables.CoreSubstreamX96) != 0)
        {
            return "the core has the X96 extension, found at the distance from the end of the frame that its size gives";
        }

        if ((_profileUnion & (Tables.CoreSubstreamXch | Tables.CoreSubstreamXxch)) != 0)
        {
            return "the core has the XCh or the XXCH extension, found where its sync word and its size or CRC say";
        }

        return (_profileUnion & Tables.CoreSubstreamCore) != 0 ? "a core with no extension" : "an extension substream without any of those components";
    }

    private static string RateText(int code) => code switch
    {
        22 => "1411.2 kbps",
        <= 24 => $"{Tables.RateTenths(code) / 10} kbps",
        Tables.RateOpen => "open",
        Tables.RateVariable => "variable (30)",
        Tables.RateLossless => "lossless (31)",
        _ => $"{code} (invalid)",
    };

    public int AssetChannels => _assetChannels;

    public int FirstAssetUnits => _firstAssetUnits;
}

// 5.2 and 7: the names a stream's components are usually given.
internal static class Profile
{
    public static string Name(int components)
    {
        if ((components & Tables.ExtensionLbr) != 0)
        {
            return "DTS Express";
        }

        if ((components & Tables.ExtensionXll) != 0)
        {
            return "DTS-HD Master Audio";
        }

        if ((components & (Tables.ExtensionXbr | Tables.ExtensionXxch | Tables.ExtensionX96)) != 0)
        {
            return "DTS-HD High Resolution Audio";
        }

        if ((components & Tables.CoreSubstreamX96) != 0)
        {
            return "DTS 96/24";
        }

        if ((components & (Tables.CoreSubstreamXch | Tables.CoreSubstreamXxch)) != 0)
        {
            return "DTS-ES";
        }

        return "DTS";
    }
}
