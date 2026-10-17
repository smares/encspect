using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Matroska;

internal sealed partial class FileReader
{
    // What the specification requires of a Segment as a whole (RFC 9559 sections 5, 6 and 10), once all of it is read.
    private void CheckSegment(SegmentState segment)
    {
        ApplyLateScales(segment);
        Node anchor = segment.Node!;
        bool hasClusters = segment.ClusterCount > 0;
        string number = _segments.Count > 1 ? $" {segment.Number}" : string.Empty;
        if (!segment.IsHeaderMissing)
        {
            if (segment.InfoCount == 0 && !segment.HasSkippedElements)
            {
                _findings.AddNow(Severity.Warning, "matroska.missing-info", $"The Segment{number} holds no Info element, the one Top-Level Element RFC 9559 requires (section 6.1).", anchor);
            }

            if (hasClusters && segment.TracksCount == 0 && !segment.HasSkippedElements)
            {
                _findings.AddNow(Severity.Warning, "matroska.missing-tracks",
                    $"The Segment{number} holds Clusters but no Tracks element, which a playable file needs (RFC 9559, section 6.1), so no block can be assigned to a track.", anchor);
            }
            else if (!hasClusters)
            {
                _findings.AddNow(Severity.Info, "matroska.no-clusters", $"The Segment{number} holds no Cluster, so no frames; a playable file needs one (RFC 9559, section 6.1).", anchor);
            }
        }

        foreach (TrackInfo track in segment.Tracks)
        {
            CheckTrack(segment, track);
            if (track.Number is { } trackNumber && segment.Statistics.TryGetValue(trackNumber, out TrackStatistics? statistics) && segment.TracksByNumber.GetValueOrDefault(trackNumber) == track)
            {
                track.Statistics = statistics;
                if (statistics.Backwards > 0 && (track.IsAudio || segment.Document?.IsWebM == true))
                {
                    TrackStatistics shown = statistics;
                    _findings.Add(Severity.Warning, "matroska.timestamps-backwards", shown.FirstBackwards!, count =>
                        $"{(count == 1 ? "A track's" : $"{count} tracks'")} blocks go back in time: track {trackNumber}'s timestamp falls {Counts.Of(shown.Backwards, "time", "times")} below the one before it in file order, the first at 0x{shown.FirstBackwards!.Offset:X}. Frames of {(track.IsAudio ? "audio" : "WebM")} tracks are stored in time order (RFC 9559, section 10; the WebM Container Guidelines).");
                }

                if (statistics.Laced && !track.AllowsLacing)
                {
                    TrackStatistics shown = statistics;
                    _findings.Add(Severity.Warning, "matroska.lacing-not-allowed", shown.FirstBlock!, count =>
                        $"{(count == 1 ? "A track whose FlagLacing is 0 has" : $"{count} tracks whose FlagLacing is 0 have")} laced blocks, which RFC 9559 forbids (section 5.1.4.1.12); track {trackNumber}'s first is at 0x{shown.FirstBlock!.Offset:X}.");
                }
            }
        }

        foreach ((ulong id, TrackStatistics statistics) in segment.Statistics.Select(p => (p.Key, p.Value)))
        {
            if (!segment.TracksByNumber.ContainsKey(id) && !segment.HasSkippedElements)
            {
                _findings.Add(Severity.Warning, "matroska.block-track-unknown", statistics.FirstBlock!, count =>
                    $"{(count == 1 ? "Blocks name" : $"Blocks name {count} tracks")} no TrackEntry describes (RFC 9559, section 10): {(id == ulong.MaxValue ? "many track numbers" : $"track {id}")} has {statistics.Blocks} {(statistics.Blocks == 1 ? "block" : "blocks")}, the first at 0x{statistics.FirstBlock!.Offset:X}; they are not assigned to a track.");
            }
        }

        CheckSeeks(segment);
        CheckCues(segment);
        CheckWebM(segment);
    }

