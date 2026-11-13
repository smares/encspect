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

    private long _position;
    private long _crc1Stored;
    private long _crc1Checked;
    private long _crc1Failed;
    private long _crc2Stored;
    private long _crc2Checked;
    private long _crc2Failed;

    public DecodeFeed(AnalysisContext context, Node stream, SyncFrame first, ReadOnlySpan<byte> firstBytes, long offset)
    {
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

    // Set where the data between two frames is no frame (a gap, which the next frame follows).
    public bool PendingGap { get; set; }

    // The gaps of the analysis this program has seen, so that each independent substream of a file notes a gap, not only the first frame after it.
    public int GapsSeen { get; set; }

    // Whether any frame of the program lay between a gap: the CRC2 of the frames then does not vouch for all of the program's audio.
    public bool HadGap { get; private set; }

    public static int ChannelCount(SyncFrame header) => Acmod[header.Acmod] + (header.Lfeon ? 1 : 0);

    private static readonly int[] Acmod = [2, 1, 2, 3, 3, 4, 4, 5];

    // One independent frame of the program: its bytes as far as the data holds them, the walk's records in written, and how it ended.
    public void Feed(IntWriter written, long offset, ReadOnlyMemory<byte> frame, SyncFrame header, bool isReadable, Ac3Analysis.CrcState crc1, Ac3Analysis.CrcState crc2)
    {
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

        if (!isReadable)
        {
            Session.Decode(new CodedFrame(new MappedBytes(ReadOnlyMemory<byte>.Empty, offset), "Frame", first, samples) { IsUnreadable = true });
            return;
        }

        SampleBuffers lent = Session.Lend(written.Count, 1);
        bool isWalked = !lent.IsEmpty;
        if (isWalked)
        {
            written.Written.CopyTo(lent.Narrow);
        }

        Session.Decode(new CodedFrame(new MappedBytes(frame, offset), "Frame", first, samples)
        {
            IsWalked = isWalked,
            IsDamaged = crc1 == Ac3Analysis.CrcState.Mismatch || crc2 == Ac3Analysis.CrcState.Mismatch,
            FollowsGap = followsGap,
        });
    }

    // Ends the session with the checksums the program stores. crc2 covers the whole frame, so with every frame there and none lost it vouches for all of the program's audio.
    public StreamVerification Finish(long lostSamples)
    {
        bool coversAll = lostSamples == 0 && !HadGap;
        return Session.Finish(
        [
            new ChecksumTally { Name = Crc1, Covers = ChecksumCoverage.CodedData, Stored = _crc1Stored, Checked = _crc1Checked, Failed = _crc1Failed },
            new ChecksumTally { Name = Crc2, Covers = ChecksumCoverage.CodedData, CoversAllAudio = coversAll, Stored = _crc2Stored, Checked = _crc2Checked, Failed = _crc2Failed },
        ]);
    }
}
