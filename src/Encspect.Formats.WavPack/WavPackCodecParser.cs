using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.WavPack;

// WavPack in a container that stores a block without its header, as Matroska does (A_WAVPACK4): a packet holds the samples of its blocks
// in 4 bytes, then each block's flags and CRC in 4 bytes apiece and, unless the flags make it the only block of its set, the block's size
// in 4 more, then the block's sub-blocks. The header that is left out is the one FFmpeg rebuilds (libavformat/matroskadec.c
// matroska_parse_wavpack): the stream version from the CodecPrivate, which is 0x410 when it
// has none, a block index and a total of 0, and the samples, flags and CRC the packet gives. The rebuilt blocks go to the decoder, which
// decodes a packet's blocks as a set and compares the CRC of each block's audio with the one the packet stores. The block checksum sub-block
// cannot be compared: it covers the original header, which the container does not keep.
public sealed class WavPackCodecParser : ICodecParser
{
    public const string CodecId = "wavpack";

    private const ushort AssumedVersion = 0x410;
    private const int MaxListedPackets = 256;
    private const int MaxListedSubBlocks = 64;

    public string Id => CodecId;

    public void Parse(CodecContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Packets is not { } packets)
        {
            context.AddFinding(Severity.Info, "wavpack.payload-not-analyzed", "WavPack handed over as one payload is not analyzed; only packets of blocks are.", context.Parent);
            return;
        }

        var run = new PacketRun(context);
        int index = 0;
        foreach (MappedBytes packet in packets)
        {
            if (index % 4096 == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
            }

            run.Add(packet, index++);
        }

