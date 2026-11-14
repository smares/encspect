# MPEG audio

MP1, MP2 and MP3 (MPEG-1, MPEG-2 and MPEG-2.5, Layers I to III).

## What is read

- **Frames:** sync and resync, every header field, free format and the CRC-16.
- **Free format:** the frame length is the distance to the next matching header, and each audio frame shows the bitrate its length gives:
  - The header's **Bitrate** stays "free", the value the header holds.
  - A **Computed Bitrate** node under the frame (not under the header, since it is no header field) shows the frame's bytes, padding included, times 8, over the duration of its samples, in kbps to two decimals (`399.96 kbps`: 1306 bytes at 44100 Hz and 1152 samples). The JSON report holds the unrounded value; the text report and GUI show two decimals.
  - It is the value of the stream's "Bitrate" series at that frame, from the same function. Frames with a fixed bitrate have no such node, and neither has a truncated frame, whose length is not the frame's.
- **Layer III:** the side information of every granule and the bit reservoir. Each frame's bytes are split into its own main data, other frames' main data and ancillary data. The scalefactors and the Huffman-coded values of every complete frame are decoded in every analysis (see "Layer III entropy pass").
- **Layer II:** the bit allocation, scfsi and scalefactors, and the MPEG-2 multichannel extension in the ancillary data.
  - **Dynamic crosstalk:** a channel that dynamic crosstalk leaves out and whose mode names no source copies the base channel on its own side, as ISO/IEC 13818-3 2.5.2.15 says: a left or left surround channel the left one, a right or right surround channel the right one, and a center or surround channel the one `dyn_cross_LR` names. The multichannel CRC checks it, since the number of scfsi fields it covers depends on which base channel a copy took its allocation from.
  - **The reference software's reading:** the ISO reference software was observed to let `dyn_cross_LR` name the source of every copy in 3/1 and 2/2, which the text does not say. A frame is read by the text first; where its multichannel CRC fails and the software's reading holds it, the frame is read that way and a warning (`mpeg-audio.multichannel-reference-software-reading`) says so.
- **VBR headers:** Xing, Info, LAME and VBRI. The LAME tag's CRC and its music CRC are verified.
- **mp3PRO and MP3 Surround:** detected from the signature their encoders put into the ancillary data of every frame.
- **Fraunhofer's Original File Length block.**

### Frames and sync

- **Sync:** a header starts a stream only where one of these holds:
  - two more frames of the same version, layer and sample rate follow at the positions its length predicts;
  - its one or two frames end exactly at the end of the data, and each passes the structural checks: in Layers I and II the audio data fits the frame and uses no forbidden bit allocation; in Layer III the side information fits, passes the side-information checks below, and holds no more main data than the frame and its main_data_begin.
  - So a file of one or two frames still parses, and a single 4-byte match in other data is not taken for audio.
- **Leading junk:** the first frame is searched within 1 MiB after any leading zero bytes, which are common padding and cheap to skip.
- **Gaps:** everything between frames that is not a frame is a gap: junk before the first frame, lost sync between frames, data after the last one.
- **Regions:** each stretch of data between tags is one "MPEG Audio" region. Findings of one kind are reported once per stream, with a count.
- **Once in sync,** a frame is taken on its header only while a frame follows it.
  - Where none does, a stream that starts inside the frame, after its header and CRC, cuts it short there. It is reported as a frame that was cut or whose header is damaged, so the next frame is not swallowed and the damage is located.
  - A free-format stream may instead have changed its frame length.
- **Stream changes** are reported both where frames of two streams touch and where sync is found again after a gap.
- **Free format:**
  - A frame too short for the header, CRC and side information or bit allocation is no frame, so data with a header every few bytes does not sync.
  - A free-format stream whose frame length changes breaks the fixed bitrate ISO/IEC 11172-3 requires: a warning.
  - Parsing asks no more of a free-format chain than of any other: a stream that starts at a chain of frames is read, protected or not.
- **Layer II CRC:** it covers the bit allocation and scfsi, so its length follows from the allocation tables.
- **MPEG 2.5** extends only Layer III, so Layer I or II frames at its rates are a warning; decoders play them.

### Layer I audio data

- Bit allocation per subband and channel (one above the joint stereo bound), the scalefactors, the length of the samples and the ancillary data after them (ISO/IEC 11172-3 2.4.1.5 and 2.4.2.5).
- Warnings:
  - the forbidden allocation 15: libmad rejects the frame, mpg123 reads 16-bit samples, as the report does;
  - scalefactor index 63;
  - audio data longer than the frame.

### Layer III side information

- **Fields:** per granule and channel part2_3_length, big_values, global_gain, scalefac_compress, block type and mixed-block flag, table selects, region counts, subblock_gain, preflag, scalefac_scale and count1table_select; scfsi per channel; mid/side and intensity stereo per frame.
- **Reservoir fill:** the free bytes after the previous frame's main data, up to what main_data_begin can reach (511 bytes in MPEG-1, 255 in MPEG-2 and 2.5).
- **The bit reservoir split:** each frame's bytes after the side information are split by owner, so every byte is accounted for:
  - its own main data;
  - main data of later frames ("Bit Reservoir");
  - ancillary data no frame uses.
- **Main data runs** through the data of consecutive frames of one stream, skipping headers and side information, and continue across junk and tags between frames.
  - A stream change, including a switch to or from free format, starts with an empty reservoir.
  - Reported: main data that begins before the stream (a cut file), overlaps the previous frame's (missing frames), reaches back across a gap (decoders disagree there) or does not fit the frame.
