using System.Buffers.Binary;
using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.MonkeysAudio;

// Reads a Monkey's Audio file: the descriptor and header (or the old header), the seek table, the WAV header data, then the frames by the
// seek table, and the terminating data (Monkey's Audio SDK 13.42, MACLib.h "APE File Format Overview", APEHeader.cpp, APEInfo.cpp). The coded
// samples are not read here: a frame is located, its stored CRC and special frame codes are read, and with decoding on the decoder takes it from there.
internal static class Reader
{
    // How far into the data the descriptor is looked for when it is not at its start, as the SDK scans (APEHeader.cpp, FindDescriptor).
    public const long SearchLimit = 1 << 20;

    private const int SeekChunk = 4096;

    public static long FindSignature(Source source, long from, long limit)
    {
        long position = Math.Max(from, source.Start);
        limit = Math.Min(limit, source.End);
        while (position < limit)
        {
            long found = source.IndexOf((byte)'M', position, limit);
            if (found < 0 || found + 4 > source.End)
            {
                return -1;
            }

            ReadOnlySpan<byte> id = source.Window(found, 4);
            if (id.Length >= 4 && id[1] == (byte)'A' && id[2] == (byte)'C' && (id[3] == (byte)' ' || id[3] == (byte)'F'))
            {
                return found;
            }

            position = found + 1;
        }

        return -1;
    }

    // Reads the header at the signature, or throws EndOfStreamException where the data ends in it. HeaderAt of the state is set for a descriptor file.
    public static ApeHeader ReadHeader(Source source, long at, out long headerAt)
    {
        headerAt = at;
        ReadOnlySpan<byte> first = source.Window(at, ApeHeader.DescriptorLength);
        if (first.Length < 6)
        {
            throw new EndOfStreamException();
        }

        if (ApeHeader.VersionOf(first) < ApeHeader.FirstDescriptorVersion)
        {
            return ApeHeader.ReadOld(source.Window(at, 40));
        }

        ApeHeader descriptor = ApeHeader.ReadDescriptor(first);
        headerAt = at + Math.Max(ApeHeader.DescriptorLength, (long)descriptor.DescriptorBytes);
        return descriptor.WithHeader(source.Window(headerAt, ApeHeader.HeaderLength));
    }

    public static bool IsPlausible(in ApeHeader h) =>
        h.Channels is >= 1 and <= ApeHeader.MaximumChannels && h.SampleRate > 0 && h.BlocksPerFrame > 0 && h.BlocksPerFrame <= 10_000_000
        && (h.HasDescriptor ? h.FileVersion >= ApeHeader.FirstDescriptorVersion : h.FileVersion is >= 3000 and < ApeHeader.FirstDescriptorVersion);

    public static void Run(State s)
    {
        ByteReader reader = s.Reader;
        ParseContext context = s.Context;
        var source = new Source(reader);
        long junk = FindSignature(source, reader.Start, reader.Start + SearchLimit + 4);
        if (junk < 0)
        {
            context.AddFinding(Severity.Error, "monkeys-audio.no-descriptor", $"The first {Counts.Of(Math.Min(reader.Length, SearchLimit), "byte", "bytes")} hold no \"MAC \" signature, which every Monkey's Audio file starts with.", context.Root);
            return;
        }

        ApeHeader h;
        long headerAt;
        try
        {
            h = ReadHeader(source, junk, out headerAt);
        }
        catch (EndOfStreamException)
        {
            context.AddFinding(Severity.Error, "monkeys-audio.header-cut", $"The data ends inside the descriptor or header at 0x{junk:X}, so nothing of the file can be read.", context.Root);
            return;
        }

        s.Junk = junk;
        s.Header = h;
        s.HasHeader = true;
        Layout(s, h, junk, headerAt, reader);
        BuildStream(s, h);
        Records records = s.Records;
        records.Junk = junk;
        records.DataStart = s.DataStart;
        records.DataLimit = reader.End;
        records.SeekTableAt = s.SeekTableAt;
        records.HasFrameFields = h.HasStoredCrc || h.HasSpecialFrames;
        records.HasSpecialFrames = h.HasSpecialFrames;
        records.HasRangeCoder = h.FileVersion >= 3891;
        if (junk > reader.Start)
        {
            long length = junk - reader.Start;
            Node junkNode = context.Root.Add(new Node("Junk", reader.Start, length, NodeValue.Integer(length, "bytes", "byte")) { Description = Explain.Junk });
            s.Findings.Add(Severity.Info, "monkeys-audio.junk-before-descriptor", junkNode, _ =>
                $"{Counts.Of(length, "byte", "bytes")} precede the descriptor; the SDK scans for it in the first MiB, but a tag in front of a file belongs to the tag and not to the stream.");
        }

        context.Root.Add(s.Stream);
        HeaderNodes(s, h, headerAt, reader);
        CheckHeader(s, h);
        StartSession(s, h, reader, headerAt);
        Frames(s, h, source);
        Hash(s, h, reader);
        Tail(s, h, reader);
        StreamFindings(s, h);
    }

