# PASC and MPEG-1 Layer I

Status: research note for item 14 of the [plan](../plan.md), 2026-11-23. Nothing is built. No real DCC-Studio file or DCC recording was available, so every statement about files below is about files made by tools, as marked.

## Result

- PASC (the Digital Compact Cassette's coding) is a subset of MPEG-1 Layer I: every source below says so, and the bytes of a reimplementation's output agree.
- No structural signal was found that a Layer I stream from another encoder does not also show. The one bit that differs between two encoders in the repository's fixtures (the original bit) is a property of those encoders, not of PASC, and one file each cannot say more.
- So no detection rule is proposed. The item goes under "Not planned" with real DCC-Studio files as its trigger.

## What the sources state

Each claim names its source and whether it can be checked here.

- **PASC is MPEG-1 Layer I with restrictions.**
  - "PASC is a subset of MP1", and every MP1 decoder plays PASC, but not every MP1 stream is valid PASC: Jac Goudsmit, DCC Museum forum, 2021-03-10 and 2021-03-14 ([thread](https://forum.dccmuseum.com/t/is-pasc-the-same-as-mp1/864)). A forum statement; consistent with the system description below.
  - The DCC Museum's own history page calls PASC "based upon MPEG-1 Audio Layer I" ([The DCC Story](https://www.dccmuseum.com/the-dcc-story/)). A secondary source.
  - A Philips employee on the DCC-L list, about 1996, said it was "basically the same" (Goudsmit's recollection in [the thread](https://forum.dccmuseum.com/t/the-difference-between-pasc-and-mpeg-1-layer-1/149)). Not checkable.
- **The Philips "DCC System Description", draft of September 1994, chapter 12** ([PDF on the DCC documentation site](https://digitalcompactcassette.github.io/Documentation/General/Philips%20DCC%20System%20Description%20Draft.pdf)).
  - The only primary description found. The site states of its documentation folder: "As far as we can tell, this information is public domain". The PDF itself carries a Philips copyright notice (reproduction prohibited without written permission) and a confidentiality classification. It is therefore read in the cache only, nothing is copied from it, and whether it counts as a checkable public source is the lead's decision.
  - It is not IEC 61119, which was neither bought nor looked for.
  - **Frame:** 32-bit slots; 96 slots at 48 kHz (8 ms), 104 slots plus padding at 44.1 kHz (8.7 ms), 144 slots at 32 kHz (12 ms).
  - **Header (slot 1):** 16 bits of sync, all ones; bitrate bits must be 1100 (384 kbit/s), anything else mutes the output; sampling frequency 00 44.1 kHz, 01 48 kHz, 10 32 kHz; padding at bit 22, valid at 44.1 kHz only; bit 23 reserved, 0 and not a validity check; mode 00 stereo, 01 joint stereo ("reserved for future use"), 10 two-channel mono, 11 reserved; bits 26 and 27 name the jointly coded subbands in joint stereo and are 0 otherwise; bit 28 copyright (a "transparent" bit copied from the source signal); bit 29 reserved, 0; bits 30 and 31 emphasis (00 none, 01 50/15 microseconds, 10 reserved, 11 CCITT J.17, the last two "not used for consumer applications").
  - **Reading against Layer I:** the 16 sync bits are the MPEG sync plus version 1, layer I and protection bit 1 (no CRC); bit 23 is the private bit; bit 29 is the original bit, which PASC fixes at 0.
  - **Padding:** at 44.1 kHz every odd frame of a sequence of 49 (numbered 0 to 48) has a dummy slot, so 24 of 49. A frame header must say so by the padding bit.
  - **Bitpool:** the bits for scale factors and samples of both channels are 2784 at 48 kHz, 3040 at 44.1 kHz (plus 32 optionally in a padding frame) and 4320 at 32 kHz. Each equals the frame's bits less the header (32) and the allocation field (64 four-bit entries, 256 bits); checked arithmetic, not a finding.
  - **Allocation:** four-bit values, 1111 unused, as in Layer I. The chapter gives an allocation algorithm (switch-off counters, a hearing threshold and masking table, bits handed out to the largest need until the pool is spent). That is the encoder's side and a description of Philips' model; the Layer I standard leaves the model free.
  - Checked: by reading the PDF here; the bit positions are the ones listed. The document is a draft, and its OCR text has errors.
- **The padding slot.** The system description's "dummy slot" and Goudsmit's statements agree that a PASC slot added by padding holds no data and is zero; in Layer I it may hold data ("in MP1 the extra slot may contain data, but in PASC the slot must be zero", [thread](https://forum.dccmuseum.com/t/is-pasc-the-same-as-mp1/864); [mp2enc announcement](https://forum.dccmuseum.com/t/announcement-mp2enc-2021-1-now-generates-files-for-dcc-studio-directly/921), 2024-09-05). Goudsmit first read the padding differently (all frames 420 bytes) and corrected himself in 2020; the first reading is wrong.
- **No CRC.** "PASC does also not use the optional CRC": Max, 2020-04-16 ([thread](https://forum.dccmuseum.com/t/the-difference-between-pasc-and-mpeg-1-layer-1/149)); matches the sync pattern above.
- **Restrictions:** 384 kbit/s only; 32, 44.1 and 48 kHz; stereo, dual-channel mono and (reserved) joint stereo, no single-channel mode (system description above and the forum thread).
- **The `.mpp` file (DCC-Studio).**
  - Starts with a word giving the sampling frequency in kHz: 0x002C for 44.1 kHz (Goudsmit, [2024-09-06](https://forum.dccmuseum.com/t/announcement-mp2enc-2021-1-now-generates-files-for-dcc-studio-directly/921)); he suspected 0x20 and 0x30 for 32 and 48 kHz and marked that unverified ([post 12](https://forum.dccmuseum.com/t/modern-pasc-mp1-software-encoder/37)).
  - Frames are padded to 420 bytes at 44.1 kHz so DCC-Studio can find a frame by number; Goudsmit's DCCU turns a file into Layer I by removing the word and the padding and leaves the audio data untouched ([DCCU page](https://digitalcompactcassette.github.io/Software/JacGoudsmit), public domain by its statement).
  - DCC-Studio crashes on an `.mpp` without its `.lvl` file; `.trk` lists the files used (ASCII), `.lvl` holds per-frame level data (unverified).
  - The plan's FF FF C0 or C4 at the start of a record is the Layer I header; no forum source states a record layout beyond the above.
- **Encoder.** Goudsmit holds that "in 1992 there was only one encoder" and that Philips' and the ISO dist10 encoders are the same lineage, with mp2enc derived from dist10 ([2024-09-04/05 posts](https://forum.dccmuseum.com/t/announcement-mp2enc-2021-1-now-generates-files-for-dcc-studio-directly/921)); the posts conflict on the word size of the modification. Not checkable; no encoder claim follows.
- **Not found:** the allocation habits of Philips' encoder beyond the system description's algorithm; Hoogendoorn's and Lokhoff's papers (only abstracts and secondary pages appeared; the search hit [a PDF of "The Digital Compact Cassette"](https://vidachestvo.ru/images/c/c7/The_Digital_Compact_Cassette.pdf) which was not read: its origin is a mirror).

## What wav2pasc writes

Black box: [github.com/MEDVEDx64/wav2pasc](https://github.com/MEDVEDx64/wav2pasc), last commit 2026-11-16 (6 commits). No license file anywhere in the checkout and none named in the README; the repository page shows none. Its README and `ANALYSIS.md` were not used beyond the usage text and the build line; its code was not read.

- **Builds** here with CMake and GCC (`cmake -S wav2pasc -B build`), two programs, `wav2pasc` and `pasc2wav`. Usage text: input 44.1 or 48 kHz stereo WAV; `-f 0` writes `.mpp` (default), `-f 1` writes a Layer I stream; a warning says 48 kHz `.mpp` does not play on DCC decks.
- **Input:** the 3-second `tones` and `quiet` signals of `scripts/fixtures/wma_signals.py` at 44.1 and 48 kHz, 16 bits, stereo; the WAVs and outputs are not committed.
- **Layer I output (`-f 1`), all four files:**
  - Every header is `FF FF C0 00` (padding 0) or `FF FF C2 00` (padding 1) at 44.1 kHz and `FF FF C4 00` at 48 kHz: version 1, Layer I, protection bit 1, 384 kbit/s, stereo, mode extension 0, copyright 0, original 0, emphasis 0, private bit 0.
  - 44.1 kHz: 344 frames of 416 bytes (176) or 420 (168); padding bits `0101...` and the pair `00` at frame 48 and 0 of the next cycle, 24 of every 49 padded, as the system description tabulates (frame 0 unpadded, odd frames padded). The slot a padding bit adds is zero in every padded frame (168 of 168).
  - 48 kHz: 375 frames of 384 bytes, no padding.
  - No allocation 15 anywhere; the highest value is 14. Scale-factor and sample bits stay within 3040 (44.1 kHz) and 2784 (48 kHz); a frame has 4 to 16 bits left over before its padding slot (checked by a script, in the scratchpad).
- **`.mpp` output (`-f 0`):**
  - A 2-byte preamble: `2C 00` at 44.1 kHz, `30 00` at 48 kHz.
  - At 44.1 kHz, 344 records of 420 bytes, each starting `FF FF C0 00` (padding bit 0 in every record, also where the Layer I output has 1) and ending in four zero bytes; each record equals the Layer I frame (416 bytes) with the zero fill, or the 420-byte padded frame with its header's padding bit cleared. At 48 kHz, 375 records of 384 bytes starting `FF FF C4 00`.
  - File sizes: 144,482 bytes at 44.1 kHz (2 + 344 x 420), 144,002 at 48 kHz (2 + 375 x 384).
  - The plan's description of the preamble and records matches these files exactly. They show what the tool writes, not what DCC-Studio writes.
- **Contrast in the repository's fixtures.** `external-xing-layer1.mp1` (Xing tompg 3.0, 44.1 kHz, 384 kbit/s, stereo) also has 24 padded frames of 49 (zero slots in 111 of 111), no CRC, and the headers `FF FF C0 04` and `FF FF C2 04`: the original bit is 1, where wav2pasc writes 0. Its 49-cycle has the pair `00` at frames 0 and 1, so it is one frame out of phase with wav2pasc's; with a file that may start anywhere in the cycle, the phase says nothing without the start of the stream.

## Real DCC transfers

- **None found** that is lawfully and publicly available as a file with a clear origin.
  - The DCC documentation site and DCCU state that their documentation and software are public domain or open source; no recorded `.mpp`, `.mp1` or tape dump was found among what was read. The repository trees could not be listed from this session (the GitHub API is limited to the session's repositories), so a sample file in DCCU or the site's repository is not ruled out.
  - Forum threads mention test recordings (a "DCC951" series by a member) and a Ben Liebrand cassette; these are not attached to any post read, and the cassette is commercial music.
  - Dumps of prerecorded tapes are commercial recordings, so they are not candidates.
- **Candidates for the lead:** ask Jac Goudsmit (DCCU) and the DCC Museum forum for a short recording of the uploader's own making, with permission and a statement of terms; the NFSA and archive.org were searched without result.

## What is not known

- Whether a DCC recorder or DCC-Studio sets the copyright, original or emphasis bits any differently from wav2pasc; the system description makes copyright a copy of the source's bit and the original bit 0, and no file shows it.
- Whether Philips' encoder leaves a pattern in allocations or scale factors that another Layer I encoder does not: the described algorithm could be run on a signal, but that tests the document, not recorded files.
- Whether DCC-Studio's `.mpp` header word is `0x20` or `0x30` for the other rates: only 44.1 kHz is stated by a source.
- Whether the lossless transcoding claims on the forum (MP2 and back) hold; they are untested.

## If it is picked up again

- Evidence that would justify a rule: two or more files whose origin is a DCC recorder or DCC-Studio, with the stated rate and settings, showing header bits or allocation behavior that the repository's Layer I fixtures (tompg, Gogo-no-coda, FFmpeg's `mp1`) do not.
- A cheaper claim worth stating on its own, with no verdict: a Layer I stream that fits the subset above (384 kbit/s, stereo, no CRC, one of the three rates, zero padding slots, original bit 0) can be a PASC stream; it is also what a Layer I encoder set to those values writes. `.mpp` files themselves (the 2-byte preamble and 420-byte records) could be detected as a container for the frames, once a real file shows the preamble and `.lvl`/`.trk` relation.
