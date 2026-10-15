# Matroska and WebM

Matroska (`.mka`, `.mkv`) and its subset WebM (`.webm`), built on EBML. One parser, "Matroska", reads both; the file is reported as WebM when the EBML header's DocType is `webm`. Written from RFC 9559 (Matroska) and RFC 8794 (EBML), with the codec IDs of draft-ietf-cellar-codec-20 and the WebM Container Guidelines (webmproject.org/docs/container, cited by address, since a web page cannot be pinned). `matroska_specs.py` downloads the RFCs and the draft, pinned by their SHA-256; `matroska_tables.py` generates the element table (`Elements.cs`, 263 elements with their IDs, types, levels, defaults and descriptions) from them and checks it in CI.

## What is read

- **EBML:** the variable-size integers of IDs and sizes (RFC 8794, section 4), the header (EBMLVersion, EBMLReadVersion, EBMLMaxIDLength, EBMLMaxSizeLength, DocType and its versions; section 11.2), unknown sizes, Void and CRC-32 elements (section 11.3). A CRC-32 is verified (IEEE, little endian, over the siblings that follow it in the parent).
- **Segment:** SeekHead (each position checked against the element it points to), Info (timestamp scale, duration, titles, MuxingApp, WritingApp, UIDs), Tracks with every TrackEntry (audio fields in full; video, subtitle and other tracks at container level only), Cues (each point's track positions checked), Chapters, Attachments and Tags, all as nodes with values and descriptions.
- **ContentEncodings:** the order, scope, type, compression and encryption elements. Header stripping is undone before a frame is routed; encryption (type 1) is reported and never decoded.
- **Clusters:** kept as compact records (about 120 bytes per block), with nodes built when read. Each block's track, timestamp (the Cluster's plus the block's own), flags and frames come from SimpleBlock and BlockGroup (Block, BlockDuration, ReferenceBlock, DiscardPadding, BlockAdditions), with all four lacing modes: none, Xiph, fixed-size and EBML (RFC 9559, section 10.3).
- **Unknown sizes:** a Segment or Cluster of unknown size ends where the next element that cannot be its child starts, as a live stream needs. Any other element with an unknown size is a finding.
- **Unknown and misplaced elements:** an element the table does not list, one with a reserved ID or one under the wrong parent is a node and a finding; the walk goes on.

## What is routed

- **Audio frames** go to the codec parsers by codec ID, through the host's routes (`ParseStream` where the frames can be handed over as one payload, `ParsePackets` for packets as mapped bytes):
  - `A_AAC` and the legacy `A_AAC/MPEG2/*` and `A_AAC/MPEG4/*` IDs to AAC (CodecPrivate is the AudioSpecificConfig);
  - `A_AC3` and `A_EAC3` to AC-3 and E-AC-3, `A_DTS` and its extensions to DTS;
  - `A_VORBIS` to Vorbis (CodecPrivate is the three headers in Xiph lacing, which are passed as the first packets) and `A_OPUS` to Opus (CodecPrivate is the OpusHead; CodecDelay is compared with the pre-skip, `opus.container-delay-differs`);
  - `A_MPEG/L1`, `A_MPEG/L2` and `A_MPEG/L3` are counted at container level (see below).
- **The codec's stream is merged** into the file's streams as MP4's is (codec, sample rate, channels, duration, bitrate, encoder), and the Matroska track's own fields are compared with it: `matroska.sample-rate-differs`, `matroska.channels-differ`.
- **Header stripping** is undone first. A track whose frames are zlib-compressed, or stripped and not AAC, Vorbis or Opus, is not routed, because the AC-3, DTS and MPEG audio parsers place their nodes at the offsets of contiguous file bytes: `matroska.track-encoding-not-read` says so. Encrypted tracks give `matroska.track-encrypted`.
- **Codec IDs without a parser** (FLAC, ALAC, TrueHD, TTA, WavPack, PCM, MLP, RealAudio, QuickTime and the rest) give an Info finding, `matroska.codec-not-analyzed`: their frames are counted and shown as blocks, and their parsers are not written yet (phase 3).
- **Several audio tracks** play together, so their streams are marked `IsParallel`: the summary and the CSV count the longest track's duration and samples once instead of adding them up. JSON streams carry `"isParallel": true`.

## What is reported

- A node tree: the EBML Header, the Segment with its elements, every Cluster with its blocks and frames, and a "Track N Frames" node for each audio track whose frames are routed. Cluster nodes are streams (`NodeKind.Stream`) and blocks are frames.
- Streams for the audio tracks and, for a track whose parser cannot read it, a container-level stream with the codec ID, channels, sample rate, bits per sample, duration and the frames counted.
- Findings, all with the prefix `matroska.`:
  - **Structure:** `no-ebml-header`, `header-field`, `header-limits`, `header-version`, `doctype-unknown`, `segment-header-missing`, `segment-size-unknown`, `cluster-size-unknown`, `unknown-size-not-allowed`, `element-header-invalid`, `element-id-long`, `size-too-long`, `element-overruns-parent`, `element-truncated`, `misplaced-element`, `unknown-element`, `deprecated-element`, `too-deep`, `too-many-elements`, `value-size`, `invalid-text`, `uid-length`.
  - **Top level:** `missing-info`, `missing-tracks`, `no-clusters`, `seek-position`, `cue-position`, `crc-mismatch`, `crc-position`, `crc-size`.
  - **Tracks:** `track-field`, `audio-fields`, `track-number-duplicate`, `track-empty`, `track-not-analyzed`, `track-encoding-not-read`, `track-encrypted`, `encoding-order`, `codec-private-missing`, `codec-private-invalid`, `codec-not-analyzed`, `sample-rate-differs`, `channels-differ`.
  - **Clusters and blocks:** `cluster-timestamp-missing`, `cluster-timestamp-position`, `timestamp-scale`, `timestamps-backwards`, `block-short`, `block-too-large`, `block-track-unknown`, `lacing-invalid`, `lacing-not-allowed`, `lacing-single`, `group-no-block`, `group-multiple-blocks`.
  - **WebM:** `webm-codec`, `webm-timestamp-scale` (WebM allows VP8, VP9, AV1, Vorbis, Opus and WebVTT, and wants a timestamp scale of 1,000,000).
- A finding points to a node of the tree; those of a block point to its SimpleBlock or Block.

## Channel layout

- For a routed track the layout is the codec parser's (AAC, AC-3, DTS, Vorbis and Opus each name their speakers in their own order); the Matroska track's `Channels` is compared with it.
- A track counted at container level has only its `Channels` count and no layout: Matroska's ChannelPositions element is not in RFC 9559, so no speaker is named.

## Encoder detection

- The audio encoder is decided by the codec parsers, from the frames they read.
- MuxingApp and WritingApp (for example "Lavf60.16.100", "libebml v1.4.5 + libmatroska v1.7.1" and "mkvmerge v81.0") are shown as Info and Segment values. They name the program that wrote the container, not the audio encoder, so they do not become claims about the stream yet: the place for them is marked in `Route.cs` (`RouteTracks`), to be filled when the encoder and writer roles are split.

## Detection

- An EBML header at the start with a DocType of `matroska` or `webm`, followed by a Segment: class 100. A header with no Segment after it: 90 (100 when the data ends right after it). A header that does not hold (an ID or size that is not an EBML header's, or no DocType): 70. Any other DocType: not Matroska.
- A Segment at the start with a consistent child, as in a file whose header was cut off: 80. A run of Top-Level Elements (or a Cluster with a Timestamp) from the start: 60.
- `DeclaredEnd` is the end of the Segment when its size is known: a trailing ID3v1 or APE tag after it is the file's. A Segment of unknown size declares no end.
- `--as matroska` (also `mkv`, `mka`, `webm`) reads any bytes as Matroska.

## Limits

- Elements outside Clusters become nodes up to 250,000 in all (`matroska.too-many-elements`); nesting is read to 40 levels (`matroska.too-deep`).
- A Cluster's blocks and elements are kept up to 6,000,000 records in all; a block of more than 64 MiB is not read (`matroska.block-too-large`).
- Memory is bounded by the compact records: `MemoryTests` hold the blocks to about 120 bytes each.

## Not covered

- MPEG audio frames are counted at container level only: the MPEG parser reads a contiguous payload, and Matroska frames are packets.
- FLAC, ALAC, TrueHD, TTA, WavPack, PCM, MLP and the RealAudio and QuickTime IDs have no parser here (phase 3).
- bzlib and lzo compression and all encryption are reported, not undone; zlib is inflated only for the codecs that can read opaque data.
- `A_MS/ACM` (a WAVEFORMATEX) is not routed.
- The legacy `A_AAC/MPEG2|4/*` IDs carry no AudioSpecificConfig, so the AAC parser warns about a missing configuration.
- Video, subtitles and buttons are read at container level only; Chapters, Attachments and Tags are shown, not interpreted (a Tags value is not matched against a stream).
- mkvmerge writes two elements (0x45BD and 0x4598) that RFC 9559 does not list; they are `matroska.unknown-element`.
- Only the WebM restrictions on codecs and the timestamp scale are checked, not the rest of the guidelines.

## Fixtures and cross-checks

- `tests/fixtures/matroska`: 54 hand-built files (`make_matroska_fixtures.py`, worked out by the independent writer and reader `matroska_model.py`) and 40 written by ffmpeg and mkvmerge (`make_external_matroska_fixtures.py`), with a manifest and a golden report each. See [development.md](../development.md).
- `crosscheck_matroska_fixtures.py` checks the manifests against mkvinfo, ffprobe (streams, packets and presentation times), ffmpeg decoding and MediaInfo.
