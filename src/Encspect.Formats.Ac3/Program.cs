using Encspect.Core;

namespace Encspect.Formats.Ac3;

// One program: the frames of an independent substream (or of AC-3) with one configuration, and
// the dependent substreams that follow each of its frames (Annex E 2.3.1.2). A program frame is
// an independent frame with its dependents; the figures count program frames.
internal sealed class Program(Ac3Analysis.ProgramKey key, int firstFrame)
{
    private readonly List<int> _dialnorms = [];
    private readonly List<int> _bsmods = [];
    private readonly HashSet<int> _nominal = [];
    private readonly HashSet<int> _sizes = [];
    private readonly List<Token> _pendingDependents = [];
    private long _pendingOffset = -1;
    private int _pendingBytes;
    private int _pendingSamples;
    private bool _firstProgramFrame = true;
    private List<Token>? _dependentTokens;

    public Ac3Analysis.ProgramKey Key => key;

    public int FirstFrame => firstFrame;

    public HashSet<Ac3Analysis.Region> Regions { get; } = [];

    public StreamFigures Figures { get; } = new(key.SampleRate);

    public int LayoutChanges { get; private set; }

    public bool HasEac3 { get; private set; } = key.IsEac3;

    public string Codec => HasEac3 ? "E-AC-3" : "AC-3";

    public string Name => key.IsEac3 ? $"independent substream {key.Id}" : "the AC-3 stream";

    public IReadOnlyList<Speaker> Layout { get; private set; } = [];

    public bool IsVbr => key.IsEac3 || HasEac3 ? _sizes.Count > 1 : _nominal.Count > 1;

    private int DependentFrames { get; set; }

    private int CrcErrors { get; set; }

    private int CompressionFrames { get; set; }

    private int Blocks { get; set; }

    private int BlocksRead { get; set; }

    private int BlockSwitching { get; set; }

    private int DynamicRange { get; set; }

    private int Coupling { get; set; }

    private int Rematrixing { get; set; }

    private int RematrixedBands { get; set; }

    private int RematrixingBands { get; set; }

    private int SpectralExtension { get; set; }

    private int DeltaBitAllocation { get; set; }

    private long SkipBytes { get; set; }

    private long AuxiliaryBits { get; set; }

    private int EmdfContainers { get; set; }

    private int JocFrames { get; set; }

    private int ExtensionFrames { get; set; }

    private int MinObjects { get; set; } = int.MaxValue;

    private int MaxObjects { get; set; }

    private int MinComplexity { get; set; } = int.MaxValue;

    private int MaxComplexity { get; set; } = -1;

    // Whether the frames carry joint object coding, and the objects it reconstructs (the most any
    // frame gives), or null where no frame says.
    public bool HasObjectAudio => JocFrames > 0;

    public int? ObjectCount => MaxObjects > 0 ? MaxObjects : null;

    private readonly long[] _strategies = new long[4];

    public void Add(int index, long offset, int length, SyncFrame header, FrameResult result, bool crcError)
    {
        if (header.IsDependent)
        {
            DependentFrames++;
            HasEac3 = true;
            _pendingBytes += length;
            _pendingDependents.AddRange(DependentTokens(header, result));
        }
        else
        {
            Complete();
            _pendingOffset = offset;
            _pendingBytes = length;
            _pendingSamples = header.Samples;
            _nominal.Add(header.NominalBitrate);
            if (result.Dialnorm >= 0 && !_dialnorms.Contains(result.Dialnorm))
            {
                _dialnorms.Add(result.Dialnorm);
            }

            if (result.Bsmod >= 0 && !_bsmods.Contains(result.Bsmod))
            {
                _bsmods.Add(result.Bsmod);
            }

            CompressionFrames += result.HasCompression ? 1 : 0;
        }

        EmdfContainers += result.EmdfContainers;
        if (result.HasJoc)
        {
            JocFrames++;
            if (result.JocObjects > 0)
            {
                MinObjects = Math.Min(MinObjects, result.JocObjects);
                MaxObjects = Math.Max(MaxObjects, result.JocObjects);
            }
        }

        if (result.ExtensionFlag == 1)
        {
            ExtensionFrames++;
            MinComplexity = Math.Min(MinComplexity, result.ComplexityIndex);
            MaxComplexity = Math.Max(MaxComplexity, result.ComplexityIndex);
        }

        CrcErrors += crcError ? 1 : 0;
        Blocks += result.Blocks;
        BlocksRead += result.BlocksRead;
        BlockSwitching += result.BlockSwitchBlocks;
        DynamicRange += result.DynamicRangeBlocks;
        Coupling += result.CouplingBlocks;
        Rematrixing += result.RematrixingBlocks;
        RematrixedBands += result.RematrixedBands;
        RematrixingBands += result.RematrixingBands;
        SpectralExtension += result.SpectralExtensionBlocks;
        DeltaBitAllocation += result.DeltaBitAllocationBlocks;
        SkipBytes += result.SkipBytes;
        AuxiliaryBits += Math.Max(0, result.AuxiliaryBits);
        for (int i = 0; i < 4; i++)
        {
            _strategies[i] += result.ExponentStrategies[i];
        }
    }

