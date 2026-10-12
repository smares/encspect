using System.Text;

namespace Encspect.Host;

// A run of an article's text: plain, emphasized (*text*), strong (**text**) or code (`text`), or a
// link to another topic ([text](topic-id)).
public sealed record TopicInline(string Text, bool IsEmphasis = false, bool IsStrong = false, bool IsCode = false, string? Link = null);

// A paragraph, or an item of a list ("- item").
public sealed record TopicBlock(IReadOnlyList<TopicInline> Inlines, bool IsListItem = false);

// The Markdown subset articles are written in: paragraphs separated by blank lines, lists of "- "
// items, emphasis, strong text, inline code and links to other topics by ID. Anything else is
// text as written, so a front end needs no Markdown library.
public static class TopicMarkdown
{
    public static IReadOnlyList<TopicBlock> Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var blocks = new List<TopicBlock>();
        var paragraph = new StringBuilder();
        string? item = null;
        foreach (string raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                Flush();
                item = line[2..].Trim();
            }
            else if (line.Length == 0)
            {
                Flush();
            }
            else if (item is not null)
            {
                item += " " + line;
            }
            else
            {
                paragraph.Append(paragraph.Length > 0 ? " " : string.Empty).Append(line);
            }
        }

        Flush();
        return blocks;

        void Flush()
        {
            if (item is not null)
            {
                blocks.Add(new TopicBlock(Inlines(item), IsListItem: true));
                item = null;
            }

            if (paragraph.Length > 0)
            {
                blocks.Add(new TopicBlock(Inlines(paragraph.ToString())));
                paragraph.Clear();
            }
        }
    }

    public static IReadOnlyList<TopicInline> Inlines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var inlines = new List<TopicInline>();
        var plain = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '`' && text.IndexOf('`', i + 1) is var codeEnd and > 0)
            {
                Add(new TopicInline(text[(i + 1)..codeEnd], IsCode: true));
                i = codeEnd + 1;
            }
            else if (text.AsSpan(i).StartsWith("**") && text.IndexOf("**", i + 2, StringComparison.Ordinal) is var strongEnd and > 0)
            {
                Add(new TopicInline(text[(i + 2)..strongEnd], IsStrong: true));
                i = strongEnd + 2;
            }
            else if (text[i] == '*' && text.IndexOf('*', i + 1) is var emphasisEnd and > 0)
            {
                Add(new TopicInline(text[(i + 1)..emphasisEnd], IsEmphasis: true));
                i = emphasisEnd + 1;
            }
            else if (text[i] == '[' && text.IndexOf("](", i + 1, StringComparison.Ordinal) is var middle and > 0
                && text.IndexOf(')', middle + 2) is var linkEnd and > 0)
            {
                Add(new TopicInline(text[(i + 1)..middle], Link: text[(middle + 2)..linkEnd]));
                i = linkEnd + 1;
            }
            else
            {
                plain.Append(text[i]);
                i++;
            }
        }

        AddPlain();
        return inlines;

        void Add(TopicInline inline)
        {
            AddPlain();
            inlines.Add(inline);
        }

        void AddPlain()
        {
            if (plain.Length > 0)
            {
                inlines.Add(new TopicInline(plain.ToString()));
                plain.Clear();
            }
        }
    }
}