    private static void Layout(State s, ApeHeader h, long junk, long headerAt, ByteReader reader)
    {
        if (h.HasDescriptor)
        {
            s.HeaderAt = headerAt;
            s.SeekTableAt = headerAt + Math.Max(ApeHeader.HeaderLength, (long)h.HeaderBytes);
            s.HeaderDataAt = s.SeekTableAt + h.SeekTableBytes;
            s.DataStart = h.DataOffset(junk);
            s.FrameDataEnd = s.DataStart + (long)Math.Min(h.FrameDataBytes, 1UL << 60);
            s.TerminatingEnd = s.FrameDataEnd + h.TerminatingBytes;
        }
        else
        {
            s.HeaderAt = junk;
            s.HeaderDataAt = junk + h.OldHeaderBytes;
            s.SeekTableAt = s.HeaderDataAt + h.HeaderDataBytes;
            long elements = h.SeekElements ?? h.TotalFrames;
            s.DataStart = s.SeekTableAt + (4 * elements) + (h.FileVersion <= 3800 ? elements : 0);
            s.TerminatingEnd = reader.End;
            s.FrameDataEnd = Math.Max(s.DataStart, reader.End - h.TerminatingBytes);
        }
    }

    private static void BuildStream(State s, ApeHeader h)
    {
        ByteReader reader = s.Reader;
        long end = Math.Min(s.FrameDataEnd, reader.End);
        end = Math.Max(end, Math.Min(s.DataStart, reader.End));
        Records records = s.Records;
        s.Stream = new Node("Monkey's Audio", s.Junk, Math.Max(0, end - s.Junk), null,
            () => new BlockedChildren(s.Leading, records.Entries.Count + (records.UnlistedFrames > 0 ? 1 : 0), i => i < records.Entries.Count ? Nodes.Entry(records, i) : Nodes.Unlisted(records)))
        {
            Kind = NodeKind.Stream,
            Description = Explain.Stream,
            Topic = Topics.File,
        };
    }

    private static void HeaderNodes(State s, ApeHeader h, long headerAt, ByteReader reader)
    {
        if (h.HasDescriptor)
        {
            Node descriptor = Nodes.Descriptor(s.Junk, h, null);
            s.DescriptorNode = descriptor;
            s.AddLeading(descriptor);
            long headerLength = Math.Max(ApeHeader.HeaderLength, (long)h.HeaderBytes);
            Node header = Nodes.Header(headerAt, h, (int)Math.Min(headerLength, reader.End - headerAt));
            s.HeaderNode = header;
            s.AddLeading(header);
        }
        else
        {
            Node header = Nodes.OldHeader(s.Junk, h);
            s.HeaderNode = header;
            s.AddLeading(header);
        }
    }

