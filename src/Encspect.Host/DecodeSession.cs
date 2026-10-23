using Encspect.Core;

namespace Encspect.Host;

// One stream's decoding, served to the parser (IDecodeSession): the exception boundary around the decoder, the work
// budget, the front end's stop and the findings the host adds for them. Without a decoder, or without decoding asked for,
// it decodes nothing and only builds the verification from what the parser declares.
internal sealed class DecodeSession : IDecodeSession
{
    private readonly ParserRouter _router;
    private readonly DecodeRun? _run;
    private readonly string _codecId;
    private readonly DecodeSetup _setup;
    private readonly List<Pending> _findings = [];
    private readonly int _bytesPerSample;
    private IFrameDecoder? _decoder;
    private DecodeStatus _status;
    private DecodeRun.GuardedSink? _sink;
    private bool _isFrameWalked;
    private bool _isEnded;
    private bool _isFailed;
    private bool _sawGap;
    private bool _isClosed;
    private int[] _narrow = [];
    private long[] _wide = [];
    private int _lentChannels;
    private int _lentStride;
    private bool _isLentWide;
    private long _framesDecoded;
    private long _framesNotDecoded;
    private bool _isAfterUnreadable;
    private long _decodedSamples;
    private StreamVerification? _verification;

    private sealed class Pending(Severity severity, string code, string message, Node? node, int count)
    {
        public Severity Severity { get; } = severity;

        public string Code { get; } = code;

        public string Message { get; } = message;

        public Node? Node { get; } = node;

        public int Count { get; set; } = count;
    }

    public DecodeSession(ParserRouter router, AnalysisContext owner, DecodeRun? run, string codecId, DecodeSetup setup, DecodeStatus status)
    {
        _router = router;
        Owner = owner;
        _run = run;
        _codecId = codecId;
        _setup = setup;
        _status = status;
        _bytesPerSample = setup.BitsPerSample is >= 1 and <= 64 ? CanonicalPcm.BytesPerSample(setup.BitsPerSample) : 0;
    }

    public AnalysisContext Owner { get; }

    public bool IsDecoding => _decoder is not null && !_isEnded;

    // Opens the decoder; a stream that cannot be decoded is reported (decode.not-possible) and the session decodes nothing.
    public void Open(IDecoder decoder)
    {
        try
        {
            _decoder = decoder.Open(new DecodeContext(_setup, Owner.CancellationToken, AddFromDecoder, DeclareOutput, LentToDecoder));
            _status = DecodeStatus.Decoded;
        }
        catch (InvalidDataException ex)
        {
            NotPossible(ex.Message);
        }
        catch (EndOfStreamException)
        {
            NotPossible("The configuration ends in the middle of a structure.");
        }
        catch (Exception ex) when (Analyzer.IsParserBug(ex))
        {
            Fail($"The {_codecId} decoder failed opening the stream", ex);
        }
    }

    // The most samples over all channels the session lends: 64 MiB of 32-bit samples.
    internal const long MaxLentSamples = 1 << 24;

    public SampleBuffers Lend(int samples, int channels, bool needsWide = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(samples);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        _lentChannels = 0;
        _lentStride = 0;
        if (!IsDecoding || (long)samples * channels > MaxLentSamples)
        {
            return default;
        }

        int total = samples * channels;
        if (_narrow.Length < total)
        {
            _narrow = new int[total];
        }

        if (needsWide && _wide.Length < total)
        {
            _wide = new long[total];
        }

        (_lentChannels, _lentStride, _isLentWide) = (channels, samples, needsWide);
        return LentBuffers();
    }

    // The decoder sees the buffers only for a frame the parser walked into them.
    private SampleBuffers LentToDecoder() => _isFrameWalked ? LentBuffers() : default;

    private SampleBuffers LentBuffers() => _lentChannels == 0
        ? default
        : new SampleBuffers(_narrow.AsSpan(0, _lentChannels * _lentStride), _isLentWide ? _wide.AsSpan(0, _lentChannels * _lentStride) : default, _lentChannels, _lentStride);

