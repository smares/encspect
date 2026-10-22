namespace Encspect.Core;

// The decoder of one coding. One instance serves every analysis, also on several threads at once: it keeps no state
// in fields, and Open returns the state of one stream. Like a parser it throws only EndOfStreamException,
// InvalidDataException and OperationCanceledException. A decoder lives in the project of its format, in the namespace
// <project>.Decoding, which parser code never references; the host registers it (docs/design/decoders.md).
public interface IDecoder
{
    // The codec ID the parsers start sessions with, as ICodecParser.Id: "flac", "monkeys-audio", "truehd".
    string Id { get; }

    // Reads the configuration and returns the decoder of one stream. InvalidDataException means the stream cannot be
    // decoded as configured (the report says why); the parser has reported the configuration's faults already.
    IFrameDecoder Open(DecodeContext context);
}

// The decoding state of one stream in one analysis. The host calls it on one thread, frame by frame, and always ends it
// with Finish, also when the parser does not (see "Ending sessions" in the design). Its buffers are its own arrays,
// dropped with it.
public interface IFrameDecoder
{
    // Decodes one frame, checks what the frame stores about its decoded audio (a Monkey's Audio frame CRC), and returns
    // the samples per channel by which the stream's decoded output advanced: 0 for a frame it could not decode, 0 for
    // the blocks of a WavPack group before its final block and the group's samples on that block, and for a WMA Lossless
    // packet the samples of the frames that end in it. The frame's bytes are valid only during the call.
    int Decode(in CodedFrame frame);

    // Ends the stream: compares the checksums over the whole stream (FLAC's MD5) where end.IsComplete, and returns what it
    // compared, by the names the parser declares them with. A result whose name the parser did not declare, or whose
    // Checked exceeds the declared Stored, is a decoder bug: the host reports decode.failed and drops the results.
    IReadOnlyList<ChecksumResult> Finish(DecodeEnd end);
}

// What a decoder compared of one checksum the parser declared (ChecksumTally.Name): the values it checked, and those of
// them that differ.
public sealed record ChecksumResult(string Name, long Checked, long Failed);

// How the stream ended, as the host knows it. IsComplete: the decoder decoded every frame the parser counted, no frame
// followed a gap, and the samples decoded equal the count the parser stated (DecodeSetup.Samples), so a checksum of the
// whole stream can be compared. FramesNotDecoded: the frames the decoder was handed and could not decode.
public readonly record struct DecodeEnd(bool IsComplete, long FramesNotDecoded);
