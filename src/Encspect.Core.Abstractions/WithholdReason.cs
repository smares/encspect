namespace Encspect.Core;

// Why a decoder holds back output of units it read and decoded (DecodeContext.Withhold, docs/design/lossy-decoders.md, "Withheld
// output"). The values are the order the writers list the reasons in; a new reason goes at the end.
public enum WithholdReason
{
    // The output after a gap or an unreadable unit that depends on the state the lost unit left: an overlap, a filter's history, a
    // predictor that has not converged.
    AfterGap,

    // The output after frames of 0 bytes (Opus DTX or lost frames), which carry no data to decode.
    EmptyFrame,

    // A Layer III frame whose main data begin in bytes that are not available (before the stream, or in a lost frame).
    MainDataUnavailable,

    // The span a change of coding mode affects, where a decoder withholds it instead of smoothing it.
    ModeTransition,
}

public static class WithholdReasons
{
    public const int Count = 4;

    // The reason as the writers spell it in text: "after a gap".
    public static string Text(WithholdReason reason) => reason switch
    {
        WithholdReason.AfterGap => "after a gap",
        WithholdReason.EmptyFrame => "frames of 0 bytes",
        WithholdReason.MainDataUnavailable => "main data not available",
        WithholdReason.ModeTransition => "mode transition",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    // The reason as JSON spells it: "afterGap".
    public static string Key(WithholdReason reason) => reason switch
    {
        WithholdReason.AfterGap => "afterGap",
        WithholdReason.EmptyFrame => "emptyFrame",
        WithholdReason.MainDataUnavailable => "mainDataUnavailable",
        WithholdReason.ModeTransition => "modeTransition",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}

// The withheld samples of a stream by reason; a struct, so that records holding it compare by value.
public readonly record struct WithheldCounts(long AfterGap, long EmptyFrame, long MainDataUnavailable, long ModeTransition)
{
    public long this[WithholdReason reason] => reason switch
    {
        WithholdReason.AfterGap => AfterGap,
        WithholdReason.EmptyFrame => EmptyFrame,
        WithholdReason.MainDataUnavailable => MainDataUnavailable,
        WithholdReason.ModeTransition => ModeTransition,
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    public long Total => AfterGap + EmptyFrame + MainDataUnavailable + ModeTransition;

    public static WithheldCounts From(ReadOnlySpan<long> byReason) => new(byReason[0], byReason[1], byReason[2], byReason[3]);
}
