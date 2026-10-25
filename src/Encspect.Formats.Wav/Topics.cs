namespace Encspect.Formats.Wav;

// The background articles the WAV nodes point to, by the IDs the host's topic library serves.
internal static class Topics
{
    public const string RiffChunks = "riff-chunks";
    public const string WaveFormat = "waveformatex";
    public const string Extensible = "wave-format-extensible";
    public const string ChannelLayouts = "channel-layouts";
    public const string PcmSamples = "pcm-samples";
    public const string WaveCompressed = "wave-compressed-formats";
    public const string RiffInfo = "riff-info";
    public const string CuePoints = "wave-cue-points";
    public const string Bext = "bwf-broadcast-extension";
    public const string Rf64 = "rf64";
    public const string Bw64 = "bw64";
    public const string Id3InChunks = "id3v2-in-chunks";
}
