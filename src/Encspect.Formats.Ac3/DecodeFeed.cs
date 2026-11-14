using Encspect.Core;

namespace Encspect.Formats.Ac3;

// The feeding of the AC-3 decoder (docs/design/lossy-decoders.md, "The parser and decoder split"): one session for each AC-3 or E-AC-3 program (a run of syncframes of one independent substream with one
// configuration), which gets the program's independent frames in order. The syntax walk of FrameSyntax fills the buffers the session lends, and the decoder reads no coded
// bit; the feed also keeps the tallies of the two CRCs, which the stream declares as its checksums (decision 2 of the design: crc1 and crc2 cover coded data, so a stream
// whose frames all pass them reads "verified (lossy stream)").
internal sealed class DecodeFeed
{
    internal const string Crc1 = "CRC1";
    internal const string Crc2 = "CRC2";
    internal const string Crc = "CRC";

    private readonly bool _isEac3;
    private long _position;
    private long _crc1Stored;
    private long _crc1Checked;
    private long _crc1Failed;
    private long _crc2Stored;
    private long _crc2Checked;
    private long _crc2Failed;

    public DecodeFeed(AnalysisContext context, Node stream, SyncFrame first, ReadOnlySpan<byte> firstBytes, long offset)
    {
        _isEac3 = first.IsEac3;
        Session = context.StartDecoding(first.IsEac3 ? Ac3CodecParser.Eac3 : Ac3CodecParser.Ac3, new DecodeSetup
        {
            Stream = stream,
            Channels = ChannelCount(first),
            BitsPerSample = 32,
            Configuration = new MappedBytes(firstBytes[..SyncFrame.Length].ToArray(), offset),
            IsLossy = true,
        });
    }

    public IDecodeSession Session { get; }

    public bool IsEac3 => _isEac3;

    // Set where the data between two frames is no frame (a gap, which the next frame follows).
    public bool PendingGap { get; set; }

    // The gaps of the analysis this program has seen, so that each independent substream of a file notes a gap, not only the first frame after it.
    public int GapsSeen { get; set; }

    // Whether any frame of the program lay between a gap: the CRC2 of the frames then does not vouch for all of the program's audio.
    public bool HadGap { get; private set; }

    // An AC-3 core with E-AC-3 dependent substreams: the program's frames go to the decoder as units (the core and its dependents), as an E-AC-3 program's do.
    public bool HasDependents { get; set; }

    private bool UsesUnits => _isEac3 || HasDependents;

    public static int ChannelCount(SyncFrame header) => Acmod[header.Acmod] + (header.Lfeon ? 1 : 0);

    public static int ChannelOf(int acmod) => Acmod[acmod];

    private static readonly int[] Acmod = [2, 1, 2, 3, 3, 4, 4, 5];

    // One independent frame of the program: its bytes as far as the data holds them, the walk's records in written, and how it ended. An E-AC-3 program frame is the independent
    // frame and the dependent frames after it, so its unit is handed to the session when the next independent frame comes, or at the end (FlushUnit).
    public void Feed(int[] written, long offset, ReadOnlyMemory<byte> frame, SyncFrame header, bool isReadable, Ac3Analysis.CrcState crc1, Ac3Analysis.CrcState crc2)
    {
        FlushUnit();
        long first = _position;
        int samples = header.Samples;
        _position += samples;
        _crc1Stored++;
        _crc2Stored++;
        if (crc1 != Ac3Analysis.CrcState.NotChecked)
        {
            _crc1Checked++;
            _crc1Failed += crc1 == Ac3Analysis.CrcState.Mismatch ? 1 : 0;
        }

        if (crc2 != Ac3Analysis.CrcState.NotChecked)
        {
            _crc2Checked++;
            _crc2Failed += crc2 == Ac3Analysis.CrcState.Mismatch ? 1 : 0;
        }

        bool followsGap = PendingGap;
        PendingGap = false;
        HadGap |= followsGap;
        if (!Session.IsDecoding)
        {
            return;
        }

        bool damaged = crc1 == Ac3Analysis.CrcState.Mismatch || crc2 == Ac3Analysis.CrcState.Mismatch;
        if (UsesUnits)
        {
            _unit = new Unit(offset, first, samples, followsGap, damaged, isReadable, frame.ToArray());
            _unit.Substreams.Add(new Substream(_isEac3 ? 0 : 0x100, Coded(Program.TokensOf(header, new FrameResult()), written), written));
            return;
        }

        Send(offset, first, samples, followsGap, damaged, isReadable, frame, written);
    }

