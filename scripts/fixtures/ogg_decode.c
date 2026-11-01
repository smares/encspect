/* The direct path for Opus and Vorbis in Ogg (docs/design/official-decoder-crosschecks.md): libogg splits the file into pages
   and packets, and libopus or libvorbis decodes each packet alone. One flushed JSON line per packet, so a crash or a stall
   leaves what was done. The decoders are used as black boxes and are not linked into Encspect.

     ogg_decode <mode> <file>

   modes:
     direct   libopus, opus_multistream_decode per packet (mapping families 0, 1, 2, 3 and 255: the multistream decoder for 0, 1, 2 and 255 with the header's table,
              the projection decoder for 3), and libvorbis with granulepos -1 and e_o_s 0 on every audio packet, so that libvorbis
              trims nothing (the coded stage)
     granule  as direct, but libvorbis gets the packets as libogg hands them, granule positions and end of stream included (the
              trimmed stage); libopus trims nothing in either mode

   Streams are told apart by serial number, so a multiplexed file is decoded stream by stream, and the codec of a stream is the one its
   first packet names; a stream of another codec (Speex) is counted apart and not decoded.

   libogg drops a page that fails its CRC: the skipped bytes are written as "skip" lines and a lost packet as "hole". A packet's file
   ranges are worked out from the pages' lacing values (a packet that spans pages has one range per page); when their sum is not the
   packet's size as libogg reports it, "ranges" is null and only the page is named. */
#include <inttypes.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ogg/ogg.h>
#include <opus/opus_multistream.h>
#include <opus/opus_projection.h>
#include <vorbis/codec.h>

#define MAX_STREAMS 64
#define CODEC_NONE 0
#define CODEC_OPUS 1
#define CODEC_VORBIS 2
#define CODEC_OTHER 3

typedef struct { long long offset, length; } Range;

typedef struct {
    Range *items;
    int count, capacity;
} RangeList;

static void ranges_add(RangeList *list, long long offset, long long length) {
    if (list->count == list->capacity) {
        list->capacity = list->capacity ? list->capacity * 2 : 4;
        list->items = realloc(list->items, sizeof(Range) * list->capacity);
        if (!list->items) {
            fprintf(stderr, "out of memory\n");
            exit(3);
        }
    }
    list->items[list->count].offset = offset;
    list->items[list->count].length = length;
    list->count++;
}

typedef struct {
    int used;
    int ended;
    long serial;
    long long index;
    long long last_sequence;
    ogg_stream_state os;
    int codec, header_packets, opus_head_seen;
    OpusMSDecoder *opus;
    OpusProjectionDecoder *projection;
    int opus_channels;
    vorbis_info vi;
    vorbis_comment vc;
    vorbis_dsp_state vd;
    vorbis_block vb;
    int vorbis_ready;
    RangeList pending;
} Slot;

static Slot slots[MAX_STREAMS];
static long long total_samples;
#define MAX_PACKET (16LL << 20)

static int is_opus(const ogg_packet *p) { return p->bytes >= 8 && !memcmp(p->packet, "OpusHead", 8); }
static int is_vorbis(const ogg_packet *p) { return p->bytes >= 7 && p->packet[0] == 1 && !memcmp(p->packet + 1, "vorbis", 6); }

static void slot_release(Slot *s) {
    if (!s->used) {
        return;
    }
    if (s->opus) {
        opus_multistream_decoder_destroy(s->opus);
    }
    if (s->projection) {
        opus_projection_decoder_destroy(s->projection);
    }
    if (s->vorbis_ready) {
        vorbis_block_clear(&s->vb);
        vorbis_dsp_clear(&s->vd);
    }
    vorbis_comment_clear(&s->vc);
    vorbis_info_clear(&s->vi);
    ogg_stream_clear(&s->os);
    free(s->pending.items);
    memset(s, 0, sizeof(*s));
}

static void print_ranges(const RangeList *list) {
    printf(",\"ranges\":[");
    for (int i = 0; i < list->count; i++) {
        printf("%s[%lld,%lld]", i ? "," : "", list->items[i].offset, list->items[i].length);
    }
    printf("]");
}

