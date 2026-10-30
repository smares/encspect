# ASF and WMA

The Advanced Systems Format container (`.wma`, `.wmv`, `.asf`) and the Windows Media Audio codecs: versions 1 and 2 down to the end of every frame's coefficients, Professional, Lossless and Voice at header level.

Sources: the ASF specification, revision 01.20.03, and FFmpeg 6.1.1 (`asfdec_f.c`, `wmalosslessdec.c`), both pinned by SHA-256 (see [development](../development.md)). Microsoft has published no specification of the codecs. The frame-level readers of versions 1 and 2, Professional and Voice were removed on 2026-10-29 because their code followed FFmpeg's LGPL decoders too closely. The reader of versions 1 and 2 is back as a clean-room rewrite from [the description](../design/wma-standard-format.md); Professional and Voice are still to come (see [the plan](../PLAN.md), phase 2).

## ASF: what is read

- **Objects:** every object is a GUID, a 64-bit size and its data; each becomes a node with its fields at their bytes.
- **Header Object:** File Properties, Stream Properties (with the WAVEFORMATEX of audio streams, including the extensible form and spread audio), Header Extension, Codec List, Script Command, Marker, Content Description, Extended Content Description, Content Branding, Stream Bitrate Properties, Mutual Exclusion, Stream Prioritization, Bandwidth Sharing, Error Correction, Padding, Index Parameters, Language List, Metadata, Metadata Library, Extended Stream Properties (with a Stream Properties Object inside), Compatibility, and the encryption and signature objects (reported, not decrypted).
- **Data Object:** every packet with its error correction data, length type flags, property flags, send time, duration and payloads; single and multiple payloads, replicated data with the media object size and presentation time, extension data, compressed payloads and padding.
- **Index objects:** Simple Index, Index, Media Object Index and Timecode Index, checked against the packets. Each of the last three is compared with its own parameter object (Index Parameters, Media Object Index Parameters, Timecode Index Parameters; sections 4.9 to 4.11 and 6.2 to 6.4): the object must be in the header and its specifiers must equal the index's (`asf.index-parameters-missing`, `asf.index-specifiers`), and a Timecode Index Parameters Object may not list index type 1 (section 4.11). Offsets are checked against packet starts only where all packets have one size, since the size of a packet in a file with different minimum and maximum sizes is not known from the header.
- **Unknown objects:** read as data and reported, as the specification tells readers to ignore them. Two GUIDs are named although the specification does not define one of them:
  - `26F18B5D-4584-47EC-9F5F-0E651F0452C9` is the Compatibility Object (section 4.12): the text of revision 01.20.03 misprints the Header Object's GUID for it in section 10.3, and in all the SDK's files of the project's fixtures it is 26 bytes with profile 2 and mode 1, as section 4.12 sets them. MediaInfoLib calls it Compatibility too.
  - `D9AADE20-7C17-4F9C-BC28-8555DD98E2A2` is in neither the specification nor FFmpeg. MediaInfoLib (`File_Wm_Elements.cpp`) calls it Index Placeholder, and the SDK's writer puts it in the Header Extension Object of the constant bitrate files of the project's fixtures (written on Windows Server 2025, wmvcore 12.0.26100), not in its variable bitrate or Lossless files. It is shown by that name, data unread, and readers ignore it.

## ASF: what is reported

- **Findings** for damage and for what breaks the specification: sizes that overrun, counts that disagree with what the file holds (header objects, packets, file size), missing required objects, reserved fields, packets whose lengths do not add up, payloads that overrun, a Data Object of size 0 outside a broadcast, and index entries that point past the data or off a packet start.
- **Statistics** for the Data Object: packets, payloads, packets with multiple payloads or error correction, compressed payloads, padding, overhead, and per stream the media objects, fragmented ones, key frames and presentation times.
- **Metadata** as the container's own tags, in the tree: text, bytes, booleans, numbers and GUIDs, a `WM/Picture` decoded, and an `ID3` byte array handed to the ID3v2 parser. That ID3v2 tag is a tag nested in the metadata object's tag, so the summary lists it ("ID3v2") next to the metadata objects.
- **Media objects** of each audio stream are put together from their payloads, also when they span packets, and go to the codec parser by the stream's format tag.
- **Playback group:** the streams of one file share one `playbackGroup`. They are interleaved in one Data Object and play at the same time, or are bitrate alternatives of which one plays, and the summary counts the longest once either way. The Bitrate Mutual Exclusion Object is shown but not used for this.

## ASF: protection

