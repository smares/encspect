namespace Encspect.Gui.ViewModels;

// The bytes of a file for the hex view, read through the stream on demand, a few pages at a time,
// never the whole file. Reads happen on the UI thread while the view draws, so pages are small
// and the last few are kept.
public sealed class HexDocument : IDisposable
{
    private const int PageSize = 1 << 16;
    private const int PagesKept = 4;

    private readonly Stream _stream;
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

    // The length when the file was opened; a file that grows later shows only these bytes.
    public long Length { get; }

    public static HexDocument Open(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess));

    // Copies the bytes at offset into destination and returns how many there were; fewer at the end
    // of the file, or where it was cut short after it was opened.
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
        _stream.Dispose();
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
            _stream.Position = index * PageSize;
            while (count < PageSize)
            {
                int read = _stream.Read(bytes, count, PageSize - count);
                if (read == 0)
                {
                    break;
                }

                count += read;
            }
        }
        catch (IOException)
        {
            // A file that cannot be read any more shows what was read; the view stays usable.
        }

        _pages.AddFirst((index, bytes, count));
        if (_pages.Count > PagesKept)
        {
            _pages.RemoveLast();
        }

        return (index, bytes, count);
    }
}
