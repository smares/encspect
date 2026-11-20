// Derived from libwavpack 5.6.0 (open_utils.c: match_wvc_header and read_wvc_block), Copyright (c) 1998 - 2022 David Bryant, under the BSD 3-clause license.
// The license text is in THIRD-PARTY-NOTICES.md.

using Encspect.Core;

namespace Encspect.Formats.WavPack;

// A hybrid .wv file and its correction file (docs/design/correction-files.md): asked of the host by the extension, read with the walk of a file of its
// own, and checked against the main file on the structure of both and on the block checksums, without decoding. The correction file's blocks are matched
// to the main file's as libwavpack's read_wvc_block and match_wvc_header match them (open_utils.c); the checksums of the audio are the decoder's.
internal sealed record PairResult(CorrectionPairing Pairing, State? Companion);

// What the host's lookup of the correction file gave and, where it was found and holds WavPack blocks, the walk of it. The lookup is made when the main file's
// first hybrid block is read, so that the decoder has the correction file's blocks as it decodes; the pairing is checked once the main file's blocks are all read.
internal sealed record Opened(Companion Handle, State? Other);

internal static class Pairing
{
    public const string Extension = "wvc";

    // The header bits that state the stream and not how a block is coded: the sample size, mono, float, 32-bit integer and false stereo flags, the
    // shift, the rate and DSD. The decorrelation flags, the magnitude and the position in a set are the blocks' own.
    private const uint StreamFlags = BlockHeader.BytesStoredMask | BlockHeader.MonoFlag | BlockHeader.FloatFlag | BlockHeader.Int32Flag | (0x1Fu << BlockHeader.ShiftLsb)
        | (0xFu << BlockHeader.SampleRateLsb) | BlockHeader.FalseStereoFlag | BlockHeader.DsdFlag;

    // Asks the host for the .wvc, once, and walks it as a file of its own, building the companion's nodes in its context. A WavPack 1 to 3 file beside a
    // version 4 or 5 file is another stream, not its correction data, and an empty one holds nothing: neither has a state.
    public static Opened Open(State main)
    {
        if (main.Opened is { } opened)
        {
            return opened;
        }

        Companion companion = main.ParseContext?.OpenCompanion(Extension) ?? Companion.Unchecked(string.Empty, "the analysis has no host to look for it");
        State? other = companion.Status == CompanionStatus.Found && companion.Context!.Reader.Length > 0 ? WavPackParser.ParseInto(companion.Context, isCompanion: true) : null;
        return main.Opened = new Opened(companion, other);
    }

    // Whether the correction file states the stream of the main file's block first, so that it is used for it (not another stream, and holding correction data).
    public static bool Belongs(in BlockHeader first, State other) => Summary.IsCorrection(other) && !Differs(first, other.FirstAudioHeader);

    // The check made for the hybrid main file of state main, once its blocks are read. isNotWritten is true where the configuration block says the encoder wrote
    // no correction file, isWritten where it says it wrote one.
    public static PairResult Check(State main, Node stream, bool isNotWritten, bool isWritten)
    {
        Opened opened = Open(main);
        Companion companion = opened.Handle;
        switch (companion.Status)
        {
            case CompanionStatus.Missing:
                return new PairResult(new CorrectionPairing { Status = isNotWritten ? CorrectionStatus.NotWritten : CorrectionStatus.Missing, Source = companion.Source, IsWritten = isWritten }, null);
            case CompanionStatus.Unchecked:
                return new PairResult(new CorrectionPairing
                {
                    Status = CorrectionStatus.Unchecked,
                    Source = companion.Source.Length == 0 ? null : companion.Source,
                    Reason = companion.Reason,
                    IsUnreadable = companion.IsUnreadable,
                }, null);
        }

        if (opened.Other is not { } other)
        {
            return new PairResult(Result(companion, [new CorrectionProblem(CorrectionProblemKind.NotCorrectionData, 1, stream)]), null);
        }

        List<CorrectionProblem> problems = Problems(main, other, stream, out long matched);
        // The companion's checksums count in the main stream's verification only where its blocks are used: where it states another stream, or none of its blocks
        // matches a block of the file, they are not the stream's.
        bool isUsed = matched > 0 && !problems.Any(p => p.Kind is CorrectionProblemKind.NotCorrectionData or CorrectionProblemKind.StreamDiffers);
        return new PairResult(Result(companion, problems), isUsed ? other : null);
    }

