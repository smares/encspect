using System.Buffers.Binary;

namespace Encspect.Formats.TrueHd;

internal enum SyncProblem
{
    None,

    // The span ends before the major sync does.
    TooShort,

    // The stream type is neither 0xBA nor 0xBB.
    StreamType,

    // MLP: group 1 states no bit depth.
    BitDepth,

    // MLP: group 2 is longer than group 1.
    GroupDepths,

    // The sampling rate code gives no rate, or one above 192 kHz.
    Rate,

    // The substream count is 0, or above the format's limit.
    SubstreamCount,
}

// The major sync (docs/design/truehd-format.md, "Major sync"): the stream's parameters and its substream count, at the start of some access
// units. Fields the decoder does not use are kept as they are written.
internal readonly struct MajorSync
{
    public const int MinLength = 28;
    public const int SyncWord = 0xF8726F;

    public bool IsTrueHd { get; init; }

    // The sync's size in bytes, its CRC included.
    public int Length { get; init; }

    public SyncProblem Problem { get; init; }

    public ushort StoredCrc { get; init; }

    public ushort ComputedCrc { get; init; }

    public bool IsCrcValid => StoredCrc == ComputedCrc;

    public bool IsUsable => Problem == SyncProblem.None && IsCrcValid;

    // The 32 bits of format information, which an MP4 dmlp box repeats.
    public uint FormatInfo { get; init; }

    // The sampling rate code (group 1's for MLP), and what it gives.
    public int RateCode { get; init; }

    public int SampleRate => Tables.RateOf(RateCode);

    public int UnitSamples => Tables.UnitSamplesOf(RateCode);

    public int NoiseBuffer => Tables.NoiseBufferOf(RateCode);

    // MLP.
    public int Group1DepthCode { get; init; }

    public int Group2DepthCode { get; init; }

    public int Group2RateCode { get; init; }

    public int MultichannelType { get; init; }

    public int Arrangement { get; init; }

    public int Group1Depth => DepthOf(Group1DepthCode);

    public int Group2Depth => DepthOf(Group2DepthCode);

    public bool HasDifferentGroupRates => Group2RateCode != 0xF && Group2RateCode != RateCode;

    // TrueHD.
    public bool SixChannelType { get; init; }

    public bool EightChannelType { get; init; }

    public int TwoChannelModifier { get; init; }

    public int SixChannelModifier { get; init; }

    public int EightChannelModifier { get; init; }

    public int SixChannelAssignment { get; init; }

    public int EightChannelAssignment { get; init; }

    public int Signature { get; init; }

    public int Flags { get; init; }

    public int Reserved { get; init; }

    public bool IsVariableRate { get; init; }

    // In sixteenths of a bit per sample period.
    public int PeakDataRate { get; init; }

    public int SubstreamCount { get; init; }

    public int ExtendedSubstreamInfo { get; init; }

    public int SubstreamInfo { get; init; }

    public ulong ChannelMeaning { get; init; }

    public bool HasExtension { get; init; }

    // The words the extension holds after its first nibble's word.
    public int ExtensionWords { get; init; }

    // The bits per second the peak data rate states.
    public long PeakBitsPerSecond => SampleRate > 0 ? (((long)PeakDataRate * SampleRate) + 8) / 16 : 0;

    // A stream of four substreams with bit 7 of the substream info: what the stream signals as Dolby Atmos.
    public bool SignalsAtmos => IsTrueHd && SubstreamCount == 4 && (SubstreamInfo & 0x80) != 0;

    public static int DepthOf(int code) => code switch { 0 => 16, 1 => 20, 2 => 24, _ => 0 };

    // The bit depth the stream's samples have: MLP's group 1, TrueHD states none and its output is 24 bits.
    public int BitDepth => IsTrueHd ? 24 : Group1Depth;

    public static bool StartsAt(ReadOnlySpan<byte> data) => data.Length >= 4 && data[0] == 0xF8 && data[1] == 0x72 && data[2] == 0x6F && (data[3] & 0xFE) == 0xBA;

    // Reads the major sync at the start of data. Null when the data is not a major sync (no sync word or stream type) or ends before it does;
    // otherwise the sync with its Problem, and its CRC compared.
    public static MajorSync? Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xF8 || data[1] != 0x72 || data[2] != 0x6F || (data[3] != 0xBA && data[3] != 0xBB))
        {
            return null;
        }

        if (data.Length < MinLength)
        {
            return null;
        }

        bool isTrueHd = data[3] == 0xBA;
        bool hasExtension = isTrueHd && (data[25] & 1) != 0;
        int words = hasExtension ? data[26] >> 4 : 0;
        int length = hasExtension ? 30 + (2 * words) : MinLength;
        if (data.Length < length)
        {
            return null;
        }

        uint info = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        int count = data[16] >> 4;
        SyncProblem problem = SyncProblem.None;
        int rateCode;
        int group1 = 0;
        int group2 = 0;
        int rate2 = 0;
        if (isTrueHd)
        {
            rateCode = data[4] >> 4;
        }
        else
        {
            group1 = data[4] >> 4;
            group2 = data[4] & 0xF;
            rateCode = data[5] >> 4;
            rate2 = data[5] & 0xF;
            if (DepthOf(group1) == 0)
            {
                problem = SyncProblem.BitDepth;
            }
            else if (DepthOf(group2) > DepthOf(group1))
            {
                problem = SyncProblem.GroupDepths;
            }
        }

        if (problem == SyncProblem.None && Tables.RateOf(rateCode) == 0)
        {
            problem = SyncProblem.Rate;
        }

        if (problem == SyncProblem.None && (count == 0 || (!isTrueHd && count > 2)))
        {
            problem = SyncProblem.SubstreamCount;
        }

        ReadOnlySpan<byte> sync = data[..length];
        return new MajorSync
        {
            IsTrueHd = isTrueHd,
            Length = length,
            Problem = problem,
            StoredCrc = BinaryPrimitives.ReadUInt16BigEndian(sync[^2..]),
            ComputedCrc = Checks.MajorSync(sync),
            FormatInfo = info,
            RateCode = rateCode,
            Group1DepthCode = group1,
            Group2DepthCode = group2,
            Group2RateCode = rate2,
            MultichannelType = isTrueHd ? 0 : data[6] & 0xF,
            Arrangement = isTrueHd ? 0 : data[7] & 0x1F,
            SixChannelType = isTrueHd && (data[4] & 0x08) != 0,
            EightChannelType = isTrueHd && (data[4] & 0x04) != 0,
            TwoChannelModifier = isTrueHd ? data[5] >> 6 : 0,
            SixChannelModifier = isTrueHd ? (data[5] >> 4) & 3 : 0,
            EightChannelModifier = isTrueHd ? (data[6] >> 5) & 3 : 0,
            SixChannelAssignment = isTrueHd ? ((data[5] & 0xF) << 1) | (data[6] >> 7) : 0,
            EightChannelAssignment = isTrueHd ? ((data[6] & 0x1F) << 8) | data[7] : 0,
            Signature = BinaryPrimitives.ReadUInt16BigEndian(data[8..]),
            Flags = BinaryPrimitives.ReadUInt16BigEndian(data[10..]),
            Reserved = BinaryPrimitives.ReadUInt16BigEndian(data[12..]),
            IsVariableRate = (data[14] & 0x80) != 0,
            PeakDataRate = BinaryPrimitives.ReadUInt16BigEndian(data[14..]) & 0x7FFF,
            SubstreamCount = count,
            ExtendedSubstreamInfo = data[16] & 3,
            SubstreamInfo = data[17],
            ChannelMeaning = BinaryPrimitives.ReadUInt64BigEndian(data[18..]),
            HasExtension = hasExtension,
            ExtensionWords = words,
        };
    }

    // The parameters a change in which the decoder cannot continue its output: the rate, the format and the substream count. Both are usable syncs.
    public bool SameStreamAs(in MajorSync other) =>
        IsTrueHd == other.IsTrueHd && RateCode == other.RateCode && SubstreamCount == other.SubstreamCount && Arrangement == other.Arrangement
        && SixChannelAssignment == other.SixChannelAssignment && EightChannelAssignment == other.EightChannelAssignment && Group1DepthCode == other.Group1DepthCode;
}
