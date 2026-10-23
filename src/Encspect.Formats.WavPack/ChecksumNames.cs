namespace Encspect.Formats.WavPack;

// The names the checksums of a WavPack stream are declared with (StreamVerification.Checksums), which the decoder's
// results find their tally by.
internal static class ChecksumNames
{
    public const string BlockChecksum = "block checksum";
    public const string BlockCrc = "block header CRC";
    public const string ExtraCrc = "extended CRC";
    public const string Md5 = "MD5";
}
