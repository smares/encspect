namespace Encspect.Core;

public enum CompanionStatus
{
    // The host opened the file and gives the parser a context over it.
    Found,

    // The host looked and there is none.
    Missing,

    // The host did not look, or could not read the file (Reason says why).
    Unchecked,
}

// A file that belongs with the one being analyzed, such as the correction file of a hybrid or DualStream file, which the host opens
// where the parser asks (ParseContext.OpenCompanion): a parser never opens a file.
public sealed class Companion
{
    private Companion(CompanionStatus status, string source, string? reason, ParseContext? context, bool isUnreadable)
    {
        Status = status;
        Source = source;
        Reason = reason;
        Context = context;
        IsUnreadable = isUnreadable;
    }

    public CompanionStatus Status { get; }

    // The companion's name as the host looked for it ("x.ofc"); empty where it did not look for one.
    public string Source { get; }

    // Why the companion was not looked for or not read; null for Found and Missing.
    public string? Reason { get; }

    // Where Status is Found: the context over the companion, with its reader and its root, through which the parser builds the companion's nodes,
    // findings and streams. Its nodes have offsets in the companion's file.
    public ParseContext? Context { get; }

    // True where the file exists and could not be opened or read (Status is Unchecked then, and Reason holds the error).
    public bool IsUnreadable { get; }

    public static Companion Found(string source, ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);
        return new Companion(CompanionStatus.Found, source, null, context, false);
    }

    public static Companion Missing(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new Companion(CompanionStatus.Missing, source, null, null, false);
    }

    public static Companion Unchecked(string source, string reason, bool isUnreadable = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return new Companion(CompanionStatus.Unchecked, source, reason, null, isUnreadable);
    }
}

// A second report inside a report: a file the main file's parser read as part of it, with the tree, findings and streams the parser built from it.
public sealed record CompanionReport(string Role, Report Report)
{
    // The role of a correction file (WavPack's .wvc, OptimFROG's .ofc).
    public const string Correction = "correction";
}