    private static void CheckHeader(State s, ApeHeader h)
    {
        FindingCollector f = s.Findings;
        Node Field(string name) => (s.HeaderNode!.Children.FirstOrDefault(c => c.Name == name) ?? s.HeaderNode!);
        Node Descriptor(string name) => (s.DescriptorNode?.Children.FirstOrDefault(c => c.Name == name) ?? s.Stream);
        if (h.FileVersion > 3990 && h.FileVersion != 4110 || h.FileVersion < 3000)
        {
            int version = h.FileVersion;
            f.Add(Severity.Warning, "monkeys-audio.version", () => h.HasDescriptor ? Descriptor("Encoder Version") : Field("Encoder Version"), _ =>
                $"The file states version {ApeHeader.VersionText(version)}, which is not one the SDK 13.42 reads (3.00 to 3.99); the fields are read as the newest layout defines them.");
        }

        if (!h.HasDescriptor)
        {
            int version = h.FileVersion;
            f.Add(Severity.Info, "monkeys-audio.old-format", () => Field("Encoder Version"), _ =>
                $"Version {ApeHeader.VersionText(version)} files start with one header and have no descriptor, so they store no MD5 of the coded data.");
        }
        else
        {
            if (h.DescriptorBytes != ApeHeader.DescriptorLength)
            {
                uint stated = h.DescriptorBytes;
                f.Add(Severity.Warning, "monkeys-audio.descriptor-size", () => Descriptor("Descriptor Bytes"), _ =>
                    $"The descriptor states {Counts.Of(stated, "byte", "bytes")} for itself, not the 52 the format defines; the SDK reads 52 and skips ahead by the difference, or reads the header straight after them where the count is smaller.");
            }

            if (h.HeaderBytes != ApeHeader.HeaderLength)
            {
                uint stated = h.HeaderBytes;
                f.Add(Severity.Warning, "monkeys-audio.descriptor-size", () => Descriptor("Header Bytes"), _ =>
                    $"The descriptor states {Counts.Of(stated, "byte", "bytes")} for the header, not the 24 the format defines.", "header");
            }

            if (h.SeekTableBytes % 4 != 0)
            {
                uint stated = h.SeekTableBytes;
                f.Add(Severity.Warning, "monkeys-audio.seek-table-size", () => Descriptor("Seek Table Bytes"), _ =>
                    $"The descriptor gives the seek table {Counts.Of(stated, "byte", "bytes")}, which is not a whole number of 4-byte entries; the SDK reads {stated / 4} entries.", "multiple");
            }

            if (h.HeaderDataBytes > ApeHeader.MaximumWavBytes)
            {
                uint stated = h.HeaderDataBytes;
                f.Add(Severity.Warning, "monkeys-audio.header-data-size", () => Descriptor("WAV Header Data Bytes"), _ =>
                    $"The descriptor states {Counts.Of(stated, "byte", "bytes")} of WAV header data, more than the 8 MiB the SDK accepts, which refuses such a file.");
            }

            if (h.TerminatingBytes > ApeHeader.MaximumWavBytes)
            {
                uint stated = h.TerminatingBytes;
                f.Add(Severity.Warning, "monkeys-audio.terminating-data-size", () => Descriptor("WAV Terminating Data Bytes"), _ =>
                    $"The descriptor states {Counts.Of(stated, "byte", "bytes")} of WAV terminating data, more than the 8 MiB the SDK accepts.");
            }
        }

        if (h.Channels is < 1 or > ApeHeader.MaximumChannels)
        {
            int channels = h.Channels;
            f.Add(Severity.Warning, "monkeys-audio.channels", () => Field("Channels"), _ =>
                $"The header states {channels} channels; the SDK refuses a file with fewer than 1 or more than 32.");
        }

        if (h.BitsPerSample is not (8 or 16 or 24 or 32))
        {
            int bits = h.BitsPerSample;
            f.Add(Severity.Warning, "monkeys-audio.bits-per-sample", () => Field("Bits Per Sample"), _ =>
                $"The header states {bits} bits per sample; the encoder writes 8, 16, 24 or 32, and the decoder writes samples of no other size.");
        }

        if (h.SampleRate == 0)
        {
            f.Add(Severity.Warning, "monkeys-audio.sample-rate", () => Field("Sample Rate"), _ => "The header states a sample rate of 0, so the duration and the bitrate cannot be worked out.");
        }

        if (h.BlocksPerFrame == 0 || h.BlocksPerFrame > h.MaximumBlocksPerFrame)
        {
            uint stated = h.BlocksPerFrame;
            long maximum = h.MaximumBlocksPerFrame;
            f.Add(Severity.Warning, "monkeys-audio.blocks-per-frame", () => Field("Blocks Per Frame"), _ =>
                stated == 0 ? "The header states 0 samples in a frame; the SDK refuses such a file." : $"The header states {stated} samples in a frame, more than the {maximum} the SDK accepts at this level.");
        }

        if (h.FinalFrameBlocks > h.BlocksPerFrame)
        {
            uint final = h.FinalFrameBlocks;
            uint size = h.BlocksPerFrame;
            f.Add(Severity.Warning, "monkeys-audio.final-frame-blocks", () => Field("Final Frame Blocks"), _ =>
                $"The header states {final} samples in the final frame, more than the {size} of a frame; the SDK refuses such a file.");
        }

        if (h.HasDescriptor && !ApeHeader.IsKnownLevel(h.CompressionLevel))
        {
            int level = h.CompressionLevel;
            f.Add(Severity.Warning, "monkeys-audio.compression-level", () => Field("Compression Level"), _ =>
                $"The header states compression level {level}, which is none of 1000 (fast) to 5000 (insane); the SDK builds no predictors for it, so the frames cannot be decoded.");
        }

        if (h.TotalFrames == 0)
        {
            f.Add(Severity.Warning, "monkeys-audio.no-frames", () => Field("Total Frames"), _ =>
                "The header counts no frame, as an encoder leaves it when it did not finish the file; the SDK refuses such a file.");
        }
    }

