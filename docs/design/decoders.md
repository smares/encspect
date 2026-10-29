# Decoders: the contract for phase 3

Status: approved by the project lead on 2026-10-22, after two independent reviews, with decoders inside their format projects (decision 1). Step 1 of the [rollout](#rollout), the contract, is implemented (see [As built in step 1](#as-built-in-step-1)); the FLAC decoder (step 2) and the WavPack decoder (step 3) are built (see [step 2](#as-built-in-step-2) and [step 3](#as-built-in-step-3-wavpack)), and so are the TTA parsers of step 3, without a decoder (see [step 3: TTA](#as-built-in-step-3-tta)). The project lead changed decision 8 on 2026-10-23: the budget counts the work decoding takes instead of the PCM it produces. [Work instead of samples](#work-instead-of-samples) is that change, approved the same day; it is built, with the departures and the measurements in [As built: the work budget](#as-built-the-work-budget). The `pcm` decoder of WAV, Wave64 and AIFF came with the measurements (step 3 of [spectral-analysis.md](spectral-analysis.md); see [As built: the PCM decoder](#as-built-the-pcm-decoder)). The decisions are collected under [Decisions](#decisions), and the few claims that no source read for this design confirms are marked **unverified**.

Renamed with step 4 of [the measurements](spectral-analysis.md): the GUI command "Verify Audio" is "Decode Audio" (it also measures the audio), and its failure text "Not verified" is "Not decoded"; this text says so wherever the command is named.

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
- This design defines the PCM path and the hook that the measurements of the decoded audio use ([spectral-analysis.md](spectral-analysis.md); once "host-side PCM analyses"), so those features add fields but change no contract (see [The PCM path](#the-pcm-path)).
- Decoders for lossy codings.

Sources read for this design, all pinned or public:

- The code: `Core.Abstractions` (parser interfaces, contexts and routes, `MappedBytes`, `BitReader`, `StreamInfo`), the host (`Analyzer`, `ParserRouter`, `AnalysisOptions`, the writers), the WavPack parser, the MP4, Ogg and ASF routes, the WMA codec parsers, and the tests (fixtures, goldens, memory, complexity, fuzz and thread tests).
- RFC 9639 (FLAC), sections 8.2, 9.1.8, 9.2.3, 9.3 and 11.
- FFmpeg 6.1.1, the release `ffmpeg_sources.py` pins (archive SHA-256 8684f4b0…): `flac.c`, `flacdec.c`, `flacenc.c`, `apedec.c`, `libavformat/ape.c`, `tta.c`, `libavformat/tta.c`, `mlp.c`, `mlp_parse.c`, `mlpdec.c`, `mlpdsp.c`, `mlpenc.c`, `alac.c`, `wmalosslessdec.c`, `libavformat/matroskadec.c`. The key lines were checked unchanged in n8.0.1, the FFmpeg that CI runs: the Monkey's Audio CRC compared only from 3.90, at most 2 channels, `APE_MAX_VERSION` 3990, FLAC's MD5 skipped, `max_decoded_substream` at most 2.
- libwavpack 5.6.0, the release `wavpack_tool.py` pins (SHA-256 8cbfa159…): `unpack.c`, `unpack_dsd.c`, `open_utils.c`, `pack_utils.c`, `wavpack.h`, `cli/wvunpack.c`.
- Monkey's Audio SDK 13.42 (`MAC_1342_SDK.zip`, SHA-256 0a763b35…, 3-clause BSD; the branch that moved to 13.44 re-read `Prepare.cpp`, `NoWindows.h` and the files it lists below in 13.44, SHA-256 0c5c1643…): `MACLib.cpp`, `APECompressCreate.cpp`, `APEHeader.cpp`, `APEInfo.h`, `APEDecompress.cpp`, `APEDecompressCore.cpp`, `CircleBuffer.cpp`, `Old/UnMACOld.cpp`, `Shared/All.h`.
- libtta-c 2.3 (SHA-256 68ba6db2…, LGPLv3): `libtta.c`.
- Apple's ALAC (macosforge/alac, Apache 2.0): `ALACDecoder.cpp`.
- Launchpad's archive API, for Ubuntu's packages.

## The design in short

- A format's decoder lives in its format project, in the namespace `<project>.Decoding`. Parser code never references that namespace, which a test checks in the compiled assembly; the host registers the decoders next to the parsers.
- The parser owns every bit of the coded syntax. The decoder reads the bits with the parser's own code and adds only what turns them into samples: prediction, channel decorrelation, matrixing, output shifts, and the checksums over decoded audio.
- A parser hands each frame to a decode session through a new route on its context, `StartDecoding`, in the same pass in which it reads the frame. The host serves the route: it finds the decoder, runs it inside an exception boundary and a work budget, and ends the session with the stream's verdict.
- The parser declares every checksum the stream stores, of either kind, with how many values it stores. It compares the ones over coded data in every analysis; the decoder compares the ones over decoded audio. Both end up in one record on the stream, `StreamInfo.Verification`, with a tri-state `IsVerified`.
- Decoding is off unless a front end asks for it: `AnalysisOptions.Decoding` in the library, `--verify` in the CLI, a "Decode Audio" command in the GUI's detail window. The file list never decodes, and no verdict over decoded audio is cached.
- Decoding never changes the parser's nodes: it adds findings, the stream's verification and the figures of its measurements (once "host-side PCM analyses").

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
- The existing parsers (WavPack, WMA Lossless) keep their readers where they are; moving them into a `Syntax` namespace would be churn with no check behind it.

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
- A decoder implements `IDecoder` (below), whose `Id` is the codec ID the parsers route by: `flac`, `monkeys-audio`, `wavpack`, `truehd`, `mlp`, `pcm` (WAV, Wave64 and AIFF, with the measurements), and later `alac`, `tta`, `wma-lossless`.
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

    // The work units per byte of the file that the analysis's work budget allows for this coding (see "Work instead of
    // samples"): DecodeWork.DefaultPerInputByte unless the coding's quietest legitimate files need more.
    long WorkPerInputByte => DecodeWork.DefaultPerInputByte;

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
    // packet the samples of the frames that end in it. It charges its work (DecodeContext.AddWork) before it does it.
    // The frame's bytes are valid only during the call.
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

    // The width of a sample, which the canonical layout writes in ceil(BitsPerSample / 8) bytes. For DSD, 8, with one
    // byte of DSD per channel as one sample.
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

    // A few bytes of the stream that follow the unit, for a decoder that reads ahead of its unit's end (Monkey's Audio's range
    // coder of versions 3.90 to 3.92 takes two bytes past an array, and the frames of version 3.80 share their last word). Null
    // where the parser gives none or the data ends with the unit. Valid only during the call, like Bytes.
    public MappedBytes? Following { get; init; }

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

    // The analysis's token, linked with the decoding's own stop (the work budget, DecodeOptions.Stop): canceling throws
    // OperationCanceledException, and the host tells which one it was. The host checks it between frames; a decoder
    // whose single frame can be charged more than 2^27 work units (Monkey's Audio at its highest levels) checks it every
    // few thousand samples inside the frame.
    public CancellationToken CancellationToken { get; }

    // The buffers the session lent the parser for the frame being decoded, where its CodedFrame.IsWalked; empty otherwise.
    public SampleBuffers LentBuffers { get; }

    // Charges work units to the analysis's work budget (see "Work instead of samples"), before the work is done: at
    // least once per frame, and per chunk inside a frame that is checked inside. Negative units throw.
    public void AddWork(long units);

    // Findings on the parser's nodes or on equal ones; findings of one kind are reported once per stream with a count.
    public void AddFinding(Severity severity, string code, string message, Node? node = null, int count = 1);

    // Called once, before the first block: the PCM the decoder will write, and where to write it. The sink is the
    // host's: it forwards to the front end's sink (DecodeOptions.Output) and to the stream's measurer (docs/design/spectral-analysis.md), and it
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
- **Samples:** `Decode` returns 0 for the blocks before a set's final block and the set's samples on the final one, so `DecodedSamples` counts each sample once, not once per block; the work budget counts the work of every block as it is decoded.
- **State:** none from set to set beyond the decoder's configuration.
- **Declared checksums:**
  - the block checksum sub-block (coded data, every block that has one; the parser compares it today);
  - the block header CRC (decoded audio, one per audio block, `CoversAllAudio` where the stream has no gap and its samples equal the total the first block states);
  - the extended CRC (decoded audio, one per block with an extra bitstream);
  - the MD5 (decoded audio, 0 or 1 stored, `ID_MD5_CHECKSUM` or `ID_ALT_MD5_CHECKSUM`).
- **DSD:** the decoder never calls `DeclareOutput`; `Decode` returns the DSD bytes per channel, which the decoder charges as samples (see [Work instead of samples](#work-instead-of-samples)).
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
| Monkey's Audio, 3.93 and later, and earlier files with the CRC format flag | Frame CRC: CRC-32 (reflected, start 0xFFFFFFFF) over the decoded output bytes in WAV layout, channel-interleaved (8-bit samples as unsigned bytes, 16-bit as 2 bytes, 24-bit as 3), inverted and shifted right by one; all 32 bits of the stored word up to 3.82, and from 3.83 on bit 31 of the word flags the special frame codes that follow it | decoded audio | decoder | SDK `APEDecompressCore.cpp` 378-423 (every file from 3.93 reads the word whatever the flag says), `Old/UnMACOld.cpp` 122-147 (below 3.93 the flag decides), `APEInfo.h` 62-64 (`> 3820`), `CircleBuffer.cpp` 48-68. The draft's gate "3.90 and later" came from FFmpeg's `apedec.c` and is not the SDK's; the SDK has none at 3.90 |
| Monkey's Audio, before 3.93 without the CRC format flag | A 30-bit Rice-coded checksum of the decoded samples, 0 marking silence (the SDK's old decoder, `MACLib.cpp` 58). The parser declares it as "frame checksum" and the decoder compares it: the sum, over the frame's samples, of the absolute values of the left and the right sample (of the sample for one channel), in 32 bits (`CalculateOldChecksum`, 247-265). A file of 3.90 to 3.92 without the flag is not decoded: the SDK's bit array of those versions reads no such value and gives silence | decoded audio | decoder | SDK `Old/UnMACOld.cpp` 125-146, 247-265, 399-414 |
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
| `monkeys-audio.frame-checksum-mismatch` | Warning | the frame | the frame's decoded audio does not have its stored 30-bit checksum (a file before 3.93 without the CRC flag); once per stream with a count |
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
| `decode.limit` | Info | the stream | the analysis's work budget ran out (see [Work instead of samples](#work-instead-of-samples)) |
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
- Decoding time grows with the samples decoded, times a cost per sample that the coding and its settings set, and that differs between codings tenfold and more:
  - FLAC's predictor uses at most 32 coefficients a sample;
  - WavPack runs up to 16 decorrelation passes over every sample; its crafted worst case decodes about 22 MB/s of 16-bit PCM (see [As built in step 3](#as-built-in-step-3-wavpack));
  - Monkey's Audio's highest level about 1,550 filter taps per sample of each channel (16 + 256 + 1,280, `apedec.c` 81-87), so it decodes far slower than FLAC, and its filters need `System.Numerics.Vector`.
- Step 1's prototype measured FLAC: the decoder alone, without the structure walk, runs at 340 to 610 times real time on the build machine's one thread (see [The second entropy pass](#the-second-entropy-pass)). The real decoder's figures, and Monkey's Audio's, come with the steps that build them and are recorded in the plan.
- So the budget counts that cost, which each decoder reports, and not the samples (see [Work instead of samples](#work-instead-of-samples)).

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

    // The analysis's work budget in work units; null for the default (see "Work instead of samples"). Tests and the
    // fuzzer lower it. An option of the call, not a setting.
    public long? MaxWork { get; init; }
}

public sealed record DecodedStream(string CodecId, Node Stream, PcmFormat Format);
```

- **CLI:** off; `--verify` sets `Decoding`. `--fail-on warning` already turns a mismatch into a failing exit code.
- **GUI:** off. The detail window gets a "Decode Audio" command, enabled where a stream's verification says `NotRequested`. It analyzes the file again with decoding, with progress and Stop, and replaces the report when done; the tree is the same, so the selection stays.
- **File list:** never decodes. A folder of thousands of files would cost minutes of decoding per gigabyte, and the list's summaries stay what they are.
- **Summary cache:** never holds a verdict over decoded audio. Its key (size, last-write time, file ID and a hash of the first and last 64 KiB) does not see a byte changed in the middle of the file, which is exactly what a verification is about.
- **Coded-data checks in every analysis:** they cost reading the bytes they cover. The FLAC, TTA and WavPack parsers read every frame anyway. Monkey's Audio's parser otherwise reads only the headers, the seek table and the frame starts; its descriptor MD5, which exists only from 3.98 on, makes it read and hash the whole file, also in the file list (see decision 3). A mismatch says the coded data changed, not where; the frame CRCs, compared with `--verify`, locate it.
- **Decision:** off everywhere, with the commands above. The alternative is to decode by default in the detail window, which opens one file at a time.

### Cancellation, progress and partial results

- Decoding runs in the structure pass, so `AnalysisOptions.BytesRead` reports its progress as it does now; it just moves slower.
- `AnalysisOptions.CancellationToken` cancels everything, as now. `DecodeOptions.Stop` ends every open session at the decoder's next check of its token (inside a frame for the decoders that check there, else at the next frame); the parse carries on to the end without decoding.
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
| Monkey's Audio | 73,728 samples per channel, × 4 at extra high and × 16 (1,179,648) at insane (`APECompressCreate.cpp` 77-81); the SDK accepts up to 10,000,000 at level 5000 and above and 1,000,000 below (`APEHeader.cpp` 305-317), with up to 32 channels (`All.h` 398) | none per frame from 3.93 on: the decoder decodes a frame in chunks of 4,096 samples per channel, with its CRC updated as it goes, so the frame's length bounds time, not memory; the SDK's limits are accepted as they are. Before 3.93 the SDK's old decoder decodes a frame whole (`Old/UnMACOld.cpp`) and a frame's length is fixed by the version at 9,216 or 73,728 samples per channel (`APEHeader.cpp` AnalyzeOld), at most 2 channels, which bounds the memory; `OldFrameDecoder` holds three arrays of that length (the two channels' values and the values before the predictor, 3 × 73,744 integers at most) and the frame's bytes, and charges and checks the token by chunks of 4,096 values of an array |
| WavPack | under 196,608 samples a block (`read_next_header`); up to 4,096 channels across the blocks of one set | 2^24 samples for the blocks of one set, which the MD5 needs interleaved; past it the blocks are decoded one at a time, their CRCs compared, and the MD5 is not (`wavpack.md5-set-too-large`) |
| TrueHD, MLP | 160 samples, 8 channels (`mlp.h`) | negligible |
| ALAC, later | the configuration's frame length, which FFmpeg accepts up to 4096 × 4096 (`alac.c` 530-532) | 2^20 samples per channel |

### The exception boundary

The host's session wraps every call to the decoder:

- `InvalidDataException`, and `EndOfStreamException` from a frame's own bytes: `decode.frame-invalid` on the frame; the next frame is decoded.
- An `InvalidDataException` from `Open`: `decode.not-possible` with its message. Any other exception from `Open` is `decode.failed`.
- `OperationCanceledException` from the analysis's token passes through; the stop token ends the session with `decode.stopped`; one that no cancellation of the host explains is `decode.failed`.
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
- **The budget:** the host counts the work all sessions of an analysis charge, in work units that each decoder reports per frame (`DecodeContext.AddWork`), whether or not a sink takes the PCM. Once it passes the larger of 2^35 units and the file's size times the units per byte of its coding (2,048, and 8,192 for Monkey's Audio), about 21 s and 1.2 s per MB (4.9 s for Monkey's Audio) on the measurement machine, it stops every session with `decode.limit`; `DecodeOptions.MaxWork` replaces that bound. The unit, what each decoder counts and the numbers are in [Work instead of samples](#work-instead-of-samples).
- A crafted file of 100 MB stops after 2 × 10^11 units, about 2 minutes, for FLAC and WavPack, and after 8.2 × 10^11 units, about 8 minutes, for Monkey's Audio; a file under 16 MiB (4 MiB for Monkey's Audio) after about 21 s.
- **Decision 8, as changed on 2026-10-23:** count work, not PCM. The approved bound counted PCM bytes (the larger of 16 GiB and 256 times the file's size), which WavPack's worst case showed to be about 13 minutes on a 3.2 MB file (see [As built in step 3](#as-built-in-step-3-wavpack)).
- **Per-frame time:** a frame's work is bounded by the format's largest frame times its work per sample, so the host's check between frames bounds the latency of cancellation where a frame is charged at most 2^27 units (about 80 ms). For the samples of its largest frame FLAC charges at most 2.8 × 10^7 (32 bits, 22 units a sample) and WavPack about 1.1 × 10^8 (a floating-point block of 196,607 samples in 2 channels with 16 decorrelation passes and 2^20 bytes of bitstream: 62 × 2 × 196,607 + 11 × 16 × 2 × 196,607 + 16 × 2^20; within about 18% of 2^27), both under 2^27, TrueHD and MLP 5 × 10^4 a substream; the charge per byte of coded data comes on top, bounded by the frame's bytes. Monkey's Audio charges and checks inside its frames.

## Work instead of samples

The project lead changed decision 8 on 2026-10-23: the work budget counts the work decoding takes, which each decoder reports frame by frame, and not the PCM it produces. The lead approved this change the same day. It is built (see [As built: the work budget](#as-built-the-work-budget)); the lists below are the design's, and that section says where the built weights differ and what was measured.

### Why samples are not enough

- PCM bytes track time only where every coding costs about the same per sample, and they do not:
  - the built FLAC decoder adds 9 to 17 ns per sample of each channel to an analysis (step 2's table, B - A over the samples of both channels);
  - WavPack's crafted worst case costs about 95 ns per sample, with 16 decorrelation passes (18.7 s for 196.6 million mono samples, see [As built in step 3](#as-built-in-step-3-wavpack));
  - Monkey's Audio's insane level runs 1,552 filter taps per sample of each channel (`apedec.c` 81-87), not measured.
- So the 16 GiB of PCM the approved bound allowed, about 29 s at the 600 MB/s the design estimated, is about 13 minutes of WavPack's worst case on a 3.2 MB file.
- Work that produces no PCM escapes a count of PCM:
  - the blocks of WavPack sets that never complete, which `StreamDecoder.MaxWastedSamples` has to cap inside the decoder;
  - a frame the decoder works through and then refuses (`decode.frame-invalid`), since the host counts a frame's PCM only when `Decode` returns.

### The unit

- **Work unit:** the work of one tap of a predictor or filter on one sample of one channel, a multiply-accumulate. Every other kind of decoding work is counted as a whole number of these units.
- A decoder charges, for each frame:
  - **per sample of each channel it outputs**, decoded or muted: `DecodeWork.ForSample(bytes)` units, for the fixed work of a sample: the steps of reconstruction that do not depend on settings, undoing the channel coding, wasted bits and shifts, the output and the checksums over the decoded audio. It is 10 plus 3 per byte of the canonical layout that the checksums hash (`ceil(BitsPerSample / 8)`): 13, 16, 19 and 22 for 1 to 4 bytes. The lists below write "16 per sample" for the 16-bit value; 32-bit integers and floats charge 22, DSD 13. The 3 per byte is MD5 at the 600 MB/s the design estimated, 1.7 ns a byte, about 2.8 units: **unverified** until the work tests measure it;
  - **per byte of entropy-coded data it reads itself**: 16 units (`DecodeWork.PerCodedByte`), for bit reads and unary and Rice codes, whose number follows the bytes and not the samples. A range decoder that does its division work per symbol is charged per sample by its decoder instead (Monkey's Audio, below). A frame whose data the parser's walk stored in lent buffers (`CodedFrame.IsWalked`) charges none: the walk is the structure pass's cost, bounded by the bytes (rule 2 of [After the milestone review](#after-the-milestone-review));
  - **per stage, per sample of each channel the stage runs on**: the stage's units from the lists below (1 per tap, `DecodeWork.PerTap`).
- The weights come from the existing measurements (below), so that a unit takes about the same time in every decoder, and the work tests (below) hold each decoder to that:
  - a sample's fixed work is about 10 ns: FLAC at level 0, with fixed predictors of at most 4 taps, adds 10.5 ns a sample;
  - a FLAC tap is about 0.75 ns: music 1 at levels 8 and 5 differs by 3 ns a sample, for up to 4 more taps;
  - a WavPack pass is about 5 ns: its worst case's 95 ns a sample, less the fixed work, over 16 passes;
  - so 16 units for the fixed work, 1 for a tap and 8 for a pass, at about 0.6 ns a unit.
- Work that grows with neither samples nor coded bytes (a header, a sub-block list, a configuration) is bounded by the frame's bytes, which the structure pass reads anyway, and is not charged. The exception is setup that costs far more than its bytes: WavPack's DSD tables, charged per block (below).

### What each decoder counts

**FLAC** (RFC 9639, section 9.2; `FrameDecoder` and `Prediction`):

- 16 per sample of each channel of the frame (its block size times its channels).
- Constant and verbatim subframes: nothing more.
- Fixed predictor: its order, 0 to 4, per sample (section 9.2.5).
- Linear predictor: its order, 1 to 32, per sample (section 9.2.6), in 32-bit and 64-bit sums alike; the 64-bit path at that weight is **unverified**.
- Rice partitions: nothing of their own. A partition costs its parameter's few bits (section 9.2.7), which the parser's walk reads; a frame the decoder reads itself (not walked) charges 16 per byte of the frame, partitions, escapes and unary codes included.
- The undoing of left/side, side/right and mid/side coding, wasted bits and the MD5: in the per-sample charge, which follows the bytes hashed (22 at 32 bits).

**WavPack** (libwavpack 5.6.0; `PcmBlockDecoder`, `DsdBlockDecoder`, `WordDecoder`):

- `DecodeWork.ForSample` per sample of each channel of the block, muted blocks included: 16 at 16 bits, 22 for 32-bit integers and floats (`Setup.BitsPerSample` is 32 for floats); for DSD per byte of each channel, the unit `Decode` returns, 13.
- 16 per byte of the bitstream sub-blocks it reads: the main bitstream, the extra bitstream of floats and of integers above 24 bits, the DSD data. This covers the entropy decoder's work that follows the bits, such as the hybrid error limit's search, one bit a step (**unverified** at that weight); a run of zeros costs per sample, in the 16.
- Decorrelation passes (`decorr_mono_pass`, `decorr_stereo_pass`): 11 per pass per sample of each channel, for every term (1 to 8, 17, 18, and -1 to -3 between the channels), up to 16 passes. Measured for term 17 only; the others are **unverified**.
- DSD: 16 more per byte of each channel in the fast mode (`decode_fast`, two 32-bit divisions a byte) and 64 more in the high mode (`decode_high`, eight bits each through the range coder and the filters); both **unverified**.
- **DSD table setup, per block, before the block is decoded:**
  - the fast mode charges 57,000 units (`DsdBlockDecoder.PrepareFast`: it clears 8,192 table entries, its summing loop always runs 8,192 times and the lookup fill takes up to 40,960 steps; the reviewer counted these from the code, the code pull request recounts them). A block of about 400 bytes then costs about 57,000 units against about 6,400 for its bytes (16 a byte), so the setup is the block's cost; even an all-zero table of about 40 bytes of run lengths costs about 16,000 steps;
  - the high mode charges 4,096 (`InitTable`, "a few thousand steps"; **unverified** count).
- The integer and lossy fix-ups (`fixup_samples`) and the block CRC: in the per-sample charge.
- **Floats (`float_values`, `PcmBlockDecoder.RestoreFloats`) are not covered by it, and are fixed in the code instead:**
  - libwavpack's `unpack_floats.c` 70-71 and 118-119 loop `while ((value & 0x800000) == 0 && --exponent != 0)`. A value that is 0 after the shift by `FloatShift` (or `int.MinValue`, which negating leaves negative and the first shift makes 0) never gets the bit, so the loop counts the exponent down to 0: up to 254 iterations a sample for a maximum exponent of 255.
  - The float info sub-block has no range check (`open_utils.c` 443-444), so a shift of 31 and a maximum exponent of 255 are accepted. Odd values cost almost no bytes: one pass of term 1 with weight 1024 and a decorrelation sample of 1 over a zero run (8 bytes for 196,607 samples) gives 1 for every sample.
  - The charge for such a sample is 22 + 8 = 30 units, about 18 ns; the loop is about 254 iterations of a shift and a decrement, about 100 to 250 ns at 1 to 2 cycles each. That is **about 6 to 14 times** the time per unit the bound assumes, which turns the 21 s floor into several minutes. **Estimated from the loop, not timed;** the work test times it.
  - **The fix, in the code pull request:** once `value` is 0, the loop is replaced by `shiftCount += exponent - 1; exponent = 0`, without iterating. The output is identical, since 0 shifted stays 0 and the exponent ends at 0 as the loop leaves it; `shiftCount` is identical, and so is `shiftCount & 0x1F`, the only use of it (the mask of the low mantissa bits and the extra bitstream read), so the CRC over the floats is unaffected. A test compares the closed form with the loop, ported unchanged, over every shift, every maximum exponent and the values around 0, `int.MinValue` and the bit 0x800000, and the float fixtures stay byte-identical.
  - The alternative, if the comparison finds a difference: charge float blocks `FloatMaxExponent` more units per sample.
  - The crafted case (shift 31, maximum exponent 255, zero runs) is in the work tests and in the WavPack tests.
- A block of a set that never completes is charged when it is decoded, as any other block.

**Monkey's Audio** (FFmpeg 6.1.1 `apedec.c`, Monkey's Audio SDK 13.44):

- 16 per sample of each channel.
- **The range decoder from 3.90 on, per sample of each channel, 48 units, not per byte:**
  - `ape_decode_value_3990` (`apedec.c`) decodes every sample with `range_get_symbol` (one division in `range_decode_culshift` and a linear search of up to 21 symbols, 461-478) and `range_decode_culfreq` (two divisions, 365-370), even where the base costs 0 bits. That is three dependent 32-bit divisions a sample, about 25 to 30 ns on the Xeon of step 1, about 45 units; 48 is the proposal, **unverified** until the Monkey's Audio pull request measures it. Normalization, the only part that follows the bytes, is in the 48.
  - Counting bytes would not follow this work: the cheapest symbol has a probability of 19,578/65,536, 1.74 bits, so a sample costs at least 0.22 bytes, 3.5 units at 16 per byte, against three divisions (the review's reading of the tables; the pull request recomputes it).
- 16 per byte of the frame for the bit reader of 3.80 to 3.89 (`ape_decode_value_3860` and `decode_array_0000`, 504-535 and 617-673), which has no divisions. The parser does not walk this data (see [Who owns the bits](#who-owns-the-bits)).
- **Silent and pseudo-stereo frames charge what they do:**
  - A frame flagged `APE_FRAMECODE_*_SILENCE` returns before any entropy decoding, predictor or filter (`ape_unpack_mono` 1484, `ape_unpack_stereo` 1509): it charges only the per-sample 16 (output and CRC), whatever the level.
  - A frame flagged `APE_FRAMECODE_PSEUDO_STEREO` (1657) decodes one channel and copies it: it charges the range decoder, the prediction stage and the filters for one channel, and the 16 for both.
- The prediction stage, its taps per sample of each channel it decodes: 5 from 3.80 to 3.92 (`filter_3800`, 902-945), 4 for 3.93 and 3.94 (`predictor_update_3930`, 1108-1136), 9 from 3.95 on (`predictor_update_filter`, 1188-1241; SDK `NewPredictor.cpp` 258-266). FFmpeg runs the stage twice where it does not know the interim mode (1243-1262); a decoder that does the same charges both.
- The NN filters from 3.93 on, 1 per tap: none at fast, 16 at normal, 64 at high, 256 + 32 at extra high, 1,280 + 256 + 16 at insane (`apedec.c` 81-87; SDK `NewPredictor.cpp` 162-182). **Unverified:** with `System.Numerics.Vector` a tap over 16-bit history may cost much less than FLAC's scalar tap. The weight comes from the pull request's measurement, not from the tap count; counting these taps in groups lowers the units per sample and with it the per-byte allowance below.
- From 3.80 to 3.92, the long filters instead: 16 taps at high, 128 (before 3.83) or 256 + 8 (from 3.83 on) at extra high (`long_filter_high_3800`, `long_filter_ehigh_3830`, 947-1032).
- Before 3.80, which only the SDK decodes (FFmpeg's `ape.c` 31 takes 3.80 and later): extra high runs pairs of offset passes whose count the frame states, up to 16 pairs before 3.32 and up to 8 later (`Old/APEDecompressCoreOld.cpp` 110-150). A decoder that takes these files charges 1 per pass per sample.
- **Per-byte allowance:** Monkey's Audio declares `WorkPerInputByte` 8,192 (see [The default bound](#the-default-bound)).
- In chunks: the decoder charges each chunk of 4,096 samples per channel before it decodes it, and checks its token inside the frame. A frame of the size the SDK accepts at most (10,000,000 samples of 32 channels at insane) is 5 × 10^11 units, so a whole-frame charge would let one frame run for minutes.

**TrueHD and MLP** (FFmpeg 6.1.1 `mlpdec.c`, `mlpdsp.c`, `mlp.h`):

- 16 per sample of each channel of every substream decoded (substreams 0 to 2, decision 6), at most 160 samples an access unit (`mlp.h` 59).
- Coded data: none where the parser's walk stored the Huffman-coded residuals (the lending variant, see [The second entropy pass](#the-second-entropy-pass)), else 16 per byte.
- The channel filters: the FIR order (up to 8) plus the IIR order (up to 4) per sample of each channel (`mlpdsp.c` 38-47, `mlp.h` 67-68).
- The matrices: per primitive matrix, its input channels per sample, the substream's channels plus the two noise channels where it has them (`mlpdsp.c` 74-78, `mlpdec.c` 1112-1131); up to 8 matrices (`mlp.h` 45), so up to 80 units per sample period, over all channels of the substream together (`ff_mlp_rematrix_channel` runs `maxchan + 1`, at most 10, multiply-accumulates per matrix per sample period). The other lists count per sample of each channel; a decoder that read this one the same way would charge eight times too much.
- So an access unit was drafted at about 5 × 10^4 units per substream (160 sample periods times 80, plus the filters and the 16 per sample of each channel). The built weights are different and measured: see [As built in step 3: TrueHD and MLP](#as-built-in-step-3-truehd-and-mlp).

### How the host counts and bounds it

- `DecodeContext.AddWork(long units)` adds to the analysis's count, one count for all sessions as now, saturating at `long.MaxValue`. Negative units throw `ArgumentOutOfRangeException`, which reaches the host as `decode.failed`, like a negative return from `Decode`.
- A decoder charges a stage before it runs it. A frame of at most 2^27 units may be charged once; a decoder whose frames can be charged more charges per chunk and checks its token after each charge.
- Past the budget, the host cancels the decoding's token, which `DecodeContext.CancellationToken` is linked to:
  - a decoder that checks inside the frame throws `OperationCanceledException` at its next check;
  - the host tells that from a canceled analysis by the analysis's own token, counts the frame as not decoded and ends the session;
  - the other sessions stop at their next frame, as now.
- The same token carries `DecodeOptions.Stop`, so a front end's Stop also takes effect inside a long frame.
- **Which finding:** the budget and Stop cancel one token, so the exception cannot say which it was. The session decides from the host's own state, as `EndIfAsked` does now and in its order: a sink failure, then `DecodeRun.IsStopRequested` (`decode.stopped`), then `IsOutOfBudget` (`decode.limit`). A frame that both a Stop and the budget interrupted reports the stop.
- **Lifetime:** `DecodeRun` creates the linked source (`CancellationTokenSource.CreateLinkedTokenSource` of the analysis's token and `DecodeOptions.Stop`) when the analysis starts decoding, and disposes it in the `finally` of `Analyze` after the last session has ended and its `Finish` has run, also when `Analyze` throws. Without that, a library caller that reuses one long-lived token keeps every analysis's linked source registered on it; `MemoryTests` and `InputReleaseTests` get a case that analyzes many files with one token and checks that the registrations do not accumulate.
- **Latency:** `AddWork` cancels synchronously, before the work it charged. A decoder that checks the token only between frames (FLAC) therefore finishes the frame it has charged: at most 2^27 units, about 80 ms, past the point where the budget was crossed. That frame counts in `FramesNotDecoded` only if it fails to finish; one that completes counts as decoded, and the session ends after it.
- After a frame the stop interrupted, the host calls `Finish` with `IsComplete` false; a decoder's `Finish` must not assume its last frame completed. The front end's sink may have received blocks of that frame beyond the samples `End` reports, as a stopped stream's PCM is incomplete anyway.
- Work charged inside a frame that then fails (`decode.frame-invalid`) counts.
- **The floor of the charge:** compared on totals, not per call. After each `Decode`, a session whose decoder has charged, over the whole stream so far, less than `DecodeWork.ForSample(bytes)` times `Setup.Channels` times the samples it has returned so far (`DecodedSamples`) ends with `decode.failed`: a decoder that forgets to charge is a bug, and no fixture passes with it.
  - Totals are needed because a WavPack set returns 0 for every block before its final one and the set's samples on that block, and the final block alone charged only its own one or two channels; a per-call check would fail a correct decoder on a 5.1 set.
  - **What it cannot catch:** it catches only a missing per-sample charge. A forgotten charge per pass, per tap or per byte, the part that varies tenfold between settings, passes it. Only `DecoderWorkTests` catches those, and only for the cases it includes.

The contract in code:

```csharp
// In Encspect.Core: the work units every decoder charges, besides the units its stages state.
public static class DecodeWork
{
    // Per sample of each channel a decoder outputs, decoded or muted: 10 plus 3 per byte of the canonical layout the
    // checksums hash (bytes = ceil(BitsPerSample / 8)): 13, 16, 19, 22.
    public static long ForSample(int bytes) => 10 + (3 * bytes);

    // What an IDecoder allows per byte of the file unless it states otherwise.
    public const long DefaultPerInputByte = 2048;

    // Per byte of entropy-coded data a decoder reads itself (not a frame its parser walked into lent buffers).
    public const long PerCodedByte = 16;

    // Per tap of a predictor or filter, per sample of each channel: the unit itself.
    public const long PerTap = 1;
}

// In Encspect.Host.
public sealed class DecodeOptions
{
    // The default work budget is the larger of this and the size of the file times the largest IDecoder.WorkPerInputByte
    // among the decoders that started a session in the analysis.
    public const long MinimumWork = 1L << 35;

    // The analysis's work budget in work units; null for the default.
    public long? MaxWork { get; init; }
}
```

`IFrameDecoder.Decode` keeps returning samples: the work goes through the context, so work done before an exception counts, a long frame can be charged in chunks, and the many tests that script `Decode` keep their meaning.

### The default bound

- **The bound:** the larger of 2^35 units and the file's size times the units per byte of its coding: 2,048 for FLAC and WavPack (`DecodeWork.DefaultPerInputByte`), 8,192 for Monkey's Audio. At 2,048 they meet at 16 MiB.
- **How a format declares its units per byte:**
  - `IDecoder.WorkPerInputByte`, a property of the decoder with the default 2,048. A decoder overrides it only with the derivation of its worst legitimate case in its "As built" section.
  - The host takes the largest value among the decoders that started a session in the analysis, and the budget is the larger of 2^35 and the file's size times it. A file is only allowed the larger figure where it parses as that coding, so a crafted Monkey's Audio file costs up to four times the others: 4.9 s per MB, about 8 minutes for 100 MB.
  - `DecodeOptions.MaxWork` still replaces all of it.
  - The budget can grow during an analysis, since it uses the largest allowance among the decoders that have opened a stream, but only until it is first crossed: the crossing cancels a token that cannot be un-canceled, so it is final (`DecodeRun.IsOutOfBudget` stays true) and every session stops with `decode.limit`. This cannot happen yet: Monkey's Audio is raw-only, so no file starts sessions of two codings.
- **Monkey's Audio's value, derived:**
  - Its worst legitimate case is insane: 16 + 48 (range decoder) + 9 (prediction) + 1,552 (filters) = 1,625 units per sample of each channel, at the weights above.
  - A sample that is not in a flagged silent frame costs at least the cheapest symbol, 1.74 bits = 0.2175 bytes (**unverified**: the review's reading of the frequency tables). So the most units per byte a legitimate insane file reaches is 1,625 / 0.2175 = 7,471, rounded up to 8,192 (2^13).
  - At 2,048 a file would need 0.79 bytes a sample of a channel, 6.35 bits, 40 % of 16-bit PCM, to verify in full beyond the floor, which covers 2^35 / 1,625 = 21.1 million samples, about 240 s of stereo at 44.1 kHz. Quiet classical music, speech and old mono transfers are commonly below that. A benchmark average of about 41 % for Monkey's Audio on classical music (from a search snippet of a 2022 EURASIP paper; its definition of the figure is **unverified**) would put a large share of such files under the line.
  - At 8,192 the line is 1.59 bits a sample, under the cheapest symbol, so no unflagged legitimate file is stopped; the floor then covers anything under 4 MiB.
- **What a unit takes**, from the existing measurements:

| Measurement | Units per sample of a channel | Time per sample of a channel | Time per unit |
| --- | --- | --- | --- |
| WavPack worst case, 1,000 complete sets (18.7 s) | 144 (16 + 16 passes of 8) | 95 ns | 0.66 ns |
| WavPack worst case, sets that never complete (1,365 blocks decoded, 22.4 s) | 144 | 83 ns | 0.58 ns |
| WavPack `complete-1000.wv` (74,000 bytes; term 17, 16 passes, 1,000 complete sets), the reviewer's run: 17.3 s user, 2.83 × 10^10 units | 144 | 88 ns | 0.61 ns |
| FLAC, music 1, level 8 (B - A of step 2) | at most 28 (order at most 12) | 17.3 ns | at least 0.62 ns |
| FLAC, music 1, level 5 | at most 24 (order at most 8) | 14.3 ns | at least 0.60 ns |
| FLAC, music 2, level 5 | at most 24 | 15.7 ns | at least 0.65 ns |
| FLAC, noise 0.05, level 0 | at most 20 (fixed, order at most 4) | 10.5 ns | at least 0.53 ns |
| FLAC, noise 0.3, level 5 | at most 24 | 9.0 ns | at least 0.37 ns |

- **Reading the table:**
  - The reviewer's row is one run on a busy 4-core machine (load average about 7 while another agent built), so it is one noisy figure; it agrees with the others.
  - The WavPack figures are exact in units, since the passes are known; both include the process and the structure pass (`encspect --verify`), on Linux x64 with one core, a machine step 3 does not name further.
  - The FLAC figures take the highest order each `flac` level allows, so they are lower bounds; the synthetic file at 0.37 ns most likely uses lower orders than 8. They were measured on the Intel Xeon at 2.80 GHz of step 2, with the MD5 and the walk's stores.
  - So a unit takes about 0.6 ns on these machines. A measurement per decoder and per worst case, on one machine, is still to be done: the work tests below record it.
- **In time**, at 0.6 ns a unit:
  - the floor, 2^35 units, is about 21 s;
  - 2,048 units a byte is about 1.2 s per MB of the file: a crafted file of 100 MB stops after about 2 minutes for FLAC and WavPack, and 8,192 units a byte (Monkey's Audio) is 4.9 s per MB, about 8 minutes for 100 MB;
  - a decoder at the edge its work test allows (twice the reference's time per unit) takes twice that.
- **Legitimate files that verify in full:**
  - An hour of CD audio in FLAC at level 8 is at most 8.9 × 10^9 units (about 5 s), under the floor whatever the file's size.
  - An hour of 8 channels at 24 bits and 192 kHz in FLAC with order 32 throughout is 2.65 × 10^11 units (about 2.7 minutes); the bound reaches it from a file of 130 MB on, and such a file holds 16.59 GB of PCM, so it is several GB.
  - WavPack at 16 passes needs about 0.07 bytes of coded data per sample of a channel to fit, FLAC at order 32 about 0.02: every file but near silence.
  - Monkey's Audio at insane, at the weights above (1,625 units a sample), would need 0.79 bytes per sample of a channel at the default 2,048, which its quiet files may not have; it declares 8,192 instead (derived above), and its pull request checks the weights and the allowance by measurement, with a test file whose samples all cost the cheapest symbol.
- **Crafted files measured so far:**
  - WavPack's worst-case blocks charge 2.83 × 10^7 units each. Step 3 gives 2.2 MB for 30,000 of them and 3.2 MB for 43,690, about 73 bytes a block, so about 390,000 units per byte. Its "3.2 MB" for the 1,000 complete sets it timed was a slip: 1,000 blocks are about 73,000 bytes, and the reviewer measured `complete-1000.wv` at 74,000 bytes.
  - A file of them stops after about 1,214 blocks, about 22 s at the measured 18.7 ms a block, at any size up to 16 MiB (at the built weights, with 160 units a sample, after 1,092 blocks in 19.1 s, measured on a 370,000-byte file). The approved bound let a 3.2 MB file of 43,690 such blocks run in full, about 13 minutes. The timed run of 1,000 blocks (2.83 × 10^10 units, under the floor) still decodes in full.
  - WavPack's 2.2 MB file of sets that never complete stops after about 1,214 blocks, about 20 s (4,369 blocks, 2.8 s, at the built weights for the review's variant of 16-byte bitstreams without passes); `MaxWastedSamples` stops it after 1,365 blocks, in 22.4 s.
  - FLAC's constant frames of 8 channels at 32 bits (`ComplexityTests.FlacFramesThatCodeMegabytesEach`) charge about 150,000 units per byte of the frames; a 20 MB file of them stops after about 5,100 frames, at 2,048 units per byte of the file.
- **Digital silence** is where legitimate and crafted files look alike: few bytes, the full work of the stage. Monkey's Audio flags silent frames and charges them only the per-sample 16, so its flagged silence is about 100 times cheaper per sample than insane work and still not free: a long silent Monkey's Audio file also stops with `decode.limit`, only about 100 times later. A long silent stream in a small file (an hour of 8-channel silence at 192 kHz in FLAC, in frames of 4,096 samples, charges 8.8 × 10^10 units in about 7 MB) stops with `decode.limit` and stays unverified.

### `MaxPcmBytes`

- Renamed to `DecodeOptions.MaxWork`, in work units, and kept for tests and the fuzzer; `MinimumBudget` becomes `MinimumWork`, and `BudgetPerInputByte` becomes `DecodeWork.DefaultPerInputByte` with `IDecoder.WorkPerInputByte` on top.
- The references to `MaxPcmBytes` in `Analyzer.cs` move too: the comment on `Analyze`'s exceptions (line 173) and the negative-option guard (line 181), `ArgumentOutOfRangeException.ThrowIfNegative(options.Decoding?.MaxPcmBytes ...)`; so does the guard in `DecodeRun` (lines 16-22).
- No alias: the option came with the contract pull request (#118), after release 0.1.0, so no released library has it.
- A negative `MaxWork` stays an `ArgumentOutOfRangeException` from `Analyze`, refused before anything is read.
- `DecodeSetup.BitsPerSample` no longer feeds the budget as PCM bytes; it feeds the per-sample charge (`DecodeWork.ForSample`) and the floor of the charge, and still sets the PCM format and the canonical layout.

### How `decode.limit` and the verdict read

- `decode.limit` stays an info on the stream, once per session: "Decoding stopped after 1,214 frames: its work passed the budget of 34,359,738,368 work units that the file's size allows." The message names units and frames, not time, which depends on the machine. It replaces the text in `DecodeSession.EndIfAsked` (line 331), "the PCM it produced passed the budget of N bytes", with "its work passed the budget of N work units that the file's size allows"; the tests that match the old text move with it.
- The verdict is as now: the stream's decoding is `Stopped`, a checksum compared before the stop that failed stays failed (`IsVerified` false), and otherwise `IsVerified` is null, "Verified: unknown (STREAMINFO MD5 not compared: decoding was stopped)". JSON, CSV and the GUI need no change.

### How a decoder that under-reports is caught

- **At run time:** the floor of the charge above catches a decoder that charges nothing or less than its samples, in every fixture, the fuzzer and `DecodeRouteTests`.
- **By its work test:** `DecoderWorkTests`, in the `ComplexityTests` collection so that it runs alone, holds each decoder to its time per unit. The time of the parse and the walk is taken out, since the cases differ widely in it (the WavPack worst case has 8 bytes of bitstream per block, the FLAC reference a large structure pass):
  - **What is timed:** decoding alone. Each case is built in memory and analyzed twice in the same process, with decoding off and on. Its decoding time in a round is the analysis time with decoding on minus the time with it off in that round; its time per unit is the median over the rounds of that difference divided by the units the decoder charged.
  - **Counting the units:** a wrapper decoder (below) sums what the real one charges, so that the report is unchanged. The wrapper replaces the registered decoder through the existing `Analyzer` constructor that takes decoders: the test builds `new Analyzer(DefaultParsers.Tags, DefaultParsers.Formats, DefaultParsers.Codecs, [new CountingDecoder(real)])`; `DefaultParsers.Decoders` is not touched. The wrapper has the real decoder's `Id` and `WorkPerInputByte`, and its `Open` builds a `DecodeContext` (the constructor is public) whose `AddWork` adds to its sum and forwards to the host's.
  - **The reference** is FLAC frames of 4,096 samples of 16-bit stereo with linear predictors of order 8, the shape of `flac` level 5, with its content pinned: the residual is a fixed-seed pseudo-random signal of about 7 bits, coded with Rice parameter 6 in every partition and no escapes, and the frames are walked (lent buffers), so that the walk, which charges nothing, is the same in every round and is subtracted with the rest of the parse.
  - **Interleaving:** a round runs the reference off, the reference on, the case off and the case on, in that order, and a test runs five rounds, so a runner that changes speed between runs changes both sides. The reference's time per unit is taken the same way; the ratio is case over reference. Each run is sized to charge about 2^29 units (about 0.3 s), so a case costs a few seconds and stays under 30 s. The whole of `DecoderWorkTests` takes about 3 to 4 minutes per platform and shard, so it runs in the nightly full run only, on all six, not on pull requests (the project lead, 2026-10-23); a regression is caught within a day.
  - **The assertion:** the case's time per unit is at most twice the reference's. The test also requires that decoding is at least a fifth of the case's analysis time with decoding on, so that a case dominated by the parse cannot pass on a difference of two noisy numbers.
  - **Why a factor of 2 is safe:** the bound's times assume 0.6 ns a unit, which is what the FLAC level 5 row (0.60 ns, B - A, so decoding alone) and the WavPack rows (0.58 to 0.66 ns) show. With the reference at that figure, a case at twice its time per unit is 1.2 ns, which makes the floor 42 s and 2,048 units a byte 2.5 s per MB: still bounded, and a decoder that slow has a weight that is wrong, not noisy. Before the time was taken out, the same factor allowed about 3.2 ns, because the parse inflated the reference (the draft's first version); this version measures what the bound's times assume.
  - **Drift:** every run prints the absolute ns per unit of the reference and of each case, so that the CI history shows slow drift even when the ratio holds.
  - **If it is noisy in the nightly run:** the factor stays at 2 and the noise is root-caused. The factor is not loosened; a test that needs a looser factor no longer tests the bound.
  - A case that fails means a weight is too low; the fix is the weight, not the factor.
- **FLAC's cases:** constant subframes of 8 channels at 32 bits (the most bytes of MD5 per unit); verbatim subframes at 32 bits; order 32 in the 64-bit path (the 33-bit side channel); fixed order 4; frames not walked (`FlacParser.LendsBuffers = false`) with escaped partitions and long unary codes.
- **WavPack's cases:** 16 passes of term 17, of term 18, of terms 1 to 8, and of terms -1 to -3 in stereo; a hybrid block whose error limit makes the search read many bits a sample; floats and 32-bit integers with an extra bitstream; **floats with a shift of 31, a maximum exponent of 255 and zero runs that give every sample the value 1** (the crafted case, which times the fix); **32-bit integers and floats with an MD5**, to hold the 22 units a sample of their hashing (and FLAC's 32-bit constant case holds it there); DSD in the fast mode with 5 history bits and in the high mode; **many small DSD blocks** (about 400 bytes each), which times the table setup; blocks muted because their bitstream ends; sets that never complete.
- **Monkey's Audio and TrueHD** bring theirs: each level and each version range of Monkey's Audio, insane with the largest frames, silent frames and pseudo-stereo frames at insane (which must charge what they do), and a file of insane frames whose samples all cost the cheapest symbol (which must verify in full under the default bound); TrueHD at 192 kHz with 8 matrices and filters of orders 8 and 4.
- **The fuzzer** analyzes with a lowered `MaxWork` and fails on `decode.failed`, so the floor check runs on its mutations too.

### Per-decoder caps

- **`StreamDecoder.MaxWastedSamples` is removed.** The blocks of sets that never complete are charged as they are decoded, so the work budget bounds them, with about the same result on the measured file (1,214 blocks against 1,365). The work the cap bounded was what the PCM budget could not see.
- **Kept, since they bound memory and not time:** `StreamDecoder.MaxSetSamples` (2^24 samples of a WavPack set, `wavpack.md5-set-too-large`), the session's lending ceiling (`DecodeSession.MaxLentSamples`), FLAC's frame ceiling (`decode.frame-too-large`), Monkey's Audio's chunks of 4,096 samples.
- **Kept, since they are the parsers':** the search and resync budgets, which bound the structure pass by the bytes.

### Compatibility and rollout

- **One pull request** changes the contract, FLAC and WavPack together, after the WavPack decoder and before Monkey's Audio and TrueHD:
  - `Core.Abstractions`: `DecodeContext.AddWork` and its constructor's new delegate, `DecodeWork`, `IDecoder.WorkPerInputByte`, the comments of `DecodeContext.CancellationToken`, `IFrameDecoder.Decode` and `DecodeSetup.BitsPerSample`;
  - the host: `DecodeRun` counts work and owns the linked token (created at the start of decoding, disposed in `Analyze`'s `finally`), `DecodeSession` catches the stop inside a frame, picks the finding from `DecodeRun`'s state and checks the floor of the charge on totals, `DecodeOptions` as above, and the `MaxPcmBytes` references and the `decode.limit` message listed under `MaxPcmBytes`;
  - FLAC: `FrameDecoder` charges per frame (its samples, each subframe's order, and the bytes of a frame it walks itself);
  - WavPack: `StreamDecoder`, `PcmBlockDecoder` and `DsdBlockDecoder` charge per block, before the passes, with the DSD table setup; `RestoreFloats` gets the closed form for a value that reached 0 (above); `MaxWastedSamples` goes.
- **Tests that change:**
  - `DecodeRouteTests`: `TheBudgetCountsCanonicalBytesAndStopsEverySessionWithAnInfo` counts charged work; `TheBudgetCountsWholeBytesOfTheSampleWidth` goes; `AGroupsBlocksCountOnceAgainstTheBudget` becomes the work of every block counting, the final block's samples once in `DecodedSamples`; `ABudgetOfZeroStopsAfterTheFirstFrameAndANegativeOneIsRefused`, `TheBudgetIsSharedByTheSessionsOfOneAnalysis`, `ANegativeBudgetIsRefusedWhateverTheFile` and `TheBudgetSaturatesInsteadOfWrappingForAbsurdFigures` move to `MaxWork` and `AddWork`; `ScriptedDecoder` charges `DecodeWork.ForSample` per sample of each channel it returns unless a test scripts otherwise. At f5b6b77 only `DecodeSession` calls the `DecodeContext` constructor; the tests reach a context through `ScriptedDecoder`'s `OnOpen` and `OnOpened` (and the helpers that keep it, `DecodeRouteTests` lines 809 to 1221), which keep working with the new delegate. A test that builds a `DecodeContext` itself passes an `AddWork` delegate; `DecoderWorkTests`' wrapper is the other.
  - `DecodeRouteTests`, new: work charged in a frame that then fails counts; a budget crossed inside a frame stops at the decoder's next check with `decode.limit`, while a canceled analysis still throws from `Analyze`; `DecodeOptions.Stop` inside a frame; a decoder that charges less than the floor, or negative units, is `decode.failed`.
  - `ComplexityTests`: `FlacFramesThatCodeMegabytesEach` sets `MaxWork` (2^30); `WavPackBlocksThatStateMoreSamplesThanTheyCode` asserts `decode.limit` and the time bound instead of the 1,365 blocks of the cap; the comment of `FlacPredictorsOfTheHighestOrderAtTheLargestBlock` ("which the budget does not count") changes, since the budget now counts them.
  - `CorruptFiles` (`AnalyzeAndCheck`) sets `MaxWork` (2^30) and the fuzzer (`Encspect.Fuzz`) `MaxWork` (2^24) in place of their PCM bytes.
  - `MemoryTests` and `InputReleaseTests`: a case that analyzes many files with decoding on and one long-lived token, checking that the linked sources are disposed.
  - New: `DecoderWorkTests` with the FLAC and WavPack cases above, including the crafted floats; WavPack tests for the closed form of `RestoreFloats` against the ported loop, and for the DSD setup charge.
- **Documentation** in the same pull request: the `--verify` cost in `docs/cli.md`, `DecodeRouteTests` and `DecoderWorkTests` in `docs/development.md`, the cap in `docs/formats/wavpack.md` ("Decoding", line 87: "which the PCM budget counts" and the 2^28 cap), the mention of the budget in `docs/formats/flac.md` (line 74, "the host's `decode.*` findings (budget, stop ...)": it says only "budget", so it becomes "work budget"), the docstring of `scripts/fixtures/wavpack_decode.py` that names `MaxWastedSamples`, and the "As built" notes of this design.
- **`CLAUDE.md`**, a line proposed for Architecture rules, after the one on cancellation: "A decoder charges its work through `DecodeContext.AddWork` before it does it, in the work units of `docs/design/decoders.md`, and `DecoderWorkTests` holds each decoder's time per unit."
- **Monkey's Audio and TrueHD and MLP** are built on the new contract: each charges its work from its first commit, brings its cases to `DecoderWorkTests`, and records its weights' measurement in its "As built" section.

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
- `Core.Abstractions` also gets the canonical byte layout as one function: interleaved, signed, little-endian, `ceil(bits / 8)` bytes a sample. FLAC's MD5 uses it, and so do the tests' hashing sink and the manifests' `decoded_pcm_md5`. Monkey's Audio and WavPack compute their checksums over their own layouts.

### Measurements of the decoded audio

This section was called "Host-side PCM analyses, later"; [spectral-analysis.md](spectral-analysis.md) renamed the concept to **measurements** (its decision 2), and its first step built it. What this design fixed stays as the contract:

Lowpass detection and its kin are analysis results, so they belong in the library ("A library others could use"), not in a front end. The hook was fixed in Gate 3 and the measurements came with their features:

- The host's sink (`DecodeContext.DeclareOutput`) forwards each block to the front end's sink and to the stream's measurer, a class of `Encspect.Host` that exists for a stream only when `DecodeOptions.Measure` is set.
- A measurement adds figures to the stream, never nodes. The rule is "decoding never changes the parser's nodes".
- The figures are one field of `StreamInfo`, `Measurements`, as `Verification` is: additive (`schemaVersion` stays 1).
- The figures reach the stream through the host: once the format parser returns, the host attaches each session's figures to the stream whose first node is that session's `DecodeSetup.Stream`, as it sets `NodePaths`. So no parser, decoder, session or sink changed.
- **Decision (Gate 3):** this hook first, the figures with the first feature. The alternative was a later contract change to `DecodeContext` and the rule.

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
| Monkey's Audio | the SDK's `macutil`, built from Monkey's Audio SDK 13.44 (3-clause BSD, CMake), pinned by SHA-256; FFmpeg for 2 channels up to 24 bits | not packaged in any Ubuntu series; the SDK build also covers more than 2 channels, 32 bits and floats, which FFmpeg cannot decode |
| WavPack | `wvunpack -m`, libwavpack's own pass (`wavpack_blocks.c`, already built); FFmpeg | wavpack 5.9.0 |
| TrueHD, MLP | FFmpeg's decoder, with `-downmix` for the lower presentations | FFmpeg 8.0.1 |
| ALAC, TTA, WMA Lossless (later) | FFmpeg; Apple's ALAC source; libtta-c 2.3 (LGPLv3, autotools), pinned by SHA-256 | TTA is not packaged in any Ubuntu series |

- The ASF cross-check already compares FFmpeg's WMA Lossless output with the encoder's input bit for bit. For the other codings, the cross-check compares FFmpeg's output with the reference coder's own (flac, the SDK, libwavpack), so no claim rests on FFmpeg alone.
- Each script asserts the tool versions it was written for (`tool_versions.py`), as the existing cross-checks do.

### Tests beyond the fixtures

- **Contract:** `DecodeRouteTests` with a scripted parser and decoder, as `FormatDetectionTests` scripts parsers:
  - the boundary for each exception, from `Open`, `Decode` and `Finish`;
  - a sink and an `Output` that throw: no `parse.failed`, the exception rethrown from `Analyze`;
  - the stop token, the work budget through `MaxWork` and `AddWork` (inside a frame too, and the floor of the charge), and `decode.frame-too-large`;
  - findings counted once per stream, sessions inside nested routes, sessions ended when a route returns with their findings merged;
  - `IsVerified` for each case of the rule, tallies with nothing stored included, and a per-frame tally whose stream has a gap or fewer samples than it states (not verified, though every value holds);
  - `Decode` returns of 0 for all but a set's final block, counted once in `DecodedSamples`, while the work of every block counts;
  - results that name no declared checksum, or compare more values than are stored (`decode.failed`, results dropped).
- **Bit cursor, MD5 and CRC-32:** `BitCursorTests` against `BitReader` on random data and at every end; `Md5Tests` with RFC 1321's vectors and against `System.Security.Cryptography.MD5`, in the test only; `Crc32Tests` against the existing copies.
- **Boundary:** `DecodingBoundaryTests` (above), with its negative control.
- **Fuzzing:** `Encspect.Fuzz` analyzes the lossless fixtures' mutations with decoding on as well, with a lowered `MaxWork`, and fails on `decode.failed`; its finds join `tests/fixtures/fuzz`.
- **Memory:** `MemoryTests` analyzes a long stream of each decoded coding with decoding on and reads the live heap from inside the sink's `Write` of the last frame, where everything a session keeps per frame is still alive; the growth of that reading above the heap the analysis ends with, between two stream lengths, is bounded per further frame, so a session that keeps something per frame fails (see "Memory tests" in `docs/development.md`).
- **Complexity:** `ComplexityTests`, with a lowered `MaxWork` so the budget stops them in seconds, and `DecoderWorkTests`, which hold each decoder's time per work unit (see [How a decoder that under-reports is caught](#how-a-decoder-that-under-reports-is-caught)):
  - FLAC frames of 65,535-sample constant subframes in 8 channels at 32 bits;
  - Rice codes that run to the end of each frame;
  - predictor order 32 at the largest block;
  - the highest Monkey's Audio level.
- **Threads:** `ParallelAnalysisMatchTests` compares parallel and sequential analysis of the lossless fixture directories. It passes no `DecodeOptions`, so it decodes nothing; no test decodes on several threads yet (the tests that would are in [parallel-decoding.md](parallel-decoding.md), "Verification").
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
   - the GUI's "Decode Audio", documented in `docs/gui.md`;
   - `docs/formats/flac.md` with a "Decoding" section, and its line in `docs/formats/README.md`.
   - A milestone review of the contract and FLAC follows, which also walks the WavPack and TrueHD data flows (above) through the contract as built: which `CodedFrame`s, which state, which tallies, which findings. Its findings are fixed before step 3 starts.
3. **In parallel, each in its own pull request, with its decoder in its format project's `Decoding` namespace and its format's "Decoding" section:**
   - the WavPack decoder (its parser exists), and a "wavpack" codec parser for Matroska's blocks, which carry a reduced header that FFmpeg rebuilds into the 32-byte one (`matroskadec.c` 3608-3670);
   - **the work budget** (decision 8 as changed on 2026-10-23), one pull request after the WavPack decoder and before the decoders below: the contract, FLAC and WavPack together, with `DecoderWorkTests` (see [Compatibility and rollout](#compatibility-and-rollout));
   - Monkey's Audio, parser and decoder, on the work contract, with its cases in `DecoderWorkTests`;
   - TrueHD and MLP, parser and decoder, raw files first, on the work contract, with its cases in `DecoderWorkTests`, built clean-room (decision 10): the format description [docs/design/truehd-format.md](truehd-format.md) comes first, written by an agent that reads FFmpeg's `mlpdec.c`, `mlp.c`, `mlp_parse.c` and `mlpdsp.c` and includes no code, and the parser and decoder are written by an agent that has not read them, from that description, the plan and the test vectors, with FFmpeg only as a black-box cross-check;
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
- **Boundary:** an `EndOfStreamException` from `Open` is `decode.not-possible` ("the configuration ends in the middle of a structure"), not `decode.failed`, since a decoder may throw it. From `Finish`, every exception (`InvalidDataException` included, which has no frame to point to) is `decode.failed`. A negative return from `Decode` is `decode.failed`. `OperationCanceledException` passes through only for the analysis's own token; one the host's state explains (stop, sink failure, budget) ends the session, and any other is `decode.failed` (see [As built: the work budget](#as-built-the-work-budget)).
- **Results:** a decoder's results are accepted for the tallies the parser declared over decoded audio only; a result that names another tally (a header or coded-data checksum, whose values the parser compared), names one twice, or has `Failed` above `Checked` or `Checked` above `Stored` is `decode.failed`. The host sets `Checked` and `Failed` of every tally over decoded audio from the results, 0 where there is none. Two tallies of one name make `Finish` throw `ArgumentException`.
- **Completeness:** `DecodeEnd.IsComplete` is true when the session was not ended early (stop, budget or sink failure), no frame followed a gap, no frame failed, and `DecodeSetup.Samples`, where set, equals the samples decoded. `FramesNotDecoded` counts the frames the decoder was handed and could not decode.
- **Findings:** the host's `decode.stopped` and `decode.limit` are added once per session; `decode.no-decoder` is added only where decoding was asked for. A finding without a node points to the stream.
- **Budget:** the default is the larger of 16 GiB and 256 times the size of the whole file (`DecodeOptions.MinimumBudget`, `BudgetPerInputByte`); a session that passes it stops at once, and the others at their next frame. A negative `MaxPcmBytes` is an `ArgumentOutOfRangeException` from `Analyze`. The work budget replaces this (see [Work instead of samples](#work-instead-of-samples)).
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
- **Not built (at Gate 3):** the host's internal list of measurements (the sink forwarded to the front end's sink only), and every decoder: `DefaultParsers.Decoders` was empty.

## As built in step 2

What the FLAC pull request implements: the parser (`Encspect.Formats.Flac`), the decoder (`Encspect.Formats.Flac.Decoding`, ID `flac`, registered in `DefaultParsers.Decoders`), the codec parser for Ogg FLAC, "Decode Audio" in the GUI and the `flac-crosscheck` job. [flac.md](../formats/flac.md) describes it from the user's side.

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
- **GUI:** "Decode Audio" in the detail window analyzes the file again with decoding on while the old analysis stays on screen; Stop cancels it; a failure shows as "Not decoded". The file list and the summary cache never decode. See [gui.md](../gui.md).
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
  - The work on blocks of sets that never complete reaches neither the sink nor the host's PCM budget, so the decoder limits it itself: `StreamDecoder.MaxWastedSamples` (2^28 samples over all channels), past which it refuses blocks (`decode.frame-invalid`). With it the bound on crafted input holds in samples, which decision 8 counts; what that is in time depends on the coding, and WavPack's worst case is far slower per sample than the 600 MB/s the design estimates for the MD5 (below).
  - **Measured worst case (Linux x64, one core, `encspect --verify`):** mono blocks of 196,607 samples whose bitstream codes one zero run (8 bytes), with 16 decorrelation passes (term 17), nothing muted. 1,000 such blocks as complete sets (about 74,000 bytes: the reviewer measured `complete-1000.wv`, and this note's earlier "3.2 MB" was a slip; 196.6 million samples) took 18.7 s: about 18 ms a block, 10.9 million samples a second, about 22 MB/s of 16-bit PCM (the reviewer measured about 25 MB/s), against 600 MB/s. The same blocks as sets that never complete (2.2 MB, 30,000 blocks) take 22.4 s with the limit at 2^28 (1,365 blocks decoded, the rest refused); with the limit at 2^32 they took 331 s (the reviewer's measurement). Without `--verify` both files take under 2 s.
  - **What the host's budget allows for complete sets:** the default 16 GiB of PCM is 4.3 billion 16-bit samples, about 43,690 such blocks (a 3.2 MB file) and about 13 minutes at this speed. The budget (decision 8) is left as approved; it is a number for the project lead to tune per coding once WavPack's cost is known (a factor per coding, or counting decorrelation passes). An analysis can be canceled. The lead chose on 2026-10-23 to count work (see [Work instead of samples](#work-instead-of-samples)), which also removes `MaxWastedSamples`.
  - **Cost of the refusal probe:** the feed reads every block's sub-block metadata with `BlockContents.Read` in every analysis, decoding or not, to decide whether libwavpack refuses the block. On 20 MB of 9,600 blocks (the largest fixture's small-block variant repeated 400 times; the fixtures themselves are too small to time) the analysis took 1.43 s of user CPU without the probe and 1.57 s with it, with run-to-run noise of about 0.15 s: at most about 15 microseconds a block, and no difference beyond the noise.
  - The Matroska codec parser declares no block checksum: `WavpackVerifySingleBlock` sums the block's header, which Matroska drops, so it cannot be computed from a packet. The checksum tally is declared by the file parser only.
  - Blocks the codec parser cannot rebuild (a header libwavpack would not take, a packet too short or with no block) are handed on as unreadable, not dropped.
  - `wavpack.md5-set-too-large` is reported only when the file stores an MD5, and points to its sub-block.
  - The refusal of a block is one reading: the feed asks `BlockContents` (the decoder's own sub-block reading) whether libwavpack refuses a block, and the parser's findings only explain it. The values of a fast mode's probability tables are checked as the DSD decoder reads them, so a table that is not valid is a `decode.frame-invalid` and not a parser finding.
  - The nodes of a block with no audio are listed beyond the 4,096-block cap (at most 64 more) so that the MD5 sub-block, which comes last, has a node for its findings.
  - The codec parser lists only the first 256 packets, so a decode finding for a later one has no node to point to.
- **Not verified:** the MediaInfo checks of the new fixtures (the local MediaInfo is 24.01, the cross-check asserts 26.01).

## As built: Monkey's Audio

What the Monkey's Audio pull request implements: the parser (`Encspect.Formats.MonkeysAudio`, "Monkey's Audio"), the decoder (`Encspect.Formats.MonkeysAudio.Decoding`, ID `monkeys-audio`, registered in `DefaultParsers.Decoders`), their fixtures, tests and the `monkeys-audio-crosscheck` job. [monkeys-audio.md](../formats/monkeys-audio.md) describes it from the user's side. The source, when it was built, was the Monkey's Audio SDK 13.42 only (the decoder now follows 13.44: see "Seven channels" below and decision 10); FFmpeg's `apedec.c` and `ape.c` were not read for it, and the figures this document took from them ("the draft") were replaced by the SDK's and by measurement.

- **Scope:** the decoder takes files of versions 3.93 to 3.99 and 4.11, the SDK's current decoder (`CAPEDecompress`, which `MACLib.cpp` 42-55 creates from 3.93 on), at all levels (the predictor of 3.93 and 3.94 has no insane level), 8, 16 and 24 bits (the old header's flags name no more), 32 bits and floating point in files with a descriptor, and 1 to 32 channels. The first draft of this section narrowed the scope to 3.99 and 4.11 on the ground that no reference file of an older version could be made; the review found that wrong (the SDK's `mac` decodes them and the FFmpeg sample archive holds a real file of 3.97), and the lead did not accept the narrowing, since decision 5 covers Monkey's Audio decoding without a cut by version. Versions 3.80 to 3.92 (`CAPEDecompressOld`, `Old/UnMACOld.cpp`: its own predictors and, up to 3.89, its own bit coding, about 2,400 lines) came in the next pull request, below ("Versions 3.80 to 3.92"); versions before 3.80 are parsed and reported `decode.not-possible`.
- **Lending:** the parser lends nothing and walks nothing: it finds the frames by the seek table and reads each frame's CRC and special codes. A frame is a node over whole words with `Ranges` (the file stores the audio in words with the bytes of each reversed), and `CodedFrame.Bytes` is the frame's `MappedBytes`, which the decoder puts back in stream order. The configuration is the descriptor and the header (76 bytes).
- **Checksums:** "descriptor MD5" (coded data, compared in every analysis) and "frame CRC-32" (decoded audio). The frame CRC covers all the audio when every frame the header counts was read in full and stores one.
- **Chunks:** the decoder charges the work of 4,096 samples of each channel before it decodes them and checks the token there; a frame may hold 10,000,000 samples. A frame over 64 MiB is not decoded (`monkeys-audio.frame-not-decoded`). The decoder holds one frame's bytes, one chunk of samples and the filters' state; a frame of 24-bit audio of up to 2^23 samples over its channels is held until its CRC has passed, for the SDK's interim-mode retry (`APEDecompressCore.cpp`; verified by `external-interim-24-stereo`, which a `mac` with one changed line of its encoder wrote and the unchanged `mac` 13.44 verifies).
- **Seven channels:** SDK 13.44 codes the seventh channel of a seven-channel file (`Prepare.cpp` starts the loop over the remaining channels at 6 for seven channels), and the decoder follows it. SDK 13.42 (and 13.43) started it at 7 and read six samples of each seven-sample block, so a file written before 13.44 fails its frame CRC-32 (`mac -V`, error 1009) and the decoder reports `monkeys-audio.frame-crc-mismatch` for it; the descriptor's program version (13420 against 13440) tells such a file apart, and the decoder does not use it.
- **Alignment:** the SDK aligns the words of a frame to the first seek entry; the decoder to the end of the WAV header data. They are the same for every file whose first entry points there, and `monkeys-audio.seek-table-start` flags the others.
- **The work, measured** (`DecoderWorkTests`, nightly, on a loaded four-core Linux x64 machine, the factor against the FLAC reference): the weights are 16 a sample by width (`DecodeWork.ForSample`), and for each coded channel 192 for the range decoder, 48 for the predictor stages and one unit for every two taps of the neural network filters (16 taps at normal, 64 at high, 288 at extra high, 1,552 at insane). The draft's 48, 9 and a unit a tap were measured first and failed: fast, normal and the multichannel and float cases took 2.1 to 3.1 times the reference's time per unit, and insane 0.4 to 0.6, because the filters run in vector registers (`System.Numerics.Vector`) at about 0.28 ns a tap and the range decoder and the predictors cost about 150 ns a sample of a channel. With the new weights: fast 0.61, normal 0.89, high 0.92, extra high 0.89, insane 1.13, insane at 8 bits 1.02, 24 bits 1.06 and 32 bits 1.50, insane pseudo stereo 1.31 (one coded channel), insane of the cheapest symbol 1.20, 6 channels 0.80, 24 bits in 8 channels 0.92, floating point 1.24, and flagged silent frames at insane 0.55. The silent frames first measured 1.63 and, at the review, 2.47 in a nightly run, over the factor of 2 (the zero fill, the pairing, the byte conversion and the CRC cost about 42 ns a sample of two channels, against the 32 units charged): they are charged 40 units more a sample of each channel (`FrameDecoder.SilentUnits`), 112 a stereo sample. The review's run, after the change, on the same kind of loaded machine: fast 0.55, normal 0.69, high 0.72, extra high 0.95, insane 1.04, insane at 8 bits 1.35, 24 bits 1.72, 32 bits 1.89, a full frame of 1,179,648 samples of 24 bits at insane 0.95 (the held frame), pseudo stereo 1.08, the cheapest symbol 0.84, 6 channels 0.71, 24 bits in 8 channels 1.07, floating point 0.78. Every case passes the factor of 2, but the insane cases at 24 and 32 bits came to 1.7 and 1.9 in that run, so they are the ones to watch; the measurement is noisy (the reference alone varied between 0.47 and 0.82 ns a unit), so the largest ratios are the ones to watch.
- **Speed:** the insane level decodes about 14 times real time for 44.1 kHz stereo on that loaded machine (0.77 ns a unit, 1,032 units a sample of a channel); the lower levels are several times faster.
- **The allowance:** `WorkPerInputByte` stays 8,192. At the weights above the most a legitimate insane file charges for a byte is 1,032 / 0.2175 = 4,745 units (6,556 at 32 bits, and 6,657 for a pseudo-stereo frame, since the second speed pass charges 32-bit filters three units for four taps: [As built: the second speed pass](#as-built-the-second-speed-pass); `DecoderChargeTests.TheInsaneLevelStaysUnderTheDefaultAllowance...` holds every width and layout) (the cheapest symbol takes 1.74 bits: `external-quiet-insane.ape`, a signal the predictors follow exactly, takes 1.84 bits a sample), so the allowance has room to spare and `DecoderChargeTests.AnInsaneFileOfTheCheapestSymbolStaysUnderTheDefaultAllowanceForItsBytes` holds it. The draft's 1,625 units a sample would have given 7,471.
- **Worst case for crafted input:** frames of 10,000,000 samples flagged silent cost 8 bytes and 1.12 billion units each, so the floor of 2^35 units ends the decoding after 31 frames (the test lowers the budget to 2^32 units, 4 frames, to stay short) (`ComplexityTests.MonkeysAudioFramesThatStateMoreSamplesThanTheyCode`).
- **Memory:** a frame is a record of about 65 bytes (`MemoryTests.ManyMonkeysAudioFramesStayCompact`, bound 140); decoding holds nothing per frame.
- **Versions 3.93 to 3.98 and the interim mode:** the decoder reads them from the SDK's `Old/UnBitArrayOld.cpp` (`CUnBitArray3891To3989`, its own model table and value coding), `Old/NewPredictorOld.cpp` (3.93 and 3.94), the old delta of the neural network filters (`NNFilterCommon.h`, `file version < 3980`) and the stereo order of `APEDecompressCore.cpp` before 3.95; a new interim stage 1 in 64 bits for samples over 16 bits (`Interim.cpp`) replaced the first version, which changed only the rounding of the filters and so verified nothing. Interim mode is kept only when the retried frame's CRC passes, so one damaged 24-bit frame cannot turn the frames after it into failures; the SDK keeps it for the rest of the file, which its tools never see because they stop at the first bad frame. References: the real file `sh3.ape` (3.97, pinned download), and 17 `legacy-*` fixtures from `monkeys_audio_legacy.py`, an encoder written as the inverse of the SDK's decoder and held to `mac -V`, `mac -d` and FFmpeg; the encoder verified at the first run for every version and level it was tried on, 8, 16 and 24 bits, mono and stereo.
- **Versions 3.80 to 3.92** (`OldFrameDecoder`, `OldBits.cs`, `OldPredictors.cs`; the SDK files `Old/UnMACOld.cpp`, `Old/APEDecompressCoreOld.cpp`, `Old/APEDecompressOld.cpp`, `Old/UnBitArrayOld.cpp`, `UnBitArrayBase.cpp`, `Old/AntiPredictorOld.cpp`, `Old/AntiPredictorFastOld.cpp`, `Old/AntiPredictorNormalOld.cpp`, `Old/AntiPredictorHighOld.cpp`, `Old/AntiPredictorExtraHighOld.cpp`, `APEHeader.cpp` `AnalyzeOld` and `APEInfo.cpp`; the format is in [monkeys-audio.md](../formats/monkeys-audio.md), "Decoding"):
  - **Scope:** the levels fast to extra high (the SDK's old decoder throws for any other), 8, 16 and 24 bits, one or two channels. Not decoded, with the reason in `decode.not-possible`: 3.90 to 3.92 without the CRC flag (the SDK's bit array of those versions answers only the 32-bit read, so `mac -d` writes silence and `mac -V` passes it), a level the SDK refuses, a channel count other than one or two (the SDK's old decoder does not refuse it but decodes no samples for it, so the frame fails its CRC or passes on a stored checksum of 0), a header version from 3891 to 3899 (the SDK's bit array of those versions reads no values) or from 3921 to 3929 (`CAPEDecompressOld` refuses a version above 3920), a 3.80 file whose table of start bits is not in the file or lists over 1,048,576 frames, and versions before 3.80. A frame that states more samples than the version's frame holds (9,216, or 73,728 for 3.90 and later and for extra high from 3.80, last frame included) is `decode.frame-invalid`.
  - **Frames and arrays:** the SDK decodes a frame as two whole arrays, and so does the decoder: the first channel's values (Rice or range-coded), its predictor over the array, then the second channel's. It holds three arrays of the version's frame length, not a chunk, and charges and looks at the token for each chunk of 4,096 values of an array, once for the bit coding and once for the predictor (the predictors and the Rice decoders are resumable). The frame's bytes are put in stream order and read as bits; the reads go on into the four bytes after the frame (`CodedFrame.Following`, new for this: the range coder of 3.90 to 3.92 has read two bytes past its array when the SDK steps back, and a frame of 3.80 shares its last word with the next), as the SDK reads them from the next frame. Where an undamaged last frame ends the data, the range coder reads zeros past it (the SDK's bit array zero-fills what the file does not hold, `FillBitArray`), so a file that ends where its coded data does decodes.
  - **3.80:** the frames follow each other bit by bit and the file holds the bit each starts at; the decoder starts each frame at its bit. The SDK seeks only for the first frame and after a gap and continues from the end of the frame before otherwise (`SeekToFrame`); the two are the same in a file whose tables are right, and differ where a start bit is wrong (`monkeys-audio.frame-start-bit` for a bit of 32 or more; `built-old-3800-start-bit-outside-word` passes `mac -V` and is not verified here). The analyzer keeps the tables so that one damaged frame cannot move the ones after it. The parser keeps the table in the decoder's configuration.
  - **Checksum:** the frame CRC as before (32 bits up to 3.82, unshifted); without the flag the 30-bit checksum, declared as "frame checksum" and compared (`monkeys-audio.frame-checksum-mismatch`), and covering all of the audio when every frame was read in full; in 3.90 to 3.92, whose SDK bit array reads no checksum, it is declared with no frame stored.
  - **The work, measured** (`DecoderWorkTests`, an exclusive run on the four-core Linux x64 machine, the reference 0.38 to 0.41 ns a unit): 32 units a value for the Rice codes and 192 for the range coder (the weight of the later versions), and 8, 40, 64 and 120 or 208 for the predictor of the levels fast, normal, high and extra high (before and from 3.83); 10 units a sample of each channel more for a silent frame. The ratio to the reference's time per unit: range-coded 3.91 normal 0.42, 3.92 extra high 0.48 and 3.92 silent frames 0.70; Rice 3.81 high mono 1.07, 3.84 normal at 8 bits 0.79, 3.84 extra high 0.65, 3.86 normal 0.83, 3.88 fast with spikes 1.03, and 3.89 normal with spikes 0.45 (the parse is half of that run). The first weights of the silent frames (the 40 of the later versions) read 0.28.
  - **References:** 38 `legacy-*` fixtures from `monkeys_audio_old.py` (the inverse of `Old/UnMACOld.cpp`: every bit coding, the four predictors, both CRC widths, special codes, the checksum, 8, 16 and 24 bits, mono and stereo, 3.80's bit-by-bit frames, files that end where the coded data does, an overflow of exactly 16 and 32), each held to `mac -V` and `mac -d`; nine damaged copies and four files with 3.99 frames under old headers (`built-old-*`), each rejected by `mac -V` but the copy with the start bit of 32 or more, which it passes; FFmpeg 8.0.1 agrees on 15 of the 38 (the list of the others is in `crosscheck_monkeys_audio_fixtures.py`). Built while reading the SDK: its decoder of 3.86 to 3.88 reads past its bit array for a Rice code of over 131,072 zero bits (the fixture with spikes of 3/4 of full scale crashed `mac`; the committed one has spikes of a tenth).
- **Not verified:** the decoder for a real file of 3.80 to 3.96 or 3.98, of which none could be found (the legacy fixtures cover them through the SDK's decoders); versions before 3.80, which are not decoded; the extra high predictor of 3.80 to 3.82 (128 taps) is not timed on its own.

## As built in step 3: TTA

What the TTA pull request implements: the format parser (`Encspect.Formats.Tta`, "TTA") and the "tta" codec parser for Matroska's frames, both without a decoder (decision 5; decision 10 keeps the decoder for the clean-room split). The Matroska route is step 4. [tta.md](../formats/tta.md) describes it from the user's side.

- **Sources:** built clean-room as decision 10 asks, from the TTA authors' format description (https://tta.sourceforge.net/en/tta-format-description/, with the "Compression Theory" page for the articles), Matroska's codec mapping for `A_TTA1`, and FFmpeg 6.1.1, MediaInfo and the files FFmpeg 6.1.1 and libtta-c 2.3 write as black boxes (libtta-c 2.3, built from its pinned source by `libtta_tool.py`, is run as an encoder only, from the command line; the review of the first version found that FFmpeg alone was the one witness of the three readings below). The FFmpeg and libtta sources this document's table cites for TTA were not read for the parser; the table's claims were checked against the files instead:
  - the header CRC-32 covers the 18 bytes before it, the seek table CRC-32 its entries, and a frame CRC-32 the frame's bytes before it (all with the CRC-32 of zlib and PNG, on every encoder-written fixture of both encoders);
  - an entry of the seek table is the length of its frame with the frame's CRC-32, and the entries add up to the length of the frames (both encoders);
  - a frame holds the sample rate times 256/245 samples, truncated: at 32,000 Hz (33,436.7) both encoders' files show 33,436, so 66,873 samples are three frames, and at 24,000 Hz (25,077.55) libtta-c 2.3's show 25,077, so 50,155 samples are three (rounding would make both two).
- **Checksums:** three tallies, declared by the parser: "header CRC-32" and "seek table CRC-32" (header) and "frame CRC-32" (coded data). The frame tally counts as covering all the audio when the header's CRC-32 holds, the header counts samples, and every frame it calls for is there whole; a stream whose CRCs all hold and that has them is verified without decoding, as the design says. A frame shorter than a CRC-32 counts as stored and failed; a frame cut by the end of the data as stored and not compared. The codec parser declares the frame tally, and the header tally where the container gives it the header.
- **No decoder:** the parsers start a decode session (`StartDecoding("tta", ...)`) only to finish it with their tallies, so a stream's `Decoding` is `NoDecoder` and `--verify` adds the info `decode.no-decoder`. They hand it no frame. When the TTA decoder comes, they feed `CodedFrame`s from the entries they keep; the setup already carries the header's bytes as its configuration.
- **Declared end:** `DeclaredEnd` is the end of the last frame by the seek table where the header and the whole table are there and the table's CRC-32 holds. The host then owns the bytes up to it: an ID3v1 look-alike 128 bytes from the end inside the last frame is `tag.near-miss`, and an ID3v2.4 SEEK pointer into the frames is `tag.next-in-container`. A table that fails its CRC-32 declares nothing, and the SEEK pointer is followed.
- **Audio format 2:** the description does not define the field. libtta-c 2.3 writes 2 for a stream encoded with a password (`external-libtta-16-stereo-password.tta`), and FFmpeg 6.1.1 asks for a password to open it. The parser reports it (`tta.audio-format-encrypted`) and compares the CRC-32s as stored. The first version of the parser assumed that they cover the stored bytes; the review asked for evidence, and the file gives it: each frame's CRC-32 equals the CRC-32 of the frame's stored bytes before it, as in a file without a password, and FFmpeg decodes the file with the password and no CRC error, to the signal that was encoded. With another password it decodes other samples and reports no CRC error, so the frame CRC-32s do not depend on the password. The stream is therefore verified when its CRC-32s hold, as any other. This rests on one file and one password. `DecodeStatus.NotPossible` is for the decoder to give.
- **TTA2:** the description lists a second header, signature `TTA2`, as in development, with a CRC-32 ("CRC32 block footer") that the parser does not compute, since no file shows what it covers. The parser reports the signature (`tta.version-2`) and reads nothing more; no tool at hand writes the version. Detection ranks the signature alone (class 70) below a signature with the 36 bytes of its header and plausible channels, bits per sample and sample rate (1 to 64, 1 to 64, 1 to 1,000,000 Hz; class 80).
- **Limits:** the report keeps at most 2,097,152 entries (`Records.MaxEntries`, as AAC's frame records): the lengths of the seek table for its nodes, and a record of 24 bytes for each frame the data holds. An entry past the end of the data is a 4-byte length and a count. Past the limit the entries and frames are counted and their CRC-32s compared (`tta.entries-limit`), and the graphs and bitrate figures cover the frames before it. The table is read in chunks and walked as it is read, and the position stops growing at the end of the data, so a table of any size neither overflows nor lists its lengths twice.
- **The codec parser:** one packet is one frame, CRC-32 included. The header comes from the configuration (the CodecPrivate, per the mapping) or, without one (FFmpeg's muxer writes none), only the rate, channels and bits from `CodecConfiguration.Format`; the route in step 4 must pass one of the two. `tta.frame-count-differs` compares the packets with the header's count.
- **Series:** the bitrate per frame and per second, as FLAC reports them. The compression ratio of [coding-graphs.md](coding-graphs.md) is not built.
- **Not verified:** the cross-check asserts CI's versions (FFmpeg 8.0.1, MediaInfo 26.01 on Ubuntu 26.04). MediaInfo 26.01 was built from source and run locally. The script was also run against FFmpeg 8.0.1 built from the release tarball on ffmpeg.org (SHA-256 05ee0b03119b45c0bdb4df654b96802e909e0a752f72e4fe3794f487229e5a41, a minimal configuration with the TTA, WAVE, Matroska and PCM parts, outside the repository; Ubuntu 26.04's package itself was not available): all 67 fixtures agree, so the six behaviors that review finding 4 listed (a decoder that fails to open leaving no audio stream, the order of the sample count and table CRC checks, audio formats 0 and 3, `bits_per_raw_sample`, the "CRC error" wording, silence at `-v error`) hold as written and no rule was loosened. The CI job on Ubuntu 26.04 decides for the package's own build.
## As built in step 3: TrueHD and MLP

What the TrueHD and MLP pull request implements: the parser and the decoder (`Encspect.Formats.TrueHd` and `Encspect.Formats.TrueHd.Decoding`, IDs `truehd` and `mlp`, one `TrueHdDecoder` for each, registered in `DefaultParsers.Decoders`), the codec parsers for both IDs (the MP4 and Matroska routes are step 4), the `truehd-crosscheck` job and the cases of `DecoderWorkTests`. [truehd.md](../formats/truehd.md) describes it from the user's side, and [truehd-format.md](truehd-format.md) is the description it was built from. It was written clean-room (decision 10) by an agent that had not read FFmpeg's TrueHD and MLP source: FFmpeg served only as a black-box tool (`ffmpeg`, `ffprobe`), and MediaInfo as another.

- **Lending, by replay:** the walk of a unit's substreams (`SubstreamWalk`, over `SubstreamReader`, a `ref struct` that steps through one substream's syntax) stores each block's residuals, the bypassed bits of the matrices and the bit where the block's data ends in the buffers the session lends (17 planes of a unit's samples for each of the 3 substreams read: 8 coded channels, 8 bypass bits, 1 for the block ends). The decoder reads the same syntax again with the same `SubstreamReader` in replay mode: it takes the parameters from the bits as the walk did and skips each block's data to the stored end, so the residuals are not decoded twice. One piece of code reads the syntax, so the walk and the decoder cannot differ. A callback into the parser's code would have a `Decoding` type implement a parser-project interface, which `DecodingBoundaryTests` forbids. Where nothing is lent (`TrueHdParser.LendsBuffers = false`, or a unit the walk did not store), the decoder's reader decodes the residuals itself.
- **The first unit is read by the decoder:** the decoding session starts when the stream's first major sync has been read, so the walk of that unit had nothing to lend; the decoder reads it itself and charges 16 units for each of its bytes. The tests that count units exactly know it.
- **Checksums:** six are declared, by the parser. Header: the major sync CRC-16, the unit parity nibble and the restart header CRC-8. Coded data: the substream parity byte and CRC-8, which cover all the audio where every substream of every unit has check data and no unit is missing, cut or unreadable. Decoded audio: the lossless check of each substream read (0 to 2), named "lossless check, substream k", which never covers all the audio (decision 6): the compared checks cover a substream's output between its first restart header and its last. The tallies are declared for the substreams of the sync the decoding session was opened with (that of the first unit that could be read), whether or not they were read, and not by the channel count of the presentation: the decoder opens with that sync and does not follow a stream that changes, so a name that depended on what the parser learned of a later sync, or of a unit the decoder read differently, could not match a result (the fuzzer found it: a stream that adds a substream with a later major sync).
- **Output:** the highest substream read. The decoder decodes substreams 0 up to it, because a higher substream's matrices take the channels the lower ones code, and keeps each substream's filter histories, noise generator and lossless accumulator. The PCM is at the stream's bit depth (16 or 24 bits; 20-bit streams are output as 24 bits, as FFmpeg passes them, so no bit is dropped), in the layout's channel order, a block for each unit.
- **After damage:** a unit the parser could not read, or one that follows a gap, is handed to the decoder as unreadable or after a gap (`FollowsGap`), and the decoder drops its state (the substreams, histories and accumulators) until the next major sync. An end of stream marker does the same, and so does a unit the decoder itself rejects (`InvalidDataException`, `EndOfStreamException`, a canceled token): the host does not mark the unit after a `decode.frame-invalid` as following a gap, so the decoder resets on any exception it throws after it began to change its state (the milestone review found false lossless check failures after a splice to a stream of another channel count). At a major sync that changes the stream's parameters the accumulators are invalidated, since the interval a restart header closes there spans two streams; the parser stops handing units to the decoder from a sync that changes the rate, the format or the substream count (until one with the session's parameters returns, which comes with a gap), so the decoder's own refusal of such a sync is a safety net and the stretch costs no finding of its own.
- **Exceptions:** `InvalidDataException` for a unit the decoder cannot read, `EndOfStreamException` for one that is cut, `OperationCanceledException` at the checks of the token (once for each substream and unit); the host reports them as `decode.frame-invalid`.
- **The work a unit is charged** (the weights are measured, below), before the unit is decoded:
  - `DecodeWork.ForSample(3)` (19 units) for each output sample of each matrix channel of each substream decoded, and `StreamDecoder.PeriodUnits` (48) for each sample period of a substream: the noise generator, the history moves and the lossless check, which the fixed work of the channels does not cover (a mono stream cost 2.8 times the reference without it);
  - `StreamDecoder.FilterTapUnits` (1) for each of the 8 FIR taps of a channel that has a FIR filter and the 4 IIR taps of one that has an IIR filter, for each sample of each block, whatever their orders: the filters run as fixed sums over the 8 outputs and 4 errors before the sample, so a channel with a FIR filter of order 1 costs as much as one of order 8 and is charged as much;
  - `StreamDecoder.MatrixTapUnits` (3) for each input of each primitive matrix, for each output sample: the matrix channels of the presentation and, with noise type 0, the two noise channels; the sums are 64-bit;
  - `DecodeWork.PerCodedByte` (16) for each byte of a unit the walk did not store.
  - At most 8 matrix channels, 8 matrices and filters on every channel give about 8 × 10^4 units per substream and unit at 160 samples, and 2.5 × 10^5 for the three substreams: far under 2^27, so a unit is charged once, before it is decoded. The draft's 5 × 10^4 a substream counted 80 units a sample period for the matrices and the taps alone; the built weights count what the measurement asked for.
  - The unit that costs the most for its bytes is the crafted one of `ComplexityTests` (stereo, 160 samples, 8 matrices, filters on both channels, no residual bits): about 220 units a byte (33,000 units a unit over about 150 bytes), far under the 2,048 of the default bound, so the budget does not stop a file of TrueHD that way before its bytes do; lowered to 2^28 it stops after a few thousand units.
- **The timings** (Linux x64, 4 shared vCPUs, load average 4 to 12, so the reference itself reads 0.47 to 0.94 ns a unit between runs; the figures are single runs of `DecoderWorkTests`, 5 rounds each, medians):

| Case | Ns a unit | Ratio to the reference | Decoding share |
| --- | --- | --- | --- |
| crafted, stereo 192 kHz, no filters, no matrices | 0.38 | 0.61 | 37 % |
| crafted, FIR 8 | 0.52 | 1.00 | 48 % |
| crafted, FIR 4 and IIR 4 | 0.49 | 0.66 | 51 % |
| crafted, 8 matrices | 0.53 | 0.87 | 63 % |
| crafted, FIR 4, IIR 4 and 8 matrices | 0.47 | 0.81 | 61 % |
| fixture, stereo 192 kHz | 0.39 | 0.56 | 42 % |
| fixture, 5.1 at 48 kHz | 0.54 | 0.80 | 50 % |
| fixture, 5.1 at 48 kHz, Cholesky filters | 0.48 | 0.71 | 44 % |
| fixture, 5.1 at 96 kHz | 0.41 | 0.51 | 42 % |
| fixture, mono at 48 kHz | 0.62 | 1.05 | 45 % |
| MLP fixture, 5.1 at 48 kHz | 0.76 | 1.28 | 57 % |
| MLP fixture, stereo at 192 kHz | 0.37 | 0.58 | 45 % |

  - **How the weights came out:** the first weights (19 a sample of each channel, a unit for each tap by the filter's order, a unit for each matrix input) read 4.06 times the reference for FIR 4, IIR 4 and 8 matrices, 2.95 for 8 matrices, 2.81 for the mono fixture and 1.98 for the stereo 192 kHz fixture. Four changes, each measured: the matrices sum channel by channel (the loop over inputs inside the loop over samples ran 3.2 ns a input), the filters run as fixed sums (an IIR tap ran twice a FIR tap), the unit of a sample period was added (24, then 32, then 48: the mono fixture read 1.69 at 32), and a matrix input counts 3 (the 8-matrix case read 1.57 at 2). The last complete run is the table; the one before it had a largest ratio of 1.69. The review of the step asked for a margin: a later complete run of the class (load average 8 to 10, 46 cases) read the TrueHD and MLP cases at 0.64 to 1.01 times the reference, none above the 2.0 allowed; a case that comes near the factor is fixed by raising the charge of its stage (and measuring again), not the factor.
  - **Speed:** `encspect --verify` on `TrueHD.raw` (16.2 s of 5.1 at 24 bits, 3.8 MB) took 1.45 s against 0.97 s without decoding, and on `luckynight.mlp` (60.5 s of stereo at 16 bits, 7.9 MB) 1.46 s against 1.07 s, both with the machine shared with other builds: decoding runs at about 30 to 150 times real time. The per-unit absolute figures above are 0.4 to 0.8 ns a unit.
- **Departures from the design above:**
  - The design's per-sample unit alone did not cover the work of a sample period (noise, history, accumulators): `PeriodUnits` was added.
  - A matrix input counts 3 units and a filter tap 1 unit (charged for 8 and 4 taps a channel), not 1 unit a tap by order.
  - The lossless check of a presentation is compared once for each interval, and a mismatch is reported once for each substream (`truehd.lossless-check-failed`); the verification counts all of them.
- **What the tests that hold the decoder found, and fixed:** the fuzzer and the corrupt-file tests found a major sync with more than four substreams that overran the directory, a search for the next major sync that could land behind the rejected unit and loop, nodes that reached past a cut unit and past the end of the data, a stream node that kept the input alive through a closure over the reader, and two declarations of the lossless checks that did not match the decoder's results; each is kept as an input (`tests/fixtures/fuzz/truehd-*.bin`) and a test.
- **After the milestone review:** the decoder resets on any exception it throws after it began to change its state, since the host marks only the unit after an unreadable one as following a gap (a splice to a stream of another channel count gave false lossless check failures); no unit is held across a call that can reload the read window (a damaged unit at a 1 MiB boundary was analyzed with the bytes of the next window); MLP with channel groups at different rates is not read, as the description and the docs say; units past 2,097,152 records are only counted, so a 2-hour stream (8.6 million units at 48 kHz) stays under about 110 MB; a stream that changes its rate, format or substream count gives one information finding and the duration of each stretch at its own rate, not a `decode.frame-invalid` for every later unit; a finding about the lossless check points to the unit where the tree keeps no detail for the restart header. Each is a test of `TrueHdDamageTests` or `TrueHdFindingTests`.
- **Not verified:** the block size check, a change of the matrices, output shifts or quantizer step sizes inside a unit, noise type 0 with a nonzero coefficient on a noise channel, the channel order of MLP arrangements with more than two channels, and substream 2 of a three-substream stream: no real file exercises them (see [the description](truehd-format.md#how-the-implementation-treats-the-open-points)). The decoder's output for the files with Dolby Atmos is that of the first three substreams.

## As built in step 3: WAV and AIFF

What the pull request implements: the WAV and AIFF format parsers (`Encspect.Formats.Wav`, `Encspect.Formats.Aiff`), registered in `DefaultParsers.Formats`. In this step neither had a decoder or started a decode session (the `pcm` decoder of the measurements changed that, below): PCM stores no checksum over decoded audio, so there is nothing to verify, and `--verify` adds neither a `verification` to their streams nor `decode.no-decoder`, which only the host's route raises, when a parser starts a session for a codec ID that no decoder has (TTA, below). The parsers are described in [wav.md](../formats/wav.md) and [aiff.md](../formats/aiff.md).

- **Why no decoder in this step:** the samples are the data. A decoder would copy them to the sink and compare them with nothing; the measurements gave it a purpose (below). The streams of PCM, floating-point, A-law and mu-law report their figures from the headers, and a data chunk of MPEG audio, AC-3, E-AC-3 or DTS (by format tag or by the sub-format GUIDs of Microsoft's headers, and a DTS stream in the 16-bit stereo samples of a PCM file) goes to that codec's parser through `ParseStream`, which has its own decoder where there is one. The bursts of IEC 61937 in 16-bit stereo samples (AC-3, E-AC-3 and DTS payloads) go through `ParsePackets`, one packet for each burst; no decoder is asked for them in this step, since the codec parsers' decoders run on streams the parser read (AC-3 and DTS have none yet).
- **Placeholders:** FFmpeg writing to a pipe leaves `0xFFFFFFFF` in the RIFF and `data` sizes. The parsers read the data to the end of the file and say so in a finding; the stream's duration then comes from the bytes present. MediaInfo 26.01 reports an absurd duration for such a file, so the cross-check skips the fixture.
- **RF64:** the 32-bit `0xFFFFFFFF` markers are resolved from `ds64`, whose table of chunk sizes keeps and lists at most 1,024 entries.
- **Budget:** each parser's tree holds at most 250,000 nodes, as MP4's and Matroska's do (`wav.node-limit`, `aiff.node-limit`); the caps on one level and on one table multiply through nested lists and repeated chunks, and a crafted 18 MB file showed it (12 s and 1.9 GB before the budget).
- **AIFF-C packed codings:** the Common Chunk counts compressed sample frames, whose sizes the AIFF-C draft's appendix C gives (MACE 3:1: 2 bytes for 6 samples, MACE 6:1: 1 for 6, ACE 2:1: 1 for 2, ACE 8:3: 3 for 8, for each channel) and Apple's Core Audio Format Specification for `ima4` (34 bytes for 64 samples); the parser reports the duration as the frames times the samples of one, and MediaInfo 26.01 does not. For GSM and unlisted types no source read says what the count counts, so the stream has no length (`aiff.length-unknown`).
- **Writer:** the only writer evidence is `LIST INFO` `ISFT` in a WAVE file; AIFF has none.
- **Tags:** an ID3v2 tag in a chunk is the chunk's and goes to the tag parser; a tag after the RIFF or FORM chunk is the host's. `DeclaredEnd` makes a candidate that starts inside the chunk a `tag.near-miss` rather than a trailing tag.
- **Follow-up (BW64, Wave64, IEC 61937, `CHAN`):** BW64 is read as RF64 with the checks of BS.2088; Wave64 is a second parser in the WAV project sharing the chunk readers; the IEC 61937 bursts are walked once, lazily, by the packets' enumerator, with the first 1,024 as nodes; `CHAN` uses `CoreAudioLayouts`, moved from MP4's parser to `Core.Abstractions`. None adds a decoder. The details are in [wav.md](../formats/wav.md), [aiff.md](../formats/aiff.md) and `docs/development.md`.
- **Cross-check:** run with FFmpeg 8.0.1 (built from the release tarball) and MediaInfo 26.01, and with FFmpeg 6.1.1 for the first version, with the same result on every compared fixture; the one difference between the two FFmpeg versions is in `docs/development.md`. sox was not used.

## As built: the work budget

What the pull request for decision 8 implements: the contract, the host's count and bound, the work of FLAC and WavPack, and `DecoderWorkTests`. [flac.md](../formats/flac.md) and [wavpack.md](../formats/wavpack.md) describe the charges from the user's side; [development.md](../development.md) describes the tests.

- **Contract:**
  - `DecodeContext.AddWork` (negative units throw `ArgumentOutOfRangeException`, which the host reports as `decode.failed`), `DecodeWork` (`ForSample`, `DefaultPerInputByte`, `PerCodedByte`, `PerTap`) and `IDecoder.WorkPerInputByte` as the draft has them.
  - The `DecodeContext` constructor takes the work delegate before the lent-buffers provider.
  - `DecodeOptions.MaxWork` replaces `MaxPcmBytes` without an alias, and `MinimumWork` (2^35) replaces `MinimumBudget`.
- **Host:**
  - The lead decided on 2026-10-23 that an `OperationCanceledException` from a decoder that the host's own state does not explain (the analysis's token not canceled, no stop, no sink failure, no crossed budget) is `decode.failed`, in `Decode`, `Open` and `Finish`. The cancellation of the analysis passes through as before; a stop, a sink failure or the budget explains the exception, which counts the frame as not decoded (in `Open` and `Finish` it ends the session with the host's finding). A sink failure now cancels the decoding's token too, so a long frame stops soon after it.
  - `Capture` of a sink failure and the crossing of the budget cancel the token; the budget crossing is sticky.
  - `DecodeRun` adds the units (saturating at a long), owns the token linked to the analysis's token and `DecodeOptions.Stop`, and cancels it once the count passes the budget. The budget is the larger of 2^35 and the file's size times the largest `WorkPerInputByte` among the decoders that started a session, so it can grow during an analysis until it is first crossed (a crossing is final, since the canceled token cannot be un-canceled), and only a decoder that opened a stream raises it.
  - `Analyzer.Analyze` creates the run right after it opens the reader, for any analysis with decoding options, and disposes it with a `using`, so also when `Analyze` throws. `InputReleaseTests.TheDecodingTokenOfEveryAnalysisIsDisposedWhenItEnds` checks the disposal and `MemoryTests.ManyDecodingAnalysesWithOneTokenLeaveNoLinkedSourceRegistered` the heap over 20,000 analyses with one token (a planted `using` removed fails both).
  - `DecodeSession` counts the units a decoder charged (the floor is compared on these totals, capped at a long, and a decoder below it is `decode.failed` naming both figures), catches an `OperationCanceledException` that comes while the analysis's own token is not canceled and a stop, a sink failure or the budget holds, counts the frame as not decoded, and picks the finding from the host's state in the order sink failure, stop, budget.
  - The `decode.limit` message is "Decoding stopped after 3 frames: its work passed the budget of 34,359,738,368 work units that the file's size allows."
- **FLAC:** `FrameDecoder` charges, once per frame and before it decodes it, `ForSample` for each sample of each channel, 16 units for each byte of a frame it reads itself, and the taps of each subframe. A frame past the budget is decoded in full, as the draft says.
- **WavPack:** `StreamDecoder` charges every block before it decodes it: the sample charge (muted blocks included), 16 units for each byte of the bitstream sub-blocks, 11 for each pass and sample, and for DSD the per-byte charge and the table setup. `MaxWastedSamples` is gone. `PcmBlockDecoder.Normalize` is the closed form of the exponent loop.
- **Tests:**
  - `DecodeRouteTests`, `ComplexityTests`, `CorruptFiles` and the fuzzer moved to `MaxWork`, and `DecodeRouteTests` gained the cases the draft lists: work charged in a frame that then fails, a budget crossed inside a frame, a stop inside a frame, a stop and the budget together, the floor on totals, negative units, the allowance per input byte, saturation, and a canceled analysis that still throws.
  - `ScriptedDecoder` charges `ForSample` for the samples it returns unless a test scripts otherwise.
  - `WavPackFloatTests` compare the closed form with the loop ported unchanged: every exponent from 1 to 255 over the values around every bit, `int.MinValue`, 0x800000 and 0x1000000, the values from -70,000 to 70,000 and 400,000 random ones; this includes the value that is 0 only after the shift (`int.MinValue` becomes 0 at the first shift of the loop).
  - `DecoderChargeTests` hold the counting to the unit; `DecoderWorkTests` is described in [development.md](../development.md).
- **CI:** `DecoderWorkTests` carries the trait `Category=Nightly`; `shards.py` adds `Category!=Nightly` to a shard's filter unless asked not to, `changes.py` asks for the nightly one on the schedule and a manual run only, `check_shards.py` checks the trait excludes exactly the tests that carry it, and `test_changes.py` the filter per event.

### Departures from the draft

- **Weights moved by the measurements** (the table below has the figures):
  - **WavPack, 16 more units (24 after the speed pass) for each sample of each channel of a PCM block** (`StreamDecoder.WorkPerPcmSample`) and 16 more for a float (`WorkPerFloatSample`). A mono block of zero-run bitstream and one pass cost 26 to 37 ns a sample at 1 to 4 bytes, 1.7 to 3.2 times the reference's time per unit, because the entropy decoder's word, the mute check, the CRC, the copy into the set's planes and the digest's byte splitting cost about 25 ns a sample beyond the passes whatever the width, against the 8 to 13 ns that `ForSample` stands for. The 16-pass worst case (144 units, 0.55 to 0.6 ns a unit) hides this, which is why the draft's calibration did not see it.
  - **WavPack DSD, 32 units for each byte of each channel in the fast mode and 128 in the high mode** (the draft's 16 and 64): the committed DSD fixtures ran at 1.9 to 2.05 times the reference's time per unit with the draft's weights, about 35 ns a byte in the fast mode and 100 ns in the high mode.
  - **FLAC, 2 units for a tap in 64-bit sums** (a linear predictor whose sum does not fit 32 bits, `Prediction.FitsInt`, and every subframe in a 64-bit plane) and **8 units for each sample of a verbatim subframe**, which is the walk's store of the sample, in the time of decoding and in no charge of the draft. A 64-bit tap measured about twice a 32-bit tap (1.4 ns against 0.65 ns).
- **The closed form is the general one.** The draft replaces the loop only for a value that is 0. With that alone a crafted float block (a shift of 31, a maximum exponent of 255, every sample 1) still ran at 2.3 to 2.4 times the reference, and a value of 1 with a large exponent, which the loop normalizes in 23 steps, at 3.1 times, so `Normalize` computes the whole loop without iterating: the shift count is the position of the highest set bit at or below bit 23, limited by the exponent, and a value with no such bit is shifted exponent - 1 times. The output, the shift count and the CRC are the loop's (`WavPackFloatTests`).
- **The DSD setup in the fast mode is counted from the history bits** (`DsdBlockDecoder.SetupWork`): 1,792 units for each of the 2^bits bins (256 cleared, 256 summed, at most 1,280 looked up), 57,344 at the 5 bits the draft counts, instead of a flat 57,000. The high mode charges 4,096.
- **An `OperationCanceledException` no cancellation explains is `decode.failed`** (the lead's decision, above), where the contract step had it pass through.
- **A muted block is charged its passes.** The charge comes before the decode, and a block whose bitstream ends is only found muted by decoding it, so the 16-pass muted block of `DecoderWorkTests` is charged 160 units a sample for work that costs about 8 ns (0.12 of the reference). This over-charges and cannot under-bound.
- **Run time:** the nightly run of `DecoderWorkTests` takes 5.2 to 5.5 minutes on the machine below (the draft estimated 3 to 4), and its 35 tests are one test for each case and the reference's own charge. Six of the cases are zero runs with 0 or 1 pass at 1, 2 and 4 bytes a sample, added after the review: a planted `WorkPerPcmSample = 0` fails four of them (2.08 to 2.81 times) and two more read 1.95 and 1.97, where only one fixture had held that weight; a run on the final code reads their ratios 1.2 to 1.43.

### The work-unit timings

- **Machine:** the build machine of the earlier steps (4 vCPUs, Linux 6.18, .NET 10.0.401, Release), shared with another agent's builds and tests, so these are noisy single-machine figures: the load average was 0.7 to 6, and the reference's own time per unit moved between 0.42 and 1.28 ns across runs. A ratio is the case's time per unit over the reference's in the same round.
- **How:** the complete runs of `DecoderWorkTests` after the first weights were set (the table has the first three; a fourth, after the last change to the tests, passed all 29 with the largest ratios 1.47 for the FLAC constant case and 1.49 for the WavPack fast mode fixture), and the single runs of the cases listed with the draft's weights. Each case charges about 2^29 units (the verbatim case 2^27, the order-32 and the escape cases 2^28).
- **Result, final weights** (the figures are the three complete runs; the first two runs had the FLAC weights of the draft, marked):

| Case | Ns a unit, run 3 | Ratio to the reference, runs 1 / 2 / 3 | Decoding share, run 3 |
| --- | --- | --- | --- |
| FLAC reference (order 8, 16-bit stereo) | 0.66 | 0.82 / 1.00 / 1.14 | 44 % |
| FLAC, constant, 8 channels, 32 bits | 0.82 | 1.35 / 1.45 / 1.29 | 100 % |
| FLAC, verbatim, 32 bits | 0.84 | 1.48 / 1.99 (draft's weights) / 1.41 | 62 % |
| FLAC, order 32 in 64-bit sums | 0.90 | 1.63 / 1.98 (draft's weights) / 1.35 | 74 % |
| FLAC, fixed order 4 | 0.37 | 0.83 / 0.72 / 0.49 | 24 % (30 to 37 % after the residual was made smaller) |
| FLAC, not walked, escapes and long unary codes | 0.22 | 0.47 / 0.35 / 0.40 | 39 % |
| WavPack, 16 passes of term 17 over a zero run (mono) | 0.55 | 0.84 / 0.95 / 0.89 | 100 % |
| WavPack, 16 passes of term 17 (stereo, noise) | 0.68 | 0.98 / 0.93 / 1.19 | 93 % |
| WavPack, 16 passes of term 18 | 0.59 | 0.81 / 0.79 / 0.88 | 92 % |
| WavPack, 16 passes of terms 1 to 8 | 0.82 | 1.38 / 1.07 / 1.31 | 95 % |
| WavPack, 16 passes of terms -1 to -3 | 0.66 | 1.05 / 1.24 / 0.89 | 93 % |
| WavPack, mixed terms in mono | 0.73 | 1.32 / 1.32 / 1.17 | 87 % |
| WavPack, hybrid, search of 20 bits a sample | 0.45 | 0.65 / 0.83 / 0.62 | 76 % |
| WavPack, floats, shift 31, exponent 255 | 0.69 | 1.00 / 1.01 / 1.15 | 100 % |
| WavPack, blocks muted by their bitstream | 0.08 | 0.12 / 0.13 / 0.12 | 95 % |
| WavPack, sets that never complete | 0.45 | 0.62 / 1.01 / 0.72 | 99 % |
| WavPack, many small DSD blocks (the table setup) | 0.57 | 1.06 / 1.01 / 0.89 | 87 % |
| WavPack, DSD fast, uniform table, 5 history bits | 0.40 | 0.54 / 0.56 / 0.55 | 87 % |
| WavPack fixture, floats | 0.62 | 1.16 / 0.98 / 1.04 | 92 % |
| WavPack fixture, 32-bit integers | 0.73 | 1.64 / 0.94 / 1.11 | 93 % |
| WavPack fixture, 24-bit integers | 0.66 | 1.08 / 1.29 / 0.96 | 92 % |
| WavPack fixture, DSD fast | 0.68 | 1.04 / 0.94 / 1.13 | 93 % |
| WavPack fixture, DSD high | 0.62 | 1.14 / 1.18 / 1.06 | 95 % |
| WavPack fixture, hybrid (the two) | 0.69 to 0.78 | 0.90 to 1.29 | 94 to 95 % |
| WavPack fixture, mode fast / very high / extra 6 | 0.86 / 0.63 / 0.74 | 1.35 to 1.66 / 1.03 to 1.16 / 1.06 to 1.64 | 92 to 95 % |

- **What the draft's weights gave** (single runs, ratios; the reference moved between 0.42 and 1.28 ns a unit, so each is good to about 20 %):
  - WavPack, one pass of term 17 over a zero run, at 1, 2, 3 and 4 bytes a sample: 1.94, 3.15 (the reference read 0.42 ns that run), 1.72 and 2.24;
  - crafted floats (shift 31, exponent 255) with only the zero case in closed form: 2.30 and 2.43; with the loop as libwavpack has it, **14.6 times, 7.7 ns a unit, about 230 ns a sample for 30 units** (the draft estimated 6 to 14 times from the loop, so the top of that is the measure), so the crafted case catches the escape and the closed form is needed;
  - the committed fixtures: DSD fast 1.88 to 1.96, DSD high 1.96 to 2.05, hybrid 1.56 and 1.75, mode fast 1.56, floats 1.29 to 1.52, 32-bit integers 1.35, extra 6 1.29 to 1.69, 24-bit integers 1.10;
  - the terms were within the factor with the draft's weight of 8: term 17 1.00, term 18 1.21, terms 1 to 8 1.24, terms -1 to -3 1.37.
- **The weights the design marked unverified:**
  - **FLAC's 64-bit path** at 1 unit a tap: 1.18 to 1.98, contradicted; now 2 units, 1.35.
  - **WavPack terms other than 17:** confirmed at 8 units a pass (0.79 to 1.38); raised to 11 after the speed pass (see below).
  - **Hybrid:** the search of 20 bits a sample is charged more than it costs (0.62 to 0.83), the fixtures are in the factor (0.90 to 1.29); the 16 units for each byte of the bitstream stay.
  - **Floats:** the per-sample charge was low by the same 25 ns as every WavPack sample, plus 7 ns for restoring a float (a crafted float block ran 1.4 ns a unit against 1.2 for an integer one); fixed with the 16 and 16 above. The exponent loop was the 14.6 times above.
  - **DSD:** the per-byte weights were low (above); the setup is confirmed by the case of many small blocks (0.89 to 1.06 at 57,344 units for a block of 405 bytes and 100 bytes of audio); `InitTable`'s steps are at most 2,803 in `Approach` (checked over every rate) and 128 table writes, so 4,096 holds with room (**not timed**: no case of small high-mode blocks).
  - **3 units for each hashed byte:** kept. The constant-subframe case of 8 channels at 32 bits, which is mostly the MD5 and the canonical bytes (22 units a sample), ran 1.05 to 1.8 times the reference (1.29 to 1.45 in the three complete runs); the WavPack 32-bit and float fixtures, with their MD5, 0.94 to 1.64. A 32-bit sample costs about 19 ns in FLAC and 35 ns in WavPack, the second because of the digest's byte splitting, which the +16 covers.
  - **16 units for each coded byte:** kept. The cases that read bytes themselves are the FLAC frames not walked (0.35 to 0.47, an over-charge: escapes and unary codes are cheap per byte) and the hybrid search; the fixtures carry real bytes and are in the factor.
  - **Not measured:** Monkey's Audio's weights and the 1.74-bit cheapest symbol (its pull request; TrueHD's are measured in [As built in step 3: TrueHD and MLP](#as-built-in-step-3-truehd-and-mlp)), the draft's WavPack per-sample figure for 8-bit and 24-bit streams except through the 24-bit fixture, and anything with the correction file.
- **Noise:** the largest ratio of the final weights in any of the three complete runs is 1.66 (WavPack mode fast, run 1; the two FLAC cases whose weights changed read 1.35 and 1.41 in run 3); the reference itself reads 0.82 to 1.14 against itself, so the factor of 2 leaves about 20 to 30 % of margin over the noisiest case. `flac-constant-8-channels-32-bits` and the fixture of the fast mode are the ones to watch in the nightly history.

### What a planted bug does

- **WavPack stops charging 16 units a PCM sample** (`WorkPerPcmSample = 0`): four of the six few-pass cases fail (see above); before them one fixture held it.
- **WavPack stops charging its passes** (the `AddWork` of the passes removed): `wavpack-term-17`, `-term-18`, `-terms-1-to-8` and `-term-17-zero-run` fail with 3.2, 3.3, 4.2 and 4.3 times the reference. The floor of the charge does not see it, and the fixtures still pass, as the draft says.
- **FLAC stops charging its taps:** the case of order 32 in 64-bit sums fails (2.16 times, with the draft's 1 unit a tap; the reference loses its taps too, which hides most of it), and `DecoderChargeTests` and `TheReferenceChargesWhatItsFramesSay` fail at once, since they hold the counting to the unit.
- **FLAC stops charging its samples:** the floor catches it in every fixture: `decode.failed`, "The decoder charged 40960 work units for 4096 samples of 2 channels, fewer than the 131072 that 16 units for each of them make."

## As built: the speed pass

A benchmark of the built decoders against flac 1.4.3, wvunpack 5.6.0, mac 13.42 and FFmpeg found them 2.5 to 6.7 times slower than the fastest reference with `--verify`, and this pass closed part of that. The inputs were 300 s files of pink noise with tones (noisier than music, so every tool runs faster on music), one pinned core of a shared Xeon at 2.8 GHz, load average under 1.5, five interleaved runs, minimum of the wall time; the weights are unchanged (below).

- **Reading:** the sources of FLAC, Monkey's Audio and TrueHD slide their window instead of reloading it, and FLAC's scanner asks for 64 KiB (doubling when a frame is cut) instead of 1 MiB at every frame. FLAC read 3.3 GB for a 34 MB file and 7.3 GB for 133 MB; it now reads about 1.6 times the 16-bit file (58 MB) and 1.2 times the 24-bit one (156 MB), with or without `--verify`: there is no second pass for the decoding, and the formats without a decoder read as much beyond one pass (measured as bytes of `read` calls: TTA 1.6 times, TrueHD 1.6, WavPack 3.1, Monkey's Audio 2.5 and, with `--verify`, 3.5, none of it through the old pattern). The analysis of the 16-bit file went from 1.07 to 0.78 s and of the 24-bit file from 2.21 to 1.61 s.
- **MD5:** the managed `Md5` stays as it was. It runs at about 470 MB/s once compiled, as measured on 64 MB chunked as the decoders feed it and on 1 MiB chunks, before and after a rewrite that read the words through one span cast and kept the state in locals (both 460 to 480 MB/s); the 140 MB/s of the benchmark's profile was a sampling artifact of the first seconds. It is not hashed from a system library: that is refused in FIPS mode and absent in browser WebAssembly.
- **Bit cursors:** `BitCursor` and `LsbBitCursor` keep a 64-bit buffer refilled a word at a time. WavPack default `--verify` went from 3.20 to 3.00 s; FLAC's walk showed no change beyond noise.
- **Loops:** WavPack's stereo decorrelation pass runs over pairs of samples with the history in locals, the word decoder reads the three medians of a channel through one span, and the digest and the canonical PCM writer pack a channel's samples without a loop over bytes (`CanonicalPcm.Write` for the common widths without a bounds check per sample).
- **Vectors:** Monkey's Audio's neural network filters use the multiply-and-add-adjacent-pairs instruction (AVX-512BW, AVX2, SSE2, or Arm's widening multiply-add), `-c5000` `--verify` went from 15.35 to 9.30 s in the same run. FLAC's linear predictor sums the oldest taps of a window of 12 or more in vectors and the newest four to eleven in scalar code (a vector load over the store of the previous sample stalls for about 12 ns, more than the sum): 11.6 to 5.9 ns a sample at order 12, 19.6 to 8.7 at 24 and 26.0 to 9.5 at 32, none of which a file of `flac -5` (order 8 at most) reaches.
- **Runtime settings of the CLI:** see "CLI runtime settings" in [development.md](../development.md): tiering cost 17 to 40% of a decode of a few seconds, and the settings recover it. The release CLI is NativeAOT from the release after 0.1.0 (0.1.0 was ReadyToRun), which has no tiering: the settings apply to the JIT build only ("NativeAOT" in the same file).

The decode times of the benchmark files after the pass, `--verify` on the JIT build of the CLI with its settings, against the fastest reference (minimum of five; the "before" is `origin/main` at d99f80d on the same machine in the same interleaved run):

| Stream | Before | After | Fastest reference | Ratio before, after |
|---|---|---|---|---|
| FLAC -5, 16-bit/44.1 kHz | 2.12 s | 1.34 s | 0.33 s (`flac -d`) | 6.4, 4.0 |
| FLAC -8, 24-bit/96 kHz | 4.34 s | 2.36 s | 1.17 s (`flac -d`) | 3.7, 2.0 |
| WavPack default, 16-bit | 3.14 s | 2.13 s | 0.87 s (`wvunpack`) | 3.6, 2.5 |
| WavPack `-hh`, 24-bit | 8.92 s | 7.71 s | 2.98 s (`wvunpack -v`) | 3.0, 2.6 |
| Monkey's Audio `-c1000` | 4.52 s | 2.79 s | 1.15 s (`mac -d`) | 3.9, 2.4 |
| Monkey's Audio `-c3000` | 5.27 s | 4.34 s | 1.65 s (`mac -d`) | 3.2, 2.6 |
| Monkey's Audio `-c5000`, 16-bit | 16.17 s | 8.87 s | 5.17 s (`mac -d`) | 3.1, 1.7 |
| Monkey's Audio `-c5000`, 24-bit/96 kHz | 42.93 s | 22.78 s | 12.08 s (`mac -V`) | 3.6, 1.9 |

- **What is left:** the Rice and range decoders and the per-sample structure (Monkey's Audio below `-c4000`, WavPack's entropy decoder and passes, which are serial per channel and do not vectorize) are where the references stay 2 to 2.6 times ahead. FLAC's walk alone, the analysis without decoding, takes 2.4 times `flac -d`'s whole decode at 16-bit, and the decoding step adds the CRC-16, the repacking and the MD5.
- **The work weights are unchanged.** `DecoderWorkTests` (62 cases, nightly) passes on the final code:
  - The largest ratio is 1.57 (the TrueHD mono fixture, which the pass did not touch: 1.44 on the same machine before).
  - Next come 1.45, 1.42, 1.42 and 1.35 for WavPack cases that read 1.04, 1.07, 1.03 and 1.05 before.
  - The reference, FLAC's decoder, is 25% faster per unit (0.57 to 0.43 ns), so every decoder that gained less than it reads a higher ratio.
- **Time per unit:** it fell or stayed within noise for every case but five, and the budget's bound in seconds only improved.
  - The five that rose: `wavpack-0-passes-8-bit-zero-run` 0.54 to 0.63 ns, `truehd-crafted-fir-4-iir-4-8-matrices` 0.48 to 0.54, `monkeys-audio-normal-float` 0.53 to 0.56, `wavpack-sets-that-never-complete` 0.51 to 0.54, `wavpack-dsd-fast-5-history-bits` 0.226 to 0.236.
  - The largest is 0.71 ns (`wavpack-terms-mono`) against 0.99 ns (`monkeys-audio-insane-32-bit`) before.
  - A weight is raised for a case at the limit of 2, and none is near it; if a CI runner's nightly reads WavPack's terms above 1.9, the per-pass weight is the one to raise (done below).
- **Weights raised after the pass:** the nightly's largest WavPack ratios were 1.83 (`wavpack-terms-mono`), 1.59 and 1.53 (`wavpack-0-passes-32-bit-zero-run` in two runs) and 1.61 for `truehd-crafted-8-matrices`, too little margin for a CI machine. The decoders did not get slower; the reference got faster per unit.
  - `StreamDecoder.WorkPerPass` went from 8 to 11 and `WorkPerPcmSample` from 16 to 24, for 40 units for a 16-bit sample of a channel instead of 32 and 11 for each pass.
  - Measured here, three runs each of the nightly: with 11 and 20 the worst WavPack ratio was 1.55 (`wavpack-0-passes-16-bit-zero-run`); with 11 and 24 it was 1.17 and 1.47 (`wavpack-fixture-mode-fast`) in two runs. `wavpack-terms-mono` read 0.79 to 0.98 and `wavpack-0-passes-32-bit-zero-run` 0.97 to 1.44 with 20.
  - `truehd-crafted-8-matrices` read 0.57 to 1.29 on the same code, so its matrix charge (3 a tap) is unchanged. The other cases above 1.5 in these runs, `monkeys-audio-insane-32-bit` (1.35 to 1.75) and `truehd-fixture-mono-48k` (1.72 once), move with the reference's noise and are not driven by these weights.
  - The legitimate worst cases stay in the budget: a WavPack file cannot exceed 864 units a byte for integers or 952 for floats outside zero runs, against 2,048, and `ComplexityTests.WavPackBlocksThatStateMoreSamplesThanTheyCode` now stops after about 4,369 blocks.
- **Table of the run:** the unchanged-weight ratios, after / before:
  - Monkey's Audio: insane 0.54 / 0.84, insane 24-bit 0.70 / 1.08, high 0.97 / 0.67, normal 0.85 / 0.61.
  - FLAC: fixed order 4 0.69 / 0.77, order 32 in 64-bit sums 0.81 / 0.98.
  - WavPack: term 17 0.96 / 1.01, term 18 1.07 / 0.89, terms 1 to 8 1.35 / 1.05, mono terms 1.45 / 1.04.
  - TrueHD fixture mono 1.57 / 1.44, the reference's own case 1.27 / 1.07.

## As built: the second speed pass

The first pass left the decoders 2 to 4 times behind the references; this one measured where the rest of the time goes before it changed anything, and implemented only what promised 15% or more of a format's analysis or `--verify` time. Same inputs and machine as the first pass (300 s files of noisy pink noise, one pinned core of a shared Xeon at 2.8 GHz, load average under 1.5, interleaved runs), profiles by `dotnet-trace` (sampled thread time) of the JIT build of the CLI.

**Where the time goes** (before this pass; `--verify` minus analysis is the decoding step, the shares are of the decoding step and come from the profiles):

| Stream | Analysis | Decoding step | Reference | Decoding step: shares |
|---|---|---|---|---|
| FLAC -5, 16-bit/44.1 kHz | 0.73 s | 0.55 s | `flac -d` 0.35 s | linear predictor 48%, MD5 28%, canonical PCM bytes 8%, channel recombination 8%, the frame's glue 8% |
| WavPack default, 16-bit | 0.36 s | 1.65 s | `wvunpack` 0.84 s | entropy decoder 64%, decorrelation passes 20%, MD5 and the byte packing before it 10%, CRC and mute loops 4%, planes 1% |
| WavPack `-hh`, 24-bit/96 kHz | 0.50 s | 7.2 s | `wvunpack` 3.08 s | decorrelation passes 66%, entropy decoder 23%, MD5 and packing 7%, the rest 4% |
| Monkey's Audio `-c3000` | 0.51 s | 3.3 s | `mac -d` 1.7 s | prediction stages and the value loop 43%, range decoder 29%, neural network filters 17%, CRC-32 5%, sample conversion 4%, the rest 2% |
| TrueHD, 24-bit/96 kHz | 2.4 s | 1.0 s | FFmpeg 10.1 s | matrices 36%, filters 38%, the rest 26% |

- **The analysis in the run:** it is 57% of a FLAC `--verify` run, 65 to 70% of a TrueHD one and 5 to 20% of WavPack's and Monkey's Audio's; about 0.15 s of every run of the release build (0.24 s of the JIT build) is the start of the runtime and of the host.
- **Not where the time goes:**
  - The decoders do not read what the parser walked again: FLAC and TrueHD decode from the planes the walk filled; WavPack and Monkey's Audio never walk their entropy-coded data. The 3.1 times the file that WavPack reads, and 2.5 for Monkey's Audio, are the parser's own: the cache of WavPack's block probes loads 64 KiB for a 32-byte header (`BlockCache.Load`), and Monkey's Audio's descriptor MD5 is a pass over the file of its own (`Reader.Hash`). Reading a file from the page cache costs 1 to 2% of a `--verify` run (the profile's `pread`), 5 to 8% of a WavPack analysis.
  - Work charges, cancellation checks and sink calls are under 0.5% in every profile.
  - The canonical PCM bytes before the digest are 4 to 8% (FLAC 3.8%, WavPack 1.5 to 2.3%), the MD5 itself 6 to 13%; MD5 is bound by its dependency chain (about 4.5 cycles a byte, 600 MB/s at 2.8 GHz; the first pass measured 470 MB/s), so a digest in place or a better MD5 gains 3% at most.
- **Where the cycles are:** the serial per-sample work of the entropy decoders and predictors, which the references run 2 to 3 times faster than the .NET code; and the start of the loops (tier 0, below).

**What changed** (each its own commit; reports are byte-identical to `origin/main` c94e71d for every fixture of every format and for the benchmark files, JSON and CSV, with and without `--verify`):

- **Rice residuals:** `BitCursor.ReadRice` reads a partition's values while the bit buffer holds them whole, with the buffer in locals, and the residual walk reads the rest one value at a time as before (the walk is 55% of the analysis of a FLAC file). Analysis 0.73 to 0.54 s (-26%), `--verify` 1.27 to 1.10 s (-13%); `BitCursorTests.ReadRiceLeavesTheCursorWhereReadingTheSameValuesOneAtATimeDoes` holds it to the one-at-a-time reading over 60,000 random cases: a random prefix of `Read`, `ReadBit`, `CountZeros`, `Skip` and `AlignToByte` (so the buffer is in any state, refilled byte by byte near the end, or past the end), parameters 0, 14, 30 and 31 and the rest, runs of exactly 63 and 64 zeros, data cut inside a code; position, bits left and the overrun flag must equal the slow cursor's. The residual walk goes on after each value the fast path stops at (one value at a time, then the fast path again); subframes of the side channel of 32-bit streams keep the one-at-a-time reading.
- **TrueHD checks:** the substreams' CRC-8 takes eight bytes at a time (eight tables, the byte step applied k times) and the parities fold words: 19% of the analysis profile of the 24-bit/96 kHz file; analysis 2.56 to 2.30 s.
- **TrueHD codewords:** the codeword branches mispredict (the code is a unary-like prefix code over noise), and the cursor's fields were reloaded for every bit. The samples that start well before the end of a substream take their codeword from a table of 512 entries per codebook, built by running the slow reader on every nine bits, with the buffer in locals (`BitCursor.Detach` and `Attach`); the last samples, and any codeword the table rejects, go through the old code, which names the faults and leaves the cursor where it did. 24-bit/96 kHz: analysis 2.30 to 1.72 s, `--verify` 3.10 to 2.78 s; 16-bit/48 kHz -11% and -2%.
- **Hot loops, optimized at the first call:** see "Hot loops and tiering" in [development.md](../development.md). The loops of MD5, CRC-32, the FLAC CRC-16, the Rice loop, the residual walk, the linear predictor, the recombination and the canonical PCM writer are no longer left to tier 0, which cost the descriptor MD5 of a Monkey's Audio file a third of its analysis.
- **Monkey's Audio 32-bit weight:** see "Work weights" below.

The decode times of the benchmark files, JIT build of the CLI with its settings, minimum of five runs (three for the last three rows). The figures of the bullets above come from separate, earlier runs of the machine (the load of other sessions moves a minimum by up to 10%), so a bullet's before and after belong together and do not add up to this table; the references are the fastest of `flac -d`, `wvunpack`, `mac -d` and FFmpeg:

| Stream | Analysis before, after | `--verify` before, after | Reference | Ratio before, after |
|---|---|---|---|---|
| FLAC -5, 16-bit/44.1 kHz | 0.77, 0.52 s | 1.29, 1.02 s | 0.32 s | 4.0, 3.2 |
| FLAC -8, 24-bit/96 kHz | 1.24, 0.83 s | 2.40, 1.88 s | 0.98 s | 2.4, 1.9 |
| WavPack default, 16-bit | 0.35, 0.34 s | 1.89, 1.84 s | 0.77 s | 2.5, 2.4 |
| WavPack `-hh`, 24-bit/96 kHz | 0.45, 0.48 s | 7.64, 7.36 s | 2.99 s | 2.6, 2.5 |
| Monkey's Audio `-c1000` | 0.53, 0.34 s | 2.69, 2.51 s | 1.09 s | 2.5, 2.3 |
| Monkey's Audio `-c3000` | 0.48, 0.36 s | 4.01, 3.73 s | 1.65 s | 2.4, 2.3 |
| Monkey's Audio `-c5000` | 0.51, 0.38 s | 10.00, 9.16 s | 4.71 s | 2.1, 1.9 |
| TrueHD, 16-bit/48 kHz | 1.62, 1.50 s | 2.24, 2.13 s | 9.40 s (FFmpeg) | 0.2, 0.2 |
| TrueHD, 24-bit/96 kHz | 2.40, 1.75 s | 3.70, 2.75 s | 9.93 s (FFmpeg) | 0.4, 0.3 |

- **The ReadyToRun publish** (the release build of the time; the release is NativeAOT from the release after 0.1.0, whose measurements against it are in "NativeAOT" of [development.md](../development.md): at or below it on every row measured; the 128-bit loops of Monkey's Audio's neural network filters were unrolled for it, which the same section describes), minimum of four: FLAC -5 analysis 0.65 to 0.39 s and `--verify` 1.15 to 1.03 s; FLAC -8 24-bit 1.16 to 0.73 s and 2.11 to 1.79 s; Monkey's Audio `-c3000` analysis 0.32 to 0.28 s; TrueHD 24-bit 2.35 to 1.49 s and 3.62 to 2.46 s; WavPack unchanged.
- **New gaps:** FLAC `--verify` 3.2 times `flac -d` (analysis 1.6 times); WavPack 2.4 to 2.5; Monkey's Audio 1.9 to 2.3; Encspect is 3.5 to 4.5 times faster than FFmpeg's TrueHD decoder on these files. The analysis of a WavPack, Monkey's Audio or FLAC file is now 0.3 to 0.5 s of 300 s, including the 0.15 s of start.
- **Tried and not kept:**
  - WavPack's word decoder with the bit buffer and the medians in locals: -6% (16-bit) and -7% (24-bit) of `--verify`, a second copy of the algorithm; a variant of it without branches (masks for the unary count, the held bits and the median updates) was 10% slower than the branches, since a predicted branch cuts the dependency chain through the bit position that a mask keeps.
  - A mask in place of the branch of `Apply` (the 16-bit and the wide form of the decorrelation weight): 17% slower, the branch is predictable.
  - Monkey's Audio's range decoder without the remainder (`low -= q * range` for `low %= range`): no change, the JIT already shares the division.
  - The optimized-at-first-call attribute on WavPack's and TrueHD's loops (no change) and Monkey's Audio's range decoder and predictors (3 to 6% of `--verify`, within the noise of five runs): not marked.
- **What is left:** the entropy decoders and predictors of WavPack and Monkey's Audio are 70 to 90% of their `--verify` time, serial per sample, and 2 to 2.5 times the references; a lower bound for an in-process gain is a rewrite of their loops for the dependency chains (fusing decorrelation passes, two channels in one vector). The multi-core gains need a change of the decoder contract and are in the lead's list, not here; [parallel-decoding.md](parallel-decoding.md) is the draft design of decoding frames in parallel.

**What `DecoderWorkTests` times.** The JIT build, in the test host: its factors above are the JIT's, tiered, with the runtime's best vector paths. The NativeAOT build of the CLI (the release) is not what it times. By the `--verify` minus analysis method on the 300 s crafted files (minimum of rounds, one pinned core, the review's figures before the 32-bit loop was unrolled), the ratio to the FLAC reference in the same build read, for NativeAOT: Monkey's Audio insane 0.57, insane 24-bit 0.65, extra high 0.73, normal 24-bit 8 channels 0.81, fast 0.53 and insane 32-bit 1.69 (the JIT build by the same method: 1.32, 1.33, 1.44, 1.41, 1.08 and 1.52). The 32-bit case was the one above the others because `Dot(ReadOnlySpan<int>)` ran `Vector<T>` at 128 bits in one chain; it now keeps four independent sums (see "NativeAOT" in [development.md](../development.md)). The limit of 2 is checked on the JIT build only.

**Work weights.** `DecoderWorkTests` (62 cases, nightly) on the final code, five runs of the class (three before the change below and two after), the factor against the FLAC reference:

- Worst before the change: `monkeys-audio-insane-32-bit` at 1.08, 1.66 and 1.30 (0.76 to 1.10 ns a unit); the next cases read 1.16 and below.
- **The reference moved, and the method was at fault.** `flac-reference` was run alternately on `origin/main` c94e71d and on the branch in one session: 0.37 to 0.44 ns a unit on c94e71d, 0.48 to 0.59 ns on the branch. The raw times (median of eight rounds, seconds): the analysis without decoding fell from 0.25 to 0.16 (-37%), the analysis with decoding from 0.48 to 0.44 (-7%), so their difference rose from 0.23 to 0.29.
  - **Not a regression of `--verify`.** The same file timed directly, `ReadRice` where the walk stores against the per-value loop there (seven rounds, one pinned core): FLAC 16-bit `--verify` 0.94 against 1.13 s, 24-bit 1.85 against 2.16 s; in the test host the run with decoding on 0.44 against 0.52 s. Where the walk does not store, 0.49 against 0.64 s. The store path wins in both.
  - **Cause:** "decoding off" does not store the residuals, "decoding on" does, and the lent-plane stores of the walk (1 to 2 ns a value) were counted as FLAC's decoding time. While both walks were equally slow the stores hid in the per-value loop; with a walk that is 40% faster they show. Removing the attributes of the decode loops changed nothing (0.28 against 0.29 s).
  - **Correction of the method, not of the factor:** for FLAC the "off" run is now a run with a hollow decoder (`HollowDecoder`: it charges the fixed work of each sample, as the host demands, and does nothing else), so the parser lends and the walk stores in both runs and only the decoding is in the difference. On the corrected method the reference reads 0.41 to 0.50 ns on c94e71d (median 0.46) and 0.46 to 0.52 on the branch (median 0.47) in one interleaved session: the shift is gone. The cases of the other formats keep "decoding off" as before (WavPack and Monkey's Audio store nothing in the walk; TrueHD's walk is unchanged in its stores).
- **The change:** the filters of 32-bit samples (64-bit sums over 32-bit history, half the lanes of the 16-bit filters) are charged three units for every four taps instead of one for every two (`FrameDecoder.TapUnits`): 1,426 units a sample of a channel at insane, 6,556 for a byte of a legitimate file against the allowance of 8,192. The case reads 1.15 and 0.89 in two runs, the other 61 cases at most 1.23 (`wavpack-fixture-dsd-high`). No limit was widened.
- The charges of the other decoders are unchanged.
- With the corrected reference, three full runs (62 cases each, all passing): every case at most 1.15 (`wavpack-fixture-dsd-high`, 0.54 to 0.60 ns), then 1.13 (`wavpack-fixture-mode-fast`); the 32-bit insane case 0.68 to 0.96; the reference reads 0.45 to 0.62 ns. No weight needed to change.

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

The project lead decided all nine on 2026-10-22: decision 1 as below, in place of the separate decoder projects this design had recommended; decisions 2 to 9 approved as written. The lead changed decision 8 on 2026-10-23, after the WavPack decoder; the lead approved it the same day. The lead added decision 10 on 2026-10-24.

1. **Layout (the lead's choice):** each decoder lives in its format project, in the namespace `<project>.Decoding`; parser code never references it, which `DecodingBoundaryTests` checks in the compiled assembly, and the host registers the decoders; `CLAUDE.md` then says "no third-party decoders". It works because the decoder reads the bits with the parser's own code, a change that breaks one fails the same build, and the only coupling that could harm, the parser's on its decoder, is checked. The alternative it replaces: a decoder project per format that references its format project.
2. **Data flow (approved):** the parser hands each frame to a decode session while it reads it, in the one pass over the file; a prototype in the contract step measured the second entropy pass at 31 % of decoding time (median of 12 files), so the session lends the parser its buffers. Alternative: a second pass the host drives.
3. **Defaults (approved):** decoding is off in the library, the CLI (`--verify`) and the GUI ("Decode Audio" in the detail window); the file list never decodes, no verdict is cached, and the CSV's `Is Verified` is filled only with `--verify`. Checks over coded data run in every analysis, Monkey's Audio's descriptor MD5 included (3.98 and later): its parser then reads the whole file, and a mismatch cannot say where the damage is, which the frame CRCs with `--verify` can. Alternative: that MD5 only with `--verify`.
4. **Meaning of verified (approved):** every checksum the stream stores was compared and holds, and at least one compared checksum covers all of its audio, coded or decoded; for the phase 3 codings and WavPack in every mode. Alternatives: count only checksums over decoded audio, or cover the lossy formats' CRCs now.
5. **Scope (approved):** Gate 3 decoders for FLAC, Monkey's Audio, WavPack, and TrueHD and MLP, the codings with a checksum over decoded audio; ALAC, TTA and WMA Lossless decoders move to "Later", which leaves their waveform and lowpass without a decoder until then (costs listed under "What the table means"). A WavPack hybrid file is verified as its lossy stream by its header CRCs; one with an MD5 stays unverified until companion-file access finds its `.wvc`. Alternative: all seven decoders, and `.wvc` access, now.
6. **TrueHD (approved):** compare the lossless check of every presentation up to substream 2, one tally each, not the Atmos substream. The check never counts as covering all audio, since the last interval of each segment is never checked; a stream is verified only where substream CRC-8s in every access unit cover it and every lossless check holds. Alternatives: compare the top presentation only, as FFmpeg does, or count the check as covering and report the samples it leaves out.
7. **PCM analyses (approved; now "measurements"):** lowpass and similar analyses run in the host on the decoded PCM and add figures to the stream, never nodes; the hook is fixed now, the figures come with the first feature. Alternative: a later change to the contract.
8. **Budget (changed by the lead on 2026-10-23, approved the same day after an independent review; `DecoderWorkTests` runs in the nightly run only, the lead's choice):** the budget counts decoding work, not PCM. Each decoder charges the work of a frame before it does it (`DecodeContext.AddWork`), in work units of one predictor or filter tap on one sample of one channel: 16 per sample of each channel (more for samples of 3 or 4 bytes, whose checksums hash more), 16 per byte of coded data it reads itself, and its stages' units (FLAC its order, WavPack 8 a decorrelation pass, Monkey's Audio 48 a sample for its range decoder). Decoding stops with an info past the larger of 2^35 units and the file's size times the units per byte of its coding, 2,048 by default and 8,192 for Monkey's Audio, about 21 s and 1.2 s per MB at the 0.6 ns a unit measured so far, which `DecodeOptions.MaxWork` overrides for tests. A decoder whose stream totals charge less than the per-sample figure is `decode.failed`, and `DecoderWorkTests` holds each decoder's decoding time per unit (the parse taken out) within twice a FLAC reference's. `StreamDecoder.MaxWastedSamples` goes. Details in [Work instead of samples](#work-instead-of-samples). Alternatives:
   - **Previous (approved 2026-10-22):** decoding stops with an info past the larger of 16 GiB of PCM in the canonical layout and 256 times the file's size, which `DecodeOptions.MaxPcmBytes` overrides for tests. WavPack's crafted worst case runs about 13 minutes under it on a 3.2 MB file.
   - One per-byte figure for all codings (Monkey's Audio may need more than FLAC and WavPack, so the draft declares it per coding instead).
   - No bound in the library.
9. **Tools (approved):** build Monkey's Audio SDK 13.42 (3-clause BSD) and libtta-c 2.3 (LGPLv3) from pinned source for the cross-checks, since no Ubuntu series packages them; no `ffmpeg` fallback for decoding (the plan's open question), FFmpeg stays a cross-check tool. Alternative: FFmpeg as the only second decoder, which cannot decode Monkey's Audio beyond 2 channels and 24 bits.

10. **Licensed sources (decided by the project lead on 2026-10-24):** decoders follow specifications and permissively licensed reference code, with notices (libwavpack now listed); TrueHD and MLP are built clean-room: an agent reads FFmpeg's LGPL `mlpdec.c`, `mlp.c`, `mlp_parse.c` and `mlpdsp.c` and writes a format description with no code in [docs/design/truehd-format.md](truehd-format.md), and an implementing agent who never reads FFmpeg's source builds the parser and decoder from that description, the plan and test vectors, using FFmpeg only as a black-box cross-check. The Monkey's Audio decoder follows the BSD-licensed SDK (13.42 on that date; 13.44 now, see "As built: Monkey's Audio"), not FFmpeg's `apedec.c`.
    - **libwavpack:** the WavPack decoder follows libwavpack 5.6.0 (BSD 3-clause) closely, and parts are ported from it. Its `COPYING` is in `THIRD-PARTY-NOTICES.md` (taken from the pinned archive, see `scripts/release/make_notices.py`), and the source files that derive from it carry a short notice: `WordDecoder.cs`, `PcmBlockDecoder.cs`, `DsdBlockDecoder.cs` and `StreamDecoder.cs` in `Decoding`, and `BlockContents.cs` and `Decode.cs`, the sub-block reading, whose `wp_exp2s` table is libwavpack's.
    - **Monkey's Audio SDK 13.42:** the Monkey's Audio parser and decoder follow the SDK (3-clause BSD, pinned by SHA-256 in `scripts/fixtures/monkeys_audio_tool.py`) closely, and parts are ported from it. Its `License.txt` is in `THIRD-PARTY-NOTICES.md` and `scripts/release/licenses/monkeys-audio.txt`, and the source files that derive from it carry a short notice: `RangeDecoder.cs`, `Predictors.cs`, `NnFilter.cs`, `FrameDecoder.cs` and `Unprepare.cs` in `Decoding`, and `ApeHeader.cs`, `Reader.cs` and `WordOrder.cs`, which transcribe the SDK's header layouts, seek table handling and word order. FFmpeg's `apedec.c` and `libavformat/ape.c` were not read. The branch `feature/monkeys-audio-sdk-1344` moved the pin and the notices to SDK 13.44 (the seventh channel and the Wave64 `GUID`); the notices name the same files.
    - **Deferred decoders (decision 5):** ALAC, TTA and WMA Lossless decoders are moved to "Later", not dropped; the PCM analyses wait for them. When they come, ALAC follows Apple's Apache 2.0 source and carries its notice; TTA follows the clean-room split of TrueHD, because libtta-c is LGPLv3 (decision 9 builds it only as a cross-check tool); WMA Lossless follows the clean-room split too, because FFmpeg's `wmalosslessdec.c` is the only detailed source (reverse-engineered, LGPL).
    - **Audit of the shipped code (2026-10-24):** only the WavPack and Monkey's Audio decoders follow a licensed source closely enough to carry a notice (the Monkey's Audio files were added on 2026-10-24 with the review of that pull request); the FLAC decoder is written from RFC 9639. `third_party/` holds test tools only, in neither build. Other places cite FFmpeg, mp3guessenc, libdca, libvorbis, libspeex or libmpcdec for behavior, limits or constants; the Musepack parser's Huffman and resolution tables are transcribed from libmpcdec (BSD) and carry its notice, and the AAC, DTS and MP4 tables are specification data, verified against faad2, fdk-aac and FFmpeg. The WMA parsers' tables are generated from FFmpeg 6.1.1 and kept as format data (`docs/development.md`, "Where the format tables come from").
    - **Correction (2026-10-29):** the audit above missed the WMA parsers themselves. Their readers of WMA versions 1 and 2, Professional and Voice followed FFmpeg's LGPL `wmadec.c`, `wma.c`, `wmaprodec.c` and `wmavoice.c` closely. They were removed on 2026-10-29: the three codecs are read at header level, as WMA Lossless is, until clean-room rewrites bring the frames back (`docs/development.md`, "Where the format tables come from").
    - The rule is in `CLAUDE.md` and the plan.

## As built: the PCM decoder

Step 3 of [spectral-analysis.md](spectral-analysis.md) (the measurements) added the decoder `pcm` to the WAV project (`Encspect.Formats.Wav.Decoding.PcmDecoder`), registered in `DefaultParsers.Decoders` after `mlp`. What it changes in this contract, which stays otherwise as built:

- **A decoder with no checksum.** `IDecoder.DeclaresChecksums` (default true) is false for `pcm`. The router opens such a decoder only where `DecodeOptions.Measure` or `DecodeOptions.Output` is set; otherwise the session is `NotRequested` and the parser reads no audio, so `--verify` alone costs a folder of WAV files nothing. Its sessions are finished with an empty list of checksums, and `StreamVerification.HasChecksums` (false for them) is what the text and JSON writers, `VerificationText` in the GUI and `CanVerifyAudio` read.
- **The setup.** `DecodeSetup.Pcm` (`PcmCoding`: encoding, byte order, container bits, and the sample rate and channel layout the container states, which the decoder needs for `DeclareOutput`) replaces `Configuration` and `Format` for this decoder. `BitsPerSample` is the output width (`PcmCoding.OutputBits`: 16 for A-law and mu-law), so the floor of the charge follows it.
- **The feed.** `PcmFeed.Run` in `Core.Abstractions` reads a data chunk in slices of whole sample frames (64 KiB, or one sample frame if that is larger) into one buffer and hands each as a `CodedFrame` (`NodeName` the data chunk's, `FirstSample` the position); both the WAV and the AIFF parser call it while `IsDecoding`. A decode is never asked for a slice the parser could not read: the frames are clamped to the bytes the file holds.
- **Work:** `DecodeWork.ForSample` of the output width for each sample of each channel, charged before the slice is unpacked; nothing per tap. `DecoderWorkTests` times it at 0.08 to 0.11 ns a unit against the reference's 0.50 to 0.63, 0.14 to 0.17 of it (three runs).
- **Errors:** a coding the decoder cannot output (integers wider than 32 bits) throws `InvalidDataException` from `Open`, which is `decode.not-possible`; a slice cannot be damaged (every bit pattern is a sample), so the decoder raises no finding of its own.

The rest is in [wav.md](../formats/wav.md#decoding) and [aiff.md](../formats/aiff.md#decoding).

## Open risks and unverified claims

- **Unverified:** whether Dolby patents cover TrueHD (decision 10). No legal conclusion is drawn here.
- **Unverified:** whether Microsoft patents cover WMA Lossless (decision 10). No legal conclusion is drawn here.

- **Unverified:** whether WMA Lossless holds a checksum that FFmpeg skips; no specification is public.
- **TrueHD presentations:** what decoding the lower presentations costs beyond their matrices and output, and whether FFmpeg's `-downmix` output gives each presentation's check, is settled by the TrueHD work; decision 6 only says what is compared.
- **Cost not measured:** Monkey's Audio's filters in managed code are an estimate until its decoder measures them. The second entropy pass is measured for FLAC (step 1); the MD5 throughput of `Md5` is measured as part of the prototype's decoder, not alone.
- **Work weights (decision 8):** the unit's time, about 0.6 ns, comes from WavPack's term 17 worst case and FLAC's step 2 table. `DecoderWorkTests` measured the FLAC and WavPack weights (see [As built: the work budget](#as-built-the-work-budget)) and moved several; still **unverified**: Monkey's Audio's 48 units a sample for the range decoder, its NN taps and its 1.74-bit cheapest symbol, and the high-mode DSD setup as a timing (its step count is checked). TrueHD's and MLP's weights are measured (see [As built in step 3: TrueHD and MLP](#as-built-in-step-3-truehd-and-mlp)).
- **Per-byte allowance:** 2,048 units per byte covers FLAC and WavPack at their highest settings for all but near-silent audio. Outside zero runs WavPack cannot exceed (40 + 176) × 4 = 864 units a byte for integers and (22 + 24 + 16 + 176) × 4 = 952 for floats at the built weights, since a word costs at least 2 bits, so hybrid files at the lowest bitrate are not at risk. Monkey's Audio declares 8,192, derived from a cheapest symbol of 1.74 bits that is **unverified**, and the classical-music average of about 41 % is **unverified**. Long digital silence in a small file stops with `decode.limit` in any coding, flagged Monkey's Audio silence included (it still charges 16 a sample, so it stops about 100 times later), since it looks like a crafted file.
- **WavPack's crafted file sizes:** settled: step 3's 3.2 MB for 1,000 timed blocks was a slip; the reviewer measured `complete-1000.wv` at 74,000 bytes, which agrees with about 73 bytes a block.
- **Timing test noise:** `DecoderWorkTests` times decoding alone (with decoding minus without), interleaved with the reference, medians of five rounds, against a factor of 2; a noisy runner may still fail it, which has not been tried. It runs in the nightly run only; if it is noisy there, the noise is root-caused rather than the factor going up. The 0.61 ns on `complete-1000.wv` is one run on a loaded machine.
- **Shared syntax:** a parser refactor now has to keep its decoder building; that is the point of the layout, but it couples their changes within one project.
