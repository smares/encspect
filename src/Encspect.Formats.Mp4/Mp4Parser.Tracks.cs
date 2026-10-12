using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Mp4;

// Each audio track's samples, from its sample tables (ISO/IEC 14496-12 8.7: a chunk's samples lie
// one after another from its offset, stsc says how many each chunk holds, stsz how large each is)
// and then its fragments, are checked against the file and the mdat boxes and handed to the codec
// parser of their coding. Its timing is checked across mvhd, tkhd, mdhd and stts, and its encoder
// delay and padding are compared where the edit list and iTunSMPB both give them.
public sealed partial class Mp4Parser
{
    private static void RouteTracks(State state)
    {
        bool first = true;
        foreach (Track track in state.Tracks)
        {
            if (track.Handler != "soun")
            {
                continue;
            }

            CheckTables(state, track);
            CheckDurations(state, track);
            Route(state, track, first);
            Gapless(state, track, first);
            first = false;
        }
    }

    // The sample entry's coding as the codec route names it: MPEG-4 audio by its esds box's object
    // type (ISO/IEC 14496-1 Table 5, ISO/IEC 14496-14 5.6), and QuickTime's ".mp3".
    private static string? CodecOf(SampleEntry entry) => entry.Format switch
    {
        "mp4a" => entry.ObjectType switch
        {
            0x40 or 0x66 or 0x67 or 0x68 => "aac",
            0x69 or 0x6B => "mpeg-audio",
            _ => null,
        },
        ".mp3" => "mpeg-audio",
        _ => null,
    };

    private static void Route(State state, Track track, bool first)
    {
        string label = track.Id is { } id ? $"track {id}" : $"the track at 0x{track.Box.Offset:X}";
        if (track.Entries.Count == 0)
        {
            state.Findings.AddNow(Severity.Warning, "mp4.no-sample-description", $"The audio {label} has no sample description, so its samples are not analyzed.", track.Box);
            return;
        }

        SampleEntry entry = track.Entries[0];
        string? codec = CodecOf(entry);
        List<ByteRange> ranges = SampleRanges(state, track);
        if (codec is null)
        {
            string what = entry.Format != "mp4a" ? $"{entry.Format} ({FormatName(entry.Format)})"
                : entry.ObjectType is { } type ? $"MPEG-4 audio of object type 0x{type:X2}" : "MPEG-4 audio without an esds box";
            state.Findings.AddNow(Severity.Info, "mp4.codec-not-analyzed", $"The audio {label} holds {what}, whose samples are not analyzed yet.", entry.Node);
            return;
        }

        if (ranges.Count == 0)
        {
            return;
        }

        // The samples' node goes into the mdat box that holds them all, or else directly into the
        // file's MP4 node, where it may span several mdat boxes.
        long start = ranges[0].Offset;
        long end = ranges[^1].End;
        Node parent = state.Mdats.FirstOrDefault(m => m.Start <= start && end <= m.End).Node ?? state.File;
        long count = SampleCount(track);
        var samples = new Node($"Track {track.Id?.ToString(CultureInfo.InvariantCulture) ?? "?"} Samples", start, end - start, NodeValue.Integer(count, "samples", "sample"))
        {
            Ranges = ranges.Count > 1 ? ranges : null,
            Description = Explain.TrackSamples,
        };
        parent.Add(samples);
        if (codec == "mpeg-audio" && ranges.Count == 1)
        {
            // MPEG audio in one stretch is read as the same bytes in a file would be.
            state.Context.ParseStream(codec, samples, state.Reader.Slice(start, end - start));
        }
        else
        {
            CodecConfiguration? configuration = null;
            if (entry.DecoderSpecificInfo is { } data && entry.DecoderSpecificInfoNode is { } node)
            {
                configuration = new CodecConfiguration(data, node) { IsVbr = IsVbr(state, entry, first) };
            }

            state.Context.ParsePackets(codec, samples, Packets(state, track), configuration);
        }
    }

