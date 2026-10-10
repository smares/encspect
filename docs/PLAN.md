# Encspect: Project Plan

Planning date: 2026-10-10. This file is the canonical copy of the plan; keep it up to date as decisions change.

## Goals and non-goals

The tool tells you everything technically knowable about an audio file's structure, and every value it reports points back to the bytes it came from. It is a GUI and CLI analyzer for people who want EncSpot-level detail, across many more formats.

**Goals**

- Deep header, metadata and structure analysis: encoder detection, bitrate behavior, bit reservoir usage, Xing/Info/LAME/VBRI headers, and all tag formats (ID3v1, ID3v2, Lyrics3, APE and others).
- Every reported value carries its byte offset and length, so the GUI can show a structure tree next to a hex view and the CLI can emit text or JSON from the same data.
- Broad format coverage, with one self-contained parser per format (see the rollout section).
- Damaged or odd files produce findings (warnings, anomalies), never crashes.
- One codebase for Windows, macOS and Linux on x64 and ARM64.

**Non-goals for v1**

- Audio playback.
- Scalefactor and Huffman-table level MP3 analysis. v1 stops at headers, side info and metadata; the deeper pass is a later phase.
- Runtime-loadable plugins. Formats are separate projects, composed at build time.
- Editing or writing tags.
- Bundling external decoders or native libraries.

## Decisions so far

The stack is C# on .NET with Avalonia for the GUI. The code is checked by tests and review agents rather than line by line by a person, which shapes several choices below.

| Decision | Choice | Why |
| --- | --- | --- |
| Language | C# (.NET) | Reads like TypeScript; a malformed file raises a catchable exception instead of corrupting memory; one `dotnet publish` per OS and architecture |
| GUI | Avalonia UI | Native OS dialogs and tree view on Windows, macOS and Linux, x64 and ARM64 |
| CLI | Console app on the same core | Thin front end over the shared library |
| Parsers | Written ourselves, one project per format | Libraries hide the fields this tool exists to show; TagLib# is used only to cross-check |
| Playback | None | Pure analyzer; removes BASS, native binaries and licensing questions |
| Plugins | Build-time modules only | No runtime loading, so no version policy and no publishing restrictions |
| Decoding | Optional, per format | Only for waveform, spectrogram, lowpass and similar; formats without a decoder still get the full structure report |
| Hosting and CI | GitHub, hosted runners | Covers Windows, macOS (Apple Silicon) and Linux on x64 and ARM64 |
| Name | Encspect | Detenc and Framescope were already taken; Encspect was free |
| License | MIT | Default in the .NET ecosystem; each dependency's license is still checked individually |

## Architecture

Every parser depends on one small abstractions library and on nothing else; the front ends reach parsers only through a host.

```mermaid
flowchart TD
    CLI["CLI<br/>text, JSON and CSV output"] --> Host
    GUI["GUI (Avalonia)<br/>tree, hex view, graphs"] --> Host
    Decoders["Decoders<br/>optional, per format"] -.->|"waveform, spectrogram"| GUI
    Host["Host<br/>sniffs the format, runs tag and format parsers,<br/>assembles one report"] --> Tags["Tag parsers<br/>ID3v1, ID3v2, Lyrics3, APE"]
    Host --> Formats["Format parsers, one project each<br/>MP1/2/3 (v1), FLAC, Ogg, MP4, ..."]
    Tags --> Core["Core.Abstractions<br/>parser interface, byte and bit readers,<br/>result model: node tree with name, value,<br/>byte offset and length, plus findings"]
    Formats --> Core
```

The host sniffs the format, lets tag parsers claim their regions, then hands the rest to a format parser. Decoders are separate and optional, so no parser ever depends on one.

- **Result model:** every node has a name, value, byte offset, length and children; findings carry a severity and point to the node that caused them.
- **Detection:** each parser reports a confidence from magic bytes; the file extension is only a hint.
- **Tags are separate from formats,** because ID3, APE and Lyrics3 appear in many of them.
- **Containers hand streams to codec parsers,** for example Ogg to Vorbis and Opus, MP4 to AAC and ALAC.
- **The abstractions stay internal** and may change freely until the second format works.

