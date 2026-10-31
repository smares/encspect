using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Formats.Wma;

namespace Encspect.Tests;

// A Windows Media Audio Professional or Lossless stream of a fixture, handed to its codec parser as an ASF file would hand it: the media objects
// (which a test may change), the WAVEFORMATEX and the codec data. The result is the parser's node, stream, and findings.
internal sealed class WmaStream(List<byte[]> objects, AudioFormat format, byte[] data)
{
    public const string Pro = "asf/external-windows-wma10pro-cbr-stereo-44k-128.wma";
    public const string ProFive = "asf/external-windows-wma10pro-cbr-5.1-48k-384.wma";
    public const string ProVbr = "asf/external-windows-wma10pro-vbr-stereo-44k-q50.wma";
    public const string Lossless = "asf/external-windows-wma92lossless-stereo-44k-16.wma";
    public const string LosslessFive = "asf/external-windows-wma92lossless-5.1-48k-24.wma";

    public List<byte[]> Objects { get; set; } = objects;

    public AudioFormat Format { get; set; } = format;

    public byte[] Data { get; set; } = data;

    public static WmaStream Load(string fixture, int tag)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, fixture));
        int at = file.AsSpan().IndexOf(new Guid("B7DC0791-A9B7-11CF-8EE6-00C00C205365").ToByteArray());
        ReadOnlySpan<byte> p = file.AsSpan(at);
        int typeLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(p[64..]);
        var format = new AudioFormat(tag, BinaryPrimitives.ReadUInt16LittleEndian(p[80..]), (int)BinaryPrimitives.ReadUInt32LittleEndian(p[82..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(p[86..]), BinaryPrimitives.ReadUInt16LittleEndian(p[90..]), BinaryPrimitives.ReadUInt16LittleEndian(p[92..]));
        return new WmaStream(AsfBuilder.Objects(fixture, tag), format, p.Slice(78 + 18, typeLength - 18).ToArray());
    }

    public static WmaStream LoadPro(string fixture = Pro) => Load(fixture, 0x0162);

    public static WmaStream LoadLossless(string fixture = Lossless) => Load(fixture, 0x0163);

    public CodecContext Run(ICodecParser parser)
    {
        var parent = new Node("Stream", 0, 1_000_000);
        var packets = Objects.Select((o, i) => new MappedBytes(o, 200 + (i * 20_000L))).ToList();
        var context = new CodecContext(parent, packets, CancellationToken.None, null, null, new CodecConfiguration(new MappedBytes(Data, 100), parent) { Format = Format });
        parser.Parse(context);
        return context;
    }

    public CodecContext RunPro() => Run(new WmaProParser());

    public CodecContext RunLossless() => Run(new WmaLosslessParser());

    // The bits of a media object as a number, for building changed objects.
    public static void SetBits(byte[] bytes, int start, int count, uint value)
    {
        for (int i = 0; i < count; i++)
        {
            int bit = start + i;
            byte mask = (byte)(0x80 >> (bit & 7));
            if (((value >> (count - 1 - i)) & 1) != 0)
            {
                bytes[bit >> 3] |= mask;
            }
            else
            {
                bytes[bit >> 3] &= (byte)~mask;
            }
        }
    }

    public static uint GetBits(byte[] bytes, int start, int count)
    {
        uint value = 0;
        for (int i = 0; i < count; i++)
        {
            int bit = start + i;
            value = (value << 1) | (uint)((bytes[bit >> 3] >> (7 - (bit & 7))) & 1);
        }

        return value;
    }

    public static Node Child(Node node, string name) => node.Children.Single(c => c.Name == name);

    public static Node StreamNode(CodecContext context) => context.Parent.Children.Single(c => c.Kind == NodeKind.Stream);

    public static string Statistic(Node stream, string name) => Child(Child(stream, "Statistics"), name).Value!.ToString()!;
}
