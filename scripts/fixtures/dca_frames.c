/* Prints, for every core frame of a raw DTS file, what libdca (0.0.7, Ubuntu's libdca-dev) reads of it:
 * the offset, the frame's length in the file, the arrangement code with the LFE flag (DCA_LFE, 0x80),
 * the sampling frequency, the bit rate its RATE code gives, the samples per channel, and whether
 * dca_frame() and every dca_block() decoded it without an error ("ok") or not ("rejected"). libdca
 * finds a frame by its sync word and the header checks of dca_syncinfo(), in all four forms, and skips
 * the bytes between frames (extension substreams, junk, tags) a byte at a time. Used by
 * crosscheck_dts_fixtures.py.
 *
 *     cc -O2 -o dca_frames dca_frames.c -ldca && ./dca_frames file.dts
 */
#include <inttypes.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <dca.h>

int main(int argc, char **argv)
{
    if (argc != 2) {
        fprintf(stderr, "usage: dca_frames file.dts\n");
        return 2;
    }
    FILE *file = fopen(argv[1], "rb");
    if (!file) {
        perror(argv[1]);
        return 2;
    }
    fseek(file, 0, SEEK_END);
    long size = ftell(file);
    fseek(file, 0, SEEK_SET);
    uint8_t *data = malloc(size + 4096);
    memset(data + size, 0, 4096);
    if (fread(data, 1, size, file) != (size_t)size) {
        return 2;
    }
    fclose(file);
    dca_state_t *state = dca_init(0);
    uint8_t *frame = aligned_alloc(16, 32768);
    long offset = 0;
    while (offset + 14 <= size) {
        int flags, rate, bitrate, samples;
        int length = dca_syncinfo(state, data + offset, &flags, &rate, &bitrate, &samples);
        if (length == 0) {
            offset++;
            continue;
        }
        if (offset + length > size) {
            printf("%ld %d %d %d %d %d truncated\n", offset, length, flags, rate, bitrate, samples);
            break;
        }
        int status = 0;
        memcpy(frame, data + offset, length);
        int request = flags | DCA_ADJUST_LEVEL;
        level_t level = 1;
        if (dca_frame(state, frame, &request, &level, 0) != 0) {
            status = 1;
        } else {
            for (int block = 0; block < dca_blocks_num(state) && !status; block++) {
                status = dca_block(state) != 0;
            }
        }
        printf("%ld %d %d %d %d %d %s\n", offset, length, flags, rate, bitrate, samples, status ? "rejected" : "ok");
        offset += length;
    }
    return 0;
}
