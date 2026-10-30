# MPEG audio: clean-room rules

Status: the description for a clean-room rewrite, written on 2026-10-29 at 6da7884, and the rewrite it was written for: all four items are implemented from it on branch `feature/cleanroom-mpeg-layer2`, and section 1 gained the second reading of `dyn_cross_LR` after the independent review. A license audit found four pieces of MPEG code and test tooling that follow LGPL code (mctoolamed, mp3guessenc, FFmpeg, mpg123) or copy getID3's table closely; they were removed from the tree, and this document is what the rewrite was built from. Each section gives the problem, the inputs and outputs, the behavior the rewrite must have, where a specification covers it, and the tests that pin it.

The four items:

1. The bit allocation of the MPEG-2 multichannel extension in Layer II (`Layer2Multichannel.cs`: `Allocate` and the tables `Shapes`, `Predictors`, `Groups`; `make_layer2_fixtures.py`: `MC_SHAPE`, `PREDICTORS`).
2. The choice of the Layer II allocation table (`Layer2AudioData.TableFor`, `make_layer2_fixtures.py` `table_for`).
3. The table of LAME 3.90 to 3.92 preset settings (`VbrHeader.cs`: `OldPresets`, `OldPreset`; `vbr_tags.py`: `OLD_PRESETS`, `old_preset`).
4. The models of mpg123's and mp3guessenc's printed output in `crosscheck_mpeg_fixtures.py` (`expected_mpg123`, `expected_guessenc`, `guessenc_strategy`, `guessenc_preset`, `guessenc_lame_recognized`, `GUESSENC_MODES_BY_MISC`, `frames_after_tag`, and the Quality special cases that `check_vbr_header` held).

No copy of ISO/IEC 11172-3 or 13818-3 is in the repository or was available while writing this. Where this document names a clause or table, the name is the one the repository already cites or the syntax element name; clause numbers for 13818-3 were not checked against the text and must be confirmed when a copy is at hand. Everything here was checked against the black-box evidence named in each section.

## 1. Multichannel bit allocation

### What it does

An MPEG-1 Layer II frame can carry the MPEG-2 multichannel extension (ISO/IEC 13818-3) after its samples. `Layer2Multichannel.Read` (not part of this rewrite) reads the multichannel header, the CRC and the composite status information. It then reads, per subband, the bit allocation of the additional transmission channels T2 to T4, then their scfsi. The multichannel CRC covers these fields, so the analyzer can only verify a frame when it reads exactly as many allocation and scfsi fields as the encoder wrote.

The rewrite supplies, for each subband, which allocations of T2, T3 and T4 the bitstream carries and which are copied or absent. Inputs per subband:

- The configuration: `center` (0 none, 1 present, 2 reserved, 3 phantom) and `surround` (0 none, 1 mono, 2 stereo, 3 second stereo programme). Center values 1 and 3 count as a coded center; 0 and 2 as none.
- The subband's group (see the table below) and that group's `tc_allocation`, `dyn_cross_mode` and `dyn_second_stereo`, plus the frame's `dyn_cross_LR`. Without dynamic crosstalk (`dyn_cross_on` 0) every mode and every `dyn_second_stereo` is 0.
- The allocation index of T0 (left base channel) and T1 (right base channel) in the subband, from the base frame; 0 where the base frame has fewer subbands.
- A way to read one allocation field of the subband's width; each field read is recorded as a node.

Output: an allocation index for T2, T3 and T4 in the subband, and the fields read, in the order T2, T3, T4. The scfsi that follows is read for every additional channel whose index is not 0, copied or read alike (that loop is in `Read` and stays).

### Configuration parameters

The standard fixes, per configuration, how many additional channels there are and how many bits `tc_allocation` and `dyn_cross_mode` take (13818-3, the syntax of `mc_composite_status_info`). The rewrite needs this table in place of `Shapes`:

