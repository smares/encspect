/* The macOS paths of the official-decoder probe (docs/design/official-decoder-crosschecks.md): what AudioToolbox can decode, and
   two ways to decode a file with it. Apple's decoders are used as black boxes; nothing of them is linked into Encspect.

     audiotoolbox_probe inventory
         the formats AudioToolbox decodes, the file types it reads, and the decoders registered per format
     audiotoolbox_probe reader <file>
         ExtAudioFile: opens the file, sets a 16-bit client format and reads to the end; one line per read
     audiotoolbox_probe convert <file> <units.tsv> <default|none>
         AudioConverter: one unit of the unit list (unit_list.py --tsv) per call, with the file's magic cookie; the prime method
         is left at the converter's default or set to none (kConverterPrimeMethod_None)

   Every result is one JSON line, written and flushed before the next call, so a killed child leaves what it did. A cut unit is fed
   from its first byte to the last byte the file holds and never padded. */
#include <AudioToolbox/AudioToolbox.h>
#include <CoreFoundation/CoreFoundation.h>
#include <inttypes.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void fourcc(UInt32 value, char *out) {
    for (int i = 0; i < 4; i++) {
        unsigned char c = (value >> (24 - 8 * i)) & 0xFF;
        out[i] = (c >= 32 && c < 127 && c != '"' && c != '\\') ? (char)c : '?';
    }
    out[4] = 0;
}

static void print_cfstring(CFStringRef s) {
    char buffer[512] = "";
    if (s && CFStringGetCString(s, buffer, sizeof buffer, kCFStringEncodingUTF8)) {
        for (char *p = buffer; *p; p++) {
            if (*p == '"' || *p == '\\') {
                *p = '?';
            }
        }
    }
    printf("%s", buffer);
}

static void print_asbd(const char *key, const AudioStreamBasicDescription *d) {
    char id[5];
    fourcc(d->mFormatID, id);
    printf("\"%s\":{\"formatID\":\"%s\",\"sampleRate\":%.1f,\"formatFlags\":%u,\"bytesPerPacket\":%u,\"framesPerPacket\":%u,\"bytesPerFrame\":%u,"
           "\"channels\":%u,\"bitsPerChannel\":%u}",
           key, id, d->mSampleRate, (unsigned)d->mFormatFlags, (unsigned)d->mBytesPerPacket, (unsigned)d->mFramesPerPacket, (unsigned)d->mBytesPerFrame,
           (unsigned)d->mChannelsPerFrame, (unsigned)d->mBitsPerChannel);
}

