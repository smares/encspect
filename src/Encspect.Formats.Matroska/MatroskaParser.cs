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

    public bool HasDeclaredEnd => true;

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
    // An element the schema does not list is skipped by its size (RFC 9559, section 7), but it and the Cluster children after it belong to the
    // elements only once a Top-Level Element (a Segment child with a four-octet ID, RFC 9559 section 4.4) follows, or the elements run to
    // the end of the data without a break. A Void, a CRC-32 or a Cluster child after it does not vouch for it. A known element that runs past
    // the end of the data (a recording cut short) owns the rest of the data, whatever comes before it, unless the bytes are a tag's. Evidence
    // that they are, weighed against the elements' (CLAUDE.md: never a signature alone, the strongest evidence wins):
    // - an unknown element that starts with the signature of a tag (an APEv2 header reads as the ID 0x4150 and a size of 1,364 bytes, and its
    //   data may look like any element) can only be vouched for by a Top-Level Element after it, not by the end of the data or by an element
    //   that runs past it;
    // - an APEv2 footer at the end of the data (ApeTagStart: the signature, a size inside the data and, where the flags announce a header, the
    //   header's signature) whose tag starts exactly where the walk is at an element's start ends the recording there, unless the elements vouch
    //   for their bytes (ElementsVouch): a Top-Level Element starts there, or the elements from there, following Clusters of unknown size into
    //   their children, run through known elements and unknown ones to the end of the data, an element running past it counting only after a
    //   whole one. The items are not examined; the APE parser reports a tag's damage.
    // A Top-Level Element that follows an unknown element still vouches for what comes before it.
    // isProbe: the walk that ElementsVouch makes from the claimed start of a tag; it starts in a Cluster if the first element is a child of one,
    // and looks for no tag.
    private static long ElementsEnd(ByteReader reader, long position, CancellationToken cancellationToken, bool isProbe = false)
    {
        ElementDefinition? clusterDefinition = Schema.Find(Schema.Cluster);
        long end = position;
        long apeStart = isProbe ? -1 : ApeTagStart(reader);
        bool isPending = false;
        bool isTagLike = false;
        bool isInCluster = isProbe && StartsInCluster(reader, position, clusterDefinition);
        int wholeElements = 0;
        int steps = 0;
        while (position + 2 <= reader.End)
        {
            if (++steps % 4096 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // The elements run up to where an APEv2 footer says its tag starts: the recording ends there.
            if (position == apeStart)
            {
                if (!ElementsVouch(reader, position, cancellationToken))
                {
                    end = isPending && !isTagLike ? position : end;
                    break;
                }

                apeStart = -1;
            }

            if (Ebml.TryReadHeader(reader, position, reader.End, 4, 8, out ElementHeader header) != HeaderProblem.None)
            {
                break;
            }

            ElementDefinition? definition = Schema.Find(header.Id);
            if (isInCluster)
            {
                // An element of unknown size is no child of the Cluster; a Cluster of unknown size starts the next one.
                if (!header.IsUnknownSize && (definition is null || (Schema.IsChildOf(definition, clusterDefinition) && !definition.IsTopLevel)))
                {
                    if (header.End!.Value > reader.End)
                    {
                        // An unknown element that runs past the data is no element of the Cluster; a child that does ends the recording.
                        return definition is null || isTagLike || (isProbe && wholeElements == 0) ? end : reader.End;
                    }

                    if (definition is null)
                    {
                        isPending = true;
                        isTagLike |= StartsLikeTag(reader, position);
                    }
                    else if (!isPending)
                    {
                        end = header.End.Value;
                    }

                    position = header.End.Value;
                    wholeElements++;
                    continue;
                }

                isInCluster = false;
            }

            if (definition is null)
            {
                if (header.IsUnknownSize || header.End!.Value > reader.End)
                {
                    break;
                }

                isTagLike |= StartsLikeTag(reader, position);
                position = header.End.Value;
                isPending = true;
                wholeElements++;
                continue;
            }

            if (header.Id is Schema.Ebml or Schema.Segment)
            {
                if (isPending)
                {
                    end = position;
                }

                break;
            }

            if (!definition.IsTopLevel && header.Id != Schema.Void && header.Id != Schema.Crc32)
            {
                break;
            }

            if (isPending)
            {
                if (!definition.IsTopLevel)
                {
                    break;
                }

                end = position;
                isPending = false;
                isTagLike = false;
            }

            if (header.IsUnknownSize)
            {
                if (header.Id != Schema.Cluster)
                {
                    break;
                }

                position = end = header.DataOffset;
                isInCluster = true;
                continue;
            }

            if (header.End!.Value > reader.End)
            {
                return reader.End;
            }

            position = end = header.End.Value;
            wholeElements++;
        }

        // Unknown elements and Cluster children that run to the very end of the data, with nothing between, are the recording's, unless a tag's
        // signature is among them.
        return isPending && !isTagLike && position == reader.End ? position : end;
    }

    // Whether the elements vouch for their bytes from position, where an APEv2 footer says a tag starts: a Top-Level Element starts there, or the
    // elements from there run to the end of the data (ElementsEnd, as a probe). The items of a tag, which are sizes, flags and keys, rarely do.
    private static bool ElementsVouch(ByteReader reader, long position, CancellationToken cancellationToken)
    {
        if (Ebml.TryReadHeader(reader, position, reader.End, 4, 8, out ElementHeader header) == HeaderProblem.None
            && header.IdLength == 4 && Schema.Find(header.Id) is { IsTopLevel: true })
        {
            return true;
        }

        return ElementsEnd(reader, position, cancellationToken, isProbe: true) == reader.End;
    }

    // Whether the element at position is a child of a Cluster, so that a walk from there starts in one.
    private static bool StartsInCluster(ByteReader reader, long position, ElementDefinition? clusterDefinition) =>
        Ebml.TryReadHeader(reader, position, reader.End, 4, 8, out ElementHeader header) == HeaderProblem.None
        && Schema.Find(header.Id) is { IsTopLevel: false } definition && Schema.IsChildOf(definition, clusterDefinition);

    // Where an APEv2 tag that ends the data (or ends 128 bytes before it, ahead of an ID3v1 tag) says it starts, by its footer, or -1: the footer's
    // tag size counts the items and the footer, and a header, which the footer's flags (bit 31) announce, is 32 bytes more, as the APE parser has
    // it (a header that is not there is that parser's finding, and the tag starts at the items). The items are not examined.
    private static long ApeTagStart(ByteReader reader)
    {
        Span<byte> footer = stackalloc byte[32];
        Span<byte> id3 = stackalloc byte[3];
        Span<byte> header = stackalloc byte[8];
        foreach (long back in new long[] { 32, 160 })
        {
            long at = reader.End - back;
            if (at < reader.Start)
            {
                continue;
            }

            if (back == 160)
            {
                reader.Position = reader.End - 128;
                reader.Read(id3);
                if (!id3.SequenceEqual("TAG"u8))
                {
                    continue;
                }
            }

            reader.Position = at;
            reader.Read(footer);
            if (!footer[..8].SequenceEqual("APETAGEX"u8))
            {
                continue;
            }

            uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(footer[12..]);
            uint flags = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(footer[20..]);
            long items = at + 32 - size;
            if (size < 32 || items < reader.Start)
            {
                continue;
            }

            if ((flags & 0x80000000) != 0 && items - 32 >= reader.Start)
            {
                reader.Position = items - 32;
                reader.Read(header);
                if (header.SequenceEqual("APETAGEX"u8))
                {
                    return items - 32;
                }
            }

            return items;
        }

        return -1;
    }

    // Whether the bytes at position begin with the signature of an ID3v2 tag (or its footer), an ID3v1 tag, an APEv2 tag or Lyrics3.
    private static bool StartsLikeTag(ByteReader reader, long position)
    {
        Span<byte> bytes = stackalloc byte[11];
        int count = (int)Math.Min(bytes.Length, reader.End - position);
        reader.Position = position;
        reader.Read(bytes[..count]);
        ReadOnlySpan<byte> head = bytes[..count];
        return head.StartsWith("APETAGEX"u8) || head.StartsWith("LYRICSBEGIN"u8) || head.StartsWith("ID3"u8) || head.StartsWith("3DI"u8) || head.StartsWith("TAG"u8);
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
