# TAK: sample reconstruction

**Status:** written by an agent that read FFmpeg 6.1.1's LGPL TAK decoder (listed under [Sources](#sources); the release `scripts/fixtures/ffmpeg_sources.py` pins, archive SHA-256 `8684f4b00f94b85461884c3719382f1261f0d9eb3d59640a1f4ac0873616f968`) and checked this text against the repository's fixtures with an independent model written from this text alone. It contains no code. Whoever implements or changes the TAK decoder must not read FFmpeg's `tak.c`, `takdec.c`, `takdsp.c` or any other TAK decoder; FFmpeg is a black box ([decoders.md](decoders.md), decision 10), and TAK's own tools run only on Windows, in the fixture workflow.

The container (metadata objects, stream info, frame headers, the two CRC-24s of a frame, the frame walk) is in [tak.md](../formats/tak.md) and [tak-optimfrog-headers.md](tak-optimfrog-headers.md); the parser reads it already. This text adds what lies between a frame header's CRC-24 and the frame's data CRC-24: the coded samples.

Names and structure are this document's own, not the source's. Statements marked **Unsure** could not be settled from the source.

## Sources

- **Read (FFmpeg 6.1.1):** `libavcodec/takdec.c` (the frame data, the residual codes and their table, the prediction filters, the decorrelation), `libavcodec/takdsp.c` and its x86 counterpart (four of the decorrelation modes), `libavcodec/tak.c` and `tak.h` (stream info and frame header as FFmpeg reads them), `libavcodec/tak_parser.c` (how FFmpeg splits frames) and `libavformat/takdec.c` (the metadata walk), with `libavcodec/unary.h` for the unary codes. They were extracted from the pinned archive; they are not (yet) in `FILES` of `ffmpeg_sources.py`.
- **Not read:** the TAK SDK and the TAK applications (closed source, black boxes only, in the fixture workflow).
- **Checked against** (see [Checks against the fixtures](#11-checks-against-the-fixtures)): the 33 encoder-written fixtures in `tests/fixtures/tak` and their inputs, which `scripts/fixtures/tak_cases.py` writes again byte for byte; the two real files of the FFmpeg sample archive that `check_tak_samples.py` fetches (`lostchord-tak_2_2_0.tak` and `lostchord-tak_1_0.tak`); frames crafted for the modes no file uses; and FFmpeg 6.1.1 (`ffmpeg`, Ubuntu 24.04's build) as a black box.

## 0. Conventions

- **Bit order:** bytes are read in file order, and within a byte from the **least significant bit** (bit 0) to bit 7. An n-bit field read this way has the first bit read as its least significant bit. This is the order the frame header is in: its sync word, the bytes `FF A0`, reads as the 16-bit value 0xA0FF.
- `u(n)`: an n-bit unsigned field in that order. `s(n)`: an n-bit two's complement field (the last bit read is the sign). `u(0)` is 0.
- **flag4:** one bit; if it is 0 the value is 0, else the value is u(4) + 1. So flag4 gives 0 to 16 and costs 1 or 5 bits.
- **unary(L):** count the **0** bits before a **1** bit, reading at most L bits: the 1 bit that ends the count is read; after L zeros the count is L and no further bit is read. So unary(6) reads 1 to 6 bits and gives 0 to 6.
- **Arithmetic:** all sample, filter and coefficient arithmetic is **32-bit two's complement with wraparound**, products too. `>>` on a signed value is an **arithmetic** shift (rounds toward minus infinity); `<<` and multiplication by 2^k wrap.
- **int16(v):** the low 16 bits of v, read as a signed 16-bit value. It matters in two places (sections 5 and 6), where the history of a filter is held at 16 bits.
- **clip14(v):** v limited to [−8192, 8191].
- n is the frame's samples per channel, C the channels, B the bits per sample of the stream info.

## 1. What the decoder takes from the stream

From the stream info (the metadata object, which info frames restate):

- **Codec:** 2 (mono and stereo, "Integer 24 bit (TAK 2.0)") and 4 (multichannel, "Integer 24 bit MC (TAK 2.2)") are the ones described here. Codecs 0, 1 and 3 are refused by the source, so their coding is not known. Codec 0 (TAK 1.0) is coded differently: read with these rules, the first frame of `lostchord-tak_1_0.tak` gives a residual mode above 50.
- **Data type** must be 0.
- **Bits per sample B:** the source decodes B = 8, 16 and 24 only and refuses the rest. The coding below only uses B as a field width, so it reads any B, but no file of another width was available (**Unsure**). The valid bits per sample of the extension are not used.
- **Channels C:** codec 2 allows 1 or 2; codec 4 allows up to 16 by its fields, and the source refuses more than 6 (**Unsure** above 6).
- **Sample rate:** sets two lengths (section 1.1).
- **Frame length** (from the frame size type and the rate, as the parser works it out) and, for the frame flagged as the last, the sample count its header states (14 bits, plus 1). n is that count for the last frame and the frame length for every other.

The source takes these values from the stream info of the most recent info frame and keeps them for the frames that follow; it does not read the metadata object for decoding. A decoder that takes them from the metadata object decodes the same where the two agree (all fixtures; **Unsure** what TAK does when they disagree).

The coded data starts at the first byte after the frame header's CRC-24 (the header always ends on a byte boundary: it is 5 or 7 bytes, and an info frame's stream info is padded to a byte).

### 1.1 Two lengths from the sample rate

Let R be the sample rate and G = ceil(R / 512) rounded **up** to a multiple of 4 (computed as (R + 511) >> 9, then up to a multiple of 4).

- **Partition length P** (residual coding, section 3): G × 8 for R < 11025, G × 4 for 11025 ≤ R < 22050, G × 2 for 22050 ≤ R < 44100, G for R ≥ 44100.
- **Subframe unit U** (subframe boundaries, section 4): 2G.

| R (Hz) | G | P | U |
|---|---|---|---|
| 8000 | 16 | 128 | 32 |
| 11025 | 24 | 96 | 48 |
| 22050 | 44 | 88 | 88 |
| 32000 | 64 | 128 | 128 |
| 44100 | 88 | 88 | 176 |
| 48000 | 96 | 96 | 192 |
| 96000 | 188 | 188 | 376 |
| 192000 | 376 | 376 | 752 |

## 2. Frame data layout

The coded data of a frame, in order:

1. **Short frame (n < 16):** for each channel in order 0…C−1, n values s(B), which are the samples. Nothing else; go to 4.
2. **Codec 2:** the channel data (section 4) of channel 0, then, for stereo, that of channel 1, then the stereo decorrelation fields (section 6.1).
3. **Codec 4:** the channel table (section 6.2), then for each of its entries the channel data of one or two channels and that entry's decorrelation fields.
4. Zero to seven bits up to the next byte boundary. The fixtures have 0 there; the source ignores them.
5. The 3-byte data CRC-24 (in [tak.md](../formats/tak.md)), which ends the frame.

After the channel data and the decorrelation, every channel is finished (section 7). Frames carry no state into the next: every value below is reset or read again in each frame, so any frame decodes on its own (the source keeps only the stream values of section 1).

## 3. Residual coding

A **residual block** of L values (L may be 0) is read as follows.

**Partitions.** One bit.

- **0:** one mode m = u(6) for all L values.
- **1:** the block is split into partitions:
  - Let Q = floor(L / P) and r = L − Q·P. If r < floor(P / 2), the last partition takes r + P values and there are Q partitions; else there are Q + 1 partitions and the last takes r values. Every other partition takes P values.
  - The partition count must be 2 to 128, else the frame is damaged.
  - The first partition's mode is u(6). Each further partition's mode follows from the one before it, by c = unary(6):
    - c = 0: the same mode; c = 1: one less; c = 2: one more;
    - c = 3, 4 or 5: one more bit g; the mode changes by c − 1 when g = 0 and by −(c − 1) when g = 1 (so ±2, ±3, ±4);
    - c = 6: a new mode u(6).
  - All modes are read first, then the values of every partition in order.

**Modes.** A mode is 0 to 50; any other value (above 50, or below 0 after a change) is damage.

- **Mode 0:** every value of the partition is 0, and no bits are read.
- **Modes 1 to 50:** each value is one code, read with the mode's row (k, t, s, a, b) of the [table](#residual-code-parameters):
  1. v = u(k).
  2. If v < t, the code is v.
  3. Else read one bit. If it is 0, the code is v. If it is 1, let w = v + 2^k, and:
     - if w < a, the code is w − t;
     - else read d = unary(9). If d < 9, the code is w + s·d − t.
     - If d = 9: read e = u(3); if e = 7, add u(5) to it (e is then 7 to 38, and e > 29 is damage). If e = 0, the code is w + b; else the code is w + b + s·(u(e) + 1).
  4. The value is code / 2 for an even code and −(code + 1) / 2 for an odd one: 0, 1, 2, 3, 4 → 0, −1, 1, −2, 2.

The codes are unsigned 32-bit with wraparound. Values do not depend on each other within a partition, so partitions with the same mode can be read as one run.

## 4. Channel data

The channel data of one channel, in order:

1. **Wasted bits** w = flag4. w ≥ B is damage.
2. **First sample** s(B − w): sample 0 of the channel, before the shift of section 7.
3. **Difference order** o = u(2): 0 to 3, applied to the whole channel in section 7.
4. **Subframe count** N = u(3) + 1 (1 to 8).
5. If N > 1, N − 1 **boundaries** u(6) each, b₁ … b_{N−1}, with b₀ = 0. Subframe i (i < N−1) has (b_{i+1} − b_i)·U samples, which must be at least 1 (so the boundaries rise strictly). The last subframe has the rest of the n − 1 samples, which must be at least 1.
6. The **subframes** (section 5), in order. Together they hold samples 1 … n−1 of the channel; sample 0 is the first sample above.

The values the subframes produce are not yet samples: they are still differenced with the channel's difference order, and neither decorrelated (section 6) nor shifted by w (section 7).

## 5. Subframes

A subframe of length L starts with one bit.

**0: no prediction.** A residual block (section 3) of L values, which are the subframe's values.

**1: prediction.** In order:

1. **Order** N = the order table entry for u(4): 4, 8, 12, 16, 24, 32, 48, 64, 80, 96, 128, 160, 192, 224, 256 for 0 to 14, and 0 for 15 (**Unsure**: order 0 never occurs in the fixtures; the source then predicts 0 for every value, so each value is the negated residual).
2. **Warm-up.** For every subframe but the first of its channel, one bit: **continue**.
   - **Continue = 1:** the N values before the subframe (the end of the previous subframe of the same channel) are the history. N must not exceed the previous subframe's length. All L values of the subframe are then predicted.
   - **Otherwise** (continue = 0, or the first subframe): N must not exceed L. A warm-up difference order h = u(2) (3 is damage), then a residual block of N values, integrated with order h (section 7.1, over these N values alone). They are the first N values of the subframe, and the remaining L − N are predicted.
3. **Shift** ds = flag4.
4. **Coefficient width** z = 6 + u(1).
5. **Precision** f: one bit; 0 gives f = 10, 1 gives f = 10 − (u(3) + 1), and f < 3 is damage. So f is 3 to 10.
6. **Parameters** k₀ … k_{N−1}, each a signed number in units of 1/512:
   - k₀ = s(10), k₁ = s(10);
   - k₂ = s(z)·2^(10−z), k₃ = s(z)·2^(10−z);
   - if N > 4: one bit y, and z' = z − y. Then for each group of four, k₄…k₇, k₈…k₁₁ and so on: a width q = z' − u(2) read at the start of the group, then each of the four is s(q)·2^(10−z) (scaled by z, not by q).
7. **Residuals:** a residual block of the predicted count (L, or L − N).

**From parameters to coefficients.** Build t₀ … t_{N−1}:

- Start with t₀ = 64·k₀.
- For i = 1 … N−1: replace every t_j, j = 0 … i−1, by t_j + ((k_i·t_{i−1−j} + 256) >> 9), using the values of t from before this step for all j at once; then t_i = 64·k_i.
- With S = 15 − f, the coefficient for the value d places back (d = 1 … N) is c_d = int16(−((t_{d−1} + 2^(S−1)) >> S)).

Everything here is 32-bit with wraparound and arithmetic shifts. The result is held at 16 bits (**Unsure** whether TAK keeps more; the fixtures never reach the limit, see the checks).

**Prediction.** Keep a history H of values at 16 bits: H(x) = int16(x >> ds). For each predicted position p, with x_{p−1}, x_{p−2}, … the values before it (the history values of the warm-up, or of the previous subframe, then the subframe's own):

- A = 2^(f−1) + Σ_{d=1}^{N} c_d · H(x_{p−d}) (32-bit, wraparound).
- x_p = clip14(A >> f) · 2^ds − e_p, where e_p is the residual. Note the subtraction.

## 6. Decorrelation

Decorrelation works on two channels' values (after the subframes, before section 7) at sample positions 1 … n−1. **Sample 0 of every channel is never changed by it.** Call the two channels X and Y in the roles each mode gives them.

**The modes:**

| Mode | Effect, for each position 1 … n−1 |
|---|---|
| none | nothing |
| add | Y ← X + Y |
| subtract | X ← Y − X |
| mid/side | a = X − (Y >> 1); X ← a; Y ← a + Y |
| scaled | read sh = flag4 and g = s(10) first; X ← (((g·(Y >> sh) + 128) >> 8) · 2^sh) − X |
| filter | parameters and steps below |

**Filter mode.** Requires n − 1 ≥ 256 (else damage). Fields: sh = flag4; order M = 8 when u(1) = 0, 16 when it is 1; a head flag u(1); a tail flag u(1); then M coefficients g₀ … g_{M−1} in groups of four, each group starting with q = 14 − u(3) (7 to 14), followed by four values s(q).

Number the positions 0 … K−1 with K = n − 1 (position j is sample j + 1), and let h = M/2.

- **Head:** positions 0 … h−1: X ← X + Y if the head flag is 1, else unchanged.
- **Middle:** positions j = h … K−h: A = 512 + Σ_{i=0}^{M−1} g_i · int16(Y_{j−h+i} >> sh); X_j ← clip14(A >> 10)·2^sh − X_j. The window of Y runs from h before to h−1 after j, and Y itself is not changed.
- **Tail:** positions K−h+1 … K−1: X ← X + Y if the tail flag is 1, else unchanged.

### 6.1 Codec 2

- Mono: no decorrelation fields.
- Stereo, after both channels' data: one bit; if it is 1, a u(6) follows that the source reads and ignores (**Unsure** what it means; every fixture has 0). Then the mode u(3), and the mode's own fields where it has any:

| u(3) | Mode | X | Y |
|---|---|---|---|
| 0 | none | | |
| 1 | add | channel 0 | channel 1 |
| 2 | subtract | channel 0 | channel 1 |
| 3 | mid/side | channel 0 | channel 1 |
| 4 | scaled | channel 1 | channel 0 |
| 5 | scaled | channel 0 | channel 1 |
| 6 | filter | channel 1 | channel 0 |
| 7 | filter | channel 0 | channel 1 |

In words: 1 codes channel 1 as its difference from channel 0; 2 codes channel 0 as channel 1 minus channel 0; 3 codes channel 0 as the mid (rounded down) and channel 1 as channel 1 minus channel 0; 4 to 7 predict one channel from the other.

### 6.2 Codec 4: the channel table

One bit.

- **0:** C entries; entry i is channel i alone. Channels are read in order 0 … C−1 with no decorrelation.
- **1:** E = u(4) + 1 entries (E ≤ C). Each entry: a channel number m = u(4) (m < C, not used by an earlier entry), then one bit **paired**; if it is 1, a type u(2) and a partner channel number r = u(4) (r < C). Then:
  - type 1: r must differ from m and not be used yet; r then counts as used;
  - types 0, 2, 3: r must already be used (decoded by an earlier entry).
  - m counts as used.

All entries are read before any channel data. Then for each entry in order:

1. If paired with type 1: the channel data of r.
2. The channel data of m.
3. If paired, the decorrelation, with its fields read here:

| Type | Mode | X | Y |
|---|---|---|---|
| 0 | add | r | m |
| 1 | mid/side | r | m |
| 2 | scaled | m | r |
| 3 | filter | m | r |

The partner used by types 0, 2 and 3 holds whatever it holds at that point (its values after its own decorrelation, if it had one).

**Unsure:** a table that leaves channels out (E < C) leaves them undecoded; the source then outputs whatever its buffers held. The two 6-channel fixtures send a table in every frame, listing all six channels, none paired. A decoder treats a table that leaves channels out as damage.

## 7. Finishing a channel

After all channels are read and decorrelated, for each channel:

1. Integrate its n values with its difference order o (section 7.1).
2. Multiply every value by 2^w (the channel's wasted bits).

The result is the channel's PCM: a signed B-bit value. A short frame (n < 16) skips both steps.

### 7.1 Integration

Integration of order o turns a sequence c₀ … c_{L−1} into y₀ … y_{L−1}. Element j < o carries the j-th difference at j, and the rest the o-th difference. Nothing happens for L < 2 or o = 0.

- **Order 1:** y₀ = c₀; y_i = y_{i−1} + c_i.
- **Order 2:** y₀ = c₀; D = c₁, y₁ = y₀ + D; for i ≥ 2: D ← D + c_i, y_i = y_{i−1} + D.
- **Order 3:** as order 2 for i ≤ 1; then E = c₂, D ← D + E, y₂ = y₁ + D; for i ≥ 3: E ← E + c_i, D ← D + E, y_i = y_{i−1} + D.

All 32-bit with wraparound. The same rule serves the warm-up values of section 5 (orders 0 to 2).

## 8. Output

- **B = 16 and 24:** the value is the sample.
- **B = 8:** the values are signed, centered on 0; the WAVE byte is value + 128.
- A value outside the signed B-bit range is damage (the source writes the low bits without a check).
- Samples are interleaved per position in channel order for a WAVE file. The channel order is the stream's (the speaker assignment or the WAVE header's mask say what each is).

## 9. End of frame and checks

- After the last field, skip to the byte boundary; the next 3 bytes are the data CRC-24, and the frame ends there. A frame whose coded data ends before or after the 3 bytes that precede the next frame (or the declared end) is damaged; the source only notes it.
- **The checks a frame carries** are the header CRC-24 and the data CRC-24, both over coded bytes ([tak.md](../formats/tak.md)). Neither covers the decoded audio. The source compares them only when asked to (its CRC check option) and, with that, still outputs the frame unless told to stop at errors.
- **The stream's MD5** (metadata object 6, when the encoder writes one with `-md5`) is the MD5 of the PCM as a WAVE file's data chunk holds it: all samples, interleaved, little-endian, B/8 bytes each, 8-bit as unsigned bytes. The source never compares it. (Checked on `external-md5.tak`, the one fixture with it.)
- **Damage** that the source refuses a frame for (it then outputs nothing for the frame and goes on with the next, which decodes on its own):
  - a frame header with the metadata flag set, a codec other than 2 or 4, a data type other than 0, codec 2 with more than 2 channels, more than 6 channels, B other than 8, 16 or 24, a frame length that is not valid for the rate;
  - wasted bits w ≥ B; subframe lengths below 1; an order above the subframe's length, or, for a continued subframe, above the previous subframe's length; a warm-up difference order of 3; a precision f below 3;
  - a residual block longer than n; a partition count outside 2 to 128; a mode outside 0 to 50; an escape width e above 29;
  - the filter mode with n − 1 < 256;
  - in the channel table: an entry count above C, a channel number of C or more, a channel used twice, a type 1 partner equal to its channel or used already, a partner of type 0, 2 or 3 not used yet.
- **The source does not check:** fields that run past the frame's data (it notes the overread only after decoding, and outputs the frame), padding bits that are not zero, a sample outside the B-bit range, history values that do not fit 16 bits. A decoder reports the first as damage and the others as it sees fit.

## 10. Tables

### Residual code parameters

Format data from FFmpeg 6.1.1's `libavcodec/takdec.c`; the rows follow the pattern below, which the source does not state. Mode m uses row m.

| m | k | t | s | a | b |
|---|---|---|---|---|---|
| 1 | 1 | 1 | 1 | 3 | 8 |
| 2 | 2 | 3 | 1 | 7 | 6 |
| 3 | 3 | 5 | 2 | 14 | 13 |
| 4 | 3 | 3 | 3 | 13 | 24 |
| 5 | 4 | 11 | 4 | 28 | 25 |
| 6 | 4 | 6 | 6 | 26 | 48 |
| 7 | 5 | 22 | 8 | 56 | 50 |
| 8 | 5 | 12 | 12 | 52 | 96 |
| 9 | 6 | 44 | 16 | 112 | 100 |
| 10 | 6 | 24 | 24 | 104 | 192 |
| 11 | 7 | 88 | 32 | 224 | 200 |
| 12 | 7 | 48 | 48 | 208 | 384 |
| 13 | 8 | 176 | 64 | 448 | 400 |
| 14 | 8 | 96 | 96 | 416 | 768 |
| 15 | 9 | 352 | 128 | 896 | 800 |
| 16 | 9 | 192 | 192 | 832 | 1536 |
| 17 | 10 | 704 | 256 | 1792 | 1600 |
| 18 | 10 | 384 | 384 | 1664 | 3072 |
| 19 | 11 | 1408 | 512 | 3584 | 3200 |
| 20 | 11 | 768 | 768 | 3328 | 6144 |
| 21 | 12 | 2816 | 1024 | 7168 | 6400 |
| 22 | 12 | 1536 | 1536 | 6656 | 12288 |
| 23 | 13 | 5632 | 2048 | 14336 | 12800 |
| 24 | 13 | 3072 | 3072 | 13312 | 24576 |
| 25 | 14 | 11264 | 4096 | 28672 | 25600 |
| 26 | 14 | 6144 | 6144 | 26624 | 49152 |
| 27 | 15 | 22528 | 8192 | 57344 | 51200 |
| 28 | 15 | 12288 | 12288 | 53248 | 98304 |
| 29 | 16 | 45056 | 16384 | 114688 | 102400 |
| 30 | 16 | 24576 | 24576 | 106496 | 196608 |
| 31 | 17 | 90112 | 32768 | 229376 | 204800 |
| 32 | 17 | 49152 | 49152 | 212992 | 393216 |
| 33 | 18 | 180224 | 65536 | 458752 | 409600 |
| 34 | 18 | 98304 | 98304 | 425984 | 786432 |
| 35 | 19 | 360448 | 131072 | 917504 | 819200 |
| 36 | 19 | 196608 | 196608 | 851968 | 1572864 |
| 37 | 20 | 720896 | 262144 | 1835008 | 1638400 |
| 38 | 20 | 393216 | 393216 | 1703936 | 3145728 |
| 39 | 21 | 1441792 | 524288 | 3670016 | 3276800 |
| 40 | 21 | 786432 | 786432 | 3407872 | 6291456 |
| 41 | 22 | 2883584 | 1048576 | 7340032 | 6553600 |
| 42 | 22 | 1572864 | 1572864 | 6815744 | 12582912 |
| 43 | 23 | 5767168 | 2097152 | 14680064 | 13107200 |
| 44 | 23 | 3145728 | 3145728 | 13631488 | 25165824 |
| 45 | 24 | 11534336 | 4194304 | 29360128 | 26214400 |
| 46 | 24 | 6291456 | 6291456 | 27262976 | 50331648 |
| 47 | 25 | 23068672 | 8388608 | 58720256 | 52428800 |
| 48 | 25 | 12582912 | 12582912 | 54525952 | 100663296 |
| 49 | 26 | 46137344 | 16777216 | 117440512 | 104857600 |
| 50 | 26 | 25165824 | 25165824 | 109051904 | 201326592 |

The pattern, as a check of a transcription: for odd m = 2j + 1 with j ≥ 2, k = j + 2, t = 11·2^(j−2), s = 2^j, a = 7·2^j, b = 25·2^(j−2); for even m = 2j + 2 with j ≥ 1, k = j + 2, t = s = 3·2^(j−1), a = 13·2^(j−1), b = 3·2^(j+2). Rows 1 to 3 are their own.

### Prediction orders

| u(4) | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12 | 13 | 14 | 15 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| order | 4 | 8 | 12 | 16 | 24 | 32 | 48 | 64 | 80 | 96 | 128 | 160 | 192 | 224 | 256 | 0 |

Also from `takdec.c`.

## 11. Checks against the fixtures

A throwaway model written from this text alone (not committed) gave these results. It reads the container itself, minimally, and stops at the first damaged frame.

- **All 33 encoder-written fixtures (`external-*`) decode exactly.** For each, every frame's coded data ends exactly 3 bytes before the next frame (or the end of the audio), both CRC-24s hold, and the samples are the input's:
  - the 31 with a wave data object: the stored WAV header, the decoded PCM and the stored footer together have the SHA-256 of the WAV file Takc was given (the provenance's `input.sha256`), the lossyWAV file `external-lossywav-fsl512` included;
  - every one but the lossyWAV file: the PCM equals the data chunk of the input `tak_cases.py` writes (`external-wm0` and `external-pipe-ihs`, which have no wave data, are checked this way only);
  - `external-md5`: the MD5 of the PCM equals the MD5 object;
  - all 33: the PCM equals FFmpeg's (`-f u8`, `s16le` or `s24le`).
- They cover 8, 16 and 24 bits, 1, 2 and 6 channels, 44.1, 48, 96 and 192 kHz, frames of 512 to 11,025 samples, codec 2 and codec 4 (with the channel table), prediction orders 4 to 160 with continued subframes, partitioned residual blocks with the long escape, wasted bits of 1 and 2 (lossyWAV), difference orders 1 and 2, and the stereo modes none, add, subtract and mid/side.
- **`lostchord-tak_2_2_0.tak`** (mono, TAK 2.2.0): its first 40 frames equal FFmpeg's output; they add precision 9.
- **Never in a file, checked on crafted frames against FFmpeg only:** the scaled and filter stereo modes (4 to 7), the paired entries of the channel table (types 0 to 3), difference order 3, the stereo bit set to 1 with its 6-bit value, and short frames of 1, 9 and 15 samples (and 16, the first that is not short). 27 crafted files, with random residuals, wasted bits, coefficients, shifts and flags, give FFmpeg's samples exactly. This pins the text to FFmpeg's reading, not to TAK's.
- **Never reached:** a coefficient or a history value that does not fit 16 bits, a frame shorter than 16 samples written by TAK, padding bits that are not zero, prediction orders 0, 48, 64, 192, 224 and 256.
- **The hand-built fixtures (`built-*`)** change the container, not the frames; the 8 whose container the model's minimal walk reads decode to the samples of their input, `cd-16-stereo`.

## 12. Worked examples (checked with the model)

**A. Residual codes, mode 5** (k = 4, t = 11, s = 4, a = 28, b = 25). The bytes `D7 76 E7 07 A0 01`, read least significant bit first, hold five codes (41 bits):

1. u(4) = 7, below t: code 7, **value −4**.
2. u(4) = 13, not below t; the next bit is 0: code 13, **value −7**.
3. u(4) = 11; the next bit is 1: w = 27, below a: code 27 − 11 = 16, **value 8**.
4. u(4) = 13; the next bit is 1: w = 29, not below a; unary(9) reads `0 0 1`, d = 2: code 29 + 8 − 11 = 26, **value 13**.
5. u(4) = 15; the next bit is 1: w = 31; unary(9) reads nine 0 bits, d = 9; e = u(3) = 2, u(2) = 3: code 31 + 25 + 4·4 = 72, **value 36**.

**B. Coefficients, order 4, precision f = 10** (S = 5). Parameters k = −480, 400, −16, 32 (k₂ and k₃ with z = 6: s(6) values −1 and 2, times 16).

| Step | t₀ | t₁ | t₂ | t₃ |
|---|---|---|---|---|
| start | −30720 | | | |
| i = 1 | −54720 | 25600 | | |
| i = 2 | −55520 | 27310 | −1024 | |
| i = 3 | −55584 | 29017 | −4494 | 2048 |

- At i = 1: t₀ = −30720 + ((400 · −30720 + 256) >> 9) = −30720 + (−12287744 >> 9) = −30720 − 24000.
- The coefficients: c₁ = −((−55584 + 16) >> 5) = 1737, c₂ = −907, c₃ = 140, c₄ = −64.
- With ds = 0 and the values 1000, 1100, 1180, 1240 before the position (1240 the latest): A = 512 + 1737·1240 − 907·1180 + 140·1100 − 64·1000 = 1174132; A >> 10 = 1146; with a residual of 3 the value is 1146 − 3 = **1143**.

## 13. Not determined

- Codecs 0, 1 and 3, bit depths other than 8, 16 and 24, more than 6 channels (section 1).
- The meaning of the stereo bit and its 6-bit value (section 6.1).
- Prediction order 0 (section 5).
- A channel table that leaves channels out (section 6.2).
- Whether TAK holds the coefficients and the filter histories at 16 bits, as the source does, or wider; and what it does with stream info of an info frame that disagrees with the metadata.