    public bool Decode(in CodedFrame incoming)
    {
        CodedFrame frame = incoming;
        if (frame.Bytes is null)
        {
            throw new ArgumentException("A coded frame needs its bytes.", nameof(frame));
        }

        if (!IsDecoding)
        {
            return false;
        }

        DecodeRun run = _run!;
        Owner.CancellationToken.ThrowIfCancellationRequested();
        if (EndIfAsked())
        {
            return false;
        }

        if (_isAfterUnreadable)
        {
            frame = frame with { FollowsGap = true };
            _isAfterUnreadable = false;
        }

        _sawGap |= frame.FollowsGap;
        if (frame.IsUnreadable)
        {
            _framesNotDecoded++;
            _isAfterUnreadable = true;
            return IsDecoding;
        }

        try
        {
            _isFrameWalked = frame.IsWalked;
            int samples = _decoder!.Decode(in frame);
            if (samples < 0)
            {
                throw new InvalidOperationException($"Decode returned {samples} samples.");
            }

            _decodedSamples += samples;
            _framesDecoded++;
            run.Count(samples, _setup.Channels, _bytesPerSample);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            _framesNotDecoded++;
            string why = ex is EndOfStreamException ? "the data ends inside the frame's coded audio" : ex.Message;
            Add(Severity.Warning, "decode.frame-invalid", $"The {_codecId} decoder could not decode the frame at 0x{frame.Bytes.FileStart:X}: {why}.", FrameNode(frame), 1);
        }
        catch (Exception ex) when (Analyzer.IsParserBug(ex))
        {
            Fail($"The {_codecId} decoder failed decoding the frame at 0x{frame.Bytes.FileStart:X}", ex);
        }

        _isFrameWalked = false;
        if (IsDecoding)
        {
            EndIfAsked();
        }

        return IsDecoding;
    }

    public StreamVerification Finish(IReadOnlyList<ChecksumTally> stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        if (_verification is not null)
        {
            return _verification;
        }

        // A parser that declares an inconsistent tally has a bug, as one that declares two of a name: parse.failed.
        if (stored.Select(t => t.Name).Distinct().Count() != stored.Count)
        {
            throw new ArgumentException("Two checksums have the same name.", nameof(stored));
        }

        foreach (ChecksumTally tally in stored)
        {
            if (tally.Stored < 0 || tally.Checked < 0 || tally.Failed < 0 || tally.Checked > tally.Stored || tally.Failed > tally.Checked)
            {
                throw new ArgumentException($"The checksum \"{tally.Name}\" declares {tally.Checked} of {tally.Stored} compared and {tally.Failed} failed, which cannot be.", nameof(stored));
            }

            if (tally.Covers == ChecksumCoverage.Header && tally.CoversAllAudio)
            {
                throw new ArgumentException($"The header checksum \"{tally.Name}\" cannot cover all of the audio.", nameof(stored));
            }
        }

        IReadOnlyList<ChecksumResult> results = Close(isFinishing: true, stored);
        var tallies = new List<ChecksumTally>(stored.Count);
        foreach (ChecksumTally tally in stored)
        {
            // The host fills in what the decoder compared of the checksums over decoded audio.
            ChecksumResult? result = tally.Covers == ChecksumCoverage.DecodedAudio ? results.FirstOrDefault(r => r.Name == tally.Name) : null;
            tallies.Add(tally.Covers == ChecksumCoverage.DecodedAudio ? tally with { Checked = result?.Checked ?? 0, Failed = result?.Failed ?? 0 } : tally);
        }

        return _verification = new StreamVerification
        {
            Decoding = _status,
            DecodedSamples = _decodedSamples,
            IsLossy = _setup.IsLossy,
            Checksums = tallies,
        };
    }

    // The route or the format parser returned without Finish: the decoder still reports what it counted once per stream,
    // and its results are dropped.
    public void EndUnfinished()
    {
        if (!_isClosed)
        {
            Close(isFinishing: false, []);
        }
    }

    // Ends the decoding, once: asks the decoder for its results, flushes the findings to the owner and ends the sink.
    private IReadOnlyList<ChecksumResult> Close(bool isFinishing, IReadOnlyList<ChecksumTally> stored)
    {
        IReadOnlyList<ChecksumResult> results = [];
        if (_isClosed)
        {
            return results;
        }

        _isClosed = true;
        IFrameDecoder? decoder = _decoder;
        _decoder = null;
        if (decoder is not null && !_isFailed)
        {
            bool isComplete = isFinishing && !_isEnded && !_sawGap && _framesNotDecoded == 0 && (_setup.Samples is not { } expected || expected == _decodedSamples);
            try
            {
                results = decoder.Finish(new DecodeEnd(isComplete, _framesNotDecoded));
                if (isFinishing)
                {
                    results = Validated(results, stored);
                }
            }
            catch (Exception ex) when (Analyzer.IsParserBug(ex))
            {
                Fail($"The {_codecId} decoder failed ending the stream", ex);
                results = [];
            }

            if (!isFinishing)
            {
                results = [];
            }
        }

        _sink?.EndByHost(_decodedSamples);

        foreach (Pending finding in _findings)
        {
            Owner.AddFinding(finding.Severity, finding.Code, finding.Message, finding.Node ?? _setup.Stream, finding.Count);
        }

        _findings.Clear();
        _router.Release(this);
        return results;
    }

