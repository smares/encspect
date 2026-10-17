using Encspect.Core;

namespace Encspect.Formats.Mp4;

// Movie fragments (ISO/IEC 14496-12 8.8) and the segment index (8.16.3): a moof box's track
// fragments place runs of samples, whose sizes come from the run, the track fragment header or the
// track's defaults in mvex, at offsets from a base: the base the fragment header states, else the
// moof box (the default-base-is-moof flag, and the first track fragment of a moof), else where the
// previous track fragment's data ended. Their samples follow the track's samples in moov.
public sealed partial class Mp4Parser
{
    // The fragment samples of a file are kept as 12-byte records, at most this many in all (a day of AAC at
    // 48 kHz); a run that declares more samples than its box or the file holds is cut there, so a
    // damaged count never allocates or loops past the file. Samples of no bytes are counted and not kept,
    // and the bytes the samples place all draw on the file's budget (State.SampleBytesLeft).
    private const int MaxFragmentSamples = 1 << 22;

    private static void FragmentBox(State state, Node node, BoxHeader header, BoxFields f)
    {
        (int version, uint flags) = FullBox(node, f);
        int size = version == 1 ? 8 : 4;
        switch (header.Type)
        {
            case "mehd":
                f.Duration(node, "Fragment Duration", size, state.MovieTimescale, Explain.FragmentDuration);
                break;
            case "trex":
                uint id = f.U32(node, "Track ID", Explain.TrexTrackId);
                uint description = f.U32(node, "Default Sample Description Index", Explain.TrexDescriptionIndex);
                uint duration = f.U32(node, "Default Sample Duration", Explain.TrexDuration);
                uint sampleSize = f.U32(node, "Default Sample Size", Explain.TrexSize, unit: "bytes");
                uint sampleFlags = (uint)f.Field(node, "Default Sample Flags", 4, Explain.SampleFlags, v => NodeValue.Hex((long)v, 8));
                state.TrackDefaults.TryAdd(id, new TrackDefaults(description, duration, sampleSize, sampleFlags));
                node.Value = $"Track Extends, track {id}";
                break;
            case "mfhd":
                node.Value = $"Movie Fragment Header, sequence {f.U32(node, "Sequence Number", Explain.SequenceNumber)}";
                break;
            case "tfhd":
                TrackFragmentHeader(state, node, f, flags);
                break;
            case "tfdt":
                f.Field(node, "Base Media Decode Time", size, Explain.BaseMediaDecodeTime);
                break;
            case "trun":
                TrackRun(state, node, f, version, flags);
                break;
            case "sidx":
                SegmentIndex(node, f, size);
                break;
            case "tfra":
                TrackFragmentRandomAccess(node, f, size);
                break;
            case "mfro":
                f.U32(node, "Size", Explain.MfroSize, unit: "bytes");
                break;
        }
    }

    // 8.8.7: the flags say which fields follow the track ID.
    private static void TrackFragmentHeader(State state, Node node, BoxFields f, uint flags)
    {
        uint id = f.U32(node, "Track ID", Explain.TfhdTrackId);
        node.Value = $"Track Fragment Header, track {id}";
        ulong? baseOffset = (flags & 0x1) != 0 ? f.Field(node, "Base Data Offset", 8, Explain.BaseDataOffset, v => NodeValue.Hex(v, 8)) : null;
        uint description = state.TrackDefaults.TryGetValue(id, out TrackDefaults? defaults) ? defaults.Description : 1;
        if ((flags & 0x2) != 0)
        {
            description = f.U32(node, "Sample Description Index", Explain.TfhdDescriptionIndex);
        }

        if (!state.FragmentDescriptions.TryGetValue(id, out HashSet<uint>? used))
        {
            state.FragmentDescriptions[id] = used = [];
        }

        used.Add(description);

        if ((flags & 0x8) != 0)
        {
            f.U32(node, "Default Sample Duration", Explain.TfhdDuration);
        }

        uint? defaultSize = (flags & 0x10) != 0 ? f.U32(node, "Default Sample Size", Explain.TfhdSize, unit: "bytes") : null;
        if ((flags & 0x20) != 0)
        {
            f.Field(node, "Default Sample Flags", 4, Explain.SampleFlags, v => NodeValue.Hex((long)v, 8));
        }

        if (state.Fragment is { } fragment)
        {
            fragment.HasHeader = true;
            fragment.HeaderCount++;
            if (fragment.HeaderCount == 1)
            {
                fragment.TrackId = id;
            }
            fragment.BaseDataOffset = baseOffset;
            fragment.BaseIsMoof = (flags & 0x20000) != 0;
            fragment.DefaultSize = defaultSize;
        }
    }

