using Encspect.Core;

namespace Encspect.Gui.ViewModels;

// rootIndex and file: which top-level row of the structure tree holds the finding's node (0 for the analyzed file, then its companion files, whose
// findings are listed with the file's) and, for a companion file, its name.
public sealed class FindingItem(Finding finding, int rootIndex = 0, string? file = null)
{
    public Finding Finding { get; } = finding;

    public int RootIndex { get; } = rootIndex;

    public Severity Severity => Finding.Severity;

    public string Code => Finding.Code;

    public string Message => Finding.Message;

    // The code, and how many cases a finding reported once per stream stands for.
    public string Detail => (Finding.Count > 1 ? $"{Finding.Code} · {Format.Count(Finding.Count, "case", "cases")}" : Finding.Code) + (file is null ? string.Empty : " · " + file);

    public IReadOnlyList<int>? Path => Finding.Path;

    public bool IsError => Severity == Severity.Error;

    public bool IsWarning => Severity == Severity.Warning;

    public bool IsInfo => Severity == Severity.Info;
}
