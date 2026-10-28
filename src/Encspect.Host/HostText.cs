namespace Encspect.Host;

// What the nodes the host makes are, as parsers describe theirs (Node.Description).
internal static class HostText
{
    public const string File = "The whole file, with the format the analyzer recognized in it and every tag and stretch of data it holds.";
    public const string Detection = "How sure the analyzer is that these bytes are a real tag, from where they sit and how well their structure checks out.";
    public const string Evidence = "One check that supports the detection, at the bytes it looked at.";
    public const string UnrecognizedData = "Bytes that no tag claims and no format parser recognized.";

    public const string DataAfterContainer = "Bytes after the end a container format declares for itself, between or after tags a SEEK frame led to; neither a tag nor the format claims them.";
}

// The background articles the host's nodes point to.
internal static class Topics
{
    public const string TagPlacement = "tag-placement";
    public const string Measurements = "measurements";
    public const string Spectrogram = "spectrogram";
    public const string SpectralCutoff = "spectral-cutoff";
    public const string Levels = "levels";
    public const string Clipping = "clipping";
    public const string DcOffset = "dc-offset";
    public const string DigitalSilence = "digital-silence";
    public const string BitDepthUsage = "bit-depth-usage";
    public const string Loudness = "loudness";
    public const string LoudnessRange = "loudness-range";
    public const string TruePeak = "sample-peak-and-true-peak";
}
