namespace Encspect.Host;

// Where the host looks for the files that belong with the one it analyzes, such as the correction file of a WavPack hybrid or an OptimFROG DualStream
// file (AnalysisOptions.Companions). A format parser asks for a companion by its extension and the host opens it, never the parser. The companion's name is
// the input's name without its last extension, a dot and the companion's extension, in the folder of the input: "x.wv" to "x.wvc", "x.ofs" to "x.ofc",
// "x.bin" to "x.wvc". The extension is written in the case of the input's ("X.WV" to "X.WVC") and in lower case otherwise; one name is tried.
public abstract class CompanionSource
{
    private protected CompanionSource()
    {
    }

    // Nothing is looked for: a pair is reported as unchecked.
    public static CompanionSource None { get; } = new NoSource("the caller asked for no companion files to be looked for");

    // The default of Analyze(source, stream): the input has no folder to look in.
    internal static CompanionSource Stream { get; } = new NoSource("no folder to look in: the input is a stream");

    // The file system, as the default of Analyzer.AnalyzeFile(path): the companion lies in the folder of the input. It is opened as an input is (a FIFO,
    // a device or a folder is not opened), read as a stream, never loaded whole, and closed with the analysis.
    public static CompanionSource Beside(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        return new FileSource(path);
    }

    // A caller with no file system, or one that knows the pair under other names: the function gets the extension asked for (no dot, lower case) and returns a
    // readable, seekable stream, or null where there is none. The host owns the stream and disposes it. A stream that cannot be read or cannot seek, and an
    // IOException or UnauthorizedAccessException from the function, make the pair unchecked; any other exception passes through Analyze.
    public static CompanionSource From(Func<string, Stream?> open)
    {
        ArgumentNullException.ThrowIfNull(open);

        return new FunctionSource(open);
    }

    internal abstract Lookup Find(string input, string extension);

    // What a lookup found. Exactly one of Stream, IsMissing and Unchecked is set. Name is the companion's file name, Path the name with the folder of the input
    // (the report's source).
    internal sealed record Lookup(string Name, string Path, Stream? Stream = null, bool IsMissing = false, string? Unchecked = null, bool IsUnreadable = false);

    // The companion's name by the rule above.
    internal static string NameOf(string input, string extension)
    {
        string file = System.IO.Path.GetFileName(input);
        string own = System.IO.Path.GetExtension(file);
        string stem = file[..(file.Length - own.Length)];
        bool isUpper = own.Length > 1 && own.Any(char.IsLetter) && !own.Any(char.IsLower);
        return $"{stem}.{(isUpper ? extension.ToUpperInvariant() : extension.ToLowerInvariant())}";
    }

    internal static string PathOf(string input, string name)
    {
        string? folder = System.IO.Path.GetDirectoryName(input);
        return string.IsNullOrEmpty(folder) ? name : System.IO.Path.Combine(folder, name);
    }

    private sealed class NoSource(string reason) : CompanionSource
    {
        internal override Lookup Find(string input, string extension)
        {
            string name = NameOf(input, extension);
            return new Lookup(name, PathOf(input, name), Unchecked: reason);
        }
    }

    private sealed class FileSource(string path) : CompanionSource
    {
        internal override Lookup Find(string input, string extension)
        {
            string name = NameOf(path, extension);
            string companion = PathOf(path, name);
            try
            {
                if (!File.Exists(companion) && !Directory.Exists(companion))
                {
                    return new Lookup(name, companion, IsMissing: true);
                }

                FileStream? stream = FileType.Open(companion, out string? notRegular);
                return stream is null
                    ? new Lookup(name, companion, Unchecked: $"{name} is {notRegular}, not a regular file", IsUnreadable: true)
                    : new Lookup(name, companion, stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new Lookup(name, companion, Unchecked: ex.Message, IsUnreadable: true);
            }
        }
    }

    private sealed class FunctionSource(Func<string, Stream?> open) : CompanionSource
    {
        internal override Lookup Find(string input, string extension)
        {
            string name = NameOf(input, extension);
            string companion = PathOf(input, name);
            Stream? stream;
            try
            {
                stream = open(extension);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new Lookup(name, companion, Unchecked: ex.Message, IsUnreadable: true);
            }

            if (stream is null)
            {
                return new Lookup(name, companion, IsMissing: true);
            }

            if (!stream.CanRead || !stream.CanSeek)
            {
                stream.Dispose();
                return new Lookup(name, companion, Unchecked: "the stream the caller gave cannot be read or cannot seek", IsUnreadable: true);
            }

            return new Lookup(name, companion, stream);
        }
    }
}
