using Encspect.Core;

namespace Encspect.Formats.MpegAudio;

// The entropy pass and the feeding of the decoder (docs/design/lossy-decoders.md, "The parser and decoder split"). Every complete Layer III frame is walked while the
// frames are read: the walk reads the scalefactors and Huffman-coded values with the same code whether or not anything is decoded, and gives the coded bandwidth.
// A stream is the run of frames AddStreams will make of them (MpegHeader.IsSameStream); its session starts at its first audio frame, and the frames of Layers I and II
// are walked into the session's buffers only where it decodes.
public sealed partial class MpegAudioParser
{
    // The checksum the frames store (ISO/IEC 11172-3 2.4.1.4, 2.4.3.1 and Table B.5): it covers the header and the side information, or the allocation and scfsi, and no sample.
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

        // The streams of MPEG-1 Layer II, which may carry the multichannel extension: they are fed after the whole file is read (DecodeDeferred), when the parser knows
        // whether it does, as their decoder's channel count is fixed when it starts.
        public List<(int Index, Node? Node)> Deferred { get; } = [];

        public bool IsDeferred { get; private set; }

        public void StartStream(int index, Node? node, MpegHeader header)
        {
            Streams++;
            IsLimited = Streams > MaxStreams;
            IsDeferred = header.Layer == 2 && header.IsMpeg1;
            (Session, StreamStart, StreamNode, Position, PendingGap) = (null, index, node, 0, false);
            if (IsDeferred && !IsLimited)
            {
                Deferred.Add((index, node));
            }
        }

        // One audio frame of the current stream: the coded lines of a walked Layer III frame (-1 where the frame was not walked), the lowest line from which its right channel
        // codes intensity positions (-1 where none does) and the granules and channels whose Huffman data ran past their part2_3_length.
        public (int CodedLines, int Overruns, int IntensityLine) Feed(StreamState state, int index, long position, long available, int length, bool isComplete, MpegHeader header, MainDataPlacement? placement, int runsBefore)
        {
            if (IsDeferred)
            {
                return (-1, 0, -1);
            }

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

        // Feeds the streams of MPEG-1 Layer II once the file is read, from the frame records, in the order and with the gap marks the frame loop would have given them.
        // A stream that carries the multichannel extension in full decodes to its configuration's channels (MultichannelPlan), any other as it always did.
        public void DecodeDeferred(StreamState state)
        {
            ChunkedList<Entry> frames = state.Build.Frames;
            foreach ((int start, Node? node) in Deferred)
            {
                int end = start;
                MpegHeader? previous = null;
                for (int i = start; i < frames.Count; i++)
                {
                    if (frames[i].IsGap)
                    {
                        continue;
                    }

                    var header = new MpegHeader(frames[i].Header);
                    if (previous is { } last && !header.IsSameStream(last))
                    {
                        break;
                    }

                    previous = header;
                    end = i + 1;
                }

                MultichannelPlan? plan = PlanFor(state, start, end);
                IDecodeSession? session = null;
                bool isPending = false;
                long position = 0;
                for (int i = start; i < end; i++)
                {
                    state.CheckCancellation(i - start);
                    ref readonly Entry entry = ref frames[i];
                    if (entry.IsGap)
                    {
                        isPending = true;
                        continue;
                    }

                    if (entry.IsVbrHeader)
                    {
                        continue;
                    }

                    var header = new MpegHeader(entry.Header);
                    if (session is null)
                    {
                        var first = new byte[4];
                        state.Blocks.ReadInto(entry.Offset, first);
                        session = state.Context!.StartDecoding(CodecId, new DecodeSetup
                        {
                            Stream = node ?? state.Context.Root,
                            Channels = plan?.Channels ?? (header.IsMono ? 1 : 2),
                            BitsPerSample = 32,
                            Configuration = new MappedBytes(first, entry.Offset),
                            IsLossy = true,
                            Shared = plan,
                        });
                        Sessions[start] = session;
                    }

                    if (!session.IsDecoding)
                    {
                        break;
                    }

                    long firstSample = position;
                    position += header.Samples;
                    FeedLayer2(session, state, entry, header, plan, firstSample, isPending);
                    isPending = false;
                }
            }
        }

        private void FeedLayer2(IDecodeSession session, StreamState state, in Entry entry, MpegHeader header, MultichannelPlan? plan, long firstSample, bool isPending)
        {
            int length = (int)entry.Length;
            bool isComplete = !entry.IsTruncated;
            if (!isComplete || length > _frame.Length)
            {
                session.Decode(new CodedFrame(new MappedBytes(ReadOnlyMemory<byte>.Empty, entry.Offset), "Frame", firstSample, header.Samples) { IsUnreadable = true });
                return;
            }

            state.Blocks.ReadInto(entry.Offset, _frame.AsSpan(0, length));
            int channels = plan is null ? (header.IsMono ? 1 : 2) : 6;
            SampleBuffers lent = session.Lend(LentLayout.PerChannel, channels);
            bool isReadable = !lent.IsEmpty && _scale.Read(header, _frame.AsSpan(0, length));
            if (isReadable)
            {
                lent.Narrow.Clear();
                isReadable = _scale.ReadSamples(header, _frame.AsSpan(0, length), lent.Narrow, lent.Stride);
            }

            if (isReadable && plan is not null)
            {
                isReadable = !header.IsMono && ReadMultichannelData(header, _frame.AsSpan(0, length).ToArray(), plan, lent);
            }

            session.Decode(new CodedFrame(new MappedBytes(_frame.AsMemory(0, length), entry.Offset), "Frame", firstSample, header.Samples)
            {
                IsWalked = isReadable,
                IsUnreadable = !isReadable,
                FollowsGap = isPending,
            });
        }

        // Reads the sample codes of the additional channels and the LFE into the lent planes; false where the extension is not the stream's, fails its CRC or runs past the frame.
        private static bool ReadMultichannelData(MpegHeader header, byte[] bytes, MultichannelPlan plan, SampleBuffers lent)
        {
            var audio = Layer2AudioData.Read(bytes, header);
            var mc = Layer2Multichannel.Read(bytes, audio.End, bytes.Length * 8, audio.BaseAllocation(), header.SampleRate);
            if (!mc.HeaderFits || !mc.IsCrcValid || mc.ChannelKey != plan.Key)
            {
                return false;
            }

            mc.ReadData(bytes, lent.Narrow, lent.Stride);
            return mc.DataFits;
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
