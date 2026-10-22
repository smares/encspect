using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Flac;

[InlineArray(SubframeLayout.MaxOrder)]
internal struct CoefficientArray
{
    private int _first;
}

internal enum SubframeType : byte
{
    Constant,
    Verbatim,
    Fixed,
    Lpc,
}

// Why a subframe cannot be read to its end (RFC 9639, section 9.2).
internal enum SubframeFault : byte
{
    None,

    // The data ends inside the subframe.
    Overrun,

    // The first bit of the subframe header is not 0.
    ReservedBit,

    // A type code the specification reserves.
    ReservedType,

    // More wasted bits than the sample has.
    WastedBits,

    // A predictor order above the block size.
    Order,

    // The forbidden coefficient precision (all four bits set).
    Precision,

    // A negative coefficient shift.
    Shift,

    // A residual coding method the specification reserves.
    ResidualMethod,

    // A partition order for which the block size does not divide into whole partitions, or the first partition would hold fewer samples than the predictor order.
    PartitionOrder,

    // A Rice code whose value does not fit 32 bits.
    ResidualOverflow,

    // The frame runs past 16 MiB, the most STREAMINFO can state for a frame.
    TooLong,
}

// Where a subframe's samples go: nowhere (the structure pass), 32-bit buffers, or 64-bit ones as well for the side channel of a 32-bit
// stream, which needs 33 bits (RFC 9639, section 9.2.3). A channel's buffers hold BlockSize samples and then SubframeLayout.MetaSlots
// integers that describe the subframe, so a decoder takes everything the walk read from them.
internal readonly ref struct SubframeSamples
{
    public SubframeSamples(Span<int> narrow, Span<long> wide)
    {
        Narrow = narrow;
        Wide = wide;
    }

    public Span<int> Narrow { get; }

    public Span<long> Wide { get; }

    public bool IsEmpty => Narrow.IsEmpty;
}

// The integers a walk stores after a channel's samples: what a decoder needs to rebuild them.
internal static class SubframeLayout
{
    public const int Type = 0;
    public const int Order = 1;
    public const int WastedBits = 2;
    public const int Precision = 3;
    public const int Shift = 4;
    public const int IsWide = 5;
    public const int Coefficients = 6;
    public const int MaxOrder = 32;
    public const int MetaSlots = Coefficients + MaxOrder;
}

// The Rice parameters and start bits of the first partitions of one subframe, for the nodes of a frame the report details.
internal sealed class PartitionLog
{
    public const int Capacity = 32;

    // One more than the capacity: the start of the first partition that is not logged.
    public int[] StartBits { get; } = new int[Capacity + 1];

    public byte[] Parameters { get; } = new byte[Capacity];

    public bool[] IsEscaped { get; } = new bool[Capacity];

    public int Count { get; set; }
}

internal struct SubframeFacts
{
    public SubframeType Type;
    public SubframeFault Fault;

    // Bits from the start of the frame.
    public int StartBit;
    public int EndBit;

    public int Order;
    public int WastedBits;

    // Bits of one sample after the wasted bits are taken off.
    public int SampleBits;
    public bool IsWide;
    public int Precision;
    public int Shift;

    // 0 Rice with 4-bit parameters, 1 with 5-bit ones, -1 for a subframe without residual.
    public int ResidualMethod;
    public int PartitionOrder;
    public int ResidualStartBit;
    public int EscapedPartitions;
    public int MinParameter;
    public int MaxParameter;

    // The value of a constant subframe, before the wasted bits are put back.
    public long Constant;

    public CoefficientArray Coefficients;
}

// Reads subframes (RFC 9639, section 9.2): the parser's one reading of the coded syntax, with the decoder calling it too. Given
// buffers it also stores the samples (warm-up samples and residual) and the description a decoder rebuilds them from.
internal static class SubframeSyntax
{
    // The bits of a subframe header: the padding bit, the type, the wasted bits flag and, where there are wasted bits, their unary count.
    public static int HeaderBits(int wastedBits) => 8 + wastedBits;