    private void CheckTrack(SegmentState segment, TrackInfo track)
    {
        Node node = track.Node;
        string which = track.Number is { } number ? $"Track {number}" : "A track";
        void Missing(string element, string section) =>
            _findings.AddNow(Severity.Warning, "matroska.track-field", $"{which} has no {element}, which RFC 9559 requires of every TrackEntry (section {section}).", node);

        if (track.Number is null)
        {
            Missing("TrackNumber", "5.1.4.1.1");
        }
        else if (track.Number == 0)
        {
            _findings.AddNow(Severity.Warning, "matroska.track-field", "A track has the TrackNumber 0, which RFC 9559 forbids (section 5.1.4.1.1).", node);
        }

        if (track.Uid is null)
        {
            Missing("TrackUID", "5.1.4.1.2");
        }

        if (track.Type is null)
        {
            Missing("TrackType", "5.1.4.1.3");
        }

        if (track.CodecId is null)
        {
            Missing("CodecID", "5.1.4.1.21");
        }

        if (track.IsAudio)
        {
            if (!track.HasAudio)
            {
                _findings.AddNow(Severity.Warning, "matroska.audio-fields",
                    $"{which} is an audio track without an Audio element, which the codec mapping requires (draft-ietf-cellar-codec, section 3.4); its sampling frequency and channels are unstated.", node);
            }
            else if (track.Channels == 0 || (track.SamplingFrequency is { } rate && !Codecs.IsRate(rate)))
            {
                _findings.AddNow(Severity.Warning, "matroska.audio-fields",
                    $"{which} states {(track.Channels == 0 ? "0 channels" : $"a sampling frequency of {track.SamplingFrequency?.ToString(CultureInfo.InvariantCulture)} Hz")}, which RFC 9559 forbids (section 5.1.4.1.29).",
                    track.Channels == 0 ? track.ChannelsNode ?? node : track.SamplingFrequencyNode ?? node);
            }
        }

        // Like the SamplingFrequency, the OutputSamplingFrequency must be above 0 (section 5.1.4.1.29.2). An invalid one is not used.
        if (track.IsAudio && track.OutputSamplingFrequency is { } output && !Codecs.IsRate(output))
        {
            bool isLegacySbr = track.CodecId is { } codecId && codecId.StartsWith("A_AAC/MPEG", StringComparison.Ordinal) && codecId.EndsWith("/SBR", StringComparison.Ordinal);
            _findings.AddNow(Severity.Warning, "matroska.audio-fields",
                $"{which} states an output sampling frequency of {output.ToString(CultureInfo.InvariantCulture)} Hz, which RFC 9559 forbids (section 5.1.4.1.29.2); it is not used{(isLegacySbr ? ", so no AudioSpecificConfig is worked out from the legacy SBR codec ID and the AAC parser gets no configuration" : string.Empty)}.",
                track.OutputSamplingFrequencyNode ?? track.AudioNode ?? node);
        }

        if (track.CodecId == "A_OPUS" && track.CodecDelayNode is null)
        {
            _findings.AddNow(Severity.Warning, "matroska.track-field",
                $"{which} (A_OPUS) has no CodecDelay, which the codec mapping requires to be the OpusHead's pre-skip in nanoseconds (draft-ietf-cellar-codec, section 3.4.32).", track.CodecIdNode ?? node);
        }

        var orders = new HashSet<ulong>();
        foreach (EncodingInfo encoding in track.Encodings)
        {
            if (!orders.Add(encoding.Order))
            {
                _findings.AddNow(Severity.Warning, "matroska.encoding-order",
                    $"{which} has two ContentEncodings with the ContentEncodingOrder {encoding.Order}, which RFC 9559 requires to be unique (section 5.1.4.1.31.2).", encoding.Node ?? node);
            }
        }

        // Encodings that cannot be ordered have no verdict to check: which one hides which, and which is next, would depend on the order of the elements.
        if (!track.HasDuplicateOrders)
        {
            foreach (OrderedEncoding ordered in track.OrderedEncodings())
            {
                CheckEncryption(which, ordered, track.CodecPrivateOffset >= 0 && track.CodecPrivateLength > 0, node);
            }
        }
    }

    // The ContentEncryption of a ContentEncoding (RFC 9559 sections 5.1.4.1.31.8 to 5.1.4.1.31.12): what the track's reader ignores or reads as clear.
    private void CheckEncryption(string which, OrderedEncoding ordered, bool hasPrivate, Node fallback)
    {
        EncodingInfo encoding = ordered.Encoding;
        Node node = encoding.Node ?? fallback;

        // Its settings are unreadable, so what they state is not checked.
        if (ordered.Hider is { } hider)
        {
            string consequence = ordered.HiderEncrypts ? "the parts it covers count as encrypted by it"
                : ordered.IsHiddenEncrypted ? "an encryption is hidden here, so the parts it covers count as encrypted by an unknown algorithm"
                : "it is not undone";
            _findings.AddNow(Severity.Info, "matroska.encoding-hidden",
                $"{which} has a ContentEncoding of order {encoding.Order} whose settings are hidden by the ContentEncoding of order {hider.Order} (ContentEncodingScope bit 4, section 5.1.4.1.31.3); they are not read, and {consequence}.", node);
            return;
        }

        if (encoding.Type == 1 && !(encoding.HasEncryption && encoding.EncryptionAlgorithm == 0) && (encoding.Scope & 1) == 0 && !((encoding.Scope & 2) != 0 && hasPrivate) && !((encoding.Scope & 4) != 0 && ordered.HasNext))
        {
            _findings.AddNow(Severity.Info, "matroska.encryption-covers-nothing",
                $"{which} has a ContentEncoding of type 1 (encryption) whose ContentEncodingScope names neither the frames nor a CodecPrivate the track has and which has no next ContentEncoding to hide (section 5.1.4.1.31.3), so it encrypts nothing and the track is not reported as protected by it.", node);
        }

        if (encoding.Type == 0 && encoding.HasEncryption)
        {
            _findings.AddNow(Severity.Info, "matroska.encryption-ignored",
                $"{which} has a ContentEncoding of type 0 (compression) with a ContentEncryption element, which a reader ignores unless the type is 1 (section 5.1.4.1.31.8).", node);
        }
        else if (encoding.Type == 1 && !encoding.HasEncryption)
        {
            _findings.AddNow(Severity.Warning, "matroska.encryption-missing",
                $"{which} has a ContentEncoding of type 1 (encryption) without a ContentEncryption element, although RFC 9559 requires one for that type (section 5.1.4.1.31.8); the algorithm is unknown.", node);
        }
        else if (encoding.Type == 1 && encoding.EncryptionAlgorithm == 0)
        {
            _findings.AddNow(Severity.Info, "matroska.encryption-none",
                $"{which} has a ContentEncoding of type 1 (encryption) whose ContentEncAlgo is 0, which states that the data are not encrypted (Table 24); this encoding changes nothing.", node);
        }

        if (encoding.Type == 1 && encoding.HasAesSettings && encoding.HasEncryption && encoding.EncryptionAlgorithm != 5)
        {
            _findings.AddNow(Severity.Warning, "matroska.aes-settings-not-aes",
                $"{which} has ContentEncAESSettings although its ContentEncAlgo is {encoding.EncryptionAlgorithm}, not AES (5), which RFC 9559 forbids (Table 25); the cipher mode is not read as part of the scheme.", node);
        }
    }