    // What the file says about the bitrate: Apple's encoder records its mode in Encoding Params, and
    // ISO/IEC 14496-1 7.2.6.6.2 sets avgBitrate to 0 for a variable bitrate.
    private static bool? IsVbr(State state, SampleEntry entry, bool first)
    {
        if (first && state.EncodingParams is { Mode: { } mode })
        {
            return mode != 0;
        }

        return entry.AverageBitrate is { } average ? average == 0 : null;
    }

    private static long SampleCount(Track track) => ((long?)track.Sizes?.Length ?? (track.SampleSize != 0 ? track.SampleCount : 0L)) + track.FragmentSamples.Count;

    // Every sample of the track in order, as offset and size: the chunks' samples, as far as stsz
    // gives sizes and stsc and stco place them, then the fragments'.
    private static IEnumerable<(long Offset, long Size)> Samples(Track track)
    {
        long sizes = (long?)track.Sizes?.Length ?? (track.SampleSize != 0 ? track.SampleCount : 0L);
        if (track.ChunkOffsets is { } offsets && track.SampleToChunk is { Count: > 0 } runs && sizes > 0)
        {
            long index = 0;
            int run = 0;
            for (int chunk = 0; chunk < offsets.Length && index < sizes; chunk++)
            {
                while (run + 1 < runs.Count && runs[run + 1].FirstChunk <= chunk + 1)
                {
                    run++;
                }

                long position = offsets[chunk];
                for (uint i = 0; i < runs[run].Samples && index < sizes; i++)
                {
                    long size = track.Sizes is { } table ? table[index] : track.SampleSize;
                    yield return (position, size);
                    position += size;
                    index++;
                }
            }
        }

        for (int i = 0; i < track.FragmentSamples.Count; i++)
        {
            FragmentSample sample = track.FragmentSamples[i];
            yield return (sample.Offset, sample.Size);
        }
    }

    private static bool InFile(State state, (long Offset, long Size) sample) =>
        sample.Offset >= state.Reader.Start && sample.Size >= 0 && sample.Offset <= state.Reader.End - sample.Size;

