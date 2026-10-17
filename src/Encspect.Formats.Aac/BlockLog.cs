using Encspect.Core;

namespace Encspect.Formats.Aac;

// Reads a stream's raw data blocks in order, for MP4 access units and ADTS frames alike: counts
// their coding, keeps what their nodes are rebuilt from (UnitStore), and reports what they break,
// once per kind with a count. A block whose reading stops is read again with nodes, which places
// the finding at the node it stopped in.
internal sealed class BlockLog
{
    // Longer access units cannot be valid: the 6144 bits per channel of the decoder's input buffer
    // that ISO/IEC 14496-3 allows, for the 48 channels a program can have, stay below it. Only this
    // much of a longer one is read.
    public const int MaxBlockBytes = 1 << 16;

    private readonly BlockReader _reader;
    private readonly FindingCollector findings;
    private readonly StreamSetup setup;

    public BlockLog(StreamSetup setup, FindingCollector findings)
    {
        this.setup = setup;
        this.findings = findings;
        _reader = new BlockReader(setup, Tally);
    }

    public StreamSetup Setup => setup;

    public CodingTally Tally { get; } = new();

    public UnitStore Store { get; } = new();

    public int Count { get; private set; }

    // The elements of the block read last, for its CRC.
    public IReadOnlyList<ElementSpan> LastElements => _reader.Elements;

    // Reads the block at bits [start, limit) of data, which where places; unitNode makes the node of
    // its access unit or frame for findings that have no node of their own. Returns where the store
    // keeps it and what reading it came to.
    public (long Position, BlockResult Result) Read(ReadOnlySpan<byte> data, int start, int limit, UnitBytes where, Func<Node> unitNode, bool report = true)
    {
        limit = Math.Min(limit, start + (MaxBlockBytes * 8));
        BlockReader reader = _reader;
        BlockResult result = reader.Read(data, start, limit, Count, where);
        long position;
        if (result.Ok)
        {
            position = Store.AddSkeleton(data, start, result.End, reader.Cuts);
        }
        else if (!report)
        {
            position = Store.AddWhole(data, start, limit);
        }
        else
        {
            position = Store.AddWhole(data, start, limit);
            new BlockReader(setup, null).Nodes(data, start, limit, where, out BlockError? located);
            BlockError error = located ?? result.Error!;
            Node node = error.Node ?? unitNode();
            string element = error.Element ?? "access unit";
            long offset = node.Offset;
            string message = error.Message;
            string rest = element == "raw data block" ? "" : "; the rest of its raw data block is not read";
            findings.Add(Severity.Warning, error.Code, node, count => count == 1
                ? $"The {element} at 0x{offset:X} {message}{rest}."
                : $"{count} raw data blocks are not read to their end; in the first, the {element} at 0x{offset:X} {message}.");
        }

        foreach (SoftFinding soft in report ? reader.SoftFindings : [])
        {
            Node node = soft.Node ?? unitNode();
            long offset = node.Offset;
            string what = soft.Message;
            findings.Add(SeverityOf(soft.Code), soft.Code, node, count => count == 1
                ? $"The raw data block at 0x{offset:X} holds {what}."
                : $"{count} raw data blocks hold {what}; the first at 0x{offset:X}.");
        }

        Count++;
        return (position, result);
    }

    // Counts a block as evidence for implicit SBR and PS (CodingTally.IsSbrEvident): one read to its end
    // whose frame is intact; its SBR payload counts where it follows a channel element, readable or not
    // (BlockReader.TallySbr). index is the block's number in the log. The callers leave out the blocks of
    // frames they flag as damaged.
    public void AddEvidence(BlockResult result, int index)
    {
        if (!result.Ok)
        {
            return;
        }

        Tally.EvidenceBlocks++;
        if (result.HasSbrEvidence)
        {
            Tally.SbrEvidenceBlocks++;
            if (Tally.FirstSbrEvidenceBlock < 0)
            {
                Tally.FirstSbrEvidenceBlock = index;
            }
        }

        if (result.HasPs)
        {
            Tally.PsEvidenceBlocks++;
        }
    }

    private static Severity SeverityOf(string code) => code switch
    {
        "aac.sbr-before-header" => Severity.Info,
        _ => Severity.Warning,
    };

    // The nodes of the block the store keeps at position, read again; where places them.
    public static List<Node> Nodes(UnitStore store, StreamSetup setup, long position, UnitBytes where) =>
        new BlockReader(setup, null).Nodes(store.Read(position), where);

    // The access unit's or frame's figures for its node's summary: its elements, by the packed IDs
    // of BlockResult.Elements.
    public static string ElementList(int packed)
    {
        int count = (packed >> 24) & 0xF;
        if (count == 0)
        {
            return "END";
        }

        var names = new List<string>(Math.Min(count, 8));
        for (int i = 0; i < Math.Min(count, 8); i++)
        {
            names.Add(AacTables.ElementName((packed >> (3 * i)) & 7));
        }

        return string.Join(", ", names) + (count > 8 ? ", ..." : string.Empty);
    }

