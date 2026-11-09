# TTA1: sample reconstruction

**Status:** written by an agent that read libtta-c 2.3 (the archive `scripts/fixtures/libtta_tool.py` fetches, SHA-256 `68ba6db2…03c7`; LGPL) and checked this text against the repository's fixtures with an independent model. It contains no code. Whoever implements or changes the TTA decoder must not read libtta, FFmpeg's `tta.c` or any other TTA decoder; `tta -d` and FFmpeg are black boxes ([decoders.md](decoders.md), decision 10). The container (header, seek table, CRC-32s) is in [tta.md](../formats/tta.md); this text adds the coded samples, which the authors' public pages leave out.

Names and structure are this document's own, not the source's. Statements marked **Unsure** could not be settled from that source.

Everything below was also checked by the source reader against the repository's committed fixtures in `tests/fixtures/tta/`, with an independent model written from this description:

- Every frame of `external-16-mono`, `external-16-stereo`, `external-16-8ch`, `external-16-loud-noise`, `external-8-stereo`, `external-libtta-16-6ch`, `external-libtta-24-stereo`, `external-libtta-16-mono-24000-last-frame-one-sample` and `external-libtta-16-stereo-password` consumes exactly its seek-table size, its CRC matches, and the decoded audio is the generator's smooth signal (for the password file, with the key from the password `secret`).
- None of this compares against a reference decoder's PCM. `scripts/fixtures/crosscheck_tta_pcm.py` does that with `tta -d` and FFmpeg, and the decoder equals both on every encoder-written fixture but the 8-bit and 8-channel files, which `tta -d` refuses (FFmpeg alone).

## 0. Conventions

- All sample, filter and predictor arithmetic is **32-bit two's complement with wraparound**. Use `unchecked` `int`, and wrap products too.
- `>>` on signed values is an **arithmetic** shift, so it rounds toward minus infinity: −508 >> 9 = −1, and −31 >> 5 = −1.
- "trunc(a/2)" is integer division rounding **toward zero**: trunc(−5/2) = −2. It is not a shift, and the difference matters (section 6).
- The Rice running sums are **unsigned 32-bit** values with wraparound.
- pow2(n) means 2ⁿ for n ≤ 31 and saturates at 2³¹ (0x80000000) for n ≥ 31. The reference compares against such a saturating table.

## 1. Stream and frame structure

**Header.** The header is 22 bytes, all fields little-endian:

| Bytes | Field |
|---|---|
| 0–3 | `TTA1` |
| 4–5 | format (u16) |
| 6–7 | channels (u16) |
| 8–9 | bits per sample (u16) |
| 10–13 | sample rate (u32) |
| 14–17 | samples per channel (u32) |
| 18–21 | CRC-32 of bytes 0–17 |

- An ID3v2 tag may come before the header. Its size is the 4 syncsafe bytes plus 10, plus another 10 when flag bit 0x10 (footer) is set.
- **Bytes per sample**: depth D = ceil(bits/8). This gives D = 1, 2 or 3. Widths that are not a multiple of 8 (12, 20) are coded at the container width D. The filter and the output depend only on D.

**Seek table.** It follows the header: one u32 per frame, then a CRC-32 over those 4·F bytes.

- Each entry is that frame's total size in bytes, its 4-byte CRC included. This was verified on the fixtures.
- The first frame starts right after the table's CRC.

**Frame length.** L = floor(256 · rate / 245) samples per channel, computed in unsigned 32-bit.

| Rate (Hz) | L |
|---|---|
| 8000 | 8359 |
| 22050 | 23040 |
| 24000 | 25077 |
| 32000 | 33436 |
| 44100 | 46080 |
| 48000 | 50155 |
| 96000 | 100310 |

- Frame count F = ceil(samples / L).
- The last frame holds samples − (F−1)·L samples, which is L when that division leaves no remainder.
- A frame holds the same number of samples for every channel.
- A rate of 0 makes L = 0, and the reference divides by zero.

**Frame contents.** Each frame is:

1. One continuous bitstream: for each sample time t = 0…n−1, one Rice code per channel, in channel order 0, 1, …, N−1. There is no alignment between codes or channels.
2. Zero to seven padding bits, to the next byte boundary. Encoders write 0 there, and the decoder ignores them.
3. A 4-byte CRC.

**Frame CRC.** This is the ordinary CRC-32 (zlib/PNG):