    // The SeekHead's entries point at the Top-Level Elements they name (RFC 9559 sections 5.1.1 and 6.3).
    private void CheckSeeks(SegmentState segment)
    {
        // A file cut at its start has lost the Segment's header, so no position counted from it can be resolved.
        if (segment.IsHeaderMissing)
        {
            return;
        }

        foreach ((ulong id, long position, Node node) in segment.Seeks)
        {
            if (id is Schema.Void or Schema.Crc32)
            {
                continue;
            }

            long absolute = segment.DataStart + position;
            string name = ElementName(id);
            if (absolute < segment.DataStart || absolute >= segment.DeclaredEnd)
            {
                _findings.Add(Severity.Warning, "matroska.seek-position", node, count =>
                    $"{(count == 1 ? "A Seek entry points" : $"{count} Seek entries point")} outside the Segment (RFC 9559, section 5.1.1.1.2); the first names the {name} at position {position}, which is not in the {Bytes(segment.DeclaredEnd - segment.DataStart)} of the Segment.");
                continue;
            }

            // The file is cut short before the element: its absence is the truncation's, which is reported on its own.
            if (absolute >= segment.End)
            {
                continue;
            }

            if (Ebml.TryReadHeader(_reader, absolute, segment.End, segment.Document!.ReadIdLength, segment.Document.ReadSizeLength, out ElementHeader header) != HeaderProblem.None || header.Id != id)
            {
                _findings.Add(Severity.Warning, "matroska.seek-position", node, count =>
                    $"{(count == 1 ? "A Seek entry does" : $"{count} Seek entries do")} not point at the element {(count == 1 ? "it names" : "they name")} (RFC 9559, section 5.1.1.1.2); the first names the {name} at position {position}, where the Segment has no such element.");
            }
        }
    }

    // A CuePoint's CueClusterPosition is the Segment position of a Cluster (RFC 9559 section 5.1.5.1.2.2).
    private void CheckCues(SegmentState segment)
    {
        if (segment.IsHeaderMissing)
        {
            return;
        }

        foreach ((long position, Node node) in segment.CuePositions)
        {
            if (!segment.ClusterPositions.Contains(position) && !(segment.DataStart + position >= segment.End && segment.DeclaredEnd > segment.End))
            {
                _findings.Add(Severity.Warning, "matroska.cue-position", node, count =>
                    $"{(count == 1 ? "A cue points" : $"{count} cues point")} at a position where no Cluster starts (RFC 9559, section 5.1.5.1.2.2); the first, at 0x{node.Offset:X}, gives {position}.");
            }
        }
    }

    // What the WebM Container Guidelines (https://www.webmproject.org/docs/container/) ask of a WebM file beyond Matroska.
    private void CheckWebM(SegmentState segment)
    {
        if (segment.Document?.IsWebM != true)
        {
            return;
        }

        foreach (TrackInfo track in segment.Tracks)
        {
            if (track.CodecId is { } codec && !Codecs.IsWebM(codec))
            {
                _findings.Add(Severity.Info, "matroska.webm-codec", track.CodecIdNode ?? track.Node, count =>
                    $"{(count == 1 ? "A track's codec is" : $"{count} tracks' codecs are")} none WebM allows (VP8, VP9 or AV1 video, Vorbis or Opus audio, WebVTT; the WebM Container Guidelines); the first is {codec} in track {track.Number?.ToString(CultureInfo.InvariantCulture) ?? "?"}.");
            }
        }

        if (segment.TimestampScale != 1_000_000)
        {
            _findings.AddNow(Severity.Info, "matroska.webm-timestamp-scale",
                $"The TimestampScale is {segment.TimestampScale} ns, where the WebM Container Guidelines set 1,000,000 (a tick of a millisecond).", segment.Node!);
        }
    }
}
