// Derived from libwavpack 5.6.0 (unpack3.c and unpack3_open.c) and, for version 1 mono files, from the decoder of WavPack 3.97 (unpack.c, words2.c),
// Copyright (c) 1998 - 2013 Conifer Software, Copyright (c) 1998 - 2022 David Bryant, under the BSD 3-clause license.
// The license text is in THIRD-PARTY-NOTICES.md.

using Encspect.Core;

namespace Encspect.Formats.WavPack.Decoding;

// The decoding of one WavPack stream of format version 1, 2 or 3 (docs/design/wavpack-legacy.md): one bitstream, which the parser hands over in chunks of its bytes from the
// WavPack header's end to the end of the data. The decoder keeps what it has not yet read (at most Margin bytes: no sample's code is longer) and decodes whole
// samples of every channel in batches of BatchSamples, which it gives to the sink, charging the batch before it. It follows libwavpack 5.6.0's unpack3.c; the correction file
// is not read, so a hybrid file decodes to its lossy audio, and the audio CRC (version 3 only) is compared when all the samples are decoded. The bytes after the
// bitstream's last bit tell where the stream ends, which the parser's placement of the trailer is compared with.
internal sealed partial class LegacyStreamDecoder : IFrameDecoder
{
    // The samples of each channel decoded and charged at a time.
    internal const int BatchSamples = 4096;

    // The most bytes one sample of all channels can read from the bitstream: two words of at most 2 x 17 bytes each, and the extra bits. A batch's sample is decoded only
    // where this many are held, so a code never straddles two chunks.
    private const int Margin = 128;

    // The work of one decorrelation pass on one sample of one channel (as the version 4 decoder's WorkPerPass).
    internal const long WorkPerPass = 11;

    // Measured, not designed (docs/design/decoders.md, "As built: the WavPack 1 to 3 decoder"): the entropy decoder's words, the CRC and the copy into the planes of a
    // sample of one channel, beyond the fixed work DecodeWork.ForSample charges.
    internal const long WorkPerWord = 24;

    private readonly DecodeContext _context;
    private readonly LegacyDecodeInfo _info;
    private readonly LegacyHeader _header;
    private readonly int _channels;
    private readonly int _version;
    private readonly bool _isMono;
    private readonly int _flags;
    private readonly int _shift;
    private readonly int _bits;
    private readonly int _minWeight;
    private readonly int _workPasses;
    private readonly int _minValue;
    private readonly int _maxValue;
    private readonly int _minShifted;
    private readonly int _maxShifted;
    private readonly long _total;
    private readonly long _workPerSample;
    private readonly int[] _output;

    private IPcmSink? _sink;
    private long _done;
    private int _stopAt;
    private bool _isEof;

    // The state the loops keep from batch to batch (unpack3.h's dc).
    private readonly int[] _sample = new int[4];
    private readonly int[] _weight = new int[2];
    private int _crc;
    private int _m;
    private int _sumLevel;
    private int _leftLevel;
    private int _rightLevel;
    private int _diffLevel;
    private int _lastExtraBits;
    private int _extraBitsCount;

    // What follows the bitstream's last bit: how many bytes, and the first of them, from which the trailer's marker is told.
    private bool _isComplete;
    private bool _hitEnd;
    private long _afterLength;
    private readonly byte[] _tail = new byte[LegacyTrailer.MarkerLength + 1];
    private int _tailLength;
    private bool _isCrcChecked;
    private bool _isCrcFailed;

