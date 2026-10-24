namespace Encspect.Formats.TrueHd;

// The names the checksums of a stream are declared with, which the decoder's results use too.
internal static class ChecksumNames
{
    public const string MajorSyncCrc = "major sync CRC-16";
    public const string ParityNibble = "unit parity nibble";
    public const string RestartHeaderCrc = "restart header CRC-8";
    public const string SubstreamParity = "substream parity byte";
    public const string SubstreamCrc = "substream CRC-8";

    // The lossless check of the presentation a substream outputs, named by the substream: the name does not depend on what the parser or the decoder
    // learned of the presentation, so that their declarations and results always match.
    public static string LosslessCheck(int substream) => $"lossless check, substream {substream}";
}
