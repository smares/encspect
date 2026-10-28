using Encspect.Core;
using Encspect.Gui.ViewModels;
using Encspect.Host;

namespace Encspect.Gui.Tests;

public class DecodeAudioTests
{
    [Fact]
    public void A_stream_with_checksums_over_its_audio_can_be_decoded_and_the_verdict_replaces_the_unknown()
    {
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);
        Task load = model.LoadAsync(Fixtures.Flac("external-16-stereo-l5.flac"));
        ui.Until(() => load.IsCompleted);

        Assert.True(model.CanDecodeAudio);
        Assert.Contains("Verified: unknown", model.VerificationText, StringComparison.Ordinal);
        Assert.Contains("STREAMINFO MD5 not compared: decoding was not asked for", model.VerificationText, StringComparison.Ordinal);
        Report before = model.Report!;

        Task decode = model.DecodeAudioAsync();
        Assert.True(model.IsDecoding);
        Assert.True(model.IsProgressShown);
        Assert.False(model.CanDecodeAudio);
        Assert.Same(before, model.Report);
        ui.Until(() => decode.IsCompleted);

        Assert.False(model.IsDecoding);
        Assert.NotSame(before, model.Report);
        Assert.StartsWith("Verified: yes (", model.VerificationText, StringComparison.Ordinal);
        Assert.Contains("STREAMINFO MD5 1 of 1", model.VerificationText, StringComparison.Ordinal);
        Assert.False(model.CanDecodeAudio);
        Assert.Null(model.CommandError);
    }

    [Fact]
    public void A_stream_whose_stored_md5_differs_is_verified_as_no()
    {
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);
        Task load = model.LoadAsync(Fixtures.Flac("built-md5-mismatch.flac"));
        ui.Until(() => load.IsCompleted);
        Task decode = model.DecodeAudioAsync();
        ui.Until(() => decode.IsCompleted);

        Assert.StartsWith("Verified: no (", model.VerificationText, StringComparison.Ordinal);
        Assert.Contains("STREAMINFO MD5 1 of 1 differ", model.VerificationText, StringComparison.Ordinal);
    }

    // WAV, Wave64 and AIFF streams have a verification in the model (the status of their decoding) and no checksum: no "Verified" line, but
    // the command is enabled, and decoding measures them.
    [Fact]
    public void A_stream_of_PCM_has_no_checksum_and_no_verified_line_but_can_be_decoded_and_is_measured()
    {
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);
        Task load = model.LoadAsync(Fixtures.Wav("external-pcm-s16-stereo-44100.wav"));
        ui.Until(() => load.IsCompleted);

        Assert.True(model.Report!.Streams.Single().Verification is { Decoding: DecodeStatus.NotRequested, HasChecksums: false });
        Assert.Null(model.Report.Streams.Single().Measurements);
        Assert.True(model.CanDecodeAudio);
        Assert.Null(model.VerificationText);
        Assert.False(model.HasVerificationText);

        Task decode = model.DecodeAudioAsync();
        Assert.True(model.IsDecoding);
        ui.Until(() => decode.IsCompleted);
        Assert.False(model.IsDecoding);
        Assert.False(model.CanDecodeAudio);
        Assert.Null(model.VerificationText);
        StreamMeasurements measured = model.Report.Streams.Single().Measurements!;
        Assert.Equal(MeasurementStatus.Complete, measured.Status);
        Assert.NotNull(measured.Spectrogram);
    }

    [Fact]
    public void A_file_without_a_decoder_has_nothing_to_decode()
    {
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);
        Task load = model.LoadAsync(Fixtures.Mpeg("external-lame-cbr128-joint.mp3"));
        ui.Until(() => load.IsCompleted);

        Assert.False(model.CanDecodeAudio);
        Task decode = model.DecodeAudioAsync();
        Assert.True(decode.IsCompleted);
        Assert.False(model.IsDecoding);
    }

    [Fact]
    public void The_file_stays_shown_while_it_is_decoded_and_the_selection_stays_on_its_node()
    {
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);
        Task load = model.LoadAsync(Fixtures.Flac("external-16-stereo-l5.flac"));
        ui.Until(() => load.IsCompleted);
        model.SelectedNode = model.Tree!.Reveal([0]);
        int index = model.SelectedNode!.ChildIndex;

        Task decode = model.DecodeAudioAsync();
        Assert.NotNull(model.Tree);
        Assert.False(model.IsLoading);
        ui.Until(() => decode.IsCompleted);

        Assert.Equal(index, model.SelectedNode!.ChildIndex);
        Assert.Equal("external-16-stereo-l5.flac", model.Title);
    }

    [Fact]
    public void A_reload_keeps_decoding_until_another_file_is_loaded()
    {
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);
        string path = Fixtures.Flac("external-16-stereo-l5.flac");
        Task load = model.LoadAsync(path);
        ui.Until(() => load.IsCompleted);
        Task decode = model.DecodeAudioAsync();
        ui.Until(() => decode.IsCompleted);

        Task reload = model.ReloadAsync();
        ui.Until(() => reload.IsCompleted);
        Assert.StartsWith("Verified: yes", model.VerificationText, StringComparison.Ordinal);

        Task other = model.LoadAsync(path);
        ui.Until(() => other.IsCompleted);
        Assert.StartsWith("Verified: unknown", model.VerificationText, StringComparison.Ordinal);
        Assert.True(model.CanDecodeAudio);
    }

    [Fact]
    public void Stopping_leaves_the_analysis_as_it_was()
    {
        var ui = new TestDispatcher();
        var parser = new DecodeBlockingParser();
        using var model = new DetailViewModel(parser.Analyzer(), ui.Post);
        using var folder = new TempFolder();
        string path = folder.Write("a.vblk", DecodeBlockingParser.File);
        Task load = model.LoadAsync(path);
        ui.Until(() => load.IsCompleted);
        Report before = model.Report!;
        Assert.True(model.CanDecodeAudio);

        Task decode = model.DecodeAudioAsync();
        ui.Until(() => parser.Started == 2);
        Assert.True(model.IsDecoding);
        model.StopDecoding();
        ui.Until(() => decode.IsCompleted);

        Assert.Equal(1, parser.Canceled);
        Assert.False(model.IsDecoding);
        Assert.Same(before, model.Report);
        Assert.True(model.CanDecodeAudio);
        Assert.Null(model.CommandError);
    }

    [Fact]
    public void Analyzing_as_a_format_while_decoding_does_not_decode()
    {
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);
        Task load = model.LoadAsync(Fixtures.Flac("external-16-stereo-l5.flac"));
        ui.Until(() => load.IsCompleted);

        Task decode = model.DecodeAudioAsync();
        Task forced = model.AnalyzeAsAsync(null);
        ui.Until(() => forced.IsCompleted && decode.IsCompleted);

        Assert.False(model.IsDecoding);
        Assert.StartsWith("Verified: unknown", model.VerificationText, StringComparison.Ordinal);
        Assert.True(model.CanDecodeAudio);
    }

    [Fact]
    public void A_file_that_cannot_be_read_again_shows_why_and_leaves_the_analysis_as_it_was()
    {
        var ui = new TestDispatcher();
        using var model = new DetailViewModel(Analyzer.CreateDefault(), ui.Post);
        using var folder = new TempFolder();
        string path = folder.Copy(Fixtures.Flac("external-16-stereo-l5.flac"), "a.flac");
        Task load = model.LoadAsync(path);
        ui.Until(() => load.IsCompleted);
        Report before = model.Report!;

        File.Delete(path);
        Task decode = model.DecodeAudioAsync();
        ui.Until(() => decode.IsCompleted);

        Assert.StartsWith("Not decoded: ", model.CommandError, StringComparison.Ordinal);
        Assert.Same(before, model.Report);
        Assert.False(model.IsDecoding);
        Assert.True(model.CanDecodeAudio);
    }

    [Fact]
    public void Loading_another_file_while_decoding_drops_the_decoding()
    {
        var ui = new TestDispatcher();
        var parser = new DecodeBlockingParser();
        using var model = new DetailViewModel(parser.Analyzer(), ui.Post);
        using var folder = new TempFolder();
        string path = folder.Write("a.vblk", DecodeBlockingParser.File);
        Task load = model.LoadAsync(path);
        ui.Until(() => load.IsCompleted);
        Task decode = model.DecodeAudioAsync();
        ui.Until(() => parser.Started == 2);

        Task other = model.LoadAsync(Fixtures.Mpeg("external-lame-cbr128-joint.mp3"));
        ui.Until(() => other.IsCompleted && decode.IsCompleted);

        Assert.False(model.IsDecoding);
        Assert.Equal("MPEG Audio", model.Report!.Root.Value);
        Assert.False(model.CanDecodeAudio);
    }
}

