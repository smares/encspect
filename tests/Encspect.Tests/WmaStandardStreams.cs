using Encspect.Core;
using Encspect.Formats.Wma;

namespace Encspect.Tests;

// Windows Media Audio version 1 and 2 streams built in the tests, bit by bit from the syntax in docs/design/wma-standard-format.md and the
// tables of the format, and handed to the parser as an ASF file would hand them.
internal sealed class WmaBitWriter
{
    private readonly List<bool> _bits = [];

    public int Length => _bits.Count;

    public WmaBitWriter Write(uint value, int count)
    {
        for (int i = count - 1; i >= 0; i--)
        {
            _bits.Add(((value >> i) & 1) != 0);
        }

        return this;
    }

    public WmaBitWriter Code(uint[] codes, byte[] lengths, int index) => Write(codes[index], lengths[index]);

    public byte[] ToBytes(int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < _bits.Count && i < length * 8; i++)
        {
            if (_bits[i])
            {
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        return bytes;
    }

    public bool this[int index] => _bits[index];
}

internal static class WmaStandardStreams
{
    public sealed record Result(IReadOnlyList<Finding> Findings, IReadOnlyList<StreamInfo> Streams, Node Parent);

    public static byte[] CodecData(int version, int options, int samplesPerBlock = 0)
    {
        byte[] data = new byte[version == 1 ? 4 : 10];
        data[version == 1 ? 2 : 4] = (byte)options;
        data[version == 1 ? 3 : 5] = (byte)(options >> 8);
        BitConverter.GetBytes(samplesPerBlock).CopyTo(data, 0);
        if (version == 1)
        {
            data[0] = data[1] = 0;
        }

        return data;
    }

    public static Result Run(int version, int channels, int rate, int bytesPerSecond, int align, byte[] data, IEnumerable<byte[]> objects, bool encrypted = false)
    {
        var parent = new Node("Stream", 0, 100_000_000);
        var format = new AudioFormat(version == 1 ? 0x0160 : 0x0161, channels, rate, bytesPerSecond, align, 16);
        var configuration = new CodecConfiguration(new MappedBytes(data, 100), parent) { Format = format, AreSamplesEncrypted = encrypted, Duration = 1 };
        long offset = 1000;
        List<MappedBytes> packets = [];
        foreach (byte[] o in objects)
        {
            packets.Add(new MappedBytes(o, offset));
            offset += o.Length + 50;
        }

        var context = new CodecContext(parent, packets, CancellationToken.None, null, null, configuration);
        new WmaStandardParser().Parse(context);
        return new Result(context.Findings, context.Streams, parent);
    }

    public static Node Find(Node node, string name) => node.Name == name ? node : node.Children.Select(c => Find(c, name)).FirstOrDefault(n => n is not null)!;

    public static IEnumerable<Node> FindAll(Node node, string name)
    {
        if (node.Name == name)
        {
            yield return node;
        }

        foreach (Node child in node.Children)
        {
            foreach (Node found in FindAll(child, name))
            {
                yield return found;
            }
        }
    }

    // One frame of a mono version 2 stream without variable block lengths and without noise substitution: the channel is coded, the
    // total gain is a value, every band's exponent code is a difference of 0, and the coefficients are runs of the shortest code
    // before the end of the block. The format and the length of a frame follow from the parameters.
    public static WmaBitWriter MonoFrame(StdParameters parameters, int gainValue, int coefficientCodes, int flaggedBand = -1, int escapeRun = -1)
    {
        var w = new WmaBitWriter();
        w.Write(1, 1);
        w.Write((uint)gainValue, 7);
        int highBands = parameters.HighBands[0].Length;
        if (parameters.UsesNoiseSubstitution)
        {
            for (int i = 0; i < highBands; i++)
            {
                w.Write(i == flaggedBand ? 1u : 0u, 1);
            }

            if (flaggedBand >= 0)
            {
                w.Write(5, 7);
            }
        }

        int covered = 0;
        for (int band = 0; covered < parameters.FrameLength; band++)
        {
            w.Code(Tables.ExponentCodes, Tables.ExponentLengths, 60);
            int width = band < parameters.ExponentBands[0].Length ? parameters.ExponentBands[0][band] : 0;
            covered += width > 0 ? width : 4;
        }

        int book = 2 * parameters.CodebookPair;
        for (int i = 0; i < coefficientCodes; i++)
        {
            w.Code(Tables.CoefficientCodes[book], Tables.CoefficientLengths[book], 2);
            w.Write(0, 1);
        }

        if (escapeRun >= 0)
        {
            int gain = gainValue + 1;
            int levelWidth = gain < 15 ? 13 : gain < 32 ? 12 : gain < 40 ? 11 : gain < 45 ? 10 : 9;
            w.Code(Tables.CoefficientCodes[book], Tables.CoefficientLengths[book], 0);
            w.Write(1, levelWidth);
            w.Write((uint)escapeRun, parameters.FrameBits);
            w.Write(0, 1);
        }

        w.Code(Tables.CoefficientCodes[book], Tables.CoefficientLengths[book], 1);
        return w;
    }
}
