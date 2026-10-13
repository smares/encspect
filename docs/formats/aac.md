# AAC

AAC in [MP4](mp4.md), and raw AAC files as ADTS or ADIF.

## What is read

- **AudioSpecificConfig:** including PCE, ELD and the top level of xHE-AAC's (USAC) configuration, and explicit SBR and PS signaling (HE-AAC and HE-AAC v2).
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
- exhale, from the layout of its UsacConfig, with medium confidence:
  - an audio pre-roll extension element first, with an empty configuration;
  - then only channel elements (SCE, CPE, LFE), without harmonic SBR or PVC, and no fill element;
  - at most one configuration extension, loudness info.
- The layout carries no version. A tool atom that names exhale supplies it ("exhale 1.2.2"); one that names another encoder is an info finding.
- The rule rests on exhale 1.2.2's writer (`bitStreamWriter.cpp`, `createAudioConfig`) and on other encoders' files, none of which has the layout:
  - Fraunhofer's demonstration files end the element list with a fill element, and the newer ones add a stream ID extension;
  - libxaac's encoder does both (checked by hand, not in CI);
  - the ISO/IEC 23003-3 conformance streams have a fill element and no pre-roll element.
- `check_usac_samples.py` checks the downloaded files in CI (see [development](../development.md)).
- Other encoders from the metadata, with low confidence.
