/* The Linux direct path for AAC (docs/design/official-decoder-crosschecks.md): feeds Fraunhofer's FDK AAC decoder one unit of
   the unit list (unit_list.py --tsv) at a time and writes one flushed JSON line per decode call, so a crash or a stall
   leaves what was done. The decoder is used as a black box and is not linked into Encspect.

     fdk_decode <mode> <file> <units.tsv> <concealment method>

   modes:
     units  ADTS frames (TT_MP4_ADTS) or MP4 access units (TT_MP4_RAW with the file's configuration), one per aacDecoder_Fill;
            the bytes the extractor skipped are fed in front of the next unit
     bulk   the whole file in one aacDecoder_Fill (TT_MP4_ADTS), decode until the data is used up: the CStreamInfo counters
     adif   TT_MP4_ADIF, the whole file in one aacDecoder_Fill, one line per decode call

   A cut unit is fed from its first byte up to the last byte the file holds and never padded. */
#include <inttypes.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "fdk-aac/aacdecoder_lib.h"

#define MAX_PCM (8 * 8192)

typedef struct {
    long offset, declared, received;
} Unit;

static INT_PCM pcm[MAX_PCM];

static void stream_info(HANDLE_AACDECODER decoder, long *samples, int *channels) {
    CStreamInfo *info = aacDecoder_GetStreamInfo(decoder);
    *samples = 0;
    *channels = 0;
    if (!info) {
        return;
    }
    *channels = info->numChannels;
    *samples = info->frameSize; /* per channel, as the analyzer counts */
    printf(",\"sampleRate\":%d,\"frameSize\":%d,\"channels\":%d,\"aot\":%d,\"extAot\":%d,\"extSampleRate\":%d,\"outputDelay\":%u"
           ",\"totalBytes\":%" PRId64 ",\"badBytes\":%" PRId64 ",\"totalAccessUnits\":%" PRId64 ",\"badAccessUnits\":%" PRId64,
           info->sampleRate, info->frameSize, info->numChannels, (int)info->aot, (int)info->extAot, info->extSamplingRate, info->outputDelay,
           (int64_t)info->numTotalBytes, (int64_t)info->numBadBytes, (int64_t)info->numTotalAccessUnits, (int64_t)info->numBadAccessUnits);
}

static unsigned char *read_file(const char *path, long *length) {
    FILE *file = fopen(path, "rb");
    if (!file) {
        perror(path);
        exit(2);
    }
    fseek(file, 0, SEEK_END);
    *length = ftell(file);
    if (*length < 0) {
        exit(2);
    }
    fseek(file, 0, SEEK_SET);
    unsigned char *data = malloc(*length ? *length : 1);
    if (!data) {
        exit(2);
    }
    if (fread(data, 1, *length, file) != (size_t)*length) {
        exit(2);
    }
    fclose(file);
    return data;
}

static int hex(const char *text, unsigned char *out, int max) {
    int n = 0;
    while (text[0] && text[1] && n < max) {
        char pair[3] = {text[0], text[1], 0};
        out[n++] = (unsigned char)strtol(pair, NULL, 16);
        text += 2;
    }
    return n;
}

static struct {
    unsigned char *data;
    HANDLE_AACDECODER decoder;
    long coded, outputs, calls, fed_end;
    int limit, raw, adif, conceal, conceal_set;
    const char *stopped;
} G = {.stopped = "end"};

/* Decodes what the library holds until it asks for more bits, and writes one line per call; numTotalBytes in each line says how
   many bytes of the input the library has used up to that call. */
