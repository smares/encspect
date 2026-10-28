/* A small program that runs libebur128 (https://github.com/jiixyj/libebur128, MIT) as a black-box tool for scripts/fixtures/crosscheck_measurement_fixtures.py.
 * Not Encspect code, and nothing of libebur128 is copied here: it only calls the library's public API.
 *
 *   ebur128_driver RATE CHANNELS MAP < interleaved little-endian 32-bit floats
 *   ebur128_driver --version
 *
 * MAP names the position of each channel, comma-separated: unused (the LFE), L, R, C, Mp060, Mm060, Mp090, Mm090, Mp110, Mm110, Mp135, Mm135, Mp180,
 * Up030, Um030, Up090, Um090, Up110, Um110, Up135, Um135 (the names of ITU-R BS.1770 Table 5). The audio is fed in granules of a fifth of 100 ms (the
 * granule boundaries Encspect uses), and the momentary loudness is read after each granule and the short-term loudness every 100 ms, so that the
 * maxima are those of a sliding window. Prints one "name value" pair per line; levels are in LUFS, LU or dB (peaks, per channel).
 */
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "ebur128.h"

static int channel_of(const char *name) {
  static const struct { const char *name; int value; } table[] = {
    {"unused", EBUR128_UNUSED}, {"L", EBUR128_LEFT}, {"R", EBUR128_RIGHT}, {"C", EBUR128_CENTER},
    {"Mp060", EBUR128_Mp060}, {"Mm060", EBUR128_Mm060}, {"Mp090", EBUR128_Mp090}, {"Mm090", EBUR128_Mm090},
    {"Mp110", EBUR128_Mp110}, {"Mm110", EBUR128_Mm110}, {"Mp135", EBUR128_Mp135}, {"Mm135", EBUR128_Mm135}, {"Mp180", EBUR128_Mp180},
    {"Up030", EBUR128_Up030}, {"Um030", EBUR128_Um030}, {"Up090", EBUR128_Up090}, {"Um090", EBUR128_Um090},
    {"Up110", EBUR128_Up110}, {"Um110", EBUR128_Um110}, {"Up135", EBUR128_Up135}, {"Um135", EBUR128_Um135},
  };
  for (size_t i = 0; i < sizeof table / sizeof table[0]; i++) {
    if (strcmp(table[i].name, name) == 0) return table[i].value;
  }
  fprintf(stderr, "unknown channel name %s\n", name);
  exit(2);
}

static double db(double linear) { return linear > 0 ? 20 * log10(linear) : -HUGE_VAL; }

int main(int argc, char **argv) {
  if (argc == 2 && strcmp(argv[1], "--version") == 0) {
    int major, minor, patch;
    ebur128_get_version(&major, &minor, &patch);
    printf("ebur128_driver libebur128 %d.%d.%d\n", major, minor, patch);
    return 0;
  }
  if (argc != 4) { fprintf(stderr, "usage: ebur128_driver RATE CHANNELS MAP\n"); return 2; }
  unsigned long rate = strtoul(argv[1], NULL, 10);
  unsigned channels = (unsigned)strtoul(argv[2], NULL, 10);
  ebur128_state *st = ebur128_init(channels, rate, EBUR128_MODE_I | EBUR128_MODE_LRA | EBUR128_MODE_TRUE_PEAK);
  if (!st) { fprintf(stderr, "ebur128_init failed\n"); return 2; }
  char *map = strdup(argv[3]);
  unsigned c = 0;
  for (char *token = strtok(map, ","); token && c < channels; token = strtok(NULL, ",")) ebur128_set_channel(st, c++, channel_of(token));
  unsigned long step = (rate + 5) / 10;
  float *buffer = malloc((size_t)(step / 5 + 2) * channels * sizeof(float));
  double max_m = -HUGE_VAL, max_s = -HUGE_VAL;
  unsigned long granule = 0, done = 0;
  for (;;) {
    unsigned long end = ((granule + 1) * step + 2) / 5, frames = end - done;
    size_t got = fread(buffer, sizeof(float) * channels, frames, stdin);
    if (got < frames) break;
    ebur128_add_frames_float(st, buffer, got);
    done = end;
    granule++;
    double m, s;
    if (ebur128_loudness_momentary(st, &m) == 0 && granule >= 20 && m > max_m) max_m = m;
    if (granule % 5 == 0 && granule >= 150 && ebur128_loudness_shortterm(st, &s) == 0 && s > max_s) max_s = s;
  }
  double value;
  if (ebur128_loudness_global(st, &value) == 0) printf("integrated %.4f\n", value);
  if (ebur128_loudness_range(st, &value) == 0) printf("range %.4f\n", value);
  printf("momentary_max %.4f\nshort_term_max %.4f\n", max_m, max_s);
  for (c = 0; c < channels; c++) {
    double peak;
    if (ebur128_true_peak(st, c, &peak) == 0) printf("true_peak %u %.4f\n", c, db(peak));
    if (ebur128_sample_peak(st, c, &peak) == 0) printf("sample_peak %u %.4f\n", c, db(peak));
  }
  ebur128_destroy(&st);
  return 0;
}
