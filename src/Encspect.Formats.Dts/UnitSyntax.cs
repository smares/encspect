using Encspect.Core;

namespace Encspect.Formats.Dts;

// Reads one unit, a core frame and the extension substreams after it, field by field as ETSI TS 102 114
// lays them out: the frame header (5.4.2) and the primary audio coding header (5.4.3), the core
// extensions XCh, X96 and XXCH (6.2.4, 6.4, 6.5) where the header says there is one and its sync
// word lies at the distance the extension's own size field gives from the end of the frame, and each
// extension substream (7.5.2) with its audio asset descriptors (7.5.3) and the sync word of each
// component of each asset.
//
// The subframes' side information and audio data (5.5, 5.6) are not read. Nothing in them is
// needed to find the extensions: 6.4.2 and 6.2.4.2 make the sync word's distance from the end of the
// frame the test of a true sync word, so the search starts at the end of the frame, and the
// extension substream starts at the frame's length rounded up to 32 bits (7.5.1). Reading the
// subframes would take the Huffman code books of Annex D.5, to learn only where the optional
// information begins, which the extensions do not depend on.
//
// One syntax reads a unit twice (UnitView): while the file is analyzed, which also checks the fields,
// and when the unit's nodes are built, long after the file is closed, from the record of the bytes
// the analysis read.
internal sealed partial class UnitSyntax
{
    private readonly UnitView _view;
    private readonly UnitLayout _layout;
    private readonly Tree? _tree;
    private readonly IssueSink? _issues;
    private readonly Verdicts _verdicts;
    private readonly ISource? _source;
    private readonly bool _isBuilding;
    private int _pos;

    // The analysis, which also needs the file's bytes where the syntax follows sizes to other parts of a unit.
    public UnitSyntax(UnitView view, UnitLayout layout, ISource source, IssueSink issues)
    {
        _view = view;
        _layout = layout;
        _source = source;
        _issues = issues;
        _verdicts = new Verdicts();
    }

    // The node pass, from the record's bytes and verdicts.
    public UnitSyntax(UnitView view, UnitLayout layout, Verdicts verdicts)
    {
        _view = view;
        _layout = layout;
        _verdicts = verdicts;
        _isBuilding = true;
        _tree = new Tree(new Locator(layout.Form, layout.Offset, layout.Length));
    }

    public UnitFacts Facts { get; } = new();

    public Verdicts Verdicts => _verdicts;

    public List<Node> Nodes => _tree?.Nodes ?? [];

    // Reads the whole unit. Where the data ends inside a field, what was read stays and the rest is left out.
    public void Run()
    {
        Facts.Form = _layout.Form;
        if (_layout.CoreLength > 0)
        {
            ReadCoreSubstream();
        }

        foreach (ExssRef substream in _layout.Substreams)
        {
            if (substream.IsMisaligned)
            {
                SkipMisalignedSubstream(substream);
            }
            else
            {
                ReadSubstream(substream);
            }
        }
    }

    // 7.5.1 puts every sync word on a 32-bit boundary. FFmpeg finds an extension substream only there (dcadec.c), so one that is not
    // is shown as the bytes it is and not read.
    private void SkipMisalignedSubstream(ExssRef substream)
    {
        int startBit = substream.Start * 8;
        int endBit = startBit + (Math.Min(substream.Length, substream.Available) * 8);
        _tree?.Add(Locator.Group("Misaligned Extension Substream", startBit, endBit, NodeValue.Integer(substream.Length, "bytes"), Explain.MisalignedSubstream, Topics.ExtensionSubstream));
        ReportGroup(Issue.SubstreamMisaligned, "Misaligned Extension Substream", startBit, endBit, $"{(4 - (substream.Start & 3)) & 3} bytes before the boundary");
    }

    private Locator Locator => new(_layout.Form, _layout.Offset, _layout.Length);

    private bool IsIncomplete => _layout.IsCut || _layout.IsTruncated;

    private void Report(Issue issue, string field, int startBit, int endBit, string detail) => Invoke(issue, field, startBit, endBit, detail, false);

    // A finding on a group of fields, whose node the tree builds with Locator.Group.
    private void ReportGroup(Issue issue, string field, int startBit, int endBit, string detail) => Invoke(issue, field, startBit, endBit, detail, true);