    private static CorrectionPairing Result(Companion companion, List<CorrectionProblem> problems) => new()
    {
        Status = problems.Count == 0 ? CorrectionStatus.Consistent : CorrectionStatus.Inconsistent,
        Source = companion.Source,
        Basis = CorrectionBasis.Structure,
        Problems = problems,
    };

    // The companion is correction data of the main stream: not another stream (the first blocks' version, total samples, first block index and the
    // flags that state the format), a correction block for each hybrid block and none that no block matches, every block's checksum holding, nothing cut.
    private static List<CorrectionProblem> Problems(State main, State other, Node stream, out long matched)
    {
        matched = 0;
        var problems = new List<CorrectionProblem>();
        if (!Summary.IsCorrection(other))
        {
            problems.Add(new CorrectionProblem(CorrectionProblemKind.NotCorrectionData, 1, stream));
            return problems;
        }

        Dictionary<int, int> blocks = [];
        int firstMain = NextMain(main, 0);
        bool differs = firstMain >= 0 && Differs(main.Entries[firstMain].Header, other.FirstAudioHeader);
        if (differs)
        {
            problems.Add(new CorrectionProblem(CorrectionProblemKind.StreamDiffers, 1, Nodes.Block(main.Records, firstMain)));
        }
        else
        {
            matched = Match(main, other, stream, problems, out blocks);
        }

        Damage(main, other, stream, problems, blocks);
        return problems;
    }

    internal static bool Differs(in BlockHeader main, in BlockHeader other) =>
        main.Version != other.Version || main.TotalSamples != other.TotalSamples || main.BlockIndex != other.BlockIndex || ((main.Flags ^ other.Flags) & StreamFlags) != 0;

    // The next block of the main file that takes a correction block: with audio and the hybrid flag.
    private static int NextMain(State s, int from)
    {
        for (int i = from; i < s.Entries.Count; i++)
        {
            Entry e = s.Entries[i];
            if (e.Kind == EntryKind.Block && e.Header.HasAudio && e.Header.IsHybrid)
            {
                return i;
            }
        }

        return -1;
    }

    // The next block of the correction file: libwavpack's read_next_header takes any block, one that holds no audio included, which then never matches
    // (its sample count differs) and is skipped or leaves the main block without a correction, as match_wvc_header says. Runs of header-only blocks past the
    // detailed ones are not kept block by block and are passed over.
    internal static int NextCorrection(State s, int from)
    {
        for (int i = from; i < s.Entries.Count; i++)
        {
            Entry e = s.Entries[i];
            if (e.Kind == EntryKind.Block)
            {
                return i;
            }
        }

        return -1;
    }

    // libwavpack's match_wvc_header: 0 the correction block is the main block's, 1 it comes before it (read the next one), -1 the main block has none
    // (the correction block is kept for the next).
    internal static int Compare(in BlockHeader main, in BlockHeader other)
    {
        if (main.BlockIndex == other.BlockIndex && main.Samples == other.Samples)
        {
            if (main.Flags == other.Flags)
            {
                return 0;
            }

            int inMain = (main.IsInitial ? -1 : 0) + (main.IsFinal ? 1 : 0);
            int inOther = (other.IsInitial ? -1 : 0) + (other.IsFinal ? 1 : 0);
            return inOther - inMain < 0 ? 1 : -1;
        }

        return ((other.BlockIndex - main.BlockIndex) & 0x8000000000L) != 0 ? 1 : -1;
    }

