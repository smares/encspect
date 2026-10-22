using Encspect.Core;

namespace Encspect.Formats.Flac;

// Reads a native FLAC stream from the file: the marker, the metadata blocks, then the frames, which it finds one after another and, where
// the data is damaged, by searching (see FrameScanner). The bytes of a frame are read once into the window that serves every frame.
internal static class RawReader
{
    private static readonly byte[] Marker = "fLaC"u8.ToArray();

    public static void Run(State s, Source source, long probeAllowance, bool lends = true)
    {
        long start = source.Start;
        var scanner = new FrameScanner(source, null, probeAllowance + (4 * (source.End - source.Start)));
        long position = Metadata(s, scanner, source, start);
        scanner.Info = s.Info;
        s.StartSession();
        Frames(s, scanner, source, position, lends);
        if (scanner.IsProbeLimitReached)
        {
            long probed = scanner.ProbedBytes;
            s.Findings.Add(Severity.Warning, "flac.resync-limit", s.Stream, _ =>
                $"The search for frames in damaged data was given up after reading {Counts.Of(probed, "byte", "bytes")} of candidates; the data it did not reach is skipped.");
        }
    }

    // Reads the marker and the metadata blocks; returns where the frames should start.
    private static long Metadata(State s, FrameScanner scanner, Source source, long start)
    {
        long end = source.End;
        long position = start;
        if (!source.Window(position, 4).StartsWith(Marker))
        {
            s.Findings.Add(Severity.Warning, "flac.no-marker", s.Stream, _ => "The data does not begin with the \"fLaC\" marker, so no metadata blocks are read; frames are searched for from the start.");
            return position;
        }

        s.AddLeading(new Node("Stream Marker", position, 4, "fLaC") { Description = Explain.Marker, Topic = Topics.Stream });
        position += 4;
        int index = 0;
        long moreStart = -1;
        long moreEnd = 0;
        int more = 0;
        Node? further = null;
        while (position < end)
        {
            s.Context.CancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> header = source.Window(position, 4);
            if (header.Length < 4)
            {
                long at = position;
                s.Findings.Add(Severity.Warning, "flac.metadata-truncated", s.Stream, _ => $"The data ends inside the header of a metadata block at 0x{at:X}.");
                s.SkippedBytes += end - position;
                position = end;
                break;
            }

            if (FrameHeader.HasSync(header))
            {
                long at = position;
                s.Findings.Add(Severity.Warning, "flac.metadata-no-last-block", s.Stream, _ => $"A frame begins at 0x{at:X} but no metadata block has the last-block flag; the metadata ends there.");
                break;
            }

            bool isLast = (header[0] & 0x80) != 0;
            int type = header[0] & 0x7F;
            int length = (header[1] << 16) | (header[2] << 8) | header[3];
            if (type == BlockTypes.Forbidden)
            {
                long at = position;
                s.Findings.Add(Severity.Warning, "flac.metadata-forbidden-type", s.Stream, _ =>
                    $"The metadata block header at 0x{at:X} has the block type 127, which the specification forbids; the metadata ends there.");
                break;
            }

            long bodyEnd = position + 4 + length;
            bool isTruncated = bodyEnd > end;
            int available = (int)Math.Min(length, end - position - 4);
            int read = type == BlockTypes.Padding ? 0 : Math.Min(available, Encspect.Formats.Flac.Metadata.MaxBodyBytes);
            bool paddingIsZero = type != BlockTypes.Padding || IsZero(source, position + 4, available);

            var block = new MappedBytes(source.Slice(position, 4 + read)[..(4 + read)].ToArray(), position);
            long bodyStart = position + 4;
            bool isListed = index < Encspect.Formats.Flac.Metadata.MaxListedBlocks;
            s.Findings.Redirect = isListed ? null : () => further!;
            Func<Node, Node?>? comment = type == BlockTypes.VorbisComment && !isTruncated && isListed
                ? parent => s.Context.ParseTag(TagName, source.Reader.Slice(bodyStart, length), parent)
                : null;
            Node node = Encspect.Formats.Flac.Metadata.Read(s, block, isLast, type, length, available, index, paddingIsZero, comment);

            s.MetadataBlocks++;
            RepeatedBlocks(s, type, node);
            if (isListed)
            {
                s.AddLeading(node);
            }
            else
            {
                moreStart = moreStart < 0 ? position : moreStart;
                moreEnd = Math.Min(end, bodyEnd);
                more++;
            }

            index++;
            if (isTruncated)
            {
                long at = position;
                int declared = length;
                s.Findings.Add(Severity.Warning, "flac.metadata-truncated", node, _ => $"The data ends inside the metadata block at 0x{at:X}, which states {Counts.Of(declared, "byte", "bytes")}.");
                position = end;
                s.Findings.Redirect = null;
                break;
            }

            s.Findings.Redirect = null;
            position = bodyEnd;
            if (isLast)
            {
                break;
            }
        }

        s.Findings.Redirect = null;

        if (more > 0)
        {
            further = new Node("Further Metadata Blocks", moreStart, moreEnd - moreStart, NodeValue.Integer(more, "blocks", "block")) { Description = Explain.FurtherMetadataBlocks, Topic = Topics.MetadataBlocks };
            s.AddLeading(further);
            int count = more;
            s.Findings.Add(Severity.Info, "flac.metadata-blocks-not-listed", s.Stream, _ => $"The report lists the first {Encspect.Formats.Flac.Metadata.MaxListedBlocks} metadata blocks; {Counts.Of(count, "more", "more")} {Counts.Agree(count, "is", "are")} read but not listed.");
        }

        if (s.Info is null)
        {
            s.Findings.Add(Severity.Warning, "flac.no-streaminfo", s.Stream, _ => "The stream has no STREAMINFO block as its first metadata block, so the sample rate, bit depth and the MD5 of the audio are not known.");
        }

        return position;
    }

