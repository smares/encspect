using Encspect.Core;

namespace Encspect.Formats.Tta;

// The state of one analysis: the records the nodes are built from, the counts worked out as frames are read, and the findings. Shared by the
// format parser, which finds the frames by the seek table, and the codec parser, which is handed them one by one.
internal sealed class State
{
    public State(AnalysisContext context, bool isContained)
    {
        Context = context;
        IsContained = isContained;
    }

    public AnalysisContext Context { get; }

    // The node of the stream; set before the frames are read.
    public Node Stream { get; set; } = null!;

    public bool IsContained { get; }

    public Records Records { get; } = new();

    public FindingCollector Findings { get; } = new();

    // The nodes before the frames: the header, the seek table, the writer and the statistics.
    public List<Node> Leading { get; } = [];

    public void AddLeading(Node node)
    {
        if (IsContained)
        {
            Stream.Add(node);
        }
        else
        {
            Leading.Add(node);
        }
    }

    public TtaHeader? Header => Records.Header;

    public Node? HeaderNode { get; set; }

    public Node? TableNode { get; set; }

    // The header's bytes, which a decoder would take as its configuration.
    public MappedBytes? HeaderBytes { get; set; }

    // What the stream states about the audio, from the header or from the container's own figures.
    public long SampleRate { get; set; }

    public int Channels { get; set; }

    public int BitsPerSample { get; set; }

    // The seek table holds all the entries the header's sample count calls for.
    public bool IsTableComplete { get; set; }

    // Frames the seek table lists but the container or the data did not hand over, and entries it was cut off in.
    public long MissingFrames { get; set; }

    public long TableEntriesExpected { get; set; }

    // A packet followed packets the container lost.
    public bool HasGap { get; set; }

    // Counts over the frames.
    public long Frames { get; private set; }

    public long CutFrames { get; private set; }

    public long TooSmallFrames { get; private set; }

    public long CrcFailures { get; private set; }

    public long Samples { get; private set; }

    public void AddEntry(in Entry entry)
    {
        Records.Entries.Add(entry);
        if ((entry.Status & EntryStatus.Missing) != 0)
        {
            MissingFrames++;
            return;
        }

        Records.FrameNodes++;
        if ((entry.Status & EntryStatus.Cut) != 0)
        {
            CutFrames++;
        }
        else if ((entry.Status & EntryStatus.TooSmall) != 0)
        {
            TooSmallFrames++;
        }
        else
        {
            Frames++;
            if (!entry.IsCrcValid)
            {
                CrcFailures++;
            }

            long samples = Records.SamplesOf(Records.Entries.Count - 1);
            if (samples > 0)
            {
                Samples += samples;
            }
        }
    }
}
