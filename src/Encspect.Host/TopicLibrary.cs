using System.Reflection;
using System.Text;

namespace Encspect.Host;

// A background article on a concept the report shows, such as mid/side stereo: why a coding tool
// exists, how it works in principle, what seeing it in a file means and where it is specified.
// Body is Markdown of the subset TopicMarkdown reads.
public sealed record Topic(string Id, string Title, IReadOnlyList<string> Related, IReadOnlyList<string> References, string Body)
{
    public IReadOnlyList<TopicBlock> Blocks => TopicMarkdown.Parse(Body);
}

// The articles parsers point their nodes to (Node.Topic), embedded in this assembly as Markdown
// files with front matter: id, title, related (topic IDs, comma-separated) and references
// (specifications, separated by semicolons). A topic
// is shared by every format that uses the concept, so parsers name it by ID only.
public static class TopicLibrary
{
    private const string Prefix = "Encspect.Topics.";

    private static readonly Lazy<IReadOnlyDictionary<string, Topic>> Loaded = new(Load);

    // Sorted by title.
    public static IReadOnlyList<Topic> All => [.. Loaded.Value.Values.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase)];

    public static Topic? Find(string? id) => id is not null && Loaded.Value.TryGetValue(id, out Topic? topic) ? topic : null;

    // Reads one article: front matter between lines of "---", then the body.
    public static Topic Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string[] lines = text.ReplaceLineEndings("\n").Split('\n');
        if (lines.Length < 2 || lines[0] != "---")
        {
            throw new InvalidDataException("A topic starts with front matter between lines of \"---\".");
        }

        int end = Array.IndexOf(lines, "---", 1);
        if (end < 0)
        {
            throw new InvalidDataException("The front matter does not end with \"---\".");
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in lines[1..end].Where(l => l.Length > 0))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || !fields.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim()))
            {
                throw new InvalidDataException($"The front matter line \"{line}\" is not a new \"key: value\".");
            }
        }

        foreach (string key in fields.Keys.Where(k => k is not ("id" or "title" or "related" or "references")))
        {
            throw new InvalidDataException($"The front matter has an unknown key \"{key}\".");
        }

        string id = Required(fields, "id");
        if (!id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
        {
            throw new InvalidDataException($"The topic ID \"{id}\" is not lower-case letters, digits and hyphens.");
        }

        string body = string.Join('\n', lines[(end + 1)..]).Trim();
        if (body.Length == 0)
        {
            throw new InvalidDataException($"The topic {id} has no text.");
        }

        return new Topic(id, Required(fields, "title"), List(fields.GetValueOrDefault("related"), ','), List(fields.GetValueOrDefault("references"), ';'), body);
    }

    // The article as plain text wrapped at width, for a terminal: lists indented, links as their
    // text, then the related topics and the references.
    public static string ToPlainText(Topic topic, int width = 100)
    {
        ArgumentNullException.ThrowIfNull(topic);

        var text = new StringBuilder();
        text.Append(topic.Title).Append('\n').Append(new string('=', topic.Title.Length)).Append("\n\n");
        foreach (TopicBlock block in topic.Blocks)
        {
            if (block.IsListItem)
            {
                Wrap(text, PlainText(block.Inlines), width, "  - ", "    ");
            }
            else
            {
                Wrap(text, PlainText(block.Inlines), width, string.Empty, string.Empty);
                text.Append('\n');
            }
        }

        if (topic.Related.Count > 0)
        {
            Wrap(text, "Related: " + string.Join(", ", topic.Related.Select(r => $"{Find(r)?.Title ?? r} ({r})")), width, string.Empty, "  ");
        }

        if (topic.References.Count > 0)
        {
            Wrap(text, "Specified in: " + string.Join("; ", topic.References), width, string.Empty, "  ");
        }

        return text.ToString().TrimEnd('\n') + "\n";
    }

    // Splits text at spaces into lines of at most width characters, the first after first and the
    // others after rest; a word longer than a line stays whole.
    public static void Wrap(StringBuilder output, string text, int width, string first, string rest)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(text);

        int lineStart = output.Length;
        output.Append(first);
        bool isEmpty = true;
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!isEmpty && output.Length - lineStart + 1 + word.Length > width)
            {
                output.Append('\n');
                lineStart = output.Length;
                output.Append(rest);
                isEmpty = true;
            }

            if (!isEmpty)
            {
                output.Append(' ');
            }

            output.Append(word);
            isEmpty = false;
        }

        output.Append('\n');
    }

    private static string PlainText(IReadOnlyList<TopicInline> inlines) => string.Concat(inlines.Select(i => i.Text));

    private static IReadOnlyDictionary<string, Topic> Load()
    {
        Assembly assembly = typeof(TopicLibrary).Assembly;
        var topics = new Dictionary<string, Topic>(StringComparer.Ordinal);
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using Stream stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Topic topic = Parse(reader.ReadToEnd());
            if (name != Prefix + topic.Id + ".md")
            {
                throw new InvalidDataException($"The topic {topic.Id} is stored as {name[Prefix.Length..]}; its file is named after its ID.");
            }

            topics.Add(topic.Id, topic);
        }

        return topics;
    }

    private static string Required(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out string? value) && value.Length > 0 ? value : throw new InvalidDataException($"The front matter has no {key}.");

    // Related topic IDs separated by commas, references by semicolons, since a reference may hold a
    // comma.
    private static string[] List(string? value, char separator) =>
        string.IsNullOrEmpty(value) ? [] : value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
