# TAK and OptimFROG at header level

Status: draft for the lead, 2026-11-09. Milestone 0.2, item 4 of the [plan](../plan.md#02-lossless-complete) and [planned.md](../formats/planned.md#in-02-header-level-only). Nothing here is signed off; the open decisions are at the end.

## Scope

- **In:** detection of TAK (`.tak`) and OptimFROG (`.ofr`, and `.off` for IEEE float) files; their headers and metadata; the encoder's version and the settings the header stores, named as Monkey's Audio's compression level is; the stored WAV header with lossyWAV's `fact` chunk; the checksums the container defines.
- **Out:** decoding, measurements and coding graphs of either format (no public description of the coded audio exists for OptimFROG, and for TAK only FFmpeg's LGPL decoder describes it); see [Not covered](#not-covered).

## Sources and licenses

### TAK

- **TAK 2.3.3** (2022-06-30, the latest), one archive with the GUI (`Tak.exe`), the command-line encoder (`Takc.exe`), the decoder library (`tak_deco_lib.dll`, x86 and x64) and the SDK.
  - Address: http://www.thbeck.de/Download/TAK_2.3.3.zip, linked from the author's page http://www.thbeck.de/Tak/Tak.html. The HTTPS address fails (a TLS alert from the server), so the download is HTTP, held by its SHA-256 `40d26992266f1377d576b88b84b88d2ab256dc321e47f90314402a80692aba6a` (1,765,343 bytes, fetched 2026-11-09).
  - License of the applications, the library and the SDK: freeware "only for private, non-commercial use", "also free for educational and scientific use"; commercial use needs the author's written approval; the intact archive may be redistributed without a fee. Windows only.
- **`Sdk/Source/takStream.pas`**, in the SDK: the author's own source of TAK's container ("Hopefully anything needed to understand, how TAK's container works", version 2.2.0). Its license: "Do with it, whatever you want", asking only that the module is not distributed without the SDK and that a modified version says it is not official. It defines every metadata object, the stream info's bit layout, the frame header, the frame size types and the CRC ("Any CRC used in the stream is CRC 24 RFC2440", OpenPGP's CRC-24).
- **The SDK documentation** (`Sdk/Doc/*.html`, `Sdk/Header/tak_deco_lib.h`): the meaning of the fields (`Ttak_str_EncoderInfo`: codec 0–63 and profile; `Ttak_str_MetaEncoderInfo`: version, preset, evaluation; the frame size types; `tak_SSD_GetEncoderInfo`). It is all rights reserved; it is read as documentation and nothing is copied from it.
- **Not used:** FFmpeg's `takdec.c`, `tak.c` and `takdec` demuxer (LGPL), and the MultimediaWiki page on TAK (its sources are not stated). Neither is needed: takStream.pas covers everything the header holds.

### OptimFROG

- **OptimFROG 5.100** (2016-09-02, the latest; SDK 1.600) by Florin Ghido, with `ofr` (lossless), `off` (IEEE float), `ofs` (DualStream), the SDK library and its documentation.
  - Builds: Windows x86, x86+SSE2 and x64; Linux x86, x86+SSE2 and x64 (statically linked); OS X x86+SSE2 and x64; FreeBSD. Download page: http://losslessaudio.org/Downloads.php (HTTPS has a self-signed certificate).
  - Linux x64: http://losslessaudio.org/Downloads/A1C0/5100/Linux/OptimFROG_Linux_x64_5100.zip, SHA-256 `d45079f8121918acda221c062943facca9bddb8680e5055508add077d69751f4` (its published SHA-1 `e3dd9fd5…` matches).
  - License (`license.html`): "completely free for personal, and also for non-commercial and commercial use"; the unmodified release packages may be redistributed under their original names, and the author asks not to link to the packages directly but to the download page.
- **`SDK/Documentation/format.txt`**: the author's "compressed file format specification" for 4.5xx and later. It defines the blocks (`OFR `, `HEAD`, `COMP`, `TAIL`, `RECV`, `MD5 `, and ID3v1, ID3v2 and APEv2 in their places), the main block's fields, the encoder ID (version = ID >> 4 + 4500, build system = ID & 0xF) and the compression byte (mode = byte >> 3, speedup = byte & 7) with the names of each value. All rights reserved; a specification read as such.
- **The SDK header** (`OptimFROG.h`): `OptimFROG_getInfo` returns the version, `method` and `speedup` as text, the sample type and the channel configuration; the cross-check calls it as a black box.

### The clean-room question

- **No split is needed.** Every field read in this item comes from the authors' own material: takStream.pas, whose terms allow following it, and OptimFROG's published specification. Nothing comes from LGPL or GPL code.
- **The TAK parser follows takStream.pas** and carries its notice (the author, the terms quoted) in the source and in [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md), as [CLAUDE.md](../../CLAUDE.md) asks of a permissive source followed closely. The module is not committed: the scripts fetch the pinned archive into the cache, which honors "don't distribute this module without the SDK". The implementer reads takStream.pas and the SDK documentation and nothing else about TAK.
- **A TAK decoder would need a split.** The coded frames are described only by FFmpeg's LGPL decoder, so one agent would read it and describe the format, and another would implement from the description, as for TrueHD and TTA. That is outside 0.2 ([Not covered](#not-covered)).

## What the files show

Checked on real files from samples.ffmpeg.org's archive (first bytes only, 2026-11-09); the fixtures confirm the rest.

- `lostchord-tak_2_2_0.tak`: `tBaK`, a stream info object (ID 1), then an encoder info object (ID 4, 4 data bytes and the CRC-24) holding version 2.2.0 and preset byte 0x02 (preset 2, standard evaluation), then a wave data object (ID 3) with a 44-byte WAV header and no footer.
- `lostchord-tak_1_0.tak`: the same stream info, and no encoder info object, as the SDK says of TAK 1.0 files.
- `luckynight.ofr` (2004): `OFR ` with a main size of 15, encoder ID 0x0090 (version 4.509, build system 0, Windows x86), compression byte 0x0A (mode 1, normal; speedup 2, 4x), then `HEAD` with the 44-byte WAV header.

## What is read and reported

### TAK

- **Nodes:** the signature; each metadata object with its header (ID, size) and CRC-24, compared:
  - stream info: codec (0 "Integer 24 bit (TAK 1.0)", 2 "Integer 24 bit (TAK 2.0)", 3 "LossyWav (TAK 2.1 Beta)", 4 multichannel (TAK 2.2), as `tak_GetCodecName` names them), profile, frame size type and the frame length it gives, the sample count, data type, sample rate, bits per sample, channels and, from 2.2.0, valid bits and speaker assignment;
  - the obsolete seek table (before 1.1.1): its entries;
  - wave data: the stored WAV header and footer, the header's chunks listed, a lossyWAV `fact` chunk read with `FactText` as in Monkey's Audio;
  - encoder info: the version, the preset and the evaluation;
  - padding, MD5 (shown, not compared), last frame info (position and size);
  - the audio data as one node from the end of the metadata to the stream's end; frames are not walked.
- **StreamInfo:** codec "TAK", sample rate, channels and layout (the speaker assignment where stored, else the WAVE default for the count), bits per sample, samples, duration, average bitrate (the audio data's bytes over the duration).
- **Statistics:** Codec, Profile, Preset, Frame Length.
- **Claims:**
  - Encoder "TAK 2.3.3" (`ClaimRole.Encoder`, high confidence) from the encoder info object.
  - A file without the object (TAK 1.0) is "TAK (format of TAK 1.0)" with medium confidence and the stream info as evidence, as Monkey's Audio names a file before 3.98.
  - Processor lossyWAV from a `fact` chunk in the wave data, high confidence, the shared path of [lossywav-detection.md](lossywav-detection.md).
- **The preset**, named as Takc prints it ("-p2", "-p3e", "-p4m"; "p" and the level, then "e" for Extra or "m" for Max). Without the encoder info object, the preset is the stream info's profile and the evaluation is unknown, as the SDK tells applications to show it. The profile is shown on its own: the SDK says it equals the preset only for the presets' standard settings (a Turbo preset with 128 predictors stores the High profile).
- **Declared end:** with the last frame info object (TAK 1.1.1 and later), the end of the last frame; the APEv2 tag after it is the host's. Without it, no declared end.

### OptimFROG

- **Nodes:** every block by its ID and length, in the order of the specification's layout rule:
  - main block: total samples (all channels: the specification counts single values, so samples per channel are the total over the channels), sample type (the 11 names of appendix A), channel configuration (mono, stereo left/right), sample rate, encoder ID with version and build system, compression with mode and speedup, and `minDecoderVersion` where the main size is over 15 (5.100);
  - `HEAD` and `TAIL`: the stored WAV header and footer, the header's chunks listed, lossyWAV's `fact` as above;
  - each `COMP` block: its CRC-32 compared over the data after it, its sample count, sample type, channel configuration and encoder ID (open decision 4);
  - `MD5 ` (shown, not compared), `RECV` (shown, not interpreted), unknown blocks by ID and length.
- **StreamInfo:** codec "OptimFROG", sample rate, channels, bits per sample (32 for the float types), samples, duration, average bitrate; with decision 4 the bitrate per block as a series under the shared names of [development.md](../development.md#names-of-statistics-and-series).
- **Statistics:** Mode, Speedup, Sample Type, Build System.
- **Claims:** Encoder "OptimFROG 5.100" (high confidence) from the encoder ID; processor lossyWAV as above.
- **The mode, not a preset.** 5.100's presets 0 to 10 and max each set a mode, `--optimize` and `--aca`/`--acm`; only the mode is stored. The report names the mode and the speedup; the article lists which presets select each mode. No preset is claimed (decision 5).
- **Declared end:** the end of the last block before an APEv2 or ID3v1 tag, which the specification places last.

### Detection

- **TAK:** `tBaK` at the start of the data (after leading tags) and a stream info object whose CRC-24 holds is certain (100); the signature with an object header of ID 1 and a plausible size but a failing CRC is likely (80); the signature alone is possible (70).
- **OptimFROG:** `OFR ` with a main size of at least 15, a sample type of 0–10, a known channel configuration, a sample rate above 0, and a block with a known ID after the main block is certain (100); the main block alone with valid fields is likely (80); the four bytes alone are possible (70).
- **Neither** is matched further into the data.

### Findings

- Both: a CRC that fails (each TAK object; each OptimFROG `COMP` block, once with a count), data cut in a metadata object or block, an unknown codec, sample type, channel configuration, mode or speedup, an object or block that breaks the specification's order, a total sample count that the channels do not divide (OptimFROG), and data after the declared end that is no tag.
- Each finding code gets a test or a manifest entry, as `FindingCoverageTests` asks.

### Verification

- **Compared:** TAK's metadata CRC-24s (header only, never covering the audio); OptimFROG's `COMP` CRC-32s, which cover the coded data when every block the samples call for is there.
- **Declared and not compared:** both MD5s over the decoded audio, which needs a decoder; the verification says so, as for a stream whose decoder is missing.

## Fixtures

### TAK, on Windows

- **Workflow** `.github/workflows/tak-fixtures.yml`, by hand or by a push to `fixtures/tak`, on `windows-2025`, as [WMA fixtures](../development.md#wma-fixtures-from-microsofts-encoders) are made: `scripts/fixtures/windows/make_windows_tak_fixtures.ps1` fetches the pinned archive, encodes signals the fixture scripts write, and uploads the files with their records; `import_windows_tak_fixtures.py` copies them to `tests/fixtures/tak` with a `.provenance.json` each.
- **Settings matrix** (0.25 s signals, to keep files small):
  - presets `-p0` to `-p4`, each standard, `e` and `m` (15 files), 16-bit stereo 44.1 kHz;
  - formats at `-p2`: 8-bit mono, 24-bit stereo, 96 kHz, 5.1 with a WAVE_FORMAT_EXTENSIBLE mask (codec 4 and the speaker assignment), 6 channels without a mask;
  - options: `-fsl512` and `-fsl16384` (frame size types), `-md5`, `-wm0` (no wave data), pipe encoding with and without `-ihs`;
  - a lossyWAV 1.4.2 output at `-p2 -fsl512` (TAK's readme recommends 512 for lossyWAV), its `fact` chunk in the wave data.
- **Records:** for each file, `Takc -fi -fim0` (codec, encoder version and preset, frame duration, wave data sizes, MD5) and the SDK library's `tak_SSD_GetStreamInfo_V22` and `tak_SSD_GetEncoderInfo` through a P/Invoke from PowerShell (the profile, which `-fi` does not print). Both are black boxes.
- **Hand-built** (`make_tak_fixtures.py`, from the committed files): a failing CRC-24 in stream info and in encoder info, a file cut in the metadata, an APEv2 tag after the last frame, a file without the last frame info object.
- **Real files** (`check_tak_samples.py`, pinned, not committed): `lostchord-tak_1_0.tak` and `lostchord-tak_2_2_0.tak` from samples.ffmpeg.org, TAK 1.0 (no encoder info) and 2.2.0. No other TAK version can be downloaded from the author.

### OptimFROG, on Linux

- **Tool** `optimfrog_tool.py`: the pinned Linux x64 package through `downloads.py`, with a Wayback fallback and a copy in encspect-private (the license allows redistributing the unmodified package). The binaries are static; no build.
- **Generator** `make_external_optimfrog_fixtures.py`, rerun in CI, which checks the files are reproducible byte for byte:
  - `ofr --preset 0` to `10` and `max`, and `--mode` with each of the 15 named modes once (this settles how the `-light` modes are stored, which the specification does not list), 16-bit stereo 44.1 kHz;
  - formats at the default preset: 8-bit unsigned mono, 24-bit stereo, 32-bit integer, `--md5`, a WAV with an extra chunk before the data and a trailing chunk (`HEAD` and `TAIL`), a lossyWAV output;
  - `off` on a 32-bit float WAV;
  - `ofs` at two bitrates, read only if the files have the specification's layout (decision 6).
- **Speedup** was deprecated in a 4.5xx release and is not offered by 5.100, so files with a speedup other than 1x come only from older encoders: `luckynight.ofr` (4.509, 4x) as a real file (`check_optimfrog_samples.py`, pinned, not committed), and optionally the Linux amd64 4.600ex build from the previous releases page.
- **Hand-built** (`make_optimfrog_fixtures.py`): a failing `COMP` CRC-32, a file cut in a block, an unknown block, an APEv2 and ID3v1 tag after the blocks.

## Cross-checks

- **TAK** (`crosscheck_tak_fixtures.py`, Linux):
  - ffprobe 8.0.1: sample rate, channels, bit depth and sample count (FFmpeg's demuxer as a black box);
  - MediaInfo 26.01: format, sample rate, bit depth, duration, `Encoded_Library` ("TAK 2.3.3") and `Encoded_Library_Settings` ("-p2e"), which `File_Tak.cpp` (BSD) reads from the encoder info object. It reads the channel count as one bit (mono or stereo), so the multichannel files are listed as read otherwise, with that line as evidence;
  - the Windows records above: codec, profile, preset and evaluation, version, frame length.
- **OptimFROG** (`crosscheck_optimfrog_fixtures.py`, Linux):
  - the SDK library `libOptimFROG.so` called through ctypes (`OptimFROG_getInfo`: channels, sample rate, bits per sample, version, method, speedup, sample type, channel configuration, samples), and `ofr --info`;
  - ffprobe and MediaInfo do not read OptimFROG (FFmpeg has no demuxer; MediaInfoLib 26.01 has no parser for it), so both are asserted to find no audio stream.
- Both jobs run under the path filter of their format, once per change.

## Docs and articles

- `docs/formats/tak.md` and `docs/formats/optimfrog.md` in the usual sections; their lines in the formats index and the README; [planned.md](../formats/planned.md) and the plan updated when each lands.
- Articles in `src/Encspect.Host/Topics`: `tak-metadata` (the objects and their CRC-24), `tak-presets` (preset, evaluation and profile), `optimfrog-blocks`, `optimfrog-modes` (mode, speedup and the presets that select each mode). The `lossywav` article gains the two formats.
- [development.md](../development.md): the fixture sections, the tools, the pinned sources.

## Not covered

- **Decoding** of either format, so no MD5 comparison, measurement, entropy figures or coding graphs.
  - OptimFROG's coded data has no public description at all; only the closed library decodes it.
  - A TAK decoder needs the clean-room split above; it goes under "Not planned", brought back if the lead wants TAK measured.
- **TAK frames** (decision 3): frame count, frame CRC-24s, the bitrate per frame.
- **OptimFROG before 4.5xx**, whose layout the specification does not cover; such a file is not matched.
- **DualStream (`ofs`) files** beyond their header, unless decision 6 says otherwise.
- **The meaning of OptimFROG's float sample types** beyond their names (`FLOAT32_0`, `_16`, `_24`): the specification gives none.
- **`RECV`** recovery data: shown as a block, not interpreted.

## Open decisions for the lead

1. **TAK's source:** follow takStream.pas directly, with its notice, and no clean-room split. Recommended: yes; its terms allow it, and FFmpeg is not needed for the header.
2. **TAK's license for CI:** run Takc.exe on a Windows runner to write fixtures for a non-commercial MIT project, commit only its output, keep the archive in the cache and in encspect-private. Recommended: yes; the terms allow private, non-commercial and educational use, and the archive is redistributable intact.
3. **TAK frames in 0.2:** walk frames by sync word and header CRC to count them, compare their CRC-24s and draw the bitrate per frame, or stay at the metadata. Recommended: metadata only in 0.2, as the plan says; the frame walk queued in 0.4's "small separate items", since bitrate over time is then owed to TAK by the one-concept rule.
4. **OptimFROG's `COMP` blocks:** walk them, compare their CRC-32s and draw the bitrate per block. Recommended: yes; the blocks are length-prefixed and specified, so this is container reading, cheap, and gives the coded data a verification.
5. **OptimFROG's preset:** report mode and speedup only. Recommended: yes; the presets also set options the header does not store, so naming a preset would be a guess.
6. **OptimFROG's other encoders:** `off` read as OptimFROG (the same layout, float sample types); `ofs` read at header level if its files follow the specification's layout, otherwise only named. Recommended: yes to both.
7. **Download addresses:** both sites serve the archives over HTTP only; pin SHA-256, add Wayback and encspect-private fallbacks, and link OptimFROG's download page (not the package) in the docs, as its author asks. Recommended: yes.

## Pull requests, in order

1. **This design**, signed off.
2. **OptimFROG:** `optimfrog_tool.py` and the fixtures, the parser project `Encspect.Formats.OptimFrog`, host registration, goldens, finding coverage, the cross-check job, docs and articles. First because the specification is public and the encoder runs on Linux.
3. **TAK fixtures workflow:** the Windows workflow and importer, run once, the files and records committed.
4. **TAK:** the parser project `Encspect.Formats.Tak` following takStream.pas, the notice, host registration, goldens, finding coverage, the cross-check job, docs and articles; the plan's item 4 marked done.
