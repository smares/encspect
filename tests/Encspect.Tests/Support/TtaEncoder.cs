using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Tests;

// An encoder of TTA1 audio format 1, the inverse of the clean-room description the decoder follows (docs/development.md, "TTA decoding"), so that tests can
// round-trip samples the encoders at hand never write (full-scale noise, long unary runs, every depth and channel count) and craft damage. It shares no code with
// the decoder; the files of libtta-c 2.3 and FFmpeg are what pins the description itself (crosscheck_tta_pcm.py).
internal static class TtaEncoder
{
    private sealed class Bits
    {
        private readonly List<byte> _bytes = [];
        private int _used;

        public void Write(ulong value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (_used == 0)
                {
                    _bytes.Add(0);
                }

                if (((value >> i) & 1) != 0)
                {
                    _bytes[^1] |= (byte)(1 << _used);
                }

                _used = (_used + 1) & 7;
            }
        }

        public void WriteUnary(long ones)
        {
            for (long i = 0; i < ones; i++)
            {
                Write(1, 1);
            }

            Write(0, 1);
        }

        public byte[] ToArray() => [.. _bytes];
    }

    private sealed class Channel
    {
        public int[] W = new int[8], V = new int[8], S = new int[8];
        public int PreviousResidual, Previous, KA = 10, KB = 10;
        public uint SumA = 1 << 14, SumB = 1 << 14;
    }

    private static uint Pow2(int n) => n >= 31 ? 0x80000000u : 1u << n;

    private static int Adapt(uint sum, int k) => k > 0 && sum < Pow2(k + 4) ? k - 1 : sum > Pow2(k + 5) ? k + 1 : k;

    // One frame: the coded samples of count sample times from the planar samples, then the CRC-32.
    public static byte[] Frame(int[][] samples, int from, int count, int depth)
    {
        int channels = samples.Length;
        var state = new Channel[channels];
        for (int c = 0; c < channels; c++)
        {
            state[c] = new Channel();
        }

        int shift = depth == 2 ? 9 : 10;
        int predictor = depth == 1 ? 4 : 5;
        var bits = new Bits();
        var x = new int[channels];
        unchecked
        {
            for (int t = from; t < from + count; t++)
            {
                if (channels == 1)
                {
                    x[0] = samples[0][t];
                }
                else
                {
                    for (int c = 0; c < channels - 1; c++)
                    {
                        x[c] = samples[c + 1][t] - samples[c][t];
                    }

                    x[channels - 1] = samples[channels - 1][t] - (x[channels - 2] / 2);
                }

                for (int c = 0; c < channels; c++)
                {
                    Channel s = state[c];
                    int y = x[c] - ((s.Previous * ((1 << predictor) - 1)) >> predictor);
                    s.Previous = x[c];
                    int e = FilterInverse(s, y, shift);
                    Rice(bits, s, e);
                }
            }
        }

        byte[] coded = bits.ToArray();
        byte[] frame = new byte[coded.Length + 4];
        coded.CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(coded.Length), Crc32.Of(coded));
        return frame;
    }

    private static int FilterInverse(Channel s, int y, int shift)
    {
        unchecked
        {
            if (s.PreviousResidual > 0)
            {
                for (int j = 0; j < 8; j++)
                {
                    s.W[j] += s.S[j];
                }
            }
            else if (s.PreviousResidual < 0)
            {
                for (int j = 0; j < 8; j++)
                {
                    s.W[j] -= s.S[j];
                }
            }

            int p = 1 << (shift - 1);
            for (int j = 0; j < 8; j++)
            {
                p += s.V[j] * s.W[j];
            }

            int[] step = [s.S[1], s.S[2], s.S[3], s.S[4], s.V[4] >= 0 ? 1 : -1, s.V[5] >= 0 ? 2 : -2, s.V[6] >= 0 ? 2 : -2, s.V[7] >= 0 ? 4 : -4];
            s.S = step;
            int e = y - (p >> shift);
            s.PreviousResidual = e;
            int[] v = s.V;
            int d1 = y - v[7];
            int d2 = d1 - v[6];
            int d3 = d2 - v[5];
            s.V = [v[1], v[2], v[3], v[4], d3, d2, d1, y];
            return e;
        }
    }

    private static void Rice(Bits bits, Channel s, int e)
    {
        long m = e > 0 ? (2L * e) - 1 : -2L * e;
        if (m < (1L << s.KA))
        {
            bits.WriteUnary(0);
            bits.Write((ulong)m, s.KA);
        }
        else
        {
            long q = m - (1L << s.KA);
            bits.WriteUnary((q >> s.KB) + 1);
            bits.Write((ulong)(q & ((1L << s.KB) - 1)), s.KB);
            unchecked
            {
                s.SumB = s.SumB + (uint)q - (s.SumB >> 4);
            }

            s.KB = Adapt(s.SumB, s.KB);
        }

        unchecked
        {
            s.SumA = s.SumA + (uint)m - (s.SumA >> 4);
        }

        s.KA = Adapt(s.SumA, s.KA);
    }

    // The frames of a whole stream and its file, the planar samples in the container's range (the header's bits decide the depth).
    public static (byte[] File, List<byte[]> Frames) Encode(uint rate, int bits, int[][] samples)
    {
        int depth = (bits + 7) / 8;
        int length = samples[0].Length;
        long frameLength = TtaBuilder.FrameLength(rate);
        var frames = new List<byte[]>();
        for (long from = 0; from < length; from += frameLength)
        {
            frames.Add(Frame(samples, (int)from, (int)Math.Min(frameLength, length - from), depth));
        }

        byte[] file = Unpooled.Join([TtaBuilder.Header(rate, samples.Length, bits, length), TtaBuilder.Table(frames.Select(f => (uint)f.Length)), Unpooled.Join(frames)]);
        return (file, frames);
    }
}
