# DTS-HD files (.dtshd)

The `.dtshd` container of DTS-HD: chunks of metadata around a DTS stream. Written from ATSC A/103:2014 Annex E ("DTS-HD File Structure", Tables E.1 to E.20), with FFmpeg 6.1.1's `libavformat/dtshddec.c` and MediaInfo 24.01's `File_Dts.cpp` for what they read. The stream inside is read by the [DTS](dts.md) parser.

## What is read

- **Chunks:** an 8-character ID, a 64-bit big endian size and a payload. The size covers the payload, reserved bytes and the one to three zero bytes that bring the next chunk to a 32-bit boundary (E.2.1). Every chunk is a node with its size, its fields, and the reserved bytes or padding that follow them.
- **DTSHDHDR** (E.2.4): the version, the time code (reference clock code, Table E.4, and the time stamp as a time of day and samples), the time code frame rate (Table E.5), the stream flags (variable bit rate, peak bit rate smoothing, navigation table, core substream, extension substreams; Table E.6), the number of audio presentations and of extension substreams.
- **FILEINFO** and **AUPRINFO** (E.2.5, E.2.9): their text, null terminated.
- **CORESSMD** and **EXTSS_MD** (E.2.6, E.2.7): the core's maximum sampling frequency, bit rate, channel mask and frame payload; the extension substreams' average bit rate and, by the variable bit rate flag, the peak rate and smoothing buffer or the frame payload.
- **AUPR-HDR** (E.2.8): the presentation's flags (Table E.12), maximum sampling frequency, number of frames, samples per frame, original samples, channel mask with its speakers (Table E.9), codec delay, and, where the flags say so, the backward compatible core's figures and the LSB trim.
- **NAVI-TBL**, **BITSHVTB** and **BRANCHPT** (E.2.10, E.2.11, E.2.16): the counts, interval and entry size; the entries are shown as bytes. A count that the chunk's size cannot hold is `dtshd.table-size`.
- **TIMECODE** (E.2.13): the clock, frame rate, start and reference positions and their residuals.
- **BLACKOUT** (E.2.15) and chunks the standard does not define: their size, no more. A reader skips what it does not know (E.2).
- **STRMDATA** (E.2.12): the payload goes to the DTS codec parser through the host's route, which reads it as a `.dts` file: frames, units, extension substreams, gaps, findings and the stream's statistics are the same, under the chunk's node.
- **BUILDVER** (E.2.14): see below.
- Chunks after STRMDATA are read too (BUILDVER and TIMECODE usually follow the stream; FFmpeg skips STRMDATA and reads on, MediaInfo reads them as the file's footer).

## BUILDVER and the encoder

- Table E.1 lists BUILDVER as the DTS-HD encoder version identification, and E.2.14 describes its text as "compiler information". ATSC A/103:2014 names the chunk "DTS-HD Encoder Version Identification" in Table E.1, so it goes to the encoder: the DTS parser names the encoder as written, with low confidence, with the chunk as evidence (`dts.encoder-claim` for a second claim that names other software). The text may still name the authoring tool, which is why the confidence stays low.
- FFmpeg ignores the chunk, MediaInfo reads the string and does not report it.

## Metadata against the stream

- The presentation header's maximum sampling frequency and the number of channels of its channel mask are compared with the stream the file holds when the file has one AUPR-HDR chunk: FFmpeg sets up its decoder from them. A difference is `dtshd.metadata-differs`. The frame count, length and codec delay are shown and not compared.

## Detection

- A file is a DTS-HD file when its first eight bytes are `DTSHDHDR`, as E.2.2 and FFmpeg's probe have it, and the chain of chunks from there holds: each chunk header has an ID of printable characters and a size of 4 to 2^61 bytes that FFmpeg accepts (a smaller size counts only for the last chunk of the file or when a header that FFmpeg accepts follows it), and the chunk ends inside the data (a STRMDATA chunk that the data cuts short counts). Class 100 with the chunks counted (`SniffResult.Frames`, at most 16) and the end of the last; no checksum is defined, so none is verified. The signature followed by a chain that breaks after the first chunk is class 80, a signature with a first chunk whose size is invalid 70. Class 100 beats the DTS parser's chain further in (class 50) in the stream chunk, whatever the order of the parsers.
- The end of the chain is the file's declared end (`DeclaredEnd`), walked as the parser reads by the same rule as the sniff: a chunk under 4 bytes, which the parser reads with a finding, goes on the chain only when it ends the file or a plain chunk header follows it. A tag signature inside a chunk is not a tag, and an ID3v1 tag after the last chunk is one even though its first sixteen bytes can read as a chunk ID and a size of zero (a title of five characters padded with nulls).
- A file whose chunks do not start at its first byte (junk in front) is not found as a DTS-HD file; the DTS parser reads the stream in it.

## Findings

- `dtshd.chunk-truncated` (the data ends inside a chunk), `dtshd.chunk-size` (a size under 4, which FFmpeg refuses, or too small for the chunk's fields), `dtshd.chunk-header` (bytes where a chunk should start that are no header, or a size beyond 2^61: the rest is not read), `dtshd.chunk-misaligned` (a chunk off a 32-bit boundary), `dtshd.trailing-data` (fewer than 16 bytes after the last chunk), `dtshd.header-first` (the first chunk is not DTSHDHDR, reachable by forcing the format), `dtshd.header-repeated`, `dtshd.stream-repeated`, `dtshd.no-stream-data`, `dtshd.reserved-value` (the reference clock code 3, a frame rate above 7, reserved flags), `dtshd.table-size`, `dtshd.metadata-differs`, `dtshd.stream-not-analyzed`, `dtshd.chunk-count` (more than 1,024 chunks: the rest is not read).
- Findings that can occur at every chunk (`dtshd.chunk-size`, `dtshd.chunk-misaligned`, `dtshd.reserved-value`, `dtshd.table-size`, `dtshd.header-repeated`, `dtshd.stream-repeated`) are reported once for each field or cause, with a count, pointing at the first case ("First case: ... (N times in the file.)"). The report orders findings by the offset of their node, so these stay in file order.

## Not covered

- The entries of the tables, the values of the residuals of the time code, the meaning of LSB trim (the standard does not give it) and a check of the frame count against the stream.
- FILEINFO's text is read as the standard has it, a null terminated string; MediaInfo reads a length byte first, which no source here confirms.
- No real `.dtshd` file or DTS:X sample could be downloaded for the tests: none is on the FFmpeg sample archive or another public address checked. The fixtures are hand-built from the standard and cross-checked with ffprobe's demuxer and MediaInfo.
