using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Matroska;

// Matroska and WebM (RFC 9559, built on EBML, RFC 8794; the WebM Container Guidelines at https://www.webmproject.org/docs/container/
// restrict WebM to a subset). A file is a run of EBML elements, each an Element ID, an Element Data Size and its data: the EBML
// Header, whose DocType says "matroska" or "webm", and the Segment, which holds the SeekHead, Info, Tracks, Cues, Chapters,
// Attachments and Tags and the Clusters that carry the SimpleBlocks and BlockGroups with the tracks' frames. Every element becomes a
// node with its fields at their bytes; the Clusters' blocks are kept as compact records and their nodes built when read. The frames of
// each audio track go to the codec parser of its codec ID (A_AAC, A_AC3, A_EAC3, A_DTS, A_VORBIS, A_OPUS, A_MPEG/L1 to L3) with the
// CodecPrivate as the configuration, after the undoing of the track's content encoding (RFC 9559 section 5.1.4.1.31); video and other
// tracks are read at container level only. The codec reports the stream. WebM is the same parser, named by its DocType.
public sealed class MatroskaParser : IFormatParser
{
    // An EBML Header larger than this is not one a sniff follows; real ones are a few dozen bytes.
    private const int MaxSniffHeader = 4096;

    public string Name => "Matroska";

    // The EBML Header at the start with a DocType of "matroska" or "webm" is certain (class 100, and Segment follows it); a header
    // that is broken in other ways, or one the data cuts short, is 70. A file cut at its start has no header: a Segment at the start
    // followed by a Top-Level Element is 80, and a run of Top-Level Elements (Clusters) from the start, 60. Elements have no checksum
    // the sniff reads.
    public SniffResult Sniff(SniffContext context)
    {
        ByteReader reader = context.Reader;
        return Detect(reader, context.CancellationToken).Result;
    }

    // The end of the last Segment by its own account, which may lie past the end of a truncated file; where a Segment's size is
    // unknown (a live stream) the end of the Top-Level Elements and Cluster children that follow one another from its start. A tag after
    // it is the file's, not an element's.
    public long? DeclaredEnd(SniffContext context)
    {
        ByteReader reader = context.Reader;
        if (!Detect(reader, context.CancellationToken).Result.IsMatch)
        {
            return null;
        }

        long position = reader.Start;
        long? end = null;
        for (int count = 0; count < 64 && position + 2 <= reader.End; count++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (Ebml.TryReadHeader(reader, position, reader.End, 4, 8, out ElementHeader header) != HeaderProblem.None)
            {
                break;
            }

            if (header.Id == Schema.Ebml)
            {
                if (header.IsUnknownSize)
                {
                    break;
                }

                position = header.End!.Value;
            }
            else if (header.Id == Schema.Segment)
            {
                if (header.IsUnknownSize)
                {
                    return ElementsEnd(reader, header.DataOffset, context.CancellationToken);
                }

                end = position = header.End!.Value;
            }
            else if (Schema.Find(header.Id) is { IsTopLevel: true })
            {
                // A file cut at its start: the Top-Level Elements it holds.
                (int elements, _) = Chain(reader, position, reader.End, context.CancellationToken);
                return elements > 0 ? ElementsEnd(reader, position, context.CancellationToken) : end;
            }
            else
            {
                break;
            }
        }

        return end;
    }

