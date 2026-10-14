namespace Encspect.Formats.Dts;

// The four forms in which a DTS core stream is written. The specification (ETSI TS 102 114 5.4.2)
// describes the sync word 0x7FFE8001 only, as bytes in order: 16-bit words, big endian. The other
// three forms are FFmpeg's (dca_syncwords.h, dca.c avpriv_dca_convert_bitstream) and libdca's
// (parse.c, dca_syncinfo): 16-bit words with each pair of bytes swapped, and the two 14-bit forms,
// where each 16-bit word, big or little endian, holds 14 bits of the stream in its low bits (the
// two top bits repeat bit 13), so a CD or S/PDIF payload fills no more than 14 of every 16 bits.
// Every field is read from the bit-packed stream, which the forms are converted to first.
internal enum StreamForm : byte
{
    BigEndian16,
    LittleEndian16,
    BigEndian14,
    LittleEndian14,
}

internal static class Forms
{
    public const int SyncLength = 6;

    public static bool Is14Bit(this StreamForm form) => form >= StreamForm.BigEndian14;

    public static bool IsLittleEndian(this StreamForm form) => form is StreamForm.LittleEndian16 or StreamForm.LittleEndian14;

    public static int WordBits(this StreamForm form) => form.Is14Bit() ? 14 : 16;

    // The form a core frame starting at bytes (at least SyncLength of them) is written in, or null.
    // A 14-bit sync word goes on into the next word (libdca's dca_syncinfo, FFmpeg's dts_probe): the
    // four bits that finish the 32-bit sync word, then the six of FTYPE and SHORT.
    public static StreamForm? Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < SyncLength)
        {
            return null;
        }

        return (bytes[0], bytes[1], bytes[2], bytes[3]) switch
        {
            (0x7F, 0xFE, 0x80, 0x01) => StreamForm.BigEndian16,
            (0xFE, 0x7F, 0x01, 0x80) => StreamForm.LittleEndian16,
            (0x1F, 0xFF, 0xE8, 0x00) when bytes[4] == 0x07 && (bytes[5] & 0xF0) == 0xF0 => StreamForm.BigEndian14,
            (0xFF, 0x1F, 0x00, 0xE8) when (bytes[4] & 0xF0) == 0xF0 && bytes[5] == 0x07 => StreamForm.LittleEndian14,
            _ => null,
        };
    }

    // How many bytes a frame of packedLength bytes (FSIZE + 1) takes in a form: the same in the 16-bit
    // forms, and in the 14-bit forms the words that hold its bits, as libdca counts them
    // (frame_size * 8 / 14 * 2).
    public static int FileLength(int packedLength, StreamForm form) => form.Is14Bit() ? packedLength * 8 / 14 * 2 : packedLength;

    // Converts file bytes to the bit-packed stream; returns its length in bytes. A 14-bit form holds
    // 14 bits per two bytes, so n bytes make n / 2 * 14 bits, rounded up to whole bytes as FFmpeg's flush_put_bits does.
    public static int Unpack(ReadOnlySpan<byte> file, StreamForm form, Span<byte> packed)
    {
        switch (form)
        {
            case StreamForm.BigEndian16:
                file.CopyTo(packed);
                return file.Length;
            case StreamForm.LittleEndian16:
                for (int i = 0; i + 1 < file.Length; i += 2)
                {
                    packed[i] = file[i + 1];
                    packed[i + 1] = file[i];
                }

                if ((file.Length & 1) != 0)
                {
                    packed[file.Length - 1] = file[^1];
                }

                return file.Length;
            default:
                int words = file.Length / 2;
                int bytes = ((words * 14) + 7) / 8;
                packed[..bytes].Clear();
                for (int word = 0; word < words; word++)
                {
                    int value = form == StreamForm.BigEndian14 ? (file[word * 2] << 8) | file[(word * 2) + 1] : (file[(word * 2) + 1] << 8) | file[word * 2];
                    PutBits(packed[..bytes], word * 14, value & 0x3FFF, 14);
                }

                return bytes;
        }
    }

    // The file index of packed byte i of a 16-bit form: little endian swaps each pair; a lone last byte stays.
    public static int FileIndex(int packedIndex, StreamForm form, int fileLength)
    {
        if (form != StreamForm.LittleEndian16)
        {
            return packedIndex;
        }

        int swapped = packedIndex ^ 1;
        return swapped < fileLength ? swapped : packedIndex;
    }

    private static void PutBits(Span<byte> target, int bit, int value, int width)
    {
        for (int i = 0; i < width; i++)
        {
            int position = bit + i;
            if ((position >> 3) >= target.Length)
            {
                return;
            }

            if (((value >> (width - 1 - i)) & 1) != 0)
            {
                target[position >> 3] |= (byte)(0x80 >> (position & 7));
            }
        }
    }
}
