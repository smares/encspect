# AAC

AAC in [MP4](mp4.md), and raw AAC files as ADTS or ADIF.

## What is read

- **AudioSpecificConfig:** including PCE, ELD and xHE-AAC's (USAC) configuration with its elements, MPEG Surround 2-1-2 settings and configuration extensions, and explicit SBR and PS signaling (HE-AAC and HE-AAC v2).
- **Raw data blocks:** every block of AAC Main, LC, SSR and LTP, element by element, without decoding the audio:
  - window sequences and shapes, section data and scalefactors;
  - mid/side and intensity stereo, noise substitution, pulses, TNS, prediction and gain control;
  - the Huffman-coded spectral data;
  - fill elements with SBR headers, parametric stereo, dynamic range control, fill and ancillary data.
- **ADTS:** sync on a chain of frames, resync after damage, every header field, and the CRC over the elements it covers.
- **ADTS detection:** a chain at the start of the data (after zero bytes) of three frames is class 91. A chain further in is class 50 only with eight consistent frames or frames to the end of the data, since other formats' bytes (E-AC-3's frame headers) can chain three.
- **ADIF:** the header, and its raw data blocks, which the parser delimits.
- **Implicit signaling:** SBR and PS found only inside the blocks make the stream HE-AAC or HE-AAC v2, with a finding that says so.

## Statistics

Window sequences, mid/side masks and bands, intensity stereo, noise substitution, TNS per window sequence, codebook use, max_sfb and the bandwidth it reaches, global gain per channel, SBR's frequencies and the fill payloads.

## Channel layout

The channel configuration, a program config element, or parametric stereo.

## Encoder and writer detection

The encoder and the writer are two fields of a stream, each with its own confidence and evidence (the "Encoder detection and confidence" and "Writer detection" topics of the GUI and `--explain` explain both); a writer that differs from the encoder is no conflict.

- **Encoder:** the stream's own evidence and the claims of the encoder role (an ID3v2 TSSE frame in a stream's file).
- **Writer:** the MP4 tool atom (©too) and QuickTime's ©swr, which no specification at hand defines as the encoder; an APEv2 Encoder item; ffmpeg's muxer version ("Lavf60.16.100"); and the layouts of Apple's file writer. Two claims that name different software are an information finding (`aac.writer-claim-differs`).

Encoder:

- ffmpeg's encoder and FAAC 1.30 to 1.50, from the version they write into a fill element, with high confidence:
  - ffmpeg writes "Lavc60.31.102" at the start of an access unit;
  - FAAC's `WriteFAACStr` writes "libfaac <version>" into the first access unit; fixtures confirm 1.30, 1.31.1, 1.40 and 1.50, in M4A and ADTS;
  - 1.31.1 writes "libfaac 1.31.1", while 1.40 and 1.50 write their build's project version, "libfaac 1.40.0" and "libfaac 1.50.0", so the encoder is named "FAAC 1.40.0" and "FAAC 1.50.0".