- **A Xing, Info or VBRI header frame** stays in the reservoir as a frame without main data, as minimp3 and mp3guessenc keep it.
  - mpg123 and ffmpeg skip it, so main data that begins in it is a warning, as after `ffmpeg -ss -c copy`. The header frame's unused bytes show which frame's main data they hold.
  - The header frame does not count as the stream's first frame, so main data that reaches back past it begins before the stream.
- **Side-information checks:** big_values above 288, the reserved block type, Huffman tables 4 and 14, scalefactors longer than part2_3_length, scfsi on a channel with short blocks, and mid/side or intensity stereo with different block types in the two channels. libmad rejects the last two.
- **The decoder buffer** (ISO/IEC 11172-3 2.4.3.4.4): the text sets a buffer of 7,680 bits, the length of a frame at the highest bitrate (bitrate_index 14, 320 kbps) at 48 kHz, and says that main_data_begin is always 0 at bitrate_index 14. For the lower sampling frequencies it says only that the buffer "should be constrained" so that the same physical buffer size is sufficient.
  - **The check:** main_data_begin plus the frame's length may not exceed the length of a frame at bitrate_index 14 at the stream's own sample rate (960 bytes at 48 kHz, 1,044 at 44.1 kHz, 1,440 at 32 kHz), and main_data_begin is 0 at 320 kbps. The text's "should" for the lower sampling frequencies is read as scaling with the sample rate (the lead's decision, 2026-11-14). At 48 kHz this is the text's rule. At 44.1 and 32 kHz it is looser than a literal 960 bytes, which 26 of the 210 MPEG-1 Layer III fixtures at those rates would break (among them the encodes of LAME 3.90's constant bitrate presets fm, hifi, cd and studio); the text says "should", and encoders and decoders go beyond it.
  - **MPEG-2 and 2.5 are not checked:** ISO/IEC 13818-3 states no buffer rule. Its 2.4.3.2 (Layer III at the lower sampling frequencies) changes the frame length constants, the intensity stereo rule and the scalefactor decoding and takes the rest from 11172-3 2.4.3.4, whose rule is set in MPEG-1 terms (bitrate_index 14 is 320 kbps). LAME's `--strictly-enforce-ISO` applies it to MPEG-2 with its top bitrate of 160 kbps; the text does not say so, so there is no finding and no fixture for it.
  - Breaking it is a warning, which says that encoders such as LAME exceed the limit and that decoders accept it; Encspect's LAME test files show it in VBR and ABR streams of releases from 3.20 to 4.0.

### Layer II audio data and multichannel

- **Bit allocation** per subband and channel from the allocation tables: ISO/IEC 11172-3 Tables B.2a to B.2d (2.4.3.3.1 selects among them), and Table B.1 of ISO/IEC 13818-3 (2.4.3.1) for the lower sampling rates.
  - Free format takes Table B.2a at 48 kHz and B.2b at the other rates, as their captions say and libmad reads it; mpg123 takes B.2a at every rate.
  - Also read: scfsi and scalefactors, the length of the sample data and where the ancillary data starts. The same tables let Layer II CRCs be verified.
- **Warnings:**
  - audio data longer than the frame, also where the allocation, scfsi or scalefactors already run past it; the CRC is still verified wherever the allocation and scfsi it covers fit;
  - scalefactor index 63, which Table B.1 of ISO/IEC 11172-3 leaves undefined, although TwoLAME writes it and ffmpeg, mpg123 and libmad decode it.
- **The MPEG-2 multichannel extension** (ISO/IEC 13818-3 2.5) rides in the ancillary data of an MPEG-1 Layer II frame, so the stereo base stays playable.
  - Read: the multichannel header (center, surround and LFE channels, audio mix, dematrix procedure, multilingual channels with their layer and sampling rate, whether an extension bitstream exists), the channel configuration such as 3/2 plus LFE, its CRC, and how many bytes the multichannel data takes.
  - The frame header has no flag for it, so it is detected by structure and consistency across frames, like a tag, never by a single match.
  - A file carries it once the multichannel CRC holds in at least two frames and in at least half of the frames whose multichannel side information fits the frame. Random ancillary data passes the 16-bit CRC about once in 65,536 frames, so a long recording passes it a few times by chance; mp3guessenc likewise wants a share of the stream.
  - Then each frame whose CRC fails is reported, and so is the reserved center value, which is read as no center.
  - An `n_ad_bytes` that declares more ancillary data than the frame holds after the multichannel header and CRC is a warning; the multichannel data of such a frame ends after its CRC.
- **Channels with the extension:** the stream's channels follow the extension where the file carries it and every frame whose multichannel CRC holds, or whose side information continues in the extension bitstream, states the same speakers.
  - The order: left and right from the base, then the center (phantom coded or not), the mono surround as the back center, the surround pair as the back pair or a second stereo program's two discrete channels, then the LFE. This is the order of mctoolamed's output channels.
  - 3/2 + LFE is 5.1 in six channels; multilingual channels are counted apart.
  - A possible, unverified extension keeps the stereo base, and so does a stream whose configuration changes, with an info finding.
  - The conformance manifest records each stream's channels and layout, and mctoolamed's channel files and multilingual channels confirm them.
- **The extension bitstream:** where one exists, the multichannel data continues in it, after the base frame's bits minus `n_ad_bytes` of MPEG-1 ancillary data at its end.
  - It is a separate file, so frames whose multichannel side information does not fit the base frame are reported as continuing there, with their CRC not verified.
  - Where no frame's side information fits, no CRC can confirm the extension. If nine in ten candidate frames carry the same multichannel header and continue in an extension bitstream, it is reported as a possible, unverified extension.
