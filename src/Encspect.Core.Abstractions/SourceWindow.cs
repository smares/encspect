namespace Encspect.Core;

// The size of the window a parser reads a file through (its Source), which moves forward with the frames and holds a megabyte, or as much as
// the frame in hand needs. A file of up to 128 KiB is held whole instead: a megabyte for each sniff of a folder of small files was a fifth of
// the time of a scan. Larger files keep the minimum so that what a Source holds does not grow with the size of the file (the memory tests
// of the decoders hold files of a few hundred kilobytes to it).
public static class SourceWindow
{
    private const long WholeFileLimit = 1 << 17;

    public static int For(long length, int minimum) => length <= WholeFileLimit ? (int)Math.Min(length, minimum) : minimum;
}
