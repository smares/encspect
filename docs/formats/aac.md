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

- ffmpeg's and FAAC's encoders, from the version they write into a fill element.
- Other encoders from the metadata, with low confidence.
