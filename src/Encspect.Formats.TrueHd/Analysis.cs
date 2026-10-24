using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.TrueHd;

// The analysis of one stream's access units, whether a raw file's (TrueHdParser) or a container's packets (TrueHdCodecParser): each unit is read
// once, field by field where its structure needs it (UnitLayout, SubstreamWalk), checked against its checksums, kept as a compact record the nodes
// are built from, and handed to the decode session with the residuals its walk stored (docs/design/decoders.md, "The second entropy pass").
internal sealed class Analysis
{
    // Units with all their bytes kept, so that their blocks can be read again for the nodes: the first ones, up to a budget of bytes.
    public const int MaxFullUnits = 512;
    public const long MaxFullBytes = 4L << 20;

    // Units with a major sync beyond those keep what their header, sync, directory and substreams' ends need, up to a budget.
    public const int MaxSparseUnits = 100_000;
    public const long MaxSparseBytes = 32L << 20;

    // The substreams the analyzer reads and decodes (FFmpeg decodes substreams 0 to 2: the fourth holds no audio it knows).
    public const int ParsedSubstreams = 3;

    private const int RunLength = 16;
    private readonly AnalysisContext _context;
    private readonly UnitLayout _layout = new();
    private readonly SubstreamState[] _states = [new(), new(), new(), new()];
    private readonly bool[] _isIntervalOpen = new bool[ParsedSubstreams];
    private readonly int[] _presentationChannels = new int[ParsedSubstreams];
    private readonly WalkResult?[] _walks = new WalkResult?[UnitLayout.MaxSubstreams];
    private MajorSync? _sync;
    private MajorSync? _firstSync;
    private int _count;
    private bool _isMlp;
    private bool _isSynced;
    private bool _hasSynced;
    private bool _isGapPending;
    private long _fullBytes;
    private long _sparseBytes;
    private int _sparseUnits;
    private int _runUnits;
    private long _runBits;
    private long _runSamples;
    private long _runOffset;

    public Analysis(AnalysisContext context)
    {
        _context = context;
    }

    // The stream's node, which the nodes of the units are children of; set before the first unit.
    public Node Stream { get; set; } = null!;

    // The nodes before the units.
    public List<Node> Leading { get; } = [];

    public Records Records { get; } = new();

    public FindingCollector Findings { get; } = new();

    public IDecodeSession? Session { get; private set; }

    public StreamFigures? Figures { get; private set; }

    // Counts over the units.
    public long Units { get; private set; }

    public long LeadingUnits { get; private set; }

    public long SyncUnits { get; private set; }

    public long SyncCrcFailures { get; private set; }

    public long ParityChecked { get; private set; }

    public long ParityFailures { get; private set; }

    public long RestartHeaders { get; private set; }

    public long RestartCrcFailures { get; private set; }

    public long SubstreamChecked { get; private set; }

    public long SubstreamParityFailures { get; private set; }

    public long SubstreamCrcFailures { get; private set; }

    public long SubstreamsWithoutCheck { get; private set; }

    public long UnreadableUnits { get; private set; }

    public long CutUnits { get; private set; }

    public long SkippedBytes { get; private set; }

    public long Samples { get; private set; }

    public long ShortestUnit { get; private set; } = long.MaxValue;

    public long LongestUnit { get; private set; }

    public bool HasGap { get; private set; }

    public bool SignalsAtmos { get; private set; }

    public long[] LosslessStored { get; } = new long[ParsedSubstreams];

    public MajorSync? FirstSync => _firstSync;

    public MajorSync? Sync => _sync;

    public bool IsSynced => _isSynced;

    public int SubstreamCount => _count;

    public bool IsMlp => _isMlp;

    public int[] PresentationChannels => _presentationChannels;

    public long FirstSample { get; private set; }

    // The first restart header of each substream in a unit that could be read: its channel count names the presentation.
    public IReadOnlyList<int> NamedChannels => _presentationChannels;

    private Records R => Records;

    // Whether the bytes at the start of data look like an access unit of this stream: a usable major sync, or the parity of a unit without one
    // where a sync has been seen. A search for the next unit uses it to accept a length word it cannot otherwise vouch for.
    public bool LooksLikeUnit(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return false;
        }

        int length = ((data[0] & 0xF) << 8 | data[1]) * 2;
        if (length < 4)
        {
            return false;
        }

