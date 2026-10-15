using System.Security.Cryptography;
using System.Text;
using Encspect.Core;
using Encspect.Gui.ViewModels;
using Encspect.Host;

namespace Encspect.Gui.Cache;

// The per-user cache of the summaries a folder view shows, so a folder seen before fills its rows
// without analyzing its files again. An entry is used only while every part of its key still holds:
// the path, the file's fingerprint (size, last-write time, file ID, and a hash of its first and last
// 64 KiB), the analyzer version and the report schema version. Only folder views read it: opening
// a file always analyzes it again, and the CLI keeps no cache.
//
// The store is one file of the format below, read whole when first needed. Writes are batched
// and made off the UI thread a moment after the last change. Each write merges the entries on disk
// with this process's changes, writes a temporary file and renames it over the store, so a reader
// never sees half a file and two GUI instances at once at worst lose one batch of the other's
// entries. A store that is corrupt, truncated or of another format version reads as empty and is
// replaced by the next write. Past MaxEntries, the least recently used entries are dropped. A hit
// only notes when the entry was used, in memory: that alone is no reason to rewrite the store, so
// it is written with the next new entry or by the final Flush.
//
// Format, little-endian as BinaryWriter writes it: the magic line, the format version, the
// analyzer versions in use, the number of entries, the entries (path, fingerprint, analyzer version
// index, schema version, when last used, the summary but its source, which is the path), and a
// SHA-256 of all that.
public sealed class SummaryCache : IDisposable
{
    public const int FormatVersion = 3;

    public const int DefaultMaxEntries = 50_000;

    // A store larger than any the entry bound allows is no store of ours.
    private const long MaxFileLength = 512L * 1024 * 1024;

    private static readonly byte[] Magic = "Encspect summary cache\n"u8.ToArray();

    private readonly object _lock = new();
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly string _analyzerVersion;
    private readonly int _schemaVersion;
    private readonly TimeSpan _flushDelay;
    private readonly Func<long> _clock;
    private Dictionary<string, Entry>? _entries;
    private Dictionary<string, Entry> _dirty = new(StringComparer.Ordinal);
    private Dictionary<string, long> _touched = new(StringComparer.Ordinal);
    private (long Length, long Ticks)? _seen;
    private bool _isFlushScheduled;
    private bool _isDisposed;

    public SummaryCache(string path, string analyzerVersion, int schemaVersion = ReportJsonWriter.SchemaVersion, int maxEntries = DefaultMaxEntries, TimeSpan? flushDelay = null, Func<long>? clock = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(analyzerVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);

        FilePath = Path.GetFullPath(path);
        _analyzerVersion = analyzerVersion;
        _schemaVersion = schemaVersion;
        MaxEntries = maxEntries;
        _flushDelay = flushDelay ?? TimeSpan.FromSeconds(2);
        _clock = clock ?? (() => DateTime.UtcNow.Ticks);
    }

