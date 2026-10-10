using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Mpeg;

// Layer II audio data (ISO/IEC 11172-3 2.4.1.6 and 2.4.2.6): a bit allocation per subband and
// channel, scfsi and scalefactors for every allocated subband, then twelve granules of samples.
// Above the joint stereo bound one allocation, and one set of samples, serves both channels.
// Whatever follows the samples is ancillary data.
internal sealed class Layer2AudioData
{
    // Quantization levels per allocation index for each group of subbands; 0 is no allocation.
    private static readonly int[] A0 = [0, 3, 7, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 16383, 32767, 65535];
    private static readonly int[] A3 = [0, 3, 5, 7, 9, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 65535];
    private static readonly int[] A11 = [0, 3, 5, 7, 9, 15, 31, 65535];
    private static readonly int[] A23 = [0, 3, 5, 65535];
    private static readonly int[] C0 = [0, 3, 5, 9, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 16383, 32767];
    private static readonly int[] C2 = [0, 3, 5, 9, 15, 31, 63, 127];
    private static readonly int[] L0 = [0, 3, 5, 7, 9, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 16383];
    private static readonly int[] L4 = [0, 3, 5, 9, 15, 31, 63, 127];
    private static readonly int[] L11 = [0, 3, 5, 9];

    // ISO/IEC 11172-3 Table B.2a to B.2d, and ISO/IEC 13818-3 Table B.1 for the lower sample rates.
    private static readonly int[][][] Tables =
    [
        [.. Repeat(A0, 3), .. Repeat(A3, 8), .. Repeat(A11, 12), .. Repeat(A23, 4)],
        [.. Repeat(A0, 3), .. Repeat(A3, 8), .. Repeat(A11, 12), .. Repeat(A23, 7)],
        [.. Repeat(C0, 2), .. Repeat(C2, 6)],
        [.. Repeat(C0, 2), .. Repeat(C2, 10)],
        [.. Repeat(L0, 4), .. Repeat(L4, 7), .. Repeat(L11, 19)],
    ];

    private static readonly string[] TableNames = ["B.2a", "B.2b", "B.2c", "B.2d", "B.1"];

    private static readonly int[] ScalefactorCounts = [3, 2, 1, 2];

    private Layer2AudioData(int table, int channels, int bound)
    {
        TableIndex = table;
        Channels = channels;
        Bound = bound;
        Allocation = new int[channels, Subbands];
        Scfsi = new int[channels, Subbands];
        Scalefactors = new int[channels, Subbands][];
        AllocationBit = new int[channels, Subbands];
        ScfsiBit = new int[channels, Subbands];
        ScalefactorBit = new int[channels, Subbands];
    }

    public int TableIndex { get; }

    public int Channels { get; }

    public int Subbands => Tables[TableIndex].Length;

    public int Bound { get; }

    // [channel, subband]: allocation index, scfsi (-1 without allocation) and scalefactor indexes.
    public int[,] Allocation { get; }

    public int[,] Scfsi { get; }

    public int[,][] Scalefactors { get; }

    // Where each field starts, in bits from the start of the frame.
    public int[,] AllocationBit { get; }

    public int[,] ScfsiBit { get; }

    public int[,] ScalefactorBit { get; }

    public int AllocationStart { get; private set; }

    public int ScfsiStart { get; private set; }

    public int ScalefactorStart { get; private set; }

    public int SampleStart { get; private set; }

    public int End { get; private set; }

    public static int[] Levels(int table, int subband) => Tables[table][subband];

    public static int SubbandCount(int table) => Tables[table].Length;

    // ISO/IEC 11172-3 2.4.3.3.1: the table follows from the bitrate per channel and the sample rate.
    public static int TableFor(MpegHeader header, int frameLength)
    {
        if (!header.IsMpeg1)
        {
            return 4;
        }

        int bitrate = header.IsFreeFormat ? (int)((long)frameLength * header.SampleRate / 144 / 1000) : header.Bitrate;
        int perChannel = bitrate / (header.IsMono ? 1 : 2);
        if ((header.SampleRate == 48000 && perChannel >= 56) || (perChannel >= 56 && perChannel <= 80))
        {
            return 0;
        }

        if (header.SampleRate != 48000 && perChannel >= 96)
        {
            return 1;
        }

        return header.SampleRate != 32000 && perChannel <= 48 ? 2 : 3;
    }

    // Bits of three samples: one codeword for 3, 5 or 9 levels, three otherwise.
    public static int SampleBits(int levels) => levels switch
    {
        3 => 5,
        5 => 7,
        9 => 10,
        _ => 3 * (32 - int.LeadingZeroCount(levels)),
    };

    // The bits the allocation field of a subband takes.
    public static int AllocationWidth(int[] levels) => levels.Length switch
    {
        16 => 4,
        8 => 3,
        _ => 2,
    };

