using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Ac3;

// The first eight bytes of a syncframe, which hold everything sync and the frame's length need:
// for AC-3 the syncinfo (A/52 5.3.1) and the start of the bit stream information up to acmod and,
// where the bits fit, lfeon (5.3.2); for E-AC-3 (Annex E 2.2.2) strmtyp, substreamid, frmsiz, fscod,
// fscod2 or numblkscod, acmod, lfeon and bsid. bsid sits five bytes into the frame in both, so it
// tells the syntaxes apart: up to 8 AC-3, 9 and 10 AC-3 at half and quarter rate as ffmpeg and
// liba52 read them, which A/52 does not define, 11 to 16 E-AC-3.
internal readonly struct SyncFrame(ulong raw)
{
    public const int Length = 8;

    // Annex E 2.3.1.3: frame sizes at the low end of frmsiz's range cannot hold a complete
    // syncframe. The fields every E-AC-3 frame has take 84 bits at the least: syncinfo and BSI 54
    // (Table E1.2, every option off), audfrm 12 (Table E1.3: eight flags, snroffststr and one
    // channel's exponent strategy) and the end 18 (auxdatae, encinfo and the CRC), so a frame
    // needs 6 words. Shorter ones are no sync; each frame kept costs about a
    // hundred bytes whatever its size, so they would also multiply the memory a file takes.
    public const int MinimumEac3Length = 12;
    public const int SyncWord = 0x0B77;

    public ulong Raw => raw;

    public int Sync => (int)(raw >> 48);

    public int Bsid => (int)(raw >> 19) & 0x1F;

    public bool IsEac3 => Bsid > 10;

    // AC-3 (5.4.1.3, 5.4.1.4).
    public int Crc1 => (int)(raw >> 32) & 0xFFFF;

    public int Fscod => (int)(raw >> 30) & 3;

    public int FrameSizeCode => (int)(raw >> 24) & 0x3F;

    // E-AC-3 (Annex E 2.3.1.1 to 2.3.1.5).
    public int StreamType => (int)(raw >> 46) & 3;

    public int SubstreamId => (int)(raw >> 43) & 7;

    public int FrameSizeWords => IsEac3 ? (int)((raw >> 32) & 0x7FF) + 1 : FrameSizeCode < 38 ? WordsOf(Fscod, FrameSizeCode) : 0;

    public int Fscod2 => (int)(raw >> 28) & 3;

    public int NumberOfBlocksCode => Fscod == 3 ? 3 : (int)(raw >> 28) & 3;

    public int Blocks => !IsEac3 ? 6 : NumberOfBlocksCode switch { 0 => 1, 1 => 2, 2 => 3, _ => 6 };

    public int Samples => Blocks * 256;

    public int Acmod => IsEac3 ? (int)(raw >> 25) & 7 : (int)(raw >> 13) & 7;

    public bool Lfeon
    {
        get
        {
            if (IsEac3)
            {
                return ((raw >> 24) & 1) != 0;
            }

            // After acmod at bit 51 of the frame: cmixlev, surmixlev and dsurmod where acmod has them.
            int acmod = Acmod;
            int bit = 51;
            bit += (acmod & 1) != 0 && acmod != 1 ? 2 : 0;
            bit += (acmod & 4) != 0 ? 2 : 0;
            bit += acmod == 2 ? 2 : 0;
            return ((raw >> (63 - bit)) & 1) != 0;
        }
    }

    public int Bsmod => IsEac3 ? 0 : (int)(raw >> 16) & 7;

    // Independent substreams (strmtyp 0 and 2) and AC-3 frames start a program; dependent ones
    // (strmtyp 1) extend the program before them.
    public bool IsDependent => IsEac3 && StreamType == 1;

    public int FrameLength => FrameSizeWords * 2;

    public int SampleRate
    {
        get
        {
            int fscod = Fscod;
            if (IsEac3)
            {
                return fscod < 3 ? Rates[fscod] : Fscod2 < 3 ? ReducedRates[Fscod2] : 0;
            }

            return fscod < 3 ? Rates[fscod] >> HalfRateShift : 0;
        }
    }

    // bsid 9 and 10 (ffmpeg's ac3_parser.c and liba52's a52_syncinfo read them as half and quarter
    // rate).
    public int HalfRateShift => IsEac3 ? 0 : Math.Max(0, Bsid - 8);

    // Nominal bitrate in kbps for AC-3 (Table 5.18), divided as the sample rate is for bsid 9 and 10.
    public int NominalBitrate => !IsEac3 && FrameSizeCode < 38 ? Tables.NominalBitrates[FrameSizeCode >> 1] >> HalfRateShift : 0;

    public bool IsValid
    {
        get
        {
            if (Sync != SyncWord)
            {
                return false;
            }

            int bsid = Bsid;
            if (bsid <= 10)
            {
                return Fscod != 3 && FrameSizeCode < 38;
            }

            return bsid <= 16 && StreamType != 3 && (Fscod != 3 || Fscod2 != 3) && FrameLength >= MinimumEac3Length;
        }
    }

    public static readonly int[] Rates = [48000, 44100, 32000];

    public static readonly int[] ReducedRates = [24000, 22050, 16000];

    public static SyncFrame Read(ReadOnlySpan<byte> bytes) => new(BinaryPrimitives.ReadUInt64BigEndian(bytes));

    public static int WordsOf(int fscod, int frameSizeCode) => fscod switch
    {
        0 => Tables.Words48[frameSizeCode],
        1 => Tables.Words44[frameSizeCode],
        _ => Tables.Words32[frameSizeCode],
    };

    // Frames of one bit stream share the sample rate: an E-AC-3 stream's substreams, and an AC-3
    // core with its E-AC-3 dependent substreams (Annex E 2.3.1.2).
    public bool Continues(SyncFrame previous) => IsValid && previous.IsValid && SampleRate == previous.SampleRate;

    public string Codec => IsEac3 ? "E-AC-3" : "AC-3";

    // A frame's line in the tree, such as "AC-3, 448 kbps, 3/2 + LFE".
    public string Summary(int length)
    {
        string mode = Ac3Names.CodingModeShort(Acmod) + (Lfeon ? " + LFE" : string.Empty);
        if (!IsEac3)
        {
            return $"{Codec}, {NominalBitrate} kbps, {mode}";
        }

        string type = StreamType switch { 0 => "independent", 1 => "dependent", 2 => "converted from AC-3", _ => "reserved type" };
        return $"{Codec}, {type} substream {SubstreamId}, {Counts.Of(length, "byte", "bytes")}, {mode}";
    }

    public NodeValue SummaryValue(int length) => Summary(length);
}
