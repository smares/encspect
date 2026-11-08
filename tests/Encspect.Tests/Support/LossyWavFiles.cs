namespace Encspect.Tests;

// The committed lossyWAV files with their text changed, for the fact text no lossyWAV version is known to write.
internal static class LossyWavFiles
{
    // The bytes of the file under tests/fixtures with " @ " after the version written as " - ": the text still starts with "lossyWAV " but has no
    // date and time to split off.
    public static byte[] WithoutTimeSeparator(string fixture) => Replace(fixture, 15, (byte)'-');

    // The same with ", " before the settings written as "; ".
    public static byte[] WithoutSettingsSeparator(string fixture)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, fixture));
        int at = file.AsSpan().IndexOf("lossyWAV 1.4.2 @ "u8);
        int comma = file.AsSpan(at).IndexOf(", --"u8);
        file[at + comma] = (byte)';';
        return file;
    }

    private static byte[] Replace(string fixture, int index, byte value)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, fixture));
        int at = file.AsSpan().IndexOf("lossyWAV 1.4.2 @ "u8);
        file[at + index] = value;
        return file;
    }
}
