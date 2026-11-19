/* Prints what libwavpack 5.6.0 built with its legacy decoder (ENABLE_LEGACY: unpack3.c, unpack3_open.c, unpack3_seek.c) reads from a WavPack file of
 * version 1 to 3, for make_external_wavpack_legacy_fixtures.py and the cross-check of the legacy fixtures: whether the file opens and the error, the
 * figures WavpackOpenFileInput gives (version, channels, sample rate, bits, samples, mode flags, the stored WAV header's size and the extension), and the
 * pass of a decoder: every sample unpacked with WavpackUnpackSamples, the errors counted (the CRC of version 3) and the samples written to a file as
 * little-endian integers of the file's bytes per sample, so the caller takes their MD5. The program is built against the pinned source tree
 * (wavpack_tool.py): it includes src/wavpack_local.h and links the library's own sources, so nothing here reads a header but libwavpack's own code.
 *
 * Usage: wavpack_legacy file.wv samples.pcm [correction.wvc]
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "wavpack_local.h"

static int32_t read_bytes(void *id, void *data, int32_t bcount) { return (int32_t) fread(data, 1, bcount, (FILE *) id); }
static int64_t get_pos(void *id) { return ftell((FILE *) id); }
static int set_pos_abs(void *id, int64_t pos) { return fseek((FILE *) id, (long) pos, SEEK_SET); }
static int set_pos_rel(void *id, int64_t delta, int mode) { return fseek((FILE *) id, (long) delta, mode); }
static int push_back_byte(void *id, int c) { return ungetc(c, (FILE *) id); }
static int64_t get_length(void *id)
{
    FILE *file = (FILE *) id;
    long here = ftell(file);
    fseek(file, 0, SEEK_END);
    long length = ftell(file);
    fseek(file, here, SEEK_SET);
    return length;
}
static int can_seek(void *id) { return 1; }

static WavpackStreamReader64 reader = { read_bytes, NULL, get_pos, set_pos_abs, set_pos_rel, push_back_byte, get_length, can_seek, NULL, NULL };

int main(int argc, char **argv)
{
    char error[100] = "";

    if (argc < 3) {
        fprintf(stderr, "usage: wavpack_legacy file.wv samples.pcm [correction.wvc]\n");
        return 2;
    }

    FILE *wv = fopen(argv[1], "rb");
    FILE *wvc = argc > 3 ? fopen(argv[3], "rb") : NULL;
    if (!wv) {
        fprintf(stderr, "cannot open %s\n", argv[1]);
        return 2;
    }

    WavpackContext *wpc = WavpackOpenFileInputEx64(&reader, wv, wvc, error, OPEN_WRAPPER | (wvc ? OPEN_WVC : 0), 0);
    if (!wpc) {
        printf("open_error %s\n", error);
        return 0;
    }

    int mode = WavpackGetMode(wpc);
    int channels = WavpackGetNumChannels(wpc);
    int bytes = WavpackGetBytesPerSample(wpc);
    printf("version %d\n", WavpackGetVersion(wpc));
    printf("channels %d\n", channels);
    printf("channel_mask 0x%x\n", WavpackGetChannelMask(wpc));
    printf("sample_rate %u\n", (unsigned) WavpackGetSampleRate(wpc));
    printf("bits_per_sample %d\n", WavpackGetBitsPerSample(wpc));
    printf("bytes_per_sample %d\n", bytes);
    printf("samples %lld\n", (long long) WavpackGetNumSamples64(wpc));
    printf("mode 0x%x\n", mode);
    printf("lossless %d\n", (mode & MODE_LOSSLESS) != 0);
    printf("hybrid %d\n", (mode & MODE_HYBRID) != 0);
    printf("correction %d\n", (mode & MODE_WVC) != 0);
    printf("high %d\n", (mode & MODE_HIGH) != 0);
    printf("fast %d\n", (mode & MODE_FAST) != 0);
    printf("wrapper_bytes %u\n", (unsigned) WavpackGetWrapperBytes(wpc));

    if (channels < 1 || bytes < 1 || bytes > 4) {
        printf("decode_samples 0\n");
        return 0;
    }

    FILE *out = fopen(argv[2], "wb");
    int32_t *buffer = malloc(4096 * channels * sizeof(int32_t));
    unsigned char *bytes_out = malloc(4096 * channels * bytes);
    long long samples = 0;
    uint32_t count;

    while ((count = WavpackUnpackSamples(wpc, buffer, 4096)) > 0) {
        for (uint32_t i = 0; i < count * channels; i++)
            for (int b = 0; b < bytes; b++)
                bytes_out[i * bytes + b] = (unsigned char) ((uint32_t) buffer[i] >> (8 * b));
        fwrite(bytes_out, bytes, count * channels, out);
        samples += count;
    }

    printf("decode_samples %lld\n", samples);
    printf("decode_errors %d\n", WavpackGetNumErrors(wpc));
    fclose(out);
    free(buffer);
    free(bytes_out);
    WavpackCloseFile(wpc);
    return 0;
}
