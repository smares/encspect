namespace Encspect.Formats.Flac;

// The 34 bytes of a STREAMINFO block (RFC 9639, section 8.2): the figures every frame is read against, and the MD5 of the
// decoded audio. The decoder reads the same bytes with this code.
internal readonly record struct StreamInfoBlock(int MinBlockSize, int MaxBlockSize, int MinFrameSize, int MaxFrameSize, int SampleRate, int Channels, int BitsPerSample, long TotalSamples)
{
    public const int Length = 34;
    public const int Md5Offset = 18;
    public const int Md5Length = 16;

    public static StreamInfoBlock Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Length)
        {
            throw new EndOfStreamException();
        }

        return new StreamInfoBlock(
            (data[0] << 8) | data[1],
            (data[2] << 8) | data[3],
            (data[4] << 16) | (data[5] << 8) | data[6],
            (data[7] << 16) | (data[8] << 8) | data[9],
            (data[10] << 12) | (data[11] << 4) | (data[12] >> 4),
            ((data[12] >> 1) & 7) + 1,
            (((data[12] & 1) << 4) | (data[13] >> 4)) + 1,
            ((long)(data[13] & 0xF) << 32) | ((long)data[14] << 24) | ((long)data[15] << 16) | ((long)data[16] << 8) | data[17]);
    }

    public static ReadOnlySpan<byte> Md5(ReadOnlySpan<byte> data) => data.Slice(Md5Offset, Md5Length);

    // An MD5 of zero means the encoder did not know it (RFC 9639, section 8.2).
    public static bool IsMd5Unknown(ReadOnlySpan<byte> data)
    {
        foreach (byte b in Md5(data))
        {
            if (b != 0)
            {
                return false;
            }
        }

        return true;
    }
}
