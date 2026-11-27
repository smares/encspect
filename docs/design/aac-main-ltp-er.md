# AAC Main, LTP and ER AAC: the design of plan 0.5 item 3

Status: draft of 2026-11-27, for the lead; nothing of it is built. It designs item 3 of [plan.md](../plan.md#05-aac-and-wma), step 13 of [lossy-decoders.md](lossy-decoders.md#order), on top of the AAC LC and HE-AAC decoders ([As built: AAC LC](lossy-decoders.md#as-built-aac-lc), [As built: HE-AAC](lossy-decoders.md#as-built-he-aac)). The decoder contract, the determinism rules, withheld output and the cross-check bounds of [lossy-decoders.md](lossy-decoders.md) apply as they are; this text adds what the new object types bring. Where it sets out options, the [decisions](#decisions-for-the-lead) are the lead's. Claims no source read here confirms are marked **unverified**, figures worked out rather than measured **estimate**.

## In short

- **Covered:** AAC Main (object type 1) with its prediction, AAC LTP (4), ER AAC LC (17), ER AAC LTP (19), ER AAC LD (23) and ER AAC ELD (39) with low-delay SBR, with the three error resilience tools (virtual codebooks, RVLC, HCR), for epConfig 0.
- **Waiting, each with its reason** ([What waits](#what-waits)): AAC SSR, AAC scalable and ER AAC scalable, ER BSAC, epConfig 1 to 3, ELDv2's low-delay MPEG Surround, and SBR inside ER AAC LC, LTP and LD payloads.
- **Four pull requests:** Main and LTP (the parser already reads their blocks); the ER syntax in the parser with ER AAC LC, LTP and LD decoding; ER AAC ELD with low-delay SBR; frames of 960 samples (decision 6).
- **Sources:** libxaac (Apache 2.0, commit `2fbadd57`) for LTP and every ER tool; the specifications alone for Main's prediction and ER AAC LTP, which libxaac does not decode.
- **Main's prediction is bit-reproducible:** ISO/IEC 13818-7 13.3.2 defines it in IEEE single precision with its own truncation and rounding, which plain float arithmetic without fused multiply-add reproduces on every platform.
- **References:** the ISO/IEC 14496-26 conformance streams with their reference waveforms (downloaded, never committed) for every covered type; FFmpeg 6.1.1 and libxaac's test decoder as black boxes where they decode a type; FDK for LD and ELD.

## What the parser reads today

- **AAC Main and LTP:** every raw data block, element by element, prediction data included: predictor_reset, the reset group and prediction_used per band up to Table 62's limit ([aac.md](../formats/aac.md#what-is-read)); ltp_data with lag, coefficient and ltp_long_used for each channel of a pair. The tally counts "Prediction" with "Predictor Resets" and "Long-Term Prediction" per channel stream.
- **ER object types:** the configuration only. ELDSpecificConfig with its low-delay SBR headers is read; GASpecificConfig's three resilience flags and epConfig are read, and epConfig 2 and 3 are `aac.error-protection-not-analyzed`. The access units are counted, but no er_raw_data_block is read (`StreamSetup.CanRead` takes object types 1 to 4 only).
- **The decoder** starts a session for every stream and refuses Main, LTP, SSR, ER AAC and frames of 960 samples (`decode.not-possible`).

## What each source implements

Read in libxaac at the pinned commit (`libxaac_tool.py`) and run as a black box, with FFmpeg 6.1.1 (Ubuntu's 6.1.1-3ubuntu5) beside it, on 61 conformance streams (one per coder setup, epConfig 0):

| Tool or type | libxaac `2fbadd57` | FFmpeg 6.1.1 (black box) | Source to follow |
| --- | --- | --- | --- |
| Main's prediction | no: object type 1 fails at initialization (`ixheaacd_headerdecode.c` accepts it in the first switch and refuses it in the final check), and predictor_data_present is an error for every type but LTP and LD (`ixheaacd_channel.c`) | decodes | the specifications alone: ISO/IEC 13818-7 clause 13 (Table 62, 13.3.2 to 13.3.3), with 14496-3 4.6.6, 4.6.8.2.4 and 4.6.13.4 |
| LTP (AAC LTP, ER AAC LD) | yes (`ixheaacd_lt_predict.c`), with a history of 16-bit samples (`ixheaacd_lt_update_state` rounds each output sample to 16 bits) | decodes AAC LTP; refuses LTP in ER AAC LD | libxaac for the structure (the forward transform, the buffer layout, the TNS analysis filter in the loop of 4.6.7.4.1), the standard's history of unrounded output (4.6.7.3) |
| ER AAC LTP (19) | refused at initialization | refused ("object type 19 is not implemented") | the specification: 4.4.2.3 and the LTP code shared with object type 4 |
| ER AAC LC (17) | yes, but 3 of the 7 conformance setups fail (below) | decodes without resilience tools only | libxaac, with the specification and the reference waveforms where they differ |
| ER AAC LD (23), VCB11, RVLC, HCR | yes (`ixheaacd_rev_vlc.c`, `ixheaacd_huff_code_reorder.c`, the section and scalefactor paths of `ixheaacd_longblock.c`) | decodes without LTP and resilience tools only | libxaac |
| ER AAC ELD (39), low-delay SBR | yes, also low-delay MPEG Surround (`ixheaacd_ld_mps_*`) | decodes without resilience tools | libxaac |
| ER AAC scalable, ER BSAC, the EP tool | no (epConfig 2 and 3 are read and ignored) | no | none in item 3 |

**Measured against the ISO reference waveforms** (24-bit WAV; RMS and largest difference in LSB of 16 bits, scripts in the private notes of 2026-11-27):

- **FFmpeg's float decoder** agrees to within the reference's 24-bit rounding on every type it decodes: `am00_44` (Main, 5.1 with a PCE) 0.002 and 0.1, `ap01_48`, `ap02_48` and `ap05_48` (LTP) 0.001 and 0.0, `er_al10_08`, `er_ad1000np_22` and `er_eld1000np_22` 0.001 and 0.0. So the specification's prediction and LTP can be matched by an independent float implementation.
- **libxaac's test decoder** (16-bit output, a fixed-point decoder): LTP 0.44 to 0.74 with up to 12.5; ER AAC LC 0.53 to 0.59 with up to 3.6 on four setups (none, VCB11, HCR, stereo); ER AAC LD, all 31 setups (LTP, every combination of the three tools, 480 and 512 samples, stereo) 0.50 to 0.68 with up to 41.5; ELD, 22 setups, 0.30 to 0.53 with up to 19.
- **Where libxaac fails:** `er_al18_08` (mono, all three tools) and `er_al23_08` (stereo, RVLC) stop with decoding errors after about half and two thirds of their output; `er_al26_08` (stereo, VCB11 and HCR) decodes its first channel and not its second. The same tools pass in ER AAC LD, so the fault is in libxaac's ER AAC LC path, not in the tools. The decoder follows the specification there and is held to the reference waveforms.
- **FDK** decodes LD and ELD ([development.md](../development.md#aac-decoding-by-fdk-aac)) and gives no output for Main and LTP; whether it decodes ER AAC LC and ER AAC LTP is **unverified**.

## Scope

### Covered

- **AAC Main (1):** prediction, with every LC tool; TNS up to order 20 on long windows (Table 4.156).
- **AAC LTP (4) and ER AAC LTP (19):** long-term prediction of long windows (4.6.7; the 2009 edition restricts it to long windows), with TNS and noise substitution as 4.6.7.4 orders them.
- **ER AAC LC (17), LTP (19) and LD (23):** er_raw_data_block (Table 4.19) in the order of the error sensitivity categories (4.5.2.4, Tables 4.148 and 4.149), its extension payloads, and individual_channel_stream with the resilience flags (Tables 4.50 to 4.56): virtual codebooks 16 to 31 (4.6.16.1), RVLC scalefactors (4.6.16.2), HCR spectral data (4.6.16.3). LD adds frames of 512 and 480 samples, their band tables (Tables 4.142 to 4.147), the low-overlap window (Table 4.171), no block switching (4.6.17.2.2) and LTP with ltp_lag_update.
- **ER AAC ELD (39):** er_raw_data_block_eld and its elements (Tables 4.75 to 4.79), the low-delay window over four frames (4.6.20.2), and low-delay SBR (4.6.19, Tables 4.80 and 4.81) in its dual-rate and downsampled modes (4.6.20.4).
- **SBR over a Main or LTP core** (object type 5 or 29 over core 1 or 4): the HE-AAC stage runs on the core's output as it does over LC.
- **Containers:** MP4 and Matroska (`A_AAC/MPEG2/MAIN`, `A_AAC/MPEG4/LTP` and the others it routes), ADTS for Main and LTP (profiles 0 and 3), ADIF; ER streams have no ADTS or ADIF form, so they come in MP4 and Matroska (`A_AAC` with a configuration) and in LATM, which no parser reads.

### What waits

| Type or tool | Why it waits | Where it goes |
| --- | --- | --- |
| AAC SSR (3) | gain control and the PQF filter bank of 13818-7 clause 16; FDK, libxaac and Core Audio refuse it, no encoder in use is known; [lossy-decoders.md](lossy-decoders.md#what-each-decoder-covers) left it out | the item after 0.5 ([plan.md](../plan.md#after-05), decision 1 as the lead decided it) |
| AAC scalable (6), ER AAC scalable (20) | layered streams over several elementary streams, often with a CELP or TwinVQ core; libxaac refuses both and FFmpeg 6.1.1 the ER type, no encoder in use is known; the conformance set has `er_ac*` | the item after 0.5 (decision 1 as the lead decided it) |
| ER BSAC (22) | a different coding: bit-sliced arithmetic coding with fine-grain scalability (4.5.2.6, with an arithmetic decoder of its own, 4.5.2.6.2.7); no permissive source (libxaac has none); used in Korean T-DMB broadcasts (**unverified**: no file of it read) | the item after 0.5 (decision 1 as the lead decided it) |
| SLS (37), the lossless layer of HD-AAC | scalable to lossless coding (subpart 12, with Technical Corrigendum 3 of 2012 to the 2009 edition); no conformance stream or file of it read here (**unverified** whether 14496-26 has any) | the item after 0.5: its configuration and layers at header level first, decoding decided when the item starts (the lead, 2026-11-27) |
| epConfig 1 | each error sensitivity category in an elementary stream of its own: the conformance file `er_al10_08_ep1.mp4` holds three tracks, the later ones synchronized on the first (MP4Box); the MP4 parser would have to join tracks | `decode.not-possible` with the reason until the item after 0.5 (decision 1 as the lead decided it) |
| epConfig 2 and 3 | the error protection tool (1.8): forward error correction, CRCs and interleaving over the classes; its ErrorProtectionSpecificConfig is not read | as epConfig 1; once read, its CRCs are checksums to declare |
| ELDv2's low-delay MPEG Surround | MPEG Surround, which [lossy-decoders.md](lossy-decoders.md#what-each-decoder-covers) leaves out beyond USAC's 2-1-2 | with MPEG Surround |
| SBR in ER AAC LC, LTP and LD payloads (4.5.2.8.2.3) | read by the walk, not decoded: no conformance stream or encoder holds it, so nothing would check it | `decode.not-possible` for such a stream |
| Coupling channel elements | read, not applied, as today; LTP types may not carry dependently switched ones (4.6.7.4.3) | unchanged |
| Downscaled ER AAC LD and ELD output | a playout option at a lower rate (4.6.20.5, which Technical Corrigendum 7 of 2015 adds to the 2009 edition: the LD window by spline interpolation, the LD-SBR filter banks with fewer bands); the stream does not ask for it | not built: the decoder outputs the stream's own rate |

## Main's prediction

### The tool

- **What it is:** a backward-adaptive second-order lattice predictor per spectral line up to Table 62's limit (at most 672 lines), adapted by LMS from the reconstructed values of the two preceding frames, so no coefficient is transmitted (13818-7 13.1, 13.3.2.1).
- **Side information** (13.3.1): predictor_data_present per element (per channel without a common window), predictor_reset with its group, prediction_used per band; the parser reads all of it.
- **Every predictor runs in every long block,** used or not (13.3.2.1); an eight-short block resets all of them (13.3.3).
- **Resets:** 30 interleaved groups (Table 63), a group reset after the frame's processing; a group must be reset within 240 frames in which it is active (13.3.3).
- **With other tools:** prediction works on the scaled, inversely quantized spectra after mid/side stereo (13818-7 1.1, the tools' overview). Intensity stereo bands of the second channel switch prediction off and update the predictors with the intensity values (4.6.8.2.4); noise substitution bands switch it off, reset the predictors and update them with zero (4.6.13.4). TNS and the filter bank follow.
- **In the decoder's order:** mid/side, intensity stereo, prediction, noise substitution, TNS. Prediction after intensity stereo sees the intensity values it must update with; noise substitution after prediction leaves its random values out of the predictors, as 4.6.13.4 asks.
- **What 4.6.13.4 leaves open:** whether "reset" and "update with zero" give the initial state or one update from it. The pull request decodes the conformance streams that use noise substitution with prediction both ways and keeps the reading the reference waveforms confirm.

### Arithmetic and determinism

- **The specification fixes the bits** (13818-7 13.3.2.2 to 13.3.2.4): every operation in IEEE single precision with round to nearest even; the six state values per predictor (r0, r1, COR1, COR2, VAR1, VAR2) stored truncated to their upper 16 bits; the estimate rounded to a 7-bit mantissa with the specification's own half-up function; b/VAR taken from two tables of 128 mantissas and 256 exponents, built once by the specification's procedure.
- **So it is bit-reproducible under the project's rules:** float additions, multiplications and the tables' divisions are correctly rounded in IEEE arithmetic, and .NET evaluates `float` in single precision on x64 and ARM64 without contracting to fused multiply-add. The truncation and rounding are integer operations on the bit pattern. Nothing of `DetMath` is needed.
- **Other decoders agree within the bound, not bit for bit:** the predictors' input is the dequantized spectrum, whose last bits can differ between implementations of the dequantization, and a truncation can then fall on the other side. FFmpeg 6.1.1 agrees with the ISO reference on `am00_44` to 0.002 LSB RMS, which shows how little this drifts; the pull request measures the analyzer against both.
- **Ranges:** NaN and infinities cannot arise. COR cannot exceed VAR in magnitude, so |k| < 1 before b; the largest dequantized value is about 2^56 (escape 8,191 at scalefactor 255), and VAR then stays below single precision's range (about 2^125 at the most, **estimate**). A crafted fixture codes the largest values in every predicted line and holds that no state overflows.
- **The flush of recursive states** below 1e-30 at each block's start (the lossy decoders' rule) touches nothing audible: the exponent table makes b/VAR zero whenever VAR is below 2, so a predictor whose VAR has decayed that far predicts zero, and a value below 1e-30 elsewhere is absorbed by the 7-bit rounding of the estimate or appears in output below 1e-30. It does change the state's bits against an implementation without it, so it is decision 2.

### Gaps and withheld output

- **What depends on the lost state:** after a gap the predictors are reset (13.3.3's third choice); a predictor is right again once the encoder resets its group, and until then a line that uses it is wrong.
- **The rule (decision 3):** per channel, a mask of the 30 groups not resynchronized since the gap. A reset of a group clears its bit; an eight-short block clears all. A block is withheld ("after a gap") while it uses prediction on any line of a group in the mask, beyond the overlap block every gap withholds, and the block after a withheld one is withheld for its overlap.
- **Bounded by the stream:** a stream that obeys the 240-frame rule resynchronizes within 240 active frames (4.B.12 says the same of tuning in); a stream that never resets a group keeps its lines wrong, so withholding them is right. The parser's finding for an overdue group (below) explains a long span. The memory is 30 bits a channel.
- **A failed checksum** (an ADTS CRC) withholds the same span with the reason "checksum failed", since the decoder keeps the state it decoded from the failed block ([damaged-units.md](damaged-units.md)).
- **A stream's start** is taken as synchronized, as every reference decodes it (decision 4). A file cut from a longer stream starts with predictors out of step, which nothing in the stream shows; [aac.md](../formats/aac.md) says so under what is not covered.

## Long-term prediction

- **What it is:** a forward-adaptive one-tap predictor of each long window from the decoder's own earlier output, at a transmitted lag (0 to 2,047; 0 to 1,023 in LD, which also offsets by half a window) and coefficient (Table 4.152), moved into the frequency domain by a forward MDCT and added in the bands ltp_long_used flags (4.6.7.3).
- **With TNS** the predicted spectrum passes a TNS analysis filter before it is added, since TNS synthesis follows (4.6.7.4.1, Figure 4.31); **with noise substitution** the noise band wins (4.6.7.4.2); **with intensity stereo** LTP applies to both channels after intensity stereo (4.6.8.2.5).
- **The history** is the fully reconstructed output, unrounded: 4.6.7.3 defines it as the decoder's output, and 4.B.10 notes that older decoders rounded it to 16 bits and that the decoding algorithm was updated. libxaac keeps the old rounding. The decoder keeps floats, by the rule [As built: AAC LC](lossy-decoders.md#as-built-aac-lc) set (the standard's arithmetic where libxaac differs), and measures the difference against libxaac's test decoder in the pull request.
- **Determinism:** the forward MDCT reuses the inverse transform's FFT in reverse and the windows of `DetMath`; it is float code under the lossy decoders' rules, and the history is flushed below 1e-30 at each block's start.
- **Gaps:** after a gap the history is zeros (4.6.7.3's start), so a block whose lag reaches samples before the gap, or samples withheld after it, predicts from wrong values. The decoder tracks, per stream, the position up to which its output is not trustworthy, and withholds a block that uses LTP over such samples, which extends that position over the block. This replaces the table's 3,072 **estimate** in [lossy-decoders.md](lossy-decoders.md#withheld-output) with an exact rule; a stream that predicts without pause from a gap onward is withheld until it pauses, which the encoder annex expects every 60 frames or so (4.B.10, "switch off for 3 frames every 60 frames"), so the span is the stream's.
- **A stream's start:** the history starts as zeros, as the specification says (decision 4).

## The error resilient syntax

### Reading it

- **Order of the data:** an ER access unit with epConfig 0 holds er_raw_data_block (or er_raw_data_block_eld) with its elements in the order of the channel configuration and no element IDs. Each element's data is regrouped into instances of the error sensitivity categories: the commonly used side information, and per channel (left before right) the channel side information, the RVLC scalefactors, the TNS data and the spectral data (4.5.2.4.2, Table 4.95, Figure 4.25, Tables 4.148 and 4.149).
  - The walk reads the instances in the order libxaac reads them, which its results on the LD and ELD conformance streams confirm, and builds the nodes in the syntax's order, each with its bit ranges (`Node.Ranges` where a field's bits are not contiguous).
- **Virtual codebooks:** codebook numbers of 5 bits, codebooks 16 to 31 each codebook 11 with a smaller largest value (4.6.16.1); a value above its virtual codebook's limit is a finding and breaks the block (decision 5).
- **RVLC:** the scalefactors, intensity positions and noise energies coded with the reversible code of Table 4.166 and escapes of Table 4.168, with length_of_rvlc_sf, the escape length, rev_global_gain and the last-position values for backward decoding (4.6.16.2.2). The walk decodes forward only; it checks the lengths, the forbidden codewords (Table 4.167) and that the redundant end values equal what the forward decoding reached (decision 5).
- **HCR:** the spectral codewords reordered into segments, priority codewords first (4.6.16.3), with length_of_reordered_spectral_data and length_of_longest_codeword. The walk decodes the segments into the values the decoder needs (`BlockRecord`), so the coded bandwidth of the values and the escape share come from HCR streams too. A segment that does not end where its codewords do breaks the block.
- **Extension payloads** after the elements (Table 4.19's loop, Table 4.57): fill, dynamic range control and ancillary data as in fill elements of the GA types; SBR payloads are read and not decoded ([What waits](#what-waits)).
- **LD and ELD:** frames of 512 and 480 samples with Tables 4.142 to 4.147; TNS_MAX_BANDS of Tables 4.172 and 4.173; ELD's individual_channel_stream_eld (no window sequence, a 6-bit max_sfb) and low_delay_sbr_data (Tables 4.80 and 4.81).

### Decoding it

- **After the walk** the values are the same as a GA stream's, so the decoder's spectral tools are shared; what is new is the frame length, the LD windows (sine and low-overlap, Table 4.171, built from `DetMath`'s sine) and ELD's low-delay window of four frames (4.6.20.2; its coefficients are format data from the specification, checked against libxaac's).
- **Low-delay SBR** (4.6.19): the SBR decoder of HE-AAC with the low-delay grid (Table 4.178), its frame classes and the complex low-delay filter banks; the prototypes are format data generated from libxaac as `make_sbr_tables.py` generates the others, with `--check`.
- **Gaps:** ER AAC LC withholds one frame (1,024 samples) as LC does; LD one frame (512 or 480) and its LTP as above; ELD three frames (its window spans four, **estimate** until the pull request shows it); low-delay SBR as SBR does.

## Coding graphs

These rows join the AAC table of [the survey](coding-graphs.md#aac-adts-adif-mp4-matroska) in the pull request that reads their fields, as [CLAUDE.md](../../CLAUDE.md) asks:

| Candidate | Field and source | Kept per unit | Pull request |
| --- | --- | --- | --- |
| Prediction per channel stream | predictor_data_present (13818-7 13.3.1) | a flag series "Prediction", named as its tally node, like "TNS" (1 byte per channel stream) | Main and LTP |
| Prediction bands | prediction_used over the bands up to min(max_sfb, PRED_SFB_MAX) | a node "Prediction Bands" under "Prediction" (a share), and a numeric series in percent per unit as "Mid/Side Bands" has (8 bytes per unit) | Main and LTP |
| Overdue predictor groups | a group active in more than 240 frames without a reset (13.3.3) | a finding, `aac.predictor-reset-overdue` (warning), one per stream with the count; 30 counters a channel | Main and LTP |
| Long-term prediction per channel stream | ltp_data_present | a flag series "Long-Term Prediction", named as its tally node | Main and LTP |
| Long-term prediction bands | ltp_long_used over the bands up to min(max_sfb, 40) | a node "Long-Term Prediction Bands" under "Long-Term Prediction" | Main and LTP |
| Coupling with LTP | a dependently switched coupling channel element in a stream of an LTP type (4.6.7.4.3) | a finding, `aac.ltp-with-dependent-coupling` (warning) | Main and LTP |
| The existing rows for ER streams | window sequences, block sizes, window shapes, stereo coding, coded bandwidth, last non-zero coefficient, escape share, TNS, noise substitution, mid/side bands, intensity stereo bound | as for the GA types; this builds the survey's row "Coded bandwidth, block switching and stereo coding of ... ER AAC LD and ELD" | ER syntax (LC, LTP, LD); ELD |
| Window shapes of LD | window_shape 1 is the low-overlap window in LD (Table 4.171) | "Window Shapes" gains "Low overlap"; ELD's channel stream codes no window sequence and no window shape (Table 4.79), so neither series has values for it, which its doc says | ER syntax |
| Virtual codebooks | codebooks 16 to 31 | counted under "Codebooks" as codebook 11 with their own entries, and in the escape share | ER syntax |
| Low-delay SBR | its frame classes and envelopes | "SBR Frame Classes" and "SBR Envelopes", with the low-delay classes as further categories | ELD |

- **Not proposed:** the LTP lag as a series (a pitch period only where the signal has one; SILK's and Speex's pitch lags are not graphed either, so the three would come together); the resilience flags per unit (they are the stream's, shown in the configuration's nodes); RVLC escapes and HCR segment fill (no other format has a counterpart, and they say nothing about the audio).
- **The entropy pass's cost:** HCR makes the walk of an ER stream heavier than a GA stream's; the second pull request measures it against [decision 10](lossy-decoders.md#decisions)'s 1.5 times.

## Cost and memory

**Work units,** all **estimates** that each pull request measures and fixes in `DecoderWorkTests` (at most twice the FLAC reference's time per unit):

| Stage | Units | When |
| --- | --- | --- |
| Main's prediction | 10 per predicted line (up to 672 a channel) | every long block of every channel, used or not, since every predictor runs (13.3.2.1) |
| LTP | per output sample 2 for the prediction, 2 for the window and 22 for the forward transform (as the inverse's weight), and the TNS order per line filtered | a block with ltp_data_present |
| LTP history | 1 per output sample | every block of an LTP stream |
| LD transform | 2 × log2 of the window, rounded (20 for 1,024 and 960), per output sample, and 2 for the window | ER AAC LD |
| ELD transform and window | as LD, plus 6 per output sample for the four-frame window | ER AAC ELD |
| Low-delay SBR | 220 per output sample, as SBR, until measured | ELD with low-delay SBR |

- **Per-byte allowance:** AAC declares 8,192 units a byte. Stereo ELD with low-delay SBR at its lowest rates comes near it (about 265 units an output sample, 960 samples of two channels from about 60 bytes: about 8,500, **estimate**); the third pull request measures the lowest-rate ELD files of FDK, Apple and libxaac's encoder and raises the declaration if they need it.
- **Memory per channel:** Main's 672 predictors of six 16-bit states (8 KB); LTP's history of 3 frames (12 KB); ELD's overlap of three frames; HCR's segments are per block. At the parser's 48 channels this is under 1 MB, inside the 4 MB [lossy-decoders.md](lossy-decoders.md#memory-per-stream) set for AAC. `MemoryTests` gets a long Main stream and a long ER stream.

## Damaged input

- **Main:** a reset group of 0 or 31 is already a finding (`aac.predictor-reset-group-invalid`); the decoder resets nothing for it.
- **ER:** a broken RVLC or HCR segment, a value beyond its virtual codebook or a length field that does not match is a finding in the walk, and the block is lost: a gap, never concealed. The ER tools exist for concealment, which the lead's rule of 2026-11-01 excludes; the decoder uses none of their backward decoding.
- **A configuration item 3 does not decode** (epConfig 1 to 3, the waiting types, SBR in ER LC, LTP and LD) starts a session that says why (`decode.not-possible`), as `DecoderSessionInvariantTests` asks.

## A correction to AAC LC

- **TNS order:** Table 4.156 sets TNS_MAX_ORDER to 20 on long windows for AAC Main only and to 12 for every other type, LTP included, and the pseudo code of 4.6.9.3 clamps a larger order (`min(order, TNS_MAX_ORDER)`).
- **What is built differs:** the LC decoder refuses a long-window filter above order 12 (`decode.frame-invalid`, as libxaac does), and the parser's comment calls LTP's limit open.
- **The first pull request** clamps as the specification says, by the rule of [As built: AAC LC](lossy-decoders.md#as-built-aac-lc) (the standard's arithmetic where libxaac differs), and corrects the comment.

## Fixtures and references

### Encoder-written, committed

- **FFmpeg 6.1.1's aac encoder** (`-profile:a aac_main -aac_pred 1`, `-profile:a aac_ltp -aac_ltp 1`): `ffmpeg-main.aac` predicts in 42 % of its channel streams; `ffmpeg-ltp.aac` sets ltp_data_present in none. New: Main in M4A and in mono, and LTP on a pitched source at a higher bitrate until it uses LTP; if no setting does, LTP rests on the hand-built and conformance streams.
- **FAAC:** whether a pinned release (1.31.1 to 2.2) or an older one writes Main or LTP is **unverified**; FAAC is LGPL, so the pull request asks the front ends (`--help`) as black boxes and adds files where one does.
- **FDK (fdkaac 1.0.0 with libfdk-aac 2.0.2):** ER AAC LD and ELD with and without low-delay SBR, mono and stereo, at 480 and 512 samples where the front end offers them (**unverified** option names).
- **Apple's afconvert** (the macOS runner, as the existing `apple-aacl` and `apple-aace` files): LD and ELD in stereo, and ELD with SBR (**unverified** format name).
- **libxaac's encoder** (Apache 2.0): LD and ELD as a second, independent writer (**unverified** which resilience flags it can set).
- **ER AAC LC and ER AAC LTP:** no encoder in use is known; the conformance streams and hand-built files cover them.

### Hand-built, committed (`aac_block_fixtures.py` with the fixture model)

- **Main:** reset groups in a cycle, a discontinuity, a group overdue past 240 active frames, an eight-short block between predicting blocks, intensity stereo and noise substitution bands under prediction_used, pairs with and without a common window, a gap in the middle, and the largest values in every predicted line.
- **LTP:** lags 0 and 2,047, ltp_long_used with max_sfb above 40, LTP with TNS, with noise substitution and with intensity stereo, a gap with prediction on both sides, and a dependently switched coupling channel element.
- **ER:** each combination of the three resilience flags for ER AAC LC and LD in mono and stereo, at 480 and 512 samples; RVLC with escapes and intensity and noise positions; a mismatched rev_global_gain; a value beyond its virtual codebook; a damaged HCR segment; extension payloads; epConfig 1, 2 and 3 configurations; ELD with and without low-delay SBR.

### Downloaded, never committed

- **ISO/IEC 14496-26 conformance** (ISO's publicly available standards site, `ISO_IEC_14496-26_2010_Bitstreams`; DVD1 `compressedMp4`, reference waveforms in DVD2 and DVD3 `referencesWav`, 24-bit; under the license [lossy-decoders.md](lossy-decoders.md#conformance-material-per-format) records):
  - `am00` to `am07` (Main, 7 setups at 12 rates, with PCEs), `ap01_48` to `ap05_48` (LTP);
  - `er_al` (ER AAC LC, 7 setups), `er_ap` (ER AAC LTP, 3), `er_ad` (ER AAC LD, 31), `er_eld` (22) and `er_eld_sbr_*` (low-delay SBR), each at epConfig 0, with the `ep1` and `ep3` variants as refused files.
- **A manifest** (`tests/conformance/aac-conformance.json`) pins a subset by SHA-256 (decision 7), with copies in the private repository as the fallback, as `sbr-samples.json` does; FFmpeg's FATE suite holds a few of them with 16-bit references (`am00_88`, `am05_44`, `ap05_48`, `er_ad6000np_44_ep0`, `er_eld*`).

### The cross-checks

- **Against the ISO references:** the float bound of [lossy-decoders.md](lossy-decoders.md#the-pcm-cross-checks) (RMS at most 2^-15/√12, largest 2^-14), the references read at 24 bits. The accuracy criteria of ISO/IEC 14496-4 remain **unverified**.
- **Against FFmpeg 6.1.1** (float, a black box) for the types it decodes, with the same bound; **against libxaac's test decoder** (its MP4 mode needs the access units with a metadata file, which a converter script writes) and **FDK** within the 16-bit bound, with libxaac's LTP rounding and its three ER AAC LC failures as listed differences.
- **Determinism:** each fixture's PCM hash in its `.decode.json` golden, and the Main and ER cases in `DecoderDeterminismTests`; `MeasurementDeterminismTests` scans the assembly as before.

## The pull requests

1. **Main and LTP:** prediction, LTP, the withheld rules, SBR over these cores, the TNS correction, the Main and LTP rows of the survey, the fixtures above; [aac.md](../formats/aac.md) and articles on prediction and long-term prediction.
2. **The ER syntax with ER AAC LC, LTP and LD:** the category-ordered walk with virtual codebooks, RVLC and HCR, LD's frame lengths and windows; the existing rows for these streams; an article on the error resilience tools; the conformance manifest and its job.
3. **ER AAC ELD with low-delay SBR:** ELD's elements, the low-delay window and SBR; the ELD rows; the per-byte allowance measured.
4. **Frames of 960 samples** (decision 6): the 960 and 120 transforms for every GA and ER type, LC included, with the `al960_*` conformance streams.

Each follows the definition of done of 0.3: decoder, entropy-pass figures, coding graphs, fixtures, goldens, cross-checks, an independent review.

## Decisions for the lead

Decisions 2 to 7 are decided as recommended (the lead, 2026-11-27); decision 1 as noted under it.

1. **Which object types.**
   - (a) Main, LTP, ER AAC LC, ER AAC LTP, ER AAC LD and ER AAC ELD with low-delay SBR, at epConfig 0; SSR stays out; scalable, ER scalable, ER BSAC and epConfig 1 to 3 go under "Not planned" with the trigger "a user's file".
   - (b) As (a), with ER BSAC as an item after 0.5 from the specification alone.
   - (c) As (a), without ELD, which brings a second SBR variant and a filter bank of its own, as an item of its own.
   - **Recommendation: (a).** ELD is the ER type with real encoders (FDK, Apple, libxaac) and committed fixtures; BSAC has no permissive source and no file in the project.
   - **Decided by the lead (2026-11-27): (a) for item 3, but nothing goes under "Not planned": AAC SSR, AAC scalable and ER AAC scalable, ER BSAC and epConfig 1 to 3 become an item after 0.5,** built from the specifications and checked against the ISO/IEC 14496-26 conformance streams where they exist.
2. **The flush of Main's predictor states below 1e-30.**
   - (a) Applied at each block's start, as the lossy decoders' rule says; no output above 1e-30 changes, since b/VAR is zero below VAR 2.
   - (b) Exempt Main's predictors, so the states follow the specification bit for bit, denormals included.
   - **Recommendation: (a):** one rule for every decoder, and no denormal arithmetic in long fades.
3. **Main's output after a gap.**
   - (a) Withheld while a block predicts from a reset group not resynchronized since the gap (30 bits a channel), plus the overlap.
   - (b) A fixed 240 frames.
   - (c) Until every group has been reset, whether used or not.
   - **Recommendation: (a):** exact, and it keeps the blocks that do not predict from the lost state.
4. **Main and LTP at a stream's start.**
   - (a) Synchronized, as the specification's start and every reference take it; a cut file is not detected and its doc says so.
   - (b) Treated as a gap: up to 240 frames of a whole file withheld.
   - **Recommendation: (a).**
5. **An ER consistency check that fails** (RVLC's end values or lengths, a value beyond its virtual codebook, an HCR segment).
   - (a) A broken block: a finding and a gap, as any broken syntax.
   - (b) A failed check of coded data: decoded, written and withheld ("checksum failed"), with the checks declared as checksums.
   - **Recommendation: (a):** these are redundancy for concealment, not checksums, and a block whose values cannot be trusted is not decoded.
6. **Frames of 960 samples.**
   - (a) A fourth pull request of item 3, for every GA and ER type, LC included.
   - (b) Not in item 3, under "Not planned" with a trigger.
   - **Recommendation: (a):** one transform size (a DCT-IV of 960 through an FFT of 240 points) opens LC, LTP and ER LC at 960 at once, and the conformance set has `al960_*` streams for it.
7. **How much conformance material CI downloads.**
   - (a) A subset: epConfig 0, one or two rates per setup (about 60 streams and their references, a few hundred MB, **estimate**), cached by SHA-256 with copies in the private repository.
   - (b) Every epConfig 0 stream (about 500 of Main, LTP and ER AAC).
   - (c) Only FATE's few files with 16-bit references.
   - **Recommendation: (a):** it covers every setup's tools; (b) adds rates, not tools.

## Sources read

- **The repository** at `origin/main` aa2bf1c: [CLAUDE.md](../../CLAUDE.md); [plan.md](../plan.md) (0.5, items 1 to 3); [lossy-decoders.md](lossy-decoders.md) (decision 1, "Scope", "What each decoder covers", "Withheld output", "Cost", "Conformance material per format", the as-built sections of AAC LC and HE-AAC); [aac.md](../formats/aac.md); [coding-graphs.md](coding-graphs.md) (the AAC survey); [development.md](../development.md) (AAC decoding); `AacTables.cs`, `StreamSetup.cs`, `BlockReader.cs` (prediction, LTP, TNS order), `AudioSpecificConfig.cs`; `libxaac_tool.py`, `crosscheck_aac_pcm.py`, `make_external_aac_fixtures.py`, `faac_tool.py`, `fdk_tool.py`.
- **libxaac** at `2fbadd57` (fetched by commit): `ixheaacd_audioobjtypes.h`, `ixheaacd_headerdecode.c` (object types, ER flags, epConfig), `ixheaacd_channel.c` (ics_info, LTP data), `ixheaacd_aacdecoder.c` (windows per object type), `ixheaacd_lt_predict.c`, the file list (RVLC, HCR, low-delay MPEG Surround); `test/decoder/ixheaacd_metadata_read.c` (the MP4 mode's metadata file). Its `xaacdec` was built with `-O2 -ffp-contract=off` and run as a black box.
- **Specifications** (private copies, clause numbers only): ISO/IEC 13818-7:2004 1.1 (the tools' overview), clause 13 (Tables 62 and 63), clause 16's title; ISO/IEC 14496-3:2009 1.6.2.2.2.3, 1.6.3.6, 4.4.2.3, Tables 4.19, 4.50 to 4.57, 4.75 to 4.81, 4.142 to 4.149, 4.152, 4.156, 4.166 to 4.173, 4.5.2.4, 4.6.6, 4.6.7, 4.6.8.2.4 and 4.6.8.2.5, 4.6.9.3 and 4.6.9.4, 4.6.13.4, 4.6.16, 4.6.17.2, 4.6.20.1 and 4.6.20.2, 4.B.10 and 4.B.12.
- **ISO/IEC 14496-26** on ISO's site: the listings of DVD1 `compressedMp4` (2,509 files), DVD2 and DVD3 `referencesWav`, `README_audio.txt`, `doc/fileNameConventions.html`, `doc/readme_al_ap_informative.txt`; 63 streams and 50 reference waveforms downloaded to the session's scratchpad, never committed; their AudioSpecificConfigs read with a script of the private notes.
- **Run as black boxes:** FFmpeg 6.1.1 (decoder and `ffmpeg -h encoder=aac`), MP4Box, libxaac's `xaacdec`. **Not read:** FFmpeg's, faad2's, FAAC's and FDK's source.
