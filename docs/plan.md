# Encspect: Project Plan

Planning date: 2026-10-10; rewritten around milestones on 2026-11-08. This file holds the goals, the decisions that steer the order of the work, the milestones and what is not planned. Keep it up to date as decisions change.

- **Where we are:** what each format reads today, by depth, is in [formats/README.md, "Depth by format"](formats/README.md#depth-by-format); what was built before 2026-11-08, with the gates and the pull requests, is in [history.md](history.md).
- **How it is built:** the parts, the contracts, format and tag detection and the verification are in [architecture.md](architecture.md); the library decision is in [library.md](library.md#a-library-others-could-use).
- **How it is checked:** fixtures, cross-checks, CI and releases are in [development.md](development.md); the front ends are in [cli.md](cli.md) and [gui.md](gui.md).

## Contents

- [Goals and non-goals](#goals-and-non-goals)
  - [Breadth and depth](#breadth-and-depth)
- [Decisions](#decisions)
  - [Working rules](#working-rules)
- [Milestones](#milestones)
  - [0.2: Lossless complete](#02-lossless-complete)
  - [0.3: Lossy formats measured](#03-lossy-formats-measured)
  - [0.4: Free decoders and routes complete](#04-free-decoders-and-routes-complete)
  - [0.5: AAC and WMA](#05-aac-and-wma)
  - [After 0.5](#after-05)
- [Not planned](#not-planned)

## Goals and non-goals

The tool tells you everything technically knowable about an audio file's structure, and every value it reports points back to the bytes it came from. It is a GUI and CLI analyzer for people who want EncSpot-level detail, across many more formats.

**Goals**

- Deep header, metadata and structure analysis: encoder detection, bitrate behavior, bit reservoir usage, VBR headers, and all tag formats.
- Every reported value carries its byte offset and length, so the GUI can show a structure tree next to a hex view and the CLI can emit text or JSON from the same data.
- Broad format coverage, with one self-contained parser per format.
- Damaged or odd files produce findings, never crashes.
- One codebase for Windows, macOS and Linux on x64 and ARM64.

**Non-goals**

- Audio playback.
- Runtime-loadable plugins. Formats are separate projects, composed at build time.
- Editing or writing tags.
- Decrypting protected content: protection is reported, no key is used and no license acquired.
- Bundling external decoders or native audio libraries.
  - The one exception is what the GUI framework itself needs: Skia and HarfBuzz, and the platform backends (ANGLE, `av_libglesv2`, on Windows; Avalonia.Native, `libAvaloniaNative`, on macOS).
  - Linux needs none beyond Skia and HarfBuzz (see [gui.md](gui.md)).
- Quality verdicts. The report says what the file shows, never whether it sounds good: no rating from an encoder's reputation or the bitrate, such as EncSpot's good, ok or bad.

### Breadth and depth

The work grows along two axes (the project lead, 2026-11-08).

- **Breadth:** the formats and containers Encspect reads.
- **Depth:** how far into a format it sees, in five levels:
  1. container structure: boxes, chunks, pages, packets and frames;
  2. codec syntax: headers, side information and the frame-level fields;
  3. entropy-coded data: the Huffman, Golomb, range or run-level coded values (the entropy pass);
  4. decoded audio: verification of the checksums over it, and the measurements;
  5. interpretation: statistics, coding graphs and encoder detection.
- v1 stopped at level 2 for MPEG audio. AAC's and WMA's parsers walk their entropy-coded data already; for the other lossy formats the entropy pass comes with the decoder.

## Decisions

The stack is C# on .NET with Avalonia for the GUI. The code is checked by tests and review agents rather than line by line by a person, which shapes several choices below. A row without a date of its own dates from the planning of 2026-10-10 or was recorded without one.

| Decision | Choice | Why | Date |
| --- | --- | --- | --- |
| Language | C# (.NET) | Reads like TypeScript; a malformed file raises a catchable exception instead of corrupting memory; one `dotnet publish` per OS and architecture | |
| GUI | Avalonia UI (Fluent theme, the MIT DataGrid; no TreeDataGrid, which moved to a commercial license) | Native OS dialogs and tree view on Windows, macOS and Linux, x64 and ARM64 | |
| CLI | Console app on the same core | Thin front end over the shared library | |
| Parsers | Written ourselves, one project per format | Libraries hide the fields this tool exists to show; TagLib# is used only to cross-check | |
| Playback | None | Pure analyzer; removes BASS, native binaries and licensing questions | |
| Plugins | Build-time modules only | No runtime loading, so no version policy and no publishing restrictions | |
| Hosting and CI | GitHub, hosted runners | Covers Windows, macOS (Apple Silicon) and Linux on x64 and ARM64 | |
| Name | Encspect | Detenc and Framescope were already taken | |
| License | MIT | Default in the .NET ecosystem; each dependency's license is still checked individually | |
| Licensed sources | Permissive only, with notices | Decoders and parsers follow specifications and BSD, MIT or Apache reference code, with their notices in the source and in [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md). Nothing is ported from LGPL or GPL code: where that is the only detailed source, a clean-room split applies ([CLAUDE.md](../CLAUDE.md)) | |
| Releases | 0.1.0 as a GitHub pre-release after Gate 2 | Replaced the earlier rule of no public version until development ends | 2026-10-17 |
| Decoding | Optional, per format, inside the format's project ([decoders.md](design/decoders.md)) | For checksum verification and for the measurements of the decoded audio ([spectral-analysis.md](design/spectral-analysis.md)). Decoders vary in sources and upkeep and some formats may never have one, so the measurements stay optional per format; formats without a decoder still get the full structure report | 2026-10-22 |
| No FFmpeg fallback | No `ffmpeg` fallback for decoding; FFmpeg stays a cross-check tool (decision 9 of [decoders.md](design/decoders.md)) | Keeps the no-bundled-decoders rule | 2026-10-22 |
| Gaps, not concealment | A frame a decoder cannot decode is a gap, never concealed audio (#208) | The lead's rule, recorded with the official-decoder cross-checks ([official-decoder-crosschecks.md](design/official-decoder-crosschecks.md)) | 2026-11-01 |
| Withheld output after a gap | Output that depends on state a lost frame took with it is reset and withheld, not measured; withheld and trimmed samples are counted apart from lost ones | [lossy-decoders.md](design/lossy-decoders.md), decisions 4 and 8 | 2026-11-08 |
| Nothing is bought | No paid specification, such as ISO/IEC 14496-3 | The formats with a free specification or permissively licensed code come first; AAC and WMA follow on clean-room or free sources ([lossy-decoders.md](design/lossy-decoders.md)) | 2026-11-07 |
| Routes | Containers route only the codec and container combinations that writers in the wild produce | Routing what nobody writes costs code and fixtures for no file; see below | 2026-11-07 |
| Lossy decoders | The design of [lossy-decoders.md](design/lossy-decoders.md), every recommendation as listed | Each decoder brings its format's entropy pass, so the figures that need it come with the decoder | 2026-11-08 |
| Patents | No format waits for advice; before the HE-AAC, USAC, E-AC-3 and WMA Professional decoders, the lead decides whether they ship as source only or stay out of the release binaries (decision 17 of [lossy-decoders.md](design/lossy-decoders.md)) | The decoders exist for analysis only, and users are responsible for the laws where they live | 2026-11-08 |
| Milestones | Releases built around what a user can newly do, each ending with a gate and a tag | The phases had lost their meaning, and parked work had no decision | 2026-11-08 |

**The gap rule in detail** (the project lead, 2026-11-01, #208):

- The decoder reports the frame (`decode.frame-invalid`) and outputs nothing for it; the measurements count its positions as lost samples.
- After the gap, a lossy decoder resets and withholds the output that depends on the lost state, reported as withheld samples apart from lost samples (decision 4 of [lossy-decoders.md](design/lossy-decoders.md#decisions), 2026-11-08).
- The positions of a lost first or last unit count too: the host gives the measurer the span of the units the parser handed over.
- The stream's own lost samples, from the parser without decoding, are another figure at the coded stage ([samples-definition.md](design/samples-definition.md)).
- Where an official decoder conceals the frame (FDK AAC fills it with noise substitution), its output covers positions Encspect's decoder leaves empty; the official-decoder cross-checks record such a difference as `explained`.

**The routes decision in detail** (the project lead, 2026-11-07):

- A combination nobody writes (WavPack in ASF and the like) stays at container level: the track is named, and the `codec-not-analyzed` finding stays.
- The planned routes are in [0.4](#04-free-decoders-and-routes-complete), each with the muxer that writes it where one is known; `A_MS/ACM` in Matroska waits there for a real file.
- Dolby TrueHD as IEC 61937 MAT bursts in WAV is not planned. The lead decided so knowing that FFmpeg's spdif muxer writes such bursts and that HDMI captures hold them; the bursts stay named and timed.
- The table of every combination is in [formats/README.md](formats/README.md#codecs-in-containers).

### Working rules

The project lead, 2026-11-08:

- One main item is in progress at a time, plus small fixes.
- A new idea goes into a later milestone, not into the current one, unless the lead says otherwise.
- Every parked item has a milestone, or sits under [Not planned](#not-planned) with the trigger that would bring it back.
- A pause the lead asks for keeps the item's place and priority, and the item resumes first.

## Milestones

Each milestone is something a user can do that they could not do before. Its items come in the order listed.

- **The gate:** a milestone ends with the independent reviews of its work ([architecture.md, "Verification"](architecture.md#verification)), their findings fixed, and the push and nightly runs green on every platform.
- **The release:** a passed gate is tagged `v0.N.0` and released ([development.md, "Releases"](development.md#releases)).
- **0.1.0** was released after Gate 2; Gate 3 and the work after it lead into 0.2 ([history.md](history.md)).

### 0.2: Lossless complete

Every lossless format with a decoder design is decoded and measured, except WMA Lossless (0.5) and DTS-HD's lossless extension (a follow-up after 0.5). Files that lossyWAV processed are recognized, and TAK and OptimFROG files are named.

1. **The lossy groundwork** (#232, merged 2026-11-08): determinism (`DetMath`, the scan and its fixes), withheld output in the contract and the measurer, and the PCM comparison tools; step 0 of the [lossy decoders' rollout](design/lossy-decoders.md#rollout).
2. **lossyWAV and lossyFLAC detection** (done: PRs #239, #240, #242 and #243), with priority. It was paused only to save tokens and was never meant to be postponed (the lead, 2026-11-08).
   - **Design:** [lossywav-detection.md](design/lossywav-detection.md), signed off by the lead on 2026-11-08 with 13 decisions, from files lossyWAV 1.4.2 wrote in CI (used only as a black-box tool).
   - **What the files show:** lossyWAV marks its WAV output with a `fact` chunk naming itself and its settings, which lossyFLAC pipelines usually drop. It zeroes low bits in blocks of 512 samples at 44.1 and 48 kHz, counted per channel unless `--linkchannels` is set.
   - **Pull requests, in order:** PR 0, the WAV parser's fixes (the misread `fact` chunk and a false `wav.truncated`); PR 1, the marker and a "Processed by" claim; PR 2, the measurement of zeroed low bits; PR 3, the wasted-bits statistic in FLAC and WavPack and the claim from behavior.
   - **Reported** as figures with their basis and as a claim with a confidence. No finding says a file is not lossless.
3. **The ALAC and TTA decoders,** small and exact, checked bit for bit (the lead, 2026-11-08); steps L1 and L2 of [lossy-decoders.md, "Order"](design/lossy-decoders.md#order).
   - **ALAC (done):** the decoder follows Apple's reference decoder and is checked sample for sample against it ([alac.md](formats/alac.md#decoding)).
   - **TTA (done):** the author's public pages describe only the container, so the decoder follows a clean-room description of libtta ([tta-format.md](design/tta-format.md)) and is checked sample for sample against the reference `tta` and FFmpeg ([tta.md](formats/tta.md)).
4. **TAK and OptimFROG at header level** ([planned.md](formats/planned.md#in-02-header-level-only)). Both are poorly documented, so they get only what their headers reveal.
   - Where a header stores the encoder's settings (OptimFROG's mode and speedup, TAK's preset), they are read and named, as Monkey's Audio's compression level and WavPack's mode flags are; whether each header holds them is checked against a source that can be verified and against files the encoders write (the lead, 2026-11-08).
5. **Two level figures** from step 6 of [spectral-analysis.md](design/spectral-analysis.md#rollout), which need no lossy decoder: a minimum length for the stretches that count as silence, and clipping at the extremes of the bits in use (16-bit audio padded to 24 bits tops out at 0x7FFF00, so only its negative runs count today).

### 0.3: Lossy formats measured

The common lossy formats are decoded, so their files get the spectrum, cutoff, levels and loudness that lossless files have.

1. **The decoders,** one pull request each, in the order of [lossy-decoders.md](design/lossy-decoders.md#order): MPEG audio (Layers I, II and III), Vorbis, Opus, AC-3, E-AC-3.
   - **Definition of done** of each: the decoder, its format's entropy-pass figures, its coding graphs and its gap wave 3 items ([lossy-decoders.md, "Gap wave 3 figures this unlocks"](design/lossy-decoders.md#gap-wave-3-figures-this-unlocks)). Coding graphs and gap wave 3 are no separate threads.
   - **The patent question** (decision 17 of [lossy-decoders.md](design/lossy-decoders.md#decisions)) is decided before E-AC-3 starts.
2. **The measurements' later steps** (step 6 of [spectral-analysis.md](design/spectral-analysis.md#rollout)), each its own change after the decoders above:
   - mid/side spectra and spectral holes, the lossy coders' finer marks, which need lossy streams and fixtures that show each mark;
   - the bandwidth figure for upsampled content, and a cutoff for mixed content from the frame counters.
   - The step's other items have places of their own: the two level figures and the TTA decoder are in 0.2, and the measurer on its own thread waits with parallel decoding under [Not planned](#not-planned).

### 0.4: Free decoders and routes complete

Every coding with a free source is decoded, the DTS extensions aside, and every codec reaches its parser in the containers that known writers fill.

1. **The Musepack, DTS core and Speex decoders,** with the same definition of done as in 0.3.
2. **The routes that known writers produce,** each its own pull request after the codec parsers it routes to:
   - MP4: FLAC (`fLaC`, FFmpeg), Opus (FFmpeg), Dolby TrueHD (`mlpa`, FFmpeg), PCM (FFmpeg);
   - MPEG audio as packets: MP4 tracks whose samples lie in several stretches and Matroska tracks (FFmpeg, mkvmerge), and the MPEG audio bursts of IEC 61937 in WAV (FFmpeg's spdif muxer). The MPEG parser reads one contiguous payload only today;
   - Matroska: PCM (FFmpeg, mkvmerge);
   - ASF: MP3 and AC-3 (the lead, 2026-11-07);
   - Matroska header stripping for AAC, AC-3, DTS and MPEG audio (mkvmerge writes it): a two-range `MappedBytes` whose prefix maps to the ContentCompSettings bytes.
3. **The small separate items:** WavPack 1 to 3; finding the correction file that belongs to a WavPack `.wv` (`.wvc`) or an OptimFROG DualStream `.ofs` (`.ofc`), with host access to a companion file, checking the pair against each other and reporting it as lossless only when the correction file is there and consistent, in the same words for both (for OptimFROG on headers and checksums alone, since it has no decoder; [tak-optimfrog-headers.md](design/tak-optimfrog-headers.md#optimfrog-dualstream), the lead, 2026-11-09); the TAK frame walk (the frame count, the frame CRC-24s and the bitrate per frame, which TAK then owes like the other lossless formats; decision 3 of [tak-optimfrog-headers.md](design/tak-optimfrog-headers.md#decisions)); ASF script command and marker contents.
4. **The coding graphs of formats whose data the parsers already read,** from the survey of [coding-graphs.md](design/coding-graphs.md#the-survey).
5. **A guess at the FLAC compression level** (the lead, 2026-11-08). The frames do not store the level, but each libFLAC level picks a typical block size, maximum predictor order and partition order.
   - Named as the encoder's settings with a confidence, as LAME's presets are, from reference files of each level, across libFLAC versions, and of FFmpeg's encoder, whose defaults differ.
   - Levels that share their settings are named together, never one of them by chance.
6. **PASC (the Digital Compact Cassette's coding) told apart from MPEG-1 Layer I** (the lead, 2026-11-08). PASC streams are Layer I bitstreams, so nothing in the syntax names them.
   - First a research step: the traits a DCC recorder or Philips' encoder leaves (the fixed 384 kbps, the header bits it sets, its allocation habits), from sources that can be checked and from real DCC transfers, since the DCC standard (IEC 61119) is sold and is not bought.
   - **A tool for fixtures:** github.com/MEDVEDx64/wav2pasc, a reimplementation of DCC2WAV.EXE. It has no license, so it is only a black-box tool for making fixtures; its code is not read or followed.
   - **The first structural signal to check:** its `.mpp` container: a 2-byte preamble (0x2C or 0x30, then 0x00) and records of 420 bytes (384 at 48 kHz), each starting FF FF C0 or FF FF C4 and then 00.
   - **Padding tells nothing by itself:** 24 of 49 frames padded at 384 kbps and 44.1 kHz is what every compliant Layer I encoder does, so padding alone does not tell PASC from MPEG-1 Layer I. Real DCC-Studio files are needed to confirm anything.
   - Then, only where files back them, an encoder claim with a confidence, as for other encoders; otherwise the research is recorded and the item goes under "Not planned".
7. **`A_MS/ACM` in Matroska,** routed by the WAVEFORMATEX format tag, once a real file from a muxer that writes it is found and committed as a fixture (the lead, 2026-11-07).

### 0.5: AAC and WMA

The most common formats without a free specification are decoded, on clean-room sources ([lossy-decoders.md](design/lossy-decoders.md#the-clean-room)).

The scope is confirmed when 0.4 is done. The decoders follow steps 9 to 15 of [lossy-decoders.md, "Order"](design/lossy-decoders.md#order), with the definition of done of 0.3:

1. **AAC LC,** after its clean-room description.
2. **HE-AAC** (SBR and parametric stereo).
3. **WMA standard, Professional and Lossless,** each after its clean-room supplement, with Microsoft's decoder as the reference.
4. **AAC Main, LTP and ER AAC.**
5. **USAC** (xHE-AAC).
6. **WMA Voice,** after its clean-room supplement. The lead wants it covered, as Speex is (2026-11-08; decision 12 of [lossy-decoders.md](design/lossy-decoders.md#decisions)).
   - **The patent decision** comes before HE-AAC, USAC and WMA Professional.

### After 0.5

The more formats of [planned.md](formats/planned.md#after-05-more-formats), as later milestones in this order; the better documented a format is, the sooner it comes.

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

- **Follow-up decoders:** the DTS extensions (XCh, XXCH, X96, XBR, LBR and DTS-HD's lossless extension XLL), which [lossy-decoders.md](design/lossy-decoders.md#what-each-decoder-covers) leaves as follow-ups of the DTS core decoder. Their place among the formats above is set when 0.5 is done.

## Not planned

Parked items carry the trigger that would bring them back. The formats at the end were never planned and carry the reason they stay out.

- **Parallel decoding** ([parallel-decoding.md](design/parallel-decoding.md), approved 2026-10-26 and deferred the same day): deferred in favor of new formats and deeper analysis. Not planned unless decoding speed becomes a problem: a decoder that still misses its speed target after its speed pass ([lossy-decoders.md, "Speed targets"](design/lossy-decoders.md#speed-targets)), or analysis times users report; the measurer on its own thread comes with it.
- **The detection pre-check** ([detection-precheck.md](design/detection-precheck.md)): recorded, not to be built (the lead, 2026-10-26). The design stays as a record for when detection becomes a bottleneck.
- **Offering the library** as packages: not planned yet; it waits for a contract the milestones no longer extend ([library.md](library.md)).
- **Sequenced formats** (MIDI, tracker modules and chiptunes): dropped by the lead on 2026-11-08, as a different domain from bitstream and encoder analysis ([history.md](history.md#dropped-plans)).
- **What the lossy decoders leave out,** by the signed-off design ([lossy-decoders.md, "Scope" and "What each decoder covers"](design/lossy-decoders.md#what-each-decoder-covers)), each with the condition the design states:
  - the MPEG-2 multichannel extension of Layer II: only its MPEG-1 compatible base channels are decoded, since ISO/IEC 13818-3 is not at hand;
  - E-AC-3 at a reduced sample rate (fscod2) and AC-3 with bsid 9 and 10: A/52 does not specify their bit allocation, and no free source for it is known;
  - AAC SSR gain control, which FDK AAC refuses; MPEG Surround beyond USAC's 2-1-2; DRC and loudness normalization, and every other decoder-side gain but Opus's output gain (decision 9);
  - Opus packet loss concealment and forward error correction, which the gap rule excludes;
  - the Musepack SV4 to SV6 streams that libmpcdec 1.2.6 refuses;
  - the WMA cases the parsers leave open (version 1 with variable block lengths and scale factor exponents, version 2 at 49,048 Hz and above, Professional frames over three packets or 32,768 bytes, the default channel transform matrices, music superframes inside Voice), until a clean-room supplement closes them (decision 12);
  - Dolby's object audio, and AC-4 and MPEG-H: the design does not cover their decoders, which wait for a design of their own.
- Game and console middleware (ADX, HCA, BRSTM, VAG, FSB, Bink), which is reverse-engineered with no public specification.
- The ancient lossless compressors and obsolete vocoders.
- Each of the many ADPCM and PCM variants as a format of its own: WAV and the containers name their codings.
- MXF, and AVI, which is mostly video.
- Codec and container combinations no known writer produces, and Dolby TrueHD as IEC 61937 MAT bursts in WAV (see the routes decision above).
