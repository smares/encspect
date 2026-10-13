namespace Encspect.Core;

public sealed class SniffContext
{
    public SniffContext(ByteReader reader, string? extensionHint = null)
    {
        ArgumentNullException.ThrowIfNull(reader);

        Reader = reader;
        string? hint = extensionHint?.TrimStart('.').ToLowerInvariant();
        ExtensionHint = string.IsNullOrEmpty(hint) ? null : hint;
    }

    public ByteReader Reader { get; }

    // The file name's extension, lower case and without the dot, or null. Only a hint: files are
    // often misnamed.
    public string? ExtensionHint { get; }

    // Where the first tag found inside the data (through an ID3v2.4 SEEK frame) starts, or null. A
    // parser reads up to it (ParseContext.EmbeddedTags), so a format that is recognized by where its
    // data ends sniffs up to it too.
    public long? FirstEmbeddedTag { get; init; }

    public CancellationToken CancellationToken => Reader.CancellationToken;
}
