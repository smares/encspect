// Follows Apple's ALAC reference implementation (macosforge/alac, ALACDecoder.cpp, Decode, and ag_dec.c, dyn_decomp), Apache License 2.0:
// Copyright (c) 2011 Apple Inc. See THIRD-PARTY-NOTICES.md.
using System.Buffers.Binary;
using System.Numerics;
using Encspect.Core;

namespace Encspect.Formats.Alac;

internal enum WalkFault : byte
{
    None,

    // The packet ends inside an element, or before the channels the cookie states are all there.
    Truncated,

    // A coupling channel element or a program config element, which ALAC defines no syntax for (ALACDecoder.cpp: "unsupported element, bail").
    UnknownElement,

    // The element header states a value the decoder cannot use: two bytes shifted off a 3rd time (the field is 2 bits, 3 is refused), or a sample
    // width of no bits or of more than 32.
    InvalidHeader,

    // A run of zeros goes past the samples of the element (ag_dec.c: "c+n <= numSamples").
    ZeroRun,

    // A compressed element, in a stream whose cookie states a maximum Golomb parameter of 0 or of more than 31, for which the reference code does not define the
    // residual code (ag_dec.c shifts by it as a 32-bit mask, and a parameter of 0 reads no bits).
    Tuning,

    // The 12 reserved bits of an element header are not 0, in an element the decoder reads (ALACDecoder.cpp: RequireAction(unusedHeader == 0)).
    ReservedBits,
}

// What the walk of one frame found: how far it got, what it saw and the first place of each oddity, as element indexes (-1 for none).
internal sealed class WalkResult
{
    public WalkFault Fault { get; set; }

    // The element the fault is in, and the bit of the frame it is found at.
    public int FaultElement { get; set; }

    // The channels the elements before the faulting element had read: the reference decoder reads no further
    // element once the cookie's channels are all there.
    public int FaultChannelsBefore { get; set; }


    // The tag of the element the fault is in, for an element the walk cannot read further.
    public int FaultTag { get; set; }

    public long FaultBit { get; set; }

    public bool HasEnd { get; set; }

    // All channels of the cookie were read and the packet ends before an end tag.
    public bool IsEndMissing { get; set; }

    // The bytes used, through the end tag and its padding to a byte boundary; 0 without an end tag.
    public int Walked { get; set; }

    // The elements read, END included.
    public int Elements { get; set; }

    // The frame's sample count: the count the last audio element read had. A partial element states it, and the elements after it that do not keep it
    // (ALACDecoder.cpp, Decode: numSamples is set by a partial element and stays).
    public uint Samples { get; set; }

    public uint FirstSamples { get; set; }

    public bool HasSamples { get; set; }

    public int ChannelsRead { get; set; }

    public FrameFlags Flags { get; set; }

    public int ExceedingElement { get; set; }

    public int ReservedElement { get; set; }

    public int ShiftedEscapeElement { get; set; }

    public int DifferingElement { get; set; }

    public int LongElement { get; set; }

    public uint LongElementSamples { get; set; }

    // The elements past the ones kept: the bits they cover.
    public long RestStart { get; set; }

    public long RestEnd { get; set; }

    public void Reset()
    {
        Fault = WalkFault.None;
        FaultElement = -1;
        FaultChannelsBefore = 0;
        FaultTag = 0;
        FaultBit = 0;
        HasEnd = false;
        IsEndMissing = false;
        Walked = 0;
        Elements = 0;
        Samples = 0;
        FirstSamples = 0;
        HasSamples = false;
        ChannelsRead = 0;
        Flags = FrameFlags.None;
        ExceedingElement = -1;
        ReservedElement = -1;
        ShiftedEscapeElement = -1;
        DifferingElement = -1;
        LongElement = -1;
        LongElementSamples = 0;
        RestStart = 0;
        RestEnd = 0;
    }
}

// Reads a frame's elements as ALACDecoder.cpp's Decode does, far enough to know where each ends: the headers, the prediction parameters, the skipped
// shifted bits, and the adaptive Golomb code of the residuals, which has to be followed code by code because its length is not stated. Nothing is
// decoded: no sample is rebuilt.
internal static class FrameWalker
{
    // ag_dec.c: the prefix of ones that switches to a value written as it is, for the residuals and for the zero runs.
    private const int MaxPrefix = 9;
    private const int RunBits = 16;
    private const int MeanShift = 9;
    private const uint MeanClamp = 0xFFFF;

