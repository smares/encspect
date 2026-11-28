// A front end of this project's own for r8brain-free-src (MIT, Aleksey Vaneev): raw little-endian float64 mono samples in on standard input, the
// same resampled with CDSPResampler::oneshot on standard output. Used as a black box by r8brain_tool.py for the Nyquist edge fixtures
// (docs/design/spectral-analysis.md, Step 10). Arguments: input rate, output rate, transition band in percent, stop band attenuation in dB.
#include "CDSPResampler.h"

#include <cstdio>
#include <cstdlib>
#include <vector>

int main(int argc, char** argv)
{
    if (argc != 5)
    {
        std::fprintf(stderr, "usage: r8brain_resample input_rate output_rate transition_band_percent attenuation_db\n");
        return 2;
    }

    const double inputRate = std::atof(argv[1]);
    const double outputRate = std::atof(argv[2]);
    const double transitionBand = std::atof(argv[3]);
    const double attenuation = std::atof(argv[4]);

    std::vector<double> in;
    double buffer[65536];
    size_t count;
    while ((count = std::fread(buffer, sizeof(double), 65536, stdin)) > 0)
    {
        in.insert(in.end(), buffer, buffer + count);
    }

    const long long outLength = (long long)((double)in.size() * outputRate / inputRate);
    std::vector<double> out((size_t)outLength);
    r8b::CDSPResampler resampler(inputRate, outputRate, 4096, transitionBand, attenuation, r8b::fprLinearPhase);
    resampler.oneshot(in.data(), (int)in.size(), out.data(), (int)outLength);
    std::fwrite(out.data(), sizeof(double), out.size(), stdout);
    return 0;
}