- Reflected, polynomial 0xEDB88320.
- Initial value 0xFFFFFFFF, final XOR 0xFFFFFFFF.
- It covers every byte of the frame's bitstream from the frame's first byte through the last byte that holds any bit of the last code, including the padded byte, and not the CRC itself.
- Stored little-endian.
- It covers **coded** bytes only, so it says nothing about the reconstruction, or about the password in format 2.
- The header and seek-table CRCs use the same CRC-32.

**Per-frame reset.** At the start of every frame, all decoder state is reset for every channel:

- Bit reader: empty, nothing carried over, CRC restarted.
- Rice: kA = kB = 10, sumA = sumB = 16384 (= 2¹⁴).
- Filter: coefficients W[0..7] = 0 (format 1), input vector V[0..7] = 0, step vector S[0..7] = 0, previous residual e_prev = 0.
- Fixed predictor: previous value p = 0.

So frames decode independently.

**Formats.**

- **Format 1** is the plain format, and the one in scope.
- **Format 2** (password) changes exactly one thing: the 8 initial filter coefficients, set at every frame reset. They are the bytes of K = CRC-64 of the password bytes, where the CRC-64 is:
  - polynomial 0x42F0E1EBA9EA3693 (ECMA-182)
  - non-reflected (most significant bit first, each byte XORed into the top byte)
  - initial value all ones, final XOR all ones.
- The bytes of K are taken least significant first, each read as a **signed** int8. So W[0] = (int8)(K & 0xFF), and so on up to W[7] = (int8)(K >> 56). Everything else is identical. The reference's console tool encodes the password as UTF-8.
  - Check value: for the password `secret`, K = 0x3996E1B49F675DDA and W = [−38, 93, 103, −97, −76, −31, −106, 57]. This decodes `external-libtta-16-stereo-password.tta` correctly.
- Format 2 is **out of scope**. Without the password a format-2 file still parses and its CRCs still match, but the audio is wrong.
- libtta rejects formats above 2 and accepts only 16–24 bits and at most 6 channels. The FFmpeg-written 8-bit and 8-channel fixtures decode with the same rules (see 4 and 6).

## 2. Adaptive Rice decoding (one value per channel per sample)

**Bit order.** Bytes are consumed in file order. Within a byte, bits are consumed from the **least significant bit** (bit 0) to bit 7.

**Unary part.**

- Count consecutive **1** bits. The first **0** bit ends the count and is consumed. Call the count u.
- There is no maximum and no escape code. A run may span any number of 0xFF bytes. The reference encoder writes long runs in pieces of up to 23 ones, but that makes no difference in the stream.
- A valid run can be very long after silence (kB can fall to 0). So bound the run by the frame's remaining bytes, not by a fixed constant, and report a run that crosses the frame's end as damage.

**Binary part.** Its width depends on u. Read the bits least significant first: the first bit read has weight 1, the next 2, and so on.

- **u = 0** (base stage): read kA bits; m = that number (m = 0 when kA = 0).
- **u ≥ 1** (second stage): read kB bits; q = ((u − 1) · 2^kB) + those bits.

**Adaptation.** The order matters. kA and kB as they stood before this value are used for the widths above and for the offset below.

1. If u ≥ 1, update the second stage with q (before the offset):
   - sumB ← sumB + q − (sumB >> 4) (unsigned)
   - then: if kB > 0 and sumB < pow2(kB+4), set kB ← kB − 1
   - else if sumB > pow2(kB+5), set kB ← kB + 1.
2. If u ≥ 1, the coded value is m = q + 2^kA, using the old kA. So the second stage carries values ≥ 2^kA.
3. Update the base stage with m, in either case:
   - sumA ← sumA + m − (sumA >> 4)
   - then: if kA > 0 and sumA < pow2(kA+4), set kA ← kA − 1
   - else if sumA > pow2(kA+5), set kA ← kA + 1.

Each k moves by at most 1 per value, both comparisons are strict, and the second test is checked only when the first fails.

**Signed residual.**

- If m is odd, e = (m + 1)/2.
- If m is even, e = −(m/2).
- So 0→0, 1→1, 2→−1, 3→2, 4→−2. m is held as a signed 32-bit value: a value ≥ 2³¹ is invalid data, so report it.

**Unsure: large k.** The reference's bit cache is 32 bits wide. With a width k above about 24 it can drop bits, and the encoder can too. A decoder that reads bits exactly matches the reference for every k ≤ 24. Streams with larger k (possible only for 24-bit near full-scale noise) may not round-trip through libtta itself. Treat any disagreement there as unsure, not as an analyzer bug.