static void decode_all(long index, long from, long to, unsigned fill, unsigned left, long offset, long declared, long received) {
    /* The loop ends when the library asks for more bits. It is cut off only when numTotalBytes stops advancing for eight calls in a row
       without an output, or after a million calls: a long run of small frames or of junk is not a limit. */
    long last_total = -1;
    int stuck = 0;
    for (int attempt = 0; attempt < 1000000; attempt++) {
        AAC_DECODER_ERROR error = aacDecoder_DecodeFrame(G.decoder, pcm, MAX_PCM, 0);
        if (G.adif && !G.conceal_set) {
            /* an ADIF header is read inside the library, so the method is set after the first decode call, which configures the stream */
            G.conceal_set = 1;
            printf("{\"type\":\"conceal\",\"when\":\"after the first decode call\",\"status\":\"0x%04X\"}\n", (unsigned)aacDecoder_SetParam(G.decoder, AAC_CONCEAL_METHOD, G.conceal));
        }
        long samples;
        int channels;
        int output = IS_OUTPUT_VALID(error);
        printf("{\"type\":\"unit\",\"index\":%ld,\"call\":%d,\"offset\":%ld,\"declared\":%ld,\"received\":%ld,\"fedFrom\":%ld,\"fedTo\":%ld"
               ",\"fill\":\"0x%04X\",\"left\":%u,\"error\":\"0x%04X\",\"outputValid\":%s",
               index, attempt, offset, declared, received, from, to, fill, left, (unsigned)error, output ? "true" : "false");
        stream_info(G.decoder, &samples, &channels);
        if (output) {
            G.coded += samples;
            G.outputs++;
            printf(",\"samples\":%ld", samples);
        }
        printf("}\n");
        fflush(stdout);
        G.calls++;
        CStreamInfo *current = aacDecoder_GetStreamInfo(G.decoder);
        long total_now = current ? (long)current->numTotalBytes : -1;
        stuck = (total_now == last_total && !output) ? stuck + 1 : 0;
        last_total = total_now;
        if (stuck >= 8) {
            printf("{\"type\":\"stuck\",\"index\":%ld,\"calls\":%d,\"totalBytes\":%ld}\n", index, attempt + 1, total_now);
            fflush(stdout);
            G.stopped = "limit";
            G.limit = 1;
            return;
        }
        /* ADTS: the library keeps the bytes it was given and asks for more with 0x1002, so decoding goes on until it does. Raw access units: one
           unit per fill, one call per unit; a second call without new data conceals a frame (USAC answers 0x4004 with a valid output forever). */
        if (error == AAC_DEC_NOT_ENOUGH_BITS || (G.raw && left == 0)) {
            return;
        }
    }
    G.stopped = "limit";
    G.limit = 1;
}

/* Fills the bytes from..to, again with what the library did not take, and decodes after every fill. */
static void feed(long index, long from, long to, long offset, long declared, long received) {
    long position = from;
    while (position < to && !G.limit) {
        UCHAR *buffer[1] = {G.data + position};
        UINT size[1] = {(UINT)(to - position)};
        UINT valid = size[0];
        AAC_DECODER_ERROR fill_error = aacDecoder_Fill(G.decoder, buffer, size, &valid);
        long accepted = (long)size[0] - (long)valid;
        decode_all(index, position, to, (unsigned)fill_error, valid, offset, declared, received);
        if (accepted <= 0) {
            G.stopped = "error";
            G.limit = 1;
            return;
        }
        position += accepted;
        if (position > G.fed_end) {
            G.fed_end = position;
        }
    }
}

