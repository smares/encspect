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
- **MediaInfoLib's `File_Tak.cpp`** (BSD, tag v26.01): a second reader of the metadata objects, used as evidence where the files depart from takStream.pas (below) and as a cross-check.
- **Not used:** FFmpeg's `takdec.c`, `tak.c` and `takdec` demuxer (LGPL), and the MultimediaWiki page on TAK (its sources are not stated).

#### Where the files depart from takStream.pas

The files win wherever they and takStream.pas disagree. Each departure is stated with its evidence, the fixtures pin it, and the implementer follows this list over the module.

- **An object's size counts its CRC.** takStream.pas says the 24-bit size covers "the Data (excluding header and Crc)". In `lostchord-tak_2_2_0.tak` the stream info states 13 bytes (10 of data and the 3-byte CRC-24, at offset 4), last frame info 11 (8 and 3, offset 21), encoder info 7 (4 and 3, offset 36), and each CRC-24 holds over the size less 3 bytes. MediaInfo's `Header_Parse` takes the element as the stated size and reads the CRC inside it (`Skip_L3("crc")`).
- **Wave data has one CRC, at the end.** takStream.pas puts a CRC right after the 6 size bytes (`tak_str_SimpleWaveDataSize`). The files hold the header size (3 bytes), the footer size (3), the header bytes, the footer bytes and one CRC-24 over all of them: 53 = 6 + 44 + 3 in both lostchord files. MediaInfo's `WAVEMETADATA` reads the same order.
- **The End object** (ID 0) is a header with size 0 and no CRC, in both files.
- **How the fixtures prove it:** every metadata CRC-24 of every Takc-written fixture and both real files must hold under this reading (a test, not only the goldens), and the SDK library opens every fixture without `tak_res_ssd_MetaDataDamaged`. A further departure that a fixture shows is added here with its evidence before the parser follows it.

### OptimFROG

- **OptimFROG 5.100** (2016-09-02, the latest; SDK 1.600) by Florin Ghido, with `ofr` (lossless), `off` (IEEE float), `ofs` (DualStream), the SDK library and its documentation.
  - Builds: Windows x86, x86+SSE2 and x64; Linux x86, x86+SSE2 and x64 (statically linked); OS X x86+SSE2 and x64; FreeBSD. Download page: http://losslessaudio.org/Downloads.php (HTTPS has a self-signed certificate).
  - Linux x64: http://losslessaudio.org/Downloads/A1C0/5100/Linux/OptimFROG_Linux_x64_5100.zip, SHA-256 `d45079f8121918acda221c062943facca9bddb8680e5055508add077d69751f4` (its published SHA-1 `e3dd9fd5…` matches).
  - License (`license.html`): "completely free for personal, and also for non-commercial and commercial use"; the unmodified release packages may be redistributed under their original names, and the author asks not to link to the packages directly but to the download page.
- **`SDK/Documentation/format.txt`**: the author's "compressed file format specification" for 4.5xx and later. It defines the blocks (`OFR `, `HEAD`, `COMP`, `TAIL`, `RECV`, `MD5 `, and ID3v1, ID3v2 and APEv2 in their places), the main block's fields, the encoder ID (version = ID >> 4 + 4500, build system = ID & 0xF) and the compression byte (mode = byte >> 3, speedup = byte & 7) with the names of each value. All rights reserved; a specification read as such.
- **The SDK header** (`OptimFROG.h`): `OptimFROG_getInfo` returns the version, `method` and `speedup` as text, the sample type and the channel configuration; the cross-check calls it as a black box.

### The clean-room question

- **No split is needed.** Every field read in this item comes from the authors' own material: takStream.pas, whose terms allow following it, and OptimFROG's published specification. Nothing comes from LGPL or GPL code.
- **The TAK parser follows takStream.pas** where the files agree with it, and the departures above where they do not.
  - Its notice, in the source and in [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md) as [CLAUDE.md](../../CLAUDE.md) asks of a permissive source followed closely, quotes "Copyright 2006 by Thomas Becker" and the terms, and states, as the terms ask of a published modified version, that Encspect's reader is not an official release of TAK.
  - The module is not committed: the scripts fetch the pinned archive into the cache, which honors "don't distribute this module without the SDK".
  - The implementer reads takStream.pas, the SDK documentation, MediaInfo's `File_Tak.cpp` and this design, and nothing else about TAK.
- **A TAK decoder would need a split.** The coded frames are described only by FFmpeg's LGPL decoder, so one agent would read it and describe the format, and another would implement from the description, as for TrueHD and TTA. That is outside 0.2 ([Not covered](#not-covered)).

