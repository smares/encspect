# Parallel decoding: frames on several threads

Status: a draft for the project lead, not approved and not built. It designs option B of the second speed pass ("Decode independent units in parallel"), the one that needs a change of the decoder contract in [decoders.md](decoders.md). A throwaway prototype outside the repository measured the gains (see [Expected gains](#expected-gains)); nothing of it is committed. The decisions it asks for are under [Open decisions](#open-decisions). It was revised after an independent review: the check of the plan before the journal is replayed (replan), WavPack sets whose blocks fail, early cancellation of workers, scratch memory, the wasted-work bound and the verification are the main changes. The **analysis thread** in this document is the thread that runs `Analyze` and with it the parsers: the term of the sink contract (`IPcmSink.Write` is called "on the analysis thread, in stream order").

## Scope

- **In:** decoding frames of one stream on several threads while the parser reads the stream, with a report, PCM and checksums identical to a serial run. The formats are Monkey's Audio, WavPack and FLAC, whose frames decode apart from each other.
- **Out, and how this design relates to them:**
  - **Option A, decoding while the parser walks** (a pipeline of the parser and one decoder thread). B contains it for the three formats above: the parser hands a frame over and reads on while workers decode. What A adds is a stateful decoder (TrueHD) on one thread of its own. The design leaves room for it: a decoder that does not split its frames could run its whole `Decode` as one worker in stream order, with the same copy of the bytes, the same lent buffers and the same commit. That is a later change with its own measurement, not part of this one.
  - **Option C, checksums on a thread of their own.** In B the stream-order checksums (FLAC's and WavPack's MD5) run in the commit, on the analysis thread. C would move the commit to a thread of its own. The commit is one method called in stream order (below), so moving it later changes where it runs and not what it does; nothing in B has to be undone for C. The stream decoder's state is therefore "used by one thread at a time, in stream order", not "only on the analysis thread". FLAC is the format that would gain (see [What stays serial](#what-stays-serial)).
  - **Parallel files in the CLI.** The CLI analyzes files one after another (`InputWalker` and the loop of `CommandLine`); a folder of small files would gain more from files in parallel than from frames in parallel. That is a separate decision (see [Open decisions](#open-decisions)).
  - TrueHD and MLP, whose frames carry state (see [Independence per format](#independence-per-format)), and the codings without a decoder.

## The design in short

- A decoder whose frames decode apart from each other splits the decoding of a frame into steps:
  - a **plan** on the analysis thread in stream order: what the frame needs of the stream's state, as a guess where that state depends on frames still in flight;
  - the **frame decoding** on a worker: the expensive part, which reads only the frame, its plan and the worker's own buffers;
  - a **replan** on the analysis thread in stream order, right before the frame's journal is replayed: the decoder checks the plan against the state the earlier commits left, and where it no longer holds the host drops the journal and decodes the frame again inline with the new plan;
  - a **commit** on the analysis thread in stream order: checksums over the whole stream, set assembly, the sink.
- One worker holds the scratch state that `IFrameDecoder` holds today. The host gives each running batch a worker of its own; the stream's state stays in the `IFrameDecoder` and is touched only by the plan, the replan and the commit.
- What a worker reports (work, findings, PCM) goes into a **journal** of the frame. The host replays the journals in stream order at the commit, so findings, sink calls, the budget and the session's end happen in the order and at the frame where a serial run has them.
- **A degree of 1 is decoding at the hand-off:** plan, frame decoding and commit run inline in the call that hands the frame, with no copy of the bytes, no window and no batches, and the journal passes every event on at once, so the PCM still streams to the sink chunk by chunk. A degree above 1 changes only where the frame decoding runs and when its journal is replayed.
- The host owns the threads: a degree of parallelism in `DecodeOptions` (an option of the call, not a setting), a window of frames in flight, batches of small frames, a memory bound, and a join of every worker before `Analyze` returns or throws.
- The report, the PCM a sink receives and every checksum are byte-identical to a serial run for every degree, which a test holds over all decoder fixtures, the corrupt-file and fuzz corpora and the benchmark files.
- **Serial reports stay as they are** wherever the design can keep them. Where a fix would change them, the design takes the option that keeps them and lists the other as an open decision (WavPack sets and DSD charge points, decisions 8 and 9). The one change in the main line is the Monkey's Audio parser's `frame-not-decoded` finding in a corner case, which needs the owner's sign-off (decision 11), and which has an alternative that changes nothing.

## Goal and expected gains

The goal is a faster `--verify` (and "Verify Audio") on a machine with free cores, for the formats where decoding is most of the run. The library's analysis without decoding is not changed.

### What stays serial

Per format, from the code and the profiles of the second speed pass (`decoders.md`, "As built: the second speed pass"; JIT build, one pinned core, 300 s files):

| Format | Decoded on workers | Serial: on the analysis thread | Analysis / `--verify` (s) | Serial share of `--verify` | Bound at 4 workers, estimated | Bound with unlimited workers |
|---|---|---|---|---|---|---|
| Monkey's Audio `-c5000`, 16-bit | the whole frame: range decoder, predictors, filters, frame CRC | the parser's walk of headers and seek table, the descriptor MD5 pass (`Reader.Hash`), the commit (tallies, sink) | 0.38 / 9.16 | about 4% | about 3.4 times: 12 frames of 1,179,648 samples (11.2 frames' work) take 3 rounds on 4 workers | about 8 times: one frame (0.8 s) is the shortest decode |
| Monkey's Audio `-c4000`, 16-bit | as above | as above | not in the pass's table; prototype: 0.10 / 4.78 in process | about 2% | about 3.6 times (45 frames) | far above 4 |
| WavPack default, 16-bit | each block: words, decorrelation passes, mute check, block CRC | the parser's walk, the plan (sub-block reading for the stream's facts), the commit: set assembly, MD5 and its byte packing (8 to 10% of the decoding step), the sink | 0.34 / 1.84 | about 25% | about 3 times | about 4 times |
| WavPack `-hh`, 24-bit/96 kHz | as above | as above, the MD5 and packing 6 to 7% of the decoding step | 0.48 / 7.36 | about 12% | about 3.5 times | about 8 times |
| FLAC -5, 16-bit | prediction, channel recombination | the parser's walk (it finds where a frame ends), the commit: the canonical bytes and the MD5 (36% of the decoding step), the sink | 0.52 / 1.02 | about 70% | about 1.4 times | about 1.4 times |
| FLAC -8, 24-bit/96 kHz | as above | as above (the shares taken from the -5 profile) | 0.83 / 1.88 | about 64% | about 1.5 times | about 1.5 times |
| TrueHD, MLP | nothing | everything (not parallel) | 1.75 / 2.75 (24-bit) | 100% | 1 | 1 |

- The "Serial share" is the analysis plus the serial part of the decoding step, over the `--verify` time. The analysis includes the start of the runtime and the host, about 0.15 s of the release build and 0.24 s of the JIT build (`decoders.md`, "As built: the second speed pass").
- **Amdahl, with the overlap B gives:** the parser and the workers run at the same time, so the run takes about the start plus the longer of the analysis thread's work (walk and commit) and the workers' work divided by their number. That is the "Bound" column; it is an estimate from the profile shares, not a measurement.
- **FLAC** is bound by the analysis thread: its walk alone is half the run and the MD5 is a dependency chain (about 4.5 cycles a byte). With option C, the commit on a thread of its own, the bound becomes the walk: about 2 times for FLAC -5 (estimated). That is the case for C.
- **FLAC within B:** the commit's 36% is two things, packing the canonical bytes (`CanonicalPcm.Write`) and the MD5. The packing needs only the frame's PCM, so a worker could write the canonical bytes into its journal and leave only the MD5 serial in B. How the 36% divides between the two is not measured; it is to be measured before decision 7.
- **Granularity:** a frame is the smallest unit. Monkey's Audio at insane holds 1,179,648 samples per channel in a frame (27 s at 44.1 kHz), so a 3-minute song has 7 frames and gains at most about 3.5 times on 4 workers, and a song under 27 s gains nothing. Within a frame the predictors, filters and the range decoder are sequential, and the two channels of a stereo pair are predicted from each other (`FrameDecoder.DecodeChunk`), so there is nothing to split further.

### What the measured gains are

The prototype (see [Expected gains](#expected-gains)) measured whole analyses with decoding of the benchmark files on this four-core machine, with reports equal to the serial ones. The figures are the best of three rounds on a shared machine (see [How far the figures carry](#how-far-the-figures-carry)), so they are best cases:

- Monkey's Audio about 3.0 to 3.7 times (`-c3000` to `-c5000`, 16 and 24 bits), as the speed pass expected (3 to 3.5 times);
- WavPack about 2.2 to 3.2 times, at the low end of the expected 2.5 to 3.5 for 16-bit files, whose serial share is the largest;
- FLAC about 1.1 to 1.25 times in the best runs, below the expected 1.4 (the prototype hashes twice; see there), and not distinguishable from no gain in others;
- TrueHD nothing, by design.

## Independence per format

What a frame needs from the frames before it, read in the code (which follows the reference sources named in its notices), and how the frame boundaries are known before decoding.

### Monkey's Audio

- **Per frame:** `FrameDecoder.DecodeBody` flushes every predictor and neural network filter (`Predictor32.Flush`, `Predictor64.Flush`, `Predictor3930.Flush`, `NnFilter16.Flush`), resets each channel's range-decoding state (`ChannelState.Flush`, the SDK's `UNBIT_ARRAY_STATE`), starts the range decoder on the frame's own bytes (`RangeDecoder.Begin`, the SDK's `FlushBitArray`) and starts the stereo pair's cross-prediction at 0 (`lastX`). This is the SDK's `StartFrame` (`APEDecompressCore.cpp`), which the decoder follows. Nothing of the coded data of one frame is read by another.
- **What crosses frames:**
  - **Interim mode** (`FrameDecoder._isInterim`): a 24-bit frame whose CRC fails in the normal mode is decoded again with the interim predictors, and when the CRC then passes the decoder keeps interim mode for the frames after it (`DecodeHeld`, "As built: Monkey's Audio" in `decoders.md`). So the mode a frame starts in is the mode the frame before it ended in: a carried state of one bit, which the design handles by planning a guess and replanning before the replay (below).
  - The frame CRC tallies (`_checked`, `_failed`), the sink (declared at the first frame) and the findings: all stream-order effects, which go to the commit.
- **Boundaries:** the seek table gives every frame's start before the first frame is read (`Reader.Frames` with `SeekReader`), and the parser reads each frame into a new array of its own (`MappedBytes.Read` in `Reader.Feed`), never reused, up to 64 MiB (`State.MaxDecodedFrameBytes`).
- **The descriptor MD5** covers the coded data and is computed by the parser after it has handed every frame (`Reader.Hash` after `Reader.Frames`), so in B it runs while the workers decode the last frames.

### WavPack

- **Per block:** the entropy decoder starts from the block's own entropy variables and, in hybrid mode, its own profile (`WordDecoder.Start` copies the medians, slow levels and bitrate figures from the block's sub-blocks); the decorrelation passes take their terms, weights and samples from the block's sub-blocks (`BlockContents`, `PcmBlockDecoder.MonoPass` and `StereoPass`); a DSD block builds its tables and filters from its own data (`DsdBlockDecoder.PrepareFast` and `PrepareHigh`). The block's header CRC is over the block's own samples. `decoders.md` records it in "As built in step 3: WavPack": "a block's entropy and decorrelation state is the block's own".
- **What crosses blocks:**
  - **Sets:** the blocks of one set, from the initial flag to the final flag, carry the channels of the same samples. Each block still decodes alone; the set's planes are assembled from the decoded blocks (`StreamDecoder.Keep`) and its samples go to the sink and the MD5 on the final block (`Complete`). A block is muted when it holds fewer samples than the first block of its set (`isMuted` in `StreamDecoder.Block`), which the set's first block decides.
  - **The stream's facts,** settled by the first blocks as libwavpack settles them when it opens a file (`StreamDecoder.Settle`): the channel info and sample rate of the first blocks that carry them, the qualify mode up to the first block with audio, whether the stream is DSD or floating point and its bytes per sample from the first block with audio, and the last stored MD5. Reading a block's sub-blocks depends on whether the channel info is known (`BlockContents.Read`'s third argument).
  - The MD5 over the whole stream in its own layout (`AudioDigest`), the CRC tallies, and the sink.
  - **Set state that depends on whether earlier blocks decoded.** `StreamDecoder.Block` adds a block's channels to `_setChannels` only after its decoding returned; where the PCM or DSD decoding throws (the `catch` that sets `_isSetFailed`), the block's channels are not added. The overflow check (`_setChannels + blockChannels > _channels`) and the completion test (`!header.IsFinal && _setBlocks < maxStreams && _setChannels < _channels`) read it, and so does `Complete`. A plan made ahead, while earlier blocks are still on workers, can only assume that they decoded.
    - Example: a 5.1 set of three stereo blocks, where block 1 is damaged and block 3 lacks the final flag. A serial run has `_setChannels` = 4 after block 3, so the set stays open and block 4 is decoded as part of it. A plan that counted block 1 would see 6 of 6 channels at block 3, complete the set there, and skip block 4 (a block that does not start a set while no set is open).
    - The design answers with the replan (below): the plan assumes every earlier block decoded, and at the replay `Replan` reruns the set logic over the state the committed blocks left and compares. Serial reports stay as they are. The alternative, counting a failed block's channels, would change serial reports and goldens (decision 8).
- **Boundaries:** each block's 32-byte header states its length, and the parser reads block by block (`Reader.Block`, `DecodeFeed.Offer`). The bytes are in a buffer the parser reuses for every block, so they are valid only during the call.

### FLAC

- **Per frame:** every subframe starts with its warm-up samples, stored unencoded, as many as the predictor's order (RFC 9639, sections 9.2.5 and 9.2.6), so prediction never reaches into the frame before. The channel assignment, the wasted bits and the block size are the frame's own (section 9.1). The decoder reconstructs from the planes the parser's walk filled (`FrameDecoder.Restore`, `Combine`) and keeps nothing of one frame for the next.
- **What crosses frames:** the STREAMINFO MD5 over every sample in stream order in the canonical layout (section 8.2; `FrameDecoder._md5`), the flag of a frame not decoded (`_isIncomplete`), and the sink.
- **Boundaries:** a frame states its length nowhere; the parser's walk reads every subframe to its end to find where the frame ends, and the CRC-16 confirms it (`decoders.md`, "Who owns the bits"). The walk is therefore the serial producer of FLAC frames, and it is half of a `--verify` run. The frame's bytes are a slice of the parser's sliding window (`Source.Window`, `RawReader`), valid only during the call.

### TrueHD and MLP

- **State that crosses access units and restart headers:** the filters' histories, "updated for every sample, also while an order is 0, and continue across blocks, units and restart headers", which a restart header does not reset ([truehd-format.md](truehd-format.md), "Values a restart header resets" and "Prediction filters"); the noise generator, which "continues from unit to unit and restarts from the seed at each restart header"; and the lossless check accumulator, which covers the output since the previous restart header.
- So neither an access unit nor a restart interval decodes alone. B gives TrueHD and MLP nothing; option A (the decoder on a thread of its own, overlapped with the walk) is what would gain, about a third of `--verify` by the speed pass's estimate.

## The contract changes

### Plan, frame decoding and commit

A decoder opts in through its stream decoder. The serial contract (`IFrameDecoder.Decode`) stays for the decoders that do not.

```csharp
namespace Encspect.Core;

// The IFrameDecoder of a coding whose frames decode apart from each other. The host runs every frame as Plan, then
// IFrameWorker.Decode, then (before the journal is replayed) Replan, then Commit; at a degree of 1 inline at the hand-off,
// otherwise the middle step on a worker. The host never calls IFrameDecoder.Decode on such a decoder.
public interface IParallelFrameDecoder : IFrameDecoder
{
    // The state of one worker: predictors, filters, tables. Called on the analysis thread; the host runs one batch on a
    // worker at a time, and a worker may decode any frame of the stream. Large buffers are not the worker's: it rents them
    // from the frame's context (FrameContext.Rent).
    IFrameWorker CreateWorker();

    // On the analysis thread, in stream order, when the frame is handed: what the frame needs of the stream's state
    // (WavPack's settled facts and the set the block belongs to, Monkey's Audio's interim mode), as an object the frame
    // carries to its worker. Where that state depends on frames still in flight, the plan is a guess: it assumes they
    // decoded and left the state a serial run usually has (Replan checks). It may charge work and add findings through the
    // stream's DecodeContext; the host records them at the head of the frame's journal. An exception it throws is the
    // frame's, reported at the frame's replay; no worker runs for that frame.
    object? Plan(in CodedFrame frame);

    // On the analysis thread, in stream order, after every earlier frame has been committed and before this frame's journal
    // is replayed. Returns null where the plan still holds: the plan the decoder would make now, from the state the commits
    // left, equals the one the frame carries. Otherwise returns the plan to decode the frame with, and the host drops the
    // frame's journal (its head included), decodes the frame inline with the returned plan and replays that journal in its
    // place. Like Plan it may charge and add findings, which go to the head of the new journal, and an exception it throws
    // is the frame's. The decoder also sets its planning copy of the state (below) to what it now holds, so frames planned
    // after this one start right. A frame decoded at the hand-off is not replanned: its plan was made on committed state.
    object? Replan(in CodedFrame frame, object? plan);

    // On the analysis thread, in stream order, for each PCM block the frame's worker wrote, in the order it wrote them:
    // what the stream does with its PCM (the sink, FLAC's MD5, WavPack's set assembly). Called before the frame's Commit.
    void Output(in CodedFrame frame, PcmBlock block);

    // On the analysis thread, in stream order, after the frame's journal has been replayed: takes the frame's result into
    // the stream's state (tallies, set assembly, the MD5 over a WavPack set) and returns the samples per channel by which
    // the stream's output advanced, as IFrameDecoder.Decode returns them today. result is null where the frame threw.
    // Commit may throw InvalidDataException or EndOfStreamException (WavPack's Complete does), which the host reports as
    // it reports the same exceptions from Decode.
    int Commit(in CodedFrame frame, object? plan, object? result);

    // An upper bound of the memory the frame's decoding rents on a worker (Monkey's Audio: the coded data in stream order and,
    // for a 24-bit frame held for its CRC, the held samples; WavPack: the block's decoded samples). The host counts it in the
    // memory bound while the frame is in flight.
    long ScratchBytes(in CodedFrame frame, object? plan);
}

// What one worker decodes with. One per running batch; never two calls at once.
public interface IFrameWorker
{
    // Decodes one frame. Reads only the frame, its plan, the setup and the worker's own state, so its result does not
    // depend on which worker runs it or on what the worker decoded before. Reports through context; returns the result
    // the commit takes (the frame CRC it computed, a WavPack block's CRC outcome). It never reads a cancellation token:
    // it looks at cancellation only in context.Charge.
    object? Decode(in CodedFrame frame, object? plan, FrameContext context);
}

// The context of one frame on a worker: its journal. Every call is recorded and replayed in stream order at the commit;
// at a degree of 1 the host replays each call at once. There is no cancellation token on it.
public sealed class FrameContext
{
    public DecodeSetup Setup { get; }

    // The lent buffers the parser's walk filled for this frame (CodedFrame.IsWalked); empty otherwise.
    public SampleBuffers LentBuffers { get; }

    // False where the analysis gives the stream's PCM to no one (DecodeOptions.Output is null and no host-side PCM
    // analysis runs). A worker then writes only what its own stream checksum needs (FLAC's MD5), and Monkey's Audio
    // nothing. Known without asking DecodeOptions.Output, which the commit still asks at the first block, as today.
    public bool IsOutputWanted { get; }

    // As DecodeContext.AddWork: charges the work before it is done. Never checks cancellation.
    public void AddWork(long units);

    // AddWork followed by a look at cancellation: a charge point. Throws OperationCanceledException when the frame's
    // cancellation (the analysis, the front end's stop, a sink failure, or the budget, see "The work budget") has fired.
    // units may be 0, for a check after work that can throw an InvalidDataException before its first check.
    public void Charge(long units);

    public void AddFinding(Severity severity, string code, string message, Node? node = null, int count = 1);

    // A block of the frame's PCM, in stream order within the frame; the host copies it into the journal (or, at a degree
    // of 1, passes it straight to IParallelFrameDecoder.Output).
    public void Write(PcmBlock block);

    // A scratch buffer from the session's pool, counted in the memory bound through ScratchBytes, valid until Decode returns.
    // Not cleared: the worker resets what it reads before it reads it.
    public Span<T> Rent<T>(int count) where T : unmanaged;
}
```

- **Object, not generic:** the host moves plans and results without knowing their types. `Core.Abstractions` adds an abstract base class `ParallelFrameDecoder<TPlan, TResult>` that implements the interface with typed methods, so a decoder casts nowhere.
- **The base class also implements `IFrameDecoder.Decode`,** as plan, decoding on a worker of its own, and commit, with every call passed on at once. A decoder therefore has no dead member, and a test or another host that only knows `IFrameDecoder` gets the same behavior as a degree of 1.
- **Why plan, replan and commit:**
  - The plan is where the stream's ordered facts are read before a frame leaves the analysis thread, so a worker never reads the stream's state.
  - The replan is where a guess is checked against the state the earlier commits left, before anything of the frame is delivered. Without it the check would come after the replay, and a frame decoded twice would charge twice, double-deliver PCM to the sink and count its findings twice.
  - The commit is where the stream's ordered state is written.
- **A plan leaves no trace by itself:** a frame can be planned and then dropped (it was handed after the session ended, see below), so what `Finish` and the report read is only what `Commit` wrote. A decoder whose plan advances state (WavPack's settled facts and set position) keeps a **planning copy** of it, which the plans advance, apart from the one the commits advance. Otherwise a block with the MD5 sub-block, planned after the budget ended the session, would give the stream a stored MD5 that a serial run never saw (and with it `wavpack.md5-set-too-large`). `Replan` sets the planning copy to the committed state advanced by the frame it just checked.
- **What each decoder puts where:**

| Decoder | Plan (analysis thread) | Frame decoding (worker) | Replan (analysis thread, before the replay) | Output and Commit (analysis thread) |
|---|---|---|---|---|
| Monkey's Audio | the frame's special codes and mode; the interim mode of the stream as the plan's guess | `DecodeBody` with the frame's CRC, and the interim retry of `DecodeHeld`; writes its chunks; returns the mode the frame ended in | the plan's interim mode against the mode the committed frames left | the sink; the CRC tally and `monkeys-audio.frame-crc-mismatch`; the carried mode |
| WavPack | the header, `BlockContents.Read` with the channel info as known, `Settle`, the set the block belongs to (assuming the earlier blocks of the set decoded), its sample count, the mute decision | the charges and `PcmBlockDecoder.Decode` or `DsdBlockDecoder.Decode`; writes the block's samples through the journal; returns the CRC outcome | the set logic (overflow, position in the set, completion) over `_inSet`, `_setChannels`, `_setBlocks` and `_isSetFailed` of the committed blocks, against the plan's | the CRC and extra CRC tallies and their findings, `Keep` (from the journal's block), `Complete` (sink, `AudioDigest`), `_isSetFailed` and the set's channels |
| FLAC | the header checks of `FrameDecoder.Decode` (channels, bits, the ceiling) | the charges, the walk where the frame was not walked, `Restore`, `Combine`; writes the frame's block | none: the plan depends on the frame and the setup only, so it always holds | the sink; the canonical bytes into the MD5 |

### Per-worker state

- **Today:** "One parser or decoder instance serves every analysis and thread: keep no per-call state in fields" (`CLAUDE.md`), and the state of one stream lives in the `IFrameDecoder` that `Open` returns, called "on one thread, frame by frame".
- **Changed:** a stream's decoding has one `IParallelFrameDecoder` (the stream's state, plan, replan and commit, used by one thread at a time, in stream order) and up to the degree of parallelism `IFrameWorker`s. The `IDecoder` instance still keeps nothing in fields and still serves every analysis and thread.
- **One worker per running batch:** the session keeps a pool of at most the degree's workers. A batch takes a worker when it starts to run (the analysis thread's helping batch included) and returns it when it ends; a worker is never in two batches at once, and is not tied to a thread.
- **The rule a worker keeps:** its result depends only on the frame, the plan and the setup. A worker may keep objects (Monkey's Audio's predictors and filters, WavPack's `WordDecoder`, the DSD tables) from frame to frame, but resets what it reads before it reads it, as `DecodeBody` flushes its predictors today. The shuffle test (below) holds every decoder to it.
- **What moves out of today's stream decoders:**
  - Monkey's Audio: `RangeDecoder`, `ChannelState`, the predictors (made for the plan's interim mode), `Unprepare`, `_values`, `_samples`, `_pcm` go to the worker; `_stream` and `_held` become rented buffers; the tallies, the sink and the stream's interim mode stay.
  - WavPack: `PcmBlockDecoder`, `DsdBlockDecoder`, `BlockContents` (one for the plan, one per worker) go to the worker; the muted buffer and the decoders' sample buffers become rented buffers; the settled facts, the set planes, `AudioDigest` and the tallies stay.
  - FLAC: `_narrow`, `_wide`, `_output` become rented buffers; `_md5`, `_bytes`, `_isIncomplete` and the sink stay.

### Lifetimes: frame bytes and lent buffers

- **`CodedFrame.Bytes`** is "valid only during the call" today, and the FLAC and WavPack parsers hand slices of a buffer they reuse. At a degree above 1 the host copies the frame's bytes into a buffer the frame owns until its commit, as a `MappedBytes` over the copy that keeps everything the original carries: its ranges (so findings still point to the file, also for a packet that spans Ogg pages or MP4 chunks), `IsOpaque` and the file bounds of an opaque one, `FollowsGap`, `EndPosition`, `EndPositionField` and `IsEndOfStream`.
- **No copy where none is needed:**
  - At a degree of 1 and for a frame the analysis thread decodes at the hand-off, nothing outlives the call.
  - A parser whose frames are arrays of their own that it never reuses or writes again (Monkey's Audio) sets `CodedFrame.IsOwned`, a new optional flag, and the host keeps the array.
- **The cost of a copy** at memory speed is about 10 ms per 100 MB (estimated); the prototype copied every frame, and its single-worker runs were no slower than serial ones for Monkey's Audio and WavPack.
- **The parsers keep the contract they have:** the bytes and the lent buffers are theirs only during `Decode`; the only addition is the optional `IsOwned`.
- **Lent buffers** (FLAC; TrueHD stays serial): `IDecodeSession.Lend` lends "the session's own [buffers], grown to the largest frame seen", valid until the next `Lend`. With frames in flight:
  - the session keeps a pool of lent buffers, one for each frame in flight plus the one being walked;
  - `Lend` takes a free one and, where none is free, commits frames until one is;
  - the frame handed next owns it, and its commit returns it to the pool;
  - one lent for a walk whose frame is never handed (a candidate the parser rejects) returns at the next `Lend`.
- **The comment of `SampleBuffers`** ("valid until the next Lend") becomes "valid until the frame is handed over (`Decode`); the parser does not touch them after". The FLAC parser already never reads them after `Decode` (`FrameWalker.Walk` writes them, `State.AddFrame` reads the walk's facts, not the lent buffers). The 2^24-sample limit of a lend (`DecodeSession.MaxLentSamples`) stays, per set of lent buffers.
- **The journal's PCM:** a worker's `Write` is copied into the journal, in buffers the host reuses from frame to frame; `PcmBlock` stays a `ref struct` valid only during the call, for the worker and for the sink alike. WavPack's decoded block samples go the same way (they are valid in its decoders only until the next `Decode`), so a worker never hands the commit a span into its own buffer.

### Ordered completion: the journal and the commit

- **The commit runs on the analysis thread, in stream order:** when the parser hands a frame (`IDecodeSession.Decode`) and the window is full or its oldest frame is done, and when the session ends (`Finish`, `EndUnfinished`; see [Cancellation and stop](#cancellation-and-stop)). `Finish` commits every frame before it computes `DecodeEnd`.
- **The turn of the oldest frame,** in this order:
  1. **Replan.** The host calls `Replan(frame, plan)`. A non-null result is the plan the frame must be decoded with: the frame's journal is dropped, its head included (a worker still running on it is canceled and its result ignored), the frame is decoded inline on the analysis thread as at a degree of 1, and that journal is used from step 3. Nothing of the first journal was delivered, so nothing is charged, written or counted twice.
  2. **A cancel the replay does not explain.** If the journal ends in an `OperationCanceledException` (a frame whose worker never started because its token was canceled is such a journal), the host checks, before replaying anything, whether the replay explains it: the analysis's token, the front end's stop or a sink failure is set, or the replayed count plus the journal's charges up to the cancel passes the budget (a sum over the journal, with no side effect). If nothing explains it, the worker stopped on a total that a serial run never had (see [The work budget](#the-work-budget)); the journal is dropped and the frame is decoded inline with the same plan.
  3. **Replay.** The journal in the order the worker recorded it: charges (against the budget, below), findings (into the session's list as `DecodeContext.AddFinding` does today), PCM blocks (to `Output`).
  4. **Commit.** The host calls `Commit`, adds the samples it returns to `DecodedSamples`, and checks the floor of the charge on the totals, as `DecodeSession.Decode` does now. An exception from `Commit` is the frame's (see [A frame that throws](#a-frame-that-throws)).
- **What that keeps:**
  - Findings of one code are "reported once per stream with a count, pointing to the first, with the message of the first": the first is the first in stream order, as in a serial run.
  - The sink: `IPcmSink.Write` is called "on the analysis thread, in stream order", as its contract says; `DeclareOutput` (and with it `DecodeOptions.Output`) is called from `Output` or `Commit`, on the same thread and before the same block as today.
  - Stream-order checksums: FLAC's MD5 takes the canonical bytes of each block in `Output`; WavPack's `AudioDigest` takes each set in `Commit`.
- **The carried state of Monkey's Audio:**
  - The plan records the interim mode the stream is in when the frame is handed (normal for almost every file), the worker decodes with it, and the worker returns the mode the frame ended in; `Commit` makes that the stream's mode.
  - `Replan` compares the plan's mode with the mode the committed frames left. Where they differ, the frame is decoded again inline with the right mode (step 1). A serial run decodes the frame once with the right mode, so the result is the same.
  - Only frames in flight when a frame changes the mode are decoded twice (once on a worker, wasted, once inline); the frames handed after that commit are planned with the new mode. That is a cost for the few files that change the mode, bounded by the window.
- **The carried state of WavPack:** the plan assumes that the earlier blocks of the set decoded. `Replan` reruns the set logic over the state of the committed blocks, which counts only the blocks that decoded, and compares (see [What crosses blocks](#wavpack)). Only a set with a failed block is decoded twice.
- **Counts taken at the hand-off, in stream order:** a serial session counts an unreadable frame (`IsUnreadable`: `_framesNotDecoded`, `_isAfterUnreadable`) and a gap (`FollowsGap`: `_sawGap`) when the parser hands them, and `DecodeEnd` reads both. With frames in flight they go into the queue as entries without a worker and are applied when their turn comes, in stream order. Otherwise a frame handed after the session ended would be counted, which a serial session never received.
- **Frames handed after the session ended:** the parser learns of the end through `IsDecoding` and `Decode`'s return value, which at a degree above 1 come a few frames late (the frame that crossed the budget is committed after later frames were handed). Those frames are dropped at the commit without a count, as a serial session never received them: `FramesNotDecoded` and the findings stay as serial. This needs one new rule for parsers (below).
- **The sessions of one analysis:**
  - **The invariant:** a session's frames are all committed before another session of the same analysis hands a frame or starts decoding. The budget is one count for the analysis, it is sticky once crossed, and it grows when a session starts (`DecodeRun.AllowPerInputByte`, at `StartDecoding`), so the replays of two sessions must happen in the order a serial run hands the frames.
  - **How it is kept:** the host drains the session that has frames in flight before it takes a frame of another session or starts one, and asserts it (a debug assertion, and a test with two scripted sessions that hand frames in turns).
  - **What it costs:** two sessions that interleave their frames (two tracks of a Matroska file) do not overlap. A run-wide ordered queue would lift that; it is not part of this design.

### The work budget

- **Today:** `DecodeRun` adds every charge to one count for the analysis (`DecodeRun.AddWork`), cancels the decoding's token as soon as the count passes the budget, and the session picks `decode.limit` from the host's state. A decoder that checks its token inside a frame (Monkey's Audio after each chunk's charge; WavPack in `ReadWords` and the DSD loops, every 4,096 samples, the first time before any output) stops there and the frame counts as not decoded; a decoder that does not (FLAC) finishes the frame it charged.
- **The rule that keeps it deterministic:** the budget is applied in stream order, at the replay. The count of the analysis is the count of what was replayed, so the frame and the charge at which the budget is crossed are the same as in a serial run, whatever the threads did.
- **The replay of a crossing:** at the first replayed charge that passes the budget, the host cancels the decoding's token (so the other sessions stop as now) and goes on through the frame's journal to the first charge point (`Charge`) at or after the crossing. A serial run's decoder throws there, so the replay keeps the PCM and findings before it, drops the rest, and counts the frame as not decoded. Where no charge point follows, a serial run finishes the frame (FLAC), and so does the replay. Frames after it are dropped. Then the session ends with `decode.limit` as today.
- **AddWork before the work:** unchanged for the decoder, which charges on its worker before it does the work. The host's count follows the replay, after the work.

#### Charge points

For the replay to know where a serial run would stop, a worker looks at cancellation only right after a charge. `FrameContext.AddWork` never checks; `FrameContext.Charge` is the pair, and `Charge(0)` is a bare check.

- **Monkey's Audio:** already charges and checks per chunk in that order (`DecodeBody`); each pair becomes one `Charge`.
- **WavPack, PCM:**
  - The block's charges stay up front as `AddWork`, with the same amounts in the same order, so the totals do not change.
  - The check goes where the first one is today, in front of the word loop, and only for a block with samples (the loop runs no iteration for none, and checks nothing then).
  - The later checks, every 4,096 samples, go. All of a block's charges are made before its first check, so the budget can fire only at the first one; a block of at most about 1.1 × 10^8 units is under the 2^27 units the contract allows between checks.
  - The largest PCM block charges about 1.1 × 10^8 units with 16 decorrelation passes: 62 × 2 × 196,607 for a floating-point sample, 11 × 16 × 2 × 196,607 for the passes and 16 × 2^20 for the bitstream (the constants of `StreamDecoder.Charge`; estimated, to be recomputed by the WavPack pull request, as is the DSD figure). That replaces an earlier figure of 5.9 × 10^7.
- **WavPack, DSD:** `DsdBlockDecoder.Prepare` can throw `InvalidDataException` (the table checks of `PrepareFast` and `PrepareHigh`) before the loops' first check, and the block then counts as invalid, not as canceled.
  - Modes 1 and 3: the `AddWork` calls stay up front, then `Prepare`, then `Charge(0)`, then the loops without checks. A crossed budget and a bad table give `decode.frame-invalid`, as in a serial run.
  - Mode 0 (stored) and muted blocks: plain `AddWork` and no check, since a serial run checks nothing there.
- **FLAC:** charges three times and checks nothing, which is a frame without a charge point. The charges are the fixed work of the samples, 16 a byte for a frame it reads itself, and, after its walk, the predictors' taps (`FrameDecoder.Decode`).

#### The worker's cancellation, and what it may not decide

- **When a worker stops:** a worker cannot wait for the replay, so its cancellation fires as soon as the budget is certainly crossed before its own position. That is when the replayed count, plus what the frames before it in stream order have charged so far, plus what it has charged itself, passes the budget.
- **For a frame whose plan held,** with every frame before it planned right, that sum only grows and never exceeds what a serial run had charged to that point. The worker therefore stops no earlier than a serial run would, and may stop later (a frame after the serial crossing is dropped whatever it did). The replay cuts its journal at the serial point.
- **After a misplanned frame** the sum can be too large: the frame charged for a decoding that is dropped, in the interim case about twice the serial work. The worker of the next frame then reads an inflated total and throws `OperationCanceledException` where a serial run goes on. Nothing the replay knows explains it, and it would be reported as `decode.failed`.
- **The replay therefore never relies on a worker's early cancel.** A journal that ends in a cancel it does not explain is dropped and the frame is decoded inline (step 2 of [the turn of the oldest frame](#ordered-completion-the-journal-and-the-commit)); the inline decoding charges against the replayed count and stops where a serial run stops. The "never earlier than serial" property is stated only for frames whose plan held, and it serves efficiency (few inline repeats), not correctness.

#### The bound on wasted work

- **What a worker can waste:** the frames in flight run ahead of the replay. A frame that charges in chunks keeps going on speculative work while the sum it reads stays under the budget, and the sum stays low when the frames before it have hardly begun. So the work done past the point where a serial run stops is about the window's frames times the smaller of one frame's work and the budget that remained when the frame started, not one charge. One Monkey's Audio frame can be about 4.5 × 10^11 units (10,000,000 samples of 32 channels; estimated).
- **What holds:**
  - **Wall time.** A worker stops by itself at its next charge point once the sum passes the budget, and its own charges are part of the sum, so no worker runs longer than a serial run takes to reach the budget. The workers run side by side, so a crafted file stops within about the time a serial run needs to cross the same budget.
  - **CPU time** may be up to the number of workers running times that.
- **The alternative** is to limit the window by planned work, at most the degree times 2^27 units in flight, which would make the work wasted past the budget small. It would also run every Monkey's Audio frame of the higher levels inline (a 16-bit insane frame is about 10^9 units; estimated from its 0.8 s decode), which removes the format's gain. Decision 10 asks for the choice; this document assumes the restated bound.
- **Accounting across threads:** the charges of a worker go into its frame's journal (one thread writes it) and into a per-frame running total that the workers of later frames read with `Volatile.Read` (a sum over the window, at each charge point); `DecodeRun`'s count stays on the analysis thread and is advanced only by the replay. Nothing else is shared between threads but the cancellation.
- **What `decode.limit` means:** unchanged: "Decoding stopped after N frames: its work passed the budget of B work units that the file's size allows", with N and B as a serial run gives them.
- **The floor of the charge** (`DecodeSession.CheckFloorOfCharge`) is checked at each commit on the replayed totals: the same check at the same frame.
- **`MaxWork`, `MinimumWork`, `WorkPerInputByte`:** unchanged.

### Cancellation and stop

- **Analysis canceled** (`AnalysisOptions.CancellationToken`): the workers' cancellation is linked to it, so they stop at their next charge point, and a batch that has not started does not start. The analysis thread throws `OperationCanceledException` at its next hand-off or commit, as today. Nothing is committed: the analysis returns no report.
- **Joining the workers:** before `Analyze` returns or throws, every worker is canceled and joined, so no worker runs past `Analyze`, touches a sink, or holds the linked token source that `Analyze` disposes (`using DecodeRun`).
  - A codec route has a `finally` (`ParserRouter.RunCodec` calls `EndOpenSessions`), which ends its sessions.
  - The format-parser path (`Analyzer.cs`, after `parser.Parse`) has no `finally`: `router.EndOpenSessions` runs only when `Parse` returns or throws what the `catch` blocks take. The join therefore lives in `DecodeRun.Dispose`, which every path reaches, and the sessions' own end only commits.
- **A parser that stops early** (`parse.truncated`, `parse.invalid`, `parse.failed`): a serial run decoded every frame the parser handed before it stopped, so `EndUnfinished` (through `EndOpenSessions`) commits the frames in flight, in stream order, as `Finish` does, and then closes the session. The commits can cross the budget or fail like any others. On the analysis's own `OperationCanceledException` it does not commit: it cancels and joins.
- **Stop** (`DecodeOptions.Stop`): the workers stop at their next charge point. The session commits the frames in stream order up to the first one that did not complete.
  - **A frame canceled part-way** counts as not decoded, as a serial run counts a frame canceled inside it; the PCM and findings it recorded before the cancel are replayed.
  - **Frames never started, or handed after,** are dropped and not counted, as a serial session never received them.
  - The session ends with `decode.stopped`, "Decoding was stopped after N frames", counting the committed frames that decoded. A serial run's N depends on when Stop comes, and so does this one's; what holds is that the report is one a serial run stopped at some point would give. A Stop raised by the sink at a given write is deterministic, since the sink is called at the replay (see Verification).
- **A sink that throws:** the sink is called only from the commit, on the analysis thread, so the host's `GuardedSink` catches it as today, cancels the token, and `Analyze` rethrows it once the format parser has returned. The workers stop at their next charge point and are joined.
- **Latency:** a worker checks at least every 2^27 units (the contract's rule for a frame charged once, about 80 ms), and the analysis thread checks between frames as today, so Stop and cancellation are as quick as now.

### A frame that throws

- **On a worker, in the plan or in the replan,** the exception is caught and recorded in the frame's journal, with the journal up to the throw; nothing is reported at that moment.
- **At the replay,** the host reports it as `DecodeSession.Decode` does today, on that frame:
  - `InvalidDataException` and `EndOfStreamException` are `decode.frame-invalid` on the frame's node ("The monkeys-audio decoder could not decode the frame at 0x…"); the frame counts as not decoded and decoding carries on with the next frame;
  - an `OperationCanceledException` that the host's state explains is a frame not decoded;
  - one that nothing explains is first decoded again inline (step 2 above), and only if the inline decoding throws one that the host's state does not explain is it `decode.failed`, as a serial run reports it;
  - anything else is `decode.failed`, and the frames after it are dropped, since a serial session ends there.
- **`Commit` may throw too:** WavPack's `Complete` throws `InvalidDataException` when a final block closes a set with fewer channels than the stream has. The host reports it as `decode.frame-invalid` on that frame and adds nothing to `DecodedSamples`, as today (the sum is added after `Decode` returns). The frame's journal was replayed before, as the events of a serial frame that throws late are.
- **The decoder's state after a failure:** what the serial code does in its `catch` (WavPack's `_isSetFailed`, Monkey's Audio's `LeaveInterim`) moves to the commit, which learns of the failure through `result` (null with the frame's exception recorded); the worker's own state needs nothing, since it resets before each frame.
- **Why at the replay:** the prototype reported a frame's exception from a later call, the way a decorator of today's contract has to, and five damaged Monkey's Audio fixtures came out `decode.failed` instead of `decode.frame-invalid` (see [Expected gains](#expected-gains)). Only the host knows which frame a journal belongs to.

### Memory bounds

- **What is in flight at a degree above 1:** at most the window's frames. Each holds:
  - its coded bytes (the host's copy, or the parser's own array for a frame with `IsOwned`);
  - its journal: PCM and result blocks (only where `IsOutputWanted`, or where the decoder's own checksum needs them: FLAC, WavPack);
  - lent buffers where it was walked (FLAC);
  - the scratch its decoding rents (`ScratchBytes`).
- **Scratch was outside the bound in the first draft.** Today's `IFrameDecoder` buffers would move to the workers one for one, and several of them are large:
  - Monkey's Audio `_held` takes up to 2^23 samples (32 MiB) for a 24-bit frame held for its CRC;
  - `_stream` (the coded data in stream order) is as long as the largest frame and grows by doubling without shrinking (`FrameDecoder.Decode`), up to 64 MiB;
  - at a degree of 8 that is about 770 MiB of scratch against about 96 MiB serial (estimated, 8 × (32 + 64) MiB);
  - WavPack's decoded samples (`_pcm.Samples`) are valid only until the worker's next `Decode`.
- **The scratch rule:** a worker keeps small state only (predictors, filters, tables). Large buffers are rented per frame from the session's pool (`FrameContext.Rent`), are counted in the bound through `ScratchBytes` while the frame is in flight, and return to the pool when its decoding returns. The pool frees what exceeds a few MiB when the window drains, so a buffer sized for one large frame is not kept for the rest of the stream. WavPack writes its decoded samples through the journal (above). Decision 5 asks whether to count scratch like this or to give it a bound of its own.
- **Copies:** a Monkey's Audio frame is copied three times in the first draft (the parser's array, the host's copy, `_stream`) and once more into `_held` for a 24-bit frame. With `IsOwned` the host's copy goes, and a frame decoded at the hand-off is not copied either.
- **The bound:** the frames in flight hold at most 64 MiB of all of the above together (the figure of the session's lending limit, 2^24 32-bit samples), and at least one frame. A frame that would pass the bound alone is decoded at the hand-off, as at a degree of 1, after the window has drained; that is deterministic, since inline and on a worker give the same journal. The largest frames need it: Monkey's Audio frames up to 64 MiB of coded data and 10,000,000 samples of 32 channels, which stream to the sink chunk by chunk as now and use the scratch a serial run uses.
- **The window:** twice the degree of parallelism in batches, plus the batch being filled, within the bound. The analysis thread waits (by committing, or by decoding the oldest batch itself) when the window is full: that is the back-pressure, and it is what keeps memory from growing with the stream. At a degree of 1 there is no window.
- **Today's ceilings stay:** `MaxLentSamples` (per set of lent buffers), WavPack's `MaxSetSamples` (a set's planes, in the commit), Monkey's Audio's chunk of 4,096 samples and `MaxHeldSamples`, FLAC's frame ceiling.
- **Nothing grows with the stream:** the journal of a frame is dropped at its commit, buffers return to pools that are the session's own and are dropped with it; `ArrayPool<T>.Shared` stays unused, as the memory section of `decoders.md` asks.

### Determinism

- **Guaranteed, for every degree of parallelism, where the analysis is not canceled, not stopped and no sink throws:**
  - the report: the tree (decoding adds no node), the findings with their order, counts, messages and nodes, and each stream's verification (status, `DecodedSamples`, every tally);
  - the PCM a front end's sink receives: the same `DeclareOutput` format, the same blocks with the same positions, lengths and samples in the same order, and the same `End`;
  - the work the analysis counts and the frame at which it crosses the budget, so `decode.limit` and its message.
- **Where a serial run is not deterministic either** (Stop at a moment), the parallel run gives a report a serial run could give: the one stopped after the frames it committed.
- **What it rests on:**
  - a degree of 1 is the same code inline;
  - a worker's result depends only on the frame, the plan and the setup (the shuffle test);
  - every plan is checked against the committed state before its journal is replayed, and a journal that a wrong plan or an early cancel made is dropped, not replayed;
  - the journal replays in stream order, and the budget, the session's end and the frame errors are decided at the replay;
  - a session's frames are drained before another session of the analysis hands one;
  - the frames after the end are dropped without a trace.
- **Timing fields:** the reports have none (no writer of `Encspect.Host` or the CLI reads a clock), so "byte-identical" is literal.

### What parsers keep, and one new rule

- Parsers do not change for the parallel pipeline: they hand frames and lend buffers as today, and `IDecodeSession` keeps its members. The one addition is the optional `CodedFrame.IsOwned`, which the Monkey's Audio parser sets.
- **The new rule:** a parser's report does not depend on `IsDecoding` or on `Decode`'s return value beyond whether decoding was asked for and a session was started. They only let a parser skip work (building a `CodedFrame`, storing into lent buffers). At a degree above 1 they turn false a few frames later than in a serial run.
- **The one place that breaks it:** the Monkey's Audio parser reports `monkeys-audio.frame-not-decoded` (a frame over 64 MiB) and counts the frame unreadable (`UnreadableFrames`) only in `Feed`, which `Reader.Frames` calls only where `IsDecoding` is true when it reaches the frame. A parallel run would call `Feed` for a few frames that a serial run skips because decoding had ended, and report the finding on an oversized one of them.
- **Which serial reports change, if the parser is changed:**
  - Only those of a file with a frame over 64 MiB that comes after the budget or a stop ended decoding. Today such a frame gets no finding and no unreadable count; after the change it gets both. No committed fixture has one (the goldens of the Monkey's Audio pull request would confirm it), so no golden changes, but such a file's report does.
  - The change is that the parser takes `IsDecoding` once, right after `StartDecoding`, and reports the finding and the count by that, while the live `IsDecoding` still decides whether it reads and feeds the frames.
- **An alternative that keeps every serial report:** `IDecodeSession` gets one member that the parser calls only where it reaches a frame over 64 MiB. The host commits the frames in flight and answers as a serial run would. It costs a drain at such a frame, which runs inline anyway (it passes the memory bound), and one more member of the session interface. Decision 11 asks the owner to choose.
- The FLAC parser (lending and building frames only while decoding), the WavPack feed (`Hand`) and the TrueHD walk (lending) only skip work.

### The rules in CLAUDE.md

- "One parser or decoder instance serves every analysis and thread: keep no per-call state in fields." stays as it is.
- After it, a sentence is added: "A decoder that decodes frames in parallel keeps the stream's state in its stream decoder, which one thread at a time uses, in stream order, and gives each worker its own state, which resets what it reads before it reads it; a frame's result depends only on the frame, its plan and the stream's setup (`docs/design/parallel-decoding.md`)." It does not say "only on the analysis thread", which option C would contradict.
- "Loops that only compute over every frame or packet check `ParseContext.CancellationToken` (a decoder's, `DecodeContext.CancellationToken`) every few thousand iterations; reads check it already." becomes "... (a decoder's, `DecodeContext.CancellationToken`, and a frame worker's only at its charge points, `FrameContext.Charge`) ...", since a worker that checks between charges cannot be replayed where a serial run stops.
  - The amendment is safe only because `FrameContext` has no token (a worker cannot check between charges) and because every charge stays at most 2^27 units, so a charge point comes at least every 80 ms. A test holds the first (see Verification).
- "A decoder charges its work through `DecodeContext.AddWork` before it does it, ..." stays; `FrameContext.AddWork` and `Charge` are the same charge on a worker.
- The exception rule ("Parsers and decoders throw only ...") stays; `decode.failed` is still reported on the frame's stream, at the frame where a serial run reports it.

## Scheduling

### The degree of parallelism

- **A host option of the call:** `DecodeOptions.MaxParallelism` (an `int?`), the most frames of one analysis decoded at once, the analysis thread included: a degree of D runs D - 1 workers, and the analysis thread decodes a batch itself whenever it would otherwise wait (below). Null or 1 is decoding at the hand-off, as today: no copy of the bytes, no window, no batches. Like `MaxWork`, it is an option of the call and not a setting; the front ends choose it.
- **Why the analysis thread counts:** it is busy for WavPack (parsing, plans, the MD5) and idle for Monkey's Audio. Counting it and letting it help gave, in the prototype, within about 7% of the better of three and four workers on every file, where a fixed number of workers was best for one format and up to 15% behind for the other (see [Expected gains](#expected-gains)).
- **The host resolves it to 1** where the decoder is not an `IParallelFrameDecoder`, where the platform has no threads (below), and for a stream whose frames are each larger than the memory bound.
- **Per analysis, not per session:** the sessions of one analysis (a Matroska file with two FLAC tracks, later) share the degree, so an analysis never decodes more than the degree's frames at once. Only one session has frames in flight at a time (the invariant under [Ordered completion](#ordered-completion-the-journal-and-the-commit)), so the sessions share the workers one after the other.

### With the GUI and the CLI

- **CLI:** analyzes one file at a time (`CommandLine`, `InputWalker`), so it passes a degree from `Environment.ProcessorCount` with `--verify` (decision 2 below), and 1 without decoding, where nothing is decoded anyway.
- **GUI:** the file list analyzes `Parallelism` files at once (`MainViewModel`: `Math.Clamp(Environment.ProcessorCount / 2, 1, 4)`), and never decodes. "Verify Audio" analyzes one file in the detail window (`DetailViewModel`, `new DecodeOptions()`), which may run while the list's background analysis is busy. The GUI passes `Environment.ProcessorCount` minus the list's `Parallelism` while the list is analyzing, and `Environment.ProcessorCount` otherwise, at least 1 (decided when the analysis starts). It is capped at 8 like the CLI's (decision 2), since both run in the range this design measured and in none beyond it.
- **A library caller** gets serial decoding unless it asks; a server that analyzes many files at once keeps 1, since its files are its parallelism.

### Threads, batches and back-pressure

- **The thread pool, not dedicated threads:**
  - The GUI already runs its analyses on the pool (`Task.Run`, `Parallel.ForEachAsync`), and a pool shared by both levels lets the runtime balance them; dedicated threads would add their own to the GUI's and cost a thread start per analysis, which a folder of small files with `--verify` would pay per file.
  - The session limits its own concurrency: at most the degree of batches run at once, queued in stream order to the pool's global queue (`TaskCreationOptions.PreferFairness`), so the oldest frames run first, which is what an in-order commit wants. The pool's work stealing between its threads stays as it is; a session does no stealing of its own.
  - **The analysis thread helps:** where it has to wait for the oldest frame (the window is full, or `Finish`), it decodes the oldest batch no worker has started instead of waiting; a batch is claimed by whichever thread takes it first (one atomic exchange). At a degree of 1 there are no batches: the frame is decoded at the hand-off.
  - NativeAOT supports the pool and tasks; nothing here uses reflection or dynamic code.
- **Batches:** a task decodes a run of consecutive frames, closed once it holds at least 2^16 samples over all channels. Batches do not align with WavPack sets: each block decodes alone and the plan carries the set position, so a set may span batches. A FLAC frame of 4,096 stereo samples decodes in about 0.1 ms, so one task per frame would spend a measurable share in scheduling; a Monkey's Audio frame (73,728 to 1,179,648 samples) or a WavPack block (11,025 to 24,000 samples in the benchmark files) is a task of its own. The figure is to be tuned by the first pull request on the benchmark files.
- **Back-pressure:** the window (twice the degree in batches) and the memory bound: `Decode` and `Lend` return only once the frame fits, by committing finished frames or decoding the oldest one. The parser therefore runs at most a window ahead of the commit.

### Without threads

- Browser WebAssembly without its threads feature has no thread pool to run work on, and a blocking wait there throws (**unverified** for .NET 10's browser runtime; it is the documented behavior of the single-threaded runtime of .NET 8). The host resolves the degree to 1 there (`OperatingSystem.IsBrowser()` and `OperatingSystem.IsWasi()`), and at a degree of 1 nothing is queued and nothing waits: plan, frame decoding and commit run inline.
- So the serial path is not a fallback beside the parallel one; it is the same path with no worker, and every test that runs at a degree of 1 covers it.

## Verification

### The same report, PCM and checksums

- **`ParallelDecodingMatchTests`:** analyzes with decoding at degrees 1, 2 and 4 (and 8 in the nightly run) and compares each with the degree-1 run: the report through all three writers (`ReportRendering.All`, as `ParallelAnalysisMatchTests` compares), every stream's verification, and the PCM through a hashing sink that records each `Write`'s position, length and samples and the `End`. Each must be byte-identical. Its inputs:
  - every file of the decoder fixtures (`tests/fixtures/flac`, `wavpack`, `monkeys-audio`, `truehd`), the corrupt-file variants the `CorruptFiles` classes build from them, and the fuzz corpus (`tests/fixtures/fuzz`);
  - **the containers that hand frames differently from a bare stream:** FLAC in Ogg, in Matroska and in MP4 (packets in several file ranges), and WavPack in Matroska (`MappedBytes.Opaque`, buffers the parser reuses). They test that the host's copy keeps ranges, `IsOpaque`, `FollowsGap` and `EndPosition`; the test builds them from the elementary-stream fixtures where the repository has no file;
  - `MaxWork` lowered as the corrupt files and the fuzzer run it, so the budget crosses in many of them.
- **No test decodes on several threads today.** `ParallelAnalysisMatchTests` runs analyses of files on several threads but passes no `DecodeOptions`, so it decodes nothing; `decoders.md` said it compared parallel decoding and is corrected together with this design.
- **Budget crossings on purpose:** the same comparison at `MaxWork` of 2^20, 2^24 and 2^28 over the decoder fixtures, so the crossing falls at different frames and inside frames; and every case of `ComplexityTests` at degrees 1 and 4.
- **Misplanned frames under a budget:**
  - the interim fixtures (`external-interim-24-stereo`, the `legacy-*` fixtures) at lowered `MaxWork`, each degree, with values that cross the budget inside the frame that changes the mode and in the frame after it (the early cancel of B3);
  - WavPack sets with a failed block: the 5.1 fixture with block 1 damaged and the final flag of block 3 cleared (built by the test), at each degree, with and without a lowered `MaxWork`.
- **A host hook for orders that real timing seldom produces:** an internal hook (visible to the tests) runs batches in reversed or seeded random order with injected delays, also between the worker's finish and the replay, over the real decoders at lowered `MaxWork`. The report must equal degree 1's for every seed (a fixed set on pull requests, more in the nightly run).
- **Stop with the real decoders:**
  - A sink raises `Stop` at the last write of the Nth frame. Parallel and serial reports and PCM must be byte-identical, since the sink is called at the replay, in stream order.
  - A `Stop` raised from another thread at a random moment cannot be compared with a serial run. The test asserts what holds: the PCM delivered is a prefix of the serial run's, the stream is not complete, `decode.stopped` is present and no worker runs after `Analyze` returns.
- **The benchmark files** (300 s, outside the repository): `--json --verify` and `--csv --verify` of the JIT and NativeAOT builds at degrees 1 and 4, byte-identical, as the speed passes compared them; run by each pull request of the rollout and recorded in its notes.
- **The fuzzer** analyzes with decoding at a degree of 4 as well as 1 and fails on a difference between the two reports, besides `decode.failed`.
- **Shuffle test (`FrameWorkerPurityTests`):** for each decoder, the frames of each decoder fixture are planned in stream order, then decoded on one worker in a random order and on fresh workers, and each frame's journal must be equal; this is the rule "a frame's result depends only on the frame, its plan and the setup", tested directly.
- **No token on a worker (`FrameWorkerPurityTests` too):** a test over the compiled assemblies, in the manner of `DecodingBoundaryTests` and with a negative control, fails when code in the `Decoding` namespaces of the three formats refers to `CancellationToken` or calls `ThrowIfCancellationRequested`. The decoders then have only `FrameContext.Charge` to look at cancellation with.

### Cancellation, budget and memory under parallelism

- `DecodeRouteTests` gains a scripted parallel decoder (as `ScriptedDecoder` scripts the serial one) with frames that sleep, throw, charge across the budget and write PCM, and cases for:
  - a frame's exception reported on that frame at the replay, and a `Commit` that throws `InvalidDataException` (`decode.frame-invalid`, nothing added to `DecodedSamples`);
  - frames after a `decode.failed` dropped;
  - a crossing at a charge point and in a frame without one;
  - a plan that no longer holds at `Replan`: the first journal never reaches the sink, the budget or the findings, and the second is used;
  - a worker that cancels early on an inflated total: the frame is decoded inline and the report is the serial one, not `decode.failed`;
  - frames handed after the end dropped without a count, and unreadable frames and gaps counted in stream order;
  - Stop in the middle: a frame canceled part-way counts as not decoded, frames never started are dropped;
  - the analysis canceled while workers run, on a codec route and on the format-parser path (no worker runs after `Analyze` throws, checked with a worker that records its exit);
  - a parser that stops early (`parse.invalid` after some frames): the frames in flight are committed, equal to the serial report;
  - a sink that throws, rethrown;
  - the floor of the charge at the replay;
  - two sessions that hand frames in turns: the invariant (a session drained before the next hands a frame) holds and the budget crosses at the serial frame.
- **Wasted work bound:** a case where the budget crosses early and frames charge in chunks, among them one frame whose work is far larger than the budget. It asserts the restated bound: the units all workers charged past the serial crossing are at most the number of running workers times the smaller of one frame's work and the budget, and the wall time is not above a serial run's plus a margin. It does not assert a bound of one charge a frame.
- **Memory:** `MemoryTests` gets the decoder streams at a degree of 4 and 8: the live heap read from the last frame's sink write grows by no more than the bound (rented scratch included) plus the workers' small state, whatever the stream's length; a case with Monkey's Audio frames larger than the bound checks they run inline. `InputReleaseTests` checks that no worker, journal or lent buffers are reachable from a report.

### Thread-safety tests

- In the style of `LongListReadTests` and `ParallelAnalysisTests` (the "Thread hammers" collection, which runs alone): four analyses with decoding at a degree of 4 at once, on more threads than the machine has cores, with collections forced, each compared with its private serial report; and the GUI's case, a verify at full degree while the list analyzes `Parallelism` files.
- `ThreadTestScope` decides the size as for the other thread tests: a sample on pull requests, all long inputs in the nightly run (`ENCSPECT_FULL_THREAD_TESTS=1`).

### `DecoderWorkTests`

- It measures time per work unit on one thread: decoding time (analysis with decoding minus analysis without, interleaved with the FLAC reference) over the units charged. That is the figure the budget's bounds assume, and it stays a single-thread figure: the test runs at a degree of 1 and is otherwise unchanged.
- The budget counts work, not wall time, so parallel decoding does not change what a unit allows; it makes the wall time of a budget shorter on a machine with free cores, by up to the degree.
- Parallel speed is not asserted: the CI runners differ in cores and load (the test notes in `docs/development.md` record loaded runs that moved the reference by a factor of 2), so a speedup assertion would be noise. The nightly run prints the speedup of a degree of 4 over 1 on the Monkey's Audio and WavPack cases, so the history shows a regression.

### NativeAOT

- `scripts/check_aot.sh` compares the JIT build and the NativeAOT build over `tests/fixtures` with `--json --verify`; at the CLI's new default degree that comparison covers the parallel path in both builds. Nothing in the design needs reflection or dynamic code; `IsAotCompatible` and the analyzers keep that true.
- The vector width differs (128 bits under the NativeAOT baseline, the processor's best under the JIT, see "NativeAOT" in `docs/development.md`), which changes the time of Monkey's Audio's filters and not their output.

### CI cost

- **What the match tests add:** the decoder fixtures decode three more times on pull requests (degrees 2 and 4 on top of degree 1 in the tests that already decode them), and the parallel runs carry the prototype's CPU overhead (0.99 to 1.42 times the serial CPU for Monkey's Audio and WavPack, 1.8 to 2.1 for FLAC).
- **Parallel does not shorten a saturated runner:** the shards run test classes side by side on all cores, so a degree of 4 run adds its CPU time to the wall time. A degree-4 run on top of degree 1 about doubles the decoding CPU of the corrupt-file classes, and degrees 1, 2 and 4 about triple it. The earlier estimate, that the classes take about the same wall time at degree 4, held only for idle cores.
- **Estimated:** `CorruptMonkeysAudio*` is about 15 minutes of CPU on a loaded four-core machine (`docs/development.md`), so the corrupt variants at degrees 1 and 4 add about 5 to 10 minutes of wall time on the `corrupt-c` and `corrupt-d` shards, and about 10 to 20 with degree 2. The first pull request measures it.
- **Plan:** on pull requests the corrupt variants run at degrees 1 and 4 (a sample of them, if the first measurement says so), and the complete set at degrees 2, 4 and 8 in the nightly run.
- The thread tests add about a minute to `rest`; the benchmark comparison is local, as in the speed passes.

## Rollout

Each step is a pull request with its own review, and each decoder's step keeps its golden reports unchanged (a parallel run must give them).

1. **The contract and the host, with Monkey's Audio** (the largest gain, the simplest split):
   - `Core.Abstractions`: `IParallelFrameDecoder` (with `Replan` and `ScratchBytes`), `IFrameWorker`, `FrameContext` (no token; `Charge`, `Rent`), the `ParallelFrameDecoder<TPlan, TResult>` base class, `CodedFrame.IsOwned`, `DecodeOptions.MaxParallelism`, and the comments of `SampleBuffers` and `CodedFrame.Bytes`.
   - The host: the frame pipeline in `DecodeSession` (copy of the bytes that keeps the ranges, `IsOpaque`, `FollowsGap` and `EndPosition`; plan, batches, window, memory bound, scratch pool, replan, the check of an unexplained cancel, replay, commit; stream-order counts of unreadable frames and gaps), the pool of lent buffers, the budget replay and the workers' cancellation in `DecodeRun`, the join in `DecodeRun.Dispose`, the drain in `EndUnfinished`, the one-session-at-a-time invariant, the degree's resolution.
   - Monkey's Audio split into plan, worker, replan and commit, with the replan for interim mode and, as decision 11 settles it, the parser change or the session member for `monkeys-audio.frame-not-decoded`.
   - Tests: `ParallelDecodingMatchTests`, `FrameWorkerPurityTests`, the `DecodeRouteTests` cases, the memory and thread cases; the interim fixtures (`external-interim-24-stereo`, the `legacy-*` fixtures) are the replan's cases.
   - The CLI's degree with `--verify` and the GUI's for "Verify Audio" (or behind the owner's default, decision 1), `docs/cli.md` and `docs/gui.md`; the `CLAUDE.md` sentences; this document's "As built".
2. **WavPack:** the plan (settle, set position, mute decision), the replan of the set logic (decision 8), the block worker (its samples written through the journal), the commit (set assembly, `AudioDigest`); the checks in `ReadWords` and the DSD loops become the block's charge point (decision 9). Its fixtures cover sets of several blocks (5.1, `external-from-caf-51-md5`), DSD, hybrid files and the sets that never complete.
3. **FLAC** (decision 7): the pool of lent buffers' first user, the frame worker (restore, recombination, and possibly the canonical bytes), the MD5 in `Output`; batches of frames. Its gain is the smallest of the three (1.1 to 1.25 times measured, about 1.4 estimated), so it comes last, and only with option C, if the owner decides so.
4. **Later, separate designs:** option A for TrueHD and MLP, option C, and the CLI's file-level parallelism.

## Expected gains

### The prototype

- **What it is:** a scratch program outside the repository (in the session's scratchpad, not committed), built against `origin/main` at b656c90, using the public API only. A wrapper `IDecoder` registered through `Analyzer`'s decoder constructor opens W real stream decoders (one per worker) and hands each frame, copied, to the thread pool with W tasks at most at once (`ConcurrentExclusiveSchedulerPair`); results are committed in stream order on the analysis thread, with each frame's findings, work and PCM replayed there. For FLAC it copies the lent buffers and gives each worker the frame's copy; for FLAC and WavPack it buffers the PCM and computes an MD5 over the canonical bytes at the commit, serially, to stand for the stream-order checksum (their workers still compute their own, so the parallel figures carry that work twice and are, if anything, low). Batches of at least 16,384 samples over all channels.
- **What it is not:** the contract above. A decorator cannot attribute a frame's exception to its frame or replay the budget, so it reserved the per-sample charge at the hand-off and returned the parser's sample count there. It ignores Monkey's Audio's interim mode (no benchmark file uses it).
- **Identity:** its reports equaled the serial ones (JSON) for every benchmark file it timed with the plain configurations (W workers, `p`), and for 108 of 113 Monkey's Audio fixtures and 107 of 114 FLAC fixtures (with one frame a batch); every one that differed is damaged or has an MD5 the wrapper treats otherwise, and the Monkey's Audio five are the case the design answers in [A frame that throws](#a-frame-that-throws): a frame's `InvalidDataException`, surfacing at a later call or in `Finish`, became `decode.failed`. WavPack's fixtures were not compared, since the wrapper does not compute WavPack's MD5 in its own layout; the reports of the WavPack benchmark files, which the comparison covered, were equal.
- **The helping configurations** (`h`: the analysis thread decodes a batch while it waits) were not identity-checked in the first runs: the check compared only the `p` configurations. They were rerun with the check on 2026-10-26 (one worker and the thread, three and four workers; JSON): the reports were identical to the serial ones for the seven files rerun, which are Monkey's Audio `-c4000` and `-c5000`, WavPack default, `-hh` 16-bit and `-hh` 24-bit/96 kHz, and FLAC -5 and -8. The other benchmark files were not rerun with `h`.

### Measured

- **Machine and method:** the four-vCPU Intel Xeon at 2.8 GHz of the speed passes, Linux 6.18, .NET 10.0.401, Release, JIT with the CLI's runtime settings (`TieredPGO` off, call counting delay 20 ms). The exclusive machine lock was held and the one-minute load average was under 1.5 at each file's start. In one process per file: one warm-up of each configuration, then three rounds, each running every configuration once; the minimum wall time of the three. "None" is the analysis without decoding, "serial" the real decoders, "W" the wrapper with W workers. In-process times have no runtime start, so they are about 0.1 to 0.25 s below the CLI's.

Minimum wall time in seconds, first run (all configurations of a file in the same rounds):

| File | Frames | None | Serial | W = 1 | W = 2 | W = 3 | W = 4 | Best speedup | CPU at W = 4 / serial |
|---|---|---|---|---|---|---|---|---|---|
| Monkey's Audio `-c3000`, 16-bit | 180 | 0.10 | 3.29 | 2.86 | 1.87 | 1.29 | 1.09 | 3.0 (W = 4) | 1.21 |
| Monkey's Audio `-c4000`, 16-bit | 45 | 0.10 | 4.78 | 4.65 | 2.87 | 1.88 | 1.44 | 3.3 (W = 4) | 1.16 |
| Monkey's Audio `-c5000`, 16-bit | 12 | 0.09 | 9.60 | 9.80 | 4.89 | 3.79 | 2.70 | 3.6 (W = 4) | 0.99 |
| Monkey's Audio `-c4000`, 24-bit/96 kHz | 98 | 0.33 | 11.21 | 10.52 | 5.62 | 3.75 | 3.03 | 3.7 (W = 4) | 1.02 |
| WavPack default, 16-bit | 1,200 blocks | 0.07 | 1.41 | 1.47 | 0.79 | 0.63 | 0.73 | 2.2 (W = 3) | 1.42 |
| WavPack `-hh`, 16-bit | 600 blocks | 0.07 | 2.30 | 2.34 | 1.18 | 0.87 | 0.89 | 2.6 (W = 3) | 1.29 |
| WavPack `-hh`, 24-bit/96 kHz | 1,200 blocks | 0.19 | 7.40 | 7.63 | 3.93 | 2.57 | 2.31 | 3.2 (W = 4) | 1.16 |
| FLAC -5, 16-bit | 3,230 | 0.23 | 0.66 | 0.52 | 0.56 | 0.58 | 0.55 | 1.25 (W = 1) | 1.82 |
| FLAC -8, 24-bit/96 kHz | 7,032 | 0.50 | 1.50 | 1.46 | 1.36 | 1.35 | 1.39 | 1.1 (W = 3) | 2.06 |

- **The reports** of every `p` configuration equaled the serial report for all nine files (JSON, 36 comparisons).
- **Monkey's Audio gains 3 to 3.7 times on four workers**, as the speed pass expected, and the CPU time stays within about 20% of the serial run's: the frames are large and the commit has nothing to do. The 16-bit insane file shows the granularity: 12 frames, 3 rounds on four workers (2.70 s), 4 rounds on three (3.79 s).
- **WavPack gains 2.2 to 3.2 times,** and at 16 bits four workers are slower than three: the analysis thread, which parses, copies and hashes, is the fifth busy thread on four cores. The prototype computes WavPack's MD5 twice (on the workers, in the decoders it wraps, and at the commit) and copies every block's PCM, which the design does not; its CPU overhead (16 to 42%) is mostly that.
- **FLAC gains 1.1 to 1.25 times,** below the speed pass's estimate of 1.4. The analysis thread does the walk, a copy of every frame's planes and of its PCM, and the MD5, and the workers hash a second time; the analysis without decoding (the walk) is already a third of the serial run. The design's FLAC (no second hash, planes lent from a pool, no PCM copy where no sink takes it) should come closer to the estimate, which stays unconfirmed; option C is what would move FLAC further.
- **One worker** (W = 1, the pipeline of option A for these formats) gains nothing for Monkey's Audio and WavPack, where the analysis thread has almost nothing to overlap. For FLAC the runs disagree: the first run read 0.52 s against 0.66 s serial for -5 (21% faster), the third 0.708 s against 0.616 s (15% slower). No gain is claimed for one worker.
- **A second run** of five of the files (same method) read Monkey's Audio `-c4000` 4.72 serial and 1.43 at W = 4, `-c5000` 9.10 and 2.76, WavPack default 1.49 and 0.66 (W = 3), `-hh` 16-bit 2.09 and 0.80 (W = 3), `-hh` 24-bit 7.50 and 2.44 (W = 4): the speedups agree within about 10%. FLAC in a third run: -5 0.62 serial, 0.54 at W = 4; -8 1.62 serial, 1.38 at W = 3 with the analysis thread helping.

**The analysis thread helping.** A variant in which the analysis thread, waiting for the oldest frame, decodes the oldest batch no worker has started (three workers plus that thread), in the same rounds as the second run:

| File | W = 3 | W = 4 | Three workers and the analysis thread helping |
|---|---|---|---|
| Monkey's Audio `-c4000`, 16-bit | 1.72 | 1.43 | 1.28 |
| Monkey's Audio `-c5000`, 16-bit | 3.60 | 2.76 | 2.93 |
| WavPack default, 16-bit | 0.66 | 0.75 | 0.70 |
| WavPack `-hh`, 16-bit | 0.80 | 0.83 | 0.85 |
| WavPack `-hh`, 24-bit/96 kHz | 2.57 | 2.44 | 2.58 |

- The helping variant is the fastest on one file and within about 7% of the better of three and four workers on the others: where the analysis thread has serial work (WavPack), it helps less, and where it would wait (Monkey's Audio), it decodes. That is why the design counts the analysis thread in the degree (see [The degree of parallelism](#the-degree-of-parallelism)).
- **A race the prototype had** shows why `DeclareOutput` belongs to the commit: with the helping thread, a worker could decode a later batch before an earlier one, and the wrapper declared the stream's output from the batch in which a worker first wrote; one run in about two decoded FLAC with `decode.failed` (a `NullReferenceException` at the commit) until the format was kept per stream. In the design the worker never declares output, and the commit declares it before the first block in stream order.

### How far the figures carry

- **Minima, not typical runs.** Each figure is the smallest of three rounds. The medians of the same rounds are 5 to 20% above the minima for most files and up to 37% for FLAC -5 serial (0.646 s against 0.885 s in `timing2.log`), so a speedup from minima is a best case.
- **Run to run.** The same configuration read up to about 19% apart between runs (Monkey's Audio `-c5000` with the analysis thread and three workers: 2.93 s, then 3.48 s in the rerun; its serial time 9.10 s, then 10.34 s). The load average at the end of a file's rounds read 1.4 to 2.6, which includes the benchmark's own threads. Differences between configurations of under about 10% are therefore not distinguishable, and the claim "within about 7% of the better of three and four workers" is at that limit.
- **FLAC** is the weakest: the configurations of one file lie within the spread of the runs, and one worker is both faster and slower than serial depending on the run.
- **A run that did not decode.** The first helping runs list `f16-5.flac` at four workers with a minimum of 0.238 s, which equals the analysis without decoding (0.23 s) and cannot be a decoding run. It is not used; the rerun with the identity check reads 0.50 s for that configuration.
- **One machine:** four vCPUs, the JIT build, 300 s files, one four-core Xeon. Nothing here says how a machine with more cores, a slower memory or the NativeAOT build behaves.

## Open decisions

1. **Parallel decoding on by default?**
   - (a) The library stays serial unless `MaxParallelism` is set; the CLI with `--verify` and the GUI's "Verify Audio" set it (the degrees above).
   - (b) The library decodes in parallel by default too.
   - (c) Off everywhere until a front-end option asks for it.
   - **Recommendation: (a).** A library that starts threads unasked surprises a caller who runs its own parallelism (a server, the GUI's own list); the two front ends know their situation, and the report is the same either way.
2. **The default degree with `--verify`, in the CLI and the GUI.**
   - (a) `Environment.ProcessorCount`, at most 8 (on four cores: three workers and the analysis thread helping), for the CLI and for the GUI's "Verify Audio" alike (the GUI's degree is the processor count minus the list's `Parallelism` while the list analyzes, within the same cap).
   - (b) `Environment.ProcessorCount`, unlimited.
   - (c) A fixed 4.
   - **Recommendation: (a).** On four cores it was within about 7% of the best fixed number of workers for every file measured. The gains flatten where the analysis thread and the frame count bind (FLAC, Monkey's Audio at insane), and memory grows with the window; the cap of 8 is to be confirmed on a machine with more cores, which this design did not have.
3. **A CLI option for the degree** (such as `--decode-threads N`, 1 for serial).
   - (a) Yes, documented in `docs/cli.md`; it makes benchmarks and bug reports reproducible.
   - (b) No; the default only.
   - **Recommendation: (a).** It is a front-end option, not a setting below it, so it fits the plan's rule.
4. **May `--verify` output differ between degrees in fields that only reflect timing?**
   - (a) No: byte-identical, and no report field may depend on timing (none does today).
   - (b) Allow timing-only fields (none exist) and exclude them from the match tests.
   - **Recommendation: (a).** The match tests stay a byte comparison, and a "decoded in N ms" field, if wanted later, would go to the CLI's standard error, not into the report.
5. **The memory bound of the frames in flight, scratch memory included.** The first draft left the workers' scratch outside the 64 MiB: Monkey's Audio's `_held` (32 MiB for 24-bit) and `_stream` (the frame's size, growing by doubling, up to 64 MiB) make about 770 MiB at a degree of 8 against about 96 MiB serial (estimated).
   - (a) 64 MiB for everything in flight: coded bytes, journal, lent buffers and scratch, with scratch rented per frame from the session's pool, counted through `ScratchBytes`, and trimmed when the window drains. A frame that does not fit runs at the hand-off.
   - (b) 64 MiB for bytes, journal and lent buffers, and scratch kept by the workers with a ceiling of its own: the total is about 64 MiB plus the degree times the ceiling, and the degree is lowered for streams whose frames need a large scratch.
   - (c) A bound scaled with the degree (16 MiB a worker), scratch included.
   - **Recommendation: (a).** One number bounds memory whatever the format, and the cost is the rental bookkeeping and a lost parallelism only for the largest 24-bit frames; it is revisited if the first pull request measures a loss on the 24-bit insane files at a degree of 8.
6. **The CLI's file-level parallelism** (several files at once, with output in input order).
   - (a) A design of its own after this rollout.
   - (b) In this design's scope.
   - **Recommendation: (a).** It helps a folder of small files, which frame-level parallelism cannot, but it changes the CLI's output path and needs its own memory bound; the two compose (the degree of each analysis then shrinks).
7. **FLAC in B.** The prototype gained 1.1 to 1.25 times, the estimate for the design is about 1.4, and FLAC alone needs the pool of lent buffers.
   - (a) FLAC as step 3 of this rollout, on B alone (with the canonical bytes packed on the worker, if the split below says so).
   - (b) FLAC together with option C (the commit on a thread of its own, about 2 times estimated for FLAC -5), after a prototype of C on the B pipeline.
   - (c) FLAC stays serial.
   - **Recommendation: (b).** B alone buys FLAC little for the pool's complexity; with C the walk becomes the bound, and the commit as one method in stream order is what C moves, so B leaves it ready. **Before deciding,** measure how the commit's 36% divides between packing the canonical bytes and the MD5: packing on the worker would leave only the MD5 serial in B and may change the answer.
8. **WavPack sets whose blocks fail.** The plan of a block depends on whether the earlier blocks of its set decoded (`_setChannels` counts only decoded blocks).
   - (a) Replan: the plan assumes they did, and `Replan` reruns the set logic over the committed state before the replay; a mismatch decodes the block inline. Serial reports and goldens do not change; only sets with a failed block cost a second decoding. Feasible: the logic reads five scalars of the set.
   - (b) Count a failed block's channels in `_setChannels`, so the plan depends on headers only. It changes serial reports and the goldens of damaged WavPack files in which a block of a multi-block set fails (the set completes or overflows at a different block, as in the 5.1 example), and would need a check against libwavpack's behavior for such a set.
   - **Recommendation: (a).** It keeps every report; (b) is simpler but is a behavior change decided by a scheduling design.
9. **DSD charge points.** `DsdBlockDecoder.Prepare` can throw before the first check.
   - (a) Exact: the `AddWork` calls stay up front, `Charge(0)` follows `Prepare` for modes 1 and 3, and mode 0 and muted blocks check nothing. Serial reports do not change.
   - (b) Accept a change: charge and check before `Prepare`. It is simpler, and a block that crosses the budget and has an invalid table is then a block not decoded at the limit, where today it is `decode.frame-invalid`; the corrupt WavPack variants would change where they hold such a block (not checked).
   - **Recommendation: (a).** It costs one line in the decoder.
10. **Early cancel of workers against a window limited by planned work.** A worker cancels as soon as the total it reads passes the budget, which can be wrong after a misplanned frame; the replay answers with an inline decoding. The wasted work past the budget is about the window times the smaller of a frame's work and the budget.
    - (a) Keep the early cancel, with the inline fallback, and state the bound as bounded wall time and CPU up to the number of running workers times that. The report never depends on the early cancel.
    - (b) Limit the window by planned work (at most the degree times 2^27 units in flight). The waste past the budget is then small and early cancels rarer, but most Monkey's Audio frames of the higher levels (a 16-bit insane frame is about 10^9 units; estimated) run inline, which removes the format's gain. It needs a planned-work estimate from every decoder's plan.
    - **Recommendation: (a).** The waste happens only in a file that crosses the budget, where the wall time is what the budget promises; (b) gives up the largest gain for it.
11. **Monkey's Audio `frame-not-decoded` with frames in flight.** `IsDecoding` turns false a few frames late, and the Monkey's Audio parser reports the finding and counts the frame unreadable only where `IsDecoding` is true.
    - (a) The parser takes `IsDecoding` once, after `StartDecoding`, for the finding and the count. A serial report changes only for a file with a frame over 64 MiB after the budget or a stop ended decoding: the frame now gets the finding and the unreadable count it did not get before. No committed fixture has one.
    - (b) One more member of `IDecodeSession`, called by the parser only at a frame over 64 MiB, where the host commits the frames in flight and answers exactly as a serial run would. No report changes; it costs a drain at such a frame (inline anyway) and a member of the interface.
    - **Recommendation: (b)** by the rule that reports stay as they are; (a) is acceptable if the owner finds the corner case irrelevant and wants no new member.

## Unverified and estimates

- The "Bound" column of [What stays serial](#what-stays-serial) and the CI cost are estimates from profile shares, not measurements.
- The behavior of a blocking wait in .NET 10's single-threaded browser runtime (see [Without threads](#without-threads)).
- Degrees above 4: this machine has four vCPUs, so the scaling past 4 and the default cap of 8 are not measured.
- The batch size of 2^16 samples is the prototype's order of magnitude (it used 16,384), not a tuned figure.
- The prototype ran the real decoders, but not the contract above: the host's replay, the replan, the pool of lent buffers and the rented scratch are designed here and not tried. Its figures are minima of three rounds on a shared machine (see [Measured](#measured)).
- The figures of the budget and memory sections are computed from the code's constants, not measured: a Monkey's Audio frame of about 4.5 × 10^11 units, the largest WavPack PCM block of about 1.1 × 10^8 units, the scratch of about 96 MiB serial and 770 MiB at a degree of 8, and a 16-bit insane frame of about 10^9 units.
- How the 36% of FLAC's commit divides between packing and the MD5.
