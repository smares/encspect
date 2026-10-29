// Follows the Monkey's Audio SDK 13.42 (Old/UnMACOld.cpp, Old/APEDecompressCoreOld.cpp, Old/UnBitArrayOld.cpp, Old/AntiPredictor*Old.cpp), 3-clause BSD license:
// Copyright 2000-2026 Matthew T. Ashland. See THIRD-PARTY-NOTICES.md.
using Encspect.Core;

namespace Encspect.Formats.MonkeysAudio.Decoding;

// The decoder of files from version 3.80 to 3.92 (Old/UnMACOld.cpp): a frame starts with its CRC (all 32 bits up to 3.82; from 3.83 31 bits, the top bit flagging the
// special codes that follow) or, in a file without the CRC flag, a checksum of 30 bits; then the whole array of the first channel is coded, and the whole array of the
// second. The arrays are undone by one of four predictors (OldPredictors.cs) and the pair made of the mid and the side channel into left and right.
internal sealed class OldFrameDecoder : IFrameDecoder
{
    // Units a value costs in the bit coding of each version, besides its fixed work, and for the predictor of each level (docs/design/decoders.md, "As built: Monkey's
    // Audio"): the arrays are decoded a chunk at a time and charged the same way.
    internal const long RiceUnits = 32;
    internal const long RangeUnits = 192;

    // Units a sample of a channel costs in a frame flagged silent, besides the fixed work: the arrays are cleared and not decoded, which is cheaper than the later versions'
    // sample-by-sample zeros (FrameDecoder.SilentUnits).
    internal const long SilentUnits = 10;

    // The zero bytes the range coder may read past the data of the last frame: the two the SDK takes after an array, with room for the reads that follow.
    private const int EndPadding = 4;

    private enum Mode
    {
        Normal,
        BothSilent,
        PseudoStereo,
    }

    private readonly DecodeContext _context;
    private readonly ApeHeader _header;
    private readonly long _dataStart;
    private readonly byte[] _seekBits;
    private readonly int _channels;
    private readonly int _bits;
    private readonly int _version;
    private readonly bool _hasCrc;
    private readonly bool _hasSpecial;
    private readonly OldArrayDecoder _array;
    private readonly OldPredictor[] _predictors;
    private readonly OldBits _reader = new();
    private readonly int[][] _arrays;
    private readonly int[] _temporary;
    private readonly int[] _samples = new int[2 * FrameDecoder.Chunk];
    private readonly byte[] _pcm;
    private readonly long _bitUnits;
    private readonly long _predictorUnits;
    private IPcmSink? _sink;
    private byte[] _stream = [];
    private long _checked;
    private long _failed;

    public OldFrameDecoder(DecodeContext context, ApeHeader header, long dataStart, byte[] seekBits)
    {
        _context = context;
        _header = header;
        _dataStart = dataStart;
        _seekBits = seekBits;
        _channels = header.Channels;
        _bits = header.BitsPerSample;
        _version = header.FileVersion;
        _hasCrc = header.HasStoredCrc;
        _hasSpecial = header.HasSpecialFrames;
        int capacity = checked((int)header.BlocksPerFrame + 16);
        _arrays = [new int[capacity], new int[capacity]];
        _temporary = new int[capacity];
        _pcm = new byte[_channels * FrameDecoder.Chunk * (_bits / 8)];
        _array = _version < 3860 ? new RiceUpTo385() : _version < 3900 ? new RiceFrom386(_version > 3880) : new RangeArray(_version);
        _predictors = [OldPredictor.Create(header.CompressionLevel, _version), OldPredictor.Create(header.CompressionLevel, _version)];
        _bitUnits = _version >= 3900 ? RangeUnits : RiceUnits;
        _predictorUnits = PredictorUnits(header.CompressionLevel, _version);
    }

    // The units a value costs in the predictor of a level.
    internal static long PredictorUnits(int level, int version) => level switch
    {
        1000 => 8,
        2000 => 40,
        3000 => 40 + 24,
        _ => 40 + (version < 3830 ? 64 + 16 : 128 + 16 + 24),
    };

