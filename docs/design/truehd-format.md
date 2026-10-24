# Dolby TrueHD and MLP: format description

**Status:** written on 2026-10-24 by a Claude agent (Opus) that read FFmpeg 6.1.1's LGPL TrueHD and MLP source (listed under [Sources](#sources)) and checked this text against real and FFmpeg-encoded files. It contains no code. Whoever implements the TrueHD parser and decoder must not read FFmpeg's TrueHD or MLP source; FFmpeg serves only as a black-box cross-check (decision 10 in [decoders.md](decoders.md#decisions)).

This is the clean-room description that [decoders.md](decoders.md) rollout step 3 asks for. It describes the bitstream as FFmpeg reads it, states FFmpeg's checks as format rules, and marks what no source settles. The plan's scope is in [PLAN.md, "Phase 3: Dolby TrueHD and MLP"](../PLAN.md#phase-3-dolby-truehd-and-mlp).

## Sources

**Read (FFmpeg 6.1.1, the release `scripts/fixtures/ffmpeg_sources.py` pins):**

- `libavcodec/mlp_parse.c` and `mlp_parse.h`: the major sync, the rate, bit depth, MLP arrangement and TrueHD channel tables.
- `libavcodec/mlp.c` and `mlp.h`: the three checksums, the parity, the Huffman tables, the MLP channel information table, the limits, the channel modifiers.
- `libavcodec/mlpdec.c`: everything below the major sync, that is the reading of the access unit, the restart header, the decoding parameters (matrices, channels, filters), the residuals and blocks, the noise generation and the output, and its noise table.
- `libavcodec/mlpdsp.c`: the prediction filter, the rematrixing and the output with the lossless check.
- `libavcodec/mlp_parser.c`: framing and the parity check of FFmpeg's parser.
- `libavcodec/mlpenc.c`: what FFmpeg's encoder writes into the fields the decoder skips (major sync, restart header, substream and unit headers).
- `libavcodec/truehd_core_bsf.c` and `profiles.c`: the Atmos substream and its signaling.
- `libavformat/mlpdec.c` (raw probe and demuxer), `mov.c` (the `dmlp` box reader), `movenc.c` (the `dmlp` box and `mlpa` sample entry writer), `isom_tags.c`, `matroska.c` and `matroskadec.c`.
- All of these are in the pinned release and listed in `FILES` of `scripts/fixtures/ffmpeg_sources.py`, which `scripts/fixtures/check_sources.py` checks against the archive.

**Not reachable:** developer.dolby.com refused the connection from this session (proxy status 502), so neither "Dolby TrueHD high-level bitstream description" nor "MLP (Dolby TrueHD) streams within the ISO Base Media File Format" was read. The AES paper on MLP was not read for this document either. Nothing here comes from them.

**Test files** (downloaded, never committed; SHA-256 given so that the implementer can fetch the same bytes):

| File | Source | SHA-256 | What it is |
| --- | --- | --- | --- |
| `TrueHD.raw` | samples.ffmpeg.org/A-codecs/TrueHD/ | `221b59a28347b610c60019e4d4896bb9ac0be88642c6964c5876ba558f0d511c` | a commercial TrueHD stream (the Matroska file below names a Blu-ray film), 48 kHz, 5.1, two substreams, 19,420 units (19,407 from the first major sync on); begins with 13 units (2,358 bytes) before the first major sync unit, whose sync word is at byte 2,362. Called "the Dolby file" below, on the assumption that it comes from Dolby's encoder; the stream does not name its encoder |
| `TrueHD Sample 1 (Final Fantasy VII Advent Children).mkv` | same folder | `1c2bb751e78c38b7a1311abfdbe7149dae62df82fc9814e7edc740a188c1226c` | mkvmerge 2.9.7; its A_TRUEHD track's payload is byte for byte `TrueHD.raw`, so it is the same stream, not a second one |
| `440hz.mlp` | samples.ffmpeg.org/A-codecs/lossless/mlp/ | `c46b722bbf56c8316a8088cbf1bc7a3f9eeac529699e7fe4600d449f4d0760d9` | MLP, 44.1 kHz, 16-bit, stereo, one substream, 11,025 units; encoder not stated |
| `luckynight.mlp` | samples.ffmpeg.org/A-codecs/lossless/ | `448a25b6f1f01f23393fa379414d51682ea266f74670bcd1f96a9560905650e0` | MLP, 44.1 kHz, 16-bit, stereo, one substream, 66,680 units, uses IIR filters with state; encoder not stated |
| FFmpeg-encoded | FFmpeg 6.1.1, `-c:a truehd` or `-c:a mlp` with `-strict -2`, from `lavfi` sine and noise sources | (made locally, reproducible) | TrueHD 5.1 at 48 kHz with an end of stream marker, TrueHD stereo at 96 kHz, MLP stereo at 44.1 kHz |

**How the text was checked:** the author wrote a throwaway model from this description (not committed; it is derived from reading FFmpeg and must not reach the implementer). On every file above it:

- passed every major sync CRC, parity nibble, restart header CRC, substream parity byte and substream CRC-8;
- gave sample-for-sample the output of FFmpeg 6.1.1's decoder (`ffmpeg -f s32le`) for the top presentation, and for the 2-channel presentation with `-downmix stereo`;
- matched every stored lossless check of every presentation, including the 2-channel presentation of the Dolby stream, which FFmpeg itself never compares (1,212 checks each in the two presentations of `TrueHD.raw`, 8,289 in `luckynight.mlp`).

What the files do not exercise is listed under [Not determined](#not-determined).

## Conventions

- Bits are read most significant first, from the first byte on. `u(n)` is an n-bit unsigned field, `s(n)` an n-bit two's complement field.
- A word is 16 bits. Lengths and offsets the stream gives in words are multiplied by 2 for bytes.
- "Floor-shift right by k" means division by 2^k rounded toward minus infinity (an arithmetic shift).
- "Clear the low Q bits" means the two's complement value with bits 0 to Q−1 set to zero.
- Hex values are written 0x…; bit strings in backticks.

## Terms

- **Access unit:** the coding unit with its own length: 40 sample periods at 48 or 44.1 kHz, 80 at 96 or 88.2 kHz, 160 at 192 or 176.4 kHz. FFmpeg calls it a frame.
- **Major sync:** a header at the start of some access units that carries the stream parameters and the substream count. Decoding can start only at a unit with one.
- **Substream:** a part of an access unit with its own channels, parameters and checks. A stream has 1 to 4 (MLP at most 2).
- **Presentation:** a channel layout a decoder can output. In TrueHD the 2-channel, 6-channel and 8-channel presentations, and with Dolby Atmos a 16-channel one; each is output by one substream from the coded channels of that substream and the lower ones.
- **Restart header:** the part of a substream that resets its parameters and carries the lossless check; in the test files it appears only in units with a major sync.
- **Decoding parameters:** the parameters a block may change (block size, matrices, shifts, filters, entropy coding).
- **Block:** a run of samples coded with one set of parameters; a substream holds one or more blocks per access unit.
- **Coded channel:** a channel whose residuals a substream carries. **Matrix channel:** a channel of a presentation, before the output assignment. **Noise channel:** one of two pseudorandom channels the matrices can use where the noise type is 0.
- **Primitive matrix:** one rematrixing step that replaces one matrix channel by a weighted sum of all channels.

## Stream structure

- A stream is a sequence of access units with no gaps or padding between them. Each unit states its length.
- A major sync appears in some units (in the test files every 8 to 16 units, see [Observations](#observations-on-the-test-files)); between them, units have none.
- Each unit holds: the unit header, the major sync where present, the substream directory, then the data of each substream in order.
- The data of a substream is its blocks, an optional end of stream marker and an optional parity byte and CRC-8.

## Access unit

### Unit header

| Byte | Bits | Field | Meaning |
| --- | --- | --- | --- |
| 0 | 4 (high nibble) | check nibble | makes the parity come out right (see [Parity nibble](#parity-nibble)) |
| 0–1 | 12 | unit length | the whole unit in words, header included; at least 2 words (4 bytes), at most 4,095 (8,190 bytes) |
| 2–3 | 16 | input timing | a 16-bit counter in sample periods, wrapping; FFmpeg's decoder ignores it (see [Observations](#observations-on-the-test-files) for what the files show) |

### Major sync presence

- A unit carries a major sync where bytes 4 to 7 read 0xF8726FBA (TrueHD) or 0xF8726FBB (MLP). FFmpeg's decoder and parser compare the first 31 bits (the last bit, which tells TrueHD from MLP, is masked); only its raw probe compares all 32.
- The major sync then fills bytes 4 to 4 + H − 1, where H is its size ([Major sync](#major-sync)); the directory follows it.
- Without a major sync, the directory starts at byte 4.

### Substream directory

One entry per substream, the count taken from the most recent major sync:

| Bits | Field | Meaning |
| --- | --- | --- |
| 1 | extra word present | a 16-bit word follows this entry; TrueHD only, an MLP entry with it set is invalid |
| 1 | non-restart | must be 0 in a unit with a major sync and 1 in a unit without; anything else is invalid |
| 1 | check data present | the substream ends with a parity byte and a CRC-8 |
| 1 | reserved | FFmpeg skips it; set in some units of every real test file, never in FFmpeg's encoder output; meaning unknown |
| 12 | substream end | where the substream's data ends, in words, counted from the first byte after the directory |
| 16 | extra word | only where flagged; FFmpeg skips it; content unknown |

- The ends are cumulative: substream i occupies the bytes from the end of substream i − 1 (0 for the first) to its own end.
- An end smaller than the previous one is invalid. An end past the unit is invalid (FFmpeg logs it and clamps the substream to the unit).
- Bytes between the last substream's end and the unit's end: none in any test file; FFmpeg ignores them; meaning unknown.

### Parity nibble

- XOR together the four bytes of the unit header and every byte of the directory (extra words included), but not the major sync.
- The XOR of the result's two nibbles must be 0xF. Otherwise the unit is invalid.
- Equivalently: the XOR of all 16-bit words of the unit header and the directory, folded to one nibble, is 0xF. An encoder computes the check nibble last, as 0xF XOR the fold of everything else.
- FFmpeg's decoder checks it on every unit once stream parameters are valid (before the first major sync it skips units unchecked); FFmpeg's parser only on units without a major sync (units with one have the CRC-16 instead).

### Example unit

The first access unit of `440hz.mlp` (102 bytes) exercises most of the syntax:

```text
9033ffd8 f8726fbb0f8f0001b752400000008d9a11054c0300008080001b538c 3022
f1ea0000011000000453bdc0000000023044417922b341380d2009ffd7eafe19ddde
07663ec49100d7601981a53c86d7848ffdda86656f52aa2d9bca9762cb635800e1ff
```

- Unit header 0x9033 0xFFD8: check nibble 9, length 0x033 = 51 words = 102 bytes, input timing 0xFFD8.
- Major sync of 28 bytes ending in the CRC 0x538C.
- Directory 0x3022: no extra word, non-restart 0, check data present, reserved 1, end 0x022 = 34 words.
- Parity: 0x90 ⊕ 0x33 ⊕ 0xFF ⊕ 0xD8 ⊕ 0x30 ⊕ 0x22 = 0x96, and 9 ⊕ 6 = 0xF.
- Substream: a restart header in bits 2 to 126 (noise type 0, output timing 0x0000, channels 0 to 1, matrix channels 0 to 1, noise seed 2, skipped fields 2, 19, 23 and 23, lossless check 0x00), then its CRC byte 0x18 (the register starts at 0, so 0x18 is the value to expect), decoding parameters with block size 8, a block, decoding parameters with block size 32 and a 5-tap FIR filter on channel 0, a block, the parity byte 0xE1 and the CRC-8 0xFF.
- Output: the first four sample periods are 131,072, 261,888, 391,680 and 520,192 (24-bit values) on both channels.

## Major sync

### Layout

Bit positions are counted from the first bit of the sync word (unit byte 4).

| Bit | Width | Field | Meaning |
| --- | --- | --- | --- |
| 0 | 24 | sync | 0xF8726F |
| 24 | 8 | stream type | 0xBB MLP, 0xBA TrueHD; anything else is invalid |
| 32 | 32 | format info | [MLP](#format-info-mlp) or [TrueHD](#format-info-truehd) |
| 64 | 16 | signature | 0xB752 in every test file; FFmpeg's decoder does not check it |
| 80 | 16 | flags | FFmpeg's decoder skips them (see below) |
| 96 | 16 | reserved | 0 in every test file |
| 112 | 1 | variable rate | 1 where the stream is variable rate; 1 in every test file |
| 113 | 15 | peak data rate | in sixteenths of a bit per sample period: bits per second = value × sampling rate / 16 (FFmpeg rounds as (value × rate + 8) / 16, truncated) |
| 128 | 4 | substream count | 1 to 4; 0 is invalid; MLP more than 2 is invalid |
| 132 | 2 | reserved | 0 in every test file |
| 134 | 2 | extended substream info | TrueHD: tied to the 16-channel presentation (see [Dolby Atmos](#dolby-atmos)); 1 in both real MLP files, 0 elsewhere |
| 136 | 8 | substream info | see [Presentations and substreams](#presentations-and-substreams) |
| 144 | 64 | channel meaning | [Channel meaning](#channel-meaning); its last bit (bit 207, bit 0 of byte 25) is the TrueHD extension flag |
| 208 | 16 + 16·n | extension | TrueHD only, where the extension flag is set ([Extension](#extension)) |
| H·8 − 16 | 16 | CRC | [CRC-16](#crc-16) |

Size H: 28 bytes; for TrueHD with the extension flag set, 30 + 2·n, where n is the high nibble of byte 26. An MLP major sync is always 28 bytes, whatever bit 0 of its byte 25 holds (it is part of another field there; `440hz.mlp` has it set).

Flags, from FFmpeg's encoder only: it writes 0x4000 for MLP (it ties bit 14 to DVD-Audio) and 0 for TrueHD, and defines bit 15 for a constant FIFO delay. Both real MLP files have 0x4000, the Dolby TrueHD file 0x0000. Meaning **unverified**.

### Format info: MLP

| Bit | Width | Field | Meaning |
| --- | --- | --- | --- |
| 32 | 4 | group 1 bit depth | 0: 16 bits, 1: 20 bits, 2: 24 bits; other codes give no bit depth, and a group 1 without one is invalid |
| 36 | 4 | group 2 bit depth | as group 1; 0xF (no group 2) in every test file; a group 2 longer than group 1 is invalid |
| 40 | 4 | group 1 sampling rate | rate code ([table](#sampling-rate-and-unit-size)) |
| 44 | 4 | group 2 sampling rate | 0xF where group 2 has no own rate; a different rate than group 1 is unsupported by FFmpeg (see [Implementation choices](#ffmpegs-implementation-choices)) |
| 48 | 4 | reserved | |
| 52 | 4 | multichannel type | FFmpeg's encoder writes 0 under that name; meaning unknown |
| 56 | 3 | reserved | |
| 59 | 5 | channel arrangement | [MLP channel arrangements](#mlp-channel-arrangements); codes 21 to 31 give no layout |

The MLP substream info byte (major sync bit 136) is described by FFmpeg's encoder as four flags, **unverified**: 0x01 at most two channels, 0x02 a high sampling rate, 0x04 always set, 0x08 two substreams. Both real MLP files have 0x05 (stereo at 44.1 kHz, one substream). An analyzer can report the byte with these names as context.

A channel group is a set of channels sharing bit depth and sampling rate (DVD-Audio uses two). How many channels each group holds follows from the arrangement (table below).

### Format info: TrueHD

| Bit | Width | Field | Meaning |
| --- | --- | --- | --- |
| 32 | 4 | sampling rate | rate code ([table](#sampling-rate-and-unit-size)) |
| 36 | 1 | 6-channel multichannel type | FFmpeg's encoder name; FFmpeg's decoder skips it; 0 in every test file |
| 37 | 1 | 8-channel multichannel type | the same for the 8-channel presentation |
| 38 | 2 | reserved | |
| 40 | 2 | 2-channel modifier | [Channel modifiers](#truehd-channel-modifiers) |
| 42 | 2 | 6-channel modifier | |
| 44 | 5 | 6-channel assignment | flags 0 to 4 of the [TrueHD channel flags](#truehd-channel-flags) |
| 49 | 2 | 8-channel modifier | |
| 51 | 13 | 8-channel assignment | flags 0 to 12 |

- TrueHD states no bit depth; FFmpeg reports 24 bits for every TrueHD stream.
- These 32 bits are what an MP4 `dmlp` box repeats ([Files and containers](#files-and-containers)).

### Sampling rate and unit size

| Code | Rate | Samples per unit | Noise buffer size P |
| --- | --- | --- | --- |
| 0 | 48 kHz | 40 | 64 |
| 1 | 96 kHz | 80 | 128 |
| 2 | 192 kHz | 160 | 256 |
| 8 | 44.1 kHz | 40 | 64 |
| 9 | 88.2 kHz | 80 | 128 |
| 10 | 176.4 kHz | 160 | 256 |
| 0xF | none | | |

- In general the rate is 48,000 (code bit 3 clear) or 44,100 (set) times 2^(code bits 0 to 2), and a unit holds 40 · 2^(code bits 0 to 2) samples per channel. P is 64 · 2^(code bits 0 to 2), the power of two at or above that.
- Codes 3 to 7 and 11 to 14 give more than 192 kHz, which FFmpeg rejects as above its maximum; code 0xF gives no rate, which is invalid for group 1 and for TrueHD.

### Channel meaning

The 64 bits from bit 144 differ between MLP and TrueHD. FFmpeg's decoder reads none of them except the TrueHD extension flag. The split below is what FFmpeg's encoder writes (all zero except where noted); field meanings are its names, **unverified** against a specification.

MLP:

| Width | Field (FFmpeg's encoder) | `440hz.mlp` |
| --- | --- | --- |
| 5 | sampling rate code (44.1 kHz: 9, 88.2: 13, 176.4: 17, 48: 10, 96: 14, 192: 18) | 9 |
| 5 | bit depth (16 or 24) | 16 |
| 6 | channel occupancy (the table below) | 3 |
| 3 | reserved | 0 |
| 10 | speaker layout | 0 |
| 3 | copy protection | 0 |
| 16 | (FFmpeg writes 0x8080) | 0x8080 |
| 7 | reserved | 0 |
| 4 | source format | 0 |
| 5 | summary info (the table below) | 0x1B |

The real MLP file agrees with FFmpeg's encoder in every field, which supports the split but not the meanings.

TrueHD:

| Width | Field (FFmpeg's encoder) | `TrueHD.raw` |
| --- | --- | --- |
| 6 | reserved | 0 |
| 1 | 2-channel control enabled | 0 |
| 1 | 6-channel control enabled | 0 |
| 1 | 8-channel control enabled | 0 |
| 1 | reserved | 0 |
| 7 | dynamic range control start-up gain | 125 |
| 6 | 2-channel dialogue normalization | 36 |
| 6 | 2-channel mix level | 40 |
| 5 | 6-channel dialogue normalization | 31 |
| 6 | 6-channel mix level | 35 |
| 5 | 6-channel source format | 0 |
| 5 | 8-channel dialogue normalization | 31 |
| 6 | 8-channel mix level | 35 |
| 6 | 8-channel source format | 0 |
| 1 | reserved | 0 |
| 1 | extension present | 0 |

The Dolby file's values are plausible under this split (the 6-channel and 8-channel presentations are the same there and have equal values), but their units and meaning are **unverified**; an analyzer reports them raw.

### Extension

- TrueHD only, present where the last bit of the channel meaning is set.
- It starts at byte 26 of the major sync: its first nibble n counts further words, and it is 2 + 2·n bytes long, so the CRC moves to bytes 28 + 2·n and 29 + 2·n.
- FFmpeg's decoder reads only n. Its `truehd_core` filter drops the extension and clears the flag when it strips the Atmos substream, which suggests Atmos streams carry it; no test file does. Content **unknown**.

### CRC-16

- Generator x^16 + x^5 + x^3 + x^2 + 1 (0x002D), most significant bit first, register starting at 0, no final XOR.
- The check is the remainder of the bytes 0 to H − 3 of the major sync (all but the stored CRC) divided by the generator, without the 16 zero bits a standard CRC appends. It must equal bytes H − 2 and H − 1 read most significant byte first.
- Equivalently: a standard CRC-16 with that generator over bytes 0 to H − 5, XORed with bytes H − 4 and H − 3 read as a 16-bit value, most significant first. This is how FFmpeg computes it (`ff_mlp_checksum16`). FFmpeg's CRC routine keeps 16-bit values byte-swapped, which is why its code reads the stored value as little endian; in plain terms it is big endian.
- A mismatch makes the major sync, and so the unit, invalid in FFmpeg.

Examples, both verified:

- `TrueHD.raw`, first major sync: `f8726fba0007800fb75200000000841b203c003ec947e307e3003602`, CRC 0x3602.
- `440hz.mlp`, first major sync: `f8726fbb0f8f0001b752400000008d9a11054c0300008080001b538c`, CRC 0x538C.

### MLP channel arrangements

Channel order within an arrangement is group 1, then group 2, as listed. Channels are written out; "surround" is the single surround channel (FFmpeg maps it to back center), "left surround" and "right surround" are the pair FFmpeg maps to back left and back right. Occupancy and summary info are what FFmpeg's encoder writes for each arrangement (`ff_mlp_ch_info`); its decoder does not use them.

| Code | Group 1 | Group 2 | Channels | Occupancy | Summary info |
| --- | --- | --- | --- | --- | --- |
| 0 | center |  | 1 | 0x01 | 0x1F |
| 1 | front left, front right |  | 2 | 0x03 | 0x1B |
| 2 | front left, front right | surround | 3 | 0x07 | 0x1F |
| 3 | front left, front right | left surround, right surround | 4 | 0x0F | 0x19 |
| 4 | front left, front right | LFE | 3 | 0x07 | 0x03 |
| 5 | front left, front right | LFE, surround | 4 | 0x0F | 0x1F |
| 6 | front left, front right | LFE, left surround, right surround | 5 | 0x1F | 0x01 |
| 7 | front left, front right | center | 3 | 0x07 | 0x1A |
| 8 | front left, front right | center, surround | 4 | 0x0F | 0x1F |
| 9 | front left, front right | center, left surround, right surround | 5 | 0x1F | 0x18 |
| 10 | front left, front right | center, LFE | 4 | 0x0F | 0x02 |
| 11 | front left, front right | center, LFE, surround | 5 | 0x1F | 0x1F |
| 12 | front left, front right | center, LFE, left surround, right surround | 6 | 0x3F | 0x00 |
| 13 | front left, front right, center | surround | 4 | 0x0F | 0x1F |
| 14 | front left, front right, center | left surround, right surround | 5 | 0x1F | 0x18 |
| 15 | front left, front right, center | LFE | 4 | 0x0F | 0x02 |
| 16 | front left, front right, center | LFE, surround | 5 | 0x1F | 0x1F |
| 17 | front left, front right, center | LFE, left surround, right surround | 6 | 0x3F | 0x00 |
| 18 | front left, front right, left surround, right surround | LFE | 5 | 0x1F | 0x01 |
| 19 | front left, front right, left surround, right surround | center | 5 | 0x1F | 0x18 |
| 20 | front left, front right, left surround, right surround | center, LFE | 6 | 0x3F | 0x00 |

### TrueHD channel flags

Bit i of a channel assignment (bit 0 the least significant) adds these channels, in this order:

| Bit | Channels | FFmpeg's channels |
| --- | --- | --- |
| 0 | left, right | front left, front right |
| 1 | center | front center |
| 2 | LFE | low frequency |
| 3 | left/right surround | side left, side right |
| 4 | left/right vertical height | top front left, top front right |
| 5 | left/right center | front left of center, front right of center |
| 6 | left/right rear surround | back left, back right |
| 7 | center surround | back center |
| 8 | top surround | top center |
| 9 | left/right surround direct | surround direct left, surround direct right |
| 10 | left/right wide | wide left, wide right |
| 11 | center vertical height | top front center |
| 12 | second LFE | low frequency 2 |

- The channel list of a presentation is the channels of its set flags in this order, each pair left before right. 5.1 is 0x000F: left, right, center, LFE, left surround, right surround.
- The 6-channel assignment has only bits 0 to 4.

### TrueHD channel modifiers

From `mlp.h` and FFmpeg's decoder:

| Value | 2-channel presentation | 6-channel and 8-channel presentations |
| --- | --- | --- |
| 0 | stereo, or not indicated | not indicated |
| 1 | Dolby Surround (Lt/Rt) | not Dolby Digital EX |
| 2 | Dolby Headphone (Lbin/Rbin) | Dolby Digital EX (FFmpeg applies it only where the presentation has a left/right surround pair) |
| 3 | mono or dual mono | not defined by any source read |

FFmpeg's encoder writes 1 in all three for stereo input and 3 in all three for mono; the Dolby file has 0 in all three.

## Substream syntax

### Blocks

The data of a substream is a sequence of blocks. Each block is:

| Bits | Field | Condition |
| --- | --- | --- |
| 1 | parameters present | always |
| 1 | restart header present | where parameters present is 1 |
| var | [restart header](#restart-header) | where restart header present is 1 |
| var | [decoding parameters](#decoding-parameters) | where parameters present is 1 |
| var | [block data](#block-data) | always |
| 1 | last block | always; 1 ends the substream's blocks in this unit |

- A substream needs a restart header before its first block can be decoded, and FFmpeg forgets that a substream had one at every major sync, not only when decoding starts. In a unit with a major sync, a substream whose first block carries no restart header is skipped for the unit and stays skipped until a restart header arrives, although its parameters from the previous restart interval are still held. The directory's non-restart bit is clear in exactly these units, so a unit with a major sync is expected to carry a restart header in every substream (all test files do).
- A restart header in a unit without a major sync: FFmpeg accepts it (it resets the state and compares the lossless check), but no test file has one. Whether that is valid is **unverified**.
- After a block's data at least one bit must remain for the last-block flag; otherwise the substream's length is wrong and the unit is invalid.
- In every test file, restart headers occur only in units with a major sync, in every substream of those units, and always in the first block (so at bit 2 of the substream). FFmpeg computes the restart header CRC as if the header started at bit 2 (see [Restart header CRC](#restart-header-crc)).

### Restart header

| Bits | Field | Meaning and range |
| --- | --- | --- |
| 13 | restart sync | 0x18F5; anything else is invalid. With the next bit it reads 0x31EA or 0x31EB as 14 bits, the names FFmpeg uses for the two kinds |
| 1 | noise type | 0: two noise channels ([Noise generation](#noise-generation)); 1: the noise table. MLP must have 0 |
| 16 | output timing | a 16-bit counter in sample periods; FFmpeg ignores it |
| 4 | first coded channel | index of the first channel this substream codes |
| 4 | last coded channel | index of the last; at most 7, and at least first − 1 (first = last + 1 codes no channel; FFmpeg allows it) |
| 4 | last matrix channel | M, the presentation's channel count less 1; at most 5 for MLP and 7 for TrueHD. M above 5 with noise type 0 is unsupported by FFmpeg |
| 4 | noise shift | left shift of the noise channels' values (noise type 0) |
| 23 | noise seed | starting value of the noise generator |
| 19 | (skipped) | FFmpeg skips them; its encoder writes four fields: 4 bits, the largest output shift, 5 bits, the largest LSB count, and twice 5 bits, the largest output bit depth, each over the samples up to the next restart header. Every test file fits that reading (see [Observations](#observations-on-the-test-files)); decoding does not need them |
| 1 | block size check present | [Block size check data](#block-size-check-data) follows every block of this substream until the next restart header |
| 8 | lossless check | [Lossless check](#lossless-check) |
| 16 | (skipped) | 0 or 0x8000 in the Dolby file, 0 elsewhere; FFmpeg's encoder writes 0; meaning unknown |
| 6 × (M + 1) | channel assignment | for matrix channels 0 to M in order, its position in the presentation's channel list ([Output](#output)) |
| 8 | CRC | [Restart header CRC](#restart-header-crc) |

- The restart header is 113 + 6·(M + 1) bits plus the 8-bit CRC.
- No check in FFmpeg ties the last coded channel to M; in every test file the last coded channel equals M.
- A channel assignment is invalid where the position does not exist: for MLP, a position above M; for TrueHD, a position at or past the number of channels in the presentation's channel list (FFmpeg also rejects one whose channel would land past M in its own channel order).

### Restart header CRC

- Generator x^8 + x^4 + x^3 + x^2 + 1 (0x1D), most significant bit first, register starting at 0.
- The check is the remainder of the restart header's bits, from the first bit of the restart sync through the last bit of the channel assignment, without the appended zero bits (as for the [CRC-16](#crc-16)); the next 8 bits must equal it.
- The bits are generally not byte aligned. FFmpeg (`ff_mlp_restart_checksum`) assumes the header starts at bit 2 of the substream data, which holds in every test file; an implementation could take the header's actual first bit (**unverified**: no test file has another start).
- A mismatch is only logged by FFmpeg, which then uses the header.

### Values a restart header resets

After a restart header, before its decoding parameters are read, the substream's state is:

- parameter presence flags 0xFF (every parameter may be present);
- no primitive matrices;
- block size 8;
- output shift 0 for every matrix channel; quantizer step size 0 for every channel;
- for each coded channel: FIR and IIR filter orders 0 and their shifts 0, Huffman offset 0, codebook 0, LSB count 24 (that is, with codebook 0, each residual is a 24-bit field minus 2^23, see [Block data](#block-data));
- lossless check accumulator 0 (after its comparison, see [Lossless check](#lossless-check));
- noise generator register: the noise seed.

Not reset: the filters' sample histories (see [Prediction filters](#prediction-filters)). FFmpeg keeps them across restart headers and starts them at zero when decoding starts.

### Decoding parameters

The parameter presence flags (8 bits) decide which groups a decoding parameters part can hold. A group whose flag is set starts with a 1-bit present field; the group's content follows only where that bit is 1, and otherwise the group keeps its value. A group whose flag is clear has no bits at all.

| Flag bit | Group | Content where present |
| --- | --- | --- |
| 0 (0x01) | presence flags | u(8): new presence flags, used from the next group on |
| 7 (0x80) | block size | u(9): samples per block, 8 to the unit's samples per channel (40, 80 or 160); others invalid |
| 6 (0x40) | matrices | [Matrix parameters](#matrix-parameters) |
| 5 (0x20) | output shifts | (M + 1) × s(4): output shift of each matrix channel 0 to M; negative values are unsupported by FFmpeg, which uses 0 |
| 4 (0x10) | quantizer step sizes | u(4) for each channel **0** to the last coded channel (not from the first coded one) |
| 3 (0x08) | FIR filter | inside each channel's parameters |
| 2 (0x04) | IIR filter | inside each channel's parameters |
| 1 (0x02) | Huffman offset | inside each channel's parameters |

The groups come in this order: presence flags, block size, matrices, output shifts, quantizer step sizes, then for each coded channel from first to last:

| Bits | Field | Condition |
| --- | --- | --- |
| 1 | channel parameters present | always |
| var | [channel parameters](#channel-parameters) | where it is 1 |

- Once a presence flags update clears bit 0, the flags cannot change again until the next restart header.
- Every test file keeps the flags at 0xFF.

### Matrix parameters

| Bits | Field | Meaning and range |
| --- | --- | --- |
| 4 | matrix count | J, at most 6 for MLP and 8 for TrueHD |

Then for each primitive matrix j = 0 to J − 1:

| Bits | Field | Meaning and range |
| --- | --- | --- |
| 4 | output channel | the matrix channel this matrix replaces, at most M |
| 4 | fractional bits | f, at most 14 |
| 1 | LSB bypass | the block data carries one extra bit per sample for this matrix ([Block data](#block-data)) |
| per input channel: 1, then f + 2 | coefficient present, coefficient | for input channels 0 to M, and for noise type 0 also M + 1 and M + 2 (the noise channels); where present, s(f + 2), else 0 |
| 4 | matrix noise shift | noise type 1 only |

- A coefficient's value is the coded number times 2^(14 − f), a fraction with 14 fractional bits, so its range is −2 to just under 2.
- FFmpeg allows matrix parameters to be read at most twice per substream in one unit (its message says once; its counter rejects the third). Every test file reads them at most once per substream and unit, in the unit's first block. Which is the rule is **unverified**.
- A matrix error leaves no matrices in FFmpeg.

### Channel parameters

| Bits | Field | Condition | Meaning and range |
| --- | --- | --- | --- |
| 1 | FIR present | presence flag 0x08 | |
| var | FIR filter | FIR present | [Filter parameters](#filter-parameters), order at most 8 |
| 1 | IIR present | presence flag 0x04 | |
| var | IIR filter | IIR present | [Filter parameters](#filter-parameters), order at most 4 |
| 1 | Huffman offset present | presence flag 0x02 | |
| 15 | Huffman offset | Huffman offset present | s(15), added to every residual of the channel |
| 2 | codebook | always | 0: no Huffman code, 1 to 3: [codebooks](#huffman-codebooks) |
| 5 | LSB count | always | N: the bits of each residual below the Huffman-coded part, the quantizer step included; with a codebook, at most 24 and at least the channel's quantizer step size |

Rules after reading (each is a reason to treat the parameters as invalid):

- FIR order plus IIR order at most 8.
- Where both orders are above 0, their shifts must be equal; where only the IIR filter is in use, its shift is the filter shift.
- With a codebook, N above 24, or N below the quantizer step size (checked also when only the step size changed), is invalid. Without a codebook, FFmpeg checks neither.
- FFmpeg allows each filter of a channel to be read at most twice per unit, as for the matrices; every test file reads each at most once per unit.

### Filter parameters

| Bits | Field | Condition | Meaning and range |
| --- | --- | --- | --- |
| 4 | order | always | 0 to 8 (FIR) or 0 to 4 (IIR) |
| 4 | shift | order > 0 | right shift of the filter sum |
| 5 | coefficient bits | order > 0 | 1 to 16 |
| 3 | coefficient shift | order > 0 | coefficient bits + coefficient shift at most 16 |
| order × coefficient bits | coefficients | order > 0 | s(coefficient bits) each, times 2^(coefficient shift), for taps 1 to order |
| 1 | state present | order > 0 | must be 0 for a FIR filter |
| 4 | state bits | state present | |
| 4 | state shift | state present | |
| order × state bits | state values | state present | s(state bits) each, times 2^(state shift), or 0 for every tap where state bits is 0 |

- An order of 0 turns the filter off and carries nothing else.
- Coefficient k (from 1) multiplies the value k sample periods back.
- IIR state values replace the IIR history ([Prediction filters](#prediction-filters)): the first value is the most recent. FFmpeg does not check them. `luckynight.mlp` sends IIR state 2,553 times.

### Block data

For each sample period of the block, in order:

1. For each primitive matrix j with LSB bypass set, in matrix order: u(1), the bypassed LSB of matrix j for this sample.
2. For each coded channel from first to last: the residual.

A residual of a channel with codebook c, LSB count N and quantizer step size Q:

- L = N − Q raw bits.
- Where c is 1 to 3: a Huffman codeword from codebook c, giving an index i ([table](#huffman-codebooks)). A bit string that is no codeword is invalid.
- Where L > 0: u(L), the raw part r.

The residual's value, with H the Huffman offset:

- With a codebook: ((i − 7) · 2^L + r + H − D) · 2^Q, where D = 2^(L + 2 − c) where L + 2 − c ≥ 0, else 0. So D is 2^(L+1) for codebook 1, 2^L for codebook 2, 2^(L−1) for codebook 3 (for codebook 3, 0 when L is 0).
- Without a codebook: (r + H − 2^(L−1)) · 2^Q where L > 0, else H · 2^Q.
- Where L ≤ 0 no raw bits are read and r is 0 (L < 0 is possible only without a codebook, where N < Q).

The default after a restart header (no codebook, N = 24, Q = 0, H = 0) reads each residual as an unsigned 24-bit field minus 2^23 (offset binary, not two's complement): a raw field of 0x000001 gives −8,388,607.

### Huffman codebooks

Index i, its value before the offsets (i − 7), and its codeword in each codebook (`ff_mlp_huffman_tables`, `mlp.c`):

- Indices 0 to 6 (−7 to −1) have the same codewords in all three codebooks.
- The codewords starting with `1` cover four values (0 to +3) in codebook 1, two in codebook 2 and one in codebook 3; the codewords `011`, `0101`, `01001` and so on carry the values after them.

| i | i − 7 | Codebook 1 | Codebook 2 | Codebook 3 |
| --- | --- | --- | --- | --- |
| 0 | −7 | `000000001` | `000000001` | `000000001` |
| 1 | −6 | `00000001` | `00000001` | `00000001` |
| 2 | −5 | `0000001` | `0000001` | `0000001` |
| 3 | −4 | `000001` | `000001` | `000001` |
| 4 | −3 | `00001` | `00001` | `00001` |
| 5 | −2 | `0001` | `0001` | `0001` |
| 6 | −1 | `001` | `001` | `001` |
| 7 | 0 | `100` | `10` | `1` |
| 8 | +1 | `101` | `11` | `011` |
| 9 | +2 | `110` | `011` | `0101` |
| 10 | +3 | `111` | `0101` | `01001` |
| 11 | +4 | `011` | `01001` | `010001` |
| 12 | +5 | `0101` | `010001` | `0100001` |
| 13 | +6 | `01001` | `0100001` | `01000001` |
| 14 | +7 | `010001` | `01000001` | `010000001` |
| 15 | +8 | `0100001` | `010000001` | none |
| 16 | +9 | `01000001` | none | none |
| 17 | +10 | `010000001` | none | none |

- The longest codeword is 9 bits.
- Each codebook leaves exactly two 9-bit strings unused: `000000000` and `010000000`. Reading either is invalid.

### Block size check data

Where the restart header's block size check flag is set, each block's data is preceded by u(16), and followed by 8 more bits:

- The 16-bit value plus the bit position of that 16-bit field's first bit gives the position where the block's residuals must end. FFmpeg logs a mismatch and carries on.
- FFmpeg skips the 8 bits after the block; their meaning is unknown.
- FFmpeg asks for a sample file when it meets this flag, so no stream known to its authors sets it, and no test file does. **Unverified**; an analyzer reports the flag.

### Substream end

After the block with the last-block flag:

1. Zero or more padding bits up to the next multiple of 16 bits, counted from the start of the substream. FFmpeg does not check their value; its encoder writes zeros.
2. Where at least 32 bits remain: the end of stream marker, u(16) = 0xD234 (anything else makes the unit invalid), then a 16-bit word:
   - MLP: it must be 0xD234 too (the marker is 0xD234D234).
   - TrueHD: where bit 13 (0x2000) is set, bits 0 to 12 give the number of samples at the end of this unit, in this substream, that are not output. FFmpeg's encoder writes bits 13 to 15 set; bits 14 and 15 have no meaning in FFmpeg's decoder.
3. Where the directory's check data bit is set: exactly 16 bits must remain, the parity byte and the CRC-8.
4. The substream's end must be reached exactly. Anything else is a length mismatch that makes the unit invalid.

- Without check data, the substream ends right after the padding or the end of stream marker.
- Parity byte: the XOR of every byte of the substream before the parity byte, XORed with the parity byte, must be 0xA9.
- CRC-8: generator x^8 + x^6 + x^5 + x + 1 (0x63), most significant bit first; a standard CRC-8 with the register starting at 0x3C over all bytes before the parity byte but the last of them, then XORed with that last byte. Equivalently, the remainder without appended zero bits over all those bytes, with the register starting at 0xA2.
- FFmpeg only logs a failed parity byte or CRC-8.

## Checksums and check values

| Check | Covers | Generator, start | Compared with |
| --- | --- | --- | --- |
| Parity nibble | unit header and directory, not the major sync | XOR | nibbles XOR to 0xF |
| Major sync CRC-16 | the major sync but its CRC | 0x002D, 0, no appended zeros | its last 2 bytes, big endian |
| Restart header CRC | the restart header's bits but its CRC | 0x1D, 0, no appended zeros | the next 8 bits |
| Substream parity | the substream's bytes before the parity byte | XOR | XOR with the parity byte gives 0xA9 |
| Substream CRC-8 | the substream's bytes before the parity byte | 0x63, 0xA2 with no appended zeros (0x3C in the standard form) | the CRC byte |
| Lossless check | the decoded output since the previous restart header | XOR | the restart header's byte |

"No appended zeros" means: for each message bit, shift the register left by one, XOR the generator in where a bit fell out of the top, then XOR the message bit into bit 0. Its result over a message equals a standard (augmented) CRC over all but the last 8 or 16 bits, XORed with those last bits.

Check values computed by the author with a plain bitwise implementation, for the ASCII bytes `123456789`:

- standard CRC-16, generator 0x002D, start 0: 0x4FF7; the major sync form over the same 9 bytes: 0xC59E;
- standard CRC-8, generator 0x63, start 0x3C: 0x15; the substream form over the 9 bytes: 0xA4;
- standard CRC-8, generator 0x1D, start 0: 0x37.

The examples in [CRC-16](#crc-16) and [Example unit](#example-unit) come from real files and are the stronger test.

## Decoding

### State

Per substream, carried from unit to unit:

- the restart header's values, the presence flags and the decoding parameters;
- for each coded channel, the FIR history (the last 8 output values of the filter) and the IIR history (the last 4 error values);
- the noise generator register;
- the lossless check accumulator, and whether it is valid.

Per unit, per substream: the samples of its coded channels, the bypassed LSBs of each matrix, and the position in the unit.

### Residuals

Each block's residuals are read for every sample period first, then each coded channel is filtered over the block ([Prediction filters](#prediction-filters)). A block must not take the unit's sample count past its samples per channel (invalid otherwise).

### Prediction filters

For each coded channel and each sample n of a block, with F the FIR order, I the IIR order, a_k and b_k their coefficients, s the filter shift and Q the channel's quantizer step size:

- prediction p[n] = floor-shift right by s of (Σ k=1..F a_k · y[n − k] + Σ k=1..I b_k · e[n − k]);
- output y[n] = p[n] + residual[n], with the low Q bits cleared;
- error e[n] = y[n] − p[n].

Details:

- y is the filter's output, the value the matrices take; e is kept for the IIR filter.
- The histories of y and e are updated for every sample, also while an order is 0, and continue across blocks, units and restart headers.
- The sums need more than 32 bits (up to 8 taps of 16-bit coefficients times 24-bit values plus headroom); FFmpeg uses 64 bits. y is kept as a 32-bit value.
- With no filter the prediction is 0, and y is the residual with its low Q bits cleared.
- Arithmetic width: every stored value (residuals, y, the IIR error e, matrix outputs after the shift and mask, and output samples after the output shift) is a 32-bit two's complement value and wraps on overflow, as does the multiplication by 2^(output shift). Valid streams stay within 24 bits after the output shift. Without a codebook an LSB count up to 31 gives up to 31 raw bits, whose value can overflow 32 bits; that wraps as well.

### Noise generation

The register R holds 23 bits. "sext8(v)" is the 8-bit two's complement value of the low 8 bits of v.

Noise type 0 (two noise channels), for each output sample n of the unit:

- noise channel M + 1: sext8(R shifted right by 15) · 2^(noise shift);
- noise channel M + 2: sext8(R shifted right by 7) · 2^(noise shift);
- then t = bits 7 to 22 of R (16 bits), and R becomes (R · 2^16 XOR t XOR t · 2^5), keeping 23 bits.

Noise type 1 (noise table), once per unit, for k = 0 to P − 1 (P from the [rate table](#sampling-rate-and-unit-size)):

- t = bits 15 to 22 of R (8 bits);
- noise[k] = the noise table's entry t;
- R becomes (R · 2^8 XOR t XOR t · 2^5), keeping 23 bits.

- In both kinds R continues from unit to unit and restarts from the seed at each restart header.
- Type 0 generates as many values as the unit outputs (after any end of stream shortening); type 1 always P.

Noise table (`noise_table` in `mlpdec.c`), 256 signed 8-bit values, entry 0 first, row by row:

```text
  30,  51,  22,  54,   3,   7,  -4,  38,  14,  55,  46,  81,  22,  58,  -3,   2,
  52,  31,  -7,  51,  15,  44,  74,  30,  85, -17,  10,  33,  18,  80,  28,  62,
  10,  32,  23,  69,  72,  26,  35,  17,  73,  60,   8,  56,   2,   6,  -2,  -5,
  51,   4,  11,  50,  66,  76,  21,  44,  33,  47,   1,  26,  64,  48,  57,  40,
  38,  16, -10, -28,  92,  22, -18,  29, -10,   5, -13,  49,  19,  24,  70,  34,
  61,  48,  30,  14,  -6,  25,  58,  33,  42,  60,  67,  17,  54,  17,  22,  30,
  67,  44,  -9,  50, -11,  43,  40,  32,  59,  82,  13,  49, -14,  55,  60,  36,
  48,  49,  31,  47,  15,  12,   4,  65,   1,  23,  29,  39,  45,  -2,  84,  69,
   0,  72,  37,  57,  27,  41, -15, -16,  35,  31,  14,  61,  24,   0,  27,  24,
  16,  41,  55,  34,  53,   9,  56,  12,  25,  29,  53,   5,  20, -20,  -8,  20,
  13,  28,  -3,  78,  38,  16,  11,  62,  46,  29,  21,  24,  46,  65,  43, -23,
  89,  18,  74,  21,  38, -12,  19,  12, -19,   8,  15,  33,   4,  57,   9,  -8,
  36,  35,  26,  28,   7,  83,  63,  79,  75,  11,   3,  87,  37,  47,  34,  40,
  39,  19,  20,  42,  27,  34,  39,  77,  13,  42,  59,  64,  45,  -1,  32,  37,
  45,  -5,  53,  -6,   7,  36,  50,  23,   6,  32,   9, -21,  18,  71,  27,  52,
 -25,  31,  35,  42,  -1,  68,  63,  52,  26,  43,  66,  37,  41,  25,  40,  70,
```

### Rematrixing

Once per unit, after all its blocks, for the presentation of substream k ([Presentations and substreams](#presentations-and-substreams)):

- Inputs: matrix channels 0 to M, each the filter output y of the highest substream at or below k that codes that channel, and for noise type 0 the noise channels M + 1 and M + 2.
- The J primitive matrices are applied in order, each to every sample of the unit, in place: a later matrix sees the channel an earlier one replaced.

For matrix j with output channel d, coefficients c_i and, for noise type 1, matrix noise shift σ:

- sum = Σ over its input channels i of c_i · x[n][i];
- for noise type 1 where σ > 0: sum += noise[idx_n] · 2^(σ + 7), where idx_n = ((J − j) + n · (2·(J − j) + 1)) mod P, n counting the unit's samples from 0;
- x[n][d] = (floor-shift right by 14 of sum, with the low Q_d bits cleared) + β_j[n],

where Q_d is this substream's quantizer step size of channel d and β_j[n] the bypassed LSB of matrix j (0 for a matrix without LSB bypass).

- FFmpeg applies the matrices and their quantizer masks in force at the end of the unit to all its samples. In every test file matrices, output shifts and quantizer step sizes change only in a unit's first block, so a change inside a unit is **unverified**.
- The sums always need more than 32 bits: a coefficient of 1.0 is 2^14, a 24-bit sample up to 2^23, so one product reaches 2^37 and a sum of up to 10 terms about 41 bits, in valid streams too. Use a 64-bit accumulator; only the value after the 14-bit shift is kept, as 32 bits.

### Output

For each sample n and matrix channel m of the presentation:

- the output sample is x[n][m] · 2^(output shift of m), a 24-bit value; FFmpeg passes it on as the top 24 bits of a 32-bit sample, or, where the stream's bit depth is 16 bits or less, as its top 16 bits;
- it goes to position a_m of the presentation's channel list, where a_m is matrix channel m's channel assignment in the restart header.

The channel list:

- TrueHD: the channels of the presentation's assignment flags in flag order ([TrueHD channel flags](#truehd-channel-flags)). FFmpeg then moves each channel to its own native order, which for 5.1 is the same and for layouts with rear surrounds or heights is not, so a cross-check maps channels by name.
- MLP: the arrangement's channels in the order of the [arrangement table](#mlp-channel-arrangements). This is inferred from FFmpeg's reordering of arrangements 18 to 20 (it swaps its assignments where its native order differs from the table's); the test files are stereo only.
- Each matrix channel should go to a different position; FFmpeg does not check this.

### Lossless check

Per substream (per presentation) a 32-bit accumulator A:

- For each output sample n of the unit (after end of stream shortening) and each matrix channel m from 0 to M: A = A XOR ((output sample, low 24 bits) · 2^m), keeping 32 bits. The output sample is the one after the output shift; the order does not matter.
- FFmpeg actually walks the output positions and takes the matrix channel assigned to each (positions no assignment names default to matrix channel 0). This is the same for valid streams. Where two matrix channels name the same position, one matrix channel then enters twice and another not at all, so the check differs from the form above.
- At the substream's next restart header, if A is valid: fold A to 8 bits (XOR of its four bytes) and compare it with the header's lossless check byte. Then A = 0 and valid.
- A is invalid when decoding starts, after a unit with an end of stream marker in that substream, and (in FFmpeg) after a flush.

So a restart header's byte covers the output since the previous restart header of its substream. The first restart header seen, and the first after an end of stream marker, have nothing to be compared with, and the output after the last restart header is never checked.

FFmpeg compares the check only for the substream it outputs and only logs a mismatch. Every presentation's check held in the test files when computed as above.

### End of stream

- The samples a TrueHD end of stream marker drops are removed before rematrixing, noise generation and the lossless check of that substream.
- After a unit with an end of stream marker, FFmpeg needs a new major sync before it decodes again, and the lossless check accumulators of the decoded substreams are invalid.
- `ff51.thd` (FFmpeg's encoder, 48,624 samples) ends with a marker dropping 16 samples, in both substreams; the model and FFmpeg agree on the 48,624 samples.

### Presentations and substreams

What a substream does:

- Substream k codes channels first to last of its restart header, and outputs a presentation of M + 1 channels.
- Its matrices may take any channel 0 to M, including channels lower substreams code; the lower substreams' channels enter as their filter outputs, before the lower substreams' own matrices.
- So the substreams form a hierarchy: decoding the presentation of substream k needs substreams 0 to k read and filtered, and only substream k's matrices, noise, output shifts, assignment and lossless check.

In the test files:

- `TrueHD.raw` (Dolby): substream 0 codes channels 0 to 1 with M = 1 (the 2-channel presentation, noise type 0); substream 1 codes channels 2 to 5 with M = 5 and builds 5.1 from all six (noise type 1).
- FFmpeg's encoder does the same split for more than two channels and writes one substream for one or two.
- Both presentations' lossless checks hold in the Dolby file, which supports this model.

Substream info (byte 17 of the major sync; for MLP see [Format info: MLP](#format-info-mlp)) in TrueHD, as far as sources go:

- FFmpeg's decoder uses bit 7 for Atmos ([below](#dolby-atmos)), bits 2 to 3 and bits 4 to 6 in a rule that skips a substream whose channels overlap the previous one's ([Implementation choices](#ffmpegs-implementation-choices)).
- The values are 0x3C in the Dolby file and in FFmpeg's encoder for more than two channels, and 0x14 in FFmpeg's encoder for one or two.
- Inferred, **unverified**: bits 2 to 3 name the substreams the 6-channel presentation uses (bit 2 substream 0, bit 3 substream 1), bits 4 to 6 those of the 8-channel presentation (bit 4 substream 0 to bit 6 substream 2), and a presentation is output by the highest substream it names. 0x3C gives both presentations from substreams 0 and 1, 0x14 both from substream 0, which fits every file. Bits 0 and 1 are 0 in every file; their meaning is unknown.

Which channel list a TrueHD substream's assignment indexes:

- Inferred: substream 0 of a stream with more than one substream outputs the 2-channel presentation (left, right); the substream that outputs the 6-channel or 8-channel presentation uses that presentation's assignment from the major sync; a single substream uses the 8-channel assignment where it is not 0, else the 6-channel one.
- FFmpeg's own choice differs in places and depends on the channel count it already knows ([Implementation choices](#ffmpegs-implementation-choices)).

Substreams of one unit that decode different sample counts, and a changed channel count:

- FFmpeg outputs as many samples as the output substream decoded; the channels of lower substreams keep stale samples beyond their own count. Worth a finding.
- FFmpeg outputs nothing for a unit when the channel count it already knows differs from M + 1 of the output substream.

MLP:

- One or two substreams. With two, FFmpeg takes substream 0 as stereo and substream 1 as the arrangement's layout. No test file has two.

### Dolby Atmos

- FFmpeg names a TrueHD stream "Dolby TrueHD + Dolby Atmos" where it has four substreams and bit 7 of the substream info is set.
- FFmpeg decodes at most substreams 0 to 2 and never the fourth, which its comment says carries non-audio data for Atmos.
- The extended substream info (2 bits) is read and unused in 6.1.1; FFmpeg's comment ties it to the 16-channel presentation.
- `truehd_core` strips the fourth substream: it keeps three directory entries and their data, sets the substream count to at most 3, clears bit 7 of the substream info, the extended substream info and the extension flag, keeps a 28-byte major sync and recomputes its CRC and the check nibble. The fourth substream's data follows the third's in the unit.
- Whether the fourth substream's parity and CRC-8 follow the rules above, and what it and the extension hold, is **unknown**; no test file has Atmos.

## Limits

Limits the syntax itself sets (field widths, tables):

| Limit | Value |
| --- | --- |
| Unit length | 4 to 8,190 bytes (12-bit word count) |
| Substream count | 1 to 4 (4-bit field; 0 invalid) |
| Coded channel indexes | 0 to 7 in the restart header (4-bit fields) |
| Samples per unit and channel | 40, 80, 160 for the rates FFmpeg accepts (40 · 2^(rate code bits 0 to 2) in general) |
| Block size | 8 to the unit's samples per channel (9-bit field) |
| Filter orders | FIR at most 8, IIR at most 4, sum at most 8 |
| Output sample | 24 bits |

FFmpeg's own limits (a decoder that follows FFmpeg rejects more than the syntax does; whether the format itself allows more is not determined):

| Limit | MLP | TrueHD |
| --- | --- | --- |
| Substreams | 1 to 2 | 1 to 4 |
| Last matrix channel | 5 | 7 (above 5 only with noise type 1) |
| Primitive matrices | 6 | 8 |
| Coded channels per stream | 8 | 8 |
| Sampling rate | at most 192 kHz (the highest rate FFmpeg's sources say was seen in files) | same |

## Validity rules and damage

What FFmpeg does with each broken rule, and how an analyzer can treat it. "Unit invalid" means the unit cannot be read; "substream invalid" means the rest of that substream cannot be read in this unit. FFmpeg's reactions differ in whether it keeps the stream state; the last column is the treatment this description suggests, not a rule from a source.

| Rule broken | FFmpeg | Suggested treatment |
| --- | --- | --- |
| Unit length below 4 bytes or past the data | unit dropped | unit invalid; resynchronize |
| Major sync: too short, CRC, sync word, stream type | unit dropped, waits for a major sync | unit invalid |
| Major sync: bit depth, rate, group 2 longer or at another rate, substream count 0 or above the format's limit | unit dropped, waits | unit invalid (group 2 at another rate: unsupported, not damage) |
| Directory longer than the unit, extra word in MLP, non-restart bit wrong, end before the previous end | unit dropped, waits | unit invalid |
| Substream end past the unit | logged, clamped | unit invalid |
| Parity nibble | unit dropped, waits | unit invalid |
| Restart header: sync, MLP with noise type 1, M too large, coded channel range, channel assignment | rest of substream skipped | substream invalid until its next valid restart header |
| Restart header CRC | logged | finding; the header's values are suspect |
| Decoding parameters: block size, matrix count, output channel, fractional bits, filter order, coefficient bits, FIR state, order sum, shifts differ, LSB count, quantizer step above LSB count | rest of substream skipped | substream invalid until its next restart header |
| Negative output shift | asks for a sample, uses 0 | unsupported |
| Matrices or a filter read a third time in one unit | rest of substream skipped | substream invalid |
| Block past the unit's samples, unknown codeword, data running out, no bit left for the last-block flag | unit dropped, state kept | unit invalid; the substream's filter and lossless state cannot be trusted until its next restart header |
| End of stream marker wrong, MLP second word not 0xD234 | unit dropped, state kept | unit invalid |
| Check data present but not exactly 16 bits left, or the end not reached exactly | unit dropped, state kept | unit invalid |
| Substream parity byte, CRC-8 | logged | finding (a damaged substream) |
| Lossless check | logged | finding (`truehd.lossless-check-failed`) |
| A substream without a restart header since the last major sync (or since decoding started) | skipped, logged | not decodable yet; not damage at the start of a file |

Not checked by FFmpeg, and worth a finding rather than a failure: the signature, a last coded channel above M, a matrix channel no substream codes, two matrix channels with the same position, padding bits that are not zero, bytes after the last substream, and a unit whose blocks hold fewer samples than the unit size without an end of stream marker.

## Files and containers

**Raw files:**

- `.thd` (TrueHD) and `.mlp` (MLP) hold the access units back to back, with nothing before or after them.
- A file cut from a longer stream may start with units without a major sync, as `TrueHD.raw` does (13 units, 2,358 bytes); they cannot be decoded and are skipped up to the first major sync.
- FFmpeg's probe follows the length words from each major sync; each major sync reached that way scores 1, plus 1 per 8 units between it and the previous one, and it needs a score of 100 in its probe buffer. Its parser takes 0xF8726FBA and 0xF8726FBB alike when searching, then checks the parity nibble of each unit without a major sync and drops sync where it fails.
- FFmpeg's raw demuxer reads the rate code from byte 8 (TrueHD) or byte 9 (MLP), high nibble, of the file's first 10 bytes, so only where the file starts with a major sync unit (`TrueHD.raw` does not).

**MP4:**

- Sample entry `mlpa` (FFmpeg's tag table cites the MP4 registration authority for it and maps it to TrueHD only), one access unit per sample; FFmpeg's muxer marks units with a major sync as sync samples.
- FFmpeg's muxer writes the sample entry's sampling rate as a 32-bit integer (48,000 as 0x0000BB80) where an audio sample entry normally has a 16.16 fixed-point value, and 16 for the sample size. That is FFmpeg's behavior; whether the registration demands it is **unverified**.
- A `dmlp` box inside it, 10 bytes of payload: the 32-bit format info of the first major sync (unit bytes 8 to 11), the 15-bit peak data rate in the top bits of 16 (unit bytes 18 to 19 shifted left by one, so the variable-rate bit drops out), and 32 reserved bits (0). FFmpeg's demuxer requires at least 10 bytes and reads only the format info: rate, and the 8-channel assignment where not 0, else the 6-channel one.
- FFmpeg's muxer writes `dmlp` only for TrueHD and only from the first packet's major sync; TrueHD in MP4 is experimental in FFmpeg.
- Example from FFmpeg 6.1.1 (`ff51.mp4`): `dmlp` payload `0007800f 18fe 00000000`.

**Matroska:**

- `A_TRUEHD` and `A_MLP`, no CodecPrivate; the plan cites the Matroska codec mapping for frames being access units (not re-read for this document).
- In the mkvmerge 2.9.7 file every block holds exactly one access unit, and the track starts with units before the first major sync.
- FFmpeg runs its parser over Matroska TrueHD and MLP blocks, which would also split a block holding several units; no source read for this document says whether a block may. An analyzer can walk the units inside a block by their length words and report a block that does not end on a unit boundary.

**Not covered:** TrueHD with an interleaved AC-3 core as on Blu-ray, and DVD-Audio's packing of MLP.

## FFmpeg's implementation choices

These are choices of FFmpeg's decoder, not rules of the format; a cross-check must account for them.

- **MLP or TrueHD:** FFmpeg picks its MLP or TrueHD decoder from the container or demuxer, applies that format's limits and rejects a major sync of the other stream type. The stream type in the major sync is the format's own signal.
- **Which presentation it outputs:** the highest of substreams 0 to 2, or with `-downmix` the lowest substream whose channel list contains every requested channel. It reads only substreams up to that one and compares only that one's lossless check.
- **Skipped substreams:** which substreams below the output substream FFmpeg reads at all. A skipped substream's filter histories are left unread. The observed rules:

  | Condition | Result |
  | --- | --- |
  | Output has 6 channels and substream info bits 2 to 3 are not 3, or output has 8 channels and bits 4 to 6 are none of 7, 6 and 3; and the substream is neither the first nor the output one; and its first coded channel is at or below the previous substream's last coded channel | substream skipped |
  | Any other substream below the output one whose coded channels overlap those of the output substream | substream skipped |
  | Neither condition | substream read |

- **Channel lists of TrueHD substreams:** which assignment each substream's channel positions index, as FFmpeg sets them from the major sync and from the channel count its parser or the container already told it, so the result can depend on what was read first:

  | Substreams | Known channel count | Substream 0 | Substream 1 | Substream 2 |
  | --- | --- | --- | --- | --- |
  | 1 | 1, and both assignments hold 1 channel | mono | | |
  | 1 | 2, and both assignments hold 2 channels | stereo | | |
  | 1 | more than 2 | 8-channel assignment | | |
  | 2 | 2 or fewer | stereo | 6-channel assignment | |
  | 2 | more than 2 | stereo | 8-channel assignment | |
  | 3 or more | any | stereo | 6-channel assignment | 8-channel assignment, or the 6-channel one where it is 0 |

  Other combinations (one substream with an unknown count) give substream 0 no list from these rules.
- **Its own channel order:** it reorders TrueHD channels to its native order, and for MLP arrangements 18 to 20 swaps assignments.
- **Output format:** 32-bit samples with the 24-bit value in the top bits, or 16-bit where the bit depth is at most 16; TrueHD is always reported as 24-bit.
- **Logging instead of rejecting:** restart header CRC, substream parity and CRC-8, lossless check, block size check mismatch; an end past the unit is clamped.
- **Accepting twice:** matrices and each filter may be read twice per substream and unit.
- **Lossless check marker:** it marks an invalid accumulator with the value 0xFFFFFFFF, so a real accumulator of that value is not compared.
- **State after errors:** some errors drop the stream state until the next major sync, others keep it; after an end of stream marker it waits for a major sync.
- **Unsupported, not invalid:** more than 4 substreams, M above 5 with noise type 0, negative output shifts, MLP channel groups at different rates, the block size check data.
- **Parser and probe:** its parser does not check the parity of units with a major sync (the decoder does); its probe needs a chain score of 100 (see [Files and containers](#files-and-containers)).
- **Filter state at a restart header:** kept, not reset; zero at the start. No test file can tell the difference ([Observations](#observations-on-the-test-files)).
- **Restart header CRC position:** computed from bit 2 of the substream, which assumes the header is in the first block.

## Observations on the test files

Observed behavior of the encoders behind Encspect's test files, not rules; none of them is a detection rule.

- **Restart interval:** a major sync and restart headers every 16 units in the Dolby file (640 samples at 48 kHz); every 8 or 9 units in both MLP files; every 16 units in FFmpeg's encoder.
- **First block after a restart header:** in all real files and FFmpeg's encoder, a unit with a restart header has two blocks: 8 samples without any prediction filter, then the rest of the unit (32 or, at 96 kHz, 72) with filters; other units have one block of the whole unit. So the 8 unfiltered samples refill the FIR history, and a decoder that resets or keeps the history at a restart header gives the same output on these files (the model was run both ways over all of `TrueHD.raw` and `luckynight.mlp`).
- **Check data:** every substream of every unit carries the parity byte and CRC-8 in all test files, the Dolby file included.
- **Extra words:** the Dolby file has them in 302 of 19,407 units: every 128 units in substream 1 and two units later in substream 0, with values such as 0xFE70 and 0xFB70; FFmpeg's encoder never writes them.
- **Timing:** in the MLP files and FFmpeg's encoder the input timing starts at minus one unit's samples (0xFFD8 for 40) and grows by one unit's samples per unit, and a restart header's output timing is its unit's input timing plus one unit's samples. In the Dolby file the input timing grows by 29 to 51 per unit, and the output timing is 41 to 51 ahead of it.
- **Noise:** the Dolby file's 6-channel substream uses noise type 1 with matrix noise shifts; no file gives a noise channel a nonzero matrix coefficient or uses a noise shift above 0.
- **Restart header maxima:** in every test file the 4-bit field after the noise seed equals the largest output shift and the next 5-bit field the largest LSB count used up to the next restart header. The two 5-bit fields after them are always equal and at least the two's complement width of every output sample in that stretch: equal in most stretches, one more in the rest (56 of 497 stretches in the first 4,000 units of `440hz.mlp`, 1 of 994 in the first 8,000 of `luckynight.mlp`). Values seen: 19 to 23 in the Dolby file, 22 to 23 and 16 to 24 in the MLP files.
- **Not used by any file:** LSB bypass, presence flags other than 0xFF, the block size check, three or four substreams, the major sync extension, MLP with two substreams.

## Not determined

- The meaning of the reserved directory bit, the extra word, the restart header's 16 skipped bits (its 19 skipped bits fit the reading under [Observations](#observations-on-the-test-files), which no specification confirms), the flags word, the multichannel type bits, the channel meaning fields and the extension.
- The meaning of TrueHD substream info bits 0 to 1, and whether bits 2 to 6 work as inferred above; for MLP, whether the encoder's flag names for the byte are the format's.
- Which TrueHD presentation each substream outputs in a three-substream stream and how a single substream chooses between the 6-channel and 8-channel assignment; no test file has three substreams or an 8-channel assignment that differs from the 6-channel one.
- Whether matrix, output shift and quantizer changes inside a unit apply from their block or to the whole unit.
- Whether filter histories reset at a restart header.
- Whether a Matroska block may hold more than one access unit, and the `mlpa` sample entry's rules beyond FFmpeg's muxer.
- Anything about Dolby Atmos beyond FFmpeg's signaling rule: the fourth substream, its checks, the extended substream info, the extension.
- LSB bypass and the block size check are described from FFmpeg alone; no file exercises them. Noise type 0 runs in every MLP file and in the Dolby file's substream 0, but no file has a nonzero noise coefficient or noise shift, so the generated noise values never reach the output and are unverified.
- The channel order of MLP arrangements 18 to 20 (inferred from FFmpeg's reordering) and of every MLP arrangement with more than two channels; all test files are stereo.
- Whether a restart header may start at a bit other than 2 of its substream; the advice under [Restart header CRC](#restart-header-crc) to use the actual first bit is unverified, as every header in the test files starts at bit 2.
- What a later major sync that changes stream parameters (substream count, rate, assignments) means for the state. FFmpeg takes the new parameters and keeps its accumulators.
- The format rule behind FFmpeg's limit of two matrix or filter reads per unit.