    private static void StartSession(State s, ApeHeader h, ByteReader reader, long headerAt)
    {
        MappedBytes configuration;
        try
        {
            configuration = h.HasDescriptor
                ? MappedBytes.Read(reader, [new ByteRange(s.Junk, ApeHeader.DescriptorLength), new ByteRange(headerAt, ApeHeader.HeaderLength)])
                : MappedBytes.Read(reader, [new ByteRange(s.Junk, h.OldHeaderBytes)]);
        }
        catch (EndOfStreamException)
        {
            return;
        }

        s.Session = s.Context.StartDecoding(MonkeysAudioCodec.Id, new DecodeSetup
        {
            Stream = s.Stream,
            Channels = h.Channels,
            BitsPerSample = h.BitsPerSample,
            Samples = h.TotalFrames == 0 ? null : h.TotalBlocks,
            Configuration = configuration,
        });
    }

    private static void Frames(State s, ApeHeader h, Source source)
    {
        ByteReader reader = s.Reader;
        Records records = s.Records;
        long elements = h.SeekElementCount;
        long present = Math.Max(0, Math.Min(elements * 4, reader.End - s.SeekTableAt)) / 4;
        s.SeekEntries = present;
        long frameCount = Math.Min(h.TotalFrames, present);
        long dataSpan = s.FrameDataEnd - s.DataStart;
        long available = Nodes.Available(records);
        s.Figures = new StreamFigures(h.SampleRate);
        IDecodeSession? session = s.Session;
        bool canFields = records.HasFrameFields;
        long previousEnd = -1;
        bool followsGap = false;
        var seek = new SeekReader(reader, s.SeekTableAt, present);
        long next = frameCount > 0 ? seek.Next() : 0;
        long expectedFirst = frameCount > 0 ? next + s.Junk - s.DataStart : 0;
        if (frameCount > 0 && expectedFirst != 0)
        {
            long first = next + s.Junk;
            long expected = s.DataStart;
            s.Findings.Add(Severity.Warning, "monkeys-audio.seek-table-start", () => Nodes.SeekEntry(records, 0), _ =>
                $"The first entry of the seek table puts the first frame at 0x{first:X}, but the descriptor, the header, the seek table and the WAV header data end at 0x{expected:X}.");
        }

        for (long f = 0; f < frameCount; f++)
        {
            if ((f & 4095) == 0)
            {
                s.Context.CancellationToken.ThrowIfCancellationRequested();
            }

            long start = next + s.Junk - s.DataStart;
            bool isLast = f == frameCount - 1;
            long end;
            if (isLast)
            {
                end = dataSpan;
            }
            else
            {
                next = seek.Next();
                end = next + s.Junk - s.DataStart;
            }

            uint blocks = f == h.TotalFrames - 1 ? h.FinalFrameBlocks : h.BlocksPerFrame;
            var status = EntryStatus.None;
            uint crc = 0;
            uint special = 0;
            if (start < 0 || start > dataSpan || end < start || end > dataSpan)
            {
                status |= EntryStatus.Misplaced;
                s.MisplacedFrames++;
            }
            else
            {
                if (end > available)
                {
                    status |= EntryStatus.Cut;
                    s.CutFrames++;
                }

                long usable = Math.Min(end, available);
                if (canFields)
                {
                    if (usable - start >= 4)
                    {
                        (crc, special, bool hasSpecial) = ReadStart(source, s.DataStart, start, usable, h.HasSpecialFrames);
                        s.StoredCrcs++;
                        if (hasSpecial)
                        {
                            status |= EntryStatus.HasSpecialCodes;
                            s.SpecialFrames++;
                            s.SilentFrames += (special & 3) == 3 ? 1 : 0;
                            s.PseudoStereoFrames += (special & 4) != 0 && (special & 3) != 3 ? 1 : 0;
                            if (usable - start < 8)
                            {
                                status |= EntryStatus.NoStartFields;
                                s.ShortFrames++;
                            }
                        }
                    }
                    else
                    {
                        status |= EntryStatus.NoStartFields;
                        s.ShortFrames++;
                    }
                }
            }

            if (previousEnd >= 0 && start != previousEnd)
            {
                s.HasGap = true;
            }

            previousEnd = end;
            var entry = new Entry { Start = start, End = end, Blocks = blocks, Crc = crc, Special = special, Status = status };
            int index = records.Entries.Count;
            if (index < State.MaxListedFrames)
            {
                records.Entries.Add(entry);
                Notice(s, status, start, end, index, blocks);
            }
            else
            {
                if (records.UnlistedFrames == 0)
                {
                    records.UnlistedStart = start;
                }

                records.UnlistedFrames++;
                records.UnlistedEnd = Math.Max(records.UnlistedEnd, end);
            }

            s.Frames++;
            s.Samples += blocks;
            if ((status & EntryStatus.Misplaced) == 0)
            {
                s.Figures.Add(FileOffsetOf(records, start), (end - start) * 8, blocks, index < State.MaxListedFrames);
            }

            if (session is { IsDecoding: true })
            {
                followsGap = Feed(s, session, entry, index < State.MaxListedFrames ? index : -1, f, followsGap);
            }
            else if ((status & (EntryStatus.Misplaced | EntryStatus.NoStartFields)) != 0)
            {
                followsGap = true;
            }
        }
    }