        var layout = new UnitLayout();
        layout.Read(data, _hasSynced ? _count : 0, _isMlp);
        if (layout.HasSync)
        {
            return layout.Sync is { IsUsable: true };
        }

        return _hasSynced && layout.IsDirectoryRead && (layout.Problems & (UnitProblems.Parity | UnitProblems.DirectoryTooLong | UnitProblems.NonRestartBit)) == 0;
    }

    // Reads a unit's header and directory without changing anything, to tell whether it is structurally sound.
    public UnitProblems Probe(ReadOnlySpan<byte> data)
    {
        _layout.Read(data, _hasSynced ? _count : 0, _isMlp);
        return _layout.Problems;
    }

    public void AddSkipped(long offset, long length)
    {
        while (length > 0)
        {
            long part = Math.Min(length, int.MaxValue);
            int index = R.Entries.Count;
            R.Entries.Add(new Entry { Kind = EntryKind.Skipped, Offset = offset, Header = (uint)part });
            offset += part;
            length -= part;
            SkippedBytes += part;
            if (_hasSynced)
            {
                HasGap = true;
                _isGapPending = true;
            }

            Records records = R;
            long skipped = part;
            long at = R.Entries[index].Offset;
            Findings.Add(Severity.Warning, "truehd.data-skipped", () => Nodes.Skipped(records, index), count =>
                $"{Counts.Of(skipped, "byte", "bytes")} at 0x{at:X} {Counts.Agree(skipped, "holds", "hold")} no access unit that checks out{(count > 1 ? $", and {count - 1} more stretches do" : string.Empty)}; the stream is read again from the next major sync that checks out.");
        }

        Desync();
    }

    private void Desync()
    {
        _isSynced = false;
        foreach (SubstreamState state in _states)
        {
            state.Forget();
        }

        Array.Clear(_isIntervalOpen);
    }

    // Reads one access unit, whose bytes are all there (or, with isCut, as far as the data goes) and starts at offset, and adds it to the stream.
    // followsLoss: bytes before it were skipped.
    public void AddUnit(ReadOnlyMemory<byte> memory, long offset, bool isCut, bool followsLoss)
    {
        ReadOnlySpan<byte> bytes = memory.Span;
        UnitLayout layout = _layout;
        layout.Read(bytes, _hasSynced ? _count : 0, _isMlp);
        int index = R.Entries.Count;
        Records records = R;
        UnitStatus status = UnitStatus.None;
        uint header = bytes.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : 0;
        int length = layout.Length;
        bool wasPending = _isGapPending;
        _isGapPending = false;
        if (followsLoss || wasPending)
        {
            status |= UnitStatus.FollowsGap;
        }

        // A unit before the first major sync, or one that waits for the next after damage, cannot be read.
        if (!layout.HasSync && !_isSynced)
        {
            status |= UnitStatus.Unsynchronized;
            R.Entries.Add(new Entry { Kind = EntryKind.Unit, Status = status, Offset = offset, Header = header });
            Units++;
            if (_hasSynced)
            {
                HasGap = true;
                _isGapPending = true;
                Findings.Add(Severity.Warning, "truehd.units-after-damage", () => Nodes.Unit(records, index), count =>
                    $"{(count == 1 ? "An access unit follows" : $"{count} access units follow")} damage and carry no major sync, so the stream cannot be read until the next major sync, at the first of them at 0x{offset:X}.");
            }
            else
            {
                LeadingUnits++;
                Findings.Add(Severity.Info, "truehd.units-before-major-sync", () => Nodes.Unit(records, index), count =>
                    $"{(count == 1 ? "An access unit comes" : $"{count} access units come")} before the first major sync, so the number of substreams and the sampling rate are not known; {(count == 1 ? "it is" : "they are")} skipped, as in a stream cut out of a longer one.");
            }

            return;
        }

        bool isUnreadable = false;
        byte syncLength = 0;
        if (layout.HasSync)
        {
            status |= UnitStatus.HasSync;
            syncLength = (byte)(layout.Sync?.Length ?? MajorSync.MinLength);
            if (layout.Sync is { } sync)
            {
                if (!sync.IsCrcValid)
                {
                    status |= UnitStatus.SyncCrcFails;
                    SyncCrcFailures++;
                    ushort stored = sync.StoredCrc;
                    ushort computed = sync.ComputedCrc;
                    Findings.Add(Severity.Warning, "truehd.major-sync-crc-mismatch", () => Nodes.MajorSyncOf(records, index), count =>
                        $"The major sync at 0x{offset + 4:X} has the CRC 0x{stored:X4}, but 0x{computed:X4} is computed over it{(count > 1 ? $"; {count - 1} more major syncs differ" : string.Empty)}; the unit cannot be read.");
                }

                if (sync.Problem != SyncProblem.None)
                {
                    status |= UnitStatus.SyncInvalid;
                    SyncProblem problem = sync.Problem;
                    Findings.Add(Severity.Warning, "truehd.major-sync-invalid", () => Nodes.MajorSyncOf(records, index), count =>
                        $"The major sync at 0x{offset + 4:X} {DescribeSync(problem)}{(count > 1 ? $"; {count - 1} more major syncs have the same fault" : string.Empty)}; the unit cannot be read.", problem.ToString());
                }
            }
            else
            {
                status |= UnitStatus.SyncInvalid;
                Findings.Add(Severity.Warning, "truehd.major-sync-invalid", () => Nodes.MajorSyncOf(records, index), count =>
                    $"The major sync at 0x{offset + 4:X} is cut short by the end of its unit{(count > 1 ? $"; {count - 1} more major syncs are" : string.Empty)}.", nameof(SyncProblem.TooShort));
            }

            isUnreadable = layout.Sync is not { IsUsable: true };
            if (layout.Sync is { IsUsable: true } usable)
            {
                NewSync(usable, offset, index);
            }
        }

        if (isCut)
        {
            status |= UnitStatus.Cut;
            CutUnits++;
            isUnreadable = true;
            long available = bytes.Length;
            int declared = length;
            Findings.Add(Severity.Warning, "truehd.unit-cut", () => Nodes.Unit(records, index), _ =>
                $"The data ends inside the access unit at 0x{offset:X}, after {Counts.Of(available, "byte", "bytes")} of its {Counts.Of(declared, "byte", "bytes")}.");
        }
        else if (!isUnreadable)
        {
            UnitProblems problems = layout.Problems & ~UnitProblems.Sync;
            if (layout.IsDirectoryRead)
            {
                ParityChecked++;
                if ((problems & UnitProblems.Parity) != 0)
                {
                    ParityFailures++;
                    status |= UnitStatus.ParityFails;
                    Findings.Add(Severity.Warning, "truehd.parity-mismatch", () => Nodes.Unit(records, index), count =>
                        $"The parity of the unit header and substream directory of the access unit at 0x{offset:X} does not come out right{(count > 1 ? $"; {count - 1} more access units differ" : string.Empty)}; the unit cannot be read.");
                }
            }

            UnitProblems directory = problems & ~UnitProblems.Parity;
            if (directory != UnitProblems.None)
            {
                status |= UnitStatus.DirectoryInvalid;
                foreach (UnitProblems flag in Enum.GetValues<UnitProblems>().Where(f => f != UnitProblems.None && (directory & f) != 0))
                {
                    UnitProblems problem = flag;
                    Findings.Add(Severity.Warning, "truehd.directory-invalid", () => Nodes.Unit(records, index), count =>
                        $"The substream directory of the access unit at 0x{offset:X} {DescribeDirectory(problem)}{(count > 1 ? $"; {count - 1} more access units have the same fault" : string.Empty)}.", problem.ToString());
                }
            }

            isUnreadable = problems != UnitProblems.None;
        }

        UnitStatus overall = status;
        bool isReadable = !isUnreadable;
        int outSamples = 0;
        bool isDamaged = false;
        UnitDetail? detail = null;
        MajorSync? current = _sync;
        bool isWalked = false;
        if (isReadable && current is { } active)
        {
            (isReadable, outSamples, isDamaged, isWalked, overall, detail) = ReadSubstreams(memory, offset, index, layout, active, overall);
        }

        // A unit that cannot be read leaves the substreams' state behind: the stream is read again from the next major sync.
        if (!isReadable)
        {
            Desync();
            UnreadableUnits++;
            HasGap = true;
            if (current is not null)
            {
                _isGapPending = true;
            }
        }

        if (isCut)
        {
            R.CutLengths[index] = bytes.Length;
        }

        Units++;
        if (_hasSynced)
        {
            long counted = isReadable ? outSamples : current?.UnitSamples ?? 0;
            Figure(offset, length, counted, bytes.Length);
        }

        R.Entries.Add(new Entry { Kind = EntryKind.Unit, Status = overall, Offset = offset, Header = header, SyncLength = syncLength });
        Store(index, memory, layout, detail, overall, isReadable, current);

        if (isReadable)
        {
            if (Session is null && current is { } start)
            {
                StartSession(start);
            }

            if (Session is { IsDecoding: true } session)
            {
                session.Decode(new CodedFrame(new MappedBytes(memory, offset), Nodes.UnitName, FirstSample, outSamples)
                {
                    IsDamaged = isDamaged,
                    FollowsGap = (overall & UnitStatus.FollowsGap) != 0,
                    IsWalked = isWalked,
                });
            }

            FirstSample += outSamples;
            Samples += outSamples;
        }
        else if (Session is { IsDecoding: true } failing)
        {
            failing.Decode(new CodedFrame(new MappedBytes(memory, offset), Nodes.UnitName, FirstSample, 0) { IsUnreadable = true, IsDamaged = true });
        }
    }

    private static string DescribeSync(SyncProblem problem) => problem switch
    {
        SyncProblem.BitDepth => "states a bit depth for the first channel group that MLP does not define",
        SyncProblem.GroupDepths => "gives the second channel group more bits than the first",
        SyncProblem.Rate => "states a sampling rate code that gives no rate or one above 192 kHz",
        SyncProblem.SubstreamCount => "states a substream count of 0, or more than the format allows (4 for Dolby TrueHD, 2 for MLP)",
        _ => "is damaged",
    };

    private static string DescribeDirectory(UnitProblems problem) => problem switch
    {
        UnitProblems.DirectoryTooLong => "runs past the end of the unit",
        UnitProblems.ExtraWordInMlp => "has an entry with an extra word in an MLP stream, which MLP does not allow",
        UnitProblems.NonRestartBit => "has a non-restart bit that is not 0 in a unit with a major sync and 1 in a unit without",
        UnitProblems.EndDecreases => "has a substream that ends before the one before it",
        UnitProblems.EndPastUnit => "has a substream that ends past the end of the unit",
        _ => "is damaged",
    };

    private void NewSync(MajorSync sync, long offset, int index)
    {
        Records records = R;
        SyncUnits++;
        if (_sync is { } before && !before.SameStreamAs(sync))
        {
            Findings.Add(Severity.Info, "truehd.stream-parameters-change", () => Nodes.MajorSyncOf(records, index), count =>
                $"The major sync at 0x{offset + 4:X} changes the stream's format, sampling rate, channel assignments or substream count from the earlier ones{(count > 1 ? $"; {count - 1} more major syncs change them again" : string.Empty)}; the audio of such a stream is decoded only while its channel count stays the same.");
        }

        if (_firstSync is null)
        {
            _firstSync = sync;
            Records.StreamRate = sync.SampleRate;
            Records.IsMlp = !sync.IsTrueHd;
        }

        if (sync.Signature != 0xB752)
        {
            int signature = sync.Signature;
            Findings.Add(Severity.Info, "truehd.unexpected-value", () => Nodes.MajorSyncOf(records, index), count =>
                $"The major sync at 0x{offset + 4:X} has the signature 0x{signature:X4}, but every file the analyzer was tested on has 0xB752{(count > 1 ? $"; {count - 1} more major syncs differ" : string.Empty)}.", "signature");
        }

        if (!sync.IsTrueHd && sync.HasDifferentGroupRates)
        {
            Findings.Add(Severity.Info, "truehd.unsupported", () => Nodes.MajorSyncOf(records, index), _ =>
                "The major sync gives the two MLP channel groups different sampling rates, which the analyzer does not read; FFmpeg's decoder does not support it either.", "group rates");
        }

        if (sync.SignalsAtmos && !SignalsAtmos)
        {
            SignalsAtmos = true;
            Findings.Add(Severity.Info, "truehd.atmos-signaled", () => Nodes.MajorSyncOf(records, index), _ =>
                "The stream has four substreams and bit 7 of its substream info set, which is how FFmpeg recognizes Dolby Atmos. The first three substreams are read; the fourth, which FFmpeg does not decode either, is checked against the directory and not read.");
        }

        _sync = sync;
        _count = sync.SubstreamCount;
        _isMlp = !sync.IsTrueHd;
        _isSynced = true;
        _hasSynced = true;
        foreach (SubstreamState state in _states)
        {
            state.Forget();
        }
    }

    private (bool IsReadable, int OutSamples, bool IsDamaged, bool IsWalked, UnitStatus Status, UnitDetail? Detail) ReadSubstreams(ReadOnlyMemory<byte> memory, long offset, int index, UnitLayout layout, MajorSync sync,
        UnitStatus status)
    {
        ReadOnlySpan<byte> bytes = memory.Span;
        Records records = R;
        int n = sync.UnitSamples;
        int count = layout.SubstreamCount;
        int read = Math.Min(count, ParsedSubstreams);
        bool isFull = index < MaxFullUnits && _fullBytes + bytes.Length <= MaxFullBytes;
        SubstreamState[]? snapshot = null;
        if (isFull)
        {
            snapshot = new SubstreamState[read];
            for (int k = 0; k < read; k++)
            {
                snapshot[k] = _states[k].Clone();
            }
        }

        SampleBuffers lent = default;
        if (Session is { IsDecoding: true } session)
        {
            lent = session.Lend(n, ParsedSubstreams * SubstreamReader.PlanesPerSubstream);
            for (int k = 0; k < read && !lent.IsEmpty; k++)
            {
                lent.Narrow.Slice(((k * SubstreamReader.PlanesPerSubstream) + SubstreamReader.BypassPlane) * n, SubstreamReader.MaxBypassPlanes * n).Clear();
            }
        }

        bool isReadable = true;
        bool isDamaged = false;
        var verdicts = new byte[count];
        for (int k = 0; k < count; k++)
        {
            DirectoryEntry entry = layout.Entries[k];
            ReadOnlySpan<byte> data = bytes[entry.Start..entry.Stop];
            bool hasCheck = entry.HasCheckData;
            byte verdict = 0;
            if (hasCheck)
            {
                verdict |= 8;
                if (data.Length < 2)
                {
                    if (k < ParsedSubstreams)
                    {
                        isReadable = false;
                        SubstreamFaultFinding(index, k, offset, SubstreamFault.CheckData);
                    }
                }
                else
                {
                    ReadOnlySpan<byte> body = data[..^2];
                    bool isParityValid = Checks.SubstreamParityHolds(body, data[^2]);
                    bool isCrcValid = body.Length > 0 && Checks.Substream(body) == data[^1];
                    verdict |= (byte)((isParityValid ? 1 : 0) | (isCrcValid ? 2 : 0));
                    if (k < ParsedSubstreams)
                    {
                        SubstreamChecked++;
                        if (!isParityValid)
                        {
                            SubstreamParityFailures++;
                            status |= UnitStatus.CheckFails;
                            isDamaged = true;
                            int substream = k;
                            Findings.Add(Severity.Warning, "truehd.substream-parity-mismatch", () => Nodes.SubstreamOf(records, index, substream), count2 =>
                                $"The parity byte of substream {substream} of the access unit at 0x{offset:X} does not make the XOR of the substream's bytes 0xA9{(count2 > 1 ? $"; {count2 - 1} more substreams differ" : string.Empty)}.");
                        }

                        if (!isCrcValid)
                        {
                            SubstreamCrcFailures++;
                            status |= UnitStatus.CheckFails;
                            isDamaged = true;
                            int substream = k;
                            Findings.Add(Severity.Warning, "truehd.substream-crc-mismatch", () => Nodes.SubstreamOf(records, index, substream), count2 =>
                                $"The CRC-8 of substream {substream} of the access unit at 0x{offset:X} differs from the one computed over the substream's bytes{(count2 > 1 ? $"; {count2 - 1} more substreams differ" : string.Empty)}.");
                        }
                    }
                }
            }
            else if (k < ParsedSubstreams)
            {
                SubstreamsWithoutCheck++;
            }

            _walks[k] = null;
            if (k >= ParsedSubstreams)
            {
                verdicts[k] = verdict;
                continue;
            }

            ReadOnlySpan<byte> content = hasCheck && data.Length >= 2 ? data[..^2] : hasCheck ? default : data;
            Span<int> planes = lent.IsEmpty ? default : lent.Narrow.Slice(k * SubstreamReader.PlanesPerSubstream * n, SubstreamReader.PlanesPerSubstream * n);
            SubstreamState state = _states[k];
            WalkResult walk = SubstreamWalk.Run(state, content, hasCheck, !sync.IsTrueHd, n, Presentation.MaxListSize(sync, k, count), planes, n);
            _walks[k] = walk;
            verdicts[k] = verdict;
            if (!walk.IsRead)
            {
                isReadable = false;
                state.Forget();
                _isIntervalOpen[k] = false;
                SubstreamFaultFinding(index, k, offset, walk.Fault);
                continue;
            }

            foreach (RestartHeader restart in walk.Restarts)
            {
                RestartHeaders++;
                if (!restart.IsCrcValid)
                {
                    RestartCrcFailures++;
                    status |= UnitStatus.RestartCrcFails;
                    isDamaged = true;
                    int substream = k;
                    byte stored = restart.StoredCrc;
                    byte computed = restart.ComputedCrc;
                    Findings.Add(Severity.Warning, "truehd.restart-header-crc-mismatch", () => Nodes.SubstreamOf(records, index, substream), count2 =>
                        $"The restart header in substream {substream} of the access unit at 0x{offset:X} has the CRC-8 0x{stored:X2}, but 0x{computed:X2} is computed over it{(count2 > 1 ? $"; {count2 - 1} more restart headers differ" : string.Empty)}.");
                }
                else
                {
                    verdict |= 4;
                    verdicts[k] = verdict;
                }

                if (_isIntervalOpen[k])
                {
                    LosslessStored[k]++;
                }

                _isIntervalOpen[k] = true;
                if (_presentationChannels[k] == 0)
                {
                    _presentationChannels[k] = restart.LastMatrixChannel + 1;
                }

                CheckAssignments(sync, k, count, restart, index, offset);
            }

            if (walk.Tail.HasEndOfStream)
            {
                _isIntervalOpen[k] = false;
                status |= UnitStatus.HasEndOfStream;
            }

            Notes(walk, k, index, offset, sync);
        }

        int outSamples = 0;
        if (isReadable)
        {
            WalkResult top = _walks[read - 1]!;
            outSamples = top.OutputSamples;
            if (top.IsDropMoreThanHeld)
            {
                int dropped = top.Tail.DroppedSamples;
                int held = top.Samples;
                Findings.Add(Severity.Info, "truehd.unit-short", () => Nodes.SubstreamOf(records, index, read - 1), _ =>
                    $"The end of stream marker in the access unit at 0x{offset:X} drops {Counts.Of(dropped, "sample", "samples")}, more than the {held} the unit holds.", "drops");
            }

            for (int k = 0; k < read - 1; k++)
            {
                if (_walks[k]!.Samples != top.Samples)
                {
                    int substream = k;
                    int samples = _walks[k]!.Samples;
                    int topSamples = top.Samples;
                    Findings.Add(Severity.Info, "truehd.unit-short", () => Nodes.SubstreamOf(records, index, substream), _ =>
                        $"Substream {substream} of the access unit at 0x{offset:X} holds {Counts.Of(samples, "sample", "samples")}, but the highest substream read holds {topSamples}.", "differ");
                    break;
                }
            }

            if (top.Samples < n && !top.Tail.HasEndOfStream)
            {
                status |= UnitStatus.Short;
                int held = top.Samples;
                Findings.Add(Severity.Info, "truehd.unit-short", () => Nodes.Unit(records, index), _ =>
                    $"The access unit at 0x{offset:X} holds {Counts.Of(held, "sample", "samples")} of each channel, fewer than the {n} its sampling rate gives, and no end of stream marker ends the stream there.", "short");
            }

            if (layout.TrailingBytes > 0)
            {
                int trailing = layout.TrailingBytes;
                Findings.Add(Severity.Info, "truehd.unexpected-value", () => Nodes.Unit(records, index), _ =>
                    $"{Counts.Of(trailing, "byte", "bytes")} at the end of the access unit at 0x{offset:X} belong to no substream.", "trailing bytes");
            }
        }
        else
        {
            status |= UnitStatus.SubstreamFault;
        }

        UnitDetail? detail = null;
        if (isFull)
        {
            _fullBytes += bytes.Length;
            detail = new UnitDetail
            {
                Store = R.Store.Add(bytes),
                Kept = bytes.Length,
                IsFull = true,
                KnownCount = count,
                KnownIsMlp = !sync.IsTrueHd,
                States = snapshot,
                Verdicts = verdicts,
                IsReadable = isReadable,
                InForce = sync,
            };
        }
        else if (verdicts.Length > 0)
        {
            detail = new UnitDetail { Verdicts = verdicts, IsReadable = isReadable, KnownCount = count, KnownIsMlp = !sync.IsTrueHd, InForce = sync };
        }

        bool isWalked = isReadable && !lent.IsEmpty;
        return (isReadable, outSamples, isDamaged, isWalked, status, detail);
    }

    // Stores what the nodes of a unit are built from.
    private void Store(int index, ReadOnlyMemory<byte> memory, UnitLayout layout, UnitDetail? detail, UnitStatus status, bool isReadable, MajorSync? inForce)
    {
        if (detail is { IsFull: true })
        {
            R.Details[index] = detail;
            return;
        }

        if ((status & UnitStatus.HasSync) == 0 || _sparseUnits >= MaxSparseUnits || _sparseBytes >= MaxSparseBytes || !layout.IsDirectoryRead)
        {
            return;
        }

        byte[] pieces = Compact(memory.Span, layout);
        _sparseBytes += pieces.Length;
        _sparseUnits++;
        R.Details[index] = new UnitDetail
        {
            Store = R.Store.Add(pieces),
            Kept = pieces.Length,
            IsFull = false,
            KnownCount = layout.SubstreamCount,
            KnownIsMlp = layout.IsMlp,
            Verdicts = detail?.Verdicts ?? [],
            IsReadable = isReadable,
            InForce = layout.Sync is { IsUsable: true } own ? own : inForce,
        };
    }

    // The pieces of a unit that nodes without its blocks need: everything up to the end of the directory, and the first and last bytes of each
    // substream, as runs of (offset, length, bytes).
    internal static byte[] Compact(ReadOnlySpan<byte> unit, UnitLayout layout)
    {
        var runs = new List<(int Start, int End)> { (0, Math.Min(unit.Length, layout.DataStart)) };
        for (int k = 0; k < layout.SubstreamCount; k++)
        {
            DirectoryEntry e = layout.Entries[k];
            int length = e.Stop - e.Start;
            if (length <= 0)
            {
                continue;
            }

            if (length <= HeadBytes + 2)
            {
                runs.Add((e.Start, e.Stop));
            }
            else
            {
                runs.Add((e.Start, e.Start + HeadBytes));
                runs.Add((e.Stop - 2, e.Stop));
            }
        }

        var result = new List<byte>();
        foreach ((int start, int end) in runs)
        {
            result.Add((byte)(start >> 8));
            result.Add((byte)start);
            result.Add((byte)((end - start) >> 8));
            result.Add((byte)(end - start));
            result.AddRange(unit[start..end].ToArray());
        }

        return [.. result];
    }

    // The bytes of each substream's start that a restart header's nodes need: the header at bit 2 takes at most 24.
    internal const int HeadBytes = 32;

    private void StartSession(MajorSync sync)
    {
        int read = Math.Min(_count, ParsedSubstreams);
        int top = read - 1;
        SubstreamState state = _states[top];
        if (!state.HasRestart)
        {
            return;
        }

        Session = _context.StartDecoding(sync.IsTrueHd ? TrueHdCodecParser.TrueHdId : TrueHdCodecParser.MlpId, new DecodeSetup
        {
            Stream = Stream,
            Channels = state.LastMatrixChannel + 1,
            BitsPerSample = sync.BitDepth,
        });
    }

    private void SubstreamFaultFinding(int index, int k, long offset, SubstreamFault fault)
    {
        Records records = R;
        bool isUnsupported = SubstreamFaults.IsUnsupported(fault);
        string code = fault switch
        {
            SubstreamFault.NoRestartHeader => "truehd.substream-without-restart-header",
            SubstreamFault.RestartSync or SubstreamFault.MlpNoiseType or SubstreamFault.ChannelRange or SubstreamFault.MatrixChannelLimit or SubstreamFault.ChannelAssignment => "truehd.restart-header-invalid",
            SubstreamFault.UnsupportedMatrixChannels or SubstreamFault.UnsupportedNegativeShift => "truehd.unsupported",
            SubstreamFault.BlockSize or SubstreamFault.MatrixCount or SubstreamFault.MatrixChannel or SubstreamFault.FractionalBits or SubstreamFault.TooManyReads or SubstreamFault.FilterOrder
                or SubstreamFault.CoefficientBits or SubstreamFault.FirState or SubstreamFault.OrderSum or SubstreamFault.ShiftsDiffer or SubstreamFault.LsbCount => "truehd.parameters-invalid",
            SubstreamFault.EndOfStreamMarker or SubstreamFault.LengthMismatch or SubstreamFault.CheckData => "truehd.substream-end-invalid",
            _ => "truehd.block-invalid",
        };
        Severity severity = isUnsupported ? Severity.Info : Severity.Warning;
        Findings.Add(severity, code, () => Nodes.SubstreamOf(records, index, k), count =>
            $"Substream {k} of the access unit at 0x{offset:X} cannot be read: {SubstreamFaults.Describe(fault)}{(count > 1 ? $"; {count - 1} more substreams fail the same way" : string.Empty)}{(isUnsupported ? string.Empty : "; it is read again from its next restart header")}.", fault.ToString());
    }

    // Findings about what a substream read says without breaking a rule: padding, reserved use and the block size check.
    private void Notes(WalkResult walk, int k, int index, long offset, MajorSync sync)
    {
        Records records = R;
        if (walk.Tail.HasPaddingBitsSet)
        {
            Findings.Add(Severity.Info, "truehd.unexpected-value", () => Nodes.SubstreamOf(records, index, k), _ =>
                $"The padding bits that bring the blocks of substream {k} of the access unit at 0x{offset:X} to a multiple of 16 bits are not all 0.", "padding bits");
        }

        if (walk.HasBlockSizeCheck)
        {
            Findings.Add(Severity.Info, "truehd.block-size-check", () => Nodes.SubstreamOf(records, index, k), _ =>
                walk.HasBlockSizeCheckMismatch
                    ? $"Substream {k} of the access unit at 0x{offset:X} carries the check data after its blocks, and a block's residuals do not end where it says. No file the analyzer was tested on uses the check data, so its reading is unconfirmed."
                    : $"Substream {k} of the access unit at 0x{offset:X} carries the check data after its blocks. No file the analyzer was tested on uses it, so its reading is unconfirmed.");
        }

        if (walk.ChangesMatricesAfterFirstBlock)
        {
            Findings.Add(Severity.Info, "truehd.parameters-change-within-unit", () => Nodes.SubstreamOf(records, index, k), _ =>
                $"A block after the first of substream {k} in the access unit at 0x{offset:X} changes the matrices, output shifts or quantizer step sizes. The analyzer applies the ones in force at the end of the unit to all its samples, as FFmpeg does; no file it was tested on changes them inside a unit, so this is unconfirmed.");
        }
    }

    private void CheckAssignments(MajorSync sync, int k, int count, RestartHeader restart, int index, long offset)
    {
        Records records = R;
        int channels = restart.LastMatrixChannel + 1;
        Speaker[]? list = Presentation.Resolve(sync, k, count, channels);
        int mask = 0;
        bool isDuplicate = false;
        for (int m = 0; m < channels; m++)
        {
            int bit = 1 << restart.Assignment(m);
            isDuplicate |= (mask & bit) != 0;
            mask |= bit;
        }

        if (list is null || isDuplicate || restart.Assignment(channels - 1) >= channels && mask != (1 << channels) - 1)
        {
            string why = list is null
                ? $"none of the channel lists the major sync gives for substream {k} has {Counts.Of(channels, "channel", "channels")}"
                : "two matrix channels share a position";
            Findings.Add(Severity.Info, "truehd.channel-assignment", () => Nodes.SubstreamOf(records, index, k), _ =>
                $"The restart header in substream {k} of the access unit at 0x{offset:X} places {Counts.Of(channels, "channel", "channels")} in a channel list, but {why}; the channels are reported in the order of the matrix channels.");
        }
    }

    private void Figure(long offset, int length, long samples, int available)
    {
        Figures ??= new StreamFigures(Records.StreamRate > 0 ? Records.StreamRate : 48000);
        if (_runUnits == 0)
        {
            _runOffset = offset;
        }

        _runUnits++;
        _runBits += available * 8L;
        _runSamples += samples;
        ShortestUnit = Math.Min(ShortestUnit, available);
        LongestUnit = Math.Max(LongestUnit, available);
        if (_runUnits == RunLength)
        {
            FlushRun();
        }
    }

    public void FlushRun()
    {
        if (_runUnits > 0)
        {
            Figures?.Add(_runOffset, _runBits, _runSamples);
        }

        _runUnits = 0;
        _runBits = 0;
        _runSamples = 0;
    }
}