    public int Decode(in CodedFrame frame)
    {
        int blocks = frame.Samples;
        if (blocks < 1 || blocks > _header.BlocksPerFrame)
        {
            throw new InvalidDataException($"The frame holds {blocks} samples; the decoder takes 1 to {_header.BlocksPerFrame} in a file of version {ApeHeader.VersionText(_version)}");
        }

        int frameLength = frame.Bytes.Length;
        if (frameLength < 4)
        {
            throw new InvalidDataException("The frame is too short for its CRC");
        }

        // The decoders of versions 3.80 and 3.90 to 3.92 read on into the bytes after the frame, as the SDK's read from the next one. Where the data ends in an undamaged
        // frame, the range coder of 3.90 to 3.92 reads zeros past it (the SDK's bit array zero-fills what the file does not hold, UnBitArrayBase.cpp, FillBitArray).
        int followingLength = frame.Following?.Length ?? 0;
        int padding = _version >= 3900 && !frame.IsDamaged && followingLength < EndPadding ? EndPadding - followingLength : 0;
        int length = frameLength + followingLength;
        if (_stream.Length < length + padding)
        {
            _stream = new byte[Math.Max(length + padding, _stream.Length * 2)];
        }

        WordOrder.ToStream(frame.Bytes, _dataStart, _stream);
        if (frame.Following is { } following)
        {
            WordOrder.ToStream(following, _dataStart, _stream, frameLength);
        }

        Array.Clear(_stream, length, padding);
        length += padding;

        long startBit = 0;
        if (_version <= 3800)
        {
            long index = frame.FirstSample / _header.BlocksPerFrame;
            if (index >= _seekBits.Length)
            {
                throw new InvalidDataException("The table of the bits the frames start at does not hold this frame");
            }

            startBit = _seekBits[index];
        }

        _reader.Begin(_stream, length, startBit);
        uint stored;
        uint special = 0;
        if (_hasCrc)
        {
            stored = _reader.Read(32);
            if (_hasSpecial)
            {
                if ((stored & 0x80000000) != 0)
                {
                    special = _reader.Read(32);
                }

                stored &= 0x7FFFFFFF;
            }
        }
        else
        {
            int overflow = _reader.ReadUnary();
            stored = ((uint)overflow << 30) | _reader.Read(30);
            if (stored == 0)
            {
                special = 3;
            }
        }

        Mode mode = _channels == 2 ? ((special & 3) == 3 ? Mode.BothSilent : (special & 4) != 0 ? Mode.PseudoStereo : Mode.Normal) : ((special & 1) != 0 ? Mode.BothSilent : Mode.Normal);
        int[] x = _arrays[0];
        int[] y = _arrays[1];
        if (mode == Mode.BothSilent)
        {
            Array.Clear(x, 0, blocks);
            Array.Clear(y, 0, blocks);
        }
        else
        {
            DecodeArray(0, x, blocks);
            if (_channels == 2)
            {
                if (mode == Mode.PseudoStereo)
                {
                    Array.Clear(y, 0, blocks);
                }
                else
                {
                    DecodeArray(1, y, blocks);
                }
            }
        }

        uint crc = Crc32.Initial;
        long perSample = DecodeWork.ForSample(_bits / 8) * _channels + (mode == Mode.BothSilent ? _channels * SilentUnits : 0);
        for (int done = 0; done < blocks; done += FrameDecoder.Chunk)
        {
            int n = Math.Min(FrameDecoder.Chunk, blocks - done);
            _context.AddWork(checked(n * perSample));
            _context.CancellationToken.ThrowIfCancellationRequested();
            Unprepare(x, y, done, n);
            crc = Crc32.Update(crc, _pcm.AsSpan(0, n * _channels * (_bits / 8)));
            WriteChunk(frame.FirstSample + done, n);
        }

        uint computed;
        if (_hasCrc)
        {
            computed = Crc32.Finish(crc);
            if (_hasSpecial)
            {
                computed >>= 1;
            }
        }
        else
        {
            computed = Checksum(x, y, blocks);
        }

        _checked++;
        if (computed != stored)
        {
            _failed++;
            if (_hasCrc)
            {
                _context.AddFinding(Severity.Warning, "monkeys-audio.frame-crc-mismatch",
                    $"The CRC of the decoded audio of the frame at 0x{frame.Bytes.FileStart:X} is 0x{computed:X8}, but the frame stores 0x{stored:X8}: the decoded samples differ from what the encoder read.",
                    Nodes.CrcOfFrame(frame.Bytes, _dataStart, stored, (int)startBit));
            }
            else
            {
                _context.AddFinding(Severity.Warning, "monkeys-audio.frame-checksum-mismatch",
                    $"The checksum of the decoded audio of the frame at 0x{frame.Bytes.FileStart:X} is {computed}, but the frame stores {stored}: the decoded samples differ from what the encoder read.",
                    FrameNode(frame));
            }
        }

        return blocks;
    }

    private static Node FrameNode(in CodedFrame frame)
    {
        MappedBytes bytes = frame.Bytes;
        IReadOnlyList<ByteRange> ranges = bytes.FileRanges();
        return new Node(frame.NodeName, bytes.FileStart, bytes.FileEnd - bytes.FileStart) { Ranges = ranges.Count > 1 ? ranges : null };
    }