    private static long FileOffsetOf(Records records, long start) => records.DataStart + Math.Max(0, start);

    private static void Notice(State s, EntryStatus status, long start, long end, int index, uint blocks)
    {
        Records records = s.Records;
        if ((status & EntryStatus.Misplaced) != 0)
        {
            long raw = start + s.DataStart;
            s.Findings.Add(Severity.Warning, "monkeys-audio.frame-misplaced", () => Nodes.Frame(records, index), count =>
                $"The seek table puts frame {index} at 0x{raw:X}, outside the frame data or before the frame it follows, so its bytes cannot be told{(count > 1 ? $"; {count - 1} more frames are placed the same way" : string.Empty)}.");
        }

        if ((status & EntryStatus.Cut) != 0)
        {
            s.Findings.Add(Severity.Warning, "monkeys-audio.frame-cut", () => Nodes.Frame(records, index), count =>
                $"The file ends inside frame {index}{(count > 1 ? $", and {count - 1} more frames lie past its end" : string.Empty)}; a frame is read only as far as the file holds whole words of it.");
        }

        if ((status & EntryStatus.NoStartFields) != 0)
        {
            s.Findings.Add(Severity.Warning, "monkeys-audio.frame-too-short", () => Nodes.Frame(records, index), count =>
                $"Frame {index} is too short for the CRC and the special frame codes it starts with{(count > 1 ? $", and {count - 1} more frames are" : string.Empty)}.");
        }
    }

    // The stored CRC (31 bits) and, where its bit 31 flags them, the special frame codes: the first four stream bytes of the frame, then four more.
    private static (uint Crc, uint Special, bool HasSpecial) ReadStart(Source source, long dataStart, long start, long usable, bool hasSpecialFrames)
    {
        long firstWord = start >> 2;
        ReadOnlySpan<byte> words = source.Window(dataStart + (firstWord << 2), 12);
        uint word = Word(words, firstWord, start);
        if (!hasSpecialFrames)
        {
            return (word & 0x7FFFFFFF, 0, false);
        }

        if ((word & 0x80000000) == 0)
        {
            return (word, 0, false);
        }

        uint special = usable - start >= 8 ? Word(words, firstWord, start + 4) : 0;
        return (word & 0x7FFFFFFF, special, true);
    }

