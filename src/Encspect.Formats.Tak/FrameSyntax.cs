using Encspect.Core;

namespace Encspect.Formats.Tak;

internal enum DecorrelationKind
{
    Add,
    Subtract,
    MidSide,
    Scaled,
    Filter,
}

// One subframe as the syntax stated it (docs/design/tak-format.md, section 5). The values it holds are in the frame's buffer: no prediction, the subframe's values; prediction, the
// warm-up values (not yet integrated) and then the residuals, at the positions of the values they give.
internal struct SubframeRecord
{
    public int Channel;
    public int Position;
    public int Length;
    public bool IsPredicted;
    public bool IsContinued;
    public int WarmOrder;
    public int Order;
    public int Shift;
    public int Precision;
    public int ParametersAt;
}

// One decorrelation of two channels with the fields its mode reads (section 6).
internal struct DecorrelationRecord
{
    public DecorrelationKind Kind;
    public int X;
    public int Y;
    public int Shift;
    public int Gain;
    public short[]? Filter;
    public bool IsHeadAdded;
    public bool IsTailAdded;
}

// What the walk over one frame's coded data found, and where it put the values: the parser reads the frame once, and the decoder reconstructs from this. The object is reused
// from frame to frame.
internal sealed class WalkedFrame
{
    public int Samples;
    public int Channels;

    // Planar, Samples for each channel; empty where the walk stored none.
    public int[] Values = [];
    public bool HasValues;
    public bool IsShort;
    public readonly int[] Wasted = new int[16];
    public readonly int[] DifferenceOrder = new int[16];
    public readonly List<SubframeRecord> Subframes = [];
    public int[] Parameters = new int[1024];
    public int ParameterCount;
    public readonly List<DecorrelationRecord> Decorrelations = [];
    private readonly List<short[]> _filters = [];

    // The coefficients of the filter of the decorrelation at an index, kept from frame to frame.
    public short[] Filter(int index, int order)
    {
        while (_filters.Count <= index)
        {
            _filters.Add([]);
        }

        if (_filters[index].Length != order)
        {
            _filters[index] = new short[order];
        }

        return _filters[index];
    }

    // The stereo mode of a frame of codec 2 with two channels (u(3)), else -1.
    public int StereoMode = -1;

    // The bytes the fields end in.
    public int ConsumedBytes;

    public void Reset(int samples, int channels, bool store)
    {
        Samples = samples;
        Channels = channels;
        IsShort = samples < FrameSyntax.ShortFrame;
        HasValues = store;
        if (store && Values.Length < samples * channels)
        {
            Values = new int[samples * channels];
        }

        Subframes.Clear();
        Decorrelations.Clear();
        ParameterCount = 0;
        StereoMode = -1;
        ConsumedBytes = 0;
        Array.Clear(Wasted);
        Array.Clear(DifferenceOrder);
    }
}

// The coded syntax of a frame, after its header and before its data CRC-24 (docs/design/tak-format.md, sections 2 to 6): the channel data, the subframes with their prediction
// fields and residuals, the decorrelation fields and the channel table. Parser code that the decoder reads with, as FLAC's SubframeSyntax is: it reads every field and stores the
// values; the reconstruction (prediction, decorrelation, integration) is the decoder's. One instance serves one stream.
internal sealed class FrameSyntax
{
    // The shortest frame that is coded with predictors; below it the samples are stored.
    public const int ShortFrame = 16;

    public const int MaxOrder = 256;

    private static readonly int[] Orders = [4, 8, 12, 16, 24, 32, 48, 64, 80, 96, 128, 160, 192, 224, 256, 0];

    private readonly int _codec;
    private readonly int _channels;
    private readonly int _bits;
    private readonly int _partition;
    private readonly int _unit;

    public FrameSyntax(StreamInfoFields info)
    {
        _codec = info.Codec;
        _channels = info.Channels;
        _bits = info.BitsPerSample;
        _partition = ResidualCodes.PartitionLength(info.SampleRate);
        _unit = 2 * (((info.SampleRate + 511) >> 9) + 3 & ~3);
    }

