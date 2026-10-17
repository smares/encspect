using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Musepack;

// Stream version 8 (the SV8 specification; libmpcdec r495 mpc_demux.c, streaminfo.c and
// mpc_bits_reader.c; ffmpeg 6.1 mpc8.c): the magic number "MPCK", then packets of a 16-bit key, a
// size of 1 to 9 bytes that counts the key and itself, and a payload. Packets are followed by their
// sizes up to the stream end packet; a key outside A to Z, which the specification does not allow
// and libmpcdec stops at, is reported and followed by its size too. Audio packets are not read: their
// frames have no length of their own.
internal static class Sv8Parser
{
    private const int MaxSizeBytes = 9;
    private const int MaxStoredPayload = 1 << 20;
    private const int MaxDetailedPackets = 4096;
    private const int MaxSeekEntryNodes = 1000;

    // The seek table entries kept over all tables, so tables that count more than their packets hold
    // or many tables cost no more than this; the stream header, ReplayGain and encoder info packets
    // read past the detailed ones, which the stream info needs, are limited to a few each.
    private const int MaxSeekEntriesKept = 1 << 22;
    private const int MaxLatePackets = 8;

    public static bool Sniff(ByteReader reader)
    {
        var cache = new BlockCache(reader);
        long position = reader.Start + 4;
        if (position == reader.End)
        {
            return true;
        }

        PacketHeader header = ReadHeader(cache, position, reader.End);
        return header.Status != HeaderStatus.Invalid && (header.Status == HeaderStatus.Cut || (IsValidKey(header.Key) && header.Size >= header.HeaderLength));
    }

    // The end of the stream end packet, where the packets reach one; null where they do not, since
    // the stream then says nothing about where it ends.
    public static long? DeclaredEnd(ByteReader reader)
    {
        var cache = new BlockCache(reader);
        long position = reader.Start + 4;
        for (int count = 0; position < reader.End; count++)
        {
            if (count % 4096 == 0)
            {
                reader.CancellationToken.ThrowIfCancellationRequested();
            }

            PacketHeader header = ReadHeader(cache, position, reader.End);
            if (header.Status != HeaderStatus.Ok || header.Size < header.HeaderLength || position + header.Size > reader.End)
            {
                return null;
            }

            position += header.Size;
            if (header.Key == Key("SE"))
            {
                return position;
            }
        }

        return null;
    }

    public static void Parse(ByteReader reader, Node parent, AnalysisContext context, IReadOnlyList<EncoderClaim> claims)
    {
        long start = reader.Start;
        long end = reader.End;
        var cache = new BlockCache(reader);
        var state = new State(start, end, context.CancellationToken);
        state.Leading.Add(new Node("Magic", start, 4, "MPCK") { Description = Explain.Sv8Magic, Topic = Topics.Sv8Packets });

        long position = start + 4;
        bool ended = false;
        int damaged = 0;
        for (long followed = 0; position < end; followed++)
        {
            if (followed % 4096 == 0)
            {
                state.Cancellation.ThrowIfCancellationRequested();
            }

            PacketHeader header = ReadHeader(cache, position, end);
            if (header.Status == HeaderStatus.Cut)
            {
                state.Unreadable = (position, "musepack.packet-truncated", $"The data ends inside the key or size of a packet at 0x{position:X}.");
                break;
            }

            if (header.Status == HeaderStatus.Invalid || header.Size < header.HeaderLength)
            {
                string why = header.Status == HeaderStatus.Invalid
                    ? $"its size field runs past {MaxSizeBytes} bytes"
                    : $"its size, {header.Size} bytes, is smaller than its key and size field";
                state.Unreadable = (position, "musepack.packet-size-invalid", $"The packet at 0x{position:X} cannot be followed: {why}. The rest of the data is not read.");
                break;
            }

            long length = Math.Min(header.Size, end - position);
            var packet = new Packet(position, length, header.Size, header.Key, (byte)header.HeaderLength);
            int index = state.Packets.Count;

            // Past the packets the analyzer details, a run of packets that do not carry audio is kept as
            // one record and not read, whatever their key, so packets of 3 bytes cost nothing each.
            bool folded = IsFolded(state, packet);
            if (folded)
            {
                if (index > 0 && state.Packets[index - 1] is { IsRun: true } run && run.Offset + run.Length == position)
                {
                    index--;
                    state.Packets.SetLast(run.Joined(packet));
                }
                else
                {
                    state.Packets.Add(packet.AsRun());
                }
            }
            else
            {
                state.Packets.Add(packet);
            }

            Handle(state, cache, index, packet, folded);
            position += length;

            // An audio packet holds frames and libmpcdec reads at least one bit of each (the number of
            // used bands), so no encoder writes one with fewer bits than frames; after a run of them,
            // as after a run of SV7 frames whose subband resolutions run past their length, the
            // packets are not followed further.
            if (header.Key == Key("AP") && (packet.Length - packet.HeaderLength) * 8 < (state.Header?.FramesPerPacket ?? 64))
            {
                damaged++;
            }
            else if (header.Key == Key("AP"))
            {
                damaged = 0;
            }

            if (damaged == Sv7Parser.MaxDamagedRun && position < end)
            {
                state.Unreadable = (position, "musepack.packets-damaged",
                    $"{Sv7Parser.MaxDamagedRun} audio packets in a row up to 0x{position:X} are too short to hold one bit for each of their frames, which no encoder writes: the data is damaged, and the rest of it is not read.");
                break;
            }

            if (header.Key == Key("SE"))
            {
                ended = true;
                break;
            }
        }

        if (state.Unreadable is { } unreadable)
        {
            state.Trailing = (unreadable.Offset, end);
            state.Findings.AddNow(Severity.Warning, unreadable.Code, unreadable.Message, GapNode(unreadable.Offset, end));
        }
        else if (position < end)
        {
            state.Trailing = (position, end);
            state.Findings.AddNow(Severity.Warning, "musepack.data-after-end",
                $"{end - position} bytes after the stream end packet, from 0x{position:X}, are not part of the stream; the SV8 specification allows only tags after it, and no tag was found there.",
                GapNode(position, end));
        }

        int count = state.Packets.Count + (state.Trailing is null ? 0 : 1);
        var node = new Node("Musepack SV8", start, end - start, null, () => new BlockedChildren(state.Leading, count, i => PacketNode(state, i)))
        {
            Kind = NodeKind.Stream,
            Description = Explain.Sv8Stream,
            Topic = Topics.Sv8Packets,
        };
        parent.Add(node);
        Check(state, ended);
        AddStream(state, node, context, claims);
        state.Findings.Flush(context);
    }

    private enum HeaderStatus
    {
        Ok,
        Cut,
        Invalid,
    }

    private readonly record struct PacketHeader(ushort Key, int HeaderLength, long Size, HeaderStatus Status);

    private static ushort Key(string key) => (ushort)((key[0] << 8) | key[1]);

