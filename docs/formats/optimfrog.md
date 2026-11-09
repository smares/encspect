# OptimFROG

OptimFROG lossless files (`.ofr`) at header level: the blocks, the main block's fields, the stored WAV header and footer, the CRC-32 of every data block and the encoder's version and compression. The compressed audio is not read: no public description of it exists, and only OptimFROG's own library decodes it. Design: [tak-optimfrog-headers.md](../design/tak-optimfrog-headers.md).

Sources:

- The author's specification of the compressed file format, `SDK/Documentation/format.txt` of OptimFROG 5.100 (SDK 1.600), and its `changelog.txt`. The download page is http://losslessaudio.org/Downloads.php.
- OptimFROG 5.100 for Linux x64 (`ofr`, `off`, `ofs` and the SDK library) as black-box writers of the files in Encspect's tests, and `luckynight.ofr` (OptimFROG 4.509, from samples.ffmpeg.org) walked once for the design.
- Nothing is read from FFmpeg, MediaInfo or any other implementation: FFmpeg has no OptimFROG demuxer and MediaInfoLib 26.01 has no parser for it.

## What is read

- The blocks, each a 4-character ID, a 4-byte length and the data (little-endian), in the order of the specification's relation: `['ID3'] 'OFR ' [...] 'HEAD' 'COMP'* 'TAIL' [...] ['MD5 '] ['RECV']`, then an optional APEv2 and ID3v1 tag.
- The main block: the total number of samples (over all channels: the samples of each channel are this number over the channels), the sample type (`UINT8` to `FLOAT32_24`), the channel configuration (`MONO` or `STEREO_LR`), the sample rate, the encoder ID (version = ID >> 4 plus 4500, written x.xxx, and the build system = ID & 15), the compression byte (mode = byte >> 3, speedup = byte & 7) and, from 5.100, the minimum decoder version (stored less 4500). Bytes after the fields it knows are shown as additional fields and skipped, as the specification asks.
- `HEAD` and `TAIL`: the stored bytes of the source file's header and footer, named as Monkey's Audio names them ("WAV Header Data", "WAV Terminating Data"). A RIFF, RF64, BW64 or Wave64 header gives its format tag, channels and channel mask by the reader Monkey's Audio shares (`WavHeader` in the core library); the RIFF chunks of the header and the footer are listed, and lossyWAV's `fact` chunk is read with `FactText`. A header that is no WAVE header (`ofr --raw --headersize`, as in `external-raw-headersize.ofr`) is shown as bytes.
- Every `COMP` block: the CRC-32 (the plain IEEE CRC-32 of zlib over the block's data after the CRC field), compared; the samples (values over all channels), the sample type, the channel configuration, the two bytes the specification calls reserved for internal use (shown, never judged), the encoder ID and the compressed data.
- `MD5 ` (shown, not compared: it covers the decoded audio), `RECV` (shown, not interpreted) and blocks of unknown ID (by ID and length).
- Tags: an ID3v2 tag before the file, and APEv2, Lyrics3 and ID3v1 tags after the last block are the host's. The file declares its end (`DeclaredEnd`: the end of the last block the walk accepts).

## The walk

- A block header is four printable ASCII bytes and a length. The walk never stops on a tag's signature, only on a header that is none (the rule DTS-HD's chunk walk follows, [dtshd.md](dtshd.md)).
- A block of a known ID whose length runs past the data is cut (`optimfrog.block-cut`): it is read as far as it goes, its CRC-32 is not compared, and it ends the walk.
- A block of an unknown ID goes on the walk only when it fits and either ends the data or is followed by a block header whose length fits too (`optimfrog.block-unknown`, Info); otherwise the walk stops before it and the bytes are left to the host's search for tags, and are `optimfrog.data-after-blocks` if they are no tag.
- The report keeps at most 1,048,576 blocks one by one (`Records.MaxBlocks`); the rest are counted and their CRC-32s compared (`optimfrog.blocks-limit`).

## Detection

