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

- ffmpeg's and FAAC's encoders, from the version they write into a fill element, with high confidence.
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
