using Encspect.Core;

namespace Encspect.Formats.TrueHd;

// What one call of ReadBlock found.
internal struct BlockInfo
{
    // The sample of the unit the block starts at, and its samples.
    public int FirstSample { get; set; }

    public int Samples { get; set; }

    public bool HasParameters { get; set; }

    public bool HasRestartHeader { get; set; }

    public RestartHeader Restart { get; set; }

    // The bit the block starts at, where its data starts and where it ends (before its last-block flag).
    public int StartBit { get; set; }

    public int DataStartBit { get; set; }

    public int DataEndBit { get; set; }

    public bool IsLast { get; set; }

    // The block size check data is present and the residuals do not end where it says.
    public bool HasBlockSizeCheckMismatch { get; set; }

    // The block changed the matrices, the output shifts or the quantizer step sizes in any block but the unit's first.
    public bool ChangesMatricesAfterFirstBlock { get; set; }
}

// What follows the last block of a substream.
internal readonly record struct SubstreamTail(bool HasEndOfStream, int DroppedSamples, bool HasPaddingBitsSet, bool HasFinalWordFlags);

// Reads the syntax of one substream of one access unit (docs/design/truehd-format.md, "Substream syntax"), block by block, updating the
// substream's state. The parser's walk and the decoder use this one reader: a walk stores the residuals and the bypassed bits in the planes it
// is given (or only counts the bits where it has none), and a replay, which the decoder runs over a unit the parser walked, reads the
// parameters again and takes the residuals from the planes, jumping over the block data to where the walk ended.
//
// The planes are Stride samples each: plane c holds the residuals of coded channel c, plane BypassPlane + j the bypassed bits of matrix j, and
// plane MetaPlane the bit at which each block's data ended.
internal ref struct SubstreamReader
{
    public const int BypassPlane = 8;
    public const int MetaPlane = 16;
    public const int PlanesPerSubstream = 17;
    public const int MaxBypassPlanes = 8;

    private BitCursor _bits;
    private readonly ReadOnlySpan<byte> _data;
    private readonly SubstreamState _state;
    private readonly bool _isMlp;
    private readonly int _unitSamples;
    private readonly int _channelListSize;
    private readonly Span<int> _planes;
    private readonly int _stride;
    private readonly bool _isReplay;
    private readonly ParameterLog? _log;
    private int _blockIndex;
    private bool _isFirstBlock;

    // content is the substream's bytes without its parity byte and CRC-8. planes is empty where nothing is stored.
    public SubstreamReader(ReadOnlySpan<byte> content, SubstreamState state, bool isMlp, int unitSamples, int channelListSize, Span<int> planes, int stride, bool isReplay,
        ParameterLog? log = null)
    {
        _data = content;
        _bits = new BitCursor(content);
        _state = state;
        _isMlp = isMlp;
        _unitSamples = unitSamples;
        _channelListSize = channelListSize;
        _planes = planes;
        _stride = stride;
        _isReplay = isReplay;
        _log = log;
        _isFirstBlock = true;
        state.MatrixReads = 0;
        foreach (ChannelState channel in state.Coded)
        {
            channel.FirReads = 0;
            channel.IirReads = 0;
        }
    }

    public SubstreamFault Fault { get; private set; }

    // The samples the blocks read so far hold.
    public int Samples { get; private set; }

    public readonly int Position => _bits.Position;

    public readonly int Length => _bits.Length;

    private bool Fail(SubstreamFault fault)
    {
        Fault = fault;
        return false;
    }

    private bool Check()
    {
        if (_bits.IsOverrun)
        {
            return Fail(SubstreamFault.Overrun);
        }

        return true;
    }

    // Reads the next block: its parameters, its residuals and its last-block flag.
    public bool ReadBlock(out BlockInfo info)
    {
        info = default;
        SubstreamState s = _state;
        info.StartBit = _bits.Position;
        info.FirstSample = Samples;
        bool hasParameters = _bits.ReadBit();
        bool hasRestart = hasParameters && _bits.ReadBit();
        info.HasParameters = hasParameters;
        info.HasRestartHeader = hasRestart;
        if (!Check())
        {
            return false;
        }

        if (hasRestart)
        {
            RestartHeader header = RestartHeader.Read(_data, _bits.Position);
            if (!header.IsSyncValid)
            {
                return Fail(SubstreamFault.RestartSync);
            }

            if (_bits.Position + header.TotalBits > _bits.Length)
            {
                return Fail(SubstreamFault.Overrun);
            }

            SubstreamFault invalid = header.Validate(_isMlp, _channelListSize);
            if (invalid != SubstreamFault.None)
            {
                return Fail(invalid);
            }

            info.Restart = header;
            _log?.Add(Nodes.RestartHeaderNode(_log.SubstreamOffset, header));
            _bits.Skip(header.TotalBits);
            header.ApplyTo(s);
        }
        else if (!s.HasRestart)
        {
            return Fail(SubstreamFault.NoRestartHeader);
        }

        bool watches = hasParameters && !_isFirstBlock;
        int before = watches ? Fingerprint(s) : 0;
        if (hasParameters && !ReadParameters())
        {
            return false;
        }

        if (watches && Fingerprint(s) != before)
        {
            info.ChangesMatricesAfterFirstBlock = true;
        }

        _isFirstBlock = false;
        int size = s.BlockSize;
        if (Samples + size > _unitSamples)
        {
            return Fail(SubstreamFault.BlockPastUnit);
        }

        int expectedEnd = -1;
        if (s.HasBlockSizeCheck)
        {
            int fieldStart = _bits.Position;
            expectedEnd = fieldStart + (int)_bits.Read(16);
        }

        info.DataStartBit = _bits.Position;
        info.Samples = size;
        if (!ReadBlockData(size))
        {
            return false;
        }

        info.DataEndBit = _bits.Position;
        if (s.HasBlockSizeCheck)
        {
            info.HasBlockSizeCheckMismatch = expectedEnd != _bits.Position;
            _bits.Skip(8);
        }

        if (!_planes.IsEmpty && !_isReplay && _blockIndex < _stride)
        {
            _planes[(MetaPlane * _stride) + _blockIndex] = _bits.Position;
        }

        _blockIndex++;
        Samples += size;
        bool last = _bits.ReadBit();
        if (_bits.IsOverrun)
        {
            return Fail(SubstreamFault.NoBitForLastFlag);
        }

        info.IsLast = last;
        return true;
    }

    // A value that changes when the matrices, the output shifts or the quantizer step sizes change.
    private static int Fingerprint(SubstreamState s)
    {
        var hash = new HashCode();
        hash.Add(s.MatrixCount);
        for (int j = 0; j < s.MatrixCount; j++)
        {
            MatrixState m = s.Matrices[j];
            hash.Add(m.OutChannel);
            hash.Add(m.FractionalBits);
            hash.Add(m.HasLsbBypass);
            hash.Add(m.NoiseShift);
            foreach (int c in m.Coefficients)
            {
                hash.Add(c);
            }
        }

        foreach (int v in s.OutputShift)
        {
            hash.Add(v);
        }

        foreach (int v in s.Quantizer)
        {
            hash.Add(v);
        }

        return hash.ToHashCode();
    }

    private bool ReadParameters()
    {
        SubstreamState s = _state;
        int start = _bits.Position;
        _log?.Open("Decoding Parameters", start, Explain.DecodingParameters, Topics.DecodingParameters);
        int flags = s.ParameterFlags;
        if ((flags & 0x01) != 0)
        {
            int at = _bits.Position;
            if (_bits.ReadBit())
            {
                s.ParameterFlags = (int)_bits.Read(8);
                flags = s.ParameterFlags;
                _log?.Field("Parameter Presence Flags", at, _bits.Position, NodeValue.Hex(flags, 2), Explain.PresenceFlags, Topics.DecodingParameters);
            }
        }

        if ((flags & 0x80) != 0)
        {
            int at = _bits.Position;
            if (_bits.ReadBit())
            {
                int size = (int)_bits.Read(9);
                if (size < 8 || size > _unitSamples)
                {
                    return Fail(SubstreamFault.BlockSize);
                }

                s.BlockSize = size;
                _log?.Field("Block Size", at, _bits.Position, NodeValue.Integer(size, "samples"), Explain.BlockSize, Topics.DecodingParameters);
            }
        }

        if ((flags & 0x40) != 0)
        {
            int at = _bits.Position;
            if (_bits.ReadBit() && !ReadMatrices(at))
            {
                return false;
            }
        }

        if ((flags & 0x20) != 0)
        {
            int at = _bits.Position;
            if (_bits.ReadBit())
            {
                var shifts = new List<int>();
                for (int m = 0; m <= s.LastMatrixChannel; m++)
                {
                    int shift = _bits.ReadSigned(4);
                    if (shift < 0)
                    {
                        if (!Check())
                        {
                            return false;
                        }

                        return Fail(SubstreamFault.UnsupportedNegativeShift);
                    }

                    s.OutputShift[m] = shift;
                    shifts.Add(shift);
                }

                _log?.Field("Output Shifts", at, _bits.Position, string.Join(", ", shifts), Explain.OutputShifts, Topics.Rematrixing);
            }
        }

        if ((flags & 0x10) != 0)
        {
            int at = _bits.Position;
            if (_bits.ReadBit())
            {
                var steps = new List<int>();
                for (int c = 0; c <= s.LastChannel; c++)
                {
                    int step = (int)_bits.Read(4);
                    s.Quantizer[c] = step;
                    steps.Add(step);
                }

                _log?.Field("Quantizer Step Sizes", at, _bits.Position, string.Join(", ", steps), Explain.QuantizerSteps, Topics.ResidualCoding);
            }
        }

        if (!Check())
        {
            return false;
        }

        for (int c = s.FirstChannel; c <= s.LastChannel; c++)
        {
            int at = _bits.Position;
            if (!_bits.ReadBit())
            {
                continue;
            }

            if (!ReadChannelParameters(c, flags, at))
            {
                return false;
            }
        }

        if (!Check())
        {
            return false;
        }

        for (int c = s.FirstChannel; c <= s.LastChannel; c++)
        {
            ChannelState channel = s.Coded[c];
            if (channel.Codebook != 0 && (channel.LsbCount > 24 || channel.LsbCount < s.Quantizer[c]))
            {
                return Fail(SubstreamFault.LsbCount);
            }
        }

        _log?.Close(_bits.Position, null);
        return true;
    }

    private bool ReadMatrices(int fieldStart)
    {
        SubstreamState s = _state;
        if (++s.MatrixReads > 2)
        {
            return Fail(SubstreamFault.TooManyReads);
        }

        int count = (int)_bits.Read(4);
        if (count > (_isMlp ? 6 : 8))
        {
            return Fail(SubstreamFault.MatrixCount);
        }

        _log?.Open("Matrices", fieldStart, Explain.Matrices, Topics.Rematrixing);
        s.MatrixCount = count;
        int inputs = s.LastMatrixChannel + 1 + (s.NoiseType == 0 ? 2 : 0);
        for (int j = 0; j < count; j++)
        {
            int matrixStart = _bits.Position;
            MatrixState matrix = s.Matrices[j];
            int output = (int)_bits.Read(4);
            int fractional = (int)_bits.Read(4);
            if (output > s.LastMatrixChannel)
            {
                return Fail(SubstreamFault.MatrixChannel);
            }

            if (fractional > 14)
            {
                return Fail(SubstreamFault.FractionalBits);
            }

            matrix.OutChannel = output;
            matrix.FractionalBits = fractional;
            matrix.HasLsbBypass = _bits.ReadBit();
            Array.Clear(matrix.Coefficients);
            for (int i = 0; i < inputs; i++)
            {
                if (_bits.ReadBit())
                {
                    matrix.Coefficients[i] = _bits.ReadSigned(fractional + 2) << (14 - fractional);
                }
            }

            matrix.NoiseShift = s.NoiseType == 1 ? (int)_bits.Read(4) : 0;
            if (!Check())
            {
                return false;
            }

            _log?.Field($"Matrix {j}", matrixStart, _bits.Position, $"replaces channel {output}, {fractional} fractional bits{(matrix.HasLsbBypass ? ", bypasses the least significant bit" : string.Empty)}", Explain.Matrix, Topics.Rematrixing);
        }

        _log?.Close(_bits.Position, NodeValue.Integer(count, "matrices", "matrix"));
        return true;
    }

    private bool ReadChannelParameters(int channelIndex, int flags, int fieldStart)
    {
        SubstreamState s = _state;
        ChannelState c = s.Coded[channelIndex];
        _log?.Open($"Channel {channelIndex} Parameters", fieldStart, Explain.ChannelParameters, Topics.DecodingParameters);
        if ((flags & 0x08) != 0)
        {
            int at = _bits.Position;
            if (_bits.ReadBit() && !ReadFilter(c, isFir: true, at))
            {
                return false;
            }
        }

        if ((flags & 0x04) != 0)
        {
            int at = _bits.Position;
            if (_bits.ReadBit() && !ReadFilter(c, isFir: false, at))
            {
                return false;
            }
        }

        if ((flags & 0x02) != 0)
        {
            int at = _bits.Position;
            if (_bits.ReadBit())
            {
                c.HuffmanOffset = _bits.ReadSigned(15);
                _log?.Field("Huffman Offset", at, _bits.Position, NodeValue.Integer(c.HuffmanOffset), Explain.HuffmanOffset, Topics.ResidualCoding);
            }
        }

        int bookAt = _bits.Position;
        c.Codebook = (int)_bits.Read(2);
        int lsbAt = _bits.Position;
        c.LsbCount = (int)_bits.Read(5);
        _log?.Field("Codebook", bookAt, lsbAt, NodeValue.Integer(c.Codebook), Explain.Codebook, Topics.ResidualCoding);
        _log?.Field("LSB Count", lsbAt, _bits.Position, NodeValue.Integer(c.LsbCount, "bits"), Explain.LsbCount, Topics.ResidualCoding);
        if (!Check())
        {
            return false;
        }

        if (c.FirOrder + c.IirOrder > ChannelState.MaxFirOrder)
        {
            return Fail(SubstreamFault.OrderSum);
        }

        if (c.FirOrder > 0 && c.IirOrder > 0 && c.FirShift != c.IirShift)
        {
            return Fail(SubstreamFault.ShiftsDiffer);
        }

        _log?.Close(_bits.Position, null);
        return true;
    }

    private bool ReadFilter(ChannelState c, bool isFir, int fieldStart)
    {
        int reads = isFir ? ++c.FirReads : ++c.IirReads;
        if (reads > 2)
        {
            return Fail(SubstreamFault.TooManyReads);
        }

        int order = (int)_bits.Read(4);
        if (order > (isFir ? ChannelState.MaxFirOrder : ChannelState.MaxIirOrder))
        {
            return Fail(SubstreamFault.FilterOrder);
        }

        if (order == 0)
        {
            if (isFir)
            {
                c.FirOrder = 0;
                c.FirShift = 0;
            }
            else
            {
                c.IirOrder = 0;
                c.IirShift = 0;
            }

            _log?.Field(isFir ? "FIR Filter" : "IIR Filter", fieldStart, _bits.Position, NodeValue.Integer(0, "taps", "tap"), isFir ? Explain.FirFilter : Explain.IirFilter, Topics.PredictionFilters);
            return Check();
        }

        int shift = (int)_bits.Read(4);
        int coefficientBits = (int)_bits.Read(5);
        int coefficientShift = (int)_bits.Read(3);
        if (coefficientBits is < 1 or > 16 || coefficientBits + coefficientShift > 16)
        {
            return Fail(SubstreamFault.CoefficientBits);
        }

        int[] coefficients = isFir ? c.FirCoefficients : c.IirCoefficients;
        for (int k = 0; k < order; k++)
        {
            coefficients[k] = _bits.ReadSigned(coefficientBits) << coefficientShift;
        }

        bool hasState = _bits.ReadBit();
        if (hasState && isFir)
        {
            return Fail(SubstreamFault.FirState);
        }

        if (hasState)
        {
            int stateBits = (int)_bits.Read(4);
            int stateShift = (int)_bits.Read(4);
            Array.Clear(c.IirState);
            for (int k = 0; k < order; k++)
            {
                c.IirState[k] = stateBits > 0 ? _bits.ReadSigned(stateBits) << stateShift : 0;
            }

            c.HasIirState = true;
        }

        if (isFir)
        {
            c.FirOrder = order;
            c.FirShift = shift;
        }
        else
        {
            c.IirOrder = order;
            c.IirShift = shift;
        }

        if (!Check())
        {
            return false;
        }

        _log?.Field(isFir ? "FIR Filter" : "IIR Filter", fieldStart, _bits.Position, $"{order} {(order == 1 ? "tap" : "taps")}, shift {shift}{(hasState ? ", with state" : string.Empty)}",
            isFir ? Explain.FirFilter : Explain.IirFilter, Topics.PredictionFilters);
        return true;
    }

    private bool ReadBlockData(int size)
    {
        SubstreamState s = _state;
        if (_isReplay)
        {
            if (_planes.IsEmpty || _blockIndex >= _stride)
            {
                return Fail(SubstreamFault.Overrun);
            }

            int end = _planes[(MetaPlane * _stride) + _blockIndex];
            if (end < _bits.Position || end > _bits.Length)
            {
                return Fail(SubstreamFault.Overrun);
            }

            _bits.Skip(end - _bits.Position);
            return true;
        }

        int first = s.FirstChannel;
        int coded = Math.Max(0, s.LastChannel - first + 1);
        Span<int> raw = stackalloc int[SubstreamState.Channels];
        Span<int> books = stackalloc int[SubstreamState.Channels];
        Span<int> offsets = stackalloc int[SubstreamState.Channels];
        Span<int> steps = stackalloc int[SubstreamState.Channels];
        Span<int> bypass = stackalloc int[SubstreamState.MaxMatrices];
        int bypassCount = 0;
        for (int j = 0; j < s.MatrixCount; j++)
        {
            if (s.Matrices[j].HasLsbBypass)
            {
                bypass[bypassCount++] = j;
            }
        }

        for (int c = 0; c < coded; c++)
        {
            ChannelState channel = s.Coded[first + c];
            steps[c] = s.Quantizer[first + c];
            raw[c] = channel.LsbCount - steps[c];
            books[c] = channel.Codebook;
            offsets[c] = channel.HuffmanOffset;
        }

        bool store = !_planes.IsEmpty;
        int position = Samples;
        for (int i = 0; i < size; i++)
        {
            for (int b = 0; b < bypassCount; b++)
            {
                uint bit = _bits.Read(1);
                if (store)
                {
                    _planes[((BypassPlane + bypass[b]) * _stride) + position + i] = (int)bit;
                }
            }

            for (int c = 0; c < coded; c++)
            {
                int book = books[c];
                int l = raw[c];
                long value;
                if (book != 0)
                {
                    int index;
                    int zeros = _bits.CountZeros(9);
                    if (zeros == 0)
                    {
                        index = 7 + (book == 1 ? (int)_bits.Read(2) : book == 2 ? (int)_bits.Read(1) : 0);
                    }
                    else if (zeros == 1)
                    {
                        int more = _bits.CountZeros(7);
                        if (more == 7)
                        {
                            return Fail(_bits.IsOverrun ? SubstreamFault.Overrun : SubstreamFault.Codeword);
                        }

                        index = (book == 1 ? 11 : book == 2 ? 9 : 8) + more;
                    }
                    else if (zeros < 9)
                    {
                        index = 8 - zeros;
                    }
                    else
                    {
                        return Fail(_bits.IsOverrun ? SubstreamFault.Overrun : SubstreamFault.Codeword);
                    }

                    if (!store)
                    {
                        if (l > 0)
                        {
                            _bits.Skip(l);
                        }

                        continue;
                    }

                    long raws = l > 0 ? _bits.Read(l) : 0;
                    int back = l + 2 - book;
                    long subtract = back >= 0 ? 1L << back : 0;
                    value = ((((long)(index - 7) << l) + raws + offsets[c] - subtract)) << steps[c];
                }
                else
                {
                    if (!store)
                    {
                        if (l > 0)
                        {
                            _bits.Skip(l);
                        }

                        continue;
                    }

                    value = l > 0 ? ((long)_bits.Read(l) + offsets[c] - (1L << (l - 1))) << steps[c] : (long)offsets[c] << steps[c];
                }

                _planes[((first + c) * _stride) + position + i] = (int)value;
            }

            if ((i & 63) == 63 && _bits.IsOverrun)
            {
                return Fail(SubstreamFault.Overrun);
            }
        }

        return Check();
    }

    // Reads what follows the last block: the padding to a multiple of 16 bits, the end of stream marker where the bytes left hold one, and
    // checks that the substream's end is reached exactly. hasCheckData: the substream ends with a parity byte and a CRC-8, which content leaves out.
    public bool ReadTail(bool hasCheckData, out SubstreamTail tail)
    {
        tail = default;
        int padding = (16 - (_bits.Position % 16)) % 16;
        bool paddingSet = false;
        if (padding > 0)
        {
            if (_bits.Position + padding > _bits.Length)
            {
                return Fail(SubstreamFault.LengthMismatch);
            }

            int at = _bits.Position;
            paddingSet = _bits.Read(padding) != 0;
            _log?.Field("Padding", at, _bits.Position, NodeValue.Integer(padding, "bits"), Explain.Padding, Topics.Substreams);
        }

        long left = _bits.Length - _bits.Position + (hasCheckData ? 16 : 0);
        bool hasEnd = false;
        int dropped = 0;
        bool flags = false;
        if (left >= 32)
        {
            int markerAt = _bits.Position;
            if (_bits.Read(16) != 0xD234)
            {
                return Fail(SubstreamFault.EndOfStreamMarker);
            }

            uint second = _bits.Read(16);
            if (_isMlp)
            {
                if (second != 0xD234)
                {
                    return Fail(SubstreamFault.EndOfStreamMarker);
                }
            }
            else
            {
                flags = (second & 0xE000) == 0xE000;
                if ((second & 0x2000) != 0)
                {
                    dropped = (int)(second & 0x1FFF);
                }
            }

            hasEnd = true;
            if (_log is not null)
            {
                _log.Open("End Of Stream Marker", markerAt, Explain.EndOfStream, Topics.Substreams);
                _log.Field("Dropped Samples", markerAt + 16, markerAt + 32, NodeValue.Integer(dropped, "samples"), Explain.DroppedSamples, Topics.Substreams);
                _log.Close(markerAt + 32, NodeValue.Hex(0xD234, 4));
            }
        }

        if (_bits.Position != _bits.Length)
        {
            return Fail(SubstreamFault.LengthMismatch);
        }

        tail = new SubstreamTail(hasEnd, dropped, paddingSet, flags);
        return true;
    }
}