    // The four stream bytes from index, most significant first, out of the words that start with word firstWord.
    private static uint Word(ReadOnlySpan<byte> words, long firstWord, long index)
    {
        uint value = 0;
        for (int i = 0; i < 4; i++)
        {
            long at = index + i;
            long position = (((at >> 2) - firstWord) << 2) + 3 - (at & 3);
            value = (value << 8) | (position >= 0 && position < words.Length ? words[(int)position] : (uint)0);
        }

        return value;
    }

    private static bool Feed(State s, IDecodeSession session, in Entry e, int index, long frame, bool followsGap)
    {
        Records records = s.Records;
        long first = frame * s.Header.BlocksPerFrame;
        bool isUnreadable = (e.Status & (EntryStatus.Misplaced | EntryStatus.NoStartFields)) != 0 || e.Length > State.MaxDecodedFrameBytes || e.End <= e.Start;
        if (e.Length > State.MaxDecodedFrameBytes && (e.Status & EntryStatus.Misplaced) == 0)
        {
            long length = e.Length;
            s.Findings.Add(Severity.Info, "monkeys-audio.frame-not-decoded", () => index >= 0 ? Nodes.Frame(records, index) : s.Stream, count =>
                $"Frame {frame} holds {Counts.Of(length, "byte", "bytes")}, more than the 64 MiB the analyzer reads into memory to decode a frame, so it is not decoded{(count > 1 ? $"; {count - 1} more frames are not either" : string.Empty)}.");
        }

        if (isUnreadable)
        {
            session.Decode(new CodedFrame(new MappedBytes(ReadOnlyMemory<byte>.Empty, records.DataStart), "Frame", first, (int)Math.Min(e.Blocks, int.MaxValue)) { IsUnreadable = true, FollowsGap = followsGap });
            return true;
        }

        List<ByteRange> ranges = Nodes.FrameRanges(records, e);
        MappedBytes bytes = MappedBytes.Read(s.Reader, ranges);
        session.Decode(new CodedFrame(bytes, "Frame", first, (int)Math.Min(e.Blocks, int.MaxValue))
        {
            IsDamaged = (e.Status & EntryStatus.Cut) != 0,
            FollowsGap = followsGap,
        });
        return false;
    }

    // The digest of the coded data that the descriptor stores (APEHeader.h and MACLib.cpp, VerifyFileW2): the WAV header data, then the frame data and
    // the terminating data, then the header and the seek table, each as long as the descriptor says.
    private static void Hash(State s, ApeHeader h, ByteReader reader)
    {
        if (!h.HasDescriptor)
        {
            return;
        }

        long headerLength = h.HeaderBytes;
        long headerEnd = s.HeaderAt + headerLength;
        long dataLength = (long)Math.Min(h.FrameDataBytes, 1UL << 60) + h.TerminatingBytes;
        bool isComplete = s.HeaderDataAt + h.HeaderDataBytes <= reader.End && s.DataStart + dataLength <= reader.End && headerEnd <= reader.End
            && s.SeekTableAt - (Math.Max(ApeHeader.HeaderLength, headerLength) - headerLength) + h.SeekTableBytes <= reader.End;
        if (!isComplete)
        {
            return;
        }

        var md5 = new Md5();
        var buffer = new byte[1 << 16];
        Append(reader, md5, buffer, s.HeaderDataAt, h.HeaderDataBytes);
        Append(reader, md5, buffer, s.DataStart, dataLength);
        Append(reader, md5, buffer, s.HeaderAt, headerLength);
        Append(reader, md5, buffer, s.SeekTableAt - (Math.Max(ApeHeader.HeaderLength, headerLength) - headerLength), h.SeekTableBytes);
        var digest = new byte[Md5.HashLength];
        md5.GetHashAndReset(digest);
        s.ComputedMd5 = digest;
        if (!h.Md5.Span.SequenceEqual(digest))
        {
            string stored = Convert.ToHexStringLower(h.Md5.Span);
            string computed = Convert.ToHexStringLower(digest);
            s.Findings.Add(Severity.Warning, "monkeys-audio.md5-mismatch", () => s.DescriptorNode!.Children.First(c => c.Name == "MD5"), _ =>
                $"The MD5 over the coded data is {computed}, but the descriptor stores {stored}. Some of the bytes it covers changed; the MD5 cannot tell where, and the frame CRCs, compared when the audio is verified, can.");
        }

        s.DescriptorNode!.Add("Computed MD5", s.Junk + 36, 16, Convert.ToHexStringLower(digest), Explain.ComputedMd5, Topics.DescriptorMd5);
        s.DescriptorNode!.Add("IsMd5Valid", s.Junk + 36, 16, NodeValue.Boolean(h.Md5.Span.SequenceEqual(digest)), Explain.IsMd5Valid, Topics.DescriptorMd5);
    }

