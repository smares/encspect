namespace Encspect.Core;

// The work units every decoder charges, besides the units its stages state (docs/design/decoders.md, "Work instead of
// samples"). The unit is the work of one tap of a predictor or filter on one sample of one channel: about 0.6 ns on the
// machines it was measured on, which DecoderWorkTests holds every decoder to.
public static class DecodeWork
{
    // Per sample of each channel a decoder outputs, decoded or muted: 10 plus 3 per byte of the canonical layout that the
    // checksums over the decoded audio hash (bytes = ceil(BitsPerSample / 8)): 13, 16, 19 and 22 for 1 to 4 bytes.
    public static long ForSample(int bytes) => 10 + (3 * bytes);

    // What an IDecoder allows per byte of the file unless it states otherwise.
    public const long DefaultPerInputByte = 2048;

    // Per byte of entropy-coded data a decoder reads itself (not a frame its parser walked into lent buffers).
    public const long PerCodedByte = 16;

    // Per tap of a predictor or filter, per sample of each channel: the unit itself.
    public const long PerTap = 1;
}
