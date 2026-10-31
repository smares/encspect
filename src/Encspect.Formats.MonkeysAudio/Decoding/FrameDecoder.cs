// Follows the Monkey's Audio SDK 13.44 (APEDecompressCore.cpp, UnBitArray.cpp, NewPredictor.cpp, Prepare.cpp, Old/NewPredictorOld.cpp, Old/UnBitArrayOld.cpp), 3-clause BSD license:
// Copyright 2000-2026 Matthew T. Ashland. See THIRD-PARTY-NOTICES.md.
using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.MonkeysAudio.Decoding;

internal sealed class FrameDecoder : IFrameDecoder
{
    // Samples of each channel decoded between two charges of work and two looks at the cancellation token (docs/design/decoders.md, "Work instead
    // of samples"). The decoder keeps no more than this many of each channel.
    internal const int Chunk = 4096;

    // Units a sample of one coded channel costs besides its fixed work: the range decoder, and the predictor's stages (docs/design/decoders.md,
    // "As built: Monkey's Audio"). The neural network filters cost a unit for every two taps (three for every four at 32 bits, see TapUnits): they run in vector registers.
    internal const long RangeUnits = 192;
    internal const long PredictorUnits = 48;

    // The most samples over all channels that a frame of 24-bit audio may hold for the decoder to keep them until the CRC has passed, in case it has to
    // decode the frame again in interim mode (APEDecompressCore.cpp, DecodeFrame). A larger frame is decoded once without output to find its CRC and
    // again to give its samples, so that no frame size is left out of the retry.
    internal const long DefaultMaxHeldSamples = 1 << 23;

    // Where a decoded chunk goes: to the sink, into the held samples, or nowhere (a pass that only works out the CRC).
    private enum Output
    {
        Sink,
        Hold,
        None,
    }

    private enum Mode
    {
        Normal,
        BothSilent,
        PseudoStereo,
    }

    private readonly DecodeContext _context;
    private readonly ApeHeader _header;
    private readonly long _wordStart;
    private readonly long _maxHeldSamples;
    private readonly int _channels;
    private readonly int _bits;
    private readonly bool _isFloat;
    private readonly Unprepare _unprepare;
    private readonly int _version;
    private readonly RangeDecoder _range;
    private readonly ChannelState[] _states;
    private readonly int[] _values;
    private readonly int[] _samples;
    private readonly byte[] _pcm;
    private readonly long _taps;
    private Predictor32[]? _narrow;
    private Predictor64[]? _wide;
    private Predictor3930[]? _legacy;
    private bool _isInterim;
    private IPcmSink? _sink;
    private byte[] _stream = [];
    private int[] _held = [];
    private int _heldStride;
    private float[] _floats = [];
    private long _checked;
    private long _failed;

    public FrameDecoder(DecodeContext context, ApeHeader header, long wordStart, long maxHeldSamples = DefaultMaxHeldSamples)
    {
        _maxHeldSamples = maxHeldSamples;
        _context = context;
        _header = header;
        _version = header.FileVersion;
        _range = new RangeDecoder(_version);
        _wordStart = wordStart;
        _channels = header.Channels;
        _bits = header.BitsPerSample;
        _isFloat = (header.Flags & FormatFlags.IsFloatingPoint) != 0 && _bits == 32;
        _unprepare = new Unprepare(_channels, _bits);
        _states = new ChannelState[_channels];
        _values = new int[_channels];
        _samples = new int[_channels * Chunk];
        _pcm = new byte[_channels * Chunk * (_bits / 8)];
        _taps = TapUnits(_bits, Levels.Taps(header.CompressionLevel));
        CreatePredictors();
    }

    // The units of the neural network filters for a sample of a channel: a unit for every two taps over 16-bit history, which runs in vector registers
    // of twice the lanes, and three for every four over the 32-bit history of 32-bit samples (measured, docs/design/decoders.md, "As built: the second
    // speed pass").
    internal static long TapUnits(int bitsPerSample, int taps) => bitsPerSample == 32 ? taps * 3 / 4 : (taps + 1) / 2;