## v1 vertical slice: MP3

v1 is MP1/MP2/MP3 analysis at the header, side-info and metadata level, with no Huffman decoding. It is built first because it forces the shared model and parser interface into shape before any second format exists.

**File layout and tags**

- Leading junk and resync: find the first valid frame, report skipped bytes, and re-sync after damage.
- ID3v2.2, 2.3 and 2.4: header, flags, extended header, footer, padding, unsynchronization, and every frame with its decoded text encoding.
- Trailing tags: ID3v1 and v1.1, the Enhanced TAG, Lyrics3 v1 and v2, and APEv1 and v2 including every item.
- Any gaps, overlaps or trailing garbage between regions are reported as findings.

**Per-frame data (all layers)**

- Every header field: MPEG version (1, 2, 2.5), layer, CRC protection, bitrate, sampling rate, padding, channel mode and mode extension, copyright, original, emphasis.
- Frame offset, length, and CRC-16 verification when protection is on.
- Mid-stream parameter changes (sampling rate, channel mode) and free-format streams.

**Layer III side info**

- `main_data_begin` and the resulting bit reservoir fill for each frame.
- Per granule and channel: `part2_3_length`, `big_values`, `global_gain`, `scalefac_compress`, block type and mixed-block flag, table selects, region counts, `subblock_gain`, `preflag`, `scalefac_scale`, `count1table_select`, plus `scfsi` per channel.
- Joint stereo: MS and intensity usage per frame.

**Encoder and VBR headers**

- Xing and Info: flags, frame and byte counts, the seek table, quality.
- LAME extension: encoder string, tag revision, VBR method, lowpass, peak, ReplayGain fields, encoding flags, ATH type, minimum or ABR bitrate, delay and padding, noise shaping, stereo mode, source sample rate, MP3 gain, preset, music length, tag CRC and music CRC (both verified).
- VBRI (Fraunhofer): version, delay, quality, byte and frame counts, seek table.
- Encoder detection from these tags, with a clearly labeled confidence level when it has to fall back on heuristics for files without tags.

**Computed statistics**

- Counted frames and duration compared with what the Xing, LAME or VBRI header claims, with mismatches flagged.
- Bitrate distribution, average, minimum and maximum, and bitrate over time for VBR.
- Bit reservoir usage over time.
- Gapless information: encoder delay, padding, and exact sample count when available.

## Tag detection and false positives

Tags are found by structure and byte ownership, never by signature alone, because short signatures collide with each other and with audio data.

**Rules**

1. **Position-anchored search.** Each tag type is looked for only where its spec allows: ID3v1 at exactly EOF-128, APE at EOF-32 or just before an ID3v1 tag, ID3v2 at the start or directly after a previous tag. Signatures are never scanned for across the whole file.
2. **Structural validation decides confidence, not existence.** A candidate is rejected only when its extent cannot be determined: an ID3v2 size that is not syncsafe, an APE size that points outside the file, a Lyrics3 v2 size that does not point at `LYRICSBEGIN`, a footer without a matching header. Everything else that breaks a specification is reported the way a lost MPEG sync is: the tag stays in the report, each violation becomes a finding, and the confidence drops. Full confidence needs everything to check out: for ID3v2, known flags, frames that fit the declared size and padding only at the end; for APE, header and footer that agree and items that end exactly at the footer; for Lyrics3 v2, at least one field, fields that end exactly at the size and field bytes from 01 to 254; for Lyrics3 v1, text without $00 or $FF within 5100 bytes. ID3v1 has almost nothing to validate, so it never rises above medium confidence.
3. **Evaluation order and ownership.** Tags are only looked for at anchors (rule 1). When several candidates claim the same anchor, the one with the strongest evidence wins and the order APE, Lyrics3, ID3v2 found through its footer, ID3v1, Enhanced TAG breaks ties. A tag that its specification places directly before another one (Lyrics3 and the Enhanced TAG before ID3v1) is reported without it too, with a finding and one level less confidence, so a correctly placed competitor wins. The host keeps a claim map, so a signature inside an already claimed region (an image in an ID3v2 tag, an APE item, an ID3v1 field) is rejected.
4. **Confidence and evidence.** Every candidate carries a confidence level and its reasons. Losing and rejected candidates become findings, such as "TAG at EOF-128 rejected: part of an APE tag header".
5. **MPEG sync.** A frame header counts only when a chain of consecutive, consistent frames follows it. Stretches that do not fit are reported as gaps, not guessed.

