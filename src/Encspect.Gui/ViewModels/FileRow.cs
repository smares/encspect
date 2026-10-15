using Encspect.Core;

namespace Encspect.Gui.ViewModels;

public enum FileState
{
    Waiting,
    Analyzed,
    Canceled,
}

// One file in the list. The typed properties (Duration, Bitrate, SampleRate, the finding counts)
// are what columns sort by; the *Text properties are what they show.
public sealed class FileRow : ObservableObject
{
    private const long Cap = (1 << 20) - 1;

    private static readonly string[] Changed =
    [
        nameof(Format), nameof(FormatText), nameof(Codec), nameof(Duration), nameof(DurationText), nameof(Bitrate), nameof(IsVbr),
        nameof(BitrateText), nameof(SampleRate), nameof(SampleRateText), nameof(ChannelMode), nameof(ChannelLayout),
        nameof(ChannelLayoutTip), nameof(Encoder), nameof(EncoderConfidence),
        nameof(EncoderText), nameof(Tags), nameof(Errors), nameof(Warnings), nameof(Infos), nameof(ErrorsText), nameof(WarningsText),
        nameof(InfosText), nameof(FindingsRank), nameof(Problem), nameof(IsProtected), nameof(ProtectionText), nameof(ProtectionTip),
    ];

    private FileState _state;
    private FileSummary? _summary;

    public FileRow(string path, string name, string? folderError = null)
    {
        Path = path;
        Name = name;
        FolderError = folderError;
    }

    public string Path { get; }

    public string Name { get; }

    // Why the folder this row stands for could not be listed; null for a file.
    public string? FolderError { get; }

    // Whether the row has reached the list, which the UI thread sets.
    internal bool IsListed { get; set; }

    public FileState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                Raise(nameof(FormatText));
            }
        }
    }

    public string? Format => _summary?.Summary.Format;

    public string FormatText => _state switch
    {
        FileState.Waiting => "…",
        FileState.Canceled => "Not analyzed",
        _ => Format ?? (Problem is not null ? "Not read" : "Unknown"),
    };

    public string? Codec => _summary?.Summary.Codec;

    public double? Duration => _summary?.Summary.Duration;

    public string? DurationText => Duration is { } seconds ? ViewModels.Format.Duration(seconds) : null;

    public double? Bitrate => _summary?.Summary.AverageBitrate;

    public bool? IsVbr => _summary?.IsVbr;

    public string? BitrateText => Bitrate is { } kbps ? ViewModels.Format.Bitrate(kbps, IsVbr) : null;

    public int? SampleRate => _summary?.Summary.SampleRate;

    public string? SampleRateText => SampleRate is > 0 and var hertz ? ViewModels.Format.SampleRate(hertz) : null;

    public string? ChannelMode => _summary?.Summary.ChannelMode;

    // The first stream's layout by its common name, such as "5.1", or by its speakers where it has
    // none.
    public string? ChannelLayout => _summary?.Summary is { ChannelLayout: { } layout } summary
        ? summary.ChannelLayoutName ?? ChannelLayouts.Speakers(layout)
        : null;

    // The speakers in the stream's channel order, for the cell's tooltip.
    public string? ChannelLayoutTip => _summary?.Summary.ChannelLayout is { } layout ? ChannelLayouts.Speakers(layout) : null;

    // Whether any stream is protected; null for a file without a stream.
    public bool? IsProtected => _summary is { Summary.Streams: > 0 } summary ? summary.Summary.IsProtected : null;

    // "Protected" or nothing, so the column stays empty for the files that matter least.
    public string? ProtectionText => IsProtected == true ? "Protected" : null;

    // The schemes and DRM systems, for the cell's tooltip.
    public string? ProtectionTip => _summary?.Summary is { IsProtected: true } summary
        ? string.Join("; ", summary.ProtectionSchemes.Concat(summary.DrmSystems.Count > 0 ? ["DRM systems: " + string.Join(", ", summary.DrmSystems)] : []))
        : null;

    public string? Encoder => _summary?.Summary.Encoder;

    public Confidence? EncoderConfidence => _summary?.Summary.EncoderConfidence;

    public string? EncoderText => Encoder is { } encoder ? ViewModels.Format.Encoder(encoder, EncoderConfidence) : null;

    public string? Tags => _summary is { Summary.Tags.Count: > 0 } summary ? string.Join(", ", summary.Summary.Tags) : null;

    public int? Errors => _summary?.Summary.Errors;

    public int? Warnings => _summary?.Summary.Warnings;

    public int? Infos => _summary?.Summary.Infos;

    // Zero shows as nothing, so the counts that matter stand out.
    public string? ErrorsText => Errors is > 0 and var count ? count.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

    public string? WarningsText => Warnings is > 0 and var count ? count.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

    public string? InfosText => Infos is > 0 and var count ? count.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

    // The findings column sorts by errors, then warnings, then infos, so the worst files come
    // first when it sorts descending.
    public long? FindingsRank => _summary is { Summary: var s }
        ? (Math.Min(s.Errors, Cap) << 40) | (Math.Min(s.Warnings, Cap) << 20) | Math.Min(s.Infos, Cap)
        : null;

    // Why the file was not analyzed, as its single error finding says.
    public string? Problem => _summary?.Problem;

    // Whether the summary came from the summary cache rather than an analysis of the file.
    public bool IsCached { get; private set; }

    public void Apply(FileSummary summary, bool isCached = false)
    {
        ArgumentNullException.ThrowIfNull(summary);

        _summary = summary;
        IsCached = isCached;
        State = FileState.Analyzed;
        foreach (string name in Changed)
        {
            Raise(name);
        }
    }

    public void MarkCanceled()
    {
        if (_state == FileState.Waiting)
        {
            State = FileState.Canceled;
        }
    }
}
