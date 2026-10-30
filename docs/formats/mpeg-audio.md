# MPEG audio

MP1, MP2 and MP3 (MPEG-1, MPEG-2 and MPEG-2.5, Layers I to III).

## What is read

- **Frames:** sync and resync, every header field, free format and the CRC-16.
- **Free format:** the frame length is the distance to the next matching header, and each audio frame shows the bitrate its length gives:
  - The header's **Bitrate** stays "free", the value the header holds.
  - A **Computed Bitrate** node under the frame (not under the header, since it is no header field) shows the frame's bytes, padding included, times 8, over the duration of its samples, in kbps to two decimals (`399.96 kbps`: 1306 bytes at 44100 Hz and 1152 samples). The JSON report holds the unrounded value; the text report and GUI show two decimals.
  - It is the value of the stream's "Bitrate" series at that frame, from the same function. Frames with a fixed bitrate have no such node, and neither has a truncated frame, whose length is not the frame's.
- **Layer III:** the side information of every granule and the bit reservoir. Each frame's bytes are split into its own main data, other frames' main data and ancillary data.
- **Layer II:** the bit allocation, scfsi and scalefactors, and the MPEG-2 multichannel extension in the ancillary data.
  - **Dynamic crosstalk in 3/1 and 2/2:** which base channel a copied channel takes its allocation from has two readings in cases the ISO conformance streams do not decide: the base channel on the copied channel's side (the description's rule), or the one `dyn_cross_LR` names (the behavior observed of the ISO reference software). The analyzer reads a frame the first way and, only where its multichannel CRC then fails and holds the second way, the second. The "Base Channel Source" node under "Dynamic Crosstalk Modes" says which was used, and a CRC mismatch that the other reading resolves is not reported. The cases are listed in `docs/design/mpeg-clean-room-rules.md`, section 1.
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
  - The free-format entry is named with the average bitrate its frame lengths give, in whole kbps: `free (400 kbps): 21 frames (100.0%)`.
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
  - Layers I and II count each channel's allocation, scalefactor selection information (scfsi, Layer II), scalefactors and samples. Above the intensity stereo bound both channels share the allocation and the samples, and those bits count toward neither channel nor the total; the scfsi and the scalefactors stay per channel.
  - The first channel is left in left/right stereo, dual channel and intensity stereo frames and mid in mid/side frames (in a frame with both tools only below the intensity stereo region), so a stream that switches between them mixes the two. The overall share is bit-weighted: the first channel's bits over both channels' bits in all frames.

Streams follow the stream rule shared with ADTS, AC-3 and DTS (the [plan](../PLAN.md), AC-3 and E-AC-3, "Framing and sync"):
- A change of version, layer, sample rate or free format ends the stream and starts another, also where an earlier configuration returns. `mpeg.stream-changed` is one Info finding with the number of changes.
- The file has at most 64 streams. The frames from the change that would start stream 65 on are in no stream and are counted in one warning, `mpeg.streams-limited`.

Each stream, which may span regions, reports its codec, duration, average bitrate and encoder, and for graphs:

- the bitrate and the bit reservoir fill per frame and per second, under the heading "Bit Distribution" (concept `bitDistribution`), each pair one `quantity` ("Bitrate", "Reservoir Fill");
- a **declared range** on two series (JSON `declaredRange`, see `docs/cli.md`): the per-frame "Bitrate" declares the steps of its version and layer's bitrate table (the 14 nonzero, unreserved entries), none for a free-format stream; "Channel Bit Share" and "Channel Bit Share Over Time" declare 0 to 100;
- the **category series** of the coding, derived at the end from the kept frame records (one code per frame, 4 per Layer III frame of two channels for the block types), with the counts of the tally node each is anchored to, under the same names, and a time index of the seconds:
  - "Channel Modes" (no heading, in "Other");
  - "Stereo Coding", "Joint Stereo Modes" (Layer III) and "Intensity Stereo Bounds" (Layers I and II), under "Stereo Coding";
  - "Block Usage" (the value of each granule and channel: granule 1 channel 1, granule 1 channel 2, granule 2 channel 1, ..., one granule in MPEG-2 and 2.5; the second channel of a mono frame has no value) and "Block Sizes", its grouping, under "Block Switching".
  - The series of a stream that a tag splits are over the stream, the tally's over each region; `CategorySummaryTests` sums them per group of regions and streams that overlap.
  - A series exists where its tally node does: a file without joint stereo frames has no "Joint Stereo Modes".
