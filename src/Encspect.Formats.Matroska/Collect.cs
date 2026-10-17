using Encspect.Core;

namespace Encspect.Formats.Matroska;

// The value of a leaf element as read, before it is shown: whichever of the fields its type uses.
internal readonly record struct Leaf(ulong U, long S, double D, string? Text, byte[]? Bytes, long Length);

internal sealed partial class FileReader
{
    // How many elements the schema does not place where they stand enclose the one being read.
    private int _displaced;
    private TrackInfo? _track;
    private EncodingInfo? _encoding;
    private ulong? _seekId;
    private long? _seekPosition;

    // A Master Element's node exists and its children are about to be read.
    private void Begin(ElementDefinition definition, Node node)
    {
        switch (definition.Id)
        {
            case 0xAE when _segment is not null:
                _track = new TrackInfo { Node = node };
                _segment.Tracks.Add(_track);
                break;
            case 0xE1 when _track is not null:
                _track.HasAudio = true;
                _track.AudioNode = node;
                break;
            case 0xE0 when _track is not null:
                _track.HasVideo = true;
                break;
            case 0x6240 when _track is not null:
                _encoding = new EncodingInfo { Node = node };
                break;
            case 0x5034 when _encoding is not null:
                _encoding.HasCompression = true;
                break;
            case 0x5035 when _encoding is not null:
                _encoding.HasEncryption = true;
                break;
            case 0x47E7 when _encoding is not null:
                _encoding.HasAesSettings = true;
                break;
            case 0x4DBB:
                _seekId = null;
                _seekPosition = null;
                break;
            case Schema.Info when _segment is not null:
                _segment.InfoCount++;
                _segment.InfoNode ??= node;
                break;
            case Schema.Tracks when _segment is not null:
                _segment.TracksCount++;
                break;
        }
    }

    // A Master Element's children have been read.
    private void End(ElementDefinition definition, Node node)
    {
        switch (definition.Id)
        {
            case 0xAE when _track is not null && _segment is not null:
                FinishTrack(_segment, _track);
                _track = null;
                break;
            case 0x6240 when _encoding is not null && _track is not null:
                _track.Encodings.Add(_encoding);
                _encoding = null;
                break;
            case 0x4DBB when _seekId is { } id && _seekPosition is { } position && _segment is not null:
                _segment.Seeks.Add((id, position, node));
                break;
        }
    }

    // A leaf element has been read and shown.
    private void Collect(ElementDefinition definition, ElementHeader header, Node node, Leaf leaf)
    {
        DocumentInfo document = _document;
        switch (definition.Id)
        {
            case 0x4286:
                document.Version = leaf.U;
                break;
            case 0x42F7:
                document.ReadVersion = leaf.U;
                break;
            case 0x42F2:
                document.MaxIdLength = (int)Math.Min(leaf.U, int.MaxValue);
                break;
            case 0x42F3:
                document.MaxSizeLength = (int)Math.Min(leaf.U, int.MaxValue);
                break;
            case 0x4282:
                document.DocType = leaf.Text;
                break;
            case 0x4287:
                document.DocTypeVersion = leaf.U;
                break;
            case 0x4285:
                document.DocTypeReadVersion = leaf.U;
                break;
        }

        if (_segment is { } segment)
        {
            CollectSegment(segment, definition, header, node, leaf);
        }
    }

    private void CollectSegment(SegmentState segment, ElementDefinition definition, ElementHeader header, Node node, Leaf leaf)
    {
        bool firstInfo = segment.InfoCount <= 1;
        switch (definition.Id)
        {
            case 0x2AD7B1 when firstInfo:
                segment.TimestampScale = leaf.U;
                if (leaf.U == 0)
                {
                    _findings.AddNow(Severity.Warning, "matroska.timestamp-scale",
                        "The TimestampScale is 0, which RFC 9559 forbids (section 5.1.2.9); timestamps are read with the default of 1,000,000 nanoseconds.", node);
                    segment.TimestampScale = 1_000_000;
                }

                break;
            case 0x4489 when firstInfo:
                segment.Duration = leaf.D;
                break;
            case 0x7BA9 when firstInfo:
                segment.Title = leaf.Text;
                break;
            case 0x4D80 when firstInfo:
                segment.MuxingApp = leaf.Text;
                segment.MuxingAppNode = node;
                break;
            case 0x5741 when firstInfo:
                segment.WritingApp = leaf.Text;
                segment.WritingAppNode = node;
                break;
            case 0x73A4 when leaf.Length != 16:
            case 0x3CB923 when leaf.Length != 16:
            case 0x3EB923 when leaf.Length != 16:
            case 0x4444 when leaf.Length != 16:
                _findings.Add(Severity.Warning, "matroska.uid-length", node, count =>
                    $"{(count == 1 ? "A UUID element of the Info holds" : $"{count} UUID elements of the Info hold")} other than the 128 bits RFC 9559 gives it (sections 5.1.2.1 to 5.1.2.7); the first, the {definition.Name} at 0x{header.Offset:X}, holds {Bytes(leaf.Length)}.");
                break;
            case 0x53AB:
                _seekId = leaf.Bytes is { Length: >= 1 and <= 4 } id ? Ebml.Unsigned(id) : null;
                break;
            case 0x53AC:
                _seekPosition = (long)Math.Min(leaf.U, long.MaxValue);
                break;
            case 0xF1:
                segment.CuePositions.Add(((long)Math.Min(leaf.U, long.MaxValue), node));
                break;
        }

        if (_track is { } track)
        {
            CollectTrack(track, definition, header, node, leaf);
        }
    }