## What the files show

Walked on the real files from samples.ffmpeg.org's archive (2026-11-09, by this design and its review); the fixtures confirm the rest.

- `lostchord-tak_2_2_0.tak`: `tBaK`; stream info (ID 1); last frame info (ID 7); encoder info (ID 4: version 2.2.0, preset 2, standard evaluation); wave data (ID 3: a 44-byte WAV header, no footer); the End object (ID 0). The audio starts at byte 108. Every CRC-24 holds.
- `lostchord-tak_1_0.tak`: `tBaK`; stream info with codec 0; a seek table (ID 2, 637 bytes, its CRC-24 holds); wave data; End. No encoder info object, as the SDK says of TAK 1.0 files. The audio starts at byte 723.
- `luckynight.ofr` (2004): `OFR ` with a main size of 15, encoder ID 0x0090 (version 4.509, build system 0, Windows x86), compression byte 0x0A (mode 1, normal; speedup 2, 4x), `HEAD` with the 44-byte WAV header, then 7 `COMP` blocks of 882,000 values (10 s of stereo at 44.1 kHz) each, the last shorter. Each block's CRC-32 is the plain IEEE (zlib) CRC-32 over the block's data after the CRC field, and all hold; the blocks' samples add up to the main block's total. The 2-byte reserved field is 0x1041 in every block.

## What is read and reported

### TAK

