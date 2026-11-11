using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Vorbis.Decoding;

// Follows libvorbis 1.3.7 (lib/mapping0.c mapping0_inverse, lib/codebook.c vorbis_book_decode*_add, lib/block.c vorbis_synthesis_blockin; BSD-3-Clause, Copyright (c) 2002-2020
// Xiph.Org Foundation; the notice is in THIRD-PARTY-NOTICES.md).
// The decoding state of one Vorbis stream: per channel the right half of the previous block (the overlap), and the setup's vectors, windows and transforms.
internal sealed class FrameDecoder : IFrameDecoder
{
    // The inverse MDCT's scale: the sum of InverseMdct as it stands, which equals what libvorbis 1.3.7 gives (checked by crosscheck_vorbis_pcm.py).
    private const float TransformScale = 1f;

    // The units a coefficient of a channel costs in each stage (docs/design/lossy-decoders.md, "Work units per format"): the floor 2, the residue vectors 2, the window and
    // the overlap 2, the inverse transform 2 per bit of the block size's length, and one for each coefficient the inverse coupling touches.
    private const int FloorUnits = 2;
    private const int ResidueUnits = 2;
    private const int OverlapUnits = 2;
    private const int CouplingUnits = 1;

    private readonly DecodeContext _context;
    private readonly SetupModel _setup;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly int[] _blockSizes;
    private readonly float[][] _vectors;
    private readonly int[] _dimensions;
    private readonly Floor0Curve?[] _floor0;
    private readonly InverseMdct[] _transforms = new InverseMdct[2];
    private readonly float[][] _slopes;
    private readonly float[][] _spectrum;
    private readonly float[][] _tail;
    private readonly float[] _raw;
    private readonly float[] _planar;
    private readonly int _stride;
    private readonly int[] _floorAt;
    private readonly bool[] _nonzero;
    private readonly int[][][] _bundles;
    private int _previousSize;
    private IPcmSink? _sink;
    private bool _lostBefore;

    public FrameDecoder(DecodeContext context, Identification identification, SetupModel setup)
    {
        _context = context;
        _setup = setup;
        _channels = identification.Channels;
        _sampleRate = (int)identification.Rate;
        _blockSizes = [identification.Blocksize0, identification.Blocksize1];
        int large = identification.Blocksize1;
        _stride = large / 2;
        _planar = new float[_channels * _stride];
        _spectrum = new float[_channels][];
        _tail = new float[_channels][];
        for (int ch = 0; ch < _channels; ch++)
        {
            _spectrum[ch] = new float[large / 2];
            _tail[ch] = new float[large / 2];
        }

        _raw = new float[large];
        _floorAt = new int[_channels];
        _nonzero = new bool[_channels];
        _bundles = new int[setup.Mappings.Length][][];
        _transforms[0] = new InverseMdct(identification.Blocksize0, TransformScale);
        _transforms[1] = identification.Blocksize1 == identification.Blocksize0 ? _transforms[0] : new InverseMdct(identification.Blocksize1, TransformScale);
        _slopes = new float[2][];
        _slopes[0] = WindowSlope.Create(identification.Blocksize0 / 2);
        _slopes[1] = identification.Blocksize1 == identification.Blocksize0 ? _slopes[0] : WindowSlope.Create(identification.Blocksize1 / 2);

        // The vectors of the books the residues and floor 0 use, built once; a total of more than 2^23 values is more than a decoder here takes.
        _vectors = new float[setup.Books.Length][];
        _dimensions = new int[setup.Books.Length];
        long total = 0;
        var wanted = new bool[setup.Books.Length];
        foreach (ResidueModel residue in setup.Residues)
        {
            foreach (int[] books in residue.PartBooks)
            {
                foreach (int book in books)
                {
                    if (book >= 0)
                    {
                        wanted[book] = true;
                    }
                }
            }
        }

        foreach (Floor0Model? floor in setup.Floor0)
        {
            if (floor is not null)
            {
                foreach (int book in floor.Books)
                {
                    wanted[book] = true;
                }
            }
        }

        for (int i = 0; i < wanted.Length; i++)
        {
            if (!wanted[i])
            {
                _vectors[i] = [];
                continue;
            }

            total += (long)setup.Books[i].EntryCount * setup.Books[i].Dimensions;
            if (total > 1L << 23)
            {
                throw new InvalidDataException("The setup header's codebooks hold more values than the decoder takes");
            }

            _vectors[i] = CodebookVectors.Build(setup.Books[i]) ?? [];
            _dimensions[i] = setup.Books[i].Dimensions;
        }

        _floor0 = new Floor0Curve?[setup.Floor0.Length];
        for (int f = 0; f < _floor0.Length; f++)
        {
            if (setup.Floor0[f] is { } floor)
            {
                _floor0[f] = new Floor0Curve(floor, [.. floor.Books.Select(b => _vectors[b])], [.. floor.Books.Select(b => _dimensions[b])]);
            }
        }

        _previousSize = -1;
    }