static void decode_opus(Slot *s, ogg_packet *op, float *pcm) {
    if (!s->opus_head_seen) {
        s->opus_head_seen = 1;
        if (!is_opus(op) || op->bytes < 19) {
            printf(",\"result\":\"not an OpusHead\",\"by\":\"harness\"}\n");
            return;
        }
        const unsigned char *h = op->packet;
        int channels = h[9], family = h[18];
        unsigned char mapping[256];
        int nstreams = 0, coupled = 0, err = 0, supported = 1;
        const char *why = "";
        if (channels < 1 || (family == 0 && channels > 2)) {
            /* RFC 7845 section 5.1.1 allows 1 or 2 channels in family 0. A channel count of 0 is handed to libopus as the header gives it; for family 0 with more than 2 channels the
               harness picks 1 uncoupled stream and the identity mapping itself, and libopus refuses that reading, not a table the header holds. */
            unsigned char identity[256];
            for (int k = 0; k < 255; k++) {
                identity[k] = (unsigned char)k;
            }
            OpusMSDecoder *probe = opus_multistream_decoder_create(48000, channels, 1, 0, identity, &err);
            if (probe) {
                opus_multistream_decoder_destroy(probe);
            }
            printf(",\"result\":\"not decoded: %s\",\"by\":\"libopus\",\"created\":%d,\"family\":%d,\"channels\":%d}\n", channels < 1 ? "channel count 0" : "family 0 with more than 2 channels", err, family, channels);
            return;
        } else if (family == 0) {
            nstreams = 1;
            coupled = channels == 2;
            mapping[0] = 0;
            mapping[1] = 1;
        } else if ((family == 1 || family == 2 || family == 255) && op->bytes >= 21 + channels) {
            /* RFC 7845 section 5.1.1.4 gives family 255 the table of family 1. */
            nstreams = h[19];
            coupled = h[20];
            memcpy(mapping, h + 21, channels);
        } else if (family == 3 && op->bytes >= 21 + 2 * channels * (h[19] + h[20])) {
            nstreams = h[19];
            coupled = h[20];
            s->projection = opus_projection_decoder_create(48000, channels, nstreams, coupled, (unsigned char *)h + 21, 2 * channels * (nstreams + coupled), &err);
        } else {
            supported = 0;
            why = "head too short for its mapping or an unknown family";
        }
        if (!supported) {
            printf(",\"result\":\"not decoded: %s\",\"by\":\"harness\",\"family\":%d,\"channels\":%d}\n", why, family, channels);
        } else {
            if (family != 3) {
                s->opus = opus_multistream_decoder_create(48000, channels, nstreams, coupled, mapping, &err);
            }
            s->opus_channels = channels;
            printf(",\"result\":\"head\",\"channels\":%d,\"family\":%d,\"preSkip\":%d,\"created\":%d}\n", channels, family, h[10] | (h[11] << 8), err);
        }
    } else if (op->bytes >= 8 && !memcmp(op->packet, "OpusTags", 8)) {
        printf(",\"result\":\"tags\"}\n");
    } else if (op->bytes == 0) {
        /* libopus treats an empty packet as a lost one and conceals as many samples as the frame size it is given; the count would be the harness's choice. */
        printf(",\"result\":\"not passed: empty packet\",\"channels\":%d,\"rate\":48000}\n", s->opus_channels);
    } else if (s->opus || s->projection) {
        int nb = opus_packet_get_nb_samples(op->packet, (opus_int32)op->bytes, 48000);
        int frames = s->opus ? opus_multistream_decode_float(s->opus, op->packet, (opus_int32)op->bytes, pcm, 5760, 0)
                             : opus_projection_decode_float(s->projection, op->packet, (opus_int32)op->bytes, pcm, 5760, 0);
        if (frames >= 0) {
            total_samples += frames;
            printf(",\"result\":\"decoded\",\"samples\":%d,\"tocSamples\":%d,\"channels\":%d,\"rate\":48000}\n", frames, nb, s->opus_channels);
        } else {
            printf(",\"result\":\"error\",\"error\":%d,\"tocSamples\":%d}\n", frames, nb);
        }
    } else {
        printf(",\"result\":\"no decoder\"}\n");
    }
}

static void decode_vorbis(Slot *s, ogg_packet *op, int keep_granule) {
    if (s->header_packets < 3 && !s->vorbis_ready) {
        int err = vorbis_synthesis_headerin(&s->vi, &s->vc, op);
        int init = 0;
        s->header_packets++;
        if (s->header_packets == 3 && err == 0) {
            init = vorbis_synthesis_init(&s->vd, &s->vi);
            if (init == 0) {
                vorbis_block_init(&s->vd, &s->vb);
                s->vorbis_ready = 1;
            }
        }
        printf(",\"result\":\"header\",\"error\":%d,\"initError\":%d%s}\n", err, init, s->header_packets == 1 && err == 0 ? ",\"detail\":\"identification\"" : "");
    } else if (s->vorbis_ready) {
        if (!keep_granule) {
            op->granulepos = -1;
            op->e_o_s = 0;
        }
        int err = vorbis_synthesis(&s->vb, op);
        long out = 0;
        if (err == 0) {
            vorbis_synthesis_blockin(&s->vd, &s->vb);
            float **pcm;
            int got_samples;
            while ((got_samples = vorbis_synthesis_pcmout(&s->vd, &pcm)) > 0) {
                out += got_samples;
                vorbis_synthesis_read(&s->vd, got_samples);
            }
            total_samples += out;
            printf(",\"result\":\"decoded\",\"samples\":%ld,\"channels\":%d,\"rate\":%ld}\n", out, s->vi.channels, s->vi.rate);
        } else {
            printf(",\"result\":\"error\",\"error\":%d}\n", err);
        }
    } else {
        printf(",\"result\":\"no decoder\"}\n");
    }
}