    // The file ranges the track's samples cover, merged and in file order. Samples outside the file
    // are reported and left out; samples outside every mdat box are reported but still read.
    private static List<ByteRange> SampleRanges(State state, Track track)
    {
        var ranges = new List<ByteRange>();
        long outside = 0;
        long outsideMdat = 0;
        long empty = 0;
        (long Offset, long Size)? firstOutside = null;
        (long Offset, long Size)? firstOutsideMdat = null;
        long index = 0;
        foreach ((long Offset, long Size) sample in Samples(track))
        {
            if (index++ % 4096 == 0)
            {
                state.Context.CancellationToken.ThrowIfCancellationRequested();
            }

            if (!InFile(state, sample))
            {
                outside++;
                firstOutside ??= sample;
                continue;
            }

            if (sample.Size == 0)
            {
                empty++;
                continue;
            }

            if (!state.Mdats.Any(m => m.Start <= sample.Offset && sample.Offset + sample.Size <= m.End))
            {
                outsideMdat++;
                firstOutsideMdat ??= sample;
            }

            if (ranges.Count > 0 && ranges[^1].End == sample.Offset)
            {
                ranges[^1] = ranges[^1] with { Length = ranges[^1].Length + sample.Size };
            }
            else
            {
                ranges.Add(new ByteRange(sample.Offset, sample.Size));
            }
        }

        Node table = track.ChunkOffsetNode ?? track.Box;
        if (firstOutside is { } cut)
        {
            state.Findings.AddNow(Severity.Warning, "mp4.sample-outside-file",
                $"{Samples(outside)} of track {track.Id} lie outside the file, the first at 0x{cut.Offset:X} with {cut.Size} bytes; they are not analyzed.", table);
        }

        if (empty > 0)
        {
            state.Findings.AddNow(Severity.Warning, "mp4.empty-samples", $"{Samples(empty)} of track {track.Id} have a size of 0 bytes; they are not analyzed.", track.SampleSizeNode ?? track.Box);
        }

        if (firstOutsideMdat is { } stray)
        {
            state.Findings.AddNow(Severity.Warning, "mp4.sample-outside-mdat",
                $"{Samples(outsideMdat)} of track {track.Id} lie outside every mdat box, the first at 0x{stray.Offset:X}.", table);
        }

        ranges.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        var merged = new List<ByteRange>();
        foreach (ByteRange range in ranges)
        {
            if (merged.Count > 0 && range.Offset <= merged[^1].End)
            {
                merged[^1] = merged[^1] with { Length = Math.Max(merged[^1].End, range.End) - merged[^1].Offset };
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }

    private static string Samples(long count) => count == 1 ? "1 sample" : $"{count} samples";

    private static IEnumerable<MappedBytes> Packets(State state, Track track)
    {
        long index = 0;
        foreach ((long Offset, long Size) sample in Samples(track))
        {
            if (index++ % 4096 == 0)
            {
                state.Context.CancellationToken.ThrowIfCancellationRequested();
            }

            if (InFile(state, sample) && sample.Size > 0)
            {
                yield return MappedBytes.Read(state.Reader, [new ByteRange(sample.Offset, sample.Size)]);
            }
        }
    }

    // The tables must agree on the number of samples: stsz lists them, stts gives each a duration,
    // and stsc with stco places them in chunks (8.6.1.2, 8.7.3, 8.7.4, 8.7.5).
    private static void CheckTables(State state, Track track)
    {
        string label = $"track {track.Id}";
        long sized = (long?)track.Sizes?.Length ?? (track.SampleSize != 0 ? track.SampleCount : 0L);
        if (track.SampleSizeNode is { } stsz && track.TimeToSample is { } stts)
        {
            long timed = stts.Sum(e => (long)e.Count);
            if (timed != sized)
            {
                state.Findings.AddNow(Severity.Warning, "mp4.sample-count-mismatch",
                    $"The stts box of {label} gives durations to {timed} samples, its {stsz.Name} box sizes {sized}.", track.TimeToSampleNode!);
            }
        }

        if (track.SampleToChunk is { } runs && track.ChunkOffsets is { } offsets && track.SampleToChunkNode is { } stsc)
        {
            long placed = 0;
            for (int run = 0; run < runs.Count; run++)
            {
                (uint firstChunk, uint samples, uint description) = runs[run];
                if (firstChunk == 0 || (run > 0 && firstChunk <= runs[run - 1].FirstChunk) || (run == 0 && firstChunk != 1))
                {
                    state.Findings.AddNow(Severity.Warning, "mp4.stsc-order",
                        $"Entry {run + 1} of the stsc box of {label} starts at chunk {firstChunk}; the first entry must start at chunk 1 and each later one after the one before.", stsc);
                    break;
                }

                if (description == 0 || description > track.Entries.Count)
                {
                    state.Findings.AddNow(Severity.Warning, "mp4.sample-description-index",
                        $"Entry {run + 1} of the stsc box of {label} refers to sample description {description}, but the stsd box holds {track.Entries.Count}.", stsc);
                }

                long last = run + 1 < runs.Count ? Math.Min(runs[run + 1].FirstChunk - 1L, offsets.Length) : offsets.Length;
                placed += Math.Max(0, last - firstChunk + 1) * (long)samples;
            }

            if (placed != sized && track.SampleSizeNode is not null)
            {
                state.Findings.AddNow(Severity.Warning, "mp4.chunk-sample-mismatch", placed < sized
                    ? $"The chunks of {label} hold {placed} samples, but stsz sizes {sized}; the {sized - placed} samples after them have no place in the file."
                    : $"The chunks of {label} hold {placed} samples, but stsz sizes only {sized}.", stsc);
            }
        }
    }

    // mdhd's duration is the sum of the media's sample durations (8.4.2.3); tkhd's is the track's
    // presentation, in the movie's timescale: the edit list's segments where there is one, else the
    // media's duration (8.3.2.3).
    private static void CheckDurations(State state, Track track)
    {
        if (track.TimeToSample is not { } stts || track.MediaHeader is not { } mdhd || state.Fragments > 0)
        {
            return;
        }

        ulong total = 0;
        foreach ((uint count, uint delta) in stts)
        {
            total += (ulong)count * delta;
        }

        string label = $"track {track.Id}";
        if (track.MediaDuration is { } media && media != total && media != uint.MaxValue && media != ulong.MaxValue)
        {
            state.Findings.AddNow(Severity.Warning, "mp4.duration-mismatch",
                $"The mdhd box of {label} gives a duration of {BoxFields.DurationText(media, track.Timescale)}, but its samples' durations in stts add up to {BoxFields.DurationText(total, track.Timescale)}.", mdhd);
        }

        if (track.Timescale == 0 || state.MovieTimescale == 0 || track.TrackDuration is not { } presented || track.TrackHeader is not { } tkhd
            || track.MediaDuration is not { } mediaDuration)
        {
            return;
        }

        double expected = track.Edits is { Count: > 0 } edits
            ? edits.Sum(e => (double)e.SegmentDuration)
            : mediaDuration * (double)state.MovieTimescale / track.Timescale;
        if (Math.Abs(presented - expected) > 1)
        {
            state.Findings.AddNow(Severity.Warning, "mp4.duration-mismatch",
                $"The tkhd box of {label} gives a duration of {BoxFields.DurationText(presented, state.MovieTimescale)}, but its {(track.Edits is { Count: > 0 } ? "edit list comes" : "media's duration comes")} to {(expected / state.MovieTimescale).ToString("F3", CultureInfo.InvariantCulture)} s.", tkhd);
        }
    }

    // What the edit list, the roll groups and iTunSMPB say about the encoder delay and padding, under
    // the trak box, and a warning where the edit list and iTunSMPB disagree. Both count in the media's
    // timescale, which is the sample rate for AAC LC and, as fdkaac writes HE-AAC, the core's rate.
    private static void Gapless(State state, Track track, bool first)
    {
        GaplessInfo? smpb = first ? state.Smpb : null;
        var nodes = new List<Node>();
        Node Cover(string name, NodeValue value, string description) =>
            new(name, track.Box.Offset, track.Box.Length, value) { Description = description, Topic = Topics.EditListsAndPriming };
        NodeValue Units(long count) => NodeValue.Integer(count, "samples", "sample");
        ulong total = 0;
        foreach ((uint count, uint delta) in track.TimeToSample ?? [])
        {
            total += (ulong)count * delta;
        }

        long? editDelay = null;
        long? editPadding = null;
        if (track.Edits is { } edits && track.EditListNode is { } elst)
        {
            CheckEdits(state, track, edits, elst);
            Edit[] media = edits.Where(e => e.MediaTime >= 0).ToArray();
            if (media.Length == 1 && track.Timescale > 0 && state.MovieTimescale > 0)
            {
                Edit edit = media[0];
                long presented = (long)Math.Round(edit.SegmentDuration * (double)track.Timescale / state.MovieTimescale, MidpointRounding.ToEven);
                editDelay = edit.MediaTime;
                nodes.Add(Cover("Encoder Delay (Edit List)", Units(edit.MediaTime), Explain.EditDelay));
                nodes.Add(Cover("Samples (Edit List)", Units(presented), Explain.EditSamples));
                if (total > 0 && (long)total - edit.MediaTime - presented is var padding and >= 0)
                {
                    editPadding = padding;
                    nodes.Add(Cover("Padding (Edit List)", Units(padding), Explain.EditPadding));
                }
            }
        }

        foreach ((string type, int distance, Node _) in track.Rolls)
        {
            nodes.Add(Cover(type == "prol" ? "Pre-Roll Distance" : "Roll Distance", NodeValue.Integer(distance, "samples", "sample"), type == "prol" ? Explain.GaplessPreRoll : Explain.GaplessRoll));
        }

        if (smpb is not null)
        {
            nodes.Add(Cover("Encoder Delay (iTunSMPB)", Units(smpb.Delay), Explain.SmpbGaplessDelay));
            nodes.Add(Cover("Samples (iTunSMPB)", Units(smpb.Samples), Explain.SmpbGaplessSamples));
            nodes.Add(Cover("Padding (iTunSMPB)", Units(smpb.Padding), Explain.SmpbGaplessPadding));
            long sum = smpb.Delay + smpb.Samples + smpb.Padding;
            if (total > 0 && state.Fragments == 0 && (ulong)sum != total)
            {
                state.Findings.AddNow(Severity.Warning, "mp4.itunsmpb-total",
                    $"iTunSMPB's encoder delay, samples and padding add up to {sum}, but the samples of track {track.Id} last {total} units of its timescale.", smpb.Node);
            }

            if (editDelay is { } delay && delay != smpb.Delay)
            {
                state.Findings.AddNow(Severity.Warning, "mp4.gapless-disagree",
                    $"The edit list of track {track.Id} skips {delay} samples at the start, but iTunSMPB gives an encoder delay of {smpb.Delay}; players that read one or the other play different samples.", smpb.Node);
            }
            else if (editPadding is { } padding && padding != smpb.Padding)
            {
                state.Findings.AddNow(Severity.Warning, "mp4.gapless-disagree",
                    $"The edit list of track {track.Id} leaves {padding} samples at the end, but iTunSMPB gives a padding of {smpb.Padding}; players that read one or the other play different samples.", smpb.Node);
            }
        }

        if (nodes.Count > 0)
        {
            Node gapless = track.Box.Add(new Node("Gapless", track.Box.Offset, track.Box.Length) { Kind = NodeKind.Statistics, Description = Explain.Gapless, Topic = Topics.GaplessPlayback });
            foreach (Node node in nodes)
            {
                gapless.Add(node);
            }
        }
    }

    // 8.6.6: an empty edit delays the presentation, more than one media edit cuts it into pieces
    // players handle differently, and a rate other than 1 is a dwell or a change of speed.
    private static void CheckEdits(State state, Track track, List<Edit> edits, Node elst)
    {
        string label = $"track {track.Id}";
        int media = edits.Count(e => e.MediaTime >= 0);
        if (edits.Any(e => e.MediaTime == -1))
        {
            state.Findings.AddNow(Severity.Info, "mp4.edit-list-empty-edit", $"The edit list of {label} starts or pauses the presentation with an empty edit.", elst);
        }

        if (media > 1)
        {
            state.Findings.AddNow(Severity.Info, "mp4.edit-list-several-edits", $"The edit list of {label} plays {media} stretches of the media; players differ in how they follow more than one.", elst);
        }

        if (edits.Any(e => e.MediaTime < -1))
        {
            state.Findings.AddNow(Severity.Warning, "mp4.edit-list-invalid-time", $"The edit list of {label} has a media time below -1, which ISO/IEC 14496-12 does not define.", elst);
        }

        if (edits.Any(e => e.MediaTime >= 0 && e.Rate != 1))
        {
            state.Findings.AddNow(Severity.Info, "mp4.edit-list-rate", $"The edit list of {label} plays media at a rate other than 1.", elst);
        }

        if (track.MediaDuration is { } duration && track.Timescale > 0 && state.MovieTimescale > 0
            && edits.FirstOrDefault(e => e.MediaTime >= 0 && e.MediaTime + (e.SegmentDuration * (double)track.Timescale / state.MovieTimescale) > duration + 1) is { } beyond)
        {
            state.Findings.AddNow(Severity.Warning, "mp4.edit-beyond-media",
                $"An edit of {label} plays media up to {(beyond.MediaTime + (beyond.SegmentDuration * (double)track.Timescale / state.MovieTimescale)).ToString("F0", CultureInfo.InvariantCulture)} units, past the media's {duration}.", beyond.Node);
        }
    }
}