    // A result that names a checksum the parser did not declare over decoded audio, names one twice, or compares more
    // values than are stored is a decoder bug.
    private IReadOnlyList<ChecksumResult> Validated(IReadOnlyList<ChecksumResult> results, IReadOnlyList<ChecksumTally> stored)
    {
        if (results is null)
        {
            Fail($"The {_codecId} decoder returned no list of results", null);
            return [];
        }

        var seen = new HashSet<string>();
        foreach (ChecksumResult result in results)
        {
            ChecksumTally? tally = result is null ? null : stored.FirstOrDefault(t => t.Name == result.Name && t.Covers == ChecksumCoverage.DecodedAudio);
            if (result is null || tally is null || !seen.Add(result.Name) || result.Checked < 0 || result.Failed < 0 || result.Failed > result.Checked || result.Checked > tally.Stored)
            {
                Fail($"The {_codecId} decoder returned a result for {(result is null ? "nothing" : $"\"{result.Name}\"")} that no declared checksum over decoded audio can hold", null);
                return [];
            }
        }

        return results;
    }

    private bool EndIfAsked()
    {
        DecodeRun run = _run!;
        if (run.HasSinkFailure)
        {
            _isEnded = true;
        }
        else if (run.IsStopRequested)
        {
            _isEnded = true;
            Add(Severity.Info, "decode.stopped", $"Decoding was stopped after {Counts.Of(_framesDecoded, "frame", "frames")}.", null, 1);
        }
        else if (run.IsOutOfBudget)
        {
            _isEnded = true;
            Add(Severity.Info, "decode.limit", $"Decoding stopped after {Counts.Of(_framesDecoded, "frame", "frames")}: the PCM it produced passed the budget of {Counts.Of(run.Budget, "byte", "bytes")}.", null, 1);
        }

        if (_isEnded && _status == DecodeStatus.Decoded)
        {
            _status = DecodeStatus.Stopped;
        }

        return _isEnded;
    }

    // Called by the router for a stream whose channels or sample width cannot be decoded.
    public void RefuseSetup()
    {
        NotPossible($"it declares {Counts.Of(_setup.Channels, "channel", "channels")} of {Counts.Of(_setup.BitsPerSample, "bit", "bits")}.");
    }

    private void NotPossible(string message)
    {
        _status = DecodeStatus.NotPossible;
        Add(Severity.Info, "decode.not-possible", $"The stream was not decoded: {message}", null, 1);
    }

    private void Fail(string what, Exception? ex)
    {
        _isFailed = true;
        _isEnded = true;
        _status = DecodeStatus.Failed;
        Add(Severity.Error, "decode.failed", ex is null ? $"{what}." : $"{what}: {ex.GetType().Name}: {ex.Message}", null, 1);
    }

    // Findings of one code are reported once with a count, pointing to the first.
    private void Add(Severity severity, string code, string message, Node? node, int count)
    {
        Pending? existing = _findings.Find(f => f.Code == code);
        if (existing is null)
        {
            _findings.Add(new Pending(severity, code, message, node, count));
        }
        else
        {
            existing.Count += count;
        }
    }

    private void AddFromDecoder(Severity severity, string code, string message, Node? node, int count) => Add(severity, code, message, node, count);

    private IPcmSink DeclareOutput(PcmFormat format)
    {
        if (_sink is not null)
        {
            throw new InvalidOperationException("DeclareOutput is called once per stream.");
        }

        return _sink = _run!.OpenSink(new DecodedStream(_codecId, _setup.Stream, format));
    }

    private static Node FrameNode(in CodedFrame frame)
    {
        MappedBytes bytes = frame.Bytes;
        IReadOnlyList<ByteRange> ranges = bytes.FileRanges();
        return new Node(frame.NodeName, bytes.FileStart, bytes.FileEnd - bytes.FileStart) { Ranges = ranges.Count > 1 ? ranges : null };
    }
}