/* A packet's size follows from the lacing values; libogg would assemble it in memory before the harness could look, so the page is checked first. */
static int page_exceeds_cap(const Slot *s, const ogg_page *page) {
    long long current = 0;
    for (int k = 0; k < s->pending.count; k++) {
        current += s->pending.items[k].length;
    }
    for (int i = 0; i < page->header[26]; i++) {
        int lace = page->header[27 + i];
        current += lace;
        if (current > MAX_PACKET) {
            return 1;
        }
        if (lace < 255) {
            current = 0;
        }
    }
    return 0;
}

int main(int argc, char **argv) {
    if (argc != 3 || (strcmp(argv[1], "direct") && strcmp(argv[1], "granule"))) {
        fprintf(stderr, "usage: ogg_decode direct|granule file\n");
        return 2;
    }
    const char *mode = argv[1];
    int keep_granule = !strcmp(mode, "granule");
    FILE *file = fopen(argv[2], "rb");
    if (!file) {
        perror(argv[2]);
        return 2;
    }
    printf("{\"type\":\"env\",\"mode\":\"%s\",\"libvorbis\":\"%s\",\"libopus\":\"%s\"}\n", mode, vorbis_version_string(), opus_get_version_string());
    fflush(stdout);

    ogg_sync_state sync;
    ogg_sync_init(&sync);
    const char *stopped = "end";
    long long offset = 0, packet_index = 0, holes = 0, skipped_bytes = 0, pages = 0, streams = 0, refused_pages = 0, pages_after_eos = 0;
    static float opus_pcm[5760 * 255];
    int eof = 0;
    while (!eof) {
        char *buffer = ogg_sync_buffer(&sync, 4096);
        size_t got = fread(buffer, 1, 4096, file);
        ogg_sync_wrote(&sync, (long)got);
        if (got == 0) {
            eof = 1;
            if (ferror(file)) {
                stopped = "error";
            }
        }
        for (;;) {
            ogg_page page;
            long n = ogg_sync_pageseek(&sync, &page);
            if (n == 0) {
                break;
            }
            if (n < 0) {
                printf("{\"type\":\"skip\",\"offset\":%lld,\"bytes\":%ld}\n", offset, -n);
                fflush(stdout);
                offset += -n;
                skipped_bytes += -n;
                continue;
            }
            long long page_offset = offset;
            offset += n;
            pages++;
            long serial = (unsigned int)ogg_page_serialno(&page);
            Slot *s = NULL;
            for (int i = 0; i < MAX_STREAMS; i++) {
                if (slots[i].used && slots[i].serial == serial) {
                    s = &slots[i];
                    break;
                }
            }
            if (ogg_page_bos(&page) && s) {
                slot_release(s);
                s = NULL;
            }
            if (!s && ogg_page_bos(&page)) {
                for (int i = 0; i < MAX_STREAMS; i++) {
                    if (!slots[i].used) {
                        s = &slots[i];
                        break;
                    }
                }
                for (int i = 0; !s && i < MAX_STREAMS; i++) {
                    if (slots[i].used && slots[i].ended) {
                        slot_release(&slots[i]);
                        s = &slots[i];
                    }
                }
                if (s) {
                    memset(s, 0, sizeof(*s));
                    s->used = 1;
                    s->serial = serial;
                    s->index = streams++;
                    s->last_sequence = -1;
                    ogg_stream_init(&s->os, (int)serial);
                    vorbis_info_init(&s->vi);
                    vorbis_comment_init(&s->vc);
                    printf("{\"type\":\"stream\",\"stream\":%lld,\"offset\":%lld,\"serial\":%ld}\n", s->index, page_offset, serial);
                }
            }
            if (s && s->ended && !ogg_page_bos(&page)) {
                pages_after_eos++;
                printf("{\"type\":\"after-eos\",\"offset\":%lld,\"serial\":%ld}\n", page_offset, serial);
            }
            if (s && page_exceeds_cap(s, &page)) {
                stopped = "limit";
                printf("{\"type\":\"limit\",\"offset\":%lld,\"serial\":%ld,\"detail\":\"a packet over 16 MiB; the page is not given to libogg\"}\n", page_offset, serial);
                fflush(stdout);
                continue;
            }
            if (!s || ogg_stream_pagein(&s->os, &page) < 0) {
                refused_pages++;
                printf("{\"type\":\"pagein\",\"offset\":%lld,\"serial\":%ld,\"error\":true}\n", page_offset, serial);
                fflush(stdout);
                continue;
            }

            /* The packets this page completes, with their file ranges, in the order libogg returns them. */
            long long sequence = ogg_page_pageno(&page);
            if (s->last_sequence >= 0 && sequence != ((s->last_sequence + 1) & 0xFFFFFFFFLL)) {
                s->pending.count = 0;
            }
            s->last_sequence = sequence;
            RangeList queue[256];
            int expected_count = 0;
            {
                const unsigned char *lacing = page.header + 27;
                int segments = page.header[26];
                long long body = page_offset + page.header_len;
                long long start = 0, cursor = 0;
                int continued = page.header[5] & 1;
                int first = 1;
                int skipping = continued && s->pending.count == 0;
                for (int i = 0; i < segments; i++) {
                    int lace = lacing[i];
                    cursor += lace;
                    if (lace < 255) {
                        if (skipping) {
                            skipping = 0;
                        } else {
                            RangeList list = {0};
                            if (first) {
                                for (int k = 0; k < s->pending.count; k++) {
                                    ranges_add(&list, s->pending.items[k].offset, s->pending.items[k].length);
                                }
                            }
                            ranges_add(&list, body + start, cursor - start);
                            queue[expected_count++] = list;
                        }
                        first = 0;
                        start = cursor;
                    }
                }
                if (expected_count > 0 || !continued) {
                    s->pending.count = 0;
                }
                if (start < cursor && !skipping) {
                    ranges_add(&s->pending, body + start, cursor - start);
                }
            }
            int next = 0;

            for (;;) {
                ogg_packet op;
                int r = ogg_stream_packetout(&s->os, &op);
                if (r == 0) {
                    break;
                }
                if (r < 0) {
                    holes++;
                    printf("{\"type\":\"hole\",\"stream\":%lld,\"pageOffset\":%lld}\n", s->index, page_offset);
                    fflush(stdout);
                    continue;
                }
                long long index = packet_index++;
                RangeList *ranges = next < expected_count ? &queue[next] : NULL;
                next++;
                long long sum = 0;
                if (ranges) {
                    for (int k = 0; k < ranges->count; k++) {
                        sum += ranges->items[k].length;
                    }
                }
                printf("{\"type\":\"packet\",\"index\":%lld,\"stream\":%lld,\"pageOffset\":%lld,\"bytes\":%ld,\"granulepos\":%" PRId64 ",\"eos\":%d", index, s->index, page_offset,
                       op.bytes, (int64_t)op.granulepos, op.e_o_s ? 1 : 0);
                if (ranges && sum == op.bytes) {
                    print_ranges(ranges);
                } else {
                    printf(",\"ranges\":null");
                }
                if (s->codec == CODEC_NONE) {
                    s->codec = is_opus(&op) ? CODEC_OPUS : is_vorbis(&op) ? CODEC_VORBIS : CODEC_OTHER;
                    printf(",\"codec\":\"%s\"", s->codec == CODEC_OPUS ? "opus" : s->codec == CODEC_VORBIS ? "vorbis" : "other");
                }
                if (op.bytes > MAX_PACKET) {
                    stopped = "limit";
                    printf(",\"result\":\"not passed: packet over 16 MiB\"}\n");
                } else if (s->codec == CODEC_OPUS) {
                    decode_opus(s, &op, opus_pcm);
                } else if (s->codec == CODEC_VORBIS) {
                    if (s->header_packets == 0 && !is_vorbis(&op)) {
                        printf(",\"result\":\"not a Vorbis identification header\"}\n");
                    } else {
                        decode_vorbis(s, &op, keep_granule);
                    }
                } else {
                    printf(",\"result\":\"other codec\"}\n");
                }
                fflush(stdout);
            }
            for (int k = 0; k < expected_count; k++) {
                free(queue[k].items);
            }
            if (ogg_page_eos(&page)) {
                s->ended = 1;
            }
        }
    }
    long long trailing = sync.fill - sync.returned;
    for (int i = 0; i < MAX_STREAMS; i++) {
        slot_release(&slots[i]);
    }
    ogg_sync_clear(&sync);
    fclose(file);
    printf("{\"type\":\"end\",\"stoppedBy\":\"%s\",\"pages\":%lld,\"streams\":%lld,\"packets\":%lld,\"holes\":%lld,\"skippedBytes\":%lld,\"trailingBytes\":%lld,\"refusedPages\":%lld,\"pagesAfterEndOfStream\":%lld,\"codedSamples\":%lld}\n",
           stopped, pages, streams, packet_index, holes, skipped_bytes, trailing, refused_pages, pages_after_eos, total_samples);
    fflush(stdout);
    return 0;
}