- the **numeric series** "Coded Bandwidth" (Hz, Layers I and II, `codedBandwidth`) and "Channel Bit Share" (percent, stereo, joint stereo and dual channel frames, `bitDistribution`), each per frame and per second ("Coded Bandwidth Over Time" is each second's highest, "Channel Bit Share Over Time" bits over bits of the frames that start in it), anchored to the statistics nodes of the same names. A frame without a value (a mono frame in a stream that changes mode, a frame whose bits are all 0, a Layer I or II frame whose allocation does not fit) has no point, so such a series has its own x values; a mono stream has no share.

## Ancillary data

The rules by which the analyzer finds the ancillary data of each frame, counts it and recognizes the mp3PRO and MP3 Surround signatures. No specification is public for these; the rules give the same results as mp3guessenc 0.27.6, which the MPEG cross-check confirms on the fixtures. They are written as rules, and the code follows them, not mp3guessenc's code.

### Terms

- **Stream:** consecutive Layer III frames with the same MPEG version, sample rate and free-format state.
  - Everything the rules keep (reservoir, first frame, signature, segment count, Original File Length block, audio frames and samples) belongs to one stream, and each stream starts afresh.
  - A stream also ends where R1 closes the scan, and at an embedded tag: the statistics are per region, between tags, and sum the counts of the streams in it (R5); detection (R4) and the block (R3) are reported for each stream.
- **Frame data:** in each frame, the bytes after the header, the CRC if present, and the side information.
- **Reservoir:** the frame data of the stream's frames, joined in file order into one numbered byte sequence that starts at 0 for each stream.
  - A Xing, Info or VBRI header frame adds its frame data to the reservoir but has no main data of its own.
  - A truncated frame adds whatever frame data it has. A frame is truncated when it has fewer bytes than its header gives: the file, an embedded tag or the start of another stream ends it early, or a frame header of its own stream begins inside it.
- **A frame's main data:**
  - It starts main_data_begin bytes before the position where that frame's own frame data begins.
  - Its length is the sum of part2_3_length over its granules and channels, in bits, rounded up to whole bytes.
- **First frame:** the stream's first frame that has side information, not counting a VBR header frame.

### R1. Segments

- **What a segment is:** each frame with side information, called P, owns at most one ancillary segment. It runs from the byte after P's main data ends to the byte where the next frame with side information, N, starts its main data.
- **The last frame:** the segment of the last such frame runs to the end of the reservoir.
- **The scan closes** at a stream change or a non-Layer-III frame, and anywhere a frame's side information is missing. The frame before that point keeps the rest of the reservoir as its segment. A new stream starts empty.
- **Skips:**
  - P has no segment if P's main data would start before position 0 or would end past the reservoir as it stands after P.
  - The segment between P and N is skipped if N's main data would start before position 0 or before P's main data ends.
- **An empty segment** (start equal to end) is still a segment.
- **Gaps:** junk between two frames of one stream does not close the scan. The reservoir continues across it without the junk's bytes, so a segment can join bytes from both sides of the gap.

### R2. Signature

- **Start value:** the two signature bytes start as 0xFF, 0xFF.
- **An empty segment** sets both to 0x00.
- **Any other segment** ANDs its byte 0 into signature byte 0 and, if it has at least two bytes, its byte 1 into signature byte 1. R3 gives the exceptions for the first frame's segment.

### R3. Original File Length block, each stream's first frame's segment only

- **mp3PRO** (MPEG-2 or 2.5): the segment is longer than 20 bytes and starts 0xE0 0xBA.
  - The delay is a big-endian 16-bit value at bytes 14-15; the original length is a big-endian 32-bit value at bytes 16-19, in samples.
  - Both count at twice the stream's sample rate.
  - The segment still feeds bytes 0 and 1 to the signature as in R2.
- **Plain MP3 or MP3 Surround** (MPEG-1): the segment is longer than 9 bytes, byte 0 is 0xB4, and byte 1 is 0x04 or 0x08.
  - The delay is a big-endian 16-bit value at bytes 1-2; the length is a big-endian 32-bit value at bytes 3-6, at the stream's rate.
  - Report the delay as stored.
  - **Byte 1 is 0x08 (MP3 Surround):** AND byte 10 into signature byte 0 and byte 11 into signature byte 1, each only where the segment is that long, and do not AND bytes 0 and 1.
  - **Byte 1 is 0x04:** the segment changes the signature not at all.
- **Report as:**
  - the block's file offset;
  - the bytes it spans: 20 for mp3PRO and 7 for MPEG-1, mapped through the reservoir to file positions, as ranges where the bytes are not contiguous in the file (a block that continues into the next frame skips that frame's header and side information);
  - a warning where the delay plus the length exceed the stream's samples, counted at the block's rate.

### R4. Detection

- **The test applies to each stream, and only when** the stream has at least 8 segments and their bytes total more than zero.
- **mp3PRO:** the stream is not MPEG-1, signature byte 0 has all of the bits 0xC0 set, and byte 1 has the bit 0x08 set.
- **MP3 Surround:** the stream is MPEG-1 at 44100 or 48000 Hz, byte 0 has all of the bits 0xCF set, and byte 1 has all of the bits 0x30 set.
- **Report:** the total segment bytes and their data rate over the stream's audio frames.

### R5. Layer III statistics

- **Total:** the bytes of all segments of all streams of the region, except the segments of truncated frames. The statistics set the total against the bytes and the time of the complete audio frames, which leave a truncated frame out; counting its segment would let the share exceed 100 percent.
- **Smallest and largest:** the smallest and largest of the segments counted in the total that are not empty; 0 when there is none.
- **Kept separately:** the bytes of the last segment, for the BladeEnc trait.

### L1. Layers I and II

- **Which frames count:** each complete audio frame whose audio data, meaning header, CRC, allocation, scfsi, scalefactors and samples as ISO/IEC 11172-3 defines them, fits within the frame.
- **Ancillary bytes of a frame:** take the bits from the end of the audio data to the end of the frame, divided by 8 and rounded down. Then subtract the padding slot when the padding bit is set: 4 bytes in Layer I, 1 in Layer II.
- **Padding is Encspect's convention:** ISO/IEC 11172-3 counts the padding slot inside ancillary_data(), so the per-frame "Ancillary Data" node includes it; leaving it out of these statistics matches the cross-check tool. A padded Layer II frame with 5 bytes after the samples shows 5 bytes in its node and counts 4.
- **Count only results greater than zero.** These go into the same total, smallest and largest as R5.

## Channel layout

Mono, stereo, or the two discrete programs of dual channel. Where the file carries the MPEG-2 multichannel extension, its configuration, such as 3/2 + LFE as 5.1, with multilingual channels counted apart.

## Encoder detection

- **Encoder, from the stream:** the LAME tag, and the version string LAME writes into ancillary data, name the encoder with a confidence level. Without a tag, Shine, Helix, Fraunhofer's encoders and TwoLAME are recognized from habits their encodes show, with low confidence.
- **Encoder, from tags:** an ID3v2 TSSE frame, which the ID3v2 specification defines as the used audio encoder and its settings, names the encoder with low confidence; naming the same encoder as low-confidence evidence from the stream, it raises that to medium. A different name is an information finding (`mpeg.encoder-claim-differs`).
- **Writer:** ffmpeg's muxer version ("Lavf60.16.100") in a TSSE frame, in an APEv2 Encoder item and in the LAME tag it writes, to seekable output with the Xing header enabled (FFmpeg n6.1.1 `mp3enc.c`): a tag laid out as its muxer writes it (an encoder string of `Lavc…` or `Lavf…`, or the CRC over the first 190 bytes) names the writer "FFmpeg" without a version, with low confidence; a bare `Lavf` is what it writes without encoder metadata. Any APEv2 Encoder item is the writer's too. ffmpeg's `Lavc…` encoder ID names the encoder. The writer is shown apart from the encoder; two claims that name different software are an information finding (`mpeg.writer-claim-differs`), and software known to write another format is `mpeg.writer-other-format`.

## Not yet covered

- Layer III scalefactors and Huffman spectral data, and Layer II samples (planned: entropy pass, MP3 first).