static int inventory(void) {
    printf("{\"type\":\"inventory\"}\n");
    UInt32 size = 0;
    OSStatus status = AudioFormatGetPropertyInfo(kAudioFormatProperty_DecodeFormatIDs, 0, NULL, &size);
    UInt32 *ids = size ? malloc(size) : NULL;
    if (status == 0 && ids) {
        status = AudioFormatGetProperty(kAudioFormatProperty_DecodeFormatIDs, 0, NULL, &size, ids);
    }
    printf("{\"type\":\"decodeFormatIDs\",\"status\":%d,\"ids\":[", (int)status);
    for (UInt32 i = 0; status == 0 && i < size / 4; i++) {
        char id[5];
        fourcc(ids[i], id);
        printf("%s\"%s\"", i ? "," : "", id);
    }
    printf("]}\n");
    fflush(stdout);
    free(ids);

    size = 0;
    status = AudioFileGetGlobalInfoSize(kAudioFileGlobalInfo_ReadableTypes, 0, NULL, &size);
    UInt32 *types = size ? malloc(size) : NULL;
    if (status == 0 && types) {
        status = AudioFileGetGlobalInfo(kAudioFileGlobalInfo_ReadableTypes, 0, NULL, &size, types);
    }
    printf("{\"type\":\"readableTypes\",\"status\":%d,\"types\":[", (int)status);
    for (UInt32 i = 0; status == 0 && i < size / 4; i++) {
        char id[5];
        fourcc(types[i], id);
        printf("%s{\"type\":\"%s\",\"name\":\"", i ? "," : "", id);
        CFStringRef name = NULL;
        UInt32 n = sizeof name;
        if (AudioFileGetGlobalInfo(kAudioFileGlobalInfo_FileTypeName, sizeof(UInt32), &types[i], &n, &name) == 0 && name) {
            print_cfstring(name);
            CFRelease(name);
        }
        printf("\",\"extensions\":[");
        CFArrayRef extensions = NULL;
        n = sizeof extensions;
        if (AudioFileGetGlobalInfo(kAudioFileGlobalInfo_ExtensionsForType, sizeof(UInt32), &types[i], &n, &extensions) == 0 && extensions) {
            for (CFIndex k = 0; k < CFArrayGetCount(extensions); k++) {
                printf("%s\"", k ? "," : "");
                print_cfstring(CFArrayGetValueAtIndex(extensions, k));
                printf("\"");
            }
            CFRelease(extensions);
        }
        printf("]}");
    }
    printf("]}\n");
    fflush(stdout);
    free(types);

    /* The decoders registered for the formats of the open questions. */
    const char *formats[] = {"aac ", "aach", "aacp", "aacl", "aace", "usac", "ac-3", "ec-3", "dts ", "dtsh", "dtsl", "mlp ", "alac", "flac", "opus", "tta ", "wma ", "ac-4", ".mp3", "mpg3"};
    for (size_t f = 0; f < sizeof formats / sizeof formats[0]; f++) {
        UInt32 format = ((UInt32)(unsigned char)formats[f][0] << 24) | ((UInt32)(unsigned char)formats[f][1] << 16) | ((UInt32)(unsigned char)formats[f][2] << 8) |
                        (UInt32)(unsigned char)formats[f][3];
        UInt32 bytes = 0;
        OSStatus s = AudioFormatGetPropertyInfo(kAudioFormatProperty_Decoders, sizeof format, &format, &bytes);
        printf("{\"type\":\"decoders\",\"format\":\"%s\",\"status\":%d,\"decoders\":[", formats[f], (int)s);
        if (s == 0 && bytes) {
            AudioClassDescription *list = malloc(bytes);
            if (AudioFormatGetProperty(kAudioFormatProperty_Decoders, sizeof format, &format, &bytes, list) == 0) {
                for (UInt32 i = 0; i < bytes / sizeof(AudioClassDescription); i++) {
                    char t[5], st[5], m[5];
                    fourcc(list[i].mType, t);
                    fourcc(list[i].mSubType, st);
                    fourcc(list[i].mManufacturer, m);
                    printf("%s{\"type\":\"%s\",\"subType\":\"%s\",\"manufacturer\":\"%s\"}", i ? "," : "", t, st, m);
                }
            }
            free(list);
        }
        printf("]}\n");
        fflush(stdout);
    }
    return 0;
}

