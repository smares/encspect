namespace Encspect.Core;

public enum Severity
{
    Info,
    Warning,
    Error,
}

public sealed record Finding(Severity Severity, string Code, string Message, Node? Node = null);
