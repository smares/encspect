namespace Encspect.Formats.Aac;

// The CRC of an SBR extension payload (ISO/IEC 14496-3 4.5.2.8.1, bs_sbr_crc_bits): the generator polynomial x^10 + x^9 + x^5 + x^4 + x + 1 and the initial value 0, over the whole
// SBR data range after the CRC field, bs_fill_bits included (4.5.2.8, Figure 4.21), most significant bit first.
internal static class Crc10
{
    private const int Polynomial = 0x233;

    // The CRC over the bits of data from its position to its limit.
    public static int Of(AacBits data)
    {
        int crc = 0;
        while (data.Remaining > 0)
        {
            bool top = (crc & 0x200) != 0;
            crc = (crc << 1) & 0x3FF;
            if (top ^ data.ReadBit())
            {
                crc ^= Polynomial;
            }
        }

        return crc;
    }
}
