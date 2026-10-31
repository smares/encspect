using Encspect.Core;

namespace Encspect.Formats.Wav.Decoding;

// The decoder of the samples that WAV, Wave64, AIFF and AIFF-C store as they are: integers of 8 to 32 bits in either byte order, IEEE floats of 32 and 64 bits, and
// G.711 A-law and mu-law (docs/design/spectral-analysis.md, "WAV, Wave64 and AIFF: a PCM decoder"). There is nothing to reconstruct, so it only unpacks:
// each slice of whole sample frames the parser hands over becomes one block of planar samples for the sink. The container's value is output as stored, whatever the
// valid bits of the format are. It stores no checksum, so the host opens it only when a front end asks for measurements or output.
public sealed class PcmDecoder : IDecoder
{
    public string Id => PcmCoding.CodecId;

    public bool DeclaresChecksums => false;

    public IFrameDecoder Open(DecodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        DecodeSetup setup = context.Setup;
        PcmCoding coding = setup.Pcm ?? throw new InvalidDataException("The stream does not state how its samples are laid out");
        int bits = coding.ContainerBits;
        switch (coding.Encoding)
        {
            case PcmEncoding.SignedInteger or PcmEncoding.UnsignedInteger:
                if (bits > 32)
                {
                    throw new InvalidDataException($"Integer samples stored in {bits} bits are wider than the 32 bits the decoder's output holds");
                }

                if (bits is not (8 or 16 or 24 or 32))
                {
                    throw new InvalidDataException($"Integer samples stored in {bits} bits are not a width of 8, 16, 24 or 32 bits");
                }

                break;
            case PcmEncoding.Float:
                if (bits is not (32 or 64))
                {
                    throw new InvalidDataException($"Floating-point samples of {bits} bits are not of 32 or 64 bits");
                }

                break;
            case PcmEncoding.ALaw or PcmEncoding.MuLaw:
                if (bits != 8)
                {
                    throw new InvalidDataException($"Companded samples of {bits} bits are not of 8 bits");
                }

                break;
            default:
                throw new InvalidDataException("The sample encoding is not one the decoder knows");
        }

        return new Unpacker(context, coding);
    }

    // A slice of the parsers' feed (PcmFeed) holds at most 64 KiB, or one sample frame; this bounds the arrays a crafted caller could make it grow.
    private const long MaxSamplesPerSlice = 1 << 24;

    private sealed class Unpacker : IFrameDecoder
    {
        private readonly DecodeContext _context;
        private readonly PcmCoding _coding;
        private readonly int _channels;
        private readonly int _frameBytes;
        private readonly int _outputBytes;
        private readonly PcmFormat _format;
        private IPcmSink? _sink;
        private int[] _integers = [];
        private float[] _floats = [];
        private double[] _doubles = [];

        public Unpacker(DecodeContext context, PcmCoding coding)
        {
            _context = context;
            _coding = coding;
            _channels = context.Setup.Channels;
            _frameBytes = checked((coding.ContainerBits / 8) * _channels);
            _outputBytes = CanonicalPcm.BytesPerSample(coding.OutputBits);
            _format = new PcmFormat(coding.SampleRate, _channels, coding.OutputBits, coding.Encoding == PcmEncoding.Float ? (coding.ContainerBits == 32 ? SampleFormat.Float32 : SampleFormat.Float64) : SampleFormat.Integer)
            {
                ChannelLayout = coding.ChannelLayout is { } layout && layout.Count == _channels ? layout : null,
            };
        }

        public int Decode(in CodedFrame frame)
        {
            int count = frame.Samples;
            if (count <= 0)
            {
                return 0;
            }

            ReadOnlySpan<byte> bytes = frame.Bytes.Span;
            if ((long)count * _frameBytes > bytes.Length)
            {
                throw new InvalidDataException($"The slice holds {Counts.Of(bytes.Length, "byte", "bytes")}, fewer than the {Counts.Of(count, "sample frame", "sample frames")} it states take");
            }

            long total = (long)count * _channels;
            if (total > MaxSamplesPerSlice)
            {
                throw new InvalidDataException($"The slice holds {Counts.Of(total, "sample", "samples")} over all channels, more than the {MaxSamplesPerSlice} the decoder takes at once");
            }

            _context.AddWork(DecodeWork.ForSample(_outputBytes) * _channels * count);
            _context.CancellationToken.ThrowIfCancellationRequested();

            PcmCoding coding = _coding;
            _sink ??= _context.DeclareOutput(_format);
            long position = frame.FirstSample;
            switch (_format.Format)
            {
                case SampleFormat.Float32:
                    Grow(ref _floats, total);
                    PcmUnpack.Floats(bytes, _floats, count, _channels, coding.IsBigEndian);
                    _sink.Write(new PcmBlock(position, count, _channels, count, _floats.AsSpan(0, (int)total)));
                    break;
                case SampleFormat.Float64:
                    Grow(ref _doubles, total);
                    PcmUnpack.Doubles(bytes, _doubles, count, _channels, coding.IsBigEndian);
                    _sink.Write(new PcmBlock(position, count, _channels, count, _doubles.AsSpan(0, (int)total)));
                    break;
                default:
                    Grow(ref _integers, total);
                    PcmUnpack.Integers(bytes, _integers, count, _channels, coding);
                    _sink.Write(new PcmBlock(position, count, _channels, count, _integers.AsSpan(0, (int)total)));
                    break;
            }

            return count;
        }

        public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end) => [];

        private static void Grow<T>(ref T[] array, long length)
        {
            if (array.Length < length)
            {
                array = new T[length];
            }
        }
    }
}
