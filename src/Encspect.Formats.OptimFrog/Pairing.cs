using Encspect.Core;

namespace Encspect.Formats.OptimFrog;

// A DualStream .ofs file and its correction file (docs/design/correction-files.md): asked of the host by the extension, read with the walk of a file of its
// own, and checked against the main file on block counts and CRC-32s alone. A CORR block holds no sample count, rate or position that is known, so an .ofc
// of another encode with as many blocks passes; the analyzer cannot tell, and no decoder compares the audio.
internal sealed record PairResult(CorrectionPairing Pairing, State? Companion);

internal static class Pairing
{
    public const string Extension = "ofc";

    // The check made for the main file of state main, whose lossy-part claim is made: asks the host for the .ofc and builds the companion's nodes in its context.
    public static PairResult Check(State main, Node stream)
    {
        Companion companion = main.Context.OpenCompanion(Extension);
        switch (companion.Status)
        {
            case CompanionStatus.Missing:
                return new PairResult(new CorrectionPairing { Status = CorrectionStatus.Missing, Source = companion.Source }, null);
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
        ByteReader reader = context.Reader;
        bool isCorrection = Walk.TryReadHeader(reader, reader.Start, out uint id, out _) && id == Layout.Corr;
        if (!isCorrection)
        {
            if (reader.Length > 0)
            {
                context.Root.Add(new Node("Data", reader.Start, reader.Length, NodeValue.Integer(reader.Length, "bytes", "byte")) { Description = Explain.NotCorrectionData });
            }

            return new PairResult(Result(companion, main, [new CorrectionProblem(CorrectionProblemKind.NotCorrectionData, 1, stream)]), null);
        }

        State other = OptimFrogParser.ParseInto(context, isCompanion: true);
        return new PairResult(Result(companion, main, Problems(main, other, stream)), other);
    }

    private static CorrectionPairing Result(Companion companion, State main, List<CorrectionProblem> problems) => new()
    {
        Status = problems.Count == 0 ? CorrectionStatus.Consistent : CorrectionStatus.Inconsistent,
        Source = companion.Source,
        Basis = CorrectionBasis.Structure,
        Problems = problems,
    };

    // One correction block for each data block, every CRC-32 holding, nothing cut and nothing after the chain.
    private static List<CorrectionProblem> Problems(State main, State other, Node stream)
    {
        var problems = new List<CorrectionProblem>();
        long data = main.DataBlocks;
        long corr = other.CorrBlocks;
        if (data > corr)
        {
            problems.Add(new CorrectionProblem(CorrectionProblemKind.BlocksMissing, data - corr, DataBlock(main, corr, stream, main.Context.CancellationToken)));
        }

        if (corr > data)
        {
            problems.Add(new CorrectionProblem(CorrectionProblemKind.BlocksUnmatched, corr - data, stream));
        }

        if (other.CrcFailures > 0)
        {
            problems.Add(new CorrectionProblem(CorrectionProblemKind.ChecksumsFail, other.CrcFailures, DataBlock(main, Math.Max(0, other.FirstCrcFailure), stream, main.Context.CancellationToken)));
        }

        long cut = other.CutBlocks + other.UnknownBlocks + other.SizeInvalidBlocks + (other.Records.End < other.Records.DataEnd ? 1 : 0);
        if (cut > 0)
        {
            problems.Add(new CorrectionProblem(CorrectionProblemKind.Cut, cut, other.FirstCut >= 0 ? DataBlock(main, other.FirstCut, stream, main.Context.CancellationToken) : stream));
        }

        return problems;
    }

    // The main file's node of its data block with this number (counted from 0), or the stream's where there is none or the records stopped before it.
    private static Node DataBlock(State main, long number, Node stream, CancellationToken cancellation)
    {
        Records r = main.Records;
        long seen = 0;
        for (int i = 0; i < r.Blocks.Count; i++)
        {
            if (i % 4096 == 4095)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            BlockRecord b = r.Blocks[i];
            if (b.Id != Layout.Comp || (b.Status & BlockStatus.Unknown) != 0)
            {
                continue;
            }

            if (seen++ == number)
            {
                return Nodes.Block(main.Source, i);
            }
        }

        return stream;
    }
}
