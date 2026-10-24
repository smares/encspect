using Encspect.Core;

namespace Encspect.Formats.TrueHd.Decoding;

// The decoding state of one stream: the syntax state of each substream, the histories of its filters, its noise generator and its lossless check.
internal sealed class StreamDecoder : IFrameDecoder
{
    private const int MaxUnit = 160;
    private const int Substreams = Analysis.ParsedSubstreams;
    private const int Channels = SubstreamState.Channels;
    private const int History = Filters.History;
    private const int Row = History + MaxUnit;

    // Units a multiply-accumulate of a matrix costs: its sum is 64 bits and its inputs are rows of the presentation.
    internal const long MatrixTapUnits = 2;

    // Units each sample period of a substream costs besides the work of its channels: the noise generator, the history moves and the lossless check.
    internal const long PeriodUnits = 32;

    // Units a filter tap costs: a 64-bit multiply-accumulate. A channel with a FIR filter is charged for 8 taps and one with an IIR filter for 4,
    // whatever their orders, since the sums run over all of them.
    internal const long FilterTapUnits = 1;

    private readonly DecodeContext _context;
    private readonly int _channels;
    private readonly int _bits;
    private readonly UnitLayout _layout = new();
    private readonly SubstreamState[] _states = [new(), new(), new()];

    // The filter outputs and errors of each substream's coded channels, History samples of history in front of the unit's samples.
    private readonly int[] _outputs = new int[Substreams * Channels * Row];
    private readonly int[] _errors = new int[Substreams * Channels * Row];
    private readonly int[] _noiseRegister = new int[Substreams];
    private readonly uint[] _accumulator = new uint[Substreams];
    private readonly bool[] _isAccumulatorValid = new bool[Substreams];
    private readonly long[] _checked = new long[Substreams];
    private readonly long[] _failed = new long[Substreams];
    private readonly int[] _presentationChannels = new int[Substreams];
    private readonly bool[] _isFailureReported = new bool[Substreams];
    private readonly int[] _listSizes = new int[Substreams];
    private readonly int[] _held = new int[Substreams];
    private readonly int[] _output = new int[Substreams];
    private readonly bool[] _ended = new bool[Substreams];

    // The matrix channels of the presentation being built, one row of MaxUnit samples each, and after them the two noise channels.
    private readonly int[] _samples = new int[10 * MaxUnit];
    private readonly int[] _noiseTable = new int[256];
    private readonly long[] _sums = new long[MaxUnit];
    private int[] _ownPlanes = [];
    private int[] _pcm = [];
    private MajorSync? _sync;
    private bool _isSynced;
    private IPcmSink? _sink;

    public StreamDecoder(DecodeContext context)
    {
        _context = context;
        _channels = context.Setup.Channels;
        _bits = context.Setup.BitsPerSample;
    }

    public int Decode(in CodedFrame frame)
    {
        ReadOnlySpan<byte> bytes = frame.Bytes.Span;
        if (frame.FollowsGap)
        {
            Reset();
        }

        _layout.Read(bytes, _sync is { } known ? known.SubstreamCount : 0, _sync is { IsTrueHd: false });
        if (_layout.Length < 4 || bytes.Length < _layout.Length)
        {
            throw new EndOfStreamException();
        }

        if (_layout.HasSync)
        {
            if (_layout.Sync is not { IsUsable: true } sync)
            {
                throw new InvalidDataException("The unit's major sync does not check out");
            }

            Accept(sync);
        }
        else if (!_isSynced)
        {
            throw new InvalidDataException("No major sync has been read yet, so the unit's substreams are not known");
        }

        MajorSync current = _sync!.Value;
        if (!_layout.IsDirectoryRead || (_layout.Problems & ~UnitProblems.Sync) != UnitProblems.None)
        {
            throw new InvalidDataException("The unit's substream directory does not read");
        }

        int n = current.UnitSamples;
        int count = _layout.SubstreamCount;
        int read = Math.Min(count, Substreams);
        if (!frame.IsWalked)
        {
            _context.AddWork(DecodeWork.PerCodedByte * bytes.Length);
        }

        SampleBuffers lent = frame.IsWalked ? _context.LentBuffers : default;
        bool isReplay = !lent.IsEmpty && lent.Stride == n && lent.Channels >= read * SubstreamReader.PlanesPerSubstream;
        Span<int> planes;
        if (isReplay)
        {
            planes = lent.Narrow;
        }
        else
        {
            int size = read * SubstreamReader.PlanesPerSubstream * n;
            if (_ownPlanes.Length < size)
            {
                _ownPlanes = new int[size];
            }

            planes = _ownPlanes.AsSpan(0, size);
            for (int k = 0; k < read; k++)
            {
                planes.Slice(((k * SubstreamReader.PlanesPerSubstream) + SubstreamReader.BypassPlane) * n, SubstreamReader.MaxBypassPlanes * n).Clear();
            }
        }

        long unitOffset = frame.Bytes.FileStart;
        for (int k = 0; k < read; k++)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            DirectoryEntry entry = _layout.Entries[k];
            ReadOnlySpan<byte> data = bytes[entry.Start..entry.Stop];
            bool hasCheck = entry.HasCheckData;
            ReadOnlySpan<byte> content = hasCheck && data.Length >= 2 ? data[..^2] : hasCheck ? default : data;
            Span<int> own = planes.Slice(k * SubstreamReader.PlanesPerSubstream * n, SubstreamReader.PlanesPerSubstream * n);
            DecodeSubstream(k, content, hasCheck, current, count, n, own, isReplay, frame.Bytes.OffsetOf(entry.Start));
        }