- **The object walk:** each object is an ID (7 bits), a reserved bit and a 24-bit size, then the data and its CRC-24 (OpenPGP's, RFC 2440: initial value 0xB704CE, polynomial 0x864CFB, stored little-endian, over the data); the End object (ID 0) has neither. Stream info must come first: the SDK's decoder signals damage otherwise (`tak.stream-info-not-first`). An unknown ID (8–127) is skipped by its size (`tak.object-unknown`, info); a reserved bit that is set is shown and is `tak.object-reserved-bit` (info).
- **Nodes,** for each object its header (ID, reserved bit, size) and CRC-24, compared:
  - stream info: codec (as Takc's readme and `tak_GetCodecName` name them: 0 "Integer 24 bit (TAK 1.0)", 1 "Experimental!", 2 "Integer 24 bit (TAK 2.0)", 3 "LossyWav (TAK 2.1 Beta)", 4 "Integer 24 bit MC (TAK 2.2)"), profile, frame size type and the frame length it gives, the sample count, data type, sample rate, bits per sample, channels, the extension flag and, where it is set (2.2.0 and later), valid bits and speaker assignment. Before 2.2 the channel count had 5 bits and no extension flag; the files hold at most 16 channels, so the top bit of the old field reads as the flag, 0, and one reader serves both;
  - the obsolete seek table (before 1.1.1): one node with its size and CRC-24. Nothing at hand describes its entries beyond "a header and a list of seek points", so they are not read;
  - wave data: the header and footer sizes, the stored WAV header data and terminating data (named as Monkey's Audio names them) with the header's chunks listed, and a lossyWAV `fact` chunk read with `FactText`;
  - encoder info: the version (major in the top byte), the preset and the evaluation;
  - padding (ID 5), MD5 (ID 6, shown, not compared), last frame info (ID 7: the last frame's position, counted from the first frame, which follows the End object, and its size);
  - End;
  - the audio data as one node from the end of the metadata to the stream's end; frames are not walked.
- **The frame length** follows takStream.pas's rule for the frame size type: a time type (94, 125, 188 or 250 ms) gives the rate times the duration, a size type its samples. A time type that comes to more than 16,384 samples, or a size type that lasts more than 250 ms, is a stream info the decoder rejects (`tak.frame-size-invalid`); types 10 to 15 are reserved or "not supported yet" (`tak.frame-size-type-reserved`).
- **StreamInfo:** codec "TAK", sample rate, channels, bits per sample, samples, duration, average bitrate (the audio data's bytes over the duration).
- **Channel layout,** in the words of TTA and Monkey's Audio: TAK's speaker assignment where the stream info stores one; else the nonzero channel mask of the stored WAV header (`ChannelLayouts.FromWaveMask`, as Monkey's Audio takes it); else one channel is center, two are left/right, and more have no layout. The "Channel Layout" statistic shows it.
- **Statistics:** Codec, Profile, Preset, Frame Length, Channel Layout, and "Verified Header Checksums" and "Failed Header Checksums" over the metadata objects (the unit "Header", as Musepack SV8 names the CRC of its stream header; [development.md](../development.md#names-of-statistics-and-series) gains the line).
- **Claims:**
  - Encoder "TAK 2.3.3" (`ClaimRole.Encoder`, high confidence) from the encoder info object.
  - A file without the object is "TAK 1.0" with medium confidence and the stream info as evidence: the SDK's decoder takes 1.0.0 as the version of such a file, as Monkey's Audio names a file before 3.98 from its file version.
  - Processor lossyWAV from a `fact` chunk in the wave data, high confidence, the shared path of [lossywav-detection.md](lossywav-detection.md).
  - Codec 3 makes no claim of its own (decision 9).
- **The preset**, named as Takc prints it ("-p2", "-p3e", "-p4m"; "p" and the level, then "e" for Extra or "m" for Max). Without the encoder info object, the preset is the stream info's profile and the evaluation is unknown, as the SDK tells applications to show it. The profile is shown on its own: the SDK says it equals the preset only for the presets' standard settings (a Turbo preset with 128 predictors stores the High profile).
- **Declared end:** the end of the last frame by the last frame info object (TAK 1.1.1 and later), used only when its CRC-24 holds, as TTA uses its seek table only when its CRC holds; the APEv2 tag after it is the host's. A declared end past the data is a file cut in the audio (`tak.audio-cut`, with the bytes missing), the most common damage. Without the object, or with a failing CRC, no declared end, and trailing tags are looked for as in a file with no declared size.
- **A tag before `tBaK`** (ID3v2) is the host's, as in every format, with `tak.leading-tag` (info): TAK's readme says its decoder throws an error on tags prepended to the file.

### OptimFROG files

- **Nodes:** every block by its ID and length, in the order of the specification's layout rule:
  - main block: total samples (all channels: the specification counts single values, so samples per channel are the total over the channels), sample type (the 11 names of appendix A), channel configuration (mono, stereo left/right), sample rate, encoder ID with version and build system, compression with mode and speedup, and, where the main size is over 15, `minDecoderVersion` (stored less 4500, so 600 is 5.100) and any further bytes as unknown fields, skipped as the specification asks;
  - `HEAD` and `TAIL`: the stored WAV header data and terminating data, named as Monkey's Audio names them, with the header's chunks listed and lossyWAV's `fact` chunk read with `FactText`. A `HEAD` that does not start with `RIFF` (`ofr --raw --headersize`) is shown as bytes with no chunk listing;
  - each `COMP` block: its CRC-32, compared (the IEEE CRC-32 of zlib, over the block's data after the CRC field, as `luckynight.ofr` shows), its sample count, sample type, channel configuration, the 2-byte field "reserved for internal use" (shown, never flagged: `luckynight.ofr` has 0x1041 in every block) and encoder ID;
  - `MD5 ` (shown, not compared), `RECV` (shown, not interpreted), unknown blocks by ID and length.
- **Checks across blocks:** a `COMP` block whose sample type, channel configuration or encoder ID differs from the main block's (`optimfrog.block-differs`); the blocks' samples not adding up to the main block's total (`optimfrog.samples-mismatch`, which also catches a file cut at a block boundary or a missing block); a total the channels do not divide; a missing `HEAD` or `TAIL`, which the layout rule does not bracket as optional (`optimfrog.head-missing`, `optimfrog.tail-missing`); a block out of the layout rule's order.
- **Undefined values** are named as such with an info finding: sample types above 10, channel configurations above 1, build systems 9 to 14 (15 is "unknown"), modes 15 to 31, speedups 3 to 7.
- **StreamInfo:** codec "OptimFROG lossless" (named as WavPack's codecs are, beside the DualStream codecs below), sample rate, channels, bits per sample (32 for the float types), samples, duration, average bitrate.
- **Channel layout,** in the words of TTA and Monkey's Audio: the channel configuration gives one channel center and two left/right; a nonzero channel mask in the stored WAV header sets the layout where it is there, as in Monkey's Audio.
- **Statistics and series,** under the names TTA and Monkey's Audio use ([development.md](../development.md#names-of-statistics-and-series)): Mode, Speedup, Sample Type, Build System, Channel Layout, "Blocks", "Verified Block Checksums" and "Failed Block Checksums", the smallest and largest block as WavPack names them, "Minimum Bitrate" and "Maximum Bitrate", "Bitrate Mode" and "Bitrate Distribution", and the series "Bitrate" (per block) and "Bitrate Over Time" (per second), as TTA draws them per frame.
- **Claims:** Encoder "OptimFROG 5.100" (high confidence) from the encoder ID; processor lossyWAV as above.
- **The mode, not a preset.**
  - The mode is informational: the changelog of 4.506 says the encoder "now writes the exact compression mode used in the informational purpose field of the header (instead of just extra for the extranew mode)", so before 4.506 "extra" can mean extranew. The description says so.
  - 5.100's `ofr --help` accepts 19 mode names (10 listed, 9 "also available"); its four `-light` modes are not in the specification's table, so the fixtures run all 19 and the article states which ones the stored mode cannot tell apart.
  - The presets 0 to 10 and max each set a mode, `--optimize` and `--aca`/`--acm` (the preset table of `ofr --help`, which the article cites); only the mode is stored, so the report names the mode and the speedup, and no preset (decision 5).
- **The block walk** never stops on a tag's signature ([CLAUDE.md](../../CLAUDE.md): never a signature match alone), by the rule DTS-HD's chunk walk follows ([dtshd.md](../formats/dtshd.md)):
  - a block header is four printable ASCII bytes and a length that ends inside the data;
  - a block of a known ID whose length runs past the data is a cut block (`optimfrog.block-cut`), read as far as it goes, and ends the walk;
  - a block of an unknown ID goes on the walk only when a valid block header follows it or it ends exactly at the end of the data (`optimfrog.block-unknown`, info); otherwise the walk stops before it;
  - a length that does not fit ends the walk with a finding and no guess at where the next block starts (no resync).
- **Declared end:** the end of the last block the walk accepts. The bytes after it go to the host's search for trailing tags (APEv2 with or without its header, Lyrics3, ID3v1), and bytes that are no tag are `optimfrog.data-after-blocks`. The specification lists ID3v1 and APEv2 among its blocks, but they are the host's, as in every other format; a tag-like run of bytes inside a `COMP` block is no tag (`tag.near-miss`).

### Detection

- **TAK:** `tBaK` at the start of the data (after leading tags) and a stream info object whose CRC-24 holds is certain (100); the signature with an object header of ID 1 and a plausible size but a failing CRC is likely (80); the signature alone is possible (70).
- **OptimFROG:** `OFR ` with a main size of at least 15, a sample type of 0–10, a known channel configuration, a sample rate above 0, and a block with a known ID after the main block is certain (100); the main block alone with valid fields is likely (80); the four bytes alone are possible (70).
- **Neither** is matched further into the data.

### Findings

- **Both:** a CRC that fails (each TAK object; each OptimFROG `COMP` block, once with a count), data cut in a metadata object or block, and data after the declared end that is no tag.
- **TAK,** beside the walk's codes above, each field a value takStream.pas does not define: a codec it does not list, a frame size type of 10 to 15 or one that does not fit the rate, a data type of 1 to 7 (only 0, PCM, is defined), a sample bits code outside the defined range, a channel count over 16, an evaluation of 3, a speaker code of 19 to 63; a stream info that is not first; a last frame past the data (`tak.audio-cut`); a missing End object; an object whose size runs past the data.
- **OptimFROG:** the codes of the walk, the checks across blocks and the undefined values in [OptimFROG files](#optimfrog-files).
- Each finding code gets a test or a manifest entry, as `FindingCoverageTests` asks.

### Verification

- **Compared:** TAK's metadata CRC-24s (header only, never covering the audio); OptimFROG's `COMP` CRC-32s, which cover the coded data when every block the samples call for is there.
- **Declared and not compared,** as [CLAUDE.md](../../CLAUDE.md) asks of every checksum a stream stores:
  - both MD5s over the decoded audio, which needs a decoder; the verification says so, as for a stream whose decoder is missing;
  - TAK's frame header CRC-24 and frame data CRC-24 (takStream.pas, "Audio frames"), since frames are not walked in 0.2 (decision 3).
- **OptimFROG's MD5** of 5.001 and earlier does not follow RFC 1321 for audio of 56 to 63 bytes modulo 64 (format.txt); the MD5 node's description and the article say so.

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
- **Hand-built** (`make_optimfrog_fixtures.py`, from the committed files):
  - a failing `COMP` CRC-32, a file cut in a block, a block length past the data (the walk stops), an unknown block followed by a valid one and an unknown block at the end;
  - tags after the blocks: APEv2 with its header, APEv2 footer only, APEv2 and ID3v1, Lyrics3 and ID3v1, and junk that is no tag (`optimfrog.data-after-blocks`);
  - tag-like bytes (`APETAGEX`, `TAG`) inside a `COMP` block (`tag.near-miss`).

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
