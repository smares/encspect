using Encspect.Core;

namespace Encspect.Formats.TrueHd;

// The checks over the whole stream, the statistics, the verification and the stream info.
internal static class Summary
{
    public static void Finish(Analysis a, long start, long end, AnalysisContext context)
    {
        a.FlushRun(isLast: true);
        Checks(a);
        StreamVerification? verification = Verify(a, context);
        StreamInfo? info = Statistics(a, start, end, verification);
        if (info is not null)
        {
            context.AddStream(info);
        }

        CountUnrecorded(a);
        a.Findings.Flush(context);
    }

    // The units past the limit of records have a node that counts them, which the findings about them point to.
    private static void CountUnrecorded(Analysis a)
    {
        if (a.UnrecordedFrom < 0)
        {
            return;
        }

        long from = a.UnrecordedFrom;
        var counted = new Node("Counted Access Units", from, a.UnrecordedEnd - from, NodeValue.Integer(a.UnrecordedUnits, "units", "unit")) { Description = Explain.CountedUnits, Topic = Topics.AccessUnits };
        a.Leading.Add(counted);
        a.Records.Fallback = counted;
        a.Findings.AddNow(Severity.Info, "truehd.units-limit",
            $"The stream has more than {Counts.Of(a.RecordLimit, "access unit", "access units")} and skipped stretches; the {Counts.Of(a.UnrecordedUnits, "access unit", "access units")} from 0x{from:X} on are read, checked, decoded and counted in the statistics and graphs, but have no node of their own, and the findings about them point to this node.",
            counted);
    }

    private static void Checks(Analysis a)
    {
        if (a.Records.Entries.Count == 0 || a.FirstSync is null)
        {
            return;
        }

        // A stream whose units are all unsupported has said so already, and is not damaged.
        if (a.SyncUnits > 0 && a.Samples == 0 && !(a.UnsupportedUnits > 0 && a.UnreadableUnits == a.UnsupportedUnits))
        {
            a.Findings.Add(Severity.Warning, "truehd.no-units", a.Stream, _ => "No access unit of the stream could be read, so it holds no audio the analyzer can count.", "unreadable");
        }
    }

    private static bool CoversAllAudio(Analysis a) =>
        a.SubstreamChecked > 0 && a.SubstreamsWithoutCheck == 0 && !a.HasGap && a.UnreadableUnits == 0 && a.CutUnits == 0 && a.SkippedBytes == 0;

    private static StreamVerification? Verify(Analysis a, AnalysisContext context)
    {
        if (a.FirstSync is not { } sync)
        {
            return null;
        }

        // The substreams the decoder was opened for, those of the sync of the first unit that could be read: it does not follow a stream that changes.
        int read = a.SessionSubstreams > 0 ? a.SessionSubstreams : Math.Min(sync.SubstreamCount, Analysis.ParsedSubstreams);
        var tallies = new List<ChecksumTally>
        {
            new() { Name = ChecksumNames.MajorSyncCrc, Covers = ChecksumCoverage.Header, Stored = a.SyncUnits, Checked = a.SyncUnits, Failed = a.SyncCrcFailures },
            new() { Name = ChecksumNames.ParityNibble, Covers = ChecksumCoverage.Header, Stored = a.ParityChecked, Checked = a.ParityChecked, Failed = a.ParityFailures },
            new() { Name = ChecksumNames.RestartHeaderCrc, Covers = ChecksumCoverage.Header, Stored = a.RestartHeaders, Checked = a.RestartHeaders, Failed = a.RestartCrcFailures },
            new() { Name = ChecksumNames.SubstreamParity, Covers = ChecksumCoverage.CodedData, CoversAllAudio = CoversAllAudio(a), Stored = a.SubstreamChecked, Checked = a.SubstreamChecked, Failed = a.SubstreamParityFailures },
            new() { Name = ChecksumNames.SubstreamCrc, Covers = ChecksumCoverage.CodedData, CoversAllAudio = CoversAllAudio(a), Stored = a.SubstreamChecked, Checked = a.SubstreamChecked, Failed = a.SubstreamCrcFailures },
        };
        tallies.AddRange(LosslessTallies(a.LosslessStored, read));
        int top = a.PresentationChannels.Take(read).LastOrDefault(c => c > 0);
        IDecodeSession session = a.Session ?? context.StartDecoding(sync.IsTrueHd ? TrueHdCodecParser.TrueHdId : TrueHdCodecParser.MlpId,
            new DecodeSetup { Stream = a.Stream, Channels = Math.Max(1, top), BitsPerSample = sync.BitDepth });
        return session.Finish(tallies);
    }

