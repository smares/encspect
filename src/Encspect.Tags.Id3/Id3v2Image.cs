namespace Encspect.Tags.Id3;

// The format and, where its header is present, the size of a picture in an APIC frame, which
// the ID3v2.4 image restrictions (main structure, section 3.2) are about.
internal readonly record struct ImageInfo(string Format, int? Width, int? Height);

internal static class Id3v2Image
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static ImageInfo? Measure(byte[] data, int start, int end)
    {
        ReadOnlySpan<byte> image = data.AsSpan(start, end - start);
        if (image.StartsWith(PngSignature))
        {
            // PNG section 11.2.2: IHDR is the first chunk, width and height its first 8 bytes.
            if (image.Length >= 24 && image.Slice(12, 4).SequenceEqual("IHDR"u8))
            {
                return new ImageInfo("PNG", BigEndian32(image, 16), BigEndian32(image, 20));
            }

            return new ImageInfo("PNG", null, null);
        }

        if (image.Length >= 3 && image[0] == 0xFF && image[1] == 0xD8 && image[2] == 0xFF)
        {
            (int Width, int Height)? size = JpegSize(image);
            return new ImageInfo("JPEG", size?.Width, size?.Height);
        }

        return null;
    }

    // ITU-T T.81 annex B: marker segments after SOI, each $FF, a marker byte (table B.1) and,
    // except for RSTn, SOI, EOI and TEM, a 16-bit length that counts itself. A frame header
    // (SOF0 to SOF15 but DHT $C4, JPG $C8 and DAC $CC) holds precision, height and width (B.2.2).
    private static (int Width, int Height)? JpegSize(ReadOnlySpan<byte> image)
    {
        int i = 2;
        while (i + 4 <= image.Length)
        {
            if (image[i] != 0xFF)
            {
                return null;
            }

            byte marker = image[i + 1];
            if (marker == 0xFF)
            {
                i++;
                continue;
            }

            if (marker is 0x01 or 0xD8 or (>= 0xD0 and <= 0xD7))
            {
                i += 2;
                continue;
            }

            if (marker is 0xD9 or 0xDA)
            {
                return null;
            }

            int length = (image[i + 2] << 8) | image[i + 3];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return i + 9 <= image.Length && length >= 7
                    ? ((image[i + 7] << 8) | image[i + 8], (image[i + 5] << 8) | image[i + 6])
                    : null;
            }

            if (length < 2)
            {
                return null;
            }

            i += 2 + length;
        }

        return null;
    }

    private static int BigEndian32(ReadOnlySpan<byte> data, int offset) =>
        (int)Math.Min(int.MaxValue, ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3]);
}
