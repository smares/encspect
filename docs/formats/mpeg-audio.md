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
  - **Stereo Coding** over all frames: left/right stereo, mid/side stereo, intensity stereo, both tools, dual channel and mono. A joint stereo frame of Layer I or II is intensity stereo above its bound, as its header selects, even where a Layer II bound lies at or above the highest coded subband. EncSpot's mode 0 is the entries left/right stereo, dual channel and mono together; mp3guessenc prints the three tools' counts and the sum of all frames;
  - **Joint Stereo Modes**, the stereo coding of the joint stereo frames of Layer III only, or **Intensity Stereo Bounds**, their intensity stereo bound in Layers I and II;
  - Layer III **Block Usage** (long, start, short, mixed, stop, reserved, granules counted per channel) and global gain per channel;
  - **Block Sizes**, the same granules grouped as EncSpot counts them: long (with start and stop), short, mixed, and the reserved block type apart, which EncSpot counts as long;
  - the mean and largest main_data_begin;
  - the share of granules with scalefactor scaling;
  - whether padding, scfsi and scalefactor scaling are used;
  - the ancillary data with its share, data rate and smallest and largest amount per frame;
- **Coded Bandwidth** (Layers I and II): the lowest, most common and highest of the frames' coded bandwidths, the upper edge of the highest subband with a bit allocation other than 0 in either channel, in whole hertz. It is read from the allocation, a bound the data states; it is not the encoder's lowpass and not a measurement of decoded audio. Layer III states no such bound in its side information, so it has none;
- **Channel Bit Share** (stereo, joint stereo and dual channel frames of all layers): the lowest, overall and highest share of the first channel in a frame's audio bits.
  - Layer III counts part2_3_length of every granule and channel (the scalefactors and the Huffman-coded values), not the header, the side information or the padding.
  - Layers I and II count each channel's allocation, scalefactor selection information, scfsi (Layer II), scalefactors and samples. Above the intensity stereo bound both channels share the allocation and the samples, and those bits count toward neither channel nor the total; the scalefactor selection and the scalefactors stay per channel.
  - The first channel is left in left/right stereo, dual channel and intensity stereo frames and mid in mid/side frames (in a frame with both tools only below the intensity stereo region), so a stream that switches between them mixes the two. The overall share is bit-weighted: the first channel's bits over both channels' bits in all frames.

Streams follow the stream rule shared with ADTS, AC-3 and DTS (the [plan](../PLAN.md), AC-3 and E-AC-3, "Framing and sync"):
- A change of version, layer, sample rate or free format ends the stream and starts another, also where an earlier configuration returns. `mpeg.stream-changed` is one Info finding with the number of changes.
- The file has at most 64 streams. The frames from the change that would start stream 65 on are in no stream and are counted in one warning, `mpeg.streams-limited`.

Each stream, which may span regions, reports its codec, duration, average bitrate and encoder, and for graphs:

- the bitrate and the bit reservoir fill per frame and per second, under the heading "Bit Distribution" (concept `bitDistribution`), each pair one `quantity` ("Bitrate", "Reservoir Fill");
- the **category series** of the coding, derived at the end from the kept frame records (one code per frame, 4 per Layer III frame of two channels for the block types), with the counts of the tally node each is anchored to, under the same names, and a time index of the seconds:
  - "Channel Modes" (no heading, in "Other");
  - "Stereo Coding", "Joint Stereo Modes" (Layer III) and "Intensity Stereo Bounds" (Layers I and II), under "Stereo Coding";
  - "Block Usage" (the value of each granule and channel: granule 1 channel 1, granule 1 channel 2, granule 2 channel 1, ..., one granule in MPEG-2 and 2.5; the second channel of a mono frame has no value) and "Block Sizes", its grouping, under "Block Switching".
  - The series of a stream that a tag splits are over the stream, the tally's over each region; `CategorySummaryTests` sums them per group of regions and streams that overlap.
  - A series exists where its tally node does: a file without joint stereo frames has no "Joint Stereo Modes".
- the **numeric series** "Coded Bandwidth" (Hz, Layers I and II, `codedBandwidth`) and "Channel Bit Share" (percent, stereo, joint stereo and dual channel frames, `bitDistribution`), each per frame and per second ("Coded Bandwidth Over Time" is each second's highest, "Channel Bit Share Over Time" bits over bits of the frames that start in it), anchored to the statistics nodes of the same names. A frame without a value (a mono frame in a stream that changes mode, a frame whose bits are all 0, a Layer I or II frame whose allocation does not fit) has no point, so such a series has its own x values; a mono stream has no share.

## Channel layout

Mono, stereo, or the two discrete programs of dual channel. Where the file carries the MPEG-2 multichannel extension, its configuration, such as 3/2 + LFE as 5.1, with multilingual channels counted apart.

## Encoder detection

- **Encoder, from the stream:** the LAME tag, and the version string LAME writes into ancillary data, name the encoder with a confidence level. Without a tag, Shine, Helix, Fraunhofer's encoders and TwoLAME are recognized from habits their encodes show, with low confidence.
- **Encoder, from tags:** an ID3v2 TSSE frame, which the ID3v2 specification defines as the used audio encoder and its settings, names the encoder with low confidence; naming the same encoder as low-confidence evidence from the stream, it raises that to medium. A different name is an information finding (`mpeg.encoder-claim-differs`).
- **Writer:** ffmpeg's muxer version ("Lavf60.16.100") in a TSSE frame, in an APEv2 Encoder item and in the LAME tag it writes, to seekable output with the Xing header enabled (FFmpeg n6.1.1 `mp3enc.c`): a tag laid out as its muxer writes it (an encoder string of `Lavc…` or `Lavf…`, or the CRC over the first 190 bytes) names the writer "FFmpeg" without a version, with low confidence; a bare `Lavf` is what it writes without encoder metadata. Any APEv2 Encoder item is the writer's too. ffmpeg's `Lavc…` encoder ID names the encoder. The writer is shown apart from the encoder; two claims that name different software are an information finding (`mpeg.writer-claim-differs`), and software known to write another format is `mpeg.writer-other-format`.

## Not yet covered

- Layer III scalefactors and Huffman spectral data, and Layer II samples (planned: entropy pass, MP3 first).
