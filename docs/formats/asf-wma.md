# ASF and WMA

The Advanced Systems Format container (`.wma`, `.wmv`, `.asf`) and the Windows Media Audio codecs: versions 1 and 2, Professional and Voice frame by frame, Lossless at header level.

Sources: the ASF specification, revision 01.20.03, and FFmpeg 6.1.1 (`asfdec_f.c`, `wmadec.c`, `wma.c`, `wmaprodec.c`, `wmalosslessdec.c`, `wmavoice.c`), both pinned by SHA-256 (see [development](../development.md)). Microsoft has published no specification of the codecs, so FFmpeg's decoders are the reference, and the Microsoft-encoded fixtures confirm the readings. For Voice, no specification, patent filing or other description is known that the project could check, and `wmavoice.c` alone describes the bitstream.

## ASF: what is read

- **Objects:** every object is a GUID, a 64-bit size and its data; each becomes a node with its fields at their bytes.
- **Header Object:** File Properties, Stream Properties (with the WAVEFORMATEX of audio streams, including the extensible form and spread audio), Header Extension, Codec List, Script Command, Marker, Content Description, Extended Content Description, Content Branding, Stream Bitrate Properties, Mutual Exclusion, Stream Prioritization, Bandwidth Sharing, Error Correction, Padding, Index Parameters, Language List, Metadata, Metadata Library, Extended Stream Properties (with a Stream Properties Object inside), Compatibility, and the encryption and signature objects (reported, not decrypted).
- **Data Object:** every packet with its error correction data, length type flags, property flags, send time, duration and payloads; single and multiple payloads, replicated data with the media object size and presentation time, extension data, compressed payloads and padding.
- **Index objects:** Simple Index, Index, Media Object Index and Timecode Index, checked against the packets.
- **Unknown objects:** read as data and reported, as the specification tells readers to ignore them. Two GUIDs are named although the specification does not define one of them:
  - `26F18B5D-4584-47EC-9F5F-0E651F0452C9` is the Compatibility Object (section 4.12): the text of revision 01.20.03 misprints the Header Object's GUID for it in section 10.3, and in all the SDK's files of the project's fixtures it is 26 bytes with profile 2 and mode 1, as section 4.12 sets them. MediaInfoLib calls it Compatibility too.
  - `D9AADE20-7C17-4F9C-BC28-8555DD98E2A2` is in neither the specification nor FFmpeg. MediaInfoLib (`File_Wm_Elements.cpp`) calls it Index Placeholder, and the SDK's writer puts it in the Header Extension Object of the constant bitrate files of the project's fixtures (written on Windows Server 2025, wmvcore 12.0.26100), not in its variable bitrate or Lossless files. It is shown by that name, data unread, and readers ignore it.

## ASF: what is reported

- **Findings** for damage and for what breaks the specification: sizes that overrun, counts that disagree with what the file holds (header objects, packets, file size), missing required objects, reserved fields, packets whose lengths do not add up, payloads that overrun, a Data Object of size 0 outside a broadcast, and index entries that point past the data or off a packet start.
- **Statistics** for the Data Object: packets, payloads, packets with multiple payloads or error correction, compressed payloads, padding, overhead, and per stream the media objects, fragmented ones, key frames and presentation times.
- **Metadata** as the container's own tags, in the tree: text, bytes, booleans, numbers and GUIDs, a `WM/Picture` decoded, and an `ID3` byte array handed to the ID3v2 parser.
- **Media objects** of each audio stream are put together from their payloads, also when they span packets, and go to the codec parser by the stream's format tag.

## Tags and tag detection

- ASF has its own metadata, so no ID3 or APE tag is looked for inside it.
- A leading ID3v2 tag is the host's, before the ASF header.
- `DeclaredEnd` is the File Properties Object's file size; for a broadcast file or a size that cannot be right, the end of the chain of top-level objects. An ID3v1 or APE tag after it is the file's.

## ASF: damaged and large files