- Layers I and III can carry the extension too; the ISO conformance set has one stream of each (tests 22 and 23). They are not read yet.

### Xing, Info and VBRI headers

- **Xing and Info:** flags, frame and byte counts, the seek table, quality.
  - Read only in Layer III, where the side information would end without a CRC (36, 21 or 13 bytes into the frame, as LAME writes them even into a protected frame).
  - Read only when every byte between the frame header, or its CRC, and the header is zero, as mpg123 requires, so audio data that happens to contain "Xing" is not taken for one.
  - A header that does not fit its frame and a TOC that decreases are warnings.
  - The frame decodes as silence in decoders that do not skip it, so it stays in the bit reservoir as a frame without main data (see the bit reservoir above).
- **The LAME extension** is read only after an encoder string that starts `LAME`, `L` and a version (`L3.99a12`), or ffmpeg's `Lavc` or `Lavf`, as mp3guessenc and ffmpeg require. Other text there is shown as a string and names nothing.
  - Fields: encoder string, tag revision, VBR method, lowpass, peak, ReplayGain fields, encoding flags, ATH type, minimum or ABR bitrate, delay and padding, noise shaping, stereo mode, source sample rate, MP3 gain (a sign bit and seven bits of 1.5 dB steps), preset, music length (0 means not set, an info), tag CRC and music CRC.
  - The tag CRC covers the frame's bytes before it; the music CRC the bytes after the frame up to the music length, which counts from the start of the frame. Both are verified.
- **A failing tag CRC or music CRC** is a warning, also where the encoder is to blame: the field breaks the tag's specification and readers act on it. Two such causes get a finding of their own that names them:
  - ffmpeg writes the tag CRC over the frame's first 190 bytes wherever the tag starts, which is only correct for MPEG-1 stereo, so LAME and ffmpeg itself reject it and ignore the tag's encoder string and ReplayGain;
  - in a file that continues a `--nogap` sequence, LAME continues the previous file's music CRC, so it cannot confirm that the audio is unchanged, and an edit such as MP3Gain's would look no different.
  - Otherwise a failing music CRC means the audio changed after encoding.
- **Music length:** one shorter than the tag's own frame leaves the music CRC unverified, with a finding of its own. So does one that runs past the end of the file: an info where the stream is cut short (its own findings report that), a warning where the stream holds every frame its header counts.
- **Old LAME versions:**
  - LAME before 3.90 wrote only a version string, which is reported as such.
  - LAME 3.90 to 3.92 have Dibrom's `--alt-preset` modes and the older `--preset` names but leave the preset field empty, which 3.93 added. Where the VBR method, Xing quality, noise shaping, stereo mode, ATH type, lowpass and (but for VBR) bitrate field are those a preset sets, the report names the preset next to the empty field.
  - The preset settings come from encodes with every preset by 3.90 and 3.92 built from source, and by the Windows builds of 3.91 and of HydrogenAudio's 3.90.3, the 3.90 line it recommended for years, keyed as getID3 keys them.
  - Those two Windows builds write some lowpasses 100 Hz higher (getID3 lists the same for 3.91), and 3.90.3 shapes noise differently in standard and extreme. getID3's other 3.90.3 values, and its medium presets, belong to a modified build and are not used.
  - 3.90.3's version, "LAME3.90.3", does not fit the tag's nine bytes, which hold "LAME3.90."; LAME writes it whole into the padding of the frames, from where the analyzer completes it, as getID3 and mp3guessenc do.
  - LAME before 3.95 computed ReplayGain against 83 dB instead of 89 dB, so a gain it determined itself reads 6 dB lower than a later version's. The report says so next to the value, which it keeps as stored (mpg123 adds the 6 dB).
- **VBRI** (Fraunhofer): version, delay, quality, byte and frame counts and the seek table, 36 bytes into the frame in every mode. Warnings: a version other than 1, which ffmpeg ignores, and a seek table entry size outside 1 to 4 bytes, which leaves the table unread.
- **A VBR header in the middle of a stream,** after any earlier frame of it, across gaps and tags, is a warning: files were probably joined, so the first header's counts and TOC cover only part of the stream, and players that only look at the first frame play the second as a frame of silence.

### Layer III entropy pass

