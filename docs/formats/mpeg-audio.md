# MPEG audio

MP1, MP2 and MP3 (MPEG-1, MPEG-2 and MPEG-2.5, Layers I to III).

## What is read

- **Frames:** sync and resync, every header field, free format and the CRC-16.
- **Layer III:** the side information of every granule and the bit reservoir. Each frame's bytes are split into its own main data, other frames' main data and ancillary data.
- **Layer II:** the bit allocation, scfsi and scalefactors, and the MPEG-2 multichannel extension in the ancillary data.
- **VBR headers:** Xing, Info, LAME and VBRI. The LAME tag's CRC and its music CRC are verified.
- **mp3PRO and MP3 Surround:** detected from the signature their encoders put into the ancillary data of every frame.
- **Fraunhofer's Original File Length block.**

## Detection

- Sniffing gives class 90 for a stream start: a valid header followed by two more frames of the same stream, or whose one or two frames end exactly at the end of the data. It reports where the chain starts, its frames (up to 16) and how many of them pass the CRC their header gives (ISO/IEC 11172-3 2.4.1.4).
- A chain with a CRC (protection bit 0) further in than the start of the data is class 30, not 90, when none of its frames has the CRC its header gives: another format's frames can hold such a header at a fixed offset (AC-3's 768-byte frames hold a protected MPEG-2.5 Layer I header of 768 bytes). Chains at the start and unprotected chains are class 90.
- A free-format chain is only a header repeating at one distance, which other formats' data can make too. It is class 90 only when it is credible, and 30 otherwise, below DTS's 50 and every other format's chain:
  - its first three frames that fit in the data must be well-formed (audio data or side information that fits the frame, no forbidden Layer I allocation 15);
  - in Layers I and II, one of them must also be protected and have the CRC its header and bit allocation give, as an all-zero frame is valid there.
- The class only ranks formats. Parsing does not depend on it: a free-format stream without a CRC, or with damaged CRCs, is read as MPEG audio when it is the only candidate or the parser is forced.

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

- **Encoder, from the stream:** the LAME tag, and the version string LAME writes into ancillary data, name the encoder with a confidence level. Without a tag, Shine, Helix, Fraunhofer's encoders and TwoLAME are recognized from habits their encodes show, with low confidence.
- **Encoder, from tags:** an ID3v2 TSSE frame, which the ID3v2 specification defines as the used audio encoder and its settings, names the encoder with low confidence; naming the same encoder as low-confidence evidence from the stream, it raises that to medium. A different name is an information finding (`mpeg.encoder-claim-differs`).
- **Writer:** ffmpeg's muxer version ("Lavf60.16.100") in a TSSE frame, in an APEv2 Encoder item or in the LAME tag it writes into every MP3 it muxes, and any APEv2 Encoder item, whose key the APEv2 item key list does not give to the encoder. The writer is shown apart from the encoder; two claims that name different software are an information finding (`mpeg.writer-claim-differs`).

## Not yet covered

- Layer III scalefactors and Huffman spectral data, and Layer II samples (planned: entropy pass, MP3 first).