**The APE and ID3v1 collision.** `APETAGEX` contains `TAG` at offset 3. An APE tag with a header that totals 131 bytes (32 header, 67 items, 32 footer) and ends the file places that `TAG` at EOF-128, exactly where ID3v1 is expected. A reader that trusts the signature reports a garbage ID3v1 tag made of the APE header, items and footer. Rule 3 prevents this: the APE tag validates fully, its evidence outweighs the bare `TAG`, and it claims the bytes.

**Fixtures that prove it**

- The 131-byte APE tag above with no ID3v1 tag: expect the APE tag only, plus a near-miss finding.
- A real ID3v1 tag directly after an APE tag, and an APE tag after an ID3v1 tag.
- Lyrics3 v1 and v2 before ID3v1, and every plausible stacking order of ID3v2, APE, Lyrics3 and ID3v1.
- ID3v2.4 tags found through their footer: at EOF, before APE and ID3v1, at both ends of the file, as the whole file, and with `TAG` at EOF-128 inside the tag. A tag appended without a footer cannot be found from the end (ID3v2.4 section 3.4) and stays data.
- Tags embedded inside cover art in an ID3v2 tag.
- Random data seeded with `TAG`, `ID3` and `APETAGEX` at random offsets: nothing is detected outside validated positions.
- User text that spells another tag's identifier exactly where a probe looks for it: ID3v1 fields, APE text items, Lyrics3 lyrics and fields, and appended ID3v2 frames, each holding every identifier at every probe distance. The real tags must come out unchanged, without warnings.
- Truncated tags and concatenated files.

The first fixture needs no external tool; a script writes the exact bytes.

## Verification strategy

Nobody will read the code line by line, so correctness comes from automated checks that do not depend on the author's judgment. The weak spot to guard against is spec details written from memory, such as field offsets and odd edge cases.

1. **Known-input fixtures.** CI installs `lame`, `ffmpeg` and `flac`, then generates files with known settings (CBR, VBR, ABR, presets, joint stereo, with and without CRC and tags). Tests assert that the analyzer reports exactly what was encoded.
2. **Cross-checks against other tools.** Where results overlap, compare with `ffprobe` and TagLib#. Disagreements fail the build until explained.
3. **Golden reports.** Each fixture has a stored expected report; any output change shows up as a diff that has to be approved.
4. **Corrupt-file tests and fuzzing.** Truncated, bit-flipped and garbage-prefixed files must never crash or hang the tool; they must yield findings. The tag-collision cases are listed under tag detection.
5. **Spec references in the code.** Each parsed field cites the spec section it implements, so a reviewer can check it quickly.
6. **Independent review.** After each milestone, a separate review agent gets the code and the spec without the author's conclusions; its findings are recorded in the pull request.

Nothing merges with a red pipeline, and nothing is pushed to GitHub unless the owner says so for that push.

## GitHub and CI

GitHub Actions is the single source of truth for "does it build and pass on every platform", so the work can be verified without anyone's computer being on. The repository is `smares/encspect` (public, which keeps hosted-runner minutes free).

**Matrix.** Windows, macOS and Linux, each on x64 and ARM64 where a hosted runner exists. Exact runner labels are checked when the workflow is written, since GitHub changes them from time to time.

