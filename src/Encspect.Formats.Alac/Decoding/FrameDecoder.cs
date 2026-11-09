// Follows Apple's ALAC reference implementation (macosforge/alac, ALACDecoder.cpp, Decode, and matrix_dec.c), Apache License 2.0:
// Copyright (c) 2011 Apple Inc. See THIRD-PARTY-NOTICES.md.
using Encspect.Core;

namespace Encspect.Formats.Alac.Decoding;

internal sealed class FrameDecoder : IFrameDecoder
{
    // Units a tap of the predictor costs: the sum and the sign adaptation that follows it (docs/design/decoders.md, "As built: ALAC").
    internal const long TapUnits = 3;

    // Units a sample costs on top of the fixed work where the element shifted bytes out: the read of their bits.
    internal const long ShiftedSampleUnits = 4;

    private readonly DecodeContext _context;
    private readonly AlacCookie _cookie;
    private readonly int _channels;
    private readonly int _bitDepth;
    private readonly long _perSample;
    private readonly int _stride;
    private readonly WalkResult _walk = new();
    private readonly CodingTally _tally = new();
    private readonly List<ElementRecord> _kept = [];
    private IPcmSink? _sink;
    private int[] _own = [];
    private int[] _output = [];

    public FrameDecoder(DecodeContext context, AlacCookie cookie)
    {
        _context = context;
        _cookie = cookie;
        _channels = cookie.Channels;
        _bitDepth = cookie.BitDepth;
        _perSample = DecodeWork.ForSample(CanonicalPcm.BytesPerSample(_bitDepth));
        _stride = (int)cookie.FrameLength + StoredLayout.MetaSlots;
    }

    public int Decode(in CodedFrame frame)
    {
        SampleBuffers lent = frame.IsWalked ? _context.LentBuffers : default;
        if (lent.IsEmpty || lent.Stride != _stride || lent.Channels < _channels)
        {
            lent = WalkHere(frame.Bytes.Span);
        }

        int n = Check(lent);
        if (n == 0)
        {
            return 0;
        }

        if (_output.Length < _channels * n)
        {
            _output = new int[_channels * n];
        }

        ReadOnlySpan<byte> data = frame.Bytes.Span;
        Span<short> coefficients = stackalloc short[StoredLayout.MaxOrder + 1];
        int channel = 0;
        while (channel < _channels)
        {
            Span<int> meta = StoredLayout.Meta(lent, channel);
            bool isPair = meta[StoredLayout.Role] == 1;
            int width = isPair ? 2 : 1;
            for (int k = 0; k < width; k++)
            {
                Reconstruct(lent, channel + k, n, coefficients);
            }

            if (isPair)
            {
                Mix(lent, channel, n, data);
            }
            else
            {
                Copy(lent, channel, n, data);
            }

            channel += width;
        }

        _sink ??= _context.DeclareOutput(new PcmFormat((int)_cookie.SampleRate, _channels, _bitDepth, SampleFormat.Integer) { ChannelLayout = _cookie.Layout });
        _sink.Write(new PcmBlock(frame.FirstSample, n, _channels, n, _output.AsSpan(0, _channels * n)));
        return n;
    }

