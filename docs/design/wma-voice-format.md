# Windows Media Audio Voice: format description

**Status:** written on 2026-10-29 by a Claude agent (Opus) that read FFmpeg 6.1.1's LGPL WMA Voice decoder (listed under [Sources](#sources)) and Encspect's current Voice reader, and checked this text against the committed fixtures, Microsoft-encoded files and ffmpeg run as a black box. It contains no code. Whoever rewrites the Voice reader and the fixture writer must not read FFmpeg's `wmavoice.c`, nor the Encspect files that [Implementer's sources](#implementers-sources) marks as derived from it; FFmpeg serves only as a black-box cross-check (decision 10 in [decoders.md](decoders.md#decisions)).

This is the clean-room description that the audit of the Voice reader asked for, in the form of [truehd-format.md](truehd-format.md). It describes the bitstream as FFmpeg reads it, states FFmpeg's checks and its handling of damaged input as rules (Encspect's reader follows them, so that it counts the samples ffmpeg decodes), and describes what the reader reports. What the analyzer reads today, from the user's side, is in [asf-wma.md](../formats/asf-wma.md#wma-voice). Microsoft has published no specification of the codec, and none was found.

## Sources

**Read (FFmpeg 6.1.1, the release `scripts/fixtures/ffmpeg_sources.py` pins; the archive's SHA-256 is `8684f4b0…f968`):**

- `libavcodec/wmavoice.c` (SHA-256 `72ed5fcd…720b`): everything in this document about the codec data, packets, superframes and frames.
- `libavcodec/wmavoice_data.h` (`47d570cf…c173`): only to confirm that none of its tables decides a width; they hold the values of LSPs, gains and codebooks, which the reader does not decode.
- `libavcodec/decode.c` (`102dad9f…1700`): how a packet that the decoder consumes in parts is fed to it again, what an error return does to the rest of it, and the call that drains it at the end of the stream.
- `libavcodec/get_bits.h`: how the bit reader behaves past the end of its buffer.
- `libavformat/asfdec_f.c`: that the ASF demuxer hands each reassembled media object to the decoder as one packet.
- `VoiceTables.cs` says its tables come from "wmavoice.c, SHA-256 8684f4b0…"; that hash is the release archive's, not the file's.

**FFmpeg 8.0.1** (the release CI uses): its `wmavoice.c` differs in declarations, in the name of a dot product and in one added check: a frame of a type with pitch-adaptive window pulses or pulse sets is rejected where its first block has no pitch. Every such type has an adaptive codebook, which sets the pitch first, so the check never fires. `wmavoice_data.h` is the same, and `decode.c` feeds packets the same way. The black-box checks below were run with ffmpeg and ffprobe 6.1.1, the installed binaries.

**Encspect's code and scripts** were read to state what the current reader reports (node names, values, statistics, findings) and how the fixture writer draws its random bits; [Implementer's sources](#implementers-sources) lists which of them the implementer may read.

**Test files.** The committed fixtures in `tests/fixtures/asf`:

| File | SHA-256 | What it is |
| --- | --- | --- |
| `external-windows-wmavoice-cbr-mono-8k-5.wma` | `9a582bc2…38b4` | Windows Media Format SDK writer 12.0.26100.3343 (`wmvcore.dll`), "5 kbps, 8 kHz, mono", 3 s of tones; 14 packets of 187 bytes, 50 superframes |
| `external-windows-wmavoice-cbr-mono-16k-16.wma` | `48e5fe19…0729` | the same writer, "16 kbps, 16 kHz, mono", tones; 14 packets of 600 bytes, 100 superframes |
| `external-windows-wmavoice-cbr-mono-8k-4-speech.wma` | `26cbf6e3…e4ae` | the same writer, "4 kbps, 8 kHz", 6 s of a speech-like signal; two packets of 150 bytes per media object, 100 superframes |
| `external-windows-wmavoice-cbr-mono-8k-5-speech.wma` | `8b8e166d…2b10` | "5 kbps, 8 kHz", speech-like; 100 superframes |
| `external-windows-wmavoice-cbr-mono-8k-8-speech.wma` | `ced1e086…d803` | "8 kbps, 8 kHz", speech-like; 100 superframes; its last tail is longer than FFmpeg's cache |
| `external-windows-wmavoice-cbr-mono-16k-16-speech.wma` | `33145452…a354` | "16 kbps, 16 kHz", speech-like; 200 superframes |
| `voice-*.asf` (28 files) | reproduced byte for byte by `make_voice_fixtures.py` in CI | hand-built: valid streams of every frame type, rejected configurations, damaged packets and media objects |

Public files (downloaded by `check_wmavoice_samples.py`, never committed; SHA-256 in `tests/conformance/wmavoice-samples.json`): from the FFmpeg sample archive's `A-codecs/WMSP/` folder `filedown-prog_CBR-7K.wma`, `streaming_CBR-7K.wma`, `webserv-prog_CBR-7K.wma`, `streaming_CBR-11K.wma`, `webserv-prog_CBR-11K.wma`, `streaming_CBR-19K.wma`, `webserv-prog_CBR-19K.wma`, `streaming_CBR-19K-timecomp.wma`, `wma9audio-notworkmplayer.wma` and `wmav_8.wma`, and `wmv3-wmaspeeech.wmv` from `A-codecs/WMA9/WMAVoice/`.

**How the text was checked:**

- The author wrote a throwaway walker from this description (in the session's scratch folder, not committed; it is derived from reading FFmpeg and must not reach the implementer). It reads the media objects of a file (from the fixture's manifest where it lists them, else as ffprobe hands them over) and walks the packets, superframes and frames as described below.
- On all 34 committed Voice fixtures (the 28 `voice-*.asf`, the six Windows files; the encrypted `protected-wma-voice.asf` is not walked) it reproduces the golden report's superframes, invalid superframes, frames, samples and frames by type, and also the packets, packet bytes, superframes spanning packets, most superframes in a packet, superframes with a sample count, with residual LSPs and with additional data, the smallest and largest superframe, ignored bits, bits after the last superframe and skipped spillover bits. For the seven rejected configurations it rejects the same check the golden report's finding names.
- On the 11 sample-archive files it reproduces the superframes, frames, samples and frame type counts of `wmavoice-samples.json`, which equal ffmpeg's samples.
- Black-box mutations: 1,640 files made by flipping bits, replacing bytes, and rewriting the superframe count, the spillover or the residual flag of a packet header in 11 of these files (valid and damaged, one and two packets per media object, 10 and 16 LSPs, a cache overflow, music superframes). For every one, the walker's samples and superframes equal the samples ffmpeg 6.1.1 decodes and the frames ffprobe counts (`-count_frames`).
- Further black-box checks: the sample rate boundaries (43, 44, 321, 322, 7,989, 7,990, 22,097 and 22,098 Hz, with ffmpeg's messages), and a superframe whose sample count is 0 (ffmpeg outputs it as a frame of 0 samples, and counts it).

## Conventions

- Bits are read most significant first, from the first byte of a buffer on. `u(n)` is an n-bit unsigned field.
- floor(x) rounds toward minus infinity; ceil(log2 x) is the smallest k with 2^k ≥ x, and 0 for x ≤ 1.
- A is the block alignment in bytes, R the sample rate in Hz, S the width of the spillover field.
- Values, field and node names are those of the golden reports where the reader reports them ("Spillover Bits", "Superframes in Packet").

## Terms

- **Media object:** the unit the ASF container carries for the stream, after its payloads are put together and, where the stream uses audio spread error correction with a span above 1, descrambled ([asf-wma.md](../formats/asf-wma.md#asf-damaged-and-large-files)). FFmpeg's demuxer hands each one to the decoder as one packet of its own.
- **Packet:** a unit of A bytes inside a media object. A media object holds one or more; the low bitrate files hold two. A packet normally starts with a header. The bytes of a media object before its whole packets, or a media object shorter than A, are a **headerless packet** (see [Media objects into packets](#media-objects-into-packets)).
- **Superframe:** 480 samples (fewer where it states a sample count) in three frames. It is what the decoder outputs at a time. Superframes follow one another bit by bit; the last one that begins in a packet continues into the next.
- **Frame:** 160 samples. Its type (one of 17) sets how many blocks it has and how its excitation is coded.
- **Block:** an equal part of a frame (1, 2, 4 or 8 per frame) with its own excitation fields. Not to be confused with the packet, which asf-wma.md also calls a block of the block alignment's size.
- **Spillover:** the bits after a packet's header that finish the superframe the packet before ended in.
- **Tail:** the bits from where the last superframe begins to the end of the packet.
- **Cache:** the 256-byte buffer in which FFmpeg's decoder keeps a tail until the next packet's spillover finishes it. Its contents matter: where a copy into it is refused, the old bytes are read.
- **LSPs:** line spectral pairs, 10 or 16 per frame, the coded filter. They are sent per frame (independent) or once per superframe for its three frames (residual), as each packet's header says.
- **Adaptive codebook:** none, a pitch per frame (FFmpeg: asymmetric window), or a pitch per block (FFmpeg: Hamming window). **Fixed codebook:** silence (comfort noise), hardcoded codebook, pitch-adaptive window pulses, or pulse sets. These are the names the reader uses in "Frame" values and statistics.
- **Pitch:** a delay in samples. The pitch state carries from frame to frame and decides the width of two fields of a pitch-adaptive window frame (see [Pitch state](#pitch-state)).

## Codec data

### WAVEFORMATEX

The ASF Stream Properties Object holds a WAVEFORMATEX with format tag 0x000A, which FFmpeg maps to this decoder and the host routes as "wma-voice".

| Field | Use |
| --- | --- |
| Channels | not used by the decoder, which always outputs mono; other than 1 is an info finding (`wma.voice-channels`) |
| Samples per second | R; it decides the pitch fields' widths. any value of 0 or less is rejected by FFmpeg's ASF reader before the decoder ("Invalid sample rate: 0"), so a stored rate of 2^31 or more, which reads as negative, is rejected like 0 |
| Average bytes per second | the nominal bitrate only |
| Block alignment | A, the packet size in bytes; the decoder accepts 1 to 2^22, and the field is 16 bits wide |
| Bits per sample | not used |
| Codec specific data | the 46 bytes below |

### The 46 bytes

| Bytes | Field | Use |
| --- | --- | --- |
| 0–17 | laid out as WMA Professional's codec data | not read. FFmpeg's comment says they are for Professional data inside Voice superframes, which it does not decode. Node "Professional Header" |
| 18–21 | flags, little-endian | below. FFmpeg's comment says bytes 19 to 22; its code reads 18 to 21, and the files agree |
| 22–45 | frame type table | 17 fields of 3 bits from the first bit of byte 22, one per frame type in type order (51 bits); the other 141 bits are not read |

**Flags** (bit 0 is the least significant bit of byte 18):

| Bits | Field | Node | Use |
| --- | --- | --- | --- |
| 0 | postfilter | "Uses Postfilter" | the decoder's postfilter is on; audio only |
| 1 | not read | "Unread Flag" | |
| 2–5 | denoise strength | "Denoise Strength" | 0 to 11; 12 to 15 are rejected; audio only |
| 6 | denoise tilt correction | "Uses Denoise Tilt Correction" | audio only |
| 7–10 | DC level | "DC Level" | audio only |
| 11 | not read | "Unread Flag" | |
| 12 | 16 LSPs | "LSP Count" (value 10 or 16) | sets the LSP widths below |
| 13 | LSP quantizer mode | "LSP Quantizer Mode" | values only |
| 14 | LSP default mode | "LSP Default Mode" | values only |
| 15–31 | not read | "Other Flags" (hex, the flags shifted right by 15) | |

Only bit 12 changes where anything ends.

### Checks

FFmpeg's decoder makes these checks, in this order, and refuses to open on the first that fails (its messages are cross-check identifiers):

| Check | FFmpeg's message | Reader's finding |
| --- | --- | --- |
| codec data size other than 46 | "Invalid extradata size %d (should be 46)" | `wma.codec-data-size` |
| A not in 1 to 2^22 | "Invalid block alignment %d." | `wma.voice-block-alignment` |
| denoise strength 12 or more | "Invalid denoise filter strength %d (max=11)" | `wma.voice-denoise-strength` |
| a fifth frame type in one group of the table (see [Frame type codes](#frame-type-codes)) | "Invalid VBM tree; broken extradata?" | `wma.voice-frame-type-table` |
| R ≥ 226,719 (a guard against overflow) | none | `wma.voice-sample-rate` |
| pitch range D ≤ 0 (R up to 43) | "Invalid pitch range; broken extradata?" | `wma.voice-sample-rate` |
| Pmin below 1, or Pmax + 8 above 416 (R from 44 to 321, or above 22,097) | "Unsupported samplerate %d (min=%d, max=%d)" | `wma.voice-sample-rate` |
| half step H ≤ 0 (R from 322 to 7,989) | "Invalid delta pitch hrange; broken extradata?" | `wma.voice-sample-rate` |

- The container's reader rejects a rate of 0 or less before these checks, so a stored rate of 2^31 or more (read as signed) is rejected as 0 is.
- So the accepted sample rates are exactly 7,990 to 22,097 Hz. FFmpeg's message says 322 to 22,097, the bounds of the check that prints it. The 416 is the decoder's signal history in samples.
- FFmpeg checks nothing else: not the channels, not the bitrate, not the unread bits.
- The reader reports the first failing check as a warning, adds `wma.voice-not-read`, and describes the stream by the container's figures only (the packets are counted, no superframe is read). It does the same, without a check finding and without `wma.voice-not-read`, when the samples are encrypted (the container's `asf.codec-not-analyzed` says so); the codec data nodes are still reported.

### Derived values

With the integer divisions rounded down:

- **Spillover width** S = 3 + ceil(log2 A) bits (11 for A = 150 and 187, 12 for 300, 13 for 600, 4 for A = 2).
- **Shortest pitch** Pmin = floor((floor(256 · R / 400) + 50) / 256).
- **Pitch bound** Pmax = floor((floor(256 · 37 · R / 2000) + 50) / 256); the longest pitch is Pmax − 1.
- **Pitch range** D = Pmax − Pmin; **frame pitch width** P = ceil(log2 D).
- **Anchors** of the block pitch scale: a0 = Pmin, a1 = floor(25 · D / 64), a2 = floor(44 · D / 64), a3 = Pmax − 1. (a1 and a2 are absolute pitches, not offsets from Pmin.)
- **Half step** H = 16 · floor(floor(D / 8) / 16); **delta width** DP = 1 + ceil(log2 H).
- **Block pitch range** Rb = a2 + a3 + 1 + 2 · (a1 − 2 · Pmin); **block pitch width** BP = ceil(log2 Rb).
- **LSP widths:** independent LSPs 24 bits (10 LSPs) or 34 (16 LSPs) per frame; residual LSPs 48 or 60 bits per superframe.

| R | Pmin | Pmax | D | P | a1 | a2 | a3 | H | DP | Rb | BP |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 7,990 and 8,000 | 20 | 148 | 128 | 7 | 50 | 88 | 147 | 16 | 5 | 256 | 8 |
| 11,025 | 27 | 204 | 177 | 8 | 69 | 121 | 203 | 16 | 5 | 355 | 9 |
| 16,000 | 40 | 296 | 256 | 8 | 100 | 176 | 295 | 32 | 6 | 512 | 9 |
| 22,050 and 22,097 | 55 | 408 | 353 | 9 | 137 | 242 | 407 | 32 | 6 | 704 | 10 |

The statistic "Pitch Range" is "Pmin to Pmax − 1 samples" (for example "20 to 147 samples").

## Frame type codes

**The code.** A frame's type is sent as a code of 2 to 14 bits, read in 2-bit units:

- A unit of value 3 (binary 11) means the code goes on; the first unit of value 0, 1 or 2 ends it, and the code's **slot** is 3 × (the number of 11 units before it) + that value.
- After six 11 units, the seventh unit ends the code whatever its value, and the slot is 18 + its value.
- So slots 0 to 2 have 2-bit codes, 3 to 5 4-bit codes, and so on to 15 to 17 with 12 bits; slots 18 to 21 have 14-bit codes. These are the 22 lengths of `FrameTypeCodeLengths` in `VoiceTables.cs`, whose codes follow from the lengths canonically (each code is the one before plus one, at its length). The code is complete: every bit string starts with a code.

**The table.** The 3-bit field of frame type t is its group g (0 to 7). Types are placed in type order: type t takes slot 3 · g + (the number of earlier types with the same group). A later type that takes a slot an earlier one already holds replaces it.

- A group that would get a fifth type is rejected ("Invalid VBM tree"). Four types in a group are accepted, though FFmpeg's comment says a group should hold three: the fourth takes the first slot of the next group, which a type of that group then takes over if there is one (`voice-config-overlap`).
- Group 6 holds slots 18 to 20 and, with a fourth type, 21. Group 7 starts at slot 21; its second to fourth types would take slots 22 to 24, which no code reaches.
- A type that ends up in no slot from 0 to 21 cannot be sent. A slot that holds no type is a code with no type: a frame with it fails ("Invalid frame type VLC code, skipping"), and so does its superframe.
- The node "Frame Type t" of the table has the code length in bits of the type's slot, or "no code". Info findings: `wma.voice-frame-type-overlap` names the first group other than 7 that gets a fourth type; `wma.voice-frame-type-unreachable` lists the types with no code.

## Packets

### Media objects into packets

FFmpeg's decoder takes the bytes it is handed and works on at most A of them at a time; it decides whether they start with a header from how many are left. The effect, for a media object of n bytes:

- If n is a multiple of A, it is n / A packets, each with a header.
- Otherwise the first n mod A bytes are a headerless packet, and the rest are whole packets with headers. Packets are cut from the end, so a media object 10 bytes longer than two packets is read as 10 bytes without a header and then two packets 10 bytes late (`voice-object-not-whole`). Findings: `wma.packet-short` (Warning) for a media object shorter than A, `wma.packet-long` (Info) for one longer and not a multiple.
- A packet that a reading went into (see [Reading past a packet](#reading-past-a-packet)) is not read with a header either.
- Every packet counts for the statistics "Packets" and "Packet Bytes", whether it is read, dropped or passed over.

### Header

| Bits | Field | Node | Use |
| --- | --- | --- | --- |
| 4 | sequence number | "Packet Sequence Number" | not read by FFmpeg |
| 1 | residual LSPs | "Has Residual LSPs" | the flag F used by the superframes read while this header is the last one read |
| 6, repeated | superframe count | "Superframes in Packet" (the sum) | the values are added up; a value of 63 means another 6-bit value follows |
| S | spillover V | "Spillover Bits" | bits after the header that finish the superframe the packet before ended in |

- The header is 5 + 6k + S bits, k being the number of 6-bit values.
- **Rejected header:** before each 6-bit value FFmpeg checks that at least 6 + S bits are left in the packet; if not, the header is rejected and the rest of the media object is dropped (FFmpeg prints nothing; the call fails). The residual flag was read before and stays in force. The superframe count of the header before, the cache and the bits to skip stay as they were (see [State](#state)). Finding `wma.voice-packet-header` (Warning); the packet's value is "invalid header".
- **Sequence numbers:** a header whose number is not the last header's plus 1 (modulo 16) is an info finding, `wma.voice-packet-sequence`; the first header is not checked, and a rejected header's number counts as the last one.
- **The count** N is the number of superframes that begin in the packet, the last of which continues into the next packet (see below). It does not say where they end; no superframe states its size.

### The walk

The reader keeps this state across packets and media objects (it is never reset):

- F, the residual flag of the last header read;
- M, the superframes left of the last header's count (it may become negative);
- the cache: 256 bytes, all zero at the start, whose bytes stay until written again; its put position W (bits written); and C, the size in bits of the superframe start it holds (0 for none);
- K, the bits to skip at the start of the next headerless packet;
- the pitch state.

Within one media object it also keeps whether an error dropped the rest of it, and the bits of the next packet that a reading went into.

**A packet with a header** (one that starts a run of whole packets, with no reading gone into it):

1. Read the header (above). On rejection, stop: the rest of the media object is dropped. Else set M to the count and K to 0.
2. If C > 0, the packet finishes the cached superframe:
   - Let G be V, but at most the bits from the header's end to the end of the media object (not of the packet).
   - The G bits after the header are copied into the cache at W only if they lie in the packet (G ≤ the bits left in it after the header) and fit (W + G ≤ 2,048); otherwise nothing is copied (`wma.voice-cache-overflow`, Info). If copied, W grows by G. Either way the bits of the last byte the cache was written to are set to 0 after W, and C grows by G.
   - Read a superframe from the first C bits of the cache, with F (see [Superframe](#superframe)), and set C to 0.
   - If it is read: go on at the header's end + G. If that is the packet's end or beyond, the packet is done (see [Reading past a packet](#reading-past-a-packet)).
   - If it fails, the media object is **not** dropped, and reading goes on at the header's end + G + 2k:
     - k is the bits left in the packet after the header, modulo 8, but at most G, where the spillover was copied; k is 0 where it was not.
     - FFmpeg's copy of the spillover had already moved its position by k bits (the bits that align the rest of the copy to a byte) when its resync adds G, so the position lies k bits later than the spillover's end. That is FFmpeg's behavior, and the walk keeps it (`voice-spillover`, `voice-invalid-superframes`).
     - The packet is **not** done after this failure, wherever the position lies, also at or beyond the packet's end (it is not a case of [Reading past a packet](#reading-past-a-packet), which only a superframe that was read starts). The packet's own superframes are looked for from that position as step 4 says: with M of 2 or more, the superframe has no bits to be read from, fails, and drops the rest of the media object; with M of 0 it sets M to −1; with M of 1 or less than 0 there is no tail, so nothing is cached and C stays 0. Nothing is carried to the next packet (K stays 0).
     - Black-box evidence: in a mutated copy of the 4 kbit/s speech file (media object 4 given a music tail and the largest spillover the field holds, so that the resync lands past the end of the packet while M is 2 or more), ffmpeg 6.1.1 outputs 43,680 samples, which this reading gives; reading the packet as done gives 45,600.
   - The residual flag of this superframe is therefore that of the packet it ends in. Where it differs from the header before, the reader adds `wma.voice-lsp-flag-changes` (Info).
3. Else, if V > 0, skip V bits: spillover with no start to finish. Finding `wma.voice-spillover-after-music` (Info) where the last superframe that failed in a packet since the header before was music, `wma.voice-spillover-orphan` (Warning) otherwise. Both add V to "Skipped Spillover Bits".
4. Read the packet's own superframes (below), from where step 2 or 3 left off.

**A headerless packet:** start at the bits a reading went into it, if any, else at K; set K to 0 and C to 0. Setting C to 0 drops a cached start: the next packet's spillover then has nothing to finish (`voice-object-short`, `voice-object-remainder-first`). Then read the packet's own superframes.

**The packet's own superframes**, from the current position, as long as the packet is not done:

- M is 0: M becomes −1 and the rest of the packet is not read.
- M is 2 or more: M is one less after the event, and a superframe is read from the packet's bits (only those, see [Superframe](#superframe)):
  - If it fails, the rest of the media object is dropped (FFmpeg's call returns an error and its decoding loop discards what is left of the packet it handed over, which is the media object).
  - If it is read and ends before the packet's end, the next one is looked for.
  - If it ends exactly at the packet's end, the packet is done, and the count's remaining superframes are not looked for in it (`voice-superframe-exact-fill`).
- M is 1 or negative: M is 0 after the event if it was 1 (a negative one stays as it is); the rest of the packet, L bits, is the tail: set C to L and W to 0, and copy the L bits into the cache from its start if L ≤ 2,048, else copy nothing (`wma.voice-cache-overflow`; the cache keeps its old bytes, and C still says L). A packet with no bits left caches nothing (C stays 0). The packet is done.

So a count N reads N − 1 superframes in the packet and caches the start of the Nth, even where the Nth ends inside the packet: it is read when the next header arrives, from the tail and the G spillover bits, and bits of the span it does not use are ignored ("Ignored Bits", `wma.voice-superframe-padding`). A count of 0 reads nothing; after it, a headerless packet's bits are cached as the start of a superframe (M negative, `voice-object-headerless-start`). Where the count says more superframes than the packet holds, the extra ones are read in a headerless packet that follows (`voice-object-superframe-in-lead`).

**At the end of the stream** (after the last media object), if C > 0, the bits of the last byte written are cleared after W, and a superframe is read from the first C bits of the cache alone, as FFmpeg does when it is drained. Bits of the tail it does not use count as "Bits After the Last Superframe"; one that needs more is `wma.voice-unfinished` (Info).

**Dropped packets:** after a rejected header or a failed superframe in a packet, the rest of the media object is not read at all: its packets have the value "not read", read no header, and change no state.

### Reading past a packet

Only the superframe finished by the spillover can end past its packet: G is limited by the media object, not the packet, and where it is longer than the packet the copy is refused, so the superframe is read from the cache's old bytes and may succeed.

- Let E be where it ended, counted from the start of its packet, and T the packet's length in bits.
- If E is in the first byte after the packet (E < T + 8), the next packet is read with its header from its first byte, and E modulo 8 becomes K. K is used only if that header is then rejected and a headerless packet follows (`voice-object-skip-kept`), as FFmpeg clears its bits to skip only after a header it accepts.
- Otherwise the next packet is not read from a header. If E − T is less than its length in bits, reading goes on in it at E − T, as in a headerless packet. Else it is passed over (value "not read"), and the same rule applies to the packet after it, with E − T in place of E and this packet's length in place of T.

### Example

The first packets of `external-windows-wmavoice-cbr-mono-8k-5.wma` (A = 187, so S = 11 and a header of 22 bits; R = 8,000, so P = 7, BP = 8, DP = 5; 10 LSPs; every header sets the residual flag; the table gives types 2, 3 and 4 2-bit codes, 0, 1 and 5 4-bit ones, 6, 7 and 11 6-bit ones, 12, 13 and 15 8-bit ones, 8, 9 and 16 10-bit ones, 10 and 14 12-bit ones):

| Packet | Count | Spillover | Superframes, bits in the packet (frame types) | Tail |
| --- | --- | --- | --- | --- |
| 0 | 4 | 0 | 22–694 (15, 7, 7), 694–1,046 (5, 2, 5), 1,046–1,454 (5, 5, 5) | 1,454–1,496, 42 bits |
| 1 | 4 | 310 | the cached one: 42 + 310 = 352 bits (2, 5, 5); then 332–684, 684–1,092, 1,092–1,444 | 1,444–1,496, 52 bits |
| 2 | 4 | 356 | the cached one: 52 + 356 = 408 bits (5, 5, 5); then three more | |

The first superframe: speech bit, sample count flag 0, 48 bits of residual LSPs (bits 22 to 72), a type 15 frame of 259 bits (an 8-bit code, an 8-bit block pitch, seven 5-bit deltas and eight blocks of 26 excitation bits besides the pitch fields: 8 + 8 + 35 + 208), two type 7 frames of 181 bits (a 6-bit code, a 7-bit pitch, four blocks of 42 bits), and the additional data flag 0: 2 + 48 + 259 + 181 + 181 + 1 = 672 bits. Type 2 frames are 63 bits (2 + 7 + 54), type 5 frames 119 (4 + 7 + 108).

## Superframe

| Field | Width | Present | Node |
| --- | --- | --- | --- |
| speech | 1 | always | "Is Speech" |
| sample count flag | 1 | speech is 1 | "Has Sample Count" |
| sample count | 12 | the flag is 1 | "Sample Count" |
| residual LSPs | 48 (10 LSPs) or 60 (16) | F is 1 | "LSPs" ("10 LSPs for the three frames") |
| three frames | see [Frame](#frame) | always | "Frame" |
| additional data flag | 1 | always | "Additional Data" (from the frames' end to the superframe's end) |
| additional data count | 4 | the flag is 1 | |
| additional data | 10 × (count + 1) | the flag is 1 | |

- **Speech 0** means music, WMA Professional data inside the superframe, which FFmpeg does not decode ("WMAPro-in-WMAVoice"): the superframe fails (`wma.voice-music`, Info).
- **Sample count:** the superframe outputs that many samples (0 to 480); without the flag, 480. A count above 480 fails ("Superframe encodes > 480 samples (%d), not allowed", `wma.voice-sample-count`, Warning). The count is not where the superframe ends: the three frames are always read.
- **Additional data:** FFmpeg skips it (its comment calls it statistics). The node's value is the bits after the count ("20 bits"), or "none".
- **End check:** a superframe whose reading goes past the bits it is read from fails (`wma.voice-superframe-overrun`, Warning; at the end of the stream `wma.voice-unfinished`). The bits are those of its packet from its start for one that lies in a packet, and the first C bits of the cache for one read from the cache. Past the end the reader reads zeros; FFmpeg reads whatever its buffer holds there (bytes of the next packet, or old cache bytes), but the result is a failure either way. The frames read past the end still update the [pitch state](#pitch-state) from those bits, so after an overrun the state can differ between FFmpeg and the reader; see [Not determined](#not-determined).
- **Which failure:** a superframe fails at the first of: speech 0, a sample count above 480, a frame type code with no type, or the end check. Where the reading had already passed the end when one of the first three showed, the reader reports the overrun. FFmpeg has the same outcome for each; only its message differs.
- The superframe's size is not stated anywhere; it is the bits the reading used. It is at most 1,247 bits (16 independent LSPs, a sample count, three type 16 frames with 14-bit codes at 22,097 Hz and the longest additional data), under the cache's 2,048.

## Frame

### Layout

| Field | Width | Present | Node |
| --- | --- | --- | --- |
| independent LSPs | 24 (10 LSPs) or 34 (16) | F is 0 | "LSPs" ("10 LSPs") |
| frame type code | 2 to 14 | always | "Frame Type" (the type) |
| frame pitch | P | pitch per frame (types 2 to 7) | "Pitch" (the pitch c below, in samples) |
| silence gain | 8 | silence (type 0) | "Silence Gain" (the index) |
| window index | 6, then 2 more where the 6 read 54 or more | pitch-adaptive window pulses (type 2) | "Window Index" (the extended index i below) |
| blocks | see below | always | "Blocks", with "Block 1" to "Block B" (each the block's width in bits) |

### Frame types

The descriptors are `FrameDescriptors` in `VoiceTables.cs`: blocks B, log2 B, adaptive codebook (0 none, 1 pitch per frame, 2 pitch per block), fixed codebook (0 silence, 1 hardcoded, 2 pitch-adaptive window pulses, 3 pulse sets) and the pulse vectors that hold a pair, d.

| Type | B | Adaptive | Fixed | d | Bits after the code (q = 5 − log2 B) |
| --- | --- | --- | --- | --- | --- |
| 0 | 1 | none | silence | | 8 |
| 1 | 2 | none | hardcoded | | 28 |
| 2 | 2 | per frame | window pulses | | P + 54 |
| 3 | 2 | per frame | pulse sets | 2 | P + 80 |
| 4 | 2 | per frame | pulse sets | 5 | P + 104 |
| 5 | 4 | per frame | pulse sets | 0 | P + 108 |
| 6 | 4 | per frame | pulse sets | 2 | P + 132 |
| 7 | 4 | per frame | pulse sets | 5 | P + 168 |
| 8 | 2 | per block | pulse sets | 0 | BP + DP + 64 |
| 9 | 2 | per block | pulse sets | 2 | BP + DP + 80 |
| 10 | 2 | per block | pulse sets | 5 | BP + DP + 104 |
| 11 | 4 | per block | pulse sets | 0 | BP + 3 DP + 108 |
| 12 | 4 | per block | pulse sets | 2 | BP + 3 DP + 132 |
| 13 | 4 | per block | pulse sets | 5 | BP + 3 DP + 168 |
| 14 | 8 | per block | pulse sets | 0 | BP + 7 DP + 176 |
| 15 | 8 | per block | pulse sets | 2 | BP + 7 DP + 208 |
| 16 | 8 | per block | pulse sets | 5 | BP + 7 DP + 256 |

A frame's size depends only on the configuration, the type and whether it has its own LSPs. The value of the "Frame" node is "Type t: " and the type's name: the fixed codebook, the blocks, the adaptive codebook and, for pulse sets, the pulses ("pulse sets, 4 blocks, pitch per frame, 2 pulse pairs and 3 single pulses"; "5 single pulses" for d = 0, "5 pulse pairs" for d = 5).

### Blocks

Each block's fields, in this order:

- **Pitch per block** (types 8 to 16): the block pitch, BP bits in the first block and DP bits (a delta) in the others.
- **Hardcoded** (type 1): a start in the codebook, 8 bits, and a gain, 6 bits.
- **Silence** (type 0): nothing; the frame's silence gain applies.
- **Pulse sets** (types 3 to 16): five pulse vectors, each a sign (1 bit) and a position (q bits), and for the first d of them a second position (q bits); then a gain index of 7 bits. That is 12 + (5 + d) · q bits.
- **Pitch-adaptive window pulses** (type 2): a first pulse set of 12 bits (10 in the first block where the window index was extended), a pulse index of 5 bits in the first block and 3 in the second where the first block has pulses at the pitch lag, else 4 bits in each, a sign bit, and a gain index of 7 bits. Where FFmpeg finds no free position for the pulse, it conceals the block and skips the sign and the gain, so the width is the same.

The two type 2 blocks are always 54 bits together with the window index: the extended index's 2 extra bits are the 2 the first block's pulse set lacks, and the pulse indexes are 8 bits together either way. The pitch state decides only where the first block ends and the second begins ("Block 1" and "Block 2").

### Pitch state

Two values carry from frame to frame, across superframes, packets, media objects and failures, and are never reset: the last pitch L (40 at the start) and the last frame's adaptive codebook kind (none at the start). A frame updates them after it is read, also where a later frame of its superframe fails; a frame whose type code has no type changes nothing.

- **Pitch per frame** (types 2 to 7): c = Pmin + u(P), but at most Pmax − 1. If the last kind is none, or 20 · |c − L| > c + L, L becomes c first. Block n (counted from 0) has the pitch p_n = floor(((2n + 1) · c + (2B − 2n − 1) · L + B) / (2B)). After the frame, L = c. The node "Pitch" is c.
- **Pitch per block** (types 8 to 16): the first block's value v is its BP-bit field; each later block's is b − H + its DP-bit field, where b is the previous v limited to H at least and Rb − H at most. The block's pitch in samples follows from v on a scale in three parts, with s1 = 4 · (a1 − a0), s2 = 2 · (a2 − a1), s3 = a3 − a2 + 1:
  - v < s1: a0 + floor(v / 4);
  - else, with v' = v − s1 < s2: a1 + floor(v' / 2);
  - else, with v'' = v' − s2 < s3: a2 + v'';
  - else a3.
  After the frame, L is the last block's pitch (from v, not from the limited b).
- **No adaptive codebook** (types 0 and 1): after the frame, L = 0.
- The kind becomes the frame's own after each frame.

**Where the first block of a type 2 frame has pulses:** let i be the window index: the 6-bit value, or, where it is 54 or more, 54 + 4 · (value − 54) + the 2 further bits (so 0 to 93). Take o = `StartOffsets[i]` (`VoiceTables.cs`); while o is negative, add p_0. The first block has pulses at the pitch lag exactly when o is below 80 (half a frame). For indexes of 6 and up the offset is not negative and the pitch does not matter; for indexes 0 to 5 it does.

## Tables in VoiceTables.cs

The reader needs exactly the three tables of `VoiceTables.cs` (format data, generated by `wma_tables.py`):

- `FrameDescriptors`: per frame type, B, log2 B, the adaptive and fixed codebook kinds and d ([Frame types](#frame-types)).
- `FrameTypeCodeLengths`: the 22 code lengths in slot order ([Frame type codes](#frame-type-codes)).
- `StartOffsets`: the 94 offsets of the first window pulse by window index ([Pitch state](#pitch-state)).

No table of `wmavoice_data.h` is needed: they hold LSP, gain and codebook values.

## State

What carries, and what resets it:

| State | Set by | Reset by |
| --- | --- | --- |
| residual flag F | every header, also one rejected after its first 5 bits | never |
| superframes left M | an accepted header | never; a headerless packet and a rejected header leave it |
| cache bytes | copies | never (they stay until overwritten) |
| C (cached start) | a tail; the spillover adds to it | reading it (at a header or at the end); a headerless packet |
| W (put position) | a tail (from 0) and a copied spillover | the next tail |
| K (bits to skip) | a spillover superframe that ends in the byte after its packet | an accepted header; a headerless packet that uses it |
| pitch L and kind | every frame read | never |
| last sequence number | every header read | never (the first header is not checked) |

After a superframe that overran, FFmpeg resets values that only shape the audio (LSPs, filter memories) and the cache size and bits to skip, which are 0 at that point anyway; it does not touch the pitch state, so nothing that decides a width is reset.

## What the reader reports

### Nodes

The node names, values and units are those of the golden reports (`tests/fixtures/asf/voice-*.report.json` and the Windows files' reports).

- **Codec data** (under the container's node for it, also for a rejected configuration, as far as the bytes go): "Professional Header" (bytes 0–17, value 18 bytes; from 18 bytes), "Flags" (bytes 18–21, hex; from 22 bytes) with the bit fields of [the flags table](#the-46-bytes), numbered least significant first, "Frame Type Table" (bytes 22 to 45, or to the end of shorter data, value 17 frame types; from 29 bytes) with "Frame Type 0" to "Frame Type 16" (3 bits each: the code length in bits, or "no code") and "Unused" (the bits after the 51, as a count), and "Extra Data" for bytes past 46.
- **Stream** "Windows Media Audio Voice" (value: the superframes read), with "Statistics", "Packets" and "Superframes".
- **Packet** (one per packet of [Media objects into packets](#media-objects-into-packets)): value "N superframes" (those that begin in it: the ones read in it and the one its tail begins), "no header, N superframes", "invalid header" or "not read". Its children tile its bits:
  - the header fields; for a headerless packet that starts at a position past 0, "Unread Bits" up to it;
  - where it finishes a cached superframe, "Superframe End" (the spillover bits the superframe used) and "Padding" (spillover bits it did not use), or "Unread Bits" where the spillover was not copied;
  - "Skipped Spillover" for spillover with no start to finish;
  - "Superframe Data" for each superframe read in the packet ("3 frames" or "invalid"), with "Unread Bits" before it where the late resync skipped bits;
  - for the tail, "Superframe Start" (the bits of it the superframe used, where that superframe was read or also took bits of the next packet), else "Unfinished Superframe", then "Padding" for tail bits it did not use; "Unread Bits" where the cache refused the tail;
  - "Unread Bits" for the rest: after a failure, after a count that is used up, or after a rejected header.
- **Superframe** (every superframe read, also one that failed): value "3 frames, 480 samples" or "invalid: music" ("more than 480 samples", "a frame type code with no type", "it needs more bits than it has"). Children:
  - the fields of [Superframe](#superframe), each "Frame" of the frames read whole, with "LSPs", "Frame Type", "Pitch", "Silence Gain", "Window Index" and "Blocks" (with "Block 1" to "Block B", each the block's bits, the block pitch field included) as the type has them;
  - then "Additional Data", or, for a failed superframe, "Unread Bits" from the end of its last whole frame to where the reading stopped;
  - a field the reading did not reach has no node;
  - a superframe that spans two packets lies in the bytes of both;
  - fields read from cache bytes that were not copied from the file have their value and no bytes, at the position of its last bit from the file.

### Node details

What the golden reports show beyond names and values:

- **Kinds:** the stream node "Windows Media Audio Voice" is `Stream`, "Statistics" is `Statistics`, "Packet" and "Superframe" are `Frame`; every other node, "Packets" and "Superframes" and all fields included, has the default kind `Detail`.
- **Offsets and lengths:** the stream node, "Statistics", its entries, "Packets" and "Superframes" cover the container's node for the stream (its offset and length). A "Packet" node starts at its first byte and ends at the last byte of its last range; it has `Ranges` when its bytes are not contiguous in the file (the media object was put together from payloads). A "Superframe" node covers the bytes of the bits it used, at most the bits it had (the smaller of the bits the reading used and the bits available); it has `Ranges` when it lies in two packets whose bytes are not adjacent.
- **Bit positions:** every field inside a packet or superframe that does not cover whole bytes carries its bit position (`Node.FromBits`), and the flag bits of the codec data are numbered least significant first (the other fields most significant first). A field whose bits lie in two ranges has `Ranges` and no bit position. A field that has a value and no bytes (read from the cache's old bytes) has length 0, kind `Detail` and no bit position, at the byte where the last used bit lies.
- **Packet children** tile the packet's bits in the order of the list above and never overlap. "Superframes in Packet" covers 6 × (the count ÷ 63 + 1) bits (a count of 63 takes two values); "Spillover Bits" has the stated value V even where the packet holds fewer bits; "Skipped Spillover" and "Padding" in a spillover have the bits the packet holds. A "Superframe Data" node is left out for a superframe none of whose bits lie in the packet. "Unread Bits" before a node fills a gap that the late resync left; every child is clipped to the packet's length, and the final "Unread Bits" is only added where bits are left.
- **Superframe children** for a failed superframe: "Is Speech" only, for music; up to "Has Sample Count" and "Sample Count" for a sample count above 480; else the fields up to the end of the last whole frame, then "Unread Bits" whose value is the bits the reading needed past that frame's end (not clipped to the bits it had; the node itself is clipped). A frame the reading started past the bits it had gets no node, except in a superframe that reads cache bytes.
- **Rejected table:** the "Frame Type t" nodes of a rejected frame type table still place all 17 types, from the table's 3-bit groups; the types that no slot from 0 to 21 holds (for `voice-config-table`, types 3 and 4) show "no code".
- **Frame children:** "LSPs" only where F is 0; "Frame Type" (value the type number, the code's bits); "Pitch" only for types 2 to 7, in samples (the node covers the P-bit field; its value is c); "Silence Gain" for type 0; "Window Index" for type 2 (6 bits, or 8); then "Blocks" (value "N blocks") with "Block 1" to "Block B", each of the width the [Blocks](#blocks) list and the [pitch state](#pitch-state) give (value "N bits"); a block is cut where the frame ends early.
- **Findings only for accepted configurations:** `wma.voice-frame-type-overlap` and `wma.voice-frame-type-unreachable` are added only where the configuration passed the [checks](#checks) (also where the samples are encrypted); `wma.voice-channels` and every finding of the packets and superframes only where it passed and the samples are not encrypted. A rejected configuration has its check's warning and `wma.voice-not-read` only.

### Statistics

The statistics count what the walk read; a superframe "read" is one that did not fail.

| Statistic | What it counts |
| --- | --- |
| Packets, Packet Bytes | every packet and its bytes, read or not |
| Superframes, Invalid Superframes | superframes read; those that failed (shown where any) |
| Frames | three per superframe read |
| Samples, Duration | the sample counts of the superframes read (480 where none is stated); duration = samples / R |
| Average Bitrate, Average Superframe Bitrate | packet bytes × 8 over the duration; the bits of the superframes read over the duration |
| Smallest Superframe, Largest Superframe (bytes, from bits / 8), Minimum and Maximum Superframe Bitrate | over the superframes read; the bitrate is bits × R / 480 |
| Superframes Spanning Packets | superframes read from the cache at a header (not the one read at the end) |
| Most Superframes in a Packet | per packet: the superframes read in it plus 1 where it finished a cached one (whether that one was read or not); 1 for a packet that the cached superframe used up |
| Superframes with a Sample Count, with Residual LSPs, with Additional Data | superframes read that have them (residual: read with F = 1) |
| Ignored Bits | bits of a cached superframe's span (its copied tail and copied spillover) that it did not use, where it used only bits from the file (`wma.voice-superframe-padding`) |
| Bits After the Last Superframe | the same for the superframe read at the end; the bits of a superframe that fails there as an overrun; and, where the last tail was refused by the cache, its length in place of either |
| Skipped Spillover Bits | the stated V of every packet whose spillover had no start to finish |
| Spillover Field, LSPs per Frame, Uses Postfilter, Pitch Range, Block Alignment, Nominal Bitrate, Frame Length (160) | from the configuration |
| Frame Types, Fixed Codebooks, Adaptive Codebooks, Blocks per Frame | the frames of the superframes read, by type and by what the type stands for |

The series are the bitrate of each superframe (bits × R / its samples; 0 for one that failed or has no samples), at its first byte, and the bitrate of each second of output.

### Statistic values and series

- **The node:** "Statistics" is added when the stream has at least one packet; its value is "N superframes, D.DDD s". Its entries come in this order, each only where its condition holds:
  - "Packets" (integer), "Superframes", "Invalid Superframes" (where any), "Frames", "Frame Length" (160 samples);
  - "Samples" (unit samples), "Duration" (decimal, 3 places, s), "Container Duration" (where the container states one above 0; decimal, 3 places, s);
  - "Block Alignment" (bytes), "Packet Bytes" (bytes);
  - "Average Bitrate" and "Average Superframe Bitrate" (where the duration is above 0; decimal, 1 place, kbps);
  - "Nominal Bitrate" (where above 0: the container's average bytes per second × 8 / 1,000; 1 place, kbps);
  - where superframes were read: "Smallest Superframe" and "Largest Superframe" (bytes, bits / 8, 1 place), "Minimum Superframe Bitrate" and "Maximum Superframe Bitrate" (bits × R / 480 / 1,000; 1 place, kbps);
  - "Superframes Spanning Packets", "Most Superframes in a Packet", "Spillover Field" (bits), "Superframes with a Sample Count", "LSPs per Frame", "Superframes with Residual LSPs" (a share), "Superframes with Additional Data", "Uses Postfilter" (boolean), "Pitch Range" (text);
  - "Ignored Bits", "Bits After the Last Superframe" and "Skipped Spillover Bits" (bits, each only above 0);
  - where frames were read, the groups "Frame Types" (value "N types"), "Fixed Codebooks" and "Adaptive Codebooks" (value "N kinds"), "Blocks per Frame" (value "N sizes").
- **Shares** read "N frames (P.P%)" or "N superframes (P.P%)": the percentage of the total with one place, rounded to even on ties, 0 for a total of 0. The children of "Frame Types" are "Type t (name)" for each type that occurs, where the name is the one the "Frame" node uses without "Type t: "; those of "Fixed Codebooks" and "Adaptive Codebooks" the kinds that occur, with the first letter capitalized; those of "Blocks per Frame" "1 block" or "N blocks".
- **Counts** are written with their noun, "1 packet", "2 packets", and the group nodes "Packets" and "Superframes" hold one child per packet and superframe, built when read.
- **Series:** "Bitrate" (kbps, on the axis of file offsets: x is the first byte of each superframe, y the bitrate above, in kbps, not rounded) and "Bitrate Over Time" (kbps, on the seconds axis: x is a second's number from 0, y its superframes' bits × R / their samples / 1,000, to one place, rounded to even; a superframe belongs to the second its first sample falls in; the series points to the "Statistics" node). Their descriptions are "The bitrate of each superframe: its bits over the time its samples last." and "The bitrate in each second of audio: the bits of the superframes that start in it over their duration." A series exists only where there are superframes and R is above 0.
- **Stream information:** codec "Windows Media Audio Voice"; the container's sample rate and channel count; no channel mode; layout mono or stereo for one or two channels, else none; samples and duration from the walk; average bitrate packet bytes × 8 / duration / 1,000 (none where the duration is 0); the VBR flag from the container; encoder and writer as for the other Windows Media codecs (`WmaEncoder`).

### Findings

| Code | Severity | When |
| --- | --- | --- |
| `wma.codec-data-size`, `wma.voice-block-alignment`, `wma.voice-denoise-strength`, `wma.voice-frame-type-table`, `wma.voice-sample-rate` | Warning | a failed check of [Checks](#checks), with `wma.voice-not-read` |
| `wma.voice-not-read` | Info | a failed check; points to the stream node |
| `wma.voice-channels` | Info | the container states other than one channel |
| `wma.voice-frame-type-overlap`, `wma.voice-frame-type-unreachable` | Info | a fourth type in a group; types with no code |
| `wma.packet-short`, `wma.packet-long` | Warning, Info | a media object shorter than A; one longer and not a multiple |
| `wma.voice-packet-sequence` | Info | a sequence number that does not follow |
| `wma.voice-packet-header` | Warning | a rejected header |
| `wma.voice-lsp-flag-changes` | Info | a cached superframe read with a residual flag other than the header before's |
| `wma.voice-spillover-orphan`, `wma.voice-spillover-after-music` | Warning, Info | spillover with no start to finish |
| `wma.voice-cache-overflow` | Info | a tail or a spillover that the cache refuses |
| `wma.voice-superframe-padding` | Info | a cached superframe that leaves bits of its span |
| `wma.voice-music`, `wma.voice-sample-count`, `wma.voice-frame-type`, `wma.voice-superframe-overrun`, `wma.voice-unfinished` | Info, Warning, Warning, Warning, Info | a superframe that fails, by cause |
| `wma.records-limit` | Info | more than 262,144 media objects, 262,144 packets or 524,288 superframes; the rest is not read |
| `wma.payload-not-analyzed`, `wma.no-format` | Info | the stream was handed over as one payload and not as media objects; the container states no WAVEFORMATEX |

Findings about packets point to the packet node, about superframes to the superframe node, about the failed checks and the channels to the container's node that holds the codec data, about the table's overlap and unreachable types to the "Frame Type Table" node, `wma.voice-not-read` to the stream node, and the others to the parent of the stream node. The container's own findings about encrypted samples (`asf.content-encrypted`, `asf.codec-not-analyzed`) are not Voice's.

### Finding messages

The texts are Encspect's own prose, so they are quoted here; `{...}` is a figure. Whoever may not read the old reader needs them for goldens that stay as they are.

- **Collection:** a finding of a packet or superframe is reported once per stream for its code, at the first case, with the number of cases as the finding's count; the message has the singular form for one case and the plural form for more, with the first case's figures ("the first has 40"). The checks, the channels, the table findings, the limits and `wma.voice-not-read` are reported at once, one each, and end with a period.
- **Function names:** three of the current texts (`wma.voice-frame-type-table`, `wma.voice-frame-type-overlap` and `wma.voice-cache-overflow`) end their parenthetical with the name of an FFmpeg function after `wmavoice.c:`. The implementer words them with the file name only, as `(wmavoice.c)`, and updates the messages of the affected goldens and only those: `voice-config-table`, `voice-config-overlap`, `voice-cache-overflow`, `voice-object-skip-kept` and `external-windows-wmavoice-cbr-mono-8k-8-speech`. The texts below are already in that form. The other `(wmavoice.c: …)` texts quote ffmpeg's own messages, as the [checks](#checks) do, and stay.
- **Counts of things:** "{n} bytes" and "{n} samples" follow the singular rule ("1 byte").

Checks and configuration (Warning unless stated; the node is the container's node that holds the codec data unless stated):

| Code | Message |
| --- | --- |
| `wma.codec-data-size` | The codec-specific data of Windows Media Audio Voice is {n bytes}; FFmpeg's decoder needs exactly 46 and refuses any other size (wmavoice.c). |
| `wma.voice-block-alignment` | The block alignment is {A}; FFmpeg's decoder accepts 1 to 4,194,304 (wmavoice.c). |
| `wma.voice-denoise-strength` | The flags set a denoise strength of {d}; FFmpeg's decoder accepts 0 to 11 (wmavoice.c). |
| `wma.voice-frame-type-table` | The frame type table puts more than four frame types in one group of codes (type {t} in group {g}); FFmpeg's decoder rejects it (wmavoice.c). {t} is the type that would be the fifth of its group. |
| `wma.voice-sample-rate` (R ≥ 226,719) | The sample rate is {R} Hz; FFmpeg's decoder rejects rates of 226719 Hz and more (wmavoice.c). |
| `wma.voice-sample-rate` (D ≤ 0) | The sample rate of {R} Hz gives a pitch range of {D samples}; FFmpeg's decoder rejects it (wmavoice.c: Invalid pitch range). |
| `wma.voice-sample-rate` (Pmin or Pmax) | The sample rate of {R} Hz is below 322 Hz or above 22,097 Hz, which FFmpeg's decoder rejects (wmavoice.c: Unsupported samplerate). |
| `wma.voice-sample-rate` (H ≤ 0) | The sample rate of {R} Hz gives a delta pitch range of {H}; FFmpeg's decoder rejects it (wmavoice.c: Invalid delta pitch hrange), and so accepts only 7,990 to 22,097 Hz. |
| `wma.voice-not-read` (Info; the stream node) | The frames of Windows Media Audio Voice are not read: FFmpeg's decoder rejects the stream's configuration, so the stream is described by what the container and the codec-specific data state, and its packets are counted. |
| `wma.voice-channels` (Info) | The container states {n channels}; FFmpeg's decoder outputs mono whatever the container says (wmavoice.c), and a superframe holds one channel. |
| `wma.voice-frame-type-overlap` (Info; "Frame Type Table") | The frame type table puts four frame types in group {g}; the fourth takes the first slot of the next group, which another type's entry may then overwrite. The comment of FFmpeg's decoder says a group should hold three (wmavoice.c). |
| `wma.voice-frame-type-unreachable` (Info; "Frame Type Table") | The frame type table places {type or types} {t1, t2, …} in no slot that a frame type code can reach (codes 0 to 21), so a stream cannot use {it or them}. |
| `wma.records-limit` (Info; the parent) | The stream has more than 262144 media objects; the {n} after them are not read, so the statistics and the series cover the first 262144. |
| `wma.records-limit` (Info; the parent) | The stream has more than {262144 or 524288} {packets or superframes}; the rest are not read, so the statistics and the series cover the first {262144 or 524288}. |
| `wma.payload-not-analyzed` (Info; the parent) | Windows Media Audio handed over as one payload is not analyzed; only the media objects of a container are. |
| `wma.no-format` (Info; the parent) | The container states no WAVEFORMATEX for the stream, so its Windows Media Audio is not analyzed. |

Packets and superframes (one case / n cases; the node is the packet or superframe of the first case; for `wma.packet-short` and `wma.packet-long` the first packet of the media object):

| Code (severity) | One case | n cases |
| --- | --- | --- |
| `wma.packet-short` (Warning) | A media object holds fewer bytes than the block alignment of {A}; a decoder takes it for the rest of a packet and reads no header in it: it drops the superframe that goes on from the packet before, and takes a superframe or the start of one from the bytes only where the packet before counted more superframes than it held, or none (wmavoice.c). | {n} media objects hold fewer bytes than the block alignment of {A}; a decoder takes them for the rest of a packet and reads no header in them: it drops the superframe that goes on from the packet before, and takes a superframe or the start of one from the bytes only where the packet before counted more superframes than it held, or none (wmavoice.c). |
| `wma.packet-long` (Info) | A media object is not a whole number of blocks of {A} bytes ({lead} bytes before its whole blocks); a decoder cuts blocks from the end of a media object (wmavoice.c), so it takes those bytes for the rest of a packet with no header, as for a media object shorter than a block. | {n} media objects are not a whole number of blocks of {A} bytes (the first has {lead} bytes before them); a decoder cuts blocks from the end of a media object (wmavoice.c), so it takes those bytes for the rest of a packet with no header, as for a media object shorter than a block. |
| `wma.voice-packet-sequence` (Info) | A packet's sequence number does not follow the packet before ({s} for {expected}); a packet may be missing. FFmpeg's decoder does not read the number. | {n} packets' sequence numbers do not follow the packet before (the first is {s} for {expected}); a packet may be missing. FFmpeg's decoder does not read the number. |
| `wma.voice-packet-header` (Warning) | A packet's header runs past the packet; a decoder rejects it and drops the rest of the media object. | {n} packets' headers run past the packet; a decoder rejects them and drops the rest of the media object. |
| `wma.voice-lsp-flag-changes` (Info) | A packet sets the flag for residual LSPs differently from the packet before, and the superframe that goes on into it is read with the later flag, as FFmpeg's decoder reads it. | {n} packets set the flag for residual LSPs differently from the packet before, and the superframe that goes on into them is read with the later flag, as FFmpeg's decoder reads it. |
| `wma.voice-spillover-after-music` (Info) | A packet states spillover bits ({V}) of a superframe that began in the packet before, and that packet's superframes were lost to a superframe of music, which a decoder cannot read: it drops the rest of the media object, the start of this superframe with it, and skips the bits. | {n} packets state spillover bits (the first has {V}) of a superframe that began in the packet before, and that packet's superframes were lost to a superframe of music, which a decoder cannot read: it drops the rest of the media object, the start of this superframe with it, and skips the bits. |
| `wma.voice-spillover-orphan` (Warning) | A packet states spillover bits ({V}) of a superframe that began in the packet before, and no start is left from it (the first packet, a packet that could not be read, a media object with no header, or a superframe that began exactly at its end); a decoder skips them. | {n} packets state spillover bits (the first has {V}) of a superframe that began in the packet before, and no start is left from it (the first packet, a packet that could not be read, a media object with no header, or a superframe that began exactly at its end); a decoder skips them. |
| `wma.voice-cache-overflow` (Info) | A packet starts or finishes a superframe that does not fit the 256 bytes a decoder keeps of one that goes on in the next packet, or whose spillover bits are not in the packet; FFmpeg's decoder copies none of the bits that do not fit (wmavoice.c), so it reads the cache's old bytes in their place. | {n} packets start or finish a superframe that does not fit the 256 bytes a decoder keeps of one that goes on in the next packet, or whose spillover bits are not in the packet; FFmpeg's decoder copies none of the bits that do not fit (wmavoice.c), so it reads the cache's old bytes in their place. |
| `wma.voice-superframe-padding` (Info) | A superframe ends before the bits the packets give it ({L} bits); a decoder ignores them and goes on at the position the next packet's header states. | {n} superframes end before the bits the packets give them (the first leaves {L} bits); a decoder ignores them and goes on at the position the next packet's header states. |
| `wma.voice-music` (Info) | A superframe's first bit is clear: it holds music, Windows Media Audio Professional data inside Voice superframes, which FFmpeg's decoder does not support (wmavoice.c) and this analysis does not read; a decoder drops {X}. | {n} superframes' first bit is clear: they hold music, Windows Media Audio Professional data inside Voice superframes, which FFmpeg's decoder does not support (wmavoice.c) and this analysis does not read; a decoder drops {X}. |
| `wma.voice-sample-count` (Warning) | A superframe states more samples than the 480 it holds; a decoder drops {X}. | {n} superframes state more samples than the 480 it holds; a decoder drops {X}. |
| `wma.voice-frame-type` (Warning) | A frame's type code is one that the frame type table gives no type, so the superframe fails; a decoder drops {X}. | {n} frames' type codes are one that the frame type table gives no type, so the superframes fail; a decoder drops {X}. |
| `wma.voice-superframe-overrun` (Warning) | A superframe needs more bits than the packets give it ({needed} for {available}); a decoder drops {X}. | {n} superframes need more bits than the packets give them (the first needs {needed} for {available}); a decoder drops {X}. |
| `wma.voice-unfinished` (Info) | The last packet ends with {available bits} that begin a superframe needing {needed} or more, and no packet follows to finish it; a decoder drops them. | (always one case) |

- **{X}** is "it" for one case, followed by nothing where the superframe is the one read at the end of the stream, by " and goes on with the packet's own superframes" where it was finished from the cache, else by " and the rest of its media object" (judged by the first case); for n cases it is "each, and with one that lies in a packet the rest of its media object".
- **Forms:** the one-case text is used when the count is 1, the n-case text otherwise; {n} is written as a plain integer.

## Limits

| Limit | Value |
| --- | --- |
| Sample rate | 7,990 to 22,097 Hz (FFmpeg's checks) |
| Block alignment | 1 to 2^22 bytes in FFmpeg; 65,535 at most from the 16-bit field |
| Spillover width S | 3 to 19 bits (up to 25 by FFmpeg's limit on A) |
| Superframe count per packet | any; 6-bit values, 63 meaning another follows |
| Samples per superframe | 0 to 480 |
| Superframe size | at least 81 bits (10 LSPs sent once, three silence frames with 2-bit codes), at most 1,247 |
| Cache | 2,048 bits; a tail or tail plus spillover above it is not copied |
| Frame types | 17; codes of 2 to 14 bits in 22 slots |
| Window index | 0 to 93 |

## Validity rules and damage

| Rule broken | FFmpeg | Reader |
| --- | --- | --- |
| A check of [Checks](#checks) | the decoder does not open, nothing is decoded | header only, the check's warning and `wma.voice-not-read` |
| Header runs out of bits | the call fails, the rest of the media object is dropped | the same, `wma.voice-packet-header` |
| Spillover with no start before it | skipped | skipped, a finding |
| Superframe in a packet fails | the rest of the media object is dropped | the same |
| Superframe finished by the spillover fails | resync 2k bits late, the packet's own superframes are read | the same |
| Tail or spillover too long for the cache | the old cache bytes are read in place of the refused bits | the same, `wma.voice-cache-overflow` |
| Media object not a whole number of packets | the bytes before the whole packets are read as the rest of a packet; a cached start is dropped | the same, `wma.packet-short` or `wma.packet-long` |
| Count larger than the packet's superframes | the extra ones are read from a following headerless packet, if any, else lost | the same |
| Sequence number gap | not looked at | info finding |
| Channels other than 1 | output is mono | info finding |
| Bits after the last superframe | ignored | counted |

## FFmpeg's implementation choices

These are choices of FFmpeg's decoder, not rules of a published format; the reader follows them so that its sample counts equal ffmpeg's.

- **Late reading of the last superframe of a packet:** it is read when the next header arrives, from the cache, with that header's residual flag, and at the end of the stream from the tail alone (the decoder declares a delay and is drained).
- **The late resync** after a failed spillover superframe ([The walk](#the-walk), step 2).
- **Spillover limited by the media object**, not the packet; a spillover past the packet is not copied, and the superframe is read from old cache bytes.
- **The cache's old bytes:** a refused copy leaves the bytes of an earlier superframe in the cache, and the decoder reads them. Two real files have it: the last superframe of `streaming_CBR-19K-timecomp.wma` and of `external-windows-wmavoice-cbr-mono-8k-8-speech.wma` starts in a tail longer than the cache and is read at the end from such bytes; FFmpeg and the reader both decode it as a superframe of 480 samples.
- **A build-dependent detail the reader does not copy:** FFmpeg's bit writer keeps up to 63 bits (64-bit builds on x86-64; 31 elsewhere) in a register until it writes them out at the next spillover. Where a tail is copied and then dropped by a headerless packet, and a later copy is refused, FFmpeg's cache lacks those bits; the reader assumes every copied bit was stored.
- **No reset after damage** of anything that decides a width; the pitch state carries on.
- **Ignored fields:** the sequence number, codec data bytes 0 to 17, flag bits 1, 11 and 15 to 31, the table's last 141 bits, and the additional data.

## Writing valid streams

What a writer of fixtures needs, so that FFmpeg decodes every superframe and the stated sample count.

**Codec data:** bytes 0 to 17 as the writer likes (the fixtures write a sample size of 16 and a channel mask of 4, front center, then zeros), the flags with a denoise strength of 11 or less, and a table with at most four types per group. A writer that wants every type reachable gives each group at most three types, and group 7 at most one. The sample rate must be 7,990 to 22,097 Hz.

**What constrains what:**

- The configuration (R, A, bit 12 of the flags) fixes every width: S, P, BP, DP, the LSP widths.
- The table fixes each type's code; a frame may use only a type with a code.
- The packet's residual flag fixes whether LSPs come once per superframe or per frame. A superframe that lies in a packet is read with that packet's flag; one that a packet ends in is read with the flag of the next packet (`voice-lsp-flag-changes`); the last one of the stream with the last packet's flag.
- The pitch state decides only how the two blocks of a type 2 frame divide their 54 bits. FFmpeg reads the same bits for the frame whatever the state, so a writer that does not track it still writes a valid stream; a writer that wants the reader's "Block 1" and "Block 2" widths to match its own must track it as [Pitch state](#pitch-state) says.

**What may be random:** every LSP bit, the frame pitch (any P-bit value; values above the range are limited), the silence gain, the window index (any 6-bit value, and 2 more bits after 54 or more), the block pitches and deltas, every excitation bit, the additional data count and bits, and the sequence numbers. FFmpeg reads values, not just widths, from these, but none of them can make a superframe fail.

**What must hold for every superframe to decode:** speech bit 1; a sample count, if stated, of 480 or less; codes of types with a code; and the packet layout:

- Each packet's header states, as N, the superframes that begin in it, the last of which continues into the next packet (that one may also end in the packet, padded to its end; the padding is ignored). The superframes follow the header and the spillover with no gap.
- Each packet's spillover V is the number of bits of the continuing superframe that lie in this packet (0 where the superframe ended in the packet before), and V must fit in S bits.
- Where superframes fill a packet exactly to its last bit, the next superframe begins in the next packet (spillover 0), and the full packet's count may include the last superframe that fills it (it is then cached whole) or not.
- The tail, and the tail plus the spillover, must be at most 2,048 bits; superframes are at most 1,247 bits, so only padding can break this.
- The last superframe of the stream may end anywhere in the last packet; it is read at the end from the tail alone.
- The stream's samples are the sum of the superframes' samples: 480 each, or the stated count. FFmpeg outputs a superframe of 0 samples as a frame of 0 samples.

**The current fixture writer's conventions**, which a rewrite must keep for `make_voice_fixtures.py` to write the committed fixtures again byte for byte (CI regenerates them and fails on a difference):

- One random generator per stream, Python's `random.Random(seed)`. Each field drawn at random is one call of `getrandbits(width)`, written most significant bit first; a field of width 0 draws nothing.
- The draws, in order, for a superframe:
  - the residual LSPs as one draw of 48 or 60 bits where the superframe has them;
  - then per frame: its independent LSPs as one draw (24 or 34) where it has them; the frame pitch (P); the silence gain (8); the 6-bit window index, then 2 more where it is 54 or more;
  - then per block: the block pitch (BP, first block) or the delta (DP, the others); for a hardcoded block one draw of 14 bits; for a window pulse block the pulse set (12 or 10), the pulse index (5, 3 or 4) and the sign (1), each one draw; for a pulse set block one draw of 5 + (5 + d) · q bits; and the gain (7) for every block of a type with an adaptive codebook;
  - after the frames, where the superframe has additional data, one draw of 10 × groups bits.
- The speech bit, the sample count flag and count, the frame type codes, the additional data flag and count are written, not drawn.
- The pitch state starts at L = 40 with the kind none, and carries through the whole stream of one generator, as above.
- The stated sample count is written only where a superframe is given one; additional data is given as a number of 10-bit groups.
- `Config` records and packs; it checks nothing. The fixtures of rejected configurations build one with five types in a group or a denoise strength of 12 (a 4-bit field), cut or extend the bytes of `codec_data()`, and override the rate, block alignment or codec data in `wave_format`; none of that may raise. A rejected rate is only passed to `wave_format` (the `Config` itself is built at 8,000 Hz), so the pitch widths need to be right for accepted rates only. `wave_format` builds the stream's WAVEFORMATEX with `asf_model.wave_format` (format tag, channels, rate, average bytes per second = rate // 8, block alignment, 16 bits per sample, codec data).
- The attributes of `Config` that `make_voice_fixtures.py` reads are `align`, `rate`, `residual`, `residual_lsp_bits`, `spillover_bits`, `codec_data()` and `wave_format(...)`; the module also holds the constants `FRAME_SAMPLES` (160), `SUPERFRAME_SAMPLES` (480) and `DEFAULT_GROUPS`.
- The writer's module provides:
  - `Config(rate, align, groups=None, postfilter=True, denoise=5, tilt=False, dc_level=3, lsp16=False, q_mode=0, default_mode=0, residual=True, header=None, flags_override=None)` with the attributes `align`, `rate`, `residual`, `spillover_bits`, `residual_lsp_bits`, `codec_data()` and `wave_format(rate=None, align=None, extra=None, tag=0x000A, channels=1)` (which builds the WAVEFORMATEX with `asf_model.wave_format`, rate / 8 average bytes per second and 16 bits per sample);
  - the codec data: bytes 0 to 17 are exactly `10 00 04 00 00 00` (a sample size of 16, then a channel mask of 4) followed by 12 zero bytes, and the table is zero after its 51 bits (24 bytes in all);
  - the flags: bit 0 postfilter, denoise × 4, 0x40 tilt, DC level × 128, 0x1000 for 16 LSPs, 0x2000 and 0x4000 for the modes;
  - the groups default to `[1, 1, 0, 0, 0, 1, 2, 2, 4, 4, 5, 2, 3, 3, 5, 3, 4]`, the table of the 8 kHz Windows files;
  - `Writer(config, seed)` with `superframe(types, samples=None, additional=0, residual=None, speech=True)` returning the bits and a record (`types`, `frames`, `samples`, `bits`, `additional`; each frame `type`, `blocks`, `bits` with its own LSPs, `pitch` for a pitch per frame (c) and per block (the last block's), `parameter` for the silence gain, `window_index` for the extended index), and a writer that `copy.deepcopy` can copy;
  - `BitWriter` with `write(value, count)`, `random(rng, count)`, `append(other, start=0, end=None)`, `to_bytes(size=None)` and `len(writer)` (the bits written);
  - `set_bits(data, position, count, value)`;
  - the tables `FRAME_DESCRIPTORS`, `CODE_LENGTHS` and `START_OFFSETS`, which `wma_tables.py --check` compares with `VoiceTables.cs`.

## Observations on the test files

Observed in Encspect's test files only; none of this is a rule the reader applies.

- **Residual LSPs:** every packet of every Windows and sample-archive file sets the flag; the per-frame LSPs occur only in hand-built fixtures.
- **LSPs:** 10 in the 8 kHz files, 16 in the 16 kHz files.
- **Sample counts:** where a file states one, it is in its last superframe only (7 in the 7K and 11K files, 433 in the 19K files, 315 in `wmav_8.wma`, 267 in `wmv3-wmaspeeech.wmv`); the Windows files and the two cut files state none.
- **Additional data:** in no real file.
- **Superframe counts:** 0 to 24 per packet; the continuation value 63 in no real file. A count of 0 occurs in the last packet of `external-windows-wmavoice-cbr-mono-16k-16.wma` and of the 19K archive files other than `streaming_CBR-19K-timecomp.wma` (which has none), and in the hand-built `voice-8k-residual`; those packets hold nothing but the end of the superframe before.
- **Two packets per media object:** in the 4 kbit/s Windows file and the three 7K archive files (packets of 150 bytes).
- **Padding:** superframes finished by the spillover that leave bits unused, in the 16 kbit/s speech Windows file (2,289 bits) and the 19K archive files (161 and 370 bits).
- **Cache overflow:** the last tail of `external-windows-wmavoice-cbr-mono-8k-8-speech.wma` (2,266 bits) and of `streaming_CBR-19K-timecomp.wma` (2,396 bits) is longer than the cache.
- **Music:** `wmav_8.wma` has 24 superframes with the speech bit 0, and the packets after them state spillover with no start to finish (6,090 bits in all); its codec data bytes 0 to 17 are text ("indows Media C…").
- **Unread codec data bits:** after the 51 table bits, the Windows files, `wmv3-wmaspeeech.wmv` and `wmav_8.wma` have bits set; the other archive files have zeros. Byte 14 is 0x10 in every 16 kHz file, 0x08 in the 8 kHz Windows files and `wmv3-wmaspeeech.wmv`, and 0 in the other 8 kHz archive files.
- **Frame type tables:** five tables occur. The 8 kHz Windows files at 5 kbit/s and `wmv3-wmaspeeech.wmv` use the fixture writer's default groups; every 16 kHz file shares one table; the 4 kbit/s Windows file and the 7K files share one, the 8 kbit/s Windows file and the 11K files another, and `wmav_8.wma` has its own. Every one gives groups 0 to 4 three types each and group 5 two, so no real file uses a 14-bit code.
- **Sequence numbers:** follow without a break in every real file.

## Not determined

- The meaning of codec data bytes 0 to 17, flag bits 1, 11 and 15 to 31, and the table's last 141 bits; whether "laid out as Professional's" is right (only FFmpeg's comment says so).
- Music superframes (WMA Professional data inside Voice): their syntax, their size, and whether a packet's count includes them. FFmpeg does not decode them, and the reader drops the rest of the media object as FFmpeg does.
- What the additional data holds.
- Whether an encoder ever writes a count of 63 or more, independent LSPs, four types in a group, or a tail that the cache refuses in the middle of a stream.
- Whether Microsoft's decoder resyncs as FFmpeg does after a superframe the spillover finishes fails, or limits the spillover to the packet; the reader follows FFmpeg, whose behavior the cross-checks pin.
- The pitch state after a superframe that overran: its frames that begin past the end are read from bits that FFmpeg takes from its buffer (the next packet's bytes, old cache bytes, or a clamped read) and the reader takes as zeros, so the two can hold different pitches afterwards. The effect is limited to the "Block 1" and "Block 2" widths of a later type 2 frame in the report (the frame's total is the same), never the superframes or samples counted. The implementer reads zeros, as the current reader does; no fixture or checked file has a case where it matters.
- A media object of 0 bytes: none occurs in the test files, and what FFmpeg does with one is not determined (an empty packet is also how its decoder is told to drain); the reader skips it.

## Implementer's sources

Every Voice-related file of the repository, classified for the implementer of the new reader and fixture writer: **safe** (nothing in it derived from FFmpeg's code: specifications, reports, Encspect's own reporting and tests, prose) or **not safe** (it encodes the bitstream as FFmpeg's code does, and must not be read).

**Not to read, anywhere:**

- All FFmpeg source, in any form and any place: the pinned archive or any other unpacked copy (including `wma-audit-ffmpeg`), headers and tables, mirrors, copies in other repositories or on the web, and anything generated from it. This is wider than `wmavoice.c`: the Windows Media decoders (`wmaprodec.c`, `wmadec.c`, `wma.c`), `get_bits.h`, `decode.c` and `asfdec_f.c` carry the same behavior. FFmpeg serves as a black box only: run `ffmpeg`, `ffprobe` and `check_wmavoice_samples.py`, read their output and messages.
- The session scratch folders of the audit and of the descriptions: `wmadesc-*` (including the walkers and tools in `wmadesc-voice-review`), `wmadesc-*-review`, `audit-*` and `wt-audit`, and the review files `reviews/wma-*.md` and `reviews/license-audit-*.md`. They quote or follow FFmpeg's code.
- The throwaway walker named under [Sources](#sources), and the checkpoint notes of whoever wrote this text.
- The Windows Media Professional files (`Pro*.cs`): that reader is being replaced by its own clean-room rewrite, so its files are not read by the Voice implementer either; what Voice needs from them is specified under [Interfaces other code uses](#interfaces-other-code-uses).

**Not safe:**

- `scripts/fixtures/voice_model.py`: mirrors FFmpeg's initialization, table construction and frame reading field by field (the file the rewrite replaces).
- `src/Encspect.Formats.Wma/VoiceParameters.cs`, `VoiceDecoder.cs`, `VoiceStream.cs`: the configuration checks, the frame and superframe reading and the packet walk, written after FFmpeg's functions. Their finding messages are quoted under [Finding messages](#finding-messages); nothing else in them is needed.
- `src/Encspect.Formats.Wma/VoiceNodes.cs`: mostly the node layout of [Nodes](#nodes), but its block width computation and frame field order repeat the decoder's reading. The node rules it implements are under [Node details](#node-details).
- `src/Encspect.Formats.Wma/WmaVoiceParser.cs`: the codec data nodes and the configuration findings, built on `VoiceParameters`; the flag layout and table slots it encodes are described here, its messages are quoted.
- `src/Encspect.Formats.Wma/Pro*.cs`: see above.

**Safe:**

- `src/Encspect.Formats.Wma/VoiceTables.cs` (generated format data), `Bits.cs` and `Vlc.cs` (generic bit readers shared with the other WMA codecs; `Vlc.cs` is not used by Voice), `PartsMap.cs`, `Records.cs` (`FramePart`), `FindingCollector.cs`, `WmaHeaderOnly.cs`, `WmaEncoder.cs`, `Topics.cs`.
- `src/Encspect.Formats.Wma/VoiceRecords.cs` (the records and totals, with their meaning in comments), `VoiceReport.cs` (statistics and series from the totals) and `VoiceExplain.cs` (the node descriptions and the frame type names): Encspect's own reporting, no bitstream reading. A rewrite may keep them and fill the same records. Their comments and descriptions cite FFmpeg function and variable names (names only, no logic); the implementer ignores the names and drops them when moving a text, as the project's own wording is used for descriptions. `VoiceReport.cs` depends on three helpers in Pro files, specified below.
- `scripts/fixtures/asf_model.py`: the model of the ASF container (builders and a reader), written from the ASF specification, with no codec logic; `make_voice_fixtures.py` uses its `Payload`, `Layout`, `packet`, `asf_file`, `stream_properties`, `spread_audio`, `AUDIO_SPREAD`, `wave_format` and `parse`. `make_asf_fixtures.py` uses it too.
- `src/Encspect.Host/Topics/wma-voice.md`, `wma-voice-superframes.md`, `wma-voice-frames.md`: prose articles.
- The golden reports and manifests (`tests/fixtures/asf/*voice*.report.json`, `.expected.json`, `.provenance.json`) and `tests/conformance/wmavoice-samples.json`.
- `docs/formats/asf-wma.md`: its Voice section is prose about the format and FFmpeg's observable behavior, with the same rules as this document and no FFmpeg internals. `docs/PLAN.md` (item 5 of "Phase 2, continued") and `docs/development.md` ("WMA Voice") are prose in the same way.
- `tests/Encspect.Tests/VoiceBuilder.cs` (with its `VoiceBitWriter`): writes packets of silence frames with the header layout of [Header](#header); format-level, no FFmpeg logic. `VoiceFixtureTests.cs`, `WmaVoiceTests.cs`, and the Voice cases of `WmaFindingTests.cs`, `WmaCorruptionTests.cs`, `WmaFixtureTests.cs`, `MemoryTests.cs`, `ParallelAnalysisTests.cs` and `ProtectionTests.cs`: they check the reader's output against manifests, goldens and the node tiling; some comments name FFmpeg functions (names only).
- `scripts/fixtures/make_voice_fixtures.py`: the fixture plans, the packetizing (header, count and spillover as [Writing valid streams](#writing-valid-streams) states them) and the damage; it calls `voice_model.py` through the interface listed there. Its descriptions explain FFmpeg's behavior in prose.
- `scripts/fixtures/check_wmavoice_samples.py`, `crosscheck_asf_fixtures.py`: run ffprobe, ffmpeg and MediaInfo as black boxes and compare with manifests and goldens.
- `scripts/fixtures/wma_tables.py`: reads FFmpeg's source to extract the tables (as a generator, not a reading of the code's logic); the implementer may run it but needs only its output, `VoiceTables.cs`. `ffmpeg_sources.py` and `check_sources.py` only fetch and pin sources.
- `scripts/fixtures/make_asf_fixtures.py` (calls `Config(8000, 300).codec_data()` for `protected-wma-voice.asf`), `import_windows_wma_fixtures.py`, `windows/make_windows_wma_fixtures.ps1`, `scripts/ci/changes.py` and `test_changes.py`, `.github/workflows/ci.yml`: no bitstream logic.

**For identical golden reports:** the finding messages are quoted under [Finding messages](#finding-messages), in the form the code produces them, except that three of them lose an FFmpeg function name; the implementer updates the messages of the five goldens affected (listed there) and no others. The node and statistic rules are under [Node details](#node-details) and [Statistic values and series](#statistic-values-and-series).

## Interfaces other code uses

The new reader replaces `VoiceParameters`, `VoiceDecoder`, `VoiceStream` and `VoiceNodes`, and the Pro reader is replaced too; the code that stays calls the following, which the replacement keeps (names and signatures; all `internal` to the Wma project unless stated).

**`VoiceParameters`** (used by `VoiceReport`, `VoiceNodes`'s replacement, `WmaVoiceParser` and the tests):

- Constants: `ExtraDataSize` 46, `FlagsOffset` 18, `TableOffset` 22, `FrameTypes` 17, `FrameSamples` 160, `FramesPerSuperframe` 3, `SuperframeSamples` 480, `CacheBytes` 256. The tests read `FrameTypes` and `FrameSamples`.
- `static VoiceParameters Create(AudioFormat format, ReadOnlySpan<byte> data)`: never throws; a failed check gives an instance with `Issues` set and nothing derived. `static int[] Slots(ReadOnlySpan<byte> table)`: for each of the 17 types its slot (a code number from 0 to 21), or −1 where no code reaches it; it works on any table, also a rejected one (the codec data nodes show "no code" for a rejected configuration).
- Properties: `SampleRate`, `BlockAlign`, `Flags` (uint), `HasPostfilter`, `DenoiseStrength`, `HasDenoiseTiltCorrection`, `DcLevel`, `LspCount` (10 or 16), `LspQuantizerMode` (0 or 1), `LspDefaultMode` (0 or 1), `SpilloverBits`, `SlotOfType` (int[17]), `MinPitch`, `MaxPitch` (Pmax; the reports print Pmax − 1), `PitchBits`, `BlockPitchBits`, `BlockDeltaPitchBits`, `IndependentLspBits`, `ResidualLspBits`, `OverfullGroup` (the first group other than 7 that has a fourth type, else −1), `IsUsable`, `Issues` (a list of the finding's code and message; the message without its period).

**`VoiceNodes`** (constructed by `WmaVoiceParser` from the records): `VoiceNodes(VoiceRecords records)`, `Node PacketNode(int index)`, `Node SuperframeNode(int index)`, `long SuperframeOffset(int index)` (the file offset of the superframe's first byte, used for the series). The node rules are under [Node details](#node-details).

**`VoiceStream`** (constructed and driven by `WmaVoiceParser`): `VoiceStream(VoiceParameters parameters, FindingCollector findings, Func<int, Node> packetNode, Func<int, Node> superframeNode, CancellationToken cancellation = default)`; `Add(MappedBytes mediaObject)` once per media object, in order; `Finish()` after the last; properties `Parameters`, `Records`, `Packets`, `Superframes`, `Totals` (a `VoiceTotals`), `Truncated`, `Limit` ("packets" or "superframes") and `LimitCount` (262,144 or 524,288). `WmaVoiceParser` has the public constant `CodecId` ("wma-voice") and the internal `MaxPackets` (262,144 media objects).

**Tables and names** (`VoiceTables.cs`, `VoiceExplain.cs`): `VoiceTables.FrameDescriptors` (17 arrays of five bytes: blocks, log2 of blocks, adaptive codebook, fixed codebook, pulse pairs), `FrameTypeCodeLengths` (22), `StartOffsets` (94); `VoiceNames.FixedCodebooks`, `AdaptiveCodebooks` and `Frame(int type)` (the text after "Type t: "; the tests require it to be non-empty, and that every descriptor's block count divides 160). `VoiceReport.CodecName` is "Windows Media Audio Voice".

**Helpers in the Pro files** that `VoiceReport` uses, to be kept (or moved to a shared file) when the Pro reader is replaced; `WmaHeaderOnly` and the Standard and Lossless parsers use further constants of `ProExplain` for the header-only stream, the statistics and the packet statistics, which that rewrite keeps as well:

- `ProNodes.Count(long count, string singular, string plural)`: "{count} {noun}", the singular for exactly 1, the integer in invariant culture.
- `ProReport.Share(long count, long total, string singular, string plural)`: `Count` followed by " (P%)", P being count × 100 / total rounded to one place (ties to even, written with one decimal), 0 for a total of 0.
- `ProExplain` descriptions used by the Voice statistics: `StatPacketBytes` "The bytes of all the packets."; `StatBlockAlignment` "The size of each packet, the container's block alignment."; `StatAverageBitrate` "The packets' bytes over the duration."; `StatContainerDuration` "The length of the stream as the container states it, the play duration less the preroll."; `StatNominalBitrate` "The average bytes per second the container states for the stream (the WAVEFORMATEX's nAvgBytesPerSec), as a bitrate. The packets cover more than the duration where the stream starts with a preroll, so the packets' bytes over the duration would overstate it."

Other shared code that Voice calls and that stays: `WmaEncoder.Apply(context, streamNode, findings, [])`, `WmaHeaderOnly.Parse(...)`, `FindingCollector`, `Counts.Of` (Core), `PartsMap`, `ChunkedList`, `BlockedChildren`, the `Topics` IDs `WmaVoice`, `WmaVoiceSuperframes` and `WmaVoiceFrames`.
