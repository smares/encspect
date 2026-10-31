namespace Encspect.Formats.Wma;

// The prefix codes of the format, built once from the tables of Tables.cs.
internal static class StdCodebooks
{
    // Symbols are the table indexes; the exponent is the index less 60.
    public static readonly Vlc Exponent = new(Tables.ExponentCodes, Tables.ExponentLengths);

    // The codes follow from the lengths in table order; the level difference is the symbol less 18.
    public static readonly Vlc NoiseLevel = Vlc.FromLengths(Tables.HighBandGainLengths, [.. Tables.HighBandGainSymbols.Select(s => (int)s)]);

    public static readonly Vlc[] Coefficients = [.. Enumerable.Range(0, 6).Select(i => new Vlc(Tables.CoefficientCodes[i], Tables.CoefficientLengths[i]))];

    // Per codebook and symbol (index 2 and up): the run of the code. Symbol 0 is the escape and 1 the end of the block; the others
    // are in groups of one level each, the table of levels giving the size of each group, and within a group the runs count up.
    public static readonly ushort[][] Runs = [.. Enumerable.Range(0, 6).Select(RunsOf)];

    private static ushort[] RunsOf(int book)
    {
        ushort[] levels = Tables.CoefficientLevels[book];
        ushort[] runs = new ushort[Tables.CoefficientLengths[book].Length];
        int symbol = 2;
        foreach (ushort count in levels)
        {
            for (int run = 0; run < count && symbol < runs.Length; run++)
            {
                runs[symbol++] = (ushort)run;
            }
        }

        return runs;
    }
}