| surround | center | Configuration | Additional channels | `tc_allocation` bits | `dyn_cross_mode` bits |
|---|---|---|---|---|---|
| 0 | none | 2/0 | 0 | 0 | 0 |
| 0 | coded | 3/0 | 1 (T2) | 2 | 1 |
| 1 | none | 2/1 | 1 (T2) | 2 | 1 |
| 1 | coded | 3/1 | 2 (T2, T3) | 3 | 3 |
| 2 | none | 2/2 | 2 (T2, T3) | 2 | 3 |
| 2 | coded | 3/2 | 3 (T2 to T4) | 3 | 4 |
| 3 | none | 2/0 + 2/0 | 2 (T2, T3) | 0 | 0 |
| 3 | coded | 3/0 + 2/0 | 3 (T2 to T4) | 2 | 1 |

`dyn_second_stereo` (one bit per group) is present only with surround 3, after each group's `dyn_cross_mode`.

### Subband groups

Twelve subband groups (13818-3 defines them for `tc_allocation`, `dyn_cross_mode` and prediction): subbands 0 to 7 are groups 0 to 7, subbands 8 and 9 group 8, 10 and 11 group 9, 12 to 15 group 10, and 16 and above group 11.

### Prediction coefficients

`Read` reads, for each of the first eight groups with `mc_prediction` set, a number of 2-bit `predsi` fields that depends on the configuration and the group's `dyn_cross_mode` (the standard's number of predictors per subband group). The table, by mode 0, 1, 2 and so on (later modes 0):

| Configuration | Fields by `dyn_cross_mode` |
|---|---|
| 3/2 | 6, 4, 4, 4, 2, 2, 2, 0, 2, 2, 2, 0, 0, 0, 0, 0 |
| 3/1, 2/2 | 4, 2, 2, 0, 0, 0, 0, 0 |
| 3/0, 2/1, 3/0 + 2/0 | 2, 0 |
| 2/0, 2/0 + 2/0 | 0 |

A cross-check of the table: it is two per additional channel that carries its own samples (by the mode tables below) and is not itself the source of another additional channel's copy; the second stereo programme is never predicted.

### What each transmission channel carries

`tc_allocation` names what T2 to T4 carry (13818-3, semantics of `tc_allocation`, a table per configuration). The rewrite needs it only to decide where a channel that dynamic crosstalk leaves out copies its allocation from.

| Configuration | `tc_allocation`: T2, T3, T4 |
|---|---|
| 3/2 | 0: C, Ls, Rs; 1: L, Ls, Rs; 2: R, Ls, Rs; 3: C, L, Rs; 4: C, Ls, R; 5: C, L, R; 6: R, L, Rs; 7: L, Ls, R |
| 3/1 | 0: C, S; 1: L, S; 2: R, S; 3: C, L; 4: C, R; 5: L, R; 6 and 7: not assigned |
| 2/2 | 0: Ls, Rs; 1: L, Rs; 2: Ls, R; 3: L, R |
| 3/0, 3/0 + 2/0 | T2: 0: C; 1: L; 2: R; 3: not assigned (T3 and T4 are the second programme) |
| 2/1 | T2: 0: S; 1: L; 2: R; 3: not assigned |

### Allocation rules

Common to every mode:

- With phantom coding (center 3), T2 has no allocation in subbands 12 and above: its index is 0 and nothing is read, whatever the mode. A channel that copies T2 there copies 0.
- A "read" channel reads one field of the subband's width; fields are read in channel order T2, T3, T4.
- "Copies Tn" means the channel takes the allocation index Tn has in the same subband (for T2 and T3 the value just decided).

Mode 0 (every group without dynamic crosstalk, and groups whose mode is 0):

- Every additional channel is read.
- With surround 3 and the group's `dyn_second_stereo` 1, the second programme's right channel copies its left channel instead of being read: with a coded center T4 copies T3, without one T3 copies T2.

Configurations with a 1-bit mode (3/0, 2/1, 3/0 + 2/0), mode 1:

