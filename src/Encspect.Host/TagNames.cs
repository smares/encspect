using Encspect.Core;

namespace Encspect.Host;

// The names a reader sees for tag formats in messages. A parser's own name stays its identifier ("APE",
// "Lyrics3"); the text names the version where the parsed tag states one, else both.
internal static class TagNames
{
    public static string Of(string parserName, Node? tag = null)
    {
        string? version = tag?.Value?.ToString();
        char major = version is { Length: > 0 } ? version[0] : ' ';
        return parserName switch
        {
            "APE" => major switch { '1' => "APEv1", '2' => "APEv2", _ => "APEv1 or APEv2" },
            "Lyrics3" => major switch { '1' => "Lyrics3 v1", '2' => "Lyrics3 v2", _ => "Lyrics3 v1 or v2" },
            _ => parserName,
        };
    }
}