    // A dependent frame after the independent frame of the unit: its records, and its bytes for the budget. A frame that is not read to its end makes the whole program frame unreadable.
    public void AddDependent(int[] written, SyncFrame header, FrameResult result, ReadOnlyMemory<byte> frame, bool isReadable, Ac3Analysis.CrcState crc2)
    {
        // The dependent frame's one CRC (Annex E 2.2.6) is part of the program's tally, an AC-3 core's crc2 tally included.
        _crc2Stored++;
        if (crc2 != Ac3Analysis.CrcState.NotChecked)
        {
            _crc2Checked++;
            _crc2Failed += crc2 == Ac3Analysis.CrcState.Mismatch ? 1 : 0;
        }

        if (!UsesUnits || _unit is not { } unit)
        {
            return;
        }

        if (unit.Substreams.Any(s => s.Id == 1 + header.SubstreamId))
        {
            // A repeated substream ID is left out of the figures and of the decoding (Annex E 2.3.1.2 gives each ID one frame in a program frame).
            return;
        }

        unit.Bytes = [.. unit.Bytes, .. frame.ToArray()];
        // A dependent frame that is lost or damaged leaves channels of the program frame without audio: the whole program frame is then lost, not output with silent channels.
        unit.IsDamaged |= crc2 == Ac3Analysis.CrcState.Mismatch;
        if (!isReadable)
        {
            unit.IsReadable = false;
        }
        else
        {
            unit.Substreams.Add(new Substream(1 + header.SubstreamId, Coded(Program.TokensOf(header, result), written), written));
        }
    }

    private Unit? _unit;
    private List<Program.Token>? _layout;

    private sealed class Unit(long offset, long first, int samples, bool followsGap, bool isDamaged, bool isReadable, byte[] bytes)
    {
        public long Offset { get; } = offset;
        public long First { get; } = first;
        public int Samples { get; } = samples;
        public bool FollowsGap { get; } = followsGap;
        public bool IsDamaged { get; set; } = isDamaged;
        public bool IsReadable { get; set; } = isReadable;
        public byte[] Bytes { get; set; } = bytes;
        public List<Substream> Substreams { get; } = [];
    }

    // The locations of the channels a frame codes: a channel map that names more locations than the frame has channels (ac-3.channel-map-count) gives the coded channels their
    // locations and leaves the others out.
    private static List<Program.Token> Coded(List<Program.Token> tokens, int[] records) =>
        records.Length >= 2 ? [.. tokens.Take(ChannelOf(records[0]) + (records[1] != 0 ? 1 : 0))] : tokens;

    private sealed record Substream(int Id, List<Program.Token> Tokens, int[] Records);

    // Hands the E-AC-3 program frame in hand to the session: the program's layout (that of the first frame, as the report gives it), then each substream's id, the output channel of each of
    // its coded channels (-1 where the layout has none) and its records.
    public void FlushUnit()
    {
        if (_unit is not { } unit)
        {
            return;
        }

        _unit = null;
        if (!unit.IsReadable)
        {
            Send(unit.Offset, unit.First, unit.Samples, unit.FollowsGap, unit.IsDamaged, false, unit.Bytes, []);
            return;
        }

        _layout ??= Program.Merge(unit.Substreams[0].Tokens, unit.Substreams.Skip(1).SelectMany(s => s.Tokens));
        var ints = new List<int>();
        if (!_isEac3)
        {
            ints.Add(-1);
        }

        ints.Add(_layout.Count);
        ints.AddRange(_layout.Select(t => (int)t));
        ints.Add(unit.Substreams.Count);
        foreach (Substream sub in unit.Substreams)
        {
            ints.Add(sub.Id);
            ints.Add(sub.Tokens.Count);
            ints.AddRange(sub.Tokens.Select(t => _layout.IndexOf(t)));
            ints.AddRange(sub.Records);
        }

        Send(unit.Offset, unit.First, unit.Samples, unit.FollowsGap, unit.IsDamaged, true, unit.Bytes, ints.ToArray());
    }

    private void Send(long offset, long first, int samples, bool followsGap, bool isDamaged, bool isReadable, ReadOnlyMemory<byte> frame, ReadOnlySpan<int> written)
    {
        if (!Session.IsDecoding)
        {
            return;
        }

        if (!isReadable)
        {
            Session.Decode(new CodedFrame(new MappedBytes(ReadOnlyMemory<byte>.Empty, offset), "Frame", first, samples) { IsUnreadable = true });
            return;
        }

        SampleBuffers lent = Session.Lend(written.Length, 1);
        bool isWalked = !lent.IsEmpty;
        if (isWalked)
        {
            written.CopyTo(lent.Narrow);
        }

        Session.Decode(new CodedFrame(new MappedBytes(frame, offset), "Frame", first, samples)
        {
            IsWalked = isWalked,
            IsDamaged = isDamaged,
            FollowsGap = followsGap,
        });
    }

    // Ends the session with the checksums the program stores. crc2 covers the whole frame, so with every frame there and none lost it vouches for all of the program's audio.
    public StreamVerification Finish(long lostSamples)
    {
        FlushUnit();
        bool coversAll = lostSamples == 0 && !HadGap;
        if (_isEac3)
        {
            // E-AC-3 has the one CRC, over the frame (Annex E 2.2.6), whose tally is kept as crc2's.
            return Session.Finish(
            [
                new ChecksumTally { Name = Crc, Covers = ChecksumCoverage.CodedData, CoversAllAudio = coversAll, Stored = _crc2Stored, Checked = _crc2Checked, Failed = _crc2Failed },
            ]);
        }

        return Session.Finish(
        [
            new ChecksumTally { Name = Crc1, Covers = ChecksumCoverage.CodedData, Stored = _crc1Stored, Checked = _crc1Checked, Failed = _crc1Failed },
            new ChecksumTally { Name = Crc2, Covers = ChecksumCoverage.CodedData, CoversAllAudio = coversAll, Stored = _crc2Stored, Checked = _crc2Checked, Failed = _crc2Failed },
        ]);
    }
}
