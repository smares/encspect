# WavPack 1 to 3

Status: signed off by the lead, 2026-11-18; the fixtures (pull request 2) and the parser (pull request 3) are in, and "What the files show" holds what the fixtures showed. Milestone 0.4, item 3 of the [plan](../plan.md#04-free-decoders-and-routes-complete): "WavPack 1 to 3, with encoder fixtures from the builds Rarewares keeps (3.4, 3.97, the first BSD release, and the DOS build of 4.31)". The parser reads them at header level ([wavpack.md](../formats/wavpack.md#wavpack-1-to-3)); their audio is not decoded yet.

## Scope

- **In:** detection of WavPack files of format versions 1, 2 and 3 (`.wv`, written by WavPack 1.0 to 3.97 from 1998 to 2003), the WAV header they keep, the WavPack header with every flag, the bitstream and the trailer as regions, the encoder's mode and settings, the stream figures the header gives, and the "raw" files of version 3. WavPack 4.31 for DOS as a source of version 4 fixtures from a library older than 5.0.
- **Proposed, decision 5:** a decoder of these streams, the only way to check their audio.
- **Out:** the `.wvc` correction file opened alone (it has no header), self-extracting `.exe` files, playback; see [Not covered](#not-covered).

## Sources and licenses

### libwavpack

- **libwavpack 5.6.0**, the archive `scripts/fixtures/wavpack_tool.py` already pins (`wavpack-5.6.0.tar.bz2`, SHA-256 `8cbfa159…39cc1`), BSD 3-Clause, whose notice is already in [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md).
  - `src/unpack3_open.c` (289 lines): opening a legacy file, the WAV header checks, the header of each version, the flag kludge, the correction file.
  - `src/unpack3.c` (1,703 lines): the decoder of every legacy mode, the end of the data and the trailer.
  - `src/unpack3.h`: the header structure (`WavpackHeader3`) and the flags.
  - `src/unpack3_seek.c`: seeking, not needed.
- The legacy code is compiled only with `ENABLE_LEGACY` (`--enable-legacy`, CMake `WAVPACK_ENABLE_LEGACY`), off by default. Without it `open_utils.c` 78 answers a file whose first byte is `R` with "this legacy WavPack file is deprecated, use version 4.80.0 to transcode". NEWS: "removed: support for legacy WavPack files (< 4.0)" in 5.0.0.
- Debian's packages of 5.6.0 (`wavpack_5.6.0-1.debian.tar.xz`, SHA-256 `b4d739e2…218b8`) and 5.9.0 (`wavpack_5.9.0-2.debian.tar.xz`, `9ee88e4b…11b0`) configure with `--disable-rpath` only, so the `wvunpack` of Ubuntu 24.04 and 26.04 refuses every legacy file.

### The original sources and documentation

ReallyRareWares (https://www.rarewares.org/rrw/wavpack.php, files under `https://www.rarewares.org/rrw/files/lossless/`) keeps every release below, "kindly contributed by David Bryant himself". Fetched 2026-11-18 into `.cache/wavpack-legacy`:

| File | Release | SHA-256 | Holds |
|---|---|---|---|
| `WAVPACK1.ZIP` | 1.0, 1998-08-15, DOS | `d639e5b41356a708bf021daed3857a63cb1569de3177f4f2adedd82b87f7bb64` | `WAVPACK.EXE`, `WVUNPACK.EXE` (Turbo C++), `WAVPACK.TXT` |
| `WAVPACK2.ZIP` | 2.0, 1998-09-02, DOS | `bcf12e827c8efe7113bea7755993e1975b4fa8103cd23b18fb67694a37d85bcb` | the same three |
| `WAVPACK3.ZIP` | 3.0, 1999-09-12, DOS | `759d289227b8d6eaec959152bf73aa091795b92c57f6591c2e4f1e549ccbc680` | the same and `THEORY.TXT` |
| `wavpck32.zip` | 3.2, 2000-06-11, DOS | `b026627c15ee8706d143dee951143a09a95007697cbe931b29f20d6baf0d0f8f` | the same four |
| `wavpck34.zip` | 3.4, 2002-12-12, DOS (Borland C++) | `6f957f7552820f336a93f6876103a510526af1a891e93e5b046ccfeb8f32586e` | `wavpack.exe`, `wvunpack.exe`, `wavpack.txt` |
| `wavpack3.97.zip` | 3.97, 2003-02-18, Win32 | `792216c2e16b5bb5f6694ca1341065423e341de210687fe5aa46685439cba870` | `wavpack.exe`, `wvunpack.exe`, `wvselfx.exe`, the Winamp plugin, the Cool Edit filter, `wavpack_doc.htm` |
| `wpsources.zip` | 3.97 source, 2003 | `b978e090a45106cd7a4d997861ec3a403f3ad0192f1a1f56897d7eec9de3a329` | `wavpack.c`, `pack.c`, `unpack.c`, `words1.c` to `words4.c`, `wvunpack.c`, `cool_wv.c`, `readme.txt` |
| `WVDOS431.ZIP` | 4.31, 2006-03-20, DOS (DOS/32A bound in) | `95a27acac8c6a791772b8ce98443273f5feb9eb69215ceafcb977e595897d4a7` | `WAVPACK.EXE`, `WVUNPACK.EXE`, `CHANGELO.TXT` |
| `WVSRC431.ZIP` | 4.31 DOS source | `e7d21d0dc0a1af181be6cdb7af98fa222b7016afeded11a3bd70f005d1b61f64` | the library and programs for OpenWatcom, `UNPACK3.C`, `LICENSE.TXT` (BSD) |

- **3.97 is the first BSD release:** "With version 3.97 the sources were released under a BSD license. Before that, WavPack was closed source freeware" (the RRW page); every file of `wpsources.zip` says "Distributed under the BSD Software License". The plan's list is therefore three builds: 3.4, 3.97 and 4.31.
- **The documentation of versions 1 to 3** is the `WAVPACK.TXT` of each release and `wavpack_doc.htm` of 3.97 (options, modes, what the file holds), and `THEORY.TXT` of 3.0 and 3.2 ("not detailed enough for implementing WAVPACK independently or decoding WAVPACK files"). The 3.97 `readme.txt` says: "there is no formal specification of the WavPack file format, [but] the code is commented enough". No other description exists; `doc/WavPack5FileFormat.pdf` covers version 4 and 5 only.
- **The licenses of the binaries:** 1.0 to 3.2 say "feel free to give it to anyone else who might find it useful"; 3.4 the same; 3.97 and 4.31 are BSD. The fixtures commit only what the encoders write.

### The clean-room question

- **No split is needed.** libwavpack's legacy code and the 3.97 and 4.31 sources are BSD, so the parser and a decoder may follow them, with libwavpack's notice in the source (the row of [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md) for libwavpack gains "the legacy decoder").
- **Not read:** FFmpeg's `wavpack.c` and `wvdec.c` (LGPL; they do not read version 3 anyway). FFmpeg and MediaInfo are run as black boxes only.
- **Which source wins:** libwavpack 5.6.0, the library the existing parser follows, except where it departs from the 3.97 decoder written by the same author with the files (below); there 3.97's `unpack.c` and `wvunpack.c` decide, and the fixtures and 3.97's `wvunpack` confirm it.

## The format

### Layout

- **A file is the source WAV file's header, the WavPack header, one bitstream and an optional trailer.** There are no frames or blocks, no sync word and no length of the coded data.
  - The RIFF header and every chunk up to and including the `data` chunk's 8-byte header, copied unchanged from the source (`wavpack.c` 708-856 of 3.97). The RIFF size and the data size are the source's: the RIFF size does not match the `.wv`, and the data size counts the PCM bytes, so the data chunk runs past the end of the file.
  - The WavPack header, `wvpk`.
  - The bitstream, from the byte after the header. Its end is known only to a decoder, which reads until the stated samples are decoded.
  - **The trailer (3.97):** where the source has bytes after its audio, the encoder writes 16 bytes of 0xFF ("128 '1's ... to recognize this data later and prevent old unpackers from choking", `wavpack.c` 1068-1112) and then those bytes. Each word reader of `unpack3.c` takes a run of ones of its limit (72, 50, 33 or 25) as the end of the data, and libwavpack fills with 0xFF past the end of the file (`unpack3.c` 1654). `unpack3.c` 1176-1214 keeps the bytes after the bitstream as the wrapper only when the first 16 are 0xFF. The documents of 1.0 to 3.2 say the encoder discards anything after the audio data; which releases write the trailer, the fixtures settled ("What the files show").
  - Tags (APEv2, ID3v1) may follow: "it will allow you to use another program to append any kind of tag" (3.97's documentation), which `wvunpack` 3.97 ignores.
- **Raw files (version 3 only, `-r1`, `-r2`):** the WavPack header is the first thing in the file, with the raw flag set, no WAV header, and up to three bytes after the last whole sample stored in the header (`extra_bc`, `extras`). libwavpack does not read them (`unpack3.c` 11-12: "not including 'raw' files"); 3.97's `wvunpack` does.
- **The correction file (`.wvc`, version 3 hybrid with `-c`, 3.4 and later):** the second bitstream alone, with no header (`pack_audio` in `wavpack.c` opens it with `bs_open_write` and writes nothing else, 1243-1257). The decoder finds it by name (`x.wv` to `x.wvc`, `wvunpack.c` 496-509 of 3.97) when the header has `bits` and the new high flag.

### The WavPack header

Little-endian, as `WavpackHeader3` in `unpack3.h` and `WavpackHeader` in 3.97's `wavpack.h`:

| Offset | Size | Field | Versions |
|---|---|---|---|
| 0 | 4 | `ckID`, `wvpk` | all |
| 4 | 4 | `ckSize`: 3.97 writes the header's size less 8, 28 (`wavpack.c` 861); nothing reads it | all |
| 8 | 2 | `version`: 1, 2 or 3 | all |
| 10 | 2 | `bits`: 0 for lossless; see below | 2, 3 |
| 12 | 2 | `flags` | 3 |
| 14 | 2 | `shift`: the low bits the samples lack | 3 |
| 16 | 4 | `total_samples` | 3 |
| 20 | 4 | `crc`: the check of the audio the `.wv` decodes to | 3 |
| 24 | 4 | `crc2`: the check of the audio with the `.wvc` | 3 |
| 28 | 4 | `extension`: the source file's extension ("wav", "raw" or what `-r` read) | 3 |
| 32 | 1 | `extra_bc`: the bytes after the last sample of a raw file | 3 |
| 33 | 3 | `extras`: those bytes | 3 |

- **Sizes:** 10 bytes in version 1, 12 in version 2, 36 in version 3; libwavpack reads 10, then 2 or 26 more by the low byte of `version` (`unpack3_open.c` 157-173), and refuses a version other than 1 to 3 (175). 1.0 and 2.0 write 2 and 4 in `ckSize`, the size less 8, as 3.x writes 28.
- **Versions 1 and 2:** libwavpack sets the flags from the WAV header (mono or not), the shift to 16 less the bits per sample, and version 1's `bits` to 0 (`unpack3_open.c` 218-225). The total samples come from the data chunk's size.
- **`bits`:** in version 2, and in version 3 without the new high flag, the cap on the bits of a coded sample, written as the option less 3 (`-b6` stores 3; the documents of 2.0 to 3.2 give the option as 4 to 15: "the upper limit on the average number of bits WAVPACK will store for each sample"), read so by `get_word2` and `get_word3`. In version 3 with the new high flag (hybrid mode), the target in bits per sample of all channels times 256: `-bN` of 3.97 writes N kbps as N × 256,000 / rate, held to 64 × 256, and N bits per sample (below 21) as (N - 3) × 512 + 1,374, halved for mono (2,910 and 1,455 for `-b6`), "to match the old lossy mode" (`wavpack.c` 906-936); `init_words3` takes `bits` less 768 (halved first for stereo) as the bitrate of one channel.
- **The total samples (version 3):** libwavpack takes the count from the data chunk's size (`unpack3_open.c` 153) and ignores the header's; 3.97's `wvunpack` takes the header's (`wvunpack.c` 708, 950). The parser reports both and the header's is the stream's.
- **Checksums:** version 3 only. `crc` and `crc2` start at 0 and take each decoded sample as `crc = crc × 3 + sample` (stereo: `crc × 9 + left × 3 + right`), over the samples before the shift (`unpack3.c` 340-1170, `pack.c` 189-865 of 3.97); the version 4 block CRC is the same sum from all ones. A lossless file has `crc` equal to `crc2`; 3.97 reports "lossy" when they differ. Versions 1 and 2 have none: "WVUNPACK does verify that the restored data is exactly the correct length" (2.0's `WAVPACK.TXT`).

### The flags of version 3

From `unpack3.h`; the use is that of `unpack3.c` and 3.97's `wavpack.c` 906-955 and 1132-1135.

| Bit | Name | Meaning |
|---|---|---|
| 0x0001 | `MONO_FLAG` | one channel |
| 0x0002 | `FAST_FLAG` | "non-adaptive predictor and stereo mode": 3.97's `-ff`, 3.0's `-f` |
| 0x0004 | `RAW_FLAG` | raw file, no WAV header |
| 0x0008 | `CALC_NOISE` | not stored |
| 0x0010 | `HIGH_FLAG` | the high mode's decoder path (3.97's default lossless and `-h`) |
| 0x0020 | `BYTES_3` | samples of 3 bytes (17 to 24 bits) |
| 0x0040 | `OVER_20` | lossless over 20 bits: 4 low bits coded apart, shift 4 |
| 0x0080 | `WVC_FLAG` | not stored; set by the decoder when it has the `.wvc` |
| 0x0100 | `LOSSY_SHAPE` | first-order noise shaping (hybrid; the default from 64 kHz) |
| 0x0200 | `VERY_FAST_FLAG` | not stored |
| 0x0400 | `NEW_HIGH_FLAG` | new high mode; with `bits`, hybrid mode |
| 0x0800 | `CANCEL_EXTREME` | the kludge below |
| 0x1000 | `CROSS_DECORR` | cross-channel decorrelation (only with `EXTREME_DECORR`) |
| 0x2000 | `NEW_DECORR_FLAG` | the 7-term decorrelator |
| 0x4000 | `JOINT_STEREO` | mid/side (lossless high mode and hybrid) |
| 0x8000 | `EXTREME_DECORR` | the 18-term decorrelator (`-h`) |

- **The kludge:** with 16 bits used, 3.97 stores the cross-channel flag without the extreme one as `EXTREME_DECORR | CANCEL_EXTREME` (`wavpack.c` 1132-1135, "Because I ran out of flag bits"). A reader with `EXTREME_DECORR` refuses the file if a not-stored flag is set, or if `bits` is set together with the new high flag and the fast or high flag, or with `CROSS_DECORR`; it then clears both on `CANCEL_EXTREME`. Without `EXTREME_DECORR` it clears `CROSS_DECORR` (`unpack3_open.c` 184-204, `wvunpack.c` 472-492). The node shows the stored bits, and the stream the effective ones.
- **The modes the flags select** (`unpack3.c`): lossless `FAST_FLAG` (`get_word3`, sum and difference); lossless without `FAST_FLAG` or `HIGH_FLAG` (`get_word3`, a per-sample choice among sum and difference, left and difference or right and difference by running levels); lossless `HIGH_FLAG` (`get_word1`, or `get_old_word1` without `NEW_HIGH_FLAG`, with the decorrelation terms of `simple_terms`, `default_terms` or `extreme_terms`); lossy `FAST_FLAG` and default (`get_word3` with the cap); hybrid (`get_word4` and the `.wvc` bits); version 1 and 2 (`get_word2`, sum and difference).
- **The WAV header libwavpack takes** (`unpack3_open.c` 145-149): format tag 1, one or two channels, a sample rate above 0, 16 to 24 bits per sample, at most 3 bytes per sample, a block align that is a whole number of samples that hold the bits; each chunk before the data at most 1 MiB (99, 121). The encoders take the same (`wavpack.c` 803-807 of 3.97); 1.0 to 3.2 take 16 bits only. libwavpack gives the channel mask 4 (center) for mono and 3 (left/right) for stereo (229).

### Where libwavpack departs from 3.97

- **Version 1 mono:** libwavpack decodes version 2 mono and version 1 and 2 stereo (`unpack3.c` 1079, 1105) and returns no samples for a version 1 mono file; 3.97's `unpack.c` 1437 decodes `version < 3` mono, and 1.0's documentation says it compresses mono. The decoder follows 3.97, and the cross-check holds it to 1.0's and 3.97's `wvunpack`, not libwavpack (listed as a departure).
- **The total samples** of version 3 (above).
- **Raw files:** libwavpack reads none.

## What the files show

The fixtures pull request ran the original programs (`wavpack_legacy_tool.py`, `make_external_wavpack_legacy_fixtures.py`): 1.0, 2.0, 3.0, 3.2 and 3.4 as real-mode DOS programs and the DOS build of 4.31 under DOSBox 0.74-3 (Ubuntu 24.04), and the Win32 build of 3.97 directly on Windows 10, over the signals of `wavpack_signal.py`, and wrote 109 files of versions 1 to 3 (`tests/fixtures/wavpack/legacy`, 102 `.wv` and 7 `.wvc`, 43 of them 3.97's) and 8 of version 4 (`tests/fixtures/wavpack`). A second run writes the same bytes. Each file was decoded by the release's own `WVUNPACK` (3.97's `wvunpack.exe`) in the same run, and libwavpack 5.6.0 built with its legacy decoder (`wavpack_legacy.c`) reads the files of 1.0 to 3.4 as the manifests say (`crosscheck_wavpack_legacy_fixtures.py`: 0 disagreements); 3.97's were written on a machine without a C compiler, so their cross-check is the CI job's. Where this section says "the source", it follows 3.97's `wavpack.c`.

### What did not run

- **3.97 under Wine:** the 32-bit Wine 9.0 of the Ubuntu container (the only Wine installed; `wine64` is not) crashes in its loader before it reads the program: `free(): invalid pointer` or a segmentation fault at `wine wineboot`, with a new prefix, under `xvfb-run`, with address-space randomization off, and for `cmd /c echo` as for `wavpack.exe`. 3.97's files were therefore written on Windows 10, where `wavpack_legacy_tool.py` runs the program directly ("3.97", below).
- **The correction file of `-c` (3.4 and 3.97):** the `.wvc` files are bitstreams with no header or signature, and the main file is the same with and without `-c` (`external-wv340-hybrid-320-correction.wv` equals `external-wv340-hybrid-320.wv` byte for byte, and so for 3.97's).

### 3.97

- **3.97 writes 3.4's files.** Each of the 38 files of 3.4's set (`external-wv397-*` against `external-wv340-*` of the same name: the modes, more than 16 bits, hybrid with and without `-c` and its `.wvc`, raw with 0 to 3 bytes left over, the chunks and the trailer, the lossyWAV file, the standard input) is 3.4's byte for byte, and 3.97's `wvunpack` writes the same PCM, with the same MD5, as 3.4's `WVUNPACK` for each. This confirms what 3.4's document says ("identical to the 3.96 versions for Win32", apart from long file names and the self-extraction): the parser may take 3.4's files as 3.97's, and the flags, `bits`, `crc` and trailer of the sections below hold for both.
- **Raw and hybrid together** (`-r2 -b320`, `-r1 -b320` and `-r2 -b320 -c`, which 3.97's document adds: "With version 3.96 the raw and hybrid modes are usable together (with a warning)", answered by `-y`): the bitstream, `bits` (1,858), `crc` and `crc2` are those of the hybrid file of the same audio from a WAV file, and so is the `.wvc`; the header differs only in the raw flag (`2404` stereo, `2405` mono, against `2400` and `2401`) and the extension `RAW`. `wvunpack` restores the raw input with the correction file and gives the hybrid file's lossy samples without it. 3.4 was not tried with these options.
- **3-byte samples of any length:** 3.97 encodes a 24-bit WAV file of 24,000 stereo frames (144,000 bytes of data, `external-wv397-lossless-24-stereo-long.wv`, `fc70` with shift 4) and its `wvunpack` restores it whole, where 3.4 under DOSBox refuses more than 16,383 bytes ("What the encoders do on other input").

### The header

- **`ckSize`:** 2 in version 1, 4 in version 2 and 28 in version 3 (the header's size less 8), in every file.
- **Sizes:** the WavPack header follows the data chunk's 8-byte header at once and the bitstream the header: 10, 12 and 36 bytes. 3.0 to 3.4 keep a `LIST` chunk before the data in the stored header (the header starts after it); 1.0 and 2.0 keep it too.
- **The stored WAV header is the source's:** the RIFF size and the data size are the source's (96,036 and 96,000 for a file of 21 to 63 KB), so the data chunk runs past the end of every file.
- **`bits` in versions 2 and 3 without the new high flag is the option less 3:** `-b4` stores 1, `-b6` 3, `-b8` 5 and `-b15` 12 (2.0, and 3.0 and 3.2 for `-b6`), as `wavpack.c` 169-170 does (`if (bits < 24) bits -= 3`). The documents' "4 to 15" is the option, not the field.
- **`bits` of the hybrid mode (3.4, new high flag):** `-b320` stores 1,858 at 44.1 kHz and 853 at 96 kHz (320 × 256,000 / rate, rounded), for mono and for 24 bits too; `-b6` stores 2,910 for stereo and 1,455 for mono, which is (6 - 3) × 512 + 1,374 and its half. The design's earlier "N × 512 + 1,374" is for the option already less 3.
- **The checks:** `crc` is the same for every lossless file of the same audio and release (30faeec2 for the 16-bit signal in 3.0, 3.2 and 3.4); a hybrid file has the lossy decode's in `crc` and the lossless one in `crc2`, and the lossless files and the old lossy ones (3.0 and 3.2 `-b`) have 0 in `crc2`. Versions 1 and 2 have neither.
- **`extension`:** 3.0 and 3.2 write `wav` and `raw` in lower case, 3.4 `WAV` and `RAW` in upper case, also for input from the standard input (`-`), so it is not taken from the name. A raw file (`-r1`, `-r2`) has the total samples of its whole samples (24,000 for 96,000 bytes) and, with 1 to 3 bytes left over, `extra_bc` of that count and the bytes in `extras` (3.4).
- **The total samples** equal the data chunk's size over the block size in every version 3 file: no file shows the mismatch the parser reports.

### The flags each option writes

- **3.0 and 3.2** write the same files, byte for byte, in every case but one: the default `0000`, `-f` `0002` (`FAST_FLAG`), mono `0001`, raw `0004` (`0005` mono), the old lossy `-b6 -f` `0002` and mono `-b6` `0001`, both with `bits` 3. The exception is stereo `-b6` (`0000`, `bits` 3), where the `crc` and the bitstream differ: 3.2 codes the lossy mode differently from 3.0.
- **3.4, lossless:** the default `fc10` (high, new decorrelator, cross-channel, joint stereo, and the kludge: extreme and cancel-extreme together, so not extreme); `-h` `f410` (extreme, new high); `-f` `0000` and `-ff` `0002`, whose bitstreams are byte for byte those of 3.0 and 3.2 without the option and with `-f` (the headers differ in the extension); `-j0` `bc10` (no joint stereo); `-j1` the default; mono `2411`; raw `fc14` (stereo) and `2415` (mono).
- **3.4, more than 16 bits:** 18 bits `fc30` with shift 6, 20 bits `fc30` with shift 4, 24 bits `fc70` (`OVER_20`) with shift 4, the samples left-aligned in 3 bytes; hybrid 24 bits `2420` with shift 0.
- **3.4, hybrid:** the default `2400` (new decorrelator and new high; no high flag), mono `2401`, `-h` `a400` (extreme decorrelator), `-j1` `6400`, `-j0` `2400`, `-s1` `2500` (`LOSSY_SHAPE`), 96 kHz `2500` by default and `2400` with `-s0`. `-f` and `-ff` change nothing in hybrid mode: their files are byte for byte the default's. The old lossy mode has no new high flag (`0000`, `0002`, `0001`).
- **The names of the compression mode** therefore follow the flags: lossless `FAST_FLAG` is "very fast", a file without the fast and high flags "fast" (3.0's and 3.2's default, 3.4's `-f`), high without extreme "normal" (3.4's default), with extreme "high" (3.4's `-h`); a hybrid file is "normal", and "high" with the extreme decorrelator; the old lossy mode follows the lossless rule. The fixtures name every option's flags above; none of 3.4's hybrid files has the fast flag.

### The trailer and the bytes after the audio

- **3.4 writes the trailer** (16 bytes of 0xFF and the source's chunks after the data, here an `id3 ` chunk of 36 bytes: 52 bytes at the end of the file) and its `WVUNPACK` restores the whole WAV file, the chunks after the data included.
- **3.0 and 3.2 drop the bytes after the audio** and write no marker (no run of 0xFF in the files), and their decoders give the chunks before the data back and not those after it, which `file_matches_input` records. 1.0 and 2.0 ask first ("extra data following samples cannot be restored", an answer of yes on their standard input) and drop them as well.
- **3.97 writes the trailer as 3.4 does,** as its source says (`wavpack.c` 1068-1112): `external-wv397-chunks.wv` and `external-wv397-trailer-only.wv` are 3.4's files byte for byte, and its `wvunpack` restores the whole WAV file.

### What the encoders do on other input

- **3.4 refuses a WAV file of 3-byte samples with more than 16,383 bytes of data**: it stops with "couldn't read all samples, file may be corrupt!!" and writes no file (2,730 stereo frames and 5,461 mono frames pass, one more frame does not; 16-bit files of 96,000 bytes and raw files of any length pass). The 18, 20 and 24-bit fixtures are therefore 2,700 frames long. Only under DOSBox 0.74-3; 3.97 on Windows takes the longer file ("3.97").
- **3.0's encoder stops when its output is redirected** (it has written the header, 80 bytes, and nothing more); run without a redirect, it finishes in a second. The tool runs it so. The redirect captures the messages of 1.0, 2.0 and 3.2; 3.4's and 4.31's output files stay empty.
- **The lossyWAV `fact` chunk** (`tests/fixtures/wav/lossywav-standard.wav`) is kept in the stored header by 3.4 (the file's header is the WAV's up to the data), as any chunk.

### Version 4 files of 4.31

- The stream version is 0x404 in every block, the blocks carry no block checksum sub-block (0x2F) and no new configuration block (0x2A); the configuration block (0x25) is there. The analyzer reads them with its current rules: "WavPack 4 stream, no WavPack 5 features" for the encoder claim, which is the first fixture set of a library older than 5.0, and `Compression Mode` normal for the default and `-x`, fast for `-f` and high for `-h` (the extra processing flag is set by `-x` without a level: "none" for `Extra Processing Level`, where libwavpack 5.6.0 reports mode extra).
- `-m` stores the MD5 in the last block (sub-block 0x26), `-c` writes a correction file the analyzer reads as a correction file, and 24-bit input gives lossless 24-bit blocks. libwavpack 5.6.0 decodes all eight without an error and the decoded audio has the MD5 of the input (the decoded PCM MD5 of the manifests); the goldens are the analyzer's.

### What the readers do with these files

- **libwavpack 5.6.0 with `ENABLE_LEGACY`** opens every file of versions 1 to 3 that starts with a RIFF header (of 1.0 to 3.4: 3 of version 1, 7 of version 2 and 44 of version 3; 3.97's 31 such files are left to the CI job, 30 of them 3.4's byte for byte) and unpacks the stated samples with no error counted; the MD5 of the samples equals the original decoder's for every file (a hybrid file with its `.wvc` equals the input, and alone equals the lossy decode of the release's `WVUNPACK`). It refuses a raw file ("not compatible with this version of WavPack file!", the reader of version 4 takes it) and, for version 1 mono (`external-wv100-mono-16.wv`), opens it and unpacks no sample, which the 1.0 decoder restores whole: both departures of "Where libwavpack departs from 3.97" are shown by files.
- **FFmpeg 6.1.1 and MediaInfo 24.01 read every file that starts with a RIFF header as a WAV file** (the data chunk is the rest of the file) and refuse the raw files; neither names a file WavPack. The cross-check fails if one does, and for `wvunpack` 5.9.0, which has no legacy decoder, if it opens one (run in CI only: wavpack 5.9.0 is not installed here). The FFmpeg of Ubuntu 26.04, which CI runs, takes two of the raw files (`external-wv300-raw-stereo`, `external-wv320-raw-stereo`) for VVC video from their bytes, a guess for a file with no header, so the cross-check shows a raw file's reading and does not hold it.
- **The analyzer before the parser** read the version 1 to 3 files as WAV files with the `wav.truncated` family of findings (the black boxes of the first draft: a crafted header of each version and a raw header gave the same); the parser (pull request 3) reads them as WavPack, and `WavPackLegacyFixtureTests` holds every file to its manifest and a golden.

## What is read and reported

### Nodes

- **The WAV header,** as the WAV parser's nodes name a RIFF header and its chunks and as the version 4 wrapper lists them ([`wavpack-wrapper`](../../src/Encspect.Host/Topics/wavpack-wrapper.md)): the RIFF header with its stated size, each chunk with its ID and size, the fmt fields (read with the shared `WavHeader`), a lossyWAV `fact` chunk with `FactText`, and the data chunk header with the PCM size it states.
- **The WavPack header,** each field of the table above as a typed node; the flags as 16 boolean fields, the not-stored ones named so; `bits` named by what it is in the file ("Lossy Bit Limit" in bits, or "Hybrid Target" in bits per sample times 256); `shift`, `total_samples`, `crc` and `crc2` (shown, compared only by a decoder), the source extension, and for raw files the extra bytes.
- **The bitstream** as one node from the header's end to the trailer or the end of the stream's data.
- **The trailer,** where the 16 bytes of 0xFF are followed by chunk headers that chain to the end of the stream's data: the marker and the chunks, as the wrapper's trailer is listed. Without a decoder the parser looks for the first such run after the header; 16 bytes of 0xFF that no chain follows are left in the bitstream (`wavpack.legacy-trailer-unrecognized`, info). The decoder (decision 5) confirms where the bitstream ends.

### StreamInfo and statistics

- **Codec:** "WavPack lossless", "WavPack hybrid" (version 3 with `bits` and the new high flag) or "WavPack lossy" (version 2 or 3 with `bits` and without it), the names version 4 uses.
- Sample rate, channels and bits per sample from the WAV header; for raw files 44,100 Hz and 16 bits, as 3.97 sets them (`wavpack.c` 864-867), and the channels from the mono flag. The samples and the duration; the average bitrate over the bitstream's bytes.
- **Channel layout:** center for mono, left/right for stereo, as libwavpack's mask.
- **Statistics,** under the names version 4 uses where the concept is the same:
  - "Compression Mode": lossless `FAST_FLAG` "very fast", neither fast nor high "fast", high without extreme "normal", with extreme "high" (the options of 3.97 that write them: `-ff`, `-f`, the default, `-h`); hybrid and lossy "fast", "normal" or "high" the same way. The fixtures check each name against the option that wrote it; 3.0 and 3.2 wrote "very fast" with their `-f` and "fast" by default, as the flags of their files show ("What the files show").
  - "Hybrid Target Bitrate" (bits per sample of one channel, `bits` / 256 / channels) and "Hybrid Target Rate" (kbps, `bits` × rate / 256,000), version 3 hybrid.
  - "Lossy Bit Limit" (bits), version 2 and old version 3 lossy; a new name, since no other format has the concept.
  - "Stereo Coding" as one value for the stream, from the flags and the version: "Mid/side stereo" (versions 1 and 2, the fast mode, joint stereo), "Left/right stereo" (the high and hybrid modes without joint stereo), and for the default lossless mode of 3.0 to 3.2, which chooses per sample, "Adaptive" with the description saying so. Not a tally or a series: there are no blocks to count.
  - "Source Extension"; `IsLossless`, `IsHybrid`, `IsNoiseShaped` (the noise shaping flag), `IsRaw`.
- **Not reported without a decoder:** Lost Samples, Bitrate Over Time, the bitrate distribution and mode (one bitstream has no variation to show).

### Encoder detection

- **The version field names the generation:** "WavPack 1", "WavPack 2" or "WavPack 3" (`ClaimRole.Encoder`, medium confidence), as libwavpack's `WavpackGetVersion` reports "the major version number of the WavPack program (or library) that created the open file". Medium because the Cool Edit filter of the 3.97 sources (`cool_wv.c`) writes version 3 too, from a header it builds.
- **Nothing in a file names the release** within a generation. The features of a version 3 file (24 bits, hybrid mode, the extreme decorrelator, the trailer) are listed as evidence, never turned into "3.4 or later": releases between 3.2 and 3.4 are not at hand. What each release writes is recorded as observed behavior of Encspect's test files in [development.md](../development.md), as the rules for articles ask.
- **lossyWAV:** a `fact` chunk in the stored WAV header is read with `FactText` and claims the processor at high confidence, the shared path of [lossywav-detection.md](lossywav-detection.md).
- **A version 4 stream of 4.31** keeps today's "WavPack 4 stream, no WavPack 5 features" claim; its fixtures are the first of a libwavpack older than 5.0, which [wavpack.md](../formats/wavpack.md#encoder-detection) says it lacks.

### Detection

- **The conflict:** a legacy file starts with a RIFF header whose first chunk fits, so the WAV sniff rates it 100 (frames > 0). Both start at offset 0 and neither has a checksum the sniff computes, so the ranking cannot tell them apart.
- **The legacy sniff** (in the WavPack parser, decision 4): class 100 for a RIFF/WAVE header whose chunks fit up to a `data` chunk header that is followed by `wvpk` and a version of 1 to 3 with the bytes that version's header needs; class 100 for a raw file (`wvpk` at the start, version 3, the raw flag, `ckSize` 28); class 80 for `wvpk` after the data chunk header with another version, or a header the data cuts short. `End` is the end of the data; `DeclaredEnd` is null (the RIFF size is the source's).
- **The WAV sniff steps back** (decision 3): when the payload of its first `data` chunk starts with `wvpk`, the WAV parser rates the file 70 with a note. Its own analysis, forced, is unchanged.
- **Version 4 does not compete:** its sniff needs major version 4 at a header, and `wvpk` with version 3 at the start fails it.
- **Tags:** APEv2 and ID3v1 at the end and ID3v2 in front are the host's; the stream ends before the first trailing tag. libwavpack's test of the first byte (`R`) is not copied, so a legacy file behind an ID3v2 tag is read.

### Findings

New codes carry the prefix `wavpack.legacy-`; where version 4 has the concept, its code is reused.

- `wavpack.version-unsupported` (a `wvpk` header after the data chunk with a version other than 1 to 3).
- `wavpack.flags-invalid`, once for each problem: a not-stored flag set with the extreme flag; `bits` with the new high flag and the fast or high flag; `bits` with the cross-channel flag (libwavpack refuses each).
- `wavpack.total-samples-mismatch` (version 3: the header's count against the data chunk's size).
- `wavpack.legacy-header-truncated` (the data ends inside the WavPack header).
- `wavpack.legacy-header-size` (info: a version 3 `ckSize` other than 28).
- `wavpack.legacy-wav-format-unsupported`, once for each field libwavpack refuses (format tag, channels, rate, bits, block align), and for a chunk before the data over 1 MiB.
- `wavpack.legacy-trailer-unrecognized` (info).
- Decoder (decision 5): `wavpack.legacy-crc-mismatch`, `wavpack.legacy-data-ended` (the bitstream ends before the stated samples, the only check of versions 1 and 2), `wavpack.legacy-crc2-needs-correction-file` (info: a hybrid file decoded without its `.wvc`, whose `crc2` is not compared).

## Decoding (decision 5)

- **Size:** `unpack3.c` holds about 1,400 lines of decoding (five word readers, three term tables, separate loops for each mode, mono and stereo, over 20 bits, noise shaping and the correction bits) and `unpack3_open.c` 289 lines; about 1,500 lines of C#. Nothing is shared with the version 4 decoder beyond the shape of a decorrelation pass: the entropy coders, the weights (9-bit `apply_weight_n`) and the fixed terms differ.
- **What it adds:**
  - the only check of a legacy file's audio: `crc` (and `crc2` once the 0.4 pairing hands it the `.wvc`), and the length check of versions 1 and 2;
  - the end of the bitstream, which places the trailer and makes the average bitrate exact, and Bitrate Over Time (the bit position each second) under version 4's name;
  - the measurements (spectrum, cutoff), and Lost Samples.
- **Where it lives:** `Encspect.Formats.WavPack.Decoding`, beside the version 4 decoder, registered under the same ID. It computes in integers only, as the version 4 decoder does, so the floating-point rules of [CLAUDE.md](../../CLAUDE.md) do not apply; the project holds no lossy floating-point decoder, so no `Decoding.Lossless` namespace is split off.
- **How it reads:** one unit, the bitstream, read through a window of the mapped bytes, never loaded whole; PCM handed in chunks of 4,096 samples; the cancellation token checked per chunk; memory bounded by the chunk and the 18 decorrelation passes.
- **Work units** ([decoders.md](decoders.md#work-instead-of-samples)), charged per chunk before it is decoded: `DecodeWork.ForSample` for 2 or 3 bytes, for each sample of each channel; 16 per byte of bitstream read; 11 per decorrelation pass per sample of each channel; the weights measured by `DecoderWorkTests`.
- **Muting:** libwavpack mutes a chunk whose samples pass its limit (`ATTEMPT_ERROR_MUTING`); the decoder does not mute (no output leaves the analyzer but the measurements) and counts the CRC.

## Verification and cross-checks

- **The model:** `wavpack_legacy_model.py` reads the WAV header, the WavPack header, the flags, the trailer and the figures without the parser's code and writes each fixture's manifest.
- **libwavpack with legacy support:** `wavpack_tool.py` builds a second program, `wavpack_legacy.c`, from the pinned sources with `-DENABLE_LEGACY` and `unpack3*.c`: whether the file opens and the error, `WavpackGetVersion`, rate, channels, bits, samples, `WavpackGetMode`, and a whole pass of `WavpackUnpackSamples` with the MD5 of the samples and `WavpackGetNumErrors` (the CRC). Every fixture's manifest states the outcome, as `crosscheck.decode` does for version 4.
- **The original decoders as black boxes:** 3.97's `wvunpack -v` (verifies the CRC; on Windows, or under Wine) and the decoded WAV; 4.31's `WVUNPACK.EXE` and, for versions 1 and 2, the `WVUNPACK.EXE` of the same release under DOSBox, compared by the MD5 of the decoded WAV with the encoder's input (lossless) and with libwavpack's samples (lossy). Version 1 mono is held to them only.
- **Current tools:** `wvunpack` 5.9.0 must refuse each file with the "deprecated" message; FFmpeg and MediaInfo are recorded as reading a WAV of PCM (`FFMPEG_READS_AS_WAV`, `MEDIAINFO_READS_AS_WAV`), and the run fails if one starts to read a file as WavPack, so the entry is reviewed.
- **Tests:** `WavPackFixtureTests` (manifests and goldens), the finding tests and `FindingCoverageTests`, `DescriptionTests`, `DetectionOrderTests` (the WAV and WavPack sniffs on every legacy fixture), `CorruptWavPackFileTests` over the legacy fixtures (no exception, no hang), `MemoryTests` and `ComplexityTests` (a header followed by a long run of 0xFF; a stored WAV header of many chunks). With the decoder: `WavPackDecoderFixtureTests`, `DecoderChargeTests`, `DecoderWorkTests`.

## Fixtures

### Encoder-written, `tests/fixtures/wavpack/legacy/`

From `wavpack_signal.py`'s deterministic signals, 24,000 frames each (0.54 s at 44.1 kHz; a legacy file is one bitstream, so length adds nothing but bytes, and the files stay small). `wavpack_legacy_tool.py` pins the archives above (RRW, a Wayback copy, and encspect-private as the last address) and runs the encoders under DOSBox; the files and manifests are committed, and CI recomputes the manifests only. **As written:** the list below is the design's; the fixtures differ by what the files showed: the 18, 20 and 24-bit files are 2,700 frames long, 3.97 (run on Windows, not under Wine) has 3.4's set and its own additions, and `external-wv340-*` and `external-wv431-*` carry the names of the files in `tests/fixtures`.

- **1.0** (DOS): 16-bit stereo and mono (the only options).
- **2.0** (DOS): lossless stereo and mono; `-b4`, `-b8` and `-b15` stereo, `-b6` mono (version 2 `bits`).
- **3.0 and 3.2** (DOS, 16 bits only): the default, `-f`, `-b6`, `-b6 -f`, mono, `-r2` and `-r1` raw, and a WAV with a `LIST` chunk before and after the data (does the encoder drop the trailer, as its document says).
- **3.4** (DOS): the default, `-f`, `-ff`, `-h`; mono; 18, 20 and 24 bits (shift 6 and 4, `OVER_20`); hybrid `-b320` and `-b6`, `-b320 -h`, `-b320 -c` (with its `.wvc`), `-b320 -j1`, `-b320 -s1`, `-b320` at 96 kHz (noise shaping by default); `-j0` lossless; `-r2` raw with 1 to 3 bytes left over; the WAV with chunks before and after the data (the trailer); stdin input (the extension "wav" from `-`); a lossyWAV-processed WAV (the `fact` chunk). The file with an APEv2 and an ID3v1 tag appended (3.97 only in the plan) is a hand-built one in the parser's pull request.
- **3.97** (Win32, run on Windows 10): 3.4's set, under the same names, and what its document adds: `-r2 -b320`, `-r2 -b320 -c` (with its `.wvc`) and `-r1 -b320` (raw and hybrid together), and a 24-bit file of 24,000 frames (`lossless-24-stereo-long`). `-e` writes a self-extracting `.exe`, which is not covered.
- **4.31** (DOS), into `tests/fixtures/wavpack/` as version 4 files (stream version 0x404): the default, `-f`, `-h`, `-x`, `-b320 -c`, `-m` (MD5), 24 bits.
- **File names** inside DOSBox are 8.3; the tool renames them to `external-wv340-hybrid-320-correction.wv` and the like (`wv100`, `wv200`, `wv300`, `wv320`, `wv340`, `wv397`, `wv431`; the correction file of a pair is `...-wvc.wvc`).
- **What the fixtures settled,** recorded in "What the files show": `ckSize` of 1.0 and 2.0; the flags each option writes in each release; which releases write the trailer; the extension 3.0 to 3.2 write (lower case); hybrid mode, which 3.4 has (3.0 and 3.2 have the old lossy mode only); that 3.97 writes 3.4's files.

### Hand-built, `built-legacy-*`

From the encoder-written files: a version of 0 and 4; each refused flag combination; a not-stored flag; `ckSize` 0; a WAV header libwavpack refuses (8 bits, three channels, format tag 3, a chunk over 1 MiB); a header cut short in each version; a bitstream cut short; a flipped bit (CRC fails); `crc` changed; the total samples changed; 16 bytes of 0xFF without chunks after them; a raw file with `extra_bc` of 4; an ID3v2 tag in front.

## Running the old encoders

- **DOSBox** for 1.0 to 3.4 and 4.31 (Ubuntu 24.04's `dosbox` 0.74-3, installed from its archive): headless with `SDL_VIDEODRIVER=dummy` and `SDL_AUDIODRIVER=dummy`, the work folder mounted as `C:`, the commands in the autoexec with each one's output redirected to a file, and `exit`; the files the programs write are read back from the folder. The programs of 1.0 to 3.4 are real-mode programs, 4.31 binds the DOS/32A extender into its executables; each run takes a second or two. The programs print their messages to the console and some to no redirectable handle (3.4's and 4.31's output files stay empty); to read one, run the autoexec without a redirect under `xvfb-run` and take a screenshot with `import -window root` (how "couldn't read all samples, file may be corrupt!!" was found).
- **3.97** runs directly on Windows: `wavpack_legacy_tool.py` runs each command through `cmd.exe` in a folder that holds the programs and the files. Elsewhere it runs Wine's `cmd`, as `old_mkvmerge_tool.py` and `lame_win_tool.py` run old Windows builds, which does not work in the Ubuntu container (see "What did not run"). The DOS programs' archives of 1.0 to 3.0 use compression methods that Python's `zipfile` cannot read, so the tool unpacks them with `unzip`.
- **Where it runs:** locally, once; the files are committed and CI recomputes the manifests only (`--manifests-only`), as for the Monkey's Audio legacy files. A second full run writes the same files.

## Docs and articles

- [wavpack.md](../formats/wavpack.md): a section "WavPack 1 to 3" (layout, what is read, the departures from libwavpack, findings), the line under "Not covered" removed, the 4.31 observation under encoder detection; [the formats index](../formats/README.md) line 73 and the README's format line; [development.md](../development.md): the legacy fixtures, the tools and the cross-check.
- **Articles:** `wavpack-legacy` (new: the header, one bitstream, the flags, the trailer marker, the correction bitstream, the check), and additions to `wavpack-wrapper` (the stored header of versions 1 to 3), `wavpack-hybrid-mode` (hybrid mode began in version 3) and, with the decoder, `wavpack-audio-checksums` (the legacy check from 0).

## Not covered

- The `.wvc` of version 3 opened alone: it has no header or signature, so it is not recognized. With its `.wv` it is the 0.4 pairing's (the other half of item 3), and a pair can be checked only by decoding (`crc2`).
- Self-extracting files of 3.97 (`-e`): an `.exe` (`wvselfx.exe`, 26,624 bytes) with the `.wv` appended.
- WavPack files written by the Cool Edit filter are read as any version 3 file; no fixture holds one.
- Without the decoder: every check of the audio, Lost Samples, the bitrate over time, the measurements.

## Decisions for the lead

Decided by the lead, 2026-11-18: every recommendation below, (b) for decision 1 and (a) for decisions 2 to 6. For decision 2, the lead allowed the fixture session to run the old encoders: 3.97 under Wine and the DOS builds under DOSBox, from Ubuntu's archive, locally once.


1. **Which builds.** (a) The plan's three, 3.4, 3.97 (the first BSD release) and 4.31; (b) every release RRW keeps: 1.0, 2.0, 3.0, 3.2, 3.4, 3.97 and 4.31. Recommended: (b); the version 1 and 2 headers come only from 1.0 and 2.0, and the old lossy mode, the old fast and default modes and the 16-bit-only era from 3.0 and 3.2. They are small DOS programs run the same way.
2. **Running them.** (a) Wine for 3.97 and DOSBox (`dosbox` from Ubuntu's universe) for the DOS builds, locally once, files committed, manifests recomputed in CI; (b) the same in a CI workflow; (c) 3.97 only, under Wine. Recommended: (a), with the permission to run these binaries given for the fixture session; (b) later if the files are ever written again.
3. **Detection.** (a) The WAV sniff rates a file whose data chunk payload starts with `wvpk` at 70; (b) the host's ranking adds more frames as a last key for overlapping candidates of one class, and the legacy sniff counts the WavPack header as one more frame; (c) the WAV parser routes the data chunk to a WavPack codec parser, the file staying a WAV. Recommended: (a); four bytes in one place, no change to the ranking every format relies on, and the file is named WavPack as its author and libwavpack name it. (b) fails when the chunk cap or a data chunk that fits gives both the same count; (c) reports a copy of the source's header as a container.
4. **One parser or two.** (a) The existing `WavPackParser` ("WavPack") takes both, by the first bytes, as libwavpack's open does; (b) a second parser, "WavPack 1 to 3". Recommended: (a); one format, one name, and a forced `--format WavPack` reads both. The legacy code lives in its own files of the project.
5. **The decoder.** (a) In item 3, as its own pull request after the parser, following `unpack3.c` (3.97 for version 1 mono); (b) header level only now, the decoder queued for 0.5; (c) not planned, with the trigger "a user brings legacy files to verify". Recommended: (a); 0.4 is the milestone of the free decoders, the source is BSD and complete, it is about 1,500 lines, and without it no legacy file's audio is ever checked and the trailer is found by a rule, not by the stream.
6. **Raw files.** (a) Detect and read them at header level (and decode them with decision 5's decoder, held to 3.97's `wvunpack`); (b) leave them unrecognized, as libwavpack does. Recommended: (a); 3.0 to 3.97 write them, and the header is the same.

## Pull requests, in order

1. **This design**, signed off.
2. **Fixtures:** `wavpack_legacy_tool.py` (the pins and the DOSBox runner), `wavpack_legacy_model.py`, `wavpack_legacy.c` and its build in `wavpack_tool.py`, `make_external_wavpack_legacy_fixtures.py`, `crosscheck_wavpack_legacy_fixtures.py`, the encoder-written files with manifests, the 4.31 version 4 files with their goldens; what the files show added to this design. The legacy files' goldens came with PR 3. **Done**; 3.97's files were added from Windows (Wine does not run in the container).
3. **Parser:** detection (the legacy sniff and the WAV sniff's step back), the nodes, the stream figures and claims, the findings, the hand-built fixtures, goldens, finding coverage, the cross-check, docs and the article; the plan's line updated. **Done.** As built: the findings of the design plus `wavpack.legacy-channels-mismatch`, `wavpack.legacy-bitstream-missing` and `wavpack.legacy-extra-bytes-invalid`, and `wavpack.legacy-header-size` for every version; the stereo coding value is "Adaptive stereo"; the article extends `wavpack-wrapper` and `wavpack-hybrid-mode`, and adds `wavpack-legacy`; the chunk over 1 MiB is crafted in a test, not a fixture.
4. **Decoder** (decision 5): the decoder, its notice, the decode goldens, the bitrate over time, the work units and timings, the decoder cross-checks; the plan's item marked done.