- T2 is not read; it copies a base channel by the source rule below.
- With 3/0 + 2/0, T3 is read, and T4 copies T3 when `dyn_second_stereo` is 1, otherwise it is read.

Configurations with a 3-bit mode (3/1, 2/2):

| Mode | T2 | T3 |
|---|---|---|
| 1 | read | base channel |
| 2 | base channel | read |
| 3 | base channel | base channel |
| 4 | read | copies T2 |
| 5, 6, 7 | base channel | base channel |

3/2 (4-bit mode):

| Mode | T2 | T3 | T4 |
|---|---|---|---|
| 1 | read | read | copies T1 |
| 2 | read | copies T0 | read |
| 3 | base channel | read | read |
| 4 | read | copies T0 | copies T1 |
| 5 | base channel | read | copies T1 |
| 6 | base channel | copies T0 | read |
| 7 | base channel | copies T0 | copies T1 |
| 8 | read | read | copies T3 |
| 9 | read | copies T2 | read |
| 10 | read | read | copies T2 |
| 11 | read | copies T2 | copies T1 |
| 12 | read | copies T0 | copies T2 |
| 13 | base channel | read | copies T3 |
| 14 | read | copies T2 | copies T2 |
| 15 | 0, not read | 0, not read | 0, not read |

Source rule for "base channel": the channel copies the base channel on the side of what it carries (by the table above): L or Ls copies T0, R or Rs copies T1, and C, S or a value the table does not assign copies the base channel `dyn_cross_LR` names (0 T0, 1 T1). In 3/2 this gives T3 always T0 and T4 always T1, as the mode table says.

### Where this differs from the removed code

The removed code applied the same rule for 3/0, 2/1, 3/0 + 2/0 and 3/2. For 3/1 and 2/2 it followed a condition chain of the reference software as mctoolamed edited it. That chain makes `dyn_cross_LR` choose the source for most channels that carry a left or right channel. Without mctoolamed's edit, the chain is the ISO reference software's, and it fails 4 of the 27 multichannel CRCs of conformance stream test18 (3/1 with dynamic crosstalk): it takes T1 for a T2 that carries C. Both the removed code and the rule above verify all 27.

The conformance streams decide only some of the cases. Every dynamic crosstalk frame in them has `dyn_cross_LR` 0, and no stream uses 2/2 with dynamic crosstalk. In test18 (`tc_allocation` 0 to 4), swapping the source of one case at a time breaks CRCs for these, so they are decided as above:

- T2 carrying C (tc 0, 3, 4) copies T0.
- T2 carrying R (tc 2) copies T1.
- T3 carrying S (tc 1, 2) copies T0.
- T3 carrying L (tc 3) copies T0.

Every other case is undecided: the conformance streams do not settle it. Either the streams do not reach it (every dynamic crosstalk frame in them has `dyn_cross_LR` 0, and no stream uses 2/2 with dynamic crosstalk), or both sources give the same zero and nonzero pattern of allocations there, which is all the CRC sees.

### Two readings of the undecided cases

The rule above is this document's reading of the standard: a copy takes the base channel on the side of what the channel carries. A second reading exists, observed as the behavior of the ISO reference software: `dyn_cross_LR` 0 copies every copied channel from the left transmission channel (T0) and 1 from the right one (T1), in 3/1 and 2/2, whatever the copied channel's side. Real encoders likely follow it, so the analyzer must not fail a valid stream's multichannel CRC for the choice. The undecided cases, where the two readings can differ, are:

- every copied channel under `dyn_cross_LR` 1 (in 3/1: T2 carrying L with `tc_allocation` 1 or 5, T3 carrying L with 3, and 6 and 7, which assign nothing; the readings agree where the channel carries C, S or R);
- 3/1 T3 carrying R (`tc_allocation` 4 or 5) under `dyn_cross_LR` 0;
- all of 2/2, under either value.

The decided cases above stay on the first reading under both.

Behavior of `Read` for a 3/1 or 2/2 frame with dynamic crosstalk:

- It reads the frame under the first reading (the side). If the multichannel CRC holds, that is the reading.
- If it does not hold, it reads the frame again under the second (the flag), changing only the source of the copies in the undecided cases. If that CRC holds, the frame keeps that reading.
- If neither holds, it keeps the first and reports the mismatch as before. A mismatch that the other reading resolves is never reported.
- Only the copies' source changes, so the same fields are read in both; what differs is which copied channels have an allocation, hence how many scfsi fields the CRC covers. A frame whose two readings have the same number of such channels reads the same under both.
- The reading used is a node, "Base Channel Source", under "Dynamic Crosstalk Modes" in the composite status of such a frame, with the value "On the channel's side" or "Named by the flag". The parser keeps no multichannel CRC statistics, so there is nothing to count.

Fixtures: `mc-3-1-crosstalk-decided` (`dyn_cross_LR` 0, decided cases only), `mc-3-1-crosstalk-side` and `mc-3-1-crosstalk-flag` (`dyn_cross_LR` 1), `mc-3-1-crosstalk-flag-right` (T3 carrying R under 0, flag reading), `mc-2-2-crosstalk-side` and `-flag` (`dyn_cross_LR` alternating), `mc-second-stereo-crosstalk` and `mc-center-reserved-second-stereo` (`dyn_second_stereo` with a coded and with a reserved center). The writer asserts that the two readings of each undecided fixture differ in the number of copied channels with an allocation, so its CRC verifies under its own reading only.

It must still be confirmed against the 13818-3 text (the semantics of `dyn_cross_LR` and of `tc_allocation`) when a copy is available. The cases where the first reading differs from the removed code:

| Configuration | `tc_allocation` | `dyn_cross_LR` | Channel | Removed code | This rule |
|---|---|---|---|---|---|
| 3/1 | 1, 5 | 1 | T2 (L) | T1 | T0 |
| 3/1 | 4, 5 | 0 | T3 (R) | T0 | T1 |
| 3/1 | 6, 7 | 1 | T3 (not assigned) | T0 | T1 |
| 2/2 | any | 0 | T3 (Rs or R) | T0 | T1 |
| 2/2 | any | 1 | T2 (Ls or L) | T1 | T0 |

One more difference: the removed code let the reserved center value 2 count as a coded center in the second-programme copy of mode 0. With center 2 and surround 3 the shape is 2/0 + 2/0, so it never copied and read T3. This document treats center 2 as no center throughout, so T3 copies T2 when `dyn_second_stereo` is 1.

Effect: no existing golden report, test or conformance figure changes; the fixtures with center 2 use surround 2, and the new fixtures above are the only ones with dynamic crosstalk. A scratch model of this section reproduces the manifest's `crc_verified_frames` for all 22 multichannel conformance streams.

### The fixture writer

`make_layer2_fixtures.py` needs, per configuration (surround, center coded), the additional channels, the `tc_allocation` bits and the number of `predsi` fields at mode 0, all from the tables above. It writes dynamic crosstalk for 3/1, 2/2 and the second stereo programme (`Dynamic`, `Multichannel.allocate`), following the tables above and, for the undecided cases, either reading; without dynamic crosstalk its output is unchanged.

### Checks

- Golden reports and manifests of `tests/fixtures/layer2/mc-*` (prediction in `mc-3-2-lfe`, phantom center, second programme, reserved center, and the `mc-*-crosstalk-*` fixtures), through `MpegFixtureTests` and the golden-report comparison (`dotnet test tests/Encspect.Tests -c Release --filter "FullyQualifiedName~Mpeg|FullyQualifiedName~ChannelLayout"`).
- The 35 ISO/IEC 13818-4 conformance streams: `python3 scripts/fixtures/conformance_mpeg_audio.py --analyzer <encspect>` (downloads them; test08, test18, test19 and test20 use dynamic crosstalk, test09, test15 and test16 prediction, test04 and test13 phantom coding, test06 and test07 the second programme). For test18 the analyzer must verify 27 of 27 frames.
- mctoolamed as a black box: the same script checks that it decodes every multichannel stream without a CRC error; it runs no Encspect code.
- Record the tables' sources (13818-3 and the conformance check) in `docs/development.md`, as `CLAUDE.md` asks for format tables.

