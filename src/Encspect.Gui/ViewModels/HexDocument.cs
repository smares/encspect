namespace Encspect.Gui.ViewModels;

// The bytes of a file for the hex view, read on demand, a few pages at a time, never the whole
// file. Reads happen on the UI thread while the view draws, so pages are small and the last few
// are kept.
//
// A file is opened only for each page it reads and closed at once: Windows refuses to replace a
// file while any handle to it is open, even one that shares deletion, so a held handle would keep a
// tagger from saving a file the detail window shows. Once the file's length or last-write time no
// longer matches the analysis, nothing more is read from it, so the view never shows bytes that the
// tree does not describe; pages read before stay, as they are the analyzed file's.
public sealed class HexDocument : IDisposable
{
    private const int PageSize = 1 << 16;
    private const int PagesKept = 4;

    private readonly Stream? _stream;
    private readonly string? _path;
    private readonly DateTime _lastWrite;
    private readonly LinkedList<(long Index, byte[] Bytes, int Count)> _pages = new();

    public HexDocument(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new ArgumentException("The hex view needs a stream that reads and seeks.", nameof(stream));
        }

        _stream = stream;
        Length = stream.Length;
    }

    private HexDocument(string path, long length, DateTime lastWrite)
    {
        _path = path;
        Length = length;
        _lastWrite = lastWrite;
    }

    // Raised once, on the thread that read, when the file turns out to have changed on disk.
    public event EventHandler? Changed;

    // The length when the file was analyzed.
    public long Length { get; }

    public bool HasChanged { get; private set; }

    // The file at path as it was when its analysis started: its length and last-write time then.
    public static HexDocument Open(string path, long length, DateTime lastWrite)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new HexDocument(path, length, lastWrite);
    }

    // Copies the bytes at offset into destination and returns how many there were; fewer at the end
    // of the file, or where it was cut short or changed after it was analyzed.
    public int Read(long offset, Span<byte> destination)
    {
        int copied = 0;
        while (copied < destination.Length && offset + copied < Length)
        {
            long position = offset + copied;
            (long _, byte[] bytes, int count) = Page(position / PageSize);
            int within = (int)(position % PageSize);
            if (within >= count)
            {
                break;
            }

            int take = Math.Min(count - within, destination.Length - copied);
            bytes.AsSpan(within, take).CopyTo(destination[copied..]);
            copied += take;
        }

        return copied;
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _pages.Clear();
    }

    private (long Index, byte[] Bytes, int Count) Page(long index)
    {
        for (LinkedListNode<(long Index, byte[] Bytes, int Count)>? node = _pages.First; node is not null; node = node.Next)
        {
            if (node.Value.Index == index)
            {
                _pages.Remove(node);
                _pages.AddFirst(node);
                return node.Value;
            }
        }

        byte[] bytes = new byte[PageSize];
        int count = 0;
        try
        {
            if (_stream is not null)
            {
                count = ReadPage(_stream, index, bytes);
            }
            else if (!HasChanged)
            {
                using var file = new FileStream(_path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
                if (file.Length != Length || File.GetLastWriteTimeUtc(_path!) != _lastWrite)
                {
                    HasChanged = true;
                    Changed?.Invoke(this, EventArgs.Empty);
                    return (index, bytes, 0);
                }

                count = ReadPage(file, index, bytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be read any more shows what was read; the view stays usable.
            return (index, bytes, count);
        }

        _pages.AddFirst((index, bytes, count));
        if (_pages.Count > PagesKept)
        {
            _pages.RemoveLast();
        }

        return (index, bytes, count);
    }

    private static int ReadPage(Stream stream, long index, byte[] bytes)
    {
        stream.Position = index * PageSize;
        int count = 0;
        while (count < PageSize)
        {
            int read = stream.Read(bytes, count, PageSize - count);
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        return count;
    }
}