    // Whether the walk understands the stream: codec 2 or 4, data type 0, 8, 16 or 24 bits, at most 2 channels (codec 2) or 6, a frame size valid for the rate.
    public static string? Refusal(StreamInfoFields info)
    {
        if (info.Codec is not (2 or 4))
        {
            return $"the stream info states the codec {info.Codec}{(Layout.CodecName(info.Codec) is { } name ? $" ({name})" : string.Empty)}; the decoder takes 2 (TAK 2.0) and 4 (TAK 2.2)";
        }

        if (info.DataType != 0)
        {
            return $"the stream info states the data type {info.DataType}; the decoder takes 0 (PCM)";
        }

        if (info.BitsPerSample is not (8 or 16 or 24))
        {
            return $"the stream info states {info.BitsPerSample} bits per sample; the decoder takes 8, 16 and 24";
        }

        int limit = info.Codec == 2 ? 2 : 6;
        if (info.Channels > limit)
        {
            return $"the stream info states {Counts.Of(info.Channels, "channel", "channels")}; the decoder takes up to {limit} for the codec {info.Codec}";
        }

        return Layout.IsFrameSizeValid(info.FrameSizeType, info.SampleRate) ? null : "the stream info states a frame size that is not valid for its sample rate, so the frames' lengths are not known";
    }

    // Reads the fields of one frame of n samples from its coded bytes into frame. Throws InvalidDataException where the syntax breaks and EndOfStreamException where the data
    // ends inside a field or the fields run past it.
    public void Read(ReadOnlySpan<byte> coded, int n, WalkedFrame frame, bool store)
    {
        frame.Reset(n, _channels, store);
        var bits = new LsbBitCursor(coded);
        if (n < ShortFrame)
        {
            if ((long)n * _channels * _bits > coded.Length * 8L)
            {
                throw new EndOfStreamException();
            }

            for (int c = 0; c < _channels; c++)
            {
                for (int i = 0; i < n; i++)
                {
                    int value = bits.ReadSigned(_bits);
                    if (store)
                    {
                        frame.Values[(c * n) + i] = value;
                    }
                }
            }
        }
        else if (_codec == 2)
        {
            ReadFixedLayout(ref bits, n, frame);
        }
        else
        {
            ReadChannelTable(ref bits, n, frame);
        }

        if (bits.IsOverrun)
        {
            throw new EndOfStreamException();
        }

        bits.AlignToByte();
        int consumed = (bits.Position + 7) / 8;
        if (consumed > coded.Length)
        {
            throw new InvalidDataException("The frame's fields run past its coded data");
        }

        frame.ConsumedBytes = consumed;
    }

    // Section 2, codec 2: one or two channels, then for two the decorrelation.
    private void ReadFixedLayout(ref LsbBitCursor bits, int n, WalkedFrame frame)
    {
        for (int c = 0; c < _channels; c++)
        {
            ReadChannel(ref bits, c, n, frame);
        }

        if (_channels != 2)
        {
            return;
        }

        // Section 6.1: a bit and, set, 6 bits the source reads and ignores; then the mode.
        if (bits.ReadBit())
        {
            bits.Read(6);
        }

        int mode = (int)bits.Read(3);
        frame.StereoMode = mode;
        switch (mode)
        {
            case 0:
                break;
            case 1:
                Decorrelate(ref bits, DecorrelationKind.Add, 0, 1, n, frame);
                break;
            case 2:
                Decorrelate(ref bits, DecorrelationKind.Subtract, 0, 1, n, frame);
                break;
            case 3:
                Decorrelate(ref bits, DecorrelationKind.MidSide, 0, 1, n, frame);
                break;
            case 4:
                Decorrelate(ref bits, DecorrelationKind.Scaled, 1, 0, n, frame);
                break;
            case 5:
                Decorrelate(ref bits, DecorrelationKind.Scaled, 0, 1, n, frame);
                break;
            case 6:
                Decorrelate(ref bits, DecorrelationKind.Filter, 1, 0, n, frame);
                break;
            default:
                Decorrelate(ref bits, DecorrelationKind.Filter, 0, 1, n, frame);
                break;
        }
    }