## 3. Adaptive filter (stage undone first)

There is one filter per channel. It has **8 taps** at every depth, with **shift s** by depth:

| Depth | Shift s | Rounding r = 2^(s−1) |
|---|---|---|
| 1 byte | 10 | 512 |
| 2 bytes | 9 | 256 |
| 3 bytes | 10 | 512 |

The state is:

- W[0..7]: coefficients.
- V[0..7]: the input vector. V[0..3] hold older third differences; V[4] is the latest third difference, V[5] the latest second difference, V[6] the latest first difference, and V[7] the latest filter output.
- S[0..7]: the step vector, the sign pattern of the V that was used for the previous prediction.
- e_prev: the previous residual.

For each residual e, in this order:

1. **Coefficient update** (sign-sign), using the step vector left by the previous sample:
   - if e_prev > 0: W[j] += S[j] for all j
   - if e_prev < 0: W[j] −= S[j]
   - if e_prev = 0: no change.
2. **Prediction**: P = r + Σ_{j=0..7} V[j]·W[j], using the updated W and the current V, in 32-bit wraparound.
3. **New step vector**, from the current V before it changes:
   - S[0..3] ← old S[1..4]
   - S[4] ← sgn(V[4])·1
   - S[5] ← sgn(V[5])·2
   - S[6] ← sgn(V[6])·2
   - S[7] ← sgn(V[7])·4
   - where sgn(x) = +1 for x ≥ 0 (**zero counts as positive**) and −1 for x < 0.
   - So the step sizes are 1,1,1,1,1,2,2,4 at every depth.
4. **Output**: y = e + (P >> s), with an arithmetic shift.
5. e_prev ← e (the residual, not y).
6. **History shift**, with old values on the right-hand side:
   - V[0..3] ← old V[1..4]
   - d1 = y − old V[7], d2 = d1 − old V[6], d3 = d2 − old V[5]
   - V[4] ← d3, V[5] ← d2, V[6] ← d1, V[7] ← y.

## 4. Fixed first-order predictor (stage undone second)

There is one per channel: x = y + ((p · (2^c − 1)) >> c), then p ← x. Here p is the previous x of this channel in this frame, and the multiply is 32-bit with an arithmetic shift.

- **16 and 24 bits: c = 5** (factor 31/32). The reference uses 5 at every depth it accepts.
- **8 bits: c = 4** (factor 15/16). **Unsure**, as far as the reference source goes:
  - libtta 2.3 refuses 8-bit input, so it gives no answer.
  - Black-box evidence: with c = 4 and filter shift 10, `external-8-stereo.tta` (FFmpeg, `-sample_fmt u8`) decodes to the 16-bit fixture `external-16-stereo` (same generator) divided by 256, within less than 1 on every sample.
  - With c = 5 it drifts by up to 27.
  - Confirm with the FFmpeg cross-check.

## 5. Order of stages

For each sample time, for each channel 0…N−1 in turn:

