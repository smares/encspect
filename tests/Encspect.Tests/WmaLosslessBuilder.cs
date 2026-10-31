using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Formats.Wma;

namespace Encspect.Tests;

// Windows Media Audio Lossless streams written bit by bit for the packet reader: frames of raw PCM samples or without any coded channel,
// packed into media objects of a block alignment (docs/design/wma-lossless-format.md, "Packets", "Frame").
internal sealed class LosslessBits
{
    private byte[] _bytes = new byte[64];

    public long Count { get; private set; }

    public void Put(int width, ulong value)
    {
        for (int i = width - 1; i >= 0; i--)
        {
            PutBit(((value >> i) & 1) != 0);
        }
    }

    public void PutBit(bool isSet)
    {
        long at = Count++;
        if ((at >> 3) >= _bytes.Length)
        {
            Array.Resize(ref _bytes, _bytes.Length * 2);
        }

        if (isSet)
        {
            _bytes[at >> 3] |= (byte)(0x80 >> (int)(at & 7));
        }
    }

    public void Zeros(long count)
    {
        for (long i = 0; i < count; i++)
        {
            PutBit(false);
        }
    }

    public bool Bit(long at) => (_bytes[at >> 3] & (0x80 >> (int)(at & 7))) != 0;
}

internal static class LosslessBuilder
{
    public const int FrameLength = 2048;

    public static AudioFormat Format(int channels, int blockAlignment) => new(0x0163, channels, 44100, 44100 * 2 * channels, blockAlignment, 16);

