using Encspect.Core;

namespace Encspect.Host.Measurement;

// The channel weights of ITU-R BS.1770-5 (Annex 3, Tables 4 and 5): 1.00 for a loudspeaker with an elevation of 30 degrees or more and for
// an azimuth under 60 degrees or over 120 degrees, 1.41 for 60 to 120 degrees (M+-060, M+-090, M+-110), and the LFE left out. Weights are
// in hundredths. The layouts are read as docs/design/spectral-analysis.md ("Loudness and loudness range") sets out.
internal static class LoudnessWeights
{
    public const int Unit = 100;

    public const int Surround = 141;

    public const string NotStated = "the channel positions are not stated";

    public const string OnlyLfe = "the stream holds no channel that loudness includes";

    // The weights of the measured channels, or null and the reason loudness cannot be measured.
    public static int[]? For(IReadOnlyList<Speaker>? layout, int measured, out string? reason)
    {
        reason = null;
        bool isStated = layout is not null && layout.Count >= measured && !layout.Take(measured).Any(s => s == Speaker.Discrete);
        if (!isStated)
        {
            // Mono is one channel, two channels are left and right; more would need a guess.
            if (measured <= 2)
            {
                return [.. Enumerable.Repeat(Unit, measured)];
            }

            reason = NotStated;
            return null;
        }

        IReadOnlyList<Speaker> speakers = [.. layout!.Take(measured)];
        bool hasSides = speakers.Contains(Speaker.SideLeft) && speakers.Contains(Speaker.SideRight);
        int[] weights = [.. speakers.Select(s => Weight(s, hasSides))];
        if (weights.All(w => w == 0))
        {
            reason = OnlyLfe;
            return null;
        }

        return weights;
    }

    // BackLeft and BackRight are the rear pair of a 7.1 (M+-135, weight 1.00) beside a side pair, otherwise the surround pair
    // (M+-110, 1.41). The wide pair's azimuth is not read from ISO/IEC 23091-3 here; it is weighted 1.00.
    private static int Weight(Speaker speaker, bool hasSides) => speaker switch
    {
        Speaker.LowFrequency or Speaker.LowFrequency2 => 0,
        Speaker.SideLeft or Speaker.SideRight => Surround,
        Speaker.BackLeft or Speaker.BackRight => hasSides ? Unit : Surround,
        _ => Unit,
    };
}