1. Rice-decode e (section 2, with that channel's Rice state).
2. Run the filter: e → y (section 3).
3. Run the fixed predictor: y → x (section 4).

When all N values x₀…x_{N−1} of the sample time are in, undo the channel decorrelation (section 6) and emit the N output samples, interleaved in channel order. The predictor's p keeps x (before decorrelation).

## 6. Channel decorrelation

**Mono (N = 1).** The output is x₀.

**N ≥ 2.** The coded values are differences of neighboring channels, plus a mid-like last channel. Recover them from the last channel down:

- out[N−1] = x[N−1] + trunc(x[N−2] / 2)
- out[i] = out[i+1] − x[i], for i = N−2 down to 0.

For stereo:

- right = x₁ + trunc(x₀/2)
- left = right − x₀

For the encoder side, in case it helps tests: x[i] = in[i+1] − in[i] for i < N−1, and x[N−1] = in[N−1] − trunc(x[N−2]/2).

- The division truncates toward zero. An arithmetic shift gives wrong samples for odd negative x[N−2].
- The same rule holds for any N. It was checked to 8 channels on `external-16-8ch.tta` (FFmpeg); libtta itself stops at 6.

## 7. Output

- Each output value is the signed PCM sample at the container width D.
- Valid streams give values in [−2^(8D−1), 2^(8D−1) − 1]. The reference writes the low 8·D bits as two's complement little-endian bytes and does not check the range. A value outside that range is damage, so report it.
- **8-bit**: the decoded values are signed and centered on 0 (silence is 0). Unsigned WAVE bytes are value + 128.
  - Evidence: `external-8-stereo` against the scaled 16-bit fixture, as in 4. The reference source would instead treat bytes as signed, but it never codes 8-bit.
  - **Unsure** for 8-bit files written by encoders other than FFmpeg.
- Widths that are not multiples of 8 decode to the container value. For a left-justified WAVE source, the low bits are zero.

## 8. End of frame

- After the last code of the frame's last sample time (all channels), discard the rest of the current byte.
- The next 4 bytes are the CRC, little-endian.
- The next frame starts after them, with all state reset (section 1).
- The bytes consumed, plus 4, should equal the frame's seek-table entry.

## 9. Worked examples (checked with the independent model)

**A. Rice decoding from reset** (kA = kB = 10, sums 16384). The bytes are `06 00 F0 01 00 00 …`. Bit positions are counted from 0, least significant bit first: bits 1, 2, 20–24 are 1, all others 0.

1. Bit 0 is 0, so u = 0. Bits 1–10 read 1,1,0,…,0, so m = 3 and **e = 2**.
   - sumA = 16384 + 3 − 1024 = 15363, which is < 2¹⁴, so kA = 9.
   - This used 11 bits.
2. Bit 11 is 0, so u = 0. Bits 12–20 (9 bits) have only bit 20 set, which is weight 2⁸, so m = 256 and **e = −128**.
   - sumA = 15363 + 256 − 960 = 14659, which is neither < 2¹³ nor > 2¹⁴, so kA stays 9.
   - The stream is now at bit 21.
3. Bits 21–24 are 1 and bit 25 is 0, so u = 4. Bits 26–35 (kB = 10) are 0, so q = 3·1024 = 3072.
   - sumB = 16384 + 3072 − 1024 = 18432, so kB stays 10.
   - m = 3072 + 2⁹ = 3584, so **e = −1792**.
   - sumA = 14659 + 3584 − 916 = 17327, which is > 2¹⁴, so kA = 10.
   - The stream is now at bit 36.

**B. Filter and predictor, 16-bit (s = 9, r = 256), one channel from reset.** The residuals are e = 100, 50, −20, 0, 30, −7.

| t | e | W after step 1 | P | y | x | V after step 6 |
|---|---|---|---|---|---|---|
| 0 | 100 | 0,0,0,0,0,0,0,0 | 256 | 100 | 100 | 0,0,0,0,100,100,100,100 |
| 1 | 50 | 0,0,0,0,1,2,2,4 | 1156 | 52 | 148 | 0,0,0,100,−248,−148,−48,52 |
| 2 | −20 | 0,0,0,1,2,4,4,8 | −508 | −21 | 122 | 0,0,100,−248,123,−25,−73,−21 |
| 3 | 0 | 0,0,−1,0,3,6,6,4 | −147 | −1 | 117 | 0,100,−248,123,118,93,20,−1 |
| 4 | 30 | 0,0,−1,0,3,6,6,4 (e_prev = 0) | 1532 | 32 | 145 | 100,−248,123,118,−80,13,33,32 |
| 5 | −7 | 1,1,−2,1,4,8,8,0 | 28 | −7 | 133 | −248,123,118,−80,−85,−72,−39,−7 |

The step vector S after each sample:

- after t=0: 0,0,0,0,1,2,2,4
- after t=1: 0,0,0,1,1,2,2,4
- after t=2: 0,0,1,1,−1,−2,−2,4
- after t=3: 0,1,1,−1,1,−2,−2,−4
- after t=4: 1,1,−1,1,1,2,2,−4
- after t=5: 1,−1,1,1,−1,2,2,4

The predictor at t=1: (100·31)>>5 = 96, so x = 52 + 96 = 148. At t=2: (148·31)>>5 = 143, so x = 122. At t=2, P >> 9 = −508 >> 9 = −1, which is floor, not truncation.

**C. Decorrelation.**

- Stereo: x = (−5, 7) gives right = 7 + trunc(−5/2) = 5, left = 5 − (−5) = 10. A shift would give right = 4, which is wrong.
- Three channels: x = (3, −4, 9) gives out2 = 9 + (−2) = 7, out1 = 7 − (−4) = 11, out0 = 11 − 3 = 8.
