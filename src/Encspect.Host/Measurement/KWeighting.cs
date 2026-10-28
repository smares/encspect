// The derivation of the K-weighting filter's coefficients for rates other than 48 kHz follows libebur128's (ebur128_init_filter): the
// parameters of the two filters (frequency, Q and gain) and the bilinear transforms that turn them into coefficients. libebur128 is
// Copyright (c) 2011 Jan Kokemuller and is released under the MIT license; its notice is in THIRD-PARTY-NOTICES.md. Calls of the
// platform's tan and pow are replaced by DetMath's, so that the coefficients are the same on every platform.

namespace Encspect.Host.Measurement;

// The K-weighting of ITU-R BS.1770-5 Annex 1: a high-shelf pre-filter (Table 1) and a highpass (Table 2), two second-order sections.
// The Recommendation gives the coefficients for 48 kHz only and says that other rates "should be chosen to provide the same frequency
// response that the specified filter provides at 48 kHz". At 48,000 Hz the published coefficients are used as written; at every other rate
// they are derived from the analog parameters the published ones come from (docs/design/spectral-analysis.md, decision 13).
internal readonly record struct KWeighting(double B0, double B1, double B2, double A1, double A2, double HighpassA1, double HighpassA2)
{
    // Table 1 and Table 2 of BS.1770-5 (the highpass numerator is 1, -2, 1).
    public static readonly KWeighting At48000 = new(1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, 0.73248077421585, -1.99004745483398, 0.99007225036621);

    public static KWeighting For(int rate)
    {
        if (rate == 48000)
        {
            return At48000;
        }

        double f0 = 1681.974450955533;
        double gain = 3.999843853973347;
        double q = 0.7071752369554196;
        double k = DetMath.Tan(DetMath.Pi * f0 / rate);
        double vh = DetMath.Pow(10.0, gain / 20.0);
        double vb = DetMath.Pow(vh, 0.4996667741545416);
        double a0 = 1.0 + (k / q) + (k * k);
        double b0 = (vh + (vb * k / q) + (k * k)) / a0;
        double b1 = 2.0 * ((k * k) - vh) / a0;
        double b2 = (vh - (vb * k / q) + (k * k)) / a0;
        double a1 = 2.0 * ((k * k) - 1.0) / a0;
        double a2 = (1.0 - (k / q) + (k * k)) / a0;

        f0 = 38.13547087602444;
        q = 0.5003270373238773;
        k = DetMath.Tan(DetMath.Pi * f0 / rate);
        double d = 1.0 + (k / q) + (k * k);
        return new KWeighting(b0, b1, b2, a1, a2, 2.0 * ((k * k) - 1.0) / d, (1.0 - (k / q) + (k * k)) / d);
    }
}