static int reader(const char *path) {
    CFURLRef url = CFURLCreateFromFileSystemRepresentation(NULL, (const UInt8 *)path, (CFIndex)strlen(path), false);
    ExtAudioFileRef file = NULL;
    OSStatus status = ExtAudioFileOpenURL(url, &file);
    printf("{\"type\":\"open\",\"status\":%d}\n", (int)status);
    fflush(stdout);
    if (status != 0) {
        printf("{\"type\":\"end\",\"stoppedBy\":\"error\",\"opened\":false}\n");
        return 0;
    }
    AudioStreamBasicDescription fileFormat;
    memset(&fileFormat, 0, sizeof fileFormat);
    UInt32 size = sizeof fileFormat;
    status = ExtAudioFileGetProperty(file, kExtAudioFileProperty_FileDataFormat, &size, &fileFormat);
    printf("{\"type\":\"fileFormat\",\"status\":%d,", (int)status);
    print_asbd("format", &fileFormat);
    printf("}\n");
    SInt64 lengthFrames = -1;
    size = sizeof lengthFrames;
    status = ExtAudioFileGetProperty(file, kExtAudioFileProperty_FileLengthFrames, &size, &lengthFrames);
    printf("{\"type\":\"fileLengthFrames\",\"status\":%d,\"frames\":%" PRId64 "}\n", (int)status, (int64_t)lengthFrames);

    /* What the underlying AudioFile says about priming and padding. */
    AudioFileID audio = NULL;
    size = sizeof audio;
    if (ExtAudioFileGetProperty(file, kExtAudioFileProperty_AudioFile, &size, &audio) == 0 && audio) {
        AudioFilePacketTableInfo table;
        memset(&table, 0, sizeof table);
        size = sizeof table;
        OSStatus s = AudioFileGetProperty(audio, kAudioFilePropertyPacketTableInfo, &size, &table);
        if (s == 0) {
            printf("{\"type\":\"packetTableInfo\",\"status\":0,\"validFrames\":%" PRId64 ",\"primingFrames\":%d,\"remainderFrames\":%d}\n",
                   (int64_t)table.mNumberValidFrames, (int)table.mPrimingFrames, (int)table.mRemainderFrames);
        } else {
            printf("{\"type\":\"packetTableInfo\",\"status\":%d,\"validFrames\":null,\"primingFrames\":null,\"remainderFrames\":null}\n", (int)s);
        }
        UInt64 packets = 0;
        size = sizeof packets;
        s = AudioFileGetProperty(audio, kAudioFilePropertyAudioDataPacketCount, &size, &packets);
        printf("{\"type\":\"packetCount\",\"status\":%d,\"packets\":%" PRIu64 "}\n", (int)s, (uint64_t)packets);
    }
    fflush(stdout);

    AudioStreamBasicDescription client;
    memset(&client, 0, sizeof client);
    client.mFormatID = kAudioFormatLinearPCM;
    client.mSampleRate = fileFormat.mSampleRate;
    client.mChannelsPerFrame = fileFormat.mChannelsPerFrame ? fileFormat.mChannelsPerFrame : 2;
    client.mBitsPerChannel = 16;
    client.mFormatFlags = kAudioFormatFlagIsSignedInteger | kAudioFormatFlagIsPacked;
    client.mFramesPerPacket = 1;
    client.mBytesPerFrame = client.mBytesPerPacket = 2 * client.mChannelsPerFrame;
    status = ExtAudioFileSetProperty(file, kExtAudioFileProperty_ClientDataFormat, sizeof client, &client);
    printf("{\"type\":\"clientFormat\",\"status\":%d,", (int)status);
    print_asbd("format", &client);
    printf("}\n");
    fflush(stdout);
    if (status != 0) {
        printf("{\"type\":\"end\",\"stoppedBy\":\"error\",\"opened\":true,\"clientFormat\":false}\n");
        return 0;
    }
    /* The length again, now that the client format is set: ExtAudioFile states it in the client's frames. */
    lengthFrames = -1;
    size = sizeof lengthFrames;
    status = ExtAudioFileGetProperty(file, kExtAudioFileProperty_FileLengthFrames, &size, &lengthFrames);
    printf("{\"type\":\"fileLengthFramesClient\",\"status\":%d,\"frames\":%" PRId64 "}\n", (int)status, (int64_t)lengthFrames);

    enum { FRAMES = 4096 };
    size_t capacity = (size_t)FRAMES * client.mBytesPerFrame;
    char *buffer = malloc(capacity);
    long long total = 0, reads = 0;
    const char *stopped = "end";
    for (;;) {
        AudioBufferList list;
        list.mNumberBuffers = 1;
        list.mBuffers[0].mNumberChannels = client.mChannelsPerFrame;
        list.mBuffers[0].mDataByteSize = (UInt32)capacity;
        list.mBuffers[0].mData = buffer;
        UInt32 frames = FRAMES;
        status = ExtAudioFileRead(file, &frames, &list);
        printf("{\"type\":\"read\",\"index\":%lld,\"status\":%d,\"frames\":%u}\n", reads, (int)status, (unsigned)frames);
        fflush(stdout);
        reads++;
        total += frames;
        if (status != 0) {
            stopped = "error";
            break;
        }
        if (frames == 0) {
            break;
        }
        if (reads > 2000000) {
            stopped = "limit";
            break;
        }
    }
    printf("{\"type\":\"end\",\"stoppedBy\":\"%s\",\"opened\":true,\"reads\":%lld,\"totalFrames\":%lld,\"lastStatus\":%d}\n", stopped, reads, total, (int)status);
    fflush(stdout);
    free(buffer);
    ExtAudioFileDispose(file);
    return 0;
}

