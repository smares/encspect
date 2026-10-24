namespace Encspect.Formats.Aiff;

// The background articles the AIFF nodes point to, by the IDs the host's topic library serves.
internal static class Topics
{
    public const string Container = "aiff-container";
    public const string Common = "aiff-common-chunk";
    public const string ExtendedRate = "extended-sample-rate";
    public const string SoundData = "aiff-sound-data";
    public const string Compression = "aiff-c-compression";
    public const string Markers = "aiff-markers-and-loops";
    public const string PcmSamples = "pcm-samples";
    public const string ChannelLayouts = "channel-layouts";
    public const string Id3InChunks = "id3v2-in-chunks";
}
