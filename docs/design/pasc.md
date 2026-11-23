# PASC and MPEG-1 Layer I

Status: research note for item 14 of the [plan](../plan.md), 2026-11-23. Nothing is built. No real DCC-Studio file or DCC recording was available, so every statement about files below is about files made by tools, as marked.

## Result

- PASC (the Digital Compact Cassette's coding) is a subset of MPEG-1 Layer I: every source below says so, and the bytes of a reimplementation's output agree.
- No structural signal was found that a Layer I stream from another encoder does not also show. The one bit that differs between two encoders in the repository's fixtures (the original bit) is a property of those encoders, not of PASC, and one file each cannot say more.
- So no detection rule is proposed. The item goes under "Not planned" with real DCC-Studio files as its trigger.

## What the sources state

Each claim names its source and whether it can be checked here.

- **PASC is MPEG-1 Layer I with restrictions.**
  - "PASC is a subset of MP1", and every MP1 decoder plays PASC, but not every MP1 stream is valid PASC: Jac Goudsmit, DCC Museum forum, 2021-03-10 and 2021-03-14 ([thread](https://forum.dccmuseum.com/t/is-pasc-the-same-as-mp1/864)). A forum statement.
  - The DCC Museum's own history page calls PASC "based upon MPEG-1 Audio Layer I" ([The DCC Story](https://www.dccmuseum.com/the-dcc-story/)). A secondary source.
  - A Philips employee on the DCC-L list, about 1996, said it was "basically the same" (Goudsmit's recollection in [the thread](https://forum.dccmuseum.com/t/the-difference-between-pasc-and-mpeg-1-layer-1/149)). Not checkable.
- **The padding slot.** Goudsmit states that a PASC slot added by padding holds no data and is zero; in Layer I it may hold data ("in MP1 the extra slot may contain data, but in PASC the slot must be zero", [thread](https://forum.dccmuseum.com/t/is-pasc-the-same-as-mp1/864); [mp2enc announcement](https://forum.dccmuseum.com/t/announcement-mp2enc-2021-1-now-generates-files-for-dcc-studio-directly/921), 2024-09-05). Goudsmit first read the padding differently (all frames 420 bytes) and corrected himself in 2020; the first reading is wrong.
- **No CRC.** "PASC does also not use the optional CRC": Max, 2020-04-16 ([thread](https://forum.dccmuseum.com/t/the-difference-between-pasc-and-mpeg-1-layer-1/149)).
- **Restrictions:** 384 kbit/s only; 32, 44.1 and 48 kHz; stereo, joint stereo or dual mono, no single-channel mode (Goudsmit, [thread](https://forum.dccmuseum.com/t/is-pasc-the-same-as-mp1/864)).
- **Frame sizes:** 144 slots (576 bytes) at 32 kHz, 96 (384) at 48 kHz, 104 or 105 (416 or 420) at 44.1 kHz, a slot being 32 bits (Goudsmit, [2020-01-20](https://forum.dccmuseum.com/t/the-difference-between-pasc-and-mpeg-1-layer-1/149)). His counts of padded frames per 49 differ between posts (24, 25 and 48), so the figure is not settled by the forum; no public source found here states which frames of the cycle are padded.
- **The `.mpp` file (DCC-Studio).**
  - Starts with a word giving the sampling frequency in kHz: 0x002C for 44.1 kHz (Goudsmit, [2024-09-06](https://forum.dccmuseum.com/t/announcement-mp2enc-2021-1-now-generates-files-for-dcc-studio-directly/921)); he suspected 0x20 and 0x30 for 32 and 48 kHz and marked that unverified ([post 12](https://forum.dccmuseum.com/t/modern-pasc-mp1-software-encoder/37)).
  - Frames are padded to 420 bytes at 44.1 kHz so DCC-Studio can find a frame by number; Goudsmit's DCCU turns a file into Layer I by removing the word and the padding and leaves the audio data untouched ([DCCU page](https://digitalcompactcassette.github.io/Software/JacGoudsmit), public domain by its statement).
  - DCC-Studio crashes on an `.mpp` without its `.lvl` file; `.trk` lists the files used (ASCII), `.lvl` holds per-frame level data (unverified).
  - The plan's FF FF C0 or C4 at the start of a record is the Layer I header; no forum source states a record layout beyond the above.
- **Encoder.** Goudsmit holds that "in 1992 there was only one encoder" and that Philips' and the ISO dist10 encoders are the same lineage, with mp2enc derived from dist10 ([2024-09-04/05 posts](https://forum.dccmuseum.com/t/announcement-mp2enc-2021-1-now-generates-files-for-dcc-studio-directly/921)); the posts conflict on the word size of the modification. Not checkable; no encoder claim follows.
- **Not found:** the allocation habits of Philips' encoder; Hoogendoorn's and Lokhoff's papers (only abstracts and secondary pages appeared; the search hit [a PDF of "The Digital Compact Cassette"](https://vidachestvo.ru/images/c/c7/The_Digital_Compact_Cassette.pdf) which was not read: its origin is a mirror).

## What wav2pasc writes

Black box: [github.com/MEDVEDx64/wav2pasc](https://github.com/MEDVEDx64/wav2pasc), last commit 2026-11-16 (6 commits). No license file anywhere in the checkout and none named in the README; the repository page shows none. Its README and `ANALYSIS.md` were not used beyond the usage text and the build line; its code was not read.

- **Builds** here with CMake and GCC (`cmake -S wav2pasc -B build`), two programs, `wav2pasc` and `pasc2wav`. Usage text: input 44.1 or 48 kHz stereo WAV; `-f 0` writes `.mpp` (default), `-f 1` writes a Layer I stream; a warning says 48 kHz `.mpp` does not play on DCC decks.
- **Input:** the 3-second `tones` and `quiet` signals of `scripts/fixtures/wma_signals.py` at 44.1 and 48 kHz, 16 bits, stereo; the WAVs and outputs are not committed.
- **Layer I output (`-f 1`), all four files:**
  - Every header is `FF FF C0 00` (padding 0) or `FF FF C2 00` (padding 1) at 44.1 kHz and `FF FF C4 00` at 48 kHz: version 1, Layer I, protection bit 1, 384 kbit/s, stereo, mode extension 0, copyright 0, original 0, emphasis 0, private bit 0.
  - 44.1 kHz: 344 frames of 416 bytes (176) or 420 (168); padding bits `0101...` and the pair `00` at frame 48 and 0 of the next cycle, 24 of every 49 padded, . The slot a padding bit adds is zero in every padded frame (168 of 168).
  - 48 kHz: 375 frames of 384 bytes, no padding.
  - No allocation 15 anywhere; the highest value is 14. A frame has 4 to 16 bits left over before its padding slot (checked by a script, in the scratchpad).
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

- Which copyright, original, emphasis and mode bits a DCC recorder or DCC-Studio sets; no public page read states them and no file shows them.
- Whether Philips' encoder leaves a pattern in allocations or scale factors that another Layer I encoder does not: no public source read describes it.
- Whether DCC-Studio's `.mpp` header word is `0x20` or `0x30` for the other rates: only 44.1 kHz is stated by a source.
- Whether the lossless transcoding claims on the forum (MP2 and back) hold; they are untested.

## If it is picked up again

- Evidence that would justify a rule: two or more files whose origin is a DCC recorder or DCC-Studio, with the stated rate and settings, showing header bits or allocation behavior that the repository's Layer I fixtures (tompg, Gogo-no-coda, FFmpeg's `mp1`) do not.
- A cheaper claim worth stating on its own, with no verdict: a Layer I stream that fits the subset above (384 kbit/s, stereo, no CRC, one of the three rates, zero padding slots, original bit 0) can be a PASC stream; it is also what a Layer I encoder set to those values writes. `.mpp` files themselves (the 2-byte preamble and 420-byte records) could be detected as a container for the frames, once a real file shows the preamble and `.lvl`/`.trk` relation.