    // The 18 bytes of codec data: 16-bit samples, no channel mask, one subframe per frame.
    public static byte[] Data(int flags = 0x0001)
    {
        byte[] data = new byte[18];
        BinaryPrimitives.WriteUInt16LittleEndian(data, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), (ushort)flags);
        return data;
    }

    public static int WidthOf(int blockAlignment) => System.Numerics.BitOperations.Log2((uint)blockAlignment) + 4;

    // A frame of raw PCM samples of every channel, without its trailer bit: 5 bits and the samples.
    public static void RawFrame(LosslessBits w, int channels)
    {
        w.Put(1, 1);
        w.Put(1, 0);
        w.Put(1, 0);
        w.Put(1, 1);
        w.Put(1, 0);
        for (int c = 0; c < channels; c++)
        {
            for (int s = 0; s < FrameLength; s++)
            {
                w.Put(16, (ulong)(((s * 7) + c) & 0xFFFF));
            }
        }
    }

    // A seekable subframe of one frame without a coded channel and without a trailer bit: one cascaded filter of the smallest order per channel.
    public static void SeekableFrame(LosslessBits w, int channels, int endTrim = -1, int[]? filters = null)
    {
        w.Put(1, 1);
        if (endTrim >= 0)
        {
            w.Put(1, 1);
            w.Put(1, 0);
            w.Put(1, 1);
            w.Put(12, (ulong)endTrim);
        }
        else
        {
            w.Put(1, 0);
        }

        w.Put(1, 1);
        w.Put(1, 0);
        w.Put(3, 0);
        w.Put(1, 0);
        for (int c = 0; c < channels; c++)
        {
            int n = filters?[c] ?? 1;
            w.Put(3, (ulong)(n - 1));
            for (int i = 0; i < n; i++)
            {
                w.Put(7, 0);
            }

            for (int i = 0; i < n; i++)
            {
                w.Put(4, 0);
            }
        }

        w.Put(3, 0);
        w.Put(8, 0);
        TinySubframe(w, channels);
    }

    // The rest of a frame without a coded channel after a seekable subframe: the raw PCM flag, a coded flag for each channel and the zero low bits flag.
    private static void TinySubframe(LosslessBits w, int channels)
    {
        w.Put(1, 0);
        w.Zeros(channels);
        w.Put(1, 0);
    }

    // A frame of 6 bits (one channel): the tile header, no trim, a subframe that is not seekable and codes no channel.
    public static void TinyFrame(LosslessBits w, int channels)
    {
        w.Put(1, 1);
        w.Put(1, 0);
        w.Put(1, 0);
        TinySubframe(w, channels);
    }

    // Packs frames into media objects. bitsOf(i) is the length of frame i with its trailer bit, write(w, i) writes it without the trailer bit.
    // The trailer bit is 1 where the next frame starts in the same packet. sequence gives a packet's sequence number (default: its index).
    public static List<byte[]> Pack(int blockAlignment, int frames, Func<int, long> bitsOf, Action<LosslessBits, int> write, Func<int, int>? sequence = null)
    {
        int width = WidthOf(blockAlignment);
        long payload = (blockAlignment * 8L) - 6 - width;
        var starts = new long[frames + 1];
        for (int i = 0; i < frames; i++)
        {
            starts[i + 1] = starts[i] + bitsOf(i);
        }

        long total = starts[frames];
        var all = new LosslessBits();
        for (int i = 0; i < frames; i++)
        {
            write(all, i);
            bool more = i + 1 < frames && starts[i + 1] / payload == starts[i] / payload;
            all.PutBit(more);
        }

        if (all.Count != total)
        {
            throw new InvalidOperationException($"The frames wrote {all.Count} bits, not {total}.");
        }

        int packets = (int)((total + payload - 1) / payload);
        var objects = new List<byte[]>(packets);
        int next = 0;
        for (int k = 0; k < packets; k++)
        {
            long from = k * payload;
            long to = Math.Min(total, from + payload);
            while (next < frames && starts[next] < from)
            {
                next++;
            }

            long first = next < frames && starts[next] < from + payload ? starts[next] : -1;
            long count = first < 0 ? 8L * blockAlignment : first - from;
            var header = new LosslessBits();
            header.Put(4, (ulong)(sequence?.Invoke(k) ?? (k & 15)));
            header.Put(1, 0);
            header.Put(1, 0);
            header.Put(width, (ulong)count);
            byte[] packet = new byte[blockAlignment];
            for (long b = 0; b < 6 + width; b++)
            {
                if (header.Bit(b))
                {
                    packet[b >> 3] |= (byte)(0x80 >> (int)(b & 7));
                }
            }

            for (long b = from; b < to; b++)
            {
                if (all.Bit(b))
                {
                    long at = 6 + width + (b - from);
                    packet[at >> 3] |= (byte)(0x80 >> (int)(at & 7));
                }
            }

            objects.Add(packet);
        }

        return objects;
    }

    // Raw PCM frames of one channel (32,774 bits each, trailer bit included).
    public static List<byte[]> RawStream(int blockAlignment, int frames, Func<int, int>? sequence = null) =>
        Pack(blockAlignment, frames, _ => 5 + (FrameLength * 16) + 1, (w, _) => RawFrame(w, 1), sequence);

    // One seekable frame and then tiny ones (7 bits each), of one channel.
    public static List<byte[]> TinyStream(int blockAlignment, int frames)
    {
        long seekable = 0;
        var probe = new LosslessBits();
        SeekableFrame(probe, 1);
        seekable = probe.Count + 1;
        return Pack(blockAlignment, frames, i => i == 0 ? seekable : 7, (w, i) =>
        {
            if (i == 0)
            {
                SeekableFrame(w, 1);
            }
            else
            {
                TinyFrame(w, 1);
            }
        });
    }

    public static CodecContext Run(List<byte[]> objects, int channels, int blockAlignment, WmaLosslessParser? parser = null, int flags = 0x0001)
    {
        var parent = new Node("Stream", 0, 1_000_000_000);
        var packets = objects.Select((o, i) => new MappedBytes(o, 200 + (i * (long)(blockAlignment + 100)))).ToList();
        var context = new CodecContext(parent, packets, CancellationToken.None, null, null, new CodecConfiguration(new MappedBytes(Data(flags), 100), parent) { Format = Format(channels, blockAlignment) });
        (parser ?? new WmaLosslessParser()).Parse(context);
        return context;
    }
}
