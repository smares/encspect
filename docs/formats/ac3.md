# AC-3 and E-AC-3

Dolby Digital and Dolby Digital Plus, as raw files and in [MP4](mp4.md).

## What is read

- **Frames:** sync on a chain of frames, with resync after damage.
- **CRCs:** crc1 and crc2.
- **Bit stream information:** every field.
- **Audio blocks:** every block up to its mantissas. The parser steps over the mantissas by running the bit allocation itself; liba52 confirms where every block of the AC-3 fixtures ends.
- **E-AC-3 substreams:** one stream per program.
- **MP4:** the dac3 and dec3 boxes, compared with the frames.
- **Object audio (E-AC-3):** the additional bit stream information's extension flag and complexity index, the EMDF containers with their payloads, and the header of the joint object coding payload. See "Object audio" below.

## Channel layout

From the audio coding mode and the LFE flag. E-AC-3's dependent substreams add their channels to their program's layout.

## Object audio

Dolby Atmos in Dolby Digital Plus adds object audio to an ordinary E-AC-3 stream: ETSI TS 103 420 carries it as joint object coding (JOC) side information and object audio metadata (OAMD) in an EMDF container, announced by an extension flag.

- **Extension flag (frames):** an E-AC-3 frame whose additional bit stream information (addbsi) has two or more bytes shows `HasExtensionTypeA` (flag_ec3_extension_type_a, the last bit of the first byte) and `Complexity Index Type A` (complexity_index_type_a, the second byte) with the seven reserved bits before the flag (TS 103 420 8.3; ffmpeg's `eac3dec.c` reads the same bits).
- **Extension flag (dec3):** after the last independent substream's entry, the dec3 box has seven reserved bits, the flag and the complexity index (TS 103 420 C.3.1); a box that ends there shows none. ETSI TS 102 366 V1.4.1 F.6 only reserves those bytes, and no later version exists.
- **EMDF containers (TS 102 366 Annex H):** found by their sync word 0x5838 in the bits after the last audio block (the unused bits and the auxiliary data field) and in the skip fields. The first container of a field is taken on its sync word, a later one only if its length fits and its version is 0. Per container: length, version, key ID, each payload with its ID, configuration and size, the end marker, the protection lengths and the padding to the byte boundary. Payload 11 is OAMD and 14 is JOC (TS 103 420 Table 55); 1 to 7 are named after TS 102 366 Table H.2.3.
- **JOC header:** the downmix configuration, the number of objects (joc_num_objects), the extension configuration, the clip gain and the sequence count (TS 103 420 6.2.2, 6.2.3). **OAMD:** the version and the object count (5.5.2).
- **Stream:** `hasObjectAudio` and `objectCount` in the JSON stream (the GUI's stream line shows them too) are true and joc_num_objects where a frame holds a JOC payload. They are left out of the JSON otherwise. The statistics add the containers, the frames with object audio and with the flag, the objects and the complexity index.
- **Findings:**
  - `ac3.emdf-truncated`, `ac3.emdf-length`, `ac3.emdf-payload-size`: a container longer than its field, fields that end before or after emdf_container_length, a payload that runs past its container.
  - `ac3.emdf-reserved`, `ac3.emdf-version-not-read`: a reserved protection length, padding that is not 0, a version Annex H does not define (its payloads are not read).
  - `ac3.object-payload-short`, `ac3.joc-reserved`, `ac3.object-audio-incomplete`: a payload too short for its first fields, a reserved or out-of-range JOC header value, one of the two payloads missing.
  - `ac3.extension-without-joc`, `ac3.joc-without-extension`: a frame whose flag and JOC payload disagree.
  - `ac3.config-differs`: a dec3 box whose flag or complexity index disagrees with the frames, or that has no extension where the frames carry JOC.
- **Interpretations:**
  - emdf_container_length counts the bytes after the length field, as MediaInfo's `Ac3_EMDF_Test` does.
  - The syntax tables end the payloads at an ID of 0 without a configuration or size, although H.2.2.2.4 says an ID of 0 comes with zero fields.
  - TS 103 420 Table 56 lists codecdatae as 1, and H.2.2.3.7 and MediaInfo take 0; both are read.
- **Where a real encoder puts the container** is not verified here: no Dolby Atmos E-AC-3 file with terms that allow downloading it for testing was found. MediaInfo 24.01 finds a container before the auxiliary data field only, so the main fixtures put it there.
- **ffmpeg 6.1** reads only the flag in the frames (profile "Dolby Digital Plus + Dolby Atmos"); its mov demuxer and muxer read and write neither dec3's extension nor the payloads.

## Not yet covered

- E-AC-3 frames that use the adaptive hybrid transform or enhanced coupling are read only up to that point (planned: entropy pass).
- E-AC-3 frames at a reduced sample rate (fscod2) and AC-3 frames with bsid 9 or 10 (half and quarter rate): only their headers are read, not their audio blocks (planned: entropy pass).
- Object audio: the payloads after the JOC and OAMD fields named above (the per-object parameters, the program assignment, the object properties), the protection bits (their calculation is the implementation's, TS 102 366 H.2.2.4.3), TS 103 420's requirement that the container is in the last dependent substream, and Dolby Atmos in TrueHD.

## Encoder detection

No encoder is named from the frames, since no fixture confirms anything that identifies one. Claims from containers and tags name one with low confidence.