- A damaged object or packet is a finding; the objects and packets that can be read are still read.
- **Limits** keep a crafted file's tree small: at most 10,000 top-level objects, 10,000 objects in the header, 1,000 entries of a Codec List or Advanced Content Encryption Object (`asf.too-many-entries`), 1,000 entries and 1,000 blocks of an index as nodes (the rest are read for the figures, with a "Further" node), 65,536 packets described one by one (each is some 12 KB of JSON), and 262,144 media objects of a Windows Media Audio stream (`wma.records-limit`). The buffer of a media object grows with the fragments that arrive, so a packet that states an object of 16 MiB costs what it holds.
- **Packet size:** a file whose File Properties Object gives one packet size as minimum and maximum has packets of that size; a packet length field below it is a finding (`asf.packet-length`) and the difference is padding, as FFmpeg reads it (`asfdec_f.c`). The packets are stepped over by the size, not by the field.
- **Spread audio** is descrambled by FFmpeg only where the chunk size is not 0 and divides the virtual packet length into more than one chunk; otherwise it decodes the media objects as they are, and so does this analysis.
- **Broadcast files:** a Data Object of size 0 runs to the end of the file, so what is at its end is the Data Object's, not a tag.
- The packets are read twice: the first pass keeps a compact record per packet and payload (the first 65,536 packets are detailed, the rest counted), and builds nodes on demand; the second pass puts media objects together as they are needed.

## WMA versions 1 and 2

Format tags 0x0160 and 0x0161, routed as the codec "wma". Read as FFmpeg's `wmadec.c` reads them.

- **Codec data:** samples per block, the encode options (exponent VLC, bit reservoir, variable block length, block size count) and the super block alignment, with FFmpeg's rule that disables variable blocks for version 2 with 8 or more bytes of codec data and options 0xD.
- **Superframes:** the block alignment in bytes. With the bit reservoir, a header of a 4-bit index, a 4-bit frame count and a bit offset; frames then continue across superframes. The tree has a physical list (superframe header, frame pieces, reservoir, padding) and a logical list of frames, whose ranges span two superframes where a frame does.
- **Frames and blocks:** block sizes, the mid/side flag, which channels are coded, total gain, noise substitution flags and gains, exponents (VLC or LSP), and the run-level coefficient codes. Where a frame ends is found by decoding its coefficients, as FFmpeg's decoder does.
- **Statistics:** frames, frame length, samples, duration, smallest and largest frame, bitrate range, use of the bit reservoir, variable block lengths and noise substitution, blocks per size, mid/side blocks, noise-substituted blocks, total gain range, non-zero and escape-coded coefficients.
- **Series:** the bitrate per frame, per second, and the bit reservoir fill.
- **Findings:** a superframe that is shorter or longer than the block alignment, a bad superframe header, a bit offset that points outside the superframe, a reservoir length that does not fit, a frame that does not end where the next one starts, a frame whose coefficients do not decode, a stream whose format cannot be analyzed, and a reservoir that holds bits at the end of the stream.

## WMA Professional

Format tag 0x0162, routed as the codec "wma-pro". Read as FFmpeg's `wmaprodec.c` reads it.

- **Codec data:** the sample size, the channel mask and the decode flags (the frame length adjustment, the most subframes per frame, the length prefix of frames, dynamic range compression), from which the frame length, the subframe sizes and the scale factor bands of each size follow.
- **Packets:** a media object of the block alignment's size. A header holds a sequence number, two unused bits and the count of bits that finish the frame before; then whole frames, each prefixed with its length, and the start of one that goes on. A gap in the sequence numbers is a finding, as a decoder takes the packet before to be lost.
- **Frames:** the length prefix, the tile header (how the frame is divided into subframes per channel), an optional postprocessing transform, the dynamic range gain, the trim of the start and end, the subframes and the trailer bit. The frame is read to its end and its length prefix must say where that is; a frame that does not agree is invalid.
- **Subframes:** the extended header, the channel groups and their transforms (with the bands the transform is on in), which channels send coefficients, the vector coefficient counts, the quantization step and each channel's modifier, the scale factors (new, or differences from the ones before) and the coefficient codes (vector codes with signs, then run-level codes), each at its bits. The audio is not rebuilt: no dequantization, channel transform or inverse transform is made.
- **Statistics and series:** as for versions 1 and 2, with the subframe sizes, the share of channel groups with a transform, the quantization steps, the samples a decoder outputs (the first frame cuts its whole length, and the frames cut what their trim says) and the bitrate per frame and per second.
- **Not read:** frames without the length prefix, which need every frame read across packets (a finding says so), and a media object longer than the block alignment, which a decoder reads as further packets.

## WMA Lossless

Format tag 0x0163, routed as the codec "wma-lossless".

- The 18 bytes of codec data (the sample size of 16 or 24 bits, the channel mask, the decode flags) are shown, the packets are counted, and the length is the container's. The frames are not read yet; a finding says so.
- The length comes from the container (the play duration less the preroll) and the bitrate is the nominal one it states, since the packets reach beyond the duration where the stream starts with a preroll.

## WMA Voice