        int top = read - 1;
        if (_states[top].LastMatrixChannel + 1 != _channels)
        {
            throw new InvalidDataException($"The highest substream outputs {Counts.Of(_states[top].LastMatrixChannel + 1, "channel", "channels")}, but the stream was opened with {_channels}");
        }

        int written = 0;
        for (int k = 0; k < read; k++)
        {
            SubstreamState state = _states[k];
            int output = _output[k];
            long matrices = 0;
            int inputs = state.LastMatrixChannel + 1 + (state.NoiseType == 0 ? 2 : 0);
            matrices += (long)state.MatrixCount * inputs;
            _context.AddWork(checked(((DecodeWork.ForSample(3) * (state.LastMatrixChannel + 1)) + PeriodUnits + (MatrixTapUnits * matrices)) * output));
            Rematrix(k, state, output, n, planes);
            if (k == top)
            {
                written = Write(frame, current, state, count, output);
            }

            if (_ended[k])
            {
                _isAccumulatorValid[k] = false;
            }

            ShiftHistory(k, state, _held[k]);
        }

        for (int k = 0; k < read; k++)
        {
            if (_ended[k])
            {
                Reset();
                break;
            }
        }

        return written;
    }

    private void Accept(MajorSync sync)
    {
        if (_sync is { } before && (before.RateCode != sync.RateCode || before.SubstreamCount != sync.SubstreamCount || before.IsTrueHd != sync.IsTrueHd))
        {
            throw new InvalidDataException("The stream's format, sampling rate or substream count changes at this major sync, which the decoder does not follow");
        }

        _sync = sync;
        _isSynced = true;
        for (int k = 0; k < Substreams; k++)
        {
            _listSizes[k] = Presentation.MaxListSize(sync, k, sync.SubstreamCount);
        }

        foreach (SubstreamState state in _states)
        {
            state.Forget();
        }
    }

    private void Reset()
    {
        _isSynced = false;
        foreach (SubstreamState state in _states)
        {
            state.Forget();
        }

        Array.Clear(_isAccumulatorValid);
        Array.Clear(_outputs);
        Array.Clear(_errors);
    }

    // Reads substream k's blocks and filters each as it is read.
    private void DecodeSubstream(int k, ReadOnlySpan<byte> content, bool hasCheck, in MajorSync sync, int count, int n, Span<int> planes, bool isReplay, long substreamOffset)
    {
        SubstreamState state = _states[k];
        var reader = new SubstreamReader(content, state, !sync.IsTrueHd, n, _listSizes[k], planes, n, isReplay);
        while (true)
        {
            if (!reader.ReadBlock(out BlockInfo block))
            {
                throw new InvalidDataException($"Substream {k}: {SubstreamFaults.Describe(reader.Fault)}");
            }

            if (block.HasRestartHeader)
            {
                Restart(k, block.Restart, state, substreamOffset);
            }

            FilterBlock(k, state, block, planes, n);
            if (block.IsLast)
            {
                break;
            }
        }

        if (!reader.ReadTail(hasCheck, out SubstreamTail tail))
        {
            throw new InvalidDataException($"Substream {k}: {SubstreamFaults.Describe(reader.Fault)}");
        }

        _held[k] = reader.Samples;
        _output[k] = Math.Max(0, reader.Samples - tail.DroppedSamples);
        _ended[k] = tail.HasEndOfStream;
    }

    // At a restart header: the lossless check of the interval it closes, and the starts of the new one.
    private void Restart(int k, RestartHeader header, SubstreamState state, long substreamOffset)
    {
        if (_isAccumulatorValid[k])
        {
            _checked[k]++;
            uint a = _accumulator[k];
            int folded = (int)((a ^ (a >> 8) ^ (a >> 16) ^ (a >> 24)) & 0xFF);
            if (folded != header.LosslessCheck)
            {
                _failed[k]++;
                if (!_isFailureReported[k])
                {
                    _isFailureReported[k] = true;
                    int computed = folded;
                    int channels = state.LastMatrixChannel + 1;
                    int stored = header.LosslessCheck;
                    _context.AddFinding(Severity.Warning, "truehd.lossless-check-failed",
                        $"The output of the {channels}-channel presentation of substream {k} since the previous restart header folds to 0x{computed:X2}, but the lossless check of the restart header at 0x{substreamOffset:X} stores 0x{stored:X2}: the decoded audio differs from what the encoder read.",
                        Nodes.LosslessCheckField(substreamOffset, header));
                }
            }
        }

        _accumulator[k] = 0;
        _isAccumulatorValid[k] = true;
        _noiseRegister[k] = state.NoiseSeed;
        if (_presentationChannels[k] == 0)
        {
            _presentationChannels[k] = state.LastMatrixChannel + 1;
        }
    }

    private void FilterBlock(int k, SubstreamState state, in BlockInfo block, Span<int> planes, int n)
    {
        if (state.LastChannel < state.FirstChannel)
        {
            return;
        }

        int size = block.Samples;
        long taps = 0;
        for (int c = state.FirstChannel; c <= state.LastChannel; c++)
        {
            ChannelState channel = state.Coded[c];
            taps += (channel.FirOrder > 0 ? ChannelState.MaxFirOrder : 0) + (channel.IirOrder > 0 ? ChannelState.MaxIirOrder : 0);
        }

        _context.AddWork(checked(FilterTapUnits * taps * size));
        for (int c = state.FirstChannel; c <= state.LastChannel; c++)
        {
            ChannelState channel = state.Coded[c];
            Span<int> y = _outputs.AsSpan(((k * Channels) + c) * Row, Row);
            Span<int> e = _errors.AsSpan(((k * Channels) + c) * Row, Row);
            if (channel.HasIirState)
            {
                for (int i = 0; i < channel.IirOrder; i++)
                {
                    e[History + block.FirstSample - 1 - i] = channel.IirState[i];
                }

                channel.HasIirState = false;
            }

            ReadOnlySpan<int> residuals = planes.Slice((c * n) + block.FirstSample, size);
            Filters.Block(residuals, y, e, block.FirstSample, size, channel, state.Quantizer[c]);
        }
    }

    // Moves the last samples of the unit in front of the next one.
    private void ShiftHistory(int k, SubstreamState state, int held)
    {
        for (int c = state.FirstChannel; c <= state.LastChannel; c++)
        {
            int at = ((k * Channels) + c) * Row;
            _outputs.AsSpan(at + held, History).CopyTo(_outputs.AsSpan(at, History));
            _errors.AsSpan(at + held, History).CopyTo(_errors.AsSpan(at, History));
        }
    }

    // Builds substream k's presentation from the filter outputs: the noise, the matrices, the output shifts and the lossless check. The result is left
    // in _samples, one row of MaxUnit samples for each matrix channel.
    private void Rematrix(int k, SubstreamState state, int count, int n, Span<int> planes)
    {
        int m = state.LastMatrixChannel + 1;
        MajorSync sync = _sync!.Value;
        for (int c = 0; c < m; c++)
        {
            Span<int> row = _samples.AsSpan(c * MaxUnit, MaxUnit);
            row.Clear();
            for (int j = k; j >= 0; j--)
            {
                SubstreamState s = _states[j];
                if (s.HasRestart && c >= s.FirstChannel && c <= s.LastChannel)
                {
                    _outputs.AsSpan((((j * Channels) + c) * Row) + History, count).CopyTo(row);
                    break;
                }
            }
        }

        int register = _noiseRegister[k];
        int bufferSize = sync.NoiseBuffer;
        if (state.NoiseType == 0)
        {
            for (int i = 0; i < count; i++)
            {
                _samples[(m * MaxUnit) + i] = (sbyte)(register >> 15) << state.NoiseShift;
                _samples[((m + 1) * MaxUnit) + i] = (sbyte)(register >> 7) << state.NoiseShift;
                int t = (register >> 7) & 0xFFFF;
                register = (int)((((uint)register << 16) ^ (uint)t ^ ((uint)t << 5)) & 0x7FFFFF);
            }
        }
        else
        {
            for (int i = 0; i < bufferSize; i++)
            {
                int t = (register >> 15) & 0xFF;
                _noiseTable[i] = Tables.Noise[t];
                register = (int)((((uint)register << 8) ^ (uint)t ^ ((uint)t << 5)) & 0x7FFFFF);
            }
        }

        _noiseRegister[k] = register;
        int matrixCount = state.MatrixCount;
        int inputs = m + (state.NoiseType == 0 ? 2 : 0);
        Span<long> sums = _sums.AsSpan(0, count);
        for (int j = 0; j < matrixCount; j++)
        {
            MatrixState matrix = state.Matrices[j];
            int d = matrix.OutChannel;
            int mask = ~((1 << state.Quantizer[d]) - 1);
            int distance = matrixCount - j;
            int[] coefficients = matrix.Coefficients;
            sums.Clear();
            for (int c = 0; c < inputs; c++)
            {
                long coefficient = coefficients[c];
                if (coefficient == 0)
                {
                    continue;
                }

                ReadOnlySpan<int> input = _samples.AsSpan(c * MaxUnit, count);
                for (int i = 0; i < input.Length && i < sums.Length; i++)
                {
                    sums[i] += coefficient * input[i];
                }
            }

            Span<int> target = _samples.AsSpan(d * MaxUnit, count);
            if (state.NoiseType == 1 && matrix.NoiseShift > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    int index = (distance + (i * ((2 * distance) + 1))) & (bufferSize - 1);
                    sums[i] += (long)_noiseTable[index] << (matrix.NoiseShift + 7);
                }
            }

            if (matrix.HasLsbBypass)
            {
                ReadOnlySpan<int> bypass = planes.Slice(((k * SubstreamReader.PlanesPerSubstream) + SubstreamReader.BypassPlane + j) * n, n);
                for (int i = 0; i < count; i++)
                {
                    target[i] = unchecked(((int)(sums[i] >> 14) & mask) + bypass[i]);
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    target[i] = (int)(sums[i] >> 14) & mask;
                }
            }
        }

        uint accumulator = _accumulator[k];
        for (int c = 0; c < m; c++)
        {
            int shift = state.OutputShift[c];
            for (int i = 0; i < count; i++)
            {
                int value = unchecked(_samples[(c * MaxUnit) + i] << shift);
                _samples[(c * MaxUnit) + i] = value;
                accumulator ^= (uint)(value & 0xFFFFFF) << c;
            }
        }

        _accumulator[k] = accumulator;
    }

    // Writes the highest substream's presentation to the sink, each matrix channel at the position of the channel list its restart header gives it.
    private int Write(in CodedFrame frame, in MajorSync sync, SubstreamState state, int substreamCount, int count)
    {
        int m = state.LastMatrixChannel + 1;
        int mask = 0;
        for (int c = 0; c < m; c++)
        {
            mask |= 1 << state.ChannelAssignment[c];
        }

        bool isPermutation = mask == (1 << m) - 1;
        if (_sink is null)
        {
            Speaker[]? list = isPermutation ? Presentation.Resolve(sync, Math.Min(substreamCount, Substreams) - 1, substreamCount, m) : null;
            _sink = _context.DeclareOutput(new PcmFormat(sync.SampleRate, m, _bits, SampleFormat.Integer) { ChannelLayout = list });
        }

        if (count == 0)
        {
            return 0;
        }

        if (_pcm.Length < m * count)
        {
            _pcm = new int[m * count];
        }

        int shift = Math.Max(0, 24 - _bits);
        for (int c = 0; c < m; c++)
        {
            int plane = isPermutation ? state.ChannelAssignment[c] : c;
            Span<int> target = _pcm.AsSpan(plane * count, count);
            ReadOnlySpan<int> row = _samples.AsSpan(c * MaxUnit, count);
            for (int i = 0; i < count; i++)
            {
                target[i] = row[i] >> shift;
            }
        }

        _sink.Write(new PcmBlock(frame.FirstSample, count, m, count, _pcm.AsSpan(0, m * count)));
        return count;
    }

    public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end)
    {
        var results = new List<ChecksumResult>();
        for (int k = 0; k < Substreams; k++)
        {
            if (_presentationChannels[k] > 0)
            {
                results.Add(new ChecksumResult(ChecksumNames.LosslessCheck(_presentationChannels[k], k), _checked[k], _failed[k]));
            }
        }

        return results;
    }
}