    public void Close()
    {
        Complete();
        if (_dependentTokens is null)
        {
            Layout = Speakers(IndependentTokens(), []);
        }
    }

    private void Complete()
    {
        if (_pendingOffset < 0)
        {
            return;
        }

        Figures.Add(_pendingOffset, _pendingBytes, _pendingSamples);
        _sizes.Add(_pendingBytes);
        if (_firstProgramFrame)
        {
            _dependentTokens = [.. _pendingDependents];
            Layout = Speakers(IndependentTokens(), _dependentTokens);
            _firstProgramFrame = false;
        }
        else if (!_pendingDependents.SequenceEqual(_dependentTokens!))
        {
            LayoutChanges++;
        }

        _pendingDependents.Clear();
        _pendingOffset = -1;
    }

    // The channel locations a coded configuration holds (Table 5.8), and those of a dependent
    // substream: its custom channel map where it has one, else its acmod and lfeon (Annex E
    // 2.3.1.7 and 2.3.1.8).
    private List<Token> IndependentTokens() => AcmodTokens(key.Acmod, key.Lfeon);

    private static List<Token> AcmodTokens(int acmod, bool lfeon)
    {
        List<Token> tokens = acmod switch
        {
            0 => [Token.Channel1, Token.Channel2],
            1 => [Token.C],
            2 => [Token.L, Token.R],
            3 => [Token.L, Token.C, Token.R],
            4 => [Token.L, Token.R, Token.S],
            5 => [Token.L, Token.C, Token.R, Token.S],
            6 => [Token.L, Token.R, Token.Ls, Token.Rs],
            _ => [Token.L, Token.C, Token.R, Token.Ls, Token.Rs],
        };
        if (lfeon)
        {
            tokens.Add(Token.Lfe);
        }

        return tokens;
    }

    private static List<Token> DependentTokens(SyncFrame header, FrameResult result)
    {
        if (!result.HasChannelMap)
        {
            return AcmodTokens(header.Acmod, header.Lfeon);
        }

        var tokens = new List<Token>();
        for (int bit = 0; bit < 16; bit++)
        {
            if ((result.ChannelMap & (0x8000 >> bit)) == 0)
            {
                continue;
            }

            switch (bit)
            {
                case 0: tokens.Add(Token.L); break;
                case 1: tokens.Add(Token.C); break;
                case 2: tokens.Add(Token.R); break;
                case 3: tokens.Add(Token.Ls); break;
                case 4: tokens.Add(Token.Rs); break;
                case 5: tokens.AddRange([Token.Lc, Token.Rc]); break;
                case 6: tokens.AddRange([Token.Lrs, Token.Rrs]); break;
                case 7: tokens.Add(Token.Cs); break;
                case 8: tokens.Add(Token.Ts); break;
                case 9: tokens.AddRange([Token.Lsd, Token.Rsd]); break;
                case 10: tokens.AddRange([Token.Lw, Token.Rw]); break;
                case 11: tokens.AddRange([Token.Vhl, Token.Vhr]); break;
                case 12: tokens.Add(Token.Vhc); break;
                case 13: tokens.AddRange([Token.Lts, Token.Rts]); break;
                case 14: tokens.Add(Token.Lfe2); break;
                default: tokens.Add(Token.Lfe); break;
            }
        }

        return tokens;
    }

