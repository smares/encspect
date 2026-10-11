using Encspect.Core;

namespace Encspect.Host;

// Reports for inputs that are not analyzed: a file that cannot be read, a folder that cannot be
// listed, and anything that is not a regular file. Each holds one Error finding, so batch outputs
// list every input.
public static class FileReports
{
    public const string UnreadableCode = "file.unreadable";
    public const string UnreadableFolderCode = "folder.unreadable";
    public const string NotRegularCode = "file.not-regular";
    public const string NotSeekableCode = "file.not-seekable";

    public static Report Unreadable(string source, string message) => Create(source, UnreadableCode, message);

    public static Report UnreadableFolder(string source, string message) => Create(source, UnreadableFolderCode, message);

    // The report for a path that names a FIFO, a device, a socket, a folder or a kernel file,
    // without reading it; null for a regular file. Throws IOException or
    // UnauthorizedAccessException when the path cannot be opened.
    public static Report? NotRegular(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using FileStream? stream = FileType.Open(path, out string? kind);
        return kind is not null ? NotRegular(path, kind) : null;
    }

    public static bool IsNotAnalyzed(Report report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return report.Findings.Any(f => f.Code is UnreadableCode or UnreadableFolderCode or NotRegularCode or NotSeekableCode);
    }

    internal static Report NotRegular(string source, string kind) =>
        Create(source, NotRegularCode, $"This is {kind}, not a regular file, so it was not read.");

    internal static Report Create(string source, string code, string message)
    {
        ArgumentNullException.ThrowIfNull(source);

        var root = new Node("File", 0, 0);
        return new Report(source, root, [new Finding(Severity.Error, code, message, root)]);
    }
}
