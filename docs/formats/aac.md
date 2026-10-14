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
    - 2.0 and 2.1 add bs_freq_scale 0 with a bs_stop_freq of 10, and are not held to the header spacing (they repeat it in the last access units of some files);
    - every sbr_data payload is plain: no extra data bits, no coupling, only FIXFIX and VARFIX frame classes, every envelope and noise floor coded across frequency, no sinusoids and no extended data;
    - at least three payloads, none unread.
  - `check_faac_sbr.py` holds the rule against FAAC 2.0, 2.1 and 2.2 (98 HE-AAC files each, over sample rates of 32 to 96 kHz, mono and stereo, and bitrates of 16 to 128 kbps, as ADTS and M4A), which it must name, and against 450 files of fdkaac 1.0.0 (HE-AAC and HE-AAC v2, both containers, bitrates and VBR modes), which it must not. fdkaac's fixtures have a start frequency of 10 or 12, a stop frequency of 9 and the default extra fields.
  - Not at hand, so not checked: Nero's, Apple's and Dolby's HE-AAC encoders.
  - The private repository's samples are xHE-AAC, whose SBR sits in the UsacConfig and not in fill elements, so the rule does not apply to them.
  - The hand-built SBR fixtures use other fields (start frequency 5) and are not named.
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

## Not yet covered

- USAC, ER AAC and the SBR envelope data (planned: entropy pass).
- Apple's encoder (planned: phase 2).