    // 8.8.8: a run of samples with, by the flags, a data offset, the first sample's flags, and per
    // sample a duration, size, flags and composition offset.
    private static void TrackRun(State state, Node node, BoxFields f, int version, uint flags)
    {
        uint count = f.U32(node, "Sample Count", Explain.RunSamples);
        long? dataOffset = (flags & 0x1) != 0 ? (int)(uint)f.Field(node, "Data Offset", 4, Explain.DataOffset, v => NodeValue.Integer((int)(uint)v, "bytes")) : null;
        if ((flags & 0x4) != 0)
        {
            f.Field(node, "First Sample Flags", 4, Explain.FirstSampleFlags, v => NodeValue.Hex((long)v, 8));
        }

        int entrySize = 4 * (int)(((flags >> 8) & 1) + ((flags >> 9) & 1) + ((flags >> 10) & 1) + ((flags >> 11) & 1));
        node.Value = NodeValue.Integer(count, "samples", "sample");
        List<uint>? sizes = (flags & 0x200) != 0 ? [] : null;
        uint listed = count;
        if (entrySize > 0 && (long)count * entrySize > f.Remaining)
        {
            listed = (uint)(f.Remaining / entrySize);
            state.Findings.AddNow(Severity.Warning, "mp4.entries-exceed-box", $"The trun box at 0x{node.Offset:X} declares {count} samples, but holds room for {listed}.", node);
        }

        long start = f.Position;
        for (uint i = 0; i < listed && entrySize > 0; i++)
        {
            if (i % 4096 == 0)
            {
                state.Context.CancellationToken.ThrowIfCancellationRequested();
            }

            long at = f.Position;
            Node? entry = entrySize > 0 && i < MaxListedEntries ? new Node("Sample", at, entrySize) { Description = Explain.RunSample } : null;
            uint? size = null;
            if ((flags & 0x100) != 0)
            {
                uint duration = (uint)f.Take(4);
                entry?.Add(new Node("Duration", at, 4, NodeValue.Integer(duration)) { Description = Explain.RunSampleDuration });
            }

            if ((flags & 0x200) != 0)
            {
                long sizeAt = f.Position;
                size = (uint)f.Take(4);
                entry?.Add(new Node("Size", sizeAt, 4, NodeValue.Integer(size.Value, "bytes")) { Description = Explain.RunSampleSize });
            }

            if ((flags & 0x400) != 0)
            {
                long flagsAt = f.Position;
                entry?.Add(new Node("Flags", flagsAt, 4, NodeValue.Hex((long)f.Take(4), 8)) { Description = Explain.RunSampleFlags });
            }

            if ((flags & 0x800) != 0)
            {
                long offsetAt = f.Position;
                uint offset = (uint)f.Take(4);
                entry?.Add(new Node("Composition Offset", offsetAt, 4, NodeValue.Integer(version == 1 ? (int)offset : offset)) { Description = Explain.RunCompositionOffset });
            }

            if (entry is not null)
            {
                entry.Value = size is { } known ? NodeValue.Integer(known, "bytes") : null;
                node.Add(entry);
            }

            if (size is { } value)
            {
                sizes!.Add(value);
            }
        }

        if (entrySize > 0)
        {
            MoreEntries(node, listed, start + (MaxListedEntries * (long)entrySize), f.Position);
        }

        state.Fragment?.Runs.Add((dataOffset, listed, sizes, node));
    }