    public int Decode(in CodedFrame frame)
    {
        SampleBuffers lent = _context.LentBuffers;
        if (!frame.IsWalked || lent.IsEmpty)
        {
            // The host lends at most 2^24 ints and this packet's records could need more: it is not decoded, and the one after it follows a loss.
            _context.AddFinding(Severity.Info, "decode.frame-too-large", $"The packet at 0x{frame.Bytes.FileStart:X} could hold more values than the host lends buffers for, so it is not decoded.");
            _previousSize = -1;
            _lostBefore = true;
            return 0;
        }

        ReadOnlySpan<int> packet = lent.Narrow;
        int mode = packet[LentLayout.Mode];
        int flags = packet[LentLayout.Flags];
        bool isLong = (flags & LentLayout.FlagLong) != 0;
        int size = _blockSizes[isLong ? 1 : 0];
        int half = size / 2;
        bool follows = frame.FollowsGap || _lostBefore;
        _lostBefore = false;
        long returned = follows || _previousSize < 0 ? frame.Samples : (_previousSize / 4) + (size / 4);
        if (!follows && returned != frame.Samples)
        {
            throw new InvalidDataException("The samples the parser counts for the packet are not those its block and the one before it return");
        }

        _context.CancellationToken.ThrowIfCancellationRequested();
        Charge(packet, mode, size, frame.Samples);
        MappingModel mapping = _setup.Mappings[_setup.ModeMapping[mode]];
        Reconstruct(packet, _setup.ModeMapping[mode], mapping, half);

        // The overlap: the previous block's right half and this block's left half over the samples between the two centers.
        int count = 0;
        bool isOutput = _previousSize >= 0 && !follows && frame.Samples > 0;
        int previousHalf = _previousSize / 2;
        if (isOutput)
        {
            count = frame.Samples;
        }

        for (int ch = 0; ch < _channels; ch++)
        {
            Transform(ch, size, half);
            if (isOutput)
            {
                Overlap(_planar.AsSpan(ch * _stride, count), _tail[ch], _raw, previousHalf, half, count);
            }

            _raw.AsSpan(half, half).CopyTo(_tail[ch]);
        }

        _previousSize = size;
        if (follows && frame.Samples > 0)
        {
            _context.Withhold(frame.FirstSample, frame.Samples, WithholdReason.AfterGap);
        }

        if (count == 0)
        {
            return 0;
        }

        _sink ??= _context.DeclareOutput(new PcmFormat(_sampleRate, _channels, 32, SampleFormat.Float32) { ChannelLayout = VorbisParser.Speakers(_channels) });
        _sink.Write(new PcmBlock(frame.FirstSample, count, _channels, _stride, _planar));
        return count;
    }