## 2. Layer II allocation table

### What it does

A Layer II frame's bit allocation follows one of four tables in MPEG-1 (ISO/IEC 11172-3, Annex B, Tables 3-B.2a to 3-B.2d) and one table at the MPEG-2 and MPEG 2.5 rates (ISO/IEC 13818-3, the table the code cites as B.1). The frame does not name the table; the sampling frequency and the bitrate per channel select it (11172-3 2.4.3.3.1 and the tables' captions). `TableFor` returns the index used everywhere else: 0 to 3 for B.2a to B.2d, 4 for the MPEG-2 table. The writer's `table_for(version, rate, bitrate, channels)` returns the same, with `bitrate` None for free format.

### Rule

- Not MPEG-1 (MPEG-2 or 2.5): table 4.
- Free format: B.2a at 48 kHz, B.2b at 44.1 and 32 kHz (the captions of Tables 3-B.2a and 3-B.2b list free format so).
- Otherwise the bitrate per channel is the header's bitrate in kbps over the channels (1 for mono, 2 for every other mode). Its class and the sampling frequency give the table:

| Bitrate per channel | 48 kHz | 44.1 kHz | 32 kHz |
|---|---|---|---|
| 48 kbps or less | B.2c | B.2c | B.2d |
| 56 to 80 kbps | B.2a | B.2a | B.2a |
| 96 kbps or more | B.2a | B.2b | B.2b |

The captions list 32 and 48 kbps per channel for B.2c and B.2d, 56 to 80 kbps (and at 48 kHz up to 192) for B.2a, and 96 to 192 kbps for B.2b. These are all the per-channel rates of the bitrate and mode combinations 11172-3 allows for Layer II. The classes extend the captions to the combinations it does not allow, which some files still use: stereo at 32 to 80 kbps in total (16 to 40 per channel) falls in the first row, and mono at 224 kbps and above in the last.

This lookup gives the same table as the removed code for every sampling frequency, bitrate index and channel count (checked by script over all of them).

### Checks

- `tests/fixtures/layer2/table-b2a` to `table-b2d`, `table-mpeg2-*`, `free-format-*` and every other Layer II golden (the "Bit Allocation" node's value names the table), through `MpegFixtureTests` and the golden comparison.
- `scripts/fixtures/stream_stats.py` uses the writer's `table_for` for the statistics fixtures; `python3 scripts/fixtures/make_layer2_fixtures.py` must regenerate the committed fixtures byte for byte.
- `crosscheck_mpeg_fixtures.py` (ffprobe, madplay, mpg123 and mp3guessenc decode the fixtures).

## 3. LAME 3.90 to 3.92 preset settings

### What it does

LAME 3.93 added a preset field to the LAME tag; 3.90 to 3.92 have presets but leave the field 0. When the tag's encoder string is `LAME3.90`, `LAME3.90.` (HydrogenAudio's 3.90.3), `LAME3.91` or `LAME3.92`, ignoring trailing spaces, and the tag has a Xing quality and preset 0, the analyzer adds a "Settings Match" node under "Preset". The node names the preset whose settings the tag holds. The text of the node and its condition stay as they are; only the table and the lookup are rewritten.

### Key

The settings come from the tag's own fields (Mp3 Info Tag revision 1, LAME tag offsets from its start):

- Xing quality: the 4-byte quality field of the Xing header.
- VBR method: low four bits of byte 9.
- Lowpass: byte 10, in units of 100 Hz.
- ATH type: low four bits of byte 19.
- Bitrate: byte 20 (ABR bitrate, or minimum bitrate; 255 means 255 or more). Left out of the key for the VBR methods 3 and 4, where it follows `-b`, not the preset.
- Noise shaping: bits 0 and 1 of byte 24; stereo mode: bits 2 to 4 of byte 24.

