# Encspect

Audio file analyzer with a GUI and a CLI. Read `docs/PLAN.md` for goals, architecture, scope and rollout before making design decisions; this file only holds the rules that apply to every change.

## Stack

C# on .NET, Avalonia UI (GUI), console app (CLI), one shared core library, one project per format. No playback, no runtime-loadable plugins, no bundled decoders or audio libraries; the native libraries the UI framework itself needs are the one exception: Skia and HarfBuzz, and the platform backends ANGLE on Windows and Avalonia.Native on macOS.

## Layout

- `src/Encspect.Core.Abstractions`: result model, byte and bit readers, mapped bytes, parser interfaces and their contexts.
- `src/Encspect.Host`: analyzer (format detection, parser orchestration, the routes between parsers), report writers and the input walker both front ends use.
- `src/Encspect.Cli`: console app, assembly name `encspect`.
- `src/Encspect.Gui`: Avalonia GUI, assembly name `encspect-gui`; it references the host only.
- `tests/Encspect.Tests`: xUnit tests for all of the above but the GUI; `tests/Encspect.Gui.Tests`: headless GUI tests on xUnit v3, which Avalonia.Headless.XUnit needs.
- Format parsers and tag parsers are added as further projects.

## Architecture rules

- Parsers depend only on `Core.Abstractions` (parser interfaces, result model, byte and bit readers, mapped bytes). They never reference each other and never reference a decoder. They reach other parsers only through the host's routes on their context: a container hands a payload to a codec parser by codec ID (`ParseStream`, or `ParsePackets` for packets as `MappedBytes`) and a tag it holds to a tag parser by name (`ParseTag`).
- Tag parsers (ID3, APE, Lyrics3) are separate from format parsers. The host orchestrates detection and assembles one report.
- The result model is a node tree; every node carries a name, a kind (`NodeKind`), a typed value (`NodeValue`: numbers, booleans and hex codes typed, with their unit; only names, descriptions and mixed text as text), byte offset and length, and a field that does not cover whole bytes also its bit position (`Node.FromBits`); a node whose bytes are not contiguous in the file (a packet that spans Ogg pages) also its `Ranges`, with offset and length covering them. Format parsers report their streams as `StreamInfo` with series for graphs. Findings carry a severity and point to a node of the tree, equal in name, offset and length; the host resolves its path. Front ends read kinds and streams, never node names. Trees nest at most `Node.MaxDepth` levels; deeper nesting is a finding.
- Every node a parser creates carries a `Description`, written by the parser that knows the field: one or two plain sentences on what the field is and, where it helps, what this value means, citing the spec section where there is one, without verdicts. It is a constant string shared across nodes, picked per value where needed, never built per frame. A node may also name a background article by ID (`Topic`, such as `mid-side-stereo`), which the host's `TopicLibrary` serves from `src/Encspect.Host/Topics`; the parser declares its IDs in a `Topics` class and never references the host. Series carry a `Description` the same way. `DescriptionTests` walks every fixture's tree and fails on a node without one.
- Every concept a format brings (a coding tool, a header, a container mechanism) gets a background article in `src/Encspect.Host/Topics`, shared across formats where the concept is: what problem it solves, how it works, what you see in a file, where it is specified, in 100 to 300 words. Write descriptions and articles only from sources that can be checked (the specification, reference or vendored source code, the tool cross-checks); leave out what cannot be verified rather than guess.
- Malformed or damaged input produces findings, never an unhandled exception and never a hang. Files are read as streams, not loaded whole. Parsers throw only `EndOfStreamException` (truncation), `InvalidDataException` and, when the analysis is canceled, `OperationCanceledException`; the host reports anything else as `parse.failed`, which is a bug, never an acceptable result.
- One parser instance serves every analysis and thread: keep no per-call state in fields. Loops that only compute over every frame or packet check `ParseContext.CancellationToken` every few thousand iterations; reads check it already.
- Tag detection uses position, structural validation and byte-range ownership, never a signature match alone. When candidates compete for the same bytes, the strongest evidence wins; the order APE, Lyrics3, ID3v2, ID3v1 only breaks ties. A tag that breaks its specification is reported with findings, not dropped. A container that states its own size (`IFormatParser.DeclaredEnd`) owns the trailing candidates that start before its end. See "Tag detection and false positives" in the plan.
- A frame header only counts as MPEG sync when a chain of consistent frames follows it.

## Code style

- Comments only for non-obvious decisions and constraints, or to cite the spec section a field implements. Do not comment what the code already says.
- Booleans that appear in the GUI or in JSON output read as booleans (`IsVbr`, `HasLameTag`), not as nouns.
- Stereo and channel terms are written out in output, messages and docs: mid/side, intensity stereo, left/right, never MS, IS or LR. Spec acronyms such as CRC, VBR, CBR, ABR, TOC, ATH, LFE and SCFSI stay abbreviated.
- American English everywhere, including comments, docs and prose (-ize, not -ise). Write "ID" capitalized in comments when it means the word.

## Verification

- Run the checks the CI workflow runs, locally, before calling a change done. Mirror the pipeline instead of picking a subset. Cloud sessions install the .NET SDK through the SessionStart hook in `.claude/settings.json` (`scripts/install-dotnet.sh`); if the SDK is still missing, a blocked host is the likely cause, so name it. If the SDK is not available, say so and rely on the CI run.
- Parsers are verified against known-input fixtures, cross-checks with other tools, golden reports and corrupt-file tests, as described in the plan. A parser change without a fixture or golden-report update is incomplete.
- After each milestone, an independent review agent that has not seen the author's conclusions reviews the code against the spec.

## Git

- Never push unless explicitly told to for that specific push. Commit locally and stop.
- Nothing merges with a red pipeline.
