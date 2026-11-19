using Encspect.Core;

namespace Encspect.Formats.WavPack;

// A hybrid .wv file and its correction file (docs/design/correction-files.md): asked of the host by the extension, read with the walk of a file of its
// own, and checked against the main file on the structure of both and on the block checksums, without decoding. The correction file's blocks are matched
// to the main file's as libwavpack's read_wvc_block and match_wvc_header match them (open_utils.c); the checksums of the audio are the decoder's.
internal sealed record PairResult(CorrectionPairing Pairing, State? Companion);

internal static class Pairing
{
    public const string Extension = "wvc";

    // The header bits that state the stream and not how a block is coded: the sample size, mono, float, 32-bit integer and false stereo flags, the
    // shift, the rate and DSD. The decorrelation flags, the magnitude and the position in a set are the blocks' own.
    private const uint StreamFlags = BlockHeader.BytesStoredMask | BlockHeader.MonoFlag | BlockHeader.FloatFlag | BlockHeader.Int32Flag | (0x1Fu << BlockHeader.ShiftLsb)
        | (0xFu << BlockHeader.SampleRateLsb) | BlockHeader.FalseStereoFlag | BlockHeader.DsdFlag;

    // The check made for the hybrid main file of state main: asks the host for the .wvc and builds the companion's nodes in its context. isNotWritten is
    // true where the configuration block says the encoder wrote no correction file.
    public static PairResult Check(State main, Node stream, bool isNotWritten)
    {
        Companion companion = main.ParseContext?.OpenCompanion(Extension) ?? Companion.Unchecked(string.Empty, "the analysis has no host to look for it");
        switch (companion.Status)
        {
            case CompanionStatus.Missing:
                return new PairResult(new CorrectionPairing { Status = isNotWritten ? CorrectionStatus.NotWritten : CorrectionStatus.Missing, Source = companion.Source }, null);
            case CompanionStatus.Unchecked:
                return new PairResult(new CorrectionPairing
                {
                    Status = CorrectionStatus.Unchecked,
                    Source = companion.Source.Length == 0 ? null : companion.Source,
                    Reason = companion.Reason,
                    IsUnreadable = companion.IsUnreadable,
                }, null);
        }

        ParseContext context = companion.Context!;
        if (context.Reader.Length == 0)
        {
            return new PairResult(Result(companion, [new CorrectionProblem(CorrectionProblemKind.NotCorrectionData, 1, stream)]), null);
        }

        State other = WavPackParser.ParseInto(context, isCompanion: true);
        return new PairResult(Result(companion, Problems(main, other, stream)), other);
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
    private static List<CorrectionProblem> Problems(State main, State other, Node stream)
    {
        var problems = new List<CorrectionProblem>();
        if (!Summary.IsCorrection(other))
        {
            problems.Add(new CorrectionProblem(CorrectionProblemKind.NotCorrectionData, 1, stream));
            return problems;
        }

        int firstMain = NextMain(main, 0);
        bool differs = firstMain >= 0 && Differs(main.Entries[firstMain].Header, other.FirstAudioHeader);
        if (differs)
        {
            problems.Add(new CorrectionProblem(CorrectionProblemKind.StreamDiffers, 1, Nodes.Block(main.Records, firstMain)));
        }
        else
        {
            Match(main, other, stream, problems);
        }

        Damage(main, other, stream, problems);
        return problems;
    }

    private static bool Differs(in BlockHeader main, in BlockHeader other) =>
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

    // The next block of the correction file with audio: the ones that hold only metadata are no correction blocks.
    private static int NextCorrection(State s, int from)
    {
        for (int i = from; i < s.Entries.Count; i++)
        {
            Entry e = s.Entries[i];
            if (e.Kind == EntryKind.Block && e.Header.HasAudio)
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

    private static void Match(State main, State other, Node stream, List<CorrectionProblem> problems)
    {
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

                int result = Compare(main.Entries[block].Header, other.Entries[correction].Header);
                if (result == -1)
                {
                    missing++;
                    firstMissing = firstMissing < 0 ? block : firstMissing;
                    break;
                }

                correction = NextCorrection(other, correction + 1);
                if (result == 0)
                {
                    break;
                }

                unmatched++;
            }
        }

        for (; correction >= 0; correction = NextCorrection(other, correction + 1))
        {
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
    }

    // The checksums that fail and the damage to the file's blocks, as WavpackVerifySingleBlock sees them: a block whose checksum fails or is missing, or
    // whose sub-blocks do not end with it, is not used. A block cut by the end of the data, one that states more than it holds and bytes that hold no
    // block are the file's being cut or having bytes the blocks do not own.
    private static void Damage(State main, State other, Node stream, List<CorrectionProblem> problems)
    {
        long failed = 0;
        long cut = 0;
        int firstFailed = -1;
        int firstCut = -1;
        int audioBlocks = 0;
        for (int i = 0; i < other.Entries.Count; i++)
        {
            Entry e = other.Entries[i];
            if (e.Kind == EntryKind.Block && e.Header.HasAudio)
            {
                BlockStatus status = e.Status;
                if ((status & (BlockStatus.Truncated | BlockStatus.Oversized)) != 0)
                {
                    cut++;
                    firstCut = firstCut < 0 ? audioBlocks : firstCut;
                }
                else if ((status & (BlockStatus.ChecksumInvalid | BlockStatus.ChecksumMissing | BlockStatus.Malformed)) != 0)
                {
                    failed++;
                    firstFailed = firstFailed < 0 ? audioBlocks : firstFailed;
                }

                audioBlocks++;
            }
            else if (e.Kind == EntryKind.Skipped)
            {
                cut++;
                firstCut = firstCut < 0 ? audioBlocks : firstCut;
            }
        }

        if (failed > 0)
        {
            problems.Add(new CorrectionProblem(CorrectionProblemKind.ChecksumsFail, failed, MainBlock(main, firstFailed, stream)));
        }

        if (cut > 0)
        {
            problems.Add(new CorrectionProblem(CorrectionProblemKind.Cut, cut, MainBlock(main, firstCut, stream)));
        }
    }

    // The main file's node of its hybrid block with this number (counted from 0), or the stream's where there is none.
    private static Node MainBlock(State main, int number, Node stream)
    {
        int block = NextMain(main, 0);
        for (int i = 0; i < number && block >= 0; i++)
        {
            block = NextMain(main, block + 1);
        }

        return block >= 0 ? Nodes.Block(main.Records, block) : stream;
    }
}