The order of the key's fields is free; use one that reads naturally (for example method, quality, bitrate, then the rest).

### Deriving the table

Every row comes from a committed fixture: an encode of the "music" signal of `make_external_mpeg_fixtures.py` (44.1 kHz stereo) with that preset, by a build the repository already makes. A row no committed encode backs is dropped. Read the key from each encode's first frame with `vbr_tags.read` (the fields above), and take the name from the encode's command line.

- Builds: 3.90.1 (writes `LAME3.90`), 3.91 and 3.92 from source (`old_lame_tool.py`), and the Windows builds of 3.91 and HydrogenAudio's 3.90.3 (`lame_win_tool.py`, under Wine).
- Presets the 3.90 to 3.92 builds list (`lame --preset help`, `lame --alt-preset help`): `--alt-preset standard`, `extreme`, `insane`, `fast standard`, `fast extreme`, `<bitrate>` and `cbr <bitrate>`; `--r3mix` (same as `--preset r3mix`); and the basic presets phone, phon+, lw, mw-eu, sw, mw-us, voice, fm, radio, tape, hifi, cd and studio.
- Encoded on 2026-10-29 for this description: the three source builds wrote the same fields for every preset. The two Windows builds wrote some lowpasses 100 Hz higher (extreme, fast extreme, insane, r3mix, mw-us). 3.90.3 also wrote noise shaping 1 for standard and extreme. Phone, phon+, lw, mw-eu, sw and voice wrote no Xing header with these builds, so they have no row.
- Already committed (`tests/fixtures/mpeg`): 3.90.1 standard, extreme (at 48 kHz), insane, `128` (ABR), r3mix and cd; 3.92 fast standard; 3.90.3 standard, extreme and insane; Windows 3.91 extreme.
- To add, one per preset and build whose fields differ from the committed ones: with 3.90.1 fast extreme, studio, hifi, tape, radio, fm, mw-us and `cbr 128`; with Windows 3.91 fast extreme, r3mix and mw-us (its higher lowpasses). Add them to `make_external_mpeg_fixtures.py` with their manifests and goldens.
- ABR and CBR alt-presets name a bitrate, so they get no row each. The rule stays: VBR method 2 (ABR) or 1 (CBR), Xing quality 58, noise shaping 2, joint stereo (stereo mode 3), ATH type 2 and a bitrate field between 1 and 254 give `--alt-preset <bitrate>` or `--alt-preset cbr <bitrate>`. The committed `128` encode and the new `cbr 128` encode back it.
- The comment over the table names the fixtures each row comes from, and `docs/development.md` names the source of the table: these encodes, not getID3. getID3 stays a cross-check (`check_getid3` in `crosscheck_mpeg_fixtures.py`).
- `vbr_tags.py` keeps the same table and rule for the cross-checks and golden additions.

### Checks

- The goldens that show "Settings Match": `external-lame390-alt-standard`, `-alt-extreme`, `-alt-insane`, `-alt-abr`, `-r3mix`, `-preset-cd`, `external-lame392-alt-fast-standard`, `external-lame3903-alt-standard`, `-alt-extreme`, `-alt-insane` and `external-lame391w-alt-extreme`, plus the new ones. `external-lame391-cbr` (`-b 128`) and the 3.93 and later encodes show none.
- `crosscheck_mpeg_fixtures.py` compares the reports with `vbr_tags.lame_fields` and with getID3.

## 4. mpg123 and mp3guessenc output models

### What they do

