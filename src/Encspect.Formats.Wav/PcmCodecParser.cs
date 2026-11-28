using Encspect.Core;

namespace Encspect.Formats.Wav;

// PCM samples that a container other than WAV stores as they are (ASF's format tags 1, 3, 6 and 7, Matroska's A_PCM IDs, MP4's ipcm, fpcm and QuickTime's sowt, twos, in24, in32, fl32, fl64 and raw entries),
// which the container hands over as packets with the layout its header states (CodecConfiguration.Pcm and Format) and the bytes it holds
// (CodecConfiguration.DataBytes). The samples are counted from the bytes, so the packets, which a container reads lazily, are read only where the
// decoder runs for measurements or output; a file that is only analyzed costs no read of its samples, as a WAV file's does not.
public sealed class PcmCodecParser : ICodecParser
{
    // Not the decoder's ID "pcm": Ogg names its PCM mapping by that ID. MP4, Matroska and ASF hand this parser a layout.
    public const string CodecId = WaveFormatRoutes.PcmSamples;

    public string Id => CodecId;

    public void Parse(CodecContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Packets is not { } packets || context.Configuration is not { Pcm: { } coding, Format: { } format, DataBytes: { } bytes })
        {
            throw new InvalidDataException("The container hands over PCM samples without their layout, rate and channels");
        }

        Node node = context.Parent;
        int channels = format.Channels;
        if (channels is < 1 or > PcmCoding.MaxChannels)
        {
            throw new InvalidDataException($"The container states {channels} channels for the PCM samples, where 1 to {PcmCoding.MaxChannels} are read");
        }

        long frameBytes = coding.FrameBytes(channels);
        long frames = frameBytes > 0 ? bytes / frameBytes : 0;
        long rate = coding.SampleRate;
        double duration = rate > 0 ? frames / (double)rate : 0;
        double? bitrate = duration > 0 ? frames * frameBytes * 8 / duration / 1000 : null;
        IReadOnlyList<Speaker>? layout = coding.ChannelLayout ?? (channels switch { 1 => ChannelLayouts.Mono, 2 => ChannelLayouts.Stereo, _ => null });
        string codec = coding.Encoding switch { PcmEncoding.Float => CodecNames.PcmFloat, PcmEncoding.ALaw => CodecNames.ALaw, PcmEncoding.MuLaw => CodecNames.MuLaw, _ => CodecNames.Pcm };

        Node Cover(string name, NodeValue? value, string description, string? topic = null) =>
            new(name, node.Offset, node.Length, value) { Ranges = node.Ranges, Description = description, Topic = topic };

        var statistics = new Node("Statistics", node.Offset, node.Length) { Ranges = node.Ranges, Kind = NodeKind.Statistics, Description = Explain.Statistics };
        statistics.Add(Cover("Codec", codec, Explain.PcmCodec, Topics.PcmSamples));
        statistics.Add(Cover("Sample Frames", NodeValue.Integer(frames, "sample frames", "sample frame"), Explain.PcmSampleFrames, Topics.PcmSamples));
        statistics.Add(Cover("Duration", NodeValue.Decimal(duration, 3, "s"), Explain.StatDuration));
        statistics.Add(Cover("Sample Rate", NodeValue.Integer(rate, "Hz"), Explain.PcmRate));
        statistics.Add(Cover("Channels", NodeValue.Integer(channels, "channels", "channel"), Explain.PcmChannels, Topics.ChannelLayouts));
        if (layout is not null)
        {
            statistics.Add(Cover("Channel Layout", ChannelLayouts.Describe(layout), Explain.StatLayout, Topics.ChannelLayouts));
        }

        statistics.Add(Cover("Bits Per Sample", NodeValue.Integer(coding.ContainerBits, "bits", "bit"), Explain.StatBits, Topics.PcmSamples));
        statistics.Add(Cover("Sample Format", SampleFormat(coding), Explain.StatSampleFormat, Topics.PcmSamples));
        statistics.Add(Cover("Byte Order", coding.IsBigEndian ? "big-endian" : "little-endian", Explain.PcmByteOrder, Topics.PcmSamples));
        if (bitrate is not null)
        {
            statistics.Add(Cover("Average Bitrate", NodeValue.Decimal(bitrate.Value, 1, "kbps"), Explain.PcmBitrate));
        }

