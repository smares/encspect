namespace Encspect.Core;

// The size of the window a parser reads its input through (its Source), which moves forward with the frames: the window size the parser wants,
// or, for a reader of up to 128 KiB (a file of that size, or a slice of one), the length of the reader, so that a folder of small files does not
// allocate and fill a megabyte for every sniff. A reader between 128 KiB and the window size keeps the window size: the memory a Source holds
// then does not grow with the size of the reader, which MemoryTests.DecodingManyTrueHdUnitsHoldsNothingPerUnit checks with readers of about
// 200 and 800 KB.
public static class SourceWindow
{
    private const long WholeReaderLimit = 1 << 17;

    public static int For(long readerLength, int window) => readerLength <= WholeReaderLimit ? (int)Math.Min(readerLength, window) : window;
}