    // Places the track fragment's samples once its boxes are read.
    private static void FinishFragment(State state, FragmentTrack fragment)
    {
        ResolveFragment(state, fragment);
        if (fragment.HeaderCount > 1)
        {
            state.Findings.Add(Severity.Warning, "mp4.fragment-two-headers", fragment.Node, count =>
                $"{(count == 1 ? "A track fragment holds" : $"{count} track fragments hold")} more than one tfhd box, so whose samples its runs place is not known and they are not analyzed, first at 0x{fragment.Node.Offset:X}.");
            return;
        }

        Track? track = state.TracksById.GetValueOrDefault(fragment.TrackId);
        if (track is null)
        {
            if (fragment.Runs.Count > 0)
            {
                state.Findings.Add(Severity.Warning, "mp4.fragment-unknown-track", fragment.Node, count =>
                    $"{(count == 1 ? "A track fragment refers" : $"{count} track fragments refer")} to track {fragment.TrackId}, which moov does not hold, first at 0x{fragment.Node.Offset:X}.");
            }

            return;
        }

        state.TrackDefaults.TryGetValue(fragment.TrackId, out TrackDefaults? defaults);
        long moof = fragment.Moof?.Offset ?? fragment.Node.Offset;
        long baseOffset = fragment.BaseDataOffset is { } stated ? (long)Math.Min(stated, long.MaxValue / 2)
            : fragment.BaseIsMoof || state.LastFragmentMoof != moof ? moof
            : state.LastFragmentEnd ?? moof;
        long position = baseOffset;
        long fileEnd = state.Reader.End;
        bool isAudio = track.Handler == "soun";
        foreach ((long? dataOffset, uint count, List<uint>? sizes, Node run) in fragment.Runs)
        {
            if (dataOffset is { } offset)
            {
                position = baseOffset + offset;
            }

            long placed = 0;
            while (placed < count)
            {
                if (placed % 4096 == 0)
                {
                    state.Context.CancellationToken.ThrowIfCancellationRequested();
                }

                uint size = sizes?[(int)placed] ?? fragment.DefaultSize ?? defaults?.Size ?? 0;
                if (position < 0 || position + size > fileEnd)
                {
                    state.Findings.Add(Severity.Warning, "mp4.fragment-sample-outside-file", run, count =>
                        $"{(count == 1 ? "A track run places samples" : $"{count} track runs place samples")} outside the file, first the run at 0x{run.Offset:X}; they are not analyzed.");
                    break;
                }

                if (size == 0)
                {
                    // Samples of no bytes all lie at one place: without sizes of their own, the rest of the run is counted at once.
                    long empty = sizes is null ? count - placed : 1;
                    track.EmptyFragmentSamples += empty;
                    placed += empty;
                    continue;
                }

                if (!isAudio)
                {
                    // Only an audio track's samples are kept; the others only move the position on, as far as the sizes are known at once.
                    long rest = sizes is null ? Math.Min(count - placed, (fileEnd - position) / size) : 1;
                    position += rest * size;
                    placed += rest;
                    if (sizes is null && placed < count)
                    {
                        state.Findings.Add(Severity.Warning, "mp4.fragment-sample-outside-file", run, count =>
                            $"{(count == 1 ? "A track run places samples" : $"{count} track runs place samples")} outside the file, first the run at 0x{run.Offset:X}; they are not analyzed.");
                        break;
                    }

                    continue;
                }

                if (state.FragmentSampleCount >= MaxFragmentSamples)
                {
                    state.Findings.Add(Severity.Warning, "mp4.fragment-samples-limit", run, _ =>
                        $"The fragments of the file hold more than {MaxFragmentSamples} samples of audio tracks; the samples from the run at 0x{run.Offset:X} on are not analyzed.");
                    break;
                }

                if (size > state.SampleBytesLeft)
                {
                    state.Findings.Add(Severity.Warning, "mp4.samples-overlap", run, _ =>
                        $"The samples of the track fragments add up to more bytes than the file has left for them, so some lie over others; the samples from the run at 0x{run.Offset:X} on are not analyzed.");
                    break;
                }

                state.SampleBytesLeft -= size;
                state.FragmentSampleCount++;
                track.FragmentSamples.Add(new FragmentSample(position, (int)Math.Min(size, int.MaxValue)));
                position += size;
                placed++;
            }
        }

        track.FirstFragment ??= fragment.Node;
        state.LastFragmentEnd = position;
        state.LastFragmentMoof = moof;
    }