- **What is read:** the scalefactors of each granule and channel (with scfsi, and the MPEG-2 partitions of intensity stereo), and the Huffman-coded values of the big-values and count1 regions with the tables of ISO/IEC 11172-3 Annex B. The tables, the scalefactor band widths and the polyphase window come from minimp3 as format data (`scripts/fixtures/mpeg_tables.py`, `--check` in CI), which names each table's source ([development.md](../development.md#mpeg-audio-decoding)).
- **In every analysis:** the walk runs while the frames are read, with or without a decoder, because it gives Coded Bandwidth. It needs a frame's main data, which `BitReservoir` places: the bytes of the frame's own data and those of earlier frames it reaches back to. A frame whose main data are not available (`mpeg-audio.main-data-before-stream`, `mpeg-audio.main-data-overlap`, `mpeg-audio.main-data-in-vbr-header-frame`, `mpeg-audio.main-data-overflow`) is not walked and has no coded bandwidth.
- **What it finds:** a granule whose Huffman-coded values run past its part2_3_length, which no stuffing explains, is one warning, `mpeg-audio.huffman-overrun`, with a count; the frame is still decoded. Side information that breaks (more than 288 big_values pairs, the reserved block type, part2_3_length beyond the data) makes the frame unreadable: a decoder is not given it. Values past the end read on into the bytes of the frame that follow its main data (as minimp3's Huffman loop does, which the PCM cross-check follows), and as zeros past the frame's end; the specification defines neither.
- **Layers I and II:** the sample codes are read only where a decoder asks for them, since they give no figure.

## Detection

- Sniffing gives class 90 for a stream start: a valid header followed by two more frames of the same stream, or whose one or two frames end exactly at the end of the data. It reports where the chain starts, its frames (up to 16) and how many of them pass the CRC their header gives (ISO/IEC 11172-3 2.4.1.4).
- A chain with a CRC (protection bit 0) further in than the start of the data is class 30, not 90, when none of its frames has the CRC its header gives: another format's frames can hold such a header at a fixed offset (AC-3's 768-byte frames hold a protected MPEG-2.5 Layer I header of 768 bytes). Chains at the start and unprotected chains are class 90.
- A free-format chain is only a header repeating at one distance, which other formats' data can make too. It is class 90 only when it is credible, and 30 otherwise:
  - its first three frames that fit in the data must be well-formed (audio data or side information that fits the frame, no forbidden Layer I allocation 15);
  - in Layers I and II, one of them must also be protected and have the CRC its header and bit allocation give, as an all-zero frame is valid there.
