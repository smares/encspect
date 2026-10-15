using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Matroska;

// One analysis of one file: the passes over the elements, which hold its state. The parser keeps none (one instance serves every
// analysis); this class does.
internal sealed partial class FileReader(ParseContext context)
{
    // Elements nest at most this deep below the file's node, so the tree stays within Node.MaxDepth with the fields under the deepest
    // element and the codec parsers' nodes beside it. Only elements that contain themselves (ChapterAtom, SimpleTag) or are misplaced
    // go deeper than the schema's own five levels.
    internal const int MaxElementDepth = 40;

    // Nodes of elements other than the blocks of Clusters, which are records. A file with more elements than this is not read further.
    internal const int MaxNodes = 250_000;

    // Block records, Cluster items and BlockGroup children together.
    internal const long MaxRecords = 6_000_000;

    private const int MaxText = 1 << 16;
    private const int MaxShownText = 2048;

    private readonly ParseContext _context = context;
    private readonly ByteReader _reader = context.Reader;
    private readonly FindingCollector _findings = new();
    private readonly List<SegmentState> _segments = [];
    private Node _file = null!;
    private SegmentState? _segment;
    private DocumentInfo _document = new();
    private int _nodes;
    private bool _budgetReported;
    private long _records;
    private bool _recordsReported;

    public void Run()
    {
        // WebM is named by the DocType of the first EBML Header, which the sniff reads.
        string name = MatroskaParser.Detect(_reader, _context.CancellationToken).DocType == "webm" ? "WebM" : "Matroska";
        _file = _context.Root.Add(new Node(name, _reader.Start, _reader.Length) { Description = Explain.File, Topic = Topics.Container });
        long position = _reader.Start;
        bool segmentSeen = false;
        bool first = true;
        while (position < _reader.End)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            HeaderProblem problem = Ebml.TryReadHeader(_reader, position, _reader.End, _document.MaxIdLength, _document.MaxSizeLength, out ElementHeader header);
            if (problem != HeaderProblem.None)
            {
                AddUnparsed(_file, position, _reader.End, problem, first ? "the start of the file" : null);
                position = _reader.End;
                break;
            }

            if (first && header.Id != Schema.Ebml)
            {
                _findings.AddNow(Severity.Warning, "matroska.no-ebml-header",
                    $"The file does not start with an EBML Header, which every EBML document must (RFC 8794, section 8); its first element, {ElementName(header.Id)}, is at 0x{position:X}. A recording or file cut at its start looks like this.", _file);
            }

            first = false;
            if (header.Id == Schema.Ebml)
            {
                _document = new DocumentInfo();
                segmentSeen = false;
                position = ParseTopLevel(header, Schema.Ebml);
            }
            else if (header.Id == Schema.Segment)
            {
                segmentSeen = true;
                position = ParseSegment(header, false);
            }
            else if (!segmentSeen && Schema.Find(header.Id) is { IsTopLevel: true })
            {
                segmentSeen = true;
                position = ParseSegment(header, true);
            }
            else
            {
                position = ParseTopLevel(header, header.Id);
            }
        }

