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
        state.MdatIndex = [.. state.Mdats.OrderBy(m => m.Start)];
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
    // type (ISO/IEC 14496-1 Table 5, ISO/IEC 14496-14 5.6), QuickTime's ".mp3", AC-3 and E-AC-3
    // (ETSI TS 102 366 F.3, F.5), and DTS (ETSI TS 102 114 E.2.2.1, and object type 0xA9, which FFmpeg's
    // mp4 object type table gives for DTS).
    // A protected entry is read as the original format its sinf box names.
    private static string? CodecOf(SampleEntry entry) => entry.EffectiveFormat switch
    {
        "mp4a" => entry.ObjectType switch
        {
            0x40 or 0x66 or 0x67 or 0x68 => "aac",
            0x69 or 0x6B => "mpeg-audio",
            0xA9 => "dts",
            _ => null,
        },
        ".mp3" => "mpeg-audio",
        "ac-3" => "ac-3",
        "ec-3" => "e-ac-3",
        "dtsc" or "dtsh" or "dtsl" or "dtse" => "dts",
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

        (SampleEntry entry, bool isProtected, bool isEncrypted) = Decide(state, track);
        track.ReadEntry = entry;
        string? codec = CodecOf(entry);
        StreamProtection? protection = isProtected ? new StreamProtection { Scheme = SchemeOf(entry), IsEncrypted = isEncrypted, Systems = [.. state.Systems] } : null;
        List<ByteRange> ranges = SampleRanges(state, track);
        if (codec is null)
        {
            string format = entry.EffectiveFormat;
            string what = format != "mp4a" ? $"{format} ({FormatName(format)})"
                : entry.ObjectType is { } type ? $"MPEG-4 audio of object type 0x{type:X2}" : "MPEG-4 audio without an esds box";
            state.Findings.AddNow(Severity.Info, "mp4.codec-not-analyzed", $"The audio {label} holds {(protection is null ? string.Empty : "protected ")}{what}, whose samples are not analyzed yet.", entry.Node);

            // A protected track is still a stream, described by its sample entry, so that its protection is reported; any other track
            // whose coding has no parser is shown as boxes only.
            if (protection is null)
            {
                return;
            }
        }

        if (ranges.Count == 0)
        {
            return;
        }

        // The samples' node goes into the mdat box that holds them all, or else directly into the
        // file's MP4 node, where it may span several mdat boxes.
        long start = ranges[0].Offset;
        long end = ranges[^1].End;
        Node? holder = FindMdat(state, start, end);
        Node parent = holder is not null && !state.Findings.IsDetached(holder) ? holder : state.File;
        long count = SampleCount(track);
        var samples = new Node($"Track {track.Id?.ToString(CultureInfo.InvariantCulture) ?? "?"} Samples", start, end - start, NodeValue.Integer(count, "samples", "sample"))
        {
            Ranges = ranges.Count > 1 ? ranges : null,
            Description = Explain.TrackSamples,
        };
        parent.Add(samples);
        int streams = state.Context.Streams.Count;
        if (protection is not null)
        {
            ProtectionNotice(state, track, label, entry, protection);
        }

        if (protection is { IsEncrypted: true } || (protection is not null && codec is null))
        {
            RouteEncrypted(state, track, entry, codec, samples, ranges, first, protection.IsEncrypted, isScattered: false);
        }
        else if (codec == "mpeg-audio" && ranges.Count == 1)
        {
            // MPEG audio in one stretch is read as the same bytes in a file would be.
            state.Context.ParseStream(codec, samples, state.Reader.Slice(start, end - start));
        }
        else if (codec is not null)
        {
            CodecConfiguration? configuration = null;
            if (entry.DecoderSpecificInfo is { } data && entry.DecoderSpecificInfoNode is { } node)
            {
                // The layout claim is about this track: the codec parser sees it, no other track does.
                IReadOnlyList<EncoderClaim> layout = codec == "aac" ? AppleEvidence(state, entry) : [];
                configuration = new CodecConfiguration(data, state.Findings.Resolve(node)) { IsVbr = IsVbr(state, entry, first), Claims = layout };
            }

            state.Context.ParsePackets(codec, samples, Packets(state, track), configuration);
            if (codec == "mpeg-audio" && state.Context.Streams.Count == streams)
            {
                // MPEG audio is read only where its samples lie in one stretch; the track is still a stream, described by its sample entry.
                RouteEncrypted(state, track, entry, codec, samples, ranges, first, isEncrypted: false, isScattered: true);
            }
        }

        if (protection is not null)
        {
            for (int i = streams; i < state.Context.Streams.Count; i++)
            {
                state.Context.UpdateStream(state.Context.Streams[i], state.Context.Streams[i] with { Protection = protection });
            }
        }

        CompareLayouts(state, entry, streams, label);

        // The tracks of one movie play at the same time, and so do the streams of one track that the codec parser reports as playing together
        // (the independent substreams of E-AC-3). Streams of one track that follow one another (a configuration change) are not marked: no one
        // number says that they follow each other and play beside the other tracks.
        List<StreamInfo> made = [.. state.Context.Streams.Skip(streams)];
        bool isTogether = made.Count == 1 || (made.Count > 1 && made[0].PlaybackGroup is not null && made.All(s => s.PlaybackGroup == made[0].PlaybackGroup));
        foreach (StreamInfo stream in made)
        {
            state.Context.UpdateStream(stream, WithUnkept(track, stream) with { PlaybackGroup = isTogether ? 1 : null });
        }
    }

    // The fragment samples past the cap on kept ones were never handed to the codec: their duration and bytes (from the runs' sizes and durations) are
    // added to the stream's figures.
    private static StreamInfo WithUnkept(Track track, StreamInfo stream)
    {
        if (track.UnkeptDuration == 0 || track.Timescale == 0)
        {
            return stream;
        }

        double extra = track.UnkeptDuration / (double)track.Timescale;
        double duration = stream.Duration + extra;
        return stream with
        {
            Duration = duration,
            Samples = stream.Samples + (long)Math.Round(extra * stream.SampleRate),
            AverageBitrate = stream.AverageBitrate is { } rate && duration > 0 ? ((rate * 1000 * stream.Duration) + (track.UnkeptBytes * 8.0)) / duration / 1000 : stream.AverageBitrate,
        };
    }

    // A protected track tells the reader so: its ciphertext is not read, or the scheme covers samples that are in the clear.
    private static void ProtectionNotice(State state, Track track, string label, SampleEntry entry, StreamProtection protection)
    {
        if (protection.IsEncrypted)
        {
            state.Findings.AddNow(Severity.Info, "mp4.samples-encrypted",
                $"The samples of the audio {label} are encrypted ({InSentence(protection.Scheme)}), so their frames are not read; the stream is described by its sample entry and sample tables.", entry.Node);
        }
        else
        {
            state.Findings.AddNow(Severity.Info, "mp4.protected-samples-clear",
                $"The audio {label} is covered by {InSentence(protection.Scheme)}, but its key information says that no sample is protected: the tenc box sets the default to not protected, and nothing in the file says otherwise (no sample group that protects, no sample encryption box).", entry.Protection?.Encryption?.Node ?? entry.Node);
        }
    }

    // The stream of a track whose samples are encrypted, from the sample entry alone: the codec parser reads the configuration
    // (AAC's AudioSpecificConfig, AC-3's dac3 box) but is handed no packets; a codec without a configuration, or whose parser
    // reports no stream, gets a stream built here from the entry. The container's timing fills what the frames would have given.
    private static void RouteEncrypted(State state, Track track, SampleEntry entry, string? codec, Node samples, List<ByteRange> ranges, bool first, bool isEncrypted, bool isScattered)
    {
        double seconds = track.MediaDuration is { } units && track.Timescale > 0 && units != uint.MaxValue && units != ulong.MaxValue ? units / (double)track.Timescale : 0;
        int before = state.Context.Streams.Count;
        int nodes = samples.Children.Count;
        if (isEncrypted && codec is not null && entry.DecoderSpecificInfo is { } data && entry.DecoderSpecificInfoNode is { } node)
        {
            state.Context.ParsePackets(codec, samples, [], new CodecConfiguration(data, state.Findings.Resolve(node))
            {
                AreSamplesEncrypted = true,
                IsVbr = IsVbr(state, entry, first),
                Duration = seconds > 0 ? seconds : null,
            });
        }

        if (state.Context.Streams.Count == before)
        {
            string name = StreamCodecName(entry);
            // The codec parser's node for the stream it read the configuration of, else one of our own.
            Node stream = samples.Children.Skip(nodes).FirstOrDefault(c => c.Kind == NodeKind.Stream) ?? samples.Add(new Node(name, samples.Offset, samples.Length, NodeValue.Text(isEncrypted ? "frames not read: encrypted" : isScattered ? "frames not read: samples not in one stretch" : "frames not read: coding not analyzed"))
            {
                Kind = NodeKind.Stream,
                Ranges = samples.Ranges,
                Description = isEncrypted ? Explain.EncryptedStream : isScattered ? Explain.ScatteredStream : Explain.UnanalyzedStream,
                Topic = isScattered ? null : Topics.CommonEncryption,
            });
            state.Context.AddStream(new StreamInfo
            {
                Nodes = [stream],
                Codec = name,
                SampleRate = (int)Math.Min(entry.SampleRate, int.MaxValue),
                Channels = entry.Channels,
            });
        }

        long bytes = ranges.Sum(r => r.Length);
        for (int i = before; i < state.Context.Streams.Count; i++)
        {
            StreamInfo stream = state.Context.Streams[i];
            if (stream.Duration > 0 || seconds <= 0)
            {
                continue;
            }

            state.Context.UpdateStream(stream, stream with
            {
                Duration = seconds,
                Samples = (long)Math.Round(seconds * stream.SampleRate),
                AverageBitrate = bytes * 8 / seconds / 1000,
            });
        }
    }

    // The software behind the AAC track, from the way it lays out the file. Every AAC file afconvert encoded on macOS 15.7
    // (59 files on each of arm64 and x86-64: AAC-LC including 5.1, HE-AAC, HE-AAC v2, AAC-LD and AAC-ELD, in M4A, M4B, MP4, 3GP and 3GP2)
    // has a DecoderConfigDescriptor whose stream type byte is 0x14, the reserved bit that ISO/IEC 14496-1 7.2.6.6
    // sets to 1 written as 0; all 85 esds boxes of the 86 other fixtures (ffmpeg, fdkaac, FAAC, MP4Box, hand-built) write 0x15. The M4A and M4B
    // files also carry an sbtd box in the sample entry, and all of them a free box that pads the audio data to a
    // 4,096-byte boundary (afconvert's --no-filler changed nothing in these files). The writer also gives the padding to
    // every copy of packets (afconvert -d 0, run 4) and the stream type byte 0x14 to a copy of an ADTS source (an M4A
    // source keeps its own esds, with 0x15), but never the sbtd box, which records the PCM source of an encode: none of the
    // copies has one. So the padding adds nothing beyond the byte in these data, and the byte tells the writer of the file
    // whether or not the writer encoded the audio:
    // - the byte with the padding names the writer, "Apple AudioToolbox", with low confidence;
    // - the byte with an sbtd box names the encoder, with medium confidence, in a claim of its own.
    // A remux by other software drops the traces and names nothing.
    private static IReadOnlyList<EncoderClaim> AppleEvidence(State state, SampleEntry entry)
    {
        if (entry.StreamTypeByte is not { } streamType || (streamType & 1) != 0 || (streamType >> 2) != 5)
        {
            return [];
        }

        string stream = $"a DecoderConfigDescriptor whose stream type byte is 0x{streamType:X2} (reserved bit 0)";
        var claims = new List<EncoderClaim>();
        if (entry.SourceBitDepthNode is not null)
        {
            claims.Add(new EncoderClaim("Apple AAC encoder (AudioToolbox)", state.Findings.Resolve(entry.SourceBitDepthNode),
                $"MP4 layout of Apple's AudioToolbox encoder: {stream} and an sbtd box in the sample entry, which records the PCM source of an encode", ClaimRole.Encoder)
            {
                Confidence = Confidence.Medium,
                IsLayout = true,
            });
        }

        if (state.HasPageAlignedMdat)
        {
            claims.Add(new EncoderClaim("Apple AudioToolbox", state.Findings.Resolve(entry.Node),
                $"MP4 layout of Apple's AudioToolbox file writer: {stream} and a free box that pads the audio data to a 4,096-byte boundary", ClaimRole.Writer)
            {
                IsLayout = true,
            });
        }

        return claims;
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

    private static long SampleCount(Track track) =>
        ((long?)track.Sizes?.Length ?? (track.SampleSize != 0 ? track.SampleCount : 0L)) + track.FragmentSamples.Count + track.EmptyFragmentSamples + track.UnkeptSamples;

    // The mdat box that holds all of [start, end), by binary search over the boxes sorted by start; mdat boxes do not overlap, so the one that
    // starts last at or before start is the only candidate.
    private static Node? FindMdat(State state, long start, long end)
    {
        (long Start, long End, Node Node)[] index = state.MdatIndex;
        int low = 0;
        int high = index.Length - 1;
        int found = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            if (index[middle].Start <= start)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found >= 0 && end <= index[found].End ? index[found].Node : null;
    }

    // How a walk over a track's sample tables is cut: the bytes of samples placed so far against the budget.
    private sealed class SampleWalk(long budget)
    {
        public long Budget => budget;

        public long Placed { get; set; }

        public bool IsCut { get; set; }
    }

    // Every sample of the track in order, as offset, size and how many samples the entry stands
    // for: the chunks' samples, as far as stsz gives sizes and stsc and stco place them, then the
    // fragments'. With one size for every sample, stsz states a count that no table backs, up to
    // 2^32 in a few bytes; a chunk's samples past the end of the file are then one entry, and the samples
    // that lie in the file are cut by the walk's budget of bytes, so the work stays bounded by the file's size.
    // The samples that lie in the file add up to at most the walk's budget of bytes: the walk ends at the first sample that would pass it, since
    // samples of a valid file do not overlap and chunks that share bytes could otherwise yield billions of samples from a few bytes of tables.
    private static IEnumerable<(long Offset, long Size, long Count)> Samples(Track track, long fileEnd, SampleWalk walk)
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
                long inChunk = Math.Min(runs[run].Samples, sizes - index);
                for (long i = 0; i < inChunk; i++)
                {
                    long size = track.Sizes is { } table ? table[index] : track.SampleSize;
                    bool isInFile = position >= 0 && position <= fileEnd - size;
                    if (track.Sizes is null && !isInFile)
                    {
                        yield return (position, size, inChunk - i);
                        index += inChunk - i;
                        break;
                    }

                    if (isInFile)
                    {
                        if (walk.Placed + size > walk.Budget)
                        {
                            walk.IsCut = true;
                            yield break;
                        }

                        walk.Placed += size;
                    }

                    yield return (position, size, 1);
                    position += size;
                    index++;
                }
            }
        }

        for (int i = 0; i < track.FragmentSamples.Count; i++)
        {
            FragmentSample sample = track.FragmentSamples[i];
            yield return (sample.Offset, sample.Size, 1);
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
        long accepted = 0;
        track.SampleBudget = state.SampleBytesLeft;
        var walk = new SampleWalk(track.SampleBudget);
        foreach ((long Offset, long Size, long Count) entry in Samples(track, state.Reader.End, walk))
        {
            if (index++ % 4096 == 0)
            {
                state.Context.CancellationToken.ThrowIfCancellationRequested();
            }

            accepted += entry.Count;

            (long Offset, long Size) sample = (entry.Offset, entry.Size);
            if (!InFile(state, sample))
            {
                outside += entry.Count;
                firstOutside ??= sample;
                continue;
            }

            if (sample.Size == 0)
            {
                empty++;
                continue;
            }

            if (FindMdat(state, sample.Offset, sample.Offset + sample.Size) is null)
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

        state.SampleBytesLeft -= walk.Placed;
        empty += track.EmptyFragmentSamples;
        Node table = track.ChunkOffsetNode ?? track.Box;
        if (walk.IsCut)
        {
            state.Findings.AddNow(Severity.Warning, "mp4.samples-overlap",
                $"The samples of track {track.Id} add up to more bytes than the file has left for them, so some lie over others (the chunks share bytes); the {Samples(accepted)} before the first one past that are analyzed, the rest are not.", table);
        }

        if (firstOutside is { } cut)
        {
            state.Findings.AddNow(Severity.Warning, "mp4.sample-outside-file",
                $"{Samples(outside)} of track {track.Id} lie outside the file, the first at 0x{cut.Offset:X} with {Counts.Of(cut.Size, "byte", "bytes")}; they are not analyzed.", table);
        }

        if (empty > 0)
        {
            state.Findings.AddNow(Severity.Warning, "mp4.empty-samples", $"{Samples(empty)} of track {track.Id} {Counts.Agree(empty, "has", "have")} a size of 0 bytes; {Counts.Agree(empty, "it is", "they are")} not analyzed.", track.SampleSizeNode ?? track.Box);
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
        foreach ((long Offset, long Size, long _) entry in Samples(track, state.Reader.End, new SampleWalk(track.SampleBudget)))
        {
            (long Offset, long Size) sample = (entry.Offset, entry.Size);
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
                    $"The stts box of {label} gives durations to {Counts.Of(timed, "sample", "samples")}, its {stsz.Name} box sizes {sized}.", track.TimeToSampleNode!);
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

                if (firstChunk > offsets.Length)
                {
                    state.Findings.AddNow(Severity.Info, "mp4.stsc-chunk-past-end",
                        $"Entry {run + 1} of the stsc box of {label} starts at chunk {firstChunk}, but the chunk offset box holds {offsets.Length}; no sample uses it, so neither does its sample description.", stsc);
                }

                long last = run + 1 < runs.Count ? Math.Min(runs[run + 1].FirstChunk - 1L, offsets.Length) : offsets.Length;
                placed += Math.Max(0, last - firstChunk + 1) * (long)samples;
            }

            if (placed != sized && track.SampleSizeNode is not null)
            {
                state.Findings.AddNow(Severity.Warning, "mp4.chunk-sample-mismatch", placed < sized
                    ? $"The chunks of {label} hold {Counts.Of(placed, "sample", "samples")}, but stsz sizes {sized}; the {Counts.Of(sized - placed, "sample", "samples")} after them {Counts.Agree(sized - placed, "has", "have")} no place in the file."
                    : $"The chunks of {label} hold {Counts.Of(placed, "sample", "samples")}, but stsz sizes only {sized}.", stsc);
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
            // Apple's lossless writer shortens the last packet by the padding, so the track lasts delay plus samples.
            bool lossless = (track.ReadEntry ?? track.Entries.FirstOrDefault())?.EffectiveFormat == "alac";
            if (total > 0 && state.Fragments == 0 && (ulong)sum != total && !(lossless && (ulong)(sum - smpb.Padding) == total))
            {
                state.Findings.AddNow(Severity.Warning, "mp4.itunsmpb-total",
                    $"iTunSMPB's encoder delay, samples and padding add up to {sum}, but the samples of track {track.Id} last {Counts.Of(total, "unit", "units")} of its timescale.", smpb.Node);
            }

            if (editDelay is { } delay && delay != smpb.Delay)
            {
                state.Findings.AddNow(Severity.Warning, "mp4.gapless-disagree",
                    $"The edit list of track {track.Id} skips {Counts.Of(delay, "sample", "samples")} at the start, but iTunSMPB gives an encoder delay of {smpb.Delay}; players that read one or the other play different samples.", smpb.Node);
            }
            else if (editPadding is { } padding && padding != smpb.Padding)
            {
                state.Findings.AddNow(Severity.Warning, "mp4.gapless-disagree",
                    $"The edit list of track {track.Id} leaves {Counts.Of(padding, "sample", "samples")} at the end, but iTunSMPB gives a padding of {smpb.Padding}; players that read one or the other play different samples.", smpb.Node);
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
            && edits.FindIndex(e => e.MediaTime >= 0 && e.MediaTime + (e.SegmentDuration * (double)track.Timescale / state.MovieTimescale) > duration + 1) is var at and >= 0)
        {
            Edit beyond = edits[at];
            // The edit's node where it is listed, else the node that counts the rest.
            Node shown = at < MaxListedEntries ? elst.Children.Where(c => c.Name == "Edit").ElementAt(at) : elst.Children.FirstOrDefault(c => c.Name == "More Entries") ?? elst;
            state.Findings.AddNow(Severity.Warning, "mp4.edit-beyond-media",
                $"An edit of {label} plays media up to {(beyond.MediaTime + (beyond.SegmentDuration * (double)track.Timescale / state.MovieTimescale)).ToString("F0", CultureInfo.InvariantCulture)} units, past the media's {duration}.", shown);
        }
    }
}
