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

    // The size the payloads declare; 0 when they declare none.
    public int Size { get; set; }
    public long PresentationTimeMs { get; set; }
    public bool IsComplete { get; set; }
    public int ReceivedBytes { get; set; }
    public bool IsKeyFrame { get; set; }

    // The bytes fed to the decoder: from offset 0 up to the first byte not received. Never padded.
    public int FedBytes { get; set; }
    public List<long[]> Ranges { get; } = [];
    public List<int[]> Missing { get; } = [];
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
    public int ObjectsRead { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[] FormatBlock { get; set; } = [];
    public List<string> Problems { get; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[] File { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public long DataStart { get; set; } = -1;
    [System.Text.Json.Serialization.JsonIgnore]
    public long DataEnd { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public int FallbackSize { get; set; }
}

public static class AsfReader
{
    // Limits so that a crafted file cannot make the probe allocate without bound; a limit that is hit
    // is recorded in the problems.
    private const int MaxObjectBytes = 16 * 1024 * 1024;
    private const int MaxObjects = 1_000_000;
    private const int MaxProblems = 50;

    private static readonly Guid HeaderObject = new("75B22630-668E-11CF-A6D9-00AA0062CE6C");
    private static readonly Guid DataObject = new("75B22636-668E-11CF-A6D9-00AA0062CE6C");
    private static readonly Guid FileProperties = new("8CABDCA1-A947-11CF-8EE4-00C00C205365");
    private static readonly Guid StreamProperties = new("B7DC0791-A9B7-11CF-8EE6-00C00C205365");
    private static readonly Guid HeaderExtension = new("5FBF03B5-A92E-11CF-8EE3-00C00C205365");
    private static readonly Guid ExtendedStreamProperties = new("14E6A5CB-C672-4332-8399-A96952065B5A");
    private static readonly Guid AudioMedia = new("F8699E40-5B4D-11CF-A8FD-00805F5C442B");

    // Everything that changes a published record goes under the lock the serializer takes.
    private static void Problem(AsfAudio info, string text)
    {
        lock (SourceReaderProbe.Gate)
        {
            if (info.Problems.Count < MaxProblems)
            {
                info.Problems.Add(text);
            }
            else if (info.Problems.Count == MaxProblems)
            {
                info.Problems.Add("more problems not listed");
            }
        }
    }

    // Reads the header objects: the first audio stream's format and where the data packets are.
    public static AsfAudio ReadHeader(string path)
    {
        var info = new AsfAudio();
        try
        {
            info.File = System.IO.File.ReadAllBytes(path);
        }
        catch (Exception e)
        {
            info.Error = "cannot read the file: " + e.Message;
            return info;
        }

        try
        {
            Parse(info.File, info);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or InvalidDataException)
        {
            Problem(info, "parse stopped: " + e.Message);
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
        uint minPacket = 0, maxPacket = 0;
        ScanObjects(file, 30, end, info, ref minPacket, ref maxPacket, 0);

        // The Data Object follows the header; look for it where the header ends.
        long scan = Math.Min(headerSize, file.Length);
        long dataEnd = file.Length;
        long dataStart = -1;
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
            Problem(info, "no Data Object where the header ends");
            return;
        }

        int fixedSize = minPacket == maxPacket && minPacket > 0 ? (int)minPacket : 0;
        info.PacketSize = fixedSize;
        // Packets of differing sizes carry their length; a file that omits it gets the larger size, so that its packets still reach the decoder.
        info.FallbackSize = fixedSize > 0 ? 0 : (int)Math.Min(Math.Max(maxPacket, minPacket), 1u << 24);
        info.DataStart = dataStart;
        info.DataEnd = dataEnd;
    }

    private static void ScanObjects(byte[] file, long start, long end, AsfAudio info, ref uint minPacket, ref uint maxPacket, int depth)
    {
        long position = start;
        while (position + 24 <= end)
        {
            var id = new Guid(file.AsSpan((int)position, 16));
            long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan((int)position + 16, 8));
            if (size < 24)
            {
                Problem(info, $"header object at {position} has size {size}");
                break;
            }

            long bodyEnd = Math.Min(Math.Min(position + size, file.Length), end);
            int body = (int)position + 24;
            if (id == FileProperties && body + 80 <= bodyEnd)
            {
                minPacket = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(body + 68, 4));
                maxPacket = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(body + 72, 4));
            }
            else if (id == StreamProperties)
            {
                ReadStreamProperties(file, body, bodyEnd, info);
            }
            else if (id == HeaderExtension && depth == 0 && body + 22 <= bodyEnd)
            {
                // Reserved GUID (16), reserved (2), data size (4), then the extension's objects.
                ScanObjects(file, body + 22, bodyEnd, info, ref minPacket, ref maxPacket, depth + 1);
            }
            else if (id == ExtendedStreamProperties && body + 64 <= bodyEnd && depth < 3)
            {
                // The fixed part, the stream names and the payload extension systems come first; a
                // Stream Properties Object may follow them.
                int nameCount = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(body + 60, 2));
                int extensionCount = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(body + 62, 2));
                long p = body + 64;
                for (int i = 0; i < nameCount && p + 4 <= bodyEnd; i++)
                {
                    p += 4 + BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan((int)p + 2, 2));
                }

                for (int i = 0; i < extensionCount && p + 22 <= bodyEnd; i++)
                {
                    p += 22 + BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan((int)p + 18, 4));
                }

                if (p + 24 <= bodyEnd)
                {
                    ScanObjects(file, p, bodyEnd, info, ref minPacket, ref maxPacket, depth + 1);
                }
            }

            position += size;
        }
    }

    private static void ReadStreamProperties(byte[] file, int body, long bodyEnd, AsfAudio info)
    {
        if (body + 54 > bodyEnd || info.FormatBlock.Length != 0 || new Guid(file.AsSpan(body, 16)) != AudioMedia)
        {
            return;
        }

        uint typeLength = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(body + 40, 4));
        int flags = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(body + 48, 2));
        int data = body + 54;
        int length = (int)Math.Min(typeLength, bodyEnd - data);
        if (length < 16)
        {
            Problem(info, $"audio type-specific data is {length} bytes");
            return;
        }

        info.AudioStream = flags & 0x7F;
        SetFormat(info, file.AsSpan(data, length).ToArray());
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

    private static int NextObjectIndex(AsfAudio info)
    {
        lock (SourceReaderProbe.Gate)
        {
            return info.ObjectsRead++;
        }
    }

    // The object a stream is putting together. Spans are the byte ranges received, in object offsets.
    private sealed class Assembly
    {
        public int Number;
        public int Size;
        public long Time;
        public bool Key;
        public byte[] Data = [];
        public int Received;
        public List<long[]> Ranges = [];
        public List<int[]> Spans = [];
    }

    // Walks the data packets and hands each media object of the audio stream to the sink in file order,
    // then drops its bytes. An object is closed, complete or not, when a payload of another object of
    // its stream begins, and at the end of the data. The bytes are never padded: a cut object is
    // fed up to the first byte not received.
    public static void WalkObjects(AsfAudio info, Action<MediaObject> sink)
    {
        if (info.DataStart < 0 || info.FormatBlock.Length == 0)
        {
            return;
        }

        var file = info.File;
        var open = new Dictionary<int, Assembly>();
        void Close(int stream, Assembly a)
        {
            open.Remove(stream);
            if (info.ObjectsRead >= MaxObjects)
            {
                Problem(info, $"more than {MaxObjects} media objects; the rest are not fed");
                return;
            }

            var obj = new MediaObject
            {
                Index = NextObjectIndex(info),
                Stream = stream,
                Number = a.Number,
                Size = a.Size,
                PresentationTimeMs = a.Time,
                ReceivedBytes = a.Received,
                IsKeyFrame = a.Key,
            };
            obj.Ranges.AddRange(a.Ranges);
            a.Spans.Sort((x, y) => x[0].CompareTo(y[0]));
            int covered = 0;
            foreach (var span in a.Spans)
            {
                if (span[0] > covered)
                {
                    break;
                }

                covered = Math.Max(covered, span[0] + span[1]);
            }

            obj.FedBytes = Math.Min(covered, a.Data.Length);
            // Gaps inside the declared size, in object offsets.
            int cursor = 0;
            foreach (var span in a.Spans)
            {
                if (span[0] > cursor)
                {
                    obj.Missing.Add([cursor, span[0] - cursor]);
                }

                cursor = Math.Max(cursor, span[0] + span[1]);
            }

            if (a.Size > cursor)
            {
                obj.Missing.Add([cursor, a.Size - cursor]);
            }

            obj.IsComplete = a.Size > 0 && obj.Missing.Count == 0;
            obj.Data = a.Data.AsSpan(0, obj.FedBytes).ToArray();
            sink(obj);
            obj.Data = [];
        }

        void Add(int stream, int number, int offset, int size, long time, bool key, int position, int length)
        {
            if (open.TryGetValue(stream, out var a) && (a.Number != number || (offset == 0 && a.Received > 0 && a.Spans.Exists(x => x[0] == 0))))
            {
                Close(stream, a);
                a = null;
            }

            if (a == null)
            {
                a = new Assembly { Number = number, Size = size, Time = time, Key = key };
                open[stream] = a;
            }

            long end = (long)offset + length;
            if (offset < 0 || end > MaxObjectBytes)
            {
                Problem(info, $"media object {number}: a payload at offset {offset} of {length} bytes passes the {MaxObjectBytes} byte limit and is not kept");
                return;
            }

            if (a.Data.Length < end)
            {
                // Geometric growth within the cap, so that many small fragments are not quadratic.
                Array.Resize(ref a.Data, (int)Math.Min(Math.Max(end, (long)a.Data.Length * 2), MaxObjectBytes));
            }

            Array.Copy(file, position, a.Data, offset, length);
            a.Received += length;
            a.Ranges.Add([position, length]);
            a.Spans.Add([offset, length]);
            if (a.Size > 0 && a.Received >= a.Size && IsCovered(a))
            {
                Close(stream, a);
            }
        }

        long pos = info.DataStart;
        int fixedSize = info.PacketSize;
        while (pos < info.DataEnd)
        {
            long packetEnd = fixedSize > 0 ? Math.Min(pos + fixedSize, info.DataEnd) : info.DataEnd;
            long next;
            try
            {
                next = ParsePacket(file, pos, packetEnd, fixedSize > 0, info, Add, Close, open);
            }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or InvalidDataException)
            {
                Problem(info, $"packet {info.PacketsRead} at {pos}: {e.Message}");
                next = fixedSize > 0 ? pos + fixedSize : info.DataEnd;
            }

            lock (SourceReaderProbe.Gate)
            {
                info.PacketsRead++;
            }

            if (next <= pos)
            {
                break;
            }

            pos = next;
        }

        foreach (var (stream, a) in open.OrderBy(x => x.Value.Ranges.Count > 0 ? x.Value.Ranges[0][0] : 0).ToList())
        {
            Close(stream, a);
        }
    }

    // True when the received spans cover [0, size) without a gap.
    private static bool IsCovered(Assembly a)
    {
        int covered = 0;
        foreach (var span in a.Spans.OrderBy(x => x[0]))
        {
            if (span[0] > covered)
            {
                return false;
            }

            covered = Math.Max(covered, span[0] + span[1]);
        }

        return covered >= a.Size;
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

    private static long ParsePacket(byte[] b, long packetStart, long packetEnd, bool isFixed, AsfAudio info, Action<int, int, int, int, long, bool, int, int> add,
        Action<int, Assembly> close, Dictionary<int, Assembly> open)
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
                packetLength = info.FallbackSize;
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
                Problem(info, $"payload at {p} runs {p + length - dataEnd} bytes past the packet's data");
                length = (int)Math.Max(0, Math.Min(length, dataEnd - p));
            }

            if (stream == info.AudioStream)
            {
                if (replicated == 1)
                {
                    if (open.TryGetValue(stream, out var previous))
                    {
                        close(stream, previous);
                    }

                    int q = p, end = p + length, k = 0;
                    while (q < end)
                    {
                        int sub = b[q++];
                        sub = Math.Min(sub, end - q);
                        add(stream, number + k, 0, sub, offset + (long)delta * k, key, q, sub);
                        if (open.TryGetValue(stream, out var a))
                        {
                            close(stream, a);
                        }

                        q += sub;
                        k++;
                    }
                }
                else
                {
                    add(stream, number, offset, size, time, key, p, length);
                }
            }

            p += length;
        }

        return isFixed ? packetEnd : limit;
    }
}
