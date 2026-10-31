# Windows Media Audio Lossless: format description

**Status:** written on 2026-10-29 by a Claude agent (Opus) that read FFmpeg 6.1.1's LGPL WMA Lossless decoder (listed under [Sources](#sources)) and checked this text against Microsoft-encoded files. It contains no code. Whoever implements the WMA Lossless frame parser, and later its decoder, must not read FFmpeg's `wmalosslessdec.c`; FFmpeg serves only as a black-box cross-check (decision 10 in [decoders.md](decoders.md#decisions)).

This is the clean-room description that decision 10 and rollout step 3 of [decoders.md](decoders.md) ask for. It describes the bitstream as FFmpeg reads it, states FFmpeg's checks as format rules, and marks what no source settles. What the analyzer reads today is in [asf-wma.md](../formats/asf-wma.md#wma-lossless).

## Sources

**Read (FFmpeg 6.1.1, the release `scripts/fixtures/ffmpeg_sources.py` pins, which lists `wmalosslessdec.c`):**

- `libavcodec/wmalosslessdec.c`: everything in this document below the codec data, that is the packet header, the reassembly of frames, the frame, the tile header, the subframes with their filter parameters and residuals, and the reconstruction.
- `libavcodec/wma_common.c`: the frame length from the sampling rate and the decode flags (its version 3 rule).
- `libavcodec/lossless_audiodsp.c`: the dot product and coefficient update the cascaded LMS filters use (the C versions).
- `libavcodec/get_bits.h`: how the bit reader behaves at the end of its buffer.
- `libavcodec/decode.c`: how a packet that the decoder consumes in parts is fed again, and what an error does to the rest of it.
- `libavformat/riff.c` and `riffdec.c`: the format tag 0x0163 and the WAVEFORMATEX fields FFmpeg passes on.
- `libavcodec/wmaprodec.c`, only to compare its length-prefix check, which is the same.
- `lossless_audiodsp.c`, `get_bits.h` and `decode.c` are not in `FILES` of `ffmpeg_sources.py`; they were taken from the same pinned archive.

**FFmpeg 8.0.1** (the release CI uses): its `wmalosslessdec.c` differs from 6.1.1's only in an added include and in how the codec declares its capabilities and sample formats, so the decoding is the same. The readings below were made with 6.1.1; no build of 8.0.1 was run over the files.

**Test files.** The committed fixtures (Microsoft's encoder on a Windows runner, see their `.provenance.json`):

| File | SHA-256 | What it is |
| --- | --- | --- |
| `tests/fixtures/asf/external-windows-wma92lossless-stereo-44k-16.wma` | `ffb6d7c9e729552856ad6ad8915f8a57e8619449d0c8b6b3ab287da81f86d6b8` | wmvcore 12.0.26100.7309, "VBR Quality 100", 44.1 kHz, 16 bits, stereo, 2 s of the "quiet" signal; 15 packets of 13,375 bytes, 44 frames |
| `tests/fixtures/asf/external-windows-wma92lossless-stereo-44k-24.wma` | `9401b088b59b3073d664dc71fce910e5c97b20a28ae41f15f24facb77d8ae81f` | the same at 24 bits; 29 packets, 44 frames |
| `tests/fixtures/asf/external-windows-wma92lossless-5.1-48k-24.wma` | `6ffa73e6639cc8ec322bbce37f89610c1d637c51e19cce02a8192a59cc8db80f` | the same encoder, 48 kHz, 24 bits, 5.1 (mask 0x3F), 0.5 s; 26 packets of 12,288 bytes, 12 frames, each longer than two packets |
| `tests/fixtures/asf/protected-wma-lossless.asf` | `92c6cfa41c8ddc6e93efaf37703fa02d8f7bed52c428a9e76a315698548866e4` | hand-built, encrypted; only its codec data is readable, so it confirms nothing about frames |

Public files (downloaded, never committed):

| File | Address | SHA-256 | What it is |
| --- | --- | --- | --- |
| `luckynight.wma` | https://samples.ffmpeg.org/A-codecs/lossless/luckynight.wma | `37fe6efab1231f07f46ed8d8bec212c0b97e1535f4931ba02d92138a32f2b6ee` | WMFSDKVersion 9.00.00.2980, 44.1 kHz, 16 bits, stereo, 60.48 s; 491 packets, 1,303 frames; decode flags without the V3 RTM flag |
| `luckynight.wav.bz2` | https://samples.ffmpeg.org/A-codecs/lossless/luckynight.wav.bz2 | `cb2c2c7284b48d75472f2ba3281d05fd18f8842b04c813d8a7103151e8bb3ebe` | the source the folder's readme names for all its files; 2,667,168 sample frames |
| `wma_0x163.wma` | https://samples.ffmpeg.org/A-codecs/WMA9/wma_0x163.wma | `e12cf4e973f06fcf6fc1813aa8f3676bea0bbae2d04b1b4eb140521656000649` | WMFSDKVersion 9.00.00.2980, 44.1 kHz, 16 bits, stereo, 97.6 s; 815 packets, 2,103 frames |
| `luckynight-partial.wma` | https://fate-suite.ffmpeg.org/lossless-audio/luckynight-partial.wma | `33d7dc28f717abcc8ef7f35c1c836840503e2d888c3dec833c53314eba81aa1b` | the first MiB of `luckynight.wma` (FATE `lossless-wma`); it ends inside a packet |
| `master_audio_2.0_24bit.wma` | https://fate-suite.ffmpeg.org/lossless-audio/master_audio_2.0_24bit.wma | `a4d6a181944cc53e60a9b7e41e2e399ead9d58947b1285e81b1182bf0d9d121f` | WMFSDKVersion 12.0.7601.17514, 48 kHz, 24 bits, stereo, 45 frames; one channel is not coded in 24 frames (FATE `lossless-wma24-1`) |
| `Mega_Weird_Audio_Test_24bit.wma` | https://fate-suite.ffmpeg.org/lossless-audio/Mega_Weird_Audio_Test_24bit.wma | `1ce71dcd98db91d23896e84c602c774fc93eeb6afc94fd762382744d680d2bbd` | remuxed by Lavf 57.34.101, 48 kHz, 24 bits with 8 zero low bits, stereo; 2 packets, cut inside its ninth frame (FATE `lossless-wma24-2`) |
| `g2_24bit.wma` | https://fate-suite.ffmpeg.org/lossless-audio/g2_24bit.wma | `0dbe31e8b7bc8b589df5109cf6e2a1a782cdf8491ecbc1cc1fee887648ff45b7` | WMFSDKVersion 11.0.5721.5275, 44.1 kHz, 24 bits, stereo, 5 frames, all raw PCM subframes (FATE `lossless-wma24-rawsubframe`) |

**How the text was checked:**

- A local FFmpeg 6.1.1 build, instrumented to print every field it reads, was run over all files above; the readings below come from it and from the source.
- The author then wrote a throwaway model from this description (not committed; it is derived from reading FFmpeg and must not reach the implementer). On every file it:
  - found every frame boundary that a packet header states, and no frame end the headers contradict (see [Frames in packets](#frames-in-packets));
  - agreed with every trailer bit and every seekable bit of the packet headers;
  - reconstructed the samples bit for bit as FFmpeg 6.1.1 decodes them (for the two cut files, up to the cut), and for `luckynight.wma` bit for bit as `luckynight.wav`, all 2,667,168 sample frames, which checks the reconstruction against the source and not only against FFmpeg;
  - counted the samples the committed fixtures' input signal has (88,200, 88,200 and 24,000), which the ASF cross-check (`crosscheck_asf_fixtures.py`) already compares bit for bit through FFmpeg.

What the files do not exercise is listed under [Not determined](#not-determined).

## Conventions

- Bits are read most significant first, from the first byte of a buffer on. `u(n)` is an n-bit unsigned field, `s(n)` an n-bit two's complement field; `u(0)` reads nothing and is 0.
- log2 is the base-2 logarithm; floor(log2 x) is the position of the highest set bit; ceil(log2 x) is the smallest k with 2^k ≥ x (0 for x ≤ 1).
- "x >> k" is a division by 2^k rounded toward minus infinity (an arithmetic shift); "wrapped to 32 bits" (16 bits) means the value modulo 2^32 (2^16) read as two's complement.
- B is the sample size the codec data states (16 or 24 bits), C the channel count, F the frame length in samples per channel, W the width of the length fields.
- Hex values are written 0x…; FFmpeg's names of fields are in backticks where naming the field helps.

## Terms

- **Packet:** the unit the container carries, one ASF media object of the block alignment's size. It starts with a short header and then carries frames as one continuous bitstream.
- **Frame:** F samples of every channel. Frames start at any bit and may span several packets.
- **Subframe:** a stretch of one channel's F samples. The tile header divides each channel into 1 to 32 of them, and the data of a subframe is coded together for the channels whose next subframes start at the same sample and have the same length. This is the subframe of WMA Professional ([asf-wma.md](../formats/asf-wma.md#wma-professional)); the word "tile" is not used for it here. "Tile header" is kept, as in asf-wma.md, for the header that divides a frame into subframes. FFmpeg uses "tile" only in flag names (seekable tile, raw PCM tile, tile aligned).
- **Seekable subframe:** a subframe whose seekable flag is 1. It carries every filter parameter and resets the decoder state (see [State and resets](#state-and-resets)); a coded subframe can be read only after one. The flag is read before the raw PCM flag, so a subframe can be both seekable and raw PCM.
- **Raw PCM subframe:** a subframe that holds the samples uncoded. It may also be seekable.
- **Residual:** the value the entropy code carries per sample, before the filters undo the prediction.
- **Filters:** the stages that turn residuals into samples: the cascaded LMS filters per channel (FFmpeg: CDLMS), a multichannel LMS filter (MCLMS), mid/side stereo (FFmpeg: inter-channel decorrelation), a recursive filter FFmpeg calls the AC filter, and quantization. FFmpeg's source does not expand CDLMS, MCLMS or AC.

## Codec data

### WAVEFORMATEX

The ASF Stream Properties Object holds a WAVEFORMATEX with format tag 0x0163 (FFmpeg maps it to its WMA Lossless decoder, `riff.c`). The fields FFmpeg passes on:

| Field | Use |
| --- | --- |
| Channels | the channel count, unless the codec data's channel mask is not 0 (below) |
| Samples per second | the sampling rate; it sets the frame length. FFmpeg's WAV header reader rejects 0 |
| Average bytes per second | the nominal bitrate only |
| Block alignment | the packet size in bytes. A 16-bit field, so at most 65,535 and W at most 19. The decoder rejects 0 and anything above 2^21, which an ASF stream cannot reach |
| Bits per sample | not used by the decoder; the codec data's sample size counts |
| Codec specific data size | the size of the codec data (18 in every file); FFmpeg caps it at what the object holds |

In all test files the ASF stream uses audio spread error correction with a span of 1, so the media objects are not descrambled ([asf-wma.md](../formats/asf-wma.md#asf-damaged-and-large-files)).

### The 18 bytes

All values little-endian:

| Bytes | Field | Meaning and FFmpeg's checks |
| --- | --- | --- |
| 0–1 | sample size B | 16 or 24; anything else is rejected ("Unknown bit-depth"). FFmpeg keeps the field in 8 bits, so the high byte is ignored: 0x0110 is accepted as 16 and 0x0118 as 24 |
| 2–5 | channel mask | the speakers, one bit each in WAVEFORMATEXTENSIBLE order (0x3 stereo, 0x3F 5.1). Where it is not 0, FFmpeg takes the channel count from it (the number of set bits) and not from the WAVEFORMATEX; more than 8 channels is unsupported |
| 6–13 | not read | 0 in every test file |
| 14–15 | decode flags | below |
| 16–17 | not read | 0 in every test file |

- Codec data shorter than 18 bytes is unsupported ("Unsupported extradata size"); FFmpeg reads only the first 18 of longer data.
- FFmpeg also works out the position of the low-frequency channel from the mask (where bit 3 is set, the number of set bits among bits 0 to 3, less one) and never uses it.
- FFmpeg has no check for a channel count of 0; such a stream cannot hold a frame, since the tile header finds no channel (see [Tile header](#tile-header)).

### Decode flags

| Bits | Mask | Meaning (FFmpeg's reading) |
| --- | --- | --- |
| 0 | 0x0001 | not read; set in every test file |
| 1–2 | 0x0006 | frame length adjustment: 0 none, 1 doubles F, 2 halves it, 3 quarters it |
| 3–5 | 0x0038 | log2 of the most subframes a channel may have per frame; values 6 and 7 (64 and 128) are rejected ("invalid number of subframes") |
| 6 | 0x0040 | length prefix: every frame starts with its length in bits |
| 7 | 0x0080 | dynamic range compression: every frame carries an 8-bit gain |
| 8 | 0x0100 | the V3 RTM flag (FFmpeg's name, `bV3RTM`): subframes carry an LPC flag, and the update speed change of the cascaded filters works on the active part of their history (see [Update speed](#update-speed)) |
| 9–15 | 0xFE00 | not read; 0 in every test file |

Values in the test files: 0x01A1 in the committed fixtures, `master_audio_2.0_24bit.wma` and `g2_24bit.wma`; 0x0121 in `Mega_Weird_Audio_Test_24bit.wma`; 0x0021 in `luckynight.wma` and `wma_0x163.wma` (both WMFSDKVersion 9.00.00.2980). So every file has adjustment 0, at most 16 subframes and no length prefix.

### Derived values

- **Frame length F** (its version 3 rule): 2^b samples per channel, with b = 9 for rates up to 16,000 Hz, 10 up to 22,050 Hz, 11 up to 48,000 Hz, 12 up to 96,000 Hz and 13 above; then b + 1, b − 1 or b − 2 for adjustments 1, 2 and 3. So F is 128 to 16,384. FFmpeg asserts F ≤ 16,384, which these rules always meet. Every test file has F = 2,048.
- **Most subframes** M = 2^(flags bits 3–5), 1 to 32. **Smallest subframe** m = F / M samples.
- **Length field width** W = floor(log2(block alignment)) + 4 bits, for the packet header's count of bits that finish the frame before and the frame length prefix. W is 4 to 19, as the block alignment is a 16-bit field; it is 17 for the blocks of 12,288 and 13,375 bytes in the test files.
- **Subframe length code width:** floor(log2(M − 1)) + 1 bits, that is log2(M) for M ≥ 2 (1, 2, 3, 4, 5 bits for M = 2, 4, 8, 16, 32); never read for M = 1.
- **Trim value width:** log2(F) + 1 bits (12 for F = 2,048).

## Packets

### Media objects and packets

- Each ASF media object of the stream is one packet. In every test file each media object is exactly the block alignment long and is carried whole in one ASF data packet, except the last of `luckynight-partial.wma` (120 bytes; the file is cut), so a packet is not always the block alignment's size.
- FFmpeg reads the first block alignment's bytes of a media object as a packet; where the object is longer, the rest is fed again as further packets (`decode.c` hands the decoder what it did not consume). A shorter object is read as a short packet: the decoder has no minimum packet size and reads up to the smaller of the block alignment and the bytes it has (the packet's bits end where the bytes do). The ASF demuxer hands over the bytes of the last media object when the file ends inside it, so a cut file's last, partial packet is read: `cut16.wma` (the 16-bit stereo fixture cut 6,000 bytes into its last media object) gives 43 frames and 88,064 samples in ffmpeg, where leaving the partial object out gives 42 frames and 86,016. Encspect's ASF layer hands the partial object to the Lossless parser only, after its `asf.object-incomplete` finding; the other Windows Media Audio parsers keep their own treatment of short objects, which their descriptions give. A packet with fewer bits than its own header is read past its end ("Overread") and counts as a loss.
- On an error FFmpeg drops the rest of the media object (`decode.c` discards a packet on an error return) and treats the next packet as following a loss; what a loss costs is in [Sequence numbers and loss](#sequence-numbers-and-loss).

### Packet header

| Bits | Field | Meaning |
| --- | --- | --- |
| 4 | sequence number | counts packets modulo 16 |
| 1 | seekable frame in packet | FFmpeg's name (`seekable_frame_in_packet`); FFmpeg skips it. In every test file it is set exactly where a frame that starts in this packet begins with a seekable subframe or a raw PCM subframe (see [Observations](#observations-on-the-test-files)) |
| 1 | spliced | FFmpeg asks for a sample file where it is set ("Bitstream splicing") and otherwise ignores it; 0 in every test file |
| W | count of bits that finish the frame before | the number of bits at the start of the payload that finish the frame begun in an earlier packet |

The header is 6 + W bits (23 in the test files); the rest of the packet is the payload.

### Frames in packets

- The payloads of consecutive packets, concatenated, form one stream of frames. A frame starts right after the previous one ends, at any bit. In the test files nothing lies between frames. FFmpeg's reading cannot show a gap after a frame whose trailer bit is 0: the bits between its end and the position the next packet's count gives are never read. So "nothing between them" is an observation of the test files there, not a rule from a source.
- **The count of bits that finish the frame before** of packet k says where the first frame that starts in packet k begins: that many bits into its payload. The bits before it are the end of the frame that started in an earlier packet.
  - 0 means a frame starts at the payload's first bit. Every test file's first packet states 0, and no later packet does.
  - A count at or above the payload's length means no frame starts in the packet: all of it continues the frame before. FFmpeg tests "at or above" and clamps the count. In every test file such a packet states exactly 8 times the block alignment (98,304 or 107,000), including the last packet where the frame it continues ends inside it; the only other case is the cut last packet of `luckynight-partial.wma`.
- **The trailer bit** at the end of each frame is 1 where another frame starts after it in the same packet (the packet the frame starts in), and 0 for the last frame that starts in its packet. So the frames that start in packet k are a run whose trailer bits read 1, …, 1, 0; the last of them ends inside packet k or, across packets whose payload it fills, at the count of a later packet.
- **The end of the stream:** the last frame's trailer bit is 0, and the bits after it to the end of the last packet are not a frame. In the test files they are zero in three files and look random in the others (see [Observations](#observations-on-the-test-files)); FFmpeg ignores them.
- **What FFmpeg does with a payload:** without the length prefix it cannot tell where a frame ends without decoding it. At the end of a packet it keeps the **saved run**: the bits from the start of the first frame that starts in the packet to the end of the payload. When the next packet arrives it appends the first N bits of that packet's payload, N being its count of bits that finish the frame before, which complete the last frame of the run, and only then decodes the run's frames one after another, until a trailer bit of 0. The first frame of the run is decoded in the call that reads the packet header, the others in the calls that follow for the rest of the packet. So the frames that start in a packet are decoded when the next packet arrives. A packet whose count reaches the end of its payload only appends its payload, and nothing is decoded then. After the last packet, its flush call decodes what is left, one frame per call, until a trailer bit of 0 or the end of the kept bits. With the length prefix it reads a frame from the packet where the prefix is not 0 and fits in the bits left, and keeps the rest as the start of a frame that goes on.
- **Which frames of a run are decoded** (the terms are those of the crossing frame in [WMA Professional's description](wma-pro-format.md#frames-in-packets-with-the-length-prefix)):
  - The first frame of the run is decoded in the call that reads the next packet's header, and its trailer bit is ignored: where bits of the run are left after it, they are decoded as further frames whatever it says. Each further frame is decoded only while bits of the run are left, and the run ends after one whose trailer bit is 0. So a first frame with trailer bit 0 followed by bits is followed by further frames, and a first frame with trailer bit 1 and no bits left is followed by none.
  - An error in a further frame is a later-frame error (see [Sequence numbers and loss](#sequence-numbers-and-loss)): the rest of the next packet is dropped. An error in the first frame clears the loss in the same call.
  - Where the next packet states a count of 0, no frame is decoded in the header call. The saved run is still kept and is decoded in the call that follows, where its first frame's trailer bit counts like any further frame's.
  - **Flush exception for a rejected trim:** a frame rejected for its end trim in the flush call outputs no samples, and draining ends there: no frame after it is decoded.
  - **Flush exception:** the flush call after the last packet has no header call, so the first frame it decodes is an ordinary one: its trailer bit counts, and each flush call decodes one frame and the run ends after a trailer bit of 0 or where no bits are left.
- **A frame that runs past the kept bits** is not a loss and is not detected inside the frame, whether it is the first frame of the run or a later one. FFmpeg reads what lies past the kept bits (zero bits, or the bytes of an earlier, longer run left in its buffer), so the frame is output as a whole frame, F minus its end trim samples per channel, and its trailer bit is read from there too. What lies past the kept bits cannot be specified, so the residuals read there and the running sums after them are undetermined until the next seekable subframe. The checks on a subframe's fields still apply to whatever is read there (a non-raw subframe that starts with no bit left is the "no bit left" error in [Validity rules and damage](#validity-rules-and-damage)); such an error is an error like any other, and the frame is then not output whole. No bits are left after such a frame, so the run ends.
- **A frame's bits** are therefore: from its start to the end of its first packet's payload, the whole payload of each packet it fills, and, in the packet where it ends, the first N bits of the payload, N being that packet's count of bits that finish the frame before. The packet headers lie between these pieces, and a frame's first and last bytes are shared with the frame or header next to it.
- **FFmpeg's buffer** for the saved run holds 32,768 × C bytes. It holds the whole saved run, not one frame: the bits from the start of the first frame that starts in the packet to the end of the payload (with up to 7 bits before them in the first byte), and every continuation appended to them, the whole payloads of packets that only continue included. A save is refused ("Too small input buffer") when the bits already held, the bits to add and 8, divided by 8 and rounded down, exceed 32,768 × C; the whole saved run is then dropped. Whether that counts as a loss depends on what is saved: a refused append of the next packet's count bits in the header step (the bits that finish the last frame of the run) clears the refusal in the same header handling, so the next packet's sequence number is checked normally and the run saved from the current packet's tail survives; only a refused save of a packet's own tail (the frames that start in it) leaves the loss pending into the next packet, whose count bits are then skipped. A packet that continues a run and only appends its payload can be the one that is refused, which drops the run (in the files of this kind the last packet of a run). The frame counts that follow from these two cases for 60 raw PCM frames of one channel, 32,774 bits each, are 52, 44, 36, 21, 28, 20 and 12 at block alignments of 29,000, 29,500, 30,000, 30,500, 31,000, 31,500 and 32,000 bytes (`WmaLosslessBufferTests`). The run is nearly a packet long when the first frame starts early in it, so with a block alignment above about 32,768 × C bytes nearly every packet is refused. That is possible for mono, as the block alignment goes up to 65,535, and such a stream is not decoded; a frame can still be walked.

### Sequence numbers and loss

- FFmpeg expects each packet's sequence number to be the previous one plus 1, modulo 16. It does not check the first packet it reads, nor a packet it reads while a loss from an error is pending. It stores the number it read whether it fits or not, so one packet with a wrong number gives two mismatches: its own, and the next packet's against the wrong one.
- On a mismatch ("Packet loss detected!"), the first N bits of the packet's payload, N being its count of bits that finish the frame before, are skipped, and reading resumes with the first frame that starts in the packet.
- **Without the length prefix, a gap costs the frames of the packet before it.** The frames that start in packet k are not decoded until packet k + 1 arrives. If packet k + 1 shows the gap, the whole saved run of packet k is dropped, undecoded, with every frame that starts in a missing packet. Black-box check with FFmpeg 6.1.1: changing the sequence number of packet 5 of the stereo 16-bit fixture loses 6 frames (12,288 samples): the frames that start in packet 4, dropped when packet 5 shows the gap, and those that start in packet 5, dropped when packet 6 does not follow the changed number either.
- **With the length prefix,** frames are decoded when they are complete in a packet, so a gap costs the frame that was crossing into the packet, and an error the rest of the media object.
- **A decode error without the length prefix** (a frame or a subframe that fails, a refused save of a packet's tail; an end trim of F or more is the exception, see [Validity rules and damage](#validity-rules-and-damage)) costs different frames by where it happens:
  - In the first frame of the saved run, which is decoded in the call that reads the next packet's header, the loss is cleared in the same call. The whole saved run is dropped, but packet k + 1 is read normally: its frames are saved and survive. The samples of the subframes before the failing one are still output.
  - In a later frame of the run, which is decoded in a call after the header, the call returns an error and FFmpeg drops the rest of packet k + 1, including the start of its frames, which were not saved yet. The loss stays pending, so the packet after is not checked for a gap, its count bits are skipped, and its frames survive. The remaining frames of the run, the frames of packet k + 1 and the failing frame's samples are all lost, as the call that fails returns an error and no frame.
- **After a dropped run, the next packet's count bits are the end of a frame whose start was lost.** A decoder appends them to an empty buffer and reads them as a frame, finds an empty or invalid subframe layout in them and logs it; the samples it outputs agree with Encspect's, which reads them the same way (`five-gap.wma`, the 5.1 fixture with packet 2's sequence number set to 3: the first read is 5 bytes at offset 54,350, and both tools decode 5,568 samples). Encspect lists those bits as no frame: the packet's "Frame End" node shows them, no invalid frame is reported for them, and the samples they output are counted.
- FFmpeg resets no decoder state on a loss: the next frames are decoded with the state of the frames before the gap. Only the start of the stream, a seek and a rejected filter order make it wait for a seekable subframe.

### Example

The first packets of `external-windows-wma92lossless-stereo-44k-16.wma` (13,375 bytes, W = 17, payload 106,977 bits):

| Packet | Sequence | Seekable bit | Bits that finish the frame before | Frames that start in it |
| --- | --- | --- | --- | --- |
| 0 | 0 | 1 | 0 | frames 0 (37,493 bits, seekable subframe), 1 (36,834) and 2 (36,678, of which 32,650 in packet 0) |
| 1 | 1 | 0 | 4,028 | frames 3, 4 and 5 |
| 2 | 2 | 0 | 6,179 | frames 6, 7 and 8 |
| 3 | 3 | 1 | 7,613 | frame 9 (seekable subframe), 10 and 11 |

Frames 0 and 1 have trailer bit 1 and frame 2 has 0; frame 2 ends 4,028 bits into packet 1, where frame 3 begins. In `external-windows-wma92lossless-5.1-48k-24.wma` every frame spans three or four packets of 98,281 payload bits, and the packets in between state 98,304; frame 8 starts in the last 3 bits of packet 16.

## Frame

### Layout

| Field | Width | Present |
| --- | --- | --- |
| frame length | u(W) | length prefix flag |
| tile header | variable | always |
| dynamic range gain | u(8) | dynamic range compression flag |
| trim present | u(1) | always |
| start trim present | u(1) | trim present is 1 |
| start trim | u(log2(F) + 1) | start trim present is 1 |
| end trim present | u(1) | trim present is 1 |
| end trim | u(log2(F) + 1) | end trim present is 1 |
| subframes | variable | one after another until every channel has F samples |
| one bit of unknown meaning | 1 | length prefix flag |
| trailer bit | u(1) | always |

- **Frame length:** the frame's length in bits, this field and the trailer bit included. FFmpeg requires it to be exactly the bits read up to the end of the last subframe plus 2, skips one bit and reads the trailer bit; otherwise it logs "would have to skip" and counts a loss. WMA Professional has the same check (`wmaprodec.c`). No test file has the length prefix.
- **Dynamic range gain:** read and not used by FFmpeg. 0 in every frame of every test file that has the flag.
- **Trims** (FFmpeg's comment says it does not know what they are for and guesses the samples to drop at the start or end of a stream):
  - The end trim shortens the frame's output: FFmpeg outputs F minus the end trim samples per channel, and rejects a frame whose end trim is F or more: FFmpeg returns right after reading the trim, before any subframe, with no loss. That frame outputs no samples and its trailer bit is not read; if bits of the run are left, the next frame is then decoded from the bit where the trim ended, as if a frame started there. In every test file only the last frame has one (1,912 samples in the two stereo fixtures, 576 in the 5.1 fixture, 97 in `g2_24bit.wma`, 1,376 in `luckynight.wma`, 1,608 in `wma_0x163.wma`), and the samples then add up to the input's: 44 × 2,048 − 1,912 = 88,200.
  - The start trim is read and not applied. No test file has one, and every file decodes from its first sample (the fixtures' output equals their input from sample 0).
- **Trailer bit:** see [Frames in packets](#frames-in-packets). Without the length prefix it follows the last subframe directly (WMA Professional's zero bits before it do not exist here).

### Tile header

The tile header divides each channel's F samples into subframes whose lengths are multiples of m.

- **Aligned subframes:** u(1), always read. Where it is 1, or M = 1, every channel has the same subframes, and no per-channel bits are read.
- Then, keeping for each channel the sum of its subframe lengths so far (all 0 at the start), repeat until every channel's sum is F:
  1. The candidates are the channels whose sum is the smallest of all channels' sums; call it s.
  2. For each candidate in channel order, one bit says whether it takes part in the next subframe length, except where the subframes are aligned, there is only one candidate, or s = F − m: then every candidate takes part without a bit.
  3. If no channel takes part, the frame is invalid ("Found empty subframe").
  4. The length: where s = F − m, it is m, with no bits read; otherwise a code u(floor(log2(M − 1)) + 1) gives (code + 1) × m.
  5. Each channel that takes part gets a subframe of that length. A channel with more than 32 subframes, or a sum above F, makes the frame invalid.
- The subframes of a channel then follow each other from sample 0.
- In every frame of every test file the aligned subframes bit is 0, every channel takes part (one bit 1 each), and the length code is the largest (15 with M = 16), so each frame is one subframe of 2,048 samples of every channel. The tile header is 1 + C + 4 bits there (7 in stereo, 11 in 5.1).

### Subframes in a frame

The subframes follow each other in the frame :

- The next subframe starts at the smallest number of samples any channel has decoded; its length is the length of the next subframe of the first channel (in channel order) with that number.
- The subframe's channels are those whose next subframe starts there and has that length; their decoded counts advance by it. The frame ends after the subframe that brings every channel to F.
- **FFmpeg reads every channel in every subframe:** the coded flags, residuals and raw samples below are read for all C channels with the subframe's length, whatever the subframe's channel set is, and the output goes only to the subframe's channels. This matches the frames of the test files, where every subframe holds all channels; what a subframe whose subframes differ between channels holds is not determined (see [Not determined](#not-determined)).

## Subframe

### Subframe start

| Field | Width | Present |
| --- | --- | --- |
| seekable subframe | u(1) | always |
| seekable subframe parameters | variable | seekable subframe is 1 (below) |
| raw PCM subframe | u(1) | always |
| coded flag of each channel | u(1) per channel, in channel order | raw PCM subframe is 0 |
| LPC present | u(1) | raw PCM subframe is 0 and the V3 RTM flag |
| LPC parameters | variable | LPC present is 1 |
| zero low bits present | u(1) | always |
| zero low bits P | u(5) | zero low bits present is 1; else P = 0 |
| samples or residuals | variable | always |

- A subframe that is not raw PCM can be read only after a seekable subframe whose filter parameters were accepted: not before the stream's first seekable subframe, nor after a seek, nor after a seekable subframe whose filter orders were rejected. Otherwise FFmpeg stops the frame ("Waiting for seekable tile"). A raw PCM subframe can be read at any time. One that is also seekable reads every seekable parameter first, resets the state like any seekable subframe (see [State and resets](#state-and-resets)) and ends the wait. No test file has such a subframe.
- In a raw PCM subframe every channel counts as coded.
- FFmpeg requires at least one bit to be left before the zero low bits flag.
- **Zero low bits P:** the samples carry B − P bits and are shifted left by P on output. A raw PCM subframe needs B − P ≥ 1, another subframe P ≤ B; otherwise the frame is invalid. `Mega_Weird_Audio_Test_24bit.wma` has P = 8 in every subframe (24-bit samples with 16 significant bits); every other file has 0.

### Seekable subframe parameters

In this order:

| Field | Width | Meaning |
| --- | --- | --- |
| arithmetic coding | u(1) | FFmpeg does not support it ("Arithmetic coding") and stops the frame; 0 in every test file. It stops after clearing the running sums and the filter state as every seekable subframe does (before this flag is read) and before it reads any filter parameter; the loss follows, but the decoder stays ready for subframes that are not seekable unless an earlier rejected filter order made it wait, and goes on with those cleared sums |
| AC filter | u(1) | the AC filter is on |
| mid/side | u(1) | inter-channel decorrelation (mid/side stereo) is on |
| MCLMS | u(1) | the multichannel LMS filter is on |
| AC filter parameters | where the AC filter is on | order code u(4) (order 1 to 16), scaling u(4) (0 to 15), then one coefficient u(scaling) per order, each value + 1 (16-bit, see below) |
| MCLMS parameters | where MCLMS is on | order code u(4) (order (code + 1) × 2, 2 to 32), scaling u(4), coefficients present u(1); where present, a width code u(ceil(log2(scaling + 1))) (width = code + 2, 2 to 17 bits), then order × C × C coefficients and C(C − 1)/2 cross-channel coefficients of that width |
| CDLMS coefficients present | u(1) | the cascaded filters' initial coefficients follow |
| CDLMS parameters, per channel in order | always | filter count code u(3) (1 to 8 filters); then each filter's order code u(7) (order (code + 1) × 8); then each filter's scaling u(4); then, where coefficients are present, for each filter a count code u(ceil(log2(order))) (count = code + 1), a width code u(ceil(log2(scaling + 1))) (width = code + 2) and count coefficients of that width |
| mean scaling | u(3) | the averaging constant of the residual code, 0 to 7 (FFmpeg: `movave_scaling`) |
| quantization step | u(8) | step = value + 1, 1 to 256 |

- **CDLMS orders:** FFmpeg rejects an order above 256 ("not supported") and then waits for the next seekable subframe. For B = 16 it asks for a sample file where an order is an odd multiple of 8 and decodes it anyway.
- **MCLMS coefficient order:** the order × C coefficients of channel 0 first, then those of channel 1, and so on; then the cross-channel coefficients, for channel 1 the one on channel 0, for channel 2 those on channels 0 and 1, and so on.
- **MCLMS coefficient values:** FFmpeg reads them as unsigned and keeps the low 16 bits as a two's complement value, so a value of up to 15 bits is never negative.
- **CDLMS coefficient values:** FFmpeg turns the raw value r of width w into floor(r × 2^(scaling + 2 − w)) and keeps the low 16 bits as two's complement. It shifts r as an unsigned value, so the top bit of r is not a sign unless it lands on bit 15 (scaling 14). A count above the order reads its coefficients and drops those past the order.
- Whether the coefficients are meant to be signed is not determined: no test file sends MCLMS or CDLMS coefficients.
- **AC filter coefficients** are stored as 16-bit values, so with scaling 15 the value 2^15 wraps to −32,768; the range 1 to 2^scaling holds only up to scaling 14.
- **LPC parameters:** order code u(5) (order 1 to 32), scaling u(4), integer bits code u(3) (1 to 8), then for each channel order coefficients s(scaling + integer bits). FFmpeg reads them and does not apply them ("Expect wrong output since inverse LPC filter"); no test file has LPC.

### Raw PCM subframe

- After P: for each channel in channel order, the subframe's length of samples s(B − P), each.
- Nothing else; no filter runs and no filter state changes (see [Reconstruction](#reconstruction)).
- **A raw PCM subframe can be seekable.** The seekable flag is read before the raw PCM flag, so such a subframe first reads every seekable parameter, sets every S to 0, clears the filter state as any seekable subframe does, and then holds raw samples. Its parameters (AC filter, mid/side, MCLMS, cascaded filters, mean scaling, quantization step) stay in force for the coded subframes after it, and it ends the wait for a seekable subframe. No test file has a seekable raw PCM subframe.
- `g2_24bit.wma` is all raw PCM subframes, none seekable: 5 frames of 98,324 bits for 2 × 2,048 samples of 24 bits (98,304 bits) and 20 bits of frame and subframe fields (14 more in the last frame, for its end trim).

### Residuals

For each channel in channel order whose coded flag is 1, with L the subframe's length:

| Field | Width | Present |
| --- | --- | --- |
| transient | u(1) | always |
| transient position | u(floor(log2 L)) | transient is 1 |
| initial mean | u(B) | seekable subframe |
| first sample | s(B + 1) where the mid/side flag is set, else s(B) | seekable subframe |
| one code per sample | variable | the other samples: L − 1 in a seekable subframe, else L |

- **Transient:** FFmpeg reads the bit and the position and does nothing with them. No test file sets the bit.
- **First sample:** taken as the residual of sample 0 as it stands; it does not change the running mean. The mid/side flag widens it whatever the channel count, also where mid/side is not applied (more than two channels).
- **Running mean:** each channel keeps a running sum S (32 bits, unsigned). A seekable subframe sets S of every channel to 0 and then, for each coded channel, to the initial mean × 2^(K + 1), with K the mean scaling (wrapped to 32 bits). Between seekable subframes S carries over from subframe to subframe and frame to frame; a channel that is not coded keeps its S.
- **One code** gives a value u:
  1. The quotient q: count the 1 bits up to the next 0 bit, which ends the count. Where the bits run out inside the count, FFmpeg stops reading this channel's residuals (the rest keep the values of an earlier subframe) and goes on with the next channel.
  2. Where q ≥ 32, an escape follows: a length code u(5), then u(length code + 1) bits whose value is added to q (wrapped to 32 bits). The count of 1 bits is not limited to 32; the escape only adds to it. Statistics count one escape for each code that takes this step, whatever its length code.
  3. The mean A = (S + 2^K) >> (K + 1), with the sum wrapped to 32 bits.
  4. Where A ≤ 1, u = q. Otherwise k = ceil(log2 A) remainder bits r follow (k is at most 31), and u = q × 2^k + r, wrapped to 32 bits.
  5. S becomes S + u − (S >> K), wrapped to 32 bits.
  6. The residual is u / 2 for even u and −(u + 1) / 2 for odd u.
- The residuals of a channel that is not coded are 0, and its codes are absent.
- **The walk does not depend on the reconstruction:** which bits are read depends only on the codec data, the frame's and subframe's fields, the mid/side flag and mean scaling of the last seekable subframe, whether a seekable subframe has been seen, and the running sums S. A parser can therefore find every frame's end without running any filter, as long as it keeps S per channel from the last seekable subframe on.

### Where a frame ends

What tells a reader that it walked a frame correctly:

- A frame ends after its trailer bit, which must lie within the data.
- The frame end must agree with the packet headers: the first frame that starts after the end of a frame with trailer bit 0 begins where the packet holding that point says (the count of bits that finish the frame before), and a frame with trailer bit 1 is followed by another frame start in the same packet.
- With the length prefix, the length must equal the bits read plus 2 at the end of the last subframe.
- A frame needs a seekable subframe since the last start or loss before it can be walked at all (or must be a raw PCM subframe), since its codes depend on S.
- In the test files every one of these holds for every frame; the walk reaches every boundary a count of bits that finish the frame before states, and the trailer bits agree with the packets they start in, apart from the last frame of each stream, whose trailer bit 0 ends the stream.

## Reconstruction

This part is for a decoder; a frame parser does not need it. All arithmetic is in 32 bits, wrapped, unless a step says otherwise; FFmpeg computes products and sums modulo 2^32 and stores 16-bit values modulo 2^16.

### State and resets

| State | Width | Reset |
| --- | --- | --- |
| running sum S per channel | 32 unsigned | every seekable subframe (above) |
| AC filter memory per channel (up to 16 outputs) and coefficients | 32 and 16 | cleared at every seekable subframe |
| MCLMS coefficients (order × C per channel), cross-channel coefficients, history and signs | 16, 16, 32, 32 | cleared at every seekable subframe |
| CDLMS coefficients, history and update entries per channel and filter | 16, 16 or 32, 16 | cleared at every seekable subframe, for the filters the channel had before the subframe; the coefficients the subframe sends are set after that |
| update speed per channel | 8 or 16 | none; 0 at the start of the stream |

- A raw PCM subframe that is not seekable reads, changes and clears none of this, so a subframe after it that is not seekable goes on with the state from before it. A raw PCM subframe that is also seekable resets all of it like any seekable subframe (including every S, to 0), and its parameters apply to the subframes after it. No test file has a raw PCM subframe followed by another kind, or a seekable one.
- FFmpeg clears the filters of the count a channel had before the seekable subframe; where a seekable subframe raises a channel's filter count, the added filters keep what they held when the channel last had that many. In every test file the counts never change.

### Order of the stages

For a subframe that is not raw PCM:

1. For each channel in order: read its residuals (above); where it is coded, set its update speed (16 in a seekable subframe, else 8) and run its cascaded LMS filters over the subframe. A channel that is not coded gets residuals of 0 and its cascaded filters do not run.
2. Where MCLMS is on, run it over the subframe, sample by sample across all channels.
3. Where mid/side is on, there are exactly two channels and at least one of them is coded, undo mid/side.
4. Where the AC filter is on, run it over every channel.
5. Where the quantization step is not 1, multiply every channel's values by it.
6. Output each of the subframe's channels: the value × 2^P. FFmpeg keeps the low 16 bits for B = 16; for B = 24 it outputs value × 2^(P + 8) in 32 bits, the 24-bit sample in the high bits.

### Cascaded LMS filters

Each channel has 1 to 8 filters; the residuals pass through the last filter first, then the one before, down to filter 0, each over the whole subframe. A filter of order N (a multiple of 8) and scaling Q has coefficients a[0..N−1] (16 bits), a history h[0..N−1] of its past outputs with h[0] the newest, and update entries d[0..N−1] (16 bits). Its histories hold 16-bit values for B = 16 and 32-bit values for B = 24, but every value is clamped to B bits anyway. For each sample with input e:

- The prediction p = (2^Q >> 1) + Σ a[j] × h[j] over j = 0 to N − 1, wrapped to 32 bits. 2^Q >> 1 is 0 for Q = 0.
- Every coefficient moves by the update entry, with the sign of e: a[j] becomes a[j] + sign(e) × d[j], wrapped to 16 bits; this uses p's coefficients and the entries before this sample's update. sign is 1, 0 or −1.
- The output x = e + (p >> Q).
- The history and the entries shift by one place (the oldest drops out); h[0] becomes x clamped to −2^(B−1) … 2^(B−1) − 1, and d[0] becomes sign(x) × the channel's update speed.
- Then d[N / 16] becomes d[N / 16] >> 2 and, after it, d[N / 8] becomes d[N / 8] >> 1. For N = 8, N / 16 is 0, so the new entry itself is shifted.
- x is the input of the next filter, or the stage's output after filter 0.

At a seekable subframe the coefficients are cleared and then set from the subframe where it sends them; the history and the entries start at 0. FFmpeg's product runs over N rounded up to a multiple of 16 for B = 16, with the coefficients past N kept at 0, which leaves the sum unchanged.

### Update speed

- The channel's speed is set in each subframe where the channel is coded: 16 in a seekable subframe, 8 in the others.
- Where the speed changes, before the filters run on the subframe, the stored update entries of each of the channel's filters are doubled (to 16) or halved (to 8, divided by 2 rounding toward 0), wrapped to 16 bits. A change from 8 to 16 happens only at a seekable subframe, whose entries are all 0, so only the change to 8 matters.
- **Which entries:** with the V3 RTM flag, all N entries d[0..N−1]. Without it, FFmpeg changes the entries that sit at the first N places of its buffer, which depends on its buffer layout:
  - FFmpeg keeps the history and the entries in a buffer of 2N places with a position r: after a seekable subframe r = N, and the active window is places r to r + N − 1, with d[j] at place r + j.
  - Each sample lowers r by one before the new value is written at r; where r is 0, the window's N values are first copied to places N to 2N − 1, and r becomes N − 1.
  - So without the flag, the change applies to d[0] up to d[N − 1 − r], with r the position at the start of the subframe: all N entries where r = 0, fewer otherwise.
- In the test files r is 0 at every subframe start where the speed changes (subframes of 2,048 samples and orders that divide 2,048), so the two readings give the same output there. The one without the flag is checked only in that case (`luckynight.wma` and `wma_0x163.wma`).

### Multichannel LMS filter

The filter of order R predicts each channel from the last R samples of all channels and from the current sample of the channels before it. With N = R × C:

- A history H[0..N−1]: H[t × C + c] is channel c's output t + 1 samples ago, clamped to B bits; signs G[0..N−1] of the same values (not clamped). Coefficients A[c][0..N−1] per channel and cross-channel coefficients X[c][0..c−1], 16 bits each, scaling Z.
- For each sample, for each channel c in order: where c is coded, p_c = Σ A[c][k] × H[k] + Σ X[c][j] × y_j over j < c, with y_j the already reconstructed value of channel j at this sample, wrapped to 32 bits; then p_c = (p_c + (2^Z >> 1)) >> Z and y_c = e_c + p_c. Where c is not coded, p_c = 0 and y_c = e_c.
- Then for each channel c: the error y_c − p_c; where it is positive, every A[c][k] grows by G[k] and every X[c][j] by sign(y_j); where it is negative, both shrink by the same; where it is 0, nothing changes. 16-bit wrapping.
- Then the history shifts by C places and takes the C new values, channel 0 at H[0]; G likewise with their signs. (FFmpeg's buffer of 2N places with a position falling by C per sample is equivalent here: N is a multiple of C.)

### Mid/side

- Where the flag is set and C = 2: channel 0's value becomes v0 − (v1 >> 1), and then channel 1's value becomes v1 + the new channel 0 value.
- So the coded channel 1 is the difference (channel 1 minus channel 0) and the coded channel 0 is channel 0 plus half that difference, rounded down.
- Every stereo test file has the flag in every seekable subframe; the 5.1 fixture does not.

### AC filter

A recursive filter of order Q_a with coefficients c[0..Q_a−1] (each the coded value + 1 stored in 16 bits: 1 to 2^scaling up to scaling 14, and for scaling 15 the value 2^15 wraps to −32,768) and scaling T. For each channel, with y[n] the outputs of this subframe and y[−1], y[−2], … the channel's last outputs before the subframe (its memory, 0 after a seekable subframe):

- y[n] = x[n] + ((Σ c[j] × y[n − 1 − j] over j = 0 to Q_a − 1) >> T), the sum wrapped to 32 bits; the filter feeds back its own outputs.
- After the subframe, the memory holds the last Q_a outputs (where the subframe is shorter than Q_a, the older memory moves back by the subframe's length).
- It runs on every channel, coded or not, and its memory carries over between subframes and frames up to the next seekable subframe.

### Quantization

- Every value of every channel is multiplied by the step, wrapped to 32 bits, where the step is not 1. Every test file has step 1.

## Samples and duration

- A frame codes F samples per channel and outputs F minus its end trim. The start trim is not applied by FFmpeg (see [Layout](#layout)).
- FFmpeg outputs the first frame (it marks it to be skipped and never reads the mark).
- Where a frame fails in a subframe, FFmpeg outputs the samples of the subframes before it (counted for channel 0) and counts a loss, if the failing frame is the first of a saved run; in a later frame the call returns an error and no frame (see [Sequence numbers and loss](#sequence-numbers-and-loss)).
- The stream's samples are the sum over its frames. In the test files that equals the source's sample count: 88,200, 88,200, 24,000, and 2,667,168 for `luckynight.wma`.

## Limits

Limits the syntax itself sets:

| Limit | Value |
| --- | --- |
| Frame length F | 128 to 16,384 samples per channel |
| Subframes per channel and frame | 1 to M, M at most 128 by its field |
| Subframe length | a multiple of m = F / M, m to F |
| AC filter | order 1 to 16, scaling 0 to 15 |
| MCLMS | order 2 to 32 (even), scaling 0 to 15, coefficient width 2 to 17 bits |
| Cascaded filters | 1 to 8 per channel, order 8 to 1,024 (multiple of 8), scaling 0 to 15, coefficient width 2 to 17 bits |
| Mean scaling | 0 to 7 |
| Quantization step | 1 to 256 |
| Frame size | no limit; a frame may span any number of packets |
| Zero low bits | 0 to 31 |
| LPC | order 1 to 32, coefficients 1 to 23 bits |
| Sequence number | 0 to 15 |
| Count of bits that finish the frame before, frame length | W bits, 4 to 19 |

FFmpeg's own limits (a decoder that follows FFmpeg rejects more than the syntax does; whether the format allows more is not determined):

| Limit | Value |
| --- | --- |
| Block alignment | 1 to 2^21 bytes (a 16-bit field, so at most 65,535 from ASF) |
| Sample size | 16 or 24 (the low 8 bits of the 16-bit field) |
| Channels | at most 8 |
| Codec data | at least 18 bytes |
| Most subframes M | at most 32 |
| Subframes per channel and frame | at most 32 |
| Cascaded filter order | at most 256 |
| Saved run (the bits from the first frame that starts in a packet to its end, and all that is appended) | 32,768 × C bytes |
| Arithmetic coding | not supported |
| LPC | read, not applied |

## Validity rules and damage

What FFmpeg does with each broken rule. "Frame invalid" means FFmpeg stops decoding the frame; "loss" means it drops frames, as [Sequence numbers and loss](#sequence-numbers-and-loss) says: without the length prefix, a gap drops every frame that starts in the packet before it, an error in the first frame of a saved run drops that run, and an error in a later frame also drops the rest of the next packet; decoding resumes at the first frame that starts in a later packet, with the state it has. The last column is the treatment this description suggests, not a rule from a source.

| Rule broken | FFmpeg | Suggested treatment |
| --- | --- | --- |
| Codec data shorter than 18 bytes, sample size other than 16 and 24, more than 8 channels, M above 32, block alignment 0 (above 2^21 cannot occur in ASF) | stream not decoded | stream not readable; the frames cannot be walked |
| Sequence number not the previous plus 1 | loss | finding; without the length prefix, every frame that starts in the packet before the gap is lost |
| Spliced bit set | asks for a sample, goes on | finding (unsupported, not damage) |
| Packet shorter than its header | loss ("Overread") | packet unreadable, `wma.packet-overread` |
| Tile header: no channel takes part, more than 32 subframes, a channel's sum above F | frame invalid, loss | frame invalid; resynchronize |
| End trim of F or more | frame rejected without a loss; FFmpeg then reads on from inside it as if a frame started there | frame invalid |
| Length prefix disagrees with the frame | loss ("would have to skip") | frame invalid |
| Arithmetic coding flag | frame stops, loss; the running sums and filter state are cleared and the decoder stays ready (no wait for a seekable subframe) unless a filter order was rejected | unsupported |
| Cascaded filter order above 256 | frame stops, loss; waits for a seekable subframe | unsupported |
| Subframe neither seekable nor raw PCM before any seekable subframe | frame stops, loss | not readable yet; not damage at the start of a stream |
| No bit left before the zero low bits flag; zero low bits too large | frame stops, loss | frame invalid |
| Bits run out inside a quotient | the channel's other residuals are left as they were and the frame goes on | frame truncated |
| Frame runs past the kept bits | not detected inside the frame; FFmpeg decodes zeros or stale bytes past the end | frame truncated (the walk must stop at the data's end) |
| Saved run too long for FFmpeg's buffer | loss ("Too small input buffer"), the whole run | `wma.run-too-long` (FFmpeg cannot decode it); Encspect does not walk those frames |
| Last frame starting in a packet does not end where the next packet's count of bits that finish the frame before says | not detected without the length prefix | frame invalid; the packet's count is the boundary to resynchronize at |
| Trailer bit 1 but no further frame starts in the packet | not detected | `wma.trailer-without-frame` (info) |

Not checked by FFmpeg, and worth a finding rather than a failure: decode flags bits 0 and 9 to 15, the codec data bytes it does not read, a seekable bit that disagrees with the frames that start in the packet, a channel mask whose bit count differs from the WAVEFORMATEX channel count, the transient bit, a nonzero dynamic range gain, a start trim, an end trim on a frame that is not the last, and bits after the last frame that are not zero.

Encspect checks none of these but the trailer bit above. The seekable bit and the bits after the last frame stay unchecked: the observations they would rest on are observations of the test files, and the stereo fixtures themselves hold nonzero bits after their last frame. `asf-wma.md` lists them under what is not covered.

**Encspect's own limit on frames:** a frame can take as few as 7 bits (one seekable frame earlier, then a mono frame with one subframe that codes no channel), so a stream holds far more frames than media objects. At most 524,288 frames are recorded and described; later frames are read, counted in the figures and in the samples, and left out of the list and the series (`wma.records-limit`). `WmaLosslessBufferTests` holds a stream of 3,000,000 such frames in 88 media objects to a bounded heap.

**Resynchronizing:** a frame cannot be walked without the running sums of the frames before it, so after damage the reliable restart is a packet whose seekable bit is set: in every test file the first frame that starts in such a packet, at its count of bits that finish the frame before, begins with a seekable subframe (or is a raw PCM frame). This is an observation, not a rule from a source.

## FFmpeg's implementation choices

These are choices of FFmpeg's decoder, not rules of the format; a cross-check must account for them.

- **Late decoding:** without the length prefix, FFmpeg decodes the frames that start in a packet when the next packet arrives, and the last packet's frames in its flush call. Its output timestamps therefore lag; its null and raw muxers warn about "non monotonically increasing dts" for several test files, which `crosscheck_asf_fixtures.py` already tolerates.
- **Every channel in every subframe** (see [Subframes in a frame](#subframes-in-a-frame)).
- **No reset after a loss:** decoding goes on with the old state at the next frame start. The running sums S and the filter state are stale, so a later frame is read with wrong code lengths and can be misparsed, fail, and cause further errors, besides giving wrong samples, until the next seekable subframe.
- **Ignored fields:** the seekable and spliced bits of the packet header, the transient bits, the dynamic range gain, the start trim, the LPC coefficients, and decode flags bits 0 and 9 to 15.
- **Coefficient signs:** MCLMS and cascaded filter coefficients are read as unsigned values (see [Seekable subframe parameters](#seekable-subframe-parameters)).
- **Filters not cleared:** filters a channel did not have before a seekable subframe keep old state (see [State and resets](#state-and-resets)).
- **Update speed without the V3 RTM flag:** applied to the entries by their place in FFmpeg's buffer (see [Update speed](#update-speed)).
- **Running out of bits:** FFmpeg's reader returns zeros (or bytes left from earlier frames) past the end of the bits it kept, so a cut frame decodes to something; only the quotient count and the check before the zero low bits flag look at the end.
- **Output:** planar 16-bit samples for B = 16, planar 32-bit samples with the 24-bit value in the high bits for B = 24.

## Observations on the test files

Observed behavior of the encoders behind the test files, not rules; none of them is a detection rule.

- **One subframe per frame:** every frame of every file is one subframe of 2,048 samples holding all channels, with the aligned subframes bit 0 and one bit 1 per channel.
- **Seekable subframes:** in the first frame and then 7 to 12 frames apart (0.3 to 0.56 s) in every file with more than one, except `master_audio_2.0_24bit.wma` (frames 0, 27 and 31): 5 of 44 frames in the stereo fixtures, 2 of 12 in the 5.1 fixture, 131 of 1,303 in `luckynight.wma`, 211 of 2,103 in `wma_0x163.wma`. Each is the first frame to start in its packet, and that packet's seekable bit is set; the bit is set in no other packet, except that in `g2_24bit.wma` it is set in every packet where a raw PCM frame starts.
- **Filters:** every seekable subframe of every file has the AC filter on with order 1 and scaling 6, mean scaling 5 and quantization step 1, and sends no coefficients.
  - The Microsoft fixtures (wmvcore 12.0.26100) and `master_audio_2.0_24bit.wma` (12.0.7601): one cascaded filter per channel, order 16 at 16 bits and 8 at 24 bits, scaling 12, no MCLMS.
  - `luckynight.wma` and `wma_0x163.wma` (9.00.00.2980): MCLMS of order 2 and scaling 8, and two cascaded filters per channel, order 64 with scaling 12 and order 16 with scaling 11. `Mega_Weird_Audio_Test_24bit.wma`: MCLMS 2 and 8, cascaded filters of order 16 (scaling 12) and 8 (scaling 11).
  - Mid/side is on in every stereo file and off in the 5.1 fixture.
- **Coded channels:** every channel is coded in every subframe, except the second channel of `master_audio_2.0_24bit.wma` in 24 of its 45 frames.
- **Escape codes** (codes with q ≥ 32): 11 in `luckynight.wma` and one in `wma_0x163.wma` (length codes 0 to 4); none in the other files.
- **Never seen:** the length prefix, the arithmetic coding flag, LPC, transients, start trims, the spliced bit, coefficients sent in a seekable subframe, a seekable raw PCM subframe, quantization steps above 1, a frame length adjustment, a nonzero dynamic range gain.
- **Packets that continue only:** whose count of bits that finish the frame before is exactly 8 times the block alignment, also as the last packet, where the frame they continue ends inside them (the 5.1 fixture's last packet and the stereo 24-bit fixture's).
- **Bits after the last frame:** zero in the 5.1 fixture, `master_audio_2.0_24bit.wma` and `g2_24bit.wma`; about half ones in the two stereo fixtures (28,373 and 99,461 bits), `luckynight.wma` (59,926) and `wma_0x163.wma` (3,547), and in none of the four do they read as a frame. Their origin is not known.
- **Frames per packet:** from frames spanning up to four packets (the 5.1 fixture) to 27 frames starting in one packet (`master_audio_2.0_24bit.wma`, where near-silent frames take about 2,300 bits each).

## Not determined

- The meaning of decode flags bit 0 and the codec data bytes FFmpeg does not read; what bits 1 to 2 and 6 do in real files (no file sets them).
- Whether a subframe carries data for all channels when the channels' subframes differ, as FFmpeg reads it, or only for the channels it covers, as WMA Professional's subframes do. No file has more than one subframe per frame; a file encoded with variable subframe sizes would settle it.
- The length prefix: the meaning of the one bit FFmpeg skips before the trailer bit, and whether its check is a format rule.
- The sign of the MCLMS and cascaded filter coefficients a seekable subframe may send, and whether FFmpeg's reading of them is right; no file sends any.
- The transient position's meaning, the dynamic range gain's use, the start trim's meaning (FFmpeg's comment guesses), and the arithmetic coding and LPC tools.
- The update speed change without the V3 RTM flag at a subframe start where FFmpeg's buffer position is not 0 (cascaded filter orders that do not divide the subframe length).
- What a non-seekable subframe after a raw PCM subframe starts from, whether an encoder writes a seekable raw PCM subframe (FFmpeg reads one as above; no file has it), and whether filters added at a seekable subframe should start cleared.
- The meaning of the packet header's seekable and spliced bits beyond the observation above.
- The bits after the last frame of a stream, and whether a packet in which a frame ends but none starts must state 8 times the block alignment.
- Whether the format has a checksum that FFmpeg skips; FFmpeg reads none, and no field above is unaccounted for in the walk, apart from the bits after the last frame.
