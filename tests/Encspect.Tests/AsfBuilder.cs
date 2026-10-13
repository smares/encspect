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
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, fixture));
        int at = file.AsSpan().IndexOf(StreamPropertiesId.ToByteArray());
        byte[] properties = file.AsSpan(at, (int)BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(at + 16))).ToArray();

        var collected = new List<byte[]>();
        var analyzer = new Analyzer(DefaultParsers.Tags, [new AsfParser(new Dictionary<int, string> { [formatTag] = "collect" })], [new Collector(collected)]);
        analyzer.Analyze(fixture, new MemoryStream(file));
        return (properties, collected[0]);
    }

    public static byte[] Build(byte[] streamProperties, byte[] mediaObject, int count, int millisecondsEach)
    {
        // One payload: the stream number, the media object number, the offset into it, the length of the replicated
        // data (8: the object size and the presentation time), that data and the object, filling the packet.
        int packetSize = 8 + 15 + mediaObject.Length;
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

    private sealed class Collector(List<byte[]> collected) : ICodecParser
    {
        public string Id => "collect";

        public void Parse(CodecContext context)
        {
            foreach (MappedBytes packet in context.Packets!)
            {
                collected.Add(packet.Span.ToArray());
                return;
            }
        }
    }
}
