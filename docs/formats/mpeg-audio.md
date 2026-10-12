# MPEG audio

MP1, MP2 and MP3 (MPEG-1, MPEG-2 and MPEG-2.5, Layers I to III).

## What is read

- **Frames:** sync and resync, every header field, free format and the CRC-16.
- **Layer III:** the side information of every granule and the bit reservoir. Each frame's bytes are split into its own main data, other frames' main data and ancillary data.
- **Layer II:** the bit allocation, scfsi and scalefactors, and the MPEG-2 multichannel extension in the ancillary data.
- **VBR headers:** Xing, Info, LAME and VBRI. The LAME tag's CRC and its music CRC are verified.
- **mp3PRO and MP3 Surround:** detected from the signature their encoders put into the ancillary data of every frame.
- **Fraunhofer's Original File Length block.**

## Statistics

Each MPEG Audio region reports:

- duration, a bitrate summary, and the bitrate distribution with the frame sizes of each bitrate;
- the counts a VBR header claims against the ones counted, and the gapless sample count;
- the coding statistics mp3guessenc and EncSpot print:
  - frames per channel mode;
  - the stereo coding of joint stereo frames (left/right, mid/side, intensity stereo), or their intensity stereo bound;
  - Layer III block usage and global gain per channel;
  - the mean and largest main_data_begin;
  - the share of granules with scalefactor scaling;
  - whether padding, scfsi and scalefactor scaling are used;
  - the ancillary data with its share, data rate and smallest and largest amount per frame.

Each stream, which may span regions, reports its codec, duration, average bitrate and encoder, and the bitrate and bit reservoir fill per frame and per second for graphs.

## Channel layout

Mono, stereo, or the two discrete programs of dual channel. Where the file carries the MPEG-2 multichannel extension, its configuration, such as 3/2 + LFE as 5.1, with multilingual channels counted apart.

## Encoder detection

- **From tags:** the LAME tag, and the version string LAME writes into ancillary data, name the encoder with a confidence level.
- **From habits:** without a tag, Shine, Helix, Fraunhofer's encoders and TwoLAME are recognized from habits their encodes show, with low confidence.