int main(int argc, char **argv) {
    if (argc != 5) {
        fprintf(stderr, "usage: fdk_decode units|bulk|adif file units.tsv concealment\n");
        return 2;
    }
    const char *mode = argv[1];
    int conceal = atoi(argv[4]);
    long length;
    unsigned char *data = read_file(argv[2], &length);

    Unit *units = calloc(1, sizeof(Unit));
    if (!units) {
        return 2;
    }
    long unit_count = 0, unit_capacity = 0;
    unsigned char config[4096];
    int config_length = 0;
    char kind[16] = "";
    FILE *tsv = fopen(argv[3], "r");
    if (!tsv) {
        perror(argv[3]);
        return 2;
    }
    char line[16384];
    while (fgets(line, sizeof line, tsv)) {
        Unit u;
        if (line[0] == 'U' && sscanf(line, "U %ld %ld %ld", &u.offset, &u.declared, &u.received) == 3) {
            if (unit_count == unit_capacity) {
                unit_capacity = unit_capacity ? unit_capacity * 2 : 1024;
                Unit *grown = realloc(units, unit_capacity * sizeof(Unit));
                if (!grown) {
                    return 2;
                }
                units = grown;
            }
            units[unit_count++] = u;
        } else if (line[0] == 'C') {
            line[strcspn(line, "\r\n")] = 0;
            config_length = hex(line + 2, config, sizeof config);
        } else if (line[0] == 'F') {
            char sha[80];
            sscanf(line, "F %79s %15s", sha, kind);
        }
    }
    fclose(tsv);

    int raw = strcmp(kind, "mp4") == 0;
    TRANSPORT_TYPE transport = !strcmp(mode, "adif") ? TT_MP4_ADIF : raw ? TT_MP4_RAW : TT_MP4_ADTS;
    HANDLE_AACDECODER decoder = aacDecoder_Open(transport, 1);
    if (!decoder) {
        printf("{\"type\":\"end\",\"stoppedBy\":\"error\",\"error\":\"aacDecoder_Open failed\"}\n");
        return 1;
    }
    LIB_INFO info[FDK_MODULE_LAST];
    memset(info, 0, sizeof info);
    aacDecoder_GetLibInfo(info);
    printf("{\"type\":\"env\",\"mode\":\"%s\",\"transport\":%d,\"fdkVersion\":\"", mode, (int)transport);
    for (int i = 0; i < FDK_MODULE_LAST; i++) {
        if (info[i].module_id == FDK_AACDEC) {
            printf("%s", info[i].versionStr);
        }
    }
    printf("\",\"concealMethod\":%d,\"setConceal\":\"0x%04X\",\"kind\":\"%s\",\"units\":%ld", conceal, (unsigned)aacDecoder_SetParam(decoder, AAC_CONCEAL_METHOD, conceal), kind, unit_count);
    if (raw) {
        UCHAR *c[1] = {config};
        UINT l[1] = {(UINT)config_length};
        printf(",\"configRaw\":\"0x%04X\",\"configBytes\":%d", (unsigned)aacDecoder_ConfigRaw(decoder, c, l), config_length);
        printf(",\"setConcealAfterConfig\":\"0x%04X\"", (unsigned)aacDecoder_SetParam(decoder, AAC_CONCEAL_METHOD, conceal));
    }
    printf("}\n");
    fflush(stdout);

    G.data = data;
    G.raw = raw;
    G.adif = !strcmp(mode, "adif");
    G.conceal = conceal;
    G.decoder = decoder;
    if (!strcmp(mode, "units")) {
        long fed_end = 0;
        int conceal_after_unit = 0;
        for (long i = 0; i < unit_count && !G.limit; i++) {
            long start = units[i].offset;
            if (start < 0 || start > length) {
                continue;
            }
            /* For ADTS everything from the end of the last fed unit is fed: the gap (junk, a resync gap) goes in front of the next unit.
               MP4 access units are fed alone. */
            long from = raw ? start : fed_end;
            long to = start + units[i].received;
            if (to > length) {
                to = length;
            }
            if (!raw && from > to) {
                continue; /* a unit that ends before the bytes already fed */
            }
            feed(i, from, to, start, units[i].declared, units[i].received);
            if (to > fed_end) {
                fed_end = to;
            }
            if (!conceal_after_unit) {
                conceal_after_unit = 1;
                printf("{\"type\":\"conceal\",\"when\":\"after the first unit\",\"status\":\"0x%04X\"}\n", (unsigned)aacDecoder_SetParam(decoder, AAC_CONCEAL_METHOD, conceal));
            }
        }
        if (!raw && fed_end < length && !G.limit) {
            feed(unit_count, fed_end, length, fed_end, length - fed_end, length - fed_end);
        }
    } else {
        feed(0, 0, length, 0, length, length);
    }
    /* what the library still holds when it answers 0x1002: the bytes fed that numTotalBytes has not counted (a frame of several raw data blocks keeps its last
       blocks until more bits arrive, and the drain discards them) */
    CStreamInfo *before = aacDecoder_GetStreamInfo(decoder);
    long fed_end = G.fed_end, used_end = before ? (long)before->numTotalBytes : -1;
    printf("{\"type\":\"held\",\"fedBytes\":%ld,\"usedBytes\":%ld,\"heldBytes\":%ld}\n", fed_end, used_end, used_end >= 0 ? fed_end - used_end : -1);
    fflush(stdout);
    long coded = G.coded, flushed = 0, calls = G.calls, outputs = G.outputs;

    /* The drain: ceil(outputDelay / frameSize) frames of AACDEC_FLUSH, as the design fixes it (probe question 7, approved lead answer 5), counted apart as
       flushedSamples and never in the coded total. AACDEC_FLUSH discards input still buffered (numTotalBytes in the last unit line shows it). */
    CStreamInfo *last = aacDecoder_GetStreamInfo(decoder);
    int drain = 0;
    if (last && last->frameSize > 0 && last->outputDelay > 0) {
        drain = (int)((last->outputDelay + (unsigned)last->frameSize - 1) / (unsigned)last->frameSize);
    }
    printf("{\"type\":\"drain\",\"outputDelay\":%u,\"frameSize\":%d,\"requests\":%d}\n", last ? last->outputDelay : 0, last ? last->frameSize : 0, drain);
    fflush(stdout);
    for (int i = 0; i < drain; i++) {
        AAC_DECODER_ERROR error = aacDecoder_DecodeFrame(decoder, pcm, MAX_PCM, AACDEC_FLUSH);
        long samples;
        int channels;
        int output = IS_OUTPUT_VALID(error);
        printf("{\"type\":\"flush\",\"index\":%d,\"error\":\"0x%04X\",\"outputValid\":%s", i, (unsigned)error, output ? "true" : "false");
        stream_info(decoder, &samples, &channels);
        if (output) {
            flushed += samples;
            printf(",\"samples\":%ld", samples);
        }
        printf("}\n");
        fflush(stdout);
        if (!output || error != AAC_DEC_OK) {
            break;
        }
    }
    printf("{\"type\":\"end\",\"stoppedBy\":\"%s\",\"calls\":%ld,\"outputs\":%ld,\"codedSamples\":%ld,\"flushedSamples\":%ld}\n", G.stopped, calls, outputs, coded, flushed);
    fflush(stdout);
    aacDecoder_Close(decoder);
    free(data);
    free(units);
    return 0;
}