    public static SubframeFacts Read(ref BitCursor bits, int blockSize, int frameBits, bool isSide, SubframeSamples samples, PartitionLog? log = null)
    {
        var facts = new SubframeFacts { StartBit = bits.Position, ResidualMethod = -1, MinParameter = -1, MaxParameter = -1 };
        int sampleBits = frameBits + (isSide ? 1 : 0);

        // The samples come out 33 bits wide when the side channel of a 32-bit stream is shifted back up by its wasted bits, even if the
        // coded samples fit 32, so the subframe is wide by the width before the wasted bits are taken off.
        bool isWide = sampleBits > 32;
        if (bits.ReadBit())
        {
            return Fail(ref bits, facts, SubframeFault.ReservedBit);
        }

        int code = (int)bits.Read(6);
        if (code == 0)
        {
            facts.Type = SubframeType.Constant;
        }
        else if (code == 1)
        {
            facts.Type = SubframeType.Verbatim;
        }
        else if (code is >= 8 and <= 12)
        {
            facts.Type = SubframeType.Fixed;
            facts.Order = code - 8;
        }
        else if (code >= 32)
        {
            facts.Type = SubframeType.Lpc;
            facts.Order = (code & 31) + 1;
        }
        else
        {
            return Fail(ref bits, facts, SubframeFault.ReservedType);
        }

        if (bits.ReadBit())
        {
            int zeros = bits.CountZeros(sampleBits);
            facts.WastedBits = zeros + 1;
            if (zeros >= sampleBits || facts.WastedBits >= sampleBits)
            {
                return Fail(ref bits, facts, SubframeFault.WastedBits);
            }
        }

        sampleBits -= facts.WastedBits;
        facts.SampleBits = sampleBits;
        facts.IsWide = isWide;
        bool store = !samples.IsEmpty;
        Span<int> narrow = samples.Narrow;
        Span<long> wide = samples.Wide;
        if (store)
        {
            if (narrow.Length < blockSize + SubframeLayout.MetaSlots || (isWide && wide.Length < blockSize))
            {
                throw new InvalidOperationException("The buffers are shorter than the subframe.");
            }

            Span<int> meta = narrow[blockSize..];
            meta[SubframeLayout.Type] = (int)facts.Type;
            meta[SubframeLayout.Order] = facts.Order;
            meta[SubframeLayout.WastedBits] = facts.WastedBits;
            meta[SubframeLayout.IsWide] = isWide ? 1 : 0;
        }

        switch (facts.Type)
        {
            case SubframeType.Constant:
                long constant = isWide ? bits.ReadSignedLong(sampleBits) : bits.ReadSigned(sampleBits);
                facts.Constant = constant;
                if (store)
                {
                    if (isWide)
                    {
                        wide[0] = constant;
                    }
                    else
                    {
                        narrow[0] = (int)constant;
                    }
                }

                break;
            case SubframeType.Verbatim:
                if (!store)
                {
                    bits.Skip(checked(blockSize * sampleBits));
                }
                else if (isWide)
                {
                    for (int i = 0; i < blockSize; i++)
                    {
                        wide[i] = bits.ReadSignedLong(sampleBits);
                    }
                }
                else
                {
                    for (int i = 0; i < blockSize; i++)
                    {
                        narrow[i] = bits.ReadSigned(sampleBits);
                    }
                }

                break;
            default:
                if (facts.Order > blockSize)
                {
                    return Fail(ref bits, facts, SubframeFault.Order);
                }

                if (store && isWide)
                {
                    for (int i = 0; i < facts.Order; i++)
                    {
                        wide[i] = bits.ReadSignedLong(sampleBits);
                    }
                }
                else if (store)
                {
                    for (int i = 0; i < facts.Order; i++)
                    {
                        narrow[i] = bits.ReadSigned(sampleBits);
                    }
                }
                else
                {
                    bits.Skip(facts.Order * sampleBits);
                }

                if (facts.Type == SubframeType.Lpc)
                {
                    int precisionCode = (int)bits.Read(4);
                    if (precisionCode == 15)
                    {
                        return Fail(ref bits, facts, SubframeFault.Precision);
                    }

                    facts.Precision = precisionCode + 1;
                    facts.Shift = bits.ReadSigned(5);
                    if (facts.Shift < 0)
                    {
                        return Fail(ref bits, facts, SubframeFault.Shift);
                    }

                    for (int i = 0; i < facts.Order; i++)
                    {
                        facts.Coefficients[i] = bits.ReadSigned(facts.Precision);
                    }

                    if (store)
                    {
                        Span<int> meta = narrow[blockSize..];
                        meta[SubframeLayout.Precision] = facts.Precision;
                        meta[SubframeLayout.Shift] = facts.Shift;
                        for (int i = 0; i < facts.Order; i++)
                        {
                            meta[SubframeLayout.Coefficients + i] = facts.Coefficients[i];
                        }
                    }
                }

                facts.ResidualStartBit = bits.Position;
                SubframeFault fault = ReadResidual(ref bits, ref facts, blockSize, store ? narrow : default, isWide ? wide : default, log);
                if (fault != SubframeFault.None)
                {
                    return Fail(ref bits, facts, fault);
                }

                break;
        }

        facts.EndBit = bits.Position;
        if (bits.IsOverrun)
        {
            facts.Fault = SubframeFault.Overrun;
        }

        return facts;
    }