        Finish();
        _findings.Flush(_context);
    }

    // An element at the root of the document that is not the Segment: the EBML Header, a second one's, or something unknown.
    private long ParseTopLevel(ElementHeader header, ulong id)
    {
        Node node = ParseElement(header, null, _reader.End, 1, out long next);
        _file.Add(node);
        if (id == Schema.Ebml)
        {
            _document.Node = node;
            if (node.End <= _reader.End && header.End is { } declaredEnd && declaredEnd <= _reader.End)
            {
                CheckHeader(node);
            }
        }
        else if (Schema.Find(header.Id) is { Id: not Schema.Void } definition && !Schema.IsChildOf(definition, null))
        {
            _findings.Add(Severity.Warning, "matroska.misplaced-element", node, count => MisplacedMessage(count, definition, header.Offset, null));
        }

        return next;
    }

    private static string ElementName(ulong id) => Schema.Find(id)?.Name ?? $"the unknown element 0x{id:X}";

    private static string Bytes(long count) => count == 1 ? "1 byte" : $"{count.ToString(CultureInfo.InvariantCulture)} bytes";

    private Node AddUnparsed(Node parent, long start, long end, HeaderProblem problem, string? where)
    {
        Node rest = parent.Add(new Node("Unparsed Data", start, end - start, NodeValue.Integer(end - start, "bytes", "byte")) { Description = Explain.UnparsedData });
        string why = problem switch
        {
            HeaderProblem.Truncated => "the data ends inside its element header",
            HeaderProblem.NotAnInteger => "it begins with a byte of zero bits, so it is no variable-size integer of eight octets or fewer (RFC 8794, section 4.1)",
            _ => "its Element ID is longer than the document's EBMLMaxIDLength allows or is the reserved value 0xFF (RFC 8794, section 5)",
        };
        _findings.AddNow(problem == HeaderProblem.Truncated ? Severity.Warning : Severity.Error, "matroska.element-header-invalid",
            $"No element can be read at 0x{start:X}{(where is null ? string.Empty : $", {where}")}: {why}. The {Bytes(end - start)} from there are not read.", rest);
        return rest;
    }

    // The EBML Header's checks (RFC 8794 section 11.2, RFC 9559 section 4.3), from what its elements said.
    private void CheckHeader(Node node)
    {
        DocumentInfo document = _document;
        if (document.DocType is null)
        {
            _findings.AddNow(Severity.Warning, "matroska.header-field", "The EBML Header holds no DocType, which RFC 8794 requires (section 11.2.6); the file is read as Matroska.", node);
        }
        else if (document.DocType is not ("matroska" or "webm"))
        {
            _findings.AddNow(Severity.Warning, "matroska.doctype-unknown",
                $"The EBML Header gives the DocType \"{document.DocType}\", where Matroska files have \"matroska\" (RFC 9559, section 4.3) and WebM files \"webm\"; the file is read as Matroska.", node);
        }

        if (document.ReadVersion is > 1)
        {
            _findings.AddNow(Severity.Warning, "matroska.header-version",
                $"The EBML Header gives an EBMLReadVersion of {document.ReadVersion}, newer than the version 1 of RFC 8794 (section 11.2.3), which a reader needs to support to read the file.", node);
        }

        if (document.DocTypeReadVersion is { } read && document.DocTypeVersion is { } version && read > version)
        {
            _findings.AddNow(Severity.Warning, "matroska.header-version",
                $"The EBML Header gives a DocTypeReadVersion of {read}, greater than its DocTypeVersion of {version}, which RFC 8794 forbids (section 11.2.8).", node);
        }

        if (document.DocTypeReadVersion is > 4)
        {
            _findings.AddNow(Severity.Info, "matroska.header-version",
                $"The EBML Header gives a DocTypeReadVersion of {document.DocTypeReadVersion}, newer than the version 4 that RFC 9559 describes (section 7).", node);
        }

        if (document.MaxIdLength != 4 || document.MaxSizeLength is < 1 or > 8)
        {
            _findings.AddNow(Severity.Warning, "matroska.header-limits",
                $"The EBML Header gives an EBMLMaxIDLength of {document.MaxIdLength} and an EBMLMaxSizeLength of {document.MaxSizeLength}; Matroska requires 4 and a value from 1 to 8 (RFC 9559, section 4.3).", node);
        }
    }

    private void Finish()
    {
        foreach (SegmentState segment in _segments)
        {
            CheckSegment(segment);
        }

        foreach (SegmentState segment in _segments)
        {
            RouteTracks(segment);
        }

        Describe();
    }

    private void Describe()
    {
        var kinds = new List<string>();
        foreach (IGrouping<string, TrackInfo> group in _segments.SelectMany(s => s.Tracks).GroupBy(TypeName))
        {
            kinds.Add($"{group.Count()} {group.Key} {(group.Count() == 1 ? "track" : "tracks")}");
        }

        _file.Value = kinds.Count == 0 ? "no tracks" : string.Join(", ", kinds);
        _context.Root.Value = _file.Name;
    }

    private static string TypeName(TrackInfo track) => track.Type switch
    {
        1 => "video",
        2 => "audio",
        3 => "complex",
        16 => "logo",
        17 => "subtitle",
        18 => "buttons",
        32 => "control",
        33 => "metadata",
        _ => "unknown",
    };
}