- FAAC 2.0 and later, which dropped that text (upstream commit 960a0ea), in two ways:
  - The MP4 tool atom (©too) of the front end, "FAAC 2.2.0 (6acbe23)", names the writer with low confidence, and the encoder is then not named.
  - The SBR layout of its HE-AAC encoder names "FAAC" with medium confidence, without a version; the ©too claim names the writer, "FAAC 2.2.0 (6acbe23)", apart from it. The layout, from `libfaac/sbr_bitstream.c` and `sbr.c` (`SbrInit`, `SbrUpdate`):
    - every SBR header sends bs_header_extra_1 and not bs_header_extra_2, with bs_noise_bands 0, bs_start_freq 15 and bs_xover_band 0;
    - 2.2 adds bs_freq_scale 1 to 3 (by bitrate per channel), bs_alter_scale 0, bs_amp_res 1 and a bs_stop_freq of 10 to 13 by sample rate (13 at 32 kHz, 12 at 44.1 kHz, 11 at 48 kHz, 10 above), and repeats the header every 30 access units;
    - 2.0 and 2.1 add bs_freq_scale 0 with a bs_stop_freq of 10. They count access units the same way, but send the header again in the last access units of some files, the flush frames: the decision to send it is made in `SbrEncode`, which flush frames skip (2.0 never calls `doHEAACFrame` when no samples are left, 2.1's `SbrContextProcessFrame` returns before `SbrEncode`), so a stale "send" stays while the frame count, advanced on every write, goes on. Their header spacing is therefore not held to the period;
    - every sbr_data payload is plain: no extra data bits, no coupling, only FIXFIX and VARFIX frame classes, every envelope and noise floor coded across frequency, no sinusoids and no extended data;
    - at least 20 payloads (the plan's minimum for evidence that is a behavior), none unread.
  - Channels: 2.2 writes the SBR data of each channel element after it. 2.0 and 2.1 write one payload after the whole frame, typed as a channel pair element for any channel count above one (`channels.c`, `BuildFrame`, `SbrContextGetBits`), so with more than two channels the payload follows no element of that kind: a 5.1 HE-AAC stream from 2.0 or 2.1 is reported with `aac.sbr-without-channel-element` and its ADTS file is not named (its M4A file only from the ©too claim, which names the writer). A 5.1 stream from 2.2 is named. Both were checked by encoding (`faac-2.2-he-51-adts`, `faac-2.1-he-51-adts` and `check_faac_sbr.py`).
  - `check_faac_sbr.py` holds the rule against FAAC 2.0, 2.1 and 2.2 (98 mono and stereo HE-AAC files each, over sample rates of 32 to 96 kHz and bitrates of 16 to 128 kbps, as ADTS at every rate and as M4A at 44.1 kHz), which it must name, and against 450 files of fdkaac 1.0.0, none of which it may name: 432 HE-AAC and HE-AAC v2 files (ADTS and M4A, mono and stereo, over the sample rates from 16 to 96 kHz that fdkaac accepts, bitrates of 16 to 128 kbps and variable bitrate modes) and 18 low complexity ADTS files. fdkaac's fixtures have a start frequency of 10 or 12, a stop frequency of 9 and the default extra fields.
  - Other encoders' HE-AAC: `check_sbr_samples.py` holds the rule against 13 downloaded files in FFmpeg's FATE suite (SHA-256 pinned, fetched in CI): the MPEG-4 audio SBR and parametric stereo conformance streams `al_sbr_*` (HE-AAC at 32 and 48 kHz, one in 5.1) and Coding Technologies' aacPlus decoder check streams `CT_DecoderCheck` (`File1` to `File7`, also published as `sbr_*`). None is named. Together with fdkaac's files, that is the rule's cross-encoder basis, thinner than exhale's.
  - Not at hand, so not checked: Nero's and Dolby's HE-AAC encoders.
  - Apple's HE-AAC writes no SBR layout of its own to use: its headers differ with the bitrate and rate (bs_start_freq 10 to 14, bs_stop_freq 7 to 12, bs_freq_scale 0 to 3, bs_noise_bands 1 to 3, with or without bs_header_extra_1), and it uses frame grids and envelope coding that the FAAC rule excludes; the FAAC rule names none of its files.
  - The private repository's samples are xHE-AAC, whose SBR sits in the UsacConfig and not in fill elements, so the rule does not apply to them.
  - The hand-built `faac-style-*` fixtures write FAAC 2.2's layout at 44.1 kHz in 32 access units. `faac-style-plain` is named FAAC; in each of the others one access unit does one thing FAAC never does, and the stream is not named: extra data bits, channel coupling, a FIXVAR frame class, a VARVAR frame class, an envelope coded across time, a noise floor coded across time, sinusoids, extended data. The FAAC 2.1 and 2.0 fixtures (`faac-2.1-he-24k-adts`, `faac-2.0-he-24k`) repeat the header in their last access units. The other hand-built SBR fixtures use other fields (start frequency 5) and are not named.
- Apple's AAC encoder (AudioToolbox, as afconvert, iTunes and QuickTime use it) writes no text into the stream, so the layout of its file writer names the encoder with an sbtd box, and the writer without; a remux by other software drops the traces:
  - "Apple AAC encoder (AudioToolbox)" with medium confidence, from the MP4 parser: a DecoderConfigDescriptor whose stream type byte is 0x14, the reserved bit that ISO/IEC 14496-1 7.2.6.6 sets to 1 written as 0, together with an sbtd box in the sample entry. The sbtd box records the PCM source of an encode (its bit depth), and none of the copies of other encoders' packets has one;
  - "Apple AudioToolbox", the writer, with low confidence, next to the encoder where the files have an sbtd box too: the stream type byte with a free box that pads the audio data to a 4,096-byte boundary but no sbtd box (MP4, 3GP; `afconvert --no-filler` changed nothing in these files), and in ADTS the MPEG-2 ID, no CRC, the original/copy bit set and an adts_buffer_fullness of 0 in every frame (at least 20 frames), the other flag bits clear;
  - there is no version: the files hold none;
  - what the rule rests on: the AAC files afconvert encoded on macOS 15.7 (run 4; on each of arm64 and x86-64, 56 M4A and M4B with an sbtd box, named at medium; 3 in MP4, 3GP and 3GP2 and 8 in ADTS, named at low; AAC-LC including 5.1, HE-AAC, HE-AAC v2, AAC-LD and AAC-ELD). A ninth ADTS file, 0.5 s of 5.1 HE-AAC, has 15 frames and is not named. None of the 86 other MP4 fixtures (85 esds boxes: ffmpeg, fdkaac, FAAC, MP4Box, hand-built, all with 0x15) or 56 other ADTS fixtures (original/copy bit clear, buffer fullness 0x7FF, 256 or varying) is named Apple;
  - the layout is the writer's, and the writer lays it around packets it only copies. Run 4 copied ffmpeg, FAAC and fdkaac streams with `afconvert -d 0` (13 encodes per runner, all of them worked, the same on both):
    - an ADTS target always gets Apple's headers, whatever the source;
    - an M4A or MP4 target always gets the `free` box and the audio data on a 4,096-byte boundary (all 7 copies per runner), and the stream type byte follows the source: 0x14 from an ADTS source, where the writer builds the esds, and the source's own 0x15 from an M4A source, whose esds is kept (and its ©too dropped). Among the run-4 data the padding therefore adds nothing beyond the 0x14, and an M4A copy shows no layout;
    - no copy has an sbtd box, so a copy names the writer at low confidence and the encoder not at all;
    - ffmpeg and FAAC copies keep their version text and are named by it with high confidence, the layout then naming the writer "Apple AudioToolbox" next to the encoder, with no finding, since the two are different facts; fdkaac writes no text, so its copies show the writer "Apple AudioToolbox" at low confidence when they show the layout, and no encoder;
  - residual risk: sbtd is the AudioFile property `kAudioFilePropertySourceBitDepth`, which an AudioFile client that writes packets might set, and "no copy gets an sbtd box" rests only on `afconvert -d 0` from ADTS and M4A sources;
  - what it does not rest on: files written by iTunes, QuickTime or Music, Logic, or Apple's hardware, which were not at hand; they use the same encoder and file writer, but may differ in the metadata they add (iTunes writes Encoding Params and ©too, which are shown; a ©too claim is a name some software wrote and outranks a layout of equal confidence as the writer, and one that names iTunes agrees with the Apple layout);
  - no stream trait separates Apple's encodes from fdkaac's or from the copies; only the sbtd box does, and it is not in the stream. Apple's and fdkaac's streams both end many access units with a fill element of count 0, the first access units and the SBR header fields of Apple's HE-AAC vary with the settings, and the ADTS headers are the writer's.
  - window shape is no evidence either way, and the analyzer does not use it:
    - the `win-*` run (workflow run 38691802940, both runners, 8 s of twelve varied signals; 159 of 171 encodes worked per runner, 81,094 channel streams, 96.0% sine on both) found sine windows only in every encode except `-q 127` (afconvert's maximum quality, `kAudioConverterQuality_Max`): AAC-LC at constant 64, 128 and 256 kbps, variable quality 0, 64 and 127, constrained variable at 128, `-q 0`, HE-AAC and HE-AAC v2, in M4A and ADTS, for speech, plucks, clicks, chords, noise, tones, sweeps, transients, wide and narrow stereo and mono;
    - at `-q 127` it writes KBD, up to every channel stream and in every window sequence: `win-lc-m4a-q127-cbr64-{clicks,pluck,transient,white}` 100% KBD, `-cbr128-sweep` 79%, `-cbr128-tones` 84%, while `-cbr128` and `-cbr256` transient and chords and the ADTS transient have 4% to 5% (long windows only); the q127 CBR file of the first signal (33.3% KBD) was the same effect;
    - so a mostly-KBD stream does not rule Apple out; three of these files are in `tests/fixtures/apple` (`apple-win-lc-m4a-q127-cbr64-clicks`, `-cbr64-pluck`, `-cbr128-tones`) and `AppleEncoderTests` asserts that they are still named "Apple AAC encoder (AudioToolbox)" at medium confidence, from the sbtd box and stream type 0x14, so that no later window rule breaks them;
    - for comparison (committed fixtures, `scripts/fixtures/window_shapes.py`): fdkaac 97.2% KBD, ffmpeg 96.7%, the real FAAC encodes (1.30 to 2.2, 2,358 channel streams) sine only, and in fdkaac and ffmpeg KBD appears in long and stop windows only (the 19.6% KBD of the `faac` prefix in `--by-prefix` comes from the nine hand-built `faac-style-*` files, hand-built, not FAAC);
    - qaac maps its `--quality` 0 to 2 to `(n + 1) << 5`, 32 to 96, and defaults to 2 (qaac's `options.cpp` and `main.cpp`, master), so its default is 96, not 127; no encode at `-q 96` was made, so what qaac's default does to the window shape is not known;
    - the 12 failed encodes are HE-AAC at 96 kbps (stereo sources) and 48 kbps (mono sources): afconvert refuses the bitrate with `Couldn't set audio converter property ('!dat')` (an invalid value; afconvert does not say what the limit is), the same on both runners.
- exhale 1.1.2 and later, from the layout of the UsacConfig, with medium confidence. Releases 1.0.0 to 1.1.1 write no audio pre-roll element and are named only by their tool atom. The layout:
  - coreSbrFrameLengthIndex 1, or 3 with SBR;
  - an audio pre-roll extension element first, with no configuration, default length or fragmentation;
  - then only channel elements (SCE, CPE, LFE) without time-warped MDCT, and no fill element;
  - with SBR: no harmonic SBR or PVC, a default start frequency of 15, a stop frequency of 8 to 10, no second header extra, and stereoConfigIndex 0;
  - one loudness info configuration extension of 7 to 9 bytes with one loudnessInfo and one measurement, or none for 7 or more channels.
- bs_interTes is not checked: exhale writes 0, but a build with `ENABLE_INTERTES` writes 1.
- The layout carries no version. A tool atom that names exhale supplies it ("exhale 1.2.2"); one that names another encoder is an info finding.
- The rule rests on exhale 1.2.2's writer (`bitStreamWriter.cpp`, `createAudioConfig`, and `exhaleApp.cpp`) and on other encoders' files, none of which has the layout:
  - Fraunhofer's four demonstration files checked end the element list with a fill element, and the newer ones add a stream ID extension;
  - libxaac's encoder does both (checked by hand, not in CI);
  - the eight ISO/IEC 23003-3 conformance streams checked have a fill element and no pre-roll element.
- `check_usac_samples.py` checks the downloaded files in CI (see [development](../development.md)).
- Other encoders from the metadata, with low confidence.
- FAAC 2.x in low complexity cannot be named by the stream:
  - it writes no text, and no fill element at all unless `--cbr` stuffs frames with zero bytes;
  - what else it writes is shared: FAAC 1.x has the same fill bit rounding (`WriteAACFillBits`), element order and ADTS header (profile LC, buffer fullness 0x7FF), and on pink noise with a tone every channel pair of 1.30, 1.31.1, 1.50, 2.2 and ffmpeg has a common window;
  - tool use (mid/side, intensity stereo, noise substitution, window sequences) depends on the signal and on the release's tuning, not on a layout that every file shows;
  - an ADTS file of 2.x LC, or a 1.x file cut after its first access units, shows no signature, so only the ©too claim in MP4 names its writer, and the encoder is not named.

## What Apple's encoder writes

From `afconvert` on macOS 15.7.9 (AudioToolbox framework bundle version 1.14, AudioCodecs.component bundle version 7.0, `afconvert` 2.0), arm64 and x86-64, 0.5 s of a test signal (1 s for HE-AAC), as the committed `apple-*` fixtures show:

- M4A: `ftyp` brand M4A (compatible M4A, mp42, isom); `moov` before the audio data; a `free` box that pads the audio data to offset 4,096; a `udta/meta` with an `mdir` handler (vendor `appl`) and one item, the freeform iTunSMPB; no ©too, no Encoding Params, no tags copied from the source (a WAV with INFO tags gives the same file); no edit list, so the priming is in iTunSMPB only, and the track lasts delay plus samples plus padding.
- iTunSMPB: an encoder delay of 2,112 samples for AAC-LC, HE-AAC and HE-AAC v2, the samples between, and the padding that completes the last access unit: 2,112 + 22,050 + 414 for 0.5 s of AAC-LC at 44.1 kHz (`apple-lc-m4a-cbr-st44`), 2,112 + 22,050 + 1,438 for 1 s of HE-AAC at 44.1 kHz (`apple-he-m4a-cbr-st44`; the figures count at the core rate, 22,050 Hz, which is the media timescale). AAC-LD and AAC-ELD write other delays (256 to 584). afinfo prints the same figures as priming, valid and remainder frames.
- The sample entry: `mp4a` with an esds box (descriptor lengths in four bytes, as other muxers write them; buffer size 6,144 in the 128 kbit/s stereo files; a maximum and an average bitrate, the average 0 for variable bitrate) and an sbtd box that holds four zero bytes and the source bit depth as text ("I16" in every file seen); afinfo prints it as "source bit depth".
- The stream type byte of the DecoderConfigDescriptor is 0x14: the reserved bit is 0, where ISO/IEC 14496-1 requires 1.
- HE-AAC signaling in M4A is explicit and hierarchical: the AudioSpecificConfig names AAC-LC at the core rate (11,025 to 24,000 Hz) and a sync extension 0x2B7 for SBR, plus 0x548 for parametric stereo in HE-AAC v2; the media timescale and iTunSMPB count the core rate (22,050 Hz for 44.1 kHz HE-AAC). ADTS cannot signal it, so the analyzer reports implicit signaling (`aac.implicit-sbr`). afconvert's HE-AAC v2 with a mono source gives mono HE-AAC.
- HE-AAC stereo uses SBR frames of the FIXVAR class, whose frequency resolutions are sent last envelope first (14496-3 4.4.2.8, sbr_grid); the analyzer read them forwards and reported `aac.sbr-past-fill` for HE-AAC files such as `apple-he-m4a-cbr-st44-32` and `apple-he-m4a-cbr-mono22` (both runners) and for three of the SBR conformance streams (`al_sbr_cm_48_2`, `al_sbr_cm_48_5.1`, `al_sbr_sr_48_2_fsaac48`, whose manifest recorded the finding) until this was fixed; faad2's trace, with a count of the bits each SBR payload takes added, showed that the payloads fit their fill elements.
- Rates: afconvert picks the output rate for the bitrate (64 kbit/s stereo from 44.1 kHz is written at 32 kHz; VBR at quality 0 at 22.05 kHz; 32 kbit/s stereo HE-AAC at 32 kHz).
- ADTS: ID 1 (MPEG-2), no CRC, original/copy 1, buffer fullness 0 in every frame, also at variable bitrate.
- CAF: `desc`, `kuki` (the esds contents for AAC, the ALAC configuration for ALAC), `chan`, `info` (approximate duration), `pakt` (packets, valid frames, priming, remainder), a `free` chunk that pads to 4,096, `data`; HE-AAC adds `ldsc`. CAF is not read yet.
- Apple Lossless in M4A (phase 3): an `alac` sample entry, an iTunSMPB with a delay of 0, a last packet whose duration in stts is shortened by the padding, so the track lasts delay plus samples; `mp4.itunsmpb-total` accepts that.
- arm64 and x86-64: no file is the same. Both runs write the same structure, the same iTunSMPB and the same number of access units, but different access unit sizes and spectral data (`apple-he-m4a-cbr-st44` and its Intel twin have the same length and 8,040 of 13,267 bytes differ; the other pairs differ in length, such as the AAC-LC ADTS files with 9,045 and 8,983 bytes); the esds bitrates follow. The ALAC files differ only in the movie's timestamps. The detection uses the structure, which is the same.
- 5.1 works once afconvert is given the output layout (`-l MPEG_5_1_D`, alone or after the input's `-l WAVE_5_1_B` or `-l MPEG_5_1_A`; or a CAF that carries MPEG_5_1_D): without it `ExtAudioFileSetProperty ('cfmt') failed ('!dat')`, the client format being refused. The M4A has channel configuration 6 and no chan box; afinfo prints "5.1 (C L R Ls Rs LFE)".
- Options of macOS 15.7's afconvert, as the second run's logs show: `--soundcheck-generate` adds a freeform iTunNORM item and a `udta/ludt/tlou` loudness box to the track; `--condensed-framing 16` writes a compact sample size box (stz2) instead of stsz; `-t` in CAF adds a `srcI` chunk (the source format and file name); `--generate-hash` and `--no-filler` change nothing in an M4A of this size (the audio data stays on the 4,096-byte boundary); `--media-kind` is refused without `--soundcheck-generate`, and with it (third run) adds a `stik` item to the item list, an integer data atom whose value is 1 for "Music", 8 for "Podcast" and 2 for "Audiobook" (the last in an `.m4b` with brand M4B), besides the iTunNORM item and the loudness box of Sound Check; `--prime-method` (0, 1, 2) fails with "Couldn't set audio converter property (-50)", and `aac#960` with `('cfmt') failed ('fmt?')`. None writes an Encoding Params item or a ©too.
- xHE-AAC: all 36 attempts failed (MP4, CAF and LOAS, 44.1 and 48 kHz, 16-bit integer and float sources, mono and stereo, default, constant bitrate and variable bitrate), each with `ExtAudioFileSetProperty ('cfmt') failed ('fmt?')`: afconvert on this macOS refuses to set the client format for `usac` with these inputs. That says nothing about other inputs or other APIs. AAC-ELD with SBR in mono fails on write (`('bada')`).

## Not yet covered

- USAC, ER AAC and the SBR envelope data (planned: entropy pass).