    private static SubframeFacts Fail(ref BitCursor bits, SubframeFacts facts, SubframeFault fault)
    {
        facts.Fault = bits.IsOverrun ? SubframeFault.Overrun : fault;
        facts.EndBit = bits.Position;
        return facts;
    }

    // RFC 9639, section 9.2.7. The residual of sample i goes to buffer[i] for i from the predictor order on.
    private static SubframeFault ReadResidual(ref BitCursor bits, ref SubframeFacts facts, int blockSize, Span<int> narrow, Span<long> wide, PartitionLog? log)
    {
        int method = (int)bits.Read(2);
        if (method > 1)
        {
            return SubframeFault.ResidualMethod;
        }

        facts.ResidualMethod = method;
        int partitionOrder = (int)bits.Read(4);
        facts.PartitionOrder = partitionOrder;
        int partitions = 1 << partitionOrder;
        if ((blockSize & (partitions - 1)) != 0 || (blockSize >> partitionOrder) < facts.Order)
        {
            return SubframeFault.PartitionOrder;
        }

        if (log is not null)
        {
            log.Count = 0;
        }

        int parameterBits = method == 0 ? 4 : 5;
        int escape = (1 << parameterBits) - 1;
        int perPartition = blockSize >> partitionOrder;
        int at = facts.Order;
        bool store = !narrow.IsEmpty;
        bool isWide = !wide.IsEmpty;
        int minimum = int.MaxValue;
        int maximum = -1;
        for (int partition = 0; partition < partitions; partition++)
        {
            int count = partition == 0 ? perPartition - facts.Order : perPartition;
            int start = bits.Position;
            int parameter = (int)bits.Read(parameterBits);
            bool isEscaped = parameter == escape;
            if (log is not null && partition < PartitionLog.Capacity)
            {
                log.StartBits[partition] = start;
                log.Parameters[partition] = (byte)parameter;
                log.IsEscaped[partition] = isEscaped;
                log.Count = partition + 1;
            }
            else if (log is not null && partition == PartitionLog.Capacity)
            {
                log.StartBits[partition] = start;
            }

            if (isEscaped)
            {
                facts.EscapedPartitions++;
                int width = (int)bits.Read(5);
                if (log is not null && partition < PartitionLog.Capacity)
                {
                    log.Parameters[partition] = (byte)width;
                }

                if (width == 0)
                {
                    if (store)
                    {
                        if (isWide)
                        {
                            wide.Slice(at, count).Clear();
                        }
                        else
                        {
                            narrow.Slice(at, count).Clear();
                        }
                    }
                }
                else if (!store)
                {
                    bits.Skip(checked(count * width));
                }
                else if (isWide)
                {
                    for (int i = 0; i < count; i++)
                    {
                        wide[at + i] = bits.ReadSigned(width);
                    }
                }
                else
                {
                    for (int i = 0; i < count; i++)
                    {
                        narrow[at + i] = bits.ReadSigned(width);
                    }
                }
            }
            else
            {
                minimum = Math.Min(minimum, parameter);
                maximum = Math.Max(maximum, parameter);
                uint limit = uint.MaxValue >> parameter;
                if (!store)
                {
                    for (int i = 0; i < count; i++)
                    {
                        int quotient = bits.CountZeros(int.MaxValue);
                        if ((uint)quotient > limit)
                        {
                            return SubframeFault.ResidualOverflow;
                        }

                        bits.Skip(parameter);
                    }
                }
                else if (isWide)
                {
                    for (int i = 0; i < count; i++)
                    {
                        int quotient = bits.CountZeros(int.MaxValue);
                        if ((uint)quotient > limit)
                        {
                            return SubframeFault.ResidualOverflow;
                        }

                        uint folded = ((uint)quotient << parameter) | bits.Read(parameter);
                        wide[at + i] = (int)(folded >> 1) ^ -(int)(folded & 1);
                    }
                }
                else
                {
                    for (int i = 0; i < count; i++)
                    {
                        int quotient = bits.CountZeros(int.MaxValue);
                        if ((uint)quotient > limit)
                        {
                            return SubframeFault.ResidualOverflow;
                        }

                        uint folded = ((uint)quotient << parameter) | bits.Read(parameter);
                        narrow[at + i] = (int)(folded >> 1) ^ -(int)(folded & 1);
                    }
                }
            }

            at += count;
            if (bits.IsOverrun)
            {
                return SubframeFault.Overrun;
            }
        }

        facts.MinParameter = maximum < 0 ? -1 : minimum;
        facts.MaxParameter = maximum;
        return SubframeFault.None;
    }
}