    private static bool IsValidKey(ushort key) => (key >> 8) is >= 'A' and <= 'Z' && (key & 0xFF) is >= 'A' and <= 'Z';

    private static string KeyText(ushort key) =>
        (key >> 8) is >= 0x20 and < 0x7F && (key & 0xFF) is >= 0x20 and < 0x7F ? $"{(char)(key >> 8)}{(char)(key & 0xFF)}" : $"0x{key:X4}";

    private static PacketHeader ReadHeader(BlockCache cache, long position, long end)
    {
        if (position + 3 > end)
        {
            return new PacketHeader(0, 0, 0, HeaderStatus.Cut);
        }

        ushort key = (ushort)((cache.ByteAt(position) << 8) | cache.ByteAt(position + 1));
        long size = 0;
        for (int n = 0; n < MaxSizeBytes; n++)
        {
            if (position + 2 + n >= end)
            {
                return new PacketHeader(key, 0, 0, HeaderStatus.Cut);
            }

            int b = cache.ByteAt(position + 2 + n);
            size = (size << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
            {
                return new PacketHeader(key, 3 + n, size, HeaderStatus.Ok);
            }
        }

        return new PacketHeader(key, 0, 0, HeaderStatus.Invalid);
    }

    // A size field inside a payload (SV8 specification, Packet formatting); false where it runs past
    // the bytes or past 9 bytes.
    private static bool ReadSize(byte[] data, ref int position, out long value)
    {
        value = 0;
        for (int n = 0; n < MaxSizeBytes; n++)
        {
            if (position >= data.Length)
            {
                return false;
            }

            int b = data[position++];
            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void Handle(State state, BlockCache cache, int index, Packet packet, bool folded)
    {
        if (packet.Key == Key("AP"))
        {
            state.AudioPackets.Add(index);
            if (state.Header is null)
            {
                AudioBeforeHeader(state, index, packet);
            }

            if (packet.IsTruncated)
            {
                state.Findings.AddNow(Severity.Warning, "musepack.packet-truncated",
                    $"The audio packet at 0x{packet.Offset:X} declares {packet.Size} bytes; the data holds {packet.Length}: the stream is cut short.", PacketShell(state, index));
            }

            return;
        }

        if (packet.IsTruncated)
        {
            state.Findings.AddNow(Severity.Warning, "musepack.packet-truncated",
                $"The {KeyText(packet.Key)} packet at 0x{packet.Offset:X} declares {packet.Size} bytes; the data holds {packet.Length}.", PacketShell(state, index));
        }

        if (!IsValidKey(packet.Key))
        {
            ReportKey(state, index, packet, "musepack.key-invalid");
        }
        else if (!IsReserved(packet.Key))
        {
            ReportKey(state, index, packet, "musepack.key-unknown");
        }

        if (folded)
        {
            Undetailed(state, index, packet);
            return;
        }

        long payloadStart = packet.Offset + packet.HeaderLength;
        var payload = new byte[(int)Math.Min(packet.Length - packet.HeaderLength, MaxStoredPayload)];
        cache.Read(payloadStart, payload);
        Node node = PacketShell(state, index);
        var fields = new Fields(node, payload, payloadStart);
        string key = KeyText(packet.Key);
        switch (key)
        {
            case "SH":
                StreamHeader(state, fields, index, packet);
                break;
            case "RG":
                ReplayGain(state, fields);
                break;
            case "EI":
                EncoderInfo(state, fields);
                break;
            case "SO":
                SeekTableOffset(state, fields, index, packet);
                break;
            case "ST":
                SeekTable(state, fields, index, packet);
                break;
            case "CT":
                Chapter(state, fields);
                break;
            case "SE":
                if (packet.Length != 3 || packet.Size != 3)
                {
                    state.Findings.AddNow(Severity.Warning, "musepack.end-size",
                        $"The stream end packet at 0x{packet.Offset:X} is {packet.Size} bytes long; the SV8 specification requires 3.", WithoutChildren(node));
                }

                break;
            default:
                if (payload.Length > 0)
                {
                    node.Add(new Node("Payload", payloadStart, packet.Length - packet.HeaderLength, NodeValue.Integer(packet.Length - packet.HeaderLength, "bytes"))
                    {
                        Description = Explain.Payload,
                    });
                }

                break;
        }

        if (state.AudioPackets.Count > 0 && key is "RG" or "SO")
        {
            PacketAfterAudio(state, index, key, packet);
        }

        state.Detailed[index] = node;
    }

    // Whether the packet is left out of the detailed ones: past MaxDetailedPackets of them, every
    // packet that does not carry audio, whatever its key, joins a run. The stream end packet ends the
    // walk, and the first few of the packets the stream info needs are still read.
    private static bool IsFolded(State state, Packet packet)
    {
        if (packet.Key == Key("AP") || packet.Key == Key("SE") || packet.IsTruncated || state.Detailed.Count < MaxDetailedPackets)
        {
            return false;
        }

        int slot = packet.Key == Key("SH") ? 0 : packet.Key == Key("RG") ? 1 : packet.Key == Key("EI") ? 2 : -1;
        if (slot >= 0 && state.LatePackets[slot] < MaxLatePackets)
        {
            state.LatePackets[slot]++;
            return false;
        }

        return true;
    }

    // What a packet in a run still contributes: the facts the checks on the whole stream need, which
    // need no fields. Its fields are not read.
    private static void Undetailed(State state, int index, Packet packet)
    {
        string key = KeyText(packet.Key);
        if (key == "SH" && state.Header is not null)
        {
            RepeatedHeader(state, index, packet);
        }
        else if (key == "RG")
        {
            state.HasReplayGain = true;
        }

        if (state.AudioPackets.Count > 0 && key is "RG" or "SO")
        {
            PacketAfterAudio(state, index, key, packet);
        }
    }

    private static void RepeatedHeader(State state, int index, Packet packet)
    {
        if (!state.Findings.Repeat("musepack.stream-header-repeated"))
        {
            AddRepeatedHeader(state, index, packet);
        }
    }

    private static void AddRepeatedHeader(State state, int index, Packet packet) =>
        state.Findings.Add(Severity.Info, "musepack.stream-header-repeated", () => WithoutChildren(PacketShell(state, index)), count =>
            $"The stream holds {count + 1} stream header packets, the second at 0x{packet.Offset:X}; the analyzer reads the first. The SV8 specification uses repeated headers for streaming.");

    // The findings repeated over many packets count in a method with no lambda: one that captures
    // its parameters allocates its closure on entry, before any check, so every case after the
    // first would still cost one. The first case builds its delegates in the Add methods.
    private static void ReportKey(State state, int index, Packet packet, string code)
    {
        if (!state.Findings.Repeat(code))
        {
            AddKey(state, index, packet, code);
        }
    }

    private static void AddKey(State state, int index, Packet packet, string code)
    {
        if (code == "musepack.key-invalid")
        {
            state.Findings.Add(Severity.Warning, code, () => PacketShell(state, index), count =>
                $"{(count == 1 ? "A packet's key" : $"{count} packets' keys")}, first {KeyText(packet.Key)} at 0x{packet.Offset:X}, hold characters outside A to Z, which the SV8 specification does not allow; libmpcdec stops decoding at such a key.");
        }
        else
        {
            state.Findings.Add(Severity.Info, code, () => PacketShell(state, index), count =>
                $"{(count == 1 ? "A packet has a key" : $"{count} packets have keys")} the SV8 specification does not reserve, first {KeyText(packet.Key)} at 0x{packet.Offset:X}; libmpcdec and ffmpeg skip packets they do not know.");
        }
    }

    private static void AudioBeforeHeader(State state, int index, Packet packet)
    {
        if (!state.Findings.Repeat("musepack.audio-before-header"))
        {
            AddAudioBeforeHeader(state, index, packet);
        }
    }

    private static void AddAudioBeforeHeader(State state, int index, Packet packet) =>
        state.Findings.Add(Severity.Warning, "musepack.audio-before-header", () => PacketShell(state, index),
            _ => $"An audio packet at 0x{packet.Offset:X} comes before any stream header packet, which the SV8 specification requires before the first audio packet.");

    private static void PacketAfterAudio(State state, int index, string key, Packet packet)
    {
        if (!state.Findings.Repeat("musepack.packet-after-audio"))
        {
            AddPacketAfterAudio(state, index, key, packet);
        }
    }

    private static void AddPacketAfterAudio(State state, int index, string key, Packet packet) =>
        state.Findings.Add(Severity.Warning, "musepack.packet-after-audio", () => WithoutChildren(PacketShell(state, index)), count =>
            count == 1
                ? $"The {key} packet at 0x{packet.Offset:X} comes after the first audio packet; the SV8 specification requires it before."
                : $"{count} packets come after the first audio packet, the first the {key} packet at 0x{packet.Offset:X}; the SV8 specification requires them before.");

    private static bool DescribesStream(ushort key) =>
        key == Key("SH") || key == Key("RG") || key == Key("EI") || key == Key("SO") || key == Key("ST") || key == Key("SE");

    private static bool IsReserved(ushort key) => DescribesStream(key) || key == Key("AP") || key == Key("CT");

    // The packet's node with its key and size, before its fields are added.
    private static Node PacketShell(State state, int index)
    {
        if (state.Detailed.TryGetValue(index, out Node? built))
        {
            return built;
        }

        Packet packet = state.Packets[index];
        if (packet.IsRun)
        {
            return RunNode(packet);
        }

        (string name, string description) = NameOf(packet.Key);
        bool isAudio = packet.Key == Key("AP");
        var node = new Node(name, packet.Offset, packet.Length, isAudio ? AudioSummary(state, index) : KeyText(packet.Key))
        {
            Kind = isAudio ? NodeKind.Frame : NodeKind.Detail,
            Description = description,
            Topic = Topics.Sv8Packets,
        };
        node.Add(new Node("Key", packet.Offset, 2, KeyText(packet.Key)) { Description = Explain.Key });
        if (packet.Length > 2)
        {
            node.Add(new Node("Size", packet.Offset + 2, Math.Min(packet.HeaderLength, packet.Length) - 2, NodeValue.Integer(packet.Size, "bytes"))
            {
                Description = Explain.Size,
            });
        }

        if (isAudio && packet.Length > packet.HeaderLength)
        {
            node.Add(new Node("Audio Frames", packet.Offset + packet.HeaderLength, packet.Length - packet.HeaderLength, NodeValue.Integer(state.FramesIn(index), "frames", "frame"))
            {
                Description = Explain.AudioFrames,
                Topic = Topics.SubbandCoding,
            });
        }

        if (!isAudio)
        {
            state.Detailed[index] = node;
        }

        return node;
    }

    private static (string Name, string Description) NameOf(ushort key) => KeyText(key) switch
    {
        "SH" => ("Stream Header", Explain.StreamHeader),
        "RG" => ("ReplayGain", Explain.ReplayGainPacket),
        "EI" => ("Encoder Info", Explain.EncoderInfo),
        "SO" => ("Seek Table Offset", Explain.SeekTableOffset),
        "ST" => ("Seek Table", Explain.SeekTable),
        "AP" => ("Audio Packet", Explain.AudioPacket),
        "CT" => ("Chapter", Explain.Chapter),
        "SE" => ("Stream End", Explain.StreamEnd),
        _ => ("Packet", Explain.UnknownPacket),
    };

    private static string AudioSummary(State state, int index)
    {
        Packet packet = state.Packets[index];
        long frames = state.FramesIn(index);
        return $"{frames} {(frames == 1 ? "frame" : "frames")}, {packet.Length} bytes";
    }

    private static Node PacketNode(State state, int index)
    {
        if (index >= state.Packets.Count)
        {
            (long from, long to) = state.Trailing!.Value;
            return GapNode(from, to);
        }

        if (state.Detailed.TryGetValue(index, out Node? node))
        {
            return node;
        }

        Packet packet = state.Packets[index];
        if (packet.IsRun)
        {
            return RunNode(packet);
        }

        (string name, string description) = NameOf(packet.Key);
        return new Node(name, packet.Offset, packet.Length, packet.Key == Key("AP") ? AudioSummary(state, index) : KeyText(packet.Key),
            () => PacketShellUncached(state, index).Children)
        {
            Kind = packet.Key == Key("AP") ? NodeKind.Frame : NodeKind.Detail,
            Description = description,
            Topic = Topics.Sv8Packets,
        };
    }

    private static Node RunNode(Packet run) =>
        new("Undetailed Packets", run.Offset, run.Length, $"{run.Count} packets, {(run.IsMixed ? "several keys" : $"key {KeyText(run.Key)}")}")
        {
            Kind = NodeKind.Detail,
            Description = Explain.UndetailedPackets,
            Topic = Topics.Sv8Packets,
        };

    // As PacketShell, for a packet whose node is built when read and must not be kept.
    private static Node PacketShellUncached(State state, int index)
    {
        Packet packet = state.Packets[index];
        bool isAudio = packet.Key == Key("AP");
        var node = new Node("Packet", packet.Offset, packet.Length);
        node.Add(new Node("Key", packet.Offset, 2, KeyText(packet.Key)) { Description = Explain.Key });
        if (packet.Length > 2)
        {
            node.Add(new Node("Size", packet.Offset + 2, Math.Min(packet.HeaderLength, packet.Length) - 2, NodeValue.Integer(packet.Size, "bytes"))
            {
                Description = Explain.Size,
            });
        }

        if (packet.Length > packet.HeaderLength)
        {
            node.Add(isAudio
                ? new Node("Audio Frames", packet.Offset + packet.HeaderLength, packet.Length - packet.HeaderLength, NodeValue.Integer(state.FramesIn(index), "frames", "frame"))
                {
                    Description = Explain.AudioFrames,
                    Topic = Topics.SubbandCoding,
                }
                : new Node("Payload", packet.Offset + packet.HeaderLength, packet.Length - packet.HeaderLength, NodeValue.Integer(packet.Length - packet.HeaderLength, "bytes"))
                {
                    Description = Explain.Payload,
                });
        }

        return node;
    }

    private static Node GapNode(long start, long end) =>
        new("Gap", start, end - start, NodeValue.Integer(end - start, "bytes")) { Description = Explain.Gap };

    private static Node WithoutChildren(Node node) =>
        new(node.Name, node.Offset, node.Length, node.Value) { Kind = node.Kind, Description = node.Description, Ranges = node.Ranges };

    private static void StreamHeader(State state, Fields f, int index, Packet packet)
    {
        if (state.Header is not null)
        {
            RepeatedHeader(state, index, packet);
            return;
        }

        if (f.Data.Length < 4)
        {
            Truncated(state, f, "SH");
            return;
        }

        uint stored = BinaryPrimitives.ReadUInt32BigEndian(f.Data);
        Node crc = f.Bytes("CRC", 0, 4, NodeValue.Hex(stored, 8), Explain.HeaderCrc, Topics.Crc);
        bool whole = !packet.IsTruncated && f.Data.Length == packet.Length - packet.HeaderLength;
        uint computed = Crc32.Compute(f.Data.AsSpan(4));
        crc.Add(new Node("Check", crc.Offset, crc.Length, !whole ? "not checked: the packet is cut short" : computed == stored ? "verified" : $"mismatch: computed 0x{computed:X8}")
        {
            Description = Explain.CrcCheck,
        });
        if (whole && computed != stored)
        {
            state.Findings.AddNow(Severity.Warning, "musepack.header-crc-mismatch",
                $"The stream header's CRC is 0x{stored:X8}, but its bytes give 0x{computed:X8}: the header is damaged, and libmpcdec refuses the stream.", WithoutChildren(crc));
        }
        else if (stored == 0)
        {
            state.Findings.AddNow(Severity.Warning, "musepack.header-crc-zero", "The stream header's CRC is 0, which the SV8 specification calls invalid, and ffmpeg does not recognize the stream.", WithoutChildren(crc));
        }

        var header = new StreamHeaderInfo { CrcVerified = whole && computed == stored };
        int position = 4;
        if (position >= f.Data.Length)
        {
            Truncated(state, f, "SH");
            state.Header = header;
            return;
        }

        header.Version = f.Data[position];
        Node version = f.Bytes("Stream Version", position, 1, NodeValue.Integer(header.Version), Explain.Sv8StreamVersion);
        position++;
        if (header.Version != 8)
        {
            state.Findings.AddNow(Severity.Warning, "musepack.version-unknown",
                $"The stream header's version is {header.Version}; libmpcdec and ffmpeg decode only version 8.", WithoutChildren(version));
        }

        if (!f.Size("Sample Count", ref position, "samples", "sample", Explain.SampleCount, out long samples) ||
            !f.Size("Beginning Silence", ref position, "samples", "sample", Explain.BeginningSilence, out long silence) ||
            position + 2 > f.Data.Length)
        {
            Truncated(state, f, "SH");
            state.Header = header;
            return;
        }

        header.Samples = samples;
        header.Silence = silence;
        state.SampleCountField = WithoutChildren(f.Node.Children.First(c => c.Name == "Sample Count"));
        int first = f.Data[position];
        int second = f.Data[position + 1];
        int rateIndex = first >> 5;
        int channelCount = (second >> 4) + 1;
        header.RateIndex = rateIndex;
        header.MaxBands = (first & 0x1F) + 1;
        header.Channels = channelCount;
        header.MidSide = (second & 0x08) != 0;
        header.BlockPower = second & 0x07;
        long at = f.Offset + position;
        Node rate = f.Node.Add(Node.FromBits("Sample Rate", at, 0, 3,
            rateIndex < 4 ? NodeValue.Integer(Sv7Parser.SampleRates[rateIndex], "Hz") : $"reserved ({rateIndex})", NodeKind.Detail, Explain.SampleRateIndex));
        Node bands = f.Node.Add(Node.FromBits("Max Used Bands", at, 3, 8, NodeValue.Integer(header.MaxBands), NodeKind.Detail, Explain.MaxUsedBands, Topics.SubbandCoding));
        Node channels = f.Node.Add(Node.FromBits("Channels", at + 1, 0, 4, NodeValue.Integer(channelCount), NodeKind.Detail, Explain.ChannelCount));
        f.Node.Add(Node.FromBits("UsesMidSideStereo", at + 1, 4, 5, NodeValue.Boolean(header.MidSide), NodeKind.Detail, Explain.Sv8MidSide, Topics.MidSideStereo));
        f.Node.Add(Node.FromBits("Audio Block Frames", at + 1, 5, 8, NodeValue.Integer(header.FramesPerPacket, "frames", "frame"), NodeKind.Detail, Explain.AudioBlockFrames));
        position += 2;
        if (rateIndex >= 4)
        {
            state.Findings.AddNow(Severity.Warning, "musepack.sample-rate-reserved",
                $"The stream header's sample rate index is {rateIndex}, which the SV8 specification does not define; libmpcdec refuses the stream.", rate);
        }

        // libmpcdec r495 check_streaminfo: max_band >= 32 fails, and SV8 stores the bands less 1.
        if (header.MaxBands == 32)
        {
            state.Findings.AddNow(Severity.Warning, "musepack.max-band-invalid",
                "The stream header gives 32 used bands, which the SV8 specification allows but libmpcdec refuses.", bands);
        }

        if (channelCount > 2)
        {
            state.Findings.AddNow(Severity.Warning, "musepack.channels-unsupported",
                $"The stream header gives {channelCount} channels; libmpcdec decodes at most 2 and refuses the stream.", channels);
        }

        f.Padding(state, position, "SH");
        state.Header = header;
    }

    private static void ReplayGain(State state, Fields f)
    {
        state.HasReplayGain = true;
        if (f.Data.Length < 1)
        {
            Truncated(state, f, "RG");
            return;
        }

        int version = f.Data[0];
        Node versionNode = f.Bytes("Version", 0, 1, NodeValue.Integer(version), Explain.ReplayGainVersion, Topics.ReplayGain);
        if (version != 1)
        {
            state.Findings.Add(Severity.Info, "musepack.replaygain-version", () => WithoutChildren(versionNode), count => count == 1
                ? $"The ReplayGain packet's version is {version}; libmpcdec reads only version 1 and ignores the values."
                : $"{count} ReplayGain packets have a version other than 1, the first {version}; libmpcdec reads only version 1 and ignores the values.");
            return;
        }

        if (f.Data.Length < 9)
        {
            Truncated(state, f, "RG");
            return;
        }

        (string Name, string Description)[] fields =
        [
            ("Title Gain", Explain.TitleGain), ("Title Peak", Explain.TitlePeak), ("Album Gain", Explain.AlbumGain), ("Album Peak", Explain.AlbumPeak),
        ];
        var values = new int[4];
        for (int i = 0; i < 4; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt16BigEndian(f.Data.AsSpan(1 + (2 * i)));
            Node node = f.Bytes(fields[i].Name, 1 + (2 * i), 2, NodeValue.Decimal(values[i] / 256.0, 2, "dB"), fields[i].Description, Topics.ReplayGain);
            if (i % 2 == 0 && values[i] != 0)
            {
                node.Add(new Node("Player Gain", node.Offset, node.Length, NodeValue.Decimal(64.82 - (values[i] / 256.0), 2, "dB", showsSign: true))
                {
                    Description = Explain.PlayerGain,
                });
            }
        }

        state.ReplayGain = values;
        f.Padding(state, 9, "RG");
    }

    private static void EncoderInfo(State state, Fields f)
    {
        if (f.Data.Length < 4)
        {
            Truncated(state, f, "EI");
            return;
        }

        double profile = (f.Data[0] >> 1) / 8.0;
        Node node = f.Node.Add(Node.FromBits("Profile", f.Offset, 0, 7, NodeValue.Decimal(profile, 3), NodeKind.Detail, Explain.Sv8Profile, Topics.SubbandCoding));
        node.Add(Node.FromBits("Name", f.Offset, 0, 7, Sv7Parser.ProfileName((int)profile), NodeKind.Detail, Explain.ProfileName));
        node.Add(Node.FromBits("Quality", f.Offset, 0, 7, NodeValue.Decimal(profile - 5, 3), NodeKind.Detail, Explain.Quality));
        f.Node.Add(Node.FromBits("UsesNoiseSubstitution", f.Offset, 7, 8, NodeValue.Boolean((f.Data[0] & 1) != 0), NodeKind.Detail, Explain.Pns, Topics.NoiseSubstitution));
        int major = f.Data[1];
        int minor = f.Data[2];
        int build = f.Data[3];
        Node version = f.Bytes("Encoder Version", 1, 3, $"{major}.{minor}.{build}", Explain.Sv8EncoderVersion, Topics.EncoderDetection);
        version.Add(new Node("Major", f.Offset + 1, 1, NodeValue.Integer(major)) { Description = Explain.VersionMajor });
        version.Add(new Node("Minor", f.Offset + 2, 1, NodeValue.Integer(minor)) { Description = Explain.VersionMinor });
        version.Add(new Node("Build", f.Offset + 3, 1, NodeValue.Integer(build)) { Description = Explain.VersionBuild });
        version.Add(new Node("Name", f.Offset + 1, 3, MusepackEncoder.Sv8Name(major, minor, build)) { Description = Explain.VersionName });
        state.EncoderVersion ??= ((major, minor, build), WithoutChildren(version));
        state.Profile ??= profile;
        f.Padding(state, 4, "EI");
    }

    private static void SeekTableOffset(State state, Fields f, int index, Packet packet)
    {
        int position = 0;
        if (!f.Size("Offset", ref position, "bytes", "byte", Explain.Offset, out long offset))
        {
            Truncated(state, f, "SO");
            return;
        }

        state.SeekOffsets.Add((index, packet.Offset + offset, f.Node.Children.First(c => c.Name == "Offset")));
        f.Padding(state, position, "SO");
    }

    private static void SeekTable(State state, Fields f, int index, Packet packet)
    {
        int position = 0;
        if (!f.Size("Seek Count", ref position, "entries", "entry", Explain.SeekCount, out long count) || position >= f.Data.Length)
        {
            Truncated(state, f, "ST");
            return;
        }

        int distance = f.Data[position] >> 4;
        f.Node.Add(Node.FromBits("Seek Distance", f.Offset + position, 0, 4, NodeValue.Integer(1L << distance, "audio packets", "audio packet"), NodeKind.Detail, Explain.SeekDistance));
        var bits = new BitReader(f.Data.AsMemory(position));
        bits.Read(4);
        int baseBit = position * 8;
        long limit = f.Data.Length * 8L;
        var entries = new List<long>();
        var starts = new List<(int Start, int End)>();
        bool broken = false;
        long decoded = 0;
        long previous = 0;
        long beforePrevious = 0;
        for (long i = 0; i < count && !broken; i++)
        {
            if (i % 4096 == 0)
            {
                state.Cancellation.ThrowIfCancellationRequested();
            }

            int from = baseBit + bits.Position;
            long value;
            if (i < 2)
            {
                value = 0;
                broken = true;
                for (int n = 0; n < MaxSizeBytes && baseBit + bits.Position + 8 <= limit; n++)
                {
                    int b = bits.Read(8);
                    value = (value << 7) | (uint)(b & 0x7F);
                    if ((b & 0x80) == 0)
                    {
                        broken = false;
                        break;
                    }
                }
            }
            else
            {
                // A Golomb code with M = 2^12: zero bits up to a one, then 12 bits (libmpcdec r495
                // mpc_bits_golomb_dec); its low bit is the sign of the change of step, in bytes times 2.
                long zeros = 0;
                while (baseBit + bits.Position < limit && bits.Read(1) == 0)
                {
                    zeros++;
                }

                if (baseBit + bits.Position + 12 > limit || zeros > 1 << 20)
                {
                    broken = true;
                    break;
                }

                long code = (zeros << 12) | (uint)bits.Read(12);
                if ((code & 1) != 0)
                {
                    code = -(code & ~1L);
                }

                value = (code >> 1) + (2 * previous) - beforePrevious;
            }

            if (broken)
            {
                break;
            }

            beforePrevious = previous;
            previous = value;
            decoded++;
            if (state.SeekEntriesKept < MaxSeekEntriesKept)
            {
                entries.Add(value);
                state.SeekEntriesKept++;
            }

            if (starts.Count < MaxSeekEntryNodes)
            {
                starts.Add((from, baseBit + bits.Position));
            }
        }

        var list = new Node("Entries", f.Offset + position, f.Data.Length - position, NodeValue.Integer(decoded, "entries", "entry"))
        {
            Description = Explain.SeekEntries,
        };
        for (int i = 0; i < starts.Count && i < entries.Count; i++)
        {
            list.Add(Node.FromBits("Entry", f.Offset, starts[i].Start, starts[i].End, NodeValue.Integer(entries[i], "bytes"), NodeKind.Detail, Explain.SeekEntry));
        }

        f.Node.Add(list);
        if (broken || decoded < count)
        {
            state.Findings.Add(Severity.Warning, "musepack.seek-table-truncated", () => WithoutChildren(f.Node), times => times == 1
                ? $"The seek table at 0x{packet.Offset:X} counts {count} entries, but its packet holds only {decoded}."
                : $"{times} seek tables count more entries than their packets hold, the first at 0x{packet.Offset:X} with {count} counted and {decoded} held.");
        }

        state.SeekTables.Add((index, distance, entries, decoded, WithoutChildren(f.Node)));
    }

    private static void Chapter(State state, Fields f)
    {
        int position = 0;
        if (!f.Size("Sample Offset", ref position, "samples", "sample", Explain.ChapterSample, out _) || position + 4 > f.Data.Length)
        {
            Truncated(state, f, "CT");
            return;
        }

        f.Bytes("Chapter Gain", position, 2, NodeValue.Decimal(BinaryPrimitives.ReadUInt16BigEndian(f.Data.AsSpan(position)) / 256.0, 2, "dB"), Explain.ChapterGain, Topics.ReplayGain);
        f.Bytes("Chapter Peak", position + 2, 2, NodeValue.Decimal(BinaryPrimitives.ReadUInt16BigEndian(f.Data.AsSpan(position + 2)) / 256.0, 2, "dB"), Explain.ChapterPeak, Topics.ReplayGain);
        position += 4;
        if (position >= f.Data.Length)
        {
            return;
        }

        Node tag = f.Bytes("Chapter Tag", position, f.Data.Length - position, null, Explain.ChapterTag, Topics.ApeTag);
        if (f.Data.Length - position < 24)
        {
            return;
        }

        long at = f.Offset + position;
        ReadOnlySpan<byte> h = f.Data.AsSpan(position, 24);
        uint items = BinaryPrimitives.ReadUInt32LittleEndian(h[8..]);
        tag.Add(new Node("Version", at, 4, NodeValue.Integer(BinaryPrimitives.ReadUInt32LittleEndian(h))) { Description = Explain.ChapterTagField });
        tag.Add(new Node("Tag Size", at + 4, 4, NodeValue.Integer(BinaryPrimitives.ReadUInt32LittleEndian(h[4..]), "bytes")) { Description = Explain.ChapterTagField });
        tag.Add(new Node("Item Count", at + 8, 4, NodeValue.Integer(items, "items", "item")) { Description = Explain.ChapterTagField });
        tag.Add(new Node("Flags", at + 12, 4, NodeValue.Hex(BinaryPrimitives.ReadUInt32LittleEndian(h[12..]), 8)) { Description = Explain.ChapterTagField });
        tag.Add(new Node("Reserved", at + 16, 8, NodeValue.Integer(8, "bytes")) { Description = Explain.ChapterTagField });
        int item = position + 24;
        for (uint i = 0; i < items && item + 8 < f.Data.Length; i++)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(f.Data.AsSpan(item));
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(f.Data.AsSpan(item + 4));
            int keyEnd = Array.IndexOf(f.Data, (byte)0, item + 8);
            if (keyEnd < 0 || keyEnd + 1 + (long)size > f.Data.Length)
            {
                break;
            }

            string key = Encoding.ASCII.GetString(f.Data, item + 8, keyEnd - item - 8);
            string value = (flags & 6) == 0 ? Encoding.UTF8.GetString(f.Data, keyEnd + 1, (int)size) : $"{size} bytes";
            int itemEnd = keyEnd + 1 + (int)size;
            tag.Add(new Node("Item", f.Offset + item, itemEnd - item, $"{key}: {value}") { Description = Explain.ChapterItem });
            item = itemEnd;
        }
    }

    private static void Truncated(State state, Fields f, string key) =>
        state.Findings.Add(Severity.Warning, "musepack.packet-fields-truncated", () => WithoutChildren(f.Node), count => count == 1
            ? $"The {key} packet at 0x{f.Node.Offset:X} ends inside its fields."
            : $"{count} packets end inside their fields, the first the {key} packet at 0x{f.Node.Offset:X}.");

    // What the packets say together: the mandatory packets, the seek table against the audio
    // packets, and the audio packets against the stream header's sample count.
    private static void Check(State state, bool ended)
    {
        if (!ended && state.Unreadable is null)
        {
            Node? last = state.Packets.Count > 0 ? WithoutChildren(PacketNode(state, state.Packets.Count - 1)) : null;
            state.Findings.AddNow(Severity.Warning, "musepack.end-missing",
                "The stream has no stream end packet, which the SV8 specification requires as its last packet; it may be cut short.", last ?? state.Leading[0]);
        }
        else if (!ended)
        {
            state.Findings.AddNow(Severity.Warning, "musepack.end-missing",
                "The stream has no stream end packet before the data that cannot be read.", state.Leading[0]);
        }

        if (state.Header is null)
        {
            state.Findings.AddNow(Severity.Warning, "musepack.header-missing",
                "The stream has no stream header packet, which the SV8 specification requires; decoders cannot decode it.", state.Leading[0]);
        }

        if (!state.HasReplayGain)
        {
            state.Findings.AddNow(Severity.Warning, "musepack.replaygain-missing",
                "The stream has no ReplayGain packet, which the SV8 specification marks as mandatory.", state.Leading[0]);
        }

        var seekTables = new HashSet<long>();
        foreach (var table in state.SeekTables)
        {
            seekTables.Add(state.Packets[table.Index].Offset);
        }

        int checkedTables = 0;
        foreach ((int index, long target, Node field) in state.SeekOffsets)
        {
            if (checkedTables++ % 4096 == 0)
            {
                state.Cancellation.ThrowIfCancellationRequested();
            }

            // A target inside a run of packets that were not read may be a seek table packet.
            if (!seekTables.Contains(target) && !InRun(state, target))
            {
                long at = state.Packets[index].Offset;
                state.Findings.Add(Severity.Warning, "musepack.seek-offset-invalid", field, count => count == 1
                    ? $"The seek table offset at 0x{at:X} points to 0x{target:X}, where no seek table packet starts; decoders seek without it."
                    : $"{count} seek table offsets point where no seek table packet starts, the first the one at 0x{at:X} to 0x{target:X}; decoders seek without them.");
            }
        }

        foreach ((_, int distance, List<long> entries, long decoded, Node node) in state.SeekTables)
        {
            long step = 1L << distance;
            long needed = (state.AudioPackets.Count + step - 1) / step;
            int wrong = 0;
            string? first = null;
            for (int i = 0; i < entries.Count; i++)
            {
                if (i % 4096 == 0)
                {
                    state.Cancellation.ThrowIfCancellationRequested();
                }

                long audio = i * step;
                long? actual = audio < state.AudioPackets.Count ? state.Packets[state.AudioPackets[(int)audio]].Offset - state.Start : null;
                if (actual != entries[i])
                {
                    wrong++;
                    first ??= actual is { } offset
                        ? $"entry {i} gives 0x{entries[i]:X} from the magic number, where audio packet {audio} starts at 0x{offset:X}"
                        : $"entry {i} stands for audio packet {audio}, which the stream does not have";
                }
            }

            if (wrong > 0)
            {
                int wrongEntries = wrong;
                int entryCount = entries.Count;
                string firstWrong = first!;
                state.Findings.Add(Severity.Warning, "musepack.seek-table-mismatch", node, count => count == 1
                    ? $"{wrongEntries} of the seek table's {entryCount} entries do not point at the audio packet they stand for: {firstWrong}. Players that seek with it land in the wrong place."
                    : $"{count} seek tables have entries that do not point at the audio packet they stand for, the first {wrongEntries} of {entryCount}: {firstWrong}. Players that seek with them land in the wrong place.");
            }

            if (decoded < needed)
            {
                state.Findings.Add(Severity.Info, "musepack.seek-table-short", node, count => count == 1
                    ? $"The seek table holds {decoded} entries; the stream's {state.AudioPackets.Count} audio packets at one entry every {step} would need {needed}. Decoders seek through the packets after the last entry."
                    : $"{count} seek tables hold fewer entries than the stream's {state.AudioPackets.Count} audio packets need, the first {decoded} where one entry every {step} needs {needed}. Decoders seek through the packets after the last entry.");
            }
        }

        if (state.Header is { Samples: > 0 } header && state.AudioPackets.Count > 0 && state.Unreadable is null)
        {
            long frames = header.ExpectedFrames;
            long packets = (frames + header.FramesPerPacket - 1) / header.FramesPerPacket;
            if (state.AudioPackets.Count < packets && !state.Packets[state.AudioPackets[^1]].IsTruncated)
            {
                state.Findings.AddNow(Severity.Warning, "musepack.audio-packets-missing",
                    $"The stream header's {header.Samples} samples need {frames} frames in {packets} audio packets; the stream holds {state.AudioPackets.Count}, so decoders end early.", state.Leading[0]);
            }
            else if (state.AudioPackets.Count > packets)
            {
                state.Findings.AddNow(Severity.Warning, "musepack.audio-packets-extra",
                    $"The stream header's {header.Samples} samples need {frames} frames in {packets} audio packets; the stream holds {state.AudioPackets.Count}, and decoders stop at the sample count.", state.Leading[0]);
            }
        }

    }

    // Whether the offset falls inside a run of packets that were not read: the packet that holds it
    // by binary search over the records, which are in order.
    private static bool InRun(State state, long offset)
    {
        int low = 0;
        int high = state.Packets.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            Packet packet = state.Packets[middle];
            if (offset < packet.Offset)
            {
                high = middle - 1;
            }
            else if (offset >= packet.Offset + packet.Length)
            {
                low = middle + 1;
            }
            else
            {
                return packet.IsRun;
            }
        }

        return false;
    }

    private static void AddStream(State state, Node node, AnalysisContext context, IReadOnlyList<EncoderClaim> claims)
    {
        StreamHeaderInfo? header = state.Header;
        int rate = header is { RateIndex: < 4 and { } rateIndex } ? Sv7Parser.SampleRates[rateIndex] : 0;
        var figures = new StreamFigures(rate);
        long frames = 0;
        for (int i = 0; i < state.AudioPackets.Count; i++)
        {
            if (i % 4096 == 0)
            {
                state.Cancellation.ThrowIfCancellationRequested();
            }

            Packet packet = state.Packets[state.AudioPackets[i]];
            if (packet.IsTruncated)
            {
                continue;
            }

            // Decoders stop at the stream header's sample count, so packets past it add no frames.
            long count = state.FramesIn(state.AudioPackets[i]);
            if (header is { Samples: > 0 })
            {
                count = Math.Min(count, Math.Max(0, header.ExpectedFrames - frames));
                if (count == 0)
                {
                    continue;
                }
            }

            frames += count;
            figures.Add(packet.Offset, packet.Length * 8, count * Sv7Parser.FrameSamples);
        }

        // libmpcdec r495 outputs the header's samples less the beginning silence, and no more than
        // the frames decoded give after the synthesis filter's delay and that silence.
        if (header is { Samples: 0 } && frames > 0 && state.SampleCountField is { } sampleCount)
        {
            state.Findings.AddNow(Severity.Info, "musepack.sample-count-unknown",
                $"The stream header's sample count is 0, which the SV8 specification uses for a length it does not know; libmpcdec then decodes no samples, though the audio packets hold {frames} frames.",
                sampleCount);
        }

        long silence = header?.Silence ?? 0;
        long samples = header is null ? 0 : Math.Max(0, Math.Min(header.Samples, (frames * Sv7Parser.FrameSamples) - Sv7Parser.SynthesisDelay) - silence);
        double duration = rate > 0 ? samples / (double)rate : 0;

        Node Cover(string name, NodeValue? value, string description) => new(name, node.Offset, node.Length, value) { Description = description };

        Node? encoder = MusepackEncoder.Detect(null, state.EncoderVersion?.Version, state.EncoderVersion?.Node, claims,
            (name, value, description) => name == "Encoder"
                ? new Node(name, node.Offset, node.Length, value) { Description = description, Topic = ClaimRoles.EncoderTopic }
                : Cover(name, value, description),
            (claim, text, name) => state.Findings.Add(Severity.Info, "musepack.encoder-claim-differs", claim.Source, _ =>
                $"The {claim.Description} names \"{text}\", but the stream points to {name}; the tag may have been written by other software, or kept when the audio was re-encoded."));
        if (encoder is not null)
        {
            state.Leading.Add(encoder);
        }

        Node? writer = WriterDetection.Detect(claims, (name, value, description) => new Node(name, node.Offset, node.Length, value) { Description = description, Topic = ClaimRoles.TopicOf(name) },
            (claim, text, name) => state.Findings.Add(Severity.Info, "musepack.writer-claim-differs", claim.Source, _ => WriterDetection.Disagreement(claim, text, name)),
            format: new WriterDetection.StreamFormat(KnownWriters.Musepack, "Musepack", (claim, message, other) => state.Findings.Add(Severity.Info, "musepack.writer-other-format", other, _ => message)));
        if (writer is not null)
        {
            state.Leading.Add(writer);
        }

        ClaimRoles.CompleteVersion(encoder, claims);

        Node statistics = new("Statistics", node.Offset, node.Length) { Kind = NodeKind.Statistics, Description = Explain.Statistics };
        statistics.Add(Cover("Audio Packets", NodeValue.Integer(figures.Units, "packets", "packet"), Explain.StatPackets));
        statistics.Add(Cover("Frames", NodeValue.Integer(frames, "frames", "frame"), Explain.StatFrames));
        statistics.Add(Cover("Coded Samples", NodeValue.Integer(frames * Sv7Parser.FrameSamples, "samples", "sample"), Explain.CodedSamples));
        statistics.Add(Cover("Decoder Delay", NodeValue.Integer(Sv7Parser.SynthesisDelay + silence, "samples", "sample"), Explain.DecoderDelay));
        statistics.Add(Cover("Samples", NodeValue.Integer(samples, "samples", "sample"), Explain.StatSamples));
        statistics.Add(Cover("Duration", NodeValue.Decimal(duration, 3, "s"), Explain.StatDuration));
        figures.AddTo(statistics, Cover, "Packet", "packets", inBytes: true, out Node bitrate);
        state.Leading.Add(statistics);

        int channels = header?.Channels ?? 0;
        context.AddStream(new StreamInfo
        {
            Nodes = [node],
            Codec = "Musepack SV8",
            SampleRate = rate,
            Channels = channels,
            ChannelMode = channels switch
            {
                1 => "Mono",
                2 => header!.MidSide ? "Stereo, mid/side per subband" : "Stereo",
                _ => null,
            },
            ChannelLayout = channels switch
            {
                1 => ChannelLayouts.Mono,
                2 => ChannelLayouts.Stereo,
                _ => null,
            },
            Samples = samples,
            Duration = duration,
            AverageBitrate = figures.AverageBitrate,
            IsVbr = true,
            Encoder = encoder?.Value?.ToString(),
            EncoderConfidence = MusepackEncoder.ConfidenceOf(encoder),
            Writer = ClaimRoles.Resolved(writer).Name,
            WriterConfidence = ClaimRoles.Resolved(writer).Confidence,
            Series = figures.Series(bitrate, state.Cancellation),
        });
    }

    // A packet in 32 bytes.
    [StructLayout(LayoutKind.Auto)]
    // A packet, or a run of packets that are not detailed (IsRun): then Offset and Length cover the
    // run, Key is the first packet's, and Count says how many it holds.
    private readonly struct Packet(long offset, long length, long size, ushort key, byte headerLength, int count = 1, byte flags = 0)
    {
        private const byte Run = 1;
        private const byte Mixed = 2;

        public long Offset => offset;

        public int Count => count;

        public bool IsRun => (flags & Run) != 0;

        public bool IsMixed => (flags & Mixed) != 0;

        public Packet AsRun() => new(offset, length, length, key, headerLength, 1, Run);

        public Packet Joined(Packet next) =>
            new(offset, length + next.Length, size + next.Length, key, headerLength, count + 1, (byte)(flags | (next.Key == key ? 0 : Mixed)));

        public long Length => length;

        public ushort Key => key;

        public byte HeaderLength => headerLength;

        // The size the packet's field states, which a cut packet's data does not reach.
        public long Size => size;

        public bool IsTruncated => size > length;
    }

    private sealed class StreamHeaderInfo
    {
        public bool CrcVerified { get; init; }

        public int Version { get; set; }

        public long Samples { get; set; }

        public long Silence { get; set; }

        // Null where the header ends before the two bytes that hold the sample rate and the channels,
        // so a cut header reports no rate or channels it never read.
        public int? RateIndex { get; set; }

        public int MaxBands { get; set; }

        public int? Channels { get; set; }

        public bool MidSide { get; set; }

        public int BlockPower { get; set; } = 3;

        public long FramesPerPacket => 1L << (2 * BlockPower);

        // The frames decoders decode: until the samples and the synthesis filter's delay are out
        // (libmpcdec r495 mpc_decoder_decode_frame; mpcenc r495 writes as many).
        // Worked out from the quotient and the remainder, since a sample count near 2^63 overflows the sum.
        public long ExpectedFrames => (Samples / Sv7Parser.FrameSamples) + (((Samples % Sv7Parser.FrameSamples) + Sv7Parser.SynthesisDelay + Sv7Parser.FrameSamples - 1) / Sv7Parser.FrameSamples);
    }

    // The fields of one packet's payload, which nodes are added for.
    private sealed class Fields(Node node, byte[] data, long offset)
    {
        public Node Node => node;

        public byte[] Data => data;

        public long Offset => offset;

        public Node Bytes(string name, int start, int length, NodeValue? value, string description, string? topic = null) =>
            node.Add(new Node(name, offset + start, length, value) { Description = description, Topic = topic });

        public bool Size(string name, ref int position, string unit, string singular, string description, out long value)
        {
            int start = position;
            if (!ReadSize(data, ref position, out value))
            {
                position = start;
                return false;
            }

            Bytes(name, start, position - start, NodeValue.Integer(value, unit, singular), description);
            return true;
        }

        // Bytes after the last field, which must be zero.
        public void Padding(State state, int position, string key)
        {
            if (position >= data.Length)
            {
                return;
            }

            Node padding = Bytes("Padding", position, data.Length - position, NodeValue.Integer(data.Length - position, "bytes"), Explain.Padding);
            if (data.AsSpan(position).ContainsAnyExcept((byte)0))
            {
                state.Findings.Add(Severity.Warning, "musepack.padding-not-zero", padding, count => count == 1
                    ? $"The {key} packet's bytes after its last field, at 0x{padding.Offset:X}, are not all zero, which the SV8 specification requires."
                    : $"{count} packets have bytes after their last field that are not all zero, which the SV8 specification requires; the first is the {key} packet's, at 0x{padding.Offset:X}.");
            }
        }
    }

    private sealed class State(long start, long end, CancellationToken cancellation)
    {
        public long Start => start;

        public long End => end;

        public CancellationToken Cancellation => cancellation;

        public ChunkedList<Packet> Packets { get; } = new();

        public List<int> AudioPackets { get; } = [];

        public Dictionary<int, Node> Detailed { get; } = [];

        public List<Node> Leading { get; } = [];

        public FindingCollector Findings { get; } = new();

        public StreamHeaderInfo? Header { get; set; }

        public Node? SampleCountField { get; set; }

        public bool HasReplayGain { get; set; }

        public int[]? ReplayGain { get; set; }

        public double? Profile { get; set; }

        public ((int Major, int Minor, int Build) Version, Node Node)? EncoderVersion { get; set; }

        public List<(int Index, long Target, Node Field)> SeekOffsets { get; } = [];

        public List<(int Index, int Distance, List<long> Entries, long Decoded, Node Node)> SeekTables { get; } = [];

        public int SeekEntriesKept { get; set; }

        // Stream header, ReplayGain and encoder info packets read past the detailed ones.
        public int[] LatePackets { get; } = new int[3];

        public (long Offset, string Code, string Message)? Unreadable { get; set; }

        public (long Start, long End)? Trailing { get; set; }

        // The frames an audio packet holds: the stream header's number, and in the last packet what
        // is left of the frames the sample count needs.
        public long FramesIn(int index)
        {
            long perPacket = Header?.FramesPerPacket ?? 64;
            if (Header is not { Samples: > 0 } header || AudioPackets.Count == 0 || index != AudioPackets[^1])
            {
                return perPacket;
            }

            long left = header.ExpectedFrames - ((AudioPackets.Count - 1) * perPacket);
            return left > 0 && left <= perPacket ? left : perPacket;
        }
    }
}
