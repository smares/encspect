using Encspect.Core;
using Encspect.Formats.Wma;

namespace Encspect.Tests;

// A frame built bit by bit with high bands coded as noise, whose gains use the high-band gain codebook's negative
// symbols: the parser must read the frame to its end, count the bands, and report nothing invalid. (A decoder that
// reads those codes wrong goes out of step in the exponents that follow.)
public class WmaNoiseTests
{
    private sealed class BitWriter
    {
        private readonly List<bool> _bits = [];

        public int Length => _bits.Count;

        public void Write(uint value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                _bits.Add(((value >> i) & 1) != 0);
            }
        }

        public byte[] ToBytes(int size, byte fill)
        {
            byte[] bytes = new byte[size];
            Array.Fill(bytes, fill);
            for (int i = 0; i < _bits.Count; i++)
            {
                if (_bits[i])
                {
                    bytes[i / 8] |= (byte)(0x80 >> (i % 8));
                }
                else
                {
                    bytes[i / 8] &= (byte)~(0x80 >> (i % 8));
                }
            }

            // The rest of the byte the frame ends in is zero.
            for (int i = _bits.Count; i % 8 != 0; i++)
            {
                bytes[i / 8] &= (byte)~(0x80 >> (i % 8));
            }

            return bytes;
        }
    }

    [Fact]
    public void FrameWithNoiseBandsAndNegativeGainsIsReadToItsEnd()
    {
        // Mono at 44.1 kHz, 24 kbit/s (version 2, exponent VLC only): the rate and bitrate make the encoder code noise.
        const int Align = 400;
        StandardParameters p = StandardParameters.Create(2, 1, 44100, 3000, Align, 0x0001, 10);
        Assert.True(p.UsesNoiseCoding, $"noise {p.UsesNoiseCoding} error {p.Error}");
        int bands = p.ExponentHighSizes[0];
        Assert.True(bands >= 4, $"bands {bands}");

        var w = new BitWriter();
        w.Write(1, 1); // channel 0 coded
        w.Write(0, 7); // total gain
        int[] noisy = [0, 1, 2, 3];
        for (int i = 0; i < bands; i++)
        {
            w.Write(noisy.Contains(i) ? 1u : 0u, 1);
        }

        (uint[] gainCodes, byte[] gainLengths, int[] gainSymbols) = VlcTests.Table("high-band-gain");
        w.Write(40, 7); // the first level
        foreach (int symbol in new[] { -11, -5, 3 })
        {
            int index = Array.IndexOf(gainSymbols, symbol);
            Assert.True(index >= 0);
            w.Write(gainCodes[index], gainLengths[index]);
        }

        // Exponents (version 2 starts at 36; the code for symbol 60 keeps it): one for each band, as the decoder walks them.
        (uint[] exponentCodes, byte[] exponentLengths, _) = VlcTests.Table("exponent");
        int position = 0;
        int band = 0;
        while (position < 1 << p.FrameLengthBits)
        {
            w.Write(exponentCodes[60], exponentLengths[60]);
            int size = band < p.ExponentSizes[0] ? p.ExponentBands[0][band] : 0;
            band++;
            position += size == 0 ? 4 : size;
        }

        // The block's coefficients end at once: the code for the end of the block.
        (uint[] coefficientCodes, byte[] coefficientLengths, _) = VlcTests.Table($"coefficient-{p.CoefficientTable * 2}");
        w.Write(coefficientCodes[1], coefficientLengths[1]);

        int frameBits = w.Length;
        byte[] superframe = w.ToBytes(Align, (byte)'N');
        var data = new byte[10];
        data[4] = 0x01;
        var format = new AudioFormat(0x0161, 1, 44100, 3000, Align, 16);
        var parent = new Node("Stream", 0, 100_000);
        var configuration = new CodecConfiguration(new MappedBytes(data, 100), parent) { Format = format };
        byte[][] objects = [superframe, superframe];
        var context = new CodecContext(parent, objects.Select((o, i) => new MappedBytes(o, 1000 + (i * 1000L))), CancellationToken.None, null, null, configuration);
        new WmaStandardParser().Parse(context);

        Assert.DoesNotContain(context.Findings, f => f.Severity != Severity.Info);
        Node substitution = Find(parent, "Noise Substitution")!;
        Assert.Equal($"{noisy.Length} of {bands} high bands", substitution.Value?.AsText());
        Node levels = Find(parent, "Noise Levels")!;
        Assert.Equal(noisy.Length, (int)levels.Value!.AsInteger()!.Value);
        // The frame ends where the bits written end: its size is the figure the statistics give.
        Assert.Equal(frameBits / 8.0, Find(parent, "Smallest Frame")!.Value!.AsDouble()!.Value, 2);
    }

    private static Node? Find(Node node, string name)
    {
        if (node.Name == name)
        {
            return node;
        }

        foreach (Node child in node.Children)
        {
            if (Find(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
