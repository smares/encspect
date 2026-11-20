using Encspect.Core;

namespace Encspect.Formats.WavPack;

// A version 3 hybrid file and its correction file (docs/design/correction-files.md, "Version 3"). The .wvc is the correction bitstream alone, with no header or signature,
// so the structure tells nothing but whether it is empty: the pairing's basis is the decoded audio. The decoder reads the correction bitstream beside the main one
// (unpack3.c's WVC_FLAG paths) and compares the second CRC of the header, which is the CRC of the lossless audio; the pair is consistent where it holds and the
// correction bitstream did not end before the samples did. Without decoding the pair is unchecked.
internal static partial class Legacy
{
    internal const string UncheckedReason = "a version 3 correction file has no header; only decoding with it compares it";

    // unpack3_open.c: a correction file is looked for only for a version 3 file with a bit limit or hybrid target and the new high flag.
    private static bool AsksForCorrection(LegacyHeader h) => h.IsVersion3 && h.Bits != 0 && (h.EffectiveFlags & LegacyHeader.NewHighFlag) != 0;

    // The bytes of the correction file where there is one that holds any: an empty file is no correction data and is not handed to the decoder.
    private static ByteReader? CorrectionBytes(Companion? companion) =>
        companion is { Status: CompanionStatus.Found, Context: { } context } && context.Reader.Length > 0 ? context.Reader : null;

    // The pairing as the decoding ended it. A decoding that was not asked for, or did not compare the second CRC, leaves the pair unchecked.
    private static CorrectionPairing Pair(Companion companion, ByteReader? correction, StreamVerification? verification, LegacyDecodeInfo info, Node stream, Node header)
    {
        switch (companion.Status)
        {
            case CompanionStatus.Missing:
                return new CorrectionPairing { Status = CorrectionStatus.Missing, Source = companion.Source, Basis = CorrectionBasis.DecodedAudio };
            case CompanionStatus.Unchecked:
                return new CorrectionPairing
                {
                    Status = CorrectionStatus.Unchecked,
                    Source = companion.Source.Length == 0 ? null : companion.Source,
                    Basis = CorrectionBasis.DecodedAudio,
                    Reason = companion.Reason,
                    IsUnreadable = companion.IsUnreadable,
                };
        }

        if (correction is null)
        {
            return Result(companion, CorrectionStatus.Inconsistent, [new CorrectionProblem(CorrectionProblemKind.NotCorrectionData, 1, stream)], null);
        }

        ChecksumTally? tally = verification?.Checksums.FirstOrDefault(t => t.Name == ChecksumNames.LegacyCrc2);
        if (verification is not { Decoding: DecodeStatus.Decoded } || tally is not { Checked: 1 })
        {
            return Result(companion, CorrectionStatus.Unchecked, [], UncheckedReason);
        }

        if (tally.Failed == 0)
        {
            return Result(companion, CorrectionStatus.Consistent, [], null);
        }

        Node crc2 = header.Children.FirstOrDefault(c => c.Name == "Correction CRC") ?? header;
        return Result(companion, CorrectionStatus.Inconsistent, [new CorrectionProblem(info.IsCorrectionShort ? CorrectionProblemKind.Cut : CorrectionProblemKind.ChecksumsFail, 1, crc2)], null);
    }

    private static CorrectionPairing Result(Companion companion, CorrectionStatus status, List<CorrectionProblem> problems, string? reason) => new()
    {
        Status = status,
        Source = companion.Source,
        Basis = CorrectionBasis.DecodedAudio,
        Problems = problems,
        Reason = reason,
    };

    // The correction file's own tree and stream: one node over all the bitstream's bytes, with decoding or without it (decoding never changes the parser's nodes; the bytes the
    // decoder did not reach are its finding).
    private static void ReportCorrectionFile(ParseContext context, LegacyDecodeInfo info)
    {
        long start = context.Reader.Start;
        long length = context.Reader.Length;
        var bitstream = new Node("Correction Bitstream", start, length, NodeValue.Integer(length, "bytes", "byte"))
        {
            Kind = NodeKind.Stream,
            Description = LegacyExplain.CorrectionBitstream,
            Topic = Topics.CorrectionFiles,
        };
        context.Root.Add(bitstream);
        context.AddStream(new StreamInfo { Nodes = [bitstream], Codec = "WavPack correction data", IsCorrectionFile = true });
    }
}