    public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end) => [];

    // The work of the packet before it is done: the floor, residue, transform and overlap of every channel that has a floor, the overlap alone of the others, and the
    // inverse coupling.
    private void Charge(ReadOnlySpan<int> packet, int mode, int size, int samples)
    {
        MappingModel mapping = _setup.Mappings[_setup.ModeMapping[mode]];
        int half = size / 2;
        int transformUnits = 2 * System.Numerics.BitOperations.Log2((uint)size);
        long units = DecodeWork.ForSample(4) * samples * _channels;
        int at = LentLayout.Floors;
        int coded = 0;
        long floor0 = 0;
        for (int ch = 0; ch < _channels; ch++)
        {
            int kind = packet[at];
            at += FloorSize(packet, at, mapping, ch);
            coded += kind != LentLayout.FloorUnused ? 1 : 0;
            if (kind == LentLayout.FloorZero)
            {
                // The curve of a floor 0 costs about its order in products and a few cosines and an exponential for each coefficient, not the 2 units of a floor 1 line.
                floor0 += (long)half * (_setup.Floor0[mapping.FloorOfSubmap[mapping.ChannelMux[ch]]]!.Order + 8);
            }
        }

        units += (long)half * ((coded * (FloorUnits + ResidueUnits + transformUnits)) + (_channels * OverlapUnits) + (mapping.CouplingMagnitude.Length * 2 * CouplingUnits));
        _context.AddWork(units + floor0);
    }

    // The ints the floor of channel ch takes from at, as the walk stored it.
    private int FloorSize(ReadOnlySpan<int> packet, int at, MappingModel mapping, int ch)
    {
        switch (packet[at])
        {
            case LentLayout.FloorOne:
                return 1 + _setup.Floor1[mapping.FloorOfSubmap[mapping.ChannelMux[ch]]]!.Posts;
            case LentLayout.FloorZero:
                return 4 + packet[at + 3];
            default:
                return 1;
        }
    }

    // mapping0_inverse after the floors: the residue vectors into the spectrum, the inverse coupling and the floor curves.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Reconstruct(ReadOnlySpan<int> packet, int mappingIndex, MappingModel mapping, int half)
    {
        int at = LentLayout.Floors;
        for (int ch = 0; ch < _channels; ch++)
        {
            _floorAt[ch] = at;
            _nonzero[ch] = packet[at] != LentLayout.FloorUnused;
            at += FloorSize(packet, at, mapping, ch);
            Array.Clear(_spectrum[ch], 0, half);
        }

        for (int step = 0; step < mapping.CouplingMagnitude.Length; step++)
        {
            if (_nonzero[mapping.CouplingMagnitude[step]] || _nonzero[mapping.CouplingAngle[step]])
            {
                _nonzero[mapping.CouplingMagnitude[step]] = true;
                _nonzero[mapping.CouplingAngle[step]] = true;
            }
        }

        int end = packet[LentLayout.Used];
        int record = packet[LentLayout.ResidueStart];
        while (record < end)
        {
            int channel = packet[record];
            int submap = packet[record + 1];
            int offset = packet[record + 2];
            Codebook book = _setup.Books[packet[record + 3]];
            int entries = packet[record + 4];
            ResidueModel residue = _setup.Residues[mapping.ResidueOfSubmap[submap]];
            ReadOnlySpan<int> list = packet.Slice(record + LentLayout.RecordHeader, entries);
            float[] vectors = _vectors[packet[record + 3]];
            switch (residue.Type)
            {
                case 0:
                    AddInterleaved(_spectrum[channel], offset, residue.Grouping, book.Dimensions, vectors, list);
                    break;
                case 1:
                    AddSequential(_spectrum[channel], offset, residue.Grouping, book.Dimensions, vectors, list);
                    break;
                default:
                    AddBundle(mappingIndex, mapping, submap, offset, residue.Grouping, book.Dimensions, vectors, list);
                    break;
            }

            record += LentLayout.RecordHeader + entries;
        }

        for (int step = mapping.CouplingMagnitude.Length - 1; step >= 0; step--)
        {
            Couple(_spectrum[mapping.CouplingMagnitude[step]], _spectrum[mapping.CouplingAngle[step]], half);
        }

        for (int ch = 0; ch < _channels; ch++)
        {
            int floor = mapping.FloorOfSubmap[mapping.ChannelMux[ch]];
            int start = _floorAt[ch];
            switch (packet[start])
            {
                case LentLayout.FloorOne:
                    Floors.ApplyFloor1(_setup.Floor1[floor]!, packet.Slice(start + 1, _setup.Floor1[floor]!.Posts), _spectrum[ch], half);
                    break;
                case LentLayout.FloorZero:
                    _floor0[floor]!.Apply(packet.Slice(start), _spectrum[ch], half);
                    break;
                default:
                    Array.Clear(_spectrum[ch], 0, half);
                    break;
            }
        }
    }

    // Residue type 0 (vorbis_book_decodevs_add): the entries' vectors interleaved: value i of entry j lands at offset + i * step + j.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void AddInterleaved(float[] target, int offset, int size, int dimensions, float[] vectors, ReadOnlySpan<int> entries)
    {
        int step = entries.Length;
        for (int i = 0; i < dimensions; i++)
        {
            int o = i * step;
            for (int j = 0; o + j < size && j < step; j++)
            {
                target[offset + o + j] += vectors[(entries[j] * dimensions) + i];
            }
        }
    }

    // Residue type 1 (vorbis_book_decodev_add): the entries' vectors one after the other.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void AddSequential(float[] target, int offset, int size, int dimensions, float[] vectors, ReadOnlySpan<int> entries)
    {
        int position = 0;
        foreach (int entry in entries)
        {
            int baseIndex = entry * dimensions;
            for (int j = 0; position < size && j < dimensions; j++)
            {
                target[offset + position++] += vectors[baseIndex + j];
            }
        }
    }

    // Residue type 2 (vorbis_book_decodevv_add): the vectors' values in turn to the channels of the submap, from the position offset / channels.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void AddBundle(int mappingIndex, MappingModel mapping, int submap, int offset, int size, int dimensions, float[] vectors, ReadOnlySpan<int> entries)
    {
        int[][] ofMapping = _bundles[mappingIndex] ??= new int[mapping.Submaps][];
        int[] bundle = ofMapping[submap] ??= BundleOf(mapping, submap);
        int channels = bundle.Length;
        int index = offset / channels;
        int end = (offset + size) / channels;
        int slot = 0;
        foreach (int entry in entries)
        {
            int baseIndex = entry * dimensions;
            for (int j = 0; index < end && j < dimensions; j++)
            {
                _spectrum[bundle[slot++]][index] += vectors[baseIndex + j];
                if (slot == channels)
                {
                    slot = 0;
                    index++;
                }
            }
        }
    }

    private int[] BundleOf(MappingModel mapping, int submap)
    {
        var list = new List<int>();
        for (int ch = 0; ch < _channels; ch++)
        {
            if (mapping.ChannelMux[ch] == submap)
            {
                list.Add(ch);
            }
        }

        return [.. list];
    }

    // The inverse square polar coupling (section 4.3.5).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Couple(float[] magnitudeVector, float[] angleVector, int half)
    {
        for (int j = 0; j < half; j++)
        {
            float magnitude = magnitudeVector[j];
            float angle = angleVector[j];
            if (magnitude > 0)
            {
                if (angle > 0)
                {
                    angleVector[j] = magnitude - angle;
                }
                else
                {
                    angleVector[j] = magnitude;
                    magnitudeVector[j] = magnitude + angle;
                }
            }
            else if (angle > 0)
            {
                angleVector[j] = magnitude + angle;
            }
            else
            {
                angleVector[j] = magnitude;
                magnitudeVector[j] = magnitude - angle;
            }
        }
    }

    // The block's samples before windowing: zeros for a spectrum that is silent (a floor that was unused leaves the channel's spectrum zero).
    private void Transform(int ch, int size, int half)
    {
        if (!_nonzero[ch])
        {
            Array.Clear(_raw, 0, size);
            return;
        }

        _transforms[size == _blockSizes[1] ? 1 : 0].Transform(_spectrum[ch].AsSpan(0, half), _raw.AsSpan(0, size));
    }

    // The samples from the center of the previous block to the center of this one: the previous block's tail and this block's head, each under the slope of the shorter
    // block of the two where they overlap (vorbis_synthesis_blockin).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Overlap(Span<float> output, float[] tail, float[] raw, int previousHalf, int half, int count)
    {
        int slopeLength = Math.Min(previousHalf, half);
        float[] slope = _slopes[slopeLength == _blockSizes[1] / 2 ? 1 : 0];
        int previousFlat = (previousHalf - slopeLength) / 2;
        int headStart = (half - slopeLength) / 2;
        int headOffset = half - count;
        for (int i = 0; i < count; i++)
        {
            float value = 0;
            if (i < previousFlat)
            {
                value = tail[i];
            }
            else if (i < previousFlat + slopeLength)
            {
                value = tail[i] * slope[slopeLength - 1 - (i - previousFlat)];
            }

            int head = headOffset + i;
            if (head >= headStart + slopeLength)
            {
                value += raw[head];
            }
            else if (head >= headStart)
            {
                value += raw[head] * slope[head - headStart];
            }

            output[i] = value;
        }
    }
}
