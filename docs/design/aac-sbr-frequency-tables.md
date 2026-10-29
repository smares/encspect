# AAC SBR frequency band tables and grid checks: clean-room description

**Status:** written on 2026-10-29 by a Claude agent (Opus) that read FFmpeg 6.1.1's LGPL SBR code, faad2 2.11.1's GPL frequency table code and the code this document replaces. It contains no code. The implementers must not read any of them (see [Implementer's sources](#implementers-sources)).

A license audit found that the SBR frequency table derivation in `src/Encspect.Formats.Aac/Sbr.cs` and its copy in `scripts/fixtures/aac_model.py` follow FFmpeg's LGPL code statement by statement, and that the validity checks of the SBR grid reader are FFmpeg's set. This document describes the behavior both must have, so that two implementers can rewrite them without that code:

- one writes the C# (`Sbr.Derive` and whatever private helpers it needs, and the checks in `Sbr.Grid`);
- another, who has not read the C# rewrite either, writes the Python model's `sbr_bands()`, so that the model stays an independent check of the parser.

The behavior below was checked against the replaced code on every input (see [How this was checked](#how-this-was-checked)).

## The problem

An SBR header (ISO/IEC 14496-3 4.4.2, `sbr_header`) does not state how many values each frame's envelope and noise floor data code. Those counts follow from the frequency band tables of 4.6.18.3, which a decoder builds from six header fields and the SBR output rate (twice the core rate):

- the master frequency band table (4.6.18.3.2), from `bs_start_freq`, `bs_stop_freq`, `bs_freq_scale` and `bs_alter_scale`;
- the high and low resolution tables and the noise floor bands (4.6.18.3.3), from the master table, `bs_xover_band` and `bs_noise_bands`.

The parser needs only five figures from them, to read `sbr_data` far enough to find the parametric stereo extension, and to show the first and last QMF band in the coding statistics. A header whose tables cannot be built is reported, and its element's SBR data is not read.

## Inputs and outputs

**C#:** `Sbr.Derive(SbrHeader header, int rate)` returns `(SbrBands? Bands, string? Error)`, exactly one of them non-null.

- It reads `StartFrequency` (0 to 15), `StopFrequency` (0 to 15), `XoverBand` (0 to 7), `FrequencyScale` (0 to 3), `AlterScale` (0 or 1) and `NoiseBands` (0 to 3) of the header, and the SBR output rate in Hz. `AmpRes` and the other fields play no part.
- `SbrBands(Kx, K2, High, Low, Noise)`: kx, the first QMF band of the SBR range; k2, the band after its last (the master table's last entry); N_high and N_low, the band counts of the high and low resolution tables; and N_Q, the noise floor band count.
- `Error` is one of the exact strings of [Rejections](#rejections). The caller puts it into the finding `aac.sbr-header-invalid` ("an SBR header whose frequency tables cannot be built: …").
- The method is static and keeps no state. The private helpers it used (the band widths and their running sum) are removed with it; the implementer structures the code freely.

**Python:** `sbr_bands(header, rate)` in `scripts/fixtures/aac_model.py` returns the tuple `(high, low, noise)`: N_high, N_low and N_Q.

- `header` is a dict with the keys `start`, `stop` and `xover`, and optionally `freq_scale` (default 2), `alter_scale` (default 1) and `noise_bands` (default 2); other keys (`amp_res`) are ignored.
- The fixture generator (`aac_block_fixtures.py`) calls it only for headers the parser accepts, at 44,100 Hz: `{start 5, stop 9, xover 0}` and `{start 15, stop 12, xover 0, freq_scale 1, alter_scale 0, noise_bands 0}`. For a header the parser rejects, the rewrite should raise `ValueError` with the same message the C# gives (the replaced function returned figures that meant nothing there).
- Standard library only and deterministic, as the module's docstring requires.

## The master frequency band table (4.6.18.3.2)

Notation: Fs is the SBR output rate; NINT(x) is the nearest integer to x. No value that is rounded below ever lies on a half (see [Arithmetic precision](#arithmetic-precision)), so the direction of halves does not matter.

### Rate groups

The parser knows these rates, in six groups that select a row of the start frequency offset table (`HuffmanData.SbrOffset` in C#, `aac_tables.SBR_OFFSET` in Python; format data, not part of the rewrite):

| Row | Rates (Hz) | Offsets for `bs_start_freq` 0 to 15 |
| --- | --- | --- |
| 0 | 16000 | -8 -7 -6 -5 -4 -3 -2 -1 0 1 2 3 4 5 6 7 |
| 1 | 22050 | -5 -4 -3 -2 -1 0 1 2 3 4 5 6 7 9 11 13 |
| 2 | 24000 | -5 -3 -2 -1 0 1 2 3 4 5 6 7 9 11 13 16 |
| 3 | 32000 | -6 -4 -2 -1 0 1 2 3 4 5 6 7 9 11 13 16 |
| 4 | 44100, 48000, 64000 | -4 -2 -1 0 1 2 3 4 5 6 7 9 11 13 16 20 |
| 5 | 88200, 96000, 128000, 176400, 192000 | -2 -1 0 1 2 3 4 5 6 7 9 11 13 16 20 24 |

Any other rate is rejected first, before anything else is computed. The specification groups the rates by ranges ("44.1 to 64 kHz", "above 64 kHz"); the parser accepts only the twelve listed values.

### Start and stop minimum

- The base frequency is 3000 Hz below 32,000 Hz, 4000 Hz from 32,000 Hz up to below 64,000 Hz, and 5000 Hz from 64,000 Hz on.
- startMin = NINT(base · 128 / Fs) and stopMin = NINT(base · 256 / Fs), that is the QMF band of the base frequency and of twice it.
- Both can be computed exactly in integers. Their values:

| Fs | 16000 | 22050 | 24000 | 32000 | 44100 | 48000 | 64000 | 88200 | 96000 | 128000 | 176400 | 192000 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| startMin | 24 | 17 | 16 | 16 | 12 | 11 | 10 | 7 | 7 | 5 | 4 | 3 |
| stopMin | 48 | 35 | 32 | 32 | 23 | 21 | 20 | 15 | 13 | 10 | 7 | 7 |

### k0 and k2

- k0 = startMin + offset[row][`bs_start_freq`].
- `bs_stop_freq` 0 to 13: split the range from stopMin to 64 into 13 bands on a logarithmic scale (the band widths L(stopMin, 64, 13) defined below), sort the 13 widths in ascending order, and add the first `bs_stop_freq` of them to stopMin. So `bs_stop_freq` 0 gives stopMin and 13 gives 64.
- `bs_stop_freq` 14 gives 2 · k0, and 15 gives 3 · k0.
- k2 is that value, limited to at most 64.

The values of k2 before the limit, for `bs_stop_freq` 0 to 13, per stopMin (a check for the implementation of L):

| stopMin | k2 for `bs_stop_freq` 0, 1, …, 13 |
| --- | --- |
| 48 | 48 49 50 51 52 53 54 55 56 57 58 60 62 64 |
| 35 | 35 36 38 40 42 44 46 48 50 52 55 58 61 64 |
| 32 | 32 34 36 38 40 42 44 46 49 52 55 58 61 64 |
| 23 | 23 25 27 29 31 34 37 40 43 47 51 55 59 64 |
| 21 | 21 23 25 27 29 32 35 38 41 45 49 54 59 64 |
| 20 | 20 22 24 26 28 31 34 37 41 45 49 54 59 64 |
| 15 | 15 17 19 21 23 26 29 33 37 41 46 51 57 64 |
| 13 | 13 15 17 19 21 24 27 31 35 39 44 50 57 64 |
| 10 | 10 11 13 15 17 20 23 27 31 36 42 48 55 64 |
| 7 | 7 8 10 12 14 16 19 23 27 32 38 46 54 64 |

Then, in this order, the header is rejected where:

1. k2 − k0 exceeds the most QMF bands the SBR range may span: 48 at Fs up to 32,000 Hz, 35 at 44,100 Hz, 32 at every other rate (48,000 Hz and up).
2. k0 is not below k2.

### Logarithmic band widths L(a, b, n)

L(a, b, n), for integers 0 < a < b and n ≥ 1, is the list of n integer widths between the boundaries p(0), …, p(n), where p(j) = NINT(a · (b / a)^(j / n)). So p(0) = a and p(n) = b exactly, and the widths add up to b − a. A width can be 0 (or, in principle, negative) where the bands are narrower than one QMF band; the checks below reject that.

### Linear master table (`bs_freq_scale` 0)

Let d = k2 − k0 and dk = 1 with `bs_alter_scale` 0, 2 with `bs_alter_scale` 1.

- The band count N is 2 · ⌊d / 2⌋ with `bs_alter_scale` 0, and 2 · NINT(d / 4) with halves up, that is 2 · ⌊(d + 2) / 4⌋, with `bs_alter_scale` 1. It is even.
- If N is 0 (d = 1), the header is rejected: "the SBR master frequency table is empty".
- Every band starts at width dk. The rest r = d − N · dk is between −2 and 1. If r is negative, the first |r| bands are one narrower each; if r is 1, the last band is one wider. The widths then add up to d.

### Logarithmic master table (`bs_freq_scale` 1 to 3)

- The bands per octave are 12, 10 and 8 for `bs_freq_scale` 1, 2 and 3; call that B.
- **Two regions** when k2 / k0 > 2.2449; then k1 = 2 · k0. Otherwise one region, with k1 = k2. (For the integers k0 and k2 up to 64, no ratio lies within 10⁻⁴ of 2.2449 or of 110/49, so a ratio in any precision and an exact comparison of integers give the same answer.)
- **First region:** N0 = 2 · NINT(B / 2 · log2(k1 / k0)). If N0 is 0 or less: rejected, "the SBR master frequency table's first region is empty". Its widths are L(k0, k1, N0), sorted ascending. If any width is 0 or less: rejected, "the SBR master frequency table has a band of no width".
- **Second region** (two regions only): with the warp w = 1.3 for `bs_alter_scale` 1 and w = 1 for 0, N1 = 2 · NINT(B / 2 · log2(k2 / k1) / w). If N1 is 0 or less: rejected, "the SBR master frequency table's second region is empty". Its widths W1 are L(k1, k2, N1).
  - If the smallest width of W1 is smaller than the largest width of the first region, W0max: sort W1 ascending, take c = min(W0max − W1min, ⌊(W1max − W1min) / 2⌋), add c to the first (smallest) width and subtract c from the last (largest). The second limit does bind: in 377 of the 18,432 combinations of rate, `bs_start_freq`, `bs_stop_freq`, `bs_freq_scale` 1 to 3 and `bs_alter_scale`, it makes c smaller than W0max − W1min (an example is in the [test vectors](#test-vectors)).
  - Sort W1 ascending. If any width is 0 or less: rejected, the same "band of no width" message.
- The master table's widths are the first region's sorted widths followed by the second region's sorted widths (not sorted together).

### The master table

The master table f_master has N_master + 1 entries: k0, then the running sum of the widths. Its last entry is always k2.

## The derived tables (4.6.18.3.3)

In this order:

1. If `bs_xover_band` ≥ N_master: rejected, "bs_xover_band X lies beyond the N bands of the master frequency table" (the specification requires `bs_xover_band` < N_master).
2. kx = f_master[`bs_xover_band`]; the end of the range is f_master[N_master] = k2.
3. If the end exceeds 64: rejected, "the SBR range ends at QMF band E, beyond 64". k2 is limited to 64 above, so this never happens; the check may stay or go.
4. If kx exceeds 32: rejected, "the SBR range starts at QMF band K, beyond 32".
5. N_high = N_master − `bs_xover_band`; N_low = ⌈N_high / 2⌉ (N_high / 2 when even, (N_high + 1) / 2 when odd).
6. N_Q = max(1, NINT(`bs_noise_bands` · log2(k2 / kx))); `bs_noise_bands` 0 gives 1. If N_Q exceeds 5: rejected, "the header gives Q noise floor bands, more than 5".

The result is (kx, k2, N_high, N_low, N_Q).

## Arithmetic precision

Every rounded value is far enough from a half that double precision gives the results the goldens need, and so does IEEE single precision with correctly rounded or libm-accurate operations:

- Over every input (the twelve rates and all 65,536 header combinations), the closest any value that NINT rounds comes to a half is 1.1 · 10⁻⁵ for a band count (B / 2 · log2(k2 / k1) / w = 3.50001 for k1 = 20, k2 = 44, B = 8, w = 1.3, which must give N1 = 8), 4.3 · 10⁻⁵ for N_Q, and 1.7 · 10⁻⁴ for a boundary p(j).
- So the direction of halves does not matter, and neither does the form of evaluation: dividing by the warp or multiplying by its inverse, a power per boundary or a running product, a base-2 logarithm or a natural one divided by ln 2. Double precision, and single precision in three such forms, each gave the replaced code's results on every input.
- Double precision is the safer choice. `Derive` is parser code, not a measurement, so the determinism rules for measurement kernels in `CLAUDE.md` do not apply. Its results are integers, so the reports do not depend on the platform's math library.
- The replaced code's comment said that it evaluates in single precision as one decoder does. That is no requirement: no result depends on it.

## Rejections

The exact strings, in the order the checks run. A header fails on the first that applies.

| Check | `Error` |
| --- | --- |
| rate not in the table | `SBR has no frequency tables for an output rate of {Fs} Hz` |
| k2 − k0 over the limit | `the SBR range spans {k2 − k0} QMF bands, more than the {limit} allowed at {Fs} Hz` |
| k0 ≥ k2 | `the SBR range would start at QMF band {k0} and end at {k2}` |
| linear table, N = 0 | `the SBR master frequency table is empty` |
| N0 ≤ 0 | `the SBR master frequency table's first region is empty` |
| a width ≤ 0, either region | `the SBR master frequency table has a band of no width` |
| N1 ≤ 0 (never happens: k2 / k1 > 1.12 gives N1 ≥ 2) | `the SBR master frequency table's second region is empty` |
| `bs_xover_band` ≥ N_master | `bs_xover_band {x} lies beyond the {N_master} band(s) of the master frequency table`, "1 band" or "N bands" (`Counts.Of`) |
| end > 64 (never happens) | `the SBR range ends at QMF band {end}, beyond 64` |
| kx > 32 | `the SBR range starts at QMF band {kx}, beyond 32` |
| N_Q > 5 | `the header gives {N_Q} noise floor bands, more than 5` |

Numbers are plain integers in the invariant culture (`44100`, not `44,100`). Of the 786,432 combinations of the twelve rates and all header fields, 370,340 are accepted.

## Grid checks (`sbr_grid`, 4.4.2)

`Sbr.Grid` reads one channel's `sbr_grid` in the order of the specification's syntax. Its field reading stays as it is; the rewrite adds back the checks and the one rule below, which were removed. Each failing check ends the reading of the SBR data with an error, which the caller reports as `aac.sbr-data-invalid` ("SBR data with …"). L_E is the frame's envelope count.

- **FIXFIX** (`bs_frame_class` 0): L_E = 2 to the power of the 2-bit `bs_num_env` field (1, 2, 4 or 8).
  - L_E above 4 is rejected right after that field: `an SBR frame of class FIXFIX with 8 envelopes, more than 4`.
  - With L_E = 1, the channel's amplitude resolution is 0 (1.5 dB steps) whatever the header's `bs_amp_res` says, as the syntax table sets it; otherwise it is the header's.
- **FIXVAR and VARFIX** (1 and 2): L_E = `bs_num_rel` + 1, at most 4; no check.
- **VARVAR** (3): L_E = `bs_num_rel_0` + `bs_num_rel_1` + 1, up to 7.
  - L_E above 5 is rejected after the two `bs_num_rel` fields and before the relative borders: `an SBR frame of class VARVAR with {L_E} envelopes, more than 5`. It has to come before `bs_pointer`, whose width table has entries for L_E up to 5 only.
- **`bs_pointer`** (every class but FIXFIX), ⌈log2(L_E + 1)⌉ bits: a value above L_E + 1 is rejected right after it is read: `bs_pointer {p} points past the {L_E} envelope(s) of the SBR frame` ("1 envelope" or "N envelopes", `Counts.Of`). Only L_E 4 and 5, with 3 bits, can fail it.
- The constant `MaxEnvelopes` (5) sizes the per-channel buffers and is the VARVAR limit. Its comment was reworded; its value stays.

The specification's syntax table sets the amplitude resolution and the noise floor count (2 when L_E > 1, otherwise 1), and defines the `bs_pointer` width. This session could not check the text of the specification that states the limits 4 and 5 and the pointer's range; they are the behavior the parser must keep.

## Tests and goldens

**What pins the behavior:**

- The golden reports of the 55 fixtures with SBR headers, in `tests/fixtures/aac`, `adts`, `mp4` and `matroska`, through `Mp4FixtureTests` (MP4 and raw AAC) and `MatroskaFixtureTests`. They show kx and k2 in the coding statistics ("Start Frequency", "Stop Frequency"), and the counts decide where the SBR data ends and whether its extension is found. The tests `AacBlockTests`, `AacEvidenceTests`, `AppleEncoderTests` and `FaacSbrLayoutTests` read the same fixtures.
- No golden or test has an `aac.sbr-header-invalid` or `aac.sbr-data-invalid` finding. The rejections and their messages are pinned only by this document; a unit test of the [test vectors](#test-vectors) and of one input per rejection would close that gap.
- Black-box cross-checks: the faad2 trace in `scripts/fixtures/crosscheck_mp4_fixtures.py` (the SBR header fields and the first SBR extension ID of every block, which only matches where the counts are right), `scripts/fixtures/check_sbr_samples.py --analyzer` (13 SBR conformance and decoder check streams, findings and encoder), and `scripts/fixtures/check_faac_sbr.py` (local only, about ten minutes).
- For the Python model: CI regenerates the hand-built fixtures (`python3 scripts/fixtures/make_mp4_fixtures.py`, which calls `aac_block_fixtures.py`); the regenerated `tests/fixtures` must equal the committed files (`git status` shows no change).

**Running them:**

- `dotnet test tests/Encspect.Tests -c Release --filter "FullyQualifiedName~Mp4FixtureTests|FullyQualifiedName~MatroskaFixtureTests|FullyQualifiedName~Aac|FullyQualifiedName~AppleEncoderTests|FullyQualifiedName~FaacSbrLayoutTests"`, plus the description and finding-coverage tests.
- `python3 scripts/fixtures/make_mp4_fixtures.py` followed by `git status --short tests/fixtures`.
- `python3 scripts/fixtures/crosscheck_mp4_fixtures.py` (needs ffmpeg, gpac, faad, mediainfo and the tracing faad2 build) and `python3 scripts/fixtures/check_sbr_samples.py --analyzer src/Encspect.Cli/bin/Release/net10.0/encspect .cache/sbr`.

Until the rewrite, `Derive` throws and the three grid checks are missing, so every fixture with an SBR header fails its tests (as `parse.failed`), and `make_mp4_fixtures.py` fails at its first SBR fixture.

### Test vectors

(Fs, `bs_start_freq`, `bs_stop_freq`, `bs_freq_scale`, `bs_alter_scale`, `bs_xover_band`, `bs_noise_bands`) → (kx, k2, N_high, N_low, N_Q), with the master table's widths:

| Input | Result | k0 and master widths |
| --- | --- | --- |
| 44100, 5, 9, 2, 1, 0, 2 | 14, 47, 16, 8, 3 | 14: 1 1 1 1 1 1 2 2 2 2 2 3 3 3 4 4 |
| 48000, 5, 9, 2, 1, 0, 2 | 13, 45, 16, 8, 4 | 13: 1 1 1 1 1 1 1 2 2 2 2 3 3 3 4 4 |
| 48000, 5, 9, 2, 1, 7, 2 | 20, 45, 9, 5, 2 | as above |
| 16000, 5, 9, 2, 1, 0, 2 | 21, 57, 14, 7, 3 | 21: 1 2 2 2 2 2 2 2 3 3 3 4 4 4 |
| 44100, 15, 12, 1, 0, 0, 0 | 32, 59, 10, 5, 1 | 32: 2 2 2 2 3 3 3 3 3 4 (one region) |
| 48000, 3, 5, 2, 0, 0, 2 | 11, 32, 16, 8, 3 | 11: 1 1 1 1 1 1 1 1 1 2 1 1 2 2 2 2 |
| 48000, 3, 5, 2, 1, 0, 2 | 11, 32, 14, 7, 3 | 11: 1 1 1 1 1 1 1 1 1 2 2 2 3 3 (the warp) |
| 44100, 1, 6, 3, 0, 0, 2 | 10, 37, 16, 8, 4 | 10: 1 1 1 1 1 1 2 2 2 2 2 2 2 2 2 3 (c = 1) |
| 32000, 13, 12, 3, 0, 0, 2 | 27, 61, 10, 5, 2 | 27: 2 3 3 3 3 4 4 5 3 4 (c limited to 0) |
| 44100, 5, 14, 2, 1, 3, 1 | 17, 28, 7, 4, 1 | 14: 1 1 1 1 1 1 2 2 2 2 |
| 44100, 5, 15, 0, 1, 1, 3 | 16, 42, 13, 7, 4 | 14: fourteen widths of 2 |
| 16000, 0, 0, 0, 0, 0, 2 | 16, 48, 32, 16, 3 | 16: thirty-two widths of 1 |
| 16000, 0, 1, 0, 0, 0, 2 | 16, 49, 32, 16, 3 | 16: thirty-one of 1, then 2 (r = 1) |
| 16000, 0, 1, 0, 1, 0, 2 | 16, 49, 16, 8, 3 | 16: fifteen of 2, then 3 |
| 16000, 0, 3, 0, 1, 0, 2 | 16, 51, 18, 9, 3 | 16: 1, then seventeen of 2 (r = −1) |
| 16000, 0, 2, 0, 1, 0, 2 | 16, 50, 18, 9, 3 | 16: 1 1, then sixteen of 2 (r = −2) |
| 16000, 0, 0, 1, 0, 0, 2 | 16, 48, 20, 10, 3 | 16: eight of 1, then twelve of 2 |

One input per rejection:

| Input | `Error` |
| --- | --- |
| 8000, 0, 0, 2, 1, 0, 2 | SBR has no frequency tables for an output rate of 8000 Hz |
| 22050, 0, 12, 0, 0, 0, 2 | the SBR range spans 49 QMF bands, more than the 48 allowed at 22050 Hz |
| 44100, 9, 15, 1, 0, 0, 2 | the SBR range spans 36 QMF bands, more than the 35 allowed at 44100 Hz |
| 24000, 15, 0, 0, 0, 0, 2 | the SBR range would start at QMF band 32 and end at 32 |
| 44100, 14, 3, 0, 0, 0, 2 | the SBR master frequency table is empty |
| 24000, 15, 1, 2, 0, 0, 2 | the SBR master frequency table's first region is empty |
| 22050, 0, 0, 1, 0, 0, 2 | the SBR master frequency table has a band of no width |
| 22050, 14, 0, 3, 0, 2, 2 | bs_xover_band 2 lies beyond the 2 bands of the master frequency table |
| 16000, 11, 0, 3, 0, 2, 2 | the SBR range starts at QMF band 33, beyond 32 |
| 88200, 0, 8, 0, 0, 0, 2 | the header gives 6 noise floor bands, more than 5 |

## How this was checked

- A model of this description (in the describer's scratch folder, not committed) was run over all 851,968 combinations of the twelve rates, the rejected rate 8000 Hz and every header field (65,536 per rate), against the replaced C# `Derive` called through a harness: equal on every input, results and messages, in double precision (base-2 and natural logarithms) and in three single precision forms.
- The replaced Python `sbr_bands()` equaled the replaced C# on all 370,340 accepted inputs.
- The goldens hold only a handful of header combinations, at the fixtures' rates, so the vectors above are the main check of the rest.

## Implementer's sources

**May read:**

- this document, `CLAUDE.md`, `docs/PLAN.md` and the rest of `docs/`;
- ISO/IEC 14496-3, especially 4.4.2 (`sbr_header`, `sbr_grid`) and 4.6.18.3, where available;
- the rest of the repository, including the callers (`BlockReader.Elements.cs`, `CodingStatistics.cs`), the rest of `Sbr.cs`, `HuffmanData.SbrOffset`, `aac_tables.py`, the fixtures and goldens;
- the outputs of the black-box checks (faad2's trace, ffprobe, the conformance streams), never their source.

**Must not read:**

- FFmpeg's SBR source (`libavcodec/aacsbr_template.c`, `aacsbr.c`, `aacsbr.h`, `aacsbrdata.h`) in any version, faad2's `libfaad/sbr_fbt.c` and `sbr_syntax.c`, or any other decoder's SBR frequency table code;
- the git history of `Sbr.Derive`, `Sbr.Grid` and `aac_model.sbr_bands()` before the commit that stubbed them, and the replaced code in any branch or copy;
- the describer's scratch folder and the license audit reports (`scratchpad/reviews/license-audit-*.md`);
- the other implementer's rewrite: the C# implementer does not read the Python `sbr_bands()` rewrite, and the Python implementer does not read the C# one, until both are done.