    // Where the Top-Level Elements from position end: each is a Top-Level Element the schema lists, and a Cluster of unknown size runs
    // while its children are elements the schema places in a Cluster (an unknown-size Cluster ends at the first element that is not,
    // RFC 8794 section 6.2). An element the data cuts short owns the rest of the data. Bytes after the last whole element, such as a tag
    // appended to a live recording, are not the elements'.
    private static long ElementsEnd(ByteReader reader, long position, CancellationToken cancellationToken)
    {
        ElementDefinition? clusterDefinition = Schema.Find(Schema.Cluster);
        long end = position;
        int steps = 0;
        while (position + 2 <= reader.End)
        {
            if (++steps % 4096 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (Ebml.TryReadHeader(reader, position, reader.End, 4, 8, out ElementHeader header) != HeaderProblem.None)
            {
                break;
            }

            // An element the schema does not list is skipped by its size and the walk goes on, as the parse does (RFC 9559, section 7); one whose
            // size runs past the data ends the walk. It belongs to the elements only if an element the schema lists follows it: what trails the
            // last of those, such as an APEv2 tag whose header reads as an element ID with a size, is not.
            if (Schema.Find(header.Id) is null)
            {
                if (header.IsUnknownSize || header.End!.Value > reader.End)
                {
                    break;
                }

                position = header.End.Value;
                continue;
            }

            if (Schema.Find(header.Id) is not { IsTopLevel: true } && header.Id != Schema.Void && header.Id != Schema.Crc32)
            {
                break;
            }

            if (header.IsUnknownSize)
            {
                if (header.Id != Schema.Cluster)
                {
                    break;
                }

                position = header.DataOffset;
                long childrenEnd = position;
                while (position + 2 <= reader.End)
                {
                    if (++steps % 4096 == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    if (Ebml.TryReadHeader(reader, position, reader.End, 4, 8, out ElementHeader child) != HeaderProblem.None || child.IsUnknownSize)
                    {
                        break;
                    }

                    if (Schema.Find(child.Id) is { } definition)
                    {
                        if (!Schema.IsChildOf(definition, clusterDefinition) || definition.IsTopLevel)
                        {
                            break;
                        }
                    }
                    else if (child.End!.Value > reader.End)
                    {
                        // An element the schema does not list is skipped by its size; one that runs past the data is no element of the Cluster.
                        break;
                    }

                    if (child.End!.Value > reader.End)
                    {
                        return reader.End;
                    }

                    position = child.End.Value;
                    if (Schema.Find(child.Id) is not null)
                    {
                        childrenEnd = position;
                    }
                }

                end = position = childrenEnd;
                continue;
            }

            if (header.End!.Value > reader.End)
            {
                return reader.End;
            }

            position = end = header.End.Value;
        }

        return end;
    }

    public void Parse(ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        new FileReader(context).Run();
    }

    internal readonly record struct Detection(SniffResult Result, string? DocType);

    internal static Detection Detect(ByteReader reader, CancellationToken cancellationToken)
    {
        long start = reader.Start;
        if (reader.Length < 6)
        {
            return new Detection(SniffResult.None, null);
        }

        if (Ebml.TryReadHeader(reader, start, reader.End, 4, 8, out ElementHeader first) != HeaderProblem.None)
        {
            return new Detection(SniffResult.None, null);
        }

        if (first.Id == Schema.Ebml)
        {
            return DetectDocument(reader, first, cancellationToken);
        }

        if (first.Id == Schema.Segment)
        {
            long dataEnd = first.End is { } declared ? Math.Min(declared, reader.End) : reader.End;
            (int children, long end) = Chain(reader, first.DataOffset, dataEnd, cancellationToken);
            return children > 0
                ? new Detection(new SniffResult(80, start, children, Note: "a Segment without the EBML Header before it", End: end), null)
                : new Detection(SniffResult.None, null);
        }

        if (Schema.Find(first.Id) is { IsTopLevel: true })
        {
            (int elements, long end) = Chain(reader, start, reader.End, cancellationToken);
            bool startsLikeCluster = first.Id == Schema.Cluster && ClusterStartsWithTimestamp(reader, first);
            return elements >= 2 || startsLikeCluster
                ? new Detection(new SniffResult(60, start, elements, Note: "Top-Level Elements without the EBML Header and the Segment before them", End: end), null)
                : new Detection(SniffResult.None, null);
        }

        return new Detection(SniffResult.None, null);
    }

    private static Detection DetectDocument(ByteReader reader, ElementHeader header, CancellationToken cancellationToken)
    {
        long start = reader.Start;
        if (header.IsUnknownSize || header.Size > MaxSniffHeader)
        {
            return new Detection(new SniffResult(70, start, 1, Note: "the EBML Header states an unusable size"), null);
        }

        long headerEnd = header.End!.Value;
        if (headerEnd > reader.End)
        {
            return new Detection(new SniffResult(70, start, 1, Note: "the data ends inside the EBML Header"), null);
        }

        string? docType = null;
        bool consistent = true;
        long position = header.DataOffset;
        while (position < headerEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Ebml.TryReadHeader(reader, position, headerEnd, 4, 8, out ElementHeader child) != HeaderProblem.None || child.IsUnknownSize || child.End > headerEnd)
            {
                consistent = false;
                break;
            }

            if (child.Id == 0x4282 && child.Size is > 0 and <= 64)
            {
                reader.Position = child.DataOffset;
                docType = Encoding.ASCII.GetString(reader.ReadBytes((int)child.Size.Value));
                if (docType.IndexOf('\0', StringComparison.Ordinal) is >= 0 and var nul)
                {
                    docType = docType[..nul];
                }
            }

            position = child.End!.Value;
        }

        if (!consistent || docType is null)
        {
            return new Detection(new SniffResult(70, start, 1, Note: consistent ? "the EBML Header holds no DocType" : "the EBML Header's elements do not fit it"), docType);
        }

        if (docType is not ("matroska" or "webm"))
        {
            return new Detection(SniffResult.None, docType);
        }

        if (Ebml.TryReadHeader(reader, headerEnd, reader.End, 4, 8, out ElementHeader next) == HeaderProblem.None && next.Id == Schema.Segment)
        {
            return new Detection(new SniffResult(100, start, 2, Note: $"DocType {docType}", End: Math.Min(next.DataOffset, reader.End)), docType);
        }

        return new Detection(new SniffResult(90, start, 1, Note: $"DocType {docType}, no Segment after the header", End: headerEnd), docType);
    }

    // Counts the Top-Level Elements from position that follow one another and fit in the data (the last may run to its end), at most
    // SniffResult.MaxFrames.
    private static (int Count, long End) Chain(ByteReader reader, long position, long limit, CancellationToken cancellationToken)
    {
        int count = 0;
        while (count < SniffResult.MaxFrames && position + 2 <= limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Ebml.TryReadHeader(reader, position, limit, 4, 8, out ElementHeader header) != HeaderProblem.None
                || (Schema.Find(header.Id) is not { IsTopLevel: true } && header.Id != Schema.Void && header.Id != Schema.Crc32))
            {
                break;
            }

            if (header.IsUnknownSize)
            {
                if (header.Id != Schema.Cluster || !ClusterStartsWithTimestamp(reader, header))
                {
                    break;
                }

                return (count + 1, limit);
            }

            count++;
            position = header.End!.Value;
            if (position >= limit)
            {
                position = limit;
                break;
            }
        }

        return (count, Math.Min(position, limit));
    }

    // A Cluster whose first child is its Timestamp (RFC 9559 section 5.1.3.1: the first child or the second after a CRC-32).
    private static bool ClusterStartsWithTimestamp(ByteReader reader, ElementHeader cluster)
    {
        long position = cluster.DataOffset;
        for (int i = 0; i < 2; i++)
        {
            if (Ebml.TryReadHeader(reader, position, reader.End, 4, 8, out ElementHeader child) != HeaderProblem.None || child.IsUnknownSize)
            {
                return false;
            }

            if (child.Id == Schema.Timestamp)
            {
                return child.Size is >= 1 and <= 8;
            }

            if (child.Id != Schema.Crc32)
            {
                return false;
            }

            position = child.End!.Value;
        }

        return false;
    }
}