    private static void Append(ByteReader reader, Md5 md5, byte[] buffer, long offset, long length)
    {
        reader.Position = offset;
        while (length > 0)
        {
            int count = (int)Math.Min(buffer.Length, length);
            reader.Read(buffer.AsSpan(0, count));
            md5.AppendData(buffer.AsSpan(0, count));
            length -= count;
        }
    }

    // The seek table, the WAV header data and the bytes after the frames.
    private static void Tail(State s, ApeHeader h, ByteReader reader)
    {
        Records records = s.Records;
        long tableLength = Math.Min(h.HasDescriptor ? h.SeekTableBytes : (h.SeekElements ?? h.TotalFrames) * 4L, Math.Max(0, reader.End - s.SeekTableAt));
        Node seek = Nodes.SeekTable(records, tableLength, (int)Math.Min(s.SeekEntries, int.MaxValue));
        if (h.HasDescriptor)
        {
            s.AddLeading(seek);
            AddHeaderData(s, h, reader);
        }
        else
        {
            AddHeaderData(s, h, reader);
            s.AddLeading(seek);
        }

        s.SeekNode = seek;
        if (h.HasDescriptor ? h.SeekTableBytes / 4 != h.TotalFrames : s.SeekEntries != h.TotalFrames)
        {
            long entries = h.HasDescriptor ? h.SeekTableBytes / 4 : s.SeekEntries;
            uint frames = h.TotalFrames;
            s.Findings.Add(Severity.Warning, "monkeys-audio.seek-table-size", () => seek, _ =>
                $"The seek table holds {Counts.Of(entries, "entry", "entries")}, but the header counts {Counts.Of(frames, "frame", "frames")}{(entries < frames ? $"; the {frames - entries} frames without an entry cannot be located" : "; the extra entries lead nowhere")}.", "count");
        }

        long available = Math.Max(0, reader.End - s.SeekTableAt);
        long wanted = h.HasDescriptor ? h.SeekTableBytes : (h.SeekElements ?? h.TotalFrames) * 4L;
        if (available < wanted)
        {
            s.Findings.Add(Severity.Warning, "monkeys-audio.seek-table-cut", () => seek, _ =>
                $"The file ends {Counts.Of(wanted - available, "byte", "bytes")} short of the seek table the {(h.HasDescriptor ? "descriptor" : "header")} describes.");
        }

        long frameEnd = Math.Min(s.FrameDataEnd, reader.End);
        if (h.HasDescriptor && s.TerminatingEnd > reader.End)
        {
            long wantedEnd = s.TerminatingEnd;
            long fileEnd = reader.End;
            s.Findings.Add(Severity.Warning, "monkeys-audio.data-cut", () => s.Stream, _ =>
                $"The descriptor counts the frame data and terminating data up to 0x{wantedEnd:X}, but the file ends at 0x{fileEnd:X}, {Counts.Of(wantedEnd - fileEnd, "byte", "bytes")} earlier.");
            if (s.ComputedMd5 is null)
            {
                s.Findings.Add(Severity.Info, "monkeys-audio.md5-not-compared", () => s.DescriptorNode!.Children.First(c => c.Name == "MD5"), _ =>
                    "The MD5 over the coded data was not compared: the file ends before the end of the data the descriptor counts.");
            }
        }
        else if (h.HasDescriptor && s.ComputedMd5 is null)
        {
            s.Findings.Add(Severity.Info, "monkeys-audio.md5-not-compared", () => s.DescriptorNode!.Children.First(c => c.Name == "MD5"), _ =>
                "The MD5 over the coded data was not compared: the structures the descriptor counts do not all lie inside the file.");
        }

        if (frameEnd < reader.End && h.TerminatingBytes > 0 && frameEnd + h.TerminatingBytes <= reader.End)
        {
            Node terminating = s.Context.Root.Add(new Node("WAV Terminating Data", frameEnd, h.TerminatingBytes, NodeValue.Integer(h.TerminatingBytes, "bytes", "byte")) { Description = Explain.TerminatingData, Topic = Topics.File });
            s.TerminatingNode = terminating;
            long after = frameEnd + h.TerminatingBytes;
            Trailing(s, after, reader);
        }
        else if (h.TerminatingBytes == 0)
        {
            Trailing(s, frameEnd, reader);
        }
        else if (frameEnd < reader.End)
        {
            long length = reader.End - frameEnd;
            s.Context.Root.Add(new Node("WAV Terminating Data", frameEnd, length, NodeValue.Integer(length, "bytes", "byte")) { Description = Explain.TerminatingData, Topic = Topics.File });
        }
    }