    // data starts at the frame header and must reach at least to the end of the scalefactors.
    public static Layer2AudioData Read(byte[] data, MpegHeader header, int frameLength)
    {
        int table = TableFor(header, frameLength);
        int channels = header.IsMono ? 1 : 2;
        int subbands = Tables[table].Length;
        int bound = header.ChannelModeBits == 1 ? Math.Min((header.ModeExtension + 1) * 4, subbands) : subbands;
        var audio = new Layer2AudioData(table, channels, bound);
        var bits = new BitReader(data);
        bits.Read(16);
        bits.Read(16);
        if (header.IsProtected)
        {
            bits.Read(16);
        }

        audio.AllocationStart = bits.Position;
        for (int sb = 0; sb < subbands; sb++)
        {
            int width = AllocationWidth(Tables[table][sb]);
            for (int ch = 0; ch < channels; ch++)
            {
                if (sb >= bound && ch > 0)
                {
                    audio.Allocation[ch, sb] = audio.Allocation[0, sb];
                    audio.AllocationBit[ch, sb] = audio.AllocationBit[0, sb];
                    continue;
                }

                audio.AllocationBit[ch, sb] = bits.Position;
                audio.Allocation[ch, sb] = bits.Read(width);
            }
        }

        audio.ScfsiStart = bits.Position;
        for (int sb = 0; sb < subbands; sb++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                audio.ScfsiBit[ch, sb] = bits.Position;
                audio.Scfsi[ch, sb] = audio.Allocation[ch, sb] != 0 ? bits.Read(2) : -1;
            }
        }

        audio.ScalefactorStart = bits.Position;
        for (int sb = 0; sb < subbands; sb++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                audio.ScalefactorBit[ch, sb] = bits.Position;
                int scfsi = audio.Scfsi[ch, sb];
                audio.Scalefactors[ch, sb] = scfsi < 0 ? [] : Enumerable.Range(0, ScalefactorCounts[scfsi]).Select(_ => bits.Read(6)).ToArray();
            }
        }

        audio.SampleStart = bits.Position;
        int granule = 0;
        for (int sb = 0; sb < subbands; sb++)
        {
            for (int ch = 0; ch < (sb < bound ? channels : 1); ch++)
            {
                int levels = Tables[table][sb][audio.Allocation[ch, sb]];
                granule += levels == 0 ? 0 : SampleBits(levels);
            }
        }

        audio.End = audio.SampleStart + (12 * granule);
        return audio;
    }

    public IEnumerable<(int Channel, int Subband, int Index)> UndefinedScalefactors()
    {
        for (int sb = 0; sb < Subbands; sb++)
        {
            for (int ch = 0; ch < Channels; ch++)
            {
                foreach (int index in Scalefactors[ch, sb].Where(i => i == 63))
                {
                    yield return (ch, sb, index);
                }
            }
        }
    }

    // A truncated or overflowing frame cuts the samples off at its last byte.
    public IEnumerable<Node> ToNodes(long frameOffset, long frameLength)
    {
        int end = (int)Math.Min(End, frameLength * 8);
        Node allocation = Span("Bit Allocation", frameOffset, AllocationStart, ScfsiStart, $"Table {TableNames[TableIndex]}");
        Node scfsi = Span("SCFSI", frameOffset, ScfsiStart, ScalefactorStart);
        Node scalefactors = Span("Scalefactors", frameOffset, ScalefactorStart, SampleStart);
        for (int sb = 0; sb < Subbands; sb++)
        {
            int width = AllocationWidth(Tables[TableIndex][sb]);
            int coded = sb < Bound ? Channels : 1;
            Node subband = allocation.Add(Span($"Subband {sb}", frameOffset, AllocationBit[0, sb], AllocationBit[coded - 1, sb] + width));
            for (int ch = 0; ch < coded; ch++)
            {
                string name = coded < Channels ? "Channels 0 and 1" : $"Channel {ch}";
                int levels = Tables[TableIndex][sb][Allocation[ch, sb]];
                subband.Add(Span(name, frameOffset, AllocationBit[ch, sb], AllocationBit[ch, sb] + width, levels == 0 ? "None" : $"{levels} levels"));
            }

            int[] allocated = Enumerable.Range(0, Channels).Where(ch => Allocation[ch, sb] != 0).ToArray();
            if (allocated.Length == 0)
            {
                continue;
            }

            Node scfsiSubband = scfsi.Add(Span($"Subband {sb}", frameOffset, ScfsiBit[allocated[0], sb], ScfsiBit[allocated[^1], sb] + 2));
            Node scalefactorSubband = scalefactors.Add(Span($"Subband {sb}", frameOffset, ScalefactorBit[allocated[0], sb],
                ScalefactorBit[allocated[^1], sb] + (6 * Scalefactors[allocated[^1], sb].Length)));
            foreach (int ch in allocated)
            {
                scfsiSubband.Add(Span($"Channel {ch}", frameOffset, ScfsiBit[ch, sb], ScfsiBit[ch, sb] + 2, Number(Scfsi[ch, sb])));
                scalefactorSubband.Add(Span($"Channel {ch}", frameOffset, ScalefactorBit[ch, sb], ScalefactorBit[ch, sb] + (6 * Scalefactors[ch, sb].Length),
                    string.Join(", ", Scalefactors[ch, sb].Select(Number))));
            }
        }

        yield return allocation;
        if (scfsi.Children.Count > 0)
        {
            yield return scfsi;
            yield return scalefactors;
        }

        if (end > SampleStart)
        {
            yield return Span("Samples", frameOffset, SampleStart, end, $"{end - SampleStart} bits");
        }
    }

    // A node covering the bytes that hold bits [startBit, endBit) of the frame.
    public static Node Span(string name, long frameOffset, int startBit, int endBit, string? value = null) =>
        new(name, frameOffset + (startBit / 8), Math.Max(((endBit - 1) / 8) - (startBit / 8) + 1, 0), value);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static IEnumerable<int[]> Repeat(int[] levels, int count) => Enumerable.Repeat(levels, count);
}