    // The program's speakers: the independent substream's channels in coded order, then each
    // location a dependent substream adds; a location the independent substream has is a
    // replacement and adds nothing. With a rear pair, the surround pair is the side pair (plan,
    // "Streams and series"). Lts/Rts are the top back pair and the surround direct pair the side
    // pair, as ffmpeg maps them (libavcodec/ac3tab.c, ff_eac3_custom_channel_map_locations).
    private static IReadOnlyList<Speaker> Speakers(List<Token> independent, List<Token> dependents)
    {
        var tokens = new List<Token>(independent);
        foreach (Token token in dependents)
        {
            if (!tokens.Contains(token))
            {
                tokens.Add(token);
            }
        }

        bool rear = tokens.Contains(Token.Lrs);
        var speakers = new List<Speaker>();
        foreach (Token token in tokens)
        {
            Speaker speaker = token switch
            {
                Token.L => Speaker.FrontLeft,
                Token.R => Speaker.FrontRight,
                Token.C => Speaker.FrontCenter,
                Token.Ls => rear ? Speaker.SideLeft : Speaker.BackLeft,
                Token.Rs => rear ? Speaker.SideRight : Speaker.BackRight,
                Token.S or Token.Cs => Speaker.BackCenter,
                Token.Lc => Speaker.FrontLeftOfCenter,
                Token.Rc => Speaker.FrontRightOfCenter,
                Token.Lrs => Speaker.BackLeft,
                Token.Rrs => Speaker.BackRight,
                Token.Ts => Speaker.TopCenter,
                Token.Lsd => Speaker.SideLeft,
                Token.Rsd => Speaker.SideRight,
                Token.Lw => Speaker.FrontLeftWide,
                Token.Rw => Speaker.FrontRightWide,
                Token.Vhl => Speaker.TopFrontLeft,
                Token.Vhr => Speaker.TopFrontRight,
                Token.Vhc => Speaker.TopFrontCenter,
                Token.Lts => Speaker.TopBackLeft,
                Token.Rts => Speaker.TopBackRight,
                Token.Lfe => Speaker.LowFrequency,
                Token.Lfe2 => Speaker.LowFrequency2,
                _ => Speaker.Discrete,
            };

            // A position taken twice (the surround direct pair beside a side pair) has no speaker
            // of its own.
            speakers.Add(speaker != Speaker.Discrete && speakers.Contains(speaker) ? Speaker.Discrete : speaker);
        }

        return speakers;
    }

