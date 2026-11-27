# Windows Media Audio versions 1 and 2: decoding supplement

**Status:** written on 2026-11-27 by a Claude agent (Opus) that read FFmpeg 6.1.1's LGPL decoder of Windows Media Audio versions 1 and 2 and the library code it calls (listed under [Sources](#sources)), and checked this text against ffmpeg as a black box, against Microsoft-encoded files and against the signals they were encoded from. It contains no code. Whoever implements the decoder must not read FFmpeg's `wmadec.c`, `wma.c`, `wma.h`, `libavutil/tx*` or `float_dsp.c`, nor any other FFmpeg source or copy, nor the files and folders [Implementer's sources](#implementers-sources) marks as not safe; FFmpeg serves only as a black-box cross-check, and Microsoft's decoder is the reference (item 6 of 0.5 in [plan.md](../plan.md#05-aac-and-wma)).

This is the clean-room supplement that decision 12 of [lossy-decoders.md](lossy-decoders.md#decisions) asks for. It continues [wma-standard-format.md](wma-standard-format.md), which describes the syntax down to the end of each block's coefficients, and uses its conventions, terms and derived parameters (F, f, L_k, N_s, s, E_k, S_k, the high bands, H_k, the codebook pair). It describes how a decoder turns the values that syntax reads into PCM:

- the coefficient values, the exponents (scale factor codes and line spectral pairs) and the reuse of exponents across blocks;
- the dequantization, with and without noise substitution, and the noise generator;
- mid/side stereo, the inverse transform, the windows and the overlap of blocks of different sizes;
- the output of a frame, of a stream's start and end, and what a lost frame takes with it.

It states FFmpeg's arithmetic as the rule where nothing contradicts it, and names the five places where FFmpeg's choice may not be Microsoft's ([Points to settle against Microsoft's decoder](#points-to-settle-against-microsofts-decoder)); for one of them, the angles of the line spectral pair curve, the evidence favors another rule, which this text adopts.

## Sources

**Read (FFmpeg 6.1.1, the release `scripts/fixtures/ffmpeg_sources.py` pins, archive SHA-256 `8684f4b0…`; the files outside its `FILES` list were taken from the same archive):**

- `libavcodec/wmadec.c`: the exponents of both kinds, the line spectral pair curve, the dequantization and noise substitution, mid/side, the windowing and overlap, the frame output and the end of the stream.
- `libavcodec/wma.c` and `wma.h`: the noise table, the windows chosen per block size, the array sizes.
- `libavutil/tx_template.c` (the reference inverse transform and the full-length inverse) and `tx.h`: the definition and scale of the transform.
- `libavutil/float_dsp.c`: the portable versions of the vector helpers the windowing and mid/side use.
- `libavcodec/sinewin_tablegen.h`: the sine window.
- `libavutil/ffmath.h`: the power of ten.
- `libavcodec/wmadata.h`: only the line spectral pair codebook, a table.
- `libavcodec/wmaenc.c`: only how ffmpeg's encoder indexes the exponents of version 1.

**Test files:**

- The committed fixtures of versions 1 and 2 in `tests/fixtures/asf` (30 streams): the 9 of ffmpeg's encoders and the 17 Windows-encoded `external-windows-wma92-*` files of [wma-standard-format.md](wma-standard-format.md#sources), the two remuxes `external-remux-ffmpeg-wma92-*`, and the two noise substitution fixtures `derived-ffmpeg-wmav2-noise-control.asf` and `derived-ffmpeg-wmav2-noise-flagged-band.asf`.
- The 80 version 2 files with audio of the private sample kit: 71 of Windows Media Encoder 9 and 9 of Windows Movie Maker 2.1, 8 to 48 kHz, 5 to 320 kbit/s; 14 of them code their exponents as line spectral pairs (one further file, `music-8k-mono-wma9-cbr-0_8_1.wma`, holds no media objects).

**How the text was checked:**

- The author wrote a throwaway decoder from this description (Python, in the session's scratch folder, not committed: it was written by someone who read FFmpeg and must not reach the implementer). It reads the syntax by [wma-standard-format.md](wma-standard-format.md) and rebuilds the PCM by the rules below, with FFmpeg's choices where this text names an alternative.
- On all 30 fixture streams and the 80 kit files it gives ffmpeg 6.1.1's sample count and its samples to within 2.4 × 10⁻⁷ of full scale (the rounding of single precision). Each of FFmpeg's choices named below was needed for that: the exact exponent index of [Exponents of another block size](#exponents-of-another-block-size) (otherwise up to 0.012 on `external-windows-wma92-cbr-stereo-44k-32-sdk.wma`), FFmpeg's fourth root (otherwise up to 8.9 × 10⁻⁶ on the files with line spectral pairs), the sign of the transform, the order of the noise values.
- The files cover: blocks of every size from 2,048 down to 128, mid/side blocks with only the first or only the second channel coded, blocks without coded channels, exponents reused from smaller and from larger blocks, flagged high bands, both exponent codings, versions 1 and 2.
- Against the signals the files were encoded from, the output is aligned at lag 0 and has the input's polarity: the 17 Windows-encoded fixtures against `wma_signals.py`'s tones, and the 69 kit files encoded at their source's rate and channel count against the kit's `sources`. [Points to settle](#points-to-settle-against-microsofts-decoder) gives the comparisons that decide between rules.
- Microsoft's decoder has not yet written PCM for any file (the probes of [development.md](../development.md#wma-decoding-by-microsofts-decoder) count samples only); its sample counts agree with [The output of a stream](#the-output-of-a-stream).

## Conventions

Those of [wma-standard-format.md](wma-standard-format.md#conventions), and:

- k is the size index of the current block and L = L_k its length; lines are numbered x = 0 to L − 1.
- e is the size index of the block a channel's exponents were last sent in, L_e its length.
- X(x) is the exponent value of line x for the current block, M the channel's largest exponent value, t the total gain ("Total Gain"); g stays the width of an escaped level, as in the format description.
- c(x) is the coded value of line x: the signed level of [Coefficient values](#coefficient-values), 0 where nothing was coded.
- 10^(v/16), 10^(t/20) and the other powers, sines, cosines and roots are the real functions; [Precision](#precision) says where FFmpeg rounds.

## Decoder state

| State | Per | Initial value | Changed by |
| --- | --- | --- | --- |
| the block sizes and the reset flag | stream | [wma-standard-format.md](wma-standard-format.md#block-sizes-and-their-state) | every block |
| exponent values for L_e lines, e, M, "received" | channel | none received; M = 1 | a block that sends the channel's exponents |
| noise position | stream | 0 | every coded channel of a block, with noise substitution |
| overlap buffer O, 2F values | channel | all 0 | every block |

Nothing else crosses blocks. The line spectral pairs are sent whole in every block that sends them; no value is predicted from an earlier block.

## Coefficient values

- A run-level code places a value at its position: its level from the level table where the sign bit is 1, minus that level where it is 0.
- An escape places its level u(g) ([Total gain](wma-standard-format.md#total-gain)) or minus it, by its sign bit in the same way. A value of 0 can be escaped; it places 0.
- Positions map to lines as [Coefficients](wma-standard-format.md#coefficients) says: from line s, skipping flagged high bands. Every other line has c(x) = 0.

## Exponents

### Scale factor codes

- A band value v (−60 to 95) stands for 10^(v/16). The 156 values are a table that the formula defines; rounded to single, FFmpeg's table and the formula agree in every entry.
- The values fill lines from line 0 on, band after band, each over its width in the size's band list (a width of 0, and every band past the end of the list, over 4 lines), as the walk of [Exponents](wma-standard-format.md#exponents) covers them. Version 1's first band takes the value u(5) + 10 without a code.
- The last band can run past line L − 1; those values are not used.
- M is the largest of the block's band values (for version 1 its first band's included).

### Line spectral pairs

- The ten indexes give ten values λ_0 to λ_9 from the line spectral pair codebook: λ_j is the entry of row j at the j-th index. The codebook is format data with ten rows of single values, 8 in rows 0, 8 and 9 (read with 3 bits) and 16 in the others (4 bits); see [Implementer's sources](#implementers-sources) for where it comes from.
- For each line x of the block, with w = 2 cos(π x / L):
  - P = 0.5 × (w − λ_1)(w − λ_3)(w − λ_5)(w − λ_7)(w − λ_9), Q = 0.5 × (w − λ_0)(w − λ_2)(w − λ_4)(w − λ_6)(w − λ_8);
  - X(x) = (P² (2 − w) + Q² (2 + w))^(−1/4).
- M is the largest X(x) of the block.
- FFmpeg uses w = 2 cos(π x / F), the frame's angles, also in shorter blocks; this text takes the block's own (see [Points to settle](#points-to-settle-against-microsofts-decoder), item 1). In a block as long as the frame both are the same.

### Exponents of another block size

A block that reuses exponents (its "Exponents Sent" flag 0) has e ≠ k where the exponents came from a block of another size. Line x of the current block takes the exponent at index floor(x × 2^k / 2^e) of the values held, the line of the other block at the same frequency. Where e ≤ k that is exactly x × 2^(k − e).

Where e > k (exponents of a shorter block used in a longer one), FFmpeg rounds per segment, and the files need it for a match:

- With noise substitution the segments are the lines below s, the lines from s to S_k, each high band in band order, and the lines from E_k on. Without it the lines from s to E_k are one segment with base 0, so its line s + i takes index floor(i × 2^k / 2^e); for version 2 (s = 0) that is the rule above, for version 1 it is item 2 of [Points to settle](#points-to-settle-against-microsofts-decoder).
- With noise substitution, a line at offset i in a segment takes index B + floor(i × 2^k / 2^e), where B is the segment's base: 0 for the lines below s, floor(s × 2^k / 2^e) for the lines from s, and then for each further segment the base before it plus floor(width × 2^k / 2^e) of the segment before it.
- The power of a flagged high band ([Noise substitution](#with-noise-substitution)) is computed the same way, but its bases start at floor(S_k × 2^k / 2^e) and grow by the widths of the high bands only.
- The lines from E_k on take one value (see below), at the final base minus 1.

## Dequantization

For a coded channel, with N the normalization and m the channel's multiplier:

- N = 2 / L for version 2 and √(2 / L) for version 1.
- m = 10^(t/20) / M × N.

A channel that is not coded has all lines 0.

### Without noise substitution

- c(x) × X(x) × m for s ≤ x < E_k, and 0 elsewhere.
- For version 1 (s = 3), FFmpeg takes the exponent of line x − s instead of line x (item 2 of [Points to settle](#points-to-settle-against-microsofts-decoder)).

### With noise substitution

Each line of a coded channel takes the next noise value r, in line order from line 0 to L − 1 (L values per coded channel and block, channel 1 before channel 2):

| Lines | Value |
| --- | --- |
| x < s | r × X(x) × m |
| s ≤ x < S_k, and the high bands not flagged | (c(x) + r) × X(x) × m |
| a flagged high band j | r × X(x) × m_j |
| x ≥ E_k | r × X(E_k − 1) × m |

- So noise is added to every coded line, also where c(x) is 0, at a level well below one quantization step.
- The level of a flagged band j:
  - m_j = √(p_j / p_last) × 10^(ℓ_j / 20) / (M × a) × N, where ℓ_j is the band's noise level ([Noise substitution](wma-standard-format.md#noise-substitution) of the syntax: the first flagged band's u(7) − 19, the others' differences added in band order), a the noise amplitude of the generator below, p_j the mean of X(x)² over the band's lines and p_last that of the channel's last flagged band.
  - The total gain does not enter m_j.
  - Since the noise values have a root mean square of a, the lines of a flagged band have a root mean square of about X(x) × √(p_j / p_last) × 10^(ℓ_j / 20) / M × N.
- The bands below s and from E_k on are not coded; they hold the noise alone.

### The noise generator

- A table of 8,192 values: a 32-bit state starts at 1, and for each entry becomes state × 314,159 + 1 modulo 2^32; the entry is the state read as a signed 32-bit integer times √3 × a / 2^31. The values are uniform on (−√3 a, √3 a), with a root mean square of a.
- a = 0.02 for streams with scale factor codes and 0.04 for streams with line spectral pairs.
- The noise position starts at 0 with the stream, advances by one per value taken, wraps from 8,191 to 0, and is never reset; blocks and channels without coded lines take none.
- Microsoft's generator is not known; a decoder that follows this one cannot match Microsoft's output in noise-substituted lines, only their level.

## Mid/side stereo

For a stereo block with the mid/side flag set, the two channels' lines are first dequantized as above (a channel not coded being all 0), then for every line left = first + second and right = first − second. There is no scaling by 1/2. Where only the first channel is coded, both outputs equal it; where only the second is, the left output equals it and the right output is its negative.

## Inverse transform

The L lines C(0) to C(L − 1) of a channel give 2L outputs:

y(n) = −(1/32768) × Σ_{q=0}^{L−1} C(q) × cos(π / L × (n + 1/2 + L/2) × (q + 1/2)), for n = 0 to 2L − 1.

- The factor 1/32768 puts the output on the scale where full scale is 1.0; the dequantized lines are in units of 16-bit samples.
- The sign is part of the rule: with it, the output has the polarity of the encoder's input (lag 0 against the source in every comparison above).
- The outputs have the usual symmetries of a full-length inverse transform: y(n) = −y(L − 1 − n) for n < L/2 and y(2L − 1 − n) = y(L + n) for n < L/2.

## Windows and overlap

The window of length N is w_N(i) = sin((i + 1/2) × π / (2N)), i = 0 to N − 1, the rising half of a sine window of 2N.

Each channel has an overlap buffer O of 2F values. A block of length L at position P of its frame (the sum of the frame's earlier blocks), with the previous size L_p and the next size L_n of the block-size state ([wma-standard-format.md](wma-standard-format.md#block-sizes-and-their-state)), starts at a = F/2 + P − L/2 and changes O as follows, i counting from 0:

| Part | Condition | O(a + i) for the first half, O(a + L + i) for the second |
| --- | --- | --- |
| first half | L ≤ L_p | O(a + i) + y(i) × w_L(i) |
| first half | L > L_p, d = (L − L_p)/2 | unchanged for i < d; O(a + i) + y(i) × w_{L_p}(i − d) for d ≤ i < d + L_p; y(i) for i ≥ d + L_p |
| second half | L ≤ L_n | y(L + i) × w_L(L − 1 − i) |
| second half | L > L_n, d = (L − L_n)/2 | y(L + i) for i < d; y(L + i) × w_{L_n}(L_n − 1 − (i − d)) for d ≤ i < d + L_n; 0 for i ≥ d + L_n |

- So the overlap between two blocks uses the window of the shorter one, centered on their boundary, and the longer block's lines outside it are taken whole or zeroed. The squares of the overlapping windows add to 1.
- The previous size of the first block after a reset is the one the block reads, whatever size the block before it had; in the Windows-encoded fixtures the two agree at every reset after the first (3 to 13 per file).
- A channel that is not coded, and every channel of a block without coded channels, still goes through this with y = 0, which completes the previous block's overlap.

## The output of a frame

- After the last block of a frame, O(0) to O(F − 1) are the frame's F output samples per channel; then O(F) to O(2F − 1) move to O(0) to O(F − 1).
- FFmpeg leaves the upper half as it was after the move. Every value of it that a later output reads is set again by the next frame's blocks first, so clearing it changes nothing in a stream whose sizes agree.
- The output of a frame is complete: the overlap of the frame's last block reaches only into O(F) and beyond.

## The output of a stream

- **The start:** the first 2F output samples are dropped, the outputs of the first two frames decoded. With them dropped, the output is aligned at lag 0 with the signal the encoder was given, in all 17 Windows-encoded fixtures and 69 kit files compared ([Sources](#sources)).
- **The end:** after the last frame, O(0) to O(F − 1) hold the last block's overlap, complete in its first (F − min(B, L_n)) / 2 values (B the last block's length, L_n its next size): those are every value the window of the last block no longer changes.
- **Microsoft's count follows from this:** with K frames decoded, outputting every complete value gives K × F + (F − min(B, L_n)) / 2 − 2F, which is the rule of [Samples and duration](wma-standard-format.md#samples-and-duration), (K − 2) × F + (F − min(B, L))/2, for the streams the rule covers. That Microsoft drops 2F at the start is inferred from this agreement, not from its PCM.
- **FFmpeg** instead outputs all F values of O after the last frame, complete or not, and does not decode the frame the last superframe announces ([The end of the stream](wma-standard-format.md#the-end-of-the-stream)), so it outputs (frames − 1) × F.
- The output can exceed full scale; nothing clips it.

## Errors, gaps and withheld output

### What FFmpeg does

- A frame that fails ends its superframe, and FFmpeg outputs nothing for that superframe, also not for the frames before the failed one ([Validity rules and damage](wma-standard-format.md#validity-rules-and-damage)).
- It does not reset the overlap buffer: the blocks of the failed frame that were windowed before the failure stay added in, and the next frame overlaps with them.
- The exponents, M, the "received" flags and the noise position are kept; the block sizes as the format text says.

### What crosses a lost frame

This settles the WMA row of "What each decoder withholds after a gap" in [lossy-decoders.md](lossy-decoders.md#withheld-output) for versions 1 and 2:

- **The overlap:** the first frame decoded after a gap starts its first block (length L_1, previous size L_p as read after the reset) on a buffer that should hold the lost frame's overlap. Its first F/2 + min(L_1, L_p)/2 output samples depend on it; the rest of its output, and all later output, does not. So the span is at most one frame, and a decoder that resets O to 0 at the gap and withholds that span outputs exactly what an undamaged stream gives.
- **Exponent reuse:** a block that reuses exponents uses the last ones its channel received. After a gap they may come from before it; they are right only where the lost frames sent none for that channel. A block as long as the frame always sends them. The dependency ends, per channel, at the first block after the gap that sends them; until then the output of reusing blocks depends on lost state.
- **The noise position:** after a gap it differs from an undamaged decode by the noise values the lost frames took, so noise-substituted lines get other noise values; their level does not depend on it.
- **The line spectral pairs** carry nothing across blocks.

## Precision

What FFmpeg rounds to single precision, for a cross-check that aims at its last bits (the throwaway decoder above computed in double with these roundings and met FFmpeg within 2.4 × 10⁻⁷):

- the exponent values 10^(v/16), M, m (10^(0.05 t) and its division by M in double, rounded to single, then times N in single), N (for version 1, single(2/L) times √(L/2) rounded to single), the noise values, the window values (sine of a double argument, rounded to single), the spectrum and the output;
- the line spectral pair curve entirely in single, with w = single(2 cos(single(π/F) × x)) and the products in the order P = 0.5 × (w − λ_1) … then P × (P × (2 − w)), and the fourth root by a table: the value's exponent field indexes 256 powers 2^(−(e − 126)/4), and its top 7 mantissa bits one of 128 linear pieces of z^(−1/4) on [1/2, 1), interpolated by the remaining 16 bits. The relative error of these pieces is below 2.4 × 10⁻⁶;
- the noise values: the integer rounded to single times single(√3 × single(a) / 2^31);
- the band powers p_j as sums in single in line order, then √(p_j / p_last) and each further factor of m_j rounded to single.

The decoder of this project follows the determinism rules of [CLAUDE.md](../../CLAUDE.md#architecture-rules) instead; the differences from FFmpeg that its own arithmetic gives are of the size above.

## Points to settle against Microsoft's decoder

FFmpeg's choices that the files at hand do not confirm as Microsoft's. Each is a candidate for the first PCM comparison with Microsoft's decoder.

1. **The angles of the line spectral pair curve** (adopted here: the block's own). FFmpeg evaluates the curve of a block of L lines at the angles π x / F of a block as long as the frame, which squeezes the curve of a short block into its lowest 2^−k of the spectrum. On the four 16 kHz mono files of the kit with line spectral pairs (Windows Media Encoder 9, 10 to 16 kbit/s, F = 512), against the source over the samples where the two rules differ (8,697 to 9,214 samples, the short-block frames), the error energy is 48 to 56 with FFmpeg's angles and 3.3 to 6.6 with the block's, against a signal energy of 148 to 159; the whole files' signal-to-noise ratio is 10.5 to 11.8 dB with FFmpeg's and 12.1 to 13.9 dB with the block's. The kit's three 8 kHz files with line spectral pairs have no short blocks, so the two rules give the same output there. ffmpeg's output differs from this rule in the short blocks of streams with line spectral pairs that send exponents in them.
2. **The exponents of version 1 without noise substitution.** FFmpeg takes line x's exponent from line x − 3 there, but from line x with noise substitution; ffmpeg's encoder uses the same offset, so ffmpeg's files cannot tell. No Microsoft-encoded version 1 file is at hand.
3. **The exponent index across block sizes** ([Exponents of another block size](#exponents-of-another-block-size)) where e > k: the per-segment rounding against floor(x × 2^k / 2^e). On the two Windows fixtures where they differ (`external-windows-wma92-cbr-stereo-44k-32-sdk.wma`, 15,618 samples; `…-22k-20-sdk.wma`, 802) the error against the source is the same to 0.2% either way.
4. **The fourth root:** FFmpeg's table of linear pieces against the exact root; the two give outputs at most 8.9 × 10⁻⁶ of full scale apart in the kit's files.
5. **The line spectral pair codebook's row 5, entry 10:** −1.40037388 stands between −0.03062936 and −0.25128968 in a row that otherwise falls steadily, where −0.14037388 would fit. In the kit's files with line spectral pairs, entries 10 to 14 of row 5 are never used, so the files cannot tell; the table is kept as it is.

Two further differences need no comparison: FFmpeg's noise generator (above), and its output on damage and at the end of the stream, which the project's own rules set.

## Not determined

- **Version 1 with variable block lengths:** besides the band lists of [wma-standard-format.md](wma-standard-format.md#not-determined), sizes other than 0 have no high bands, so with noise substitution FFmpeg writes the lines from s to S_k and then the noise of the lines from E_k on directly after them, and the lines between are left from an earlier block. No file of this kind is at hand.
- **Version 2 at 49,048 Hz and above:** where a size's band list stops short of E_k, its high bands do too, and the same shift occurs. No file is at hand.
- **Microsoft's noise generator and its noise amplitude a.** Only the level of noise-substituted lines can be compared.
- Whether the previous size read after a reset can differ from the block before in a real file, and what Microsoft does then.

## Implementer's sources

[Implementer's sources](wma-standard-format.md#implementers-sources) of the format description apply unchanged, with these additions.

**Never read, whatever the folder:**

- the scratch folder `wmadesc-standard-decoding` and the cache folder `.cache/wmadesc-standard-decoding` (FFmpeg's sources and the throwaway decoder of this supplement);
- FFmpeg's `libavutil` (the transform and vector helpers) as well as `libavcodec`.

**Safe:** this document, and the sources the project's other decoders use for generic parts (the transform's definition above is complete; any fast algorithm for it may be used).

**Tables:** `Tables.cs` holds every table the decoder needs but the line spectral pair codebook. It is format data under the same rule as the others ([development.md](../development.md#where-the-format-tables-come-from)): the array `ff_wma_lsp_codebook` of `libavcodec/wmadata.h`, ten rows of 16 single values (rows 0, 8 and 9 use their first 8), which `scripts/fixtures/wma_tables.py` can extract with the others; the window, the powers of ten, the noise table and the transform's cosines follow from the formulas above.
