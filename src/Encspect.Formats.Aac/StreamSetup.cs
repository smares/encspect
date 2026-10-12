namespace Encspect.Formats.Aac;

// What reading a stream's raw data blocks needs from its configuration: the object type, the
// scalefactor band tables of its sample rate and frame length, the channel elements its channel
// configuration defines, and the rate SBR runs at. Shared by every reader of the stream's blocks,
// so it is never changed.
internal sealed class StreamSetup
{
    public StreamSetup(AacConfig config)
    {
        ObjectType = config.ObjectType;
        SampleRate = config.CoreSampleRate;
        SampleIndex = SampleRateIndex(config.CoreSampleRate);
        Is960 = config.CoreFrameLength == 960;
        LongOffsets = Is960 ? BandTables.Long960[SampleIndex] : BandTables.Long[SampleIndex];
        ShortOffsets = Is960 ? BandTables.Short120[SampleIndex] : BandTables.Short[SampleIndex];
        LongBands = Is960 ? BandTables.NumLong960[SampleIndex] : BandTables.NumLong[SampleIndex];
        ShortBands = Is960 ? BandTables.NumShort120[SampleIndex] : BandTables.NumShort[SampleIndex];
        LongLines = Is960 ? 960 : 1024;
        PredictionBands = BandTables.PredictionMax[SampleIndex];
        ChannelConfiguration = config.ChannelConfiguration;
        SbrSampleRate = config.Sbr == true && config.ExtensionSampleRate is { } rate ? rate : 2 * config.CoreSampleRate;
        IsSbrSignaledAbsent = config.Sbr == false;
        (ExpectedSce, ExpectedCpe, ExpectedLfe) = config.ChannelConfiguration switch
        {
            1 => (1, 0, 0),
            2 => (0, 1, 0),
            3 => (1, 1, 0),
            4 => (2, 1, 0),
            5 => (1, 2, 0),
            6 => (1, 2, 1),
            7 or 12 or 14 => (1, 3, 1),
            11 => (2, 2, 1),
            _ => (-1, -1, -1),
        };
    }

    // Raw data blocks of these object types are read element by element: AAC Main, LC, SSR and LTP
    // (ISO/IEC 14496-3 4.4.2.1). The scalable and TwinVQ types code their frames otherwise.
    public static bool CanRead(AacConfig config) => config.ObjectType is 1 or 2 or 3 or 4 && config.CoreSampleRate > 0;

    public int ObjectType { get; }

    public int SampleRate { get; }

    public int SampleIndex { get; }

    public bool Is960 { get; }

    public ushort[] LongOffsets { get; }

    public ushort[] ShortOffsets { get; }

    public int LongBands { get; }

    public int ShortBands { get; }

    public int LongLines { get; }

    public int PredictionBands { get; }

    public int ChannelConfiguration { get; }

    public int SbrSampleRate { get; }

    public bool IsSbrSignaledAbsent { get; }

    // The single channel, channel pair and LFE elements the channel configuration defines, or -1
    // where it leaves them to a program config element (ISO/IEC 14496-3 1.6.3.5, Table 1.19).
    public int ExpectedSce { get; }

    public int ExpectedCpe { get; }

    public int ExpectedLfe { get; }

    // The sampling frequency index whose band tables a rate uses: for a rate stated explicitly, the
    // nearest standard rate's, by the ranges ffmpeg and faad2 both use.
    public static int SampleRateIndex(int rate) => rate switch
    {
        >= 92017 => 0,
        >= 75132 => 1,
        >= 55426 => 2,
        >= 46009 => 3,
        >= 37566 => 4,
        >= 27713 => 5,
        >= 23004 => 6,
        >= 18783 => 7,
        >= 13856 => 8,
        >= 11502 => 9,
        >= 9391 => 10,
        _ => 11,
    };

    // Where line k of a long window lies, in Hz: lines are spaced by half the sample rate over the
    // lines per window.
    public double LineFrequency(int line) => line * (double)SampleRate / (2.0 * LongLines);
}
