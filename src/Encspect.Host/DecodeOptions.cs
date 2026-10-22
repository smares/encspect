using Encspect.Core;

namespace Encspect.Host;

// What a front end asks of decoding (AnalysisOptions.Decoding): every stream whose codec has a decoder is decoded while
// it is read, and the checksums over its decoded audio are compared. See docs/design/decoders.md.
public sealed class DecodeOptions
{
    // The default PCM budget in bytes of the canonical layout is the larger of this and BudgetPerInputByte times the
    // size of the file.
    public const long MinimumBudget = 16L << 30;

    public const long BudgetPerInputByte = 256;

    // Stops decoding only: the structure analysis carries on, and the report says how far decoding got
    // (decode.stopped). AnalysisOptions.CancellationToken still cancels the whole analysis.
    public CancellationToken Stop { get; init; }

    // Where each decoded stream's PCM goes, asked once per stream before its first block; null, or a null return,
    // only checks. An exception from it or from the sink it returns ends the decoding and makes Analyze throw it once
    // the format parser has returned.
    public Func<DecodedStream, IPcmSink?>? Output { get; init; }

    // The analysis's PCM budget in bytes of the canonical layout; null for the default (the larger of 16 GiB and 256
    // times the file's size). Tests and the fuzzer lower it. An option of the call, not a setting.
    public long? MaxPcmBytes { get; init; }
}

// The stream a front end's sink is asked for: the codec ID the parser started the session with, the parser's node of the
// stream (DecodeSetup.Stream) and the PCM the decoder will write.
public sealed record DecodedStream(string CodecId, Node Stream, PcmFormat Format);