    private LegacyStreamDecoder(DecodeContext context, LegacyDecodeInfo info)
    {
        _context = context;
        _info = info;
        _header = info.Header;
        _channels = info.Channels;
        _version = _header.Version;
        _total = info.Samples;
        _workPerSample = DecodeWork.ForSample(CanonicalPcm.BytesPerSample(context.Setup.BitsPerSample));
        _output = new int[_channels * BatchSamples];

        // unpack3_open.c: versions 1 and 2 take the flags and the shift from the stored WAV header, version 1 has no bit limit; version 3 takes the flags as the kludge leaves
        // them and has no correction file here (the correction flag clear).
        if (_version == 3)
        {
            _flags = _header.EffectiveFlags & ~LegacyHeader.WvcFlag;
            _shift = _header.Shift;
            _isMono = (_flags & LegacyHeader.MonoFlag) != 0;
            _bits = (short)_header.Bits;
        }
        else
        {
            _isMono = info.Channels == 1;
            _flags = _isMono ? LegacyHeader.MonoFlag : 0;
            _shift = 16 - info.SourceBits;
            _bits = _version == 1 ? 0 : (short)_header.Bits;
        }

        if (_bits != 0)
        {
            if ((_flags & (LegacyHeader.NewDecorrFlag | LegacyHeader.ExtremeDecorrFlag)) != 0)
            {
                _minWeight = -256;
            }
        }
        else if ((_flags & LegacyHeader.NewDecorrFlag) != 0)
        {
            _minWeight = (_flags & LegacyHeader.ExtremeDecorrFlag) != 0 ? -512 : -256;
        }

        if ((_flags & LegacyHeader.Bytes3Flag) != 0)
        {
            _minShifted = (_minValue = -8388608 >> _shift) << _shift;
            _maxShifted = (_maxValue = 8388607 >> _shift) << _shift;
        }
        else
        {
            _minShifted = (_minValue = -32768 >> _shift) << _shift;
            _maxShifted = (_maxValue = 32767 >> _shift) << _shift;
        }

        InitPasses();
        _workPasses = (_version == 3 && _bits == 0 ? (_flags & LegacyHeader.HighFlag) != 0 : _version == 3 && (_flags & (LegacyHeader.HighFlag | LegacyHeader.NewHighFlag)) != 0 && (_flags & LegacyHeader.FastFlag) == 0) ? _passes.Length : 0;
        _bitrate = _isMono ? _bits - 768 : (_bits / 2) - 768;
        _input = new byte[Margin];
    }

    public static IFrameDecoder Open(DecodeContext context, LegacyDecodeInfo info)
    {
        if (info.Refusal is { } refusal)
        {
            throw new InvalidDataException(refusal);
        }

        LegacyHeader header = info.Header;
        if (header.Version is < 1 or > 3 || info.Channels is < 1 or > 2)
        {
            throw new InvalidDataException($"The header has version {header.Version} and the audio {info.Channels} channels; versions 1 to 3 hold one or two");
        }

        if (header.Version == 3 ? header.Shift is < 0 or > 24 : info.SourceBits != 16)
        {
            throw new InvalidDataException(header.Version == 3
                ? $"The header's shift of {header.Shift} bits is more than a sample of 24 bits holds"
                : $"A file of version {header.Version} holds audio of 16 bits, the stored WAV header states {info.SourceBits}");
        }

        return new LegacyStreamDecoder(context, info);
    }

    public int Decode(in CodedFrame frame)
    {
        ReadOnlySpan<byte> data = frame.Bytes.Span;
        bool isLast = frame.Bytes.FileEnd >= _info.DataEnd;

        // The bytes of the bitstream, charged as they arrive (their entropy decoding), before they are read.
        _context.AddWork(checked(DecodeWork.PerCodedByte * data.Length));
        _context.CancellationToken.ThrowIfCancellationRequested();
        if (_isComplete)
        {
            AddAfter(data);
            return 0;
        }

        Append(data);
        _stopAt = isLast ? 0 : Margin;
        int produced = 0;
        while (_done < _total && !_isEof && (isLast || _end - _pos >= Margin))
        {
            int want = (int)Math.Min(BatchSamples, _total - _done);
            _context.AddWork(checked(((_workPerSample + WorkPerWord + (WorkPerPass * _workPasses)) * _channels) * want));
            _context.CancellationToken.ThrowIfCancellationRequested();
            int got = Run(want, _output, BatchSamples);
            if (got > 0)
            {
                _sink ??= _context.DeclareOutput(new PcmFormat((int)Math.Min(int.MaxValue, _info.SampleRate), _channels, _info.OutputBits, SampleFormat.Integer)
                {
                    ChannelLayout = ChannelLayouts.FromWaveMask(_channels == 1 ? 4u : 3u, _channels),
                });
                _sink.Write(new PcmBlock(_done, got, _channels, BatchSamples, _output));
                _done += got;
                produced += got;
            }

            if (got < want)
            {
                break;
            }
        }

        if (_done == _total || _isEof)
        {
            Complete(isLast);
        }

        return produced;
    }

    // Moves what is left of the earlier bytes to the front and adds the new ones.
    private void Append(ReadOnlySpan<byte> data)
    {
        int left = _end - _pos;
        if (_input.Length < left + data.Length)
        {
            byte[] grown = new byte[left + data.Length];
            Buffer.BlockCopy(_input, _pos, grown, 0, left);
            _input = grown;
        }
        else
        {
            Buffer.BlockCopy(_input, _pos, _input, 0, left);
        }

        data.CopyTo(_input.AsSpan(left));
        _pos = 0;
        _end = left + data.Length;
    }

