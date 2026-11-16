namespace Encspect.Formats.Dts;

// What the primary audio coding header (5.4.3) and the frame header state that the syntax of the subframes (5.5, 5.6) depends on, kept while the analysis reads the headers.
internal sealed class CoreAudioHeader
{
    public const int MaxChannels = 5;

    public int Channels { get; private init; }

    // SUBFS + 1.
    public int Subframes { get; private init; }

    // nSUBS, nVQSUB, JOINX, THUFF, SHUFF and BHUFF of each channel, and SEL and the ADJ index of each channel and quantizer ABITS 1 to 10.
    public int[] Subs { get; private init; } = [];

    public int[] VqSub { get; private init; } = [];

    public int[] Joinx { get; private init; } = [];

    public int[] Thuff { get; private init; } = [];

    public int[] Shuff { get; private init; } = [];

    public int[] Bhuff { get; private init; } = [];

    public int[][] Sel { get; private init; } = [];

    public int[][] Adj { get; private init; } = [];

    // CPF, DYNF, ASPF, LFF, RATE and NBLKS of the frame header, and the flags of the reconstruction.
    public bool HasCrc { get; private init; }

    public bool HasDynamicRange { get; private init; }

    public bool SyncPerSubsubframe { get; private init; }

    public int Lff { get; private init; }

    public int Blocks { get; private init; }

    public static CoreAudioHeader Of(int channels, int subframes, int[] subs, int[] vqsub, int[] joinx, int[] thuff, int[] shuff, int[] bhuff, int[][] sel, int[][] adj, CoreHeader header) => new()
    {
        Channels = channels,
        Subframes = subframes,
        Subs = [.. subs.Take(channels).Select(v => Math.Min(v + 2, 32))],
        VqSub = [.. vqsub.Take(channels).Select(v => v + 1)],
        Joinx = [.. joinx.Take(channels)],
        Thuff = [.. thuff.Take(channels)],
        Shuff = [.. shuff.Take(channels)],
        Bhuff = [.. bhuff.Take(channels)],
        Sel = [.. sel.Take(channels).Select(row => (int[])row.Clone())],
        Adj = [.. adj.Take(channels).Select(row => (int[])row.Clone())],
        HasCrc = header.HasCrc,
        HasDynamicRange = header.HasDynamicRange,
        SyncPerSubsubframe = header.SyncPerSubsubframe,
        Lff = header.Lff,
        Blocks = header.Blocks,
    };
}

internal enum WalkProblem : byte
{
    None,

    // A subframe or subsubframe does not end with the synchronization word 0xFFFF (5.5, 5.6).
    Sync,

    // The subframes run past the end of the core's data.
    Overrun,

    // A field holds a value the syntax cannot use: an index of a table that is invalid there, a block code beyond its levels, a reserved LFE flag.
    Invalid,

    // A subframe with a partial subsubframe (PSC above 0), whose coding the specification does not give.
    Partial,
}

// What reading the subframes of one core frame found.
internal sealed class SubframeWalk
{
    public WalkProblem Problem { get; set; }

    // Where the problem is, in bits of the unit, with the subframe and a few words on what it is.
    public int ProblemBit { get; set; }

    public int ProblemSubframe { get; set; }

    public string ProblemDetail { get; set; } = string.Empty;

    // Subframes read whole.
    public int Subframes { get; set; }

    // The subband samples per channel of those subframes (8 per subsubframe).
    public int Samples { get; set; }

    // The highest subband that carries audio in any subframe and channel, plus one: 0 where none does. A subband carries audio where bits are allocated to it, where its
    // samples are vector quantized, or where joint intensity coding copies it from another channel.
    public int Subbands { get; set; }

    // A bit for each subframe read: set where some subband of some channel has a transient (TMODE above 0).
    public int TransientMask { get; set; }

    public int Transients { get; set; }

    public bool IsComplete => Problem == WalkProblem.None;
}
