using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Tests;

// Builds ALAC cookies, frames and MP4 files bit by bit from Apple's cookie description and the element syntax of its decoder, so a test changes one thing and
// nothing else. The frames hold uncompressed (escape) elements: the analyzer walks the syntax and does not decode.
internal static class AlacBuilder
{
    public const int ConfigLength = 24;

    public static byte[] Cookie(uint frameLength = 64, int bits = 16, int channels = 2, uint rate = 44100, int pb = 40, int mb = 10, int kb = 14, int version = 0, uint maxFrameBytes = 0)
    {
        byte[] bytes = new byte[ConfigLength];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, frameLength);
        bytes[4] = (byte)version;
        bytes[5] = (byte)bits;
        bytes[6] = (byte)pb;
        bytes[7] = (byte)mb;
        bytes[8] = (byte)kb;
        bytes[9] = (byte)channels;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), 255);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), maxFrameBytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), rate);
        return bytes;
    }

    // The cookie in an alac atom of 36 bytes, with a format atom before it and a terminator atom after it for the older form.
    public static byte[] AtomCookie(byte[] cookie, bool older = false)
    {
        byte[] atom = [.. Mp4Builder.U32(12 + cookie.Length), .. "alac"u8, 0, 0, 0, 0, .. cookie];
        return older ? [.. Mp4Builder.U32(12), .. "frma"u8, .. "alac"u8, .. atom, .. Mp4Builder.U32(8), 0, 0, 0, 0] : atom;
    }

    private sealed class Bits
    {
        private readonly List<byte> _bytes = [];
        private int _used;

        public void Write(long value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                if (_used == 0)
                {
                    _bytes.Add(0);
                }

                if (((value >> i) & 1) != 0)
                {
                    _bytes[^1] |= (byte)(0x80 >> _used);
                }

                _used = (_used + 1) % 8;
            }
        }

        public byte[] Bytes => [.. _bytes];
    }

    // A frame of one element holding `samples` samples of every channel uncompressed: a single channel element for one channel, a channel pair for two,
    // and the end tag. The samples are a ramp of the given seed, so frames differ.
    public static byte[] EscapeFrame(int channels, int bits, int samples, int frameLength, int seed = 0, bool endTag = true)
    {
        var w = new Bits();
        int tag = channels == 1 ? 0 : 1;
        w.Write(tag, 3);
        w.Write(0, 4);
        w.Write(0, 12);
        bool partial = samples != frameLength;
        w.Write(partial ? 1 : 0, 1);
        w.Write(0, 2);
        w.Write(1, 1);
        if (partial)
        {
            w.Write(samples, 32);
        }

        for (int i = 0; i < samples * channels; i++)
        {
            w.Write((seed + (i * 37)) & ((1L << bits) - 1), bits);
        }

        if (endTag)
        {
            w.Write(7, 3);
        }

        return w.Bytes;
    }

    // An MP4 file of one stereo or mono track of ALAC frames with the cookie in the alac box of its sample entry.
    public static byte[] M4a(IReadOnlyList<byte[]> frames, byte[] cookie, int channels = 2, int bits = 16, uint rate = 44100, long samplesPerFrame = 64, bool withCookie = true)
    {
        byte[] entry = Mp4Builder.Box("alac", [new byte[6], [0, 1], new byte[8], [0, (byte)channels, 0, (byte)bits, 0, 0, 0, 0, (byte)(rate >> 8), (byte)rate, 0, 0],
            .. withCookie ? [Mp4Builder.FullBox("alac", cookie)] : Array.Empty<byte[]>()]);
        byte[] mdat = Mp4Builder.Join([.. frames]);
        return Mp4Builder.FileWithMoov(offset => Mp4Builder.Trak(1, entry, Mp4Builder.Stts(frames.Count, samplesPerFrame), Mp4Builder.Stsc(frames.Count),
            Mp4Builder.Sizes(frames.Select(f => f.Length)), Mp4Builder.ChunkOffsets([offset])), mdat);
    }

    // An MP4 file of `count` mono frames of one sample, the fewest bytes a frame can have (6): one chunk, one size, so the file's own tables stay small.
    public static byte[] SmallFrames(int count)
    {
        byte[] frame = EscapeFrame(1, 16, 1, 1);
        byte[] cookie = Cookie(frameLength: 1, channels: 1);
        byte[] mdat = new byte[frame.Length * count];
        for (int i = 0; i < count; i++)
        {
            frame.CopyTo(mdat, i * frame.Length);
        }

        byte[] entry = Mp4Builder.Box("alac", [new byte[6], [0, 1], new byte[8], [0, 1, 0, 16, 0, 0, 0, 0, 0xAC, 0x44, 0, 0], Mp4Builder.FullBox("alac", cookie)]);
        return Mp4Builder.FileWithMoov(offset => Mp4Builder.Trak(1, entry, Mp4Builder.Stts(count, 1), Mp4Builder.Stsc(count), Mp4Builder.ConstantSizes(frame.Length, count),
            Mp4Builder.ChunkOffsets([offset])), mdat);
    }
}
