namespace Encspect.Formats.Opus;

// Whether the identification header describes streams libopus's multistream decoder accepts (opus_multistream_decoder_init): the channel mapping family is one whose
// mapping the decoder applies (0, 1, 2 and 255), and the counts and the mapping are consistent.
internal static class OpusDecodable
{
    public static bool Check(OpusHead head)
    {
        if (head.Family is not (0 or 1 or 2 or 255) || head.Channels < 1 || (head.Version & 0xF0) != 0)
        {
            return false;
        }

        if (head.Family == 0)
        {
            return head.Channels <= 2;
        }

        if (head.Family == 1 && head.Channels > 8)
        {
            return false;
        }

        if (head.Streams < 1 || head.Coupled > head.Streams || head.Streams + head.Coupled > 255 || head.Mapping.Length != head.Channels)
        {
            return false;
        }

        foreach (byte m in head.Mapping)
        {
            if (m != 255 && m >= head.Streams + head.Coupled)
            {
                return false;
            }
        }

        return true;
    }
}
