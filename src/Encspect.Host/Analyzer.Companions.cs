using Encspect.Core;

namespace Encspect.Host;

public sealed partial class Analyzer
{
    // The companion files one analysis opened (ParseContext.OpenCompanion): prepared as a file is, with its tags found at both ends and its data window cut by
    // the end the format declares, so that the tree a parser builds in a pair is the tree it gets alone, and finished into a report of its own once the
    // main file's parser has returned. Disposing it closes the streams.
    internal sealed class CompanionSession(Analyzer analyzer, string input, CompanionSource source, CancellationToken cancellationToken) : IDisposable
    {
        private readonly List<Stream> _streams = [];
        private readonly List<Run> _runs = [];
        private readonly Dictionary<string, Companion> _opened = [];
        private IFormatParser? _parser;
        private ParserRouter? _router;
        private Exception? _failure;

        public void ThrowIfFailed()
        {
            if (_failure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_failure).Throw();
            }
        }

        public void Begin(IFormatParser parser, ParserRouter router)
        {
            _parser = parser;
            _router = router;
        }

        public Companion Open(string extension)
        {
            if (_opened.TryGetValue(extension, out Companion? opened))
            {
                return opened;
            }

            CompanionSource.Lookup lookup;
            try
            {
                lookup = source.Find(input, extension);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A caller's function that throws something else is not a parser bug: it reaches the caller of Analyze once the format parser has returned, as a sink's does.
                _failure ??= ex;
                string name = CompanionSource.NameOf(input, extension);
                Companion failed = Companion.Unchecked(name, "the caller's function failed", isUnreadable: false);
                _opened[extension] = failed;
                return failed;
            }

            Companion companion = Prepare(lookup);
            _opened[extension] = companion;
            return companion;
        }

        private Companion Prepare(CompanionSource.Lookup lookup)
        {
            if (lookup.Unchecked is { } reason)
            {
                return Companion.Unchecked(lookup.Name, reason, lookup.IsUnreadable);
            }

            if (lookup.IsMissing || lookup.Stream is not { } stream)
            {
                return Companion.Missing(lookup.Name);
            }

            _streams.Add(stream);
            try
            {
                return Read(lookup, stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Companion.Unchecked(lookup.Name, ex.Message, isUnreadable: true);
            }
        }

        private Companion Read(CompanionSource.Lookup lookup, Stream stream)
        {
            ParserRouter router = _router!;
            var reader = new ByteReader(stream, cancellationToken);
            var root = new Node("Correction File", 0, reader.Length) { Description = HostText.CorrectionFile, Value = _parser!.Name };
            var findings = new List<Finding>();
            var pending = new List<PendingFinding>();
            var failures = new List<Failure>();
            if (reader.Length == 0)
            {
                findings.Add(new Finding(Severity.Warning, "file.empty", "The file is empty.", root) { Path = [] });
                var emptyContext = new ParseContext(reader, root, [], [], router);
                _runs.Add(new Run(lookup, root, emptyContext, null, findings, pending, failures));
                return Companion.Found(lookup.Name, emptyContext);
            }

            string extension = Path.GetExtension(lookup.Name);
            TagScan scan = analyzer.ScanTags(reader, extension, _parser, root, findings, pending, failures);
            long embeddedBytes = scan.Embedded.Sum(c => c.Tag.Length);
            ByteReader window;
            List<Node> inWindow = [];
            List<EncoderClaim> claims = [];
            if (scan.DataEnd - scan.DataStart > embeddedBytes)
            {
                claims = scan.Leading.Concat(scan.Embedded).Concat(scan.Trailing).OrderBy(c => c.Tag.Offset).SelectMany(c => c.Probe.EncoderClaims).ToList();
                long formatEnd = scan.Container is not null && scan.Embedded.Count > 0 ? scan.Embedded[0].Tag.Offset : scan.DataEnd;
                window = reader.Slice(scan.DataStart, formatEnd - scan.DataStart);
                inWindow = scan.Embedded.Where(c => c.Tag.Offset < formatEnd).Select(c => scan.Shown[c.Tag]).ToList();
                AddDataBetweenTags(root, scan.Embedded.Where(c => c.Tag.Offset >= formatEnd), scan.DataEnd, findings);
            }
            else
            {
                findings.Add(new Finding(Severity.Warning, "layout.tags-only", "The file holds only tags; no bytes are left for audio data.", root));
                window = reader.Slice(scan.DataStart, 0);
            }

            var context = new ParseContext(window, root, inWindow, claims, router);
            _runs.Add(new Run(lookup, root, context, scan, findings, pending, failures));
            return Companion.Found(lookup.Name, context);
        }

        // The reports of the companions the parser read, once it has returned. Sessions a parser left open are ended first, as the main file's are.
        public IReadOnlyList<CompanionReport> Finish(DecodeRun? decoding)
        {
            var reports = new List<CompanionReport>();
            foreach (Run run in _runs)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    _router!.EndOpenSessions(run.Context);
                }

                Node root = run.Root;
                root.SortChildrenByOffset();
                var findings = run.Findings;
                if (run.Scan is { } scan)
                {
                    findings.AddRange(scan.Layout);
                    foreach (PendingFinding finding in run.Pending)
                    {
                        Node node = DeepestNodeAt(root, finding.Offset, scan.DetectionNodes);
                        findings.Add(new Finding(finding.Severity, finding.Code, finding.Message(node, root), node));
                    }
                }

                findings.AddRange(run.Failures.Select(f => new Finding(Severity.Error, "parse.failed", $"The {f.Parser} parser failed {f.Activity}: {f.Error.GetType().Name}: {f.Error.Message}", root)));
                findings.AddRange(run.Context.Findings);
                var paths = new TreePaths(root);
                Dictionary<Node, Node> shown = run.Scan?.Shown ?? [];
                List<Finding> ordered = findings
                    .Select(f => f.Node is { } node && shown.TryGetValue(node, out Node? copy) ? f with { Node = copy } : f)
                    .OrderBy(f => f.Node?.Offset ?? 0)
                    .Select(f => f with { Path = paths.Find(f.Node) })
                    .ToList();
                List<StreamInfo> streams = run.Context.Streams
                    .Select(s => s with { NodePaths = s.Nodes.Select(paths.Find).ToList(), Measurements = WithChannelReason(s, decoding?.RecordFor(s) ?? s.Measurements) })
                    .ToList();
                reports.Add(new CompanionReport(CompanionReport.Correction, new Report(run.Lookup.Path, root, ordered, streams)));
            }

            return reports;
        }

        public void Dispose()
        {
            foreach (Stream stream in _streams)
            {
                stream.Dispose();
            }
        }

        private sealed record Run(CompanionSource.Lookup Lookup, Node Root, ParseContext Context, TagScan? Scan, List<Finding> Findings, List<PendingFinding> Pending, List<Failure> Failures);
    }
}
