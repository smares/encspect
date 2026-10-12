namespace Encspect.Host;

// What the nodes the host makes are, as parsers describe theirs (Node.Description).
internal static class HostText
{
    public const string File = "The whole file, with the format the analyzer recognized in it and every tag and stretch of data it holds.";
    public const string Detection = "How sure the analyzer is that these bytes are a real tag, from where they sit and how well their structure checks out.";
    public const string Evidence = "One check that supports the detection, at the bytes it looked at.";
    public const string UnrecognizedData = "Bytes that no tag claims and no format parser recognized.";
}

// The background articles the host's nodes point to.
internal static class Topics
{
    public const string TagPlacement = "tag-placement";
}
