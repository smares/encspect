# Windows Media Audio versions 1 and 2: format description

**Status:** written on 2026-10-29 by a Claude agent (Opus) that read FFmpeg 6.1.1's LGPL decoder of Windows Media Audio versions 1 and 2 (listed under [Sources](#sources)) and Encspect's current reader of the format, and checked this text against the committed fixtures. It contains no code. Whoever implements the replacement of `src/Encspect.Formats.Wma/Standard*.cs` must not read FFmpeg's `wmadec.c`, `wma.c` or `wma.h`, nor any other FFmpeg source or copy, nor the files [Implementer's sources](#implementers-sources) marks as not safe; FFmpeg serves only as a black-box cross-check.

This is the clean-room description that the architecture rules of `CLAUDE.md` ask for where LGPL code is the only detailed source, written in the form of [truehd-format.md](truehd-format.md). It covers what the analyzer reads today and reports ([asf-wma.md](../formats/asf-wma.md#wma-versions-1-and-2)): the codec data, the superframes and the bit reservoir, and the syntax of every frame down to the end of its coefficients. No audio is rebuilt, so the dequantization, the noise generator, the line spectral pair curve, the transform and the windows are left out, apart from the facts a reader needs to know that it can leave them out.

## Sources

**Read (FFmpeg 6.1.1, the release `scripts/fixtures/ffmpeg_sources.py` pins, archive SHA-256 `8684f4b0…`):**

- `libavcodec/wmadec.c`: the codec data, the superframes, the bit reservoir, the frames and blocks, the exponents and the noise substitution fields.
- `libavcodec/wma.c` and `wma.h`: the derived parameters (block sizes, the width of the bit offset, noise substitution, exponent bands, coefficient ends, high bands, codebook choice), the gain-to-width rule and the run-level coefficient codes.
- `libavcodec/wma_common.c`: the frame length from the sampling rate.
- `libavcodec/wmadata.h`, `wma_freqs.c` and `aactab.c`: only the tables, which `Tables.cs` holds as format data.
- `libavcodec/get_bits.h`: how the bit reader behaves at the end of its buffer. It is not in `FILES` of `ffmpeg_sources.py`; it was taken from the same pinned archive.
- `libavcodec/decode.c`: what happens to the part of a media object the decoder did not consume, and to the rest after an error.
- `libavformat/riffdec.c` and `asfdec_f.c`: which WAVEFORMATEX fields reach the decoder, and the bitrate it falls back on.
- `libavcodec/wmaenc.c`, only for the 'N' padding of ffmpeg's encoder.

**Encspect's current reader** (`WmaStandardParser.cs`, `StandardParameters.cs`, `StandardStream.cs`, `StandardDecoder.cs`, `StandardNodes.cs`, `StandardReport.cs`, `Records.cs`, `Explain.cs`) was read for what it reports: the nodes, statistics, series and findings in [What Encspect reports](#what-encspect-reports), and the places where it departs from FFmpeg on purpose.

**Test files.** The committed fixtures with streams of versions 1 and 2:

| File | SHA-256 | What it is |
| --- | --- | --- |
| `tests/fixtures/asf/external-ffmpeg-wmav1-mono-22k-24k.wma` | `c18efebcbecb5bab6d20e3066622c07291f7da0d6d4cd686e91dcc0648728884` | ffmpeg's wmav1 encoder, 22.05 kHz mono, 24 kbit/s, 44 superframes of 139 bytes; noise substitution on |
| `tests/fixtures/asf/external-ffmpeg-wmav1-stereo-44k-128k.wma` | `e5bdcda634276f41824d696ccb784851ccdfeeff7e8b99324b4b8558cb06dc15` | wmav1, 44.1 kHz stereo, 128 kbit/s, 743-byte superframes |
| `tests/fixtures/asf/external-ffmpeg-wmav1-stereo-44k-48k.wma` | `320c358fe879d92f801f2b647c2ae1b081009aebb046ab53cfd11de9e36de46c` | wmav1, 44.1 kHz stereo, 48 kbit/s |
| `tests/fixtures/asf/external-ffmpeg-wmav2-mono-44k-48k.wma` | `d8debe4b3609ede9c6d3501d41c3a7075daf40031ea09d59d163add722c660ab` | ffmpeg's wmav2 encoder, 44.1 kHz mono, 48 kbit/s |
| `tests/fixtures/asf/external-ffmpeg-wmav2-stereo-22k-32k.wma` | `35a45b944e1f86b2485473f09a2b34a6cf2d8601d908351f8df9d0d27065f66c` | wmav2, 22.05 kHz stereo, 32 kbit/s |
| `tests/fixtures/asf/external-ffmpeg-wmav2-stereo-32k-64k.wma` | `9771b0849a86552a6e65a3ec7cc6d9fbb9eed9f7fb1f5be60499bc9f42076815` | wmav2, 32 kHz stereo, 64 kbit/s |
| `tests/fixtures/asf/external-ffmpeg-wmav2-stereo-44k-128k.wma` | `777178738c9d2040410aee424c179f7197d2fd3eb51a4e6e334de6673b26aa3d` | wmav2, 44.1 kHz stereo, 128 kbit/s |
| `tests/fixtures/asf/external-ffmpeg-wmav2-stereo-44k-32k.wma` | `77cf6918b82651ca4fa8db0f1835f03058150c304415487156085db07db9994b` | wmav2, 44.1 kHz stereo, 32 kbit/s; noise substitution on, codebook pair 0 |
| `tests/fixtures/asf/external-ffmpeg-wmav2-stereo-44k-64k.wma` | `b2a080f602d076a25ad3e7ca173fb43ec614fcded41d2b957b405725d7c140fb` | wmav2, 44.1 kHz stereo, 64 kbit/s |
| `tests/fixtures/asf/external-windows-wma92-*.wma` (15 files) | see their `.provenance.json` | Windows Media Audio 9.2 from the Windows Media Format SDK's writer and Media Foundation (wmvcore 12.0.26100.7309): 22.05, 44.1 and 48 kHz, 20 to 192 kbit/s, constant and variable bitrate; all use the bit reservoir and variable block lengths |
| `tests/fixtures/asf/external-remux-ffmpeg-wma92-cbr-stereo-22k-20-sdk.wma`, `…-22k-32.wma` | `a01669a4…`, `14e463db…` | `ffmpeg -c copy` of two of the Windows files: the same superframes in another container layout |
| `tests/fixtures/asf/derived-ffmpeg-wmav2-tool-name-lavc.asf` | `dde4ce950c1e92344a919e6997b4eb2f0c02cf75a164658d19a1d7de7c80e564` | the wmav2 mono file with an attribute added |
| `tests/fixtures/asf/fragments-in-two-streams.asf`, `writer-stamp-last.asf`, `writer-stamp-not-last.asf` | `a747b88d…`, `0352fa0f…`, `6de1ba09…` | hand-built: media objects of 100 bytes of 0x11 or 0x22 under a configuration with the bit reservoir; they are not real superframes and exercise the damage paths |

The SHA-256 of each Windows file is in its `.provenance.json`; for example `external-windows-wma92-cbr-stereo-44k-128.wma` is `eed295f24b93bd115ece824b3db6a19d21c10733f128a44937f8ebe700a7d903`.

**How the text was checked:**

- The author wrote a throwaway walker from this description (Python, in the session's scratch folder, not committed: it was written by someone who read FFmpeg and must not reach the implementer). It takes the WAVEFORMATEX and the superframes' bytes from each golden report's tree and reads them by the rules below.
- On all 31 streams of versions 1 and 2 in the 30 golden reports above (the protected fixtures excluded, whose frames are not read) it reproduces every superframe's value and frame positions to the bit, the bit reservoir after each superframe, every frame's block count and length, every field node of every block (name, value, byte offset, bit offset and bit length), every statistic and the finding codes and counts.
- Changing one rule in the walker breaks the match: the byte alignment of version 1 (2 streams), the reservoir statistics (17 streams), the width of an escape's run (27 streams). Two rules the fixtures do not pin: the zero-width band rule of the exponents and the noise substitution threshold of 0.61 at 44.1 kHz (no fixture lies between 0.61 and 0.81).
- ffmpeg 6.1.1 as a black box decodes every real fixture without an error, to (frames − 1) × frame length samples, which equals the golden reports' sample counts; on the hand-built fixtures it logs the messages quoted in [Validity rules and damage](#validity-rules-and-damage) at the same superframes.

## Conventions

- Bits are read most significant first, from the first byte of a buffer on. u(n) is an n-bit unsigned field; u(0) reads nothing and is 0.
- floor(log2 x) is the position of the highest set bit, and 0 for x ≤ 0.
- "Single" means IEEE 754 single precision with rounding to nearest even, "double" double precision. Where a formula mixes them, the text says at which step a value is rounded to single.
- trunc(x) is the integer part toward zero; for x ≥ 0, trunc(x + 0.5) rounds halves up.
- r is the sampling rate in Hz, C the channel count, R the bitrate in bit/s, A the block alignment in bytes, V the version (1 for format tag 0x0160, 2 for 0x0161).
- F = 2^f is the frame length in samples per channel; the block of size index k is L_k = F / 2^k samples long.
- The names of nodes the analyzer reports are in quotes ("Last Frame Bit Offset"), as the golden reports have them.

## Terms

- **Superframe:** one ASF media object of the stream, of the block alignment's size. Without the bit reservoir it holds one frame; with it, a header and the bits of several frames.
- **Frame:** F samples of every channel, coded as one or more blocks. With the bit reservoir a frame starts at any bit and may run on into later superframes.
- **Block:** a transform of L_k samples per channel: its sizes, mid/side flag, coded flags, total gain, noise substitution, exponents and coefficients. The blocks of a frame add up to F.
- **Bit reservoir:** the bits of a superframe after the last frame that ends in it, which the next superframe's bit offset finishes. The encode options turn it on.
- **Bit offset:** the superframe header field ("Last Frame Bit Offset") that counts the bits after the header which finish the frame begun earlier.
- **Frame spanning superframes:** a frame that begins in one superframe and ends in a later one ("Frames Spanning Superframes").
- **Superframe inside a frame:** a superframe in which no frame ends, so all of it after its first byte is the middle of a frame ("Superframes Inside a Frame", "Frame Middle").
- **Exponents:** the envelope of a block's spectrum, one value per band, coded with the scale factor codebook or as line spectral pairs.
- **High bands:** the bands between the high-band start and the coefficient end, which noise substitution may replace by a noise level.
- **Filler:** bits between the end of a frame spanning superframes and the bit offset; zero bits are stuffing (`wma.frame-stuffing`), others filler (`wma.frame-filler`).

## Codec data

### WAVEFORMATEX

The ASF Stream Properties Object holds a WAVEFORMATEX (ASF specification, section 11.1.1) with format tag 0x0160 (version 1) or 0x0161 (version 2). What the decoder uses:

| Field | Use |
| --- | --- |
| Channels | C; 1 or 2 |
| Samples per second | r; it sets the frame length, the bands and the thresholds below |
| Average bytes per second | R = 8 × this value; it sets the width of the bit offset, the number of block sizes, noise substitution and the codebooks |
| Block alignment | A, the size of a superframe in bytes |
| Bits per sample | not used |
| Codec specific data size | the size of the codec data below |

- FFmpeg's ASF reader takes R from the Stream Bitrate Properties Object where the WAVEFORMATEX states 0 average bytes per second. Encspect does not: it reports such a stream as not analyzed (below).
- FFmpeg computes R in 64 bits. Encspect caps it at 2^31 − 1 where the field is above 268,435,455, which only a crafted header reaches.

### The codec-specific data

All values little-endian.

| Version | Bytes | Field (node) | Meaning |
| --- | --- | --- | --- |
| 1 | 0–1 | "Unused" | not read by the decoder |
| 1 | 2–3 | "Encode Options" | the options below |
| 2 | 0–3 | "Samples Per Block" | dwSamplesPerBlock (ASF 11.1.1); not read by the decoder |
| 2 | 4–5 | "Encode Options" | wEncodeOptions, the options below |
| 2 | 6–9 | "Super Block Align" | dwSuperBlockAlign; not read by the decoder |

- The options are read only where the data holds them: 4 bytes or more for version 1, 6 or more for version 2. Shorter data gives options of 0.
- The expected size is 4 for version 1 (what ffmpeg's encoder writes; the ASF specification defines no version 1 layout) and 10 for version 2 (section 11.1.1). Bytes after it are shown as "Extra Data".

### Encode options

| Bit | Mask | Node | Meaning |
| --- | --- | --- | --- |
| 0 | 0x0001 | "Uses Exponent VLC" | exponents coded with the scale factor codebook; clear: line spectral pairs |
| 1 | 0x0002 | "Uses Bit Reservoir" | superframes have a header and frames cross superframes |
| 2 | 0x0004 | "Uses Variable Block Length" | frames are divided into blocks of several sizes |
| 3–4 | 0x0018 | "Block Size Count Code" | with variable block lengths, sets how many sizes (below) |
| 5–15 | 0xFFE0 | "Other Options" | not read |

- **Variable block lengths disabled:** for version 2 with 8 or more bytes of codec data and options exactly 0x000D, FFmpeg turns variable block lengths off ("Disabling use_variable_block_len, if this fails contact the ffmpeg developers and send us the file"). The node still shows the raw bit; every derived value below uses the effective setting.
- In the fixtures the options are 0x0001 in all of ffmpeg's files, and 0x000F, 0x001F (44.1 and 48 kHz) or 0x0017 (22.05 kHz) in Microsoft's.

### Configurations that are not decoded

FFmpeg's decoder does not open, and Encspect reports the stream as not analyzed (`wma.format-unsupported`), where:

- the block alignment is 0 ("block_align is not set");
- r is 0 or above 50,000, C is 0 or above 2, or R is 0;
- the bit offset field (below) would be wider than 25 bits ("byte_offset_bits %d is too large"), which needs b (below) of at least 2^24 / F, 8,192 bits per sample and channel at F = 2,048.

## Derived parameters

### Frame length

f = 9 for r ≤ 16,000; else 10 for r ≤ 22,050, and also for version 1 with r ≤ 32,000; else 11. So F is 512, 1,024 or 2,048 ("Frame Length"). Versions 1 and 2 never use the larger lengths of later versions.

### Block sizes

- Without variable block lengths there is one size, F.
- With them: n = ((options >> 3) & 3) + 1; n grows by 2 where R / C (integer division) is 32,000 or more; n is capped at f − 7. The number of sizes is N_s = n + 1, from F down to F / 2^n, at least 128 samples. Every fixture with variable blocks has N_s = 5 at 44.1 and 48 kHz and 4 at 22.05 kHz.
- A block size is sent as an index of w_s = floor(log2(N_s − 1)) + 1 bits: 1 bit for 2 sizes, 2 for 3 or 4, 3 for 5.

### Bits per sample

b = single(R) / single(C × r), the quotient rounded to single. For stereo b' = single(b × 1.6), with 1.6 a double and the product rounded to single; for mono b' = b. The comparisons below compare these single values with double constants.

### Width of the bit offset

- x = single(b × F) / 8, the product rounded to single and the division done in double.
- The bit offset field is w_o = floor(log2(trunc(x + 0.5))) + 2 + 3 bits wide.
- In the fixtures: 13 bits at 44.1 kHz, 128 kbit/s stereo; 11 bits at 32 kbit/s; 10 bits at 22.05 kHz, 20 kbit/s.

### Noise substitution and the high-band frequency

The high-band frequency h starts as single(r × 0.5). For version 2 the rate is first normalized: r ≥ 44,100 counts as 44,100, ≥ 22,050 as 22,050, ≥ 16,000 as 16,000, ≥ 11,025 as 11,025, ≥ 8,000 as 8,000; lower rates stay. Version 1 is not normalized, so only the exact rates below match.

| Rate (normalized) | Rule (h × c means h = single(h × c), c a double) |
| --- | --- |
| 44,100 | b' ≥ 0.61: no noise substitution; else h × 0.4 |
| 22,050 | b' ≥ 1.16: none; else b' ≥ 0.72: h × 0.7; else h × 0.6 |
| 16,000 | b > 0.5: h × 0.5; else h × 0.3 |
| 11,025 | h × 0.7 |
| 8,000 | b ≤ 0.625: h × 0.5; else b > 0.75: none; else h × 0.65 |
| any other | b ≥ 0.8: h × 0.75; else b ≥ 0.6: h × 0.6; else h × 0.5 |

- Noise substitution is on unless a rule turns it off ("Uses Noise Substitution"). The 16,000 and 8,000 rows and the last row compare b, not b'.
- Without noise substitution h stays r / 2, so no high band exists and nothing below depends on h.

### Exponent bands

For each block size k (0 to N_s − 1, L = L_k), a list of band widths in coefficients:

- **Version 2, tables:** where log2(L) − 7 is 0, 1 or 2 (blocks of 128, 256 and 512) and r ≥ 22,050, the list is a row of `ExponentBands44100` (r ≥ 44,100), `ExponentBands32000` (r ≥ 32,000) or `ExponentBands22050` (r ≥ 22,050), indexed by log2(L) − 7. The first value of a row is the number of bands, the widths follow.
- **Version 2, otherwise:** for each frequency a of `CriticalFrequencies` in order, p = floor((2 × L × a + 2 × r) / (4 × r)) × 4, capped at L. Where p exceeds the previous p (0 at the start), the width p − previous is added. The list ends after the first p that reaches L.
- **Version 1:** for each a in order, p = floor((2 × L × a + floor(r / 2)) / r), capped at L. The width p − previous is added even where it is 0, and the list ends after the width whose p reaches L.
- **Version 1 with more than one block size:** FFmpeg keeps one list for version 1 and rewrites it for each size in turn, so the list that remains is that of the smallest block, and the lists of the other sizes are empty.
  - Encspect uses that list for the exponents of blocks of size index 0 and an empty list for the others.
  - The high bands of size 0 are the exception: they are worked out while the list is still the full-size one (see [High bands](#high-bands)).
  - No fixture has version 1 with variable block lengths (see [Not determined](#not-determined)).
- In all of ffmpeg's and Microsoft's fixtures the widths of a list add up to L. For version 2 at 49,048 Hz and above they stop 4 to 40 short of it for the two largest sizes (the critical frequencies end at 24,500 Hz).

### Coefficient range

- The first coded coefficient is s = 3 for version 1 and 0 for version 2.
- The coefficient end of size k is E_k = (F − floor(9 × F / 100)) / 2^k, rounded down: 1,864 for a block of 2,048 in a frame of 2,048, 932 for 1,024 in 1,024.
- A block codes at most E_k − s coefficients.

### High bands

- The high-band start of size k is S_k = trunc(single(single(2 × L_k × h) / single(r)) + 0.5): the product of the integer 2 × L_k and h rounded to single, the division by r rounded to single, and the 0.5 added in double.
- Walking the size's band list from coefficient 0 (for version 1 size 0, the full-size list, not the smaller one kept for the exponents), each band [start, end) is clipped to [S_k, E_k); every band with something left is a high band, its width what is left. H_k is the number of high bands.
- For version 1, sizes other than 0 have no high bands (their band list is empty). With one block size nothing differs.
- Examples: 44.1 kHz stereo at 32 kbit/s (version 2): S_0 = 819, high bands 65, 232, 324 and 424 wide; 22.05 kHz stereo at 20 kbit/s: S_0 = 717, widths 167 and 48, and for blocks of 256 widths 1, 40 and 13.
- A sweep over rates from 4,000 to 50,000 Hz, both versions and bitrates around every threshold found at most 8 high bands; FFmpeg's arrays hold 16.

### Codebooks

- The run-level codebook pair is chosen once per stream: pair 2, except that for r ≥ 32,000 (the actual rate, not normalized) b' < 0.72 gives pair 0 and b' < 1.16 pair 1.
- Pair p is the codebooks 2p and 2p + 1 of `CoefficientCodes`, `CoefficientLengths` and `CoefficientLevels`. The second channel ("Channel 2 Coded") of a block coded as mid/side uses 2p + 1; every other coded channel, and both channels of a block not coded as mid/side, use 2p.
- All codebooks of `Tables.cs` are complete prefix codes (the Kraft sum of each is 1), so every bit string starts with a code; a reader that reads zeros past the end of its data still finds codes.

| Codebook | Tables (`Tables.cs`) | Symbols |
| --- | --- | --- |
| Exponents | `ExponentCodes`, `ExponentLengths` (121 codes, up to 19 bits) | index − 60, a difference |
| Noise level differences | `HighBandGainLengths`, `HighBandGainSymbols` (37 codes, up to 13 bits) | symbol − 18; the codes follow from the lengths in table order: the code of an entry is the sum of 2^−length over the entries before it (not its own), written as a binary fraction of its own length in bits |
| Run-level coefficients | `Coefficient0…5Codes`, `…Lengths`, `…Levels` (666, 555, 1,336, 1,072, 476 and 435 codes, up to 22 bits) | index 0 escape, 1 end of block, then runs and levels from the level table (below) |
| Band frequencies | `CriticalFrequencies` (25 values, 100 to 24,500 Hz) | |
| Fixed band lists | `ExponentBands22050`, `ExponentBands32000`, `ExponentBands44100` | count, then widths |

**Run and level of a code:** the codes from index 2 on are in groups, one per level starting at 1. Entry j of the level table is the number of codes of level j + 1, and within a group the runs are 0, 1, 2 and so on. So the first `CoefficientLevels[0]` codes after the two special ones have level 1 and runs 0 up, the next group level 2, and so on; the table sums to the code count less 2.

### Values in the fixtures

| Stream | F | N_s | w_o | Noise | Pair | E_0 | High bands of size 0 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| wmav1 22.05 kHz mono 24 kbit/s | 1,024 | 1 | 12 | on | 2 | 932 | 165, 50 |
| wmav1 44.1 kHz stereo 128 kbit/s | 2,048 | 1 | 13 | off | 2 | 1,864 | none |
| wmav2 44.1 kHz stereo 32 kbit/s | 2,048 | 1 | 11 | on | 0 | 1,864 | 65, 232, 324, 424 |
| wmav2 22.05 kHz stereo 32 kbit/s | 1,024 | 1 | 11 | off | 2 | 932 | none |
| WMA 9.2 44.1 kHz stereo 128 kbit/s | 2,048 | 5 | 13 | off | 2 | 1,864 | none |
| WMA 9.2 44.1 kHz stereo 32 kbit/s (options 0x001F) | 2,048 | 5 | 11 | on | 0 | 1,864 | 65, 232, 324, 424 |
| WMA 9.2 22.05 kHz stereo 20 kbit/s (options 0x0017) | 1,024 | 4 | 10 | on | 2 | 932 | 167, 48 |

## Superframes

### Media objects

- Each media object of the stream is one superframe. A decoder reads its first A bytes.
- A media object shorter than A is rejected ("Input packet size too small (%d < %d)"). Encspect does not read it and empties the bit reservoir; FFmpeg leaves its reservoir as it was, so the next frame spanning superframes is rebuilt from bytes that do not belong together.
- A media object longer than A: FFmpeg reads A bytes as a superframe and feeds the rest in again as another packet (`decode.c`), which is then usually too short. Encspect reads the first A bytes only.
- On any error FFmpeg drops the rest of the media object; since a media object is one superframe, nothing else is lost.

### Without the bit reservoir

A superframe is one frame, read from bit 0. The bits after it to the end of the superframe are padding, which nothing reads. ffmpeg's encoder fills them with 'N' bytes (0x4E) after the byte the frame ends in.

### With the bit reservoir: the header

| Bits | Field (node) | Meaning |
| --- | --- | --- |
| 4 | superframe index ("Superframe Index") | not read by the decoder; in every real fixture it counts 0, 1, 2 … from the first superframe (the hand-built ones begin with 0x11 or 0x22 and so show 1 or 2) |
| 4 | frame count ("Frames in Superframe") | the frames that begin in this superframe, the last one counted where it goes on in the next |
| w_o | bit offset ("Last Frame Bit Offset") | present unless the superframe continues a frame (below): how many bits after the header finish the frame begun earlier |

The header is 8 + w_o bits (21 at 44.1 kHz, 128 kbit/s stereo).

### Frames a superframe holds

Let m be the frame count, and "a frame is in progress" mean that the reservoir holds bytes (it is empty at the start of the stream and after an error).

1. **Frames to decode:** d = m − 1 where no frame is in progress, else d = m.
2. **d < 0** (count 0 and nothing in progress), or **d = 0 with 8 or fewer bits left after the 8 header bits** (a block alignment of 2 bytes or less): the header is invalid ("nb_frames is %d bits left %d", logged as an error). Encspect reports `wma.superframe-header` and does not read the superframe. Neither empties the reservoir.
3. **d = 0 otherwise** (count 0 with a frame in progress, or count 1 with none): a superframe inside a frame.
   - There is no bit offset. All A − 1 bytes after the first byte are appended to the reservoir, and nothing is decoded ("nb_frames is 0 bits left %d", logged as a warning).
   - Encspect appends them, and so continues the frame in progress (see [FFmpeg's implementation choices](#ffmpegs-implementation-choices) for how FFmpeg differs).
   - Where the count is 1 and no frame was in progress, the bytes appended start with what would have been the bit offset, and the frame read from the reservoir later starts there (start bit 0); the hand-built fixtures have this case.
   - FFmpeg instead skips, at the start of that frame, the number of bits it last computed for the end of some earlier superframe's frames (0 at the start of the stream, and never cleared by an error), so it reads a stale value there.
4. **Otherwise** the bit offset is read. Where it is larger than the bits left in the superframe after the header, the superframe is rejected ("Invalid last frame bit offset %d > buf size %d (%d)") and the reservoir emptied (`wma.bit-offset`).
5. **The frame spanning superframes:** where a frame is in progress, the first bit-offset bits after the header are appended to the reservoir, and the frame is read from the reservoir, starting at the reservoir's start bit (below). It counts as one of the d frames. A frame that fails here empties the reservoir and ends the superframe.
6. **The frames of this superframe:** the remaining frames (d − 1 after a frame spanning superframes, else d) are read one after another, the first from the bit right after the header and the bit offset's bits, that is bit 8 + w_o + bit offset of the superframe (bit 8 + w_o when no frame was in progress and the offset is 0). Before the first of them the block sizes are reset (see [Block sizes and their state](#block-sizes-and-their-state)). A frame that fails empties the reservoir and ends the superframe.
7. **The new reservoir:** let e be the bit of the superframe where the last frame of step 6 ends, or, where step 6 reads no frame, 8 + w_o + bit offset. The reservoir becomes bytes floor(e / 8) to A − 1, and its start bit is e mod 8. Where that is more than 32,768 bytes the superframe is rejected ("len %d invalid") and the reservoir emptied (`wma.reservoir-length`). The bits from e on are the start of the next frame, or, at the end of the stream, bits no frame uses.

Consequences:

- In the first superframe of a stream, and in the one after an error, no frame is in progress: the bits before the bit offset belong to a frame that is lost, and one frame fewer than the count is decoded. Every real fixture's first superframe states a bit offset of 0 (the hand-built 0x22 stream states 1,092).
- A frame that ends exactly at the end of a superframe leaves a reservoir of 0 bytes, so the next superframe treats no frame as in progress.
- The bits of the frame spanning superframes are the reservoir from its start bit on, and the bit-offset bits. It must end within them. Where it ends before their end, the bits between its end and the bit offset are skipped: zero bits are stuffing, other bits filler (`wma.frame-stuffing`, `wma.frame-filler`, info). The Windows fixtures have both.
- The frame count is 4 bits, so at most 15 frames begin in one superframe (the quality 90 fixture's first superframe states 15).

### The reservoir across superframes inside a frame

- A frame may span any number of superframes inside a frame: its bits are the tail of the superframe it began in, then A − 1 bytes of each superframe inside a frame after its first byte, then the bit-offset bits of the superframe where it ends.
- Encspect keeps the reservoir up to 65,536 bytes; appending past that empties it silently (no finding), and the superframe leaves no reservoir. FFmpeg rejects a superframe inside a frame that would take its reservoir past 32,768 of its units (see below) and empties it.
- In the fixtures two of Microsoft's files end with a superframe inside a frame (quality 25 and quality 90): their last frame never ends.

### The end of the stream

- The reservoir after the last superframe holds bits that no frame uses: a frame that was begun and never finished. A decoder drops them; Encspect reports their number (`wma.reservoir-at-end`, info): 8 × reservoir bytes − start bit.
- In every Microsoft fixture these bits are not all zero.

## Frames and blocks

### A frame

A frame is a run of blocks whose lengths add up to F. Each block is read as below, the frame position advancing by the block's length; the frame ends after the block that brings it to F. A block whose length would take the frame past F makes it invalid ("frame_len overflow").

### Block sizes and their state

The reader keeps three block sizes across blocks, frames and superframes: the previous, the current and the next, and a reset flag.

- The reset flag is set at the start of the stream and at step 6 of every superframe that gets there, also where no frame follows; the first block read after that clears it. So a frame spanning superframes goes on with the sizes the last block before it left, unless no block was read since the flag was last set: a frame that begins right after the bit offset of a superframe whose only frame was a straddling one starts with a reset. Without the bit reservoir the flag is set only at the start of the stream.
- **With variable block lengths**, each block starts with:
  - where the reset flag is set: an index for the previous size and an index for the current size, each w_s bits ("Previous Block Size", "Block Size"); the flag is then clear;
  - otherwise the previous size becomes the current one and the current size becomes the next one;
  - then always an index for the next size, w_s bits ("Next Block Size").
- An index v gives the size F / 2^v; an index of N_s or more makes the frame invalid ("prev_block_len_bits %d out of range", "block_len_bits %d out of range", "next_block_len_bits %d out of range").
- **What a failed index leaves:** the reset flag is cleared before the first index is read. A failed first index changes nothing else; a failed second index has set the previous size; a failed next index has set the previous and current sizes but not the next.
- **Without variable block lengths** nothing is read and all three sizes are F.
- FFmpeg also rejects a current size outside the stream's sizes ("block_len_bits not initialized to a valid value"), which the rules above never produce.

### Block layout

| Order | Field (node) | Bits | Present |
| --- | --- | --- | --- |
| 1 | block size indexes ("Previous Block Size", "Block Size", "Next Block Size") | w_s each | variable block lengths (above) |
| 2 | mid/side ("Uses Mid/Side Stereo") | 1 | C = 2, in every block |
| 3 | coded flag of each channel ("Channel 1 Coded", "Channel 2 Coded") | 1 each | always |
| 4 | total gain ("Total Gain") | 7 per step | some channel coded |
| 5 | noise flags ("Noise Substitution") | H_k per coded channel | noise substitution on and some channel coded |
| 6 | noise levels ("Noise Levels") | 7, then codes | a noise flag set |
| 7 | exponents sent ("Exponents Sent") | 1 | some channel coded and L_k < F |
| 8 | exponents of each coded channel ("Exponents (channel n)") | codes or 37 | some channel coded, and L_k = F or the flag set |
| 9 | coefficients of each coded channel ("Coefficients (channel n)") | codes | some channel coded |
| 10 | alignment to the next byte | 0 to 7 | version 1, C = 2: after each channel's coefficients, coded or not |

Where no channel is coded, the block ends after its coded flags: no gain, no exponents and no alignment.

### Total gain

- The gain is 1 plus a sum of 7-bit values: values are read while each is 127, and the first value below 127 ends the sum. So 0 to 126 gives 1 to 127, and 127, 5 gives 133.
- Before each 7-bit value at least 7 bits must be left in the frame's buffer; otherwise the frame is invalid ("total_gain overread").
- The gain sets g, the width of an escaped level: 13 for a gain below 15, 12 below 32, 11 below 40, 10 below 45, 9 from 45 on.

### Noise substitution

Only where the stream uses noise substitution (H_k may be 0, and then nothing is read):

1. **Flags:** for each coded channel in channel order, one bit for each of the H_k high bands in band order. A set bit replaces the band by noise: its width is taken off the coefficients the channel codes.
2. **Levels:** then, for each coded channel in channel order, a level for each flagged band in band order: the first band's is u(7) (the level less 19), each later one a code of the noise level difference codebook, added to the one before.

The levels are not reported, only how many there are; the flags' node says "n of m high bands", m being H_k times the coded channels.

### Exponents

**Exponents sent:** a block as long as the frame always sends them. A shorter block reads a flag: 1, it sends them; 0, every coded channel reuses the exponents it last received, which may come from a block of another size.

Where they are sent, each coded channel in channel order sends its exponents in one of two ways, by bit 0 of the options:

- **Line spectral pairs:** ten indexes of 3, 4, 4, 4, 4, 4, 4, 4, 3 and 3 bits, 37 bits in all. Their values only shape the envelope and are not checked.
- **Scale factor codes:** the band list of the block's size is walked until the bands cover L_k coefficients:
  - version 2 starts from a value of 36 and reads a code for each band, starting with the first;
  - version 1 starts from u(5) + 10, which is the value of the first band, and reads a code for each band from the second on;
  - each code is a difference added to the value; a value outside −60 to 95 makes the frame invalid ("Exponent out of range: %d");
  - a band covers its width; a width of 0, and every band past the end of the list, covers 4 coefficients;
  - the walk stops as soon as the bands cover L_k or more, so the number of codes is the number of bands needed to reach L_k (one fewer for version 1).

**Exponents missing:** after the exponents (sent or not), a coded channel that has never received exponents since the start of the stream makes the frame invalid. A channel has received them once a block sent them for it without error; this is never undone, not even by an error or an emptied reservoir.

### Coefficients

For each coded channel in channel order, with n = E_k − s less the widths of the channel's flagged high bands, and the channel's codebook (above):

1. A position p starts at 0. While p < n, a code is read:
   - **index 2 or more:** p grows by the code's run; a sign bit follows; the coefficient is at p (its level is the code's level); then p grows by 1;
   - **index 1:** the end of the block; the channel's coefficients end here;
   - **index 0, an escape:** the level is u(g); then the run is u(f) bits (the frame length's exponent f, not the block's) and p grows by it; a sign bit follows; the coefficient is at p; then p grows by 1.
2. After the loop, p > n makes the frame invalid ("overflow (%d > %d) in spectral RLE, ignoring", which despite its words stops the frame). That is, a coefficient at position n or beyond is invalid, and one at n − 1 is the last that fits.
3. The end-of-block code may be left out where the coefficients reach n.
4. For version 1 stereo, the reader then moves to the next byte boundary of the frame's buffer, also after a channel that is not coded.

Reported per channel: the non-zero coefficients (every run-level code and every escape, whatever its level), the escapes, and the position of the last coefficient, p mod L_k ("n non-zero, last at p").

The sign and level are read and not used further. Positions are relative to s, so version 1's position 0 is coefficient 3.

### Where a frame ends

- A frame ends after its last block's last field, or alignment for version 1 stereo; there is no length field, no checksum and no trailer. Only reading the whole syntax finds the end.
- What confirms a walk: without the bit reservoir, a frame must end within its superframe; with it, the frames of a superframe must end within it, the last leaving a reservoir that the next superframe's bit offset finishes, and a frame spanning superframes must end within the reservoir and the bit-offset bits.
- In the fixtures every frame ends within its bits; ffmpeg's superframes have only 'N' bytes after their frame's byte, and no frame spanning superframes needs more bits than its bit offset gives.

### Reading past the end of the data

- FFmpeg's reader yields zero bits past the end of its buffer (the packet's padding, or the zeros after its reservoir copy; in a media object longer than A, the bytes after the first A) and stops advancing 8 bits past the end.
  - It checks the end only before a total gain value and, for the frames read from a bit offset, when it works out the new reservoir.
  - A last frame that ends up to 7 bits past the superframe leaves a reservoir of 0 bytes and is accepted; one that ends 8 or more past it gives a negative length and is rejected ("len %d invalid").
  - It does not check the frame spanning superframes or the frames of streams without the bit reservoir at all.
- Encspect reads zero bits past the end too, and a frame that needs more bits than it has is invalid ("the frame needs more bits than it has"). It checks after every exponent code, after every coefficient code (stopping the channel at once), after each block and at the end of the frame. Where the coefficient loop stopped for this reason, a position past n is reported as the overflow, else as running out of bits.

## State and resets

| State | Kept across | Reset |
| --- | --- | --- |
| previous, current and next block size | blocks, frames, superframes | sent again after the reset flag; never reset by an error |
| reset flag | blocks, frames, superframes | set at the start and at step 6 of every superframe that reaches it, cleared before the first index read after that |
| exponents received, per channel | the whole stream | never |
| reservoir bytes and start bit | superframes | emptied by an error, a short media object (Encspect) or a rejected header field; kept by an invalid header (step 2) |
| bands, high bands, codebook pair, w_o, N_s | the whole stream | fixed by the codec data |

## Limits

| Limit | Value |
| --- | --- |
| Channels | 1 or 2 |
| Sampling rate | 1 to 50,000 Hz (FFmpeg) |
| Frame length F | 512, 1,024 or 2,048 |
| Block sizes | 1 to 5, from F down to at least 128 |
| Frames beginning in a superframe | 0 to 15 |
| Bit offset | w_o bits, at most 25 (FFmpeg) |
| Reservoir after a superframe | at most 32,768 bytes (FFmpeg) |
| Total gain | 1 and up, unbounded; 127 per step |
| Exponent values | −60 to 95 |
| High bands | H_k ≤ 8 in practice, 16 in FFmpeg's arrays |
| Coefficients per block and channel | E_k − s, at most 1,864 |
| Escaped level | 9 to 13 bits |
| Escaped run | f bits, 9 to 11 |

Encspect's own limits: 262,144 superframes are read and the rest counted (`wma.records-limit`); the reservoir is kept up to 65,536 bytes. `MemoryTests` holds an ASF stream of version 2 to under 550 bytes kept per superframe (about 273 measured).

## Validity rules and damage

What FFmpeg 6.1.1 does with each broken rule and what Encspect reports today. "Rejected" means FFmpeg returns an error for the superframe: its frames produce no output, even those decoded before the error.

| Rule broken | FFmpeg (log message) | Reservoir in FFmpeg | Encspect |
| --- | --- | --- | --- |
| Block alignment 0; rate, channels or bitrate out of range; bit offset wider than 25 bits | decoder does not open | | `wma.format-unsupported` (warning), stream not analyzed |
| Media object shorter than A | rejected ("Input packet size too small") | kept | `wma.superframe-short` (warning), not read, reservoir emptied |
| Media object longer than A | first A bytes; the rest fed again | | `wma.superframe-long` (info), first A bytes only |
| Frame count 0 with nothing in progress, or no room for the middle of a frame | rejected ("nb_frames is %d bits left %d") | kept | `wma.superframe-header` (warning), not read |
| Bit offset past the superframe's end | rejected ("Invalid last frame bit offset") | emptied | `wma.bit-offset` (warning) |
| Frames starting at byte 32,768 or later (A above 32,768) | rejected (no message) | kept | no limit; its own check for frames past the end (`wma.bit-offset`) cannot trigger once the bit offset fits |
| New reservoir negative or above 32,768 bytes | rejected ("len %d invalid") | emptied | `wma.reservoir-length` (warning) |
| Reservoir plus bit-offset bits above 32,768 | rejected | emptied | not checked |
| Block size index out of range | frame fails ("…_len_bits %d out of range"), rejected | emptied | `wma.frame-invalid` (warning) |
| Blocks past the frame length | "frame_len overflow", rejected | emptied | `wma.frame-invalid` |
| Fewer than 7 bits before a gain value | "total_gain overread", rejected | emptied | `wma.frame-invalid` |
| Exponent value outside −60 to 95 | "Exponent out of range", rejected | emptied | `wma.frame-invalid` |
| Coded channel without exponents ever received | rejected (no message) | emptied | `wma.frame-invalid` |
| Coefficient at or past n | "overflow (%d > %d) in spectral RLE, ignoring", rejected | emptied | `wma.frame-invalid` |
| Frame reads past its data | not detected, except as above | | `wma.frame-invalid` |
| Bits after a frame spanning superframes up to the bit offset | skipped | | `wma.frame-stuffing` or `wma.frame-filler` (info) |
| Bits left in the reservoir at the end | dropped | | `wma.reservoir-at-end` (info) |

- A frame that fails ends its superframe in both: the frames after it in the superframe are not read, and the next superframe starts with no frame in progress.
- On the hand-built fixtures FFmpeg logs, in order: "nb_frames is 0 bits left 792" for the first superframe of 0x11 bytes (inside a frame), "overflow (117 > 116) in spectral RLE, ignoring" for the frame spanning superframes in the second, and for the 0x22 stream "Invalid last frame bit offset 1092 > buf size 779 (100)". Encspect's goldens have a superframe inside a frame, `wma.frame-invalid` and `wma.bit-offset` at the same superframes.

## Samples and duration

- FFmpeg outputs F samples per channel for every frame of a superframe that decodes without error, then one frame of F at the end of the stream (the transform's overlap), and drops 2 × F at the start. A stream of n such frames outputs (n − 1) × F samples, and 0 for none.
- Encspect counts the frames read without error, also those before a failed frame in the same superframe, which FFmpeg drops: "Samples" is max(0, valid frames − 1) × F, and "Duration" is that over r. On every real fixture this equals FFmpeg's output (`WmaFixtureTests` holds it to ffprobe's decoded samples); on a damaged stream the two can differ.

## What Encspect reports

This section describes the current reader's output, which the golden reports hold; none of it comes from FFmpeg.

### Stream

- "Statistics" has the kind Statistics. The stream node, "Statistics" and each of its children, "Superframes" and "Frames" (the two list nodes) all cover the same bytes, the container's node for the stream; only the superframe, frame and block nodes below cover their own bytes.
- A stream node named "Windows Media Audio 1" or "Windows Media Audio 2", kind Stream, covering the container's node for the stream, with the value "n frames" (the valid frames).
- Its children, in order: the "Encoder" node where a trait names one (below), "Statistics" (only where at least one frame was read), "Superframes" ("n superframes") and "Frames" ("n frames"), the last two built lazily, a child per superframe and per frame.
- `StreamInfo`: the codec name, r and C from the WAVEFORMATEX, "Mono" or "Stereo" with the matching layout, the samples and duration above, the average bitrate (below), IsVbr from the container, the encoder and writer and the series.
- Encrypted media objects (`AreSamplesEncrypted`) take precedence over an unsupported configuration: no `wma.format-unsupported` is reported for them.
  - They give the codec data nodes and a stream node (kind Stream, over the container's node, topic `wma-standard`) with the value "frames not read: encrypted".
  - Its `StreamInfo` has the container's duration, samples of round(duration × r) and an average bitrate of 8 × average bytes per second / 1000 where that is above 0.
- A configuration that is not decoded: `wma.format-unsupported` and the same stream node with the value "not analyzed", 0 samples and no bitrate.
- The Professional parser and the header-only parsers of Lossless and Voice build their streams with the same helper of `WmaStandardParser.cs` (with their own description and topic), so the replacement keeps it, with the signature given under [Implementer's sources](#implementers-sources).
- A stream handed over as one payload instead of media objects gives `wma.payload-not-analyzed` (info), and one without a WAVEFORMATEX `wma.no-format` (info).

### Codec data nodes

Under the container's codec data node: the fields of [The codec-specific data](#the-codec-specific-data), each over its bytes; the options' bits as five sub-nodes with bit positions counted from the least significant bit of the 16-bit word ("Other Options" is the value shifted right by 5). Findings, both reported at once:

- `wma.codec-data-size` where the size is not 4 (version 1) or 10 (version 2): a warning where the data is too short to hold the options, info otherwise.
- `wma.variable-blocks-disabled` (info) for the 0x000D rule.

### Superframe nodes

A "Superframe" node (kind Frame) over the media object's first A bytes (or its ranges where it spans packets). Its value: "not read" (short object or invalid header), "middle of a frame" (inside a frame) or "n frames", n being the frames read in it, the straddling one and a failed one included. Its children, each over its bits:

| Node | Bits | When |
| --- | --- | --- |
| "Superframe Index", "Frames in Superframe" | 0–3, 4–7 | with the bit reservoir and a full-size object |
| "Last Frame Bit Offset" | 8 to 8 + w_o | where the bit offset was read |
| "Frame End" (value: the bit offset) | 8 + w_o for the bit offset's length | bit offset above 0 that fits in the superframe, even where no frame was in progress |
| "Frame Middle" (value: 8 × (A − 1) bits) | 8 to the end | superframe inside a frame |
| "Frame Data" ("n blocks", or "invalid") | each frame that begins in the superframe, from its first to its last bit | the frames read here, the one spanning superframes excluded |
| "Bit Reservoir" (value: its bytes) | from the byte the last frame ends in to the end | with the bit reservoir, a reservoir above 0 bytes left by step 7 (not for a superframe inside a frame) |
| "Padding" (value: bytes) | from the byte after the frame's last byte to the end | without the bit reservoir, where bytes are left |

### Frame nodes

- A "Frame" node (kind Frame) per frame in reading order, the ones spanning superframes included, with the value "n blocks, m bits" or "invalid: reason". It covers the bytes of its bits; a frame across superframes has ranges in each.
- **A frame's bits** are counted in its buffer, the concatenation of its parts. A frame read in one superframe has one part, the whole superframe. A frame spanning superframes has: the tail of the superframe it began in (from byte floor(e / 8), so its start bit is e mod 8), the A − 1 bytes after the first of each superframe inside a frame, and the bit offset's bits after the header of the superframe it ends in. Its length is end bit − start bit in that buffer.
- A child "Block" per block, with the value "L samples", followed by ", mid/side", ", not coded" (no channel coded) and ", invalid" (the block a failed frame stopped in) where they apply. A block that failed before its size was known shows "1 sample" and counts under no size in "Block Sizes"; a block size field whose index was out of range shows "1 sample" too, and so do the sizes the failed block had not yet read.
- The block's fields as in [Block layout](#block-layout), each over its bits. A field is shown only where its first bit lies inside the block as far as it was read, so a failed block shows the fields up to where it stopped.
- The values:
  - block sizes in samples; mid/side and coded flags as booleans; the total gain;
  - the noise flags as "n of m high bands" (where H_k is 0 the node is still shown, with the length 0 and the value "0 of 0 high bands"; no fixture has it);
  - the noise levels as their count in bands, only where there is one;
  - "Exponents Sent" only where the flag was read;
  - the exponents as "scale factor codes" or "line spectral pairs";
  - the coefficients as "n non-zero, last at p".
- The exponents' node of a channel runs from the end of the field before to the end of its own exponents; the coefficients' node likewise, so for version 1 stereo it includes the alignment bits after the channel. The alignment bits after an uncoded channel belong to no node: the next node starts after them. A failed channel's exponents or coefficients get no node.

The reason texts of an invalid frame, by the rule it broke:

- "a block size index outside the stream's block sizes";
- "its blocks add up to more samples than a frame holds";
- "the total gain runs past the data";
- "an exponent is outside the range of the table of powers";
- "a block codes a channel but reuses exponents it never received";
- "coefficients run past the end of the block";
- "the frame needs more bits than it has (FFmpeg accepts a frame read from a superframe's bit offset that ends up to 7 bits past the superframe, and does not check the other frames; this analysis reports any)".

Since every codebook is complete, "bits that are no code of the coefficient codebook" never occurs.

### Statistics

All under "Statistics", whose value is "n frames, d.ddd s" (all frames, the duration to three decimals). In this order, those in brackets only where they apply. With the bit reservoir the four statistics "Frames per Superframe", "Frames Spanning Superframes", "Largest Bit Reservoir" and "Average Bit Reservoir" are always shown, with 0 where nothing counts; the filler pair and "Superframes Inside a Frame" need a case of their own:

| Node | Value |
| --- | --- |
| "Superframes" | media objects read (short ones included) |
| "Frames" | frames read, valid or not |
| ["Invalid Frames"] | frames that failed, where any |
| "Frame Length" | F |
| "Samples", "Duration" | see [Samples and duration](#samples-and-duration) |
| "Block Alignment" | A |
| ["Average Bitrate"] | 8 × A × (superframes not short) / duration / 1000, where the duration is above 0 |
| ["Smallest Frame", "Largest Frame"] | the bits of the shortest and longest valid frame / 8, in bytes |
| ["Minimum Frame Bitrate", "Maximum Frame Bitrate"] | those bits × r / F / 1000 |
| "Uses Bit Reservoir" | bit 1 of the options |
| ["Frames per Superframe"] | with the bit reservoir: the most frames read in one superframe |
| ["Frames Spanning Superframes"] | with the bit reservoir: frames spanning superframes read |
| ["Largest Bit Reservoir"] | with the bit reservoir: the largest reservoir after a superframe, superframes inside a frame included |
| ["Average Bit Reservoir"] | with the bit reservoir: the sum of the reservoirs left by step 7 (superframes inside a frame not counted) / superframes |
| ["Frames Followed by Filler", "Filler Bits"] | only where at least one frame has stuffing or filler: frames spanning superframes with stuffing or filler, and those bits |
| ["Superframes Inside a Frame"] | only where there is one: superframes inside a frame |
| "Exponent Coding" | "scale factor codes" or "line spectral pairs" |
| "Uses Variable Block Length" | the effective setting |
| "Uses Noise Substitution" | the derived setting |
| "Blocks" | every block of every frame, failed ones included |
| "Block Sizes" | the number of sizes seen, with a child per size seen, from the largest down ("2048 samples"): "n blocks (x.x%)" of all blocks |
| ["Mid/Side Blocks"] | stereo, where a block has a known size: blocks with the flag set, as a share of those blocks |
| ["Noise-Substituted Blocks"] | with noise substitution: blocks with a coded channel and a flagged band, as a share of all blocks |
| "Blocks without Coded Channels" | blocks with no coded flag set, failed blocks that stopped before their flags included |
| "Blocks Reusing Exponents" | blocks with a coded channel that did not send exponents, those that stopped before the decision included |
| ["Smallest Total Gain", "Largest Total Gain"] | only where a block has a coded channel: over those blocks (0 for one that stopped before its gain) |
| "Non-Zero Coefficients", "Escape-Coded Coefficients" | over blocks with a coded channel |

Shares are rounded to one decimal, halves to even, applied to the double count × 100 / total. The averages and bitrates are doubles as computed, unrounded. Units, singular units and display precision of every node ("samples", "bytes", "kbps", "bits", a duration of 3 decimals and bitrates and averages of 1) are as the golden reports show them.

### Series

Where at least one frame was read and r is above 0:

- "Bitrate" (kbps, by file offset): each frame's first byte in the file and its bits × r / F / 1000, 0 for an invalid frame.
- "Bitrate Over Time" (kbps, by second, attached to "Statistics"): frame i falls in second floor(i × F / r), i counting all frames; each second holds the valid frames' bits × r / (their count × F) / 1000, rounded to one decimal with halves to even, 0 for a second with none; the list ends at the last second with a valid frame.
- "Bit Reservoir" (bytes, by file offset), with the bit reservoir only: each superframe's offset and the reservoir it leaves: 0 where it failed or was short, the reservoir kept for an invalid header, the accumulated bytes for a superframe inside a frame.

### Findings

Each code of the stream's superframes and frames is reported once per stream with a count, at the node of its first case, and its message is built for the count; the first case also fixes the parts of the message that vary (which of two texts, the offsets). The findings reported at once (codec data, configuration, record limit, reservoir at the end) come first, then the counted ones in the order they first occurred, then the encoder detection's. That is the order in which the analyzer collects them; the host sorts the report's findings by the offset of their node, and this order only settles ties.

Messages, with the alternatives for a count of 1 and for more in braces ({singular|plural}, N the count), A the block alignment and OFFSET a file offset:

| Code | Severity | Node | Message |
| --- | --- | --- | --- |
| `wma.superframe-short` | warning | the superframe | "{A media object holds\|N media objects hold} fewer bytes than the block alignment of A, which a decoder rejects (wmadec.c); the first is at 0xOFFSET." |
| `wma.superframe-long` | info | the superframe | "{A media object holds\|N media objects hold} more bytes than the block alignment of A; a decoder takes A bytes as a superframe and feeds the rest in again (FFmpeg's decode.c), which this analysis does not: it reads the first A only." |
| `wma.superframe-header` | warning | the superframe | "{A superframe states\|N superframes state} no frame ending in {it\|them}, or fewer than none, with no room for the middle of a frame; a decoder rejects {it\|them}. The first is at 0xOFFSET." |
| `wma.bit-offset` | warning | the superframe | "{A superframe gives\|N superframes give} a last frame bit offset past {its\|their} end; a decoder rejects {it\|them}. The first is at 0xOFFSET." For frames that start past the end (not reachable): "{A superframe's frames start\|N superframes' frames start} past {its\|their} end; a decoder rejects {it\|them}." |
| `wma.frame-stuffing`, `wma.frame-filler` | info | the frame | "{A frame that began in the superframe before is\|N frames that began in the superframe before are} followed by {zero bits\|bits that are not zero} up to the bit offset the superframe states, which a decoder skips; the first has B (in the superframe at 0xOFFSET)." B is the first case's bits; the middle braces are by code, not count |
| `wma.reservoir-length` | warning | the superframe | "{The last frame of a superframe ends\|The last frame of N superframes ends} {past its end\|leaving more than 32,768 bytes}; a decoder rejects {it\|them}." (the second braces by the first case; the first alternative cannot occur in Encspect, whose frame that needs bits past the superframe is invalid) |
| `wma.frame-invalid` | warning | the frame | "{A frame is\|N frames are} invalid: REASON; a decoder drops the superframe and its bit reservoir. The first is at 0xOFFSET." |
| `wma.reservoir-at-end` | info | the stream's "Frames" node | "The last superframe ends with B bits after its last frame that no frame uses, since no superframe follows to finish a frame begun in them; a decoder drops them." ("1 bit" for one), only where a valid frame exists |
| `wma.records-limit` | info | the container's node for the stream | "The stream has more than 262144 media objects; the N after them are not read, so the statistics and the series cover the first 262144." |
| `wma.codec-data-size` | warning or info | the codec data's holder | "The codec-specific data of Windows Media Audio V is B bytes; {FFmpeg's encoder writes 4 (wmaenc.c), the specification defines no version 1 layout\|the ASF specification, section 11.1.1, has 10}, and FFmpeg's decoder reads the options from {4\|6} bytes on[, so it takes none here]." (braces by version; the bracket where the data is too short, which is also when the severity is warning) |
| `wma.variable-blocks-disabled` | info | "Encode Options" | "The encode options are 0x000D with variable block length. FFmpeg's decoder disables variable block length for them (wmadec.c, its issue 1503), so frames are read with one block size." |
| `wma.format-unsupported` | warning | the codec data's holder | "The stream is not analyzed: REASON." with a reason that names the rule broken and the stream's values (no golden report holds one; `WmaFindingTests` asks for the code) |
| `wma.payload-not-analyzed` | info | the parent node | "Windows Media Audio handed over as one payload is not analyzed; only the media objects of a container are." |
| `wma.no-format` | info | the parent node | "The container states no WAVEFORMATEX for the stream, so its Windows Media Audio is not analyzed." |

Numbers in messages have no thousands separators except the fixed "32,768" and "50,000"; offsets are hexadecimal in upper case.

- OFFSET is the superframe's file offset in hexadecimal, upper case. For `wma.frame-invalid` it is the file offset of the superframe the frame was read in plus the frame's start bit / 8, so for a frame spanning superframes it points into the superframe where the frame ends, not where it begins (the hand-built fixtures show 0x612 for a frame that begins at 0x1DA).
- The stuffing and filler message names the superframe whose bit offset ends the frame; the node is the frame itself, which begins in the superframe before.

### Encoder traits

The stream adds two traits to the shared encoder detection (`WmaEncoder`):

- **FFmpeg (wmavN encoder)**, family FFmpeg:
  - It applies without the bit reservoir (bit 1) and without variable block lengths (effective). A superframe whose frame was read without error and ends before its last byte is padded.
  - Where there is at least one padded superframe and every padded one has only 'N' bytes from the byte after the frame's last byte to its end, the trait names "FFmpeg (wmav1 encoder)" or "FFmpeg (wmav2 encoder)" by the version: high confidence with two or more padded superframes, medium with one.
  - Its evidence text: "All N padded superframes have bytes after the frame that are all 'N' (0x4E), which ffmpeg's wmavV encoder writes (wmaenc.c), with no bit reservoir and no variable block lengths", or with one, "The superframe has bytes after the frame …" and the same ending.
- **Microsoft's Windows Media Audio encoder**, family Microsoft, medium: version 2, codec data of at least 10 bytes, bit 1 (the bit reservoir) and effective variable block lengths, and "Samples Per Block" equal to 17 × F. Its evidence text: "The codec data states S samples per block (17 frames) with the bit reservoir and variable block lengths, as the encoders of the Windows Media Format SDK and of Media Foundation write them", S without separators ("34816").
- The traits go to the shared detection with the codec data's trait first; it adds the "Encoder" and "Writer" nodes and their findings.

## Worked example

The first superframes of `external-windows-wma92-cbr-stereo-44k-128.wma`: codec data `00 88 00 00 0F 00 00 00 00 00` (34,816 samples per block, options 0x000F, super block align 0), 16,002 bytes per second, A = 5,945. So F = 2,048, N_s = 5 (code 1 gives 2, plus 2), w_s = 3, w_o = 13, header 21 bits, no noise substitution, codebook pair 2. Bit positions are in each superframe, or in the frame's buffer for a frame spanning superframes.

| Superframe (offset) | Index | Count | Bit offset | Frames read (bits) | Reservoir left |
| --- | --- | --- | --- | --- | --- |
| 0 (1,719) | 0 | 7 | 0 | 6 frames: 21–192 (1 block of 2,048), 192–5,346 (8 blocks: 512, 256, 128, 128, 128, 128, 256, 512), 5,346–7,805, 7,805–9,853, 9,853–17,347 (1,024, 512, 256, 128, 128), 17,347–44,414 (128, 128, 256, 512, 1,024) | 394 bytes from byte 5,551, start bit 6 |
| 1 (7,693) | 1 | 8 | 1,395 | frame spanning superframes 6–4,547 of its buffer (2 blocks of 1,024; 3,152 bits of reservoir and 1,395 of this superframe); then 7 frames from bit 1,416 to 40,743 | 853 bytes |
| 2 (13,667) | 2 | 10 | 3,801 | frame spanning superframes 7–10,625, which ends 3,801 bits into this superframe; then 9 frames from bit 3,822 to 44,380 | 398 bytes |

- Superframe 0: no frame is in progress, so 7 − 1 = 6 frames are read, and the 7th begins at bit 44,414 and goes on in superframe 1.
- The first block of frame 0 (bits 21 to 192): the previous, current and next size indexes at 21–29 (2,048, 2,048 and 512 samples), mid/side 1 at 30, coded flags 1 and 1 at 31–32, total gain 64 (one 7-bit value) at 33–39, the exponents of channel 1 at 40–110 and of channel 2 at 111–181 (scale factor codes, 25 bands each for a block of 2,048 at 44.1 kHz), and an end-of-block code for each channel at 182–187 (codebook 4) and 188–191 (codebook 5, channel 2 under mid/side).
- Frame 1's first three blocks code no channel (no gain is read), which is where the statistic "Blocks without Coded Channels = 3" comes from.
- After superframe 2's frame spanning superframes no bits are left before the bit offset. The file has stuffing and filler elsewhere: three frames spanning superframes are followed by 7,395 bits in all ("Frames Followed by Filler = 3", "Filler Bits = 7,395").
- Over the file: 9 superframes, 66 frames, 204 blocks, 8 frames spanning superframes, the largest reservoir 5,543 bytes; the last superframe leaves 44,343 bits (`wma.reservoir-at-end`), and the 65 valid frames after the first give 133,120 samples, which ffmpeg decodes too.

And one superframe of `external-ffmpeg-wmav2-stereo-44k-32k.wma` (A = 185, no reservoir, noise substitution with H_0 = 4, pair 0): frame 0 is bits 0 to 1,262 of the superframe: mid/side 1, both channels coded, total gain 104 at 3–9, noise flags at 10–17 (8 bits, none set: "0 of 8 high bands"), exponents at 18–53 and 54–89, coefficients at 90–737 (106 non-zero, 1 escape) and 738–1,261 (72 non-zero, 1 escape); the 27 bytes from byte 158 on are 'N'.

## FFmpeg's implementation choices

These are choices of FFmpeg 6.1.1's decoder, not rules of the format; a cross-check must account for them, and where Encspect departs from them it says so.

- **Superframes inside a frame:** FFmpeg appends the A − 1 bytes, then adds 8 × A − 8 to its reservoir length, which it counts in bytes. So the bit-offset bits of the superframe where the frame ends are appended 7 × (A − 1) bytes too far, and the frame is read with stale bytes in between; its reservoir limit of 32,768 is reached after a few continuations. Encspect keeps the bytes in order, and `WmaFindingTests.AFrameEndsAfterASuperframeInsideIt` holds that a frame spanning such a superframe is read to its end. No real file is known where FFmpeg decodes such a frame right.
- **A frame begun in a superframe inside a frame with nothing in progress:** FFmpeg starts reading it at its stale bit position (step 3 above); Encspect at bit 0.
- **Short media objects:** FFmpeg leaves the reservoir as it was (above); Encspect empties it.
- **Invalid header and frames past the end:** FFmpeg returns without emptying the reservoir; Encspect keeps it for the header case and empties it for the other.
- **Overrun:** FFmpeg's checks of the end are partial (see [Reading past the end of the data](#reading-past-the-end-of-the-data)); Encspect checks every frame.
- **Rejected superframes:** FFmpeg outputs nothing for a superframe with a failed frame, also not the frames decoded before it; Encspect counts those (see [Samples and duration](#samples-and-duration)).
- **The bit offset of the first superframe** is read and the bits before it skipped, whatever they hold.
- **Bitrate fallback:** with 0 average bytes per second FFmpeg's ASF reader takes the Stream Bitrate Properties Object's figure; Encspect does not analyze the stream.
- **Version 1 with variable block lengths:** the band list FFmpeg reads for the exponents is the smallest size's, followed by leftovers of the larger sizes (see [Not determined](#not-determined)).

## Observations on the test files

Observed behavior of the encoders behind the fixtures; none of it is a rule the analyzer applies, apart from ffmpeg's 'N' padding, which encoder detection uses.

- **ffmpeg's wmav1 and wmav2 encoders** (the 9 external files): options 0x0001 (scale factor exponents only), no reservoir, one block per frame, 'N' bytes after the byte the frame ends in, codec data of 4 bytes (version 1) or 10 with only the options set (version 2). Mid/side is set in every block of every stereo file.
- **Microsoft's encoders** (the 15 Windows files, wmvcore 12.0.26100.7309): the bit reservoir and variable block lengths in every file, "Samples Per Block" 17 × F (34,816 or 17,408), "Super Block Align" 0, options 0x000F, 0x001F or 0x0017; all five block sizes (four at 22.05 kHz) occur; the superframe index counts up from 0; the first superframe's bit offset is 0 (as in ffmpeg's files); at most 15 frames begin in one superframe.
- **Filler:** seven of the Windows files have bits after a frame spanning superframes up to the bit offset, all seven zero bits and five of them also bits that are not zero ([asf-wma.md](../formats/asf-wma.md#observed-in-microsofts-files)).
- **End of stream:** every Windows file ends with bits in the reservoir that no frame uses, and they are not zero; the quality 25 and 90 files end with a superframe inside a frame.
- **Exponent reuse** occurs in 11 of the 15 Windows files (28 to 50 blocks each); the four constant bitrate files at 128 and 192 kbit/s have none.
- **Never seen:** line spectral pair exponents, version 1 from Microsoft's encoders, a frame that fails, a short or long media object in a real file, a superframe inside a frame in the middle of a stream.

## Not determined

- **Version 1 with variable block lengths and scale factor exponents:** the exponents of a block read widths from the row of size index 0 of FFmpeg's table, which after the smallest size's list holds the leftovers of the larger sizes' lists.
  - A full-size block reads the smallest size's list, then the entries the larger sizes' lists left behind in the same row, then zeros, which count as 4 coefficients each. Only past the end of the row would it read outside the table.
  - The other sizes have empty lists and read the same row from its start.
  - Encspect counts 4 coefficients for each band past the list instead of the leftovers.
  - No file of this kind is at hand; one would show whether the format meant per-size lists, as version 2 has.
- **Version 2 at 49,048 Hz and above:** the bands of the two largest sizes stop 4 to 40 coefficients short of the block; FFmpeg takes the next widths from the following size's list, Encspect counts 4 each. No file is at hand.
- The meaning of "Samples Per Block", "Super Block Align", options bits 5 to 15 and the superframe index; whether 0x000D with variable blocks is really an encoder that sends one block size.
- Whether the bits before the bit offset of a stream's first superframe can hold anything.
- Whether stuffing and filler are intended or a by-product of the encoder's rate control.
- The levels' scale, the line spectral pair values and the escape levels' meaning beyond their widths, which a reader of the syntax does not need.

## Implementer's sources

The implementer may read the files marked safe: they hold nothing taken from FFmpeg's code (format tables are data, and prose descriptions of behavior are not code). The files marked not safe encode the bitstream as FFmpeg's code reads it, or are the code being replaced, or belong to the other WMA descriptions.

**Never read, whatever the folder:**

- FFmpeg source of any version, anywhere on the machine: the 6.1.1 archive and every other copy or checkout of it, including `libavcodec`, `libavformat` and `libavutil`.
- These scratch folders and everything in them: `wmadesc-standard`, `wmadesc-standard-review`, `wma-audit-ffmpeg`, `wmadesc-pro`, `wmadesc-voice`, `wmal-*`, `ff801*`, and `audit-*`.
- Any review under `reviews/` named `wma-*.md`.
- FFmpeg is run only as a black box, as `docs/development.md` describes the cross-checks.

**Safe:**

| File | Why |
| --- | --- |
| `docs/design/wma-standard-format.md` | this description |
| `docs/formats/asf-wma.md`, `docs/PLAN.md`, `docs/development.md`, `docs/design/decoders.md` | prose; asf-wma.md names a few FFmpeg identifiers in describing behavior and contains no code |
| `tests/fixtures/asf/*.report.json`, `*.expected.json`, `*.provenance.json` and the fixtures | outputs and data |
| `src/Encspect.Formats.Wma/Tables.cs` | format tables generated from FFmpeg's data files, no code |
| `src/Encspect.Formats.Wma/Bits.cs`, `Vlc.cs` | a generic bit reader and prefix-code reader; their comments name an FFmpeg type and function for what they mirror |
| `src/Encspect.Formats.Wma/PartsMap.cs`, `FindingCollector.cs`, `WmaEncoder.cs`, `Topics.cs` | Encspect's own mapping of bits to the file, findings, encoder detection and topic IDs, shared with the other WMA parsers |
| `src/Encspect.Formats.Wma/WmaHeaderOnly.cs` | the header-only streams of the Lossless and Voice parsers; no bitstream syntax |
| `src/Encspect.Host/Topics/wma-standard.md`, `wma-superframes.md`, `wma-bit-reservoir.md`, `wma-frames-and-blocks.md`, `noise-substitution.md`, `mid-side-stereo.md`, `bit-reservoir.md` | prose articles |
| `tests/Encspect.Tests/WmaVoiceTests.cs`, `VoiceFixtureTests.cs`, `VoiceBuilder.cs` | Voice tests and packet builder: safe to read, nothing in them derives from FFmpeg's code (comments name FFmpeg's behavior and file names only); the Voice implementation adapts them to its members |
| `tests/Encspect.Tests/WmaFixtureTests.cs`, `WmaCorruptionTests.cs`, `WmaBitsPeekTests.cs`, `VlcTests.cs`, `AsfBuilder.cs`, `AsfFixtureTests.cs`, `AsfFindingTests.cs`, `MemoryTests.cs`, `ComplexityTests.cs`, `LongInputs.cs`, `ParallelAnalysisTests.cs`, `ProtectionTests.cs`, `WriterRoleTests.cs`, `DescriptionTests.cs`, `FindingCoverageTests.cs` | tests of outputs, of the generic readers and of the container; no bitstream syntax |
| `scripts/fixtures/make_asf_fixtures.py`, `asf_model.py`, `make_external_asf_fixtures.py`, `import_windows_wma_fixtures.py`, `wma_signals.py`, `crosscheck_asf_fixtures.py`, `asf_specs.py`, `scripts/fixtures/windows/*`, `.github/workflows/wma-fixtures.yml` | the container, the signals, the encoder and tool runs; the hand-built WMA media objects are constant bytes |
| `scripts/fixtures/wma_tables.py` | extracts the arrays from FFmpeg's source text into `Tables.cs`; no decoding logic (its Voice part is the Voice description's concern) |

**Not safe:**

| File | Why |
| --- | --- |
| `src/Encspect.Formats.Wma/StandardParameters.cs`, `StandardStream.cs`, `StandardDecoder.cs` | the code being replaced; it follows `wma.c` and `wmadec.c` step by step |
| `src/Encspect.Formats.Wma/StandardNodes.cs`, `StandardReport.cs`, `WmaStandardParser.cs`, `Records.cs` | the current reader's records and node building, interleaved with its FFmpeg-shaped state; [What Encspect reports](#what-encspect-reports) describes their output, and the members other code needs are listed below |
| `src/Encspect.Formats.Wma/Explain.cs` | prose only, but each description cites FFmpeg's variable and function names for the field; write new descriptions from this document instead |
| `src/Encspect.Formats.Wma/Pro*.cs`, `WmaProParser.cs` | the Professional parser and decoder, which follow another FFmpeg decoder; the Professional description's concern |
| `src/Encspect.Formats.Wma/Voice*.cs`, `WmaVoiceParser.cs` | the Voice parser and decoder; the Voice description's concern |
| `src/Encspect.Formats.Wma/WmaLosslessParser.cs` | the Lossless header reader, which cites FFmpeg's names; not needed |
| `tests/Encspect.Tests/WmaNoiseTests.cs` | builds a frame bit by bit in FFmpeg's reading order, including the zero-width band rule |
| `tests/Encspect.Tests/WmaFindingTests.cs` | its version 1 and 2 cases build superframe headers from FFmpeg's layout and cite FFmpeg's reservoir arithmetic; its Professional and Voice cases belong to the other descriptions |

**Members other code uses.** The replaced files hold members that the rest of the parser uses and that are not taken from FFmpeg; the replacement keeps them with these signatures:

- `public sealed class WmaStandardParser : ICodecParser` with `public const string CodecId = "wma"` (the host registers it), and `internal const int MaxPackets = 1 << 18` (the record limit above).
- `internal static void AddBasicStream(CodecContext context, Node parent, string codec, AudioFormat format, int version, CodecConfiguration? configuration = null, string? description = null, string? topic = null)`: the header-only stream described under [Stream](#stream). `WmaProParser.cs` and `WmaHeaderOnly.cs` call it, the Professional one with version 3 and its own description and topic. Where the caller passes no description, an encrypted stream takes `ProExplain.EncryptedHeaderOnlyStream` (a constant in `ProExplain.cs`, which stays) and any other the standard stream description, which the replacement writes anew.
- `internal static string Count(long count, string singular, string plural)`: "N singular" or "N plurals" with N in invariant digits; `WmaHeaderOnly.cs` calls it.
- `internal readonly record struct FramePart(int Superframe, int StartBit, int BitCount)` (now in `Records.cs`, which the replacement removes or keeps only for this): one run of a frame's bits, the index of the superframe holding it, the first bit in that superframe and the number of bits. `PartsMap.cs` takes lists of it, and the Professional and Voice nodes build them.
- In the class `Explain` (of `Explain.cs`), the four constants `WmaEncoder.cs` uses: `Encoder` "Which program encoded the stream, as the analyzer concludes from the evidence below.", `Confidence` "How sure the naming is: high when the file carries the encoder's own trait, medium for a pattern of the encoder's, low when only a claim in the metadata names it.", `Evidence` "What points to the encoder: a trait of the stream, or an attribute of the file that names it, with its offset in the file where it has one." and `WriterEvidence` "What points to the writer: the attribute that names it or the header layout that is its habit, with its offset in the file."

**What the tests need of the new code.** `WmaNoiseTests` and `WmaFindingTests` call members of the current reader that mirror FFmpeg's variables, so they are not kept as they are. The implementer adapts them to the new reader's own members, from what they check, which this document states:

- `WmaNoiseTests`: that the high bands derived for a mono 44.1 kHz stream with noise substitution are the ones in [High bands](#high-bands), and that a frame with a flagged high band is read to its end with the node "Noise Substitution" saying "n of m high bands", the levels counted and the coefficients stopping short by the flagged widths.
- `WmaFindingTests`: that each damaged stream of versions 1 and 2 gives the finding of [Findings](#findings) at the rule that [Validity rules and damage](#validity-rules-and-damage) names, that a frame spanning a superframe inside a frame is read to its end, that the smallest frame is the size the golden reports state, and that the codes of configurations not decoded (`wma.format-unsupported`) are asked for. Its Professional and Voice cases stay as they are.