    private void CreatePredictors()
    {
        _narrow = null;
        _wide = null;
        _legacy = null;
        if (_bits == 32)
        {
            _wide = [.. Enumerable.Range(0, _channels).Select(_ => new Predictor64(_header.CompressionLevel))];
        }
        else if (_version < 3950 && !_isInterim)
        {
            _legacy = [.. Enumerable.Range(0, _channels).Select(_ => new Predictor3930(_header.CompressionLevel))];
        }
        else
        {
            // The interim predictors always update the filters' deltas the new way (Interim.cpp).
            bool isNewDelta = _isInterim || _version >= 3980;
            _narrow = [.. Enumerable.Range(0, _channels).Select(_ => new Predictor32(_header.CompressionLevel, _isInterim, _bits, isNewDelta))];
        }
    }

    public int Decode(in CodedFrame frame)
    {
        int blocks = frame.Samples;
        if (blocks < 1 || blocks > _header.MaximumBlocksPerFrame)
        {
            throw new InvalidDataException($"The frame holds {blocks} samples; the decoder takes 1 to {_header.MaximumBlocksPerFrame}");
        }

        int length = frame.Bytes.Length;
        if (length < 4)
        {
            throw new InvalidDataException("The frame is too short for its CRC");
        }

        if (_stream.Length < length)
        {
            _stream = new byte[Math.Max(length, _stream.Length * 2)];
        }

        WordOrder.ToStream(frame.Bytes, _wordStart, _stream);
        uint word = BinaryPrimitives.ReadUInt32BigEndian(_stream);
        int at = 4;
        uint special = 0;
        if ((word & 0x80000000) != 0)
        {
            if (length < 8)
            {
                throw new InvalidDataException("The frame is too short for its special frame codes");
            }

            special = BinaryPrimitives.ReadUInt32BigEndian(_stream.AsSpan(4));
            at = 8;
        }

        uint stored = word & 0x7FFFFFFF;
        Mode mode = _channels == 2 && (special & 3) == 3 ? Mode.BothSilent
            : _channels == 1 && (special & 1) != 0 ? Mode.BothSilent
            : _channels == 2 && (special & 4) != 0 ? Mode.PseudoStereo
            : Mode.Normal;
        bool isRetried = _bits == 24 && !_isInterim;
        uint computed;
        if (isRetried)
        {
            computed = (long)blocks * _channels <= _maxHeldSamples
                ? DecodeHeld(frame, blocks, mode, at, length, stored)
                : DecodeTwice(frame, blocks, mode, at, length, stored);
        }
        else
        {
            computed = DecodeBody(frame, blocks, mode, at, length, Output.Sink);
        }

        _checked++;
        if (computed != stored)
        {
            _failed++;
            _context.AddFinding(Severity.Warning, "monkeys-audio.frame-crc-mismatch",
                $"The CRC of the decoded audio of the frame at 0x{frame.Bytes.FileStart:X} is 0x{computed:X8}, but the frame stores 0x{stored:X8}: the decoded samples differ from what the encoder read.",
                frame.NodeName == Nodes.UnlistedName ? _context.Setup.Stream : Nodes.CrcOfFrame(frame.Bytes, _wordStart, stored));
        }

        return blocks;
    }

    // A frame of 24-bit audio in the normal mode: the samples are held until their CRC has passed, and if it does not (or the coded data does not read),
    // the SDK decodes the frame once more with the predictors of interim mode (APEDecompressCore.cpp). The SDK keeps that mode for the rest of the file
    // and its tools stop at the first bad frame; the analyzer goes on, so it keeps interim mode only when the frame's CRC passes in it. Otherwise the
    // frame is damaged, and the next one is decoded in the normal mode again, so that one damaged frame does not damage the frames after it; the samples
    // it gives are those of the normal mode, which a damaged CRC word leaves right, where it could decode them.
    private uint DecodeHeld(in CodedFrame frame, int blocks, Mode mode, int at, int length, uint stored)
    {
        _heldStride = blocks;
        if (_held.Length < _channels * blocks)
        {
            _held = new int[_channels * blocks];
        }

        uint normal = 0;
        bool isRead = true;
        try
        {
            normal = DecodeBody(frame, blocks, mode, at, length, Output.Hold);
        }
        catch (InvalidDataException)
        {
            isRead = false;
        }

        if (isRead && normal == stored)
        {
            Release(frame.FirstSample, blocks);
            return normal;
        }

        _isInterim = true;
        CreatePredictors();
        uint interim;
        try
        {
            interim = DecodeBody(frame, blocks, mode, at, length, Output.Hold);
        }
        catch (InvalidDataException)
        {
            LeaveInterim();
            throw;
        }

        if (interim == stored)
        {
            Release(frame.FirstSample, blocks);
            return interim;
        }

        LeaveInterim();
        if (isRead)
        {
            DecodeBody(frame, blocks, mode, at, length, Output.Hold);
        }

        Release(frame.FirstSample, blocks);
        return isRead ? normal : interim;
    }