        run.Finish();
    }

    private readonly record struct ReducedBlock(uint Flags, uint Crc, int HeaderLength, int DataStart, int DataLength);

    private sealed class PacketRun(CodecContext context)
    {
        private readonly FindingCollector _findings = new();
        private readonly ushort _version = VersionOf(context);
        private IDecodeSession? _session;
        private byte[] _rebuilt = [];
        private long _packets;
        private long _blocks;
        private long _audioBlocks;
        private long _samples;
        private long _bytes;
        private long _invalid;
        private long _extraBlocks;
        private long _md5Blocks;
        private long _start = -1;
        private long _end;
        private bool _hasGap;
        private BlockHeader _first;
        private bool _hasFirst;
        private int _channels;
        private int _packetChannels;
        private uint _channelMask;
        private int _rate;
        private bool _isLossy;
        private int _listed;

        private static ushort VersionOf(CodecContext context) =>
            context.Configuration is { Data.Length: >= 2 } configuration ? BinaryPrimitives.ReadUInt16LittleEndian(configuration.Data.Span) : AssumedVersion;

        public void Add(MappedBytes packet, int index)
        {
            _start = _start < 0 ? packet.FileStart : _start;
            _end = Math.Max(_end, packet.FileEnd);
            _packets++;
            _bytes += packet.Length;
            _hasGap |= packet.FollowsGap;
            ReadOnlySpan<byte> data = packet.Span;
            Node? node = _listed < MaxListedPackets ? NewPacketNode(packet, index) : null;
            if (data.Length < 12)
            {
                Invalid(packet, node, $"is {Counts.Of(data.Length, "byte", "bytes")} long, too short for the samples and one block's flags and CRC");
                HandUnreadable(packet);
                return;
            }

            uint samples = BinaryPrimitives.ReadUInt32LittleEndian(data);
            node?.Add(Field(packet, 0, 4, "Samples", NodeValue.Integer(samples, "samples", "sample"), Explain.PacketSamples));
            var blocks = new List<ReducedBlock>();
            int position = 4;
            string? fault = null;
            while (data.Length - position >= 8 && blocks.Count < data.Length / 8)
            {
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data[position..]);
                uint crc = BinaryPrimitives.ReadUInt32LittleEndian(data[(position + 4)..]);
                bool hasSize = (flags & (BlockHeader.InitialBlockFlag | BlockHeader.FinalBlockFlag)) != (BlockHeader.InitialBlockFlag | BlockHeader.FinalBlockFlag);
                int headerLength = hasSize ? 12 : 8;
                int length;
                if (hasSize)
                {
                    if (data.Length - position < 12)
                    {
                        fault = "ends inside a block's size";
                        break;
                    }

                    uint size = BinaryPrimitives.ReadUInt32LittleEndian(data[(position + 8)..]);
                    if (size > data.Length - position - 12)
                    {
                        fault = $"states a block of {size} bytes, but {data.Length - position - 12} are left in the packet";
                        break;
                    }

                    length = (int)size;
                }
                else
                {
                    length = data.Length - position - 8;
                }

                blocks.Add(new ReducedBlock(flags, crc, headerLength, position + headerLength, length));
                position += headerLength + length;
            }

            if (fault is null && position < data.Length)
            {
                fault = $"has {Counts.Of(data.Length - position, "byte", "bytes")} after its last block that are too few for another";
            }

            if (blocks.Count == 0)
            {
                Invalid(packet, node, fault ?? "holds no block");
                HandUnreadable(packet);
                return;
            }

            if (fault is not null)
            {
                string message = fault;
                _invalid++;
                _findings.Add(Severity.Warning, "wavpack.matroska-packet-invalid", () => node ?? context.Parent, _ => $"The packet at 0x{packet.FileStart:X} {message}; the blocks before it are read.", "packet");
            }

            _packetChannels = blocks.Sum(b => (b.Flags & BlockHeader.MonoFlag) != 0 ? 1 : 2);
            foreach (ReducedBlock block in blocks)
            {
                Block(packet, node, block, samples, index);
            }

            _samples += samples;
        }

        private Node? NewPacketNode(MappedBytes packet, int index)
        {
            _listed++;
            var node = new Node("Packet", packet.FileStart, packet.FileEnd - packet.FileStart, NodeValue.Integer(index + 1)) { Description = Explain.MatroskaPacket, Topic = Topics.Matroska };
            context.Parent.Add(node);
            return node;
        }

        // A packet that holds no block: a unit the decoder cannot get anything from.
        private void HandUnreadable(MappedBytes packet)
        {
            if (_session is { IsDecoding: true } session)
            {
                session.Decode(new CodedFrame(MappedBytes.Opaque(ReadOnlyMemory<byte>.Empty, packet.FileStart, packet.FileEnd), "Packet", _samples, 0)
                {
                    FollowsGap = packet.FollowsGap,
                    IsUnreadable = true,
                });
            }
        }

        private void Invalid(MappedBytes packet, Node? node, string reason)
        {
            _invalid++;
            _findings.Add(Severity.Warning, "wavpack.matroska-packet-invalid", () => node ?? context.Parent, _ => $"The packet at 0x{packet.FileStart:X} {reason}, so it is not read as blocks.", "packet");
        }

        private static Node Field(MappedBytes packet, int offset, int length, string name, NodeValue value, string description, string? topic = null) =>
            new(name, packet.OffsetOf(offset), length, value) { Description = description, Topic = topic };

        private void Block(MappedBytes packet, Node? packetNode, ReducedBlock block, uint samples, int index)
        {
            int total = BlockHeader.Length + block.DataLength;
            if (_rebuilt.Length < total)
            {
                _rebuilt = new byte[total];
            }

            Span<byte> rebuilt = _rebuilt.AsSpan(0, total);
            rebuilt[..BlockHeader.Length].Clear();
            "wvpk"u8.CopyTo(rebuilt);
            BinaryPrimitives.WriteUInt32LittleEndian(rebuilt[4..], (uint)(block.DataLength + 24));
            BinaryPrimitives.WriteUInt16LittleEndian(rebuilt[8..], _version);
            BinaryPrimitives.WriteUInt32LittleEndian(rebuilt[20..], samples);
            BinaryPrimitives.WriteUInt32LittleEndian(rebuilt[24..], block.Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(rebuilt[28..], block.Crc);
            packet.Span.Slice(block.DataStart, block.DataLength).CopyTo(rebuilt[BlockHeader.Length..]);
            int blockStart = block.DataStart - block.HeaderLength;
            long fileStart = packet.OffsetOf(blockStart);
            long fileEnd = packet.OffsetOf(block.DataStart + block.DataLength);
            var header = new BlockHeader(rebuilt);
            _blocks++;
            bool hasAudio = samples != 0;
            bool isPlausible = BlockHeader.IsPlausible(rebuilt);
            if (hasAudio)
            {
                _audioBlocks++;
            }

            if (!isPlausible)
            {
                _invalid++;
                _findings.Add(Severity.Warning, "wavpack.matroska-block-invalid", () => packetNode ?? context.Parent,
                    _ => $"The block at 0x{fileStart:X} has {(block.DataLength % 2 != 0 ? "a size that is odd" : "more than 196,607 samples or a size libwavpack refuses")}, so libwavpack's reader would not take its header for a block's.", "header");
            }

            if (packetNode is not null)
            {
                AddBlockNode(packet, packetNode, header, block, blockStart, rebuilt);
            }

            if (isPlausible && !_hasFirst)
            {
                _first = header;
                _hasFirst = true;
            }

            if (isPlausible)
            {
                CountSubBlocks(rebuilt);
            }

            if (!isPlausible)
            {
                // A block the session cannot read is handed on as unreadable, so the host counts it as not decoded and the unit after it follows a gap.
                if (_session is { IsDecoding: true } unreadable)
                {
                    unreadable.Decode(new CodedFrame(MappedBytes.Opaque(_rebuilt.AsMemory(0, total), fileStart, fileEnd), "Block", _samples, (int)Math.Min(samples, int.MaxValue))
                    {
                        FollowsGap = packet.FollowsGap && blockStart == 4,
                        IsUnreadable = true,
                    });
                }

                return;
            }

            if (_session is null && hasAudio)
            {
                Start(header, rebuilt, fileStart);
            }

            if (_session is { IsDecoding: true } session)
            {
                session.Decode(new CodedFrame(MappedBytes.Opaque(_rebuilt.AsMemory(0, total), fileStart, fileEnd), hasAudio ? "Block" : "Metadata Block", _samples, (int)samples)
                {
                    FollowsGap = packet.FollowsGap && blockStart == 4,
                });
            }
        }

        // The sub-blocks a decoder reads the extra CRC and the MD5 from, counted so the checksums can be declared.
        private void CountSubBlocks(ReadOnlySpan<byte> block)
        {
            int position = BlockHeader.Length;
            bool hasExtra = false;
            bool hasMd5 = false;
            while (BlockContents.TryRead(block, position, out SubBlockSpan sub))
            {
                hasExtra |= sub.Function == SubBlockIds.WvxBitstream;
                hasMd5 |= sub.Function is SubBlockIds.Md5Checksum or SubBlockIds.AltMd5Checksum && sub.DataLength == 16;
                position = sub.Next;
            }

            _extraBlocks += hasExtra ? 1 : 0;
            _md5Blocks += hasMd5 ? 1 : 0;
        }

        private void Start(in BlockHeader header, ReadOnlySpan<byte> rebuilt, long fileStart)
        {
            // What the first block says, read as the decoder opens the stream with it; a block the decoder refuses is reported by its Open.
            var contents = new BlockContents();
            try
            {
                contents.Read(rebuilt, header, channelsAreKnown: false);
            }
            catch (InvalidDataException)
            {
                contents = new BlockContents();
            }

            int rate = contents.SampleRate is > 0 and { } stated ? stated : header.SampleRate != 0 ? header.SampleRate : 44100;
            if (header.IsDsd && contents.Dsd.Length >= 2)
            {
                long native = (long)rate << Math.Min(rebuilt[contents.Dsd.Start], (byte)31);
                rate = (int)Math.Min(native > Reader.MaxNativeRate / 8 ? Reader.MaxNativeRate : native * 8, Reader.MaxNativeRate);
            }

            _rate = rate;
            _isLossy = header.IsHybrid;
            _channels = contents.ChannelInfo is { } info ? info.Channels : header.IsMono ? 1 : 2;
            _channelMask = contents.ChannelInfo?.Mask ?? (uint)(0x5 - _channels);
            _session = context.StartDecoding(CodecId, new DecodeSetup
            {
                Stream = context.Parent,
                Channels = _channels,
                BitsPerSample = header.IsDsd ? 8 : header.IsFloat ? 32 : header.BytesPerSample * 8,
                IsDsd = header.IsDsd,
                Configuration = new MappedBytes(rebuilt.ToArray(), fileStart),
                IsLossy = _isLossy,
            });
        }

        private void AddBlockNode(MappedBytes packet, Node packetNode, in BlockHeader header, ReducedBlock block, int blockStart, ReadOnlySpan<byte> rebuilt)
        {
            long start = packet.OffsetOf(blockStart);
            long end = packet.OffsetOf(block.DataStart + block.DataLength);
            var node = new Node("Block", start, end - start, NodeValue.Integer(header.Samples, "samples", "sample"))
            {
                Kind = NodeKind.Frame,
                Description = Explain.MatroskaBlock,
                Topic = Topics.Matroska,
            };
            node.Add(Nodes.FlagsNode(header, start));
            node.Add(Nodes.BlockCrc(MappedBytes.Opaque(ReadOnlyMemory<byte>.Empty, start, end), header.Crc));
            if (block.HeaderLength == 12)
            {
                node.Add(Field(packet, blockStart + 8, 4, "Block Size", NodeValue.Integer(block.DataLength, "bytes", "byte"), Explain.MatroskaBlockSize, Topics.Matroska));
            }

            var subBlocks = new Node("Sub-blocks", packet.OffsetOf(block.DataStart), block.DataLength, null) { Description = Explain.SubBlocksOfBlock, Topic = Topics.SubBlocks };
            int position = BlockHeader.Length;
            int listed = 0;
            while (BlockContents.TryRead(rebuilt, position, out SubBlockSpan sub) && listed < MaxListedSubBlocks)
            {
                long at = packet.OffsetOf(block.DataStart + position - BlockHeader.Length);
                int length = sub.Next - position;
                subBlocks.Add(Nodes.SubBlockSummary(at, length, sub.Function, sub.DataLength));
                position = sub.Next;
                listed++;
            }

            node.Add(subBlocks);
            packetNode.Add(node);
        }

        public void Finish()
        {
            if (_start < 0)
            {
                return;
            }

            bool coversAll = _invalid == 0 && !_hasGap && _audioBlocks > 0;
            IDecodeSession session = _session ?? context.StartDecoding(CodecId, new DecodeSetup { Stream = context.Parent, Channels = 1, BitsPerSample = 16 });
            StreamVerification verification = session.Finish(
            [
                new ChecksumTally { Name = ChecksumNames.BlockCrc, Covers = ChecksumCoverage.DecodedAudio, CoversAllAudio = coversAll, Stored = _audioBlocks },
                new ChecksumTally { Name = ChecksumNames.ExtraCrc, Covers = ChecksumCoverage.DecodedAudio, Stored = _extraBlocks },
                new ChecksumTally { Name = ChecksumNames.Md5, Covers = ChecksumCoverage.DecodedAudio, CoversAllAudio = true, Stored = Math.Min(1, _md5Blocks) },
            ]);
            _findings.Flush(context);
            if (!_hasFirst || _audioBlocks == 0)
            {
                return;
            }

            BlockHeader first = _first;
            double duration = _rate > 0 ? (double)_samples * (first.IsDsd ? 8 : 1) / _rate : 0;
            var statistics = new Node("Statistics", _start, _end - _start) { Kind = NodeKind.Statistics, Description = Explain.Statistics };
            Node Stat(string name, NodeValue value, string description, string? topic = null) =>
                new(name, _start, _end - _start, value) { Description = description, Topic = topic };
            statistics.Add(Stat("Packets", NodeValue.Integer(_packets, "packets", "packet"), Explain.StatPackets, Topics.Matroska));
            statistics.Add(Stat("Blocks", NodeValue.Integer(_blocks, "blocks", "block"), Explain.StatBlocks, Topics.Blocks));
            statistics.Add(Stat("Samples", NodeValue.Integer(_samples, "samples", "sample"), Explain.StatSamples));
            statistics.Add(Stat("Duration", NodeValue.Decimal(duration, 3, "s"), Explain.StatDuration));
            statistics.Add(Stat("Sample Rate", NodeValue.Integer(_rate, "Hz"), first.IsDsd ? Explain.StatRateDsd : Explain.StatRate));
            statistics.Add(Stat("Channels", NodeValue.Integer(_channels, "channels", "channel"), Explain.StatChannels, Topics.Channels));
            statistics.Add(Stat("Stream Version", NodeValue.Hex(_version, 4), Explain.StreamVersionOfCodecPrivate, Topics.Matroska));
            statistics.Add(Stat("IsHybrid", NodeValue.Boolean(first.IsHybrid), Explain.StatIsHybrid, Topics.Hybrid));
            statistics.Add(Stat("IsFloatingPoint", NodeValue.Boolean(first.IsFloat), Explain.StatIsFloat, Topics.FloatAndLargeIntegers));
            statistics.Add(Stat("IsDsd", NodeValue.Boolean(first.IsDsd), Explain.StatIsDsd, Topics.Dsd));
            context.Parent.Add(statistics);
            int shifted = (first.BytesPerSample * 8) - first.LeftShift;
            context.AddStream(new StreamInfo
            {
                Nodes = [context.Parent],
                Codec = first.IsHybrid ? "WavPack hybrid" : "WavPack lossless",
                SampleRate = _rate,
                Channels = _channels,
                ChannelLayout = ChannelLayouts.FromWaveMask(_channelMask, _channels),
                BitsPerSample = first.IsDsd ? 1 : first.IsFloat ? 32 : shifted > 0 ? shifted : null,
                Samples = _samples * (first.IsDsd ? 8 : 1),
                Duration = duration,
                AverageBitrate = duration > 0 ? _bytes * 8 / duration / 1000 : null,
                Verification = verification,
            });
        }
    }
}
