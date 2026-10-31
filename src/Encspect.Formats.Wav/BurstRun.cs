using Encspect.Core;

namespace Encspect.Formats.Wav;

// One walk over the bursts of a chain in the data chunk. It yields the payload of each burst of the stream's data type as the coding's own bytes
// (mapped to the file bytes they come from), which the codec parser takes as packets, and builds the nodes of the bursts and the findings about
// them as it goes, so the data is read once and the walk costs no memory beyond the nodes it lists.
internal sealed class BurstRun(State state, ByteReader data, BurstChain chain, Node container)
{
    private int _listed;
    private bool _isInPauseRun;

    public int Bursts { get; private set; }

    // The pause and null bursts, which carry no audio and are counted, not listed, and the runs of them with no other burst between.
    public long Pauses { get; private set; }

    public long PauseRuns { get; private set; }

    public long PayloadBytes { get; private set; }

    // Where the bytes after the last burst of the stream that are not stuffing start, or -1 when they are all zeros.
    public long Rest { get; private set; } = -1;

    public IEnumerable<MappedBytes> Payloads()
    {
        var window = new BurstWindow(data);
        long end = window.End;
        BurstType type = chain.Type;
        long period = (long)type.Repetition * Iec61937.FrameLength;
        long position = chain.First;
        long expected = position;
        long searchFrom = position;
        int steps = 0;
        while (true)
        {
            if (steps++ % 4096 == 0)
            {
                state.Context.CancellationToken.ThrowIfCancellationRequested();
            }

            if (Iec61937.HeaderAt(window, position) is not { } header)
            {
                long? found = FindBurst(window, searchFrom);
                if (found is not { } next)
                {
                    Rest = RestAfter(window, searchFrom);
                    if (Rest >= 0)
                    {
                        long at = Rest;
                        state.Findings.Add(Severity.Warning, "wav.iec61937-data-after-bursts", container, _ =>
                            $"The bytes from 0x{at:X} to the end of the data chunk are neither a data-burst nor stuffing of zeros, {Counts.Of(data.End - at, "byte", "bytes")} in all.");
                    }

                    ReportPauses();
                    yield break;
                }

                if (expected >= 0)
                {
                    long wanted = expected;
                    long actual = next;
                    Node target = container;
                    if (next < expected)
                    {
                        state.Findings.Add(Severity.Warning, "wav.iec61937-burst-spacing", target, _ =>
                            $"A burst starts at 0x{actual:X}, {Counts.Of(wanted - actual, "byte", "bytes")} before the repetition period of {Counts.Of(type.Repetition, "IEC 60958 frame", "IEC 60958 frames")} puts the next one.");
                    }
                    else
                    {
                        state.Findings.Add(Severity.Warning, "wav.iec61937-burst-missing", target, _ =>
                            $"No burst starts at 0x{wanted:X}, where the repetition period of {Counts.Of(type.Repetition, "IEC 60958 frame", "IEC 60958 frames")} puts the next one; the next sync words are at 0x{actual:X}.");
                    }
                }

                position = next;
                expected = -1;
                continue;
            }

            if (Iec61937.IsPauseOrNull(header.DataType))
            {
                // Stepped over by its own length code, so that a gap of pause bursts at their recommended period costs a few bytes each; where nothing
                // starts after it the search goes on from its preamble.
                Pauses++;
                if (!_isInPauseRun)
                {
                    PauseRuns++;
                    _isInPauseRun = true;
                }

                searchFrom = position + Iec61937.PreambleLength;
                position = Iec61937.EndOfPause(position, header);
                expected = -1;
                continue;
            }

            _isInPauseRun = false;
            if (header.DataType != chain.DataType || header.Bitstream != chain.Bitstream)
            {
                Node other = AddBurst(position, Iec61937.PreambleLength, header, null);
                int found = header.DataType;
                int bitstream = header.Bitstream;
                state.Findings.Add(Severity.Warning, "wav.iec61937-type-changed", other, _ =>
                    $"A burst of {Iec61937.NameOf(found)} (data type {found}, bitstream {bitstream}) comes among bursts of {type.Name} (data type {chain.DataType}, bitstream {chain.Bitstream}); it is not read as part of the stream.");
                searchFrom = position + Iec61937.PreambleLength;
                position = searchFrom;
                expected = -1;
                continue;
            }

            long length = Iec61937.LengthBytes(type, header.Pd);
            long room = period - Iec61937.PreambleLength;
            long start = position + Iec61937.PreambleLength;
            bool isTooLong = length > room;
            bool isCut = start + Math.Min(length, room) > end;
            long payload = Math.Min(Math.Min(length, room), end - start);
            bool isHalfSample = false;
            if (payload % 2 == 1 && start + payload >= end)
            {
                // The last byte of an odd payload is the second byte of a sample the data ends before.
                payload--;
                isHalfSample = true;
            }

            Node burst = AddBurst(position, Iec61937.PreambleLength + payload, header, payload);
            if (isTooLong || isCut || isHalfSample || payload == 0)
            {
                long stated = length;
                state.Findings.Add(Severity.Warning, "wav.iec61937-length-invalid", burst, _ =>
                    $"The length code of a burst states a payload of {Counts.Of(stated, "byte", "bytes")}, which does not fit the room of {Counts.Of(room, "byte", "bytes")} that its repetition period leaves after the preamble or the data that is left; it is read as far as it fits.");
            }

            if (header.HasError)
            {
                state.Findings.Add(Severity.Info, "wav.iec61937-error-flag", burst, n =>
                    $"The burst-info of {Counts.Of(n, "burst", "bursts")} sets the error flag, which states that the payload may contain errors (IEC 61937-1, Table 5).");
            }

            Bursts++;
            PayloadBytes += payload;
            expected = position + period;
            // A burst the data ends in owns the rest of the data.
            searchFrom = isCut ? end : start + ((payload + Iec61937.FrameLength - 1) / Iec61937.FrameLength * Iec61937.FrameLength);
            if (payload > 0)
            {
                int count = (int)Math.Min(payload + (payload % 2), end - start);
                yield return new MappedBytes(Iec61937.Payload(window.At(start, count), (int)payload), start);
            }

            position = expected;

            // A burst that starts after the payload's frames but before the period ends is early, not absent: without this the burst at the
            // period would hide it.
            if (searchFrom < expected && FindBurst(window, searchFrom, expected) is { } early)
            {
                long wanted = expected;
                state.Findings.Add(Severity.Warning, "wav.iec61937-burst-spacing", container, _ =>
                    $"A burst starts at 0x{early:X}, {Counts.Of(wanted - early, "byte", "bytes")} before the repetition period of {Counts.Of(type.Repetition, "IEC 60958 frame", "IEC 60958 frames")} puts the next one.");
                position = early;
                expected = -1;
            }
        }
    }