    // The same as DecodeHeld for a frame too large to hold: each attempt that decides the outcome is decoded first without output, and the attempt
    // that is kept is decoded once more to the sink (the work of every pass is charged before it is done).
    private uint DecodeTwice(in CodedFrame frame, int blocks, Mode mode, int at, int length, uint stored)
    {
        uint normal = 0;
        bool isRead = true;
        try
        {
            normal = DecodeBody(frame, blocks, mode, at, length, Output.None);
        }
        catch (InvalidDataException)
        {
            isRead = false;
        }

        if (isRead && normal == stored)
        {
            DecodeBody(frame, blocks, mode, at, length, Output.Sink);
            return normal;
        }

        _isInterim = true;
        CreatePredictors();
        uint interim;
        try
        {
            interim = DecodeBody(frame, blocks, mode, at, length, Output.None);
        }
        catch (InvalidDataException)
        {
            LeaveInterim();
            throw;
        }

        if (interim == stored)
        {
            DecodeBody(frame, blocks, mode, at, length, Output.Sink);
            return interim;
        }

        if (isRead)
        {
            LeaveInterim();
            DecodeBody(frame, blocks, mode, at, length, Output.Sink);
            return normal;
        }

        DecodeBody(frame, blocks, mode, at, length, Output.Sink);
        LeaveInterim();
        return interim;
    }

    private void LeaveInterim()
    {
        _isInterim = false;
        CreatePredictors();
    }

    // Gives the held samples to the sink.
    private void Release(long first, int blocks)
    {
        for (int done = 0; done < blocks; done += Chunk)
        {
            int n = Math.Min(Chunk, blocks - done);
            Sink().Write(new PcmBlock(first + done, n, _channels, _heldStride, _held.AsSpan(done, _heldStride * (_channels - 1) + n)));
        }
    }

    private IPcmSink Sink() => _sink ??= _context.DeclareOutput(new PcmFormat((int)Math.Min(_header.SampleRate, int.MaxValue), _channels, _bits, _isFloat ? SampleFormat.Float32 : SampleFormat.Integer)
    {
        ChannelLayout = Summary.Layout(_channels, _context.Setup.Format?.ChannelMask),
    });

    // Decodes the coded samples chunk by chunk and returns the CRC the decoded audio has (APEDecompressCore.cpp, StartFrame and EndFrame).
    private uint DecodeBody(in CodedFrame frame, int blocks, Mode mode, int at, int length, Output output)
    {
        foreach (Predictor32 predictor in _narrow ?? [])
        {
            predictor.Flush();
        }

        foreach (Predictor64 predictor in _wide ?? [])
        {
            predictor.Flush();
        }

        foreach (Predictor3930 predictor in _legacy ?? [])
        {
            predictor.Flush();
        }

        foreach (ref ChannelState state in _states.AsSpan())
        {
            state.Flush();
        }

        if (mode != Mode.BothSilent)
        {
            _range.Begin(_stream, length, at);
        }

        int codedChannels = mode == Mode.BothSilent ? 0 : mode == Mode.PseudoStereo ? 1 : _channels;
        long perSample = (DecodeWork.ForSample(_bits / 8) * _channels) + (codedChannels * (RangeUnits + PredictorUnits + _taps));
        uint crc = Crc32.Initial;
        int lastX = 0;
        for (int done = 0; done < blocks; done += Chunk)
        {
            int n = Math.Min(Chunk, blocks - done);
            _context.AddWork(checked(n * perSample));
            _context.CancellationToken.ThrowIfCancellationRequested();
            lastX = DecodeChunk(n, mode, lastX);
            crc = Crc32.Update(crc, _pcm.AsSpan(0, n * _channels * (_bits / 8)));
            if (output == Output.Hold)
            {
                for (int c = 0; c < _channels; c++)
                {
                    _samples.AsSpan(c * Chunk, n).CopyTo(_held.AsSpan((c * _heldStride) + done, n));
                }
            }
            else if (output == Output.Sink)
            {
                WriteChunk(frame.FirstSample + done, n);
            }
        }

        return Crc32.Finish(crc) >> 1;
    }

