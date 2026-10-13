# ASF and WMA

The Advanced Systems Format container (`.wma`, `.wmv`, `.asf`) and Windows Media Audio versions 1 and 2.

Sources: the ASF specification, revision 01.20.03, and FFmpeg 6.1.1 (`asfdec_f.c`, `wmadec.c`, `wma.c`), both pinned by SHA-256 (see [development](../development.md)).

## ASF: what is read

- **Objects:** every object is a GUID, a 64-bit size and its data; each becomes a node with its fields at their bytes.
- **Header Object:** File Properties, Stream Properties (with the WAVEFORMATEX of audio streams, including the extensible form and spread audio), Header Extension, Codec List, Script Command, Marker, Content Description, Extended Content Description, Content Branding, Stream Bitrate Properties, Mutual Exclusion, Stream Prioritization, Bandwidth Sharing, Error Correction, Padding, Index Parameters, Language List, Metadata, Metadata Library, Extended Stream Properties (with a Stream Properties Object inside), Compatibility, and the encryption and signature objects (reported, not decrypted).
- **Data Object:** every packet with its error correction data, length type flags, property flags, send time, duration and payloads; single and multiple payloads, replicated data with the media object size and presentation time, extension data, compressed payloads and padding.
- **Index objects:** Simple Index, Index, Media Object Index and Timecode Index, checked against the packets.
- **Unknown objects:** read as data and reported, as the specification tells readers to ignore them.

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
- The packets are read twice: the first pass keeps a compact record per packet and payload (the first 1,048,576 packets are detailed, the rest counted), and builds nodes on demand; the second pass puts media objects together as they are needed.

## WMA versions 1 and 2

Format tags 0x0160 and 0x0161, routed as the codec "wma". Read as FFmpeg's `wmadec.c` reads them.

- **Codec data:** samples per block, the encode options (exponent VLC, bit reservoir, variable block length, block size count) and the super block alignment, with FFmpeg's rule that disables variable blocks for version 2 with 8 or more bytes of codec data and options 0xD.
- **Superframes:** the block alignment in bytes. With the bit reservoir, a header of a 4-bit index, a 4-bit frame count and a bit offset; frames then continue across superframes. The tree has a physical list (superframe header, frame pieces, reservoir, padding) and a logical list of frames, whose ranges span two superframes where a frame does.
- **Frames and blocks:** block sizes, the mid/side flag, which channels are coded, total gain, noise substitution flags and gains, exponents (VLC or LSP), and the run-level coefficient codes. Where a frame ends is found by decoding its coefficients, as FFmpeg's decoder does.
- **Statistics:** frames, frame length, samples, duration, smallest and largest frame, bitrate range, use of the bit reservoir, variable block lengths and noise substitution, blocks per size, mid/side blocks, noise-substituted blocks, total gain range, non-zero and escape-coded coefficients.
- **Series:** the bitrate per frame, per second, and the bit reservoir fill.
- **Findings:** a superframe that is shorter or longer than the block alignment, a bad superframe header, a bit offset that points outside the superframe, a reservoir length that does not fit, a frame that does not end where the next one starts, a frame whose coefficients do not decode, a stream whose format cannot be analyzed, and a reservoir that holds bits at the end of the stream.

## Channel layout

- WMA versions 1 and 2 carry one or two channels: mono, or stereo (left and right, with mid/side coded per block).
- The channel mask of a WAVEFORMATEXTENSIBLE is shown in the container's tree; the multichannel layouts it describes belong to WMA Pro and are reported with that parser.

## Encoder detection

- The container's own claims (`WM/EncodingSettings`, `WM/ToolName`, `WM/ToolVersion`, `WMFSDKVersion`) name an encoder with low confidence, since a claim alone is low.
- Traits of the frames come with the Microsoft-encoded fixtures: see the plan.

## Not yet covered

- WMA Pro (0x0162), WMA Lossless (0x0163) and WMA Voice (0x000A): the container hands their streams on, but no parser reads them yet.
- Decrypting protected content, and the contents of script commands and markers beyond their fields.