    // The lossless checks, one tally for each substream the decoding session was opened for, whether or not it was read: a tally with nothing stored
    // counts for nothing.
    internal static IEnumerable<ChecksumTally> LosslessTallies(IReadOnlyList<long> stored, int read)
    {
        for (int k = 0; k < read; k++)
        {
            yield return new ChecksumTally { Name = ChecksumNames.LosslessCheck(k), Covers = ChecksumCoverage.DecodedAudio, Stored = stored[k] };
        }
    }

    private static StreamInfo? Statistics(Analysis a, long start, long end, StreamVerification? verification)
    {
        if (a.FirstSync is not { } sync)
        {
            return null;
        }

        // The substreams and the layout are those of the sync the decoder was opened with, as the channels and the verification are.
        MajorSync followed = a.SessionSync ?? sync;
        int read = a.SessionSubstreams > 0 ? a.SessionSubstreams : Math.Min(sync.SubstreamCount, Analysis.ParsedSubstreams);
        int channels = a.PresentationChannels.Take(read).LastOrDefault(c => c > 0);
        if (channels == 0)
        {
            return null;
        }

        Node Cover(string name, NodeValue? value, string description, string? topic = null) => new(name, start, end - start, value) { Description = description, Topic = topic };
        var statistics = new Node("Statistics", start, end - start) { Kind = NodeKind.Statistics, Description = Explain.Statistics };
        int rate = followed.SampleRate;
        double duration = a.Duration;
        int top = read - 1;
        Speaker[]? layout = Presentation.Resolve(followed, top, followed.SubstreamCount, channels);
        statistics.Add(Cover("Access Units", NodeValue.Integer(a.Units - a.LeadingUnits, "units", "unit"), Explain.StatUnits, Topics.AccessUnits));
        statistics.Add(Cover("Major Syncs", NodeValue.Integer(a.SyncUnits, "syncs", "sync"), Explain.StatMajorSyncs, Topics.AccessUnits));
        if (a.LeadingUnits > 0)
        {
            statistics.Add(Cover("Units Before First Major Sync", NodeValue.Integer(a.LeadingUnits, "units", "unit"), Explain.StatLeadingUnits, Topics.AccessUnits));
        }

        statistics.Add(Cover("Samples", NodeValue.Integer(a.Samples, "samples"), Explain.StatSamples));
        statistics.Add(Cover("Duration", NodeValue.Decimal(duration, 3, "s"), Explain.StatDuration));
        statistics.Add(Cover("Sampling Rate", NodeValue.Integer(rate, "Hz"), Explain.StatRate));
        statistics.Add(Cover("Channels", NodeValue.Integer(channels, "channels"), Explain.StatChannels, Topics.ChannelLayouts));
        if (layout is not null)
        {
            statistics.Add(Cover("Channel Layout", ChannelLayouts.Describe(layout), Explain.StatLayout, Topics.ChannelLayouts));
        }

        statistics.Add(Cover("Bits Per Sample", NodeValue.Integer(followed.BitDepth, "bits"), Explain.StatBits));
        statistics.Add(Cover("Substreams", NodeValue.Integer(followed.SubstreamCount, "substreams", "substream"), Explain.StatSubstreams, Topics.Substreams));
        statistics.Add(Cover("Presentations", string.Join(", ", a.PresentationChannels.Take(read).Where(c => c > 0).Select(c => $"{c} {(c == 1 ? "channel" : "channels")}")), Explain.StatPresentations, Topics.Substreams));
        statistics.Add(Cover("SignalsAtmos", NodeValue.Boolean(a.SignalsAtmos), Explain.StatSignalsAtmos, Topics.Substreams));
        statistics.Add(Cover("Restart Headers", NodeValue.Integer(a.RestartHeaders, "headers", "header"), Explain.StatRestartHeaders, Topics.RestartHeader));
        statistics.Add(Cover("Verified Parity Nibbles", NodeValue.Integer(a.ParityChecked - a.ParityFailures, "units", "unit"), Explain.StatVerifiedParity, Topics.AccessUnits));
        if (a.ParityFailures > 0)
        {
            statistics.Add(Cover("Failed Parity Nibbles", NodeValue.Integer(a.ParityFailures, "units", "unit"), Explain.StatFailedParity, Topics.AccessUnits));
        }

        statistics.Add(Cover("Verified Major Sync Checksums", NodeValue.Integer(a.SyncUnits - a.SyncCrcFailures, "syncs", "sync"), Explain.StatVerifiedSyncs, Topics.Crc));
        if (a.SyncCrcFailures > 0)
        {
            statistics.Add(Cover("Failed Major Sync Checksums", NodeValue.Integer(a.SyncCrcFailures, "syncs", "sync"), Explain.StatFailedSyncs, Topics.Crc));
        }

        statistics.Add(Cover("Verified Restart Header Checksums", NodeValue.Integer(a.RestartHeaders - a.RestartCrcFailures, "headers", "header"), Explain.StatVerifiedRestarts, Topics.Crc));
        if (a.RestartCrcFailures > 0)
        {
            statistics.Add(Cover("Failed Restart Header Checksums", NodeValue.Integer(a.RestartCrcFailures, "headers", "header"), Explain.StatFailedRestarts, Topics.Crc));
        }

        statistics.Add(Cover("Verified Substream Checksums", NodeValue.Integer(a.SubstreamChecked - a.SubstreamsFailing, "substreams", "substream"), Explain.StatVerifiedSubstreams, Topics.Crc));
        if (a.SubstreamsFailing > 0)
        {
            statistics.Add(Cover("Failed Substream Checksums", NodeValue.Integer(a.SubstreamsFailing, "substreams", "substream"), Explain.StatFailedSubstreams, Topics.Crc));
        }

        if (a.SubstreamsWithoutCheck > 0)
        {
            statistics.Add(Cover("Substreams Without Checksums", NodeValue.Integer(a.SubstreamsWithoutCheck, "substreams", "substream"), Explain.StatUncheckedSubstreams, Topics.Crc));
        }

        statistics.Add(Cover("Stored Lossless Checks", NodeValue.Integer(a.LosslessStored.Sum(), "checks", "check"), Explain.StatLosslessChecks, Topics.LosslessCheck));
        if (a.Units > a.LeadingUnits)
        {
            statistics.Add(Cover("Shortest Access Unit", NodeValue.Integer(a.ShortestUnit, "bytes"), Explain.Smallest));
            statistics.Add(Cover("Longest Access Unit", NodeValue.Integer(a.LongestUnit, "bytes"), Explain.Largest));
        }

        Node Covered(string name, NodeValue? value, string description) => Cover(name, value, description, null);
        a.Figures?.AddTo(statistics, Covered, "Run", "runs", true, out _);
        Node? anchor = a.Figures is null ? null : statistics.Children.FirstOrDefault(c => c.Name == "Average Bitrate");
        a.Leading.Add(statistics);

        string? mode = channels switch { 1 => "Mono", 2 => "Stereo", _ => "Multichannel" };
        return new StreamInfo
        {
            Nodes = [a.Stream],
            Codec = followed.IsTrueHd ? "Dolby TrueHD" : "MLP",
            SampleRate = rate,
            Channels = channels,
            ChannelMode = mode,
            ChannelLayout = layout,
            BitsPerSample = followed.BitDepth,
            Samples = a.Samples,
            Duration = duration,
            AverageBitrate = a.Figures?.AverageBitrate,
            IsVbr = a.Figures is { Units: > 0 } figures && figures.MaximumBitrate > figures.MinimumBitrate,
            Verification = verification,
            Series = a.Figures?.Series(anchor, a.Units, default) ?? [],
        };
    }
}
