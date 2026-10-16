# Matroska and WebM

Matroska (`.mka`, `.mkv`) and its subset WebM (`.webm`), built on EBML. One parser, "Matroska", reads both; the file is reported as WebM when the EBML header's DocType is `webm`. Written from RFC 9559 (Matroska) and RFC 8794 (EBML), with the codec IDs of draft-ietf-cellar-codec-20 and the WebM Container Guidelines (webmproject.org/docs/container, cited by address, since a web page cannot be pinned). `matroska_specs.py` downloads the RFCs, the draft and the Matroska element registry (`ebml_matroska.xml`, pinned by commit and SHA-256), all pinned by their SHA-256; `matroska_tables.py` generates the element table (`Elements.cs`, 273 elements with their IDs, names, types, paths and default values, and descriptions) from them and checks it in CI. Ten elements muxers write are in the registry and not in RFC 9559 (EditionFlagHidden, ChapterFlagEnabled, ChapterTrack, ChapterTrackUID, ChapterSkipType, EditionDisplay, EditionString, EditionLanguageIETF, TagBlockAddIDValue and Emphasis); their descriptions cite "the Matroska element registry".

## What is read

- **EBML:** the variable-size integers of IDs and sizes (RFC 8794, section 4), the header (EBMLVersion, EBMLReadVersion, EBMLMaxIDLength, EBMLMaxSizeLength, DocType and its versions; section 11.2), unknown sizes, Void and CRC-32 elements (section 11.3). A CRC-32 is verified (IEEE, little endian, over the siblings that follow it in the parent).
- **Segment:** SeekHead (each position checked against the element it points to), Info (timestamp scale, duration, titles, MuxingApp, WritingApp, UIDs), Tracks with every TrackEntry (audio fields in full; video, subtitle and other tracks at container level only), Cues (each point's track positions checked), Chapters, Attachments and Tags, all as nodes with values and descriptions.
- **ContentEncodings:** the order, scope, type, compression and encryption elements. Header stripping is undone before a frame is routed; encryption (type 1) is reported and never decoded.
- **Clusters:** kept as compact records (about 120 bytes per block), with nodes built when read. Each block's track, timestamp (the Cluster's plus the block's own), flags and frames come from SimpleBlock and BlockGroup (Block, BlockDuration, ReferenceBlock, DiscardPadding, BlockAdditions), with all four lacing modes: none, Xiph, fixed-size and EBML (RFC 9559, section 10.3).
- **Unknown sizes:** a Segment or Cluster of unknown size ends where the next element that cannot be its child starts, as a live stream needs. Any other element with an unknown size is a finding.
- **Unknown and misplaced elements:** an element the table does not list, one with a reserved ID or one under the wrong parent is a node and a finding; the walk goes on.

## What is routed

- **Audio frames** go to the codec parsers by codec ID, through the host's route `ParsePackets`, one packet per frame as mapped bytes:
  - `A_AAC` to AAC (CodecPrivate is the AudioSpecificConfig); the legacy `A_AAC/MPEG2/*` and `A_AAC/MPEG4/*` IDs have no CodecPrivate, so the AudioSpecificConfig is worked out from the ID (object type MAIN 1, LC 2, SSR 3, LTP 4), the SamplingFrequency, the Channels and, for `/SBR`, the OutputSamplingFrequency (explicit SBR signaling), following ISO/IEC 14496-3, as FFmpeg's `matroskadec.c` does for the common rates, and shown under the CodecID that gives its values;
  - `A_AC3` and `A_EAC3` to AC-3 and E-AC-3, `A_DTS` and its extensions to DTS;
  - `A_VORBIS` to Vorbis (CodecPrivate is the three headers in Xiph lacing, which are passed as the first packets) and `A_OPUS` to Opus (CodecPrivate is the OpusHead; CodecDelay is compared with the pre-skip, `opus.container-delay-differs`);
  - `A_MPEG/L1`, `A_MPEG/L2` and `A_MPEG/L3` are counted at container level (see below).
- **The codec's stream is merged** into the file's streams as MP4's is (codec, sample rate, channels, duration, bitrate, encoder), and the Matroska track's own fields are compared with it: `matroska.sample-rate-differs`, `matroska.channels-differ`.
- **Header stripping and zlib** are undone first, for Opus and Vorbis only. A track of another codec whose frames are zlib-compressed or header-stripped is not routed, because the AAC, AC-3, DTS and MPEG audio parsers place their nodes at the offsets of contiguous file bytes: `matroska.track-encoding-not-read` says so. Encrypted tracks give `matroska.track-encrypted`. A frame or CodecPrivate that is no zlib data gives `matroska.frame-encoding-invalid` or `matroska.codec-private-invalid`; the frames before it are analyzed.
- **Codec IDs without a parser** (FLAC, ALAC, TrueHD, TTA, WavPack, PCM, MLP, RealAudio, QuickTime and the rest) give an Info finding, `matroska.codec-not-analyzed`: their frames are counted and shown as blocks, and their parsers are not written yet (phase 3).
- **Several audio tracks** play together, so their streams share a `PlaybackGroup` (one per Segment): the summary and the CSV count the longest track's duration and samples once instead of adding them up, and two Segments add up. JSON streams carry `"playbackGroup"`. A track whose codec parser reports several streams is not grouped.

## What is reported

- A node tree: the EBML Header, the Segment with its elements, every Cluster with its blocks and frames, and a "Track N Frames" node for each audio track whose frames are routed. Cluster nodes are streams (`NodeKind.Stream`) and blocks are frames.
- Streams for the audio tracks and, for a track whose parser cannot read it, a container-level stream with the codec ID, channels, sample rate, bits per sample, duration and the frames counted.
- Findings, all with the prefix `matroska.`:
  - **Structure:** `no-ebml-header`, `header-field`, `header-limits`, `header-version`, `doctype-unknown`, `segment-header-missing`, `segment-size-unknown`, `cluster-size-unknown`, `unknown-size-not-allowed`, `element-header-invalid`, `element-id-long`, `size-too-long`, `element-overruns-parent`, `element-truncated`, `misplaced-element`, `unknown-element`, `deprecated-element`, `too-deep`, `too-many-elements`, `value-size`, `invalid-text`, `uid-length`.
  - **Top level:** `missing-info`, `missing-tracks`, `no-clusters`, `seek-position`, `cue-position`, `crc-mismatch`, `crc-position`, `crc-size`.
  - **Tracks:** `track-field`, `audio-fields`, `track-number-duplicate`, `track-empty`, `track-not-analyzed`, `track-encoding-not-read`, `track-encrypted`, `encoding-order`, `frame-encoding-invalid`, `codec-private-missing`, `codec-private-invalid`, `codec-not-analyzed`, `sample-rate-differs`, `channels-differ`.
  - **Clusters and blocks:** `cluster-timestamp-missing`, `cluster-timestamp-position`, `timestamp-scale`, `timestamps-backwards`, `block-short`, `block-too-large`, `block-track-unknown`, `lacing-invalid`, `lacing-not-allowed`, `lacing-single`, `group-no-block`, `group-multiple-blocks`.
  - **Writer:** `writer-claim-differs`, `writer-other-format`.
  - **WebM:** `webm-codec`, `webm-timestamp-scale` (WebM allows VP8, VP9, AV1, Vorbis, Opus and WebVTT, and wants a timestamp scale of 1,000,000).
- A finding points to a node of the tree; those of a block point to its SimpleBlock or Block.

## Channel layout

- For a routed track the layout is the codec parser's (AAC, AC-3, DTS, Vorbis and Opus each name their speakers in their own order); the Matroska track's `Channels` is compared with it.
- A track counted at container level has only its `Channels` count and no layout: Matroska's ChannelPositions element is not in RFC 9559, so no speaker is named.

## Encoder and writer

- **The encoder** is decided by the codec parsers, from the frames they read. The Matroska parser names none.
- **The writer** is the software that put the audio into the file. The Segment's MuxingApp and WritingApp (RFC 9559, sections 5.1.2.13 and 5.1.2.14) are claims of the writer role, added before the tracks are routed, so every codec parser weighs them with the claims of the tags. Each is a low-confidence claim of its element; the evidence points at the element.
  - ffmpeg's muxer version ("Lavf60.16.100", in both elements) names FFmpeg. A libavcodec encoder ID ("Lavc60.31.102 libopus") in either element takes the encoder role instead, as in the tags.
  - mkvmerge writes the libraries in the MuxingApp ("libebml v1.4.5 + libmatroska v1.7.1") and the program in the WritingApp ("mkvmerge v82.0 ('I'm The President') 64-bit"). The libraries say no more than the program that uses them and would only contradict it, so a MuxingApp that names them counts only when no WritingApp does.
  - Two claims that name different software are `matroska.writer-claim-differs` (Info), and software known to write another kind of stream (LAME on a PCM or FLAC track) is `matroska.writer-other-format` (Info). The codec parsers that read a tag of their own (Vorbis and Opus comments, AAC) keep their own findings for it.
  - A stream no codec parser reads, and a stream whose parser names none (Opus and Vorbis in a Matroska track carry a comment only in their headers), get the Writer from the Matroska parser: a Writer node under the track's frames node (or the TrackEntry), and the stream's Writer. A comment inside the stream that names a writer (a Vorbis vendor string copied from an Ogg file) is the stream's and outranks the container's claims there.
  - The claims of every Segment join the file's. See the article on writer detection.

## Detection

- An EBML header at the start with a DocType of `matroska` or `webm`, followed by a Segment: class 100. A header with no Segment after it: 90. A header that does not hold (an ID or size that is not an EBML header's, or no DocType): 70. Any other DocType: not Matroska.
- A Segment at the start with a consistent child, as in a file whose header was cut off: 80. A run of Top-Level Elements (or a Cluster with a Timestamp) from the start: 60.
- `DeclaredEnd` is the end of the Segment when its size is known. For a Segment of unknown size (a live recording) it is the end of the Top-Level Elements and, in a Cluster of unknown size, of the children the schema places in a Cluster: a trailing ID3v1 or APE tag after them is the file's, not an element's.
- `--as matroska` (also `mkv`, `mka`, `webm`) reads any bytes as Matroska.

## Limits

- Elements outside Clusters become nodes up to 250,000 in all (`matroska.too-many-elements`); nesting is read to 40 levels (`matroska.too-deep`).
- A Cluster's blocks and elements are kept up to 6,000,000 records in all; a block of more than 64 MiB is not read (`matroska.block-too-large`).
- The frames of laced blocks that go to a codec parser (or whose track is not known yet) are counted against a budget of 4,000,000 in all (`matroska.too-many-elements`), since a block of seven bytes can state 256 frames of no bytes; fixed-size lacing keeps no sizes.
- Memory is bounded by the compact records: `MemoryTests` hold the blocks to about 120 bytes each.

## Not covered

- MPEG audio frames are counted at container level only: the MPEG parser reads a contiguous payload, and Matroska frames are packets.
- FLAC, ALAC, TrueHD, TTA, WavPack, PCM, MLP and the RealAudio and QuickTime IDs have no parser here (phase 3).
- bzlib and lzo compression and all encryption are reported, not undone; zlib is inflated only for the codecs that can read opaque data.
- `A_MS/ACM` (a WAVEFORMATEX) is not routed.
- Video, subtitles and buttons are read at container level only; Chapters, Attachments and Tags are shown, not interpreted (a Tags value is not matched against a stream).
- DiscardPadding is read and shown but not applied: the Opus sample count of a track includes the padding at the end that DiscardPadding says a player drops.
- Only the WebM restrictions on codecs and the timestamp scale are checked, not the rest of the guidelines.

## Fixtures and cross-checks

- `tests/fixtures/matroska`: 79 hand-built files (`make_matroska_fixtures.py`, worked out by the independent writer and reader `matroska_model.py`) and 40 written by ffmpeg and mkvmerge (`make_external_matroska_fixtures.py`), with a manifest and a golden report each. See [development.md](../development.md).
- `crosscheck_matroska_fixtures.py` checks the manifests against mkvinfo, ffprobe (streams, packets and presentation times), ffmpeg decoding and MediaInfo.
