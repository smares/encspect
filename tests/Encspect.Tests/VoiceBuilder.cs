namespace Encspect.Tests;

// Windows Media Audio Voice packets built bit by bit, for the tests that need many of them or one of a given shape: the layout
// of wmavoice.c (a packet header, then superframes, each a speech bit, a sample count flag, residual LSPs, three frames and an
// additional data flag). The frames are silence (frame type 0 of the 8 kHz fixture's table: a 4-bit code and an 8-bit gain).
internal sealed class VoiceBitWriter
{
    private readonly List<byte> _bytes = [];
    private int _bits;

    public int Length => _bits;

    public VoiceBitWriter Write(uint value, int count)
    {
        for (int i = count - 1; i >= 0; i--)
        {
            if ((_bits & 7) == 0)
            {
                _bytes.Add(0);
            }

            if (((value >> i) & 1) != 0)
            {
                _bytes[^1] |= (byte)(0x80 >> (_bits & 7));
            }

            _bits++;
        }

        return this;
    }

    public VoiceBitWriter Append(VoiceBitWriter other)
    {
        for (int i = 0; i < other._bits; i++)
        {
            Write((uint)((other._bytes[i >> 3] >> (7 - (i & 7))) & 1), 1);
        }

        return this;
    }

    public byte[] ToArray(int bytes)
    {
        byte[] result = new byte[bytes];
        _bytes.CopyTo(0, result, 0, Math.Min(_bytes.Count, bytes));
        return result;
    }
}

internal static class VoiceBuilder
{
    // The code of frame type 0 in the 8 kHz fixture (external-windows-wmavoice-cbr-mono-8k-5.wma), whose table gives it slot 3.
    private const uint SilenceCode = 0b1100;
    private const int SilenceCodeBits = 4;

    // A superframe of three silence frames with residual LSPs (10 of them, 48 bits): 87 bits.
    public static VoiceBitWriter Superframe(bool residual = true, int? samples = null)
    {
        var bits = new VoiceBitWriter();
        bits.Write(1, 1);
        if (samples is { } count)
        {
            bits.Write(1, 1).Write((uint)count, 12);
        }
        else
        {
            bits.Write(0, 1);
        }

        if (residual)
        {
            bits.Write(0, 48);
        }

        for (int frame = 0; frame < 3; frame++)
        {
            if (!residual)
            {
                bits.Write(0, 24);
            }

            bits.Write(SilenceCode, SilenceCodeBits).Write(0, 8);
        }

        return bits.Write(0, 1);
    }

    // A packet of blockAlign bytes: the header with the sequence number, the residual flag, the count and the spillover, then the
    // bits given.
    public static byte[] Packet(int blockAlign, int sequence, bool residual, int count, int spillover, VoiceBitWriter body)
    {
        int spilloverBits = 3 + (blockAlign <= 1 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(blockAlign - 1)));
        var bits = new VoiceBitWriter();
        bits.Write((uint)sequence, 4).Write(residual ? 1u : 0, 1);
        int remaining = count;
        while (remaining >= 63)
        {
            bits.Write(63, 6);
            remaining -= 63;
        }

        bits.Write((uint)remaining, 6).Write((uint)spillover, spilloverBits);
        return bits.Append(body).ToArray(blockAlign);
    }
}