typedef struct {
    long offset, declared, received;
} Unit;

typedef struct {
    const unsigned char *data;
    const Unit *unit;
    int have;
    AudioStreamPacketDescription description;
    int delivered;
    int eos; /* set for the drain: the input proc then answers with no packets and no error, which tells the converter the stream has ended */
    int adts; /* the converter takes access units, not ADTS frames: the header (7 bytes, 9 with a CRC) is left out */
} Feed;

#define NO_MORE_DATA ((OSStatus)0x6e6f6d6f) /* 'nomo': the input has no further unit for this call */

static OSStatus supply(AudioConverterRef converter, UInt32 *packets, AudioBufferList *data, AudioStreamPacketDescription **descriptions, void *user) {
    (void)converter;
    Feed *feed = user;
    if (!feed->have) {
        *packets = 0;
        return feed->eos ? 0 : NO_MORE_DATA;
    }
    feed->have = 0;
    feed->delivered++;
    long skip = 0;
    if (feed->adts) {
        skip = (feed->data[feed->unit->offset + 1] & 1) ? 7 : 9;
        if (skip > feed->unit->received) {
            skip = feed->unit->received;
        }
    }
    data->mNumberBuffers = 1;
    data->mBuffers[0].mNumberChannels = 0;
    data->mBuffers[0].mDataByteSize = (UInt32)(feed->unit->received - skip);
    data->mBuffers[0].mData = (void *)(feed->data + feed->unit->offset + skip);
    feed->description.mStartOffset = 0;
    feed->description.mVariableFramesInPacket = 0;
    feed->description.mDataByteSize = (UInt32)(feed->unit->received - skip);
    if (descriptions) {
        *descriptions = &feed->description;
    }
    *packets = 1;
    return 0;
}

static unsigned char *read_file(const char *path, long *length) {
    FILE *file = fopen(path, "rb");
    if (!file) {
        perror(path);
        exit(2);
    }
    fseek(file, 0, SEEK_END);
    *length = ftell(file);
    fseek(file, 0, SEEK_SET);
    unsigned char *data = malloc(*length ? *length : 1);
    if (fread(data, 1, *length, file) != (size_t)*length) {
        exit(2);
    }
    fclose(file);
    return data;
}

static int hexbytes(const char *text, unsigned char *out, int max) {
    int n = 0;
    while (text[0] && text[1] && n < max) {
        char pair[3] = {text[0], text[1], 0};
        out[n++] = (unsigned char)strtol(pair, NULL, 16);
        text += 2;
    }
    return n;
}

/* An esds payload: ES_Descriptor { DecoderConfigDescriptor { DecoderSpecificInfo }, SLConfigDescriptor }. */
static unsigned char *build_esds(const unsigned char *config, int config_length, UInt32 *size) {
    unsigned char *cookie = calloc(1, (size_t)config_length + 26);
    unsigned char *p = cookie;
    *p++ = 3; *p++ = (unsigned char)(config_length + 23); *p++ = 0; *p++ = 0; *p++ = 0;
    *p++ = 4; *p++ = (unsigned char)(config_length + 15); *p++ = 0x40; *p++ = 0x15; p += 3; p += 8;
    *p++ = 5; *p++ = (unsigned char)config_length;
    memcpy(p, config, config_length);
    p += config_length;
    *p++ = 6; *p++ = 1; *p++ = 2;
    *size = (UInt32)(p - cookie);
    return cookie;
}

