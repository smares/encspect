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
| Encoder output as fixtures | What an encoder writes is committed as a fixture unless the encoder's own license text forbids it. Encoders licensed for evaluation only, leaked code and licenses that forbid it are run by hand and nothing they write is kept, or the files stay in the private repository | The rule the Rarewares tools (Fraunhofer's mp3sEncoder, gogo, Helix) were already handled by | 2026-11-11 |
| Routes | Containers route only the codec and container combinations that writers in the wild produce | Routing what nobody writes costs code and fixtures for no file; see below | 2026-11-07 |
| Lossy decoders | The design of [lossy-decoders.md](design/lossy-decoders.md), every recommendation as listed | Each decoder brings its format's entropy pass, so the figures that need it come with the decoder | 2026-11-08 |
| Patents | No format waits for advice; the HE-AAC, USAC, E-AC-3 and WMA Professional decoders are built and included in the release binaries (the lead, 2026-11-13; decision 17 of [lossy-decoders.md](design/lossy-decoders.md)) | The decoders exist for analysis only, and users are responsible for the laws where they live | 2026-11-08 |
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
- A format's `pending` differences from the official decoders go to the lead with the pull request that commits its `official-decoders.json`; the analyzer changes that follow are their own pull requests, each with its goldens ([official-decoder-crosschecks.md](design/official-decoder-crosschecks.md#rollout), step 8). A total that is no reference is `keep`, never `pending`.
- When a format's decoder starts, check whether the OxideAV project ([github.com/OxideAV](https://github.com/OxideAV), MIT, written from the specifications under a clean-room rule stricter than ours) has a crate for the format. Its notes' measurements against other decoders are input for the design, and its decoder can serve as one more black-box cross-check. Agreement with it is weak evidence, since it is also written with Claude and can share a misreading; a disagreement points to a passage to reread. Code that follows it closely carries its MIT notice (the lead, 2026-11-13).

## Milestones

Each milestone is something a user can do that they could not do before. Its items come in the order listed.