- A class 30 chain never identifies a file (`SniffResult.WeakClass`; the host drops it from the ranking, see [architecture.md](../architecture.md#classes)). Another format's match wins as before. Where nothing else matches, the file is reported as unrecognized, with the info finding `format.weak-candidate` naming the chain, its offset, its frame count and why it is not trusted (`free format` or `protected, no CRC holds`), so a genuine but damaged stream is still pointed out. A free-format stream without a CRC (Layer I or II) or a protected stream whose CRCs all fail is therefore read as MPEG audio only when the parser is forced (`--as`), which parses it as any other chain.
- Fixtures: `free-format-weak-chain` (random data holding a Layer I free-format header six times, as an installer's binary data can) and `protected-weak-chain` (six protected frames whose CRCs all fail, after 300 bytes of data).

## Statistics

Each MPEG Audio region reports:

- **Units counted:** each audio frame counts the samples its header declares (1,152, 576 or 384). A frame cut by the end of the data or by the next frame counts them too, in Samples and Duration, and they are also reported as Lost Samples (and the stream's `lostSamples`); the frame count, the bitrates and the distribution leave the cut frame out.
- duration, a bitrate summary, and the bitrate distribution with the frame sizes of each bitrate;
  - The free-format entry is named with the average bitrate its frame lengths give, in whole kbps: `free (400 kbps): 21 frames (100.0%)`.
- the counts a VBR header claims against the ones counted, and the gapless sample count;
- the coding statistics mp3guessenc and EncSpot print:
  - frames per channel mode;
  - **Stereo Coding** over all frames: left/right stereo, mid/side stereo, intensity stereo, both tools, dual channel and mono. A joint stereo frame of Layer I or II is intensity stereo above its bound, as its header selects, even where a Layer II bound lies at or above the highest coded subband. EncSpot's mode 0 is the entries left/right stereo, dual channel and mono together; mp3guessenc prints the three tools' counts and the sum of all frames;
  - **Joint Stereo Modes**, the stereo coding of the joint stereo frames of Layer III only, or **Intensity Stereo Bounds**, their intensity stereo bound in Layers I and II, as the band the mode extension selects;
  - **Intensity Stereo Bound** (lowest and highest, in whole hertz): the lowest frequency from which a joint stereo frame codes intensity stereo, as the lower edge of its bound subband (Layers I and II: `bound` × a 64th of the sample rate, where the bound is below the highest coded subband) or, in Layer III, of the lowest band, over the frame's granules, whose right channel carries intensity positions: the bands above the last one in which the right channel has a nonzero pair, whose position is legal (ISO/IEC 11172-3 2.4.3.4.9.3; the rule of minimp3's `L3_stereo_top_band`, taken from the quantized values the walk reads, so a band the right channel codes as zero counts as intensity). A granule whose right channel is all zero has its bound at 0 Hz, which a frame then states. In MPEG-2 and 2.5 the illegal position is the largest value the band's scalefactor length can code (ISO/IEC 13818-3), which the walk keeps as the decoder does. A frame without such a band, and a Layer III frame the entropy pass did not walk, has none;
  - Layer III **Block Usage** (long, start, short, mixed, stop, reserved, granules counted per channel) and global gain per channel;
  - **Block Sizes**, the same granules grouped as EncSpot counts them: long (with start and stop), short, mixed, and the reserved block type apart, which EncSpot counts as long;
  - the mean and largest main_data_begin, and the largest and the mean reservoir fill ("Maximum Reservoir Fill" and "Average Reservoir Fill", the bytes the bit reservoir holds as a frame starts);
  - **Verified Frame Checksums** and, above 0, **Failed Frame Checksums**: the protected frames whose stored CRC-16 equals the one computed over the header bits and the side information or bit allocation it covers (a frame whose covered bytes are cut has none and is in neither count);
  - the share of granules with scalefactor scaling;
  - whether padding, scfsi and scalefactor scaling are used;
  - the ancillary data with its share, data rate and smallest and largest amount per frame;
- **Coded Bandwidth:** the lowest, most common and highest of the frames' coded bandwidths, in whole hertz. It is a bound the coded data states, not a measurement of decoded audio and not the encoder's lowpass setting:
  - Layers I and II: the upper edge of the highest subband with a bit allocation other than 0 in either channel, read from the allocation.
  - Layer III: the upper edge of the highest frequency line that the big-values and count1 regions of a granule and channel code, over the frame's granules and channels, from the entropy pass. The count1 region is read a quadruple of lines at a time, so the edge is known to four lines (153 Hz at 44,100 Hz). A quadruple that does not fit in the 576 lines is not read: a count1 region that begins at an even pair of lines leaves 574 lines, and its last quadruple, which BladeEnc, Fraunhofer's encoders, Gogo, Shine and LAME 3.20 write there in Encspect's test files, adds nothing, so those granules end at line 574 (21,973 Hz at 44,100 Hz), as in libmad, mpg123 and FFmpeg (minimp3 decodes its first two lines). No finding is raised for it: the stream is not damaged, and the writer's last quadruple only carries values beyond the spectrum. A short block has three windows of 192 lines, and a line takes its frequency from the band it lies in. A frame the entropy pass did not walk (its main data are not available, or its side information breaks) has no value.
- **Channel Bit Share** (stereo, joint stereo and dual channel frames of all layers): the lowest, overall and highest share of the first channel in a frame's audio bits.
  - Layer III counts part2_3_length of every granule and channel (the scalefactors and the Huffman-coded values), not the header, the side information or the padding.
  - Layers I and II count each channel's allocation, scalefactor selection information (scfsi, Layer II), scalefactors and samples. Above the intensity stereo bound both channels share the allocation and the samples, and those bits count toward neither channel nor the total; the scfsi and the scalefactors stay per channel.
  - The first channel is left in left/right stereo, dual channel and intensity stereo frames and mid in mid/side frames (in a frame with both tools only below the intensity stereo region), so a stream that switches between them mixes the two. The overall share is bit-weighted: the first channel's bits over both channels' bits in all frames.

Streams follow the stream rule shared with ADTS, AC-3 and DTS ([architecture](../architecture.md#stream-changes), "Stream changes"):
- A change of version, layer, sample rate or free format ends the stream and starts another, also where an earlier configuration returns. `mpeg-audio.stream-changed` is one Info finding with the number of changes.
- The file has at most 64 streams. The frames from the change that would start stream 65 on are in no stream and are counted in one warning, `mpeg-audio.streams-limited`.

Each stream, which may span regions, reports its codec, duration, average bitrate and encoder, and for graphs:

- the bitrate and the bit reservoir fill per frame and per second, under the heading "Bit Distribution" (concept `bitDistribution`), each pair one `quantity` ("Bitrate", "Reservoir Fill"; the name, unit and axes of "Reservoir Fill" are WMA's too, see [`docs/development.md`](../development.md#names-of-statistics-and-series), "Names of statistics and series");
- a **declared range** on two series (JSON `declaredRange`, see [`docs/cli.md`](../cli.md)): the per-frame "Bitrate" declares the steps of its version and layer's bitrate table (the 14 nonzero, unreserved entries), none for a free-format stream; "Channel Bit Share" and "Channel Bit Share Over Time" declare 0 to 100;
- the **category series** of the coding, derived at the end from the kept frame records (one code per frame, 4 per Layer III frame of two channels for the block types), with the counts of the tally node each is anchored to, under the same names, and a time index of the seconds:
  - "Channel Modes" (no heading, in "Other");
  - "Stereo Coding", "Joint Stereo Modes" (Layer III) and "Intensity Stereo Bounds" (Layers I and II), under "Stereo Coding";
  - "Block Usage" (the value of each granule and channel: granule 1 channel 1, granule 1 channel 2, granule 2 channel 1, ..., one granule in MPEG-2 and 2.5; the second channel of a mono frame has no value) and "Block Sizes", its grouping, under "Block Switching".
  - The series of a stream that a tag splits are over the stream, the tally's over each region; `CategorySummaryTests` sums them per group of regions and streams that overlap.
  - A series exists where its tally node does: a file without joint stereo frames has no "Joint Stereo Modes".
- the **numeric series** "Intensity Stereo Bound" (Hz, all layers, concept `stereoCoding`, a quantity of its own on the axis of "Coded Bandwidth" (Hz over the frame offset), per frame only, at the joint stereo frames that have a bound; the name, unit and axis Opus and AAC use for the same concept), "Coded Bandwidth" (Hz, all layers, `codedBandwidth`) and "Channel Bit Share" (percent, stereo, joint stereo and dual channel frames, `bitDistribution`), each per frame and per second ("Coded Bandwidth Over Time" is each second's highest, "Channel Bit Share Over Time" bits over bits of the frames that start in it), anchored to the statistics nodes of the same names. A frame without a value (a mono frame in a stream that changes mode, a frame whose bits are all 0, a Layer I or II frame whose allocation does not fit) has no point, so such a series has its own x values; a mono stream has no share.

- **The tools' own labels** for these figures: mp3guessenc's "stereo mode frame count" is "Joint Stereo Modes" in Layer III, its "size distr" the frame sizes under each bitrate, its "Max reservoir" the largest main_data_begin;
  - EncSpot's "Av.
  - Reservoir" (which it truncates to whole bytes) and "Max.
  - Reservoir" are the mean and largest main_data_begin, and its "Scalefac" the share of granules with scalefactor scaling.

### Counting and the VBR header's claims

- **Which frames:** the Statistics node counts the region's complete audio frames: not a frame holding a VBR header, not a truncated last frame.
- **Bitrate Mode:** variable where the frames use more than one bitrate (free format counts as its own), constant where they use one, unknown for a single frame. A VBR header alone does not make a file variable. The statistic is the same concept as in the other formats, beside MPEG audio's own Bitrate Distribution by exact bitrate; the stream's `isVbr` follows it.
- **Clock:** time runs on a clock of 1/14,112,000 s (ffmpeg's time base for MP3, which every MPEG sample rate divides), so frames of different sample rates add up exactly; figures are rounded half to even.
- **When the coding statistics are counted:** the tallies are counted while the frames are read; the category series add a byte per value, derived at the end from the kept frame records.
- **A VBR header's counts** are compared with the frames and bytes counted from the header to the end of its stream (the next header or a change of version, layer or sample rate, across gaps and tags).
  - A truncated last frame was written whole, so it counts there and in the gapless sample count.
  - Xing's and Fraunhofer's encoders count the header's own frame and LAME does not, so either count holds; a mismatch is a warning.
  - Behind a LAME tag the count also sets where the music ends, since the delay and padding count from the frames after the header.
    - LAME 3.94 beta 1 counts the header frame too, so ffmpeg and mpg123, which take the length from the count, play one frame more than was encoded.
    - That is a warning, and the report's own sample count follows the frames.
- **Gapless figures from a LAME tag:** the encoder delay, the padding and the exact sample count, the frames' samples minus both.
  - This is what ffmpeg and mpg123 output with their gapless trimming when the padding covers their own 529-sample decoder delay.
  - Delay and padding longer than the stream are a warning.
- **A header names its own frames:** each header's counts and gapless figures cover the frames from it to the next header or a change of stream, and say so where those are not all of the region's.

## Ancillary data

The rules by which the analyzer finds the ancillary data of each frame, counts it and recognizes the mp3PRO and MP3 Surround signatures. No specification is public for these; the rules give the same results as mp3guessenc 0.27.6, which the MPEG cross-check confirms on the fixtures. They are written as rules, and the code follows them, not mp3guessenc's code. The code is not a clean-room rewrite as a whole: only `Segment()` and the Layer I and II count were rewritten from a description of the rules, and the rest of the scan (adding and closing areas, the skip logic) is Encspect's own earlier code, written with its own position arithmetic.

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
- **What the extensions are:** mp3PRO keeps spectral band replication data, from which an mp3PRO decoder restores the high frequencies of an MPEG-2 or 2.5 core; MP3 Surround keeps the spatial data that turns an MPEG-1 stereo downmix at 44.1 or 48 kHz back into 5.1 channels. What the extension data holds is not read.
- **In the report:** the region gets an Enhancement node with the ancillary bytes and their data rate, and an info finding says that decoders without support play only the core or the downmix.
- **The block's delay** counts the samples before the audio in a plain decoder's output, the decoder's 529 included, which ffmpeg's output of encoder-written fixtures confirms. mp3guessenc adds 576 for MP3 Surround; the report keeps the stored value.
- **mp3PRO's block** counts at twice the core's sample rate, the rate an mp3PRO decoder outputs: a real encode by Thomson's mp3PRO encoder stores 88,200 samples for two seconds at 44,100 Hz in a 22,050 Hz stream.

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
- **Encoder, from tags:** an ID3v2 TSSE frame, which the ID3v2 specification defines as the used audio encoder and its settings, names the encoder with low confidence; naming the same encoder as low-confidence evidence from the stream, it raises that to medium. A different name is an information finding (`mpeg-audio.encoder-claim-differs`).
- **Writer:** ffmpeg's muxer version ("Lavf60.16.100") in a TSSE frame, in an APEv2 Encoder item and in the LAME tag it writes, to seekable output with the Xing header enabled (FFmpeg n6.1.1 `mp3enc.c`): a tag laid out as its muxer writes it (an encoder string of `Lavc…` or `Lavf…`, or the CRC over the first 190 bytes) names the writer "FFmpeg" without a version, with low confidence; a bare `Lavf` is what it writes without encoder metadata. Any APEv2 Encoder item is the writer's too. ffmpeg's `Lavc…` encoder ID names the encoder. The writer is shown apart from the encoder; two claims that name different software are an information finding (`mpeg-audio.writer-claim-differs`), and software known to write another format is `mpeg-audio.writer-other-format`.

### Tags and headers that name the encoder

- **A LAME tag** names its encoder: high confidence when its CRC holds, medium when not. A VBRI header names Fraunhofer (high).
- **ffmpeg's LAME tag:** ffmpeg writes a LAME tag into every MP3 it muxes, with the encoder string of the stream it copies (a remux of a LAME file keeps `LAME3.100`) or its own.
  - `Lavc` means libavcodec encoded, which it does only with libmp3lame or libshine. So LAME's version in the ancillary data, or Shine's traits behind it, are medium confidence; other evidence keeps its own; without any, `Lavc` names ffmpeg (medium).
  - `Lavf` marks a remux of a stream whose encoder ffmpeg did not know and names no encoder: the stream's evidence names it as it would alone, with the tag as support.
- **Which frames a header names:** a header names the encoder of the frames it describes, from its frame to the next header or a change of stream.
  - Only a header in a region's first frame names the region's encoder from its tag. Where the region also holds frames it does not describe, or headers that name another encoder, files were joined, and that name is at most medium confidence.
  - A header further on names no more than the frames after it: the stream's other evidence names the region's encoder, which such a header supports where it names the same. Without any, the header names it with low confidence.
- **LAME writes its version twice** (libmp3lame/version.c):
  - into the tag's nine bytes: `LAME`, major.minor and a marker (`a` alpha, `b` beta, `r` a release with a patch level above 0 that does not fit, a space otherwise; 3.99 alphas wrote `L3.99a12`; 3.100 and later have no room for a marker);
  - whole, into the padding it drains into ancillary data (`3.98 (beta)`, `3.99 (alpha 12)`, `3.99.5`).
  - The report spells the version out (`LAME 3.98 (beta)`, `LAME 3.99 (alpha 12)`, `LAME 3.99.x` for an `r` without a patch level) and keeps the raw text in the tag's node.
  - Where the two agree on major.minor and marker, the name takes the more specific one and lists both as evidence when the ancillary one carries a marker. Where they disagree, the tag's is shown, and a warning says the frames and the tag may come from different encoders.
- **LAME before 3.90 has no LAME tag.** Its VBR modes (`-v`, `--r3mix` from 3.88) write a Xing header with the version ("LAME3.20", "LAME3.88 (beta)") in the nine or more bytes after the header's fields, which names the encoder with medium confidence (checked on 3.20 to 3.89b). 3.80 and later also drain the version into the padding (low confidence alone). CBR streams before 3.80 (checked on 3.20, 3.34, 3.57 and 3.70) carry nothing that names the encoder, and the analyzer names none; mp3guessenc says only "dist10 encoder or other encoder". Those builds also leave a truncated last frame (`mpeg-audio.frame-truncated`).
- **Helix:** the maintained Helix encoder (hmp3 5.2.4) writes a LAME tag whose encoder string is `LAMEH5.24`; it names Helix, not LAME, with the tag's confidence.
- **mp3PRO and MP3 Surround** name the encoder with medium confidence: Fraunhofer's MP3 Surround encoder, or an mp3PRO encoder, since several licensees wrote those (see "Ancillary data").
- **The Original File Length block** names Fraunhofer with medium confidence; a VBRI header still decides the encoder, with high confidence.

### Signatures in the padding

- Encoders fill unused bits with their name and version, a strong hint without tags.
- **LAME** writes `LAME3.100` (possibly cut short to `LAME`) into ancillary data mid-stream and into the padding of the last frame, even when the Info tag is disabled.
  - A version that runs to the end of the ancillary data may be cut short there (`LAME3.10`), so a whole one elsewhere is preferred, and a cut one names only LAME.
  - The string starts wherever the main data ends, at any bit position, and can run on into the next frame's data. So it is searched at every bit offset in the ancillary data the bit reservoir split identifies, joined across frames, not in raw frame bytes.
  - Found alone, it is low confidence.
  - The version may be up to 36 bytes long: 3.87 writes `LAME3.87 (beta 1, Sep 29 2000)`.
- **TwoLAME** writes zeros.
- **Gogo-no-coda** (3.12) fills the bits no frame uses with `GOGO` over and over, found at any bit position as two in a row (low confidence alone). In VBR it writes `GOGO` where its Xing header's quality belongs, which names it with medium confidence and is shown as such instead of a number.
- Which other encoders sign their padding, and how, is established from encoder-written fixtures before it is used.

### Habits of encoders without tags

- **The rule:** an encoder's habits are low-confidence evidence, used only where encodes of known signals with that encoder show them every time and the other encoders available for testing do not, over at least 20 frames.
- **Shine** never uses the bit reservoir, short blocks, scalefactor scaling or ancillary data. Behind ffmpeg's `Lavc` tag, which only libmp3lame and libshine encodes carry, that is medium confidence.
- **Helix** without its tag (5.2.4 and 5.0 r11) sets the copyright and original bits in every frame and uses scfsi and scalefactor scaling.
- **Xing's encoder** (tompg 3.0) never switches to short blocks, not even at a tone's onset where every other encoder does, never uses scfsi, and sets the original bit in every frame.
- **BladeEnc** switches to short blocks without scalefactor scaling, codes plain stereo or mono at one bitrate in MPEG-1, and from 0.82 on leaves ancillary data at most in its last frame.
  - Checked on every surviving source release from 0.80 to 0.94.2 and the Windows builds of 0.82, 0.90 and 0.91.
  - 0.80 and 0.81 drain an overflowing bit reservoir into ancillary data mid-stream, as the ISO dist10 encoder does, and mp3guessenc does not name them either.
- **Fraunhofer's encoders** use scalefactor scaling and short blocks but never scfsi (MPEG-1 only, since MPEG-2 has no scfsi).
- **TwoLAME and Xing** both write scalefactor index 63 into Layer II, which names them together.
- **Philips' multichannel encoder** (2.5, 1999) is not named either: none of its eighteen test streams uses scalefactor index 63 (`layer2/external-philips-*`).
- **mctoolame** (01a, the ISO software's multichannel encoder) is not named: none of its fourteen test streams uses scalefactor index 63, so the rule above does not apply, and the stream has no tag or text in ancillary data. What its streams do show is the multichannel extension, which the analyzer reads and verifies by its CRC (`layer2/external-mctoolame-*`).
- **mp3guessenc** agrees on every Layer III encode of these encoders. Its rules for encoders that cannot be run here (older releases) are not used, since on encoders that can, it names Shine "Xing" and Helix "Lame".
- **Silence** gives an encoder nothing to decide, so its traits do not show there, and a silent stream may match another encoder's habits.

### The TSSE frame

- An ID3v2 TSSE frame (TSS in ID3v2.2) is the weakest evidence of the encoder: whatever software writes the tag fills it in. That may be the encoder (LAME's front end writes `LAME 64bits version 3.100 (http://lame.sf.net)`), a later tagger or ffmpeg's muxer, and it is kept when the audio is re-encoded.
- The tag parser hands the text to the host, which passes the claims of every accepted tag, in file order, to the format parser.
- Names match on whole words, so `Flame` does not name LAME nor `Remixing` Xing. A claim never lowers or overrides a LAME tag or VBRI header.
- ffmpeg writes its own version (`Lavf60.16.100`) whatever encoder it used, so that text is a claim of the writer role, also in an APEv2 item or the LAME tag ffmpeg writes: it is shown as the writer "FFmpeg (Lavf60.16.100)", never as evidence of the encoder.
- A claim of the encoder role that names another encoder than the stream points to is an info finding on the tag frame, not a change of encoder.

## Decoding

The decoder (`mpeg-audio`, codec ID of the parser) follows minimp3 (CC0 1.0, `third_party/minimp3`) operation by operation, in single precision, and covers Layers I, II and III of MPEG-1, MPEG-2 and MPEG-2.5, free format included. It runs where a front end asks for the samples (`--measure`, `Output`): the stream stores no checksum over its audio, so decoding alone would compare nothing.

- **The split:** the parser's entropy pass reads every coded bit into the buffers the session lends, per channel and granule: the 576 quantized values, the scalefactors and the intensity stereo positions of Layer III, the sample codes of Layers I and II. The decoder reads the side information again from the frame's bytes and adds requantization, mid/side and intensity stereo, reordering of short blocks, the antialias butterflies, the inverse transforms with overlap and the polyphase synthesis.
- **Output:** planar floats at full scale 1.0, unclipped and undithered, at the stream's sample rate and channel count, untrimmed from the first audio frame. A Xing, Info or VBRI header frame holds no audio and is no position. The powers (x^(4/3), 2^(x/4)) come from `DetMath`; the stages use no fused multiply-add and sum in one fixed order, so the output is the same on every platform.
- **Channels:** a mono frame in a stereo stream goes to both channels and a stereo frame in a mono stream is averaged (decision 13 of the design). A Layer II stream that carries the MPEG-2 multichannel extension decodes to its configuration's channels (see "The multichannel extension" below); one whose extension is in a separate bitstream decodes its stereo base only.
- **Verification:** the frame CRC-16 is declared as a header checksum (it covers no sample): streams that store it read "unknown (frame CRC-16 n of n, no checksum that was compared covers all of the audio)", the others "unknown (the stream stores no checksum)", and a stream with a failed CRC reads "no (frame CRC-16 k of n differ)".
- **Gaps and withheld output:**
  - A frame cut by the end of the data or by the next frame, or whose side information breaks, is not decoded and counts as lost; the frame after it, and the frame after a stretch of junk, start from an empty state.
  - After such a gap the output that depends on the lost state is withheld, not played: 1,056 samples of Layer III (the first granule and 480 of the next, the overlap and the synthesis history) and 480 of Layers I and II (the history), reason "after a gap".
  - A Layer III frame whose main data are not available (a track split from a continuous stream, a frame cut by the next, junk that could have held a frame) is withheld whole, reason "main data not available", and the next frame that decodes is withheld like after a gap, also at the start of a stream.
- **The multichannel extension** (ISO/IEC 13818-3 2.5.3.2, [design](../design/lossy-decoders.md#mpeg-2-multichannel)), where the file carries it and no frame has an extension bitstream:
  - **Channels:** left, right, the center, the surround channels (or a second stereo program's two channels), then the LFE, as the stream's layout says.
  - **Steps:** the additional channels' samples, dynamic crosstalk (a missing channel takes its source's quantized samples with its own scalefactors), prediction from the base channels, dematrixing per subband group by `tc_allocation` and `dematrix_procedure`, de-normalization, and the polyphase filter bank. The −90 degree phase shift and dynamic expansion that 2.5.3.2.1.1 allows after dematrixing are not applied.
  - **LFE:** twelve samples a frame at 1/96 of the sample rate, output at the stream's rate by holding each sample for 96 samples, scaled by 2 (observed in ISO's reference output of test01; the text gives no output scale for it). The 125 Hz low-pass the standard recommends for reproduction is not applied.
  - **A frame that cannot be decoded** (the multichannel CRC fails, the data run past the base frame, another configuration) counts as not decoded, and the output after it is withheld like after a gap.
- **Accuracy:** the output agrees with minimp3's float output and with FFmpeg's float decoders within the bounds of [development.md](../development.md#mpeg-audio-decoding); neither is claimed bit-exact.
- **Cost:** 2,048 work units are allowed per byte of the file. A frame charges 22 units per sample of each channel plus the stages (Layer III 41, Layers I and II 29, and 8 more in a multichannel stream).

## Not yet covered

- The multilingual channels of the MPEG-2 multichannel extension are read for the CRC and counted, not decoded: no stream without an extension bitstream holds any.
- An extension bitstream, the separate file ISO/IEC 13818-3 allows for data that does not fit the base frame, is never read: a stream whose frames set `ext_bit_stream_present` decodes its two base channels only, and its measurements say in their reason that only those were measured. Layers I and III with the extension (test22 and test23 of ISO/IEC 13818-4) are not read for it either.
- Layer II samples and Layer III Huffman values are not shown as nodes; the report holds figures from them (Coded Bandwidth), not the values.
- The Layer III intensity stereo bound is the frame's lowest intensity band in any granule: which granule or how many granules use intensity stereo is not reported, and the bound is read from the walk's quantized values, not from decoded audio.
