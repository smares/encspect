# Decoders: the contract for phase 3

Status: approved by the project lead on 2026-10-22, after two independent reviews, with decoders inside their format projects (decision 1). Step 1 of the [rollout](#rollout), the contract, is implemented (see [As built in step 1](#as-built-in-step-1)); the FLAC decoder (step 2) and the WavPack decoder (step 3) are built (see [step 2](#as-built-in-step-2) and [step 3](#as-built-in-step-3-wavpack)). The decisions are collected under [Decisions](#decisions), and the few claims that no source read for this design confirms are marked **unverified**.

## Scope

What phase 3 brings, from the rollout table of the [plan](../PLAN.md):

- FLAC, WAV and AIFF;
- ALAC in MP4 and WMA Lossless in ASF;
- Monkey's Audio and TTA;
- Dolby TrueHD and MLP frame by frame;
- "lossless decoders that verify the stored checksums (WavPack MD5 and DSD, FLAC, ALAC, Monkey's Audio, TTA, WMA Lossless, Dolby TrueHD and MLP)".

What the plan already settles, and this design keeps:

- Decoding is optional, per format. Formats without a decoder still get the full structure report.
- Decoders are optional and live in their format's project apart from its parsers, which reach them only through the host, so no parser ever depends on one. "Apart" means kept apart inside the format project (see [Where decoders live](#where-decoders-live)), and optional means a host may register none.
- No bundled decoders or native audio libraries: every decoder is Encspect's own managed code.
- No playback, and no settings below the front ends: options reach the analysis through the host's public API.

What is not in Gate 3:

- Waveform, spectrogram and lowpass detection. The plan puts "decoders for waveform and spectrogram" under "Later" and the waveform panels in the GUI section, not in a phase.
- This design defines the PCM path and the hook that host-side PCM analyses will use, so those features add fields but change no contract (see [The PCM path](#the-pcm-path)).
- Decoders for lossy codings.

Sources read for this design, all pinned or public:

- The code: `Core.Abstractions` (parser interfaces, contexts and routes, `MappedBytes`, `BitReader`, `StreamInfo`), the host (`Analyzer`, `ParserRouter`, `AnalysisOptions`, the writers), the WavPack parser, the MP4, Ogg and ASF routes, the WMA codec parsers, and the tests (fixtures, goldens, memory, complexity, fuzz and thread tests).
- RFC 9639 (FLAC), sections 8.2, 9.1.8, 9.2.3, 9.3 and 11.
- FFmpeg 6.1.1, the release `ffmpeg_sources.py` pins (archive SHA-256 8684f4b0…): `flac.c`, `flacdec.c`, `flacenc.c`, `apedec.c`, `libavformat/ape.c`, `tta.c`, `libavformat/tta.c`, `mlp.c`, `mlp_parse.c`, `mlpdec.c`, `mlpdsp.c`, `mlpenc.c`, `alac.c`, `wmalosslessdec.c`, `libavformat/matroskadec.c`. The key lines were checked unchanged in n8.0.1, the FFmpeg that CI runs: the Monkey's Audio CRC compared only from 3.90, at most 2 channels, `APE_MAX_VERSION` 3990, FLAC's MD5 skipped, `max_decoded_substream` at most 2.
- libwavpack 5.6.0, the release `wavpack_tool.py` pins (SHA-256 8cbfa159…): `unpack.c`, `unpack_dsd.c`, `open_utils.c`, `pack_utils.c`, `wavpack.h`, `cli/wvunpack.c`.
- Monkey's Audio SDK 13.42 (`MAC_1342_SDK.zip`, SHA-256 0a763b35…, 3-clause BSD): `MACLib.cpp`, `APECompressCreate.cpp`, `APEHeader.cpp`, `APEInfo.h`, `APEDecompress.cpp`, `APEDecompressCore.cpp`, `CircleBuffer.cpp`, `Old/UnMACOld.cpp`, `Shared/All.h`.
- libtta-c 2.3 (SHA-256 68ba6db2…, LGPLv3): `libtta.c`.
- Apple's ALAC (macosforge/alac, Apache 2.0): `ALACDecoder.cpp`.
- Launchpad's archive API, for Ubuntu's packages.

## The design in short

- A format's decoder lives in its format project, in the namespace `<project>.Decoding`. Parser code never references that namespace, which a test checks in the compiled assembly; the host registers the decoders next to the parsers.
- The parser owns every bit of the coded syntax. The decoder reads the bits with the parser's own code and adds only what turns them into samples: prediction, channel decorrelation, matrixing, output shifts, and the checksums over decoded audio.
- A parser hands each frame to a decode session through a new route on its context, `StartDecoding`, in the same pass in which it reads the frame. The host serves the route: it finds the decoder, runs it inside an exception boundary and a work budget, and ends the session with the stream's verdict.
- The parser declares every checksum the stream stores, of either kind, with how many values it stores. It compares the ones over coded data in every analysis; the decoder compares the ones over decoded audio. Both end up in one record on the stream, `StreamInfo.Verification`, with a tri-state `IsVerified`.
- Decoding is off unless a front end asks for it: `AnalysisOptions.Decoding` in the library, `--verify` in the CLI, a "Verify Audio" command in the GUI's detail window. The file list never decodes, and no verdict over decoded audio is cached.
- Decoding never changes the parser's nodes: it adds findings, the stream's verification and, once host-side PCM analyses exist, their figures on the stream.

## Where decoders live

### The layout

The project lead chose the layout on 2026-10-22: a format's decoder lives in its format project, in a namespace of its own.

- `src/Encspect.Formats.Flac` holds the FLAC parsers and, in `Encspect.Formats.Flac.Decoding`, the FLAC decoder. Monkey's Audio, WavPack and TrueHD get the same, and later ALAC, TTA and WMA Lossless (`Encspect.Formats.Wma.Decoding`).
- No project is added for a decoder. The format project still references `Core.Abstractions` only, so "parsers depend only on `Core.Abstractions`" holds, and so does every rule about project references.
- Why it works:
  - One reading of the bits: the decoder calls the parser's internal syntax readers directly, with no `InternalsVisibleTo` between assemblies.
  - A parser change that breaks its decoder fails the build of the same project, so the two cannot drift.
  - The separation that matters is the direction of use, not the assembly: parser code never uses decoding code. That direction is checked in the compiled assembly (below), which is stricter than a project reference, since it sees every type a method touches.

Options that were considered:

- **A decoder that reads the syntax a second time with code of its own** (its own project on `Core.Abstractions` only): two readings of the same bits, which can drift apart.
- **A decoder project that references its format project** through `InternalsVisibleTo`: one reading of the bits too, at the cost of a project per decoder and an assembly boundary that the rule needs no longer.
- **The decoder in the format project (chosen).**

### The rule inside one assembly

- Parser code never references the `Decoding` namespace of its project. "Parser code" is every type of the format project outside `<project>.Decoding`, including the types the compiler generates for its lambdas, iterators and local functions.
- Decoding code may use any internal type of its project: the parser's syntax readers, the configuration's reader, the frame and field names and the node factories its findings need.
- The parser reaches decoding only through `StartDecoding` on its context (below), which the host serves. It never creates, holds or calls a decoder.

The earlier draft limited a decoder to a `Syntax` namespace of its parser, to keep the coupling across an assembly boundary deliberate. That limit is dropped:

- Inside one assembly the coupling that can do harm is the parser's on the decoder, and the test below forbids it.
- The existing parsers (WavPack, WMA) keep their readers where they are; moving them into a `Syntax` namespace would be churn with no check behind it.

### How a test enforces it

`DecodingBoundaryTests` reads each format assembly's metadata with `System.Reflection.Metadata`, which is part of .NET and needs no package.

**Which assemblies.** The assemblies of the format and codec parsers the host lists (`DefaultParsers.Formats` and `DefaultParsers.Codecs`), so a new format project is covered without a change to the test.

**Which types are decoding code.** A type is decoding code where its namespace has `Decoding` as a whole segment (`X.Decoding` and `X.Decoding.Y`, not `X.DecodingTables`). A nested type has no namespace of its own, so it takes the namespace of the outermost type that declares it. The same rule classifies both sides of every reference:

- the referencing side, so the display classes, iterator and async state machines and local functions the compiler nests in a parser type count as parser code;
- the referenced side, so `Decoding.FlacDecoder.State` counts as decoding code.

**What it reads.** For every type that is parser code, every type and member it touches:

- its base type, interfaces, and the constraints of its generic parameters and of its methods' generic parameters;
- the signatures of its fields, methods, properties and events, with `modreq` and `modopt` modifiers;
- in each method body:
  - the local variable signature;
  - every token in the IL (`call`, `callvirt`, `newobj`, `ldftn`, `ldfld`, `stsfld`, `ldtoken`, `castclass`, `calli` and the rest);
  - the catch types of the exception regions, which a `catch` without a variable leaves nowhere else.
- the custom attributes on the type, its members, their parameters and return values, and its generic parameters, with their blobs decoded (`CustomAttribute.DecodeValue`) so that `typeof(…)` arguments and enum arguments, which the blob holds as type names, are resolved. The assembly's and the module's attributes are read once per assembly, since they belong to no type.

**How tokens resolve.**

- A TypeDef is classified by the rule above.
- A MethodDef or FieldDef is classified by its declaring type. Inside one assembly the compiler emits these, not member references, for calls, delegates and field accesses on non-generic types: direct calls, `using static` calls, extension methods and method groups all take this path.
- A TypeSpec, MemberRef (on a TypeSpec parent), MethodSpec and StandAloneSig (`calli`, function pointers) are decoded to every type they name, generic arguments included.

**What fails.** Any resolved type that is decoding code, named with the parser type, the method and the decoding type. And, to close the indirect path below, any `Decoding` type that derives from or implements a type of its own project that is not decoding code: a decoder implements `Core.Abstractions` contracts and .NET types only, so a parser cannot call into it through an abstract type or interface of its own.

**The negative control.** The test project holds one sample per path, each a parser-side type in `Encspect.Tests.BoundarySamples` that reaches a type in `Encspect.Tests.BoundarySamples.Decoding`:

- a static call, a field, a constructor, a nested `Decoding` type;
- `List<Decoding.X>`, a member of `Decoding.G<int>`, `M<Decoding.X>()`;
- `typeof`, a method group as a delegate, a function pointer, `catch (Decoding.E)`;
- an attribute with `typeof(Decoding.X)`, a `using static` call, a default interface method's body;
- an iterator, an async method, a local function;
- a `Decoding` type that implements a parser-side interface.

The check counts only references to decoding types defined in the assembly it scans, the same rule it applies to the format assemblies, so the test assembly's own tests that use `Encspect.Formats.*.Decoding` types on purpose are not counted. Over the test assembly it must then report exactly these pairs, no more and no fewer, because the IL walker is hand-written (`System.Reflection.Metadata` has no IL reader), so an operand-size mistake either reads an undefined opcode or runs past the body, which fails the scan with a message naming the method and the IL offset, or lands on valid opcodes and reads wrong tokens, which shows as a missing or extra pair. The control's C# samples have the shapes real code has, but a compiler adds evidence of its own (a constructor calls the base constructor, a property has accessors that carry its type), so a second part of the test builds a small assembly with `System.Reflection.Emit` for each way of reaching a type, in which that way alone is the evidence, and shows that scanning with that way switched off loses exactly the pair. Two ways have no such assembly because a C# or IL type cannot have the evidence alone: the signature of a member reference, and an explicit interface implementation (which also implements the interface).

Why the compiled assembly and not the source:

- A source scan for `using …Decoding` misses fully qualified names, `global using`, `<Using>` items in a project file, aliases and code in the same namespace as a partial type.
- Roslyn's semantic model would be exact, but it brings a large package into the tests for one check.
- The compiled assembly is what runs: whatever path a reference takes in the source, it ends as a token or a type name in the metadata or the IL, where the check reads it.

**What no metadata check sees,** so reviews hold these as rules and the test is no proof of them:

- `const` fields and enum members of `Decoding` types used in parser code, which the compiler inlines as literals, and `nameof(…)`, which becomes a string;
- reflection by name, such as `Type.GetType("…Decoding…")`;
- a delegate or a static field of parser code that decoding code sets, through which parser code would call decoding code without naming it.

### Optional, and how the host finds decoders

- The host references every format project already, so it lists the decoders itself: `DefaultParsers.Decoders` holds `new Encspect.Formats.Flac.Decoding.FlacDecoder()` and the others, next to `Codecs`. The parser is never asked for its decoder and does not know one exists.
- A decoder class is public, as the parser classes the host constructs are (`new Mp4Parser()`); the parser internals it calls stay internal.
- A decoder implements `IDecoder` (below), whose `Id` is the codec ID the parsers route by: `flac`, `monkeys-audio`, `wavpack`, `truehd`, `mlp`, and later `alac`, `tta`, `wma-lossless`, `pcm`.
- A raw format parser uses the same ID for its coding: the FLAC format parser and the FLAC codec parser that Ogg, MP4 and Matroska reach both start sessions with `flac`.
- `Analyzer` gets a constructor that takes the decoders after the codec parsers; `Analyzer.CreateDefault()` passes `DefaultParsers.Decoders`. Two decoders with one ID are an `ArgumentException`, as two codec parsers are.
- Decoders stay optional where it counts, at run time:
  - An analyzer built without decoders, by the host or by a third party, works as today: every session reports `NoDecoder`, and the structure report is the same.
  - No parser depends on a decoder, which the boundary test holds.
- What is lost is leaving the decoding code out of the default build: it ships in the format's assembly. A program that publishes trimmed and never refers to the decoders still has them trimmed away.

The rules need lines in `CLAUDE.md` and the plan; they are proposed under [Rollout](#rollout) and land with the contract pull request.

## The interface

### Who owns the bits

- The parser owns the whole coded syntax and the code that reads it, including entropy-coded data it walks to find where a part ends.
- FLAC is the case in point: the header of channel 2's subframe lies after channel 1's residual, so the FLAC parser reads every Rice partition to its end anyway, and the frame's CRC-16 confirms where the walk ends.
- That code takes an optional destination. Given none, it only walks the bits and counts what the structure report shows; given buffers, it also stores warm-up samples and residuals. One function, two uses:

```csharp
namespace Encspect.Formats.Flac;

// Where a subframe's samples go: nowhere (the structure pass), 32-bit buffers, or 64-bit ones for the side channel of a
// 32-bit stream, which needs 33 bits (RFC 9639, section 9.2.3).
internal readonly ref struct SubframeSamples
{
    public Span<int> Narrow { get; init; }

    public Span<long> Wide { get; init; }

    public bool IsEmpty => Narrow.IsEmpty && Wide.IsEmpty;
}

// Parser code, which Encspect.Formats.Flac.Decoding calls too.
internal static class SubframeSyntax
{
    // Reads one subframe from the bit after the frame header (RFC 9639, section 9.2). With samples empty it walks the bits
    // and fills facts for the structure report; otherwise it also stores the warm-up samples and the residual.
    public static SubframeFacts Read(ref BitCursor bits, in FrameHeader header, int channel, SubframeSamples samples);
}
```

- The decoder adds what the structure report never needs: restoring samples from the predictor, undoing left/side, side/right and mid/side coding, wasted bits, and the MD5. It never reads a field with code of its own.
- A coding whose entropy-coded data the parser does not walk (Monkey's Audio's range coder, whose frames the seek table delimits) keeps its entropy decoder in its `Decoding` namespace. The parser still owns the header, seek table and frame start fields, which the decoder reads with the parser's code.
- The configuration reaches the decoder as the bytes the parser read it from (`DecodeSetup.Configuration`), and the decoder reads them with the parser's own reader: a few dozen bytes read twice by one piece of code, not two readings.
- Guards against drift beyond the shared code: the fixture tests check that the decoder ends every frame at the bit where the parser's walk ends, and the bit-exact cross-checks (below) catch a misreading the two would share.

### Options considered for the data flow

- **A second pass driven by the host** from the parser's frame records, after the parse.
  - The structure pass would stay exactly as it is, but the file is read twice.
  - A codec parser inside a container gets its packets once (`CodecContext.Packets` is enumerated once) and would have to keep every packet's file ranges.
  - Decompressed Matroska frames do not map back to file bytes at all.
- **The decoder walks the file itself.** It would have to repeat the parser's sync, resync and damage decisions, so it could decode other frames than the report shows.
- **A session the parser feeds while it reads (recommended).**
  - The parser already holds each frame's bytes when it parses it; it hands the same bytes on.
  - One read of the file, the same frames as the report, and the same code path for raw files and for every container, since a codec parser inside MP4, Matroska or Ogg holds its packets the same way.

**Decision:** the session.

### The second entropy pass

With decoding on, a coding whose parser walks its entropy-coded data (FLAC, ALAC, TrueHD) would read that data twice: once in the parser's walk, once in the decoder's read into buffers. This was settled before the contract merged:

- Step 1 built a throwaway FLAC prototype (outside the repository, not merged): the structure walk alone, and the walk followed by the decoder's own read, prediction and MD5. The rule was: if the second read costs less than a quarter of the decoding time, the contract stays as drafted; otherwise step 1 merges the lending variant.
- **Decision: the lending variant.** The second read cost more than a quarter of the decoding time (below), so the contract has it:
  - the session lends the parser its sample buffers (`IDecodeSession.Lend(int samples, int channels, bool needsWide = false)`, returning a `SampleBuffers` of 32-bit planes and, where asked, 64-bit planes);
  - the parser's walk fills them (the `SubframeSamples` destination above);
  - `CodedFrame.IsWalked` tells the decoder that warm-up samples and residuals are there, and the decoder takes the buffers from `DecodeContext.LentBuffers`, so it reads only what the walk does not store.
- Either variant keeps one reading of the bits.

**The measurement.**

- **Machine:** Intel Xeon at 2.80 GHz, 4 vCPUs, 15 GB, Linux 6.18, .NET 10.0.401 (Release, tiered compilation with PGO as the default; a run with `DOTNET_TieredCompilation=0` agreed within the noise), one thread.
- **Prototype:** a FLAC frame reader on `BitCursor` (headers with CRC-8, every subframe type, Rice partitions with escapes, wasted bits, CRC-16), a decoder (fixed and LPC prediction, with `System.Numerics.Vector` when the sum fits 32 bits and scalar 64-bit otherwise, undoing left/side, side/right and mid/side coding, `CanonicalPcm.Write` into `Md5`), and a driver. The whole file is in memory and the frames are located before timing. The MD5 of every variant equals the STREAMINFO MD5 of every file.
- **Files:** made with FFmpeg 6.1.1's FLAC encoder (`flac` is not installed in the container) from synthetic signals (three sines with a tremolo, plus uniform noise at 0.002, 0.05 and 0.3 of full scale), so they are not music; they differ in noise and encoder setting, which is what moves the entropy read. 16-bit 44.1 kHz stereo at compression levels 0, 5, 8 and 12 (10 minutes), 5 and 8 (one hour), noise levels 0.002 and 0.3 (10 minutes, level 5), mono, LPC precision 12 (which takes the 32-bit vector path), and 24-bit 96 kHz stereo (10 minutes).
- **Variants** (each run over all frames, the fastest of 7 runs after a warm-up, 3 for the hour files):
  - `walk`: the parser's pass alone, with CRCs, storing nothing;
  - `walk, then decoder` (the contract as drafted): the walk, then the decoder reads every subframe again into buffers, predicts, hashes;
  - `read again`: the decoder's second read alone (header and subframes into buffers);
  - `walk storing`: the walk with the stores of the lending variant.
- **Second pass cost S** = `read again` - (`walk storing` - `walk`), the read the lending variant saves net of what the stores add to the walk. **Decoding time D** = the decoder alone (read, prediction, MD5, no CRC). The rule compares S with D.

| File | Length | Walk | Decoder alone (D) | Second read S | S / D |
| --- | --- | --- | --- | --- | --- |
| 16-bit stereo, level 5, noise 0.05 | 1 min | 0.065 s | 0.145 s | 0.050 s | 34.9 % |
| level 0 | 10 min | 0.670 s | 0.966 s | 0.504 s | 52.1 % |
| level 5 | 10 min | 0.640 s | 1.344 s | 0.427 s | 31.8 % |
| level 8 | 10 min | 0.642 s | 1.625 s | 0.499 s | 30.7 % |
| level 12 | 10 min | 0.642 s | 2.181 s | 0.503 s | 23.0 % |
| level 5, noise 0.3 | 10 min | 0.655 s | 1.279 s | 0.467 s | 36.5 % |
| level 5, noise 0.002 | 10 min | 0.580 s | 1.457 s | 0.468 s | 32.1 % |
| level 5, LPC precision 12 | 10 min | 0.620 s | 1.620 s | 0.459 s | 28.3 % |
| mono, level 5 | 10 min | 0.359 s | 0.757 s | 0.235 s | 31.0 % |
| 24-bit 96 kHz stereo, level 5 | 10 min | 1.490 s | 3.301 s | 1.000 s | 30.3 % |
| 16-bit stereo, level 5 | 60 min | 4.166 s | 9.198 s | 3.446 s | 37.5 % |
| 16-bit stereo, level 8 | 60 min | 3.769 s | 10.354 s | 2.649 s | 25.6 % |

- **Result:** S / D is 23 % to 52 % with a median of 31 %; eleven of twelve files are above a quarter, and the hour of CD audio at level 5 (the case the design named) is at 37.5 %. The decoder alone runs at 340 to 610 times real time, so an hour of CD audio decodes in 9 to 10 s.
- **End to end:** lending saves S / (walk + decoder), about 21 % of the analysis time of a FLAC file with decoding on in the direct measurement (the review's rerun of the prototype, S taken from the `read again` and `walk storing` variants); the differential form, the prototype's `RESULT` column S / (walk + decoder), gives 19 % to 34 % over the files.
- **Noise:** repeated runs of one file differ by up to ten points (the same file read 23.8 % to 41.8 % in the differential form, S taken as the difference of the walk-and-decode time and the lending variant's, `RESULT` lines of the prototype), because S is a difference of times. The direct form above is the steadier one, and it also reads above a quarter on 11 of 12 files; the one below it is the highest-order setting, where prediction is most expensive.
- **What would lower S / D:** a faster decoder, not a faster read: both reads use the same cursor, so making `BitCursor` faster lowers S and the walk alike, while vectorizing the 64-bit LPC path (this prototype's 17-bit side channel at LPC precision 15 runs it scalar) lowers D and raises S / D. Neither moves a median of 31 % below a quarter.
- **Not measured:** ALAC and TrueHD (their walks are built in Gate 3), and music. The lending variant is the contract for all three; a coding whose walk cannot store (Monkey's Audio's range coder, which the parser does not walk) does not use it.

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

// The decoding state of one stream in one analysis. The host calls it on one thread, frame by frame, and ends it
// with Finish, also when the parser does not (see "Ending sessions"); only a decoder that failed (decode.failed) is not
// called again. Its buffers are its own arrays, dropped with it.
public interface IFrameDecoder
{
    // Decodes one frame, checks what the frame stores about its decoded audio (a Monkey's Audio frame CRC), and returns
    // the samples per channel by which the stream's decoded output advanced: 0 for a frame it could not decode, 0 for
    // the blocks of a WavPack set before its final block and the set's samples on that block, and for a WMA Lossless
    // packet the samples of the frames that end in it. The frame's bytes are valid only during the call.
    int Decode(in CodedFrame frame);

    // Ends the stream: compares the checksums over the whole stream (FLAC's MD5) where end.IsComplete, and returns what it
    // compared, by the names the parser declares them with. A result whose name the parser did not declare, or whose
    // Checked exceeds the declared Stored, is a decoder bug: the host reports decode.failed and drops the results.
    IReadOnlyList<ChecksumResult> Finish(DecodeEnd end);
}

public sealed record ChecksumResult(string Name, long Checked, long Failed);

public readonly record struct DecodeEnd(bool IsComplete, long FramesNotDecoded);
```

What the parser states when it starts a session, and what it hands per frame:

```csharp
public sealed record DecodeSetup
{
    // The parser's node of the stream, the first of StreamInfo.Nodes: findings about the whole stream point to it.
    public required Node Stream { get; init; }

    public required int Channels { get; init; }

    // The width of a sample in the canonical layout's terms: ceil(BitsPerSample / 8) bytes count toward the budget. For
    // DSD, 8, with one byte of DSD per channel as one sample.
    public required int BitsPerSample { get; init; }

    // The samples per channel the parser counts in the stream, or null where it cannot tell.
    public long? Samples { get; init; }

    // The configuration as the parser read it (FLAC's STREAMINFO block, ALAC's magic cookie, a TTA header), which
    // the decoder reads with the parser's code; null for a coding whose frames carry it (TrueHD's major sync).
    public MappedBytes? Configuration { get; init; }

    // The WAVEFORMATEX a container states (ASF's WMA Lossless, WAV's PCM), or null.
    public AudioFormat? Format { get; init; }

    // True where the stream's decoded audio is a lossy coding's output (a WavPack hybrid file without its correction
    // file); the host copies it into StreamVerification.IsLossy.
    public bool IsLossy { get; init; }
}

// The unit the coding decodes one at a time, as the parser read it: a frame, a block or an access unit; a packet where
// the coding's frames are not byte-aligned (WMA Lossless), whose decoder then finds the frames with the parser's code.
//
// Bytes: the unit's bytes, mapped to the file. NodeName, with the file range of Bytes, names the parser's node of the
// unit (equal in name, offset and length), which the host builds for a finding only when one is reported.
// FirstSample: the unit's position in the stream in samples per channel. Samples: how many the parser says it codes.
public readonly record struct CodedFrame(MappedBytes Bytes, string NodeName, long FirstSample, int Samples)
{
    // The parser found the frame damaged: a checksum over its coded data fails, or the data ends inside it. The decoder
    // still decodes what it can, so a whole-stream checksum then says the audio differs.
    public bool IsDamaged { get; init; }

    // The parser could not read the unit at all and reported that itself. The host counts it as not decoded and
    // does not call the decoder, and the next unit reaches it with FollowsGap set (see "After the milestone review").
    public bool IsUnreadable { get; init; }

    // Frames the parser counted in the stream were lost before this one (a gap, a resync), so the decoder resets
    // what it carries from frame to frame and the whole-stream checksums cannot be compared.
    public bool FollowsGap { get; init; }

    // The parser's walk stored the frame's warm-up samples and residuals in the buffers the session lent (IDecodeSession.Lend),
    // so the decoder does not read them again: it takes them from DecodeContext.LentBuffers.
    public bool IsWalked { get; init; }
}
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

    // The buffers the session lent the parser for the frame being decoded, where its CodedFrame.IsWalked; empty otherwise.
    public SampleBuffers LentBuffers { get; }

    // Findings on the parser's nodes or on equal ones; findings of one kind are reported once per stream with a count.
    public void AddFinding(Severity severity, string code, string message, Node? node = null, int count = 1);

    // Called once, before the first block: the PCM the decoder will write, and where to write it. The sink is the
    // host's: it forwards to the front end's sink (DecodeOptions.Output) and later to the host's PCM analyses, and it
    // never throws, and the host alone ends it (IPcmSink.End): a call from the decoder is ignored. A coding that decodes to something other than PCM (WavPack's DSD) does not call it.
    public IPcmSink DeclareOutput(PcmFormat format);
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

    // Lends the parser the buffers its walk fills with warm-up samples and residuals, so the entropy-coded data is read once
    // (the lending variant of "The second entropy pass"). samples: the most per channel the frame can hold; needsWide: also lend
    // 64-bit planes, for the side channel of a 32-bit FLAC stream. The buffers are the session's own, grown to the largest frame
    // seen; empty where IsDecoding is false or the frame would pass 2^24 samples over all channels, and the parser then walks
    // without storing and hands the frame not walked.
    SampleBuffers Lend(int samples, int channels, bool needsWide = false);

    // Hands the next frame to the decoder, in stream order. Returns IsDecoding afterward: false once the budget, the
    // front end's stop, a sink's exception or a decoder failure ended the session.
    bool Decode(in CodedFrame frame);

    // Ends the session, once. stored lists every checksum the stream stores, of either kind, with Stored set, and for
    // those over coded data what the parser compared. The host fills in the decoder's results for those over decoded
    // audio and returns the verification the parser puts on its StreamInfo.
    StreamVerification Finish(IReadOnlyList<ChecksumTally> stored);
}

public interface IParserRouter
{
    IDecodeSession StartDecoding(AnalysisContext caller, string codecId, DecodeSetup setup);
}
```

The buffers a session lends (channel c holds its samples from c * Stride on):

```csharp
public readonly ref struct SampleBuffers
{
    public Span<int> Narrow { get; }

    public Span<long> Wide { get; }

    public int Channels { get; }

    public int Stride { get; }

    public bool IsEmpty { get; }

    public Span<int> Channel(int channel);

    public Span<long> WideChannel(int channel);
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

    public long ReadSignedLong(int count);  // 1 to 64 bits, for FLAC's 33-bit warm-up samples

    public bool ReadBit();

    // Counts the zero (or one) bits up to the next bit of the other value, which it consumes. It stops after limit bits
    // or at the end of the data (IsOverrun), and its time grows with the bits it consumes, a word at a time.
    public int CountZeros(int limit);

    public int CountOnes(int limit);

    public void Skip(int count);

    public void AlignToByte();
}
```

MD5 and CRC-32 in managed code, for the new parsers, the decoders and the tests:

```csharp
// MD5 (RFC 1321). System.Security.Cryptography's MD5 goes to the system's OpenSSL on Linux, which refuses MD5 in FIPS
// mode, so verification would fail on such a system. The ID3 parser's SHA256.HashData takes the same path, but
// SHA-256 is approved in FIPS mode.
public sealed class Md5
{
    public void AppendData(ReadOnlySpan<byte> data);

    public void GetHashAndReset(Span<byte> destination);   // 16 bytes
}

// CRC-32 with the reflected polynomial 0xEDB88320, the one TTA, Monkey's Audio, Matroska, Musepack and ID3v2 use.
// Matroska, Musepack and ID3v2 keep their own copies for now; moving them is a change of its own.
public static class Crc32
{
    public static uint Update(uint crc, ReadOnlySpan<byte> data);
}
```

### What a parser does

The FLAC format parser, in outline; a codec parser does the same with the packets a container hands it.

```csharp
IDecodeSession decoding = context.StartDecoding("flac", new DecodeSetup
{
    Stream = framesNode,
    Channels = info.Channels,
    BitsPerSample = info.BitsPerSample,
    Samples = info.TotalSamples == 0 ? null : info.TotalSamples,
    Configuration = info.Bytes,
});

foreach (frame in the frames the parser reads)
{
    // ... the header, its CRC-8, then the subframes: with decoding on, the walk stores them in the lent buffers
    // (SampleBuffers lent = decoding.Lend(header.BlockSize, header.Channels, needsWide); the walk gets a SubframeSamples over them),
    // otherwise it only walks; then the CRC-16, as without decoding ...
    if (decoding.IsDecoding)
    {
        decoding.Decode(new CodedFrame(new MappedBytes(frameBytes, frameOffset), FrameNames.Frame, firstSample, header.BlockSize)
        {
            IsDamaged = !crc16Holds || isCut,
            FollowsGap = followsGap,
            IsWalked = !lent.IsEmpty && walkedToTheEnd,
        });
    }
}

StreamVerification verification = decoding.Finish(
[
    new ChecksumTally { Name = ChecksumNames.HeaderCrc8, Covers = ChecksumCoverage.Header, Stored = frames, Checked = frames, Failed = headerCrcFailures },
    // A per-frame checksum covers all audio only where no frame is missing: no gap, and the samples STREAMINFO counts.
    new ChecksumTally { Name = ChecksumNames.FrameCrc16, Covers = ChecksumCoverage.CodedData, CoversAllAudio = !hasGap && samplesMatchStated, Stored = frames, Checked = wholeFrames, Failed = frameCrcFailures },
    new ChecksumTally { Name = ChecksumNames.StreamInfoMd5, Covers = ChecksumCoverage.DecodedAudio, CoversAllAudio = true, Stored = md5IsZero ? 0 : 1 },
]);
context.AddStream(stream with { Verification = verification });
```

Rules for the parser:

- It declares every checksum the stream stores, both kinds, and counts how many values it stores, also where nothing is decoded. That is what lets a default analysis say "the MD5 was not compared".
- It hands every frame it counts in the stream, damaged ones included, in stream order, and nothing it does not count (junk, gaps).
- It keeps nothing for the decoder: no record, no bytes. The session works on the frame while the parser holds it.
- Its samples, duration and statistics come from its own reading, never from the decoder. A frame that decodes to another number of samples than its header says is the decoder's finding.

### Ending sessions

- A parser ends its session with `Finish` once it has read the stream.
- When a route returns (`ParsePackets`, `ParseStream`), the host ends every session the routed parser opened and did not finish, before it merges the route's findings into the caller. When the format parser returns, the host does the same for its own sessions.
- Ending such a session calls `IFrameDecoder.Finish` with `IsComplete` false, so the findings the decoder counts once per stream are added while they can still be merged; its results are dropped, since no stream carries them.

### Data flows beyond FLAC

FLAC exercises the simplest flow. The flows of WavPack and TrueHD, walked through the contract on paper, so that the contract fits them before they are built; the milestone review after FLAC checks them again (see [Rollout](#rollout)).

WavPack:

- **Units:** one `CodedFrame` per block. The blocks of one set, from the block with the initial flag to the one with the final flag, carry the channels of the same samples; the decoder reads the flags with the parser's code and keeps the set's samples until its final block, then writes one `PcmBlock`.
- **Samples:** `Decode` returns 0 for the blocks before a set's final block and the set's samples on the final one, so `DecodedSamples` and the budget count each sample once, not once per block.
- **State:** none from set to set beyond the decoder's configuration.
- **Declared checksums:**
  - the block checksum sub-block (coded data, every block that has one; the parser compares it today);
  - the block header CRC (decoded audio, one per audio block, `CoversAllAudio` where the stream has no gap and its samples equal the total the first block states);
  - the extended CRC (decoded audio, one per block with an extra bitstream);
  - the MD5 (decoded audio, 0 or 1 stored, `ID_MD5_CHECKSUM` or `ID_ALT_MD5_CHECKSUM`).
- **DSD:** the decoder never calls `DeclareOutput`; `Decode` returns the DSD bytes per channel, which count toward the budget at 8 bits.
- **A set past the ceiling** (below): its blocks are decoded one at a time and their CRCs compared, and the MD5 is not, with `wavpack.md5-set-too-large`.
- **Hybrid files:**
  - The main file decodes to the lossy samples, the stream the report describes, and their header CRCs are compared; the CRC covers all of that lossy stream (`CoversAllAudio` as above).
  - A hybrid file without an MD5 (the encoder stores one only with `-m`) is therefore verified as its lossy stream. The parser, which reads the hybrid flag, sets `DecodeSetup.IsLossy`, and the host copies it into `StreamVerification.IsLossy`: where the stream is verified, the text output and the GUI say "verified (lossy stream)", and JSON writes `isLossy` next to `isVerified`.
  - A hybrid file with an MD5, which is of the lossless audio, stays unverified: the MD5 is not compared (`wavpack.md5-needs-correction-file`) until the host can find the `.wvc`.
  - A `.wvc` file analyzed alone decodes nothing (`NotPossible`, `decode.not-possible`), since its blocks only correct the main file's.

TrueHD and MLP:

- **Units:** one `CodedFrame` per access unit.
- **State:** each substream's restart header and decoding parameters, the filter and matrix state, and one lossless check accumulator per presentation decoded.
- **Presentations:** the decoder decodes substreams 0 to 2 where they exist and computes the lossless check of every presentation in them, as FFmpeg computes the one of the presentation it outputs (see decision 6). The fourth substream of a stream with Dolby Atmos is not decoded, as FFmpeg does not decode it.
- **Declared checksums:**
  - the major sync CRC-16, the restart header CRC and the parity nibble (header);
  - the substream parity byte and CRC-8 (coded data, `CoversAllAudio` where every substream of every access unit carries them, the stream has no gap and no access unit is missing);
  - one lossless check tally per presentation (decoded audio, never `CoversAllAudio`).
- **What a lossless check covers:** a restart header's byte is compared with the output since the previous restart header, and the accumulator then starts again (`mlpdec.c` 586-593, 627). So the second restart header checks the first interval, and the audio no check covers is the last interval of each segment: from the last restart header to the end of the data, and the interval that ends at an end of stream marker, after which the accumulator is invalid (1346, 1378-1383).
- **Stored lossless checks:** the restart headers that close an interval, that is every one but the first of a presentation and the first after an end of stream marker, whose bytes have no output to be compared with.
- A TrueHD stream is therefore verified only where its substream CRC-8s cover all of it and hold, and every stored lossless check was compared and holds (decision 6).

WMA Lossless, later:

- **Units:** frames start at any bit of a packet and cross packets, so the parser hands packets. The decoder finds the frames with the parser's packet code, keeps the bits of a frame that crosses into the next packet, and reports the samples of the frames that end in each packet.

## Checksums and verdicts

### What each format stores

"Header" means the checksum covers a header only; "coded data" means it covers coded bytes that hold audio, so the parser compares it in every analysis; "decoded audio" means it covers the samples, so only the decoder can.

| Format | Checksum | Covers | Compared by | Source |
| --- | --- | --- | --- | --- |
| FLAC | Frame header CRC-8 (x^8 + x^2 + x + 1, start 0, the header from the sync code) | header | parser | RFC 9639, 9.1.8 |
| FLAC | Frame CRC-16 (x^16 + x^15 + x^2 + 1, start 0, the whole frame before it) | coded data | parser | RFC 9639, 9.3 |
| FLAC | STREAMINFO MD5 of all samples interleaved, signed, little-endian, sign-extended to whole bytes; 0 means not known | decoded audio | decoder | RFC 9639, 8.2; FFmpeg skips it (`flac.c` 221-222); its encoder writes that layout (`flacenc.c` 1574-1613) |
| Monkey's Audio, 3.98 and later | Descriptor MD5 over the stored WAV header data, then the frame data and the WAV terminating bytes, then the APE header, then the seek table; not the descriptor | coded data | parser | SDK `MACLib.cpp` 594-661 (quick verify, encoder 3.98 and later), `APECompressCreate.cpp` 162-352; FFmpeg reads it (`ape.c` 177) and never compares it |
| Monkey's Audio, 3.90 and later | Frame CRC: CRC-32 (reflected, start 0xFFFFFFFF) over the decoded output bytes in WAV layout, channel-interleaved (8-bit samples as unsigned bytes, 16-bit as 2 bytes, 24-bit as 3), inverted and shifted right by one; from 3.83 on, bit 31 of the stored word flags frame flags instead | decoded audio | decoder | `apedec.c` 765-779, 1673, 1694-1723 (FFmpeg compares it only from 3.90, with `-err_detect crccheck`); `apedec.c` 777 and SDK `APEInfo.h` 64 (`> 3820`); SDK `APEDecompressCore.cpp` 378-423, `CircleBuffer.cpp` 48-68 |
| Monkey's Audio, before 3.93 | The SDK's old decoder (`MACLib.cpp` 58): with the CRC format flag a CRC-32 over the output bytes; without it a 30-bit Rice-coded sum of the samples' absolute values, 0 marking silence | decoded audio | decoder | SDK `Old/UnMACOld.cpp` 125-146, 247-265, 399-414; FFmpeg compares neither |
| TTA | Header CRC-32 over the header before it | header | parser | `libavformat/tta.c` 62-85 |
| TTA | Seek table CRC-32 | header | parser | `libavformat/tta.c` 118-133 |
| TTA | Frame CRC-32 over the frame's bytes before it | coded data | parser | `tta.c` 76-87, 239-243; libtta-c 2.3 `libtta.c` 374-417 |
| TrueHD, MLP | Major sync CRC-16 (polynomial 0x002D), the parity nibble, restart header CRC | header | parser | `mlp_parse.c` 99-101, `mlp.c` 89-105, `mlpdec.c` 618-621, 1264-1267 |
| TrueHD, MLP | Substream parity byte (0xA9) and CRC-8 (polynomial 0x63, start 0x3C), where the directory flags them | coded data | parser | `mlpdec.c` 1349-1361, `mlp.c` 87, 107-109 |
| TrueHD, MLP | Lossless check: the XOR of `(sample & 0xffffff) << matrix channel` since the last restart header, folded to 8 bits, compared at the next one | decoded audio | decoder | `mlpdsp.c` 100-125, `mlpdec.c` 586-593; FFmpeg compares it for the presentation it decodes, at most substream 2 (398) or a lower one for a requested downmix (304-305, 570-577) |
| WAV, AIFF | none | | | |
| ALAC | none | | | FFmpeg `alac.c`; Apple's `ALACDecoder.cpp` 224-579 |
| WMA Lossless | none in FFmpeg | | | `wmalosslessdec.c`; no specification is public, so beyond FFmpeg this is **unverified** |
| WavPack | Block checksum sub-block (5.0 and later) | coded data | parser (done) | plan, "Phase 2: WavPack" |
| WavPack | Block header CRC: `crc * 3 + s` per sample of each channel; without the correction file over the samples the main file decodes to (lossy in hybrid mode), with it the correction block's header holds the one compared | decoded audio | decoder | `unpack.c` 152-157, 451, 812-826; `open_utils.c` 1043-1044 |
| WavPack | Extended CRC of floats and of integers above 24 bits (the extra bitstream) | decoded audio | decoder | `unpack.c` 707-726, 821 |
| WavPack | MD5, one stored: `ID_MD5_CHECKSUM` (0x26) where the qmode's low byte is 0 (WAV layout: little-endian, 8-bit unsigned), else `ID_ALT_MD5_CHECKSUM` (0x29) in the qmode's layout (big-endian, signed bytes, unsigned words, reordered channels); `wvunpack` compares it only for lossless decoding with an unchanged qmode | decoded audio | decoder | `pack_utils.c` 783-791, `wavpack.h` 186-189, `wvunpack.c` 1996, 2157-2159 |
| WavPack | DSD: the block header CRC over the decoded DSD bytes and the MD5 over them | decoded audio | decoder | `unpack_dsd.c` 86-441, `wvunpack.c` 2306-2307 |

- Each decoder owns the byte layout its checksum is computed over. The canonical layout of the PCM path (interleaved, signed, little-endian) is FLAC's and the manifests'; Monkey's Audio and WavPack each need their own.
- The qmode of a WavPack stream comes from its new configuration block, which the parser reads.

What the table means for the plan's list:

- ALAC and WMA Lossless store no checksum, and TTA's checksums all cover coded data, which its parser compares.
- The decoders that compare something are FLAC, Monkey's Audio, WavPack, and TrueHD and MLP. Decision 5 sets the scope from this.
- What deferring the other three costs:
  - Their waveform, spectrogram and lowpass panels say "no decoder" until "Later" (plan, GUI: they "appear only for formats that have a decoder"). Lowpass detection of lossless files made from lossy sources is a prime use for ALAC.
  - The contract is not tried on WMA Lossless frames that cross packets, the hardest data flow of the seven, beyond the walk-through on paper above.
  - It saves less than it seems: the parsers' entropy walk (ALAC's elements, WMA Lossless's frames) is built in Gate 3 anyway, so deferring moves only the reconstruction.
  - Nothing in the contract stands in the way of adding them later.
- WavPack hybrid files with an MD5 stay unverified whatever the scope: their MD5 is of the lossless audio, and finding the `.wvc` of a `.wv` is under "Later" in the plan (host access to a companion file). Without an MD5, a hybrid file is verified as its lossy stream by its header CRCs (see the WavPack flow above).

### The verification record

```csharp
public enum ChecksumCoverage
{
    // A header only: it vouches for no audio.
    Header,

    // Coded bytes that hold audio.
    CodedData,

    DecodedAudio,
}

// One kind of checksum a stream stores, declared by the parser, and how its stored values compared.
public sealed record ChecksumTally
{
    // The format's name for it, as its field is named: "frame CRC-16", "STREAMINFO MD5", "lossless check, 6-channel
    // presentation". The decoder's results find their tally by it.
    public required string Name { get; init; }

    public required ChecksumCoverage Covers { get; init; }

    // Whether the stored values together cover all of the stream's audio: a checksum of the whole stream, or one that
    // every frame of the stream carries where no frame is missing (no gap, and the samples equal the count the stream
    // states, where it states one: STREAMINFO, the TTA or Monkey's Audio header, WavPack's total samples). The parser
    // sets it. Never for a header checksum, and never for TrueHD's lossless check, which leaves the last interval of
    // each segment unchecked.
    public bool CoversAllAudio { get; init; }

    // Stored values, those compared, and those of them that differ.
    public long Stored { get; init; }

    public long Checked { get; init; }

    public long Failed { get; init; }

    // Not compared: decoding off or stopped, a frame not decoded, a frame the data cuts short.
    public long NotChecked => Stored - Checked;
}

public enum DecodeStatus
{
    // A decoder exists, but the front end did not ask for decoding.
    NotRequested,

    NoDecoder,

    // The stream cannot be decoded as it is (TTA's encrypted format, a configuration the decoder refuses, a WavPack
    // correction file alone).
    NotPossible,

    // Every frame handed was decoded, or reported where it could not be.
    Decoded,

    // The front end stopped decoding or the budget ran out. (A sink that throws leaves no report: Analyze rethrows.)
    Stopped,

    // The decoder failed (decode.failed), a bug.
    Failed,
}

public sealed record StreamVerification
{
    public required DecodeStatus Decoding { get; init; }

    // The samples per channel the decoder decoded, the sum of what IFrameDecoder.Decode returned.
    public long DecodedSamples { get; init; }

    // True where the audio the checksums vouch for is a lossy coding's output: a WavPack hybrid file decoded without its
    // correction file. The host copies it from DecodeSetup.IsLossy, which the parser sets. Where IsVerified is true, the
    // text output and the GUI say "verified (lossy stream)"; JSON writes isLossy where it is true.
    public bool IsLossy { get; init; }

    public IReadOnlyList<ChecksumTally> Checksums { get; init; } = [];

    // False where a stored checksum differs. True where every stored checksum was compared and holds, and at least one
    // that was compared covers all of the stream's audio. Null otherwise.
    public bool? IsVerified =>
        Checksums.Any(c => c.Failed > 0) ? false
        : Checksums.Any(c => c.NotChecked > 0) ? null
        : Checksums.Any(c => c.CoversAllAudio && c.Checked > 0) ? true
        : null;
}

public sealed record StreamInfo
{
    // How far the checksums the stream stores vouch for it; set for the phase 3 codings and for WavPack in every mode,
    // null for the others.
    public StreamVerification? Verification { get; init; }
}
```

- "Verified" keeps the meaning it has in `SniffResult.VerifiedFrames`, a stored checksum compared that holds, and asks two things more of a stream:
  - every checksum it stores was compared;
  - at least one compared checksum covers all of its audio, coded or decoded.
- A tally with nothing stored counts for nothing: a TrueHD stream with one restart header has no lossless check to compare, and header CRCs never make a stream verified.
- A per-frame checksum vouches for all the audio only where no frame is missing: a FLAC file cut after a whole frame, or with a gap, whose CRC-16s all hold, is not verified by them.
- Examples:
  - A TTA stream whose frame CRCs all hold, with no gap and the samples its header counts, is verified without decoding.
  - A FLAC stream is verified once its MD5 is compared and holds; where its MD5 is 0, its CRC-16s alone verify it, if no frame is missing.
  - A Monkey's Audio file of 3.98 or later needs its frame CRCs compared, which needs decoding, though its descriptor MD5 holds already.
  - A TrueHD stream needs substream CRC-8s in every access unit, since its lossless checks never cover all of its audio.
- **Decision:** this meaning, for the phase 3 codings and WavPack in every mode. The alternatives are to count only checksums over decoded audio, which would leave out TTA's CRCs and Monkey's Audio's MD5, or to give the lossy formats' CRCs (MPEG, AC-3, Ogg pages) a verification now.
- A whole-stream checksum (FLAC's and WavPack's MD5) is compared only where `DecodeEnd.IsComplete`:
  - the decoder decoded every frame the parser counted;
  - no frame followed a gap;
  - the samples equal the stream's count.
- Otherwise it is not compared, and the finding that explains why is the one that stopped it.

### Findings

Every code follows the plan's severity rules: a stored checksum that differs is a warning (the file holds something damaged), a check that could not be made for a reason reported separately is an info, a decoder that fails is an error.

| Code | Severity | Points to | When |
| --- | --- | --- | --- |
| `flac.md5-mismatch` | Warning | the STREAMINFO MD5 field | the decoded audio's MD5 differs from the stored one |
| `monkeys-audio.frame-crc-mismatch` | Warning | the frame's CRC field | the frame's decoded audio does not have its stored CRC; once per stream with a count |
| `monkeys-audio.md5-mismatch` | Warning | the descriptor's MD5 field | the parser's MD5 over the coded data differs (3.98 and later, where the descriptor exists); the message says that the MD5 cannot tell where the damage is, and that the frame CRCs, compared with `--verify`, can |
| `wavpack.audio-crc-mismatch` | Warning | the block header's CRC field | a block's decoded audio does not have its header CRC (the extended CRC included); once per stream |
| `wavpack.md5-mismatch` | Warning | the MD5 sub-block | lossless decoding gives another MD5 |
| `wavpack.md5-needs-correction-file` | Info | the MD5 sub-block | a hybrid file decoded without its `.wvc`, whose MD5 is of the lossless audio |
| `wavpack.md5-set-too-large` | Info | the MD5 sub-block | a set of blocks holds more than the ceiling's 2^24 samples, so its blocks were decoded one at a time and the MD5, which needs them interleaved, was not compared |
| `truehd.lossless-check-failed` | Warning | the restart header's lossless check byte | a presentation's output since the previous restart header does not give it; once per stream and presentation |
| `<format>.decoded-samples-differ` | Warning | the frame | a frame decodes to another number of samples than its header gives |
| `decode.frame-invalid` | Warning | the frame | the decoder could not decode a frame (`InvalidDataException`, or data that ends inside its coded audio); once per stream with a count; decoding carries on with the next frame |
| `decode.frame-too-large` | Info | the frame | the frame is past the decoder's memory ceiling (below) and is not decoded; decoding carries on, and the whole-stream checksums are not compared |
| `decode.no-decoder` | Info | the stream | decoding was asked for and no decoder has the stream's codec ID |
| `decode.not-possible` | Info | the stream | the decoder refused the configuration or the stream cannot be decoded alone; the message says why |
| `decode.stopped` | Info | the stream | the front end stopped decoding; the message says how many frames were decoded |
| `decode.limit` | Info | the stream | the analysis's PCM budget ran out (below) |
| `decode.failed` | Error | the stream | any other exception from the decoder, or results that name no declared checksum or compare more values than are stored: a bug, never an acceptable result, like `parse.failed`; that stream's decoding stops and its decoder results are dropped |

- The decoder builds the node of a mismatch with its parser's node factories, equal in name, offset and length to the one the parser builds, so the host resolves its path as for any finding.
- The computed value goes into the finding's message ("computed 3e84…, stored 9a1f…").
- Decoding adds no node, so the tree is the same with decoding and without it, which a test holds for every fixture.

### When decoding is skipped

- The stream's verification says `NotRequested` (or `NoDecoder`) and lists every checksum the parser declared: the ones over coded data compared, the ones over decoded audio with `Checked` 0. The report thus says that an MD5 exists and was not compared.
- No finding is added: a default analysis of a FLAC file stays as quiet as any other.
- The descriptions of the stored checksum fields say whether they are compared in every analysis or only when the stream is decoded.

### Where the verdict shows

- **JSON:** `verification` in each stream, after `protection`:
  - `isVerified`, written where it is not null;
  - `isLossy`, written where it is true (a WavPack hybrid file decoded without its `.wvc`);
  - `decoding`: `notRequested`, `noDecoder`, `notPossible`, `decoded`, `stopped` or `failed`;
  - `decodedSamples`;
  - `checksums`, each with `name`, `covers` (`header`, `codedData`, `decodedAudio`), `coversAllAudio`, `stored`, `checked` and `failed`.
  - A field is added, so `schemaVersion` stays 1.
- **Text:** a line under each stream, such as "Verified: yes (frame header CRC-8 40 of 40, frame CRC-16 40 of 40, STREAMINFO MD5 1 of 1)", "no (…)" or "unknown (STREAMINFO MD5 not compared: decoding was not asked for)". A WavPack hybrid file decoded without its `.wvc`, verified and with `IsLossy` set, reads "Verified: yes (lossy stream; block header CRC 120 of 120)".
- **CSV:** `Is Verified` after `Is Protected`, filled only where decoding was asked for (`--verify`): false where a stream's is false, true where every stream's is true, empty otherwise. Without `--verify` it is empty for every file, so a folder export never mixes verdicts from coded data alone (TTA) with empty ones (FLAC). `ReportSummary` gains the field, so the GUI's summary cache format becomes 6.
- **GUI:** the stream line of the detail window names the verdict, "verified (lossy stream)" where the stream is verified and `IsLossy` is set (a WavPack hybrid file without its `.wvc`), and links to the article. The file list gets no column in Gate 3, for the same reason as the CSV.
- **Article:** `verification` in `src/Encspect.Host/Topics`: what stored checksums cover (a header, coded data, decoded audio), why an MD5 needs decoding, what "verified" means in the report. The stored checksum fields of the lossless formats point to it.

## When decoding runs

### Cost

- Decoding reads every coded bit into buffers and writes every sample. The structure pass of a format that walks its entropy-coded data (FLAC, ALAC, TrueHD) does the first half already (see [The second entropy pass](#the-second-entropy-pass)).
- Decoding time grows with the samples decoded, with a constant per coding:
  - FLAC's predictor uses at most 32 coefficients a sample;
  - Monkey's Audio's highest level about 1,550 filter taps per sample of each channel (16 + 256 + 1,280, `apedec.c` 81-87), so it decodes far slower than FLAC, and its filters need `System.Numerics.Vector`.
- Step 1's prototype measured FLAC: the decoder alone, without the structure walk, runs at 340 to 610 times real time on the build machine's one thread (see [The second entropy pass](#the-second-entropy-pass)). The real decoder's figures, and Monkey's Audio's, come with the steps that build them and are recorded in the plan.

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

    // The analysis's PCM budget in bytes of the canonical layout; null for the default (below). Tests and the fuzzer
    // lower it. An option of the call, not a setting.
    public long? MaxPcmBytes { get; init; }
}

public sealed record DecodedStream(string CodecId, Node Stream, PcmFormat Format);
```

- **CLI:** off; `--verify` sets `Decoding`. `--fail-on warning` already turns a mismatch into a failing exit code.
- **GUI:** off. The detail window gets a "Verify Audio" command, enabled where a stream's verification says `NotRequested`. It analyzes the file again with decoding, with progress and Stop, and replaces the report when done; the tree is the same, so the selection stays.
- **File list:** never decodes. A folder of thousands of files would cost minutes of decoding per gigabyte, and the list's summaries stay what they are.
- **Summary cache:** never holds a verdict over decoded audio. Its key (size, last-write time, file ID and a hash of the first and last 64 KiB) does not see a byte changed in the middle of the file, which is exactly what a verification is about.
- **Coded-data checks in every analysis:** they cost reading the bytes they cover. The FLAC, TTA and WavPack parsers read every frame anyway. Monkey's Audio's parser otherwise reads only the headers, the seek table and the frame starts; its descriptor MD5, which exists only from 3.98 on, makes it read and hash the whole file, also in the file list (see decision 3). A mismatch says the coded data changed, not where; the frame CRCs, compared with `--verify`, locate it.
- **Decision:** off everywhere, with the commands above. The alternative is to decode by default in the detail window, which opens one file at a time.

### Cancellation, progress and partial results

- Decoding runs in the structure pass, so `AnalysisOptions.BytesRead` reports its progress as it does now; it just moves slower.
- `AnalysisOptions.CancellationToken` cancels everything, as now. `DecodeOptions.Stop` ends every open session at the next frame; the parse carries on to the end without decoding.
- A stopped session keeps what it found: a frame CRC that failed stays reported, the tallies count what was compared, and the whole-stream checksums say "not compared". The stream's `Decoding` is `Stopped`.

### Ranges

- A session decodes whole streams. Decoding only a range, which a zoomed spectrogram would want, needs no contract change: the host can skip the decoder's calls outside the range and hand the first frame inside it as following a gap.
- The structure pass still reads the whole file, so a range costs at least a structure analysis.

## Resources and robustness

### Memory

- Nothing grows with the stream: a session holds one frame's buffers and the state the coding carries from frame to frame (TrueHD's filter and matrix state, Monkey's Audio's filters). The report keeps only the tallies.
- Buffers are the session's own arrays, grown to the largest frame seen and dropped with the session. `ArrayPool<T>.Shared` is not used for them: it keeps large arrays for the whole process after the analysis, which `MemoryTests` would read as growth.
- Each decoder has a memory ceiling per frame, sized by the frame at hand and never by a declared size alone. A frame past it is not decoded (`decode.frame-too-large`), and decoding carries on with the next frame:

| Coding | Largest frame | Ceiling |
| --- | --- | --- |
| FLAC | 65,535 samples, 8 channels, 32 bits (RFC 9639, 8.2) | about 2.6 MB, with 64-bit buffers for the side channel of 32-bit streams |
| Monkey's Audio | 73,728 samples per channel, × 4 at extra high and × 16 (1,179,648) at insane (`APECompressCreate.cpp` 77-81); the SDK accepts up to 10,000,000 at level 5000 and above and 1,000,000 below (`APEHeader.cpp` 305-317), with up to 32 channels (`All.h` 398) | none per frame from 3.93 on: the decoder decodes a frame in chunks of 4,096 samples per channel, with its CRC updated as it goes, so the frame's length bounds time, not memory; the SDK's limits are accepted as they are. Before 3.93 a frame's coefficients are not interleaved, so it is decoded whole (`apedec.c` 1619-1622), and its length is fixed by the version at 9,216 or 73,728 samples per channel (`ape.c` 224-229), at most 2 channels, which bounds the memory |
| WavPack | under 196,608 samples a block (`read_next_header`); up to 4,096 channels across the blocks of one set | 2^24 samples for the blocks of one set, which the MD5 needs interleaved; past it the blocks are decoded one at a time, their CRCs compared, and the MD5 is not (`wavpack.md5-set-too-large`) |
| TrueHD, MLP | 160 samples, 8 channels (`mlp.h`) | negligible |
| ALAC, later | the configuration's frame length, which FFmpeg accepts up to 4096 × 4096 (`alac.c` 530-532) | 2^20 samples per channel |

### The exception boundary

The host's session wraps every call to the decoder:

- `InvalidDataException`, and `EndOfStreamException` from a frame's own bytes: `decode.frame-invalid` on the frame; the next frame is decoded.
- An `InvalidDataException` from `Open`: `decode.not-possible` with its message. Any other exception from `Open` is `decode.failed`.
- `OperationCanceledException` from the analysis's token passes through; the stop token ends the session with `decode.stopped`.
- Any other exception: `decode.failed`, an error naming the decoder and the exception; that session decodes nothing more, and the analysis carries on.
- The fuzzer fails on `decode.failed` as it does on `parse.failed`.

Exceptions from the front end, which run inside the parser's call stack, never travel through parser code:

- The sink the decoder gets is the host's. It catches every exception from the front end's `IPcmSink` and from `DecodeOptions.Output`, keeps the first one (`ExceptionDispatchInfo`), ignores further writes and ends every session of the analysis at the next frame.
- Parsers catch `InvalidDataException` and `EndOfStreamException` themselves, and the host turns other exceptions into `parse.failed`; a front end's exception that reached them would become a finding about the file.
- Once the format parser returns, the host rethrows the kept exception to the caller of `Analyze`, without a report.

### Per-call state

- `IDecoder` keeps no state in fields; everything of a stream lives in the `IFrameDecoder` that `Open` returns.
- The host calls one session on the thread that parses its stream, and ends every session when its route or the format parser returns.
- Nothing in the report refers to a decoder or a session: `StreamVerification` is plain data, so `InputReleaseTests` hold with decoding on.

### Complexity against crafted input

- **Unary codes and escapes:** `BitCursor.CountZeros` and `CountOnes` stop at the end of the frame's bytes, so a Rice code that claims gigabytes (RFC 9639, section 11) costs the bits it has.
- **Orders, partitions and channels:** every count is checked against the format's maximum before anything is allocated or looped over, and a value past one is the parser's finding; the decoder does not decode that frame:
  - FLAC: a predictor order of at most 32 and at most the block size, a partition order of at most 15 whose partitions fit the block;
  - Monkey's Audio: the filter orders of its compression levels;
  - TrueHD: `MAX_FIR_ORDER` 8, `MAX_IIR_ORDER` 4, `MAX_MATRICES` 8;
  - WavPack: 16 decorrelation terms.
- **Amplification:** a 49-byte FLAC frame codes more than 2 MB of PCM (RFC 9639, section 11), so 100 MB of such frames code about 4.3 TB.
- **The budget:** the host counts the PCM bytes all sessions of an analysis decode, in the canonical layout (`ceil(bits / 8)` bytes a sample, from `DecodeSetup.BitsPerSample` and what `Decode` returns), whether or not a sink takes them. Once they pass the larger of 16 GiB and 256 times the file's size, it stops every session with `decode.limit`; `DecodeOptions.MaxPcmBytes` replaces that bound.
- What the budget allows:
  - An hour of 8 channels of 24 bits at 192 kHz is 16.59 GB in 3-byte samples and fits under the 16 GiB (17.18 GB).
  - Music coded losslessly gives a few bytes of PCM per coded byte, far below 256 (not measured here).
  - A crafted file of 100 MB stops after 25.6 GB of PCM, about 43 seconds of MD5 at an estimated 600 MB/s, instead of hours.
- **Decision:** these two numbers, to be tuned once measured. The alternative is no bound in the library, leaving the front ends' Stop as the only one.
- **Per-frame time:** a frame's work is bounded by the format's largest frame times its per-sample constant, so the host's check between frames bounds the latency of cancellation; Monkey's Audio checks inside its frames.

## The PCM path

Defined now so that waveform, spectrogram and lowpass detection need no change to the contract.

```csharp
public enum SampleFormat
{
    // Signed integers in the low BitsPerSample bits of an int, sign-extended (8-bit WAV's unsigned samples converted).
    Integer,

    // IEEE 754 single precision (WavPack, WAV and AIFF-C floats of 32 bits).
    Float32,

    // IEEE 754 double precision (WAV and AIFF-C floats of 64 bits).
    Float64,
}

public sealed record PcmFormat(int SampleRate, int Channels, int BitsPerSample, SampleFormat Format)
{
    // The speaker of each channel in the stream's channel order, which is the format's, as StreamInfo.ChannelLayout.
    public IReadOnlyList<Speaker>? ChannelLayout { get; init; }
}

// Samples of every channel for one stretch of the stream, planar: channel c holds the Length samples from c * Stride on,
// in the one span of the block's sample format. Valid only during IPcmSink.Write.
public readonly ref struct PcmBlock
{
    // The first sample's position in the stream in samples per channel, before any gapless trimming; where frames were
    // lost, the next block's position jumps.
    public long Position { get; }

    public int Length { get; }

    public int Channels { get; }

    public int Stride { get; }

    public SampleFormat Format { get; }

    public ReadOnlySpan<int> Integers { get; }

    public ReadOnlySpan<float> Floats { get; }

    public ReadOnlySpan<double> Doubles { get; }

    public ReadOnlySpan<int> IntegerChannel(int channel) => Integers.Slice(channel * Stride, Length);
}

public interface IPcmSink
{
    // On the analysis thread, in stream order; a sink copies what it keeps.
    void Write(PcmBlock block);

    // Called once by the host when the stream's decoding ends, with the samples per channel decoded; a decoder never calls it.
    void End(long samples);
}
```

- Planar, because waveform and spectrogram read one channel at a time; one span with a stride, because a ref struct cannot hold a variable number of spans.
- 32-bit integers, because every lossless coding in phase 3 outputs samples of at most 32 bits; FLAC's 33-bit side channel stays inside the decoder (64-bit buffers, `ReadSignedLong`).
- Gapless trimming (TrueHD's samples to drop, an MP4 edit list) is the report's business, not the PCM's: positions are untrimmed, and a later feature trims with what the stream reports.
- `Core.Abstractions` also gets the canonical byte layout as one function: interleaved, signed, little-endian, `ceil(bits / 8)` bytes a sample. FLAC's MD5 uses it, and so do the tests' hashing sink, the manifests' `decoded_pcm_md5` and the budget's count. Monkey's Audio and WavPack compute their checksums over their own layouts.

### Host-side PCM analyses, later

Lowpass detection is an analysis result, so it belongs in the library ("A library others could use"), not in a front end. The hook is fixed now; the analyses come with their features:

- The host's sink (`DecodeContext.DeclareOutput`) forwards each block to the front end's sink and to the host's own PCM analyses, an internal list in `Encspect.Host` that is empty in Gate 3.
- An analysis adds figures to the stream, never nodes. The rule becomes "decoding never changes the parser's nodes".
- The first such analysis adds one field to `StreamInfo` for its figures, as `Verification` is added now. That is additive (`schemaVersion` stays 1).
- The figures reach the stream through the host: once the format parser returns, the host attaches each session's figures to the stream whose first node is that session's `DecodeSetup.Stream`, as it sets `NodePaths` on the streams today. So no parser, decoder, session or sink changes.
- **Decision:** this hook now, the figures with the first feature. The alternative is to leave lowpass detection to a later contract change, which would then change `DecodeContext` and the rule.

## Verifying the decoders

### Fixtures and manifests

- Every fixture of a decoded coding gets `decoded_samples` and `decoded_pcm_md5` (the canonical layout) in its manifest, and the verification the analyzer must report.
- The fixture scripts compute them from a reference decoder's output, never from the analyzer; the cross-check job recomputes them with a second decoder where one exists.
- `DecoderFixtureTests` decodes every such fixture with a hashing sink and holds the analyzer to the manifest, and checks that the tree equals the one of the analysis without decoding.
- **Damage that only the decoded audio shows**, hand-built for each coding:
  - A residual bit flipped with the frame's coded checksums recomputed (and, for Monkey's Audio, the descriptor MD5, since it covers the coded data), so every coded check holds and only the MD5, the Monkey's Audio frame CRC, the WavPack header CRC or the TrueHD lossless check fails.
  - A stored MD5 changed.
  - A frame cut short, so the whole-stream checksum is not compared.
  - A FLAC file with an MD5 of 0, cut after a whole frame, and one with frames removed from the middle: every CRC-16 holds, and the stream must not be verified (the parser reports the missing samples or the gap).
- The reference tool must agree with each verdict:
  - `flac -t` reports the MD5;
  - FFmpeg with `-err_detect crccheck`, and the SDK's `macutil` verifying the file, report the Monkey's Audio CRC;
  - FFmpeg's log reports the lossless check (with `-downmix` for the lower presentations);
  - `wvunpack` reports the CRC errors and the MD5.
- **Monkey's Audio insane level:** a fixture from the SDK at its insane level, whose frames hold 1,179,648 samples per channel.
- **TrueHD:** FFmpeg's MLP and TrueHD encoder is experimental (`mlpenc.c` 2304, 2325) and writes one substream for MLP and at most two for TrueHD (`mlpenc.c` 589). Three presentations, Dolby Atmos and real encoders' output therefore need real files, downloaded at pinned SHA-256 and never committed, as for DTS-HD and E-AC-3. FFmpeg's encoder always writes the substream parity and CRC-8 (`mlpenc.c` 1073-1080); no source says whether Dolby's encoders do, so the real-file check records how often each substream carries them, since a TrueHD stream without them is never verified.

### Reference decoders

| Coding | Reference | In Ubuntu 26.04 |
| --- | --- | --- |
| FLAC | `flac -d`, `flac -t`, `metaflac --show-md5sum`; FFmpeg | flac 1.5.0+ds-5, which `tool_versions.py` pins at 1.5.0; FFmpeg 8.0.1 |
| Monkey's Audio | the SDK's `macutil`, built from Monkey's Audio SDK 13.42 (3-clause BSD, CMake), pinned by SHA-256; FFmpeg for 2 channels up to 24 bits | not packaged in any Ubuntu series; the SDK build also covers more than 2 channels, 32 bits and floats, which FFmpeg cannot decode |
| WavPack | `wvunpack -m`, libwavpack's own pass (`wavpack_blocks.c`, already built); FFmpeg | wavpack 5.9.0 |
| TrueHD, MLP | FFmpeg's decoder, with `-downmix` for the lower presentations | FFmpeg 8.0.1 |
| ALAC, TTA, WMA Lossless (later) | FFmpeg; Apple's ALAC source; libtta-c 2.3 (LGPLv3, autotools), pinned by SHA-256 | TTA is not packaged in any Ubuntu series |

- The ASF cross-check already compares FFmpeg's WMA Lossless output with the encoder's input bit for bit. For the other codings, the cross-check compares FFmpeg's output with the reference coder's own (flac, the SDK, libwavpack), so no claim rests on FFmpeg alone.
- Each script asserts the tool versions it was written for (`tool_versions.py`), as the existing cross-checks do.

### Tests beyond the fixtures

- **Contract:** `DecodeRouteTests` with a scripted parser and decoder, as `FormatDetectionTests` scripts parsers:
  - the boundary for each exception, from `Open`, `Decode` and `Finish`;
  - a sink and an `Output` that throw: no `parse.failed`, the exception rethrown from `Analyze`;
  - the stop token, the budget through `MaxPcmBytes`, and `decode.frame-too-large`;
  - findings counted once per stream, sessions inside nested routes, sessions ended when a route returns with their findings merged;
  - `IsVerified` for each case of the rule, tallies with nothing stored included, and a per-frame tally whose stream has a gap or fewer samples than it states (not verified, though every value holds);
  - `Decode` returns of 0 for all but a set's final block, counted once in `DecodedSamples` and the budget;
  - results that name no declared checksum, or compare more values than are stored (`decode.failed`, results dropped).
- **Bit cursor, MD5 and CRC-32:** `BitCursorTests` against `BitReader` on random data and at every end; `Md5Tests` with RFC 1321's vectors and against `System.Security.Cryptography.MD5`, in the test only; `Crc32Tests` against the existing copies.
- **Boundary:** `DecodingBoundaryTests` (above), with its negative control.
- **Fuzzing:** `Encspect.Fuzz` analyzes the lossless fixtures' mutations with decoding on as well, with a lowered `MaxPcmBytes`, and fails on `decode.failed`; its finds join `tests/fixtures/fuzz`.
- **Memory:** `MemoryTests` analyzes a long stream of each decoded coding with decoding on and reads the live heap from inside the sink's `Write` of the last frame, where everything a session keeps per frame is still alive; the growth of that reading above the heap the analysis ends with, between two stream lengths, is bounded per further frame, so a session that keeps something per frame fails (see "Memory tests" in `docs/development.md`).
- **Complexity:** `ComplexityTests`, with a lowered `MaxPcmBytes` so the budget stops them in seconds:
  - FLAC frames of 65,535-sample constant subframes in 8 channels at 32 bits;
  - Rice codes that run to the end of each frame;
  - predictor order 32 at the largest block;
  - the highest Monkey's Audio level.
- **Threads:** `ParallelAnalysisMatchTests` compares parallel and sequential decoding of the lossless fixture directories.
- **Golden reports:**
  - The default golden of each fixture stays the full report; for the phase 3 codings and WavPack it now holds the verification with what was not compared.
  - A small `<name>.decode.json` holds what decoding adds (the decoder's tallies and the decode findings), compared on every run and rewritten with `ENCSPECT_UPDATE_GOLDEN=1`, so the full report is not stored twice.

## Rollout

1. **Contract** (one pull request):
   - the types above in `Core.Abstractions`, with `BitCursor`, `LsbBitCursor`, `Md5`, `Crc32` and the canonical layout;
   - the router's sessions, boundary, sink guard and budget in the host; `AnalysisOptions.Decoding`; the writers; `--verify`;
   - the `verification` article;
   - `DecodeRouteTests`, `DecodingBoundaryTests`, `BitCursorTests`, `Md5Tests`, `Crc32Tests`;
   - the throwaway FLAC prototype and its measurement, which settle [the second entropy pass](#the-second-entropy-pass) before this pull request merges;
   - `ffmpeg_sources.py`, whose `FILES` list holds none of the files this design cites yet: it gets the FLAC, Monkey's Audio, TTA, TrueHD and MLP, and ALAC files, and `check_sources.py` checks them; the citations are to 6.1.1, and CI's FFmpeg 8.0.1 keeps the key lines (see Sources);
   - documentation: `--verify` in `docs/cli.md`, the verification output in the JSON and CSV sections there, the decode goldens and manifests in `docs/development.md`;
   - the lines of `CLAUDE.md` and the plan below.
   - No format changes.
2. **FLAC, the reference implementation:**
   - `Encspect.Formats.Flac`: the raw format, the codec "flac" (which Ogg already routes by that ID), and the decoder in `Encspect.Formats.Flac.Decoding`;
   - fixtures from `flac` and FFmpeg and hand-built damage; a `flac-crosscheck` job;
   - the GUI's "Verify Audio", documented in `docs/gui.md`;
   - `docs/formats/flac.md` with a "Decoding" section, and its line in `docs/formats/README.md`.
   - A milestone review of the contract and FLAC follows, which also walks the WavPack and TrueHD data flows (above) through the contract as built: which `CodedFrame`s, which state, which tallies, which findings. Its findings are fixed before step 3 starts.
3. **In parallel, each in its own pull request, with its decoder in its format project's `Decoding` namespace and its format's "Decoding" section:**
   - the WavPack decoder (its parser exists), and a "wavpack" codec parser for Matroska's blocks, which carry a reduced header that FFmpeg rebuilds into the 32-byte one (`matroskadec.c` 3608-3670);
   - Monkey's Audio, parser and decoder;
   - TrueHD and MLP, parser and decoder, raw files first;
   - TTA as a format parser and a "tta" codec parser for Matroska's frames, which carry no header (FFmpeg builds it from the track, `matroskadec.c` 2700 and on), without a decoder;
   - WAV and AIFF parsers, without decoders;
   - ALAC's codec parser and WMA Lossless's frames, without decoders if the scope decision moves them.
4. **Containers, one at a time:** each container's routes are their own pull request, after the codec parsers they route to.
   - MP4: FLAC's `fLaC` entry, `alac`, `mlpa`.
   - Matroska: `A_FLAC`, `A_ALAC`, `A_TRUEHD`, `A_MLP`, `A_TTA1`, `A_WAVPACK4`.
   - Ogg needs no change for FLAC, and ASF none for WMA Lossless, whose route exists.

Lines applied with the contract pull request (step 1):

- `CLAUDE.md`, Stack: "no third-party decoders or audio libraries" in place of "no bundled decoders or audio libraries", since Encspect's own decoders are part of the build.
- `CLAUDE.md`, Layout, in place of "Format parsers and tag parsers are added as further projects.": "Format parsers and tag parsers are added as further projects. A format's decoder, where it has one, lives in its format project under the namespace `<project>.Decoding`; the host registers it next to the parsers."
- `CLAUDE.md`, Architecture rules, in place of "They never reference each other and never reference a decoder.": "They never reference each other, and parser code never references a decoder, the `Decoding` namespace of its own project included; `DecodingBoundaryTests` checks this in the compiled assemblies."
- `CLAUDE.md`, Architecture rules, a new item: "Parsers reach decoding only through `StartDecoding` on their context, and declare every checksum a stream stores. A decoder reads the coded syntax with its parser's code and adds only reconstruction and the checksums over decoded audio; decoding never changes the parser's nodes."
- `CLAUDE.md`, Architecture rules: "One parser instance serves every analysis and thread" becomes "One parser or decoder instance serves every analysis and thread", and "Parsers throw only …" becomes "Parsers and decoders throw only …", since decoders live in the same projects and the contract asks both of them (`IDecoder`).
- Applied to the plan with the approval (2026-10-22): the Architecture sentence "Decoders are separate and optional, so no parser ever depends on one." now reads "Decoders are optional and live in their format's project apart from its parsers, which reach them only through the host, so no parser ever depends on one.", and the architecture diagram shows the decoders in the format projects, with frames to decode and decoded PCM going through the host.
- The plan's decision table, with the contract pull request: decoding is "for checksum verification, waveform, spectrogram, lowpass and similar". The phase 3 row names the decoders the scope decision keeps.

## As built in step 1

What the contract pull request implements, and where it differs from the signatures above (the smallest consistent fix each time):

- **Lending:** `IDecodeSession.Lend` takes `bool needsWide = false`, since the session cannot know whether the 64-bit planes of a 32-bit FLAC stream are needed. `DecodeContext` gets the lent buffers through `LentBuffers` (backed by a `LentBuffersProvider` delegate, since a ref struct cannot be a type argument of `Func`), and its constructor is public so that a test or another host can build one. The session lends at most 2^24 samples over all channels and returns empty buffers past it.
- **Ending a session:** `Finish` is idempotent (the second call returns the first verification). A session ended without `Finish` calls `IFrameDecoder.Finish` with `IsComplete` false and drops the results; a decoder that throws there is `decode.failed`.
- **Boundary:** an `EndOfStreamException` from `Open` is `decode.not-possible` ("the configuration ends in the middle of a structure"), not `decode.failed`, since a decoder may throw it. From `Finish`, every exception (`InvalidDataException` included, which has no frame to point to) is `decode.failed`. A negative return from `Decode` is `decode.failed`. `OperationCanceledException` always passes through.
- **Results:** a decoder's results are accepted for the tallies the parser declared over decoded audio only; a result that names another tally (a header or coded-data checksum, whose values the parser compared), names one twice, or has `Failed` above `Checked` or `Checked` above `Stored` is `decode.failed`. The host sets `Checked` and `Failed` of every tally over decoded audio from the results, 0 where there is none. Two tallies of one name make `Finish` throw `ArgumentException`.
- **Completeness:** `DecodeEnd.IsComplete` is true when the session was not ended early (stop, budget or sink failure), no frame followed a gap, no frame failed, and `DecodeSetup.Samples`, where set, equals the samples decoded. `FramesNotDecoded` counts the frames the decoder was handed and could not decode.
- **Findings:** the host's `decode.stopped` and `decode.limit` are added once per session; `decode.no-decoder` is added only where decoding was asked for. A finding without a node points to the stream.
- **Budget:** the default is the larger of 16 GiB and 256 times the size of the whole file (`DecodeOptions.MinimumBudget`, `BudgetPerInputByte`); a session that passes it stops at once, and the others at their next frame. A negative `MaxPcmBytes` is an `ArgumentOutOfRangeException` from `Analyze`.
- **Byte layout:** `CanonicalPcm` (`BytesPerSample`, `ByteCount`, `Write`) is the canonical layout; floats are written as their IEEE 754 bytes.
- **`Crc32`:** `Update` works on the register, as the copies the formats keep; `Initial`, `Finish` and `Of` are added. `Md5` has `HashLength`.
- **Output:** `VerificationText.Describe` is the text of the verification for the text tree and for a front end; the text tree prints "Verified: …" under the stream's first node, one level deeper. `ReportSummary.IsVerified` holds the verdict of the audio streams, and the GUI's summary cache stores it (format 6); the detail window shows it from step 2.
- **CSV:** `ReportCsvWriter.Write(reports, writer, isVerifying)` fills `Is Verified` only when `isVerifying` is true.
- **Sink:** the host alone ends the PCM sink (`IPcmSink.End`, once, with the samples it counted; also for a session ended without `Finish`); the sink a decoder gets ignores `End`, so a front end never sees two. A sink that throws only in `End` is rethrown like any other.
- **Setup figures:** a stream that states fewer than 1 channel, or a sample width outside 1 to 64 bits, is not decoded: `decode.not-possible` (status `NotPossible`) when decoding was asked for, nothing otherwise. A crafted file can state these, so a parser may pass them as they are; a null setup or stream is the parser's bug.
- **Tallies:** a parser's tallies are checked in `Finish`: no negative count, `Checked` at most `Stored`, `Failed` at most `Checked`, no `CoversAllAudio` on a header checksum, and no two of one name. A violation is a parser bug and throws `ArgumentException` (`parse.failed`), not `decode.failed`, which is for decoders.
- **Lent buffers:** `DecodeContext.LentBuffers` is empty unless the frame being decoded has `IsWalked`.
- **Verdict text:** the null verdict reads "Verified: unknown (…)" in the text tree. The reason a checksum over decoded audio was not compared when decoding ran but the stream is incomplete names the three causes: a frame not decoded, a frame missing (a gap), or samples that differ from the stated count.
- **Analyze:** a negative `MaxPcmBytes` is refused before anything is read, for every input; the exceptions `Analyze` throws are listed in its comment.
- **CLAUDE.md:** the sentence "Design documents are drafted on Opus with high effort; fixes to them count as implementation." in Verification is not one of the proposed lines: the project lead added it to this pull request on 2026-10-22. Two of the proposed lines are completed for decoders: the host reports `decode.failed` for a decoder as it reports `parse.failed` for a parser, and a decoder checks `DecodeContext.CancellationToken`.
- **For step 2:** `decode.frame-too-large` is listed among the contract tests but cannot be tested before a decoder exists, since only a decoder knows its ceiling. The FLAC decoder's ceiling is 65,535 samples of eight channels, which no frame header can exceed, so the guard is unreachable and no test covers it; the next decoders are to say whether theirs can be reached.
- **Not built:** the host's internal list of PCM analyses (the sink forwards to the front end's sink only), and every decoder: `DefaultParsers.Decoders` is empty.

## As built in step 2

What the FLAC pull request implements: the parser (`Encspect.Formats.Flac`), the decoder (`Encspect.Formats.Flac.Decoding`, ID `flac`, registered in `DefaultParsers.Decoders`), the codec parser for Ogg FLAC, "Verify Audio" in the GUI and the `flac-crosscheck` job. [flac.md](../formats/flac.md) describes it from the user's side.

- **Lending:** the parser's walk is the only reader of the coded syntax: `SubframeSyntax.Read` fills the lent planes (warm-up samples and residuals) and 38 meta slots after the samples (type, order, wasted bits, coefficient precision and shift, whether the plane is wide, 32 coefficients), so a lent stride is the block size plus `SubframeLayout.MetaSlots`. The decoder reads those and reconstructs. `DecodingBoundaryTests` stay green: the decoder references no reader of its own.
- **Frames the walk did not store:** a frame that was walked without a session (or by a parser that does not lend) is read by the decoder with the same `SubframeSyntax.Read` into buffers of its own. This is the path the measurement below uses for "walk, then decoder"; it is covered by a test.
- **The wide plane** is chosen before the wasted bits are subtracted (`frameBits + side > 32`): a 32-bit side channel with wasted bits still needs 64-bit planes for the samples of the frame, and the 33-bit fixture caught the decoder taking the narrow plane.
- **Checksums:** three tallies, declared by the parser: "frame header CRC-8" (header), "frame CRC-16" (coded data) and "STREAMINFO MD5" (decoded audio). `CoversAllAudio` of the CRC-16 is true only when no frame is missing, the frames' samples equal STREAMINFO's count, no frame was cut and none was unreadable. An MD5 of 0 is "not stored", not a mismatch. Fixtures where every CRC holds but frames are missing, repeated or out of order (the `*-md5-zero` ones) are not verified, which the contract's R2 asked for.
- **Frames the decoder refuses:** a frame whose channels or bit depth differ from STREAMINFO's cannot go into the PCM layout the session declared. The decoder throws `InvalidDataException` for it, so the host reports `decode.frame-invalid` on the frame (a warning), the stream's decoding is `Decoded`, the samples are fewer and the stream is not verified. This is the contract's exception boundary and it is kept: a decoder cannot change the stream's status after `Open`, and a per-frame refusal is what the boundary is for. Only a STREAMINFO the decoder cannot use at all (fewer than 4 bits per sample, or none) is `NotPossible`.
- **Resync:** a header counts as a frame when its CRC-8 holds and either a consistent header follows or the stream ends; a frame whose CRC-8 fails is taken when its walk completes and a header follows (a flipped bit in the CRC-8 byte also fails the CRC-16 of an intact frame). The search past damage reads at most 64 MiB plus four times the file. A frame is at most 16 MiB (`MaxFrameBytes`).
- **Deviations from this document:**
  - `decode.frame-too-large` cannot be reached by any file: a frame header gives at most 65,535 samples and a stream has at most eight channels, which is exactly the decoder's ceiling of 65,535 x 8 samples. The guard stays, since the constant is the decoder's own statement of what it handles, and no test reaches it.
  - An unreadable frame (a header that reads and a subframe that breaks the syntax) makes `CoversAllAudio` of the CRC-16 and of the MD5 tally false although the frame count may match, since that frame's samples are missing from the MD5.
  - A frame whose CRC-8 fails is decoded when the walk completes and a header follows; one whose extent was only found by search (`ExtentFound`) and whose walk completed is decoded too. The frames that fail the CRC-16 or are cut are handed to the decoder flagged `IsDamaged`.
- **GUI:** "Verify Audio" in the detail window analyzes the file again with decoding on while the old analysis stays on screen; Stop cancels it; a failure shows as "Not verified". The file list and the summary cache never decode. See [gui.md](../gui.md).
- **Measurement on the built decoder** (below); its harness is `FlacDecodingMeasurement` in `tests/Encspect.Tests`, run as [development.md](../development.md) says.

### The second entropy pass, measured again on the built decoder

The step 1 prototype used FFmpeg's encoder on synthetic signals. This run uses the real parser and decoder, files written by `flac` 1.4.3, and music.

- **Machine and build:** as in step 1 (Intel Xeon at 2.80 GHz, 4 vCPUs, Linux 6.18, .NET 10.0.401, Release, one thread). Each figure is the best of 5 runs after a warm-up, over the whole analysis of the file from the page cache.
- **Files:** `flac` 1.4.3 at levels 0, 5 and 8. Synthetic: 10 minutes of 16-bit 44.1 kHz stereo, three sines (440, 1,000 and 3,000 Hz) plus white noise at 0.002, 0.05 and 0.3 of full scale. Music: two commercial tracks of 3:46 and 4:20, decoded from MP3 to 16-bit 44.1 kHz stereo (so music with a lossy source's spectrum; the files are not committed).
- **Variants:** A is the analysis without decoding. B is the analysis with decoding as built (the walk stores into the lent buffers). C is the analysis with decoding and the walk not storing (`FlacParser.LendsBuffers = false`), so the decoder reads every frame again: the contract as first drafted. The decoder alone of step 1 corresponds to D = C - A; the second read, net of what the stores add to the walk, to S = C - B.

| File | Length | A | B | C | D = C - A | S = C - B | S / D | Decoding alone (B - A), times real time |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| music 1, level 5 | 3:46 | 0.470 s | 0.755 s | 0.885 s | 0.415 s | 0.130 s | 31.3 % | 791 |
| music 1, level 8 | 3:46 | 0.412 s | 0.756 s | 0.940 s | 0.528 s | 0.184 s | 34.9 % | 657 |
| music 2, level 5 | 4:20 | 0.469 s | 0.829 s | 1.027 s | 0.558 s | 0.198 s | 35.4 % | 722 |
| noise 0.002, level 5 | 10 min | 0.816 s | 1.518 s | 1.790 s | 0.975 s | 0.272 s | 27.9 % | 854 |
| noise 0.3, level 5 | 10 min | 0.880 s | 1.354 s | 1.629 s | 0.748 s | 0.274 s | 36.6 % | 1,265 |
| noise 0.05, level 0 | 10 min | 2.529 s | 3.085 s | 3.757 s | 1.228 s | 0.672 s | 54.8 % | 1,080 |
| noise 0.05, level 5 | 10 min | 0.857 s | 1.494 s | 1.737 s | 0.880 s | 0.243 s | 27.7 % | 943 |
| noise 0.05, level 8 | 10 min | 0.864 s | 1.547 s | 1.788 s | 0.924 s | 0.241 s | 26.1 % | 879 |

- **Result:** S / D is 26 % to 55 % with a median of 33 %; the seven files at levels 5 and 8 sit between 26 % and 37 %, and the level 0 file, whose frames are mostly fixed predictors and so cheap to predict, is at 55 %. Seven of eight files are above a quarter, as in step 1 (23 % to 52 %, median 31 %): the lending variant stays the right contract.
- **End to end:** lending saves (C - B) / C of the time of an analysis with decoding, 13.5 % to 19.6 % (median 16 %), in line with the 19 % to 34 % that step 1's differential form gave for S / (walk + decoder).
- **Speed:** the decoding added to the analysis (B - A: prediction, reconstruction, undoing the channel assignment and the MD5, with the walk's stores) runs at 650 to 1,270 times real time, which is faster than step 1's decoder alone (340 to 610 times) since the read is no longer part of it. The analysis without decoding (A) runs at 480 to 740 times real time for the files at levels 5 and 8.
- **Not measured:** 24-bit and multichannel files, the hour-long files of step 1, and level 0 on music.

## As built in step 3: WavPack

What the WavPack pull request implements: the decoder (`Encspect.Formats.WavPack.Decoding`, ID `wavpack`, registered in `DefaultParsers.Decoders`), the `wavpack` codec parser for Matroska's reduced-header blocks (the Matroska route itself is step 4), and the `wavpack-crosscheck` job's decoded-audio checks. [wavpack.md](../formats/wavpack.md) describes it from the user's side.

- **Lending:** the parser lends nothing; the decoder reads each block's sub-blocks with the parser's `BlockContents` and decodes the bitstream itself, which the format needs since a block's entropy and decorrelation state is the block's own. `DecodingBoundaryTests` stay green.
- **Configuration:** `DecodeSetup.Configuration` is a copy of the first block that libwavpack opens a file with (the first audio block that verifies), and `Open` refuses what libwavpack refuses there (`NotPossible`). Up to 16 leading blocks without audio (at most 64 KiB) are held and handed first.
- **Unreadable blocks:** `BlockStatus.Refused` marks a block libwavpack's decoder refuses (a decorrelation term of 0, a sub-block of the wrong size, a DSD block too short); with truncated and malformed blocks it becomes `CodedFrame.IsUnreadable`. `FollowsGap` comes from the block indexes.
- **Checksums:** four tallies, declared by the parser: "block checksum" (coded data), "block header CRC", "extended CRC" and "MD5" (decoded audio, `CoversAllAudio` true). A block that says it has a checksum and has none counts as failed in the first.
- **Sets:** a block set is decoded into planes for all its channels; over 2^24 samples (all channels) the blocks are decoded one at a time and the MD5 is not compared (`wavpack.md5-set-too-large`).
- **PCM format:** the bits per sample of the PCM format and of `Setup.BitsPerSample` are the container's width (`bytes * 8`, 32 for floats), 8 for DSD, whose unit of `DecodeSetup.Samples` is the bytes the decoder returns.
- **Deviations from this document:**
  - A block is muted whole (zeros, 0x55 for DSD) and fails its CRC when it has fewer samples than the first block of its set states or its bitstream runs out before its samples do. The first case is libwavpack's (`unpack.c` 89; the block's index plays no part, `open_utils.c` 357). In the second libwavpack reads its bitstream again from the start (`open_utils.c` 842-846) and mutes only on its end-of-data code or its mute limit, so the audio differs and the CRC fails in both. A block that fails its CRC is not zeroed, as `unpack_utils.c` 377-386 does.
  - The work on blocks of sets that never complete reaches neither the sink nor the host's PCM budget, so the decoder limits it itself: `StreamDecoder.MaxWastedSamples` (2^32 samples over all channels), past which it refuses blocks (`decode.frame-invalid`). The design's bound on crafted input holds through it.
  - The Matroska codec parser declares no block checksum: `WavpackVerifySingleBlock` sums the block's header, which Matroska drops, so it cannot be computed from a packet. The checksum tally is declared by the file parser only.
  - Blocks the codec parser cannot rebuild (a header libwavpack would not take, a packet too short or with no block) are handed on as unreadable, not dropped.
  - `wavpack.md5-set-too-large` is reported only when the file stores an MD5, and points to its sub-block.
  - The refusal of a block is one reading: the feed asks `BlockContents` (the decoder's own sub-block reading) whether libwavpack refuses a block, and the parser's findings only explain it. The values of a fast mode's probability tables are checked as the DSD decoder reads them, so a table that is not valid is a `decode.frame-invalid` and not a parser finding.
  - The nodes of a block with no audio are listed beyond the 4,096-block cap (at most 64 more) so that the MD5 sub-block, which comes last, has a node for its findings.
  - The codec parser lists only the first 256 packets, so a decode finding for a later one has no node to point to.
- **Not verified:** the MediaInfo checks of the new fixtures (the local MediaInfo is 24.01, the cross-check asserts 26.01).

## After the milestone review

The independent review of the FLAC step found two blocking defects and a set of smaller ones, all fixed; the rules it drew from them hold for every format, and the work on WavPack, TrueHD and WMA Lossless starts from them.

1. **Every walk counts against the budget.** A search for the next unit that walks a candidate counts the bytes of the walk whether or not the candidate holds, and checks what a header alone can say (its checksum, its fields, its agreement with the stream's configuration) before it walks anything. The FLAC search had counted only the candidates whose CRC-16 held: a crafted file made it walk a full frame for every 12 bytes, about an hour per 100 MB, and the sniffer ran the same search on every file without the marker. This is a parser rule for every format that resyncs, and the sniffers are included.
2. **Walk loops stop at the end of the data.** What a walk costs follows the bytes present and not the count a header declares: a loop over the samples, residuals or coefficients ends as soon as the cursor runs past the data, or is not started where the data cannot hold it. A packet of a dozen bytes that claims 65,535 samples must cost about a dozen bytes, in the Ogg route as in a file.
3. **A unit the parser cannot read is handed once and reported once.** A parser reports a unit whose syntax breaks itself (a finding on the unit) and hands it to the session with `CodedFrame.IsUnreadable`. The host counts it as not decoded (`DecodeEnd.FramesNotDecoded`, so the stream is incomplete) and does not call the decoder, which would fail on the same bytes and add a second finding (`decode.frame-invalid`) for one fault. The unit after an unreadable one reaches the decoder with `FollowsGap` set, whatever the parser said, so a decoder that carries state from unit to unit (TrueHD's filters and matrices, WavPack's block groups) resets it instead of decoding on stale state.
4. **Decoder messages end without a period.** The exception message of a decoder is the reason in "The flac decoder could not decode the frame at 0x…: <reason>.", and the host adds the period. A message that ends in one reads "..".
5. **An unreadable unit makes `CoversAllAudio` false, for every format.** A checksum over the decoded audio, or over coded data, vouches for the units that were read; a unit the parser could not read is missing from what it vouches for, so the tally cannot cover all of the audio however the counts come out. This is a rule of the contract and not a FLAC deviation.
6. **WMA Lossless hands reassembled frames.** Lending serves one frame per `Decode`, but a packet can end several frames, so the unit of WMA Lossless is the frame and not the ASF packet: the parser reassembles the frames that cross packets and hands each as a `CodedFrame` whose `MappedBytes` carries the file ranges of its pieces, as the FLAC codec parser already does for Ogg packets that span pages. The unit is then uniform and lending works.
7. **WavPack checks the DSD sample units.** For DSD the unit of `DecodeSetup.Samples` must equal what `Decode` returns (the same count in the same unit), or `IsComplete` never holds and the MD5 is never compared; the WavPack step checks it before building the decoder and states the unit in its notes.

Smaller decisions of the same review: a parser that offers an unlisted node (a block the report does not list) points its findings to the node that stands for it, not to a node that is not in the tree; the GUI's Stop cancels the whole analysis, which leaves the structure report on screen, since decoding runs inside the structure pass; and the GUI decodes a reload or another format only after a verification has completed.

## Decisions

The project lead decided all nine on 2026-10-22: decision 1 as below, in place of the separate decoder projects this design had recommended; decisions 2 to 9 approved as written.

1. **Layout (the lead's choice):** each decoder lives in its format project, in the namespace `<project>.Decoding`; parser code never references it, which `DecodingBoundaryTests` checks in the compiled assembly, and the host registers the decoders; `CLAUDE.md` then says "no third-party decoders". It works because the decoder reads the bits with the parser's own code, a change that breaks one fails the same build, and the only coupling that could harm, the parser's on its decoder, is checked. The alternative it replaces: a decoder project per format that references its format project.
2. **Data flow (approved):** the parser hands each frame to a decode session while it reads it, in the one pass over the file; a prototype in the contract step measured the second entropy pass at 31 % of decoding time (median of 12 files), so the session lends the parser its buffers. Alternative: a second pass the host drives.
3. **Defaults (approved):** decoding is off in the library, the CLI (`--verify`) and the GUI ("Verify Audio" in the detail window); the file list never decodes, no verdict is cached, and the CSV's `Is Verified` is filled only with `--verify`. Checks over coded data run in every analysis, Monkey's Audio's descriptor MD5 included (3.98 and later): its parser then reads the whole file, and a mismatch cannot say where the damage is, which the frame CRCs with `--verify` can. Alternative: that MD5 only with `--verify`.
4. **Meaning of verified (approved):** every checksum the stream stores was compared and holds, and at least one compared checksum covers all of its audio, coded or decoded; for the phase 3 codings and WavPack in every mode. Alternatives: count only checksums over decoded audio, or cover the lossy formats' CRCs now.
5. **Scope (approved):** Gate 3 decoders for FLAC, Monkey's Audio, WavPack, and TrueHD and MLP, the codings with a checksum over decoded audio; ALAC, TTA and WMA Lossless decoders move to "Later", which leaves their waveform and lowpass without a decoder until then (costs listed under "What the table means"). A WavPack hybrid file is verified as its lossy stream by its header CRCs; one with an MD5 stays unverified until companion-file access finds its `.wvc`. Alternative: all seven decoders, and `.wvc` access, now.
6. **TrueHD (approved):** compare the lossless check of every presentation up to substream 2, one tally each, not the Atmos substream. The check never counts as covering all audio, since the last interval of each segment is never checked; a stream is verified only where substream CRC-8s in every access unit cover it and every lossless check holds. Alternatives: compare the top presentation only, as FFmpeg does, or count the check as covering and report the samples it leaves out.
7. **PCM analyses (approved):** lowpass and similar analyses run in the host on the decoded PCM and add figures to the stream, never nodes; the hook is fixed now, the figures come with the first feature. Alternative: a later change to the contract.
8. **Budget (approved):** decoding stops with an info past the larger of 16 GiB of PCM in the canonical layout and 256 times the file's size, which `DecodeOptions.MaxPcmBytes` overrides for tests. Alternative: no bound in the library.
9. **Tools (approved):** build Monkey's Audio SDK 13.42 (3-clause BSD) and libtta-c 2.3 (LGPLv3) from pinned source for the cross-checks, since no Ubuntu series packages them; no `ffmpeg` fallback for decoding (the plan's open question), FFmpeg stays a cross-check tool. Alternative: FFmpeg as the only second decoder, which cannot decode Monkey's Audio beyond 2 channels and 24 bits.

## Open risks and unverified claims

- **Unverified:** whether WMA Lossless holds a checksum that FFmpeg skips; no specification is public.
- **TrueHD presentations:** what decoding the lower presentations costs beyond their matrices and output, and whether FFmpeg's `-downmix` output gives each presentation's check, is settled by the TrueHD work; decision 6 only says what is compared.
- **Cost not measured:** Monkey's Audio's filters in managed code are an estimate until its decoder measures them. The second entropy pass is measured for FLAC (step 1); the MD5 throughput of `Md5` is measured as part of the prototype's decoder, not alone.
- **Shared syntax:** a parser refactor now has to keep its decoder building; that is the point of the layout, but it couples their changes within one project.