    public Node Statistics(Func<string, NodeValue?, NodeKind, string, string?, Node> cover, out Node bitrate)
    {
        Node statistics = Figures.Statistics((name, value, kind, description) => cover(name, value, kind, description, null), "Frame", "Frames", out bitrate);
        Node Add(string name, NodeValue value, string description, string? topic = null) =>
            statistics.Add(cover(name, value, NodeKind.Detail, description, topic));
        NodeValue Blocked(int count) => NodeValue.Integer(count, "blocks", "block");

        if (DependentFrames > 0)
        {
            Add("Dependent Frames", NodeValue.Integer(DependentFrames, "frames", "frame"), Explain.StatDependentFrames, Topics.Eac3Substreams);
        }

        if (!key.IsEac3 && _nominal.Count > 0)
        {
            Add("Nominal Bitrate", _nominal.Count == 1 ? NodeValue.Integer(_nominal.First(), "kbps") : string.Join(", ", _nominal.Order().Select(n => $"{n} kbps")),
                Explain.StatNominalBitrate, Topics.BitrateModes);
        }

        if (_dialnorms.Count > 0)
        {
            Add("Dialogue Normalization", _dialnorms.Count == 1 ? Ac3Names.DialogueNormalization(_dialnorms[0])
                : string.Join(", ", _dialnorms.Select(d => d == 0 ? "0 (reserved)" : $"-{d} dB")), Explain.StatDialnorm, Topics.DialogueNormalization);
        }

        if (_bsmods.Count > 0)
        {
            Add("Bit Stream Mode", string.Join("; ", _bsmods.Select(b => $"{b} ({Ac3Names.ServiceType(b, key.Acmod)})")), Explain.StatBsmod);
        }

        Add("Frames With Compression Gain", NodeValue.Integer(CompressionFrames, "frames", "frame"), Explain.StatCompression, Topics.DialogueNormalization);
        Add("Frames Failing a CRC", NodeValue.Integer(CrcErrors, "frames", "frame"), Explain.StatCrcErrors, Topics.Crc);
        Add("Audio Blocks", Blocked(Blocks), Explain.StatBlocks, Topics.AudioBlocks);
        Add("Audio Blocks Read", Blocked(BlocksRead), Explain.StatBlocksRead, Topics.AudioBlocks);
        if (BlocksRead > 0)
        {
            Add("Blocks With Block Switching", Blocked(BlockSwitching), Explain.StatBlockSwitching, Topics.AudioBlocks);
            Add("Blocks With Dynamic Range Gain", Blocked(DynamicRange), Explain.StatDynamicRange, Topics.DialogueNormalization);
            if (key.Acmod >= 2)
            {
                Add("Blocks With Coupling", Blocked(Coupling), Explain.StatCoupling, Topics.Coupling);
            }

            if (key.Acmod == 2)
            {
                Add("Blocks With Rematrixing", Blocked(Rematrixing), Explain.StatRematrixing, Topics.Rematrixing);
                Add("Bands Coded as Sum/Difference", $"{RematrixedBands} of {RematrixingBands}", Explain.StatRematrixedBands, Topics.Rematrixing);
            }

            if (key.IsEac3)
            {
                Add("Blocks With Spectral Extension", Blocked(SpectralExtension), Explain.StatSpectralExtension, Topics.SpectralExtension);
            }

            Add("Blocks With Delta Bit Allocation", Blocked(DeltaBitAllocation), Explain.StatDeltaBitAllocation, Topics.AudioBlocks);
            Node strategies = Add("Exponent Strategies", NodeValue.Integer(_strategies.Sum(), "exponent sets", "exponent set"), Explain.StatExponentStrategies, Topics.AudioBlocks);
            string[] names = ["D15", "D25", "D45", "Reuse"];
            for (int i = 0; i < 4; i++)
            {
                strategies.Add(cover(names[i], NodeValue.Integer(_strategies[i], "exponent sets", "exponent set"), NodeKind.Detail, Explain.StatExponentStrategy, null));
            }

            Add("Skip Field Bytes", NodeValue.Integer(SkipBytes, "bytes"), Explain.StatSkipBytes);
            Add("Auxiliary Data", NodeValue.Integer(AuxiliaryBits, "bits"), Explain.StatAuxiliary);
        }

        if (EmdfContainers > 0 || ExtensionFrames > 0)
        {
            Add("EMDF Containers", NodeValue.Integer(EmdfContainers, "containers", "container"), Explain.StatEmdfContainers, Topics.ObjectAudio);
            Add("Frames With Object Audio", NodeValue.Integer(JocFrames, "frames", "frame"), Explain.StatObjectAudioFrames, Topics.ObjectAudio);
            Add("Frames With Extension Type A", NodeValue.Integer(ExtensionFrames, "frames", "frame"), Explain.StatExtensionFrames, Topics.ObjectAudio);
            if (MaxObjects > 0)
            {
                Add("JOC Object Count", Range(MinObjects, MaxObjects, "objects", "object"), Explain.StatObjectCount, Topics.ObjectAudio);
            }

            if (MaxComplexity >= 0)
            {
                Add("Complexity Index Type A", Range(MinComplexity, MaxComplexity, null, null), Explain.StatComplexityIndex, Topics.ObjectAudio);
            }
        }

        return statistics;
    }

    private static NodeValue Range(int low, int high, string? unit, string? singular) =>
        low == high ? (unit is null ? NodeValue.Integer(low) : NodeValue.Integer(low, unit, singular!)) : $"{low} to {high}{(unit is null ? string.Empty : " " + unit)}";

    private enum Token
    {
        Channel1,
        Channel2,
        L,
        C,
        R,
        Ls,
        Rs,
        S,
        Lc,
        Rc,
        Lrs,
        Rrs,
        Cs,
        Ts,
        Lsd,
        Rsd,
        Lw,
        Rw,
        Vhl,
        Vhr,
        Vhc,
        Lts,
        Rts,
        Lfe2,
        Lfe,
    }
}