- `OFR ` at the start of the data (after leading tags) with a main size of at least 15, a sample type of 0 to 10, a channel configuration of 0 or 1, a sample rate above 0 and a block of a known ID after the main block is certain (class 100).
- The main block alone with valid fields is likely (80); the four bytes alone, or fields that cannot be a main block's, are possible (70).
- `OFRX` (below) is matched like `OFR `.
- A correction file (`.ofc`) is matched by structure: `CORR` blocks one after another, the first one's CRC-32 holding and followed by another header or the end of the data is certain (100); blocks that fit with a CRC-32 that fails are likely (80); the signature alone is possible (70). It is named "OptimFROG correction data" (`optimfrog.correction-file`, Info: the file holds correction data only, adds to the `.ofs` with the same blocks and cannot be decoded alone, as for WavPack's `.wvc`); its blocks' CRC-32s are compared. Matches are not made further into the data, and a self-extracting file (`--selfextract`: an executable stub before the data) is not matched.

## Observations from Encspect's test files

All from OptimFROG 5.100 (`tests/fixtures/optimfrog`, [development.md](../development.md)); every file was encoded twice and the two files are equal byte for byte.

- **Speedup:** every file stores 4x. The speedup was deprecated in 4.506; `luckynight.ofr` (4.509) stores 4x too.
- **Mode:** the stored mode does not tell the command line's modes apart ([optimfrog-modes](../../src/Encspect.Host/Topics/optimfrog-modes.md) lists which store the same value). The report names the mode and the speedup as stored and never a preset, which sets options the header does not store.
- **Blocks** count values over all channels: `--seek normal` gives blocks of 882,000 values, `fast` 441,000, `min` one block for the file.
- **Float files:** `off` writes the main block's ID as `OFRX` and a block `DETA` after the data blocks, neither defined by the specification. The fields of `OFRX` are those of `OFR ` (the SDK library is expected to read them the same way, which the cross-check compares; it has not run yet), so the analyzer reads it as the main block and shows `DETA` as a block of its own (neither claim of equivalence has been checked against the library until the cross-check job has run green). `off` writes `.ofr` files: it appends the extension to any other name it is given. Both are read without a finding: `OFRX` is the main block `off` writes, and `DETA` is shown as a known, undocumented block. Its `--sampletype FLOAT32_1`, `FLOAT32_16` and `FLOAT32_24` give the same file as no option (sample type 8, `FLOAT32_0`), `--mantissabits` leaves the main block unchanged, and `off` refuses an integer WAVE file.
- **DualStream:** `ofs` writes the same blocks with a main block that differs from a lossless file's only in the minimum decoder version (4.520; a lossless file stores 4.600, or 4.520 at preset 0) and in the reserved bytes of its data blocks. It writes a correction file (`.ofc`) beside the `.ofs` with `--correction`, a run of `CORR` blocks with a CRC-32 each, and the `.ofs` is the same file with and without it, so its header does not say whether a correction file was written and no `IsCorrectionFileCreated` is reported. The `.ofc` fixtures are named `*-correction-data.ofc` so that their manifests do not take the stem of the `.ofs`.

## Damage

- A block whose CRC-32 fails (`optimfrog.block-crc-mismatch`, once with a count).
- Data cut in a block (`optimfrog.block-cut`), a block whose length breaks the specification (`optimfrog.block-size-invalid`: a main block under 15 bytes, a data block under 14, an MD5 block other than 16), blocks out of the order of the relation (`optimfrog.block-out-of-order`), a block that differs from the main block in sample type, channel configuration or encoder ID (`optimfrog.block-differs`).
- Data blocks whose samples do not add up to the main block's total (`optimfrog.samples-mismatch`, which also catches a file cut at a block boundary or a missing block), a total the channels do not divide (`optimfrog.samples-channels-mismatch`), a sample rate of 0 (`optimfrog.sample-rate-zero`), a missing `HEAD` or `TAIL` (`optimfrog.head-missing`, `optimfrog.tail-missing`).
- Values the specification does not define: a sample type above 10, a channel configuration above 1, a build system of 9 to 14, a mode of 15 to 31, a speedup of 3 to 7 (`optimfrog.value-undefined`, Info).
- The stored WAV header: `optimfrog.wav-header-damaged`, `optimfrog.wav-channels-differ`, `optimfrog.channel-mask-mismatch`, `optimfrog.header-fact-text-unrecognized`.
- Correction files: `optimfrog.correction-file` (Info), and the block findings above for a CRC-32 that fails or a cut block.
- `optimfrog.extension-differs` (Info, from the host): the name ends in `.ofs` and the bytes look lossless.
- Data that does not begin with a main block, when the parser is forced (`optimfrog.main-block-missing`), and the writer claims of tags (`optimfrog.writer-claim-differs`).

