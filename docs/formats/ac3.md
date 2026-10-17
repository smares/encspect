# AC-3 and E-AC-3

Dolby Digital and Dolby Digital Plus, as raw files and in [MP4](mp4.md).

## What is read

- **Frames:** sync on a chain of frames, with resync after damage.
- **Detection:** a chain at the start of the data (after zero bytes) is class 92, one after junk class 50.
  - The sniff also reports where the chain starts and how many of its first 16 frames pass their CRCs (crc1 and crc2 in AC-3).
  - The host ranks by class, then the earlier start; chains of one class that overlap are ranked by their verified frames first. A file with junk in front stays AC-3 or E-AC-3 where ADTS-looking or MPEG-looking bytes inside its frames chain too: its own chain starts first. Where the start of the file is cut off, so that the look-alike in the partial first frame starts before the first whole frame, its frames' CRCs hold and the look-alike's do not exist, and the AC-3 chain wins (`junk-adts-in-frames-cut.eac3`). See "Format detection" in the plan.
- **CRCs:** crc1 and crc2.
- **Bit stream information:** every field.
- **Audio blocks:** every block up to its mantissas. The parser steps over the mantissas by running the bit allocation itself; liba52 confirms where every block of the AC-3 fixtures ends, delta bit allocation sent in block 0 (and reused by the blocks after it) included (`derived-ffmpeg-stereo-192k-delta-block0`).
  - A field that the end of the file cuts is `ac3.frame-truncated` alone when it fits its frame (`truncated-in-skip`); `ac3.block-overrun` is for a field or an audio block that does not fit the frame.
- **Programs:** one stream per program, an independent substream (AC-3 is one) with one configuration, and the dependent substreams after each of its frames.
  - A program frame is an independent frame with its dependent frames. It holds each dependent substream ID once (Annex E 2.3.1.2 allows at most 8); a dependent frame that repeats an ID ends the program frame, is left out of its figures and statistics, and is counted in `ac3.dependent-excess`, as are the dependent frames after it up to the next independent frame.
  - A configuration that changes ends the program and starts another one (a run of one configuration is one stream, as in the other formats): `ac3.stream-changed` is one finding with a count. The analyzer keeps at most 64 programs; past them, frames stay in the last program of their substream and the finding says so.
  - A program's statistics cover its own frames, from its first to its last in the first region it appears in (the whole region where the bytes are not contiguous).
  - The independent substreams of one raw file play at the same time, so their streams share one `playbackGroup` and a summary counts the longest once. Where a substream changes its configuration, or the file comes from a container, no group is given: the container groups its own tracks. A group counts only its longest stream, so it cannot say that one substream's runs follow one another beside another substream; such a file stays ungrouped and the summary adds all its streams up (more than the playing time).
- **MP4:** the dac3 and dec3 boxes, compared with the frames. A sample that ends inside its frame is `ac3.frame-truncated`, counted once for the file.
  - **Interpretation:** num_dep_sub is read as the number of dependent substreams of the independent substream, as the dec3 syntax (chan_loc follows when it is above 0) and ffmpeg's muxer (`movenc.c` counts them) use it. ETSI TS 102 366 F.6.2.12 words it as the substreamid of the last dependent substream before the next independent one, which is one less than the count; a box that follows the wording is reported as differing from the frames.
- **Object audio (E-AC-3):** the additional bit stream information's extension flag and complexity index, the EMDF containers with their payloads, and the header of the joint object coding payload. See "Object audio" below.

