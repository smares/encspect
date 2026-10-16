using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Formats.Asf;
using Encspect.Host;

namespace Encspect.Tests;

// ASF files built in memory for the tests that need many packets: a header with one stream (the Stream
// Properties Object of a fixture) and a Data Object whose packets each hold one payload, a whole media object.
internal static class AsfBuilder
{
    private static readonly Guid HeaderId = new("75B22630-668E-11CF-A6D9-00AA0062CE6C");
    private static readonly Guid DataId = new("75B22636-668E-11CF-A6D9-00AA0062CE6C");
    private static readonly Guid FilePropertiesId = new("8CABDCA1-A947-11CF-8EE4-00C00C205365");
    private static readonly Guid StreamPropertiesId = new("B7DC0791-A9B7-11CF-8EE6-00C00C205365");
    private static readonly Guid HeaderExtensionId = new("5FBF03B5-A92E-11CF-8EE3-00C00C205365");
    private static readonly Guid ReservedId = new("ABD3D211-A9BA-11CF-8EE6-00C00C205365");

    // The first stream's Stream Properties Object of a fixture, and its first media object.
    public static (byte[] StreamProperties, byte[] MediaObject) Take(string fixture, int formatTag)
    {
        byte[] file = System.IO.File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, fixture));
        int at = file.AsSpan().IndexOf(StreamPropertiesId.ToByteArray());
        byte[] properties = file.AsSpan(at, (int)BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(at + 16))).ToArray();

        var collected = new List<byte[]>();
        var analyzer = new Analyzer(DefaultParsers.Tags, [new AsfParser(new Dictionary<int, string> { [formatTag] = "collect" })], [new Collector(collected)]);
        analyzer.Analyze(fixture, new MemoryStream(file));
        return (properties, collected[0]);
    }

    // ---- objects that state millions of entries, for the tests of the bounds on nodes ----

    public static byte[] Object(string guid, byte[] body)
    {
        byte[] result = new byte[24 + body.Length];
        Guid.Parse(guid).TryWriteBytes(result);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16), (ulong)result.Length);
        body.CopyTo(result.AsSpan(24));
        return result;
    }

    // A fixture's bytes with an object put at the end of its Header Object (or of its Header Extension Object).
    public static byte[] InHeader(string fixture, byte[] obj, bool extension = false) =>
        InHeader(System.IO.File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "asf", fixture + ".asf")), obj, extension);

    // The same for a file in memory (which this changes).
    public static byte[] InHeader(byte[] file, byte[] obj, bool extension = false)
    {
        int header = (int)BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(16));
        int at = header;
        if (extension)
        {
            int e = file.AsSpan().IndexOf(HeaderExtensionId.ToByteArray());
            BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(e + 16), BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(e + 16)) + (ulong)obj.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(e + 42), BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(e + 42)) + (uint)obj.Length);
            at = e + (int)BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(e + 16)) - obj.Length;
        }

        byte[] result = [.. file[..at], .. obj, .. file[at..]];
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16), (ulong)(header + obj.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24), BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(24)) + 1);
        return result;
    }

    // An Index Object (one specifier) of this many blocks of no entries.
    public static byte[] IndexOfBlocks(int blocks)
    {
        var body = new MemoryStream(10 + (blocks * 12));
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(four, 1000);
        body.Write(four);
        body.Write([1, 0]);
        BinaryPrimitives.WriteUInt32LittleEndian(four, (uint)blocks);
        body.Write(four);
        body.Write([1, 0, 1, 0]);
        byte[] block = new byte[12];
        for (int i = 0; i < blocks; i++)
        {
            body.Write(block);
        }

        return Object("D6E229D3-35DA-11D1-9034-00A0C90349BE", body.ToArray());
    }

    // A Codec List Object of this many entries of 8 bytes (a type and three lengths of 0).
    public static byte[] CodecListOf(int entries)
    {
        var body = new MemoryStream(20 + (entries * 8));
        body.Write(Guid.Parse("86D15241-311D-11D0-A3A4-00A0C90348F6").ToByteArray());
        body.Write(BitConverter.GetBytes((uint)entries));
        byte[] entry = [2, 0, 0, 0, 0, 0, 0, 0];
        for (int i = 0; i < entries; i++)
        {
            body.Write(entry);
        }

        return Object("86D15240-311D-11D0-A3A4-00A0C90348F6", body.ToArray());
    }

    // An Advanced Content Encryption Object of this many records of this many encrypted object records.
    public static byte[] EncryptionRecords(int records, int objects)
    {
        var body = new MemoryStream();
        body.Write(BitConverter.GetBytes((ushort)records));
        for (int i = 0; i < records; i++)
        {
            body.Write(new byte[16]);
            body.Write(new byte[4]);
            body.Write(BitConverter.GetBytes((ushort)objects));
            for (int j = 0; j < objects; j++)
            {
                body.Write(new byte[4]);
            }

            body.Write(new byte[4]);
        }

        return Object("43058533-6981-49E6-9B74-AD12CB86D58C", body.ToArray());
    }

    // A fixture's bytes with an object after the last one.
    public static byte[] Append(string fixture, byte[] obj) =>
        [.. System.IO.File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "asf", fixture + ".asf")), .. obj];

    // This many objects of 24 bytes no specification defines, after the Data Object.
    public static byte[] WithUnknownObjects(string fixture, int count)
    {
        byte[] file = System.IO.File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "asf", fixture + ".asf"));
        byte[] unknown = Object("12345678-1234-1234-1234-123456789ABC", []);
        byte[] result = new byte[file.Length + (count * unknown.Length)];
        file.CopyTo(result, 0);
        for (int i = 0; i < count; i++)
        {
            unknown.CopyTo(result, file.Length + (i * unknown.Length));
        }

        return result;
    }

    // Every media object of a fixture's first stream, as the stream's parser would get them.
    public static List<byte[]> Objects(string fixture, int formatTag)
    {
        byte[] file = System.IO.File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, fixture));
        var collected = new List<byte[]>();
        var analyzer = new Analyzer(DefaultParsers.Tags, [new AsfParser(new Dictionary<int, string> { [formatTag] = "collect" })], [new Collector(collected, all: true)]);
        analyzer.Analyze(fixture, new MemoryStream(file));
        return collected;
    }

    // A Stream Properties Object for an audio stream with the codec-specific data after the WAVEFORMATEX's 18 bytes.
    public static byte[] StreamProperties(int number, int formatTag, int channels, int rate, int bytesPerSecond, int blockAlign, int bits, byte[] codecData)
    {
        var body = new MemoryStream();
        body.Write(new Guid("F8699E40-5B4D-11CF-A8FD-00805F5C442B").ToByteArray());
        body.Write(new Guid("20FB5700-5B55-11CF-A8FD-00805F5C442B").ToByteArray());
        Span<byte> eight = stackalloc byte[8];
        body.Write(eight);
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(four, (uint)(18 + codecData.Length));
        body.Write(four);
        body.Write(new byte[4]);
        body.Write([(byte)number, 0]);
        body.Write(new byte[4]);
        Span<byte> format = stackalloc byte[18];
        BinaryPrimitives.WriteUInt16LittleEndian(format, (ushort)formatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(format[2..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(format[4..], (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(format[8..], (uint)bytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(format[12..], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(format[14..], (ushort)bits);
        BinaryPrimitives.WriteUInt16LittleEndian(format[16..], (ushort)codecData.Length);
        body.Write(format);
        body.Write(codecData);
        var result = new MemoryStream();
        result.Write(StreamPropertiesId.ToByteArray());
        BinaryPrimitives.WriteUInt64LittleEndian(eight, (ulong)(24 + body.Length));
        result.Write(eight);
        result.Write(body.ToArray());
        return result.ToArray();
    }

    // A packet of one payload that fills it (no payload length, no padding): the stream, media object number, offset
    // into the object, the object's size and presentation time stated in the replicated data, and the object's bytes
    // from there; packetSize - 23 of them.
    public static byte[] Packet(int packetSize, int stream, int number, uint offset, uint objectSize, uint time, byte fill = 0x55)
    {
        byte[] p = new byte[packetSize];
        p[0] = 0x00;
        p[1] = 0x5D;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(2), time);
        p[8] = (byte)(0x80 | stream);
        p[9] = (byte)number;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(10), offset);
        p[14] = 8;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(15), objectSize);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(19), time);
        Array.Fill(p, fill, 23, packetSize - 23);
        return p;
    }

    // The packet size every Packet has: 40 bytes of payload.
    public const int SmallPacket = 63;

    public static byte[] Build(byte[] streamProperties, byte[] mediaObject, int count, int millisecondsEach)
    {
        // One payload: the stream number, the media object number, the offset into it, the length of the replicated
        // data (8: the object size and the presentation time), that data and the object, filling the packet.
        int packetSize = 8 + 15 + mediaObject.Length;
        return Wrap(streamProperties, packetSize, count, Enumerable.Empty<byte[]>(), mediaObject, millisecondsEach);
    }

    // A file of the given packets, all of packetSize bytes.
    public static byte[] Assemble(byte[] streamProperties, int packetSize, IReadOnlyList<byte[]> packets)
    {
        return Wrap(streamProperties, packetSize, packets.Count, packets, null, 0);
    }

    private static byte[] Wrap(byte[] streamProperties, int packetSize, int count, IEnumerable<byte[]> packets, byte[]? mediaObject, int millisecondsEach)
    {
        byte[] header = HeaderObject(streamProperties, count, packetSize);
        var file = new MemoryStream(header.Length + (50 + (count * packetSize)));
        file.Write(header);
        file.Write(DataId.ToByteArray());
        Span<byte> eight = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(eight, (ulong)(50 + ((long)count * packetSize)));
        file.Write(eight);
        file.Write(DataId.ToByteArray());
        BinaryPrimitives.WriteUInt64LittleEndian(eight, (ulong)count);
        file.Write(eight);
        file.Write([0x01, 0x01]);

        foreach (byte[] given in packets)
        {
            file.Write(given);
        }

        if (mediaObject is null)
        {
            return file.ToArray();
        }

        byte[] packet = new byte[packetSize];
        for (int i = 0; i < count; i++)
        {
            Span<byte> p = packet;
            p[0] = 0x00;
            p[1] = 0x5D;
            BinaryPrimitives.WriteUInt32LittleEndian(p[2..], (uint)(i * millisecondsEach));
            BinaryPrimitives.WriteUInt16LittleEndian(p[6..], (ushort)millisecondsEach);
            p[8] = 0x81;
            p[9] = (byte)(i & 0xFF);
            BinaryPrimitives.WriteUInt32LittleEndian(p[10..], 0);
            p[14] = 8;
            BinaryPrimitives.WriteUInt32LittleEndian(p[15..], (uint)mediaObject.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(p[19..], (uint)(i * millisecondsEach));
            mediaObject.CopyTo(p[23..]);
            file.Write(packet);
        }

        return file.ToArray();
    }

    private static byte[] HeaderObject(byte[] streamProperties, int packets, int packetSize)
    {
        var fileProperties = new MemoryStream();
        fileProperties.Write(FilePropertiesId.ToByteArray());
        Span<byte> body = stackalloc byte[80];
        BinaryPrimitives.WriteUInt64LittleEndian(body[16..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(body[32..], (ulong)packets);
        BinaryPrimitives.WriteUInt32LittleEndian(body[64..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(body[68..], (uint)packetSize);
        BinaryPrimitives.WriteUInt32LittleEndian(body[72..], (uint)packetSize);
        byte[] size = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(size, 104);
        fileProperties.Write(size);
        fileProperties.Write(body);

        var extension = new MemoryStream();
        extension.Write(HeaderExtensionId.ToByteArray());
        BinaryPrimitives.WriteUInt64LittleEndian(size, 46);
        extension.Write(size);
        extension.Write(ReservedId.ToByteArray());
        extension.Write([6, 0, 0, 0, 0, 0]);

        byte[] children = [.. fileProperties.ToArray(), .. streamProperties, .. extension.ToArray()];
        var header = new MemoryStream();
        header.Write(HeaderId.ToByteArray());
        BinaryPrimitives.WriteUInt64LittleEndian(size, (ulong)(30 + children.Length));
        header.Write(size);
        header.Write([3, 0, 0, 0, 1, 2]);
        header.Write(children);
        return header.ToArray();
    }

    private sealed class Collector(List<byte[]> collected, bool all = false) : ICodecParser
    {
        public string Id => "collect";

        public void Parse(CodecContext context)
        {
            foreach (MappedBytes packet in context.Packets!)
            {
                collected.Add(packet.Span.ToArray());
                if (!all)
                {
                    return;
                }
            }
        }
    }
}
