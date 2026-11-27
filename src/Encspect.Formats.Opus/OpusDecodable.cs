namespace Encspect.Formats.Opus;

// Whether the identification header describes streams libopus's multistream decoder accepts (opus_multistream_decoder_init): the channel mapping family is one whose
// mapping the decoder applies (0, 1, 2, 3 and 255), and the counts and the mapping are consistent. Family 3 is the one divergence: RFC 8486 (section 3.2) lets the decoded
// channel count K = N + M differ from the output channel count C and defines S = D X over all K columns, which the analyzer follows (K < C is decoded, with the rows that
// no column reaches silent); libopus's projection decoder refuses K < C (validate_layout) and routes only the first C decoded channels when K > C.
internal static class OpusDecodable
{
    public static bool Check(OpusHead head)
    {
        if (head.Family is not (0 or 1 or 2 or 3 or 255) || head.Channels < 1 || (head.Version & 0xF0) != 0)
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

        if (head.Streams < 1 || head.Coupled > head.Streams || head.Streams + head.Coupled > 255)
        {
            return false;
        }

        if (head.Family == 3)
        {
            // opus_projection_decoder_init: the matrix has exactly C x (N + M) coefficients, and no more than 65,004 bytes (mapping_matrix_get_size).
            return head.Mapping.Length == 2 * head.Channels * (head.Streams + head.Coupled) && head.Mapping.Length <= 65004;
        }

        if (head.Mapping.Length != head.Channels)
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