    public static void Walk(ReadOnlySpan<byte> data, AlacCookie cookie, WalkResult result, CodingTally tally, List<ElementRecord> kept, ByteStore? store, CancellationToken cancellation)
    {
        result.Reset();
        kept.Clear();
        long total = (long)data.Length * 8;
        long position = 0;
        int channels = 0;
        int index = 0;
        long restStart = -1;
        uint frameSamples = cookie.FrameLength;
        int fillers = 0;
        while (true)
        {
            if (position + 3 > total)
            {
                if (channels >= cookie.Channels)
                {
                    result.IsEndMissing = true;
                }
                else
                {
                    Fail(result, WalkFault.Truncated, index, position);
                }

                break;
            }

            int tag = (int)Peek(data, position, 3);
            int channelsBefore = channels;
            long start = position;
            position += 3;
            var element = new ElementRecord { Tag = (byte)tag, Start = start, AreReservedBitsZero = true };
            WalkFault fault = tag switch
            {
                ElementTags.SingleChannel or ElementTags.Lfe or ElementTags.ChannelPair => ReadAudio(data, total, ref position, cookie, frameSamples, channels + (tag == ElementTags.ChannelPair ? 2 : 1) <= cookie.Channels, ref element, store, cancellation),
                ElementTags.DataStream => DataStream(data, total, ref position, ref element),
                ElementTags.Fill => Fill(data, total, ref position, ref element),
                ElementTags.End => WalkFault.None,
                _ => WalkFault.UnknownElement,
            };
            element.End = fault == WalkFault.UnknownElement ? start + 3 : Math.Min(position, total);
            if (fault == WalkFault.Truncated && channelsBefore >= cookie.Channels && total - start < 8)
            {
                // Padding to the byte boundary after the last channel: the reference decoder stops there and never reads a tag from it.
                result.IsEndMissing = true;
                break;
            }

            if (element.IsHeaderRead)
            {
                if (!element.AreReservedBitsZero && result.ReservedElement < 0)
                {
                    result.ReservedElement = index;
                }

                CountAudio(result, cookie, in element, index, ref channels);
                frameSamples = element.Samples;
            }

            tally.Count(in element);
            // Fill and data stream elements carry nothing the analysis shows, and a crafted frame can hold thousands: past MaxDetailedFillElements of them
            // the rest of the frame is one node, counted and walked but not listed.
            bool isFiller = tag is ElementTags.Fill or ElementTags.DataStream;
            if (restStart < 0 && kept.Count < Records.MaxDetailedElements && (!isFiller || fillers < Records.MaxDetailedFillElements))
            {
                kept.Add(element);
                fillers += isFiller ? 1 : 0;
            }
            else if (restStart < 0)
            {
                restStart = start;
            }

            index++;
            if (fault != WalkFault.None)
            {
                Fail(result, fault, index - 1, fault == WalkFault.UnknownElement ? start : element.End);
                // A pair that does not fit the cookie's channels is skipped by the reference decoder (ID_CPE, NoMoreChannels), like any element after the last channel.
                result.FaultChannelsBefore = tag == ElementTags.ChannelPair && channelsBefore + 2 > cookie.Channels ? Math.Max(channelsBefore, cookie.Channels) : channelsBefore;
                result.FaultTag = tag;
                break;
            }

            if ((index & 0xFFF) == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            if (tag == ElementTags.End)
            {
                result.HasEnd = true;
                result.Walked = (int)Math.Min(int.MaxValue, (position + 7) / 8);
                break;
            }
        }

        result.Elements = index;
        if (restStart >= 0)
        {
            result.RestStart = restStart;
            result.RestEnd = Math.Min(position, total);
        }

        if (result.Fault != WalkFault.None)
        {
            result.Flags |= FrameFlags.Unreadable | (result.Fault == WalkFault.Truncated ? FrameFlags.IsTruncated : FrameFlags.None);
        }
        else if (result.HasEnd)
        {
            result.Flags |= FrameFlags.HasEnd;
        }
    }

    private static void Fail(WalkResult result, WalkFault fault, int element, long bit)
    {
        result.Fault = fault;
        result.FaultElement = element;
        result.FaultBit = bit;
    }

    // Counts an audio element in the frame's figures: its channels, its sample count against the first element's and the frame length, and the
    // places of the oddities.
    private static void CountAudio(WalkResult result, AlacCookie cookie, in ElementRecord element, int index, ref int channels)
    {
        if (!result.HasSamples)
        {
            result.HasSamples = true;
            result.FirstSamples = element.Samples;
        }
        else if (element.Samples != result.FirstSamples && result.DifferingElement < 0)
        {
            result.DifferingElement = index;
        }

        result.Samples = element.Samples;

        if (element.IsPartial)
        {
            result.Flags |= FrameFlags.Partial;
            if (element.Samples > cookie.FrameLength && result.LongElement < 0)
            {
                result.LongElement = index;
                result.LongElementSamples = element.Samples;
            }
        }

        if (element.IsUncompressed)
        {
            result.Flags |= FrameFlags.Uncompressed;
            if (element.BytesShifted != 0 && result.ShiftedEscapeElement < 0)
            {
                result.ShiftedEscapeElement = index;
            }
        }

        channels += ElementTags.Channels(element.Tag);
        if (channels > cookie.Channels && result.ExceedingElement < 0)
        {
            result.ExceedingElement = index;
        }

        result.ChannelsRead = channels;
    }

    // A single channel, LFE or channel pair element (ALACDecoder.cpp, Decode, ID_SCE, ID_LFE and ID_CPE). The caller has read the 3-bit tag.
    private static WalkFault ReadAudio(ReadOnlySpan<byte> data, long total, ref long position, AlacCookie cookie, uint frameSamples, bool isDecoded, ref ElementRecord element, ByteStore? store, CancellationToken cancellation)
    {
        bool isPair = element.Tag == ElementTags.ChannelPair;
        int channels = isPair ? 2 : 1;
        if (position + 20 > total)
        {
            position = total + 1;
            return WalkFault.Truncated;
        }

        element.Instance = (byte)Peek(data, position, 4);
        element.AreReservedBitsZero = Peek(data, position + 4, 12) == 0;
        if (!element.AreReservedBitsZero && isDecoded)
        {
            position += 16;
            return WalkFault.ReservedBits;
        }

        uint header = Peek(data, position + 16, 4);
        position += 20;
        bool isPartial = (header & 8) != 0;
        int shifted = (int)((header >> 1) & 3);
        bool isUncompressed = (header & 1) != 0;
        element.BytesShifted = (byte)shifted;
        element.Flags = (isPartial ? ElementFlags.Partial : 0) | (isUncompressed ? ElementFlags.Uncompressed : 0);
        element.Samples = frameSamples;
        if (shifted == 3)
        {
            return WalkFault.InvalidHeader;
        }

        // The width of a sample as the decoder works it out: a pair's channels have a bit more than the source's, to hold the side channel.
        int width = cookie.BitDepth - (shifted * 8) + (isPair ? 1 : 0);
        if (isPartial)
        {
            if (position + 32 > total)
            {
                position = total + 1;
                return WalkFault.Truncated;
            }

            element.Samples = Peek(data, position, 32);
            position += 32;
        }

        element.IsHeaderRead = true;
        long samples = element.Samples;
        if (isUncompressed)
        {
            // A pair's uncompressed samples are the source's width; a single channel's are the width it would have been compressed at.
            int bits = isPair ? cookie.BitDepth : cookie.BitDepth - (shifted * 8);
            if (bits is < 1 or > 32)
            {
                return WalkFault.InvalidHeader;
            }

            position += samples * bits * channels;
            return position > total ? WalkFault.Truncated : WalkFault.None;
        }

        if (width is < 1 or > 32)
        {
            return WalkFault.InvalidHeader;
        }

        if (cookie.Kb is 0 or > 31)
        {
            return WalkFault.Tuning;
        }

        // mixBits and mixRes, then each channel's parameters.
        if (position + 16 > total)
        {
            position = total + 1;
            return WalkFault.Truncated;
        }

        Span<byte> coefficients = stackalloc byte[64];
        element.MixBits = (byte)Peek(data, position, 8);
        element.MixResolution = (sbyte)Peek(data, position + 8, 8);
        position += 16;
        for (int channel = 0; channel < channels; channel++)
        {
            if (position + 16 > total)
            {
                position = total + 1;
                return WalkFault.Truncated;
            }

            uint first = Peek(data, position, 8);
            uint second = Peek(data, position + 8, 8);
            position += 16;
            var record = new ChannelRecord
            {
                PredictionType = (byte)(first >> 4),
                PredictorShift = (byte)(first & 15),
                WeightFactor = (byte)(second >> 5),
                Order = (byte)(second & 31),
            };
            long coefficientBits = record.Order * 16L;
            if (position + coefficientBits > total)
            {
                position = total + 1;
                return WalkFault.Truncated;
            }

            if (store is not null && record.Order > 0)
            {
                for (int i = 0; i < record.Order; i++)
                {
                    uint value = Peek(data, position + (i * 16L), 16);
                    coefficients[2 * i] = (byte)(value >> 8);
                    coefficients[(2 * i) + 1] = (byte)value;
                }

                record.CoefficientsAt = store.Add(coefficients[..(record.Order * 2)], default);
            }

            position += coefficientBits;
            if (channel == 0)
            {
                element.First = record;
            }
            else
            {
                element.Second = record;
            }
        }

        // The bits the shift moved out of every sample, which the decoder skips here and reads after the residuals.
        position += (long)shifted * 8 * samples * channels;
        if (position > total)
        {
            return WalkFault.Truncated;
        }

        // The weight the decoder gives the adaptation of each channel's mean: (pb * pbFactor) / 4 (ALACDecoder.cpp).
        for (int channel = 0; channel < channels; channel++)
        {
            ref ChannelRecord record = ref channel == 0 ? ref element.First : ref element.Second;
            uint weight = (uint)(cookie.Pb * record.WeightFactor / 4);
            WalkFault fault = Residuals(data, total, ref position, (uint)samples, width, cookie, weight, ref record, cancellation);
            record.ResidualEnd = Math.Min(position, total);
            if (fault != WalkFault.None)
            {
                return fault;
            }

            element.ChannelsDone++;
        }

        return WalkFault.None;
    }

    // dyn_decomp of ag_dec.c: the residuals of a channel, an adaptive Golomb code. Each value has a prefix of ones ended by a zero and a remainder of k
    // bits, where k follows a running mean of the values so far; after a value that leaves the mean small, a run of zeros is coded as one count.
    // The arithmetic wraps on 32 bits as the C code's unsigned arithmetic does.
    private static WalkFault Residuals(ReadOnlySpan<byte> data, long total, ref long position, uint samples, int maxBits, AlacCookie cookie, uint weight, ref ChannelRecord record, CancellationToken cancellation)
    {
        uint mean = (uint)cookie.Mb;
        uint kb = (uint)cookie.Kb;
        uint kMask = kb >= 32 ? uint.MaxValue : (1u << (int)kb) - 1;
        uint isRunMode = 0;
        uint count = 0;
        uint zeroRuns = 0;
        uint longCodes = 0;
        long at = position;
        int steps = 0;
        WalkFault fault = WalkFault.None;
        unchecked
        {
            while (count < samples)
            {
                if ((++steps & 0xFFFF) == 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                }

                if (at >= total)
                {
                    at = total + 1;
                    fault = WalkFault.Truncated;
                    break;
                }

                uint m = mean >> MeanShift;
                int k = BitOperations.Log2(m + 3);
                if ((uint)k > kb)
                {
                    k = (int)kb;
                }

                m = (1u << k) - 1;
                uint window = Peek(data, at, 32);
                uint prefix = (uint)BitOperations.LeadingZeroCount(~window);
                uint n;
                if (prefix >= MaxPrefix)
                {
                    n = Peek(data, at + MaxPrefix, maxBits);
                    at += MaxPrefix + maxBits;
                    longCodes++;
                }
                else
                {
                    at += prefix + 1;
                    if (k != 1)
                    {
                        uint v = k == 0 ? 0 : (window << (int)(prefix + 1)) >> (32 - k);
                        at += k - 1;
                        n = prefix * m;
                        if (v >= 2)
                        {
                            n += v - 1;
                            at += 1;
                        }
                    }
                    else
                    {
                        n = prefix;
                    }
                }

                count++;
                mean = (weight * (n + isRunMode)) + mean - ((weight * mean) >> MeanShift);
                if (n > MeanClamp)
                {
                    mean = MeanClamp;
                }

                isRunMode = 0;
                if ((mean << 2) < (1u << MeanShift) && count < samples)
                {
                    isRunMode = 1;
                    int runBits = BitOperations.LeadingZeroCount(mean) - 24 + (int)((mean + 16) >> 6);
                    uint runMask = ((1u << runBits) - 1) & kMask;
                    window = Peek(data, at, 32);
                    prefix = (uint)BitOperations.LeadingZeroCount(~window);
                    uint run;
                    if (prefix >= MaxPrefix)
                    {
                        run = Peek(data, at + MaxPrefix, RunBits);
                        at += MaxPrefix + RunBits;
                    }
                    else
                    {
                        at += prefix + 1;
                        uint v = (window << (int)(prefix + 1)) >> (32 - runBits);
                        at += runBits;
                        run = (prefix * runMask) + v - 1;
                        if (v < 2)
                        {
                            run -= v - 1;
                            at -= 1;
                        }
                    }

                    if ((ulong)count + run > samples)
                    {
                        fault = WalkFault.ZeroRun;
                        break;
                    }

                    count += run;
                    zeroRuns++;
                    if (run >= 65535)
                    {
                        isRunMode = 0;
                    }

                    mean = 0;
                }

                if (at > total)
                {
                    fault = WalkFault.Truncated;
                    break;
                }
            }
        }

        record.ZeroRuns = zeroRuns;
        record.LongCodes = longCodes;
        position = at;
        return fault;
    }

    // ALACDecoder.cpp, DataStreamElement: a 4-bit instance tag, a byte-align flag, an 8-bit count extended by 8 more bits when it is 255, then the bytes.
    private static WalkFault DataStream(ReadOnlySpan<byte> data, long total, ref long position, ref ElementRecord element)
    {
        if (position + 13 > total)
        {
            position = total + 1;
            return WalkFault.Truncated;
        }

        element.Instance = (byte)Peek(data, position, 4);
        bool isAligned = Peek(data, position + 4, 1) != 0;
        uint count = Peek(data, position + 5, 8);
        position += 13;
        if (count == 255)
        {
            if (position + 8 > total)
            {
                position = total + 1;
                return WalkFault.Truncated;
            }

            count += Peek(data, position, 8);
            position += 8;
        }

        if (isAligned)
        {
            element.Flags |= ElementFlags.ByteAligned;
            position = (position + 7) & ~7L;
        }

        element.Samples = count;
        position += count * 8L;
        return position > total ? WalkFault.Truncated : WalkFault.None;
    }

    // ALACDecoder.cpp, FillElement: a 4-bit count, extended by an 8-bit one (less 1) when it is 15, then that many bytes.
    private static WalkFault Fill(ReadOnlySpan<byte> data, long total, ref long position, ref ElementRecord element)
    {
        if (position + 4 > total)
        {
            position = total + 1;
            return WalkFault.Truncated;
        }

        int count = (int)Peek(data, position, 4);
        position += 4;
        if (count == 15)
        {
            if (position + 8 > total)
            {
                position = total + 1;
                return WalkFault.Truncated;
            }

            count += (int)Peek(data, position, 8) - 1;
            position += 8;
        }

        element.Samples = (uint)count;
        position += count * 8L;
        return position > total ? WalkFault.Truncated : WalkFault.None;
    }

    // The n bits (1 to 32) at a bit position, most significant first; bits past the end of the data read as zeros.
    private static uint Peek(ReadOnlySpan<byte> data, long position, int n)
    {
        int at = (int)(position >> 3);
        int shift = (int)(position & 7);
        ulong value;
        if (at >= 0 && at + 8 <= data.Length)
        {
            value = BinaryPrimitives.ReadUInt64BigEndian(data[at..]);
        }
        else
        {
            value = 0;
            for (int i = 0; i < 8; i++)
            {
                value = (value << 8) | (at + i >= 0 && at + i < data.Length ? data[at + i] : 0u);
            }
        }

        return (uint)((value << shift) >> (64 - n));
    }
}