Format tag 0x000A, routed as the codec "wma-voice". Read as FFmpeg's `wmavoice.c` reads it (FFmpeg 6.1.1, `wmavoice_data.h` pinned with it): the sizes of everything that decides where a frame ends, and the fields that name a frame, not the audio.

**Codec data (46 bytes).**

- Bytes 0 to 17 are laid out as Professional's, for the music superframes inside Voice that FFmpeg does not decode; shown as one node, since the decoder does not read them.
- The flags (bytes 18 to 21, little-endian; the comment in `wmavoice.c` says 19 to 22, the code reads 18): postfilter (bit 0), denoise strength (bits 2 to 5, 12 and more are rejected), tilt correction (bit 6), DC level (bits 7 to 10, a high-pass filter above 8), 16 LSPs instead of 10 (bit 12), the quantizer mode (bit 13) and the default mode (bit 14).
- The frame type table (bytes 22 to 45): 17 groups of 3 bits, one for each frame type, which give the type's code a length (2 bits for group 0 up to 14 for groups 6 and 7).
- Anything FFmpeg rejects is a warning and the stream is described by the container's figures only (`wma.voice-not-read`): a size other than 46 (`wma.codec-data-size`), a block alignment of 0 or above 4,194,304 (`wma.voice-block-alignment`), a denoise strength of 12 or more (`wma.voice-denoise-strength`), five or more types in one group of the table (`wma.voice-frame-type-table`), a sample rate outside 322 to 22,097 Hz or one that leaves no room for the pitch fields (`wma.voice-sample-rate`). FFmpeg makes no check of the bitrate.
- Informational: a fourth type in a group (FFmpeg's comment says three, `wma.voice-frame-type-overlap`), types that no code reaches (`wma.voice-frame-type-unreachable`), and a container that states other than one channel (`wma.voice-channels`; the decoder outputs mono).

**Packets.** A media object holds whole blocks of the block alignment's size, which FFmpeg's decoder cuts it into (the 7 kbit/s files of the sample archive have two to an ASF packet); bytes after the last whole block are not read (`wma.packet-long`) and a block shorter than the alignment is skipped (`wma.packet-short`). A packet starts with a 4-bit sequence number, the flag for residual LSPs, the count of superframes that begin in it (6-bit values, each 63 saying another follows), and the spillover: how many bits after the header finish the superframe the packet before ended in, in 3 + ceil(log2(block alignment)) bits.

- The superframes follow one another bit by bit. The last of the counted ones goes on to the end of the packet, and the next packet's spillover bits finish it; at the end of the stream the last is read from its packet's bits alone, as FFmpeg's decoder does when it is flushed.
- A superframe is read from the bits of the packet, not from a stated size, since none is stated. One that ends before the bits the packets give it leaves bits that are ignored (`wma.voice-superframe-padding`); one that needs more is dropped (`wma.voice-superframe-overrun`); one that the stream ends in is `wma.voice-unfinished`.
- Spillover bits with no start to finish are skipped (`wma.voice-spillover-orphan`). The sequence number is not read by FFmpeg; a break is an info finding (`wma.voice-packet-sequence`). A header that runs past the packet is `wma.voice-packet-header`.
- After a superframe in a packet fails, FFmpeg drops the rest of the packet (`decode.c` unreferences a packet whose decode call fails) and so does this analysis; after one finished from the packet before fails, it goes on with the packet's own. Its resync then skips the spillover from a position that `copy_bits` has already moved by up to 7 bits, so it resumes that many bits twice too late; the analysis does the same, and the fixtures `voice-spillover` and `voice-invalid-superframes` decode to the same number of samples in both.
- A superframe that FFmpeg's cache cannot hold (more than 256 bytes of a packet's tail, `SFRAME_CACHE_MAXSIZE`) is read from stale bytes there; the analysis reads the real bits and says so (`wma.voice-cache-overflow`). One real file of the sample archive has it, and its last superframe, which states 419 samples, is decoded as 480 by FFmpeg.
- The flag for residual LSPs that a superframe is read with is that of the packet it ends in, as in FFmpeg (`wma.voice-lsp-flag-changes` if it differs from the packet before).

**Superframes.** A speech bit (clear: music, Windows Media Audio Professional data that FFmpeg does not decode, `wma.voice-music`), a flag and 12 bits for the sample count (at most 480, else `wma.voice-sample-count`), the LSPs of all three frames once (48 bits for 10 LSPs, 60 for 16) when the packet's flag says residual, three frames, and a flag with an optional 4-bit count and that many plus one groups of 10 bits of additional data, which FFmpeg skips ("Statistics?").