    // 8.16.3: references to subsegments with their sizes, durations and stream access points.
    private static void SegmentIndex(Node node, BoxFields f, int size)
    {
        f.U32(node, "Reference ID", Explain.ReferenceId);
        uint timescale = f.U32(node, "Timescale", Explain.SidxTimescale, unit: "units per second");
        f.Duration(node, "Earliest Presentation Time", size, timescale, Explain.EarliestPresentationTime);
        f.Field(node, "First Offset", size, Explain.FirstOffset, unit: "bytes");
        f.Skip(2);
        int count = f.U16(node, "References", Explain.ReferenceCount);
        for (int i = 0; i < count && i < MaxListedEntries && f.Remaining >= 12; i++)
        {
            long at = f.Position;
            uint first = (uint)f.Take(4);
            uint duration = (uint)f.Take(4);
            uint sap = (uint)f.Take(4);
            var reference = new Node("Reference", at, 12, $"{first & 0x7FFFFFFF} bytes, {BoxFields.DurationText(duration, timescale)}") { Description = Explain.Reference };
            reference.Add(Node.FromBits("Reference Type", at, 0, 1, (first >> 31) == 0 ? "0 (media)" : "1 (segment index)", description: Explain.ReferenceType));
            reference.Add(Node.FromBits("Referenced Size", at, 1, 32, NodeValue.Integer(first & 0x7FFFFFFF, "bytes"), description: Explain.ReferencedSize));
            reference.Add(new Node("Subsegment Duration", at + 4, 4, BoxFields.DurationText(duration, timescale)) { Description = Explain.SubsegmentDuration });
            reference.Add(Node.FromBits("StartsWithSap", at + 8, 0, 1, NodeValue.Boolean((sap >> 31) != 0), description: Explain.StartsWithSap));
            reference.Add(Node.FromBits("SAP Type", at + 8, 1, 4, NodeValue.Integer((sap >> 28) & 7), description: Explain.SapType));
            reference.Add(Node.FromBits("SAP Delta Time", at + 8, 4, 32, NodeValue.Integer(sap & 0x0FFFFFFF), description: Explain.SapDeltaTime));
            node.Add(reference);
        }
    }

    // 8.8.10: the moof boxes and samples a player can start at.
    private static void TrackFragmentRandomAccess(Node node, BoxFields f, int size)
    {
        node.Value = $"Track Fragment Random Access, track {f.U32(node, "Track ID", Explain.TfraTrackId)}";
        uint lengths = (uint)f.Field(node, "Length Sizes", 4, Explain.LengthSizes, v => NodeValue.Hex((long)v, 8));
        uint count = f.U32(node, "Entries", Explain.TfraEntries);
        int traf = (int)((lengths >> 4) & 3) + 1;
        int trun = (int)((lengths >> 2) & 3) + 1;
        int sample = (int)(lengths & 3) + 1;
        for (uint i = 0; i < count && i < MaxListedEntries && f.Remaining >= (2 * size) + traf + trun + sample; i++)
        {
            long at = f.Position;
            var entry = new Node("Entry", at, (2 * size) + traf + trun + sample) { Description = Explain.TfraEntry };
            f.Field(entry, "Time", size, Explain.TfraTime);
            f.Field(entry, "Moof Offset", size, Explain.MoofOffset, v => NodeValue.Hex(v, 8));
            f.Field(entry, "Track Fragment Number", traf, Explain.TrafNumber);
            f.Field(entry, "Track Run Number", trun, Explain.TrunNumber);
            f.Field(entry, "Sample Number", sample, Explain.TfraSampleNumber);
            node.Add(entry);
        }
    }
}