    private const string TagName = "Vorbis Comment";

    // Blocks the specification allows once.
    private static void RepeatedBlocks(State s, int type, Node node)
    {
        if (type is not (BlockTypes.SeekTable or BlockTypes.VorbisComment or BlockTypes.CueSheet))
        {
            return;
        }

        int seen = s.Seen(type);
        if (seen > 1)
        {
            string name = BlockTypes.Name(type);
            s.Findings.Add(Severity.Warning, "flac.metadata-block-repeated", node, _ => $"The stream has more than one {name} block; the specification allows one.", name);
        }
    }

    private static bool IsZero(Source source, long offset, int length)
    {
        long end = offset + length;
        while (offset < end)
        {
            ReadOnlySpan<byte> window = source.Window(offset, 1);
            int count = (int)Math.Min(window.Length, end - offset);
            if (window[..count].ContainsAnyExcept((byte)0))
            {
                return false;
            }

            offset += count;
        }

        return true;
    }

    private static void Frames(State s, FrameScanner scanner, Source source, long position, bool lends)
    {
        long end = source.End;
        long skipFrom = -1;
        int step = 0;
        while (position < end)
        {
            if (++step % 64 == 0)
            {
                s.Context.CancellationToken.ThrowIfCancellationRequested();
            }

            IDecodeSession? session = lends && s.Session is { IsDecoding: true } active ? active : null;
            Attempt attempt = scanner.Read(position, session, scanner.Logs);
            long length = -1;
            EntryStatus status = EntryStatus.None;
            if (attempt.HasHeader)
            {
                FrameWalk walk = attempt.Walk;
                bool isHeaderIntact = attempt.Fault == HeaderFault.None;
                if (walk.IsComplete)
                {
                    long frameEnd = position + walk.Length;
                    if (walk.Crc16Holds || frameEnd == end || scanner.IsHeaderAt(frameEnd))
                    {
                        length = walk.Length;
                    }
                    else if (isHeaderIntact && scanner.FindNext(position + 1, position + FrameWalker.MaxFrameBytes) is var next and >= 0)
                    {
                        length = next - position;
                        status = EntryStatus.ExtentFound;
                    }
                }
                else if (walk.IsCut && isHeaderIntact)
                {
                    length = end - position;
                    status = EntryStatus.Cut;
                }
                else if (walk.Fault != SubframeFault.None && isHeaderIntact && scanner.FindNext(position + 1, position + FrameWalker.MaxFrameBytes) is var following and >= 0)
                {
                    length = following - position;
                    status = EntryStatus.Unreadable | EntryStatus.ExtentFound;
                }
            }

            if (length > 0)
            {
                if (skipFrom >= 0)
                {
                    s.AddSkipped(skipFrom, position - skipFrom);
                    skipFrom = -1;
                }

                var bytes = new MappedBytes(source.Slice(position, (int)length)[..(int)length], position);
                s.AddFrame(bytes, attempt.Header, attempt.Walk, scanner.Facts, status, scanner.Logs);
                position += length;
                continue;
            }

            skipFrom = skipFrom < 0 ? position : skipFrom;
            long found = scanner.FindNext(position + 1, end);
            position = found < 0 ? end : found;
        }

        if (skipFrom >= 0)
        {
            s.AddSkipped(skipFrom, end - skipFrom);
        }
    }
}
