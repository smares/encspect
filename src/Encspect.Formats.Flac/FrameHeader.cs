using System.Buffers.Binary;

namespace Encspect.Formats.Flac;

internal enum ChannelAssignment : byte
{
    Independent,
    LeftSide,
    SideRight,
    MidSide,
}

// Why bytes do not read as a frame header (RFC 9639, section 9.1).
internal enum HeaderFault : byte
{
    None,

    // The data ends inside the header.
    Truncated,

    // The 14-bit sync code is not there.
    Sync,

    // A reserved bit is set.
    ReservedBit,

    // The reserved block size code 0, or a block of 65,536 samples, which the 16-bit field could state but no stream can hold.
    BlockSize,

    // The forbidden sample rate code 15, or a stated rate of 0.
    SampleRate,

    // A reserved channel assignment.
    ChannelAssignment,

    // The reserved sample size code 3.
    SampleSize,

    // The coded number is not a well-formed UTF-8 style number, or is too large for its blocking strategy.
    Number,

    // Everything reads, but the CRC-8 differs.
    Crc8,
}

// A frame header (RFC 9639, section 9.1). SampleRate and BitsPerSample are 0 where the header leaves them to STREAMINFO.
internal readonly record struct FrameHeader
{
    public const int MinLength = 6;
    public const int MaxLength = 16;

    public int Length { get; init; }

    public bool IsVariable { get; init; }

    public int BlockSizeCode { get; init; }

    public int BlockSize { get; init; }

    public int SampleRateCode { get; init; }

    public int SampleRate { get; init; }

    public int ChannelCode { get; init; }

    public ChannelAssignment Assignment => ChannelCode switch { 8 => ChannelAssignment.LeftSide, 9 => ChannelAssignment.SideRight, 10 => ChannelAssignment.MidSide, _ => ChannelAssignment.Independent };

    public int Channels => ChannelCode < 8 ? ChannelCode + 1 : 2;

    public int SampleSizeCode { get; init; }

    public int BitsPerSample { get; init; }

    // The frame number of a stream of fixed block size, or the number of the frame's first sample of a stream of variable block size.
    public long Number { get; init; }

    public int NumberLength { get; init; }

    // Bytes of the stated block size and sample rate that follow the coded number: 0, 1 or 2 each.
    public int BlockSizeBytes => BlockSizeCode is 6 ? 1 : BlockSizeCode is 7 ? 2 : 0;

    public int SampleRateBytes => SampleRateCode is 12 ? 1 : SampleRateCode is 13 or 14 ? 2 : 0;

    public byte StoredCrc8 { get; init; }

    public byte ComputedCrc8 { get; init; }

    // This frame's figures where the header leaves them to STREAMINFO.
    public FrameHeader Resolve(int sampleRate, int bitsPerSample) =>
        this with { SampleRate = SampleRateCode == 0 ? sampleRate : SampleRate, BitsPerSample = BitsPerSample == 0 ? bitsPerSample : BitsPerSample };

    // The channel of a frame that holds the side signal, which has one bit more than the others (RFC 9639, section 9.2).
    public bool IsSide(int channel) => Assignment switch
    {
        ChannelAssignment.LeftSide or ChannelAssignment.MidSide => channel == 1,
        ChannelAssignment.SideRight => channel == 0,
        _ => false,
    };

    public static bool HasSync(ReadOnlySpan<byte> data) => data.Length >= 2 && data[0] == 0xFF && (data[1] & 0xFC) == 0xF8;

    private static readonly long[] Smallest = [0, 0, 0x80, 0x800, 0x10000, 0x200000, 0x4000000, 0x80000000];

    private static readonly int[] FixedRates = [0, 88200, 176400, 192000, 8000, 16000, 22050, 24000, 32000, 44100, 48000, 96000];

    // Reads the header at the start of data. On Crc8 the header is complete and the fields are those it states.
    public static HeaderFault TryRead(ReadOnlySpan<byte> data, out FrameHeader header)
    {
        header = default;
        if ((data.Length >= 1 && data[0] != 0xFF) || (data.Length >= 2 && !HasSync(data)))
        {
            return HeaderFault.Sync;
        }

        if (data.Length < 4)
        {
            return HeaderFault.Truncated;
        }

        if ((data[1] & 2) != 0 || (data[3] & 1) != 0)
        {
            return HeaderFault.ReservedBit;
        }

        bool isVariable = (data[1] & 1) != 0;
        int blockSizeCode = data[2] >> 4;
        int rateCode = data[2] & 0xF;
        int channelCode = data[3] >> 4;
        int sizeCode = (data[3] >> 1) & 7;
        if (blockSizeCode == 0)
        {
            return HeaderFault.BlockSize;
        }

        if (rateCode == 15)
        {
            return HeaderFault.SampleRate;
        }

        if (channelCode > 10)
        {
            return HeaderFault.ChannelAssignment;
        }

        if (sizeCode == 3)
        {
            return HeaderFault.SampleSize;
        }

        int at = 4;
        HeaderFault fault = ReadNumber(data, isVariable, ref at, out long number, out int numberLength);
        if (fault != HeaderFault.None)
        {
            return fault;
        }

        int blockSize;
        switch (blockSizeCode)
        {
            case 1:
                blockSize = 192;
                break;
            case 6:
                if (data.Length < at + 1)
                {
                    return HeaderFault.Truncated;
                }

                blockSize = data[at++] + 1;
                break;
            case 7:
                if (data.Length < at + 2)
                {
                    return HeaderFault.Truncated;
                }

                blockSize = BinaryPrimitives.ReadUInt16BigEndian(data[at..]) + 1;
                at += 2;
                if (blockSize > 65535)
                {
                    return HeaderFault.BlockSize;
                }

                break;
            default:
                blockSize = blockSizeCode < 6 ? 576 << (blockSizeCode - 2) : 256 << (blockSizeCode - 8);
                break;
        }

        int sampleRate;
        switch (rateCode)
        {
            case 12:
                if (data.Length < at + 1)
                {
                    return HeaderFault.Truncated;
                }

                sampleRate = data[at++] * 1000;
                break;
            case 13:
            case 14:
                if (data.Length < at + 2)
                {
                    return HeaderFault.Truncated;
                }

                sampleRate = BinaryPrimitives.ReadUInt16BigEndian(data[at..]) * (rateCode == 14 ? 10 : 1);
                at += 2;
                break;
            default:
                sampleRate = FixedRates[rateCode];
                break;
        }

        if (data.Length < at + 1)
        {
            return HeaderFault.Truncated;
        }

        byte computed = FlacCrc.Crc8(data[..at]);
        header = new FrameHeader
        {
            Length = at + 1,
            IsVariable = isVariable,
            BlockSizeCode = blockSizeCode,
            BlockSize = blockSize,
            SampleRateCode = rateCode,
            SampleRate = sampleRate,
            ChannelCode = channelCode,
            SampleSizeCode = sizeCode,
            BitsPerSample = sizeCode switch { 1 => 8, 2 => 12, 4 => 16, 5 => 20, 6 => 24, 7 => 32, _ => 0 },
            Number = number,
            NumberLength = numberLength,
            StoredCrc8 = data[at],
            ComputedCrc8 = computed,
        };
        return computed == data[at] ? HeaderFault.None : HeaderFault.Crc8;
    }

    // The number is coded as UTF-8 is, extended to seven bytes for 36 bits (RFC 9639, section 9.1.5), in its shortest form.
    private static HeaderFault ReadNumber(ReadOnlySpan<byte> data, bool isVariable, ref int at, out long number, out int length)
    {
        number = 0;
        length = 0;
        if (data.Length <= at)
        {
            return HeaderFault.Truncated;
        }

        byte first = data[at];
        int count;
        if (first < 0x80)
        {
            count = 1;
            number = first;
        }
        else if ((first & 0xE0) == 0xC0)
        {
            count = 2;
            number = first & 0x1F;
        }
        else if ((first & 0xF0) == 0xE0)
        {
            count = 3;
            number = first & 0x0F;
        }
        else if ((first & 0xF8) == 0xF0)
        {
            count = 4;
            number = first & 0x07;
        }
        else if ((first & 0xFC) == 0xF8)
        {
            count = 5;
            number = first & 0x03;
        }
        else if ((first & 0xFE) == 0xFC)
        {
            count = 6;
            number = first & 0x01;
        }
        else if (first == 0xFE)
        {
            count = 7;
        }
        else
        {
            return HeaderFault.Number;
        }

        if (data.Length < at + count)
        {
            return HeaderFault.Truncated;
        }

        for (int i = 1; i < count; i++)
        {
            byte next = data[at + i];
            if ((next & 0xC0) != 0x80)
            {
                return HeaderFault.Number;
            }

            number = (number << 6) | (long)(next & 0x3F);
        }

        // A longer form than the number needs is not allowed; six bytes hold 31 bits, which a frame number may use; seven hold 36.
        if (count > 1 && number < Smallest[count])
        {
            return HeaderFault.Number;
        }

        if (!isVariable && number > int.MaxValue)
        {
            return HeaderFault.Number;
        }

        at += count;
        length = count;
        return HeaderFault.None;
    }
}