- **dmixmod 3:** A/52:2018 Annex D Table D2.2 reserves it (E-AC-3's mixing metadata has the same field and Annex E gives no table of its own), and the decoder should still reproduce audio. Dolby's own test files write it, and ffmpeg's `ac3defs.h` notes that encoders use it to indicate Dolby Pro Logic II. It is an info finding (`ac3.dmixmod-reserved`), not a warning. The other reserved values of the shared tables (the surround mix levels 0 to 2, dheadphonmod and roomtyp 3) stay warnings; no real file the project has uses them.

## Channel layout

From the audio coding mode and the LFE flag. E-AC-3's dependent substreams add their channels to their program's layout.

## Object audio

Dolby Atmos in Dolby Digital Plus adds object audio to an ordinary E-AC-3 stream: ETSI TS 103 420 carries it as joint object coding (JOC) side information and object audio metadata (OAMD) in an EMDF container, announced by an extension flag.

- **Extension flag (frames):**
  - An E-AC-3 frame whose additional bit stream information (addbsi) is exactly two bytes shows `HasExtensionTypeA` (flag_ec3_extension_type_a, the last bit of the first byte), `Complexity Index Type A` (complexity_index_type_a, the second byte) and the seven reserved bits before the flag, with a finding if they are not 0 (TS 103 420 8.3.1).
  - Other lengths show the bytes only. ffmpeg's `eac3dec.c` reads the flag from the first byte of an addbsi of any length, even one byte, and sets the profile "Dolby Digital Plus + Dolby Atmos" from it; MediaInfo needs two bytes or more, and Encspect requires two, as 8.3.1 lists. A two-byte addbsi that carries something else cannot be told from the extension.
- **Extension flag (dec3):** after the last independent substream's entry, the dec3 box has seven reserved bits, the flag and the complexity index (TS 103 420 C.3.1); a box that ends there shows none. ETSI TS 102 366 V1.4.1 F.6 only reserves those bytes, and no later version exists. The box is compared with the frames' addbsi fields, which are read in every frame, and with the payloads.
- **EMDF containers (TS 102 366 Annex H):**
  - Found by their sync word 0x5838 in the skip fields, in the bits after the last audio block read (the unused bits and the auxiliary data field) and, for a frame whose audio blocks are not read to their end (adaptive hybrid transform, enhanced coupling, a reduced sample rate), in all the bits after where reading stopped. A skip field is read before its block's mantissas, so the skip fields of the blocks read are never part of the unread bits.
  - A candidate must pass a dry run of the structure, as MediaInfo's `Ac3_EMDF_Test` makes it: the length fits, the version is 0, the payloads reach the end marker inside the length, protection_length_primary is not 0, and the fields end exactly where emdf_container_length says, after zero padding to the byte boundary. Random bytes that contain the sync word are no container.
  - A frame whose addbsi sets the flag announces a container, so one that starts a skip field or the bits after the last block read, or follows another, is read on its sync word alone and its damage is reported; the bits after where reading stopped mid-block start nowhere, so a candidate there always has to pass the test.
  - Per container: length, version, key ID, each payload with its ID, configuration and size, the end marker, the protection lengths and the padding. Payload 11 is OAMD and 14 is JOC (TS 103 420 Table 55); 1 to 7 are named after TS 102 366 Table H.2.3.
- **JOC header:** the downmix configuration, the number of objects (joc_num_objects), the extension configuration, the clip gain and the sequence count (TS 103 420 6.2.2, 6.2.3). **OAMD:** the version and the object count (5.5.2), which is not the JOC count: Dolby's own file has 16 objects in the OAMD payload, one of them the LFE channel, and 15 in the JOC header.
- **Stream:** `hasObjectAudio` and `jocObjectCount` (joc_num_objects) in the JSON stream, the GUI's stream line, the summary row, the CSV (`Has Object Audio` and `JOC Object Count`) and the GUI's summary cache are set where a frame holds a JOC payload. The JSON leaves them out otherwise. The statistics add the containers, the frames with object audio and with the flag, the JOC object count and the complexity index.
- **Findings:**
  - `ac3.emdf-truncated`, `ac3.emdf-length`, `ac3.emdf-payload-size`: a container longer than its field, fields that end before or after emdf_container_length, a payload that runs past its container.
  - `ac3.emdf-reserved`, `ac3.emdf-version-not-read`, `ac3.emdf-limit`: a reserved protection length, padding that is not 0, a duration with more than the two groups H.2.2.3.4 allows; a version Annex H does not define (its payloads are not read); a container beyond what the analyzer reads (a fifth variable_bits group or more than 32 payloads, limits of the analyzer, not of the specification).
  - `ac3.object-payload-short`, `ac3.joc-reserved`, `ac3.object-audio-incomplete`: a payload too short for its first fields, a reserved or out-of-range JOC header value, one of the two payloads missing.
  - `ac3.extension-without-joc`, `ac3.joc-without-extension`: judged over an access unit (an independent frame and its dependent frames), since TS 103 420 8.3.1 puts the flag in the substream that holds the container and ffmpeg reads it from either; the first only when every frame of the unit was read to its end.
  - `ac3.config-differs`: a dec3 box whose flag or complexity index disagrees with the frames, or that has no extension where the frames set the flag or carry JOC.
- **Verified against a real file:** Dolby's DD+ Online Delivery Kit v1.4.1 serves JOC test files (`check_eac3_samples.py` downloads three of them in CI, with three files from the FFmpeg archive (two AHT files and one with junk in front), and checks the analyzer, ffprobe and MediaInfo against `tests/conformance/eac3-samples.json`). They show:
  - the container at the start of a skip field, in block 2 of each frame, filling it exactly;
  - emdf_container_length counting the bytes after the length field (389 in a 393-byte skip field);
  - the payload list ending with a bare 5-bit ID 0;
  - codecdatae 0, protection lengths 2 and 1, JOC with the downmix configuration 3, the complexity index 16, and a program of dynamic objects with an LFE channel.
  - `joc-atmos` is laid out like them. The other placements (after the last block, in the auxiliary data field) follow H.1 and are hand-built; MediaInfo 24.01 and 26.01 parse a container before the auxiliary data field only.
- **Interpretations:**
  - The syntax tables end the payloads at an ID of 0 without a configuration or size, although H.2.2.2.4 says an ID of 0 comes with zero fields; Dolby's file follows the tables.
  - TS 103 420 Table 56 lists codecdatae as 1; H.2.2.3.7, MediaInfo and Dolby's file take 0. Both are read.
- **ffmpeg 6.1** reads only the flag in the frames; its mov demuxer and muxer read and write neither dec3's extension nor the payloads.

## Not yet covered

- E-AC-3 frames that use the adaptive hybrid transform or enhanced coupling are read only up to that point (planned: entropy pass).
- E-AC-3 frames at a reduced sample rate (fscod2) and AC-3 frames with bsid 9 or 10 (half and quarter rate): only their headers are read, not their audio blocks (planned: entropy pass).
- Object audio: the payloads after the JOC and OAMD fields named above (the per-object parameters, the program assignment, the object properties), the protection bits (their calculation is the implementation's, TS 102 366 H.2.2.4.3), TS 103 420's requirement that the container is in the last dependent substream, and Dolby Atmos in TrueHD.

## Encoder detection

No encoder is named from the frames, since no fixture confirms anything that identifies one. An ID3v2 TSSE frame names the encoder with low confidence (`ac3.encoder-claim` for a second one). The writer is resolved apart from claims of the writer role: an APEv2 Encoder item, ffmpeg's muxer version, and a container's tool atom; two that name different software are `ac3.writer-claim-differs`, and software known to write another format is `ac3.writer-other-format`.
