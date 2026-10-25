namespace Encspect.Tests;

// What the thread-safety tests cover on a pull request and what they cover in the nightly run. The fixture-wide
// tests read the whole corpus in every mode: a sample of it once dropped the three DTS files that exposed a
// race. What differs is the cost of writing long reports on several threads while collections run:
// ENCSPECT_FULL_THREAD_TESTS=1 (the nightly workflow) writes every long input with all three writers on four
// threads and uses the full thread counts. The hammer (LongListReadTests) and the block list tests run in full
// on every run.
internal static class ThreadTestScope
{
    public const string FullVariable = "ENCSPECT_FULL_THREAD_TESTS";

    // One input per list implementation, with DTS (its own list builder, and the frame nodes whose CRC verdicts a
    // second build once read as "not checked") and E-AC-3 (the most reliable catch of a frame-syntax state shared
    // between threads). The Ogg codecs, Musepack stream versions and the MP4 copies of the AC-3 and DTS inputs are
    // left to the nightly run where a test writes or rebuilds long reports; the hammer and the input release
    // tests read all of them on every run, and writing the costly ones on several threads takes minutes.
    private static readonly string[] PullRequestLongInputs =
        ["mpeg-layer3-frames", "adts", "ac3", "ogg-vorbis", "asf-wma-pro", "wavpack", "flac", "monkeys-audio", "musepack-sv8", "mp4-aac", "adif", "dts", "eac3", "matroska", "truehd", "wav", "aiff"];

    // The AC-3 input has the most nodes by far (about 450,000), and writing it on several threads while
    // collections run takes over half a minute; its fixtures are written that way by ParallelAnalysisTests.
    private const string CostliestToWrite = "ac3";

    public static bool Full { get; } = Environment.GetEnvironmentVariable(FullVariable) == "1";

    public static TheoryData<string> LongInputNames()
    {
        var data = new TheoryData<string>();
        foreach (string name in LongInputs.Names.Where(n => Full || PullRequestLongInputs.Contains(n)))
        {
            data.Add(name);
        }

        return data;
    }

    public static TheoryData<string> LongInputNamesToWrite()
    {
        var data = new TheoryData<string>();
        foreach (string name in LongInputs.Names.Where(n => Full || (PullRequestLongInputs.Contains(n) && n != CostliestToWrite)))
        {
            data.Add(name);
        }

        return data;
    }
}
