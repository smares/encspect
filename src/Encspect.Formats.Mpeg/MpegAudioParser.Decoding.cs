using Encspect.Core;

namespace Encspect.Formats.Mpeg;

// The entropy pass and the feeding of the decoder (docs/design/lossy-decoders.md, "The parser and decoder split"). Every complete Layer III frame is walked while the
// frames are read: the walk reads the scalefactors and Huffman-coded values with the same code whether or not anything is decoded, and gives the coded bandwidth.
// A stream is the run of frames AddStreams will make of them (MpegHeader.IsSameStream); its session starts at its first audio frame, and the frames of Layers I and II
// are walked into the session's buffers only where it decodes.
public sealed partial class MpegAudioParser
{
    // The checksum the frames store (ISO/IEC 11172-3 2.4.1.3): it covers the header and the side information, or the allocation and scfsi, and no sample.
    internal const string FrameCrc = "frame CRC-16";

    // What the feeding of one stream keeps.
    private sealed class DecodeFeed
    {
        private readonly Layer3Walker _walker = new();
        private readonly Layer12Scale _scale = new();
        private readonly byte[] _frame = new byte[8192];
        private readonly byte[] _mainData = new byte[8192 + (2 * Layer3Syntax.MaxReservoir)];

        public MpegHeader? Previous { get; set; }

        public int Streams { get; private set; }

        public bool IsLimited { get; private set; }

        public bool PendingGap { get; set; }

        public IDecodeSession? Session { get; private set; }

        public Dictionary<int, IDecodeSession> Sessions { get; } = [];

        public int StreamStart { get; private set; }

        public Node? StreamNode { get; private set; }

        public long Position { get; private set; }

        public void StartStream(int index, Node? node)
        {
            Streams++;
            IsLimited = Streams > MaxStreams;
            (Session, StreamStart, StreamNode, Position, PendingGap) = (null, index, node, 0, false);
        }

        // One audio frame of the current stream: the coded lines of a walked Layer III frame (-1 where the frame was not walked), the lowest line from which its right channel
        // codes intensity positions (-1 where none does) and the granules and channels whose Huffman data ran past their part2_3_length.
        public (int CodedLines, int Overruns, int IntensityLine) Feed(StreamState state, int index, long position, long available, int length, bool isComplete, MpegHeader header, MainDataPlacement? placement, int runsBefore)
        {
            if (Session is null)
            {
                var first = new byte[4];
                state.Blocks.ReadInto(position, first);
                Session = state.Context!.StartDecoding(CodecId, new DecodeSetup
                {
                    Stream = StreamNode ?? state.Context.Root,
                    Channels = header.IsMono ? 1 : 2,
                    BitsPerSample = 32,
                    Configuration = new MappedBytes(first, position),
                    IsLossy = true,
                });
                Sessions[StreamStart] = Session;
            }

            IDecodeSession session = Session;
            bool isDecoding = session.IsDecoding;
            int samples = header.Samples;
            long first0 = Position;
            Position += samples;
            int codedLines = -1;
            int intensityLine = -1;
            int overruns = 0;
            if (!isComplete || length > _frame.Length)
            {
                if (isDecoding)
                {
                    session.Decode(new CodedFrame(new MappedBytes(ReadOnlyMemory<byte>.Empty, position), "Frame", first0, samples) { IsUnreadable = true });
                }

                PendingGap = false;
                return (codedLines, overruns, intensityLine);
            }

            // Without a decoder a Layer III frame is read as far as its side information; the main data come through the reservoir's runs.
            Span<byte> frame = _frame.AsSpan(0, isDecoding || header.Layer != 3 ? length : Math.Min(length, 6 + Layer3SideInfo.Size(header)));
            if (header.Layer == 3)
            {
                bool isAvailable = placement is { Problem: MainDataProblem.None or MainDataProblem.AcrossGap, Overflows: false, IsLost: false };
                Span<int> planes = default;
                int stride = LentLayout.PerChannel;
                int channels = header.IsMono ? 1 : 2;
                SampleBuffers lent = default;
                if (isDecoding)
                {
                    lent = session.Lend(LentLayout.PerChannel, channels);
                }

                if (!lent.IsEmpty)
                {
                    planes = lent.Narrow;
                    stride = lent.Stride;
                }

                state.Blocks.ReadInto(position, frame);
                WalkFacts facts = default;
                if (isAvailable)
                {
                    ReadOnlySpan<byte> mainData = GatherMainData(state, runsBefore, position, length, out int claimed);
                    facts = _walker.Walk(header, frame, mainData, planes, stride, claimed);
                }
                else
                {
                    if (!planes.IsEmpty)
                    {
                        planes.Clear();
                        for (int ch = 0; ch < channels; ch++)
                        {
                            planes[(ch * stride) + LentLayout.FlagsIndex] = LentLayout.MainDataUnavailable;
                        }
                    }
                }

                bool isUnreadable = (facts.Problems & (WalkProblems.Unreadable | WalkProblems.PartsExceedData)) != 0;
                if (isAvailable && !isUnreadable)
                {
                    codedLines = facts.CodedLines;
                    intensityLine = facts.IntensityLine;
                    overruns = facts.Overruns;
                }

                if (isDecoding)
                {
                    session.Decode(new CodedFrame(new MappedBytes(_frame.AsMemory(0, length), position), "Frame", first0, samples)
                    {
                        IsWalked = !lent.IsEmpty && !isUnreadable,
                        IsUnreadable = isUnreadable || lent.IsEmpty,
                        IsDamaged = (facts.Problems & WalkProblems.HuffmanOverrun) != 0,
                        FollowsGap = PendingGap,
                    });
                }
            }
            else if (isDecoding)
            {
                state.Blocks.ReadInto(position, frame);
                SampleBuffers lent = session.Lend(LentLayout.PerChannel, header.IsMono ? 1 : 2);
                bool isReadable = !lent.IsEmpty && _scale.Read(header, frame);
                if (isReadable)
                {
                    lent.Narrow.Clear();
                    isReadable = _scale.ReadSamples(header, frame, lent.Narrow, lent.Stride);
                }

                session.Decode(new CodedFrame(new MappedBytes(_frame.AsMemory(0, length), position), "Frame", first0, samples)
                {
                    IsWalked = isReadable,
                    IsUnreadable = !isReadable,
                    FollowsGap = PendingGap,
                });
            }

            PendingGap = false;
            return (codedLines, overruns, intensityLine);
        }

        // The frame's main data: the runs the reservoir placed for it, in file order, read into one buffer (claimed bytes), then the bytes of the frame after the last run.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        private ReadOnlySpan<byte> GatherMainData(StreamState state, int runsBefore, long position, int length, out int claimed)
        {
            int total = 0;
            ChunkedList<MainDataRun> runs = state.Build.Runs;
            for (int i = runsBefore; i < runs.Count; i++)
            {
                MainDataRun run = runs[i];
                int count = Math.Min(run.Length, _mainData.Length - total);
                state.Blocks.ReadInto(run.Offset, _mainData.AsSpan(total, count));
                total += count;
            }

            claimed = total;
            if (runs.Count > runsBefore)
            {
                MainDataRun last = runs[runs.Count - 1];
                long end = last.Offset + last.Length;
                int extra = (int)Math.Min(position + length - end, _mainData.Length - total);
                if (extra > 0)
                {
                    state.Blocks.ReadInto(end, _mainData.AsSpan(total, extra));
                    total += extra;
                }
            }

            return _mainData.AsSpan(0, total);
        }
    }
}