**Frames.** Per frame: its own LSPs (24 or 34 bits) unless the superframe sends them once, the frame type (a code of 2 to 14 bits whose slot the table maps to one of 17 types, `wma.voice-frame-type` where it maps to none), then fields that depend on the type:

- The type sets the blocks (1, 2, 4 or 8), the adaptive codebook (none, a pitch for the frame, a pitch for each block) and the fixed codebook (silence, hardcoded, pitch-adaptive window pulses, pulse sets with 0, 2 or 5 of the 5 vectors holding a pair).
- A pitch per frame is `ceil(log2(max - min))` bits, `min` and `max` following from the sample rate. A pitch per block is an absolute value for the first block and a difference for the others, converted by FFmpeg's piecewise table; the pitch carries from frame to frame (`last_pitch_val`).
- Pitch-adaptive window frames read 6 or 8 bits for the position of the first pulse, which with the pitch decides how many of the first block's positions are pulses at the pitch lag, and so the width of one field of each block. That is the one place where a frame's size depends on earlier frames; the analysis keeps the pitch state as FFmpeg does, also across a failed superframe.
- Every block's pulse positions, signs and gains are read to be skipped and are not reported; silence frames read an 8-bit gain index. No LSP value, gain or pulse position is reported, and no audio is built.

**Reported.**