        node.Add(statistics);

        Node? writer = WriterDetection.Detect(context.EncoderClaims, (name, value, description) => Cover(name, value, description, ClaimRoles.TopicOf(name)),
            (claim, text, name) => context.AddFinding(Severity.Info, "pcm.writer-claim-differs", WriterDetection.Disagreement(claim, text, name), claim.Source));
        if (writer is not null)
        {
            node.Add(writer);
        }

        Node? processor = ProcessorDetection.Detect(context.EncoderClaims, (name, value, description) => Cover(name, value, description, ClaimRoles.TopicOf(name)),
            (claim, text, name) => context.AddFinding(Severity.Info, "pcm.processor-claim-differs", ProcessorDetection.Disagreement(claim, text, name), claim.Source));
        if (processor is not null)
        {
            node.Add(processor);
        }

        long leftover = frameBytes > 0 ? bytes - (frames * frameBytes) : bytes;
        if (leftover > 0)
        {
            context.AddFinding(Severity.Warning, "pcm.partial-sample-frame",
                $"The stream's {Counts.Of(bytes, "byte", "bytes")} are not a whole number of sample frames of {Counts.Of(frameBytes, "byte", "bytes")}; {Counts.Of(leftover, "byte", "bytes")} {(leftover == 1 ? "is" : "are")} left over.", node);
        }

        // The decoder runs only where a front end asks for measurements or output; the packets are not read before.
        IDecodeSession session = context.StartDecoding(PcmCoding.CodecId, new DecodeSetup
        {
            Stream = node,
            Channels = channels,
            BitsPerSample = coding.OutputBits,
            Samples = frames,
            Pcm = coding,
        });
        if (session.IsDecoding)
        {
            Feed(context, session, packets, node, frameBytes, frames);
        }