`check_vbr_header` in `crosscheck_mpeg_fixtures.py` runs mp3guessenc 0.27.6 (`guessenc_tool.py`: `mp3guessenc -v -r -s <file>`) and mpg123 (`mpg123 -vvv -t <file>`, its standard error) on every fixture whose manifest has a `vbr_header`. It compares what they print with what the manifest's header values imply. The models below predict that printed text from the manifest's raw tag (`vbr_tags.read` fields: `type`, `frames`, `bytes`, `toc`, `quality`, `truncated` and `lame` with `encoder`, `revision`, `method`, `lowpass`, `flags`, `bitrate`, `delay`, `padding`, `misc`, `radio_gain`, `audiophile_gain`, `preset_surround`, `music_length`, `music_crc`, `tag_crc`, `tag_crc_computed`). They were written from the tools' output on the 101 fixtures with a VBR header, run on 2026-10-29 with mpg123 1.32.5 and the pinned mp3guessenc: the model below matches all 98 complete headers (the other three are truncated or hidden behind a leading tag). Cases no fixture shows are not modeled: name them as such in the code, or leave them out.

### mp3guessenc

`guessenc_tag_lines` (kept) reads "key : value" lines, strips both sides, and keeps the first of each key. It also keeps the first word of the "... tag detected into the first frame" line as "detected", and the last word of "Music CRC verification ..." as "music crc". `expected_guessenc(tag, music_crc)` returns the expected value per key; the caller compares each. Observed:

