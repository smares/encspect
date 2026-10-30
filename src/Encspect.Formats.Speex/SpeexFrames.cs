// The walk over a packet's frames (Walk, SkipLayer) follows libspeex 1.2.1 (nb_decode and wb_skip_table of nb_celp.c, sb_decode of sb_celp.c, speex_inband_handler of speex_callbacks.c),
// and the tables of bits per submode and of in-band request sizes are taken from it (modes.c, modes_wb.c, speex_callbacks.c),
// Copyright (c) 2002-2008 Jean-Marc Valin and Xiph.org Foundation and the other holders named in its COPYING, under the BSD 3-clause license.
// The license text is in THIRD-PARTY-NOTICES.md.

using Encspect.Core;

namespace Encspect.Formats.Speex;

// One frame as a Speex decoder walks it: the bits of in-band and user data and of skipped wideband
// layers before its narrowband layer, the narrowband submode, and the submodes of its wideband and
// ultra-wideband layers, or Absent. Four bytes, since a stream has fifty frames a second.
internal readonly record struct FrameRecord(ushort PrefixBits, byte Narrowband, byte High)
{
    public const byte Absent = 0xF;

    public int Wideband => High & 0xF;

    public int UltraWideband => High >> 4;

    public int Bits => PrefixBits + SpeexFrames.NarrowbandBits[Narrowband]
        + (Wideband == Absent ? 0 : SpeexFrames.WidebandBits[Wideband])
        + (UltraWideband == Absent ? 0 : SpeexFrames.UltraWidebandBits[UltraWideband]);
}

internal enum WalkEnd
{
    // The packet's bits ran out before another frame header (fewer than 5 bits remain).
    EndOfPacket,

    // A narrowband submode of 15, the terminator speex_bits_insert_terminator writes.
    Terminator,

    // As many frames as the walk reads.
    FrameLimit,

    // A submode the mode does not define: decoders stop decoding the packet.
    Invalid,

    // A frame that runs past the end of the packet: decoders read zero bits there and stop.
    Overrun,
}

internal readonly record struct PacketWalk(int Frames, int EndBit, WalkEnd End, int ErrorBit, string? Error, int InBand, int Stereo, int StrayLayers);

// Walks a packet's frames the way libspeex 1.2.1 decodes them (nb_celp.c nb_decode, sb_celp.c
// sb_decode, speex_callbacks.c speex_inband_handler), reading only the bits that say how long each
// part is: a frame starts with the narrowband layer, whose first bit is 0 and whose next 4 bits are
// its submode, 13 (user in-band data), 14 (in-band data, such as the stereo parameters) or 15
// (the terminator); in a wideband stream a wideband layer follows, marked by a 1 bit and a 3-bit
// submode, and in an ultra-wideband stream another such layer. A 1 bit where a narrowband layer
// starts is a wideband layer a narrowband decoder skips. Bits are read most significant first
// (bits.c speex_bits_unpack_unsigned).
internal static class SpeexFrames
{
    // SPEEX_SUBMODE_BITS_PER_FRAME of each submode (speex_mode_query on speex_nb_mode,
    // speex_wb_mode and speex_uwb_mode, libspeex 1.2.1), header bits included: a narrowband
    // submode 0 frame is its 5 header bits, a wideband layer of submode 0 its 4. -1 marks a submode
    // the mode does not define.
    public static ReadOnlySpan<int> NarrowbandBits => [5, 43, 119, 160, 220, 300, 364, 492, 79];

    public static ReadOnlySpan<int> WidebandBits => [4, 36, 112, 192, 352, -1, -1, -1];

    public static ReadOnlySpan<int> UltraWidebandBits => [4, 36, -1, -1, -1, -1, -1, -1];

    // The bits speex_inband_handler skips for an in-band request it has no handler for, by its
    // 4-bit ID; the standard handlers (stereo, mode requests) read as many.
    private static ReadOnlySpan<int> InBandBits => [1, 1, 4, 4, 4, 4, 4, 4, 8, 8, 16, 16, 32, 32, 64, 64];

    private const int StereoRequest = 9;