        StreamVerification? verification = session.Finish([]);
        context.AddStream(new StreamInfo
        {
            Nodes = [node],
            Verification = verification,
            Codec = codec,
            SampleRate = (int)Math.Min(rate, int.MaxValue),
            Channels = channels,
            ChannelMode = channels switch { 1 => "Mono", 2 => "Stereo", > 2 => "Multichannel", _ => null },
            ChannelLayout = layout,
            BitsPerSample = coding.ContainerBits,
            Samples = frames,
            Duration = duration,
            AverageBitrate = bitrate,
            Writer = ClaimRoles.Resolved(writer).Name,
            WriterConfidence = ClaimRoles.Resolved(writer).Confidence,
            Processor = ClaimRoles.Resolved(processor).Name,
            ProcessorConfidence = ClaimRoles.Resolved(processor).Confidence,
        });
    }

    private static string SampleFormat(PcmCoding coding) => coding.Encoding switch
    {
        PcmEncoding.Float => "IEEE 754 floating point",
        PcmEncoding.ALaw or PcmEncoding.MuLaw => "companded",
        PcmEncoding.UnsignedInteger => "unsigned integer",
        _ => "signed integer",
    };

    // Hands the packets' bytes to the session in slices of whole sample frames, as PcmFeed does a data chunk: bytes that do not fill a sample frame wait for the next
    // packet, and the stream's last bytes that do not are not samples.
    private static void Feed(CodecContext context, IDecodeSession session, IEnumerable<MappedBytes> packets, Node node, long frameBytes, long frames)
    {
        if (frameBytes < 1 || frames < 1)
        {
            return;
        }

        int capacity = (int)Math.Max(PcmFeed.SliceBytes / frameBytes, 1) * (int)frameBytes;
        byte[] buffer = new byte[capacity];
        int held = 0;
        var pieces = new List<ByteRange>();

        // A packet that fails its container's check (MappedBytes.FailsChecksum) marks the slice it gives bytes to.
        bool fails = false;
        long done = 0;
        int index = 0;
        foreach (MappedBytes packet in packets)
        {
            if (!session.IsDecoding)
            {
                return;
            }

            if (index++ % 4096 == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
            }

            ReadOnlySpan<byte> data = packet.Span;
            int position = 0;
            while (position < data.Length && done < frames)
            {
                int take = Math.Min(data.Length - position, capacity - held);

                // The slice ends where the failing packets begin or end, so that only the sample frames of failing packets are withheld. A sample frame that spans a good and a
                // failing packet goes with the failing side.
                if (held > 0 && fails != packet.FailsChecksum)
                {
                    int partial = held % (int)frameBytes;
                    if (fails && partial > 0)
                    {
                        take = Math.Min(take, (int)frameBytes - partial);
                    }
                    else
                    {
                        done = Hand(session, buffer, held, pieces, node, frameBytes, done, frames, fails);
                        DropFront(buffer, pieces, held - partial, held);
                        held = partial;
                        fails = packet.FailsChecksum;
                        if (done >= frames)
                        {
                            break;
                        }

                        take = Math.Min(data.Length - position, capacity - held);
                    }
                }

                // A slice that spans packets lies in the runs of the file the packets came from, which need not follow one another.
                foreach (ByteRange piece in packet.FileRanges(position, position + take))
                {
                    if (pieces.Count > 0 && pieces[^1].End == piece.Offset)
                    {
                        pieces[^1] = pieces[^1] with { Length = pieces[^1].Length + piece.Length };
                    }
                    else
                    {
                        pieces.Add(piece);
                    }
                }

                data.Slice(position, take).CopyTo(buffer.AsSpan(held));
                fails |= packet.FailsChecksum;
                held += take;
                position += take;
                if (held == capacity)
                {
                    done = Hand(session, buffer, held, pieces, node, frameBytes, done, frames, fails);
                    held = 0;
                    fails = false;
                    pieces.Clear();
                }
            }
        }

        if (held >= frameBytes && done < frames)
        {
            Hand(session, buffer, held, pieces, node, frameBytes, done, frames, fails);
        }
    }

    private static void DropFront(byte[] buffer, List<ByteRange> pieces, int count, int held)
    {
        if (count < 1)
        {
            return;
        }

        Buffer.BlockCopy(buffer, count, buffer, 0, held - count);
        long left = count;
        while (left > 0 && pieces.Count > 0)
        {
            ByteRange first = pieces[0];
            if (first.Length <= left)
            {
                left -= first.Length;
                pieces.RemoveAt(0);
            }
            else
            {
                pieces[0] = first with { Offset = first.Offset + left, Length = first.Length - left };
                left = 0;
            }
        }
    }

    private static long Hand(IDecodeSession session, byte[] buffer, int held, List<ByteRange> pieces, Node node, long frameBytes, long done, long frames, bool fails)
    {
        int count = (int)Math.Min(held / frameBytes, frames - done);
        if (count < 1 || !session.IsDecoding)
        {
            return done;
        }

        session.Decode(new CodedFrame(SliceOf(buffer, (int)(count * frameBytes), pieces), node.Name, done, count) { Node = node, FailsChecksum = fails });
        return done + count;
    }

    // The first bytes of the buffer with the runs of the file they lie in, cut to their length; packets that do not follow one another in file order are placed
    // at the first one's offset.
    private static MappedBytes SliceOf(byte[] buffer, int length, List<ByteRange> pieces)
    {
        var ranges = new List<ByteRange>();
        long left = length;
        long previousEnd = -1;
        foreach (ByteRange piece in pieces)
        {
            if (left <= 0)
            {
                break;
            }

            if (piece.Offset < previousEnd)
            {
                return new MappedBytes(buffer.AsMemory(0, length), pieces[0].Offset);
            }

            long take = Math.Min(piece.Length, left);
            ranges.Add(new ByteRange(piece.Offset, take));
            previousEnd = piece.Offset + take;
            left -= take;
        }

        return new MappedBytes(buffer.AsMemory(0, length), ranges);
    }
}