    // Section 6.2, codec 4: the table of entries, then each entry's channel data and decorrelation.
    private void ReadChannelTable(ref LsbBitCursor bits, int n, WalkedFrame frame)
    {
        int entries = _channels;
        bool isTable = bits.ReadBit();
        Span<int> channel = stackalloc int[16];
        Span<int> type = stackalloc int[16];
        Span<int> partner = stackalloc int[16];
        Span<bool> paired = stackalloc bool[16];
        Span<bool> used = stackalloc bool[16];
        if (!isTable)
        {
            for (int i = 0; i < entries; i++)
            {
                channel[i] = i;
            }
        }
        else
        {
            entries = (int)bits.Read(4) + 1;
            if (entries > _channels)
            {
                throw new InvalidDataException($"The channel table has {entries} entries for {Counts.Of(_channels, "channel", "channels")}");
            }

            for (int i = 0; i < entries; i++)
            {
                int m = (int)bits.Read(4);
                if (m >= _channels || used[m])
                {
                    throw new InvalidDataException($"The channel table lists channel {m} {(m >= _channels ? "which the stream does not have" : "twice")}");
                }

                channel[i] = m;
                if (bits.ReadBit())
                {
                    paired[i] = true;
                    type[i] = (int)bits.Read(2);
                    int r = (int)bits.Read(4);
                    if (r >= _channels)
                    {
                        throw new InvalidDataException($"The channel table pairs channel {m} with channel {r}, which the stream does not have");
                    }

                    partner[i] = r;
                    if (type[i] == 1)
                    {
                        if (r == m || used[r])
                        {
                            throw new InvalidDataException($"The channel table pairs channel {m} with channel {r}, which is the same channel or has an entry already");
                        }

                        used[r] = true;
                    }
                    else if (!used[r])
                    {
                        throw new InvalidDataException($"The channel table pairs channel {m} with channel {r}, which has no entry before");
                    }
                }

                used[m] = true;
            }

            for (int c = 0; c < _channels; c++)
            {
                if (!used[c])
                {
                    throw new InvalidDataException($"The channel table leaves channel {c} out");
                }
            }
        }

        for (int i = 0; i < entries; i++)
        {
            if (paired[i] && type[i] == 1)
            {
                ReadChannel(ref bits, partner[i], n, frame);
            }

            ReadChannel(ref bits, channel[i], n, frame);
            if (!paired[i])
            {
                continue;
            }

            int m = channel[i];
            int r = partner[i];
            switch (type[i])
            {
                case 0:
                    Decorrelate(ref bits, DecorrelationKind.Add, r, m, n, frame);
                    break;
                case 1:
                    Decorrelate(ref bits, DecorrelationKind.MidSide, r, m, n, frame);
                    break;
                case 2:
                    Decorrelate(ref bits, DecorrelationKind.Scaled, m, r, n, frame);
                    break;
                default:
                    Decorrelate(ref bits, DecorrelationKind.Filter, m, r, n, frame);
                    break;
            }
        }
    }

    // Reads the mode's own fields (section 6).
    private static void Decorrelate(ref LsbBitCursor bits, DecorrelationKind kind, int x, int y, int n, WalkedFrame frame)
    {
        var record = new DecorrelationRecord { Kind = kind, X = x, Y = y };
        if (kind == DecorrelationKind.Scaled)
        {
            record.Shift = ReadFlag4(ref bits);
            record.Gain = bits.ReadSigned(10);
        }
        else if (kind == DecorrelationKind.Filter)
        {
            record.Shift = ReadFlag4(ref bits);
            int order = bits.ReadBit() ? 16 : 8;
            record.IsHeadAdded = bits.ReadBit();
            record.IsTailAdded = bits.ReadBit();
            short[] g = frame.Filter(frame.Decorrelations.Count, order);
            for (int i = 0; i < order; i += 4)
            {
                int width = 14 - (int)bits.Read(3);
                for (int j = 0; j < 4; j++)
                {
                    g[i + j] = (short)bits.ReadSigned(width);
                }
            }

            if (n - 1 < 256)
            {
                throw new InvalidDataException("The filter mode is used on a frame of fewer than 257 samples");
            }

            record.Filter = g;
        }

        frame.Decorrelations.Add(record);
    }