    private void CollectTrack(TrackInfo track, ElementDefinition definition, ElementHeader header, Node node, Leaf leaf)
    {
        switch (definition.Id)
        {
            case 0xD7:
                track.Number = leaf.U;
                break;
            case 0x73C5:
                track.Uid = leaf.U;
                break;
            case 0x83:
                track.Type = leaf.U;
                break;
            case 0xB9:
                track.IsEnabled = leaf.U != 0;
                break;
            case 0x9C:
                track.AllowsLacing = leaf.U != 0;
                break;
            case 0x86:
                track.CodecId = leaf.Text;
                track.CodecIdNode = node;
                break;
            case 0x63A2:
                track.CodecPrivateOffset = header.DataOffset;
                track.CodecPrivateLength = leaf.Length;
                track.CodecPrivateNode = node;
                break;
            case 0x56AA:
                track.CodecDelay = leaf.U;
                track.CodecDelayNode = node;
                break;
            case 0x56BB:
                track.SeekPreRoll = leaf.U;
                break;
            case 0x23E383:
                track.DefaultDuration = leaf.U;
                break;
            case 0x23314F:
                track.TimestampScale = leaf.D;
                break;
            case 0x536E:
                track.Name = leaf.Text;
                break;
            case 0x22B59C when track.Language is null:
                track.Language = leaf.Text;
                break;
            case 0x22B59D:
                track.Language = leaf.Text;
                break;
            case 0xB5:
                track.SamplingFrequency = leaf.D;
                track.SamplingFrequencyNode = node;
                break;
            case 0x78B5:
                track.OutputSamplingFrequency = leaf.D;
                track.OutputSamplingFrequencyNode = node;
                break;
            case 0x9F:
                track.Channels = leaf.U;
                track.ChannelsNode = node;
                break;
            case 0x6264:
                track.BitDepth = leaf.U;
                break;
            case 0xB0:
                track.PixelWidth = leaf.U;
                break;
            case 0xBA:
                track.PixelHeight = leaf.U;
                break;
        }

        if (_encoding is { } encoding)
        {
            switch (definition.Id)
            {
                case 0x5031:
                    encoding.Order = leaf.U;
                    break;
                case 0x5032:
                    encoding.Scope = leaf.U;
                    break;
                case 0x5033:
                    encoding.Type = leaf.U;
                    break;
                case 0x4254:
                    encoding.CompressionAlgorithm = leaf.U;
                    break;
                case 0x4255:
                    encoding.SettingsOffset = header.DataOffset;
                    encoding.SettingsLength = leaf.Length;
                    break;
                case 0x47E1:
                    encoding.EncryptionAlgorithm = leaf.U;
                    break;
                case 0x47E8:
                    encoding.CipherMode = leaf.U;
                    break;
            }
        }
    }

    // The TrackEntry's children have been read: its summary, and the number the blocks name it by.
    private void FinishTrack(SegmentState segment, TrackInfo track)
    {
        track.Node.Value = TrackText(track);
        if (track.Number is { } number)
        {
            if (!segment.TracksByNumber.TryAdd(number, track))
            {
                _findings.Add(Severity.Warning, "matroska.track-number-duplicate", track.Node, count =>
                    $"{(count == 1 ? "Two tracks have" : $"{count} tracks repeat")} a TrackNumber another track has, so the blocks that name it cannot be told apart (RFC 9559, section 5.1.4.1.1); the first repeated number is {number}.");
            }
        }
    }

    private static string TrackText(TrackInfo track)
    {
        var parts = new List<string> { Values.TrackTypeName(track.Type ?? 0) ?? (track.Type is { } t ? $"type {t}" : "no type") };
        if (track.CodecId is not null)
        {
            parts.Add(track.CodecId);
        }

        if (track.IsAudio)
        {
            if (track.Channels is { } channels)
            {
                parts.Add(channels == 1 ? "1 channel" : $"{channels} channels");
            }

            if (track.SamplingFrequency is { } rate)
            {
                parts.Add($"{rate.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} Hz");
            }
        }
        else if (track.PixelWidth is { } width && track.PixelHeight is { } height)
        {
            parts.Add($"{width}x{height}");
        }

        return string.Join(", ", parts);
    }
}
