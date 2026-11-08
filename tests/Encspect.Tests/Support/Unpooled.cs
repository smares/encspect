namespace Encspect.Tests;

// Byte arrays joined and repeated without the shared array pools, which LINQ's ToArray takes the segments of a sequence of unknown length
// from: the memory tests build their inputs with these (SharedPoolGuard).
internal static class Unpooled
{
    public static byte[] Join(IEnumerable<byte[]> parts)
    {
        ICollection<byte[]> all = parts as ICollection<byte[]> ?? new List<byte[]>(parts);
        long length = 0;
        foreach (byte[] part in all)
        {
            length += part.Length;
        }

        byte[] joined = new byte[checked((int)length)];
        int at = 0;
        foreach (byte[] part in all)
        {
            part.CopyTo(joined, at);
            at += part.Length;
        }

        return joined;
    }

    public static byte[] Repeat(byte[] part, int count)
    {
        byte[] data = new byte[checked(part.Length * count)];
        for (int at = 0; at < data.Length; at += part.Length)
        {
            part.CopyTo(data, at);
        }

        return data;
    }
}