    // The ID of the block's first element, or -1 where it holds none but END.
    public static int FirstElement(int packed) => ((packed >> 24) & 0xF) == 0 ? 7 : packed & 7;

    // The findings of what the stream's blocks hold as a whole: SBR and PS that the configuration
    // does not signal, or signals absent. node is the configuration's node, or the stream's; where
    // names the configuration ("The AudioSpecificConfig", "ADTS"). Implicit SBR and PS rest on the
    // evidence of AddEvidence, the signaled-absent findings on every block that holds a payload.
    public void ReportSignaling(AacConfig config, Node node, string where)
    {
        CodingTally tally = Tally;
        if (config.Ps != false && config.Channels is > 1 && (config.Ps == true || tally.PsEvidenceBlocks > 0))
        {
            string signaled = config.Ps == true ? $"{where} signals PS" : string.Empty;
            string found = tally.PsEvidenceBlocks > 0
                ? $"{(signaled.Length > 0 ? ", and " : string.Empty)}{tally.PsEvidenceBlocks} of {tally.EvidenceBlocks} raw data blocks carry the parametric stereo extension's ID"
                : string.Empty;
            findings.AddNow(Severity.Warning, "aac.ps-without-mono-core",
                $"{signaled}{found}, but the core has {config.Channels} channels; decoders apply parametric stereo only to a core of one channel (fdk-aac, ffmpeg), so the report shows the stream without it.", node);
        }

        if (tally.SbrBlocks == 0)
        {
            return;
        }

        if (config.Sbr == false)
        {
            findings.AddNow(Severity.Warning, "aac.sbr-signaled-absent",
                $"{where} signals SBR absent, but {tally.SbrBlocks} of {tally.Blocks} raw data blocks hold SBR data; ffmpeg's decoder ignores it, other decoders may apply it.", node);
            return;
        }

        if (config.Ps == false && config.Sbr == true && tally.PsBlocks > 0)
        {
            findings.AddNow(Severity.Warning, "aac.ps-signaled-absent",
                $"{where} signals PS absent, but {tally.PsBlocks} of {tally.Blocks} raw data blocks carry the parametric stereo extension; ffmpeg's decoder warns and ignores it, as the report does.", node);
        }

        string units = $"{tally.SbrEvidenceBlocks} of {tally.EvidenceBlocks} raw data blocks";
        if (config.IsSbrImplicit)
        {
            string first = tally.FirstSbrEvidenceBlock > 0
                ? $" The first is in raw data block {tally.FirstSbrEvidenceBlock + 1}, not the first, so ffmpeg's decoder, which takes implicit SBR only where it starts with the stream, plays the core alone."
                : string.Empty;
            string codec = config.HasPs ? "HE-AAC v2" : "HE-AAC";
            string ps = config.HasPs ? $", and {tally.PsEvidenceBlocks} carry the parametric stereo extension" : string.Empty;
            findings.AddNow(Severity.Info, "aac.implicit-sbr",
                $"{(where == "ADTS" ? "ADTS cannot signal SBR or PS explicitly" : $"{where} signals no SBR")}, but {units} hold SBR data{ps}: the stream is {codec}, signaled only implicitly, with an output rate of {config.OutputSampleRate} Hz. "
                + (config.CoreSampleRate > 24000 ? $"faad2 doubles only a core rate of up to 24 kHz, so it plays this one at {config.CoreSampleRate} Hz. " : string.Empty)
                + $"Decoders without SBR play the {AacTables.ObjectTypeName(config.ObjectType)} core at {config.CoreSampleRate} Hz.{first}", node);
        }
        else if (config.IsPsImplicit)
        {
            findings.AddNow(Severity.Info, "aac.implicit-ps",
                $"{where} signals SBR but nothing about PS, and {tally.PsEvidenceBlocks} of {tally.EvidenceBlocks} raw data blocks carry the parametric stereo extension: the stream is HE-AAC v2, "
                + "with PS signaled only implicitly. Decoders without PS play the mono downmix.", node);
        }
        else if (config.Sbr is null && tally.SbrEvidenceBlocks > 0)
        {
            findings.AddNow(Severity.Info, "aac.sbr-data-sparse",
                $"{where} signals no SBR, and only {units} hold SBR data after a channel element: too few for the stream to count as HE-AAC, which takes SBR data in at least two blocks and in half of the blocks that are read to their end and not in a damaged frame. The report shows the {AacTables.ObjectTypeName(config.ObjectType)} core alone.", node);
        }
    }
}