Protection is reported on the streams (`StreamInfo.Protection`), in the JSON, the CSV and the GUI. Nothing is decrypted and no license is fetched.

- **Content Encryption Object (3.14):** Microsoft Digital Rights Manager version 1. The secret data, protection type, key ID and license URL are read; a protection type other than "DRM" is a finding (`asf.protection-type`).
- **Extended Content Encryption Object (3.15):** the Windows Media Rights Manager 7. Its data is read as a size and bytes, since the specification gives it no structure.
- **Advanced Content Encryption Object (4.13):** the scheme and the system are both "Windows Media DRM for network devices" (section 10.7) unless a record names another system ID. An object with no record protects no stream (the info finding `asf.no-content-encryption-record` says so); a record is kept as far as it was read when the object is cut or has too many entries; data past the object is `asf.encryption-data-size`. Each record's system ID is named from section 10.7, which lists one (`7A079BB6-DAA4-4E12-A5CA-91D38DC11A8D`), its version, the objects it covers and its data. An object record of type 1 names an ASF stream (0 is the whole file) in a WORD; a record may name a stream the file does not have (`asf.encrypted-object-unknown-stream`).
  - Section 4.13 defines type 1 only, and says the length of its ID "should" be 2, a recommendation and not a requirement. Another type (`asf.encrypted-object-id-type`) or another length (`asf.encrypted-object-id-length`) is a finding.
  - The stream such a record names cannot be told, and a record that protects nothing it names would pass ciphertext on to a codec parser as clear data. So the record is taken to cover every stream, as an object that cannot be read in full is, and `asf.content-encrypted` says so.
  - The finding is made once the streams are known. When no record covers a stream of the file, because the records name no stream or only streams the file does not have, it is the info finding `asf.no-content-encryption-record`, which says the object protects none, and no stream is reported as protected.
- **Digital Signature Object (3.16):** the signature type (2 is the only value defined; others are `asf.signature-type`) and data. It signs the header and protects nothing, so a file with only a signature is not protected, and the signature is not verified.
- **Streams:** a stream, of any type, is protected when its Encrypted Content Flag is set (3.3), when a Content Encryption Object or an Extended Content Encryption Object is present (they cover the file), or when an Advanced Content Encryption record names it or 0. The scheme is that of the first object in file order that covers it (the flag alone is "Protection scheme not stated (Encrypted Content Flag)"), and the systems are the Advanced records' system names.
- **Streams no codec parser describes** (a coding without a parser such as MPEG audio, video, a stream without a readable WAVEFORMATEX) are still reported when protected, from the header alone.
- **Encrypted media objects** are never handed to a codec parser. The codec parser (WMA versions 1 and 2, Professional, Lossless, Voice) describes the stream from its WAVEFORMATEX and codec-specific data without packets, and shows "frames not read: encrypted"; the container's play duration and the format's nominal bitrate give the duration and bitrate. An info finding says so (`asf.codec-not-analyzed`).
- **Not covered:** the encrypted payload extension (Encryption Sample ID) is named but its data is not read; DRM license acquisition and any decryption.

## Tags and tag detection

- ASF has its own metadata, so no ID3 or APE tag is looked for inside it.
- A leading ID3v2 tag is the host's, before the ASF header.
- `DeclaredEnd` is the File Properties Object's file size; for a broadcast file or a size that cannot be right, the end of the chain of top-level objects. An ID3v1 or APE tag after it is the file's.

## ASF: damaged and large files

- A damaged object or packet is a finding; the objects and packets that can be read are still read.
- **Limits** keep a crafted file's tree small: at most 10,000 top-level objects, 10,000 objects in the header, 1,000 entries of a Codec List or Advanced Content Encryption Object (`asf.too-many-entries`), 256 claims about the writer or encoder from the header's attributes (the first 256 count; later ones are dropped without a finding), 1,000 entries and 1,000 blocks of an index as nodes (the rest are read for the figures, with a "Further" node), 65,536 packets described one by one (each is some 12 KB of JSON), and 262,144 media objects of a Windows Media Audio stream (`wma.records-limit`). The buffer of a media object grows with the fragments that arrive, so a packet that states an object of 16 MiB costs what it holds.
- **Packet size:** a file whose File Properties Object gives one packet size as minimum and maximum has packets of that size; a packet length field below it, or a present field of 0, is a finding (`asf.packet-length`) and the difference is padding, as FFmpeg reads it (`asfdec_f.c`). The packets are stepped over by the size, not by the field. The packet's Padding node and the statistics' padding bytes both count that difference, only for the bytes the file has: a truncated packet counts no missing bytes, and a packet cut inside its header has no padding.
- **Media object findings per stream:** the findings of a stream's media objects (`asf.object-incomplete`, `asf.object-size-invalid`, `asf.fragment-order`, `asf.fragment-overrun`) are counted per code and per stream, so each stream's damage is reported once under its own stream number.
- **Spread audio** is descrambled by FFmpeg only where the chunk size is not 0 and divides the virtual packet length into more than one chunk; otherwise it decodes the media objects as they are, and so does this analysis.
- **Broadcast files:** a Data Object of size 0 runs to the end of the file, so what is at its end is the Data Object's, not a tag.
- The packets are read twice: the first pass keeps a compact record per packet and payload (the first 65,536 packets are detailed, the rest counted), and builds nodes on demand; the second pass puts media objects together as they are needed.

