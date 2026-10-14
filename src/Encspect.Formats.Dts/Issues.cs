namespace Encspect.Formats.Dts;

// What the syntax finds wrong or notable in a unit's fields, by kind; the analysis turns each
// into a finding of one code, reported once with a count (Findings).
internal enum Issue
{
    ReservedValue,
    UserDefinedArrangement,
    RateCode,
    RevisionIncompatible,
    CrcPresent,
    FrameTypeInconsistent,
    BlockCount,
    SubframeCount,
    RateExceeded,
    ChannelCount,
    SubbandCount,
    JointSource,
    HeaderOverrun,
    ExtensionMissing,
    ExtensionSizeLegacy,
    CrcMismatch,
    SubstreamOverrun,
    AssetDescriptorOverrun,
    ComponentSync,
    CodingModeAuxiliary,
}

internal delegate void IssueSink(Issue issue, string field, int startBit, int endBit, string detail);

// The verdicts a unit's checks gave (CRCs, sync words at the places the sizes lead to), in the order
// the syntax reached them: 0 failed, 1 held, 2 not checked. The analysis writes them, the node pass reads them back.
internal sealed class Verdicts
{
    private readonly List<byte> _values = [];
    private int _next;

    public Verdicts()
    {
    }

    public Verdicts(ReadOnlySpan<byte> values)
    {
        _values.AddRange(values.ToArray());
    }

    public int Count => _values.Count;

    public ReadOnlySpan<byte> Values => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_values);

    public void Add(int verdict)
    {
        // A unit of many assets could list more than a record keeps; the rest read as not checked.
        if (_values.Count < 255)
        {
            _values.Add((byte)verdict);
        }
    }

    public int Next() => _next < _values.Count ? _values[_next++] : 2;
}