    // The next preamble from position on at a frame of the chain, or null.
    // With limit, only a preamble that starts before it counts.
    private long? FindBurst(BurstWindow window, long from, long limit = long.MaxValue)
    {
        const int Block = 1 << 16;
        long position = from + ((Iec61937.FrameLength - ((from - data.Start) % Iec61937.FrameLength)) % Iec61937.FrameLength);
        long end = data.End;
        while (position < limit && position + Iec61937.PreambleLength <= end)
        {
            // A request past the window refills it, so a search up to a limit asks for no more than the limit and a preamble.
            ReadOnlySpan<byte> block = window.At(position, (int)Math.Min(Block, limit - position + Iec61937.PreambleLength));
            int length = block.Length;
            for (int i = 0; i + Iec61937.PreambleLength <= length && position + i < limit; i += Iec61937.FrameLength)
            {
                // The preamble lies inside the block, so looking at it reads nothing and the block stays valid.
                if (block[i] == 0x72 && block[i + 1] == 0xF8 && Iec61937.HeaderAt(window, position + i) is not null)
                {
                    return position + i;
                }
            }

            // The next block starts at a frame, and re-reads the frames whose preamble the block cut.
            position += Math.Max(Iec61937.FrameLength, (length - Iec61937.PreambleLength) / Iec61937.FrameLength * Iec61937.FrameLength);
        }

        return null;
    }

    // Where the first byte that is not zero lies from position to the end of the data, or -1 when there is none: what follows the last burst is stuffing
    // of zeros, and anything else is not.
    private long RestAfter(BurstWindow window, long from)
    {
        const int Block = 1 << 16;
        long position = Math.Min(from, data.End);
        while (position < data.End)
        {
            ReadOnlySpan<byte> block = window.At(position, Block);
            int found = block.IndexOfAnyExcept((byte)0);
            if (found >= 0)
            {
                return position + found;
            }

            position += block.Length;
        }

        return -1;
    }

    // What the walk found that is not a burst of the stream: pause and null bursts, counted and not listed.
    private void ReportPauses()
    {
        if (Pauses == 0)
        {
            return;
        }

        long pauses = Pauses;
        long runs = PauseRuns;
        state.Findings.Add(Severity.Info, "wav.iec61937-pause-bursts", container, _ =>
            $"The data holds {Counts.Of(pauses, "pause or null burst", "pause or null bursts")} in {Counts.Of(runs, "run", "runs")}, which carry no audio (IEC 61937-1 7.2 and 7.4); they are counted and not listed.");
    }

    private Node AddBurst(long position, long length, BurstHeader header, long? payload)
    {
        if (_listed >= Iec61937.MaxListed)
        {
            return container;
        }

        _listed++;
        var burst = new Node($"Burst {_listed - 1}", position, length, Iec61937.NameOf(header.DataType)) { Description = Explain.BurstNode, Topic = Topics.Iec61937 };
        burst.Add("Sync Words", position, 4, NodeValue.Text("F872h 4E1Fh"), Explain.BurstSync, Topics.Iec61937);
        Node info = burst.Add("Burst Info", position + 4, 2, NodeValue.Hex(header.Pc, 4), Explain.BurstInfo, Topics.Iec61937);
        info.Add(Node.FromBits("Data Type", position + 4, 0, 7, NodeValue.Integer(header.DataType), NodeKind.Detail, Explain.BurstDataType, Topics.Iec61937, BitOrder.LeastSignificantFirst));
        info.Add(Node.FromBits("Error Flag", position + 4, 7, 8, NodeValue.Boolean(header.HasError), NodeKind.Detail, Explain.BurstErrorFlag, Topics.Iec61937, BitOrder.LeastSignificantFirst));
        info.Add(Node.FromBits("Data-Type-Dependent Info", position + 4, 8, 13, NodeValue.Integer(header.Info), NodeKind.Detail, Explain.BurstDependentInfo, Topics.Iec61937, BitOrder.LeastSignificantFirst));
        info.Add(Node.FromBits("Bitstream Number", position + 4, 13, 16, NodeValue.Integer(header.Bitstream), NodeKind.Detail, Explain.BurstBitstream, Topics.Iec61937, BitOrder.LeastSignificantFirst));
        burst.Add("Length Code", position + 6, 2, NodeValue.Integer(header.Pd), Explain.BurstLength, Topics.Iec61937);
        if (payload is > 0)
        {
            burst.Add("Burst Payload", position + Iec61937.PreambleLength, payload.Value, NodeValue.Integer(payload.Value, "bytes", "byte"), Explain.BurstPayload, Topics.Iec61937);
        }

        container.Add(burst);
        state.Nodes += Walker.Count(burst);
        return burst;
    }
}