static int convert(const char *path, const char *tsv_path, const char *prime) {
    long length;
    unsigned char *data = read_file(path, &length);
    Unit *units = NULL;
    long count = 0, capacity = 0;
    char codec[16] = "", kind[16] = "", line[2048];
    unsigned char config[512];
    int config_length = 0, adts_profile = -1, adts_sfi = 0, adts_channels = 0;
    FILE *tsv = fopen(tsv_path, "r");
    if (!tsv) {
        perror(tsv_path);
        return 2;
    }
    while (fgets(line, sizeof line, tsv)) {
        Unit u;
        if (line[0] == 'U' && sscanf(line, "U %ld %ld %ld", &u.offset, &u.declared, &u.received) == 3) {
            if (count == capacity) {
                capacity = capacity ? capacity * 2 : 1024;
                units = realloc(units, capacity * sizeof(Unit));
            }
            units[count++] = u;
        } else if (line[0] == 'F') {
            char sha[80];
            sscanf(line, "F %79s %15s", sha, kind);
        } else if (line[0] == 'T') {
            sscanf(line, "T %15s", codec);
        } else if (line[0] == 'C') {
            line[strcspn(line, "\r\n")] = 0;
            config_length = hexbytes(line + 2, config, sizeof config);
        } else if (line[0] == 'H') {
            sscanf(line, "H %d %d %d", &adts_profile, &adts_sfi, &adts_channels);
        }
    }
    fclose(tsv);

    /* The input description: what AudioFile reads from the file (stream description and magic cookie) is the first source, since
       the stream description must be filled out in full for some formats; the second is built from the unit list and the
       kAudioFormatProperty_FormatInfo of the cookie. The record says which one was used. */
    if (!strcmp(kind, "adts") && adts_profile >= 0 && config_length == 0) {
        int value = ((adts_profile + 1) << 11) | (adts_sfi << 7) | (adts_channels << 3);
        config[0] = (unsigned char)(value >> 8);
        config[1] = (unsigned char)value;
        config_length = 2;
    }
    AudioStreamBasicDescription input;
    memset(&input, 0, sizeof input);
    unsigned char *cookie = NULL;
    UInt32 cookie_size = 0;
    const char *source = "none";
    CFURLRef url = CFURLCreateFromFileSystemRepresentation(NULL, (const UInt8 *)path, (CFIndex)strlen(path), false);
    AudioFileID audio = NULL;
    OSStatus status = AudioFileOpenURL(url, kAudioFileReadPermission, 0, &audio);
    printf("{\"type\":\"setup\",\"audioFileOpen\":%d", (int)status);
    if (status == 0) {
        UInt32 size = sizeof input;
        OSStatus s = AudioFileGetProperty(audio, kAudioFilePropertyDataFormat, &size, &input);
        printf(",\"dataFormat\":%d", (int)s);
        if (s == 0) {
            source = "audiofile";
        }
        /* The file's format list names the layers of a stream (HE-AAC: the base layer and the extended one); the first playable entry is the
           one a player uses and the one the converter is built from. */
        UInt32 list_size = 0, list_writable = 0;
        if (AudioFileGetPropertyInfo(audio, kAudioFilePropertyFormatList, &list_size, &list_writable) == 0 && list_size >= sizeof(AudioFormatListItem)) {
            AudioFormatListItem *list = malloc(list_size);
            if (AudioFileGetProperty(audio, kAudioFilePropertyFormatList, &list_size, list) == 0) {
                UInt32 count = list_size / sizeof(AudioFormatListItem), index = 0, index_size = sizeof index;
                OSStatus fs = AudioFormatGetProperty(kAudioFormatProperty_FirstPlayableFormatFromList, list_size, list, &index_size, &index);
                printf(",\"formatListEntries\":%u,\"firstPlayable\":%d,\"firstPlayableIndex\":%u,\"formatList\":[", (unsigned)count, (int)fs, (unsigned)index);
                for (UInt32 i = 0; i < count && i < 8; i++) {
                    char id[5];
                    fourcc(list[i].mASBD.mFormatID, id);
                    printf("%s{\"formatID\":\"%s\",\"sampleRate\":%.1f,\"framesPerPacket\":%u,\"channels\":%u}", i ? "," : "", id, list[i].mASBD.mSampleRate,
                           (unsigned)list[i].mASBD.mFramesPerPacket, (unsigned)list[i].mASBD.mChannelsPerFrame);
                }
                printf("]");
                if (fs == 0 && index < count) {
                    input = list[index].mASBD;
                    source = "audiofile-format-list";
                }
            }
            free(list);
        }
        UInt32 cs = 0;
        UInt32 writable = 0;
        if (AudioFileGetPropertyInfo(audio, kAudioFilePropertyMagicCookieData, &cs, &writable) == 0 && cs > 0) {
            cookie = malloc(cs);
            cookie_size = cs;
            if (AudioFileGetProperty(audio, kAudioFilePropertyMagicCookieData, &cs, cookie) != 0) {
                free(cookie);
                cookie = NULL;
                cookie_size = 0;
            }
        }
    }
    if (strcmp(source, "audiofile") != 0 && strcmp(source, "audiofile-format-list") != 0) {
        UInt32 id = !strcmp(codec, "ac-3") ? kAudioFormatAC3 : !strcmp(codec, "ec-3") ? kAudioFormatEnhancedAC3 : kAudioFormatMPEG4AAC;
        input.mFormatID = id;
        if (adts_profile >= 0) {
            static const double rates[] = {96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350};
            input.mSampleRate = adts_sfi < 13 ? rates[adts_sfi] : 0;
            input.mChannelsPerFrame = adts_channels == 7 ? 8 : adts_channels;
            input.mFramesPerPacket = 1024;
        }
        if (config_length > 0 && id == kAudioFormatMPEG4AAC) {
            cookie = build_esds(config, config_length, &cookie_size);
        } else if (config_length > 0) {
            cookie = malloc(config_length);
            memcpy(cookie, config, config_length);
            cookie_size = (UInt32)config_length;
        }
        UInt32 size = sizeof input;
        OSStatus s = AudioFormatGetProperty(kAudioFormatProperty_FormatInfo, cookie_size, cookie, &size, &input);
        printf(",\"formatInfo\":%d", (int)s);
        source = "unit-list";
    }
    if (!cookie && config_length > 0 && !strcmp(codec, "aac")) {
        cookie = build_esds(config, config_length, &cookie_size);
        source = strncmp(source, "audiofile", 9) ? source : "audiofile+esds";
    }
    printf(",\"source\":\"%s\",\"cookieBytes\":%u,", source, (unsigned)cookie_size);
    print_asbd("input", &input);
    printf("}\n");
    fflush(stdout);

    AudioStreamBasicDescription output;
    memset(&output, 0, sizeof output);
    output.mFormatID = kAudioFormatLinearPCM;
    output.mSampleRate = input.mSampleRate;
    output.mChannelsPerFrame = input.mChannelsPerFrame ? input.mChannelsPerFrame : 2;
    output.mFormatFlags = kAudioFormatFlagsNativeFloatPacked;
    output.mBitsPerChannel = 32;
    output.mFramesPerPacket = 1;
    output.mBytesPerFrame = output.mBytesPerPacket = 4 * output.mChannelsPerFrame;
    AudioConverterRef converter = NULL;
    status = AudioConverterNew(&input, &output, &converter);
    printf("{\"type\":\"converter\",\"status\":%d,", (int)status);
    print_asbd("output", &output);
    printf("}\n");
    fflush(stdout);
    if (status != 0) {
        printf("{\"type\":\"end\",\"stoppedBy\":\"error\",\"converter\":false}\n");
        return 0;
    }
    if (cookie_size) {
        status = AudioConverterSetProperty(converter, kAudioConverterDecompressionMagicCookie, cookie_size, cookie);
        printf("{\"type\":\"cookie\",\"status\":%d}\n", (int)status);
    }
    UInt32 method = 0, size = sizeof method;
    status = AudioConverterGetProperty(converter, kAudioConverterPrimeMethod, &size, &method);
    if (status == 0) {
        printf("{\"type\":\"primeMethod\",\"when\":\"default\",\"status\":0,\"method\":%u}\n", (unsigned)method);
    } else {
        printf("{\"type\":\"primeMethod\",\"when\":\"default\",\"status\":%d,\"method\":null}\n", (int)status);
    }
    if (!strcmp(prime, "none")) {
        method = kConverterPrimeMethod_None;
        status = AudioConverterSetProperty(converter, kAudioConverterPrimeMethod, sizeof method, &method);
        printf("{\"type\":\"primeMethod\",\"when\":\"set none\",\"status\":%d}\n", (int)status);
    }
    AudioConverterPrimeInfo prime_info;
    memset(&prime_info, 0, sizeof prime_info);
    size = sizeof prime_info;
    status = AudioConverterGetProperty(converter, kAudioConverterPrimeInfo, &size, &prime_info);
    if (status == 0) {
        printf("{\"type\":\"primeInfo\",\"status\":0,\"leadingFrames\":%u,\"trailingFrames\":%u}\n", (unsigned)prime_info.leadingFrames, (unsigned)prime_info.trailingFrames);
    } else {
        printf("{\"type\":\"primeInfo\",\"status\":%d,\"leadingFrames\":null,\"trailingFrames\":null}\n", (int)status);
    }
    fflush(stdout);

    enum { OUT_FRAMES = 16384 };
    size_t out_bytes = (size_t)OUT_FRAMES * output.mBytesPerFrame;
    char *out_buffer = malloc(out_bytes);
    long long total = 0, calls = 0, failed = 0;
    Feed feed = {data, NULL, 0, {0, 0, 0}, 0, 0, !strcmp(kind, "adts")};
    for (long i = 0; i <= count + 3; i++) {
        int is_unit = i < count;
        if (is_unit) {
            feed.unit = &units[i];
            feed.have = 1;
        } else {
            feed.have = 0;
            feed.eos = 1; /* the drain calls: the end of the stream */
        }
        AudioBufferList list;
        list.mNumberBuffers = 1;
        list.mBuffers[0].mNumberChannels = output.mChannelsPerFrame;
        list.mBuffers[0].mDataByteSize = (UInt32)out_bytes;
        list.mBuffers[0].mData = out_buffer;
        UInt32 frames = OUT_FRAMES;
        status = AudioConverterFillComplexBuffer(converter, supply, &feed, &frames, &list, NULL);
        int consumed = is_unit ? !feed.have : 0;
        printf("{\"type\":\"%s\",\"index\":%ld,", is_unit ? "unit" : "drain", i);
        if (is_unit) {
            printf("\"offset\":%ld,\"declared\":%ld,\"received\":%ld,\"consumed\":%s,", units[i].offset, units[i].declared, units[i].received, consumed ? "true" : "false");
        }
        printf("\"status\":%d,\"noMoreData\":%s,\"frames\":%u}\n", (int)status, status == NO_MORE_DATA ? "true" : "false", (unsigned)frames);
        fflush(stdout);
        calls++;
        total += frames;
        if (status != 0 && status != NO_MORE_DATA) {
            failed++;
        }
    }
    printf("{\"type\":\"end\",\"stoppedBy\":\"%s\",\"calls\":%lld,\"failedCalls\":%lld,\"codedFrames\":%lld}\n", failed ? "error" : "end", calls, failed, total);
    fflush(stdout);
    AudioConverterDispose(converter);
    free(out_buffer);
    free(units);
    free(data);
    return 0;
}

int main(int argc, char **argv) {
    if (argc >= 2 && !strcmp(argv[1], "inventory")) {
        return inventory();
    }
    if (argc == 3 && !strcmp(argv[1], "reader")) {
        return reader(argv[2]);
    }
    if (argc == 5 && !strcmp(argv[1], "convert")) {
        return convert(argv[2], argv[3], argv[4]);
    }
    fprintf(stderr, "usage: audiotoolbox_probe inventory | reader file | convert file units.tsv default|none\n");
    return 2;
}