- Nodes: the packets (header fields, the end of the superframe before, the superframes that lie in the packet, the start of the one that goes on, padding and unread bits) and the superframes (header, LSPs, frames with type, pitch and block sizes, additional data). A superframe across two packets lies in the bytes of both.
- Statistics: packets, superframes, frames, samples (the superframes' sample counts, 480 unless they state fewer, which is what FFmpeg decodes), duration, average bitrate (the superframes' bits over that time), nominal bitrate, smallest and largest superframe, superframes across packets, the flags that matter, bits ignored or left at the end, and the frames by type, by fixed and adaptive codebook and by number of blocks. The types are named as `wmavoice.c` names its codebooks, written out ("pulse sets, 4 blocks, pitch per frame, 5 pulse pairs"); the file's own table does not say what sound a type stands for, so none is called voiced or unvoiced.
- Series: the bitrate of each superframe and of each second.
- Memory: about 175 bytes are kept for a superframe (a record of 48 bytes, three of 20 for the frames and the packet's); the nodes are built when read, and at most 524,288 superframes are described (`wma.records-limit`).

**Not covered.** Music superframes (Professional data inside Voice) are not read, and the rest of the packet with them; the values of the LSPs, gains and pulses are not decoded; audio is not rebuilt.

## Channel layout

- WMA versions 1 and 2 carry one or two channels: mono, or stereo (left and right, with mid/side coded per block).
- Professional and Lossless name the speaker of each channel from the channel mask of the codec data (or of the WAVEFORMATEXTENSIBLE), in the order of the mask's bits, so a mask of 0x3F is 5.1 (front left and right, center, low-frequency, back left and right). The mask of Professional also gives the low-frequency channel, whose subframes keep only the bands below the cutoff.

## Encoder detection

The Encoder node of a stream lists its evidence, each at its bytes, and names the strongest.

- **High:** ffmpeg's wmav1 and wmav2 encoders pad every superframe after its frame with 'N' bytes (`wmaenc.c`), with no bit reservoir and no variable block lengths. Two or more padded superframes, all of them 'N', name "FFmpeg (wmav2 encoder)".
- **Medium, from the container's layout:** the files the project encoded on a Windows runner (wmvcore 12.0.26100) show two layouts that tell the writers apart. The objects are the specification's; that a writer chooses them is its habit.
  - The Windows Media Format SDK's writer states WMFSDKVersion, writes a Codec List Object with a description such as "VBR Quality 75, 44 kHz, stereo 1-pass VBR", a Compatibility Object (section 4.12) in the Header Extension Object, and an Index Object (with a Simple Index Object of 0 entries) in its variable bitrate and Lossless files (12 of its 20 fixtures; its 8 constant bitrate files have none). The analyzer does not check the index.
  - Media Foundation's ASF sink states MediaFoundationVersion, writes no Codec List Object, WMFSDKVersion, Compatibility Object or index (11 fixtures). The analyzer's rule for it also requires a Language List, Extended Stream Properties, a Metadata Object, a Padding Object and Stream Bitrate Properties, and its evidence text lists them as the layout; all 31 fixtures of both writers carry them, so they do not tell the writers apart. The evidence text overstates them (follow-up).
- **Medium, from the codec data of versions 1 and 2:** Microsoft's encoders write a samples-per-block of 17 frames with the bit reservoir and variable block lengths; ffmpeg's write 0 and neither.
- **Low:** a claim in the metadata (WMFSDKVersion, MediaFoundationVersion, WM/EncodingSettings, WM/ToolName), which survives a re-encode. ffmpeg's muxer writes its own version whatever encoded the stream, so alone it names none.
- A claim that names another encoder than the evidence is an info finding.
- A layout names the writing software, not the encoding stage: the Windows Media Format SDK and Media Foundation both write the file around whatever codec they drive. That is why a writer version alone stays Low and the layout is Medium, never High.
- What the writer says about its settings is added to that evidence, as its statement and not as something read from the audio: the Codec List Object's name and description ("Windows Media Audio 9.2", "VBR Quality 75, 44 kHz, stereo 1-pass VBR") and the IsVBR attribute. The stream's `isVbr` follows IsVBR. Media Foundation's files carry IsVBR but no Codec List.
- Not used as evidence: `WMFSDKNeeded` and `DeviceConformanceTemplate`, which the SDK writes with fixed values.
- Voice has no trait of its own: its 46 bytes of codec data and its frames name no encoder, so it is named by the container's layout and claims only.

## Observed in Microsoft's files

- A frame read from a superframe's bit offset that ends up to 7 bits past the superframe is accepted by FFmpeg: `wmadec.c` rejects it only when the bytes left in the superframe come out negative. It does not check frames rebuilt from the reservoir or the frames of streams without the bit reservoir at all. This analysis reports any frame that needs more bits than it has as invalid.
- A superframe in the middle of a frame (no frame ends in it) adds its `align - 1` bytes after the first to the reservoir. `wmadec.c` adds `8 * buf_size - 8` to `last_superframe_len`, bits to a count of bytes, so a frame that spans such a superframe is not rebuilt right by FFmpeg; the analysis keeps the bytes, and `AFrameEndsAfterASuperframeInsideIt` builds such a stream.
- The superframe's frame count field counts the frames that begin in the superframe, the last one included where it goes on in the next. The decoder takes one off before it decodes from the bit offset, so it decodes one frame fewer than the field says wherever `last_superframe_len` is 0 or less: in the first superframe, which no frame precedes, and in the one after an error reset.
- A media object longer than the block alignment is taken as a superframe of `align` bytes and the rest is fed in again by FFmpeg (`decode.c`); this analysis reads the first `align` bytes only.
- A frame that begins in one superframe and ends in the next is often followed by bits up to the bit offset the next superframe states: zero bits, or more coded data. A decoder skips them (`wmadec.c` goes on at the bit offset); they are info findings (`wma.frame-stuffing`, `wma.frame-filler`), and the statistics count them.
- The Simple Index Object of a file whose index is empty states an interval of 0, which is no finding.
- Professional's packets of Media Foundation's CBR files end with padding after a frame whose trailer bit says no frame follows.
- Voice, from the two Windows Media Format SDK files (8 kHz, 5 kbit/s; 16 kHz, 16 kbit/s) and the 11 files of the FFmpeg sample archive (`check_wmavoice_samples.py`):
  - Every packet of every file sets the flag for residual LSPs, and every sequence number counts up from 0 to 15 and around.
  - The Windows files state no sample count. The archive's files state one in the last superframe only (the samples that remain, so that the total equals the container's duration to the sample), except the broken one.
  - The 8 kHz Windows file uses the pitch per frame types 2 to 7 almost throughout, and the 16 kHz one the pitch per block types 11 to 13; the archive's files, which contain speech and pauses, use silence (type 0) and the hardcoded codebook (type 1) as well. All real files have the postfilter on, denoise strengths of 3, 7 and 9, DC levels of 1, 3 and 5, tilt correction in two of them, 10 LSPs at 8 kHz and 16 at 16 kHz, and the quantizer and default modes at 0.
  - The Windows files' superframes fill the packets exactly, apart from padding after the last superframe of the 8 kHz file. The archive's 16 kHz files pad the end of their first packet after a superframe that ends within it (161 and 370 bits).
  - The archive's `wmav_8.wma` has music superframes in about half of its packets and bytes of text ("Windows Media C", "wmenc.exe") in the unused bytes of its codec data; FFmpeg decodes the speech superframes of it and refuses the others.

## Not yet covered

- The frames of WMA Lossless (planned in phase 3, with the checksum-verifying decoders).
- Professional frames without the length prefix (planned: later, a separate item), and the audio of any codec (no decoder is built).
- Decrypting protected content (out of scope); the protection objects are planned for phase 2's protection reporting.
- The contents of script commands and markers beyond their fields (planned: later, a separate item).
