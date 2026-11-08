# Lossy decoders: what decoding the lossy formats adds to the contract

Status: approved by the project lead on 2026-11-08, with every recommendation of [Decisions](#decisions) as listed (Opus's units per byte: 8,192), after an independent review (22 findings), a review of its fixes and the lead's rule of 2026-11-07 that nothing is bought and that the formats with a free specification or permissively licensed code come first. Nothing of it is built. Where the text sets out options, the one recorded under [Decisions](#decisions) is the design; the others stay as the record of what was weighed. It designs the "Later" item of the [plan](../plan.md) on lossy decoders, which the measurements of decoded audio ([spectral-analysis.md](spectral-analysis.md)) and gap wave 3 wait for. Claims that no source read for this design confirms are marked **unverified**; figures worked out rather than measured are marked **estimate**.

- The decoder contract of [decoders.md](decoders.md) applies as it is: `IDecoder`, `IFrameDecoder`, the session the parser feeds while it reads, `CodedFrame`, the work budget, the exception boundary and the PCM sink. This text adds only what lossy decoding brings.
- Three parts of the contract change, each for a stated reason: `DetMath` moves from the host to `Core.Abstractions`; the determinism rules gain a clause for decoders; and `DecodeContext` gains a way to report withheld output (see [Contract changes](#contract-changes)).
- The lead's rule of 2026-11-01 (no concealment, `decode.frame-invalid`, positions counted as lost) is applied. It is extended to the output after a lost unit, which depends on that unit's state (decision 4).

## The lead's decision of 2026-11-07

- Nothing is bought: no ISO/IEC 14496-3 and no other paid specification.
- The formats whose specification or permissively licensed reference code is freely available come first: MPEG audio (minimp3), Vorbis, Opus (RFC 6716), Speex, Musepack (libmpcdec's BSD files), AC-3 and E-AC-3 (ATSC A/52, ETSI TS 102 366) and DTS core (ETSI TS 102 114).
- AAC and WMA follow, on free sources only: the clean-room path from FFmpeg that [CLAUDE.md](../../CLAUDE.md) describes, or a free specification where one exists and its license allows it.

## Scope

- **In:**
  - decoders for MPEG audio Layers I, II and III, Vorbis, Opus (SILK, CELT, hybrid), AC-3 and E-AC-3, Musepack SV4 to SV8, DTS core, Speex, AAC (LC, then SBR and parametric stereo, then the other object types the parser reads) and WMA standard, Professional and Voice;
  - the entropy pass each needs in its parser, and the gap wave 3 figures it gives;
  - sources and licenses, output and determinism, the verdict and the PCM cross-checks, gaps and priming, cost, and the order of the pull requests.
- **Out:**
  - DTS-HD's lossless extension (XLL);
  - the ALAC, TTA and WMA Lossless decoders, which the order below schedules (the project lead, 2026-11-08) but which [decoders.md](decoders.md)'s lossless rules govern, not this design: their output is exact, so their check is a bit-exact comparison;
  - DTS extensions beyond the core, the MPEG-2 multichannel extension of Layer II, Dolby's object audio, AC-4 and MPEG-H;
  - parallel decoding of lossy frames (see [Parallel decoding](#parallel-decoding)).

## The design in short

- **One decoder per coding, one pull request each,** in the order of [Order](#order): a groundwork pull request; then MPEG audio, Vorbis, Opus, AC-3, E-AC-3, Musepack, DTS core and Speex; then AAC and WMA, each after the clean-room description it needs.
- **Sources:** free specifications (A/52, ETSI TS 102 366 and 102 114, the Vorbis I specification, RFC 6716 with RFC 8251) and permissive reference code (minimp3, libvorbis, libopus, libspeex, libmpcdec), each license read for this design. AAC has no free specification, and WMA has none at all.
- **The entropy pass joins the parsers:** each lossy parser walks its entropy-coded data in every analysis, with code that takes an optional destination ("The second entropy pass" in [decoders.md](decoders.md)).
  - The walk gives the wave 3 figures.
  - With decoding on, it also fills the session's lent buffers, so the decoder reads no coded bit itself.
- **Output:** planar `Float32` blocks, full scale ±1.0, unclipped and undithered, at the stream's output rate, with untrimmed positions. The integer Speex decoder writes 16-bit `Integer` blocks.
- **Determinism:** the decoders, and the parser code they share that computes in floating point, follow the measurement rules, with a clause of their own for NaN and flushing. A hash of each decoder's raw output, pinned per fixture, holds it.
- **The verdict:** [CLAUDE.md](../../CLAUDE.md) asks every parser to declare every checksum its stream stores; how the coded checksums of lossy streams (AC-3's CRC1 and CRC2 among them) count toward a verdict is decision 2.
  - Correctness of the decoded audio comes from conformance streams and test vectors, and from PCM cross-checks within a tolerance with a stated basis.
- **Gaps:** a unit that cannot be decoded outputs nothing; the decoder resets, and also withholds the output after it that depends on the lost state.
  - Withheld positions are a figure of their own, apart from lost samples (decision 4).
- **Cost:** each decoder charges its stages in work units and declares its units per byte from its cheapest coded audio; the budget's floor covers hours of silence, which costs more per byte in Vorbis and Opus. Each is held to at most 2.5 times the fastest reference decoder's time.

## Formats and order

### What each decoder covers

| Decoder (codec ID) | Covers | Leaves out, with the reason |
| --- | --- | --- |
| `mpeg-audio` | MPEG-1, MPEG-2 and MPEG-2.5, Layers I, II and III, free format | the MPEG-2 multichannel extension: the base channels (the MPEG-1 compatible stereo) are decoded; ISO/IEC 13818-3 is not at hand ([mpeg-clean-room-rules.md](mpeg-clean-room-rules.md)) |
| `vorbis` | Vorbis I: floors 0 and 1, residues 0 to 2 | nothing |
| `opus` | SILK, CELT and hybrid, mono, stereo and the multistream mappings of RFC 7845, output at 48 kHz | packet loss concealment and forward error correction (the lead's rule); the transitions of decision 6 |
| `ac-3` and `e-ac-3` | step 1: AC-3 (A/52); step 2: E-AC-3 (spectral extension, adaptive hybrid transform, enhanced coupling, dependent substreams) | E-AC-3 frames at a reduced sample rate (fscod2) and AC-3 with bsid 9 and 10 (half and quarter rate): the parser notes that A/52 does not specify their bit allocation (`Ac3Analysis.cs` line 262), and no free source for it is known; the decoder-side gains of decision 9 |
| `musepack` (new; the format parser starts no session today) | SV7 and SV8; SV4 to SV6 as libmpcdec 1.2.6 decodes them | the SV4 to SV6 streams that libmpcdec 1.2.6 refuses (the parser reads their header only) |
| `dts` | the core, at the core rate, in all four bitstream forms | every extension (XCh, XXCH, X96, XBR, LBR, XLL): follow-ups |
| `speex` | narrowband, wideband and ultra-wideband, in-band stereo | nothing |
| `aac` and `aac-adts` | step 1: AAC LC; step 2: SBR (HE-AAC) and parametric stereo (HE-AAC v2); step 3: AAC Main, LTP, ER AAC LC, LD and ELD (with low-delay SBR); step 4: USAC (xHE-AAC) | SSR gain control (FDK refuses it); MPEG Surround beyond USAC's 2-1-2; DRC and loudness normalization (decision 9) |
| `wma`, `wma-pro`, `wma-voice` | versions 1 and 2, Professional, Voice | what the parsers leave open: version 1 with variable block lengths and scale factor exponents, and version 2 at 49,048 Hz and above ([asf-wma.md](../formats/asf-wma.md): the description leaves their band lists open); Professional frames that span three packets or exceed the 32,768 bytes a decoder buffers, and the default channel transform matrices, which the parser does not read ([asf-wma.md](../formats/asf-wma.md)); music superframes inside Voice. Each supplement (decision 12) states whether it closes these |

### Order

Value is how many files a decoder makes measurable and how much the measurements say about them. Risk is the source situation and the size of the reconstruction.

| Step | Decoder | Value | Risk | Why here |
| --- | --- | --- | --- | --- |
| 0 | groundwork (no format) | | low | `DetMath` in `Core.Abstractions`, the determinism scan and its fixes, withheld output in the contract and the measurer, the PCM dump tool and comparison script |
| L1 | ALAC (lossless) | medium: ALAC in MP4 and Matroska gets measurements | lowest: Apple's reference decoder (Apache 2.0), which the parser already follows | small and exact; checked bit for bit against Apple's decoder |
| L2 | TTA (lossless) | low to medium | low: the format author's public description; the parser is clean-room, libtta (LGPL) stays a black box | small and exact; the author's reference codec runs in the cross-checks already |
| 1 | MPEG audio (I, II, III) | highest: the most common lossy format | lowest: minimp3 (CC0) is a complete reference | sets the pattern every later decoder follows: walk, lending, output, cross-check |
| 2 | Vorbis | high | low: a free specification and libvorbis (BSD) | the Ogg harness exists |
| 3 | Opus | high | medium: large, but the reference is normative, and every packet's final range coder state must match exactly | the strongest check any lossy decoder can have |
| 4 | AC-3 | medium | low: A/52 is free and complete for AC-3 | Core Audio is the official reference on the macOS runners |
| 5 | E-AC-3 | medium | medium: four more tools in ETSI TS 102 366 | after AC-3 |
| 6 | Musepack | low to medium | low: libmpcdec (BSD), small | its SV7 sample and scalefactor tables and SV8's canonical tables are taken from libmpcdec as format data |
| 7 | DTS core | medium | medium: pseudo-code and tables in the specification, no free official decoder | FFmpeg and libdca stay black boxes |
| 8 | Speex | low | low: libspeex (BSD), small, integer | bit-exact against a fixed-point build |
| 9 | AAC LC | very high | high: no free specification; a clean-room description of FFmpeg's decoder comes first (decision 1) | the first format on the clean-room path after the free ones |
| 10 | HE-AAC (SBR, parametric stereo) | high | high: two large tools with QMF banks, also clean-room | builds on step 9 |
| 11, 12 | WMA standard, Professional, and WMA Lossless | medium | high: clean-room supplements of the reconstruction come first (decision 12) | Microsoft's decoder on the Windows runners is the reference; WMA Lossless shares the clean-room write-up and is checked bit for bit |
| 13 | AAC Main, LTP and ER AAC | low | medium | rare object types |
| 14 | USAC (xHE-AAC) | low today | highest: a second codec (arithmetic coding, LPD with ACELP and TCX, eSBR, MPEG Surround 2-1-2) | last but one |
| 15 | WMA Voice | low | high: a speech codec with many tables, clean-room | last, and only if the lead wants it |

## Sources and licenses

### Per format

Each license was read from its own text on 2026-11-07, as noted. This is a reading for the project, not legal advice.

| Format | Specification | Permissive reference code | License read | Path |
| --- | --- | --- | --- | --- |
| MPEG audio | ISO/IEC 11172-3 and 13818-3: not free and not at hand | minimp3 at `ea99364f`, vendored in `third_party/minimp3` (Layers I to III, float) | CC0 1.0 (`third_party/minimp3/LICENSE`) | follow minimp3 closely; the Layer III Huffman tables (Annex B) and the band tables come from it as format data |
| Vorbis | the Vorbis I specification, free | libvorbis 1.3.7 | BSD-3-Clause (Ubuntu's `libvorbis0a` copyright file) | the specification first, libvorbis for what it leaves open |
| Opus | RFC 6716, whose normative part is the reference decoder of Appendix A, updated by RFC 8251 | the RFC's code; libopus 1.5.2 | the RFC's code: Simplified BSD (the RFC's copyright notice); libopus 1.5.2's `COPYING`: BSD-3-Clause (release tarball, SHA-256 `65c1d2f7…`) | follow the RFC's decoder as RFC 8251 updates it, with libopus 1.5.2 where the RFC calls its repository current |
| AC-3, E-AC-3 | ATSC A/52:2018 and ETSI TS 102 366, both free | none needed; liba52 (GPL-2+, Ubuntu's copyright file) is not read | | the specifications alone |
| Musepack | none for the decoding (musepack.net specifies the SV8 container) | libmpcdec r495 (SV7, SV8) and 1.2.6 (SV4 to SV7), the archives the project pins | 1.2.6: every file BSD-3-Clause. r495: `libmpcdec/*` BSD-3-Clause (`COPYING`, each file's header), **but** `include/mpc/minimax.h`, `mpcmath.h`, `datatypes.h` and `common/fastmath.c` are LGPL 2.1 | follow `libmpcdec/*`; the LGPL files are neither read further nor ported. `mpc_decoder.c` and `mpc_demux.c` include `minimax.h`, which holds only minimum, maximum and clip macros, so nothing of it is needed |
| DTS core | ETSI TS 102 114 V1.6.1, free: the core's syntax and decoding in pseudo-code, with Huffman, VQ and 512-tap QMF tables in Annex D | none needed; libdca (GPL-2+ with LGPL parts) and FFmpeg (LGPL) are not read | | the specification alone |
| Speex | none normative; the Speex manual describes the codec | libspeex 1.2.1, already pinned by `speex_tool.py` and listed in [`THIRD-PARTY-NOTICES.md`](../../THIRD-PARTY-NOTICES.md) | BSD-3-Clause (`COPYING`); only `src/getopt*`, part of the command-line tool, is LGPL | follow libspeex's fixed-point decoder (decision 11) |
| AAC | ISO/IEC 14496-3 and 23003-3: not free, not bought (the lead) | the PacketVideo decoder in AOSP (below), Apache 2.0 with ISO's conditions; faad2 GPL-2+, FFmpeg LGPL; FDK AAC under Fraunhofer's own license; 3GPP TS 26.410's code with no license grant (below) | as below | decision 1 |
| WMA | none published | none: FFmpeg's decoders are the only detailed source (LGPL) | | clean-room, decision 12 |

### AAC's free sources, read for this revision

- **The PacketVideo decoder in AOSP** (`platform/frameworks/av`, tag `android-4.1.1_r1`, `media/libstagefright/codecs/aacdec`, 343 files; listing and file headers fetched from android.googlesource.com):
  - The folder holds `MODULE_LICENSE_APACHE2` and a `NOTICE` with the Apache License 2.0. The files carry "Copyright (C) 1998-2010 PacketVideo", licensed under Apache 2.0.
  - 107 of the 343 files also carry an ISO/IEC SC 29 disclaimer. 70 name Coding Technologies as the original developer (the SBR and PS files, "Copyright (c) ISO/IEC 2002"), 37 name AT&T, Dolby Laboratories and Fraunhofer IIS (the MPEG-2 NBC and MPEG-4 reference decoder).
  - The disclaimer gives users "free license to this software module or modifications thereof for use in products claiming conformance" to the ISO/IEC standards, and warns that use "may infringe existing patents".
  - It says "Copyright is not released for products that do not conform" and "This copyright notice must be included in all copies or derivative works".
  - The tag's `Android.mk` builds Fraunhofer's library by default (`AAC_LIBRARY = fraunhofer`); the PacketVideo files sit in its other branch.
  - The decoder is fixed point with 16-bit output. Its coverage, as the review read it from the file listing (LC, LTP, TNS, noise substitution, mid/side and intensity stereo, SBR and PS), is **unverified** here beyond the file names.
- **The ISO/IEC 14496-5 reference software:** its own license text was not found. The PacketVideo files that derive from it quote the same kind of disclaimer, so it is the same kind of option, and no better.
- **3GPP TS 26.410,** read in the archive `26410-j00.zip` (the latest in 3GPP's listing; it holds the specification `26410-j00.docx` and wraps the code as `26410-i00-ANSI-C_source_code.zip`, the floating-point Enhanced aacPlus encoder and decoder of July 2009). `26410-i00.zip` was not opened:
  - The specification's copyright notification: "No part may be reproduced except as authorized by written permission."
  - The code holds no license file; its only notice is "Copyright (c) Coding Technologies 2004".
  - So nothing grants the right to follow it, and it is not a source.
  - Whether it may even be built as a black-box test tool is unclear, so it is left out.
- **3GPP TS 26.401 and 26.402,** read in `26401-j00.zip` and `26402-j00.zip` (their scope and normative references): no free specification of AAC's decoding hides there.
  - TS 26.401 "describes the detailed mapping from an MPEG-4 bitstream containing Enhanced aacPlus coded audio to PCM sample output", and says the codec "is based on the AAC, SBR and Parametric Stereo coding tools defined in the MPEG-4 Audio standard", to which it refers.
  - TS 26.402 covers only "the error concealment algorithm, SBR parameter downmix and output resampling", with ISO/IEC 14496-3 as its normative reference.

### The clean room

- **WMA:** [wma-standard-format.md](wma-standard-format.md), [wma-pro-format.md](wma-pro-format.md) and [wma-voice-format.md](wma-voice-format.md) describe the syntax to the end of the coefficients. They say so: "No audio is rebuilt, so the dequantization, the noise generator, the line spectral pair curve, the transform and the windows are left out".
  - Each WMA decoder starts with a supplement to its description, written without code by an agent that reads FFmpeg 6.1.1's `wmadec.c`, `wma.c`, `wmaprodec.c` or `wmavoice.c`.
  - An agent that has read none of them implements it, as for TrueHD.
  - Numeric tables it needs (Voice's LSP, gain and codebook tables in `wmavoice_data.h`, any tabulated windows) are format data, generated as `wma_tables.py` generates the others.
- **AAC:** the same split over FFmpeg's `aacdec_template.c`, `aacsbr_template.c`, `aacps.c` and their tables, in the steps of the order. The implementer may read the PacketVideo decoder, which is permissively licensed, for behavior the description leaves open, but follows neither it nor FFmpeg closely (decision 1).
- **Nowhere else:** the free specifications and the permissive sources cover every other format. FFmpeg, faad2, liba52 and libdca are run as black boxes only.

### Notices

- [CLAUDE.md](../../CLAUDE.md) asks for a notice where code follows a BSD, MIT or Apache implementation closely. Each decoder that follows one carries a short notice in the derived files and a row in [`THIRD-PARTY-NOTICES.md`](../../THIRD-PARTY-NOTICES.md) and `scripts/release/licenses`:
  - libvorbis (BSD-3-Clause);
  - the RFC 6716 code (Simplified BSD) and libopus (BSD-3-Clause);
  - libspeex and libmpcdec: their existing rows, extended to the decoders;
  - minimp3, which as CC0 asks for nothing, gets a row and file notices all the same, so the source is on record (the CC0 "magma" colormap has one).
- Code written from a specification (A/52, TS 102 366, TS 102 114, the Vorbis I specification) carries no notice. Its tables are specification data, and [`development.md`](../development.md) names each table's source and check, as [CLAUDE.md](../../CLAUDE.md) asks.
- If the lead takes option (b) of decision 1, the AAC files that follow the PacketVideo decoder carry the Apache notice and, where they follow one of the 107 files, the ISO disclaimer.

## The parser and decoder split

### The entropy pass joins the parsers

[CLAUDE.md](../../CLAUDE.md): "A decoder reads the coded syntax with its parser's code and adds only reconstruction". Today the lossy parsers stop before most of their entropy-coded data:

| Parser | Reads today | Stops before |
| --- | --- | --- |
| MPEG audio | headers; Layer III side information and the reservoir placement of each granule's main data; Layer I and II allocations, scfsi and scalefactors | Layer III scalefactors and Huffman data; Layer I and II samples |
| AAC | every raw data block of Main, LC, SSR and LTP, element by element, the Huffman-coded spectral data included (to reach the fill elements); every SBR and PS codeword | the values, which are not kept; ER AAC and USAC raw data blocks are not read |
| AC-3 | every audio block: exponents, bit allocation, mantissa lengths (to reach the skip fields and the auxiliary data) | the mantissa values; E-AC-3 blocks with the adaptive hybrid transform or enhanced coupling, past that point |
| DTS | the core header and the audio coding header (JOINX, SUBS) | the subframes: side information, scale factors, samples |
| Vorbis | the setup header (codebooks, floors, residues, mappings, modes) and each packet's mode | floors and residues |
| Opus | the TOC byte and the frame packing | the range-coded SILK and CELT data |
| Speex | submodes, in-band requests and the lengths they imply | the field values (LSPs, pitch, gains, excitation) |
| Musepack | SV7 and SV4 to SV6 frames: used, mid/side and noise-filled subbands; SV8 packets | scalefactors and samples; the frames inside SV8 audio packets |
| WMA | standard and Professional frames to the end of their coefficients; Voice frames down to the sizes that decide where a frame ends | the values; Voice's LSPs, pitch, gains and codebook indexes |

The plan's "Later" lists this work as "an entropy pass, format by format, MP3 first". This design makes it the first half of each decoder:

- **Parser code:** the missing syntax goes into the format project outside its `Decoding` namespace, one function per structure with an optional destination. That is the pattern of FLAC's `SubframeSyntax.Read` ([decoders.md](decoders.md#who-owns-the-bits), "Who owns the bits"): given no destination it walks and returns the facts the report shows; given buffers, it also stores the values.
- **In every analysis (decision 10):** the wave 3 figures come from the walk, and they belong in the default report and its goldens, which decoding never changes.
  - The walk finds damage the side information cannot show, such as a Huffman region that ends before or after its stated length. That becomes the parser's findings, and `CodedFrame.IsUnreadable` where the unit cannot be read.
  - The rules of "After the milestone review" in [decoders.md](decoders.md) hold: the walk counts against the parse budget, and its loops stop at the end of the data.
- **Node building stays apart from the walk.** The walk returns facts and stores values; the parser builds nodes from the facts as it does now, lazily where the format builds them lazily (AC-3's records, the blocked children).
- **Cost of the structure pass:** at most 1.5 times today's structure-analysis time on the benchmark files of 300 s ([development.md](../development.md#analysis-speed-of-the-lossy-formats), "Analysis speed of the lossy formats"), **estimate**, measured in each pull request.
- **Two pull requests per format** where the walk changes many goldens: the walk first, so that the decoder's pull request changes only `.decode.json` and `.measure.json` goldens. The lead's "one pull request per decoder" holds for the decoders.

### Lending for lossy frames

The session lends the parser its sample buffers ([decoders.md](decoders.md#the-second-entropy-pass), "The second entropy pass"). For lossy codings they hold quantized values, which `SampleBuffers` serves without a change:

- **Integers only:** every walk above produces integers. They are quantized spectral values, scalefactor and exponent indexes, Vorbis floor values and residue codebook entry numbers, CELT's energy indexes and pulse vectors, and SILK's and Speex's parameter indexes and excitation.
- **The layout is the format's own,** documented on the syntax type that the parser and the decoder share in one project. For Layer III it is, per granule and channel, 576 values, then the scalefactors.
- **Frames are always walked when decoding.** A lossy decoder decodes only frames with `CodedFrame.IsWalked` and has no reader of its own for the entropy-coded data.
  - The 2^24-sample lending ceiling is never reached: AAC's largest frame is 1,024 values in 48 channels, Vorbis's 4,096 in 255.
  - A frame the session lends nothing for is `decode.frame-too-large`.
- **Side information** beyond the lent values (an MPEG header and side information, an AAC element's window and band layout) is read again from the frame's bytes with the parser's code. That is a few dozen bytes per frame, read twice by one piece of code, as [decoders.md](decoders.md) accepts for a configuration.
- **Layer III main data** lies partly in earlier frames' bytes. The parser's walk, which knows the placement, decodes them into the lent planes, so `CodedFrame.Bytes` stays the frame's own bytes and the decoder keeps no reservoir.
- **Why lend rather than read twice:** the rule of [decoders.md](decoders.md) (lend where a second read costs a quarter of the decoding or more) was measured for FLAC. For lossy codings the transform dominates, so a second read may cost less (**estimate**).
  - Lending is chosen anyway: the walk runs in every analysis, lending saves the read whatever its share, and one path is simpler to test than two.
  - The MPEG pull request measures the share once, for the record.

### What moves into parser code, per format

- **MPEG audio:** Layer III scalefactors (with scfsi and MPEG-2's intensity stereo partitions) and the Huffman decoding of the big-values and count1 regions; Layer I and II samples, Layer II's grouped codes included.
- **AAC:** the walk keeps its values (scalefactors, spectral values, pulse, TNS, prediction, LTP, mid/side and intensity flags), and the SBR and PS values it now discards. For steps 13 and 14 of the order: the ER AAC raw data blocks with their error resilience tools, and the USAC elements with their arithmetic-coded spectra.
- **AC-3, E-AC-3:** mantissa values (the grouped codes of bit allocation pointers 1, 2 and 4); E-AC-3's adaptive hybrid transform (gain-adaptive and vector quantization), enhanced coupling and spectral extension parameters.
- **DTS core:** each subframe's side information (prediction modes and coefficients, bit allocation, transient modes, scale factors, joint intensity) and its samples in every coding (plain, Huffman, block code, high-frequency VQ), and the LFE samples.
- **Vorbis:** floor 0 and floor 1 packet decoding, residue classifications and values.
- **Opus:** the range decoder (RFC 6716, 4.1); SILK's header bits, LSFs, gains, pitch lags, LTP filters and excitation (4.2.7); CELT's coarse and fine energy, the bit allocation, the PVQ codes and the anti-collapse, postfilter and transient bits (4.3). CELT's allocation is integer arithmetic the syntax needs to know how many bits a band takes, so it is parser code too.
- **Speex:** the LSP indexes, pitch, gains and innovation codebook indexes the walk skips by size today, and the wideband layers' fields.
- **Musepack:** SV7's scalefactor indexes, their differential coding and the samples, with the SCFI, DSCF and sample tables of libmpcdec, which the parser does not carry yet (it holds only the resolution table, `Sv7Parser.cs` `HuffHdr`, and the SV4 to SV6 region tables); SV8's frames inside audio packets with libmpcdec's canonical tables (`mpc_can_*`).
- **WMA:** the coefficient values the walk decodes and discards, the exponent or LSP values and the noise values; Voice's frame fields.

### Gap wave 3 figures this unlocks

| Figure | Formats | From |
| --- | --- | --- |
| "Coded Bandwidth" | MPEG Layer III | the end of the count1 region per granule ([mpeg-audio.md](../formats/mpeg-audio.md#not-yet-covered), "Not yet covered") |
| "Coded Bandwidth" | Vorbis | the highest residue partition coded and the floor's used range per channel ([vorbis.md](../formats/vorbis.md)) |
| "Coded Bandwidth" | DTS core | the subframes' bit allocation ([dts.md](../formats/dts.md)) |
| "Block Switching" | DTS core (TMODE), Opus CELT (the transient flag) | the subframe side information; CELT's range-coded bits ([coding-graphs.md](coding-graphs.md#the-survey), "later") |
| "Stereo Coding" | Opus CELT (intensity, dual stereo, mid/side of a band) | the range-coded bits ([coding-graphs.md](coding-graphs.md#the-survey), "later") |
| "Coded Bandwidth" | xHE-AAC (USAC), AAC-ELD | their raw data blocks, steps 13 and 14 ([aac.md](../formats/aac.md)) |
| "Stereo Coding" | TrueHD | the matrix parameters of the substream blocks ([truehd.md](../formats/truehd.md)) |

- **DTS's stereo coding** is not in the table: [dts.md](../formats/dts.md) already reports the core's "Stereo Coding" from the header flags.
  - What it leaves open is the stereo coding of the extensions' channels, which comes with the extension decoders this design leaves out.
- **TrueHD's stereo coding** does not wait for a lossy decoder. Its decoder exists and reads the matrix parameters with the parser's code, so the figure needs only a tally in the parser.
- Each figure keeps the name, unit and axis of the series it joins ([CLAUDE.md](../../CLAUDE.md)), and each format's doc moves the line from "not covered yet" to what is read.

## Output and determinism

### The PCM handed to the sink

- **Format:** `SampleFormat.Float32`, planar, as `PcmBlock` defines it, with `PcmFormat.BitsPerSample` 32, so the per-sample charge is `DecodeWork.ForSample(4)`, 22 units.
  - The integer Speex decoder writes 16-bit `Integer` blocks.
- **Scale:** full scale is ±1.0. A decoder never clips, saturates or dithers, so the measurements count overs ("Samples Over Full Scale").
- **Rate:** the stream's output rate as the parser reports it: twice the core rate for HE-AAC, 48 kHz for Opus whatever the header's input rate, the core rate for DTS.
- **Channels:** every coded channel in the format's order, with the parser's `ChannelLayout`, and no downmix. MPEG-2 Layer II multichannel streams output their two base channels, the MPEG-1 compatible stereo, and the measurement's reason says so.
- **Channel changes inside a stream:** the decoder outputs the stream's channel count. A mono frame goes to both channels and a stereo frame in a mono stream is averaged, as RFC 6716 section 2.1.2 describes for Opus.
  - Opus's units may differ from the stream's channel count by design.
  - MPEG streams split only on version, layer, sample rate and free format (`MpegAudioParser.Streams.cs`), so a joined recording can mix channel modes. Decision 13 applies the Opus rule there.
- **Positions:** untrimmed, at the coded stage, as [decoders.md](decoders.md) fixes it. The first unit's output starts at position 0, priming included (decision 8 settles what the measurer does with trims).
- **Format changes:** the parsers end a stream where its configuration changes, so each stream has its own session. A change inside a stream that the parser does not split, beyond the channel rule above, is `decode.frame-invalid` for the unit.

### Decoders fall under the determinism rules

The `--measure` goldens compare figures byte for byte on all six CI platforms and against the NativeAOT build. The figures come from the decoder's PCM, so a decoder whose last bit differs between platforms moves a quantized figure sooner or later, as [spectral-analysis.md](spectral-analysis.md) measured for the FFT. The lossless decoders are integer code and exact; the lossy ones compute in floating point. So:

- **The measurement rules apply** to every lossy decoder and to the parser code it shares that computes in floating point:
  - no fused multiply-add, no `*Native` or `*Estimate` API, no `TensorPrimitives`, no BCL reduction over floating-point spans;
  - no platform math library: cosine tables, windows (sine, Kaiser-Bessel derived, Vorbis's), powers (x^(4/3), 2^(x/4)), Opus's and Speex's exponentials and logarithms and the SBR band edges come from `DetMath` or from tables in the source;
  - `Vector128` element-wise only: a polyphase window or a filter computes several output samples in its lanes, each lane's sum in the scalar order;
  - every sum, butterfly and filter in an order that depends only on the transform's size, with one scalar reference per kernel and a vector path bit-identical to it (tested with `DOTNET_EnableHWIntrinsic=0`, "Vector paths of the decoders" in [development.md](../development.md));
  - each float kernel marked `[MethodImpl(MethodImplOptions.AggressiveOptimization)]`, and no `ArrayPool<T>.Shared`.
- **Two rules differ, so the decoder clause is written out on its own** (see [Contract changes](#contract-changes)):
  - **NaN and huge values:** the measurer counts non-finite input and replaces it with 0. A decoder instead clamps every gain and scale exponent to the range its specification allows before using it (the walk reports a value outside it).
    - So coded data cannot make a NaN or an infinity, and the measurer's count stays as a guard.
  - **Flushing:** the measurer flushes at fixed stream positions, never at the end of a decoder's block. A decoder with recursive state (Opus's de-emphasis and postfilter, Speex's and WMA Voice's synthesis filters, DTS's subband prediction, AAC Main prediction) flushes values below 1e-30 at the start of each of its units and after each gap.
    - A unit is the decoder's own fixed grid, so the result does not depend on how blocks reach a sink.
- **Single precision:** the decoders compute in `float`, as their references do (minimp3, libvorbis, CELT). Tables are computed once in double with `DetMath` and rounded to `float`.
- **Random noise:** AAC's noise substitution, AC-3's dither of zero-bit mantissas, WMA's noise coding and Speex's comfort noise in submode 0 each use a generator.
  - Where a reference defines the generator, the decoder ports it with its seed and state: Speex's (needed to be bit-exact) and CELT's, whose seed is the range coder state the bitstream defines.
  - Where the specification leaves it to the decoder (AAC, AC-3, WMA), one generator with a fixed seed at the start of the stream and after each gap keeps the output deterministic. The cross-check compares those bands by energy (below).

### Integer decoders

- **Opus:** SILK and its resampler are integer code in both of libopus's builds. `silk/float` holds only encoder files (libopus 1.5.2's `silk_sources.mk`), and `silk_Decode` outputs `opus_int16`, so they are exact in any port.
  - CELT has a float and a fixed-point implementation. RFC 6716 makes the float configuration's output normative (Appendix A) and accepts any decoder within `opus_compare`'s threshold with the same final range coder state (section 6).
  - Recommended: the float CELT (decision 11), normative and not limited to the 16 bits of the fixed-point build's `opus_decode`.
- **Speex:** libspeex builds a float or a fixed-point decoder, and neither is normative. The fixed-point decoder is integer, so the port can be bit-exact against libspeex 1.2.1 built with `--enable-fixed-point` from the pinned source (decision 11).
- **The others** define their decoding in real numbers (A/52, TS 102 114, the Vorbis I specification) or have only float references (minimp3, libmpcdec's default build). They are float decoders under the rules above.

### How it is held

- **The scan.** `MeasurementDeterminismTests.BannedCalls` scans the nine lossy format assemblies whole, parsers and decoders together, since the walk's shared code may compute in floating point.
  - The parsers' lossless code in those assemblies (WMA Lossless's reader, DTS's XLL reading) falls under the rules too; it is integer code and needs no change beyond the inventory below.
  - A lossless decoder in those assemblies (WMA Lossless in steps 11 and 12, DTS XLL under "Later") goes into a namespace `<project>.Decoding.Lossless`, which the scan leaves out, so that it may use `Vector<T>` as the FLAC decoder does.
- **The inventory at `origin/main` adb9673, fixed in the groundwork pull request with goldens unchanged:**
  - `Math.Pow`: `Aac/Sbr.cs` line 172 (SBR band edges, rounded to integers), `Vorbis/Setup.cs` lines 271 (`lookup1_values`) and 712 (`float32_unpack`), `Wma/Vlc.cs` line 152 (a codebook's Kraft sum).
  - `Math.Log2`: `Aac/Sbr.cs` lines 153, 231 and 248 (SBR band counts, rounded to integers) and `Ac3/FrameSyntax.cs` line 1019 (a field width).
  - `Enumerable.Sum`, `Max` and `Min`: 36 calls across the nine projects, all over integers, banned by name today.
- **The replacements:**
  - powers of two: `DetMath.PowerOfTwo`, not `Math.ScaleB`, which the scan bans as well;
  - `lookup1_values` and the AC-3 width: integer loops, or `BitOperations.Log2`, which the scan allows;
  - the SBR band edges and counts: `DetMath.Pow` and `DetMath.Ln`, held by an exhaustive test over every rate and header that compares them with the current results. Where the two differ at a tie, the integer the specification intends decides, and the goldens show it;
  - the integer LINQ calls (all 36 are over integers): the scan today reads only the parent's type of a call (`BannedCalls` decodes the TypeSpecification, `MeasurementDeterminismTests.cs` line 405), never the member's signature or its generic instantiation, so it cannot tell a sum of integers from a sum of doubles. Two ways, chosen in the groundwork pull request:
    - **new work in the scan (the design's choice):** decode the MemberReference's signature and the MethodSpecification's instantiation with the existing type provider, and ban an `Enumerable` reduction whose result is `float`, `double` or `Half` (for the selector overloads that is the `Func`'s result type), with negative controls for both overload kinds. [CLAUDE.md](../../CLAUDE.md) bans reductions "over floating-point spans" only, and parsers keep using integer LINQ;
    - **or rewrite** the 36 calls as loops, which keeps the scan as it is at the cost of churn in nine parsers.
- **`DetMath` moves to `Core.Abstractions`** (namespace `Encspect.Core`, public), since decoders and parsers may reference nothing else. The measurements use it from there, and its hash tests move with it.
- **`DecoderDeterminismTests`:** the raw float output of each lossy decoder on its fixtures hashes to the value its `.decode.json` golden holds, on every platform; `scripts/check_aot.sh` with `--measure` covers NativeAOT.
  - The hash is the analyzer's own, a pin of determinism and not a claim of correctness (decision 14).

## The verdict and accuracy

### Coded checksums and the verdict

- [CLAUDE.md](../../CLAUDE.md): parsers "declare every checksum a stream stores". No lossy parser starts a decode session today, so none declares one. Once each starts a session for its decoder, each must declare its codec's checksums:
  - MPEG audio: the CRC-16 (header and side information, or Layer I and II allocations and scfsi), which covers no audio sample;
  - AC-3: CRC1 and CRC2, which together cover the whole syncframe; E-AC-3: one CRC, over the frame ([ac3.md](../formats/ac3.md));
  - ADTS: the CRC over the header and the elements it covers, not the whole block ([aac.md](../formats/aac.md));
  - DTS: the core's HCRC and AHCRC, which TS 102 114 says shall not be applied ([dts.md](../formats/dts.md)), so never compared;
  - Musepack SV8: the stream header's CRC-32 (header only);
  - Vorbis, Opus, Speex, WMA: none of their own. Ogg's page CRCs and Matroska's CRC-32 elements are the container's and stay out of a codec's verification.
- Decision 4 of [decoders.md](decoders.md) makes a stream verified where at least one compared checksum covers all of its audio, "coded or decoded"; a TTA stream "is verified without decoding". Its line 732 deferred giving the lossy formats' CRCs a verification. Declaring them now settles that, so decision 2 sets out the options:
  - **(a) Declare the coded checksums with their coverage, as for TTA.** No rule changes.
    - AC-3 and E-AC-3 streams whose CRCs all hold, with no gap, become "Verified: yes (lossy stream; CRC1 1,000 of 1,000, CRC2 1,000 of 1,000)" in every analysis.
    - MPEG and ADTS streams that store CRCs read "unknown", with their CRC-16 tallies, since those cover no audio or not all of it; those without one (most MP3 files lack the protection bit) read that the stream stores no checksum.
    - Codings that define no checksum of their own (Vorbis, Opus, Speex, WMA, and raw AAC in MP4 and ADIF) get no verification block at all, as WAV streams have none today.
  - **(b) Exempt lossy codings from the declare rule.** Nothing changes in any report. [CLAUDE.md](../../CLAUDE.md)'s item becomes "Parsers of lossless codings declare every checksum a stream stores"; a lossy parser keeps reporting its CRCs as statistics and findings, as now.
  - **(c) Declare them, but keep coded checksums of lossy streams out of the verdict.** The tallies are listed, and `IsVerified` is never true for a lossy stream from coded checksums alone.
    - WavPack hybrid streams stay verified, since their CRCs are over decoded audio; [decoders.md](decoders.md)'s decision 4 and `StreamVerification.IsVerified` gain that clause.
- **Decided on 2026-11-08: (a).** It needs no exception to [CLAUDE.md](../../CLAUDE.md) or [decoders.md](decoders.md).
  - The verdict is true as worded: the coded data that AC-3's CRCs cover are intact, and "lossy stream" says what was verified. Its churn is under [Consequences and churn](#consequences-and-churn).

### What `--verify` means for a lossy stream

- A lossy stream stores no checksum over decoded audio, so a lossy decoder declares none (`IDecoder.DeclaresChecksums` false), as the `pcm` decoder does.
- With decision 3's recommendation, `--verify` alone opens no decode for a lossy stream; `--measure` and a front end's `Output` do. The coded checksums of option 2(a) are the parser's, compared in every analysis whether or not anything is decoded.
- `DecodeSetup.IsLossy` is true for every lossy stream.
- **What a decode tells:** the units that could not be decoded (`decode.frame-invalid`), the withheld output and the decoded samples, in the measurement's status and reason. It never says "intact" or "lossless".
- **Why not decode under `--verify`:** with the walk in the parser, the structure pass already finds the syntax damage a decode would, and reconstruction fails on almost nothing the walk accepted. Decoding a folder of MP3 files would cost minutes to compare nothing.

### Conformance material per format

| Format | Criterion | Test material |
| --- | --- | --- |
| MPEG audio | ISO/IEC 11172-4 and 13818-4 "full accuracy": the RMS of the difference from an exact reference below 2^-15/√12 of full scale, and its largest magnitude at most 2^-14 (**unverified**: from secondary sources; the standards' texts were not read) | see below |
| Opus | RFC 6716 section 6: within `opus_compare`'s threshold for every test vector at every output rate and channel count, and the reference's final range coder state for every packet; RFC 8251 section 11 accepts either set of test vectors | RFC 8251's test vectors, downloaded at a pinned SHA-256 (their license is **unverified**); `opus_compare` built from the RFC's code |
| Vorbis | none: the specification states no accuracy | libvorbis's float output, by the tolerance below |
| AC-3, E-AC-3 | none in A/52 or TS 102 366 known here; Dolby's test signals are not free (**unverified**) | Core Audio's output on the macOS runners (float, **unverified** until the probe records it) and FFmpeg's |
| Musepack | none | libmpcdec's float output |
| DTS core | none in TS 102 114 known here (**unverified**) | FFmpeg's and libdca's output, black boxes |
| Speex | none | bit-exact equality with libspeex 1.2.1's fixed-point build |
| AAC | ISO/IEC 14496-4 and 14496-26 define criteria by output width (**unverified**: not read) | 14496-26's `compressedAdts`, `compressedMp4` and `compressedAdif` with `referencesWav`, and 14496-4's `audio_conformance`, on ISO's site under its license for electronic inserts ("in their original format without any modifications for the purposes specified"), so downloaded at pinned SHA-256 and never committed |
| WMA | none published | Microsoft's decoder on the Windows runners (the WMA check's Source Reader and DMO paths, writing PCM), FFmpeg second |

**MPEG's material, counted** (`tests/conformance/mpeg2-audio.json`, and ISO's site as listed on 2026-11-07):

- The 35 ISO/IEC 13818-4 streams are 22 MPEG-1 Layer II streams with the multichannel extension, 10 MPEG-2 Layer II, one each of MPEG-1 Layer I, MPEG-1 Layer III and MPEG-2 Layer I. Their reference output is 16-bit PCM per channel (`test01_l.pcm`: 66,816 bytes, 29 frames of 1,152 samples of 2 bytes).
- **The 22 multichannel streams are left out of the comparison.** Their reference is the dematrixed L, R, C, Ls, Rs and LFE (`test01.txt`: matrix 3, 3/2), while the decoder outputs the base channels. Rebuilding those from the reference channels needs 13818-3's matrix coefficients, which are not at hand.
- **So the conformance comparison covers 13 streams:** 10 MPEG-2 Layer II, 2 Layer I and a single Layer III stream, MPEG-1.
- **ISO/IEC 14496-26's DVD1** holds 37 Layer III streams (`compressedMpeg12/add-opt/l3_*.mpg`), whose names suggest MPEG-1, MPEG-2 and MPEG-2.5 (**unverified**). The folder is informative by its `README_audio.txt`, and no Layer III reference output was found in DVD1's `referencesWav` (DVD2 and DVD3 were not searched).
- **References for MPEG-2 and MPEG-2.5 Layer III,** and for Layer III beyond the one stream:
  - minimp3 with float output: the decoder follows it closely, so it catches porting errors but not an error the two share;
  - mpg123 and FFmpeg as black boxes, with float output where they offer it (**unverified** for mpg123 1.32's options).
  - These are cross-checks, not conformance. The doc says so.

### The PCM cross-checks

How a decoder's output is compared with a reference's, in its format's cross-check job:

- **What is compared:** the analyzer's PCM, written by a dump tool in the test tree (`tests/Encspect.PcmDump`, not shipped: the library with an `Output` sink that writes each stream's samples and positions), with the reference's output at the coded stage, never from a path that trims.
  - The references' direct paths: minimp3 with float output, libvorbis with granule positions cleared as the official-decoder harness does, libopus's float decoder, FDK fed access units, Core Audio's `AudioConverter`, PacketVideo's decoder built from AOSP (decision 1) and Microsoft's DMO.
- **Alignment:** by position. Positions either side withholds or does not output (gaps, withheld output, units a reference conceals, priming a reference drops) are left out and listed.
- **Against a float reference** (minimp3, libvorbis, libopus float, libmpcdec, Core Audio if it outputs float): per channel, the RMS of the difference at most 2^-15/√12 of full scale (about -101 dBFS) and its largest magnitude at most 2^-14 (about -84 dBFS).
  - **Basis:** the full-accuracy criterion above, which bounds a decoder against an exact reference. A float reference with the same algorithm in single precision is close to exact at that scale, and both differ by far less than 16-bit rounding.
  - The bound is far below what the measurements resolve: tenths of a dB, and a cutoff found from a step of tens of dB.
- **Against a 16-bit or fixed-point reference** (the 13818-4 output, FDK, PacketVideo, Microsoft's decoder, FFmpeg's fixed-point decoders): the analyzer's floats are rounded to nearest and clipped to the reference's width. Then, per channel, the largest difference is at most 2 LSB and the RMS at most 1 LSB.
  - **Basis:** where both round to nearest from nearly the same value, they differ by 1 LSB only near half-LSB boundaries; a reference that truncates differs by 1 LSB on about half of all samples, an RMS of about 0.71 LSB.
  - A fixed-point reference adds errors of its own, which the full-accuracy criterion lets reach 2 LSB at 16 bits (2^-14).
  - How each reference rounds is **unverified** and recorded from the first runs. A reference whose output exceeds the bound is listed per file, not loosened for.
  - Samples where either side reaches full scale are left out where the reference clips (any 16-bit output, `opusdec`'s soft clipping).
- **Opus** adds `opus_compare` and the range coder state, and **Speex** demands equality.
- **Bands of random noise** (AAC noise substitution, AC-3 dither, WMA noise coding) cannot match sample by sample, so frames that use them are compared by the energy of each scalefactor or exponent band, within 0.5 dB (**estimate**, set from the first fixtures) of the mean over all frames: two independent noise fills of equal energy differ by several dB in one frame (median 3.9 dB at 2 bins, 0.6 dB at 128), so the comparison is of the mean, and a band with too few bins and frames to show 0.5 dB is inconclusive ([development.md](../development.md), "Comparing decoded PCM").
  - The strict comparison runs on fixtures without those tools, which every encoder can be told to write.
- **What a reference conceals** is left out and recorded as `explained`, by the lead's rule of 2026-11-01.
- A file outside its bound is a bug until shown otherwise; it goes to the lead as a `pending` item of the official-decoder cross-checks before any bound is widened. FFmpeg is always a second column.

### Fixtures and goldens

- **Encoder-written fixtures** are committed already for every format. Each decoder's pull request adds `.decode.json` goldens with the PCM hash, and `.measure.json` goldens for a few of them.
- **Hand-built damage,** per decoder:
  - a unit whose walk fails (the parser's finding, no decode) and a unit cut short;
  - units removed from the middle (a gap and the withheld output after it);
  - for Layer III, a frame whose main data begin in a removed frame; for Vorbis and Opus, a lost packet; for AAC, an SBR frame coded as a time difference after a gap.
- **Downloaded, never committed:** the ISO conformance streams with their reference PCM, RFC 8251's test vectors, and the real files the format cross-checks already fetch (mp3PRO, xHE-AAC, HE-AAC, Dolby's E-AC-3 kit, DTS, WMA).
- **Tests:**
  - `DecoderFixtureTests` decodes every fixture of the format with a hashing sink, and holds the hash, the decoded samples and the tree, which decoding leaves unchanged.
  - Lossy decoders declare no checksum, so these tests, `MemoryTests` and the fuzzer pass an `Output` sink or `Measure`, or the sessions never open.

## Gaps, withheld output and priming

### What decoding adds to the rule

The lead's rule of 2026-11-01: a unit the decoder cannot decode is a gap, never concealed audio; it raises `decode.frame-invalid` and gets no output, and the measurements count its positions as lost. Lossy decoding adds a question the rule does not answer: the output **after** a lost unit, which in every lossy coding depends on the lost unit's state.

- **Overlapped transforms** (the MDCT of Layer III, AAC, AC-3, Vorbis, CELT and WMA) add the second half of the previous unit's inverse transform to the current one's first half. Without it, the first unit after the gap holds aliasing that does not cancel.
- **Polyphase filterbanks** (Layers I to III, Musepack, DTS, SBR's QMF banks) keep the last 15 subband vectors, 480 samples of history at 32 subbands.
- **Recursive state** (CELT's energy prediction, postfilter and de-emphasis; SILK's and Speex's LPC and pitch state; DTS's subband prediction; AAC Main prediction) converges only gradually.
- **Syntax state:** Layer III main data in earlier frames' bytes, SBR envelopes coded as differences in time, Musepack's scalefactors coded as differences from the previous frame's.

### Withheld output

**Recommendation (decision 4):** after a gap, or after a unit that was unreadable or not decoded, the decoder resets its state and withholds the output that depends on the lost state. The withheld positions are **withheld samples**, a figure of their own:

- **Not lost samples.** Lost samples keep the meaning of [samples-definition.md](samples-definition.md): the declared samples of units that are cut, unreadable or in a stream whose state was lost, a figure at the coded stage and in the measurements. Withheld samples belong to units that were read and decoded; the decoder holds their output back because it would be wrong.
- **One figure with reasons:** "Withheld Samples" in the measurements (JSON `measurements.withheldSamples` and `withheldReasons`, the text line, the GUI caption), with each span's reason: after a gap, after frames of 0 bytes (decision 5), main data not available (decision 7), a mode transition (decision 6, option b only).
- **No finding.** `decode.frame-invalid` was raised for the unit that caused it, where one did.
- **How it is reported (a contract change):** the decoder calls `DecodeContext.Withhold(long firstSample, long samples, WithholdReason reason)` for each span, and the host passes it to the measurer, which counts those positions as withheld and the rest of a position jump as lost.
  - A sink still sees only the jump.

**Partial units.** A withheld span is a count of samples, not of units, so it can end inside a unit:

- The decoder decodes the unit whole, writes the unit's output from the first position past the span (`PcmBlock.Position` = the unit's `FirstSample` plus the withheld part), and reports the withheld part with `Withhold`.
- `IFrameDecoder.Decode` returns the samples it wrote: 0 for a unit withheld whole, the rest for a unit withheld in part. `DecodedSamples` therefore counts samples written.
- The work of the whole unit is charged, so the floor of the charge (`ForSample` times the samples returned) always holds.
- [samples-definition.md](samples-definition.md)'s invariant, `DecodedSamples ≤ coded stage − LostSamples`, still holds. Its equality case gains "and nothing was withheld".

**What each decoder withholds after a gap:**

| Decoder | State that crosses units | Withheld, in samples per channel |
| --- | --- | --- |
| MPEG Layer III | the hybrid transform's overlap (one granule) and the synthesis history (480 samples) | 576 + 480 = 1,056: the first granule, then 480 samples of the next |
| MPEG Layers I and II | the synthesis history | 480 |
| AAC LC | overlap (one frame) | 1,024 |
| AAC LTP | overlap and LTP's history of two frames | 3,072 **estimate** |
| AAC Main | backward-adaptive prediction over every earlier frame | until a frame whose predictor resets have reached every group, as the specification's resets intend |
| SBR, PS | QMF banks, time-differential envelope and noise coding, smoothing, PS's decorrelation | until the first SBR frame coded without time differences, plus the core's 1,024 |
| AC-3, E-AC-3 | overlap (one block) | 256 |
| DTS core | the synthesis history and the subband prediction | 480 for the history; the prediction converges within it (**estimate**). A frame of 256 samples is withheld whole and the next in part |
| Vorbis | overlap with the previous block | the first packet after a reset returns no audio ("Data is not returned from the first frame; it must be used to 'prime' the decode engine", Vorbis I, section 1.3.2). At a stream's start the parser counts nothing for it. After a gap in mid-stream it counts `BlockSize(previous) / 4 + size / 4`, since its previous packet is still the last one before the gap (`VorbisParser.Stream.cs` lines 555-561); the decoder withholds those counted samples with the reason "after a gap". The alternative, a parser that counts none for that packet, changes the goldens of Vorbis fixtures with gaps |
| Opus | CELT's energy prediction, postfilter and de-emphasis; SILK's state; the resampler | 3,840 (80 ms): RFC 7845's pre-roll, "sufficient history to the decoder that it will have converged" (sections 4.2 and 4.6); ends inside a packet where packets are not 80 ms apart |
| Speex | LPC, pitch and enhancer state | one frame (160, 320 or 640 samples), **estimate**, set from the fixtures |
| Musepack SV8 | synthesis history; scalefactors as differences from the previous frame inside a packet, absolute in the key frame that starts each packet (libmpcdec r495, `mpc_decoder_read_bitstream_sv8`) | to the end of the packet |
| Musepack SV7 | synthesis history; scalefactors as differences from the previous frame (`mpc_decoder.c` lines 391-436), with no key frames | decision 15: 32 frames (36,864 samples), libmpcdec's own seek window (`FAST_SEEKING_WINDOW` 32, `mpc_demux.c`), or until every active band has been coded absolutely |
| WMA standard, Professional | overlap; WMA standard's LSP or exponent state | one frame; the supplements settle it (decision 12) |
| WMA Voice | LPC, pitch and postfilter state | set by its supplement |

- **Exact where it can be:** an overlap and a polyphase history are pure functions of the last unit, so the output after the withheld span equals what an undamaged stream decodes to. Recursive state is not exact; its spans are the specifications' convergence times or estimates, and the articles say so.
- **The alternative** (option b of decision 4) outputs everything after a reset. The measurements would then measure aliasing and convergence artifacts as audio.

### The bit reservoir across a lost frame

- A Layer III frame's main data can begin in earlier frames (main_data_begin, up to 511 bytes in MPEG-1 and 255 in MPEG-2). Where they begin before the start of the parser's reservoir, or in a frame the parser counted as lost, the frame cannot be decoded, however intact its own bytes are.
- The parser knows it: it places every granule's main data, and warns today where they begin before the stream (`mpeg.main-data-before-stream`, in 5 committed fixtures).
- **Intact files have it too:** tracks split without re-encoding from a continuous MP3 (mp3DirectCut, mp3splt, CUE splits, stream captures) start with a frame whose main data lie in the previous track. The count over the downloaded samples and a set of split tracks is done in the MPEG pull request, before the lead's final word.
- **Options (decision 7):**
  - (a) the parser counts such a frame as lost and hands it unreadable, [samples-definition.md](samples-definition.md#words)'s "state was lost" case. Intact split tracks would then report Lost Samples.
  - (b) the decoder withholds the frame's output with the reason "main data not available", and the parser stays as it is.
- **Decided on 2026-11-08: (b).** Nothing is damaged in a split track, so "lost" would misname it. Withheld samples say exactly what happened.
- Junk between two frames does not close the reservoir ([mpeg-audio.md](../formats/mpeg-audio.md)), so a frame after junk decodes where its main data lie in the frames before.

### Opus frames of 0 bytes (DTX or lost)

- RFC 6716 section 3.2.1: a frame length of 0 means "No frame (Discontinuous Transmission (DTX) or lost packet)", which a decoder is to conceal.
- **What DTX looks like:** libopus writes a TOC-only packet of 1 byte for each silent frame, and a coded frame after at most 20 of them (`MAX_CONSECUTIVE_DTX` 20, 400 ms, in `silk/define.h`).
- **Options (decision 5):**
  - (a) no decode: the frame and the 80 ms after it are withheld with the reason "frames of 0 bytes", and the decoder resets;
  - (b) decode it as silence without a reset: zeros for its positions, state kept from the last coded frame;
  - (c) libopus's concealment, which breaks the lead's rule.
- **What (a) does to a measurement:** the coded frame that follows a run of 20 DTX frames is 20 ms and lies inside the 80 ms withheld, so a DTX recording's silences are not measured at all. "Withheld Samples" says how much was held back and why, and lost samples stay 0.
- **What (b) does:** the silences read as digital silence, which no player outputs (players conceal with comfort noise).
- **Decided on 2026-11-08: (a),** the RFC's own reading that no frame is available, stated in the figure.

### Opus mode transitions

- RFC 6716, section 4.5: a transition between SILK and CELT, or a change of SILK bandwidth, "is only normatively specified when it includes redundancy. For those without redundancy, it is RECOMMENDED that the decoder use a concealment technique (e.g., make use of a PLC algorithm)".
- libopus 1.5.2 does this in normal decoding of intact data (`src/opus_decoder.c`): at such a transition it decodes 5 ms with its concealment (`opus_decode_frame` without data) and cross-fades it over 2.5 ms into the new mode's output (`smooth_fade`).
  - The test vectors contain such transitions (the review's reading).
- **Options (decision 6):**
  - (a) follow libopus: the 5 ms of concealment and the cross-fade, inside a stream that lost nothing. This is likely needed to pass `opus_compare` on the test vectors (**unverified**);
  - (b) withhold the span the transition affects (the 2.5 ms of the cross-fade), with the reason "mode transition";
  - (c) output the new mode's frame without smoothing, a discontinuity the encoder did not intend.
- **Decided on 2026-11-08: (a).** The lead's rule speaks of units that cannot be decoded, and here nothing is lost; the RFC recommends the behavior, the normative reference has it, and conformance likely depends on it.
  - Had the rule been read as banning any synthesis without data, (b) would have been the choice, with conformance tested only on the vectors without such transitions.

### Priming, delay and trims

Positions stay untrimmed ([decoders.md](decoders.md)). What each format puts before and after the audio, and who states it:

| Format | What the decoder outputs first | Stated by the stream's syntax | Stated by a container or encoder |
| --- | --- | --- | --- |
| MPEG audio | the encoder's delay plus the decoder's (528 + 1 samples by LAME's convention, **unverified** here) | nothing | the LAME tag's delay and padding, the Xing frame count |
| AAC | one frame of priming (two with SBR, **unverified**) | nothing in ADTS | MP4 edit lists, iTunSMPB |
| AC-3, E-AC-3, DTS | the encoder's delay | nothing | MP4 edit lists |
| Vorbis | nothing for the first packet; the rest by overlap | start and end trimming from granule positions | |
| Opus | everything | pre-skip and end trimming (RFC 7845, 4.2 and 4.4) | |
| Speex | the encoder's lookahead (40, 143, 349 samples) | granule positions ([speex.md](../formats/speex.md)) | |
| Musepack | 481 samples of delay (`MPC_DECODER_SYNTH_DELAY`) | the header's sample count and the last frame's end; SV8's beginning silence (`beg_silence`, `mpc_demux.c`) | |
| WMA | half a frame of overlap | the frames' trims (Professional, Lossless); Microsoft's rule for standard | ASF's preroll |

- **Options (decision 8):**
  - (a) the measurer measures the range the stream's own syntax trims to, the cases [samples-definition.md](samples-definition.md) puts in `samples` (Opus, Vorbis, Speex, Musepack, WMA Professional and Lossless, TrueHD's end of stream marker). It counts the rest as "Trimmed Samples", a figure apart from lost and withheld samples;
  - (b) keep [spectral-analysis.md](spectral-analysis.md)'s rule: measure as decoded, untrimmed;
  - (c) also apply the container's and the encoder's figures (LAME tag, iTunSMPB, edit lists).
- **Decided on 2026-11-08: (a).** The measured samples then equal the stream's `samples` where nothing is lost or withheld, and Opus's pre-skip, which holds the decoder's convergence, does not reach the levels and the true peak.
- (a) changes [spectral-analysis.md](spectral-analysis.md)'s rule ("TrueHD: ... the samples to drop at the start are measured too"). TrueHD's goldens do not change: its decoder writes only the samples an end of stream marker leaves (`StreamDecoder.cs`, `_output`), so it has no trimmed range (see [As built in the groundwork](#as-built-in-the-groundwork)).
- The sink's PCM stays untrimmed either way, and the cross-checks compare at the coded stage.

## Cost

### Work units per format

The unit of [decoders.md](decoders.md) applies unchanged: one predictor or filter tap on one sample of one channel, about 0.6 ns. Every decoder charges `DecodeWork.ForSample(4)` (22) per output sample of each channel, nothing per coded byte (its frames are walked), and its stages before it runs them. The drafted weights are all **estimates**; each pull request measures and fixes its own, and `DecoderWorkTests` holds it to at most twice the FLAC reference's time per unit:

| Stage | Units | Formats |
| --- | --- | --- |
| dequantization, per coefficient | 2 | every transform coding |
| mid/side, intensity stereo, coupling, rematrixing, channel transforms, per coefficient they touch | 1 per input channel | MPEG, AAC, AC-3, Vorbis, WMA, CELT |
| inverse transform, per output sample | 2 × log2 of its length (22 for 2,048, 16 for 256, 10 for Layer III's 36) | MPEG III, AAC, AC-3, Vorbis, CELT, WMA |
| window and overlap-add, per output sample | 2 | as above |
| polyphase synthesis, 32 bands and 512 taps, per output sample | 26 (16 taps and the matrixing) | MPEG, Musepack, DTS |
| TNS, per coefficient filtered | its order | AAC |
| AAC Main prediction, per coefficient | 10 | AAC Main |
| LTP, per frame | a forward transform of the frame plus its taps | AAC LTP |
| SBR, per output sample | 60 (analysis and synthesis banks, generation, adjustment) | HE-AAC, USAC |
| parametric stereo, per output sample | 40 | HE-AAC v2 |
| Vorbis floor, per coefficient | 2 | Vorbis |
| CELT postfilter and de-emphasis, per sample | 6 | Opus |
| SILK synthesis, per internal sample | the LPC order (10 or 16) plus 5 for LTP | Opus |
| Opus resampler, per output sample | 16 | Opus |
| Speex synthesis and enhancer, per sample | 40 | Speex |
| DTS subband prediction, per subband sample predicted | 4 | DTS |

- **Silence charges what it does.** An inverse transform of an all-zero spectrum is zero, so a decoder skips it and charges only the window and overlap of the previous unit's tail, as Monkey's Audio charges its silent frames less. The output is identical.
- **Per frame:** a decoder charges a frame before it decodes it, at most 2^27 units a charge. Every frame fits but Opus's largest: a 120 ms packet in 255 channels is about 1.5 × 10^8 units, so Opus charges and checks the token per stream of a multistream packet.
- **The floor of the charge** compares the work charged with `ForSample(4)` times the samples returned. Withheld units return fewer samples than they charge, so they never trip it.

### Per-byte allowance

`IDecoder.WorkPerInputByte` is derived per decoder from the cheapest coded audio its encoders write, as Monkey's Audio's 8,192 was derived from its cheapest symbol, and checked against the silence its encoders write. Where silence needs more than is worth declaring (Vorbis, Opus), the floor of the budget covers it for hours, stated below. All figures but the measured packet sizes are **estimates**, each checked by its pull request with a silent fixture:

| Decoder | Cheapest legitimate frames | Units per byte they need | Declared |
| --- | --- | --- | --- |
| MPEG audio | constant-bitrate frames: 8 kbps MPEG-2.5 at 8 kHz; a silent frame costs as much as any other at that rate | about 600 | 2,048 |
| AAC (one decoder for LC and HE-AAC) | silence: 6 bytes per 1,024 stereo samples (an AAC LC file of 60 s of stereo silence in `scratchpad/lossy/sil`, 2,574 of its 2,585 frames; its encoder is not recorded), transforms skipped; HE-AAC v2 at 16 kbps with 48 kHz output | about 8,200 for silence (2,048 × 24 / 6), 4,800 for HE-AAC v2 | 8,192 |
| Vorbis | coded audio at about 32 kbps, 44.1 kHz stereo; silence: oggenc 1.4.2 at `-q 3` writes 60 s of stereo silence as 2,586 packets of 1 byte, one per long block returning 1,024 samples (the review's measurement, confirmed on its file) | about 1,300 for coded audio; about 49,000 for silence (1,024 × 2 × 24) | 2,048 |
| Opus | 6 kbps at 48 kHz output, about 2,800 mono and 4,200 stereo at 8 kbps; silence: opusenc's defaults write 60 s of stereo silence as 3,001 packets of 3 bytes (the review's measurement, confirmed) | about 19,200 for silence (960 × 2 × 30 / 3) | 8,192 (decision 18) |
| Speex | the 2.15 kbps vocoder mode; ultra-wideband at about 4 kbps | about 2,200 to 3,600 | 4,096 |
| AC-3, DTS, Musepack, WMA standard | constant-bitrate or bitrate-bounded frames at their lowest rates | under 1,000 | 2,048 |
| WMA Voice | 4 kbps at 16 kHz | about 2,400 | 4,096 |

- **Silence is covered by the floor, not by the allowance,** for Vorbis and Opus, whose silent packets would need far more than any allowance worth declaring: 2^35 units cover about 4.5 hours of silent Vorbis stereo at 44.1 kHz (24 units a sample) and about 3.3 hours of silent Opus stereo at 48 kHz (30 units a sample), and a file's coded audio adds its own allowance on top.
  - Longer silence in a small file stops with `decode.limit`, as [decoders.md](decoders.md) says of digital silence in any coding.
  - A crafted file of 100 MB stops after about 2 minutes at 2,048 units a byte and about 8 minutes at 8,192, as Monkey's Audio's does; Opus at 16,384 would double that (decision 18).
- **The floor alone** covers anything under 16 MiB at 2,048 units a byte and under 4 MiB at 8,192. Opus frames of 0 bytes are not decoded (decision 5), so DTX costs nothing.
- **Free-format MPEG** frames can be smaller than any listed bitrate; the MPEG pull request checks the free-format fixtures against the 2,048.

### Memory per stream

Nothing grows with the stream ([decoders.md](decoders.md#memory), "Memory"). A session holds one frame's lent and output buffers and the state that crosses units:

| Decoder | Largest state, **estimate** | Ceiling |
| --- | --- | --- |
| MPEG audio | 2 channels × (576 overlap + 1,024 synthesis history) floats | negligible |
| AAC | per channel: 1,024 overlap, 2,048 LTP history, SBR's QMF buffers (about 40 KB), PS; up to 48 channels from a PCE | about 4 MB |
| AC-3, E-AC-3 | up to 16 channels × 256 overlap | negligible |
| DTS core | 6 channels × 512 synthesis history, plus LFE | negligible |
| Vorbis | up to 255 channels × 4,096 overlap and an 8,192-sample block | about 12 MB |
| Opus | up to 255 streams of decoder state (about 30 KB each) and a 120 ms frame | about 10 MB |
| Speex, Musepack | 2 channels | negligible |
| WMA | 8 channels × a frame of up to 8,192 samples (Professional) | about 1 MB |

A frame past a ceiling is `decode.frame-too-large`, and `MemoryTests` gets a long stream of each coding.

### Parallel decoding

[parallel-decoding.md](parallel-decoding.md) is approved and deferred. It takes the formats whose frames decode apart from each other, and no lossy coding is one, but some come close:

- **Finite history** (an overlap, a polyphase history): a worker decodes one unit before its batch and discards that unit's output, and then its state equals the serial run's, bit for bit, since that state is a pure function of the unit.
  - Such codings: MPEG audio, AAC LC, AC-3, Vorbis, and Musepack SV8 batched by packets (its scalefactors refer across frames only inside a packet).
  - The cost is one unit decoded twice per batch.
- **Infinite history** (CELT's energy prediction, SILK, Speex, DTS's prediction, AAC Main): not parallel without changing the output.
- **Recommendation:** none of it in the lossy decoders' pull requests. The table goes to the parallel design's "Independence per format" when that design is built.

### Speed targets

- **The reference time:** each decoder's pull request times the fastest reference for its format on the benchmark files of 300 s ([development.md](../development.md#analysis-speed-of-the-lossy-formats), "Analysis speed of the lossy formats"): minimp3's tool, FFmpeg, `oggdec`, `opusdec`, `mpcdec`, `speexdec`, FDK through its harness. Each reference's own start is taken out: its time on a file of 1 s is subtracted.
  - For scale: FFmpeg's MP3 decoder takes 0.56 s for the 300 s MP3 of 4.8 MB, start included.
- **The analyzer's time:** the analysis with decoding to a sink that keeps nothing, minus the analysis without decoding, so its start and the structure pass are out too.
- **The target:** that time at most 2.5 times the reference's, in the JIT and the NativeAOT build, the middle of the 1.7 to 3.2 times the lossless decoders reached after two speed passes ([decoders.md](decoders.md)).
  - A decoder above it gets a speed pass before the next one starts.
- **In absolute terms:** an hour of stereo MP3 at 44.1 kHz is about 2 × 10^10 units at the drafted weights, about 12 s at 0.6 ns a unit (**estimate**); the MPEG pull request measures it.
  - The budget counts units, not time, so those units fit under its floor of 2^35 at any speed; a decoder at the work tests' factor of 2 is slower (about 24 s), never cut.

## Consequences and churn

What the recommendations change outside the decoders, so that none of it arrives unannounced:

- **Goldens:**
  - decision 2(a): the streams of codings that define a checksum (MPEG audio, ADTS, AC-3, E-AC-3, DTS, Musepack SV8) gain a `verification` block in their `.report.json`, and the text report a "Verified" line: about 540 goldens, counted from the codecs the committed goldens name (492 in `mpeg`, `aac`, `adts`, `ac3` and `dts`, 47 Musepack SV8, and the AC-3 and E-AC-3 tracks of `mp4` and `matroska`). AC-3 and E-AC-3 streams read "yes (lossy stream; …)", the others "unknown" or that they store no checksum;
  - decision 14: `.decode.json` goldens of lossy fixtures gain `pcmSha256`, a field no `.decode.json` has today (771 files hold none);
  - decision 8(a): every measured stream with a syntax trim gains `trimmedSamples`; TrueHD's goldens do not change (the decoder never writes the samples a marker drops);
  - decision 4: `.measure.json` goldens with gaps gain `withheldSamples`;
  - decision 7(a) only: MPEG goldens with `mpeg.main-data-before-stream` gain Lost Samples (5 committed fixtures);
  - the groundwork's determinism fixes change none.
- **Report fields:**
  - `measurements.withheldSamples` and `withheldReasons`, and `trimmedSamples` with decision 8(a): new JSON fields, additive, so `schemaVersion` stays 1; the text line gains them where they are above 0; the CSV has no measurement counts of lost samples (its `Lost Samples` is the parser's figure), so it gains none;
  - `IsLossy` true on every decoded lossy stream writes `"isLossy": true` in JSON (`ReportJsonWriter`) and "(lossy stream)" in the Measured line (`MeasurementText`) of every measured lossy stream, not only in a verification;
  - the CSV's `Is Verified` (filled with `--verify` only) becomes true for verified AC-3 files under decision 2(a).
- **GUI:**
  - the caption "The audio of this … stream is not decoded here" ([gui.md](../gui.md)) no longer appears for lossy streams that have a decoder, and "Decode Audio" becomes enabled for them;
  - the waveform draws 16-bit sample codes ([gui.md](../gui.md)), so unclipped overs beyond full scale are drawn at the lane's edge. The levels count them, and the hover text says so;
  - the caption for withheld output, next to the one for frames not decoded;
  - the stream line shows the verdict of decision 2(a);
  - measuring a folder of lossy files takes the decoders' time.
- **CLI and CI:**
  - [`docs/cli.md`](../cli.md): what `--verify` and `--measure` do for lossy streams, and the verdict;
  - `scripts/check_aot.sh` with `--measure` and CI's measurement jobs run longer by each decoder's time on the fixtures;
  - each format's cross-check job gains the PCM comparison and its downloads.
- **Docs:** each format's "Decoding" section; [`docs/development.md`](../development.md) for the cross-checks, the tolerance and the downloads; the articles (below).
- **Articles:** `lossy-decoding` (what Encspect's decoding of a lossy stream is, that it is not bit-exact with other decoders and within what it agrees, withheld output, priming); `withheld-samples`; the verification article gains the lossy stream's coded checksums; format articles for the new walks (Layer III Huffman regions, Vorbis floors and residues, CELT, SILK, DTS subframes).

## Contract changes

- **`DetMath`** moves to `Core.Abstractions`, public, with its tests. `Encspect.Host.Measurement` uses it from there.
- **`DecodeContext.Withhold(long firstSample, long samples, WithholdReason reason)`** and the enum `WithholdReason` (`AfterGap`, `EmptyFrame`, `MainDataUnavailable`, `ModeTransition`), passed by the host to the measurer. A front end's sink sees only the position jump, as now.
- **The determinism scan** covers the nine lossy format assemblies but their `Decoding.Lossless` namespaces, tells integer from floating-point `Enumerable` reductions by their signatures (new work, above), and the inventory above is fixed.
- **[`CLAUDE.md`](../../CLAUDE.md),** in the groundwork pull request:
  - Layout, `src/Encspect.Core.Abstractions`, at the end of its sentence: "…, the shared lazy-children list (`BlockedChildren`) with the record containers it is built from (`ChunkedList`, `ByteStore`), and the deterministic math (`DetMath`) that measurements and lossy decoders share."
  - Architecture rules, a new item after the measurement determinism item: "Lossy decoders, and the parser code they share that computes in floating point, are deterministic under the same rules as measurement code (no fused multiply-add, no platform math library but `DetMath`, no `*Native` or `*Estimate` API, no `TensorPrimitives` and no BCL sum, dot product, maximum or average over floating-point spans, only element-wise `Vector128`, sums and transforms in an order that depends only on their length, each float kernel `[MethodImpl(MethodImplOptions.AggressiveOptimization)]`, no `ArrayPool<T>.Shared`), with two rules of their own: a decoder clamps every gain and scale exponent to the range its specification allows instead of counting NaN, and flushes recursive states below 1e-30 at the start of each of its units and after a gap. A lossless decoder in the same project lives in `<project>.Decoding.Lossless`, outside these rules. `DecoderDeterminismTests` pins hashes of the lossy decoders' PCM, and `MeasurementDeterminismTests` scans their assemblies."
  - The plan's "Later" item on lossy decoders, which holds the lead's gap rule, gains the withheld output with the lead's decision; [CLAUDE.md](../../CLAUDE.md) holds no item on gaps today.
  - Under decision 2(b) only, the item "Parsers reach decoding only through `StartDecoding` on their context, and declare every checksum a stream stores." ends instead "…, and, for lossless codings, declare every checksum a stream stores."
- **[decoders.md](decoders.md),** under decision 2(c) only: decision 4 and `StreamVerification.IsVerified` gain "a lossy stream is verified only by checksums over decoded audio".
- **[spectral-analysis.md](spectral-analysis.md),** under decision 8(a): the trimmed range and "Trimmed Samples".
- **`IFrameDecoder.Decode`'s comment:** "the samples per channel by which the stream's decoded output advanced" becomes "the samples per channel it wrote, which is the advance unless it withheld output (`DecodeContext.Withhold`)". The signature does not change.
- No change to `IDecoder`, the signatures of `IFrameDecoder`, `CodedFrame`, `DecodeSetup`, `SampleBuffers`, `PcmBlock` or the router.

### As built in the groundwork

What the groundwork pull request did differently from the text above, or added to it:

- **`IDecodeSession.Trim(firstPosition, endPosition)`** is a new member of the parser's side of the contract (the design named none). The measurer needs the trimmed range before the blocks it applies to arrive, and the parsers know it (Opus's pre-skip from the header, the end from the last granule position) while the session's `DecodeSetup` is built when the stream starts. The range is in absolute positions at the coded stage; the leading trim holds until the measuring has begun, the trailing one for blocks written after the call. Lost and withheld samples stay counted at the coded stage, trimmed range or not. No parser calls it yet.
- **TrueHD:** nothing to change. The decoder writes `Samples - DroppedSamples` of each unit and the parser's positions advance by the same, so the "samples to drop" are never in the PCM. The design's remark that TrueHD's goldens change was wrong; no committed golden changed.
- **The records:** `StreamMeasurements.WithheldSamples` (with `LeadingWithheldSamples` and `TrailingWithheldSamples`, carved out of the leading and trailing lost samples, and `WithheldByReason` as a value type so that records compare by value), `LeadingTrimmedSamples` and `TrailingTrimmedSamples`. `SkippedSamples` (lost and withheld together) and its leading, trailing and inner parts are what the GUI places frames and columns by. A jump that is withheld whole still counts as a gap.
- **`Trim`** ignores a widening of the end once a block past the old end has arrived, and throws for a negative first position or an end before it.
- **Pending spans** are kept between two blocks (at most 1,024, contiguous spans of one reason merged); past that a span is counted as withheld at once, as if it lay in a jump.
- **`DetMath.Log2`** was added for the SBR band counts, which the inventory listed as `Math.Log2`.
- **The Vorbis project** gained `InternalsVisibleTo` for the tests, like the other format projects.
- **Not done:** no `.decode.json` golden has `pcmSha256` yet, since no lossy decoder exists; `PcmHashSink` and the golden writer are ready for the first.

## Rollout

The steps follow [Order](#order); each is one pull request for the decoder, with the parser's walk before it where that walk changes many goldens.

0. **Groundwork** (no format):
   - the contract changes above, and the determinism fixes with goldens unchanged;
   - `DecoderDeterminismTests` with a scripted float decoder;
   - `tests/Encspect.PcmDump` and `scripts/fixtures/compare_pcm.py`, with both bounds and the band-energy comparison;
   - withheld and trimmed samples in the measurer and the writers; the `lossy-decoding` and `withheld-samples` articles; [CLAUDE.md](../../CLAUDE.md)'s lines.
1. **MPEG audio:**
   - the parser: the Layer III walk and the Layer I and II samples, Coded Bandwidth for Layer III, the checksum declarations of decision 2;
   - the decoder, following minimp3;
   - the checks: the 13 comparable 13818-4 streams against their reference PCM; minimp3, mpg123 and FFmpeg as cross-checks.
2. **Vorbis:** floors and residues in the parser with Coded Bandwidth; the decoder; libvorbis and FFmpeg.
3. **Opus:**
   - the parser: the range decoder and the SILK and CELT syntax, with CELT's block switching and stereo coding;
   - the decoder;
   - the checks: RFC 8251's vectors with `opus_compare` and the range state; libopus 1.5.2 built without OSCE, deep PLC and DRED (all off by default in its `configure.ac`); FFmpeg.
4. **AC-3**, and 5. **E-AC-3:** the walk of the tools the parser stops at, then their reconstruction; Core Audio and FFmpeg; Dolby's E-AC-3 kit as downloaded input.
6. **Musepack:** the SV7 tables and the SV8 frames in the parser first, then the decoder.
7. **DTS core:** the subframes in the parser, with Coded Bandwidth and block switching, then the decoder.
8. **Speex.**
9. **AAC LC,** after its clean-room description (decision 1), and 10. **HE-AAC**.
11. **WMA standard** and 12. **WMA Professional,** each after its clean-room supplement, with Microsoft's decoder writing PCM on the Windows runner.
13. **AAC Main, LTP and ER AAC,** with AAC-ELD's Coded Bandwidth, and 14. **USAC,** with xHE-AAC's.
15. **WMA Voice,** if the lead wants it.

Each step brings:

- its fixtures and goldens, and its notices and articles;
- its `DecoderWorkTests` cases: its worst cases (Layer III short blocks with intensity stereo at the smallest frames, Vorbis 8,192-sample blocks in 255 channels, Opus 120 ms hybrid and multistream packets) and a silent stream;
- `MemoryTests` and fuzz coverage, and its format doc's "Decoding" section.

A milestone review follows steps 1 and 3: the pattern, then the hardest exact check.

## Decisions

The project lead decided all of them on 2026-11-08: every recommendation as listed, and the patent question as written in item 17. The numbers are this design's.

1. **AAC's source:** a clean-room description of FFmpeg's AAC, SBR and PS decoder, then an implementer who has not read FFmpeg; the PacketVideo decoder (Apache 2.0) is read for behavior the description leaves open and built as a cross-check if the probe succeeds (it is built for Android's `Android.mk` and outputs 16-bit fixed point), never followed closely.
   - Considered: following the PacketVideo decoder closely, under Apache 2.0 and, for 107 of its files, ISO/IEC SC 29's disclaimer, which licenses only "products claiming conformance".
   - Considered: 3GPP TS 26.410's code, which grants no license; TS 26.401 and 26.402 defer to ISO/IEC 14496-3.
2. **The verdict for lossy streams:** the parsers declare their codings' coded checksums with their coverage. AC-3 and E-AC-3 read "verified (lossy stream)" in every analysis, MPEG and ADTS streams with CRCs "unknown" and those without one that they store none; codings without checksums (Vorbis, Opus, Speex, WMA, raw AAC) get no verification.
   - Considered: exempting lossy codings from [CLAUDE.md](../../CLAUDE.md)'s declare rule.
   - Considered: declaring them but keeping coded checksums of lossy streams out of the verdict, through a [decoders.md](decoders.md) change.
3. **`--verify` for lossy streams:** decodes nothing, since no checksum covers the decoded audio; `--measure` decodes.
   - Considered: decoding under `--verify` to report units that do not decode.
4. **Output after a gap:** the decoder resets and withholds the output that depends on the lost state, reported as "Withheld Samples" apart from lost samples, partial units by position (`DecodeContext.Withhold`).
   - Considered: resetting and outputting everything, artifacts included.
5. **Opus frames of 0 bytes:** withheld with the 80 ms after them, reason "frames of 0 bytes", so DTX silences are not measured.
   - Considered: silence without a reset.
   - Considered: libopus's concealment, which the lead's rule of 2026-11-01 excludes.
6. **Opus transitions without redundancy:** libopus's 5 ms of concealment and 2.5 ms cross-fade, as RFC 6716 section 4.5 recommends, inside a stream that lost nothing.
   - Considered: withholding the cross-fade's span.
   - Considered: no smoothing.
7. **Layer III frames whose main data are not available** (split tracks, damage): the decoder withholds them, reason "main data not available"; the parser is unchanged. The MPEG pull request counts the affected files.
   - Considered: the parser counting them as lost.
8. **Trims in the measurements:** the measurer measures the range the stream's syntax trims to and counts the rest as "Trimmed Samples".
   - Considered: measuring untrimmed, as before.
   - Considered: also applying container and encoder trims.
9. **Decoder-side gains:** none but Opus's output gain.
   - Considered: no gain at all.
   - Considered: each reference's default.
10. **The entropy pass:** in every analysis, giving the wave 3 figures, with a structure pass up to about 1.5 times slower.
    - Considered: only when decoding, which cannot give the figures.
11. **Integer or float:** Opus with the float CELT (normative, unclipped; SILK and the range state exact); Speex with the fixed-point decoder, bit-exact, the enhancer on.
    - Considered: the fixed-point CELT, bit-exact but limited to 16 bits.
    - Considered: the float Speex decoder within the tolerance.
12. **WMA:** clean-room supplements of the reconstruction for standard and Professional; Voice last, and only if wanted.
    - Considered: no WMA decoding.
13. **A change of channel mode inside an MPEG stream:** the decoder keeps the stream's channels (a mono frame on both, a stereo frame averaged).
    - Considered: the parser splitting the stream, with golden churn.
14. **A PCM hash in `.decode.json`:** `pcmSha256`, the analyzer's own output pinned for determinism.
    - Considered: no pin, with determinism held only by the `.measure.json` goldens.
15. **Musepack SV7 after a gap:** 32 frames withheld, libmpcdec's seek window.
    - Considered: withholding until every active band was coded absolutely.
16. **Determinism:** the measurement rules with the decoder clause, `DetMath` in `Core.Abstractions`, the scan over the lossy assemblies but their `Decoding.Lossless` namespaces. The two [CLAUDE.md](../../CLAUDE.md) edits are applied by the main session.
    - Considered: tolerance in the `.measure.json` goldens of lossy streams.
17. **Patents** (the lead's answer): no format waits for advice.
    - A short note in [`README.md`](../../README.md) and [cli.md](../cli.md) says that the decoders exist for analysis only (no playback or conversion) and that users are responsible for complying with the laws where they live.
    - Before the HE-AAC, USAC, E-AC-3 and WMA Professional decoders are built, the lead decides whether they ship as source only or stay out of the release binaries.
18. **Opus's units per byte:** 8,192, from its coded audio at the lowest bitrates, with silence covered by the floor (about 3.3 hours of stereo) and a crafted 100 MB file stopping after about 8 minutes, as for Monkey's Audio.
    - Considered: 16,384, which doubles that worst case and still does not cover silence.
    - Considered: 32,768, which covers silence in full at about 33 minutes for a crafted 100 MB file.

## Unverified claims and estimates

- **Unverified:**
  - the ISO/IEC 11172-4 and 13818-4 full-accuracy criterion (from secondary sources) and the 14496-4 and 14496-26 criteria;
  - the license of RFC 8251's test vectors; the absence of free AC-3 and DTS conformance material;
  - what the 14496-26 Layer III stream names stand for, and Layer III reference output in DVD2 and DVD3;
  - the PacketVideo decoder's coverage beyond its file names;
  - that libopus's transition handling is needed to pass `opus_compare`; that the test vectors contain such transitions (the review's reading);
  - LAME's 528 + 1 decoder delay and HE-AAC's priming;
  - how each reference rounds to 16 bits, Core Audio's and mpg123's float output, and the defaults of FFmpeg and Core Audio for decoder-side gains;
  - the expiry of MP3 patents.
- **Estimates:** every work weight and per-byte figure, the memory figures, the 1.5 times of the structure pass, the 0.5 dB band-energy bound, the withheld spans for recursive state (AAC LTP, DTS, Speex), the share of a second read for lossy codings, and the hour of MP3 in about 12 s.

## Sources read

- **The repository** at `origin/main` adb9673:
  - [`CLAUDE.md`](../../CLAUDE.md); [`docs/plan.md`](../plan.md#later) ("Later", "Phase 3, continued");
  - [`docs/design/decoders.md`](decoders.md), [`spectral-analysis.md`](spectral-analysis.md), [`samples-definition.md`](samples-definition.md), [`official-decoder-crosschecks.md`](official-decoder-crosschecks.md), [`parallel-decoding.md`](parallel-decoding.md), [`coding-graphs.md`](coding-graphs.md) (the concept table), [`mpeg-clean-room-rules.md`](mpeg-clean-room-rules.md) (its source statement), and the status and scope lines of [`wma-standard-format.md`](wma-standard-format.md), [`wma-pro-format.md`](wma-pro-format.md) and [`wma-voice-format.md`](wma-voice-format.md);
  - the format docs' "not covered" lines and what they read; [`docs/gui.md`](../gui.md) (the captions and the waveform); [`development.md`](../development.md#analysis-speed-of-the-lossy-formats) ("Analysis speed of the lossy formats", "Where the format tables come from", the conformance downloads);
  - [`THIRD-PARTY-NOTICES.md`](../../THIRD-PARTY-NOTICES.md) and [`third_party/README.md`](../../third_party/README.md); `tests/conformance/mpeg2-audio.json`;
  - the shapes of the lossy parsers (the banned calls listed, `MpegAudioParser.Streams.cs`, `Ac3Analysis.cs`, `Sv7Parser.cs`, `OldStreamParser.cs`, the codec IDs), `MappedBytes`, `DetMath` and `MeasurementDeterminismTests`.
- **Licenses and reference code:**
  - `third_party/minimp3/LICENSE` and `minimp3.h`;
  - libopus 1.5.2 (release tarball from GitHub, SHA-256 `65c1d2f7…`): `COPYING`, `silk_sources.mk`, `silk/define.h`, `celt/mathops.h`, `src/opus_decoder.c`, `configure.ac`;
  - libmpcdec 1.2.6 and r495 (the archives the Musepack work pinned): `COPYING`, file headers and includes, `mpc_decoder.c` (scalefactor coding, key frames), `mpc_demux.c` (the seek window, `beg_silence`), `decoder.h`;
  - libspeex 1.2.1: `COPYING`, the decoder defaults in `nb_celp.c` and `sb_celp.c`, `speexdec.c`;
  - the PacketVideo decoder in AOSP `platform/frameworks/av`, tag `android-4.1.1_r1`, `media/libstagefright/codecs/aacdec`: the listing, `NOTICE`, `MODULE_LICENSE_APACHE2`, `Android.mk` and the header comments of all 343 files (for the license and disclaimer counts);
  - 3GPP TS 26.410 j00: the specification's copyright notification and the code package's README and file notices;
  - Ubuntu's copyright files of `libopus0`, `libvorbis0a`, `libogg0`, `libspeex1`, `libmpcdec6`, `libfaad2`, `liba52-0.7.4`, `libdca0` and `libfdk-aac2` (their license fields only).
- **Specifications:** RFC 6716 (sections 1, 2.1.2, 3.2.1, 4.2.9, 4.5, 6, Appendix A); RFC 8251 (abstract, test vectors); RFC 7845 (sections 4.2, 4.6, 5.1); the Vorbis I specification (section 1.3.2 and the accuracy statements); ETSI TS 102 114 V1.6.1 (table of contents, core decoding overview).
- **ISO's publicly available standards site,** 2026-11-07: the listings of `ISO_IEC_13818-4_2004_Conformance_Testing/Audio` (and the contents of `test01.tar.gz`), `ISO_IEC_14496-4_2004_Conformance_Testing` and `ISO_IEC_14496-26_2010_Bitstreams` (DVD1's `README_audio.txt`, `compressedMpeg12/add-opt` and `referencesWav`), and the license text those pages show.
- **Not read, by the clean-room rules:** FFmpeg, faad2, liba52, libdca, mutagen, libtta, getID3, mp3guessenc and mctoolamed source, and the LGPL files of libmpcdec r495.