- **The gate:** a milestone ends with the independent reviews of its work ([architecture.md, "Verification"](architecture.md#verification)), their findings fixed, and the push and nightly runs green on every platform.
- **The release:** a passed gate is tagged `v0.N.0` and released ([development.md, "Releases"](development.md#releases)).
- **0.1.0** was released after Gate 2; Gate 3 and the work after it lead into 0.2 ([history.md](history.md)).
- **0.2.0** follows Gate 0.2, passed on 2026-11-10 ([history.md](history.md#gates-in-detail)); its notes are [releases/0.2.0.md](releases/0.2.0.md).

### 0.2: Lossless complete

Every lossless format with a decoder design is decoded and measured, except WMA Lossless (0.5) and DTS-HD's lossless extension (a follow-up after 0.5). Files that lossyWAV processed are recognized, and TAK and OptimFROG files are named.

**Status:** built; the gate passed on 2026-11-10 at a858ea0b, with seven independent reviews and their fixes (#254 to #260).

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
   - **Design:** [tak-optimfrog-headers.md](design/tak-optimfrog-headers.md), decided by the lead on 2026-11-09.
   - **OptimFROG (done):** the parser, fixtures and cross-check ([optimfrog.md](formats/optimfrog.md)). The lead decided (2026-11-09): an `.ofc` is detected by its structure (a chain of `CORR` blocks whose CRC-32s hold) and named "OptimFROG correction data"; an `.ofs` is named "OptimFROG" with a low-confidence claim of the lossy part of a DualStream from the data blocks' reserved bytes, raised to medium when the file name ends in `.ofs`; `OFRX` and `DETA` are read as what `off` writes.
   - **TAK (done):** the parser, fixtures and cross-check ([tak.md](formats/tak.md)): the metadata objects with their CRC-24s, the stream info, the encoder info and preset, the stored WAV header and the end the last frame info gives. The frames are not walked (0.4, item 3) and the codec 3 of the 2.1 betas is named without a claim (the lead, 2026-11-09).
   - **OptimFROG DualStream** (the lead, 2026-11-09, as built): an `.ofs` is named "OptimFROG" with a claim that it is the lossy part of a DualStream (low confidence from the data blocks' reserved bytes, medium when the file name ends in `.ofs`), and a correction file opened alone is "OptimFROG correction data". Finding the correction file beside the `.ofs` and checking the pair waits for 0.4, item 3, with WavPack's `.wvc`.
5. **Two level figures (done)** from step 6 of [spectral-analysis.md](design/spectral-analysis.md#rollout), which need no lossy decoder: a minimum length for the stretches that count as silence, and clipping at the extremes of the bits in use (16-bit audio padded to 24 bits tops out at 0x7FFF00, so only its negative runs count today). Design: [Step 6: silence stretches and clipping at the bits in use](design/spectral-analysis.md#step-6-silence-stretches-and-clipping-at-the-bits-in-use), with four decisions for the lead, all taken as recommended (2026-11-10): the minimum of 10 ms applies at the start and end too, clipping counts at the bits in use from 8 bits in use, the CSV gains `Clipping Runs` and `Digital Silence Stretches`, and floats on the 16-bit and 24-bit grids are included. Built as designed: [As built: step 6 levels](design/spectral-analysis.md#as-built-step-6-levels).

### 0.3: Lossy formats measured

The common lossy formats are decoded, so their files get the spectrum, cutoff, levels and loudness that lossless files have.

1. **The decoders,** one pull request each, in the order of [lossy-decoders.md](design/lossy-decoders.md#order): MPEG audio (Layers I, II and III), Vorbis, Opus, AC-3, E-AC-3. **Opus in Matroska needs the Vorbis parser's feed delay for its DiscardPadding, and so does AAC in Matroska (item 1 of 0.5)** (the padding is the end trimming of the track's last block and the decoder must be told before the frames it takes samples from); the Vorbis follow-up keeps it in the Vorbis parser, since moving it into the host's session for every codec (the session holds the last frames of a track whose configuration states a padding and its block's frame count) is a design change, to be decided with the Opus decoder.
   - **Definition of done** of each: the decoder, its format's entropy-pass figures, its coding graphs and its gap wave 3 items ([lossy-decoders.md, "Gap wave 3 figures this unlocks"](design/lossy-decoders.md#gap-wave-3-figures-this-unlocks)). Coding graphs and gap wave 3 are no separate threads.
   - **MPEG audio (built, [as built](design/lossy-decoders.md#as-built-mpeg-audio)):** the decoder for Layers I to III, the Layer III entropy pass in every analysis with "Coded Bandwidth", the frame CRC-16 declared as a header checksum, and the PCM cross-checks against minimp3, FFmpeg and ISO/IEC 13818-4's reference output. **The intensity stereo bound (built, the lead's decision):** one numeric series, "Intensity Stereo Bound" in Hz on the axis of "Coded Bandwidth", the same name, unit and axis in every format with the concept, with a statistics node of the same name (lowest and highest hertz): Layers I and II (the bound subband of the mode extension, as hertz, next to the existing "Intensity Stereo Bounds" categories), Layer III (the lowest line from which a granule's right channel codes intensity positions, which the walk returns in `WalkFacts`), AAC (the lowest intensity stereo band of the frame) and Opus CELT (its intensity band, built with the Opus decoder), with their goldens ([coding-graphs.md](design/coding-graphs.md)).
   - **Vorbis (built, [as built](design/lossy-decoders.md#as-built-vorbis)):** the decoder for floors 0 and 1 and residues 0 to 2, the floor and residue entropy pass in every analysis with "Coded Bandwidth" (statistics and series), the granule positions' trims reaching the measurer, and the PCM cross-check against libvorbis and FFmpeg. Open for the lead: whether the floor adds a bandwidth figure; FFmpeg's native decoder disagrees with libvorbis on the one stream FFmpeg's own encoder wrote.
   - **Opus (built, [as built](design/lossy-decoders.md#as-built-opus)):** the decoder for SILK, CELT and hybrid frames, redundant frames, multistream mappings and the output gain; mode-change concealment (libopus's 5 ms cross-fade at a change of mode or SILK bandwidth, which RFC 6716 only recommends); a hybrid frame whose redundancy overruns its data is `opus.redundancy-overrun` and its packet is not decoded (libopus ignores the redundancy and conceals the CELT part; nothing is concealed here); the entropy pass in every analysis with the final range coder state of every frame, CELT's "Block Sizes", "Stereo Coding" and "Intensity Stereo Bound"; the pre-skip and end trimming (the Matroska DiscardPadding through the feed delay, and an end trimming that reaches back over earlier packets) as trimmed ranges, which take precedence over withheld and lost spans; Opus in MP4 routed (the `Opus` sample entry and its `dOps` box, with the end trimming taken from what the track's samples hold after its one media edit); the cross-checks against libopus 1.5.2 and RFC 8251's test vectors.
   - **AC-3 (built, [as built](design/lossy-decoders.md#as-built-ac-3)):** the decoder for AC-3 (bsid up to 8, Annex D's syntax included) written from A/52:2018 alone: mantissa dequantization with a fixed-seed dither, coupling, rematrixing, the transforms of 512 and 256 points with the Kaiser-Bessel derived window and the overlap; no dynamic range control or downmix. The parser reads every mantissa code in every analysis (checks 18 to 20 of A/52 7.10.2, `ac-3.block-inconsistent`) and declares crc1 and crc2, so a stream whose frames all pass reads "verified (lossy stream)"; a lost frame is a gap, and the frame after it withholds its first block (256 samples). No trims (the syntax states none and edit lists are not applied). The PCM cross-check against liba52 and FFmpeg (`crosscheck_ac3_pcm.py`: every file agrees with liba52 to 2.4 x 10^-7 of full scale with the dither flags cleared; dithered files by band energy); Core Audio and Dolby's decoder on the lead's notebook are still to run (below).
   - **E-AC-3 (built, [as built](design/lossy-decoders.md#as-built-e-ac-3)):** the decoder for the independent substreams of E-AC-3 (every substream ID its own program and stream) written from A/52:2018 Annex E, on AC-3's foundation: the adaptive hybrid transform (vector and gain adaptive quantization with the inverse DCT across the six blocks), spectral extension with its noise blend and notch filter, enhanced coupling (the angles and chaos values of A/52:2018, which ETSI TS 102 366 V1.4.1 leaves reserved; Dolby's decoder reads it as V1.4.1 does, amplitude-only, and the choice of A/52:2018 stands: the lead, 2026-11-14), standard coupling and rematrixing as in AC-3; no dynamic range control or downmix, and transient pre-noise processing with the Hanning windows A/52:2018 Annex E 3.7.2 names; the dependent substreams' channels (of an E-AC-3 core and of an AC-3 core alike) are decoded and joined to the program's layout by the channel map (Annex E 2.8, 3.8), so a 7.1 program gives its eight channels. The entropy pass reads every block of every E-AC-3 frame, the adaptive hybrid transform's values and enhanced coupling's parameters included, in every analysis; the one CRC is declared as the stream's checksum. Hand-built files with content (`eac3-signal-aht`, `-spx`, `-ecpl`), the FFmpeg-decoded real files of `check_eac3_samples.py` and FFmpeg's decoder as a black box; Core Audio (macOS job) and Dolby's decoder (on the lead's notebook, four runs) are cross-checks too: [development.md](development.md#e-ac-3-decoding) records what each does.
   - **Follow-up to the Vorbis decoder (the lead, 2026-11-11, built):** Matroska's DiscardPadding as the end trimming of a Vorbis track (a one-packet feed delay in the parser, so `Trim` reaches the decoder before the last packet) and the mid-stream "Gap Trimming" as trimmed ranges of the measurements (`IDecodeSession.TrimInner`, "inner" trimmed samples), with Matroska fixtures that carry DiscardPadding ([as built](design/lossy-decoders.md#as-built-vorbis)). Gap Trimming comes from Ogg granule positions; Matroska has none.
   - **The patent question** (decision 17 of [lossy-decoders.md](design/lossy-decoders.md#decisions)) is decided (the lead, 2026-11-13): the E-AC-3, HE-AAC, USAC and WMA Professional decoders are built and included in the release binaries.
   - **The official-decoder cross-checks of AC-3 and E-AC-3** that rollout steps 5 and 7 left open ([official-decoder-crosschecks.md](design/official-decoder-crosschecks.md)) come with these decoders: the AC-3 variants (rules in `derive_variants.py`), the downloaded E-AC-3 samples (`eac3-samples.json`), Core Audio's independent substream 1 and dependent substreams (`substreams.eac3`), and the comparison of the PCM.
   - **Rollout step 7, AC-3 and E-AC-3 against Dolby's decoder on Windows,** comes with them too (the lead, 2026-11-11). The runner images have no Dolby decoder, but the lead's Windows 10 22H2 notebook has "Microsoft Dolby Digital Plus Decoder MFT" (`DolbyDecMFT.dll` 10.0.19041.7844, input AC-3, the AC-3 WAVE tag and E-AC-3; the probe's inventory created it). The probe and the files are run there by hand, and the record states that machine's build and DLL versions in place of a runner image's.
2. **The measurements' later steps** (step 6 of [spectral-analysis.md](design/spectral-analysis.md#rollout)), designed in [Step 7](design/spectral-analysis.md#step-7-spectral-bandwidth-partial-cutoff-midside-spectra-and-spectral-holes) (approved by the lead, 2026-11-14), in two changes after the decoders above:
   - first the spectral bandwidth (the figure for upsampled content) and the partial cutoff (a cutoff for mixed content, from a per-frame ceiling counter) (built, [as built](design/spectral-analysis.md#as-built-step-7-first-change); the thresholds of the ceiling and of the share are open there);
   - then the mid/side spectra and spectral holes, the lossy coders' finer marks, with stereo encoder fixtures that show each mark.
   - The step's other items have places of their own: the two level figures and the TTA decoder are in 0.2, and the measurer on its own thread waits with parallel decoding under [Not planned](#not-planned).
3. **Encoder-written MPEG fixtures and a survey of old encoders** (the lead, 2026-11-11, started at once beside the decoders):
   - **mctoolame,** the multichannel Layer II encoder (2003) whose decoder mctoolamed already cross-checks the multichannel extension: built when the fixtures are made (CI builds only mctoolamed) and run as a black box, so the MPEG-2 multichannel fixtures include files a real encoder wrote, not only hand-built frames. The project site's real 5-channel stream states no license and is fetched in CI at a pinned hash, not committed.
   - **Philips' multichannel MP2 encoder 2.5** (1999, Rarewares' Windows build, under Wine): a second encoder-written source of MPEG-2 multichannel Layer II beside mctoolame.
   - **LAME before 3.90** (Rarewares' Windows builds of 3.20 to 3.89, under Wine, the releases that change what a stream shows) **and 3.95 to 3.98.4** (built from source as `old_lame_tool.py` builds the others), for the encoder detection of old rips.
   - **libvorbis before 1.3** (sample kit row OGG-01, built, the lead 2026-11-12): oggenc with libvorbis 1.0, 1.0.1, 1.1.0, 1.1.2, 1.2.0, 1.2.2 and 1.2.3 built from the tags of xiph/vorbis (`xiph_tool.py`), since Rarewares keeps only 1.3.7-era builds and no archived Windows build could be reached; 12 `external-libvorbis-*` fixtures in `tests/fixtures/ogg`, cross-checked against libvorbis's PCM.
   - **A survey** of the encoders Rarewares keeps against what Encspect covers, with their licenses, ranked by value and feasibility; it ends in a list for the lead, not in code.
   - **`lame_enc.dll` builds** (CDex, Audiograbber, dBpoweramp, Exact Audio Copy, Rarewares' bundles; the lead, 2026-11-11), only where their version string or behavior differs from the releases covered: none is a fixture yet. Rarewares' bundles carry the DLL of 3.99.5, 3.100 and 4.0 only, which the fixtures already cover from source, and the front ends' older DLLs have no stable address; a host for the BladeEnc-compatible API (built with mingw, run under Wine) waits for archives the project can pin.
4. **Small fixes found by the Vorbis trims review** (the lead, 2026-11-12; lost or refused units that are not counted; none is fixed in the trims pull request):
   - WavPack `built-block-size-too-large`, `-too-small` and `built-block-trailing-byte`: the report says 24,000 samples, the measurements start at 11,025 and hold 12,975; the damaged first block's 11,025 samples are neither decoded nor counted lost, and no finding says so.
   - WavPack `built-set-without-final-limit`: 16,000 samples lost against a report of 3,000, and 2,000 of the report's samples are neither measured nor shown apart.
   - TrueHD `built-parameters-change.thd`: the decoder stops at the stream parameters change (`truehd.stream-parameters-change`); the report says 4,800 samples, the measurements 1,600, and the 3,200 samples after it are counted nowhere, with no finding.
5. **Small fixes queued with the intensity stereo bound** (the lead, 2026-11-12; the first from parallel local runs of the tests, the second from the Opus decoder's MP4 route):
   - `MemoryTests`' `SharedPoolGuard` fails `DecodingManyVorbisPacketsHoldsNothingPerPacket`, `ManyEmptyVorbisCommentsStayBounded` and an MP4 memory test in parallel local runs; each passes alone, so the guard sees another test's pool use.
   - Opus in MP4 takes no ©too writer claim from the container, as AC-3 and AAC in MP4 do.
6. **Rename of the MPEG audio project (done,** the lead, 2026-11-13): `Encspect.Formats.Mpeg` is now `Encspect.Formats.MpegAudio`, with its tests in `Formats/MpegAudio` and its fixtures in `tests/fixtures/mpeg-audio`, so that the project names match the format name used elsewhere. Codec IDs and node names are unchanged, and `--only-format mpeg` and `--as mpeg` still work as the start of the name. The lead decided on the same day that the 62 `mpeg.*` finding codes become `mpeg-audio.*`, as other formats prefix their codes with their name (`monkeys-audio.*`): JSON finding codes change, the structure does not, `schemaVersion` stays 1, and the change goes into the release notes.
7. **Rename of the AC-3 and DTS-HD finding codes (done,** the lead, 2026-11-13; done 2026-11-14): `ac3.*` becomes `ac-3.*` and `dtshd.*` becomes `dts-hd.*`, after the E-AC-3 decoder merges; the same treatment as item 6 (JSON finding codes change, `schemaVersion` stays 1, release notes).

### 0.4: Free decoders and routes complete

Every coding with a free source is decoded, the DTS extensions aside, and every codec reaches its parser in the containers that known writers fill.

1. **The Musepack, DTS core and Speex decoders,** with the same definition of done as in 0.3.
2. **The routes that known writers produce,** each its own pull request after the codec parsers it routes to:
   - MP4: FLAC (`fLaC`, FFmpeg), Dolby TrueHD (`mlpa`, FFmpeg), PCM (FFmpeg);
   - MPEG audio as packets: MP4 tracks whose samples lie in several stretches and Matroska tracks (FFmpeg, mkvmerge), and the MPEG audio bursts of IEC 61937 in WAV (FFmpeg's spdif muxer). The MPEG parser reads one contiguous payload only today;
   - Matroska: PCM (FFmpeg, mkvmerge);
   - ASF: MP3 and AC-3 (the lead, 2026-11-07);
   - Matroska header stripping for AAC, AC-3, DTS and MPEG audio (mkvmerge writes it): a two-range `MappedBytes` whose prefix maps to the ContentCompSettings bytes.
3. **The small separate items:** WavPack 1 to 3, with encoder fixtures from the builds Rarewares keeps (3.4, 3.97, the first BSD release, and the DOS build of 4.31); finding the correction file that belongs to a WavPack `.wv` (`.wvc`) or an OptimFROG DualStream `.ofs` (`.ofc`), with host access to a companion file (found by base name and extension, `x.wv` to `x.wvc` and `x.ofs` to `x.ofc`, beside the input; an input with no folder, a stream or standard input, reports the pair as unchecked), checking the pair against each other and reporting it as lossless only when the correction file is there and consistent, in the same words for both (for OptimFROG on headers and checksums alone, since it has no decoder; [tak-optimfrog-headers.md](design/tak-optimfrog-headers.md#optimfrog-dualstream), the lead, 2026-11-09); the TAK frame walk (the frame count, the frame CRC-24s and the bitrate per frame, which TAK then owes like the other lossless formats; decision 3 of [tak-optimfrog-headers.md](design/tak-optimfrog-headers.md#decisions)); ASF script command and marker contents.
4. **The coding graphs of formats whose data the parsers already read,** from the survey of [coding-graphs.md](design/coding-graphs.md#the-survey).
   - With them, TrueHD's "Stereo Coding" from the matrix parameters of the substream blocks, which the parser already reads: a tally and its category series under the name, unit and axis of the other formats' "Stereo Coding" (the lead, 2026-11-10; [lossy-decoders.md, "Gap wave 3 figures this unlocks"](design/lossy-decoders.md#gap-wave-3-figures-this-unlocks)).
5. **A guess at the FLAC compression level** (the lead, 2026-11-08). The frames do not store the level, but each libFLAC level picks a typical block size, maximum predictor order and partition order.
   - Named as the encoder's settings with a confidence, as LAME's presets are, from reference files of each level, across libFLAC versions, and of FFmpeg's encoder, whose defaults differ.
   - Levels that share their settings are named together, never one of them by chance.
6. **PASC (the Digital Compact Cassette's coding) told apart from MPEG-1 Layer I** (the lead, 2026-11-08). PASC streams are Layer I bitstreams, so nothing in the syntax names them.
   - First a research step: the traits a DCC recorder or Philips' encoder leaves (the fixed 384 kbps, the header bits it sets, its allocation habits), from sources that can be checked and from real DCC transfers, since the DCC standard (IEC 61119) is sold and is not bought.
   - **A tool for fixtures:** github.com/MEDVEDx64/wav2pasc, a reimplementation of DCC2WAV.EXE. It has no license, so it is only a black-box tool for making fixtures; its code is not read or followed.
   - **The first structural signal to check:** its `.mpp` container: a 2-byte preamble (0x2C or 0x30, then 0x00) and records of 420 bytes (384 at 48 kHz), each starting FF FF C0 or FF FF C4 and then 00.
   - **Padding tells nothing by itself:** 24 of 49 frames padded at 384 kbps and 44.1 kHz is what every compliant Layer I encoder does, so padding alone does not tell PASC from MPEG-1 Layer I. Real DCC-Studio files are needed to confirm anything.
   - Then, only where files back them, an encoder claim with a confidence, as for other encoders; otherwise the research is recorded and the item goes under "Not planned".
7. **The TAK decoder** (the lead, 2026-11-09), after the TAK header parser of 0.2 and with the frame walk of item 3.
   - TAK's author publishes only the container source (`takStream.pas`) and a closed decoder library; the only detailed source for the coding is FFmpeg's reverse-engineered TAK decoder (LGPL).
   - So the clean-room split applies, as for TrueHD and TTA: one agent reads it and writes a description without code, another implements from that description.
   - The TAK SDK's decoder library on the Windows runner is the black-box reference for the PCM cross-check, with FFmpeg as a second column.
8. **`A_MS/ACM` in Matroska,** routed by the WAVEFORMATEX format tag, once a real file from a muxer that writes it is found and committed as a fixture (the lead, 2026-11-07).
9. **The coding's class on the coding line** (the lead, 2026-11-11). Each stream states whether its coding is lossless, lossy or hybrid: a property of the coding, never of the audio's history.
   - **The classes,** set by each parser for every format: lossless for linear and floating-point PCM and the lossless codecs; lossy for A-law and mu-law (ITU-T G.711 rounds 13- or 14-bit samples to 8 bits), the ADPCM codings and the perceptual codecs; a WavPack hybrid or OptimFROG DualStream file lossless only with its correction file (item 3), lossy without it, which `StreamVerification.IsLossy` then follows.
   - **lossyWAV:** a file it processed keeps the class of its coding, with the processor claim beside it.
   - **Worded with the basis:** "lossless coding", not "lossless"; and "Verified" says what it covers (the decoded audio matches the stored checksum), which tells nothing about where the audio came from.
   - **The figures beside it:** the spectral cutoff, the low bits unused and the processor claim stand next to the coding line in the text report and the GUI, so the evidence about a file's history is where a reader looks first.
   - **An article** on what the coding class and these figures can and cannot show about a file's history, linked from the coding line. No figure becomes a verdict (the non-goals).
10. **Nero's `tags` box in MP4** (the lead, 2026-11-11). neroAacTag writes its tags twice: as iTunes metadata, which the MP4 parser reads, and in a `tags` box of its own in `moov/udta`, which the parser shows as an unknown box.
   - **What is read:** the box's fields as a tag (title, artist, album and the rest, user fields such as `-meta-user`, the encoder's `cdec` command line), the covers `-write-nd-covers` puts there, and Nero's metadata per chapter, which iTunes metadata cannot hold.
   - **Sources:** no specification is known. The layout is read from files neroAacTag 1.5.4.0 wrote, with neroAacTag's own listings (`-list-meta`, `-list-covers`, `-list-chapters`) as the cross-check; nothing of Nero's code is read or decompiled.
   - **Fixtures:** the lead's four neroAacTag files and their log (a cover in both formats, non-ASCII text that neroAacTag stores as question marks, three chapters with their own titles, a user field), committed when the item starts; a field that disagrees with the iTunes metadata beside it is a finding.
11. **"Show Only Audio" in the GUI's file list** (the lead, 2026-11-11): a switch that hides the files in which Encspect found no audio, by the detected format and never by the file name, as `--only-format` does on the command line.
   - The files stay analyzed and listed in the summary cache; the switch only filters the view, and the list says how many files it hides.
   - File names never decide what is read: a list of extensions to scan would hide the mislabeled files Encspect is meant to find.
12. **The sample kit's files as fixtures** (the lead, 2026-11-11). The encodes received through the private sample kit (Nero Burning ROM's MP3, mp3PRO and AAC, neroAacEnc 1.0.0.2 to 1.5.4, qaac with Core Audio, iTunes' AAC and MP3 encoders, Winamp's Fraunhofer AAC, aacPlus and Vorbis encoders, Windows 10's Media Foundation encoders) come from synthetic sources, so the audio carries no copyright.
   - **A selection per encoder,** the files that show something the fixtures lack (a profile, mode, rate, header or tag), not every bitrate; the kit holds about 85 MB, most of it Media Foundation's lossless files.
   - **Imported by a script** like `import_macos_aac_fixtures.py`, with a manifest, a golden report and the cross-checks of each format, and a provenance record of encoder, version and settings; the contributor's logs and machine details stay private.
   - **Nero's files included:** Nero's license limits how the software is used, not the files it writes.
13. **Old AAC and MP3 encoders under Wine** (the lead, 2026-11-11, from the [Rarewares survey](#03-lossy-formats-measured)), for the encoder detection of real files of their time, each archive pinned by SHA-256:
   - **PsyTEL AACenc 2.x,** the common AAC encoder of 2001 to 2003 (ADTS output);
   - **Fraunhofer's MP3 ACM codecs** (l3codeca 1.9, l3codecp 3.3 and 3.4) driven by nyaochi's acmenc, the source of most Fraunhofer-written MP3s in the wild;
   - **Plugger 0.4, 8hz-MP3 0.2b and M3E 0.99a,** the small encoders whose names EncSpot-style rules already use, as ground truth for those rules.
   - **Fastencc 1.02** is built on code leaked from Fraunhofer, so it is run by hand and nothing it writes is kept; its known stereo coding bug is described from that run only.
14. **One page of the reference tools' differences** (the lead, 2026-11-12). The cross-check scripts keep, per tool and version, the files where a reference tool reads or decodes differently from the analyzer, each with its evidence and failing once the tool agrees; but the entries are spread over a dozen scripts.
   - **Generated:** `docs/reference-tool-differences.md`, written by a script from those entries and checked in CI to match them, grouped by tool and version.
   - **A kind per entry:** the tool breaks a specification (FFmpeg 6.1.1 applying a Matroska block's DiscardPadding to every laced frame, RFC 9559), the tool reads otherwise within what the specification allows (MediaInfo taking the LAME tag's ABR target as the bitrate), or open (the cause not established).
   - **What it is for:** the starting point for reporting the violations upstream, which stays the lead's decision per report.
15. **ISO/IEC 11172-3 and 13818-3 against their text** (the lead, 2026-11-14). Copies of both standards' text (11172-3's first edition, 1993, and 13818-3's second edition, 1997) are at hand as private references: they stay out of the repository and the downloads, and the docs cite their clauses only. It brings back the item that waited under "Not planned" for it.
   - **The multichannel decoder:** the MPEG-2 multichannel extension of Layer II decoded in full (the transmission channels, dematrixing, dynamic crosstalk, prediction and the LFE), where today only the MPEG-1 compatible base channels are; with the 22 multichannel conformance streams of ISO/IEC 13818-4 in the PCM cross-check against their dematrixed reference, and mctoolamed as a second column.
   - **The extension's parser against the text:** the clause numbers that [mpeg-clean-room-rules.md](design/mpeg-clean-room-rules.md) and [development.md](development.md#mpeg-audio-decoding) cite unchecked, and the `dyn_cross_LR` cases the conformance streams leave undecided ([development.md](development.md#mpeg-audio-decoding), "Multichannel extension"); where the text settles one, the analyzer takes that reading and the CRC fallback goes.
   - **The decoder buffer rule for MPEG-2 and 2.5,** which [mpeg-audio.md](formats/mpeg-audio.md) checks for MPEG-1 only.
   - **11172-3's citations and tables:** every clause and table the MPEG audio code and docs cite checked against the text, and the tables taken from minimp3 as format data (Annex B: the Huffman tables, the Layer II allocation tables, the scalefactors and the band widths) compared with Annex B, each difference confirmed on the page image, since the copy is a scan.

### 0.5: AAC and WMA

The most common formats without a free specification are decoded, on clean-room sources ([lossy-decoders.md](design/lossy-decoders.md#the-clean-room)).

The scope is confirmed when 0.4 is done. The decoders follow steps 9 to 15 of [lossy-decoders.md, "Order"](design/lossy-decoders.md#order), with the definition of done of 0.3:

1. **AAC LC,** after its clean-room description. Its decoder needs the Vorbis parser's feed delay for a Matroska DiscardPadding (the padding is per block and the decoder must be told before the frames it takes samples from; see the Opus item of 0.3).
2. **HE-AAC** (SBR and parametric stereo), with the replicated bands as a measurement of the decoded audio (SBR, E-AC-3's spectral extension, CELT's folding), whose fixtures it brings (decision S10 of [spectral-analysis.md](design/spectral-analysis.md#decisions-for-the-lead-1)).
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
12. mp3HD, the lossless extension of MP3 (the lead, 2026-11-11)

- **Follow-up decoders:** the DTS extensions (XCh, XXCH, X96, XBR, LBR and DTS-HD's lossless extension XLL), which [lossy-decoders.md](design/lossy-decoders.md#what-each-decoder-covers) leaves as follow-ups of the DTS core decoder. Their place among the formats above is set when 0.5 is done.

## Not planned

Parked items carry the trigger that would bring them back. The formats at the end were never planned and carry the reason they stay out.

- **Parallel decoding** ([parallel-decoding.md](design/parallel-decoding.md), approved 2026-10-26 and deferred the same day): deferred in favor of new formats and deeper analysis. Not planned unless decoding speed becomes a problem: a decoder that still misses its speed target after its speed pass ([lossy-decoders.md, "Speed targets"](design/lossy-decoders.md#speed-targets)), or analysis times users report; the measurer on its own thread comes with it.
- **Inter-channel coherence of decoded audio** (decision S10 of [spectral-analysis.md](design/spectral-analysis.md#decisions-for-the-lead-1), 2026-11-14): the pair's bins of the mid/side spectra would give the coherence that shows intensity stereo above its bound, which the parsers' "Intensity Stereo Bound" could cross-check. Not planned unless a user or a review asks for a figure of intensity stereo in decoded audio.
- **The detection pre-check** ([detection-precheck.md](design/detection-precheck.md)): recorded, not to be built (the lead, 2026-10-26). The design stays as a record for when detection becomes a bottleneck.
- **Offering the library** as packages: not planned yet; it waits for a contract the milestones no longer extend ([library.md](library.md)).
- **Sequenced formats** (MIDI, tracker modules and chiptunes): dropped by the lead on 2026-11-08, as a different domain from bitstream and encoder analysis ([history.md](history.md#dropped-plans)).
- **What the lossy decoders leave out,** by the signed-off design ([lossy-decoders.md, "Scope" and "What each decoder covers"](design/lossy-decoders.md#what-each-decoder-covers)), each with the condition the design states:
  - E-AC-3 at a reduced sample rate (fscod2) and AC-3 with bsid 9 and 10: A/52 does not specify their bit allocation, and no free source for it is known;
  - AAC SSR gain control, which FDK AAC refuses; MPEG Surround beyond USAC's 2-1-2; DRC and loudness normalization, and every other decoder-side gain but Opus's output gain (decision 9);
  - Opus packet loss concealment and forward error correction, which the gap rule excludes;
  - the Musepack SV4 to SV6 streams that libmpcdec 1.2.6 refuses;
  - the WMA cases the parsers leave open (version 1 with variable block lengths and scale factor exponents, version 2 at 49,048 Hz and above, Professional frames over three packets or 32,768 bytes, the default channel transform matrices, music superframes inside Voice), until a clean-room supplement closes them (decision 12);
  - Dolby's object audio, and AC-4 and MPEG-H: the design does not cover their decoders, which wait for a design of their own.
- **The official-decoder runs on the Intel and arm64 Windows images** (the Windows column runs on `windows-2025`, x64, only). Trigger: a difference that depends on the build.
- **An OptimFROG decoder** (the lead, 2026-11-09): the codec is closed and no open implementation exists, not even under the LGPL; its published `format.txt` covers the container only, which is what the header-level parser reads. Reverse-engineering the binaries is neither allowed by its license nor a source that can be checked. Not planned unless its author publishes the coding or an open implementation appears. Until then OptimFROG streams are checked by their block CRC-32s, and a DualStream pair is judged on headers and checksums alone.
- Game and console middleware (ADX, HCA, BRSTM, VAG, FSB, Bink), which is reverse-engineered with no public specification.
- The ancient lossless compressors and obsolete vocoders.
- Each of the many ADPCM and PCM variants as a format of its own: WAV and the containers name their codings.
- MXF, and AVI, which is mostly video.
- Codec and container combinations no known writer produces, and Dolby TrueHD as IEC 61937 MAT bursts in WAV (see the routes decision above).
