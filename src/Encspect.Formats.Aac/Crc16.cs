namespace Encspect.Formats.Aac;

// The ADTS CRC (ISO/IEC 14496-3 1.A.4.3, which refers to ISO/IEC 11172-3 2.4.3.1): CRC-16 with the
// polynomial 0x8005 and the initial value 0xFFFF, over bit fields of any length, most significant
// bit first.
internal struct Crc16()
{
    public ushort Value { get; private set; } = 0xFFFF;

    public void Add(ReadOnlySpan<byte> data, int startBit, int count)
    {
        ushort crc = Value;
        for (int bit = startBit; bit < startBit + count; bit++)
        {
            int input = (data[bit >> 3] >> (7 - (bit & 7))) & 1;
            bool top = (crc & 0x8000) != 0;
            crc <<= 1;
            if (top ^ (input != 0))
            {
                crc ^= 0x8005;
            }
        }

        Value = crc;
    }

    // Adds count zero bits, with which a region shorter than the bits the CRC covers is filled up.
    public void AddZeros(int count)
    {
        ushort crc = Value;
        for (int bit = 0; bit < count; bit++)
        {
            bool top = (crc & 0x8000) != 0;
            crc <<= 1;
            if (top)
            {
                crc ^= 0x8005;
            }
        }

        Value = crc;
    }

    // Adds the regions of a raw data block's elements that the ADTS CRC covers (ISO/IEC 14496-3
    // 1.A.4.3, as fdk-aac's decoder reads its element lists in libFDK/src/FDK_tools_rom.cpp): after
    // each element's ID, the first 192 bits of a single channel, LFE or coupling element and of a
    // channel pair, the first 128 bits of the pair's second channel from its global_gain, and the
    // whole of a program config or data stream element, each filled up with zero bits where the
    // element is shorter; fill elements are not covered.
    public void AddElements(ReadOnlySpan<byte> block, IReadOnlyList<ElementSpan> elements)
    {
        foreach (ElementSpan element in elements)
        {
            int start = element.Start + 3;
            switch (element.Id)
            {
                case 0 or 2 or 3:
                    AddRegion(block, start, element.End, 192);
                    break;
                case 1:
                    AddRegion(block, start, element.End, 192);
                    AddRegion(block, element.SecondChannel, element.End, 128);
                    break;
                case 4 or 5:
                    Add(block, start, element.End - start);
                    break;
            }
        }
    }

    private void AddRegion(ReadOnlySpan<byte> block, int start, int end, int bits)
    {
        int length = Math.Min(end - start, bits);
        Add(block, start, length);
        AddZeros(bits - length);
    }
}