    public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end) => [];

    // The frame was not walked into buffers the session lent, so the same walk runs here, into buffers of this decoder.
    private SampleBuffers WalkHere(ReadOnlySpan<byte> data)
    {
        _context.AddWork(checked(DecodeWork.PerCodedByte * data.Length));
        if (_own.Length < _channels * _stride)
        {
            _own = new int[_channels * _stride];
        }

        var buffers = new SampleBuffers(_own.AsSpan(0, _channels * _stride), default, _channels, _stride);
        FrameWalker.Walk(data, _cookie, _walk, _tally, _kept, null, _context.CancellationToken, buffers);
        CodingRecorder.Clear(_tally);
        _kept.Clear();
        return _walk.Fault switch
        {
            WalkFault.None => buffers,
            WalkFault.Truncated => throw new EndOfStreamException(),
            WalkFault.UnknownElement => throw new InvalidDataException("The frame has an element for which ALAC defines no syntax"),
            WalkFault.InvalidHeader => throw new InvalidDataException("An element states a header the decoder cannot use"),
            WalkFault.ZeroRun => throw new InvalidDataException("A run of zero residuals goes past the samples of its element"),
            WalkFault.Tuning => throw new InvalidDataException("The cookie's maximum Golomb parameter defines no residual code"),
            _ => throw new InvalidDataException("The 12 reserved bits of an element header are not 0"),
        };
    }

    // What the walk stored must be a whole frame: an element for every channel of the cookie, each of the same length.
    private int Check(in SampleBuffers lent)
    {
        int n = -1;
        for (int channel = 0; channel < _channels; channel++)
        {
            Span<int> meta = StoredLayout.Meta(lent, channel);
            switch ((StoredKind)meta[StoredLayout.Kind])
            {
                case StoredKind.None:
                    throw new InvalidDataException($"The frame holds elements for {channel} of the {_channels} channels the cookie states");
                case StoredKind.TooLong:
                    throw new InvalidDataException($"An element states {(uint)meta[StoredLayout.Samples]} samples, more than the frame length of {_cookie.FrameLength} in the cookie");
            }

            int samples = meta[StoredLayout.Samples];
            if (n >= 0 && samples != n)
            {
                throw new InvalidDataException("The elements of the frame state different numbers of samples");
            }

            n = samples;
        }

        return n;
    }

    // Charges the channel's work and runs the predictor of a compressed element over its residuals, which become the samples (ALACDecoder.cpp: unpc_block).
    private void Reconstruct(in SampleBuffers lent, int channel, int n, Span<short> coefficients)
    {
        Span<int> meta = StoredLayout.Meta(lent, channel);
        bool isCompressed = (StoredKind)meta[StoredLayout.Kind] == StoredKind.Compressed;
        int order = isCompressed ? meta[StoredLayout.Order] : 0;
        int mode = isCompressed ? meta[StoredLayout.PredictionType] : 0;
        long taps = order == Prediction.DifferenceOrder ? 1 : order;
        taps += isCompressed && mode != 0 ? 1 : 0;
        long units = _perSample * n + (TapUnits * taps * n) + (meta[StoredLayout.BytesShifted] != 0 ? ShiftedSampleUnits * n : 0);
        _context.AddWork(units);
        _context.CancellationToken.ThrowIfCancellationRequested();
        if (!isCompressed)
        {
            return;
        }

        Span<int> plane = StoredLayout.Plane(lent, channel)[..n];
        int shifted = meta[StoredLayout.BytesShifted];
        int chanBits = _bitDepth - (shifted * 8) + (meta[StoredLayout.Role] != 0 ? 1 : 0);
        if (mode != 0)
        {
            // The first stage of a nonzero mode is the first-order difference, in place (ALACDecoder.cpp: "the special numActive == 31 mode").
            Prediction.Difference(plane, chanBits);
        }

        for (int i = 0; i < order && order < Prediction.DifferenceOrder; i++)
        {
            coefficients[i] = (short)meta[StoredLayout.Coefficients + i];
        }

        Prediction.Restore(plane, coefficients, order, chanBits, meta[StoredLayout.PredictorShift]);
    }

    // The shifted bits of sample i of the element's channel k: the low bits of the sample, kept apart in the packet (ALACDecoder.cpp: mShiftBuffer).
    private static int Shifted(ReadOnlySpan<byte> data, Span<int> meta, int shiftBits, int width, int k, int i)
    {
        long at = ((long)(uint)meta[StoredLayout.ShiftedAtLow]) | ((long)meta[StoredLayout.ShiftedAtHigh] << 32);
        return (int)FrameWalker.Peek(data, at + ((((long)i * width) + k) * shiftBits), shiftBits);
    }

    // A single channel or LFE element to the output plane (ALACDecoder.cpp, the conversion after the predictor; copyPredictorTo20, 24 and 32).
    private void Copy(in SampleBuffers lent, int channel, int n, ReadOnlySpan<byte> data)
    {
        Span<int> meta = StoredLayout.Meta(lent, channel);
        ReadOnlySpan<int> plane = StoredLayout.Plane(lent, channel)[..n];
        Span<int> output = _output.AsSpan(channel * n, n);
        int shifted = meta[StoredLayout.BytesShifted];
        int shiftBits = shifted * 8;
        for (int i = 0; i < n; i++)
        {
            int value = plane[i];
            if (shifted != 0 && _bitDepth >= 24)
            {
                value = unchecked((value << shiftBits) | Shifted(data, meta, shiftBits, 1, 0, i));
            }

            output[i] = Output(value);
        }
    }

    // A channel pair to the output planes: the mixing undone (matrix_dec.c, unmix16, unmix20, unmix24 and unmix32), then the shifted bytes put back.
    private void Mix(in SampleBuffers lent, int channel, int n, ReadOnlySpan<byte> data)
    {
        Span<int> meta = StoredLayout.Meta(lent, channel);
        ReadOnlySpan<int> u = StoredLayout.Plane(lent, channel)[..n];
        ReadOnlySpan<int> v = StoredLayout.Plane(lent, channel + 1)[..n];
        Span<int> left = _output.AsSpan(channel * n, n);
        Span<int> right = _output.AsSpan((channel + 1) * n, n);
        int shifted = meta[StoredLayout.BytesShifted];
        int shiftBits = shifted * 8;
        int mixBits = meta[StoredLayout.MixBits] & 31;
        int mixRes = meta[StoredLayout.MixResolution];
        bool hasShift = shifted != 0 && _bitDepth >= 24;
        unchecked
        {
            for (int i = 0; i < n; i++)
            {
                int l = u[i];
                int r = v[i];
                if (mixRes != 0)
                {
                    l = l + r - ((mixRes * r) >> mixBits);
                    r = l - r;
                }

                if (hasShift)
                {
                    l = (l << shiftBits) | Shifted(data, meta, shiftBits, 2, 0, i);
                    r = (r << shiftBits) | Shifted(data, meta, shiftBits, 2, 1, i);
                }

                left[i] = Output(l);
                right[i] = Output(r);
            }
        }
    }

    // The sample as the output holds it: the low bits of the word the reference decoder writes, sign-extended. It writes 16-bit samples as they are, 20-bit ones
    // left-justified in 3 bytes (here right-aligned, as a stream of 20 bits is), 24-bit ones in 3 bytes and 32-bit ones whole.
    private int Output(int value) => _bitDepth switch
    {
        16 => (short)value,
        20 => (value << 12) >> 12,
        24 => (value << 8) >> 8,
        _ => value,
    };
}
