/* Prints what libwavpack 5.6.0 reads from a WavPack file, for crosscheck_wavpack_fixtures.py: the
 * file's figures as WavpackOpenFileInput gives them (the version, channels, channel mask, rates, bits,
 * samples, mode flags, MD5 digest, source format and wrapper size), then every block read_next_header
 * finds, with its header and whether WavpackVerifySingleBlock accepts it, and last the full pass of a
 * decoder: the file opened again as WavpackOpenFileInput does for wvunpack (block checksums verified),
 * every sample unpacked with WavpackUnpackSamples, and the errors it counted. The program is built against
 * the pinned source tree (wavpack_tool.py): it includes src/wavpack_local.h and links the library's own
 * sources, so nothing here reads a block but libwavpack's own code.
 *
 * Usage: wavpack_blocks file.wv [correction.wvc]
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

/* The pass of a decoder: open the main file alone, unpack every sample, count what libwavpack counts as an error
 * (a block it skips, a CRC that fails). Prints decode_open_error when the file does not open. */
static void decode(const char *path)
{
    char error[100] = "";
    FILE *file = fopen(path, "rb");
    WavpackContext *wpc = file ? WavpackOpenFileInputEx64(&reader, file, NULL, error, OPEN_ALT_TYPES | OPEN_WRAPPER | OPEN_DSD_NATIVE, 0) : NULL;

    if (!wpc) {
        printf("decode_open_error %s\n", error);
        if (file) fclose(file);
        return;
    }

    int channels = WavpackGetNumChannels(wpc);
    int32_t *buffer = malloc(4096 * (channels > 0 ? channels : 1) * sizeof(int32_t));
    long long samples = 0;
    uint32_t count;

    while ((count = WavpackUnpackSamples(wpc, buffer, 4096)) > 0)
        samples += count;

    printf("decode_samples %lld\n", samples);
    printf("decode_errors %d\n", WavpackGetNumErrors(wpc));
    printf("decode_lossy %d\n", WavpackLossyBlocks(wpc));
    free(buffer);
    WavpackCloseFile(wpc);
    fclose(file);
}

int main(int argc, char **argv)
{
    char error[100] = "";
    unsigned char md5[16];
    int flags = OPEN_WVC | OPEN_WRAPPER | OPEN_ALT_TYPES | OPEN_DSD_NATIVE | OPEN_NO_CHECKSUM;

    if (argc < 2) {
        fprintf(stderr, "usage: wavpack_blocks file.wv [correction.wvc]\n");
        return 2;
    }

    FILE *wv = fopen(argv[1], "rb");
    FILE *wvc = argc > 2 ? fopen(argv[2], "rb") : NULL;
    if (!wv) {
        fprintf(stderr, "cannot open %s\n", argv[1]);
        return 2;
    }

    WavpackContext *wpc = WavpackOpenFileInputEx64(&reader, wv, wvc, error, flags & ~(wvc ? 0 : OPEN_WVC), 0);
    if (!wpc) {
        printf("open error: %s\n", error);
    }
    else {
        int mode = WavpackGetMode(wpc);
        printf("version %d\n", WavpackGetVersion(wpc));
        printf("channels %d\n", WavpackGetNumChannels(wpc));
        printf("channel_mask 0x%x\n", WavpackGetChannelMask(wpc));
        printf("sample_rate %u\n", (unsigned) WavpackGetNativeSampleRate(wpc));
        printf("byte_rate %u\n", (unsigned) WavpackGetSampleRate(wpc));
        printf("bits_per_sample %d\n", WavpackGetBitsPerSample(wpc));
        printf("bytes_per_sample %d\n", WavpackGetBytesPerSample(wpc));
        printf("samples %lld\n", (long long) WavpackGetNumSamples64(wpc));
        printf("mode 0x%x\n", mode);
        printf("lossless %d\n", (mode & MODE_LOSSLESS) != 0);
        printf("hybrid %d\n", (mode & MODE_HYBRID) != 0);
        printf("float %d\n", (mode & MODE_FLOAT) != 0);
        printf("correction %d\n", (mode & MODE_WVC) != 0);
        printf("high %d\n", (mode & MODE_HIGH) != 0);
        printf("very_high %d\n", (mode & MODE_VERY_HIGH) != 0);
        printf("fast %d\n", (mode & MODE_FAST) != 0);
        printf("extra %d\n", (mode & MODE_EXTRA) ? (mode & MODE_XMODE) >> 12 : -1);
        printf("md5_stored %d\n", (mode & MODE_MD5) != 0);
        if (WavpackGetMD5Sum(wpc, md5)) {
            printf("md5 ");
            for (int i = 0; i < 16; i++) printf("%02x", md5[i]);
            printf("\n");
        }
        printf("file_format %d\n", WavpackGetFileFormat(wpc));
        printf("file_extension %s\n", WavpackGetFileExtension(wpc));
        printf("wrapper_bytes %u\n", (unsigned) WavpackGetWrapperBytes(wpc));
        printf("qualify_mode 0x%x\n", WavpackGetQualifyMode(wpc));
        printf("float_norm_exp %d\n", WavpackGetFloatNormExp(wpc));
        WavpackCloseFile(wpc);
    }

    /* Every block read_next_header finds from the start, as a reader would. */
    rewind(wv);
    WavpackHeader header;
    for (;;) {
        uint32_t skipped = read_next_header(&reader, wv, &header);
        if (skipped == (uint32_t) -1) break;
        long start = ftell(wv) - sizeof(WavpackHeader);
        unsigned char *block = malloc(header.ckSize + 8);
        memcpy(block, &header, sizeof(header));
        WavpackNativeToLittleEndian(block, WavpackHeaderFormat);
        long wanted = header.ckSize - 24;
        long got = fread(block + 32, 1, wanted, wv);
        int verified = 0;
        if (got == wanted) {
            WavpackLittleEndianToNative(block, WavpackHeaderFormat);
            verified = WavpackVerifySingleBlock(block, 1);
        }
        printf("block %ld %u %x %lld %u %08x %08x %d %d\n", start, header.ckSize + 8, header.version, (long long) GET_BLOCK_INDEX(header),
               header.block_samples, header.flags, header.crc, verified, got == wanted);
        free(block);
        if (got != wanted) break;
    }

    fclose(wv);
    decode(argv[1]);
    return 0;
}