    private void WriteChunk(long position, int n)
    {
        IPcmSink sink = Sink();
        if (!_isFloat)
        {
            sink.Write(new PcmBlock(position, n, _channels, Chunk, _samples.AsSpan(0, ((_channels - 1) * Chunk) + n)));
            return;
        }

        if (_floats.Length < _samples.Length)
        {
            _floats = new float[_samples.Length];
        }

        for (int c = 0; c < _channels; c++)
        {
            for (int i = 0; i < n; i++)
            {
                _floats[(c * Chunk) + i] = FloatOf((uint)_samples[(c * Chunk) + i]);
            }
        }

        sink.Write(new PcmBlock(position, n, _channels, Chunk, _floats.AsSpan(0, ((_channels - 1) * Chunk) + n)));
    }

    // The transform of floating-point files (FloatTransform.cpp): the encoder moves bits of the exponent so that the integer coding sees small values.
    private static float FloatOf(uint input)
    {
        uint output = input & 0xC3FFFFFF;
        output |= ~(input & 0x3C000000) ^ 0xC3FFFFFF;
        if ((output & 0x80000000) != 0)
        {
            output = ~output | 0x80000000;
        }

        return BitConverter.UInt32BitsToSingle(output);
    }

    private int DecodeChunk(int n, Mode mode, int lastX)
    {
        Span<int> values = _values;
        if (mode == Mode.BothSilent)
        {
            // All values are zero, which makes every sample the same: the first one is made as usual and the rest copies of it.
            values.Clear();
            _unprepare.Write(values, 0, Chunk, _samples, _pcm);
            int width = _bits / 8;
            int block = _channels * width;
            for (int c = 0; c < _channels; c++)
            {
                _samples.AsSpan((c * Chunk) + 1, n - 1).Fill(_samples[c * Chunk]);
            }

            Span<byte> pcm = _pcm.AsSpan(0, n * block);
            for (int filled = block; filled < pcm.Length; filled <<= 1)
            {
                pcm[..Math.Min(filled, pcm.Length - filled)].CopyTo(pcm[filled..]);
            }

            return lastX;
        }

        for (int i = 0; i < n; i++)
        {
            switch (mode)
            {
                case Mode.BothSilent:
                    values.Clear();
                    break;
                case Mode.PseudoStereo:
                    values.Clear();
                    values[0] = Predict(0, _range.DecodeValue(ref _states[0]), 0);
                    break;
                default:
                    if (_channels > 2 || _channels == 1)
                    {
                        for (int c = 0; c < _channels; c++)
                        {
                            values[c] = Predict(c, _range.DecodeValue(ref _states[c]), 0);
                        }
                    }
                    else if (_version < 3950)
                    {
                        // Before 3.95 the channels are predicted on their own, the first one's value coded first.
                        values[0] = Predict(0, _range.DecodeValue(ref _states[0]), 0);
                        values[1] = Predict(1, _range.DecodeValue(ref _states[1]), 0);
                    }
                    else
                    {
                        long y = _range.DecodeValue(ref _states[1]);
                        long x = _range.DecodeValue(ref _states[0]);
                        int valueY = Predict(1, y, lastX);
                        int valueX = Predict(0, x, valueY);
                        lastX = valueX;
                        values[0] = valueX;
                        values[1] = valueY;
                    }

                    break;
            }

            _unprepare.Write(values, i, Chunk, _samples, _pcm);
        }

        return lastX;
    }

    private int Predict(int channel, long a, long b) =>
        _narrow is not null ? _narrow[channel].Decompress(a, b) : _wide is not null ? _wide[channel].Decompress(a, b) : _legacy![channel].Decompress(a);

    public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end) => _checked == 0 ? [] : [new ChecksumResult(ChecksumNames.FrameCrc, _checked, _failed)];
}