    // mode: 0 narrowband, 1 wideband, 2 ultra-wideband. At most maxFrames frames are read. Wideband layers are counted as stray
    // only before the first countedFrames frames are complete: a decoder reads no further.
    public static PacketWalk Walk(ReadOnlySpan<byte> data, int mode, int maxFrames, ChunkedList<FrameRecord> frames, int countedFrames = int.MaxValue)
    {
        var bits = new Bits(data);
        int total = data.Length * 8;
        int count = 0;
        int inBand = 0;
        int stereo = 0;
        int stray = 0;

        while (true)
        {
            int frameStart = bits.Position;
            if (count == maxFrames)
            {
                return new PacketWalk(count, frameStart, WalkEnd.FrameLimit, 0, null, inBand, stereo, stray);
            }

            int m;
            while (true)
            {
                if (total - bits.Position < 5)
                {
                    return new PacketWalk(count, frameStart, WalkEnd.EndOfPacket, 0, null, inBand, stereo, stray);
                }

                int at = bits.Position;
                if (bits.Read(1) == 1)
                {
                    // nb_decode skips up to two wideband layers "for compatibility".
                    if (SkipLayer(ref bits, WidebandBits) is { } error)
                    {
                        return Fail(count, frameStart, at, error, inBand, stereo, stray);
                    }

                    // Only a layer libspeex skips counts as one a narrowband decoder skips.
                    stray += count < countedFrames ? 1 : 0;

                    if (total - bits.Position < 5)
                    {
                        return new PacketWalk(count, frameStart, WalkEnd.EndOfPacket, 0, null, inBand, stereo, stray);
                    }

                    if (bits.Read(1) == 1)
                    {
                        if (SkipLayer(ref bits, WidebandBits) is { } second)
                        {
                            return Fail(count, frameStart, at, second, inBand, stereo, stray);
                        }

                        if (bits.Read(1) == 1)
                        {
                            return Fail(count, frameStart, at, "more than two wideband layers come before a narrowband layer", inBand, stereo, stray);
                        }
                    }
                }

                if (total - bits.Position < 4)
                {
                    return new PacketWalk(count, frameStart, WalkEnd.EndOfPacket, 0, null, inBand, stereo, stray);
                }

                m = bits.Read(4);
                if (m == 15)
                {
                    return new PacketWalk(count, frameStart, WalkEnd.Terminator, 0, null, inBand, stereo, stray);
                }

                if (m == 14)
                {
                    int id = bits.Read(4);
                    inBand++;
                    stereo += id == StereoRequest ? 1 : 0;
                    bits.Advance(InBandBits[id]);
                }
                else if (m == 13)
                {
                    // speex_default_user_handler: a 4-bit size, then 5 + 8 * size bits.
                    int size = bits.Read(4);
                    inBand++;
                    bits.Advance(5 + (8 * size));
                }
                else if (m > 8)
                {
                    return Fail(count, frameStart, bits.Position - 4, $"narrowband submode {m} is not defined", inBand, stereo, stray);
                }
                else
                {
                    break;
                }

                if (bits.IsOverrun)
                {
                    return new PacketWalk(count, frameStart, WalkEnd.Overrun, frameStart, "in-band data runs past the end of the packet", inBand, stereo, stray);
                }
            }

            int prefix = bits.Position - 5 - frameStart;
            if (prefix > ushort.MaxValue)
            {
                return Fail(count, frameStart, frameStart, "more in-band data comes before the frame than the walk follows", inBand, stereo, stray);
            }

            bits.Advance(NarrowbandBits[m] - 5);
            int wideband = FrameRecord.Absent;
            int ultra = FrameRecord.Absent;
            if (mode >= 1 && !bits.IsOverrun)
            {
                if (PeekLayer(ref bits, total, WidebandBits, out wideband) is { } error)
                {
                    return Fail(count, frameStart, bits.Position - 4, $"wideband submode {wideband} is not defined", inBand, stereo, stray, error);
                }

                if (mode >= 2 && !bits.IsOverrun && PeekLayer(ref bits, total, UltraWidebandBits, out ultra) is { } uwb)
                {
                    return Fail(count, frameStart, bits.Position - 4, $"ultra-wideband submode {ultra} is not defined", inBand, stereo, stray, uwb);
                }
            }

            if (bits.IsOverrun)
            {
                return new PacketWalk(count, frameStart, WalkEnd.Overrun, frameStart, "the frame runs past the end of the packet", inBand, stereo, stray);
            }

            frames.Add(new FrameRecord((ushort)prefix, (byte)m, (byte)(wideband | (ultra << 4))));
            count++;
        }
    }

    // A wideband layer after the 1 bit that marks it: a 3-bit submode, then the rest of its bits. nb_decode skips by its own
    // wb_skip_table {0, 36, 112, 192, 352, 0, 0, 0} and then steps back over the 4 bits it has read, so a submode of 0, 5, 6 or 7
    // puts it back before the layer's marker bit, which it reads again as a wideband layer, until "More than two wideband layers
    // found. The stream is corrupted." (nb_celp.c, 1.2.1; the commented-out speex_mode_query would have skipped submode 0).
    private static string? SkipLayer(ref Bits bits, ReadOnlySpan<int> table)
    {
        int submode = bits.Read(3);
        if (submode is 0 or > 4)
        {
            return $"a skipped wideband layer has submode {submode}, which libspeex 1.2.1 cannot skip: it reads the layer again and reports a corrupted stream";
        }

        bits.Advance(table[submode] - 4);
        return null;
    }

    // sb_decode: a 1 bit next means a layer of this kind follows; anything else, or no bits left,
    // means the frame has none.
    private static string? PeekLayer(ref Bits bits, int total, ReadOnlySpan<int> table, out int submode)
    {
        submode = FrameRecord.Absent;
        if (total - bits.Position <= 0)
        {
            return null;
        }

        if (bits.Peek() == 0)
        {
            return null;
        }

        bits.Read(1);

        submode = bits.Read(3);
        if (table[submode] < 0)
        {
            return "undefined";
        }

        bits.Advance(table[submode] - 4);
        return null;
    }

    private static PacketWalk Fail(int count, int frameStart, int errorBit, string error, int inBand, int stereo, int stray, string? _ = null) =>
        new(count, frameStart, WalkEnd.Invalid, errorBit, error, inBand, stereo, stray);

    // Most significant bit first, zero bits past the end, as speex_bits_unpack_unsigned reads them
    // once the data has overflowed.
    private ref struct Bits(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public int Position { get; private set; }

        public readonly bool IsOverrun => Position > _data.Length * 8;

        public readonly int Peek() => Bit(Position);

        public int Read(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                value = (value << 1) | Bit(Position + i);
            }

            Position += count;
            return value;
        }

        public void Advance(int count) => Position += count;

        private readonly int Bit(int position) => position < _data.Length * 8 ? (_data[position >> 3] >> (7 - (position & 7))) & 1 : 0;
    }
}
