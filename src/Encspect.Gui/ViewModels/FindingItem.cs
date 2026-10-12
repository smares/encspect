using Encspect.Core;

namespace Encspect.Gui.ViewModels;

public sealed class FindingItem(Finding finding)
{
    public Finding Finding { get; } = finding;

    public Severity Severity => Finding.Severity;

    public string Code => Finding.Code;

    public string Message => Finding.Message;

    // The code, and how many cases a finding reported once per stream stands for.
    public string Detail => Finding.Count > 1 ? $"{Finding.Code} · {Format.Count(Finding.Count, "case", "cases")}" : Finding.Code;

    public IReadOnlyList<int>? Path => Finding.Path;

    public bool IsError => Severity == Severity.Error;

    public bool IsWarning => Severity == Severity.Warning;

    public bool IsInfo => Severity == Severity.Info;
}