## Reported

- The sample rate, channels, bits per sample (32 for the float types), samples, duration and average bitrate (the bits of the data blocks, headers included, over the time their samples last).
- Statistics: Mode, Speedup, Sample Type, Build System, Channel Layout, Blocks, Verified Block Checksums and Failed Block Checksums, the smallest and largest block, Minimum Bitrate and Maximum Bitrate, Bitrate Mode and Bitrate Distribution.
- Series: Bitrate (per block) and Bitrate Over Time (per second), as TTA draws them.
- Channel layout: one channel is the center and two are left and right; a nonzero channel mask in the stored WAV header sets the layout where it is there, as in Monkey's Audio.
- Encoder "OptimFROG 5.100" (high confidence) from the encoder ID; processor lossyWAV from a `fact` chunk in the stored header, high confidence, as for the other formats that keep a WAV header.
- **The lossy part of a DualStream** (`Lossy Stream`): a typed claim (`StreamInfo.Claims`: kind, value, confidence, evidence, read by the JSON report as data; the node shows it) made when the reserved bytes of every whole data block have the low byte 0x01 (0x1101 for stereo, 0x0901 for mono). This is observed behavior of OptimFROG 5.100 in Encspect's test files, where the 8 distinct `.ofs` encodes show it and no lossless file does; the analyzer uses it, the bytes are undocumented, and a lossless file written with preset 0 shares the `.ofs` minimum decoder version of 4.520 (so that version is not used). The confidence is low. The file name is a second signal, applied by the host after the parse because the parsers do not see the name ([architecture.md](../architecture.md)): the pattern and the `.ofs` extension agreeing give medium, with evidence naming both; the pattern under another name, or none (a stream, standard input), stays low. Where every whole data block shows another pattern and the name ends in `.ofs`, no claim is made and `optimfrog.extension-differs` (Info) says what was found; with no whole data block, or a mixture, nothing is said. The extension never drives detection and never makes anything lossless. The claim is not copied into the verification: `IsLossy` stays false, so the verification reports what the block CRC-32s show and that the MD5 of the original audio is not compared, until the 0.4 pairing with the `.ofc` confirms the claim. The codec is named "OptimFROG" (a correction file "OptimFROG correction data"); `IsCorrectionFile` is true for a correction file and false otherwise, as in WavPack, and no `IsCorrectionFileCreated` is reported since the header does not record it.

## Verification

Two checksums are declared; no decoder exists, so the host reports that no decoder handles the coding.

| Checksum | Covers | Counts as covering all of the audio when |
|---|---|---|
| block CRC-32 | the compressed data of a block | the main block's fields were read, every block it calls for is there whole and the samples add up |
| MD5 | the decoded audio | never compared |

A stream is verified when every stored checksum was compared, none failed and the block CRC-32s cover all of its audio. A file with an MD5 block is therefore unknown, since the MD5 needs a decoder. OptimFROG's MD5 of 5.001 and earlier does not follow RFC 1321 for audio of 56 to 63 bytes modulo 64 (format.txt).

## Not covered

- Decoding, so no MD5 comparison, measurement, entropy figures or coding graphs.
- OptimFROG before 4.5xx, whose layout the specification does not cover.
- Self-extracting files.
- The DualStream pair (finding the `.ofc` beside the `.ofs`, by base name and extension, and checking the two together), which waits for 0.4 with WavPack's `.wvc`; an input with no folder reports the pair as unchecked.
- The 4.600ex build with `--speedup`, which was not tried.
- The meaning of `OFRX`, `DETA`, the reserved bytes of a data block and the float sample types beyond their names.
- `RECV` recovery data: shown as a block.
