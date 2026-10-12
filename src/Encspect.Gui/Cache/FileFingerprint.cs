using System.Security.Cryptography;
using Encspect.Host;

namespace Encspect.Gui.Cache;

// What the cache knows a file by, besides its path: its size, last-write time and file ID, and a
// hash of its first and last 64 KiB, which catches a tagger that rewrites a tag in place and keeps
// the time. Never a hash of the whole file, which would cost as much reading as the analysis.
public sealed record FileFingerprint(long Size, long LastWriteTicks, FileId? Id, byte[] Hash)
{
    public const int EdgeLength = 64 * 1024;

    public const int HashLength = 32;

    // Null for anything but a regular file, or a file that cannot be read: those are analyzed,
    // which reports why, and never cached. Opened as the analysis opens files (FileType), so a FIFO
    // does not block.
    public static FileFingerprint? Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        try
        {
            using FileStream? stream = FileType.Open(path, out _);
            if (stream is null)
            {
                return null;
            }

            long size = stream.Length;
            long ticks = File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks;
            FileId? id = FileId.Of(stream.SafeFileHandle);

            byte[] edges = new byte[(int)Math.Min(size, 2L * EdgeLength)];
            int head = (int)Math.Min(size, EdgeLength);
            int tail = edges.Length - head;
            if (RandomAccess.Read(stream.SafeFileHandle, edges.AsSpan(0, head), 0) != head
                || RandomAccess.Read(stream.SafeFileHandle, edges.AsSpan(head, tail), size - tail) != tail)
            {
                return null;
            }

            return new FileFingerprint(size, ticks, id, SHA256.HashData(edges));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool Matches(FileFingerprint other) =>
        other is not null && Size == other.Size && LastWriteTicks == other.LastWriteTicks && Id == other.Id && Hash.AsSpan().SequenceEqual(other.Hash);
}
