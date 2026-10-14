# ASF and WMA

The Advanced Systems Format container (`.wma`, `.wmv`, `.asf`) and the Windows Media Audio codecs: versions 1 and 2 and Professional frame by frame, Lossless at header level, Voice identified.

Sources: the ASF specification, revision 01.20.03, and FFmpeg 6.1.1 (`asfdec_f.c`, `wmadec.c`, `wma.c`, `wmaprodec.c`, `wmalosslessdec.c`, `wmavoice.c`), both pinned by SHA-256 (see [development](../development.md)). Microsoft has published no specification of the codecs, so FFmpeg's decoders are the reference, and the Microsoft-encoded fixtures confirm the readings.

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

## WMA Lossless and Voice

- **Lossless (0x0163, "wma-lossless"):** the 18 bytes of codec data (the sample size of 16 or 24 bits, the channel mask, the decode flags) are shown, the packets are counted, and the length is the container's. The frames are not read yet; a finding says so.
- **Voice (0x000A, "wma-voice"):** identified from the container's figures, with the packets counted; the 46 bytes of codec data and the frames are not read.
- The length comes from the container (the play duration less the preroll) and the bitrate is the nominal one it states, since the packets reach beyond the duration where the stream starts with a preroll.

## Channel layout

- WMA versions 1 and 2 carry one or two channels: mono, or stereo (left and right, with mid/side coded per block).
- Professional and Lossless name the speaker of each channel from the channel mask of the codec data (or of the WAVEFORMATEXTENSIBLE), in the order of the mask's bits, so a mask of 0x3F is 5.1 (front left and right, center, low-frequency, back left and right). The mask of Professional also gives the low-frequency channel, whose subframes keep only the bands below the cutoff.

## Encoder detection

The Encoder node of a stream lists its evidence, each at its bytes, and names the strongest.

- **High:** ffmpeg's wmav1 and wmav2 encoders pad every superframe after its frame with 'N' bytes (`wmaenc.c`), with no bit reservoir and no variable block lengths. Two or more padded superframes, all of them 'N', name "FFmpeg (wmav2 encoder)".
- **Medium, from the container's layout:** the files the project encoded on a Windows runner (wmvcore 12.0.26100) show two layouts that tell the writers apart. The objects are the specification's; that a writer chooses them is its habit.
  - The Windows Media Format SDK's writer states WMFSDKVersion, writes a Codec List Object with a description such as "VBR Quality 75, 44 kHz, stereo 1-pass VBR", a Compatibility Object (section 4.12) in the Header Extension Object, and an Index Object.
  - Media Foundation's ASF sink states MediaFoundationVersion, writes a Language List, Extended Stream Properties and Metadata Object in the Header Extension Object and a Padding Object, and no Codec List Object, WMFSDKVersion or index.
- **Medium, from the codec data of versions 1 and 2:** Microsoft's encoders write a samples-per-block of 17 frames with the bit reservoir and variable block lengths; ffmpeg's write 0 and neither.
- **Low:** a claim in the metadata (WMFSDKVersion, MediaFoundationVersion, WM/EncodingSettings, WM/ToolName), which survives a re-encode. ffmpeg's muxer writes its own version whatever encoded the stream, so alone it names none.
- A claim that names another encoder than the evidence is an info finding.
- A layout names the writing software, not the encoding stage: the Windows Media Format SDK and Media Foundation both write the file around whatever codec they drive. That is why a writer version alone stays Low and the layout is Medium, never High.
- What the writer says about its settings is added to that evidence, as its statement and not as something read from the audio: the Codec List Object's name and description ("Windows Media Audio 9.2", "VBR Quality 75, 44 kHz, stereo 1-pass VBR") and the IsVBR attribute. The stream's `isVbr` follows IsVBR. Media Foundation's files carry IsVBR but no Codec List.
- Not used as evidence: `WMFSDKNeeded` and `DeviceConformanceTemplate`, which the SDK writes with fixed values.

## Observed in Microsoft's files

- A frame read from a superframe's bit offset that ends up to 7 bits past the superframe is accepted by FFmpeg: `wmadec.c` rejects it only when the bytes left in the superframe come out negative. It does not check frames rebuilt from the reservoir or the frames of streams without the bit reservoir at all. This analysis reports any frame that needs more bits than it has as invalid.
- A superframe in the middle of a frame (no frame ends in it) adds its `align - 1` bytes after the first to the reservoir. `wmadec.c` adds `8 * buf_size - 8` to `last_superframe_len`, bits to a count of bytes, so a frame that spans such a superframe is not rebuilt right by FFmpeg; the analysis keeps the bytes, and `AFrameEndsAfterASuperframeInsideIt` builds such a stream.
- The superframe's frame count field counts the frames that begin in the superframe, the last one included where it goes on in the next. The decoder takes one off before it decodes from the bit offset, so it decodes one frame fewer than the field says wherever `last_superframe_len` is 0 or less: in the first superframe, which no frame precedes, and in the one after an error reset.
- A media object longer than the block alignment is taken as a superframe of `align` bytes and the rest is fed in again by FFmpeg (`decode.c`); this analysis reads the first `align` bytes only.
- A frame that begins in one superframe and ends in the next is often followed by bits up to the bit offset the next superframe states: zero bits, or more coded data. A decoder skips them (`wmadec.c` goes on at the bit offset); they are info findings (`wma.frame-stuffing`, `wma.frame-filler`), and the statistics count them.
- The Simple Index Object of a file whose index is empty states an interval of 0, which is no finding.
- Professional's packets of Media Foundation's CBR files end with padding after a frame whose trailer bit says no frame follows.

## Not yet covered

- The frames of WMA Lossless (planned in phase 3, with the checksum-verifying decoders), and everything inside WMA Voice (planned: phase 2, read like Speex).
- Professional frames without the length prefix (planned: later, a separate item), and the audio of any codec (no decoder is built).
- Decrypting protected content (out of scope); the protection objects are planned for phase 2's protection reporting.
- The contents of script commands and markers beyond their fields (planned: later, a separate item).
