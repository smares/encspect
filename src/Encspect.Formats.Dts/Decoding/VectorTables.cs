namespace Encspect.Formats.Dts.Decoding;

// The two vector tables of Annex D.10, which the specification's PDF does not carry: D.10.1, the 4,096 vectors of four ADPCM prediction coefficients (each entry a multiple of 2^-13),
// and D.10.2, the 1,024 vectors of 32 samples of the high-frequency subbands (each 16-bit entry two elements of 8 bits divided by 2^4). V1.6.1 says of both "Due to its extensive
// size, this table is not included here". Until a source of them is pinned (scripts/fixtures/dts_tables.py), a frame that needs one is not decoded.
internal static class VectorTables
{
    public static bool IsAvailable => false;

    public static void Coefficients(int index, Span<float> destination) => throw new InvalidDataException("The ADPCM coefficient vectors of Annex D.10.1 are not available");

    public static void HighFrequency(int index, Span<float> destination) => throw new InvalidDataException("The high-frequency vectors of Annex D.10.2 are not available");
}
