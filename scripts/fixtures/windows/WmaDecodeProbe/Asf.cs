// A minimal ASF reader for the probe: the first audio stream's WAVEFORMATEX and the media objects of
// its data packets, assembled from their payloads. It reads the header objects it needs and walks the
// packets itself, so a damaged or crafted file still gets as far as the decoder.
using System.Buffers.Binary;

namespace WmaDecodeProbe;

public sealed class MediaObject
{
    public int Index { get; set; }
    public int Stream { get; set; }
    public int Number { get; set; }
    public int Size { get; set; }
    public long PresentationTimeMs { get; set; }
    public bool IsComplete { get; set; }
    public int ReceivedBytes { get; set; }
    public bool IsKeyFrame { get; set; }
    public List<long[]> Ranges { get; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[] Data { get; set; } = [];
}

public sealed class AsfAudio
{
    public string? Error { get; set; }
    public int PacketSize { get; set; }
    public int PacketsRead { get; set; }
    public int AudioStream { get; set; }
    public int FormatTag { get; set; }
    public int Channels { get; set; }
    public int SampleRate { get; set; }
    public int AverageBytesPerSecond { get; set; }
    public int BlockAlign { get; set; }
    public int BitsPerSample { get; set; }
    public int ExtraBytes { get; set; }
    public bool IsExtensible { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[] FormatBlock { get; set; } = [];
    public List<string> Problems { get; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public List<MediaObject> Objects { get; } = [];
}

public static class AsfReader
{
    private static readonly Guid HeaderObject = new("75B22630-668E-11CF-A6D9-00AA0062CE6C");
    private static readonly Guid DataObject = new("75B22636-668E-11CF-A6D9-00AA0062CE6C");
    private static readonly Guid FileProperties = new("8CABDCA1-A947-11CF-8EE4-00C00C205365");
    private static readonly Guid StreamProperties = new("B7DC0791-A9B7-11CF-8EE6-00C00C205365");
    private static readonly Guid AudioMedia = new("F8699E40-5B4D-11CF-A8FD-00805F5C442B");

    public static AsfAudio Read(string path)
    {
        var info = new AsfAudio();
        byte[] file;
        try
        {
            file = File.ReadAllBytes(path);
        }
        catch (Exception e)
        {
            info.Error = "cannot read the file: " + e.Message;
            return info;
        }

        try
        {
            Parse(file, info);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or InvalidDataException)
        {
            info.Problems.Add("parse stopped: " + e.Message);
        }

        if (info.FormatBlock.Length == 0 && info.Error == null)
        {
            info.Error = "no audio stream found";
        }

        return info;
    }

    private static void Parse(byte[] file, AsfAudio info)
    {
        if (file.Length < 30 || new Guid(file.AsSpan(0, 16)) != HeaderObject)
        {
            info.Error = "no ASF header object at the start";
            return;
        }

        long headerSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(16, 8));
        long end = Math.Min(file.Length, headerSize);
        long position = 30;
        uint minPacket = 0, maxPacket = 0;
        long dataStart = -1, dataEnd = file.Length;
        while (position + 24 <= end)
        {
            var id = new Guid(file.AsSpan((int)position, 16));
            long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan((int)position + 16, 8));
            if (size < 24)
            {
                info.Problems.Add($"header object at {position} has size {size}");
                break;
            }

            long bodyEnd = Math.Min(position + size, file.Length);
            int body = (int)position + 24;
            if (id == FileProperties && body + 80 <= bodyEnd)
            {
                minPacket = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(body + 68, 4));
                maxPacket = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(body + 72, 4));
            }
            else if (id == StreamProperties && body + 54 <= bodyEnd && info.FormatBlock.Length == 0
                && new Guid(file.AsSpan(body, 16)) == AudioMedia)
            {
                uint typeLength = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(body + 40, 4));
                int flags = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(body + 48, 2));
                int data = body + 54;
                int length = (int)Math.Min(typeLength, bodyEnd - data);
                if (length < 16)
                {
                    info.Problems.Add($"audio type-specific data is {length} bytes");
                }
                else
                {
                    info.AudioStream = flags & 0x7F;
                    SetFormat(info, file.AsSpan(data, length).ToArray());
                }
            }

            position += size;
        }

        // The Data Object follows the header; look for it where the header ends.
        long scan = Math.Min(headerSize, file.Length);
        if (scan + 50 <= file.Length && new Guid(file.AsSpan((int)scan, 16)) == DataObject)
        {
            dataStart = scan + 50;
            long dataSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan((int)scan + 16, 8));
            if (dataSize >= 50 && scan + dataSize <= file.Length)
            {
                dataEnd = scan + dataSize;
            }
        }

        if (dataStart < 0)
        {
            info.Problems.Add("no Data Object where the header ends");
            return;
        }

        if (info.FormatBlock.Length == 0)
        {
            return;
        }

        int fixedSize = minPacket == maxPacket && minPacket > 0 ? (int)minPacket : 0;
        info.PacketSize = fixedSize;
        // Packets of differing sizes carry their length; a file that omits it gets the larger size, so that its packets still reach the decoder.
        int fallback = fixedSize > 0 ? 0 : (int)Math.Min(Math.Max(maxPacket, minPacket), 1u << 24);
        WalkPackets(file, dataStart, dataEnd, fixedSize, fallback, info);
    }

    private static void SetFormat(AsfAudio info, byte[] block)
    {
        info.FormatBlock = block;
        info.FormatTag = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(0, 2));
        info.Channels = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(2, 2));
        info.SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(4, 4));
        info.AverageBytesPerSecond = (int)BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(8, 4));
        info.BlockAlign = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(12, 2));
        info.BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(14, 2));
        info.ExtraBytes = block.Length >= 18 ? BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(16, 2)) : 0;
        if (info.FormatTag == 0xFFFE && block.Length >= 40)
        {
            info.IsExtensible = true;
            info.FormatTag = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(24, 2));
        }
    }

    private sealed class Assembly
    {
        public int Size;
        public long Time;
        public bool Key;
        public byte[] Data = [];
        public int Received;
        public List<long[]> Ranges = [];
    }

    private static void WalkPackets(byte[] file, long start, long end, int fixedSize, int fallbackSize, AsfAudio info)
    {
        var open = new Dictionary<(int, int), Assembly>();
        var order = new List<(int, int)>();
        void Emit(int stream, int number, Assembly a, bool complete)
        {
            var obj = new MediaObject
            {
                Index = info.Objects.Count,
                Stream = stream,
                Number = number,
                Size = a.Size,
                PresentationTimeMs = a.Time,
                IsComplete = complete,
                ReceivedBytes = a.Received,
                IsKeyFrame = a.Key,
                Data = a.Data,
            };
            obj.Ranges.AddRange(a.Ranges);
            info.Objects.Add(obj);
        }

        long position = start;
        while (position < end)
        {
            long packetEnd = fixedSize > 0 ? Math.Min(position + fixedSize, end) : end;
            long next;
            try
            {
                next = ParsePacket(file, position, packetEnd, fixedSize > 0, fallbackSize, info, open, order, Emit);
            }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or InvalidDataException)
            {
                info.Problems.Add($"packet {info.PacketsRead} at {position}: {e.Message}");
                next = fixedSize > 0 ? position + fixedSize : end;
            }

            info.PacketsRead++;
            if (next <= position)
            {
                break;
            }

            position = next;
        }

        foreach (var key in order)
        {
            if (open.TryGetValue(key, out var a))
            {
                Emit(key.Item1, key.Item2, a, false);
            }
        }
    }

    private static int ReadVar(byte[] b, ref int p, int type)
    {
        switch (type)
        {
            case 0:
                return 0;
            case 1:
                return b[p++];
            case 2:
                p += 2;
                return BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p - 2, 2));
            default:
                p += 4;
                return (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p - 4, 4));
        }
    }

    private static long ParsePacket(byte[] b, long packetStart, long packetEnd, bool isFixed, int fallbackSize, AsfAudio info,
        Dictionary<(int, int), Assembly> open, List<(int, int)> order, Action<int, int, Assembly, bool> emit)
    {
        int p = (int)packetStart;
        int first = b[p++];
        int lengthFlags = first;
        if ((first & 0x80) != 0)
        {
            p += first & 0x0F;
            lengthFlags = b[p++];
        }

        bool multiple = (lengthFlags & 1) != 0;
        int sequenceType = (lengthFlags >> 1) & 3, paddingType = (lengthFlags >> 3) & 3, lengthType = (lengthFlags >> 5) & 3;
        int property = b[p++];
        int replicatedType = property & 3, offsetType = (property >> 2) & 3, numberType = (property >> 4) & 3, streamType = (property >> 6) & 3;
        int packetLength = ReadVar(b, ref p, lengthType);
        ReadVar(b, ref p, sequenceType);
        int padding = ReadVar(b, ref p, paddingType);
        p += 6; // send time, duration
        long limit = packetEnd;
        if (!isFixed)
        {
            if (packetLength <= 0)
            {
                packetLength = fallbackSize;
            }

            if (packetLength <= 0)
            {
                throw new InvalidDataException("variable-size packet without a length");
            }

            limit = Math.Min(packetStart + packetLength, packetEnd);
        }

        long dataEnd = Math.Max(p, limit - padding);
        int count = 1, payloadLengthType = 0;
        if (multiple)
        {
            int flags = b[p++];
            count = flags & 0x3F;
            payloadLengthType = (flags >> 6) & 3;
        }

        for (int i = 0; i < count && p < dataEnd; i++)
        {
            int streamByte = ReadVar(b, ref p, streamType == 0 ? 1 : streamType);
            int stream = streamByte & 0x7F;
            bool key = (streamByte & 0x80) != 0;
            int number = ReadVar(b, ref p, numberType);
            int offset = ReadVar(b, ref p, offsetType);
            int replicated = ReadVar(b, ref p, replicatedType);
            int size = 0;
            long time = 0;
            int delta = 0;
            if (replicated == 1)
            {
                delta = b[p++];
            }
            else if (replicated >= 8)
            {
                size = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p, 4));
                time = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 4, 4));
                p += replicated;
            }
            else
            {
                p += replicated;
            }

            int length = multiple ? ReadVar(b, ref p, payloadLengthType) : (int)(dataEnd - p);
            if (length < 0 || p + length > dataEnd)
            {
                info.Problems.Add($"payload at {p} runs {p + length - dataEnd} bytes past the packet's data");
                length = (int)Math.Max(0, Math.Min(length, dataEnd - p));
            }

            if (stream == info.AudioStream)
            {
                if (replicated == 1)
                {
                    int q = p, end = p + length, k = 0;
                    while (q < end)
                    {
                        int sub = b[q++];
                        sub = Math.Min(sub, end - q);
                        var a = new Assembly { Size = sub, Time = offset + (long)delta * k, Key = key, Data = b.AsSpan(q, sub).ToArray(), Received = sub };
                        a.Ranges.Add([q, sub]);
                        emit(stream, number + k, a, true);
                        q += sub;
                        k++;
                    }
                }
                else
                {
                    AddFragment(info, open, order, emit, stream, number, offset, size, time, key, b, p, length);
                }
            }

            p += length;
        }

        return isFixed ? packetEnd : limit;
    }

    private static void AddFragment(AsfAudio info, Dictionary<(int, int), Assembly> open, List<(int, int)> order,
        Action<int, int, Assembly, bool> emit, int stream, int number, int offset, int size, long time, bool key,
        byte[] b, int p, int length)
    {
        var id = (stream, number);
        if (open.TryGetValue(id, out var a) && offset == 0 && a.Received > 0)
        {
            emit(stream, number, a, false);
            open.Remove(id);
            order.Remove(id);
            a = null;
        }

        if (a == null)
        {
            a = new Assembly { Size = size, Time = time, Key = key };
            open[id] = a;
            order.Add(id);
        }

        int need = Math.Max(a.Size, offset + length);
        if (need > 64 * 1024 * 1024 || offset < 0)
        {
            info.Problems.Add($"media object {number} has an impossible size or offset ({need}, {offset})");
            return;
        }

        if (a.Data.Length < need)
        {
            Array.Resize(ref a.Data, need);
        }

        Array.Copy(b, p, a.Data, offset, length);
        a.Received += length;
        a.Ranges.Add([p, length]);
        if (a.Size > 0 && a.Received >= a.Size)
        {
            emit(stream, number, a, true);
            open.Remove(id);
            order.Remove(id);
        }
    }
}