    private void Invoke(Issue issue, string field, int startBit, int endBit, string detail, bool isGroup)
    {
        if (_issues is null)
        {
            return;
        }

        // Frames Failing a CRC counts a unit with a header CRC that fails, wherever the header lies.
        if (issue == Issue.CrcMismatch)
        {
            Facts.HasCrcMismatch = true;
        }

        _issues(issue, field, startBit, endBit, detail, isGroup);
    }

    // The end of the group being read, in bits: a field never runs past it, so every node lies inside its parent. A read that would is the
    // data ending inside the group.
    private int _limit = int.MaxValue;

    private int Here => Math.Min(_pos, _limit);

    private uint Read(int width)
    {
        if (_pos + width > _limit)
        {
            throw new EndOfStreamException();
        }

        uint value = _view.Read(_pos, width);
        _pos += width;
        return value;
    }

    private void Advance(int bits)
    {
        if (bits < 0 || _pos + bits > _limit)
        {
            throw new EndOfStreamException();
        }

        _pos += bits;
    }

    // A field with a number.
    private int _headerSizeStart;
    private int _headerSizeEnd;

    // A header's size field, in bytes less one: where it lies is kept for the finding that fields run past the size.
    private int HeaderSize(int width, string description, string topic)
    {
        _headerSizeStart = _pos;
        int size = Num("Header Size", width, description, topic, "bytes", 1) + 1;
        _headerSizeEnd = _pos;
        return size;
    }

    private int Num(string name, int width, string description, string? topic = null, string? unit = null, int add = 0)
    {
        int start = _pos;
        int value = (int)Read(width);
        if (_tree is not null)
        {
            NodeValue shown = unit is not null && unit.EndsWith('s') ? NodeValue.Integer(value + add, unit, unit[..^1]) : NodeValue.Integer(value + add, unit);
            _tree.Field(name, start, _pos, shown, description, topic);
        }

        return value;
    }

    // A field whose value reads as the text or typed value value gives.
    private int Num(string name, int width, string description, Func<int, NodeValue> value, string? topic = null)
    {
        int start = _pos;
        int read = (int)Read(width);
        _tree?.Field(name, start, _pos, value(read), description, topic);
        return read;
    }

    private bool Flag(string name, string description, string? topic = null)
    {
        int start = _pos;
        bool value = Read(1) != 0;
        _tree?.Field(name, start, _pos, NodeValue.Boolean(value), description, topic);
        return value;
    }

    // A field of up to 32 bits shown in hexadecimal.
    private uint Hex(string name, int width, string description, string? topic = null)
    {
        int start = _pos;
        uint value = Read(width);
        _tree?.Field(name, start, _pos, NodeValue.Hex(value, (width + 3) / 4), description, topic);
        return value;
    }

    private void Skip(string name, int width, string description, NodeValue? value = null)
    {
        if (width <= 0)
        {
            return;
        }

        int start = _pos;
        Advance(width);
        _tree?.Field(name, start, _pos, value ?? NodeValue.Integer(width, "bits"), description);
    }

    // A check whose result the record keeps: compute gives it while the file is analyzed, and the node pass reads it back.
    private int Verdict(Func<bool?> compute)
    {
        if (_isBuilding)
        {
            return _verdicts.Next();
        }

        bool? result;
        try
        {
            result = compute();
        }
        catch (EndOfStreamException)
        {
            result = null;
        }

        int verdict = result switch { true => 1, false => 0, null => 2 };
        _verdicts.Add(verdict);
        return verdict;
    }

    private static NodeValue VerdictText(int verdict, string notChecked) => verdict switch
    {
        1 => "verified",
        0 => "mismatch",
        _ => notChecked,
    };

    // The CRC16 of Annex B over bytes [start, start + length) of the unit comes to zero.
    private int CrcVerdict(int start, int length) => Verdict(() => Crc16.Compute(_view.Peek(start, length)) == 0);

    // A 16-bit CRC field, with the node of its verdict under it.
    private uint Crc(string name, string description, int verdict, string notChecked)
    {
        int start = _pos;
        uint value = Read(16);
        if (_tree is not null)
        {
            Node crc = _tree.Field(name, start, _pos, NodeValue.Hex(value, 4), description, Topics.Crc);
            crc.Add(new Node("Check", crc.Offset, crc.Length, VerdictText(verdict, notChecked)) { Description = Explain.CrcCheck, Topic = Topics.Crc, Ranges = crc.Ranges });
        }

        return value;
    }

    private int Aligned(int bit) => (bit + 7) & ~7;
}