    // One channel's array: its values from the bit coding, then the predictor (Old/APEDecompressCoreOld.cpp, GenerateDecodedArray). The fast level predicts in place.
    private void DecodeArray(int channel, int[] output, int blocks)
    {
        int[] input = _header.CompressionLevel == 1000 ? output : _temporary;
        _array.Begin(_reader, blocks);
        for (int done = 0; done < blocks; done += FrameDecoder.Chunk)
        {
            int to = Math.Min(blocks, done + FrameDecoder.Chunk);
            _context.AddWork(checked((to - done) * _bitUnits));
            _context.CancellationToken.ThrowIfCancellationRequested();
            _array.Decode(input, done, to);
        }

        _array.Finish(input);
        OldPredictor predictor = _predictors[channel];
        predictor.Begin(input, output, blocks);
        for (int done = 0; done < blocks; done += FrameDecoder.Chunk)
        {
            int to = Math.Min(blocks, done + FrameDecoder.Chunk);
            _context.AddWork(checked((to - done) * _predictorUnits));
            _context.CancellationToken.ThrowIfCancellationRequested();
            predictor.Run(done, to);
        }
    }

    // CUnMACOld::CalculateOldChecksum: the sum of the absolute values of the samples.
    private uint Checksum(int[] x, int[] y, int blocks)
    {
        uint sum = 0;
        if (_channels == 2)
        {
            for (int i = 0; i < blocks; i++)
            {
                int r = x[i] - (y[i] / 2);
                int l = r + y[i];
                sum += (uint)(Math.Abs((long)r) + Math.Abs((long)l));
            }
        }
        else
        {
            for (int i = 0; i < blocks; i++)
            {
                sum += (uint)Math.Abs((long)x[i]);
            }
        }

        return sum;
    }

    // UnprepareMono and UnprepareStereo for n samples from index start: the samples as planar integers, and as the bytes of the WAV data that the CRC covers. Up to
    // version 3.83 the predictors see the 8-bit samples as the unsigned bytes they are in a WAV file; later ones as signed numbers, 128 below.
    private void Unprepare(int[] x, int[] y, int start, int n)
    {
        int width = _bits / 8;
        int offset = _bits == 8 && _version > 3830 ? 128 : 0;
        for (int i = 0; i < n; i++)
        {
            int at = i * _channels * width;
            int xv = x[start + i];
            if (_channels == 1)
            {
                _samples[i] = Put(xv + offset, at);
                continue;
            }

            int yv = y[start + i];
            int first = xv - (yv / 2);
            int second;
            switch (_bits)
            {
                case 16:
                    first = (short)first;
                    second = (short)(first + yv);
                    break;
                case 8:
                    first = (byte)(first + offset);
                    second = (byte)(first + yv);
                    break;
                default:
                    second = first + yv;
                    break;
            }

            _samples[i] = Put(first, at);
            _samples[FrameDecoder.Chunk + i] = Put(second, at + width);
        }
    }

    // Writes one sample's bytes at `at` as the SDK does and returns its value as the canonical signed integer.
    private int Put(int value, int at)
    {
        switch (_bits)
        {
            case 8:
                _pcm[at] = (byte)value;
                return (byte)value - 128;
            case 16:
                _pcm[at] = (byte)value;
                _pcm[at + 1] = (byte)(value >> 8);
                return (short)value;
            default:
                uint t = value < 0 ? (uint)((value + 0x800000) | 0x800000) : (uint)value;
                _pcm[at] = (byte)t;
                _pcm[at + 1] = (byte)(t >> 8);
                _pcm[at + 2] = (byte)(t >> 16);
                return (int)(t << 8) >> 8;
        }
    }

    private IPcmSink Sink() => _sink ??= _context.DeclareOutput(new PcmFormat((int)Math.Min(_header.SampleRate, int.MaxValue), _channels, _bits, SampleFormat.Integer)
    {
        ChannelLayout = Summary.Layout(_channels, _context.Setup.Format?.ChannelMask),
    });

    private void WriteChunk(long position, int n) =>
        Sink().Write(new PcmBlock(position, n, _channels, FrameDecoder.Chunk, _samples.AsSpan(0, ((_channels - 1) * FrameDecoder.Chunk) + n)));

    public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end) =>
        _checked == 0 ? [] : [new ChecksumResult(_hasCrc ? ChecksumNames.FrameCrc : ChecksumNames.FrameChecksum, _checked, _failed)];
}