## WMA versions 1 and 2

Format tags 0x0160 and 0x0161, routed as the codec "wma". The syntax is described in [wma-standard-format.md](../design/wma-standard-format.md); no audio is rebuilt.

### What is read

- **Codec data:** the encode options (exponent coding, bit reservoir, variable block lengths, block size count code), Samples Per Block and Super Block Align of version 2, each at its bytes. Findings for a size other than 4 (version 1) or 10 bytes (version 2) and for the options 0x000D that turn variable block lengths off.
- **Parameters:** the frame length (512, 1,024 or 2,048 samples), the block sizes, the width of the bit offset, noise substitution and its high bands, the exponent bands and the codebook pair, derived from the sampling rate, the channels, the bitrate and the options.
- **Superframes:** one per media object. With the bit reservoir the header (index, frame count, bit offset), the frame that spans from the superframe before, the frames that begin here, and the reservoir left; without it one frame and its padding.
- **Frames and blocks:** block sizes, mid/side flag, coded flags, total gain, noise substitution flags and levels, exponents sent, the exponents (scale factor codes or line spectral pairs, walked to the end of the block) and the run-level coefficients, each field at its bits. A frame has no length field, so reading the syntax is the only way to find where it ends.

### What is reported

- **Statistics:** superframes, frames, samples, duration, average bitrate, the smallest and largest frame, the reservoir figures, the filler between a spanning frame and the bit offset, and counts over all blocks (sizes, mid/side, noise-substituted, reusing exponents, gains, non-zero and escape-coded coefficients).
- **Series:** the bitrate of each frame, the bitrate over time, and with the bit reservoir the bytes each superframe leaves.
- **Samples and duration:** the valid frames less one, times the frame length. For every fixture this equals the samples ffmpeg decodes.
- **Findings:** media objects shorter or longer than the block alignment, a frame count that leaves no frame to end in a superframe, a bit offset past the superframe, a reservoir over 32,768 bytes, invalid frames (`wma.frame-invalid`, with the rule that broke), stuffing and filler between a spanning frame and the bit offset (info), the bits left in the reservoir at the end (info), configurations the decoders do not open (`wma.format-unsupported`) and more than 262,144 media objects (`wma.records-limit`).
- **Encrypted media objects** are not read; the stream is shown as "frames not read: encrypted".

### Channel layout

- One channel is mono and two are stereo (left and right); other counts are not decoded.

### Encoder detection

- ffmpeg's wmav1 and wmav2 encoders pad each superframe with 'N' bytes after the frame, with no bit reservoir and no variable block lengths; with two or more padded superframes the encoder is named with high confidence, with one with medium.
- Microsoft's encoders: version 2, a Samples Per Block of 17 frames, the bit reservoir and variable block lengths name "Microsoft's Windows Media Audio encoder" with medium confidence.

### What is not covered

- The values of exponents, coefficients and noise levels, and any reconstruction of audio.
- Version 1 with variable block lengths and scale factor exponents, and version 2 at 49,048 Hz and above, where the format description leaves the band lists open (no file at hand).
- FFmpeg's handling of a frame that spans a superframe inside a frame, short media objects and partial end checks differs from this analysis in places; the description lists each.

## WMA Professional and Voice

Format tags 0x0162 ("wma-pro") and 0x000A ("wma-voice"). Header level until the clean-room rewrites, as Lossless is:

- The codec-specific data (the type-specific data after the WAVEFORMATEX):
  - **Professional:** the 18 bytes are shown the way Lossless's are (below).
  - **Voice:** the bytes stay the container's bytes; its channel mask is read from them (see "Channel layout").
