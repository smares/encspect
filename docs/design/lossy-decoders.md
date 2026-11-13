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
| `ac-3` and `e-ac-3` | step 1: AC-3 (A/52); step 2: E-AC-3 (spectral extension, adaptive hybrid transform, enhanced coupling; the independent substreams, [as built](#as-built-e-ac-3)) | transient pre-noise processing (a decoder option, A/52:2018 Annex E 3.7.2), E-AC-3 frames at a reduced sample rate (fscod2) and AC-3 with bsid 9 and 10 (half and quarter rate): the parser notes that A/52 does not specify their bit allocation (`Ac3Analysis.cs` line 262), and no free source for it is known; the decoder-side gains of decision 9 |
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
| L1 | ALAC (lossless; built, see [decoders.md](decoders.md#as-built-alac)) | medium: ALAC in MP4 and Matroska gets measurements | lowest: Apple's reference decoder (Apache 2.0), which the parser already follows | small and exact; checked bit for bit against Apple's decoder |
| L2 | TTA (lossless; built, see [decoders.md](decoders.md#as-built-the-tta-decoder)) | low to medium | low to medium: the author's public pages describe the container only, so the decoder follows a clean-room description of libtta (LGPL) written by an agent that read it, as for TrueHD (decision 1 of [decoders.md](decoders.md)); `tta -d` and FFmpeg stay black boxes | small and exact; the author's reference codec and FFmpeg decode the same bits, so the check is bit for bit (built: [decoders.md](decoders.md#as-built-the-tta-decoder)) |
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
| 15 | WMA Voice | low | high: a speech codec with many tables, clean-room | last (the lead wants it, 2026-11-08) |

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

The plan's "Later" listed this work as "an entropy pass, format by format, MP3 first". This design makes it the first half of each decoder:

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
- **TrueHD's stereo coding** does not wait for a lossy decoder. Its decoder exists and reads the matrix parameters with the parser's code, so the figure needs only a tally in the parser; it is planned with the coding graphs of 0.4, item 4 ([plan.md](../plan.md#04-free-decoders-and-routes-complete)).
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
- **`test22`, the MPEG-1 Layer I stream with an extension, is left out too:** its archive holds only the dematrixed channels, not the base stereo.
- **So the conformance comparison covers 12 streams:** 10 MPEG-2 Layer II, the MPEG-2 Layer I stream and the single MPEG-1 Layer III stream with an extension (`test23`, whose base stereo is `test23.pcm`).
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
- Junk between two frames does not close the reservoir ([mpeg-audio.md](../formats/mpeg-audio.md)), so a frame after junk decodes where its main data lie in the frames before, unless the main data reach back across junk or a destroyed header where the gap could have held a frame (a stretch of at least the stream's smallest frame length): the frames that read from it are withheld like the output after a gap (a frame a decoder cannot decode is a gap, never concealed audio).

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
| Opus | up to 255 streams of decoder state (about 30 KB each), a 120 ms frame, and the packets held back for the end trimming (at least 120 ms, kept as bytes: about 3 MB at 255 streams of 2.5 ms frames; the records of the entropy pass are made when a packet is fed) | about 10 MB of decoding state; measured with the hold on 255 streams of 2.5 ms frames (2,600 packets of 1,274 bytes): 51 MB peak, of which the report keeps 18 MB (before the hold kept bytes: 188 MB) |
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
  - The plan's gap rule ("Decisions" in [plan.md](../plan.md#decisions), once under "Next") gains the withheld output with the lead's decision; [CLAUDE.md](../../CLAUDE.md) holds no item on gaps today.
  - Under decision 2(b) only, the item "Parsers reach decoding only through `StartDecoding` on their context, and declare every checksum a stream stores." ends instead "…, and, for lossless codings, declare every checksum a stream stores."
- **[decoders.md](decoders.md),** under decision 2(c) only: decision 4 and `StreamVerification.IsVerified` gain "a lossy stream is verified only by checksums over decoded audio".
- **[spectral-analysis.md](spectral-analysis.md),** under decision 8(a): the trimmed range and "Trimmed Samples".
- **`IFrameDecoder.Decode`'s comment:** "the samples per channel by which the stream's decoded output advanced" becomes "the samples per channel it wrote, which is the advance unless it withheld output (`DecodeContext.Withhold`)". The signature does not change.
- No change to `IDecoder`, the signatures of `IFrameDecoder`, `CodedFrame`, `DecodeSetup`, `SampleBuffers`, `PcmBlock` or the router.

### As built in the groundwork

What the groundwork pull request did differently from the text above, or added to it:

- **`IDecodeSession.Trim(firstPosition, endPosition)`** is a new member of the parser's side of the contract (the design named none). The measurer needs the trimmed range before the blocks it applies to arrive, and the parsers know it (Opus's pre-skip from the header, the end from the last granule position) while the session's `DecodeSetup` is built when the stream starts. The range is in absolute positions at the coded stage; the leading trim holds until the measuring has begun, the trailing one for blocks written after the call. Lost and withheld samples stay counted at the coded stage, trimmed range or not. No Vorbis parser call uses it (As built: Vorbis states its trims with `TrimInner(firstPosition, samples)`, a range inside the stream: Gap Trimming, the start and the end trimming).
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
   - the checks: the 12 comparable 13818-4 streams against their reference PCM; minimp3, mpg123 and FFmpeg as cross-checks.
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
15. **WMA Voice,** after its clean-room supplement (the lead, 2026-11-08).

Each step brings:

- its fixtures and goldens, and its notices and articles;
- its `DecoderWorkTests` cases: its worst cases (Layer III short blocks with intensity stereo at the smallest frames, Vorbis 8,192-sample blocks in 255 channels, Opus 120 ms hybrid and multistream packets) and a silent stream;
- `MemoryTests` and fuzz coverage, and its format doc's "Decoding" section.

An independent review of the step follows steps 1 and 3: the pattern, then the hardest exact check.

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
12. **WMA:** clean-room supplements of the reconstruction for standard, Professional and Voice; Voice last. The lead wants Voice decoded, as Speex is (2026-11-08).
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
    - **Decided (the lead, 2026-11-13):** the E-AC-3, HE-AAC, USAC and WMA Professional decoders are built and included in the release binaries, like the others.
18. **Opus's units per byte:** 8,192, from its coded audio at the lowest bitrates, with silence covered by the floor (about 3.3 hours of stereo) and a crafted 100 MB file stopping after about 8 minutes, as for Monkey's Audio.
    - Considered: 16,384, which doubles that worst case and still does not cover silence.
    - Considered: 32,768, which covers silence in full at about 33 minutes for a crafted 100 MB file.

## As built: MPEG audio

What the MPEG audio pull request (rollout step 1) did differently from the text above, or added to it. Branch `feat/mpeg-decoder`; the decoder is `src/Encspect.Formats.Mpeg/Decoding`, the entropy pass is in the parser's own namespace.

- **The walk is part of the frame loop.** `AddFrame` runs the Layer III walk (`Layer3Walker`, over `Layer3Syntax`) for every complete frame with or without a decoder, on the main data the bit reservoir's runs place (`DecodeFeed.GatherMainData`), so the figures need no second pass over the file. The Layer I and II sample codes (`Layer12Scale`) are walked only where a session decodes, since they give no figure. A frame's coded lines are kept in its record (a `ushort`), from which the "Coded Bandwidth" tally and series are derived as the Layer I and II ones are.
- **Streams and sessions.** The parser starts the streams that `AddStreams` makes (the same `IsSameStream` rule, at most 64) while it reads: the session starts at the stream's first audio frame with the first header as its `Configuration`, and `AddStreams` finishes it with the stream's frame CRC-16 tally. A VBR header frame is not fed, so positions count audio frames, as `StreamInfo.Samples` does. MPEG audio in a container (WAV, MP4) reaches the decoder through the codec route: the inner `ParseContext` of `Parse(CodecContext)` takes the route's context as `decodingOwner` (a new optional parameter of `ParseContext`), so its sessions route as the owner's do and the host ends them with it.
- **The lent layout** (`LentLayout`): per channel one plane of 1,316 integers, in Layer III two granules of 576 quantized values, 40 scalefactors as coded and 40 intensity stereo positions, then a flags entry; in Layers I and II the sample codes by part, subband and time slot. A frame whose main data are not available is lent with the flag and no values.
- **Decision 2 (the verdict):** the frame CRC-16 is declared as `ChecksumCoverage.Header` with the protected frames as Stored, so a stream reads "unknown" (with its CRC counts) or, with a failed CRC, "no". Every MPEG golden gained a `verification` block.
- **Decision 7 (main data not available):** as written: the parser walks nothing for a frame whose placement is before the stream, overlaps, reaches a VBR header frame or overflows, and the decoder withholds the frame whole (reason "main data not available"). Not written in the design and chosen here: every frame whose main data are not available is withheld whole, and the decoder starts again from the empty state after it, so the output after it is withheld like after a gap (1,056 samples of Layer III, 480 of Layers I and II), at the start of a stream too. A frame whose main data reach back across a destroyed frame header, or across junk of at least the stream's smallest frame length, is lost the same way; junk shorter than a frame leaves the frames behind it exact.
- **Decision 4 (gaps):** a frame the parser cannot read (cut by the end of the data or the next frame, side information that breaks, part2_3_length beyond the data) is handed unreadable and counts as lost; the host marks the next frame as following a gap, and so does any stretch of junk between frames (`AddGap`). After a gap the decoder withholds 1,056 samples of Layer III and 480 of Layers I and II with the reason "after a gap".
- **Decision 13 (channel changes):** as written. The decoder opens with the stream's first header and refuses a frame of another version, layer or sample rate (`decode.frame-invalid`).
- **New finding:** `mpeg.huffman-overrun` (warning, with a count): a granule whose Huffman-coded values run past its part2_3_length. It fires on none of the committed encoder-written fixtures; the hand-built `MpegWalkTests` frames show it.
- **Accuracy** (the PCM cross-checks, [development.md](../development.md#mpeg-audio-decoding)): every fixture the references decode agrees with minimp3's float output to 3.3 x 10^-7 of full scale at most (RMS 3 x 10^-8) but for two hand-built files that minimp3 reads differently from the specification (`side-info-mono.mp3` and `side-info-long-blocks.mp3`: it takes private bits as scfsi bits), and with FFmpeg's float decoders but for `reservoir-across-gap.mp3` and `external-xing-mpeg2.mp3` (below); the ISO/IEC 13818-4 streams the decoder covers (12 of 35; `test22`'s only references are dematrixed) are within 1 LSB of 16 bits, `test34` within 8 x 10^-7 of full scale. **`external-xing-mpeg2.mp3`** (an MPEG-2 intensity stereo file, frames 61 to 64): the three references disagree with one another, and the analyzer keeps minimp3, which it follows operation by operation. Largest differences in four granules, measured with FFmpeg 6.1.1 (`mp3float` and `mp3` agree to 3 x 10^-5) and mpg123 1.32.5: minimp3 and FFmpeg 0.0236 of full scale (RMS 3.7 x 10^-4 over the file), minimp3 and mpg123 0.0221 (RMS 2.2 x 10^-4), FFmpeg and mpg123 0.0174 (RMS 2.9 x 10^-4). The cause is not established.
- **Where the decoder differs from minimp3:** x^(4/3) is `DetMath.Pow` for every magnitude (minimp3 interpolates above 128); the DCT-II adds the vector code's order; the private bits of the side information are not taken for scfsi; and the intensity stereo test needs joint stereo.
- **Cost, measured** ([development.md](../development.md#mpeg-audio-decoding)): the structure pass with the entropy pass is 1.35 times `origin/main`'s on a 300 s MP3 of pink noise at 128 kbps (the bound is 1.5; the first version was 1.9 to 2.4 times); decoding is about 1.5 to 2 times minimp3's tool, inside 2.5. The weights are 22 units a sample plus 41 (Layer III) or 29 (Layers I and II); `DecoderWorkTests` gives 0.55 to 1.17 against the FLAC reference's time per unit, so they are generous. The declared 2,048 units a byte holds for the smallest Layer III frames (about 500 units a byte in MPEG-2.5 at 8 kHz) and not for tiny free-format frames, which the 2^35 floor covers for hours. Memory: 200,000 frames of silence hold nothing per frame.
- **The intensity stereo bound** (the 0.3 follow-up, built): "Intensity Stereo Bound" in Hz for all layers, from the walk for Layer III ([coding-graphs.md](coding-graphs.md)).
- **Not built:** the inverse transform of an all-zero spectrum is not skipped, so silence is charged in full.

## As built: Vorbis

What the Vorbis pull request (rollout step 2) did differently from the text above, or added to it. The decoder is `src/Encspect.Formats.Vorbis/Decoding`; the entropy pass (`PacketWalker`, `SetupModel`, `Codebook`, `PacketBits`) is in the parser's own namespace.

- **The walk** runs in every analysis for every packet that codes a block (`VorbisParser.Decoding.cs`): the floors of every channel, then the residues of every submap, read with the setup header's codebooks as libvorbis 1.3.7 reads them (`mapping0_inverse`, `floor1_inverse1`, `floor0_inverse1`, `_01inverse`, `res2_inverse`), including what a packet's end does: a floor that ends is unused, a residue that ends keeps what it read (type 0 drops the partition whose vectors were not all read, as `vorbis_book_decodevs_add` adds them last). The walk is integer-only; with a decoding session it stores the floor values after unwrapping and the codebook entries each residue partition read (`LentLayout`), and the decoder reads no coded bit. `SetupModel` re-reads the setup header without nodes, refusing what libvorbis refuses (a test holds it to the header walk's warnings on every fixture). A setup that states more codebook entries than 4,096 plus 64 for each byte of its header (or 4 Mi in all), or whose decoded vectors would hold more than 2^23 values, is refused where libvorbis accepts up to 8 Mi entries a book: an ordered codebook states millions of entries in a few bytes, and the tables cost the analyzer time and memory the file's size does not pay for (the review's 14 KB file of 64 such links took 24 s). The decoding tables of a codebook are built when a packet first decodes with it, and the decoder takes the parser's model (`SetupModel.Share`) instead of reading the header again, so a setup's tables are built once per analysis and only for the books its packets use. The ceiling is about 12 MB of overlap and spectra plus at most 32 MB of vectors; the entry arrays stay under 4 Mi entries times 13 bytes only for a header of about 64 KB.
- **Sessions.** The session starts at the stream's first audio packet where libvorbis would accept all three headers: the identification header (version 0, framing bit, channels and rate above 0, block sizes 64 to 8,192), the comment header (the lengths of `_vorbis_unpack_comment` and its framing bit) and the setup header. Its `Configuration` is the identification header's 30 bytes and the setup packet. A stream libvorbis refuses has no session and no verification. The gate also refuses a sample rate above 2^31 - 1, which libvorbis accepts (it stores a long): a refusal of the analyzer's own, without a fixture, since `PcmFormat` takes an int. The Ogg harness and Matroska's CodecPrivate reach it through the same codec route.
- **Gaps, as the design decides (decisions 4 and 8):** a packet the container cut is handed unreadable (its block's samples count as lost, no output); the host marks the packet after it as following a gap, and so does a lost page (`MappedBytes.FollowsGap`). The decoder then resets, decodes the packet for its overlap and withholds all `blockSize(previous) / 4 + size / 4` samples the parser counts for it, with the reason "after a gap"; the packet after that decodes exactly. **Not in the design, chosen here:** a packet that is empty, not audio or names no mode is not handed to the decoder at all: libvorbis returns nothing for it and keeps its state, so the output equals that of the stream without the packet (a test holds this), and it is not a gap.
- **Trims (decision 8):** the Vorbis parser does not call `Trim` (it stays for Opus's pre-skip and the formats that trim at the stream's ends); `external-oggenc-q5.ogg` measures 66,150 samples of 67,136 coded, 986 trimmed, as `oggdec` writes. The start, end and Gap Trimming are ranges inside the stream, stated with `IDecodeSession.TrimInner(firstPosition, samples)` before the packet that carries the granule position: libvorbis discards them from that packet's own samples (`vorbis_synthesis_blockin`), the first ones for the start trimming and Gap Trimming and the last ones for the end trimming, so `Trim` is not used. A cut last packet is lost whatever the padding says, so the report trims it and the measurements, which count it as lost, do not. The measurer cuts the ranges out of the blocks (the measured stretches resume after them as after a jump, with no lost sample): a range before the first measured sample counts as leading, one after the last as trailing, the others as "inner" trimmed samples (`InnerTrimmedSamples`, `innerTrimmedSamples` in the JSON next to `trimmedSamples`), and the measurements keep what the report's Samples state (lost and withheld positions stay counted at the coded stage, trimmed or not; a test holds it on every Vorbis fixture). Matroska's DiscardPadding is the end trimming of a Vorbis track and is per block (RFC 9559): capped at the samples of that block's frames (`CodecConfiguration.DiscardPaddingFrames`, `ContainerPadding`; `vorbis.discard-padding-large`, `opus.discard-padding-large` and `aac.discard-padding-large` for more), and the parser feeds the decoder behind by those frames (`_held`, one packet for an unlaced block, flushed in order by a cut packet or at the end of the stream), since the padding means something only against the total. The feed delay lives in the Vorbis parser; Opus in Matroska needs the same for its decoder ([plan.md](../plan.md), the decoders item), where moving it into the host's session is to be decided. Gap Trimming exists only in Ogg (it comes from granule positions); Matroska has none.
- **Coded Bandwidth** (the gap wave 3 row): per packet, the upper edge of the highest coefficient that a residue partition reads an entry for, in whole hertz (`end * rate / block size`, rounded half up); statistics "Coded Bandwidth" (lowest, most common, highest) and the series "Coded Bandwidth" and "Coded Bandwidth Over Time", concept `codedBandwidth`, Hz on the axis of the other formats'. The design named "the highest residue partition coded and the floor's used range per channel"; the floor adds nothing here, since a floor 1 curve spans the block (its last post is at n/2) and an unused floor leaves the channel silent, which the residue then does not code. The lead decided on 2026-11-11 that the figure stays residue-only. Two independent walks hold the figure: the manifests' Python walk (`vorbis_walk.py`, from the specification, for every fixture) and the PCM agreement with libvorbis (a wrong entry would show in the samples).
- **Output** is planar floats at full scale, the stream's rate and channel order (the specification's, section 4.3.9). The inverse MDCT is the DCT-IV of the coefficients through a complex FFT of n/4 points (`InverseMdct`), unnormalized: libvorbis's gain equals it. The windows are the specification's `sin(pi/2 sin^2(...))`, applied as libvorbis does it in `vorbis_synthesis_blockin` from the sizes of the two blocks (libvorbis ignores the window flags a long block states; so does the decoder, and the parser's `vorbis.window-flag-mismatch` says when they disagree). Floor 0 follows the exact `vorbis_lsp_to_curve` that libvorbis compiles (lsp.c leaves the table versions undefined), with the gain's exponent limited to a float's range and each spectral value limited to 1e30, so that the decoder never writes NaN or infinity where libvorbis's unlimited gain overflows (`vorbis.walk-capped` and `vorbis.setup-too-large` report the two places where the analyzer's own bounds change what is read); its work is charged per coefficient by the order, and the cross-check compares randomized packets of a setup with a floor 0.
- **Determinism:** `VorbisDecoderFixtureTests` pins the hash of every fixture's PCM in its `.decode.json` golden (decision 14); the float code is scalar single precision (the floors' integer lines, the transform's butterflies, the windows; floor 0's curve computes in double with `DetMath`) with the tables from `DetMath`, no fused multiply-add, and `MeasurementDeterminismTests` scans the assembly.
- **Accuracy** ([development.md](../development.md#vorbis-decoding)): every Ogg fixture libvorbis decodes agrees with it to 1.8 x 10^-7 of full scale at most, the hand-built ones exactly, and 18 randomized variants (encoders' setups, random audio packets) to 3.3 x 10^-7 of their peaks; FFmpeg's native decoder agrees to the same on the mono and stereo files libvorbis and its derivatives wrote and differs on `external-ffmpeg-vorbis.ogg`, the stream FFmpeg's own encoder wrote (largest 0.286, RMS 0.056): listed in the cross-check as an open difference (channel 0 is bit-identical, the whole difference is in channel 1, and against the encoder's input FFmpeg's decoder reaches 15.1 dB SNR and libvorbis 10.8 dB), with libvorbis kept as the reference.
- **Cost:** per packet the decoder charges 22 units a sample and, per coefficient of each channel with a floor, 2 for the floor, 2 for the residue, 2 per bit of the block size's length for the transform and 2 for the window and the overlap (2 alone for a channel with an unused floor), and 1 per coefficient of each coupled channel. `DecoderWorkTests` and `MemoryTests` hold it (figures in [development.md](../development.md#vorbis-decoding)). The declared 2,048 units a byte stands, with the silence covered by the floor of the budget as the table says.
- **Not built:** the inverse transform of an all-zero spectrum is skipped, but a silent channel's overlap is still charged; the floor figure above; a cut last packet is lost, not trimmed, whatever the padding says, and a track's padding reaches the decoder only for the frames still held (those of the block that carries it). A packet whose records could need more than the 2^24 ints the host lends is not decoded and reports `decode.frame-too-large`; the bound is tight (the records a pass can store).

## As built: Opus

What the Opus pull request (rollout step 3) did differently from the text above, or added to it. The decoder is `src/Encspect.Formats.Opus/Decoding`; the entropy pass (`OpusWalker`, `CeltSyntax`, `SilkSyntax`, `RangeDecoder`) is in the parser's project.

- **The walk** runs in every analysis for every packet whose framing is valid (`OpusParser.Decoding.cs`): each SILK and CELT frame is read to its end with the range decoder, which gives its final range coder state, CELT's transient flag, stereo coding and intensity stereo bound; where the session decodes, the walk also writes the integers the decoder replays (`IntWriter`, lent through the session), so the decoder reads no coded bit. A CELT frame that needs more bits than it holds is `opus.frame-overrun`, as libopus refuses it; a hybrid frame whose redundancy length reaches past its data is `opus.redundancy-overrun` (libopus ignores the redundancy and conceals the CELT part, so the analyzer's gap differs from libopus there by design: nothing is concealed).
- **Figures** (the gap wave 3 rows): statistics "Block Sizes" (entries "Long" and "Short", as Vorbis) and "Stereo Coding" (each frame once, with the entries of MPEG and AAC: "Left/right stereo" is CELT's dual stereo, and a frame with intensity bands is "Intensity stereo" over left and right or "Mid/side and intensity stereo") with the category series of the same names, "Frames With Redundancy", and "Intensity Stereo Bound" (lowest and highest hertz, and the numeric series per packet: the name, unit and axis the plan gives every format with the concept; MPEG's categories "Intensity Stereo Bounds" stay a tally of bands).
- **Gaps and empty frames (decisions 4 and 5):** a lost, empty (discontinuous transmission) or undecodable frame is never concealed: the decoder starts over and withholds 80 ms (3,840 samples), "after a gap" or "empty frame". The only synthesis without coded data is decision 6's: inside a stream that lost nothing, at a change of mode or of SILK bandwidth without redundancy, 5 ms of the mode before it (libopus's concealment code, `opus_decode_frame` without data) cross-faded over 2.5 ms into the new frame. RFC 6716 section 4.5 only RECOMMENDS it (the transitions are normative only with redundancy); it is what the reference decoder does, and without it the output at those transitions differs from the reference's by up to 0.1 of full scale (vector 10). The redundant frames (section 4.5.1) and the state resets (section 4.5.2) are normative. No lost or empty frame reaches this code, and a frame that follows a gap or an empty frame starts over, so it has no mode before it to conceal.
- **Trims (decision 8, and the precedence below):** the pre-skip is stated once before the first packet; the end trimming (the last page's granule position or Matroska's DiscardPadding) is stated when the stream ends, with the packets still held back by the feed delay: the parser holds at least 5,760 samples (the largest packet) and the padding's block, so `Trim` reaches the decoder before them and an end trimming longer than the last packet reaches back over earlier ones. **Trimming takes precedence over withholding and loss:** positions the syntax trims are not samples of the stream, so `StreamMeasurer.Result` counts a leading or trailing gap (and the withheld spans in it) only outside the trimmed ranges (decided by the lead on review); `OpusTrimTests` holds measured + lost + withheld = Samples on every Opus fixture.
- **Final range:** the state of every frame equals libopus's on all packets of both sets of test vectors; the packet's state is the exclusive or of its frames' and redundancy's.
- **Output** is planar floats at 48 kHz, full scale, with the header's output gain; the multistream mappings of families 0, 1, 2 and 255 are applied, family 3 is not decoded.
- **Determinism and accuracy:** see [development.md](../development.md#opus-decoding); the cross-check builds libopus without SIMD.
- **Cost:** per frame at least 22 units a sample and channel, plus 22 (CELT) or 24 (SILK) per sample and channel and 8,000 for each CELT frame, calibrated with `DecoderWorkTests` to at most 1.26 times the FLAC reference's time per unit ([decoders.md](decoders.md#as-built-the-opus-decoders-work-weights)).

## As built: AC-3

What the AC-3 pull request (rollout step 4) did differently from the text above, or added to it. The decoder is `src/Encspect.Formats.Ac3/Decoding` (E-AC-3 is step 5); the entropy pass (`FrameSyntax`, `IntWriter`, `DecodeFeed`) is in the parser's own namespace. It is written from ATSC A/52:2018 alone; liba52 and FFmpeg were run as black boxes (liba52 through the system's shared library, its source unread).

- **The walk:** `FrameSyntax` already stepped over the mantissas by the length the bit allocation gives. It now reads their codes in every analysis (A/52 7.3: the grouped codes of bap 1, 2 and 4 with checks 18 to 20 of 7.10.2, which the parser had left out, and the fixed widths of the rest) and, where a session decodes, writes each block's records into an `IntWriter` that is copied into the lent buffers (`DecodeFeed`): the blksw and dither flags, the coupling state with the effective coordinates, the rematrixing flags, and for every channel the number of bins and `exp | bap << 5 | code << 9` per bin. The layout is documented on `FrameSyntax.WriteBlock`. A grouped code above its quantizer's range is limited to the highest and is `ac3.block-inconsistent` (the existing code for the checks of 7.10.2): it fires on none of the encoder-written fixtures and on the three `derived-ffmpeg-*delta*` ones, whose mantissa bits were rewritten without re-encoding them (their change files and manifests say so, and their rows of `official-decoders.json` were extended by the rule that file's other findings follow: the converter decoded their units).
- **Streams and sessions:** one session for each AC-3 program (`ProgramKey`: a run of independent frames with one configuration, `bsid` up to 8), started before the program's first frame is walked (the walk needs to know whether to write) and ended with the stream's figures; `Verification` joins `StreamInfo`. Containers reach it through the codec route (`Ac3CodecParser`, MP4, Matroska, ASF, WAV and IEC 61937 bursts alike). The positions count the program's independent frames at 1,536 samples, the cut and broken ones included, so they equal `StreamInfo.Samples`.
- **Decision 2 (the verdict):** crc1 and crc2 are declared as coded-data checksums (`ChecksumNames` "CRC1", "CRC2"); crc2 covers all the program's audio where no frame is lost and no gap lies between its frames. Every AC-3 golden gained a `verification` block (the change files list them), and a stream whose frames all pass reads "verified (lossy stream)".
- **Decision 4 (gaps):** a frame the parser counts as lost (cut, broken syntax) is handed unreadable; the frame after it, and a frame after bytes between frames that are no frame (`ac3.sync-lost`, a sample that is no frame), follows a gap: the decoder resets the overlap and the dither generator and withholds that frame's first block, 256 samples, "after a gap". A frame whose CRC fails is decoded: the parser counts it as read, not lost (a decision the design did not take, chosen here to keep the report's lost samples equal to the measurements'); its `CodedFrame.IsDamaged` is set.
- **Decision 8 (trims):** none. A/52 states no priming or padding, and the edit lists of MP4 are not applied (the design's option (c) was not chosen); `Ac3SamplesTests` holds measured + lost + withheld = Samples on every AC-3 fixture.
- **Decision 9 (gains):** none: no dynamic range control, dialogue normalization or downmix. The output is every coded channel in the coded order at full scale.
- **Not decoded:** E-AC-3 (built next, below) and bsid 9 and 10 (a program whose first frame has a bsid above 8 has no session; a later such frame is lost to the measurements, though the report does not count it lost), the dependent substreams after an AC-3 core (the core's channels are decoded), and AC-3 in an MP4 or Matroska track whose samples are encrypted or compressed so that the container hands over no frame.
- **The dither** is a 32-bit linear congruential generator with a fixed seed, reset at the start of the stream and after each gap, uniform between -0.707 and 0.707 (A/52 7.3.4: "the optimum scaling"); a coupled channel dithers its own copy of the coupling channel's zero-bit bins after the coordinate has scaled them (7.4.3). The cross-check compares the dithered files by the energy of bands.
- **The window** is a Kaiser-Bessel derived window with the parameter 5 computed with `DetMath`; the specification gives the 256 values of its Table 7.33 to five digits and no formula, and the test compares each with the table (the largest difference is 4.99 x 10^-6, half a unit of the last digit).
- **Accuracy** ([development.md](../development.md#ac-3-decoding)): with the dither flags cleared the 20 fixtures liba52 0.7.4 decodes agree with its float output to 2.4 x 10^-7 of full scale at most (RMS 3.8 x 10^-8; the bound is 6.1 x 10^-5), and with FFmpeg 6.1.1's float decoder (a black box) to 2.0 x 10^-5, but for one block of `external-aften-stereo-192k-blocksw.ac3` (largest 0.025, RMS 9.5 x 10^-4, all in the long channel of a block whose other channel is block switched while rematrixing flags are in force; the analyzer agrees with liba52 there, and against the encoder's input signal it and liba52 are within 0.0021 in that block where FFmpeg is off by 0.0243, and A/52 7.5.4 puts no condition on block switching). The two `derived-ffmpeg-*-delta*` files (mantissa codes no quantizer has) and `stream-changes.ac3` (three streams) are not compared; the dithered files of the encoders agree with both by the energy of bands to 0.02 dB.
- **Cost, measured** ([decoders.md](decoders.md#as-built-the-ac-3-decoders-work-weights)): 22 units a sample and channel on top of the floor; `DecoderWorkTests` gives 0.65 to 1.02 against the FLAC reference's time per unit. The structure pass with the mantissa codes is 1.31 times `origin/main`'s on a file of 114 MB of 5.1 at 448 kbps and 1.33 times on one of 123 MB of stereo at 192 kbps (the bound is 1.5; the first version was 1.4 to 1.5 times), measured with the published CLI as CSV, startup included.
- **Not built:** the inverse transform of an all-zero spectrum is skipped but the window and overlap of the block before are still charged in full, so silence is charged as any frame; the figures the entropy pass could add (per-channel block switching, exponent strategies) stay in the coding graphs' "later".

## As built: E-AC-3

What the E-AC-3 pull request (rollout step 5) did differently from the text above, or added to it. The decoder is `Eac3Decoder` and `Eac3FrameDecoder` in `src/Encspect.Formats.Ac3/Decoding`, on the foundation of AC-3's (its transforms, window, dequantization, standard coupling and rematrixing are the same code); the entropy pass is the parser's `FrameSyntax` (`FrameSyntax.Eac3.cs`). It is written from ATSC A/52:2018 Annex E, with ETSI TS 102 366 V1.4.1 beside it; FFmpeg was run as a black box and no source of it was read.

- **The walk:** `FrameSyntax` now reads every block of every E-AC-3 frame. Enhanced coupling's strategy and parameters (the begin and end sub-bands, the band structure, each coupled channel's amplitudes, angles, chaos values and transient flag) are read from A/52:2018 Annex E 2.3.3.14 to 2.3.3.26, where ETSI V1.4.1 leaves the angles and chaos values as reserved bits. A channel that uses the adaptive hybrid transform has its bit allocation run with the high efficiency table (Table E.2.1) in block 0 and its six blocks' mantissas read there: the gain adaptive quantization mode and gains, then for each bin its vector's index or its six values with their tags. Their length depends on the values, so the block is read in every analysis (`ReadAht`); the later blocks have no mantissa bits for the channel. The finding codes `ac3.aht-not-read` and `ac3.enhanced-coupling-not-read` are gone. A composite gain word above 26 is `ac3.block-inconsistent`. Real files with the transform read to the end of every frame (`check_eac3_samples.py`).
- **The record** the walk lends (`FrameSyntax.WriteBlockEac3` documents the layout): AC-3's, with per block the spectral extension's band layout and coordinates, the coupling mode (none, standard, enhanced) with the enhanced parameters, and per channel a kind (coded in the block, the transform's six blocks, the transform's later block) with, for the transform, each bin's exponent, hebap, gain and the index or the six values.
- **Streams and sessions:** one session for each independent substream (every substream ID of an E-AC-3 file is its own program and stream, the independent frames of strmtyp 0 and 2), started before the program's first frame is walked, as for AC-3, under the decoder ID `e-ac-3`; a frame at a reduced sample rate (fscod2) and bsid 9 and 10 have no session. The dependent substreams are walked, reported and decoded: their blocks join the program's, merged by first sample, and the channels are placed by chanmap into the program's layout (a hand-built 5.1 core with a dependent substream, `eac3-signal-dependent`, gives four channels; Core Audio's output is its reference in the macOS job). A gap is noted per program (`GapsSeen`), since the substreams of a file interleave.
- **Decision 2 (the verdict):** E-AC-3 declares its one CRC ("CRC"), over the frame; a stream whose frames all pass it reads "verified (lossy stream)". An AC-3 core with dependent frames keeps crc1 and crc2.
- **Decisions 4 and 8 (gaps, trims):** as for AC-3: a lost frame is a gap, the frame after it withholds its first block (256 samples), no trims. `Ac3SamplesTests` holds measured + lost + withheld = Samples on every AC-3 and E-AC-3 fixture of every container.
- **Decision 9 (gains):** none: no dynamic range control, dialogue normalization or downmix; the samples are the program's coded channels at full scale.
- **Transient pre-noise processing** is read (the location and length of each channel) and reported, and **not applied**: Annex E 3.7.2 gives the pseudo code but leaves the cross-fade windows open ("nearly any pair of constant amplitude cross-fade windows may be used"), so two decoders would not give the same samples. The measurements see the decoded signal before the correction.
- **Enhanced coupling's two choices:** the factor of 2 of 7.9.4.1's step 6 is applied to the non-aliased coupling channel, which 3.5.5.1's pseudo code leaves out (without it every channel would come back at half; `EnhancedCouplingTests` holds the identity), and the last block of a frame that has it waits for the next frame's first block, as 3.5.5.1 needs (the lead, 2026-11-13): its samples are written when that frame is read, or at a gap or the end of the stream with no next block; `Eac3DelayedBlockTests` holds measured + lost + withheld = Samples there. The sources are A/52:2018 Annex E 3.5 and 2.3.3.20 to 2.3.3.26, and ETSI TS 102 366 V1.4.1 E.2.5 for the rest (the lead, 2026-11-13). Transient pre-noise processing stays unapplied: its windows are not normative (3.7.2).
- **Where the specification is read as printed and FFmpeg differs** ([development.md](../development.md#e-ac-3-decoding)): the vectors of hebap 4 (FFmpeg decodes index i as the table's row i + 1) and the noise in the zero-bit bins of a transform channel (FFmpeg fills them whatever the dither flags say, and with 1.5 times the power). Spectral extension, the transform's other quantizers and the standard tools agree with FFmpeg to float precision.
- **The noise:** spectral extension's blend noise has zero mean and unit variance (a linear congruential generator with a fixed seed, uniform between -sqrt(3) and sqrt(3)), reset after each gap like the dither generator; the angles' random numbers of enhanced coupling come from two more generators with fixed seeds (the per-bin values made once, the transient channels' per band and block).
- **Cost, measured** ([decoders.md](decoders.md#as-built-the-e-ac-3-decoders-work-weights)): AC-3's 44 units a sample and channel, plus 40 a bin for each transform channel and 8,192 plus 192 a bin and coupled channel for each block with enhanced coupling; `DecoderWorkTests` gives 0.5 to 1.4 against the FLAC reference's time per unit.

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
  - [`CLAUDE.md`](../../CLAUDE.md); [`docs/plan.md`](../plan.md) ("Later", "Phase 3, continued", as they stood on 2026-11-08; the latter is now in [history.md](../history.md#phase-3-continued));
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
- **Not read, by the clean-room rules:** FFmpeg, faad2, liba52, libdca, mutagen, libtta (but by the describing agent of the TTA decoder, see [decoders.md](decoders.md#as-built-the-tta-decoder)), getID3, mp3guessenc and mctoolamed source, and the LGPL files of libmpcdec r495.