    // Returns how many main blocks took a correction block; blocks says which main entry each correction entry was matched with.
    private static long Match(State main, State other, Node stream, List<CorrectionProblem> problems, out Dictionary<int, int> blocks)
    {
        blocks = [];
        long matched = 0;
        long missing = 0;
        long unmatched = 0;
        int firstMissing = -1;
        int correction = NextCorrection(other, 0);
        int step = 0;
        for (int block = NextMain(main, 0); block >= 0; block = NextMain(main, block + 1))
        {
            if (++step % 4096 == 0)
            {
                main.Cancellation.ThrowIfCancellationRequested();
            }

            while (true)
            {
                if (correction < 0)
                {
                    missing++;
                    firstMissing = firstMissing < 0 ? block : firstMissing;
                    break;
                }

                if (++step % 4096 == 0)
                {
                    main.Cancellation.ThrowIfCancellationRequested();
                }

                int result = Compare(main.Entries[block].Header, other.Entries[correction].Header);
                if (result == -1)
                {
                    missing++;
                    firstMissing = firstMissing < 0 ? block : firstMissing;
                    break;
                }

                int taken = correction;
                correction = NextCorrection(other, correction + 1);
                if (result == 0)
                {
                    matched++;
                    blocks[taken] = block;
                    break;
                }

                unmatched++;
            }
        }

        for (; correction >= 0; correction = NextCorrection(other, correction + 1))
        {
            if (++step % 4096 == 0)
            {
                main.Cancellation.ThrowIfCancellationRequested();
            }

            unmatched++;
        }

        if (missing > 0)
        {
            problems.Add(new CorrectionProblem(CorrectionProblemKind.BlocksMissing, missing, Nodes.Block(main.Records, firstMissing)));
        }

        if (unmatched > 0)
        {
            problems.Add(new CorrectionProblem(CorrectionProblemKind.BlocksUnmatched, unmatched, stream));
        }

        return matched;
    }

    // The checksums that fail and the damage to the file's blocks, as WavpackVerifySingleBlock sees them: a block whose checksum fails or is missing, or
    // whose sub-blocks do not end with it, is not used. A block cut by the end of the data, one that states more than it holds and bytes that hold no
    // block are the file's being cut or having bytes the blocks do not own.
    private static void Damage(State main, State other, Node stream, List<CorrectionProblem> problems, Dictionary<int, int> blocks)
    {
        long failed = 0;
        long cut = 0;
        int firstFailed = -1;
        int firstCut = -1;
        for (int i = 0; i < other.Entries.Count; i++)
        {
            if (i % 4096 == 4095)
            {
                main.Cancellation.ThrowIfCancellationRequested();
            }

            Entry e = other.Entries[i];
            if (e.Kind == EntryKind.Block && e.Header.HasAudio)
            {
                BlockStatus status = e.Status;
                if ((status & (BlockStatus.Truncated | BlockStatus.Oversized)) != 0)
                {
                    cut++;
                    firstCut = firstCut < 0 ? i : firstCut;
                }
                else if ((status & (BlockStatus.ChecksumInvalid | BlockStatus.ChecksumMissing | BlockStatus.Malformed)) != 0)
                {
                    failed++;
                    firstFailed = firstFailed < 0 ? i : firstFailed;
                }
            }
            else if (e.Kind == EntryKind.Skipped)
            {
                cut++;
                firstCut = firstCut < 0 ? i : firstCut;
            }
        }

        if (failed > 0)
        {
            problems.Add(new CorrectionProblem(CorrectionProblemKind.ChecksumsFail, failed, MainBlock(main, blocks, firstFailed, stream)));
        }

        if (cut > 0)
        {
            problems.Add(new CorrectionProblem(CorrectionProblemKind.Cut, cut, MainBlock(main, blocks, firstCut, stream)));
        }
    }

    // The main file's node of the block the correction block at this entry was matched with, or the stream's where it was matched with none.
    private static Node MainBlock(State main, Dictionary<int, int> blocks, int entry, Node stream) =>
        entry >= 0 && blocks.TryGetValue(entry, out int block) ? Nodes.Block(main.Records, block) : stream;
}