- The packets are counted, with their total, smallest and largest size, the block alignment, the nominal bitrate the container states and the average bitrate.
- The length comes from the container (the play duration less the preroll), and the stream's samples are that length at the sample rate.
- The stream's average bitrate is the bytes of the packets over that length. The nominal bitrate (the WAVEFORMATEX's nAvgBytesPerSec) is shown apart in the statistics, since it can differ much for a variable bitrate stream. Where the stream starts with a preroll, the packets cover the preroll as well and the figure is a little high. A stream with encrypted samples counts no packets and reports the nominal bitrate.
- The channel mode is Mono for one channel and Stereo for two, and has no value for other counts.
- One info finding per stream says the frames are not read: `wma.pro-not-read` and `wma.voice-not-read`.
- The encoder and writer are named from the file's metadata and layout only (see below).
- A stream whose samples are encrypted is shown as "frames not read: encrypted", with no finding of its own.


## WMA Lossless

Format tag 0x0163, routed as the codec "wma-lossless".

- The 18 bytes of codec data (the sample size of 16 or 24 bits, the channel mask, the decode flags) are shown, the packets are counted, and the length is the container's. The frames are not read yet; a finding says so.
- The length comes from the container (the play duration less the preroll) and the average bitrate is the packets' bytes over it, as for the other codecs above.

## Channel layout

- The speakers come from the channel mask of the WAVEFORMATEXTENSIBLE or, for Professional, Lossless and Voice with 18 bytes or more of codec data, from its bytes 2 to 5 (where they keep it; versions 1 and 2 have other fields there, section 11.1.1 of the ASF specification), in the order of the mask's bits, so a mask of 0x3F is 5.1 (front left and right, center, low-frequency, back left and right). The mask counts only where it names as many speakers as the stream has channels.
- Without a usable mask, one channel is mono and two are stereo (left and right); other counts have no layout.

## Encoder detection