**Pipeline stages**

1. Restore and build with warnings treated as errors.
2. Formatting check (`dotnet format --verify-no-changes`).
3. Unit tests for every parser.
4. Install `lame`, `ffmpeg` and `flac`, generate fixtures, run integration tests and the cross-checks.
5. Corrupt-file and fuzz smoke tests.
6. Headless GUI tests using Avalonia's headless mode.
7. Publish self-contained CLI and GUI builds for each target (`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-arm64`, and `osx-x64` if a runner or cross-build is available) and attach them to the run.
8. On a version tag, create a release with those builds.

**Local checks.** Cloud sessions install the .NET SDK through a SessionStart hook (`scripts/install-dotnet.sh`), so the CI steps can be mirrored locally before pushing. CI still covers the platforms a session cannot run.

## Format rollout

MP3 comes first, and each later phase starts only after the previous one passes its gate.

| Phase | Scope |
| --- | --- |
| 1 (v1) | MP1/2/3 and tags: all tags, side info, Xing, LAME, VBRI, reference tests |
| Gate 1 | Pipeline green everywhere, review findings fixed, abstractions settled |
| 2 | FLAC, WAV, AIFF, Ogg with Vorbis and Opus (Ogg hands streams to codec parsers) |
| Gate 2 | Pipeline green everywhere, review findings fixed |
| 3 | MP4 with AAC and ALAC, WavPack, Monkey's Audio, TTA |
| Gate 3 | Pipeline green everywhere, review findings fixed |
| 4 | Long tail: Musepack, WMA, AC-3; TAK and OptimFROG at header level only |
| Later, order not fixed | MP3 scalefactor and Huffman analysis; decoders for waveform and spectrogram, format by format |

A gate means the pipeline is green on every platform and the review agent's findings are resolved; the first gate also requires the shared contract to have settled. TAK and OptimFROG are poorly documented, so they stay at header level. The deeper MP3 pass and the decoders are not tied to a phase yet.

## CLI and GUI

Both front ends are thin views over the same result model, so neither can show something the other cannot.

**CLI**

- Analyze one file, several files or a whole folder tree (recursive, with a format filter).
- Output as readable text, JSON, or CSV for batch summaries; exit codes reflect whether findings of a given severity were found.
- Options to include or hide sections (tags, frames, side info, statistics) and to print byte offsets.

**GUI**

- Folder tree and file list using native OS dialogs; drag and drop; batch analysis in the background.
- Report view with the structure tree, where selecting a node highlights its bytes in a hex view.
- Findings list with severity, filterable and linked to the node that caused them.
- Graphs: bitrate over time, bitrate distribution, bit reservoir fill, all from header-level data and available in v1.
- Waveform and spectrogram panels that need decoded audio. These appear only for formats that have a decoder and otherwise say so.
- Export of the report as text or JSON.

## Risks and open questions

The largest risks are a wrong core contract and spec details that no reviewer will catch by eye; both are addressed by building MP3 first and by the verification stages above.

**Risks**

- **Core model reworked late.** If the result model or reader APIs are wrong, every format pays. MP3 goes first and the contract stays internal and changeable until the second format works.
- **Spec details wrong.** Field offsets and edge cases written from memory can be off; known-input fixtures and tool cross-checks are the safety net.
- **Decoders.** Quality, license and upkeep of managed decoders vary, and some formats (AAC, Musepack, Monkey's Audio) may have none. Waveform and spectrum are therefore optional per format.
- **Closed formats.** TAK and OptimFROG are poorly documented, so they get only what their headers reveal.
- **Heuristic encoder detection.** Files without LAME or Xing tags can be misidentified; results carry a confidence label.
- **Large inputs.** Multi-gigabyte files and 100,000-frame reports need streaming reads and a lazily loaded, virtualized tree in the GUI.

**Open questions**

- Whether an optional, not bundled `ffmpeg` fallback for decoding is acceptable.