    // Section 4: the channel data.
    private void ReadChannel(ref LsbBitCursor bits, int channel, int n, WalkedFrame frame)
    {
        int wasted = ReadFlag4(ref bits);
        if (wasted >= _bits)
        {
            throw new InvalidDataException($"A channel states {wasted} wasted bits of {_bits}");
        }

        frame.Wasted[channel] = wasted;
        int first = bits.ReadSigned(_bits - wasted);
        if (frame.HasValues)
        {
            frame.Values[channel * n] = first;
        }

        frame.DifferenceOrder[channel] = (int)bits.Read(2);
        int count = (int)bits.Read(3) + 1;
        Span<int> boundaries = stackalloc int[8];
        for (int i = 1; i < count; i++)
        {
            boundaries[i] = (int)bits.Read(6);
        }

        // The subframes are the boundaries' multiples of the unit and the rest, each at least 1 sample (section 4): checked before any is read, so that none runs past the channel.
        for (int i = 1; i < count; i++)
        {
            if (boundaries[i] <= boundaries[i - 1])
            {
                throw new InvalidDataException($"A subframe of a channel has {(boundaries[i] - boundaries[i - 1]) * _unit} samples, and at least 1 is needed");
            }
        }

        int rest = n - 1 - (boundaries[count - 1] * _unit);
        if (rest < 1)
        {
            throw new InvalidDataException($"The last subframe of a channel has {rest} samples, and at least 1 is needed");
        }

        int position = 1;
        int previous = 0;
        for (int i = 0; i < count; i++)
        {
            int length = i < count - 1 ? (boundaries[i + 1] - boundaries[i]) * _unit : rest;
            ReadSubframe(ref bits, channel, n, position, length, i == 0, previous, frame);
            position += length;
            previous = length;
        }
    }

    // Section 5.
    private void ReadSubframe(ref LsbBitCursor bits, int channel, int n, int position, int length, bool isFirst, int previousLength, WalkedFrame frame)
    {
        int at = channel * n;
        Span<int> Slice(int from, int count) => frame.HasValues ? frame.Values.AsSpan(at + from, count) : [];
        var record = new SubframeRecord { Channel = channel, Position = position, Length = length };
        if (!bits.ReadBit())
        {
            ResidualCodes.Read(ref bits, Slice(position, length), length, _partition);
            frame.Subframes.Add(record);
            return;
        }

        record.IsPredicted = true;
        int order = Orders[(int)bits.Read(4)];
        record.Order = order;
        bool isContinued = !isFirst && bits.ReadBit();
        record.IsContinued = isContinued;
        int warm = 0;
        if (isContinued)
        {
            if (order > previousLength)
            {
                throw new InvalidDataException($"A subframe continues from {order} values, but the one before it has {previousLength}");
            }
        }
        else
        {
            if (order > length)
            {
                throw new InvalidDataException($"A subframe of {length} values has a filter of order {order}");
            }

            record.WarmOrder = (int)bits.Read(2);
            if (record.WarmOrder == 3)
            {
                throw new InvalidDataException("A subframe's warm-up values have the difference order 3");
            }

            ResidualCodes.Read(ref bits, Slice(position, order), order, _partition);
            warm = order;
        }

        record.Shift = ReadFlag4(ref bits);
        int width = 6 + (int)bits.Read(1);
        record.Precision = 10;
        if (bits.ReadBit())
        {
            record.Precision = 10 - ((int)bits.Read(3) + 1);
            if (record.Precision < 3)
            {
                throw new InvalidDataException($"A filter has the precision {record.Precision}, and 3 to 10 are allowed");
            }
        }

        record.ParametersAt = frame.ParameterCount;
        ReadParameters(ref bits, order, width, frame);
        ResidualCodes.Read(ref bits, Slice(position + warm, length - warm), length - warm, _partition);
        frame.Subframes.Add(record);
    }

    // Section 5, step 6: the filter's parameters, in units of 1/512, appended to the frame's pool.
    private static void ReadParameters(ref LsbBitCursor bits, int order, int width, WalkedFrame frame)
    {
        if (order == 0)
        {
            return;
        }

        if (frame.Parameters.Length < frame.ParameterCount + order)
        {
            Array.Resize(ref frame.Parameters, Math.Max(2 * frame.Parameters.Length, frame.ParameterCount + order));
        }

        int[] k = frame.Parameters;
        int at = frame.ParameterCount;
        k[at] = bits.ReadSigned(10);
        k[at + 1] = bits.ReadSigned(10);
        int scale = 10 - width;
        k[at + 2] = bits.ReadSigned(width) << scale;
        k[at + 3] = bits.ReadSigned(width) << scale;
        if (order > 4)
        {
            int reduced = width - (bits.ReadBit() ? 1 : 0);
            for (int i = 4; i < order; i += 4)
            {
                int q = reduced - (int)bits.Read(2);
                for (int j = 0; j < 4; j++)
                {
                    k[at + i + j] = bits.ReadSigned(q) << scale;
                }
            }
        }

        frame.ParameterCount += order;
    }

    private static int ReadFlag4(ref LsbBitCursor bits) => bits.ReadBit() ? (int)bits.Read(4) + 1 : 0;
}
