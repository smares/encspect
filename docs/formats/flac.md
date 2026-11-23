# FLAC

Native FLAC files (`.flac`, RFC 9639) and FLAC in Ogg (`.oga` and `.ogg`, through the Ogg parser's codec route).

## What is read

- The `fLaC` marker, then every metadata block by its 4-byte header (type, last-block flag, 24-bit length):
  - STREAMINFO: block sizes, frame sizes, sample rate, channels, bits per sample, total samples and the MD5 of the audio;
  - PADDING (a nonzero byte is a finding), APPLICATION (the registered ID), SEEKTABLE (every seek point, placeholders, order), CUESHEET (tracks and index points; the lead-out must be track 170 for a compact disc and 255 otherwise, but flac 1.4.3 writes 170 with the flag clear for a cue sheet imported with `--cuesheet`, so that mismatch is the info `flac.cuesheet-lead-out-medium` and not a warning; the reserved bytes of index points must be zero), PICTURE (type, MIME type, description, size and the length of the data, which is not read);
  - VORBIS_COMMENT, handed to the Vorbis comment tag parser by name (`ParseTag`);
  - reserved types (7 to 126) and the invalid type 127 (`flac.metadata-forbidden-type`).
- Every frame:
  - the header (sync code, blocking strategy, block size, sample rate, channel assignment, sample size and the UTF-8-coded frame or sample number) and its CRC-8;
  - every subframe: constant, verbatim, fixed predictor or linear predictor, with the order, the coefficient precision and shift, the wasted bits and the warm-up samples;
  - the residual: the method (4-bit or 5-bit Rice parameters), the partition order, every partition with its parameter, and escaped partitions with the width of their samples;
  - the padding to a byte boundary and the CRC-16.
- The samples are not decoded unless the front end asks for it (see "Decoding"); the structure pass reads every bit of the coded syntax, so a frame whose syntax is broken is found without it.
- The tree lists the first 4,096 frames in detail; the rest are listed in runs that add nothing. A file of any length costs a bounded amount of memory (see "Memory").

## Frames and resync

- A header counts as a frame only when its CRC-8 holds, its fields are legal and agree with STREAMINFO, its frame reads to the end with a CRC-16 that holds, and either the stream ends there or a consistent header follows. The header is checked before any frame is walked, and every walk counts against the search budget whether or not the candidate holds. After bytes that are no frame, the next frame is found by a chain of such headers, never by the 15-bit sync code alone (it occurs in audio).
- A frame whose CRC-8 fails is still taken where its walk completes and either its CRC-16 holds, a header follows or the data ends, since a flipped bit in the header's CRC-8 byte also fails the CRC-16 of a frame whose data is intact (`flac.header-crc-mismatch`; its message says whether the CRC-16 holds).
- A frame whose CRC-16 fails is a frame with a finding (`flac.crc16-mismatch`); a frame cut by the end of the data is `flac.frame-cut`.
- A frame whose header is intact but whose subframes break the syntax (`flac.subframe-invalid`) is unreadable: it ends at the next chained header and is handed to the decoder as unreadable, which counts it as not decoded and reports nothing more. A frame that runs past the end of the data gets the same search first: where a chained frame follows within 16 MiB, the damaged frame ends there, its CRC-16 is compared and the frames after it are kept; only where none follows is it cut (`flac.frame-cut`) and the rest of the data its bytes.
- The loops of a walk cost what the data present holds, not the block size a header declares: a packet of a dozen bytes that claims 65,535 samples ends at once.
- Bytes between frames are shown as skipped (`flac.data-skipped`). The search for the next frame reads at most 64 MiB plus four times the file's length in all; past that the rest is skipped (`flac.resync-limit`).
- A frame may not be longer than 16 MiB; a longer one is a fault of its syntax.
- The frame or sample number must follow without a gap: a jump is `flac.sample-number-discontinuity`. A stream that does not start at sample 0 is `flac.first-sample-not-zero`.
- A sample rate of 0 in a header is legal for content that is not audio (RFC 9639, section 9.1.7): the frame is read, with `flac.sample-rate-zero`, and agrees with any STREAMINFO.
- A residual whose partition order leaves as many samples in a partition as the predictor order (section 9.2.7 requires more) is `flac.partition-order-too-high`, and a residual sample of -2^31 (section 9.2.7.3 excludes it) is `flac.residual-out-of-range`. Both are decoded as libFLAC decodes them; the frame is not unreadable.
- Frames whose sample rate, channels or bit depth differ from STREAMINFO's are `flac.frame-differs-from-streaminfo`.
- A block size over STREAMINFO's maximum, or under its minimum in a frame that is not the last (RFC 9639, section 8.2 lets the last block be shorter), is `flac.block-size-out-of-range`; a frame larger than the maximum frame size or smaller than the minimum is `flac.frame-size-out-of-range`.

## Reported

- **The predictor each subframe uses**, as tallies and series (the concept `prediction`, "Prediction" in the GUI, for the types, the fixed orders, the predictor order and the precision; the residual's partition orders and method and the wasted bits have none and sit under "Other"), one unit per frame that has a bitrate point, recorded while the frames are read (a frame past the first 4,096 keeps no subframes), and counted over those same frames:
  - category series "Subframe Types" (constant, verbatim, fixed predictor, linear predictor), "Fixed Predictor Orders" (ordered, orders 0 to 4, a new tally node), "Partition Orders" (ordered, 0 to 15), "Rice With 5-Bit Parameters" (a flag series: 5-bit or 4-bit parameters) and "Subframes With Wasted Bits" (a flag series); a value for each subframe of the frame that has one, so a stereo frame has two;
  - numeric series "Highest Predictor Order" (in orders, 1 to 32) and "Coefficient Precision" (bits), the highest of the frame's linear predictor subframes, each with an "Over Time" form (each second's highest) anchored to the tally of the same name; a frame without a linear predictor has no point;
  - numeric series "Block Size" (samples) per frame, only in a stream whose frames differ in size apart from a shorter last frame;
  - a frame that the analyzer cannot read to its end has no value; the tallies cover the frames that have a bitrate point, so a frame that a container cut (an Ogg or Matroska packet that ends early) but whose subframes still read whole counts in neither;
- **Stereo Coding**, a tally and a category series of the channel assignment of each frame of two channels (left/right, left/side, side/right, mid/side), in place of the counts "Independent Frames", "Left/Side Frames", "Side/Right Frames" and "Mid/Side Frames"; **Container Samples** and **Container Duration**, the total samples STREAMINFO states (RFC 9639, section 8.2) and their duration at its sample rate, beside the samples the frames hold, in a native FLAC file only: in Matroska or Ogg STREAMINFO is the codec's header, not the container's statement, so it stays a field node (with `flac.total-samples-mismatch` where it disagrees) and the container adds its own figure.
- the sample rate, channels and layout, bits per sample, the samples (the block sizes of the frames, a cut frame's included; Lost Samples counts the block sizes of the frames that are cut or unreadable; STREAMINFO's count is compared, `flac.total-samples-mismatch`, and 0 means unknown, `flac.total-samples-unknown`), the duration and the average bitrate, which counts the bytes of the frames held whole (a cut frame counts in the samples only);
- **Compression Ratio**, a statistics node ("Lowest", "Overall" and "Highest", in percent) and the series "Compression Ratio" and "Compression Ratio Over Time" (percent, concept `bitDistribution`, sharing the x of "Bitrate"): the coded size over the size of the audio as PCM, the bitrate over the stream's sample rate times channels times bits per sample. The bitrate series carry the concept `bitDistribution` and the quantity "Bitrate".
- the bitrate per frame and per second as series, with the smallest and largest and a distribution; the bitrate mode is variable where the frame bitrates differ (`IsVbr`);
- statistics: the blocking strategy, the block sizes, the share of each stereo assignment, the subframe types with the predictor orders and coefficient precisions, the wasted bits (the share of subframes that have them, and **Wasted Bits**, below), the partition orders, the Rice parameter width, the escaped partitions, the checksums that held and failed, and the MD5.

## Wasted Bits

- **What it counts:** the number of subframes with each count of wasted bits (0 bits, 1 bit, ...), and **Changes**: the subframes whose count differs from the count of the same channel's previous frame. A mid or side subframe states the coding's own count (a mid subframe one bit fewer than left and right), so only a channel coded as itself (left in left/right and left/side, right in left/right and side/right, every channel of a frame of one or three or more channels) is compared; mid and side subframes are in the distribution only. WavPack reports the same statistic under the same name for its blocks (its Zero Bits).
- **Silent subframes:** a constant subframe of 0 is digital silence; libFLAC states 0 wasted bits for it. It is listed as **Silent**, left out of the other entries, the changes and the rule, and the channel's previous count stays, so 16-bit audio in 24 bits with passages of silence has no changes.
- **When it shows:** only in a stream where some subframe has wasted bits; "Subframes With Wasted Bits" is reported for every stream.
- **As coded:** the subframes are counted as they are coded, and the channel assignment is not undone. A side channel (left minus right) has at least the smaller count of the two channels, and a mid channel (their sum shifted right by one) one bit fewer. In the 512-sample codings of the 24-bit lossyWAV recording, 97% of the frames are left/right at the default and 95% with `--linkchannels`; flac chose no mid/side there.
- **Where it comes from:** the counts are read from the subframe headers, without decoding. The levels' "Low Bits Unused per Span" (with `--measure`) counts the same thing over the decoded samples, per span of 32 samples; the two agree on the lossyWAV fixtures where the frames are as long as lossyWAV's block (68 changes in `external-lossyflac-b512.flac`).
- **Not seen:** where in a frame the count holds. A frame shows the smallest count of the samples it holds, so a tool that zeroes bits per block of 4,096 samples reads, in frames of 512, as changes on every eighth frame, and one with blocks of exactly lossyWAV's length reads as lossyWAV does.

## Channel layout

- One to eight channels in FLAC's order (RFC 9639, section 9.1): mono is front center; two channels are front left and right; three add front center; four are front left, front right, back left, back right; five add front center before the back pair; six add the low-frequency channel before the back pair; seven and eight follow the layouts of the specification's table.
- The channel mode is Mono, Stereo, Joint stereo, "Stereo, joint in some frames" or Multichannel (three or more channels) from the share of left/side, side/right and mid/side frames.
- Left/side, side/right and mid/side are shown as such; the side channel of a 32-bit stream has 33 bits.

## Encoder detection

- Observed in Encspect's test file from the private sample kit (`flac/sample-kit`; [development](../development.md#sample-kit-fixtures)): Windows 10's Media Foundation FLAC encoder (`MSFlacEncoder.dll`) writes the vendor string "reference libFLAC 1.3.0 20130526" and is named "libFLAC 1.3.0 (20130526)", with high confidence; the string names the library the encoder is built on, not Microsoft's program, which the file names nowhere.
- libFLAC: the Vorbis comment vendor string `reference libFLAC <version> <date>` is the encoder, with high confidence. A vendor string of that form with a version the analyzer cannot parse is "libFLAC" alone.
- FFmpeg's native encoder: an `ENCODER` comment of the form `Lavc<version> flac` is the encoder, with medium confidence, since the comment is a tag any tool may write.
- An ID3v2 or APEv2 claim of the encoder or writer weighs in as in the other formats (`flac.writer-claim-differs`, `flac.writer-other-format`, `flac.encoder-other-format`).
- lossyWAV (a **processor**, not an encoder or a writer): `flac --keep-foreign-metadata` keeps the chunks of a WAVE file in APPLICATION blocks with the ID `riff`, one block for each chunk, holding the chunk's ID, its size and its body. When the `fact` chunk before the data holds lossyWAV's text, the block shows the chunk's ID and size and the "Text" node with its version, date and time and settings, and the stream's Processor is "lossyWAV 1.4.2" with high confidence and the settings as evidence. The shared reader is `FactText`. A text that starts with `lossyWAV ` but has not the form is `flac.riff-fact-text-unrecognized` (info) and makes no claim. Without `--keep-foreign-metadata` flac keeps no chunk, so lossyFLAC made that way carries no marker; a missing marker says nothing. See the `lossywav` article.
- lossyWAV, from behavior (the same processor, at **low** confidence, named "lossyWAV" without a version): the stream's counts of wasted bits show the pattern lossyWAV's output has. The rule, set from the fixtures ([the design](../design/lossywav-detection.md#from-behavior)):
  - the sample rate is one of 22.05, 32, 44.1, 48, 88.2, 96, 176.4 and 192 kHz, and lossyWAV's block at that rate is 256, 512, 1,024 or 2,048 samples;
  - at least 20 moments: distinct frame positions at which some channel's count changes (many channels changing at one passage are one moment each);
  - the greatest common divisor of the distances between the changes' positions equals lossyWAV's block at that rate: every change lies on its grid, and not all on a coarser one. Frames of the block (512 samples at 44.1 and 48 kHz) or a divisor of it can show it; 4,096-sample frames, whose changes lie at multiples of 4,096, cannot. This test replaced one of "95% of the changes at one phase", which it makes redundant (all changes then lie at one phase), at every rate;
  - more than half of the compared subframes that have a count (all but the silent ones; mid and side subframes are not compared) above the stream's smallest count.
  - The evidence is the figures: the changes, the grid and the share above the smallest. A file with the marker gets the figures as a second piece of evidence under the high-confidence claim.
  - On Encspect's test files it names the 512-sample codings of the −70 dBFS noise-floor signal, the −100 dBFS noise-floor files at every tested rate (frames as long as the block) and the six-channel file; it does not name the 4,096-sample codings, the 16-bit files (no bit removed) or the synthetic files with pure tones.
  - **A tool that zeroes bits per block of 4,096 samples** (the simulated `synthetic-lowbits-other-4096.flac`), coded in frames of 512, is not named: its changes are at multiples of 4,096. A tool that zeroes per block of exactly lossyWAV's length would read the same as lossyWAV; the confidence is low for that reason, and the claim names a pattern, not a cause.
  - Audio that lossyWAV did not touch is not named: `crosscheck_lossywav_behavior.py` codes the 157 FLAC fixtures of the repository that are not lossyWAV's output again, in frames of their rate's lossyWAV block and in frames of 512 (and, where their rate is not a tested one, at 44.1 kHz): none is named, and none has more than 3 changes of wasted bits but the simulated `synthetic-lowbits-other-4096.flac`, whose 58 are by construction (A-law audio expanded to 16 bits has none in frames of 512 and 111 in frames of 64, on a grid of 64; 16-bit audio in 24 bits coded by FFmpeg in frames of 512 with some mid/side frames, `external-ffmpeg-16-in-24-mid-side-b512.flac`, has none because mid and side subframes are not compared). The edits after lossyWAV (a gain, dither) leave no wasted bits at all. Run again with flac 1.4.3 after the 52 compression level fixtures were added (248 files, 286 codings): none of them is named and none has more than 3 changes.
- The compression level is not stored: the frames do not say which level wrote them. A level is guessed from the settings the frames fit, below ("Compression level"), and named as a claim of its own next to the encoder, with its confidence.

## Compression level

- **What it is:** a **Compression Level** node (and a statistic of the same name) after the Encoder node, naming the levels of the encoder whose limits the frames fit, with the encoder in the value ("libFLAC 7 or 8", "FFmpeg 5 to 12"), with a Confidence and Evidence nodes: the block size, the highest linear predictor order, the highest partition order, the stereo coding and the settings of the levels named. It is a claim about settings ("fit", "consistent with"), never a verdict, and it adds no finding. Article: `flac-compression-levels`.
- **When it is made:** the encoder is known (below), the stream has fixed blocks (STREAMINFO's smallest and largest block size are equal, and every frame but the last has that size), the frames agree with STREAMINFO on the sample rate and the channels, at least 3 frames and 8 fixed or linear predictor subframes, and at least one level fits. Otherwise there is no node.
- **Which encoders:**
  - libFLAC 1.2.1 to 1.5.0, named by the vendor string (the confidence of the Encoder node caps the claim). Releases before 1.2.1 and after 1.5.0 are not guessed: their tables were not run. The Windows 10 Media Foundation encoder writes libFLAC 1.3.0's vendor string, so its file (private sample kit) is named "libFLAC 5 to 8" at medium confidence: the settings its frames fit (highest order 8, partition order 5), not a statement about Microsoft's program.
  - FFmpeg's native encoder, libavcodec 60.31 and libavformat 60.16 only (the FFmpeg 6.1.1 that was run; 6.0 and other versions are not guessed), at the sample rates that have a committed fixture (8, 22.05, 44.1, 48, 96 and 192 kHz). It is named by the `Lavc60.31 ... flac` ENCODER comment of an Ogg stream (as the Encoder), or, where no encoder is named, by the `Lavf60.16` vendor string of a native file (the Writer) over linear predictors. The muxer's string alone does not name an encoder, since FFmpeg also copies other encoders' streams (`external-ffmpeg-remux-libflac-level-2-stereo` and `-5-`: libFLAC's frames under FFmpeg's vendor string), so a native file with no linear predictor (levels 0 to 2) is not guessed, and every linear predictor must have FFmpeg's coefficient precision of 15 bits, which caps the claim at medium. libFLAC writes 15 bits as well above 16 bits per sample (`external-24-stereo`), so at 20 and 24 bits only the block size separates the two; the evidence says so.
- **The rule:**
  - A level fits when the block size is its size, the highest linear predictor order and the highest partition order of the stream do not exceed its limits, no fixed predictor is above its limit (FFmpeg's level 0 stops at order 3), and, for libFLAC with two channels, the stream has no mid/side, left/side or side/right frame unless the level decorrelates (levels 0 and 3 never do).
  - **Every level that fits is named**, never fewer: frames that do not use the extra room of a higher level look like the lower level's. Two committed measurement signals coded again show it (`external-level-6-from-lines-slope-mono`: libFLAC 1.4.3's level 6 never uses partition order 6, so the frames fit levels 5 to 8; `external-ffmpeg-level-5-from-lowbits-mixed-stereo`: FFmpeg's level 5 reaches order 8 and partition order 3, "4 to 12"); a rule that named only the levels whose limits the frames reach named "5" and "4" alone there, at medium (`FlacCompressionLevelTests.AFittingLevelIsNeverDropped` fails under it). Levels whose limits are the same are named together whatever the stream shows (libFLAC's 7 and 8 from release 1.3.1; FFmpeg's 5, 6 and 7, 8, 9 and 10, 11 and 12). A stream with no mid/side frame is "0, 1 or 2" (levels that differ only in how stereo is chosen).
  - **Confidence:** medium where the frames reach the highest linear predictor order and partition order of the lowest level they fit (and, for levels without a linear predictor, the highest fixed order; the partition order must be used by at least a fifth of the subframes, and for FFmpeg's levels with a highest partition order of 8 the order 7 counts, since few subframes use 8), and medium where only one group of levels with the same limits fits, because the frames exceed the limits of all the others (FFmpeg's 11 and 12 at a linear predictor order above 12); low otherwise. The confidence of the encoder claim caps it.
- **libFLAC's levels** (the text of `flac --help` of every release from 1.2.1 to 1.5.0, `flac_tool.py --levels`; BSD-3-Clause, built from the release tarballs pinned in `flac_tool.py` and used as a program only):

| Level | Block size | Highest order | Highest partition order | Stereo coding |
|---|---|---|---|---|
| 0 | 1,152 | none (fixed predictors) | 3 | none |
| 1 | 1,152 | none | 3 | adaptive (`-M`) |
| 2 | 1,152 | none | 3 | every assignment tried (`-m`) |
| 3 | 4,096 | 6 | 4 | none |
| 4 | 4,096 | 8 | 4 | adaptive |
| 5 | 4,096 | 8 | 5 | every assignment |
| 6 | 4,096 | 8 | 6 | every assignment |
| 7 | 4,096 | 8 up to 1.3.0, 12 from 1.3.1 | 6 | every assignment |
| 8 | 4,096 | 12 | 6 | every assignment |

  - Levels 0 to 5 read the same in every release. The help text (1.4.0's library already uses `subdivide_tukey`, which its help text does not yet name) also names the analysis windows of levels 6 to 8: none in 1.2.1 and 1.3.0; `tukey(0.5)` and `partial_tukey(2)`, with `punchout_tukey(3)` at level 8, in 1.3.1 to 1.4.0; `subdivide_tukey(2)`, `(3)` at level 8, from 1.4.1. Levels 7 and 8 up to 1.3.0 add an exhaustive model search (`-e`). None of this shows in the frames, so none of it tells levels apart.
  - Checked by running every release at every level on the signal `levels` (`flac_signal.py`): the highest orders in the frames equal the table, in the stereo fixtures `external-level-<n>-stereo.flac` (1.4.3, the flac package of Ubuntu 24.04) and `external-level-<n>-stereo-libflac-<release>.flac` (1.2.1, 1.3.0, 1.3.1 and 1.5.0). The mono fixtures `external-level-<n>-mono.flac` have no decorrelation to show.
  - libFLAC's coefficient precision depends on the block size and the bit depth: 12 bits at 4,096 samples and 16 bits in every release run, and 15 above 16 bits (`external-24-stereo`, `external-20-stereo`). It is not used in the rule.
  - **Not used, and why:** the stereo assignments of the adaptive levels. In Encspect's test files (`levels` and `music`, 20 s of stereo) release 1.3.4's levels 1 and 4 chose left/side and side/right frames as well as mid/side ones, while 1.5.0's chose left/right and mid/side only, so left/side or side/right says nothing about the level; only a frame that is not left/right rules out levels 0 and 3.
- **FFmpeg's levels** (6.1.1's `-compression_level 0` to `12`, as black box: the files it writes from the signal `levels`, `external-ffmpeg-level-<n>-stereo.flac` and `-mono.flac`; the highest orders confirmed by encoding with `-max_partition_order` and `-max_prediction_order` set to the same values, which write the same bytes as the level):

| Level | Predictors | Highest order | Highest partition order | Note |
|---|---|---|---|---|
| 0 | fixed | 3 (orders up to 3) | 2 | small blocks |
| 1 | fixed | 4 | 2 | small blocks; level 0 with `-max_prediction_order 4` |
| 2 | fixed | 4 | 3 | small blocks |
| 3 | linear | 6 | 3 | large blocks, precision 15 |
| 4 | linear | 8 | 3 | |
| 5, 6, 7 | linear | 8 | 8 | the same limits |
| 8, 9, 10 | linear | 12 | 8 | the same limits |
| 11, 12 | linear | 32 | 8 | the same limits |

  - Blocks by sample rate (small for levels 0 to 2, large for 3 to 12), each backed by committed fixtures at level 0 and level 5 (`external-ffmpeg-level-<n>-mono-<rate>`; 44.1 kHz by the level fixtures): 8 kHz 192 and 576; 22.05 kHz 576 and 2,304; 44.1 and 48 kHz 1,152 and 4,608; 96 kHz 2,304 and 8,192; 192 kHz 4,608 and 16,384. Other rates were seen in uncommitted runs (11.025 to 16 kHz 256 and 1,152; 24 and 32 kHz as 22.05; 64 kHz as 48; 88.2 kHz as 96; 176.4 kHz as 192) and are not guessed. A native file at level 0 is not named, so the small sizes show in the fixtures' frames and not in a claim.
  - The stereo assignment is chosen per frame at every level (`-ch_mode auto`), so it says nothing. Mono and stereo fixtures reach the same limits.
  - Only FFmpeg 6.1.1 (libavcodec 60.31, libavformat 60.16) was run. Levels 5 to 12 reach a highest partition order of 8 in few subframes (7 in `external-ffmpeg-level-5-stereo.flac`, and that file is medium, as order 7 counts as reaching).
- **Observed in Encspect's test files:** every fixture written at a known level has that level among the ones named (`FlacCompressionLevelTests`); `external-16-stereo-b576.flac` and the other files written with a block size that is no level's, and the lossyWAV files in frames of 512 samples, are not named.
- **Fixtures:** `make_external_flac_fixtures.py` writes `external-level-<n>-stereo` and `-mono` (libFLAC 1.4.3, levels 0 to 8), `external-level-<n>-stereo-libflac-<release>` (1.2.1: 6, 7, 8; 1.3.0: 7, 8; 1.3.1: 7; 1.5.0: 1, 7) and `external-ffmpeg-level-<n>-stereo` and `-mono` (levels 0 to 12), `external-ffmpeg-level-<n>-mono-<rate>` (levels 0 and 5 at 8, 22.05, 48, 96 and 192 kHz), two measurement signals coded again at libFLAC 6 and FFmpeg 5 (`external-level-6-from-lines-slope-mono`, `external-ffmpeg-level-5-from-lowbits-mixed-stereo`: a higher level fits) and the remuxed libFLAC streams (`external-ffmpeg-remux-libflac-level-<n>-stereo`).

## Ogg FLAC

- The first packet is the mapping header: `0x7F`, `FLAC`, the major and minor version, the 16-bit count of header packets, `fLaC` and the STREAMINFO block. Then one metadata block per header packet, then one frame per packet.
- The tree is the same as a native file's. Findings specific to the mapping: `flac.ogg-version`, `flac.ogg-header-count`, `flac.ogg-header-invalid`, `flac.ogg-packet-invalid`, `flac.ogg-packet-trailing` and `flac.ogg-granule-mismatch` (a page's granule position against the samples counted; the first audio page may state more than the samples counted, as for a stream joined mid-way, and its excess is the base the other pages count from).
- A packet that spans pages is shown with its byte ranges, a metadata block over 1 MiB included: the node covers the whole block, only its first MiB is read.
- An Ogg packet holds exactly one frame or metadata block (RFC 9639, section 10.1). A frame that overruns its packet, or whose packet ends before its CRC-16, is unreadable and its CRC-16, stored at the packet's end, is compared. Bytes after a frame or a metadata block, the mapping packet's included, are `flac.ogg-packet-trailing`: one finding with a count, and a "Trailing Data" node for the first case only.
- Metadata blocks past the 1,024th are counted under "Further Metadata Blocks", as in a native file.
- A frame in an Ogg stream is decoded from the packet's bytes.

## Decoding

- The decoder (`Encspect.Formats.Flac.Decoding`, ID `flac`) runs when the front end asks for it: `--verify` on the command line, "Decode Audio" in the detail window (which also measures the audio). The tree is the same either way.
- `--measure` implies this decode: the host hands every block the decoder writes to the stream's measurer too (the average spectrum, the spectral cutoff and the grids, and the levels; `encspect --learn measurements`, `--learn levels`), and the positions of the blocks are the frames' sample numbers, so frames lost to a gap are skipped by the measurements as well. Measuring changes neither the checksums compared nor a decoding finding. The levels' Low Bits Unused counts over the whole decoded stream the low bits that are never set, which the subframes' wasted bits state per subframe (`encspect --learn bit-depth-usage`): in `synthetic-levels-low-bits-24-in-16`, subframes with 8 and 12 wasted bits read 8 and 12 low bits unused for the two channels.
- It reads no coded syntax of its own. The structure pass stores each frame's warm-up samples, residuals and the description of every subframe in buffers the host lends, and the decoder adds what the syntax does not give: the predictors, the wasted bits and the undoing of the channel assignment, then the MD5 of the samples.
- The MD5 is over the interleaved samples, signed, little-endian, in as many whole bytes as the bit depth needs (RFC 9639, section 8.2). An MD5 of 0 means the encoder did not store one.
- Three checksums are declared and tallied:

| Checksum | Covers | Counts as covering all of the audio when |
|---|---|---|
| frame header CRC-8 | the header | never |
| frame CRC-16 | the coded data | no gap, the frame count matches the samples STREAMINFO states, no cut frame and no unreadable frame |
| STREAMINFO MD5 | the decoded audio | the decoder decoded every frame and the sample count matches |

- A stream is verified when every stored checksum was compared, none failed, and at least one compared checksum covers all the audio. Frames whose every CRC holds do not verify a stream that is missing frames, has them out of order or has a frame it could not read: with an MD5 of 0, such a stream reads "unknown" (`encspect --learn verification`).
- Findings from the decoder: `flac.md5-mismatch` (the MD5 differs) and the host's `decode.*` findings (work budget, stop, a frame that cannot be decoded, failure). `flac.md5-not-stored` (an MD5 of 0, with the CRCs as the only check) is the parser's, with or without decoding.
- A frame whose channels or bit depth differ from STREAMINFO's cannot go into the PCM layout the stream declared: the decoder refuses that frame (`decode.frame-invalid`, a warning on the frame), the stream's decoding is `Decoded` with fewer samples, and it is not verified. A STREAMINFO with fewer than 4 bits per sample, or none, is `NotPossible` for the whole stream.
- **Frames whose CRC-16 fails** are decoded and written as before, so the decoded samples, the verification and the findings stay as they are, but the measurements leave their positions out: they are withheld samples ("checksum failed", the Withheld Samples figure) and the next frame follows a gap. A frame that fails its CRC-16 and cannot be decoded is lost, not withheld. A CRC-8 that fails withholds nothing: the CRC-16 covers the header too. Frames always carry a CRC-16, so no FLAC frame is unprotected; the STREAMINFO MD5 cannot say which frame is damaged and withholds nothing ([the design](../design/damaged-units.md)).
- **Frames in Ogg or Matroska** are also withheld from the measurements when the container's check over their packet fails (a byte on an Ogg page whose CRC fails, or inside a Matroska Cluster or Segment whose CRC-32 fails), though their own CRC-16 holds. That check is no tally of the stream's (the frame CRC-16 tally and the verification stay as they are), and a frame that spans a failing page and a good one is withheld once. FLAC keeps no audio state across frames, so only the frames themselves are withheld ([the design](../design/damaged-units.md#as-built-pull-request-5)).
- The 33-bit side channel and 32-bit data are decoded in 64-bit planes; the other streams use 32-bit planes.
- **The work a frame is charged** to the host's work budget, before the decoder does it (see "As built: the work budget" in [the decoder design](../design/decoders.md)):
  - for each sample of each channel, the fixed work of the sample for its width: 13, 16, 19 or 22 units for 1 to 4 bytes;
  - for each sample of each channel, the order of its predictor (a fixed predictor's 0 to 4, a linear predictor's 1 to 32), two units a tap where the sums need 64 bits (the 33-bit side channel, or a linear predictor whose sum does not fit 32 bits); and 8 more for each sample of a verbatim subframe, the walk's store of it;
  - for a frame the parser's walk did not store, which the decoder reads itself, 16 units for each byte of the frame.
  - A frame is charged once, before it is decoded, so one that passes the budget is decoded in full and then ends the decoding.

## Memory

- The frames are kept as a 56-byte entry each (a frame that spans Ogg pages also keeps its byte ranges) and expanded into nodes when the tree is opened; the frame details (subframes, partitions) are kept in compact records. A file of a million frames costs tens of megabytes, not a node tree.
- The metadata is capped per block (1,024 blocks listed, 1 MiB of a block's body read; Ogg FLAC has the same caps) and the comment, picture and cuesheet text per field. Blocks past the 1,024th are read and checked, and their findings point to the "Further Metadata Blocks" node that stands for them; their Vorbis comments are not read.
- The decoder holds one frame's planes and the MD5 state, nothing per frame.

## Tags

- A Vorbis comment block is read by the Vorbis comment parser. `flac.comment-not-read` shows when none is available to the analyzer.
- ID3v2 tags before the marker and ID3v1 and APEv2 tags after the last frame are the host's. An ID3v2.4 SEEK frame that points between frames ends the stream there and the bytes after the tag are reported as data (`flac.data-after-tag`).
- A tag candidate inside the chain of frames belongs to the stream.

## FLAC in MP4

An `fLaC` sample entry whose `dfLa` box holds the metadata blocks goes to the codec parser as MP4 does for Matroska's CodecPrivate: each block is a header packet, and each sample is one frame ([mp4.md](mp4.md#flac)).

- **Where the nodes go:** the blocks' nodes under the `dfLa` box in the movie box, the frames, the statistics and the encoder under the track's samples. A Vorbis comment block in the box is read as in a FLAC file.
- **The sample entry** states the channels, the bits of a sample and the sample rate, which MP4 compares with STREAMINFO (`mp4.sample-rate-differs`, `mp4.channels-differ`, `mp4.bit-depth-differs`). A sample rate above 65,535 Hz does not fit the entry's field and is not compared.
- **Fixtures:** `external-ffmpeg-flac-in-mp4.mp4` and `external-ffmpeg-flac-24-bit-51-in-mp4.mp4` (ffmpeg 6.1.1's muxer, which writes a `dfLa` box with the STREAMINFO block alone), and the hand-built `flac-entry-differs-in-mp4` and `flac-vorbis-comment-in-mp4`, in `tests/fixtures/mp4`; `make_external_mp4_fixtures.py` writes the first two, `crosscheck_mp4_fixtures.py` holds them to ffprobe and MediaInfo, and the decoded samples to ffmpeg's.

## Findings

- Stream: `flac.no-marker`, `flac.no-streaminfo`, `flac.no-frames`, `flac.total-samples-mismatch`, `flac.total-samples-unknown`, `flac.first-sample-not-zero`, `flac.blocking-strategy-changes`, `flac.sample-number-discontinuity`, `flac.data-skipped`, `flac.resync-limit`, `flac.data-after-tag`, `flac.payload-not-analyzed`, `flac.sample-rate-zero`.
- Metadata: `flac.streaminfo-invalid`, `flac.streaminfo-length`, `flac.streaminfo-misplaced`, `flac.metadata-forbidden-type`, `flac.metadata-no-last-block`, `flac.metadata-truncated`, `flac.metadata-block-repeated`, `flac.metadata-blocks-not-listed`, `flac.padding-not-zero`, `flac.seek-table-length`, `flac.seek-table-unordered`, `flac.seek-table-placeholder`, `flac.cuesheet-invalid`, `flac.cuesheet-lead-out-medium`, `flac.cuesheet-reserved`, `flac.picture-invalid`, `flac.comment-not-read`, `flac.comment-trailing-bytes`.
- Frames: `flac.header-crc-mismatch`, `flac.crc16-mismatch`, `flac.frame-cut`, `flac.subframe-invalid`, `flac.padding-bits-set`, `flac.partition-order-too-high`, `flac.residual-out-of-range`, `flac.frame-differs-from-streaminfo`, `flac.block-size-out-of-range`, `flac.frame-size-out-of-range`.
- Decoding: `flac.md5-mismatch`, `decode.frame-too-large` (a guard: a frame the specification allows cannot reach it).
- Encoder: `flac.encoder-other-format`, `flac.writer-claim-differs`, `flac.writer-other-format`, `flac.riff-fact-text-unrecognized`, `flac.processor-claim-differs` (info: a second `riff` fact text that names other software or another version than the first; the processor is taken from the first).
- Ogg: `flac.ogg-version`, `flac.ogg-header-count`, `flac.ogg-header-invalid`, `flac.ogg-packet-invalid`, `flac.ogg-packet-trailing`, `flac.ogg-granule-mismatch`.

## Not covered

- Playback and any output other than the checksum comparison (the PCM sink is for tests and a later export).
- A protected FLAC track in MP4 (its samples are encrypted) is named by its sample entry and not analyzed (`mp4.codec-not-analyzed`). `flac.payload-not-analyzed` is the codec parser's answer to a container that hands it a whole payload, which no container does today.
- The contents of PICTURE data and the audio of an APPLICATION block. The `riff` blocks are read for their chunk ID and size, and for the `fact` text; the other chunks' bodies are not read.
- Subset conformance (the limits of the FLAC subset) is not judged.
