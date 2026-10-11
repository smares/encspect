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

    public CancellationToken CancellationToken => Reader.CancellationToken;
}