- VBRI header: detected "VBRI"; "Tag version" the version; "Encoder delay" "<delay> samples"; "File size" "<bytes> bytes"; "Number of frames" the count; "Quality" the quality; "TOC" "<entry size × entries> bytes (<entries> entries, <entry size> byte each)", with "bytes each" for entry sizes above 1. Nothing more is compared.
- Xing or Info header: detected "Xing" for both; "File size" "<bytes> bytes" and "Number of frames" "<frames>", each 0 when the field is absent; "TOC" "100 bytes (100 entries, 1 byte each)" or "no".
- "Quality": the decimal quality. The line is absent when the field is absent, 0xFFFFFFFF (Helix's CBR), or "GOGO" (0x474F474F, Gogo); the caller compares an absent line as "", so return "" there. mp3guessenc appends " (...)" to some LAME qualities, which the caller cuts off at " (".
- "Lame tag" "yes" when the encoder string starts with "LAME" (seen: LAME3.x, LAME4.0, LAMEH5.24), with "L", a digit, ".", two digits (seen: L3.99a12), or with "Lavc"; "no" for "Lavf" and without a LAME tag. With "no", nothing below is compared.
- "Lame short string": the encoder string without trailing spaces.
- "Tag revision": the revision (0 and 1 seen).
- "Bitrate strategy": a prefix by method, then the bitrate. The prefix is "CBR, " for method 1, "ABR, " for 2, "VBR method old/rh, min " for 3, "VBR method mtrh, min " for 4, and none for 0 (the only methods seen). The bitrate is "unknown" for 0, "<b> kbps" otherwise, with " or higher" added for 255.
- "Lowpass value": lowpass × 100, or "unknown" for 0.
- "nspsytune", "nssafejoint", "nogap continued", "nogap continuation": "yes" or "no" for flag bits 0x10, 0x20, 0x40, 0x80.
- "ATH type": flags & 15.
- "Encoder delay (start)" "<delay> samples", "Encoder padding (end)" "<padding> samples".
- "Encoding mode" by stereo mode (bits 2 to 4 of misc): 0 "mono", 1 "simple LR stereo", 3 "joint stereo", 4 "forced MS stereo", 7 "other" (the values seen).
- "Unwise settings": "used" when misc bit 0x20 is set, else "not used".
- "Source frequency" by misc bits 6 and 7: 0 "32 kHz or below", 1 "44.1 kHz", 2 "48 kHz" (seen).
- "Preset" by the low 11 bits of `preset_surround`: 0 "No preset."; a bitrate "<n> kbps" (seen: 96, 128, 160, 192); 410 to 500 in steps of 10 "V<(500 − n) / 10> / VBR_<n − 400>" (seen: 460, 480, 490, 500); 1001 "Standard." (seen).
- "Originally encoded": "<music_length> bytes".
- "Tag verification": "passed" when the tag CRC holds, else "failed".
- "music crc": "performed" when the tag CRC fails. When it holds, "passed" if the stored music CRC equals a CRC-16 over the music frames, else "failed". The music frames are every complete frame after the header's frame, up to the first frame whose MPEG version or sample rate differs, not the tag's music length. Without a frame list in the manifest (free format), use the manifest's `ffprobe_packets`. `check_vbr_header` computes this CRC with `vbr_tags.crc16`; with neither list and a LAME tag, use `music_crc_computed`.

### mpg123

The comparison keeps the stderr lines that start with "Note: ", without that prefix, when they start with "Xing", "Info: " or "Encoder delay". It then drops "Info: rev ...", "Info: peak ...", "Xing stream size ..." and the "Old LAME detected" line (that filter stays in `check_vbr_header`). For a VBRI header nothing must remain. For a Xing or Info header without a separate `encoder_string` and not truncated, `expected_mpg123(tag)` returns this list, in order:

- "Xing/Lame/Info header detected".
- "Xing: <frames> frames", "Xing: <bytes> bytes", "Xing: quality = <quality>", each only when the field is present.
- With a LAME tag (also Lavc and Lavf ones):
  - "Info: Encoder: <encoder>", the string with its trailing spaces.
  - "Info: Cannot determine LAME version." when the string starts with "LAME" but not with "LAME", digits, ".", digits (seen: LAMEH5.24).
  - "Info: vbr mode <method>".
  - "Info: Radio Gain = <g>dB" and "Info: Audiophile Gain = <g>dB", from the two ReplayGain fields, with one decimal ("0.0", "-4.4", "1.2"). A field whose originator (bits 10 to 12) is 0 gives 0.0. Otherwise the value is ± (low 9 bits) / 10, negative when bit 9 is set. Add 6 when the originator is 3 (automatic) and the encoder string is "LAME<major>.<minor>" below 3.95 (seen: LAME3.94 and LAME3.94b; mpg123 prints its "Old LAME detected" note for 3.90 to 3.94). In every fixture a set first field has name 1 and a set second field name 2.
  - "Info: ABR rate = <bitrate>" for method 2.
  - "Encoder delay = <delay>; padding = <padding>".

### Checks

Run `python3 scripts/fixtures/crosscheck_mpeg_fixtures.py` (needs mpg123, ffprobe, madplay, a C compiler; the MPEG cross-check job in CI). It must report no problem on the committed fixtures, as on main.

## Tests that pinned the rewrite

These failed while the members were stubbed and pass with the rewrite:

- Every Layer II fixture and golden: `tests/fixtures/layer2`, the Layer II files in `tests/fixtures/stats` and `mpeg`, and the tests that read them (`MpegFixtureTests`, `ChannelLayoutTests`, `DescriptionTests`, `FindingCoverageTests`, `MemoryTests`).
- The 11 goldens with "Settings Match".
- `make_layer2_fixtures.py`, `stream_stats.py` and `conformance_mpeg_audio.py --analyzer` (35 streams, no problem). `crosscheck_mpeg_fixtures.py` needs tools that CI installs.

## Implementer's sources

- May read: this document; ISO/IEC 11172-3, 13818-3 and 13818-4 (the conformance streams and their descriptions); the Mp3 Info Tag specification; the rest of the repository at its current state, including `Read` and `ToNode` in `Layer2Multichannel.cs`, the fixture writers, `vbr_tags.py` and the goldens. May run as black boxes: mctoolamed, mp3guessenc, mpg123, ffprobe, madplay, getID3 and the LAME builds.
- Must not read: mctoolamed's and mp3guessenc's source (`third_party/mp3guessenc`, any download of mctoolamed), mpg123's, FFmpeg's and libmad's source, getID3's source (`module.audio.mp3.php`), the git history of the stubbed members (before the commit that stubbed them), the describer's scratch folder, and the audit reports (`scratchpad/reviews/license-audit-*.md`).