    // The stream's samples are all decoded, or its data has ended: the CRC is compared, and what follows the last bit read is kept to tell the trailer.
    private void Complete(bool isLast)
    {
        if (_isComplete || (!_isEof && _done < _total))
        {
            return;
        }

        _isComplete = true;
        if (_done == _total && _total > 0 && _version == 3 && !_isEof)
        {
            _isCrcChecked = true;
            _isCrcFailed = (uint)_crc != _header.Crc;
            if (_isCrcFailed)
            {
                _context.AddFinding(Severity.Warning, "wavpack.legacy-crc-mismatch",
                    $"The decoded audio has the CRC {(uint)_crc:x8}, but the header stores {_header.Crc:x8}: the audio differs from what the encoder read (or the file is damaged).",
                    new Node("Audio CRC", _header.At + 20, 4));
            }
        }

        if (!_isEof)
        {
            // Bytes taken past the data (0xFF fill) mean the stream ran into its end; there is nothing after it then.
            long dataLength = _info.DataEnd - _info.BitstreamAt;
            _hitEnd = _fetched > dataLength;
            if (!_hitEnd)
            {
                _afterLength = 0;
                AddAfter(_input.AsSpan(_pos, _end - _pos));
            }
        }
    }

    private void AddAfter(ReadOnlySpan<byte> data)
    {
        if (_hitEnd)
        {
            return;
        }

        _afterLength += data.Length;
        int take = Math.Min(data.Length, _tail.Length - _tailLength);
        data[..take].CopyTo(_tail.AsSpan(_tailLength));
        _tailLength += take;
    }

    public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end)
    {
        long bitstreamBytes = _info.DataEnd - _info.BitstreamAt;
        if (_isEof && _done < _total)
        {
            _context.AddFinding(Severity.Warning, "wavpack.legacy-data-ended",
                $"The bitstream's {Counts.Of(bitstreamBytes, "byte", "bytes")} hold {Counts.Of(_done, "sample", "samples")} of each channel, but the header states {_total}; the stream ends early.",
                BitstreamNode());
        }
        else if (_isComplete && !_hitEnd)
        {
            CheckEnd();
        }

        if (end.IsComplete && _info.Header.IsHybrid && _isComplete && !_isEof)
        {
            _context.AddFinding(Severity.Info, "wavpack.legacy-crc2-needs-correction-file",
                "The header's second CRC is of the lossless audio, which this hybrid file alone does not decode to (the correction file adds what it lacks), so it is not compared.",
                new Node("Correction CRC", _header.At + 24, 4));
        }

        return _isCrcChecked ? [new ChecksumResult(ChecksumNames.LegacyCrc, 1, _isCrcFailed ? 1 : 0)] : [];
    }

    private Node BitstreamNode() => new("Bitstream", _info.BitstreamAt, _info.BitstreamEnd - _info.BitstreamAt);

    // Where the bitstream ends is where its last bit lies; 3.97's encoder follows it with 16 bytes of 0xFF and the source's chunks, which libwavpack keeps as the
    // wrapper only when more than 16 bytes follow and the first 16 are 0xFF (unpack3.c). The parser placed the trailer by its own rule: the two are compared.
    private void CheckEnd()
    {
        long end = _info.BitstreamAt + _fetched;
        bool hasMarker = _afterLength > LegacyTrailer.MarkerLength && _tailLength >= LegacyTrailer.MarkerLength && !_tail.AsSpan(0, LegacyTrailer.MarkerLength).ContainsAnyExcept((byte)0xFF);
        bool agrees = hasMarker ? _info.HasTrailer && _info.BitstreamEnd == end : _afterLength == 0 && !_info.HasTrailer;
        if (agrees)
        {
            return;
        }

        string placed = _info.HasTrailer ? $"the trailer begins at 0x{_info.BitstreamEnd:X}" : $"the bitstream runs to 0x{_info.BitstreamEnd:X}";
        string last = _done > 0 ? "The last sample's bits end" : "The bitstream, which holds no sample, ends";
        string message = hasMarker
            ? $"{last} at 0x{end:X}, where the 16 bytes of 0xFF of a trailer follow, but {placed}."
            : $"{last} at 0x{end:X}, and the {Counts.Of(_afterLength, "byte", "bytes")} after them are not a trailer (16 bytes of 0xFF followed by the source's bytes), which no decoder reads; {placed}.";
        _context.AddFinding(Severity.Info, "wavpack.legacy-bitstream-end-differs", message, BitstreamNode());
    }
}
