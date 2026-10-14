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
- **ADIF:** the header, and its raw data blocks, which the parser delimits.
- **Implicit signaling:** SBR and PS found only inside the blocks make the stream HE-AAC or HE-AAC v2, with a finding that says so.

## Statistics

Window sequences, mid/side masks and bands, intensity stereo, noise substitution, TNS per window sequence, codebook use, max_sfb and the bandwidth it reaches, global gain per channel, SBR's frequencies and the fill payloads.

## Channel layout

The channel configuration, a program config element, or parametric stereo.

## Encoder detection

- ffmpeg's encoder and FAAC 1.30 to 1.50, from the version they write into a fill element, with high confidence:
  - ffmpeg writes "Lavc60.31.102" at the start of an access unit;
  - FAAC's `WriteFAACStr` writes "libfaac <version>" into the first access unit; fixtures confirm 1.30, 1.31.1, 1.40 and 1.50, in M4A and ADTS;
  - 1.31.1 writes "libfaac 1.31.1", while 1.40 and 1.50 write their build's project version, "libfaac 1.40.0" and "libfaac 1.50.0", so the encoder is named "FAAC 1.40.0" and "FAAC 1.50.0".
- FAAC 2.0 and later, which dropped that text (upstream commit 960a0ea), in two ways:
  - The MP4 tool atom (©too) of the front end, "FAAC 2.2.0 (6acbe23)", names the encoder with low confidence, as any claim does.
  - The SBR layout of its HE-AAC encoder names "FAAC" with medium confidence, without a version, which an agreeing ©too claim supplies. The layout, from `libfaac/sbr_bitstream.c` and `sbr.c` (`SbrInit`, `SbrUpdate`):
    - every SBR header sends bs_header_extra_1 and not bs_header_extra_2, with bs_noise_bands 0, bs_start_freq 15 and bs_xover_band 0;
    - 2.2 adds bs_freq_scale 1 to 3 (by bitrate per channel), bs_alter_scale 0, bs_amp_res 1 and a bs_stop_freq of 10 to 13 by sample rate (13 at 32 kHz, 12 at 44.1 kHz, 11 at 48 kHz, 10 above), and repeats the header every 30 access units;
    - 2.0 and 2.1 add bs_freq_scale 0 with a bs_stop_freq of 10. They count access units the same way, but send the header again in the last access units of some files, the flush frames: the decision to send it is made in `SbrEncode`, which flush frames skip (2.0 never calls `doHEAACFrame` when no samples are left, 2.1's `SbrContextProcessFrame` returns before `SbrEncode`), so a stale "send" stays while the frame count, advanced on every write, goes on. Their header spacing is therefore not held to the period;
    - every sbr_data payload is plain: no extra data bits, no coupling, only FIXFIX and VARFIX frame classes, every envelope and noise floor coded across frequency, no sinusoids and no extended data;
    - at least 20 payloads (the plan's minimum for evidence that is a behavior), none unread.
  - Channels: 2.2 writes the SBR data of each channel element after it. 2.0 and 2.1 write one payload after the whole frame, typed as a channel pair element for any channel count above one (`channels.c`, `BuildFrame`, `SbrContextGetBits`), so with more than two channels the payload follows no element of that kind: a 5.1 HE-AAC stream from 2.0 or 2.1 is reported with `aac.sbr-without-channel-element` and its ADTS file is not named (its M4A file only from the ©too claim, with low confidence). A 5.1 stream from 2.2 is named. Both were checked by encoding (`faac-2.2-he-51-adts`, `faac-2.1-he-51-adts` and `check_faac_sbr.py`).
  - `check_faac_sbr.py` holds the rule against FAAC 2.0, 2.1 and 2.2 (98 mono and stereo HE-AAC files each, over sample rates of 32 to 96 kHz and bitrates of 16 to 128 kbps, as ADTS at every rate and as M4A at 44.1 kHz), which it must name, and against 450 files of fdkaac 1.0.0, none of which it may name: 432 HE-AAC and HE-AAC v2 files (ADTS and M4A, mono and stereo, over the sample rates from 16 to 96 kHz that fdkaac accepts, bitrates of 16 to 128 kbps and variable bitrate modes) and 18 low complexity ADTS files. fdkaac's fixtures have a start frequency of 10 or 12, a stop frequency of 9 and the default extra fields.
  - Other encoders' HE-AAC: `check_sbr_samples.py` holds the rule against 13 downloaded files in FFmpeg's FATE suite (SHA-256 pinned, fetched in CI): the MPEG-4 audio SBR and parametric stereo conformance streams `al_sbr_*` (HE-AAC at 32 and 48 kHz, one in 5.1) and Coding Technologies' aacPlus decoder check streams `CT_DecoderCheck` (`File1` to `File7`, also published as `sbr_*`). None is named. Together with fdkaac's files, that is the rule's cross-encoder basis, thinner than exhale's.
  - Not at hand, so not checked: Nero's and Dolby's HE-AAC encoders.
  - Apple's HE-AAC writes no SBR layout of its own to use: its headers differ with the bitrate and rate (bs_start_freq 10 to 14, bs_stop_freq 7 to 12, bs_freq_scale 0 to 3, bs_noise_bands 1 to 3, with or without bs_header_extra_1), and it uses frame grids and envelope coding that the FAAC rule excludes; the FAAC rule names none of its files.
  - The private repository's samples are xHE-AAC, whose SBR sits in the UsacConfig and not in fill elements, so the rule does not apply to them.
  - The hand-built `faac-style-*` fixtures write FAAC 2.2's layout at 44.1 kHz in 32 access units. `faac-style-plain` is named FAAC; in each of the others one access unit does one thing FAAC never does, and the stream is not named: extra data bits, channel coupling, a FIXVAR frame class, a VARVAR frame class, an envelope coded across time, a noise floor coded across time, sinusoids, extended data. The FAAC 2.1 and 2.0 fixtures (`faac-2.1-he-24k-adts`, `faac-2.0-he-24k`) repeat the header in their last access units. The other hand-built SBR fixtures use other fields (start frequency 5) and are not named.
- Apple's AAC encoder (AudioToolbox, as afconvert, iTunes and QuickTime use it) writes no text into the stream, so it is named from the way its files are written, which a remux drops:
  - in M4A, MP4, 3GP and 3GP2, with medium confidence, from the MP4 parser: a DecoderConfigDescriptor whose stream type byte is 0x14, the reserved bit that ISO/IEC 14496-1 7.2.6.6 sets to 1 written as 0, together with an sbtd box in the sample entry (M4A) or a free box that pads the audio data to a 4,096-byte boundary (all of them; `afconvert --no-filler` turns it off);
  - in ADTS, with low confidence: the MPEG-2 ID, no CRC, the original/copy bit set and an adts_buffer_fullness of 0 in every frame (at least 20 frames); the other flag bits clear;
  - the name is "Apple AAC encoder (AudioToolbox)", without a version: the files hold none;
  - what the rule rests on: 112 files of afconvert on macOS 15.7 (56 each on arm64 and x86-64: AAC-LC including 5.1, HE-AAC, HE-AAC v2, AAC-LD and AAC-ELD) all show it, 16 ADTS files (8 each) show the ADTS headers (a ninth, 0.5 s of 5.1 HE-AAC, has 12 frames and is not named), and none of the 83 other MP4 fixtures (85 esds boxes: ffmpeg, fdkaac, FAAC, MP4Box, hand-built, all with 0x15) or 55 other ADTS fixtures (original/copy bit clear, buffer fullness 0x7FF, 256 or varying) does;
  - what it does not rest on: files written by iTunes, QuickTime or Music, Logic, or Apple's hardware, which were not at hand; they use the same encoder and file writer, but may differ in the metadata they add (iTunes writes Encoding Params and ©too, which are shown and, for ©too, a claim);
  - the AAC stream itself shows no habit that fdkaac's and others' fixtures do not share: both end many access units with a fill element of count 0, Apple's first access unit carries a long EXT_FILL, and its bit allocation differs between runs on arm64 and x86-64 (below).
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
  - an ADTS file of 2.x LC, or a 1.x file cut after its first access units, shows no signature, so only the ©too claim in MP4 names it.

## What Apple's encoder writes

From `afconvert` on macOS 15.7.9 (AudioToolbox 1.14, `afconvert` 2.0), arm64 and x86-64, 2 s of a test signal:

- M4A: `ftyp` brand M4A (compatible M4A, mp42, isom); `moov` before the audio data; a `free` box that pads the audio data to offset 4,096; a `udta/meta` with an `mdir` handler (vendor `appl`) and one item, the freeform iTunSMPB; no ©too, no Encoding Params, no tags copied from the source (a WAV with INFO tags gives the same file); no edit list, so the priming is in iTunSMPB only, and the track lasts delay plus samples plus padding.
- iTunSMPB: an encoder delay of 2,112 samples for AAC-LC, HE-AAC and HE-AAC v2 (counted at the output rate), the padding that completes the last access unit (824 for 88,200 samples of AAC-LC, 892 for 44,100 samples of HE-AAC), and the samples between. AAC-LD and AAC-ELD write other delays (256 to 584). afinfo prints the same figures as priming, valid and remainder frames.
- The sample entry: `mp4a` with an esds box (descriptor lengths in four bytes, as other muxers write them; buffer size 6,144 for stereo at 128 kbit/s; maximum bitrate above the average, 0 average for variable bitrate) and an sbtd box that holds a version and flags word and the source bit depth as text ("I16"); afinfo prints it as "source bit depth".
- The stream type byte of the DecoderConfigDescriptor is 0x14: the reserved bit is 0, where ISO/IEC 14496-1 requires 1.
- HE-AAC signaling in M4A is explicit and hierarchical: the AudioSpecificConfig names AAC-LC at the core rate (11,025 to 24,000 Hz) and a sync extension 0x2B7 for SBR, plus 0x548 for parametric stereo in HE-AAC v2; the media timescale and iTunSMPB count the output rate. ADTS cannot signal it, so the analyzer reports implicit signaling (`aac.implicit-sbr`). afconvert's HE-AAC v2 with a mono source gives mono HE-AAC.
- HE-AAC stereo uses SBR frames of the FIXVAR class, whose frequency resolutions are sent last envelope first (14496-3 4.4.2.2); the analyzer read them forwards and reported `aac.sbr-past-fill` for six of the Apple files and for three of the SBR conformance streams (`al_sbr_cm_48_2`, `al_sbr_cm_48_5.1`, `al_sbr_sr_48_2_fsaac48`, whose manifest recorded the finding) until this was fixed; faad2's trace, with a count of the bits each SBR payload takes added, showed that the payloads fit their fill elements.
- Rates: afconvert picks the output rate for the bitrate (64 kbit/s stereo from 44.1 kHz is written at 32 kHz; VBR at quality 0 at 22.05 kHz; 32 kbit/s stereo HE-AAC at 32 kHz).
- ADTS: ID 1 (MPEG-2), no CRC, original/copy 1, buffer fullness 0 in every frame, also at variable bitrate.
- CAF: `desc`, `kuki` (the esds contents for AAC, the ALAC configuration for ALAC), `chan`, `info` (approximate duration), `pakt` (packets, valid frames, priming, remainder), a `free` chunk that pads to 4,096, `data`; HE-AAC adds `ldsc`. CAF is not read yet.
- Apple Lossless in M4A (phase 3): an `alac` sample entry, an iTunSMPB with a delay of 0, a last packet whose duration in stts is shortened by the padding, so the track lasts delay plus samples; `mp4.itunsmpb-total` accepts that.
- arm64 and x86-64: no file is the same. Both runs write the same structure, the same iTunSMPB and the same number of access units, but different access unit sizes and spectral data (for AAC-LC at 128 kbit/s 29,496 of 37,044 bytes differ; an ADTS file 29,942 of 33,571); esds bitrates and HE-AAC/LC bitrates follow. The ALAC files differ only in the movie's timestamps. The detection uses the structure, which is the same.
- 5.1 works once afconvert is given the output layout (`-l MPEG_5_1_D`, alone or after the input's `-l WAVE_5_1_B` or `-l MPEG_5_1_A`; or a CAF that carries MPEG_5_1_D): without it `ExtAudioFileSetProperty ('cfmt') failed ('!dat')`, the client format being refused. The M4A has channel configuration 6 and no chan box; afinfo prints "5.1 (C L R Ls Rs LFE)".
- Options of macOS 15.7's afconvert, as the second run's logs show: `--soundcheck-generate` adds a freeform iTunNORM item and a `udta/ludt/tlou` loudness box to the track; `--condensed-framing 16` writes a compact sample size box (stz2) instead of stsz; `-t` in CAF adds a `srcI` chunk (the source format and file name); `--generate-hash` and `--no-filler` change nothing in an M4A of this size (the audio data stays on the 4,096-byte boundary); `--media-kind` is refused without `--soundcheck-generate`; `--prime-method` (0, 1, 2) fails with "Couldn't set audio converter property (-50)", and `aac#960` with `('cfmt') failed ('fmt?')`. None writes an Encoding Params item or a ©too.
- xHE-AAC: all 36 attempts failed (MP4, CAF and LOAS, 44.1 and 48 kHz, 16-bit integer and float sources, mono and stereo, default, constant bitrate and variable bitrate), each with `ExtAudioFileSetProperty ('cfmt') failed ('fmt?')`: afconvert on this macOS refuses to set the client format for `usac` with these inputs. That says nothing about other inputs or other APIs. AAC-ELD with SBR in mono fails on write (`('bada')`).

## Not yet covered

- USAC, ER AAC and the SBR envelope data (planned: entropy pass).
