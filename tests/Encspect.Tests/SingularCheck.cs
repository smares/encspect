using System.Text.RegularExpressions;
using Encspect.Core;

namespace Encspect.Tests;

// Finds a count of one that is not written in the singular: "1 bytes", "1 audio packets", and "1 byte are". The plural nouns are the ones the code itself
// counts with: the units of NodeValue.Integer that read in the singular for one, and every plural argument of the calls that count in running text
// (Counts.Of and the 3-argument NodeValue.Integer), read from the sources, plus the nouns listed below. A phrase that is not a count may be allowed by
// an exact pattern in Allowed, each with its reason. A verb after "not", "does", "whose", "where", "which", "that" or "than" belongs to another subject.
internal static partial class SingularCheck
{
    // Nouns that findings and values use with a count and that the call scan cannot see (built from other helpers).
    private static readonly string[] ExtraPlurals =
    [
        "escapes", "audio packets", "channels", "coupled streams", "times", "changes", "sets", "points", "subframes", "speakers", "dimensions", "descriptors",
        "units of the media's timescale", "units per second", "channel descriptions", "frames", "samples", "bytes", "bits", "blocks", "packets", "items", "entries",
        "elements", "offsets", "extensions", "objects", "pages", "streams", "records", "segments", "sections", "tracks", "chunks", "payloads", "fields", "values",
        "bands", "subbands", "words", "headers", "sizes", "groups", "ranges", "parts", "steps", "granules", "coefficients", "levels", "degrees", "ticks",
    ];

    // Phrases that contain "1" and a plural without counting anything.
    private static readonly (string Pattern, string Reason)[] Allowed =
    [
        (@"\b\d+ to 1 [a-z ]+s\b", "the end of a range: \"1 to 1 bytes\""),
        (@"groups of [\d, ]*1 windows", "a list of window counts: \"3 groups of 4, 3, 1 windows\""),
        (@"\b[Ss]tream 1 codes\b", "\"codes\" is a verb after a stream number"),
        (@"\b(?:order|every|stage|case) 1 [a-z]+s\b", "a number that names something: \"order 1 whose settings\", \"every 1 would need\""),
        (@"\b1 (?:changes|points|sets) (?:the|its|a|to|from|at|in|on)\b", "a verb after a number: \"1 changes the\""),
    ];

    private static readonly Lazy<string[]> Plurals = new(CollectPlurals);

    private static readonly string[] PluralVerbs = ["are", "were", "have", "do", "follow", "come", "differ", "need", "hold", "point", "fail", "remain", "lie"];

    private static string[] CollectPlurals()
    {
        var plurals = new HashSet<string>(ExtraPlurals, StringComparer.Ordinal);
        foreach (string unit in NodeValue.SingularUnits.Keys)
        {
            plurals.Add(unit);
        }

        string source = Path.Combine(Fixtures.FixtureDirectory, "..", "..", "src");
        if (Directory.Exists(source))
        {
            foreach (string file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                foreach (Match m in CountArguments().Matches(File.ReadAllText(file)))
                {
                    if (m.Groups[2].Value != m.Groups[1].Value && m.Groups[2].Value.EndsWith('s'))
                    {
                        plurals.Add(m.Groups[2].Value);
                    }
                }
            }
        }

        return [.. plurals.OrderByDescending(p => p.Length)];
    }

    // , "singular", "plural")  of Counts.Of, Counts.Agree-free helpers and the 3-argument NodeValue.Integer.
    [GeneratedRegex(@", ""([a-z][a-z' ]*)"", ""([a-z][a-z' ]*s)""\)")]
    private static partial Regex CountArguments();

    public static IReadOnlyList<string> Problems(string text)
    {
        foreach ((string pattern, _) in Allowed)
        {
            text = Regex.Replace(text, pattern, " ");
        }

        var problems = new List<string>();
        string nouns = string.Join('|', Plurals.Value.Select(Regex.Escape));
        foreach (Match m in Regex.Matches(text, $@"(?<![\w.%#^-])1 (?:{nouns})\b"))
        {
            problems.Add(m.Value);
        }

        string singulars = string.Join('|', Plurals.Value.Select(Singular).Distinct(StringComparer.Ordinal).Select(Regex.Escape));
        string verbs = string.Join('|', PluralVerbs);
        foreach (Match m in Regex.Matches(text, $@"(?<![\w.%#^-])1 (?:{singulars})\b(?:(?!\bdoes\b|\bnot\b|\bwhose\b|\bwhere\b|\bwhich\b|\bthat\b|\bthan\b)[^,.;:()]){{0,80}}?\b(?:{verbs})\b"))
        {
            problems.Add(m.Value);
        }

        foreach (Match m in Regex.Matches(text, $@"(?<![\w.%#^-])1 of [^,.;:()]{{0,60}}?\b\d+ (?:{nouns}) (?:{verbs})\b"))
        {
            problems.Add(m.Value);
        }

        return problems;
    }

    // The singular of a plural noun, for the last word of a phrase: from the NodeValue table where it has one, else without the s.
    private static string Singular(string plural)
    {
        if (NodeValue.SingularUnits.TryGetValue(plural, out string? singular))
        {
            return singular;
        }

        int space = plural.LastIndexOf(' ');
        string last = plural[(space + 1)..];
        string head = space < 0 ? string.Empty : plural[..(space + 1)];
        return last.EndsWith("ies", StringComparison.Ordinal) ? head + last[..^3] + "y" : head + last[..^1];
    }
}
