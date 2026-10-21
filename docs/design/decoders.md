# Decoders: the contract for phase 3

Status: draft for the project lead's sign-off (October 2026). Nothing here is implemented, and no format agent starts on a decoder before the sign-off. The decisions that need it are collected under [Decisions for sign-off](#decisions-for-sign-off); claims that no source read for this design confirms are marked **unverified**.

## Scope

What phase 3 brings, from the rollout table of the [plan](../PLAN.md):

- FLAC, WAV and AIFF;
- ALAC in MP4 and WMA Lossless in ASF;
- Monkey's Audio and TTA;
- Dolby TrueHD and MLP frame by frame;
- "lossless decoders that verify the stored checksums (WavPack MD5 and DSD, FLAC, ALAC, Monkey's Audio, TTA, WMA Lossless, Dolby TrueHD and MLP)".

What the plan already settles, and this design keeps:

- Decoding is optional, per format. Formats without a decoder still get the full structure report.
- Decoders are separate and optional, so no parser ever depends on one.
- No bundled decoders or native audio libraries: every decoder is Encspect's own managed code.
- No playback, and no settings below the front ends: options reach the analysis through the host's public API.

What is not in Gate 3:

- Waveform, spectrogram and lowpass detection. The plan puts "decoders for waveform and spectrogram" under "Later" and the waveform panels in the GUI section, not in a phase. This design defines the PCM path only far enough that those features need no contract change (see [The PCM path](#the-pcm-path)).
- Decoders for lossy codings.

Sources read for this design:

- The code: `Core.Abstractions` (parser interfaces, contexts and routes, `MappedBytes`, `BitReader`, `StreamInfo`), the host (`Analyzer`, `ParserRouter`, `AnalysisOptions`, the writers), the WavPack parser, the MP4, Ogg and ASF routes, the WMA codec parsers, and the tests (fixtures, goldens, memory, complexity, fuzz and thread tests).
- RFC 9639 (FLAC), sections 8.2, 9.1.8, 9.3 and 11.
- FFmpeg 6.1.1, the release `ffmpeg_sources.py` pins: `flac.c`, `flacdec.c`, `flacenc.c`, `apedec.c`, `libavformat/ape.c`, `tta.c`, `libavformat/tta.c`, `mlpdec.c`, `alac.c`, `wmalosslessdec.c`.
- libwavpack 5.6.0, the release `wavpack_tool.py` pins: `unpack.c`, `unpack_dsd.c`, `open_utils.c`, `cli/wvunpack.c`.

## The design in short

- A decoder is a project of its own, `Encspect.Decoders.<Format>`, that references `Core.Abstractions` and the one format project whose syntax it decodes. Parsers never reference a decoder.
- The parser owns every bit of the coded syntax; the decoder reads the bits with the parser's own code and adds only what turns them into samples: prediction, channel decorrelation, matrixing, output shifts, and the checksums over decoded audio.
- A parser hands each frame to a decode session through a new route on its context, `StartDecoding`, in the same pass in which it reads the frame. The host serves the route: it finds the decoder, runs it inside an exception boundary and a work budget, and ends the session with the stream's verdict.
- Checksums over coded bytes stay with the parser and are checked in every analysis; checksums over decoded audio need the decoder. Both end up in one record on the stream, `StreamInfo.Verification`, with a tri-state `IsVerified`.
- Decoding is off unless a front end asks for it: `AnalysisOptions.Decoding` in the library, `--verify` in the CLI, a "Verify Audio" command in the GUI's detail window. The file list never decodes, and no verdict is cached.
- Decoding never changes the tree: it adds findings and the stream's verification, nothing else.

## Where decoders live

### Options considered

- **A. A decoder project that references only `Core.Abstractions`.** The cleanest dependency graph, but the decoder must read the coded syntax again: FLAC's subframe headers, Rice partitions and coefficients, ALAC's elements, TrueHD's restart headers, decoding parameters and Huffman-coded block data. That is two readings of the same bits, which can drift apart: the parser says a subframe ends at one bit and the decoder reads another.
- **B. The decoder inside the format project.** One reading of the bits, no new projects. But the parser and the decoder share an assembly, so the rule "parsers never reference a decoder" can no longer be checked from the project files, and a front end or a third party cannot leave the decoders out of a build.
- **C. A decoder project that references `Core.Abstractions` and its own format project (recommended).** The format project grants it `InternalsVisibleTo`, so the decoder calls the parser's internal syntax readers; the dependency points from the decoder to the parser, never back. One reading of the bits, and a parser change that breaks the decoder fails the build instead of drifting.

**Decision:** C.

### Reference rules

- `Encspect.Formats.*` and `Encspect.Tags.*` reference `Core.Abstractions` only, as today.
- `Encspect.Decoders.<Format>` references `Core.Abstractions` and exactly one `Encspect.Formats.*` project, the one whose coding it decodes (`Encspect.Decoders.Flac` references `Encspect.Formats.Flac`; a WMA Lossless decoder would reference `Encspect.Formats.Wma`). It references no other decoder.
- Only the host references decoder projects. The front ends reach decoding through the host's public API.
- A test (`ProjectReferenceTests`) reads every `.csproj` and fails on any other reference, so the rule holds without a review.

The rules need two lines in `CLAUDE.md`, in "Layout" and "Architecture rules"; they are proposed under [Rollout](#rollout) and applied only after the sign-off.

### Wiring

- A decoder implements `IDecoder` (below), whose `Id` is the codec ID the parsers route by: `flac`, `monkeys-audio`, `wavpack`, `truehd`, `mlp`, and later `alac`, `tta`, `wma-lossless`, `pcm`. A raw format parser uses the same ID for its coding (the FLAC format parser and the FLAC codec parser that Ogg, MP4 and Matroska reach both start sessions with `flac`).
- `DefaultParsers.Decoders` lists them, next to `Codecs`.
- `Analyzer` gets a constructor that takes the decoders after the codec parsers; `Analyzer.CreateDefault()` passes `DefaultParsers.Decoders`. Two decoders with one ID are an `ArgumentException`, as two codec parsers are.
- An analyzer built without decoders works as today: every session reports `NoDecoder`.

## The interface

### Who owns the bits

- The parser owns the whole coded syntax and the code that reads it, including entropy-coded data it walks to find where a part ends. FLAC is the case in point: the header of channel 2's subframe lies after channel 1's residual, so the FLAC parser reads every Rice partition to its end anyway, and the frame's CRC-16 confirms where the walk ends.
- That code takes an optional destination. Given none, it only walks the bits and counts what the structure report shows; given buffers, it also stores warm-up samples and residuals. One function, two uses:

```csharp
// In Encspect.Formats.Flac, internal and visible to Encspect.Decoders.Flac.
internal static class SubframeSyntax
{
    // Reads one subframe from the bit after the frame header (RFC 9639, section 9.2). With samples empty it walks the bits
    // and fills facts for the structure report; with room for the block it also stores the warm-up samples and the residual.
    public static SubframeFacts Read(ref BitCursor bits, in FrameHeader header, int channel, Span<int> samples);
}
```

- The decoder adds what the structure report never needs: restoring samples from the predictor, undoing left/side, right/side and mid/side coding, wasted bits, and the MD5. It never reads a field with code of its own.
- A coding whose entropy-coded data the parser does not walk (Monkey's Audio's range coder, whose frames the seek table delimits) keeps its entropy decoder in the decoder project; the parser still owns the header, seek table and frame start fields, which the decoder reads with the parser's code.
- The configuration reaches the decoder as the bytes the parser read it from (`DecodeSetup.Configuration`), and the decoder reads them with the parser's own reader: a few dozen bytes read twice by one piece of code, not two readings.
- Guards against drift beyond the shared code: the fixture tests check that the decoder ends every frame at the bit where the parser's walk ends, and the bit-exact cross-checks (below) catch a misreading the two would share.

### Options considered for the data flow

- **A second pass driven by the host** from the parser's frame records, after the parse. The structure pass would stay exactly as it is, but it reads the file twice, a codec parser inside a container gets its packets once (`CodecContext.Packets` is enumerated once) and would have to keep every packet's file ranges, and decompressed Matroska frames do not map back to file bytes at all.
- **The decoder walks the file itself.** It would have to repeat the parser's sync, resync and damage decisions, so it could decode other frames than the report shows.
- **A session the parser feeds while it reads (recommended).** The parser already holds each frame's bytes when it parses it; it hands the same bytes on. One read of the file, the same frames as the report, and the same code path for raw files and for every container, since a codec parser inside MP4, Matroska or Ogg holds its packets the same way.

**Decision:** the session. The cost is that the entropy-coded data is decoded twice when decoding is on: once by the parser's walk, once by the decoder's read into buffers. Decoding is off by default, and the measurement in the first pull request (see [Rollout](#rollout)) says whether that matters.

### Types in Core.Abstractions

All of these are in the namespace `Encspect.Core`.

The decoder, shared by every analysis and thread like a parser, and its per-stream state:

```csharp
// The decoder of one coding. One instance serves every analysis, also on several threads at once: it keeps no state
// in fields, and Open returns the state of one stream. Like a parser it throws only EndOfStreamException,
// InvalidDataException and OperationCanceledException.
public interface IDecoder
{
    // The codec ID the parsers start sessions with, as ICodecParser.Id: "flac", "monkeys-audio", "truehd".
    string Id { get; }

    // Reads the configuration and returns the decoder of one stream. InvalidDataException means the stream cannot be
    // decoded as configured (the report says why); the parser has reported the configuration's faults already.
    IFrameDecoder Open(DecodeContext context);
}

// The decoding state of one stream in one analysis. The host calls it on one thread, frame by frame, and disposes it
// at the end of the analysis, finished or not, so pooled buffers go back.
public interface IFrameDecoder : IDisposable
{
    // Decodes one frame and checks what the frame stores about its decoded audio (a Monkey's Audio frame CRC). The
    // frame's bytes are valid only during the call.
    void Decode(in CodedFrame frame);

    // Ends the stream: compares the checksums over the whole stream (FLAC's MD5) where end.IsComplete, and returns a
    // tally for every checksum over decoded audio the stream stores, compared or not.
    IReadOnlyList<ChecksumTally> Finish(DecodeEnd end);
}
```

What the parser states when it starts a session, and what it hands per frame:

```csharp
public sealed record DecodeSetup
{
    // The parser's node of the stream, the first of StreamInfo.Nodes: findings about the whole stream point to it.
    public required Node Stream { get; init; }

    public required int Channels { get; init; }

    // The samples per channel the parser counts in the stream, or null where it cannot tell.
    public long? Samples { get; init; }

    // The configuration as the parser read it (FLAC's STREAMINFO block, ALAC's magic cookie, a TTA header), which
    // the decoder reads with the parser's code; null for a coding whose frames carry it (TrueHD's major sync).
    public MappedBytes? Configuration { get; init; }

    // The WAVEFORMATEX a container states (ASF's WMA Lossless, WAV's PCM), or null.
    public AudioFormat? Format { get; init; }
}

// The unit the coding decodes one at a time, as the parser read it: a frame, a block, an access unit; a packet where
// the coding's frames are not byte-aligned (WMA Lossless), whose decoder then finds the frames with the parser's code.
public readonly record struct CodedFrame(MappedBytes Bytes, string NodeName, long FirstSample, int Samples)
{
    // Bytes, NodeName and the file range of Bytes name the parser's node of the frame (equal in name, offset and
    // length), which the host builds for a finding only when one is reported.

    // The frame's position in the stream in samples per channel, and how many the parser says it codes.

    // The parser found the frame damaged: a checksum over its coded bytes fails, or the data ends inside it. The decoder
    // still decodes what it can, so a whole-stream checksum then says the audio differs.
    public bool IsDamaged { get; init; }

    // Frames the parser counted in the stream were lost before this one (a gap, a resync), so the decoder resets
    // what it carries from frame to frame and the whole-stream checksums cannot be compared.
    public bool FollowsGap { get; init; }
}

public readonly record struct DecodeEnd(bool IsComplete, long FramesNotDecoded);
```

The context the decoder reports through:

```csharp
public sealed class DecodeContext
{
    public DecodeSetup Setup { get; }

    // The analysis's token: canceling throws OperationCanceledException. The host checks it between frames; a decoder
    // whose single frame can take more than about 100 ms (Monkey's Audio at its highest levels) checks it every few
    // thousand samples inside the frame.
    public CancellationToken CancellationToken { get; }

    // Findings on the parser's nodes or on equal ones; findings of one kind are reported once per stream with a count.
    public void AddFinding(Severity severity, string code, string message, Node? node = null, int count = 1);

    // Called once, before the first block: the PCM the decoder will write, and the sink the front end wants it in, or
    // null where it only checks. A coding that decodes to something other than PCM (WavPack's DSD) does not call it.
    public IPcmSink? DeclareOutput(PcmFormat format);
}
```

The parser's side of the route, on `AnalysisContext` and served by the host through `IParserRouter`:

```csharp
public abstract class AnalysisContext
{
    // Starts the decoding of one stream by the decoder with that codec ID. Never null: where decoding was not asked for,
    // or no decoder has the ID, the session decodes nothing and its Finish says so.
    public IDecodeSession StartDecoding(string codecId, DecodeSetup setup);
}

public interface IDecodeSession
{
    // False where nothing is decoded (not asked for, no decoder, stopped): the parser then builds no CodedFrame.
    bool IsDecoding { get; }

    // Hands the next frame to the decoder, in stream order. Returns IsDecoding afterward: false once the budget, the
    // front end's stop or a decoder failure ended the session.
    bool Decode(in CodedFrame frame);

    // Ends the session, once: merges the parser's tallies of checksums over coded bytes with the decoder's, and returns
    // the verification the parser puts on its StreamInfo.
    StreamVerification Finish(IReadOnlyList<ChecksumTally> codedChecksums);
}

public interface IParserRouter
{
    IDecodeSession StartDecoding(AnalysisContext caller, string codecId, DecodeSetup setup);
}
```

A fast bit reader for loops over every coded bit, since `BitReader` is a class meant for header fields:

```csharp
// Reads bits most significant first from a span through a 64-bit window, without allocating. Past the end it reads
// zero bits and sets IsOverrun, as BitReader does. LsbBitCursor has the same members for least significant first
// (TTA, WavPack).
public ref struct BitCursor
{
    public BitCursor(ReadOnlySpan<byte> data);

    public readonly int Position { get; }   // in bits

    public readonly int Length { get; }

    public readonly bool IsOverrun { get; }

    public uint Read(int count);            // 0 to 32 bits

    public ulong ReadLong(int count);       // 0 to 64 bits

    public int ReadSigned(int count);       // 1 to 32 bits, two's complement

    public bool ReadBit();

    // Counts the zero (or one) bits up to the next bit of the other value, which it consumes. It stops after limit bits
    // or at the end of the data (IsOverrun), and its time grows with the bits it consumes, a word at a time.
    public int CountZeros(int limit);

    public int CountOnes(int limit);

    public void Skip(int count);

    public void AlignToByte();
}
```

An MD5 in managed code, for FLAC, WavPack and the tests:

```csharp
// MD5 (RFC 1321). System.Security.Cryptography's MD5 calls the system's OpenSSL on Linux, which is a native dependency
// the analysis does not have otherwise and which a system in FIPS mode refuses.
public sealed class Md5
{
    public void AppendData(ReadOnlySpan<byte> data);

    public void GetHashAndReset(Span<byte> destination);   // 16 bytes
}
```

### What a parser does

The FLAC format parser, in outline; a codec parser does the same with the packets a container hands it.

```csharp
IDecodeSession decoding = context.StartDecoding("flac", new DecodeSetup
{
    Stream = framesNode,
    Channels = info.Channels,
    Samples = info.TotalSamples == 0 ? null : info.TotalSamples,
    Configuration = info.Bytes,
});

foreach (frame in the frames the parser reads)
{
    // ... the header, its CRC-8, the subframes, the CRC-16, as without decoding ...
    if (decoding.IsDecoding)
    {
        decoding.Decode(new CodedFrame(new MappedBytes(frameBytes, frameOffset), FrameNames.Frame, firstSample, header.BlockSize)
        {
            IsDamaged = !crc16Holds || isCut,
            FollowsGap = followsGap,
        });
    }
}

StreamVerification verification = decoding.Finish(
[
    new ChecksumTally { Name = "frame header CRC-8", Covers = ChecksumCoverage.CodedData, Checked = frames, Failed = headerCrcFailures },
    new ChecksumTally { Name = "frame CRC-16", Covers = ChecksumCoverage.CodedData, Checked = wholeFrames, Failed = frameCrcFailures, NotChecked = cutFrames },
]);
context.AddStream(stream with { Verification = verification });
```

Rules for the parser:

- It hands every frame it counts in the stream, damaged ones included, in stream order, and nothing it does not count (junk, gaps).
- It keeps nothing for the decoder: no record, no bytes. The session works on the frame while the parser holds it.
- Its samples, duration and statistics come from its own reading, never from the decoder. A frame that decodes to another number of samples than its header says is the decoder's finding.

## Checksums and verdicts

### What each format stores

"Coded data" means the checksum covers the coded bytes, so the parser checks it in every analysis; "decoded audio" means it covers the samples, so only the decoder can.

| Format | Checksum | Covers | Checked by | Source | Status |
| --- | --- | --- | --- | --- | --- |
| FLAC | Frame header CRC-8 (x^8 + x^2 + x + 1, start 0, the header from the sync code) | coded data | parser | RFC 9639, 9.1.8 | verified |
| FLAC | Frame CRC-16 (x^16 + x^15 + x^2 + 1, start 0, the frame before it) | coded data | parser | RFC 9639, 9.3 | verified |
| FLAC | STREAMINFO MD5 of the samples interleaved, signed, little-endian, sign-extended to whole bytes; 0 means not known | decoded audio | decoder | RFC 9639, 8.2; FFmpeg skips it (`flac.c` 221) and its encoder writes that layout (`flacenc.c`, `update_md5_sum`) | verified |
| Monkey's Audio | Frame CRC: the CRC-32 of FFmpeg's `AV_CRC_32_IEEE_LE` table over the decoded samples' bytes in channel-interleaved order (24-bit samples as 3 bytes), inverted and shifted right by one; from 3.82 on, bit 31 of the stored word flags frame flags instead | decoded audio | decoder | `apedec.c` 765-779 and 1694-1723 | verified for file versions 3.90 and later, the only ones FFmpeg compares; earlier versions **unverified** |
| Monkey's Audio | MD5 in the descriptor (3.98 and later) | **unverified**: believed to cover coded data (header, seek table, frame data), which would make it the parser's | parser, if confirmed | FFmpeg reads it (`ape.c` 177) and never compares it; Monkey's Audio's SDK not read | **unverified** |
| TTA | Header CRC-32 over the header before it | coded data | parser | `libavformat/tta.c` 62-85 | verified |
| TTA | Seek table CRC-32 | coded data | parser | `libavformat/tta.c` 118-133 | verified |
| TTA | Frame CRC-32 over the frame's bytes before it | coded data | parser | `tta.c` 76-87, 239-243 | verified |
| TrueHD, MLP | Major sync CRC-16, the parity nibble, restart header CRC-8, substream parity byte and CRC-8 | coded data | parser | plan, "Phase 3: Dolby TrueHD and MLP" | as the plan states |
| TrueHD, MLP | Lossless check: the XOR of the output samples since the last restart header, folded to 8 bits, compared at the next one | decoded audio | decoder | `mlpdec.c` 586-593, `mlpdsp.c` | verified; FFmpeg compares it for the highest decoded substream only (`max_decoded_substream`, at most 2); whether the lower substreams' checks can be compared is **unverified** |
| WAV, AIFF | none | | | | |
| ALAC | none | | | `alac.c` has no check; Apple's source not read | verified as far as FFmpeg |
| WMA Lossless | none known | | | `wmalosslessdec.c` has no check | whether the bitstream holds one is **unverified** |
| WavPack | Block checksum sub-block (5.0 and later) | coded data | parser (done) | plan, "Phase 2: WavPack" | verified |
| WavPack | Block header CRC: `crc * 3 + sample` per channel; without the correction file over the samples the main file decodes to (lossy in hybrid mode), with it the correction block's header holds the one compared | decoded audio | decoder | `unpack.c` 77-502, 812-826; `open_utils.c` 1043-1044 | verified |
| WavPack | Extended CRC of floats and of integers above 24 bits (the extra bitstream) | decoded audio | decoder | `unpack.c` 707-726, 821 | verified |
| WavPack | MD5 sub-block over the samples in the source's stored layout, which `wvunpack` compares only for lossless decoding | decoded audio | decoder | `wvunpack.c` 1996, 2157-2159 | verified |
| WavPack | DSD: the block header CRC over the decoded DSD bytes and the MD5 over them | decoded audio | decoder | `unpack_dsd.c` 86-441, `wvunpack.c` 2306 | verified |

What the table means for the plan's list:

- ALAC and WMA Lossless store no checksum, and TTA's checksums all cover coded data, which its parser checks. Decoding them proves only that every frame decodes to its length; no stored checksum gets checked.
- The decoders that check something are FLAC, Monkey's Audio, WavPack, and TrueHD and MLP.
- **Decision:** move the ALAC, TTA and WMA Lossless decoders to "Later", with the waveform, and keep the four above in Gate 3. The alternative is to keep all seven as the plan row lists them.

### The verification record

```csharp
public enum ChecksumCoverage
{
    CodedData,
    DecodedAudio,
}

// One kind of checksum a stream stores, and how its stored values compared.
public sealed record ChecksumTally
{
    // The format's name for it, as its field is named: "frame CRC-16", "STREAMINFO MD5", "lossless check".
    public required string Name { get; init; }

    public required ChecksumCoverage Covers { get; init; }

    // Stored values compared, those that differ, and those not compared (decoding off or stopped, a frame not decoded,
    // a frame the data cuts short).
    public long Checked { get; init; }

    public long Failed { get; init; }

    public long NotChecked { get; init; }
}

public enum DecodeStatus
{
    // A decoder exists, but the front end did not ask for decoding.
    NotRequested,

    NoDecoder,

    // The stream cannot be decoded as it is (TTA's encrypted format, a configuration the decoder refuses).
    NotPossible,

    // Every frame handed was decoded, or reported where it could not be.
    Decoded,

    // The front end stopped decoding or the work budget ran out.
    Stopped,

    // The decoder failed (decode.failed), a bug.
    Failed,
}

public sealed record StreamVerification
{
    public required DecodeStatus Decoding { get; init; }

    public long DecodedSamples { get; init; }

    public IReadOnlyList<ChecksumTally> Checksums { get; init; } = [];

    // True where every stored checksum was compared and holds; false where one fails; null where some were not
    // compared or the stream stores none.
    public bool? IsVerified =>
        Checksums.Any(c => c.Failed > 0) ? false
        : Checksums.Count > 0 && Checksums.All(c => c.NotChecked == 0) ? true
        : null;
}

public sealed record StreamInfo
{
    // How far the checksums the stream stores vouch for it, for lossless codings; null for a coding that reports none.
    public StreamVerification? Verification { get; init; }
}
```

- "Verified" keeps the meaning it has in `SniffResult.VerifiedFrames`: a stored checksum was compared and holds. A stream is verified when every one of its stored checksums is. A TTA stream whose CRCs all hold is therefore verified without decoding, a FLAC stream only once its MD5 is compared, unless its MD5 is 0.
- **Decision:** this meaning, and `Verification` for lossless codings only in Gate 3. The alternatives are to count only checksums over decoded audio, or to give the lossy formats' CRCs (MPEG, AC-3, Ogg pages) a verification now.
- A whole-stream checksum (FLAC's and WavPack's MD5) is compared only where the decoder decoded every frame the parser counted, without a gap, and the samples equal the stream's count (`DecodeEnd.IsComplete`). Otherwise it is not compared, and the finding that explains why is the one that stopped it.

### Findings

Every code follows the plan's severity rules: a stored checksum that differs is a warning (the file holds something damaged), a check that could not be made for a reason reported separately is an info, a decoder that fails is an error.

| Code | Severity | Points to | When |
| --- | --- | --- | --- |
| `flac.md5-mismatch` | Warning | the STREAMINFO MD5 field | the decoded audio's MD5 differs from the stored one |
| `monkeys-audio.frame-crc-mismatch` | Warning | the frame's CRC field | the frame's decoded audio does not have its stored CRC; once per stream with a count |
| `wavpack.audio-crc-mismatch` | Warning | the block header's CRC field | a block's decoded audio does not have its header CRC (the extended CRC included); once per stream |
| `wavpack.md5-mismatch` | Warning | the MD5 sub-block | lossless decoding gives another MD5 |
| `wavpack.md5-needs-correction-file` | Info | the MD5 sub-block | a hybrid file decoded without its `.wvc`, whose MD5 is of the lossless audio |
| `truehd.lossless-check-failed` | Warning | the restart header's lossless check byte | the output since the previous restart header does not give it; once per stream |
| `<format>.decoded-samples-differ` | Warning | the frame | a frame decodes to another number of samples than its header gives |
| `decode.frame-invalid` | Warning | the frame | the decoder could not decode a frame (`InvalidDataException`, or data that ends inside its coded audio); once per stream with a count; decoding carries on with the next frame |
| `decode.no-decoder` | Info | the stream | decoding was asked for and no decoder has the stream's codec ID |
| `decode.not-possible` | Info | the stream | the decoder refused the configuration; the message says why |
| `decode.stopped` | Info | the stream | the front end stopped decoding; the message says how many frames were decoded |
| `decode.limit` | Info | the stream | the work budget ran out (see below) |
| `decode.failed` | Error | the stream | any other exception from the decoder: a bug, never an acceptable result, like `parse.failed`; that stream's decoding stops |

- The decoder builds the node of a mismatch with the parser's internal node code (it references the parser's project), equal in name, offset and length to the one the parser builds, so the host resolves its path as for any finding.
- The computed value goes into the finding's message ("computed 3e84…, stored 9a1f…"). Decoding adds no node, so the tree is the same with decoding and without it, which a test holds for every fixture.

### When decoding is skipped

- The stream's verification says `NotRequested` (or `NoDecoder`), and lists the checksums over decoded audio with `NotChecked` set, so the report says that an MD5 exists and was not compared.
- No finding is added: a default analysis of a FLAC file stays as quiet as any other. The descriptions of the stored checksum fields say that they are compared when the stream is decoded.

### Where the verdict shows

- **JSON:** `verification` in each stream, after `protection`: `isVerified` (written where it is not null), `decoding` (`notRequested`, `noDecoder`, `notPossible`, `decoded`, `stopped`, `failed`), `decodedSamples`, and `checksums` with `name`, `covers` (`codedData`, `decodedAudio`), `checked`, `failed` and `notChecked`. A field is added, so `schemaVersion` stays 1.
- **Text:** a line under each stream: "Verified: yes (frame header CRC-8 40 of 40, frame CRC-16 40 of 40, STREAMINFO MD5 1 of 1)", "no (...)" or "not verified (STREAMINFO MD5 not compared: decoding was not asked for)".
- **CSV:** `Is Verified` after `Is Protected`: false where a stream's is false, true where every stream's is true, empty otherwise. `ReportSummary` gains the field, so the GUI's summary cache format becomes 6.
- **GUI:** the stream line of the detail window names the verdict and links to the article; the file list gets no column in Gate 3, since it never decodes and a column would show TTA's verdicts but never FLAC's.
- **Article:** `verification` in `src/Encspect.Host/Topics`: what stored checksums cover, coded data against decoded audio, why an MD5 needs decoding, what "verified" means in the report. The stored checksum fields of the lossless formats point to it.

## When decoding runs

### Cost

- Decoding reads every coded bit into buffers and writes every sample; the structure pass of a format that walks its entropy-coded data (FLAC, ALAC, TrueHD) does the first half already, and does it again when decoding is on.
- Decoding time grows with the samples decoded, with a constant per coding: FLAC's predictor uses at most 32 coefficients a sample, Monkey's Audio's highest level about 1,550 filter taps per sample of each channel (16 + 256 + 1,280, FFmpeg's `ape_filter_orders`), so Monkey's Audio decodes far slower than FLAC and its filters need `System.Numerics.Vector`.
- Nothing has been measured. The first pull request measures structure pass and decoding of an hour of CD audio in FLAC and records both in the plan.

### Defaults and how a front end asks

- **Library:** off. `AnalysisOptions` gets one property:

```csharp
public sealed class AnalysisOptions
{
    // Null (the default): nothing is decoded. Set: every stream whose codec has a decoder is decoded while it is read,
    // and the checksums over its decoded audio are compared.
    public DecodeOptions? Decoding { get; init; }
}

public sealed class DecodeOptions
{
    // Stops decoding only: the structure analysis carries on, and the report says how far decoding got
    // (decode.stopped). AnalysisOptions.CancellationToken still cancels the whole analysis.
    public CancellationToken Stop { get; init; }

    // Where each decoded stream's PCM goes, asked once per stream before its first block; null, or a null return,
    // only checks.
    public Func<DecodedStream, IPcmSink?>? Output { get; init; }
}

public sealed record DecodedStream(string CodecId, Node Stream, PcmFormat Format);
```

- **CLI:** off; `--verify` sets `Decoding`. `--fail-on warning` already turns a mismatch into a failing exit code.
- **GUI:** off. The detail window gets a "Verify Audio" command, enabled where a stream's verification says `NotRequested`. It analyzes the file again with decoding, with progress and Stop, and replaces the report when done; the tree is the same, so the selection stays.
- **File list:** never decodes. A folder of thousands of files would cost minutes of decoding per gigabyte, and the list's summaries stay what they are.
- **Summary cache:** never holds a verdict over decoded audio. Its key (size, last-write time, file ID and a hash of the first and last 64 KiB) does not see a byte changed in the middle of the file, which is exactly what a verification is about.
- **Decision:** off everywhere, with the commands above. The alternative is to decode by default in the detail window, which opens one file at a time.

### Cancellation, progress and partial results

- Decoding runs in the structure pass, so `AnalysisOptions.BytesRead` reports its progress as it does now; it just moves slower.
- `AnalysisOptions.CancellationToken` cancels everything, as now. `DecodeOptions.Stop` ends every open session at the next frame; the parse carries on to the end without decoding.
- A stopped session keeps what it found: a frame CRC that failed stays reported, the tallies count what was compared, and the whole-stream checksums say "not compared". The stream's `Decoding` is `Stopped`.

## Resources and robustness

### Memory

- Nothing grows with the stream: a session holds one frame's buffers and the state the coding carries from frame to frame (TrueHD's filter and matrix state, Monkey's Audio's filters). The report keeps only the tallies.
- Buffers come from `ArrayPool<T>.Shared`, sized by the frame at hand, never by a declared size alone, and are returned in `Dispose`.
- Each decoder has a fixed ceiling, beyond which it reports `decode.limit` and decodes nothing more of the stream:

| Coding | Largest frame | Ceiling |
| --- | --- | --- |
| FLAC | 65,535 samples, 8 channels, 32 bits (RFC 9639, 8.2) | about 2.6 MB, with 64-bit buffers for the side channel of 32-bit streams |
| Monkey's Audio | from 3.98 on a 32-bit count in the header; before, 9,216, 73,728 or 294,912 by version (`ape.c` 224-229); FFmpeg decodes 2 channels | a frame of at most 2^20 samples per channel |
| WavPack | under 196,608 samples a block (`read_next_header`); up to 4,096 channels across the blocks of one group | 2^24 samples for the blocks of one group, which the MD5 needs interleaved; past it the block CRCs are still compared, the MD5 is not |
| TrueHD, MLP | 160 samples, 8 channels (`mlp.h`) | negligible |
| ALAC, later | the configuration's frame length, which FFmpeg accepts up to 4096 × 4096 | 2^20 samples per channel |

### The exception boundary

The host's session wraps every call to the decoder:

- `InvalidDataException`, and `EndOfStreamException` from a frame's own bytes: `decode.frame-invalid` on the frame; the next frame is decoded.
- An exception from `Open`: `decode.not-possible` with its message.
- `OperationCanceledException` from the analysis's token passes through; the stop token ends the session with `decode.stopped`.
- Any other exception: `decode.failed`, an error naming the decoder and the exception; that session decodes nothing more, and the analysis carries on.
- Exceptions from the front end's `IPcmSink` are the front end's own and pass through to the caller of `Analyze`.
- The fuzzer fails on `decode.failed` as it does on `parse.failed`.

### Per-call state

- `IDecoder` keeps no state in fields; everything of a stream lives in the `IFrameDecoder` that `Open` returns.
- The host calls one session on the thread that parses its stream, and disposes every session at the end of the analysis.
- Nothing in the report refers to a decoder or a session: `StreamVerification` is plain data, so `InputReleaseTests` hold with decoding on.

### Complexity against crafted input

- **Unary codes and escapes:** `BitCursor.CountZeros` and `CountOnes` stop at the end of the frame's bytes, so a Rice code that claims gigabytes (RFC 9639, section 11) costs the bits it has.
- **Orders, partitions and channels:** every count is checked against the format's maximum before anything is allocated or looped over (FLAC: a predictor order of at most 32 and at most the block size, a partition order of at most 15 whose partitions fit the block; Monkey's Audio: the filter orders of its compression levels; TrueHD: `MAX_FIR_ORDER` 8, `MAX_IIR_ORDER` 4, `MAX_MATRICES` 8; WavPack: 16 decorrelation terms). A value past one is the parser's finding, and the decoder does not decode the frame.
- **Amplification:** a 49-byte FLAC frame codes more than 2 MB of PCM (RFC 9639, section 11), so a small file can code terabytes. The host counts the PCM bytes all sessions of an analysis write and stops every session with `decode.limit` once they pass the larger of 16 GiB and 256 times the file's size. Music coded losslessly gives a few bytes of PCM per coded byte (not measured here), an hour of digital silence in 8 channels of 24 bits at 192 kHz fits under the 16 GiB, and a crafted file of 100 MB stops after about 26 GB of PCM, a minute or so of hashing, instead of hours. **Decision:** these two numbers, to be tuned once measured; the alternative is no bound in the library, leaving the front ends' Stop as the only one.
- **Per-frame time:** a frame's work is bounded by the format's largest frame times its per-sample constant, so the host's check between frames bounds the latency of cancellation, and Monkey's Audio checks inside its frames.

## The PCM path

Defined now so that waveform, spectrogram and lowpass detection need no contract change. In Gate 3 only the tests read it.

```csharp
public enum SampleFormat
{
    // Signed integers in the low BitsPerSample bits of an int, sign-extended (8-bit WAV's unsigned samples converted).
    Integer,

    // IEEE 754 single precision, as WavPack's and WAV's floating-point samples are stored.
    Float,
}

public sealed record PcmFormat(int SampleRate, int Channels, int BitsPerSample, SampleFormat Format)
{
    // The speaker of each channel in the stream's channel order, which is the format's, as StreamInfo.ChannelLayout.
    public IReadOnlyList<Speaker>? ChannelLayout { get; init; }
}

// Samples of every channel for one stretch of the stream, one span per channel. Valid only during IPcmSink.Write.
public readonly ref struct PcmBlock
{
    // The first sample's position in the stream in samples per channel, before any gapless trimming; where frames were
    // lost, the next block's position jumps.
    public long Position { get; }

    public int Length { get; }

    public int Channels { get; }

    public ReadOnlySpan<int> Samples(int channel);

    public ReadOnlySpan<float> FloatSamples(int channel);
}

public interface IPcmSink
{
    // On the analysis thread, in stream order; a sink copies what it keeps.
    void Write(PcmBlock block);

    void End(long samples);
}
```

- Planar, because waveform and spectrogram read one channel at a time; 32-bit integers, because every lossless coding in phase 3 fits (FLAC's 33-bit side channel is internal to the decoder).
- Gapless trimming (TrueHD's samples to drop, an MP4 edit list) is the report's business, not the PCM's: positions are untrimmed, and a later feature trims with what the stream reports.
- `Core.Abstractions` also gets the canonical byte layout as one function, interleaved, signed, little-endian, `ceil(bits / 8)` bytes a sample: FLAC's MD5 and Monkey's Audio's CRC use it, and so do the tests' hashing sink and the manifests' `decoded_pcm_md5`.

## Verifying the decoders

### Fixtures and manifests

- Every fixture of a decoded coding gets `decoded_samples` and `decoded_pcm_md5` (the canonical layout above) in its manifest, and the verification the analyzer must report.
- The fixture scripts compute them from a reference decoder's output, never from the analyzer; the cross-check job recomputes them with a second decoder where one exists.
- `DecoderFixtureTests` decodes every such fixture with a hashing sink and holds the analyzer to the manifest, and checks that the tree equals the one of the analysis without decoding.
- **Damage that only the decoded audio shows**, hand-built for each coding: a residual bit flipped with the frame's coded checksums recomputed, so every CRC holds and only the MD5, the Monkey's Audio frame CRC, the WavPack header CRC or the TrueHD lossless check fails. A stored MD5 changed. A frame cut short, so the whole-stream checksum is not compared. The reference tool must agree with each verdict: `flac -t` reports the MD5, FFmpeg with `-err_detect crccheck` the Monkey's Audio CRC, FFmpeg's log the lossless check, `wvunpack` the CRC errors and MD5.

### Reference decoders

| Coding | Reference | In Ubuntu |
| --- | --- | --- |
| FLAC | `flac -d`, `flac -t`, `metaflac --show-md5sum`; FFmpeg | `flac` is in 24.04 (1.4.3) and installed by no CI job yet; FFmpeg 8.0.1 is in 26.04 |
| Monkey's Audio | Monkey's Audio's `mac`; FFmpeg | no package in 24.04; 26.04 **unverified**; else built from the SDK's pinned source (license **unverified**) |
| WavPack | `wvunpack -m`, libwavpack's own pass (`wavpack_blocks.c`, already built); FFmpeg | `wavpack` 5.9.0 in 26.04 |
| TrueHD, MLP | FFmpeg's decoder; FFmpeg's encoder writes fixtures | FFmpeg 8.0.1 |
| ALAC, TTA, WMA Lossless (later) | FFmpeg; Apple's ALAC source and the TTA reference coder if built | FFmpeg 8.0.1; `ttaenc` not in 24.04, 26.04 **unverified** |

- FFmpeg's lossless decoders are bit-exact, which the ASF cross-check already relies on for WMA Lossless.
- Each script asserts the tool versions it was written for (`tool_versions.py`), as the existing cross-checks do.

### Tests beyond the fixtures

- **Contract:** `DecodeRouteTests` with a scripted parser and decoder, as `FormatDetectionTests` scripts parsers: the boundary for each exception, the stop token, the budget, findings counted once per stream, sessions inside nested routes, disposal of unfinished sessions.
- **Bit cursor and MD5:** `BitCursorTests` against `BitReader` on random data and at every end; `Md5Tests` with RFC 1321's vectors and against `System.Security.Cryptography.MD5`, in the test only.
- **References:** `ProjectReferenceTests` (above).
- **Fuzzing:** `Encspect.Fuzz` analyzes the lossless fixtures' mutations with decoding on as well, and fails on `decode.failed`; its finds join `tests/fixtures/fuzz`.
- **Memory:** `MemoryTests` analyzes a long stream of each decoded coding with decoding on and bounds the peak to that of the analysis without it plus a constant, so a session that keeps something per frame fails.
- **Complexity:** `ComplexityTests` times FLAC frames of 65,535-sample constant subframes in 8 channels at 32 bits (the budget must stop them), Rice codes that run to the end of each frame, predictor order 32 at the largest block, and the highest Monkey's Audio level.
- **Threads:** `ParallelAnalysisMatchTests` compares parallel and sequential decoding of the lossless fixture directories.
- **Golden reports:** the default golden of each fixture stays as it is. A small `<name>.decode.json` holds what decoding adds (the verification and the decode findings), compared on every run and rewritten with `ENCSPECT_UPDATE_GOLDEN=1`, so the full report is not stored twice.

## Rollout

1. **Contract** (one pull request): the types above in `Core.Abstractions` with `BitCursor`, `LsbBitCursor`, `Md5` and the canonical layout; the router's sessions, boundary and budget in the host; `AnalysisOptions.Decoding`; the writers; `--verify`; the `verification` article; `DecodeRouteTests`, `ProjectReferenceTests`, `BitCursorTests`, `Md5Tests`; the two lines of `CLAUDE.md` and the plan's text. No format changes.
2. **FLAC, the reference implementation:** `Encspect.Formats.Flac` (the raw format and the codec "flac", which Ogg already routes by that ID) and `Encspect.Decoders.Flac`; fixtures from `flac` and FFmpeg and hand-built damage; a `flac-crosscheck` job; the GUI's "Verify Audio"; the measurement of the cost. A milestone review of the contract and FLAC follows before any other decoder starts.
3. **In parallel, each in its own projects and pull request:**
   - the WavPack decoder (its parser exists);
   - Monkey's Audio, parser and decoder;
   - TrueHD and MLP, parser and decoder, raw files first;
   - TTA, WAV and AIFF parsers, without decoders;
   - ALAC's codec parser and WMA Lossless's frames, without decoders if the scope decision moves them.
4. **Containers, one at a time:** each container's routes are their own pull request, after the codec parsers they route to: MP4 (FLAC's `fLaC` entry, `alac`, `mlpa`), then Matroska (`A_FLAC`, `A_ALAC`, `A_TRUEHD`, `A_MLP`, `A_TTA1`, `A_WAVPACK4`). Ogg needs no change for FLAC. ASF needs none for WMA Lossless, whose route exists.

Proposed `CLAUDE.md` lines, applied in step 1 after the sign-off:

- Layout: "`src/Encspect.Decoders.<Format>`: the optional decoder of one coding; it references `Core.Abstractions` and the format project whose syntax it decodes, which grants it `InternalsVisibleTo`, and only the host references it."
- Architecture rules: "Parsers reach decoders only through `StartDecoding` on their context and never reference one. A decoder reads the coded syntax with its parser's code and adds only reconstruction and the checksums over decoded audio; decoding never changes the tree."

## Decisions for sign-off

1. **Layout:** each decoder is a project that references `Core.Abstractions` and its own format project, reading the bits with the parser's code. Alternatives: the decoder inside the format project, or a decoder that reads the syntax a second time with code of its own.
2. **Data flow:** the parser hands each frame to a decode session while it reads it, in the one pass over the file. Alternative: a second pass the host drives from the parser's frame records.
3. **Defaults:** decoding is off in the library, the CLI (`--verify`) and the GUI (a "Verify Audio" command in the detail window); the file list never decodes and no verdict is cached. Alternative: decode by default in the detail window.
4. **Meaning of verified:** every checksum the stream stores, over coded data or decoded audio, was compared and holds; reported for lossless codings only in Gate 3. Alternatives: count only checksums over decoded audio, or cover the lossy formats' CRCs now.
5. **Scope:** Gate 3 decoders for FLAC, Monkey's Audio, WavPack, and TrueHD and MLP, the codings with a checksum over decoded audio; ALAC, TTA and WMA Lossless decoders move to "Later" with the waveform. Alternative: all seven, as the rollout row lists them.
6. **Budget:** decoding stops with an info past the larger of 16 GiB of PCM and 256 times the file's size. Alternative: no bound in the library.
7. **Reference tools:** build Monkey's Audio's `mac` (and later the TTA coder) from pinned source where Ubuntu 26.04 has no package, once their licenses are checked.
8. **The plan's open question:** no `ffmpeg` fallback for decoding; FFmpeg stays a cross-check tool.

## Open risks and unverified claims

- **Unverified:** what Monkey's Audio's descriptor MD5 covers (believed coded data); Monkey's Audio frame CRCs before version 3.90; whether TrueHD's lower substreams' lossless checks can be compared; whether WMA Lossless holds a checksum; Ubuntu 26.04 packages for `mac` and `ttaenc`, and those tools' licenses.
- **Cost not measured:** the double entropy pass, Monkey's Audio's filters in managed code, and the MD5 throughput are estimates until step 2 measures them.
- **Shared syntax:** a parser refactor now has to keep its decoder building; that is the point of option C, but it couples their pull requests.
- **Monkey's Audio multichannel:** FFmpeg 6.1.1 decodes 2 channels only, so streams of more have no second decoder in the cross-check.
- **The plan's row** names checksum verification for ALAC and WMA Lossless, which store none; the row needs rewording whatever the scope decision is.
