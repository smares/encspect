# Windows Media Audio Professional: decoding supplement

**Status:** written on 2026-11-27 by a Claude agent (Opus) that read FFmpeg 6.1.1's LGPL WMA Professional decoder and the library code it calls for the reconstruction (listed under [Sources](#sources)), and checked this text against ffmpeg as a black box, against Microsoft-encoded files and against the signals they were encoded from. It contains no code. Whoever implements the decoder must not read FFmpeg's `wmaprodec.c`, `wmaprodata.h`, `wma.c`, `sinewin_tablegen.h`, `libavutil/tx*` or `float_dsp.c`, nor any other FFmpeg source or copy, nor the files and folders [Implementer's sources](#implementers-sources) marks as not safe; FFmpeg serves only as a black-box cross-check, and Microsoft's decoder is the reference (item 6 of 0.5 in [plan.md](../plan.md#05-aac-and-wma)).

This is the clean-room supplement that decision 12 of [lossy-decoders.md](lossy-decoders.md#decisions) asks for. It continues [wma-pro-format.md](wma-pro-format.md), which describes the syntax down to the end of each subframe's coefficient codes, and uses its conventions and terms (C, F, M, m, L, B, W, subframe, channel group, scale factor band, the trims). It describes how a decoder turns the values that syntax reads into PCM:

- the coefficient values and the channel transforms of a channel group;
- the scale factors, their carrying over between subframe sizes, and the dequantization;
- the inverse transform, the windows and the overlap of subframes of different sizes;
- the output of a frame and of a stream, its start and end, and what a lost frame takes with it;
- the arithmetic that gives a deterministic decoder, and the cost in work units.

It states FFmpeg's arithmetic as the rule where nothing contradicts it. No run of Microsoft's decoder has written PCM of a Professional stream yet (see [What Microsoft's output showed](#what-microsofts-output-showed)); its sample counts, and the signals the files were encoded from, agree with the output this text describes. The places where FFmpeg's choice may not be Microsoft's are listed under [Points to settle](#points-to-settle-against-microsofts-decoder).

## Sources

**Read (FFmpeg 6.1.1, the release `scripts/fixtures/ffmpeg_sources.py` pins, archive SHA-256 `8684f4b0…`; the files outside its `FILES` list were taken from the same archive):**

- `libavcodec/wmaprodec.c`: the scale factors and their carrying over, the channel transforms and their matrices, the dequantization, the inverse transform's scale, the windowing and overlap, the frame output, the trims, and the state kept across frames and losses.
- `libavcodec/wmaprodata.h`: the critical band frequencies and the default channel transform matrices (tables).
- `libavcodec/wma.c`: the large value and the run-level loop the coefficients share with versions 1 and 2 (how a value is placed).
- `libavutil/tx_template.c`: the definition and scale of the inverse transform (the reference inverse, the half-length output and where the scale enters).
- `libavutil/float_dsp.c`: the portable versions of the windowing and scaling helpers.
- `libavcodec/sinewin_tablegen.h`: the sine window.
- `libavutil/ffmath.h`: the power of ten.
- `libavcodec/vlc.c`: only to rebuild the codes from their lengths in the throwaway decoder below.

**Test files:**

- The 11 Microsoft-encoded fixtures `tests/fixtures/asf/external-windows-wma10pro-*.wma` ([wma-pro-format.md](wma-pro-format.md#sources)): stereo at 44.1 kHz (16 and 24 bits, 64 to 384 kbit/s and quality 10 to 98) and 5.1 at 48 kHz.
- The 72 Professional files of the private sample kit, all by Windows Media Encoder 9 (9.0.0.2980 on Windows XP SP3): stereo at 44.1 and 48 kHz and 5.1 at 44.1 and 48 kHz, 16 and 24 bits, 128 to 768 kbit/s, constant, two-pass constant, peak-constrained variable, bitrate-based variable and quality-based variable bitrates. 64 of them are encoded at their source's rate and channel count, from the kit's `sources`; the other 8 are 5.1 at 44.1 kHz from a 48 kHz source.
- Six public files of [wma-pro-format.md](wma-pro-format.md#sources), checked against the hashes listed there: `8_Channel_ID.wma` (8 channels), `latin_192_mulitchannel.wma` (5.1, WMFSDKVersion 9), `Classical_48_24_6_Q25_2_3.wma` (F = 512, M = 4), `Classical_96_24_2_Q75_2_3.wma` and `Classical_96_24_6_Q75_2_13.wma` (96 kHz, F = 1,024 and 4,096), and `classical_16_16_1_8000_off_0_off_1_29.wma` (16 kHz mono, F = 512).

**How the text was checked:**

- The author wrote a throwaway decoder from this description (Python, in the session's scratch folder, not committed: it was written by someone who read FFmpeg and must not reach the implementer). It reads the syntax by [wma-pro-format.md](wma-pro-format.md) and rebuilds the PCM by the rules below, in double precision, with the full-length inverse transform and the boundary windows of [Windows and overlap](#windows-and-overlap).
- **Against ffmpeg 6.1.1:** on all 11 fixtures, all 72 kit files and the six public files, with FFmpeg's output rule ([The output of a stream](#the-output-of-a-stream)), it gives ffmpeg's sample count and its samples to within 2.7 × 10⁻⁷ of full scale, the rounding of single precision.
- **Against the signals the files were encoded from:** with Microsoft's output rule, the output has the source's length, lag 0 and the source's polarity in all 11 fixtures (against `wma_signals.py`) and the 64 kit files encoded at their source's rate (against the kit's `sources`). Its gain against the source is within 2% of 1 in the fixtures and, in the kit, at 192 kbit/s and above and at quality 75 and above; the encodes at lower settings, which leave out more of the signal, give 0.67 to 0.99. The low-frequency channel of the fixtures decodes to near silence: their source's tone on that channel is 495 Hz, which Microsoft's encoder does not code there.
- **What the files cover:** subframes of every size from 2,048 down to 128 samples at 44.1 and 48 kHz, scale factors carried over from smaller and from larger subframes, stereo pairs with the transform on in all bands and in some, pairs of a multichannel stream, matrices sent as angles for groups of three, four and five channels, groups of six with the default matrix (`Classical_96_24_6_Q75_2_13.wma`), bands of a multichannel group with the transform off, quantization step modifiers, 8 channels, the low-frequency channel, and frames of 512, 1,024, 2,048 and 4,096 samples.
- **Not covered by any file:** default matrices for three to five channels, a channel whose scale factors are kept with no set sent in the frame ([Scale factors kept from an earlier frame](#scale-factors-kept-from-an-earlier-frame)), a run past the subframe's end, a postprocessing transform and a dynamic range gain other than 0.
- **Microsoft's decoder** has written PCM only of versions 1 and 2 so far (runs 45947146147 and 45979958270 of `windows-wma-decode.yml`); its sample counts of the Professional fixtures, variants and public files are those of [wma-pro-format.md](wma-pro-format.md#what-microsofts-decoder-outputs).

## Conventions

Those of [wma-pro-format.md](wma-pro-format.md#conventions), and:

- k is the size index of the current subframe and L = F >> k its length; lines are numbered x = 0 to L − 1. The subframe starts at sample P of its frame (the sum of the channel's earlier subframes in the frame).
- e_k is the edge list of size index k: band b covers lines e_k(b) to e_k(b + 1) − 1 ([Derived values and checks](wma-pro-format.md#derived-values-and-checks)); n_k is its band count.
- c(x) is the coded value of line x of a channel, an integer; c'(x) the value after the channel transform; X(x) the dequantized value.
- 10^(v/20), the sines, cosines and roots are the real functions; [Precision](#precision) says where FFmpeg rounds, [Determinism](#determinism) what this project's decoder does.

## Decoder state

| State | Per | Initial value | Changed by |
| --- | --- | --- | --- |
| overlap: what the channel's last subframe adds after the frame's output, F/2 values in FFmpeg's form (the values before its falling window, then the second half of its transform output, unwindowed) | channel | all 0 | every subframe that holds the channel |
| the length of the channel's last subframe | channel | F | every subframe that holds the channel |
| the last scale factor set sent, its size index and its scale factor step | channel | none | every subframe in which the channel's set is sent |
| whether the channel has sent a set in the current frame | channel | no | reset at every frame's start |

- Nothing else crosses frames. The quantization step, its modifiers, the channel groups and their matrices are read anew in every subframe.
- The scale factors cross frames only in the case of [Scale factors kept from an earlier frame](#scale-factors-kept-from-an-earlier-frame), which no file has.

## Coefficient values

Every line of a channel that sends coefficients gets an integer c(x), 0 where nothing was coded:

- **Vector codes** place their values (0 to 15 from the 4- and 2-value codes, the symbol of a 1-value code, and that symbol plus a large value where it is 100) from line 0 on, each with its sign bit where it is not 0.
- **Run-level codes** place the level of the code's table, or a large value after an escape, at the position the run gives, with its sign bit.
- A value can reach 100 + 2^31 − 1 (a 1-value code of 100 with a 31-bit large value), so it needs more than 32 signed bits; FFmpeg stores every value as a single-precision float, which rounds values above 2^24.
- **A run past the end** (p beyond L, a damaged code by [Coefficient codes](wma-pro-format.md#coefficient-codes)): FFmpeg writes the value at position p modulo L, before it notices the overflow, so the damaged code changes a low line. This text places nothing for a code whose position is L or beyond.
- A channel of the subframe that sends no coefficients, or whose codes would start at or after the frame's end, has all lines 0.

## Channel transforms

Applied first, to the integer values c(x) of each channel group whose transform is on, before any scaling. Each group's members are its channels in channel order, a_1 to a_n their values at one line; the transform gives the members' new values a'_1 to a'_n:

- a'_j = Σ_i T(j, i) × a_i, for j and i from 1 to n, with T the group's n × n matrix: row j is output member j, column i input member i.
- It applies to every line of each band whose transform flag is on; bands with the flag off are left as they are, but see the stereo rule below.
- A channel of the group that sends no coefficients takes part with its zeros, and receives the mixed values like any other member.

### Pairs

- **In a stereo stream** (C = 2): T = [[1, −1], [1, 1]]. The first channel (left) becomes a_1 − a_2 and the second (right) a_1 + a_2. There is no factor 1/2 or 1/√2: with this matrix the decoded level is the source's (gain 1.0000 against the source at 256 kbit/s and above).
- **Stereo bands with the transform off:** in a stereo stream, where the pair's transform is on but a band's flag is off, both channels' values of that band are multiplied by 181/128 (1.4140625). A pair whose transform is off has no such factor. See item 1 of [Points to settle](#points-to-settle-against-microsofts-decoder).
- **In a stream of more than two channels:** T = [[q, −q], [q, q]] with q = 181/256 (0.70703125), and nothing is done in bands with the flag off.

### Matrices sent as angles

For a group of n > 2 members whose matrix is sent ([Channel transform](wma-pro-format.md#channel-transform)): the n(n − 1)/2 angle values r (6 bits each) come first, then n sign bits.

1. Start with the diagonal matrix whose entry (j, j) is +1 where member j's sign bit is 1 and −1 where it is 0; all other entries are 0.
2. For i = 1 to n − 1, and within it for x = 0 to i − 1 (rows counted from 0), take the angle value r of index i(i − 1)/2 + x, θ = r × π / 64, s = sin θ and c = cos θ, and for every column y = 0 to i replace the pair of entries (x, y) and (i, y) by s × T(x, y) − c × T(i, y) and c × T(x, y) + s × T(i, y), both from the values before this replacement.
3. The result is T. Each step rotates rows x and i, so T is orthogonal up to rounding.

- θ runs from 0 to 63 × π / 64. FFmpeg takes s and c from a table S(i) = sin(i × π / 64) for i = 0 to 32: for r below 32, s = S(r) and c = S(32 − r); from 32 on, s = S(64 − r) and c = −S(r − 32). These are sin θ and cos θ.
- The steps run in the order above; each uses the rows as the steps before it left them.

### Default matrices

For a group of n > 2 members whose transform is on with the matrix not sent, T is a fixed matrix for n = 3 to 6. Its entries are multiples of 1/256; the table gives them times 256, row by row (output member j), the columns in member order:

| n | Rows of 256 × T |
| --- | --- |
| 3 | (148, 181, 105), (148, −181, 105), (148, 0, −209) |
| 4 | (128, 167, 128, 69), (128, 69, −128, −167), (128, −69, −128, 167), (128, −167, 128, −69) |
| 5 | (114, 154, 131, 95, 50), (114, 95, −50, −154, −131), (114, 0, −162, 0, 162), (114, −95, −50, 154, −131), (114, −154, 131, −95, 50) |
| 6 | (105, 143, 128, 105, 74, 38), (105, 105, 0, −105, −148, −105), (105, 38, −128, −105, 74, 143), (105, −38, −128, 105, 74, −143), (105, −105, 0, 105, −148, 105), (105, −143, 128, −105, 74, −38) |

- They are 8-bit roundings of orthonormal cosine bases: the first column is the members' sum (148/256 ≈ 1/√3 for n = 3).
- These are format data: the array `default_decorrelation_matrices` of `libavcodec/wmaprodata.h`, 91 values, whose entries for n = 3 to 6 start at offsets 5, 14, 30 and 55 (offset 0 holds 1 for n = 1, offsets 1 to 4 the pair, which the rule above replaces). FFmpeg's table writes each value with six decimals (0.707031 for 181/256), so its single-precision values differ from k/256 by up to 4 × 10⁻⁷; see item 4 of [Points to settle](#points-to-settle-against-microsofts-decoder).
- **More than six members** with the matrix not sent: FFmpeg has no matrix, asks for a sample and keeps whatever matrix that group's slot held from an earlier subframe. This text treats the subframe's frame as one the decoder cannot decode (a gap, [Errors, gaps and withheld output](#errors-gaps-and-withheld-output)). No file has such a group.

## Scale factors

### The set a subframe uses

For each channel of a subframe in which at least one channel sends coefficients (the syntax reads scale factors for every channel there), the channel's set for this subframe, n_k values s(b):

- **A new set** (the channel's first set in this frame, [Scale factors](wma-pro-format.md#scale-factors) step 2): the values read. Its scale factor step is the one read with it.
- **Differences** (step 3): the channel's last sent set carried over to size k (below), plus the levels the codes add to their bands.
- **Not sent** (step 4): the channel's last sent set carried over to size k.
- After a new set or differences, this subframe's set (at size k, with the differences added) becomes the channel's last sent set, with size index k. The scale factor step is that of the last new set.
- A set that is not sent leaves the last sent set as it was: a later subframe carries it over again from its own size, never from the carried-over copy.
- In a subframe in which no channel sends coefficients nothing is read, and the channels' sets are not touched.

### Carrying a set over to another size

The last sent set has size index k_s. Band b of size k takes the value of band v of size k_s, where v is the band of size k_s that holds the middle of band b, by the map of [Derived values and checks](wma-pro-format.md#derived-values-and-checks):

- the middle is floor(((e_k(b) + e_k(b + 1) − 1) × 2^k) / 2), in samples of a whole frame;
- v is the first band of size k_s whose upper edge times 2^(k_s) is at least that middle.

Where k_s = k the map is the identity. Both directions occur in every file: sets of shorter subframes used in longer ones and the reverse.

### Scale factors kept from an earlier frame

A channel whose first subframes in a frame lie in subframes where no channel sends coefficients has sent no set in this frame when it reaches a subframe that codes; there its flag bit may say "not sent".

- **FFmpeg** then uses the contents of the second of its two buffers for the channel, which hold an older set or an older carried-over copy, with no carrying over to the current size.
- **This text:** the channel's last sent set from any earlier frame, carried over to size k, with its scale factor step, as for "not sent" above. A channel that has never sent a set uses 0 in every band, so all its bands take E(b) = Q ([Dequantization](#dequantization)).
- No file has the case (counted in all 89 files above). See item 6 of [Points to settle](#points-to-settle-against-microsofts-decoder).

## Dequantization

For each channel of a subframe in which at least one channel sends coefficients, with Q the channel's quantization step (the subframe's step plus the channel's modifier, [Quantization step](wma-pro-format.md#quantization-step)), S the channel's largest scale factor in this subframe's set, and t its scale factor step:

- the exponent of band b is E(b) = Q − (S − s(b)) × t, an integer;
- X(x) = c'(x) × 10^(E(b) / 20) for every line x of band b.

Notes:

- The largest scale factor S is taken over the n_k values of this subframe's set, after any carrying over and differences.
- Q may be negative; E(b) is then negative too.
- Observed ranges: E(b) from −12 to 194 in the fixtures (Q from 18 to 194, scale factors from 1 to 84), and from −35 to 202 over the fixtures, the kit and three of the public files (`8_Channel_ID.wma`, `latin_192_mulitchannel.wma` and the 16 kHz file). The quantization step's start value grows with B (90 for 16 bits, 135 for 24), which keeps the output at the same scale for every B (see [Inverse transform](#inverse-transform)).
- **The low-frequency channel** is dequantized like any other. FFmpeg 6.1.1 prepares to zero its lines from a cutoff of clamp(floor((440 × L + 3 × floor(rate / 2) − 1) / rate), 4, L) on (about 220 Hz), but writes every line again afterwards, so nothing is zeroed.
  - In the fixtures, the kit's 5.1 files and three of the four public files with the channel, its lines from that cutoff on are all 0 after the channel transform, so the cutoff changes nothing.
  - In `Classical_96_24_6_Q75_2_13.wma`, whose groups of six use the default matrix, they are not 0 in 35,271 of the channel's 35,272 subframes: the matrix's 8-bit entries leak the other channels into it. Over its first 400 frames that part is 44 dB below the channel's level, at most 0.009 of full scale. See item 7 of [Points to settle](#points-to-settle-against-microsofts-decoder).

## Inverse transform

The L lines X(0) to X(L − 1) of a channel's subframe give 2L outputs:

y(n) = −(2 / L) × 2^−(B − 1) × Σ_{q=0}^{L−1} X(q) × cos(π / L × (n + 1/2 + L/2) × (q + 1/2)), for n = 0 to 2L − 1.

- B is the sample size of the codec data (bytes 0 and 1). The factor 2^−(B − 1) puts the output on the scale where full scale is 1.0, and together with the quantization step's start value of (90 × B) >> 4 it makes the decoded level independent of B: the 16- and 24-bit encodes of a source decode to the same level.
- 2 / L and 2^−(B − 1) are powers of two.
- The sign is part of the rule: with it, the output has the polarity of the encoder's input (lag 0 and positive gain against every source above).
- The outputs have the symmetries of a full-length inverse transform: y(n) = −y(L − 1 − n) and y(2L − 1 − n) = y(L + n) for n < L/2. So the middle half, y(L/2) to y(3L/2 − 1), determines the rest; FFmpeg computes only that half (its "half" inverse transform returns the middle half with the sign above).
- A channel whose lines are all 0 after the channel transform has y = 0, and a decoder may skip the transform for it. FFmpeg skips it for a subframe in which no channel sends coefficients.

## Windows and overlap

Each channel's subframes follow one another without gaps, also from one frame to the next. Subframe j of a channel (length L_j, starting at stream sample A_j = frame index × F + P) places its 2L_j outputs from position A_j + F/2 − L_j/2 on. Positions are those of the timeline of [The output of a stream](#the-output-of-a-stream), which starts F/2 before the first frame's first sample: stream sample A is at position A + F/2.

At each boundary between two consecutive subframes of a channel, the earlier one of length L_a and the later one of length L_b, the overlap uses the window of the shorter one, w = min(L_a, L_b), centered on the boundary:

- The rising window of length w is r(i) = sin((i + 1/2) × π / (2w)), i = 0 to w − 1 (the rising half of a sine window of 2w).
- With the boundary at position β, the overlap covers β − w/2 to β + w/2 − 1; position β − w/2 + i takes y_a(3L_a/2 − w/2 + i) × r(w − 1 − i) + y_b(L_b/2 − w/2 + i) × r(i).
- Before the overlap, from β − L_a/2 on, the earlier subframe's outputs are taken whole (factor 1) and the later subframe contributes nothing; after it, to β + L_b/2 − 1, the later subframe's outputs are taken whole and the earlier contributes nothing.

In other words, each subframe's output is multiplied by a window that is 0, then rises over the overlap with the subframe before, is 1, falls over the overlap with the subframe after, and is 0 again:

| Part of y (i counted from 0 within the half) | Factor |
| --- | --- |
| first half, i < d_a, with d_a = (L − min(L_p, L))/2 | 0 |
| first half, d_a ≤ i < d_a + min(L_p, L) | r(i − d_a), the rising window of length min(L_p, L) |
| first half, from d_a + min(L_p, L) on | 1 |
| second half, i < d_b, with d_b = (L − min(L, L_n))/2 | 1 |
| second half, d_b ≤ i < d_b + min(L, L_n) | r(min(L, L_n) − 1 − (i − d_b)), the falling window of length min(L, L_n) |
| second half, from d_b + min(L, L_n) on | 0 |

Here L_p and L_n are the lengths of the channel's subframes before and after. The outputs of all subframes are added.

- The squares of the two windows that overlap add to 1, and with the symmetries above, the aliasing of the two transforms cancels.
- **The first subframe of a stream** has L_p = F and a previous output of 0, so it starts with its own rising window from F/2 − L_1/2 on, where L_1 is its length: the positions before are 0.
- **A subframe in which the channel sends no coefficients,** and every channel of a subframe in which none does, goes through this with y = 0: it completes the previous subframe's falling window and contributes nothing itself.
- **The next subframe's length is not known when a subframe is decoded:** it may belong to the next frame. So a decoder keeps the second half of each subframe's output unwindowed and applies the falling window when the next subframe arrives, at the boundary's w. FFmpeg does this with its half-length outputs: it keeps the middle half of each transform and combines the two halves at each boundary in one step that uses the symmetries of y.

## The output of a frame

- After all subframes of frame i, the stream positions i × F to (i + 1) × F − 1 of the timeline are complete: the last boundary of every channel lies at position (i + 1) × F + F/2, and its overlap reaches back at most F/2.
- These F values per channel are the frame's output; a decoder writes them as the unit's output at positions i × F (the frame's position at the coded stage, as for versions 1 and 2).
- In FFmpeg's terms the output buffer holds F + F/2 values per channel: the frame's output is its first F values, and the last F/2 (the second half of each channel's last subframe, unwindowed) move to its start for the next frame.

## The output of a stream

The timeline starts at position 0, F/2 samples before the first frame's first sample. Every frame's output is its F positions on it; after the last frame, the positions N × F to N × F + (F − L_last)/2 − 1 are complete too (the values before the last subframe's falling window), where N is the number of frames and L_last the largest, over the channels, of the length of the channel's last subframe.

**Microsoft's output** is the stream's "Samples" by the rule of [What Microsoft's decoder outputs](wma-pro-format.md#what-microsofts-decoder-outputs): the timeline from the start cut to E less the end cut, with E = N × F + (F − L_last)/2.

- The start cut is the first frame's start trim where its flag is set, counted from position 0. The trim of F that Microsoft's encoders write makes the output start at the first frame's sample F/2.
- Without the flag, the cut is F/2 + L_first/2: the output starts where the first subframe's rising window ends.
- The end cut is the last frame's end trim, at most the output of the last frame as that section defines it.
- So a decoder writes, after the last frame, a last unit of (F − L_last)/2 positions from N × F on, as the decoder of versions 1 and 2 writes the part of its last block's overlap that no block changes.
- Checked: with this rule the output has the source's length and is aligned with it at lag 0 in the 11 fixtures and the 64 kit files of [Sources](#sources).

**FFmpeg's output** differs:

- It does not output the first frame decoded: its first output is the second frame's, positions F to 2F − 1. The start trim of the first frame (F in the files) is used up on that frame. So with the start trim of F, FFmpeg and Microsoft start at the same position.
- Without a start trim, FFmpeg starts at position F, Microsoft at F/2 + L_first/2.
- It applies each frame's trims to that frame's own F values, and outputs nothing after the last frame. So it ends at N × F less the last frame's end trim, (F − L_last)/2 positions before Microsoft's end where the last subframe is shorter than F: 512 fewer in the nine stereo fixtures.
- The values are the same where both output a position.

The output can exceed full scale (1.17 in `latin_192_mulitchannel.wma`, 1.03 in a kit file); nothing clips it.

## Errors, gaps and withheld output

### What FFmpeg does

- A frame that fails ([Sequence numbers, errors and loss](wma-pro-format.md#sequence-numbers-errors-and-loss)) outputs nothing, and FFmpeg does not reset its state: the subframes decoded before the failure stay in the overlap buffer with their lengths, the frame's output is not moved on, and the next frame overlaps with what is there.
- A loss of packets resets nothing either. Only a seek clears the first F values of the output buffer and starts again with the first frame skipped.

### What crosses a lost frame

This settles the WMA Professional row of "What each decoder withholds after a gap" in [lossy-decoders.md](lossy-decoders.md#withheld-output):

- **The overlap:** the first frame decoded after a gap starts each channel's first subframe (length L_1 for that channel) on a buffer that should hold the lost frame's last subframe. The lost subframe's length is lost with it.
  - A decoder that resets each channel's overlap to 0 and its last subframe's length to F at the gap, as at the start of the stream, gets the same output as an undamaged decode from position F/2 + L_1/2 of the frame's output on: there its own rising window has ended, and the undamaged decode's shorter window (w = min(L_p, L_1) ≤ L_1) has too.
  - So the first frame after a gap withholds its first F/2 + L_1/2 positions, L_1 the largest first subframe length over the channels: at most F, one frame. What follows equals an undamaged decode.
- **Scale factors:** every channel's first subframe in a frame that codes coefficients sends a new set, so no set crosses frames, except in the case of [Scale factors kept from an earlier frame](#scale-factors-kept-from-an-earlier-frame). Where that case occurs in a frame after a gap before the channel sends a set, the set may come from before the gap; the decoder withholds that subframe's window of 2L positions.
- **Nothing else crosses frames:** quantization steps, channel groups and matrices are per subframe, and the format has no noise generator.
- **A frame the decoder cannot decode** (a group of more than six channels with a default matrix) is treated like a lost frame.

## Noise filling and other tools

- **No noise filling:** FFmpeg's decoder adds no noise and has no generator; every line a channel does not code is 0.
- **The extended header's bits** ([Extended header](wma-pro-format.md#extended-header)) are skipped. In the two 22.05 kHz files of WMFSDKVersion 11 they carry audio: FFmpeg decodes `classical_22_16_2_16000_v3c_0_exclusive_0_29.wma` to silence ([Observations](wma-pro-format.md#observations-on-the-test-files)). What they code is not known.
- **The postprocessing transform** (a C × C matrix of 4-bit entries in the frame header) and **the dynamic range gain** are read and dropped. No file sets the first or has a gain other than 0.

## Precision

What FFmpeg rounds to single precision, for a cross-check that aims at its last bits (the throwaway decoder computed in double and met FFmpeg within 2.7 × 10⁻⁷):

- every coefficient value (as a float, exact up to 2^24);
- the channel transforms in single precision: the sine table of the angles (sin(i × π / 64) rounded to single), each rotation step, the sums of the matrix products, starting from 0 and adding the members in order; the default matrices as six-decimal literals; 181/128 and 181/256 are exact;
- the band multiplier: 10^(E/20) computed in double as 2^(E × log2(10) / 20), rounded to single, then multiplied by the line's value in single;
- the inverse transform in single precision by a fast algorithm, with the square root of the factor 2/L × 2^−(B − 1) in each of its two twiddle passes (its rounding depends on the build: x86 and ARM codelets differ);
- the window: sin((i + 1/2) × π / (2w)) with the argument rounded to single and the sine of single precision; each output of the overlap as two products in single and their sum (or difference).

## Determinism

FFmpeg's last bits cannot be a target: its transform's rounding depends on the build, and Microsoft's decoder is a black box. The project's decoder follows the determinism rules of [CLAUDE.md](../../CLAUDE.md#architecture-rules) for lossy decoders and the single precision of [lossy-decoders.md](lossy-decoders.md) instead. Arithmetic that meets them:

- **Values:** the coefficient values as 64-bit integers (they need 33 bits), converted to single precision with rounding to nearest, ties to even.
- **Channel transforms in single precision:** the matrix of angles built as in [Matrices sent as angles](#matrices-sent-as-angles) from a table of 33 values sin(i × π / 64) computed with `DetMath.SinCos(i, 128)` and rounded to single (so that s and c come from the same entries as FFmpeg's); the default matrices as k/256, which are exact in single; each product rounded, the sum taken in member order, no fused multiply-add. The stereo factor 181/128 and the pair's 181/256 are exact.
- **Band multipliers:** E clamped to −128 to 384 (the files use −35 to 202), and 10^(E/20) taken from a table over that range, computed once in double with `DetMath.Exp(E × ln 10 / 20)` and rounded to single.
  - At the clamp's ends the largest value, 2^31 + 99 times 10^19.2, times the matrix entries and summed over 8,192 lines, stays far below the single-precision maximum, and a line value of 1 times 10^−6.4 and the transform's factor down to 2^−43 stays far above the subnormal range: no infinity or NaN can arise, and no subnormal from the dequantization.
  - Values outside the range do not occur in valid streams that Microsoft's encoders write; the clamp decides what a damaged or crafted stream gives.
- **The inverse transform** in single precision by any fixed algorithm whose operations depend only on L, with twiddles from `DetMath.SinCos` rounded to single. The factor 2/L × 2^−(B − 1) is a power of two, so applying it before, inside or after the transform changes no bit (no product underflows).
- **The window:** r(i) from `DetMath.SinCos(2i + 1, 8w)` (the sine of 2π(2i + 1)/(8w)), rounded to single; each overlapped output as y_a × r(w − 1 − i) + y_b × r(i), two rounded products and a rounded sum, no fused multiply-add.
- **No recursive state:** the overlap is a pure function of the last subframe, so there is nothing to flush below 1e-30 and no state that drifts.
- **Output:** planar single-precision floats at full scale 1.0, unclipped.

## Cost per sample

The weights of [Work units per format](lossy-decoders.md#work-units-per-format) apply, for each channel of each subframe:

| Stage | Units |
| --- | --- |
| output, per sample of each channel | 22 (`DecodeWork.ForSample(4)`) |
| dequantization, per line of a channel that is transformed | 2 |
| channel transform, per line of each member in a band with the transform on | n, the group's member count (2 for a pair) |
| stereo bands with the transform off, per line of each channel | 1 |
| inverse transform, per output sample of a channel that is transformed | 2 × log2(2L): 24 for L = 2,048, 16 for 128 |
| window and overlap, per output sample of every channel | 2 |

- A channel whose lines are all 0 after the channel transform skips the dequantization and the transform, and charges only the window: silence costs 24 units a sample.
- **Measured with this model** (estimates; the pull request times them): 40 to 51 units a sample over whole files, 52 to 54 for the frames with every channel coded in subframes of 2,048 (5.1 groups of five charge 5 per line), 24 for silent frames.
- **Per byte:** over whole files, 250 to 740 units a byte at 128 kbit/s (stereo and 5.1), 1,020 to 1,100 for stereo at quality 10 and 1,620 to 1,850 for 5.1 at quality 10 (the kit's files, a tenth of each being digital silence); the fixtures at 64 kbit/s and quality 10, 420 and 560; the 16 kHz mono public file at 8 kbit/s, 730.
- **Silence:** Windows Media Encoder 9 writes a silent frame of 2,048 samples in 36 bits (stereo) to 47 bits (5.1), about 22,000 to 50,000 units a byte. The budget's floor covers it, as it covers Vorbis's and Opus's silence: 2^35 units are about 4.5 hours of silent stereo at 44.1 kHz and 1.4 hours of silent 5.1 at 48 kHz.
- **Recommended:** 4,096 units a byte declared (**estimate**): the quality 10 files of 5.1 come within 10% of 2,048 over the whole file, so 2,048 would leave no room for quieter coded audio.

## Points to settle against Microsoft's decoder

FFmpeg's choices that the files at hand do not confirm as Microsoft's. Each is a candidate for the first PCM comparison with Microsoft's decoder.

1. **The factor 181/128 of stereo bands with the transform off.** FFmpeg scales both channels of such a band by 181/128; a pair with its transform off entirely gets no factor. Against the source, over the samples the factor changes, the error energy is 2.7% (256 kbit/s) and 3.3% (384 kbit/s) lower with it in the fixtures, and the same within 0.1% at lower rates: weak evidence for the factor. The bands concerned hold few lines with values (590 to 5,900 per stereo fixture).
2. **The end of the stream.** The (F − L_last)/2 positions after the last frame are in Microsoft's counts; their values are not compared.
3. **The start without a start trim.** Microsoft's count starts at F/2 + L_first/2 (the variants of [wma-pro-format.md](wma-pro-format.md#what-microsofts-decoder-outputs)); whether its first values are the rising window's output as this text says is not compared.
4. **The default matrices:** k/256 as this text takes them, or FFmpeg's six-decimal values (at most 4 × 10⁻⁷ apart); and whether Microsoft's matrices for three to five channels are these (only groups of six with the default matrix occur, in one public file).
5. **The pair of a multichannel stream:** 181/256 (FFmpeg) against 1/√2, 0.01% apart.
6. **Scale factors kept from an earlier frame:** FFmpeg's older buffer against the last sent set carried over. No file has the case.
7. **The low-frequency channel's cutoff:** whether Microsoft zeroes its lines above about 220 Hz, which FFmpeg prepares and then overwrites. Only `Classical_96_24_6_Q75_2_13.wma` has values there (up to 0.009 of full scale, see [Dequantization](#dequantization)); Microsoft's PCM of that file would tell. This text follows FFmpeg and zeroes nothing.
8. **The postprocessing transform and the dynamic range gain,** which Microsoft's decoder may apply. No file uses them.

Two differences need no comparison: FFmpeg's output rule at the start and end, and its handling of damage (a run past the end, a group above six channels, the state after a failed frame), which the project's own rules set.

## What Microsoft's output showed

- **Sample counts** (runs 41252306359, 41362454853 and 41363084542, before this text): the rule of [wma-pro-format.md](wma-pro-format.md#what-microsofts-decoder-outputs), which [The output of a stream](#the-output-of-a-stream) derives from the timeline here: every position the transform has completed, from the start cut on.
- **PCM:** none yet. Runs 45947146147 and 45979958270 of `windows-wma-decode.yml` wrote the Source Reader's samples of the version 1 and 2 files only; the step that writes them selects those files by their names.
- **Indirect evidence:** the description's output, cut by Microsoft's rule, matches the signals Microsoft's encoders were given in length, alignment (lag 0), polarity and level (gain within 2% of 1 at 192 kbit/s and above), in 75 files from two encoder generations (Media Foundation and the Format SDK of Windows Server 2025, and Windows Media Encoder 9). This is evidence of what the encoders intend, not of the decoder's last bits.
- **Next step:** add the Professional fixtures to the step that writes PCM and run the workflow once; the comparison then settles items 1 to 3 on the fixtures (stereo bands with the transform off, the end, and, with a variant without the start trim, the start). `Classical_96_24_6_Q75_2_13.wma`, given to the same step, would settle items 4 (for six channels) and 7.

## Not determined

- What the extended header carries, and how Microsoft's decoder uses it in the WMFSDKVersion 11 files.
- The default matrices of groups above six channels.
- The postprocessing transform and the dynamic range gain.
- Microsoft's arithmetic: whether it rounds anywhere this text does not (its PCM will tell how far it is from this text's output).

## Implementer's sources

[Implementer's sources](wma-pro-format.md#implementers-sources) of the format description apply unchanged, with these additions.

**Never read, whatever the folder:**

- the scratch folder `wmadesc-pro-decoding` and the cache folder `.cache/wmadesc-pro-decoding` (FFmpeg's sources, the throwaway decoder of this supplement and its scripts, and the notes of the session that wrote it);
- FFmpeg's `libavutil` (the transform and vector helpers), `sinewin_tablegen.h` and `vlc.c` as well as `wmaprodec.c`, `wmaprodata.h` and `wma.c`;
- the scratch folder `wmadesc-standard-decoding` and `.cache/wmadesc-standard-decoding` of the versions 1 and 2 supplement, as that supplement says.

**Safe:**

- this document and [wma-pro-format.md](wma-pro-format.md);
- the decoder of versions 1 and 2 (`src/Encspect.Formats.Wma/Decoding`, on its branch until it merges), written by the implementing half of that split, and its supplement; the inverse transform there computes the same transform as [Inverse transform](#inverse-transform) at another scale;
- the sources the project's other decoders use for generic parts: the transform's definition above is complete, and any fast algorithm for it may be used;
- the committed fixtures, the private sample kit and its `sources`, and the public files by the hashes of [wma-pro-format.md](wma-pro-format.md#sources).

**Tables:** besides `ProTables.cs` (the codebooks, run and level tables and critical frequencies), the decoder needs only the default matrices above, which `scripts/fixtures/wma_tables.py` can extract with the others from `default_decorrelation_matrices` (rounding each value times 256 to an integer, which gives the table above); the windows, the angle sines and the powers of ten follow from the formulas.