// A format of files that start with "VBLK" that gives one stream whose checksum was not compared; parsing a file the second time
// (the run that decodes) waits until the analysis is canceled.
internal sealed class DecodeBlockingParser : IFormatParser
{
    private int _started;
    private int _canceled;

    public static byte[] File => "VBLK and some bytes"u8.ToArray();

    public string Name => "DecodeBlocking";

    public int Started => Volatile.Read(ref _started);

    public int Canceled => Volatile.Read(ref _canceled);

    public SniffResult Sniff(SniffContext context) => context.Reader.Length >= 4 && context.Reader.MatchesAt(context.Reader.Start, "VBLK"u8) ? new(100, context.Reader.Start) : SniffResult.None;

    public void Parse(ParseContext context)
    {
        int number = Interlocked.Increment(ref _started);
        context.AddStream(new StreamInfo
        {
            Nodes = [context.Root],
            Codec = "Test",
            Verification = new StreamVerification
            {
                Decoding = DecodeStatus.NotRequested,
                Checksums = [new ChecksumTally { Name = "test MD5", Covers = ChecksumCoverage.DecodedAudio, CoversAllAudio = true, Stored = 1 }],
            },
        });
        if (number < 2)
        {
            return;
        }

        try
        {
            context.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            context.CancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _canceled);
            throw;
        }
    }

    public Analyzer Analyzer() => new(DefaultParsers.Tags, [this, .. DefaultParsers.Formats], DefaultParsers.Codecs);
}