    private static void Trailing(State s, long from, ByteReader reader)
    {
        if (from >= reader.End)
        {
            return;
        }

        long length = reader.End - from;
        Node data = s.Context.Root.Add(new Node("Trailing Data", from, length, NodeValue.Integer(length, "bytes", "byte")) { Description = Explain.TrailingData });
        s.Findings.Add(Severity.Info, "monkeys-audio.trailing-data", data, _ =>
            $"{Counts.Of(length, "byte", "bytes")} at 0x{from:X} follow the terminating data and belong to no tag; the descriptor does not count them.");
    }

    private static void AddHeaderData(State s, ApeHeader h, ByteReader reader)
    {
        if (h.HeaderDataBytes == 0 || s.HeaderDataAt >= reader.End)
        {
            return;
        }

        long length = Math.Min(h.HeaderDataBytes, reader.End - s.HeaderDataAt);
        Node data = new("WAV Header Data", s.HeaderDataAt, length, NodeValue.Integer(length, "bytes", "byte")) { Description = Explain.HeaderData, Topic = Topics.File };
        string? container = WavHeader.Container(reader, s.HeaderDataAt, length);
        if (container is not null)
        {
            data.Value = $"{Counts.Of(length, "byte", "bytes")}, {container}";
        }

        s.AddLeading(data);
    }

    private static void StreamFindings(State s, ApeHeader h)
    {
        if (s.Records.UnlistedFrames > 0)
        {
            long unlisted = s.Records.UnlistedFrames;
            s.Findings.Add(Severity.Info, "monkeys-audio.frames-not-listed", () => Nodes.Unlisted(s.Records), _ =>
                $"The report lists the first {State.MaxListedFrames} frames one by one; the {Counts.Of(unlisted, "frame", "frames")} after them are read and checked and shown as one stretch.");
        }

        Summary.Finish(s);
    }

    // The seek table read in chunks, with the 32-bit offsets made 64-bit as the SDK does: an offset below the one before it means the file passed 4 GiB.
    private sealed class SeekReader(ByteReader reader, long at, long count)
    {
        private readonly byte[] _buffer = new byte[SeekChunk * 4];
        private int _inBuffer;
        private int _used;
        private long _read;
        private long _position = at;
        private uint _previous;
        private long _add;

        public long Next()
        {
            if (_used == _inBuffer)
            {
                int take = (int)Math.Min(SeekChunk, count - _read);
                if (take <= 0)
                {
                    return _add + _previous;
                }

                reader.Position = _position;
                reader.Read(_buffer.AsSpan(0, take * 4));
                _position += take * 4L;
                _inBuffer = take;
                _used = 0;
            }

            uint current = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(_used * 4));
            _used++;
            _read++;
            if (current < _previous)
            {
                _add += 1L << 32;
            }

            _previous = current;
            return _add + current;
        }
    }
}
