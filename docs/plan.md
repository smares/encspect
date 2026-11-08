# Encspect: Project Plan

Planning date: 2026-10-10; brought up to date on 2026-11-07. This file is the canonical copy of the plan: the goals, the decisions that steer the work, the order of the work, what is built and what comes next. Keep it up to date as decisions change.

How the analyzer is built is in [architecture.md](architecture.md); what each format reads is in [formats](formats/README.md) and [tags](tags/README.md); fixtures, cross-checks and CI are in [development.md](development.md).

## Contents

- [Goals and non-goals](#goals-and-non-goals)
- [Decisions so far](#decisions-so-far)
- [Architecture](#architecture)
  - [A library others could use](#a-library-others-could-use)
- [Format detection](#format-detection)
- [Tag detection and false positives](#tag-detection-and-false-positives)
- [Verification strategy](#verification-strategy)
- [GitHub and CI](#github-and-ci)
- [CLI and GUI](#cli-and-gui)
- [Risks and open questions](#risks-and-open-questions)
- [Format rollout](#format-rollout)
  - [Phases](#phases)
  - [Built](#built)
  - [Next](#next)
  - [Then, in order](#then-in-order)
  - [Paused and on hold](#paused-and-on-hold)
  - [Later](#later)
  - [More formats](#more-formats)
  - [Sequenced formats](#sequenced-formats)
  - [Not planned](#not-planned)

## Goals and non-goals

The tool tells you everything technically knowable about an audio file's structure, and every value it reports points back to the bytes it came from. It is a GUI and CLI analyzer for people who want EncSpot-level detail, across many more formats.

**Goals**

- Deep header, metadata and structure analysis: encoder detection, bitrate behavior, bit reservoir usage, VBR headers, and all tag formats.
- Every reported value carries its byte offset and length, so the GUI can show a structure tree next to a hex view and the CLI can emit text or JSON from the same data.
- Broad format coverage, with one self-contained parser per format (see [Format rollout](#format-rollout)).
- Damaged or odd files produce findings, never crashes.
- One codebase for Windows, macOS and Linux on x64 and ARM64.

**Non-goals**

- Audio playback.
- Runtime-loadable plugins. Formats are separate projects, composed at build time.
- Editing or writing tags.
- Bundling external decoders or native audio libraries.
  - The one exception is what the GUI framework itself needs: Skia and HarfBuzz, and the platform backends (ANGLE, `av_libglesv2`, on Windows; Avalonia.Native, `libAvaloniaNative`, on macOS); Linux needs none beyond Skia and HarfBuzz (see [gui.md](gui.md)).
- Quality verdicts. The report says what the file shows, never whether it sounds good: no rating from an encoder's reputation or the bitrate, such as EncSpot's good, ok or bad.
- For v1: scalefactor and Huffman-table level MP3 analysis. v1 stopped at headers, side information and metadata; the entropy pass comes with the lossy decoders under [Next](#next).

## Decisions so far

The stack is C# on .NET with Avalonia for the GUI. The code is checked by tests and review agents rather than line by line by a person, which shapes several choices below.

| Decision | Choice | Why |
| --- | --- | --- |
| Language | C# (.NET) | Reads like TypeScript; a malformed file raises a catchable exception instead of corrupting memory; one `dotnet publish` per OS and architecture |
| GUI | Avalonia UI (Fluent theme, the MIT DataGrid; no TreeDataGrid, which moved to a commercial license) | Native OS dialogs and tree view on Windows, macOS and Linux, x64 and ARM64 |
| CLI | Console app on the same core | Thin front end over the shared library |
| Parsers | Written ourselves, one project per format | Libraries hide the fields this tool exists to show; TagLib# is used only to cross-check |
| Playback | None | Pure analyzer; removes BASS, native binaries and licensing questions |
| Plugins | Build-time modules only | No runtime loading, so no version policy and no publishing restrictions |
| Decoding | Optional, per format, inside the format's project ([decoders.md](design/decoders.md), approved 2026-10-22) | For checksum verification and for the measurements of the decoded audio ([spectral-analysis.md](design/spectral-analysis.md)); formats without a decoder still get the full structure report |
| No FFmpeg fallback | No `ffmpeg` fallback for decoding; FFmpeg stays a cross-check tool ([decoders.md](design/decoders.md), decision 9, 2026-10-22) | Keeps the no-bundled-decoders rule; settles the plan's former open question |
| Hosting and CI | GitHub, hosted runners | Covers Windows, macOS (Apple Silicon) and Linux on x64 and ARM64 |
| Name | Encspect | Detenc and Framescope were already taken |
| License | MIT | Default in the .NET ecosystem; each dependency's license is still checked individually |
| Licensed sources | Permissive only, with notices | Decoders and parsers follow specifications and BSD, MIT or Apache reference code, with their notices in the source and in [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md). Nothing is ported from LGPL or GPL code: where that is the only detailed source, a clean-room split applies ([CLAUDE.md](../CLAUDE.md)) |
| Routes | Containers route only the codec and container combinations that writers in the wild produce (the project lead, 2026-11-07) | Routing what nobody writes costs code and fixtures for no file; see the list below the table |
| Releases | 0.1.0 as a GitHub pre-release after Gate 2 (the project lead, 2026-10-17) | Replaced the earlier rule of no public version until development ends |

**The routes decision in detail** (the project lead, 2026-11-07):

- A combination nobody writes (WavPack in ASF and the like) stays at container level: the track is named, and the `codec-not-analyzed` finding stays.
- Planned: PCM in MP4 and Matroska, and MP3 and AC-3 in ASF; the other planned routes are under [Then, in order](#then-in-order), each with the muxer that writes it where one is known.
- Not planned: Dolby TrueHD as IEC 61937 MAT bursts in WAV. The lead decided so knowing that FFmpeg's spdif muxer writes such bursts and that HDMI captures hold them; the bursts stay named and timed.
- `A_MS/ACM` in Matroska waits under [Later](#later) for a real file.
- The table of every combination is in [formats/README.md](formats/README.md#codecs-in-containers).

## Architecture

- **Where it is:** [architecture.md](architecture.md) holds the parts and their contracts, under the headings the code and the docs cite: "Streams and series" (with channel layout, names, encoder and writer, and "Stream changes"), "Parser contract", "Routes between parsers", "Format detection" and "Large inputs".
- **The shape:** every parser depends on one small abstractions library and nothing else; the front ends reach parsers only through the host. Tag parsers are separate from format parsers, and containers reach codec and tag parsers only through the host's routes.
- **Decoders** are optional, live in their format's project, and are reached only through the host, so no parser depends on one.
- **Why MP3 first:** it forced the shared model and parser interface into shape before any second format existed.

### A library others could use

- **Decision:** the core, the host and the parsers stay a front-end-agnostic library, and the CLI and GUI are only two front ends of it.
- **Why:** a third party could one day build its own GUI on them or analyze files from its own program.
- **What follows:**
  - nothing that belongs to one front end goes below it: no UI types, dialogs, settings, caches or console output in the host, Core.Abstractions or a parser;
  - front ends reach the analysis only through the host's public API (`Analyzer`, `Report`, the writers, the topic library), and add nothing to it that another front end could not use.
- **Not planned yet:** offering the library.
  - It would take NuGet packages, a deliberately public API under semantic versioning with an API-compatibility check in CI, and a contract declared stable once the format phases stop extending it.
  - The options are in [library.md](library.md).

## Format detection

- **Decision:** the host picks the format from the evidence every parser's sniff reports (an evidence class, where the structure starts, how many consistent and checksum-verified frames follow), never from the order of the parser list.
- **Why:** short signatures and frame headers occur inside other formats' data; a chain of consistent frames, the earlier start and passing checksums decide instead.
- **The rules,** the class table, alternatives and forcing a format: [architecture.md, "Format detection"](architecture.md#format-detection).

## Tag detection and false positives

- **Decision:** tags are found by position, structural validation and byte ownership, never by a signature alone.
- **Why:** short signatures collide with each other and with audio data; `APETAGEX` holds `TAG` at offset 3, so a 131-byte APE tag at the end of a file puts a false ID3v1 signature at EOF-128.
- **What follows:**
  - each tag type is looked for only where its specification allows;
  - structural validation decides confidence, not existence: a tag that breaks its specification is reported with findings, not dropped;
  - when candidates compete for the same bytes, the strongest evidence wins, and the order APE, Lyrics3, ID3v2, ID3v1 only breaks ties;
  - a container that states its size owns the trailing candidates that start before its end;
  - a frame header only counts as MPEG sync when a chain of consistent frames follows it.
- **The rules and the fixtures that prove them:** [tags/README.md, "How tags are found"](tags/README.md#how-tags-are-found).

## Verification strategy

Nobody reads the code line by line, so correctness comes from automated checks that do not depend on the author's judgment. The weak spot to guard against is spec details written from memory.

1. **Known-input fixtures:** hand-built ones (standard library only, regenerated and compared in CI) control every byte; encoder-written ones are committed, since their bytes depend on the tool version.
   - Each has a manifest of the expected result.
2. **Cross-checks against other tools:** disagreements fail the build until explained; every format is also compared with MediaInfo.
3. **Official decoders:** where FFmpeg was the only reference, the format owner's decoder or reference implementation is the reference for sample counts, durations and the handling of damage, with FFmpeg as a second cross-check ([official-decoder-crosschecks.md](design/official-decoder-crosschecks.md)).
4. **Golden reports:** each fixture has a stored expected report; any output change shows up as a diff that has to be approved.
5. **Corrupt-file tests and fuzzing:** truncated, bit-flipped and garbage-prefixed files must never crash or hang the tool.
   - The fuzzer fails on `parse.failed` and on children that could not be built, as on an escaping exception.
6. **Spec references in the code:** each parsed field cites the spec section it implements.
7. **Independent review:** after each milestone, a separate review agent gets the code and the spec without the author's conclusions; its findings are recorded in the pull request.

- The per-format fixtures, tools and their pinned versions are in [development.md, "Fixtures and cross-checks"](development.md#fixtures-and-cross-checks).
- Nothing merges with a red pipeline. Work is pushed to its own branch, never to main directly.

## GitHub and CI

- **Decision:** GitHub Actions is the single source of truth for "does it build and pass on every platform", so the work can be verified without anyone's computer being on.
- **The repository** is public, which keeps hosted-runner minutes free.
- **The matrix:** Windows, macOS and Linux, on x64 and ARM64 where a hosted runner exists. Pull requests run the jobs their change needs on four platforms; a push to main and the nightly run run everything on all six.
- **No moving labels:** every Linux job runs on `ubuntu-26.04`, and the cross-checks fail when a tool's version differs from the one their script expects.
- **Releases** are built from a version tag by `release.yml`; CI itself uploads no build.
- **Cloud sessions** install the .NET SDK through a SessionStart hook, so the fast checks run locally before pushing; CI covers the platforms a session cannot run.
- The jobs, the stages, the shards, the path filter and the release steps are in [development.md, "CI"](development.md#ci) and "Releases"; `scripts/ci_report.py` reports red or canceled runs of main and the nightly schedule.

## CLI and GUI

- **Decision:** both front ends are thin views over the same result model, so neither can show something the other cannot.
- **CLI:** one file, several files or a folder tree; text, JSON or CSV; exit codes by finding severity; sections to show or hide; descriptions and background articles (`--explain`, `--learn`). See [cli.md](cli.md).
- **GUI:** an Explorer-like main window with a folder tree, a file list of per-file summaries and a summary cache; one reusable detail window with the structure tree, hex view, bits, findings, explanations, graphs and the Audio panels of the measurements; export as text or JSON. See [gui.md](gui.md).
- **Graphs:** the GUI draws its own graphs, from the streams' series alone, without a charting library.
- **Native where it matters:** the operating system's file dialogs, the macOS menu bar, platform shortcuts, light and dark mode and the accent color following the system.

## Risks and open questions

The largest risks were a wrong core contract and spec details that no reviewer catches by eye; building MP3 first and the verification stages answer both.

- **Core model reworked late:** settled at Gate 1; the result model still grows with the format phases, so the contract is not declared stable yet.
- **Spec details wrong:** known-input fixtures and tool cross-checks are the safety net.
- **Decoders:** quality, license and upkeep vary, and some formats may have no decoder; the measurements that need decoded audio stay optional per format.
- **Closed formats:** TAK and OptimFROG are poorly documented, so they get only what their headers reveal.
- **Shared misreadings:** where a model written for this project and the one reference tool agree (faad2's trace for AAC, mctoolamed for MPEG-2 multichannel), a misreading both share would pass; second opinions and real files narrow it. Details are in each format's page and in [development.md](development.md).
- **Heuristic encoder detection:** files without tags can be misidentified, so results carry a confidence. Encoder habits are trusted only where encodes with that encoder confirm them, which leaves out encoders that no longer run.
- **Tools and references disappearing:** checks rely on old tools and on files the repository may not hold. Vendored sources, pinned downloads with fallback addresses, a last copy in the private repository and a weekly download check guard them ([development.md, "Conformance streams and downloads"](development.md#conformance-streams-and-downloads)).
- **Large inputs:** multi-gigabyte files and million-frame reports need streaming reads, compact frame records, nodes built on demand and a virtualized tree. Memory tests hold every parser to a bound per frame ([architecture.md, "Large inputs"](architecture.md#large-inputs)).

**Open questions**

- None at present.

## Format rollout

MP3 came first, then the other lossy formats, then the lossless ones; each later phase starts only after the previous one passes its gate. A gate means the pipeline is green on every platform and the independent reviews' findings are resolved.

### Phases

| Phase | Scope | State |
| --- | --- | --- |
| 1 (v1) | MP1/2/3 and tags | built |
| Gate 1 | Pipeline green, review findings fixed, abstractions settled | passed, October 2026 |
| 2 (lossy) | MP4 and AAC, Ogg with Speex, Vorbis and Opus, Musepack, AC-3 and E-AC-3, ASF and WMA, WavPack, DTS; then "Phase 2, continued" | built |
| Gate 2 | Pipeline green, review findings fixed | passed, 2026-10-18 at fa4e0c5 |
| 3 (lossless) | FLAC, WAV and AIFF, ALAC, WMA Lossless frames, Monkey's Audio, TTA, Dolby TrueHD and MLP; decoders for the codings that store a checksum over decoded audio | built |
| Gate 3 | Pipeline green, review findings fixed | passed, 2026-11-01 at 5f054cc |
| 3, continued | Official-decoder cross-checks (built); lossyWAV and lossyFLAC detection (paused) | see "Phase 3, continued" |
| Next | Lossy decoders | in design |
| 4 | TAK and OptimFROG at header level only ([planned.md](formats/planned.md#phase-4-header-level-only)) | planned |
| 5 | More formats, the better documented first | planned |
| 6 | Sequenced formats: MIDI, tracker modules and chiptunes | planned |
| Later | The items under "Later", order not fixed | planned |

**Gates in detail**

- **Gate 1** (October 2026): six independent reviews found 56 problems and the fuzzer 2 more, all fixed (#26 to #31, and three host defects with the contract).
  - Areas: ID3v2; the other tags and tag detection; MPEG framing and Layers I and II; Layer III and ancillary data; VBR headers, encoder detection and statistics; the contract, host and CLI.
  - The contract review's 12 gaps closed in four steps (#32 to #35): exception boundary, cancellation and parser contexts; typed data with node kinds, streams, series and linked findings; routes from containers to codec and tag parsers; compact MPEG frame records.
- **Gate 2** (2026-10-18, fa4e0c5): ten independent reviews, each followed by a review of its fixes and a verification.
  - Areas: ASF and WMA; WavPack; AAC; Ogg, Speex, Vorbis and Opus; AC-3 and E-AC-3; Musepack; MP4; DTS; Matroska and WebM; the host, contract and front ends.
  - Every finding was fixed in #98 to #110, one pull request per area, plus the shared record containers, QuickTime user data as tags, one stream-change rule for the raw stream formats with the APE item budget, and the host's contract and naming.
  - Release 0.1.0 followed it; Gate 3 work started after that.
- **Gate 3** (2026-11-01, 5f054cc): ten independent reviews, each followed by a review of its fixes.
  - Areas: the decoder host; FLAC; WavPack; Monkey's Audio; TrueHD and MLP; TTA and ALAC; WAV and AIFF; WMA Lossless; the measurements; the front ends.
  - Every finding was fixed in #189 to #198, one pull request per area, plus a fuzz finding in WMA superframes (#201).
- Each gate's push run and nightly run were green on every platform.

### Built

One line per format or feature; the details are on its page.

#### v1 vertical slice: MP3

- **MPEG audio** (MP1, MP2, MP3): frames, side information, the bit reservoir, Layer II allocation and the MPEG-2 multichannel extension, Xing, Info, LAME and VBRI headers, mp3PRO and MP3 Surround, encoder detection and the coding statistics: [mpeg-audio.md](formats/mpeg-audio.md).
- **Tags:** ID3v1, ID3v2, APE and Lyrics3, and the tag detection rules: [tags](tags/README.md).
- **File layout and tags:** the region and sync rules are in [mpeg-audio.md, "Frames and sync"](formats/mpeg-audio.md#frames-and-sync).

#### Phase 2: MP4 and AAC

- **MP4** (MP4, M4A, M4B, 3GP, QuickTime): every box, the sample tables and fragments, iTunes and QuickTime metadata, channel layouts, gapless data: [mp4.md](formats/mp4.md).
- **AAC** in MP4, ADTS and ADIF, with every raw data block of AAC Main, LC, SSR and LTP, implicit SBR and PS, ELD and USAC configurations: [aac.md](formats/aac.md).
- **Why the raw data blocks were read in phase 2:** HE-AAC and HE-AAC v2 may be signaled only inside the frames, in a fill element after the coded spectra, so the parser reads each block far enough to reach it, without dequantizing; the same pass gives AAC's coding statistics. For the other codecs that depth comes with their decoders' entropy pass.

#### Phase 2: Ogg, Speex, Vorbis and Opus

- **Ogg:** pages, multiplexed and chained streams, routes by the first packet: [ogg.md](formats/ogg.md).
- **Speex, Vorbis and Opus** as codec parsers on the same container, without changes to it: [speex.md](formats/speex.md), [vorbis.md](formats/vorbis.md), [opus.md](formats/opus.md).
- **Vorbis comments** as a tag parser shared by Speex, Vorbis, Opus and FLAC: [vorbis-comments.md](tags/vorbis-comments.md).

#### Phase 2: Musepack

- **Musepack** SV4 to SV8: [musepack.md](formats/musepack.md).

#### Phase 2: AC-3 and E-AC-3

- **AC-3 and E-AC-3,** raw and in MP4, with every audio block up to its mantissas and Dolby Atmos object audio: [ac3.md](formats/ac3.md).
- **The stream-change rule** first written for this slice is shared by every raw stream format: [architecture.md, "Stream changes"](architecture.md#stream-changes).

#### Phase 2: ASF and WMA

- **ASF:** every object, packets, payloads, metadata and protection: [asf-wma.md](formats/asf-wma.md).
- **WMA versions 1 and 2, Professional and Voice,** frame by frame, as clean-room rewrites from [wma-standard-format.md](design/wma-standard-format.md), [wma-pro-format.md](design/wma-pro-format.md) and [wma-voice-format.md](design/wma-voice-format.md).
- **Why the rewrites:** the first readers followed FFmpeg's LGPL code too closely and were removed on 2026-10-29. One agent described each bitstream without code, and another, who had read neither FFmpeg nor the removed reader, wrote the reader from that description; the golden reports equal the earlier ones except for the intended differences.
- **Microsoft's decoder** is the reference for WMA sample counts, run on the Windows runners; FFmpeg is the second cross-check.

#### Phase 2: WavPack

- **WavPack:** lossless, hybrid and DSD, `.wv` and `.wvc`, with the block checksum recomputed: [wavpack.md](formats/wavpack.md). Its decoder came in phase 3.

#### Phase 2: DTS

- **DTS:** the core in its four forms, the core extensions and the extension substreams, raw and in MP4: [dts.md](formats/dts.md).

#### Phase 2, continued

Done in this order before Gate 2:

1. **FAAC 1.31 to 2.2 detection:** [aac.md](formats/aac.md#encoder-and-writer-detection), "Encoder and writer detection".
2. **Apple's AAC encoder fixtures** from macOS runners, and the rules that name Apple's encoder and writer: [aac.md](formats/aac.md#what-apples-encoder-writes), "What Apple's encoder writes".
3. **Dolby Atmos detection in E-AC-3:** [ac3.md](formats/ac3.md#object-audio), "Object audio".
4. **DTS-HD:** the lossless extension's channel sets, `.dtshd` files and the DTS:X signal: [dts.md](formats/dts.md), [dtshd.md](formats/dtshd.md).
5. **WMA Voice frames:** first built, removed on 2026-10-29 for its provenance, and back as a clean-room rewrite: [asf-wma.md](formats/asf-wma.md).
6. **Matroska and WebM:** audio tracks routed to the codec parsers, video at container level: [matroska.md](formats/matroska.md).
7. **DRM and protection reporting** in MP4, ASF and Matroska, without decrypting: [mp4.md](formats/mp4.md), [asf-wma.md](formats/asf-wma.md), [matroska.md](formats/matroska.md).
   - Decrypting stays out of scope: no key is used, no license acquired.

#### Phase 3: lossless formats and decoders

- **FLAC,** native and in Ogg, the reference implementation of the decoder contract: [flac.md](formats/flac.md).
- **The decoder contract,** the work budget and `--verify`: [decoders.md](design/decoders.md).
- **Monkey's Audio,** all versions parsed, 3.80 to 3.99 and 4.11 decoded, following the BSD-licensed SDK: [monkeys-audio.md](formats/monkeys-audio.md).
- **WAV, Wave64, BW64 and AIFF,** with IEC 61937 bursts routed to the codec parsers and the `pcm` decoder for the measurements: [wav.md](formats/wav.md), [aiff.md](formats/aiff.md).
- **TTA,** verified by its CRC-32s without a decoder: [tta.md](formats/tta.md).
- **ALAC** frame by frame in MP4 and Matroska, following Apple's Apache-licensed source: [alac.md](formats/alac.md).
- **WMA Lossless** frame by frame, clean-room from [wma-lossless-format.md](design/wma-lossless-format.md): [asf-wma.md](formats/asf-wma.md).
- **Decoders** for FLAC, WavPack, Monkey's Audio, and Dolby TrueHD and MLP, the codings that store a checksum over decoded audio; ALAC, TTA and WMA Lossless store none; their decoders are scheduled under [Next](#next) for the measurements (the project lead, 2026-11-08).

#### Phase 3: Dolby TrueHD and MLP

- **TrueHD and MLP,** raw `.thd` and `.mlp` files and the codec parser, with the decoder that checks the lossless checks: [truehd.md](formats/truehd.md).
- **Clean room:** FFmpeg's LGPL source is the only detailed one, so one agent wrote the code-free description [truehd-format.md](design/truehd-format.md), and an agent that had not read FFmpeg implemented from it, with FFmpeg as a black-box cross-check.
- **Scope set by what can be checked:** a field is added only where FFmpeg's source or one of Dolby's public documents states it; Dolby's documents had not been read when the description was written ([truehd-format.md](design/truehd-format.md#sources), "Sources").
- **Containers:** Matroska routes `A_TRUEHD` and `A_MLP` (#229); MP4's `mlpa` is planned (FFmpeg writes it).

#### Measurements and the GUI's graphs

- **Measurements of the decoded audio** (spectrum, spectral cutoff, spectrogram, waveform, levels, loudness and true peak), steps 1 to 5: [spectral-analysis.md](design/spectral-analysis.md).
- **The GUI:** both slices (the main and detail windows, then the graphs linked to the tree and the summary cache), the Audio panels and the Frame Map ([frame-map.md](design/frame-map.md)): [gui.md](gui.md).
- **Coding graphs** for MPEG audio, AAC, and AC-3 and E-AC-3, with the contract for every format: [coding-graphs.md](design/coding-graphs.md). The other formats are on hold (see [Paused and on hold](#paused-and-on-hold)).
- **Samples and lost samples** across all formats: [samples-definition.md](design/samples-definition.md).

#### Phase 3, continued

After Gate 3, in this order:

1. **Cross-checks against the formats' official decoders,** rollout steps 1 to 6 of [official-decoder-crosschecks.md](design/official-decoder-crosschecks.md): Opus and Vorbis against Xiph's decoders, TTA against libtta-c, AAC against FDK AAC, Core Audio and Media Foundation, AC-3 and E-AC-3 against Core Audio.
   - Built.
   - Windows has no decoder for AC-3 and E-AC-3 on any runner image, so step 7 does not apply.
   - DTS, DTS-HD, TrueHD and MLP have no freely available official decoder; FFmpeg stays their reference.
   - Each difference from FFmpeg that would change counts, findings or goldens goes to the project lead before the analyzer follows it.
2. **lossyWAV and lossyFLAC detection:** paused (see [Paused and on hold](#paused-and-on-hold)).

#### Gap audits

- **Wave 1** (#226): one name per concept across formats, and new series and tallies where a format had the concept but not the figure. The names are mapped in [development.md, "Names of statistics and series"](development.md#names-of-statistics-and-series).
- **Wave 2** (#228): the bitrate mode each stream states, container lengths, Matroska trims, stereo coding tallies, AAC buffer fullness.
- **Matroska routes** for FLAC, WavPack, TrueHD and MLP (#229), so those tracks are analyzed and decoded as in their own files.

### Next

1. **Lossy decoders, with the entropy pass,** one pull request per decoder; the measurements of lossy streams and gap wave 3 wait for them.
   - Design: [lossy-decoders.md](design/lossy-decoders.md), signed off by the project lead on 2026-11-08.
   - Order:
     1. groundwork: determinism and the PCM comparison tools;
     2. the ALAC and TTA decoders, small and exact, checked bit for bit (the project lead, 2026-11-08);
     3. MPEG audio, Vorbis, Opus, AC-3, E-AC-3, Musepack, DTS core and Speex, whose specifications or permissively licensed code are free;
     4. AAC and WMA, WMA Lossless included, on clean-room sources.
   - Nothing is bought (the project lead, 2026-11-07).
   - Each decoder brings its format's entropy pass: the parser reads the entropy-coded data in every analysis (Layer III scalefactors and Huffman data, Vorbis floors and residues, Opus SILK and CELT, and the like), so the figures that need it come with the decoder.
   - **A frame a decoder cannot decode is a gap, never concealed audio** (the project lead, 2026-11-01, #208). The decoder reports the frame (`decode.frame-invalid`) and outputs nothing for it; the measurements count its positions as lost samples.
   - The positions of a lost first or last unit count too: the host gives the measurer the span of the units the parser handed over. The stream's own lost samples, from the parser without decoding, are another figure at the coded stage ([samples-definition.md](design/samples-definition.md)).
   - Where an official decoder conceals the frame (FDK AAC fills it with noise substitution), its output covers positions Encspect's decoder leaves empty; the official-decoder cross-checks record such a difference as `explained`.

### Then, in order

1. **Routes that known writers produce,** each its own pull request after the codec parsers it routes to:
   - MP4: FLAC (`fLaC`, FFmpeg), Opus (FFmpeg), Dolby TrueHD (`mlpa`, FFmpeg), PCM (FFmpeg);
   - MPEG audio as packets: MP4 tracks whose samples lie in several stretches and Matroska tracks (FFmpeg, mkvmerge), and the MPEG audio bursts of IEC 61937 in WAV (FFmpeg's spdif muxer); the MPEG parser reads one contiguous payload only today;
   - Matroska: PCM (FFmpeg, mkvmerge);
   - ASF: MP3 and AC-3 (the project lead, 2026-11-07);
   - Matroska header stripping for AAC, AC-3, DTS and MPEG audio (mkvmerge writes it): a two-range `MappedBytes` whose prefix maps to the ContentCompSettings bytes.
2. **Phase 4:** TAK and OptimFROG at header level only.
3. **Phase 5:** [More formats](#more-formats).
4. **Phase 6:** [Sequenced formats](#sequenced-formats).

### Paused and on hold

- **lossyWAV and lossyFLAC detection:** paused by the project lead. The plan as it stood:
  - lossyWAV sets the lowest bits of every channel to zero in fixed blocks, as many in each block as its noise allows, so that a lossless coder stores them almost for free; lossyFLAC is its output in FLAC at the same block size;
  - the pattern: zeroed low bits in most blocks, in all channels at once, a count that changes from block to block, and block edges on one fixed grid. Audio that is lossless throughout has none or a constant count (a 16-bit recording in a 24-bit file);
  - FLAC from the wasted bits of each subframe, which the parser reads already; WavPack, Monkey's Audio, TrueHD and WAV from a measurement of the decoded audio;
  - reported as figures with their basis (the share of blocks with zeroed low bits, the distribution of their counts, the block grid found) and as an encoder claim for lossyWAV with a confidence;
  - backed by files lossyWAV writes (GPL, used only as a black-box tool for fixtures). No finding says a file is not lossless;
  - limits: silence and audio truncated on purpose also have zeroed bits; processing after lossyWAV (gain, dither, resampling) removes the pattern; another tool that zeroes bits per block the same way looks the same. Whether lossyWAV leaves a marker of its own is checked with its files first.
- **Coding graphs for the remaining formats:** on hold. The survey and the order are in [coding-graphs.md](design/coding-graphs.md).
- **Parallel decoding:** approved, deferred by the project lead on 2026-10-26 in favor of new formats and deeper analysis ([parallel-decoding.md](design/parallel-decoding.md)).
- **Detection pre-check:** recorded, not to be built (the project lead, 2026-10-26; [detection-precheck.md](design/detection-precheck.md)).

### Later

The order is not fixed.

- **The measurements' later steps** (step 6 of [spectral-analysis.md](design/spectral-analysis.md)).
- **`A_MS/ACM` in Matroska,** routed by the WAVEFORMATEX format tag, once a real file from a muxer that writes it is found and committed as a fixture (the project lead, 2026-11-07).
- **Small separate items:**
  - WavPack 1 to 3;
  - finding the `.wvc` that belongs to a `.wv` (host access to a companion file);
  - ASF script command and marker contents.

### More formats

Phase 5, in this order; the better a format is documented, the sooner it comes. Specifications, extensions, scope and the reason for each place are in [planned.md](formats/planned.md).

1. CAF
2. DSF and DSDIFF
3. Sun AU, VOC, IRCAM and NIST SPHERE
4. AC-4
5. AAC in LATM and LOAS
6. AMR and AMR-WB
7. MPEG transport and program streams
8. MPEG-H 3D Audio
9. MPEG-4 ALS
10. Shorten
11. The formats whose only detailed public source is FFmpeg, clean-room: OSQ; ATRAC, ATRAC3 and ATRAC3plus; RealAudio; TwinVQ; QDesign Music; Nellymoser

### Sequenced formats

Phase 6, in this order, analyzed at the structure level and never rendered ([planned.md](formats/planned.md#phase-6-sequenced-formats)):

1. MIDI
2. Tracker modules
3. Chiptunes

### Not planned

- Game and console middleware (ADX, HCA, BRSTM, VAG, FSB, Bink), which is reverse-engineered with no public specification.
- The ancient lossless compressors and obsolete vocoders.
- Each of the many ADPCM and PCM variants as a format of its own: WAV and the containers name their codings.
- MXF, and AVI, which is mostly video.
- Codec and container combinations no known writer produces (see the routes decision above).
