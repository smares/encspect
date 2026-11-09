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
        private long _lost;
        private bool _isPacketLost;
        private bool _isPacketCut;
        private StreamFigures? _figures;
        private readonly UnitCategoryTally _stereo = new("Stereo Coding", State.StereoCodings, "stereo block", GraphConcept.StereoCoding, isVariable: true);
        private readonly List<byte> _setStereo = [];
        private bool _hasNewConfig;
        private bool _hasLossyBlocks;
        private ConfigValue? _config;
        private int _termsBlocks;
        private int _termsMinimum = int.MaxValue;
        private int _termsMaximum;
        private bool _hasChecksum;
        private long _extraBlocks;
        private long _md5Blocks;
        private long _start = -1;
        private long _end;
        private bool _hasGap;
        private BlockHeader _first;
        private bool _hasFirst;
        private int _channels;
        private uint _channelMask;
        private int _rate;
        private bool _isLossy;
        private int _listed;
        private int _member;
        private int _blockZeroBits;
        private readonly WastedBitsTally _wasted = new();

        private static ushort VersionOf(CodecContext context) =>
            context.Configuration is { Data.Length: >= 2 } configuration ? BinaryPrimitives.ReadUInt16LittleEndian(configuration.Data.Span) : AssumedVersion;

        public void Add(MappedBytes packet, int index)
        {
            _start = _start < 0 ? packet.FileStart : _start;
            _end = Math.Max(_end, packet.FileEnd);
            _packets++;
            _bytes += packet.Length;
            _hasGap |= packet.FollowsGap;
            _isPacketLost = packet.IsCut;
            _isPacketCut = packet.IsCut;
            _setStereo.Clear();
            _member = 0;
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
            if (packet.IsCut)
            {
                long cutBytes = packet.Length;
                _findings.Add(Severity.Warning, "wavpack.matroska-packet-cut", () => node ?? context.Parent, _ =>
                    $"The data ends inside the packet at 0x{packet.FileStart:X}, which holds {Counts.Of(samples, "sample", "samples")}: only {Counts.Of(cutBytes, "byte", "bytes")} of it {(cutBytes == 1 ? "is" : "are")} in the file.", "packet");
            }

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
                _isPacketLost = true;
                _invalid++;
            }

            if (fault is not null && !packet.IsCut)
            {
                string message = fault;
                _findings.Add(Severity.Warning, "wavpack.matroska-packet-invalid", () => node ?? context.Parent, _ => $"The packet at 0x{packet.FileStart:X} {message}; the blocks before it are read.", "packet");
            }

            foreach (ReducedBlock block in blocks)
            {
                Block(packet, node, block, samples, index);
            }

            _samples += samples;
            // A packet that is cut or holds a block that cannot be read is lost whole: it declares its samples, which the stream counts.
            _lost += _isPacketLost ? samples : 0;
            if (_rate > 0)
            {
                bool isDsd = _hasFirst && _first.IsDsd;
                _figures ??= new StreamFigures(_rate);
                // The bytes of the packet stand for the block set's: the headers a Matroska muxer strips are not in the file.
                _figures.Add(packet.FileStart, packet.Length * 8L, samples * (isDsd ? 8 : 1));
                _stereo.AddAll(isDsd ? [] : System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_setStereo));
            }
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
                _isPacketLost = true;
                string reason = ImplausibleReason(block.DataLength, rebuilt);
                if (!_isPacketCut)
                {
                    _findings.Add(Severity.Warning, "wavpack.matroska-block-invalid", () => packetNode ?? context.Parent,
                        _ => $"The block at 0x{fileStart:X} has {reason}, so libwavpack's reader would not take its header for a block's.", "header");
                }
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
                CountSubBlocks(rebuilt, header);
                if (header.HasAudio && !header.IsDsd)
                {
                    // The packet's place in the stream is the samples of the packets before it (the block index a muxer strips is not in the file).
                    _wasted.Add(_member++, _samples, _blockZeroBits);
                }

                _hasChecksum |= (header.Flags & BlockHeader.HasChecksumFlag) != 0;
                if (!header.IsMono && _setStereo.Count < State.MaxSetBlocks)
                {
                    _setStereo.Add((header.Flags & BlockHeader.JointStereoFlag) != 0 ? (byte)1 : (byte)0);
                }
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
        private void CountSubBlocks(ReadOnlySpan<byte> block, in BlockHeader header)
        {
            byte int32SentBits = 0;
            byte floatFlags = 0;
            _blockZeroBits = 0;
            int position = BlockHeader.Length;
            bool hasExtra = false;
            bool hasMd5 = false;
            while (BlockContents.TryRead(block, position, out SubBlockSpan sub))
            {
                hasExtra |= sub.Function == SubBlockIds.WvxBitstream;
                _hasNewConfig |= sub.Function == SubBlockIds.NewConfigBlock;
                ReadOnlySpan<byte> data = block.Slice(sub.DataStart, sub.DataLength);
                switch (sub.Function)
                {
                    case SubBlockIds.ConfigBlock:
                        _config ??= Decode.Config(data);
                        break;
                    case SubBlockIds.DecorrTerms:
                        if (Decode.Terms(data, header.IsMono) is { } terms)
                        {
                            _termsBlocks++;
                            _termsMinimum = Math.Min(_termsMinimum, terms.Length);
                            _termsMaximum = Math.Max(_termsMaximum, terms.Length);
                        }

                        break;
                    case SubBlockIds.Int32Info when data.Length == 4:
                        int32SentBits = data[0];
                        _blockZeroBits = data[1];
                        break;
                    case SubBlockIds.FloatInfo when data.Length == 4:
                        floatFlags = data[0];
                        break;
                }

                _hasChecksum |= sub.Function == SubBlockIds.BlockChecksum;
                hasMd5 |= sub.Function is SubBlockIds.Md5Checksum or SubBlockIds.AltMd5Checksum && sub.DataLength == 16;
                position = sub.Next;
            }

            _extraBlocks += hasExtra ? 1 : 0;
            // As the file reader decides it: bits dropped from integers or floats with no extra bitstream to restore them.
            if (header.HasAudio)
            {
                _hasLossyBlocks |= !hasExtra && (((header.Flags & BlockHeader.Int32Flag) != 0 && int32SentBits != 0) || (header.IsFloat && (floatFlags & 0x2E) != 0));
            }

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

        private static string ImplausibleReason(int dataLength, ReadOnlySpan<byte> rebuilt) =>
            dataLength % 2 != 0 ? "a size that is odd"
            : rebuilt[9] != 4 ? $"the major version {rebuilt[9]} where libwavpack takes 4"
            : rebuilt[22] >= 3 || rebuilt[23] != 0 ? "more than 196,607 samples"
            : "a size libwavpack refuses (1 MiB or more)";

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
            // The sub-blocks past the cap are not listed, except the last MD5 and the last extra bitstream, which the decoder's findings point at.
            Node? lastMd5 = null;
            Node? lastExtra = null;
            while (BlockContents.TryRead(rebuilt, position, out SubBlockSpan sub))
            {
                int length = sub.Next - position;
                if (listed < MaxListedSubBlocks)
                {
                    subBlocks.Add(Nodes.SubBlockSummary(packet.OffsetOf(block.DataStart + position - BlockHeader.Length), length, sub.Function, sub.DataLength));
                    listed++;
                }
                else if (sub.Function is SubBlockIds.Md5Checksum or SubBlockIds.AltMd5Checksum or SubBlockIds.WvxBitstream)
                {
                    Node kept = Nodes.SubBlockSummary(packet.OffsetOf(block.DataStart + position - BlockHeader.Length), length, sub.Function, sub.DataLength);
                    if (sub.Function == SubBlockIds.WvxBitstream)
                    {
                        lastExtra = kept;
                    }
                    else if (sub.DataLength == 16)
                    {
                        lastMd5 = kept;
                    }
                }

                position = sub.Next;
            }

            foreach (Node kept in new[] { lastMd5, lastExtra }.OfType<Node>().OrderBy(n => n.Offset))
            {
                subBlocks.Add(kept);
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
            statistics.Add(Stat("Audio Blocks", NodeValue.Integer(_audioBlocks, "blocks", "block"), Explain.StatAudioBlocks));
            statistics.Add(Stat("Samples", NodeValue.Integer(_samples, "samples", "sample"), Explain.StatSamples));
            if (_lost > 0)
            {
                statistics.Add(Stat("Lost Samples", NodeValue.Integer(_lost * (first.IsDsd ? 8 : 1), first.IsDsd ? "bits" : "samples", first.IsDsd ? "bit" : "sample"), StreamInfo.LostSamplesDescription, StreamInfo.LostSamplesTopic));
            }

            statistics.Add(Stat("Duration", NodeValue.Decimal(duration, 3, "s"), Explain.StatDuration));
            statistics.Add(Stat("Sample Rate", NodeValue.Integer(_rate, "Hz"), first.IsDsd ? Explain.StatRateDsd : Explain.StatRate));
            statistics.Add(Stat("Channels", NodeValue.Integer(_channels, "channels", "channel"), Explain.StatChannels, Topics.Channels));
            List<Speaker>? layout = ChannelLayouts.FromWaveMask(_channelMask, _channels);
            if (layout is not null)
            {
                statistics.Add(Stat("Channel Layout", ChannelLayouts.Describe(layout), Explain.StatLayout, Topics.Channels));
            }

            statistics.Add(Stat("Stream Version", NodeValue.Hex(_version, 4), Explain.StreamVersionOfCodecPrivate, Topics.Matroska));
            int shifted = (first.BytesPerSample * 8) - first.LeftShift;
            int? bits = first.IsDsd ? 1 : first.IsFloat ? 32 : shifted > 0 ? shifted : null;
            if (bits is { } sampleBits)
            {
                statistics.Add(Stat("Bits Per Sample", NodeValue.Integer(sampleBits, "bits", "bit"), first.IsDsd ? Explain.StatBitsDsd : Explain.StatBits));
            }

            bool isLossless = !first.IsHybrid && !_hasLossyBlocks;
            statistics.Add(Stat("IsLossless", NodeValue.Boolean(isLossless), Explain.StatIsLossless, Topics.Hybrid));
            statistics.Add(Stat("IsHybrid", NodeValue.Boolean(first.IsHybrid), Explain.StatIsHybrid, Topics.Hybrid));
            bool isCorrectionCreated = _config is { } created && (created.Flags & Decode.ConfigCreateWvc) != 0;
            statistics.Add(Stat("IsCorrectionFileCreated", NodeValue.Boolean(isCorrectionCreated), Explain.StatIsCorrectionFileCreated, Topics.Hybrid));
            statistics.Add(Stat("IsCorrectionFile", NodeValue.Boolean(false), Explain.StatIsCorrectionFile, Topics.Hybrid));
            statistics.Add(Stat("IsFloatingPoint", NodeValue.Boolean(first.IsFloat), Explain.StatIsFloat, Topics.FloatAndLargeIntegers));
            statistics.Add(Stat("IsDsd", NodeValue.Boolean(first.IsDsd), Explain.StatIsDsd, Topics.Dsd));
            if (_config is { } config)
            {
                string mode = (config.Flags & Decode.ConfigVeryHighMode) != 0 ? "very high" : (config.Flags & Decode.ConfigHighMode) != 0 ? "high" : (config.Flags & Decode.ConfigFastMode) != 0 ? "fast" : "normal";
                statistics.Add(Stat("Compression Mode", mode, Explain.StatMode));
                statistics.Add(Stat("Extra Processing Level", config.ExtraLevel is { } level ? NodeValue.Integer(level) : "none", Explain.StatExtraLevel, Topics.Decorrelation));
            }

            if (_termsBlocks > 0)
            {
                statistics.Add(Stat("Decorrelation Passes", _termsMinimum == _termsMaximum ? NodeValue.Integer(_termsMinimum, "passes", "pass") : $"{_termsMinimum} to {_termsMaximum} passes", Explain.StatPasses, Topics.Decorrelation));
            }

            if (!first.IsDsd)
            {
                _wasted.AddTo(statistics, (name, value, description, topic) => Stat(name, value ?? NodeValue.Text(string.Empty), description, topic), Summary.Wording, Topics.WastedBits);
            }

            Node? stereoNode = first.IsDsd ? null : _stereo.AddTo(statistics, (name, value, description, topic) => Stat(name, value ?? NodeValue.Text(string.Empty), description, topic),
                Explain.StatStereoCoding, Topics.MidSideStereo, "Stereo Block", "Stereo Blocks");
            Node? average = null;
            if (_figures is not null)
            {
                int before = statistics.Children.Count;
                _figures.AddTo(statistics, (name, value, description) => new Node(name, _start, _end - _start, value) { Description = description }, "Block Set", "block sets", true, out _);
                average = statistics.Children.Skip(before).FirstOrDefault(c => c.Name == "Average Bitrate");
            }

            bool isFive = _hasNewConfig || _hasChecksum;
            string encoderName = isFive ? "WavPack 5.0 or later" : "WavPack 4 stream, no WavPack 5 features";
            Node encoder = Stat("Encoder", encoderName, Explain.Encoder, ClaimRoles.EncoderTopic);
            encoder.Add(Stat("Confidence", isFive ? "Medium" : "Low", isFive ? Explain.ConfidenceMedium : Explain.ConfidenceLow));
            encoder.Add(Stat("Evidence", isFive
                ? $"The blocks hold {(_hasNewConfig ? "a new config block" : "a block checksum")}, which libwavpack has written since 5.0; libwavpack reports encoder version 5 for a file with one"
                : $"The stream version is 0x{_version:X3} and the blocks hold neither a new config block nor a block checksum, which libwavpack has written since 5.0; libwavpack reports encoder version 4 for such a stream, which other encoders write as well", Explain.Evidence));
            context.Parent.Add(encoder);
            Node? processor = null;
            if (!first.IsDsd && _rate > 0 && _wasted.Pattern(_rate) is { } pattern)
            {
                context.AddEncoderClaim(ProcessorDetection.BehaviorClaim(pattern, context.Parent, Summary.Wording));
                processor = ProcessorDetection.Detect(context.EncoderClaims, (name, value, description) => Stat(name, value ?? NodeValue.Text(string.Empty), description, ClaimRoles.TopicOf(name)));
                if (processor is not null)
                {
                    context.Parent.Add(processor);
                }
            }

            context.Parent.Add(statistics);
            List<Series> series = _figures?.Series(average, context.CancellationToken) ?? [];
            context.AddStream(new StreamInfo
            {
                Nodes = [context.Parent],
                Codec = first.IsHybrid ? "WavPack hybrid" : _hasLossyBlocks ? "WavPack lossy" : "WavPack lossless",
                SampleRate = _rate,
                Channels = _channels,
                ChannelLayout = ChannelLayouts.FromWaveMask(_channelMask, _channels),
                BitsPerSample = first.IsDsd ? 1 : first.IsFloat ? 32 : shifted > 0 ? shifted : null,
                Samples = _samples * (first.IsDsd ? 8 : 1),
                LostSamples = _lost * (first.IsDsd ? 8 : 1),
                Duration = duration,
                AverageBitrate = _figures?.AverageBitrate,
                IsVbr = _figures is { Units: > 0 } figures && figures.MaximumBitrate > figures.MinimumBitrate,
                Encoder = encoderName,
                EncoderConfidence = isFive ? Confidence.Medium : Confidence.Low,
                Processor = ClaimRoles.Resolved(processor).Name,
                ProcessorConfidence = ClaimRoles.Resolved(processor).Confidence,
                Series = series,
                CategorySeries = stereoNode is not null && series.Count > 0 && _stereo.Build(series[0], stereoNode, Explain.StereoCodingSeries, Topics.MidSideStereo, null) is { } stereoSeries ? [stereoSeries] : [],
                Verification = verification,
            });
        }
    }
}