    // Where the operating system keeps a user's caches: %LOCALAPPDATA% on Windows, ~/Library/Caches
    // on macOS, $XDG_CACHE_HOME or ~/.cache elsewhere.
    public static string DefaultPath
    {
        get
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
            string root = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify)
                : OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Caches")
                : Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg && Path.IsPathRooted(xdg) ? xdg
                : Path.Combine(home, ".cache");
            return Path.Combine(root, "Encspect", "summaries.bin");
        }
    }

    public string FilePath { get; }

    public int MaxEntries { get; }

    // Entries held in memory, written or not.
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return Loaded().Count;
            }
        }
    }

    // The summary cached for the file, if its key still holds; null otherwise. A hit counts as a
    // use, which keeps the entry from being evicted.
    public FileSummary? TryGet(string path, FileFingerprint fingerprint)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(fingerprint);

        lock (_lock)
        {
            if (!Loaded().TryGetValue(path, out Entry? entry)
                || entry.AnalyzerVersion != _analyzerVersion
                || entry.SchemaVersion != _schemaVersion
                || !entry.Fingerprint.Matches(fingerprint))
            {
                return null;
            }

            entry = entry with { LastUsed = _clock() };
            _entries![path] = entry;
            _touched[path] = entry.LastUsed;
            return entry.Summary;
        }
    }

    public void Put(string path, FileFingerprint fingerprint, FileSummary summary)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(summary);

        lock (_lock)
        {
            var entry = new Entry(fingerprint, _analyzerVersion, _schemaVersion, _clock(), summary);
            Loaded()[path] = entry;
            MarkDirty(path, entry);
        }
    }

    // Writes the changes not written yet, also when entries were last used, merged with what is
    // on disk now.
    public void Flush() => Flush(isFinal: true);

    private void Flush(bool isFinal)
    {
        _writing.Wait();
        try
        {
            Dictionary<string, Entry> dirty;
            Dictionary<string, long> touched;
            lock (_lock)
            {
                _isFlushScheduled = false;
                if (_dirty.Count == 0 && (!isFinal || _touched.Count == 0))
                {
                    return;
                }

                dirty = _dirty;
                touched = _touched;
                _dirty = new(StringComparer.Ordinal);
                _touched = new(StringComparer.Ordinal);
            }

            Dictionary<string, Entry> merged;
            if (State() is { } state && state == _seen)
            {
                // Nobody wrote the store since this process read or wrote it, so memory holds it.
                lock (_lock)
                {
                    merged = new(_entries!, StringComparer.Ordinal);
                }
            }
            else
            {
                merged = Read() ?? new(StringComparer.Ordinal);
                foreach ((string path, Entry entry) in dirty)
                {
                    if (!merged.TryGetValue(path, out Entry? other) || other.LastUsed <= entry.LastUsed)
                    {
                        merged[path] = entry;
                    }
                }
            }

            Touch(merged, touched);
            Evict(merged);
            bool isWritten = Write(merged);
            lock (_lock)
            {
                // Changes made while the store was written stay in _dirty for the next write.
                foreach ((string path, Entry entry) in _dirty)
                {
                    merged[path] = entry;
                }

                Touch(merged, _touched);
                if (!isWritten)
                {
                    foreach ((string path, Entry entry) in dirty)
                    {
                        _dirty.TryAdd(path, entry);
                    }

                    foreach ((string path, long lastUsed) in touched)
                    {
                        _touched.TryAdd(path, lastUsed);
                    }
                }

                _entries = merged;
            }
        }
        finally
        {
            _writing.Release();
        }
    }

    // Forgets every entry, also those other instances wrote, and deletes the store.
    public void Clear()
    {
        _writing.Wait();
        try
        {
            lock (_lock)
            {
                _entries = new(StringComparer.Ordinal);
                _dirty.Clear();
                _touched.Clear();
                _seen = null;
            }

            try
            {
                File.Delete(FilePath);
                if (Directory.Exists(Path.GetDirectoryName(FilePath)))
                {
                    foreach (string temporary in Directory.EnumerateFiles(Path.GetDirectoryName(FilePath)!, Path.GetFileName(FilePath) + ".*.tmp"))
                    {
                        File.Delete(temporary);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        finally
        {
            _writing.Release();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }

        Flush();
    }

    private Dictionary<string, Entry> Loaded()
    {
        if (_entries is null)
        {
            _seen = State();
            _entries = Read() ?? new(StringComparer.Ordinal);
        }

        return _entries;
    }

    private void MarkDirty(string path, Entry entry)
    {
        _dirty[path] = entry;
        if (_isFlushScheduled || _isDisposed)
        {
            return;
        }

        _isFlushScheduled = true;
        _ = Task.Delay(_flushDelay).ContinueWith(_ => Flush(isFinal: false), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private static void Touch(Dictionary<string, Entry> entries, Dictionary<string, long> touched)
    {
        foreach ((string path, long lastUsed) in touched)
        {
            if (entries.TryGetValue(path, out Entry? entry) && entry.LastUsed < lastUsed)
            {
                entries[path] = entry with { LastUsed = lastUsed };
            }
        }
    }

    private void Evict(Dictionary<string, Entry> entries)
    {
        if (entries.Count <= MaxEntries)
        {
            return;
        }

        foreach (string path in entries.OrderByDescending(e => e.Value.LastUsed).Skip(MaxEntries).Select(e => e.Key).ToList())
        {
            entries.Remove(path);
        }
    }

    private (long Length, long Ticks)? State()
    {
        try
        {
            var info = new FileInfo(FilePath);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // The store's entries, or null when it is missing, unreadable or not a store of this format.
    private Dictionary<string, Entry>? Read()
    {
        byte[] bytes;
        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxFileLength || stream.Length < Magic.Length + SHA256.HashSizeInBytes)
            {
                return null;
            }

            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        int body = bytes.Length - SHA256.HashSizeInBytes;
        if (!bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic) || !SHA256.HashData(bytes.AsSpan(0, body)).AsSpan().SequenceEqual(bytes.AsSpan(body)))
        {
            return null;
        }

        try
        {
            using var reader = new BinaryReader(new MemoryStream(bytes, Magic.Length, body - Magic.Length), Encoding.UTF8);
            if (reader.ReadInt32() != FormatVersion)
            {
                return null;
            }

            string[] versions = new string[Bounded(reader.ReadInt32(), 1 << 16)];
            for (int i = 0; i < versions.Length; i++)
            {
                versions[i] = reader.ReadString();
            }

            int count = Bounded(reader.ReadInt32(), 1 << 24);
            var entries = new Dictionary<string, Entry>(count, StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                string path = reader.ReadString();
                entries[path] = ReadEntry(reader, path, versions);
            }

            return entries;
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or IOException or ArgumentException or FormatException or OverflowException)
        {
            return null;
        }
    }

    // A summary's source is the path it is stored under, so it is not stored twice.
    private static Entry ReadEntry(BinaryReader reader, string path, string[] versions)
    {
        long size = reader.ReadInt64();
        long ticks = reader.ReadInt64();
        FileId? id = reader.ReadBoolean() ? new FileId(reader.ReadUInt64(), reader.ReadUInt64()) : null;
        byte[] hash = reader.ReadBytes(FileFingerprint.HashLength);
        if (hash.Length != FileFingerprint.HashLength)
        {
            throw new EndOfStreamException();
        }

        string version = versions[Bounded(reader.ReadInt32(), versions.Length - 1)];
        int schemaVersion = reader.ReadInt32();
        long lastUsed = reader.ReadInt64();

        var summary = new ReportSummary(
            path,
            ReadText(reader),
            reader.ReadInt32(),
            ReadText(reader),
            reader.ReadBoolean() ? reader.ReadDouble() : null,
            reader.ReadBoolean() ? reader.ReadInt64() : null,
            reader.ReadBoolean() ? reader.ReadDouble() : null,
            reader.ReadBoolean() ? reader.ReadInt32() : null,
            ReadText(reader),
            reader.ReadBoolean() ? ReadLayout(reader) : null,
            ReadText(reader),
            reader.ReadBoolean() ? ReadConfidence(reader) : null,
            [.. Enumerable.Range(0, Bounded(reader.ReadInt32(), 1 << 10)).Select(_ => reader.ReadString())],
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadBoolean(),
            reader.ReadBoolean() ? reader.ReadInt32() : null);
        bool? isVbr = reader.ReadBoolean() ? reader.ReadBoolean() : null;
        string? problem = ReadText(reader);
        return new Entry(new FileFingerprint(size, ticks, id, hash), version, schemaVersion, lastUsed, new FileSummary(summary, isVbr, problem));
    }

    private static Confidence ReadConfidence(BinaryReader reader)
    {
        var confidence = (Confidence)reader.ReadInt32();
        return Enum.IsDefined(confidence) ? confidence : throw new InvalidDataException("Unknown confidence.");
    }

    private static Speaker[] ReadLayout(BinaryReader reader)
    {
        var layout = new Speaker[Bounded(reader.ReadInt32(), 1 << 10)];
        for (int i = 0; i < layout.Length; i++)
        {
            var speaker = (Speaker)reader.ReadByte();
            layout[i] = Enum.IsDefined(speaker) ? speaker : throw new InvalidDataException("Unknown speaker.");
        }

        return layout;
    }

    private static string? ReadText(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;

    private static int Bounded(int value, int max) => value >= 0 && value <= max ? value : throw new InvalidDataException("Out of range.");

    private bool Write(Dictionary<string, Entry> entries)
    {
        var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            List<string> versions = [.. entries.Values.Select(e => e.AnalyzerVersion).Distinct()];
            writer.Write(versions.Count);
            foreach (string version in versions)
            {
                writer.Write(version);
            }

            writer.Write(entries.Count);
            foreach ((string path, Entry entry) in entries)
            {
                writer.Write(path);
                WriteEntry(writer, entry, versions.IndexOf(entry.AnalyzerVersion));
            }
        }

        byte[] hash = SHA256.HashData(body.GetBuffer().AsSpan(0, (int)body.Length));
        body.Write(hash);

        string temporary = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(body.GetBuffer().AsSpan(0, (int)body.Length));
            }

            // Windows refuses to replace a file another process has open without sharing delete;
            // the readers here share it, so a refusal is short-lived.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temporary, FilePath, overwrite: true);
                    break;
                }
                catch (Exception ex) when (attempt < 4 && ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(25 * (attempt + 1));
                }
            }

            _seen = State();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            return false;
        }
    }

    private static void WriteEntry(BinaryWriter writer, Entry entry, int version)
    {
        FileFingerprint fingerprint = entry.Fingerprint;
        writer.Write(fingerprint.Size);
        writer.Write(fingerprint.LastWriteTicks);
        writer.Write(fingerprint.Id is not null);
        if (fingerprint.Id is { } id)
        {
            writer.Write(id.Volume);
            writer.Write(id.File);
        }

        writer.Write(fingerprint.Hash);
        writer.Write(version);
        writer.Write(entry.SchemaVersion);
        writer.Write(entry.LastUsed);

        ReportSummary summary = entry.Summary.Summary;
        WriteText(writer, summary.Format);
        writer.Write(summary.Streams);
        WriteText(writer, summary.Codec);
        WriteOptional(writer, summary.Duration, writer.Write);
        WriteOptional(writer, summary.Samples, writer.Write);
        WriteOptional(writer, summary.AverageBitrate, writer.Write);
        WriteOptional(writer, summary.SampleRate, writer.Write);
        WriteText(writer, summary.ChannelMode);
        writer.Write(summary.ChannelLayout is not null);
        if (summary.ChannelLayout is { } layout)
        {
            writer.Write(layout.Count);
            foreach (Speaker speaker in layout)
            {
                writer.Write((byte)speaker);
            }
        }

        WriteText(writer, summary.Encoder);
        WriteOptional(writer, (int?)summary.EncoderConfidence, writer.Write);
        writer.Write(summary.Tags.Count);
        foreach (string tag in summary.Tags)
        {
            writer.Write(tag);
        }

        writer.Write(summary.Errors);
        writer.Write(summary.Warnings);
        writer.Write(summary.Infos);
        writer.Write(summary.HasObjectAudio);
        WriteOptional(writer, summary.ObjectCount, writer.Write);
        WriteOptional(writer, entry.Summary.IsVbr, writer.Write);
        WriteText(writer, entry.Summary.Problem);
    }

    private static void WriteText(BinaryWriter writer, string? text)
    {
        writer.Write(text is not null);
        if (text is not null)
        {
            writer.Write(text);
        }
    }

    private static void WriteOptional<T>(BinaryWriter writer, T? value, Action<T> write)
        where T : struct
    {
        writer.Write(value.HasValue);
        if (value is { } present)
        {
            write(present);
        }
    }

    private sealed record Entry(FileFingerprint Fingerprint, string AnalyzerVersion, int SchemaVersion, long LastUsed, FileSummary Summary);
}