The Encoder node and the Writer node of a stream each list their evidence and name the strongest. The nodes cover the stream. The evidence text gives the offset in the file of the attribute or object it comes from where it has one (the writer's attributes and layout objects are in the header, outside the stream); They are resolved apart: the encoder from claims in the metadata, the writer from the container's layout and metadata. An ASF file's attributes (WMFSDKVersion, MediaFoundationVersion, WM/ToolName, WM/EncodingSettings) are claims of the writer role, since the Windows Media attribute lists give them to no encoder; WMFSDKVersion and MediaFoundationVersion stay so whatever their text. In WM/ToolName, WM/Tool, WM/ToolVersion and WM/EncodingSettings, text that is ffmpeg's muxer version (`Lavf…`) is a writer claim anyway, and text that is a libavcodec encoder ID (`Lavc…`) is an encoder claim, which encoder detection takes only where the stream itself shows ffmpeg.

### Encoder

- **Versions 1 and 2:** the stream's own traits name the encoder, ffmpeg's wmav1 and wmav2 from the 'N' padding of their superframes (high with two or more padded superframes) and Microsoft's from the codec data of version 2 (medium); see above.
- A claim of the encoder role names the encoder (low) unless it names FFmpeg: a `Lavc…` ID in an attribute counts only where the stream itself shows ffmpeg's, as the padding of ffmpeg's encoders does.
- Professional, Lossless and Voice have no trait of their own.

### Writer

- **Medium, from the container's layout:** the files the project encoded on a Windows runner (wmvcore 12.0.26100) show two layouts that tell the writers apart. The objects are the specification's; that a writer chooses them is its habit.
  - The Windows Media Format SDK's writer states WMFSDKVersion, writes a Codec List Object with a description such as "VBR Quality 75, 44 kHz, stereo 1-pass VBR", a Compatibility Object (section 4.12) in the Header Extension Object, and an Index Object (with a Simple Index Object of 0 entries) in its variable bitrate and Lossless files (12 of its 24 fixtures; its 12 constant bitrate files have none, 6 of them WMA Voice). The analyzer does not check the index.
  - Media Foundation's ASF sink states MediaFoundationVersion, writes no Codec List Object, WMFSDKVersion, Compatibility Object or index (11 fixtures). A Language List, Extended Stream Properties, a Metadata Object, a Padding Object and Stream Bitrate Properties are in the files of both writers (all 35 Windows fixtures: 24 SDK, 11 Media Foundation), so they do not tell the writers apart. The rule for Media Foundation requires them anyway, as what "both Microsoft writers write", together with MediaFoundationVersion and none of the SDK's markers, so its medium confidence does not rest on one attribute and two absences; the evidence names what distinguishes the writers.
- **Low:** a claim in the metadata (WMFSDKVersion, MediaFoundationVersion, WM/EncodingSettings, WM/ToolName), which survives a rewrite. ffmpeg's muxer version names FFmpeg as the writer.
  - Of several Low claims the first one names the writer. The claims are in file order, except that the stream's detection puts a muxer's version (`Lavf…`) first when it is the last attribute of the Extended Content Description Object, where ffmpeg's muxer leaves it (`mux.c` removes the old "encoder" entry and appends the new one, and `asfenc.c` writes the attributes in that order). The stamp is last after a plain remux; an explicit `creation_time` (`asfenc.c` removes it) or an `encoder-*` key (`mux.c` removes it) moves the stamp into the removed entry's place, and then the copied claim stays first. No source separates those files from ones other software wrote, so a stamp with attributes after it keeps its place. ffmpeg's ASF muxer copies every attribute of the file it reads (`asfdec_f.c` puts them into the metadata, `asfenc.c` writes them all) and then adds its own `WM/EncodingSettings` after them, so an ffmpeg remux of a Windows Media file keeps WMFSDKVersion or MediaFoundationVersion; the stamp is the program that wrote this file, and the copied version is a claim that differs (`wma.writer-claim-differs`).
  - The fixtures `external-remux-ffmpeg-wma92-cbr-stereo-22k-20-sdk` and `external-remux-ffmpeg-wma92-cbr-stereo-22k-32` are such remuxes (`ffmpeg -c copy` of two committed Windows files, without the bit-exact flags that the other ffmpeg fixtures use). ffmpeg writes the copied attributes as Unicode strings, so a remux's IsVBR is text, which the analyzer takes no bit rate mode from.
  - A file that has the layout of the Windows Media Format SDK or Media Foundation is named by it at Medium confidence, which outranks any Low claim, the muxer's stamp included (no fixture is such a rewrite).
- Two claims of the writer role that name different software are an info finding (`wma.writer-claim-differs`); a claim of the encoder role that names another encoder than the stream is `wma.encoder-claim-differs`. A claim that names software known to write another format (the table in `KnownWriters`) is `wma.writer-other-format`. A writer that differs from the encoder is no finding by itself.
- A layout names the writing software, not the encoding stage: the Windows Media Format SDK and Media Foundation both write the file around whatever codec they drive. That is why a writer version alone stays Low and the layout is Medium, never High.
- What the writer says about its settings is added to that evidence, as its statement and not as something read from the audio: the Codec List Object's name and description ("Windows Media Audio 9.2", "VBR Quality 75, 44 kHz, stereo 1-pass VBR") and the IsVBR attribute. The stream's `isVbr` follows IsVBR. Media Foundation's files carry IsVBR but no Codec List.
- Not used as evidence: `WMFSDKNeeded` and `DeviceConformanceTemplate`, which the SDK writes with fixed values.

## Observed in Microsoft's files

- A frame that begins in one superframe and ends in the next may be followed by bits up to the bit offset the next superframe states: zero bits, or more coded data. A decoder skips them; they are info findings (`wma.frame-stuffing`, `wma.frame-filler`), and the statistics count them. Each finding points to the frame it describes, in the superframe its message names.
  - In the 15 Windows Media Audio 9.2 fixtures (wmvcore 12.0.26100) seven have such bits: all seven have zero bits, and five of them also bits that are not zero. Eight have none: `-22k-20-sdk`, `-22k-32`, `-44k-32-sdk`, `-44k-48-sdk`, and the variable bitrate files at quality 10, 75 (with and without tags) and 90.
  - The analyzer does not use them to name the encoder or the writer.
- Every file ends with bits in the reservoir that no frame uses (`wma.reservoir-at-end`), and they are not all zero. The files at quality 25 and 90 end with a superframe inside a frame.
- The superframe's frame count field counts the frames that begin in the superframe, the last one included where it goes on in the next. The first superframe of a stream, and the one after an error, has no frame in progress, so the bits before its bit offset belong to a frame that is lost and one frame fewer than the field says is read.
- The Simple Index Object of a file whose index is empty states an interval of 0, which is no finding.

## Not yet covered

- The frames of WMA Professional and Voice, removed on 2026-10-29 for their provenance and to return as clean-room rewrites (phase 2), and those of WMA Lossless (phase 3, with the checksum-verifying decoders).
- The audio of any codec (no decoder is built).
- Decrypting protected content (out of scope; see "ASF: protection" for what is reported).
- The contents of script commands and markers beyond their fields (planned: later, a separate item).
