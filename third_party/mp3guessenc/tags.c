/*
 *   tags is a support module which provides access to metadata tags (and xing/vbri/lame tags as well)
 *   Copyright (C) 2013-2021 Elio Blanca <eblanca76@users.sourceforge.net>
 *
 *   This library is free software; you can redistribute it and/or
 *   modify it under the terms of the GNU Lesser General Public
 *   License as published by the Free Software Foundation; either
 *   version 2.1 of the License, or (at your option) any later version.
 *
 *   This library is distributed in the hope that it will be useful,
 *   but WITHOUT ANY WARRANTY; without even the implied warranty of
 *   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 *   Lesser General Public License for more details.
 *
 *   You should have received a copy of the GNU Lesser General Public
 *   License along with this library; if not, write to the Free Software
 *   Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 */

#include "mp3g_io_config.h"

#include <ctype.h>
#include <stdlib.h>
#include <string.h>

#include "bit_utils.h"
#include "mp3guessenc.h"


#define ID3V2_HEADER_LENGTH                     10
#define ID3V2_FOOTER_LENGTH    ID3V2_HEADER_LENGTH
#define ID3V2_FLAGS_FOOTER_PRESENT            0x10
#define ID3V2_ID_STRING                      "ID3"

#define LYRICS3V1_TAG_MAXSIZE                 5100
#define LYRICS3_BEGIN_SIGNATURE      "LYRICSBEGIN"
#define LYRICS3_BEGIN_SIGNATURE_LEN             11
#define LYRICS3V1_END_SIGNATURE        "LYRICSEND"
#define LYRICS3V2_END_SIGNATURE        "LYRICS200"
#define LYRICS3_END_SIGNATURE_LEN                9
#define LYRICS3V2_TAGSIZE_LEN                    6

#define APETAGEX_SIGNATURE              "APETAGEX"
#define APETAGEX_FLAGS_HEADER_PRESENT   0x80000000
#define APETAGEX_FLAGS_THIS_IS_HEADER   0x20000000

#define MMTAG_FOOTER_SIZE                       48
#define MMTAG_DATA_OFFSETS_SIZE                 20
#define MMTAG_META_DATA_MINIMUM_SIZE          7868
#define MMTAG_VERSION_INFO_SIZE                256
#define MMTAG_HEADER_SIZE  MMTAG_VERSION_INFO_SIZE
/* This `safe search size' is choosen on purpose. A larger buffer may lead to
 * include the optional mmtag header and searching for the sync bytes will
 * result in detecting that optional header as the mandatory version info block
 * (unless one would do a further second search and after that dives into
 * pointer calculations...)
 * With this size and the metadata section at its maximum size (8132 bytes), we
 * can find the mandatory version block at the head of the buffer (at most) */
#define MMTAG_SAFE_SEARCH_SIZE                 520

#define MMTAG_HEADER                             0
#define MMTAG_IMAGE_EXTENSION                    1
#define MMTAG_IMAGE_BINARY                       2
#define MMTAG_UNUSED                             3
#define MMTAG_VERSION_INFO                       4
#define MMTAG_AUDIO_METADATA                     5
#define MMTAG_OFFSET_ENTRIES                     6

#define MMTAG_SIGNATURE      "Brava Software Inc."
#define MMTAG_SIGNATURE_LEN                     19
#define MMTAG_SIGNATURE_OFFSET                   0
#define MMTAG_VERSION_LEN                        4
#define MMTAG_VERSION_OFFSET                    32

#define MMTAG_VERSION_BLOCK_SYNC_STRING "18273645"
#define MMTAG_VERSION_BLOCK_SYNC_OFFSET          0
#define MMTAG_VERSION_BLOCK_SYNC_LENGTH          8
#define MMTAG_VERSION_BLOCK_SUBSECTION_LENGTH   10
#define MMTAG_VERSION_BLOCK_XING_VER_OFFSET     10
#define MMTAG_VERSION_BLOCK_MM_VER_OFFSET       20
#define MMTAG_VERSION_BLOCK_STRING_LEN           4

#define MMTAG_IMAGE_EXTENSION_LEN                4
#define MMTAG_IMAGE_SIZE_LEN                     4

#define WAVE_RIFF_STRUCTURE_ID              "RIFF"
#define WAVE_RIFF_IDS_LENGTH                     4
#define WAVE_RIFF_WAVE_ID                   "WAVE"
#define WAVE_RIFF_HEADER_LENGTH                 12
//#define WAVE_RIFF_FMT_ID                    "fmt "
//#define WAVE_RIFF_FACT_ID                   "fact"
#define WAVE_RIFF_DATA_ID                   "data"

#define VBRI_TAG_START_OFFSET                   36
#define VBRI_TAG_ID_STRING                  "VBRI"
#define LAME_TAG_ID_STRING                  "Info"
#define XING_TAG_ID_STRING                  "Xing"
#define VBR_TAG_ID_STRING_LEN                    4


char *genre_list[]={
/* Compliant with https://en.wikipedia.org/wiki/List_of_ID3v1_Genres */
    "Blues"                            /*   0 */
,   "Classic Rock"                     /*   1 */
,   "Country"                          /*   2 */
,   "Dance"                            /*   3 */
,   "Disco"                            /*   4 */
,   "Funk"                             /*   5 */
,   "Grunge"                           /*   6 */
,   "Hip-Hop"                          /*   7 */
,   "Jazz"                             /*   8 */
,   "Metal"                            /*   9 */
,   "New Age"                          /*  10 */
,   "Oldies"                           /*  11 */
,   "Other"                            /*  12 */
,   "Pop"                              /*  13 */
,   "Rhythm and Blues"                 /*  14 */
,   "Rap"                              /*  15 */
,   "Reggae"                           /*  16 */
,   "Rock"                             /*  17 */
,   "Techno"                           /*  18 */
,   "Industrial"                       /*  19 */
,   "Alternative"                      /*  20 */
,   "Ska"                              /*  21 */
,   "Death Metal"                      /*  22 */
,   "Pranks"                           /*  23 */
,   "Soundtrack"                       /*  24 */
,   "Euro-Techno"                      /*  25 */
,   "Ambient"                          /*  26 */
,   "Trip-Hop"                         /*  27 */
,   "Vocal"                            /*  28 */
,   "Jazz & Funk"                      /*  29 */
,   "Fusion"                           /*  30 */
,   "Trance"                           /*  31 */
,   "Classical"                        /*  32 */
,   "Instrumental"                     /*  33 */
,   "Acid"                             /*  34 */
,   "House"                            /*  35 */
,   "Game"                             /*  36 */
,   "Sound Clip"                       /*  37 */
,   "Gospel"                           /*  38 */
,   "Noise"                            /*  39 */
,   "Alternative Rock"                 /*  40 */
,   "Bass"                             /*  41 */
,   "Soul"                             /*  42 */
,   "Punk"                             /*  43 */
,   "Space"                            /*  44 */
,   "Meditative"                       /*  45 */
,   "Instrumental Pop"                 /*  46 */
,   "Instrumental Rock"                /*  47 */
,   "Ethnic"                           /*  48 */
,   "Gothic"                           /*  49 */
,   "Darkwave"                         /*  50 */
,   "Techno-Industrial"                /*  51 */
,   "Electronic"                       /*  52 */
,   "Pop-Folk"                         /*  53 */
,   "Eurodance"                        /*  54 */
,   "Dream"                            /*  55 */
,   "Southern Rock"                    /*  56 */
,   "Comedy"                           /*  57 */
,   "Cult"                             /*  58 */
,   "Gangsta"                          /*  59 */
,   "Top 40"                           /*  60 */
,   "Christian Rap"                    /*  61 */
,   "Pop/Funk"                         /*  62 */
,   "Jungle Music"                     /*  63 */
,   "Native American"                  /*  64 */
,   "Cabaret"                          /*  65 */
,   "New Wave"                         /*  66 */
,   "Psychedelic"                      /*  67 */
,   "Rave"                             /*  68 */
,   "Showtunes"                        /*  69 */
,   "Trailer"                          /*  70 */
,   "Lo-Fi"                            /*  71 */
,   "Tribal"                           /*  72 */
,   "Acid Punk"                        /*  73 */
,   "Acid Jazz"                        /*  74 */
,   "Polka"                            /*  75 */
,   "Retro"                            /*  76 */
,   "Musical"                          /*  77 */
,   "Rock & Roll"                      /*  78 */
,   "Hard Rock"                        /*  79 */

                                       /* Extension by Winamp */
,   "Folk"                             /*  80 */
,   "Folk/Rock"                        /*  81 */
,   "National Folk"                    /*  82 */
,   "Swing"                            /*  83 */
,   "Fast Fusion"                      /*  84 */
,   "Bebop"                            /*  85 */
,   "Latin"                            /*  86 */
,   "Revival"                          /*  87 */
,   "Celtic"                           /*  88 */
,   "Bluegrass"                        /*  89 */
,   "Avantgarde"                       /*  90 */
,   "Gothic Rock"                      /*  91 */
,   "Progressive Rock"                 /*  92 */
,   "Psychedelic Rock"                 /*  93 */
,   "Symphonic Rock"                   /*  94 */
,   "Slow Rock"                        /*  95 */
,   "Big Band"                         /*  96 */
,   "Chorus"                           /*  97 */
,   "Easy Listening"                   /*  98 */
,   "Acoustic"                         /*  99 */
,   "Humour"                           /* 100 */
,   "Speech"                           /* 101 */
,   "Chanson"                          /* 102 */
,   "Opera"                            /* 103 */
,   "Chamber Music"                    /* 104 */
,   "Sonata"                           /* 105 */
,   "Symphony"                         /* 106 */
,   "Booty Bass"                       /* 107 */
,   "Primus"                           /* 108 */
,   "Porn Groove"                      /* 109 */
,   "Satire"                           /* 110 */
,   "Slow Jam"                         /* 111 */
,   "Club"                             /* 112 */
,   "Tango"                            /* 113 */
,   "Samba"                            /* 114 */
,   "Folklore"                         /* 115 */
,   "Ballad"                           /* 116 */
,   "Power Ballad"                     /* 117 */
,   "Rhythmic Soul"                    /* 118 */
,   "Freestyle"                        /* 119 */
,   "Duet"                             /* 120 */
,   "Punk Rock"                        /* 121 */
,   "Drum Solo"                        /* 122 */
,   "A cappella"                       /* 123 */
,   "Euro-House"                       /* 124 */
,   "Dance Hall"                       /* 125 */
,   "Goa Music"                        /* 126 */
,   "Drum & Bass"                      /* 127 */
,   "Club House"                       /* 128 */
,   "Hardcore Techno"                  /* 129 */
,   "Terror"                           /* 130 */
,   "Indie"                            /* 131 */
,   "BritPop"                          /* 132 */
,   "Negerpunk"                        /* 133 */
,   "Polsk Punk"                       /* 134 */
,   "Beat"                             /* 135 */
,   "Christian Gangsta Rap"            /* 136 */
,   "Heavy Metal"                      /* 137 */
,   "Black Metal"                      /* 138 */
,   "Crossover"                        /* 139 */
,   "Contemporary Christian"           /* 140 */
,   "Christian Rock"                   /* 141 */

                                       /* Winamp 1.91 */
,   "Merengue"                         /* 142 */
,   "Salsa"                            /* 143 */
,   "Thrash Metal"                     /* 144 */
,   "Anime"                            /* 145 */
,   "Jpop"                             /* 146 */
,   "Synthpop"                         /* 147 */

                                       /* Winamp 5.6 */
,   "Abstract"                         /* 148 */
,   "Art Rock"                         /* 149 */
,   "Baroque"                          /* 150 */
,   "Bhangra"                          /* 151 */
,   "Big beat"                         /* 152 */
,   "Breakbeat"                        /* 153 */
,   "Chillout"                         /* 154 */
,   "Downtempo"                        /* 155 */
,   "Dub"                              /* 156 */
,   "EBM"                              /* 157 */
,   "Eclectic"                         /* 158 */
,   "Electro"                          /* 159 */
,   "Electroclash"                     /* 160 */
,   "Emo"                              /* 161 */
,   "Experimental"                     /* 162 */
,   "Garage"                           /* 163 */
,   "Global"                           /* 164 */
,   "IDM"                              /* 165 */
,   "Illbient"                         /* 166 */
,   "Industro-Goth"                    /* 167 */
,   "Jam Band"                         /* 168 */
,   "Krautrock"                        /* 169 */
,   "Leftfield"                        /* 170 */
,   "Lounge"                           /* 171 */
,   "Math Rock"                        /* 172 */
,   "New Romantic"                     /* 173 */
,   "Nu-Breakz"                        /* 174 */
,   "Post-Punk"                        /* 175 */
,   "Post-Rock"                        /* 176 */
,   "Psytrance"                        /* 177 */
,   "Shoegaze"                         /* 178 */
,   "Space Rock"                       /* 179 */
,   "Trop Rock"                        /* 180 */
,   "World Music"                      /* 181 */
,   "Neoclassical"                     /* 182 */
,   "Audiobook"                        /* 183 */
,   "Audio Theatre"                    /* 184 */
,   "Neue Deutsche Welle"              /* 185 */
,   "Podcast"                          /* 186 */
,   "Indie-Rock"                       /* 187 */
,   "G-Funk"                           /* 188 */
,   "Dubstep"                          /* 189 */
,   "Garage Rock"                      /* 190 */
,   "Psybient"                         /* 191 */
};

typedef struct apefooter_t {
    char          preamble[8];
    unsigned char version[4];
    unsigned char tag_size[4];
    unsigned char item_count[4];
    unsigned char tag_flags[4];
    char          reserved[8];
} apefooter_t;

typedef struct mmtag_tail_infos_t {
    unsigned char image_extension[4];
    unsigned char image_binary[4];
    unsigned char unused[4];
    unsigned char version_info[4];
    unsigned char audio_metadata[4];
    unsigned char footer[MMTAG_FOOTER_SIZE];
} mmtag_tail_infos_t;

#if defined(__WATCOMC__)
/* Watcom compiler sets default alignment to 1 byte */
typedef struct mmtag_partial_infos_t {
#else
typedef struct __attribute__((packed)) mmtag_partial_infos_t {
#endif
    unsigned char empty[12];
    unsigned char sync[MMTAG_VERSION_BLOCK_SUBSECTION_LENGTH];
    unsigned char xing[MMTAG_VERSION_BLOCK_SUBSECTION_LENGTH];
    unsigned char musm[MMTAG_VERSION_BLOCK_SUBSECTION_LENGTH];
} mmtag_partial_infos_t;

typedef struct wave_riff_chunk_t {
    char          ckid[WAVE_RIFF_IDS_LENGTH];
    unsigned char cksize[4];
} wave_riff_chunk_t;

unsigned char sideinfo_tab[2][2] = {{32,17},{17,9}};  /* MPEG1 {2ch,1ch}, MPEG2/2.5 {2ch,1ch} */


/*
 * memsrch
 *
 * this function searches for a character string 'needle' into a raw byte sequence
 * called 'haystack' which is 'haystack_len' bytes long
 *
 * return value: when found, the pointer to the first character of 'needle',
 * else NULL
 * Note: memsrch was developed because of unreliability of 'strstr' search algorithm
 * into a raw byte sequence. Cannot use 'memmem' (which is very similar) because it
 * is a GNU libc extension
 */
unsigned char *memsrch(unsigned char *haystack, int haystack_len, char *needle)
{
    unsigned char *p, *q;
    int i, len;

    p = NULL;

    if (haystack != NULL && needle != NULL)
    {
        q = haystack;
        i = 0;
        len = strlen(needle);

        if (len <= haystack_len)
        {
            haystack_len -= len - 1;

            while (i < haystack_len)
            {
                q=memchr(&haystack[i], needle[0], haystack_len-i);
                if (q != NULL)
                {
                    if (!memcmp(q, needle, len))
                    {
                        p = q;
                        break;
                    }
                    else
                    {
                        i = (int)(q - haystack) + 1;
                    }
                }
                else
                {
                    break;
                }
            }
        }
    }

    return p;
}


/*
 * get_little_endian_uint
 *
 * this function reads a byte buffer with a little endian integer stored
 * and returns its unsigned integer value (32 bit)
 * This is often the case with integer/flag fields into metadata tags.
 *
 * I think I will need this when running mp3guessenc
 * on a big-endian architecture
 */
unsigned int get_little_endian_uint(unsigned char *pbuff)
{
    return  (unsigned int)pbuff[0]     +
           ((unsigned int)pbuff[1]<< 8)+
           ((unsigned int)pbuff[2]<<16)+
           ((unsigned int)pbuff[3]<<24);
}


/*
 * here the search for lame extended version string is performed
 * lame stores this string into ancillary data since version 3.80b (may 6th 2000 - the previous
 * lame version 3.70 apr 6 2000 didn't have it)
 * Return values:
 * - an integer value, the length of the extracted string (if any)
 * - lame_string contains the encoder extended version string (it is untouched when not found)
 * Note: this routine may be called several times since ancillary data may be found in
 * several frames, expecially in CBR files
 * it overwrites `lame_string' several times in order to fullfill it with data from the longest
 * of the strings it finds.
 */
int extract_enc_string(char *lame_string, unsigned char *p, int len)
{
//#define SHOW_LAME_STRINGS  /* useful when debugging */
    int idx,len_before,resultLen=0;
    unsigned char *q;

#ifdef SHOW_LAME_STRINGS
    printf("begin - lame_string=`%s', len=%d\n",lame_string,len);
    for (idx=0; idx<len; idx++)
        if (isprint(p[idx])) putchar(p[idx]); else putchar('.');
    printf("\n");
#endif

    q = memsrch(p,len,"LAME");
    if (q == NULL)
    {
        q = memsrch(p,len,"GOGO");
        if (q != NULL)
        {
            strcpy(lame_string,"GOGO");
            resultLen = 4;
        }
        else
        {
#ifdef SHOW_LAME_STRINGS
            printf("No_lame_string.\n");
#endif
            resultLen = 0;
        }
    }
    else
    {
        /* `LAME' string found! */
        len -= (q-p);

        if (lame_string[0] == '\0')
            strcpy(lame_string,"LAME"); /* this is likely to be the first call to extract_lame_string */
        else
        {
            if (memcmp(lame_string, "LAME", 4))
            {
                /* lame_string is only modified here, it MUST be "" (null string) the first time,
                   or it MUST start with "LAME". In any other case I want to be warned */
                printf("WARNING: buffer overwrite on lame_string (\"%s\")! RESETTING!\n",lame_string);
                memset(lame_string, 0, LAME_STRING_LENGTH);
                strcpy(lame_string,"LAME");
            }
        }

        idx=4;   /* I'm sure q points to `LAME' so the loop can be shortened */
        len_before=strlen(lame_string);

        while (idx<len
               &&
               idx<LAME_STRING_LENGTH-1
               &&
               isprint((int)q[idx])
               &&
               q[idx]!='L'
               &&
               q[idx]!=0x55
               &&
               (unsigned char)q[idx]!=0xAA)
        /* in recent versions, lame strings may end either with a 0x55 or a 0xAA byte
           older releases used to put random non-printable bytes.
           Also, I check for a capital `L' in order to skip string repetitions such as `LAME3LAME3' */
        {
            lame_string[idx]=q[idx];
            idx++;
        }

        if (idx>=len_before)
            lame_string[idx]=0;
        resultLen = idx;
    }
#ifdef SHOW_LAME_STRINGS
    printf("exiting - lame_string=%s\n",lame_string);
#endif
    return resultLen;
}

///////////////////////////////////////////////////////
// VBR TAG related routines

void reset_tag_data(vbrtagdata_t *p)
{
    memset((void *)p, 0, sizeof(vbrtagdata_t));
    p->tagId=NULL;
    p->infoTag=TAG_NOTAG;
    p->vbr_scale=-1;    /* GOGO encoder does not put vbr quality value into its vbr tag, so I need here a flag */
    p->lameMusicCRC=-1; /* no music crc found */
}

unsigned int parse_vbri_tag(vbrtagdata_t *p, unsigned char *buf)
{
    unsigned int offset=4*8;

    p->tagId=VBRI_TAG_ID_STRING;
    p->infoTag=TAG_VBRITAG;
    p->version=extract_bits(buf, &offset, 16);

    p->encDelay=extract_bits(buf, &offset, 16);

    p->vbr_scale=extract_bits(buf, &offset, 16);

    p->bytes=extract_bits(buf, &offset, 32);

    p->reported_frames=extract_bits(buf, &offset, 32);

    p->tocEntries=extract_bits(buf, &offset, 16);

    offset+=2*8;
    p->sizePerTocEntry=extract_bits(buf, &offset, 16);

    p->framesPerTocEntry=extract_bits(buf, &offset, 16);

    p->tocSize=(int)p->sizePerTocEntry*(int)p->tocEntries;

    /* switch to byte */
    offset = offset/8 + p->tocSize;

    return offset;
}

unsigned int parse_xing_tag(vbrtagdata_t *p, unsigned char *buffer_start, unsigned int tag_start, char *lame_string)
{
#define XING_FLAG_FRAMES     0x0001
#define XING_FLAG_BYTES      0x0002
#define XING_FLAG_TOC        0x0004
#define XING_FLAG_VBR_SCALE  0x0008

    unsigned char *buf=buffer_start+tag_start;
    unsigned int offset=sizeof(unsigned int)*8, flags;
    unsigned short sum;

    p->tagId = XING_TAG_ID_STRING;
    if (buf[0]=='I')
        p->infoTag = TAG_LAMECBRTAG;
    else
        p->infoTag = TAG_XINGTAG;

    flags = extract_bits(buf, &offset, 32);

    if (flags & XING_FLAG_FRAMES)
    {
        p->reported_frames = extract_bits(buf, &offset, 32);
    }

    if (flags & XING_FLAG_BYTES)
    {
        p->bytes = extract_bits(buf, &offset, 32);
    }

    /* switch to byte */
    offset /= 8;

    if (flags & XING_FLAG_TOC)
    {
        /* I know the toc entries amount */
        p->tocEntries = 100;
        p->sizePerTocEntry = 1;
        p->tocSize = 100;
        offset+=100;
    }

    if (buf[offset] == 'G' && buf[offset+1] == 'O' && buf[offset+2] == 'G' && buf[offset+3] == 'O')
    {
        /* gogo seems not to put a vbr quality value into its tag */
        offset += extract_enc_string(lame_string,buf+offset,4);
    }
    else
    {
        if (flags & XING_FLAG_VBR_SCALE)
        {
            p->vbr_scale = extract_bits(buf+offset, NULL, 32);
            offset+=4;
        }
    }

/*
 * Starting with lame-3.99 alpha releases the devs changed the lame tag signature so it didn't
 * start with the string `LAME', instead it just contained the capital `L' at the beginning and the
 * following expected characters in the form `Lx.xxyzz', where x is a digit and z is an optional digit.
 * Anyway the old signature `LAMEx.xxy' (y may be one of a/b/r) was restored with lame 3.99.2 release
 * in order to keep lame tag readable by old sw/hw players.
 */
    if (
        (buf[offset] == 'L' && buf[offset+1] == 'A' && buf[offset+2] == 'M' && buf[offset+3] == 'E')
        ||
        (buf[offset] == 'L' && isdigit((int)buf[offset+1]) && buf[offset+2] == '.' && isdigit((int)buf[offset+3]) && isdigit((int)buf[offset+4]))
        ||
        (tolower(buf[offset])=='l' && tolower(buf[offset+1])=='a' && tolower(buf[offset+2])=='v' && tolower(buf[offset+3])=='c')
       )
    {
        /*
         * maybe we found a lame info tag
         * warn: a version check has to be done because lame 3.90 was the first
         * version able to write a real info tag.
         * previous versions wrote just a string
         * Updated `if' statement for better handling of upcoming 3.100 release
         */
        if (buf[offset+1]=='A' && buf[offset+4]=='3' && buf[offset+5]=='.' && isdigit((int)buf[offset+6]) && buf[offset+6]<'9' && isdigit((int)buf[offset+7]) && !isdigit((int)buf[offset+8]))
            /* old versions had more room for detailed strings due to lack of info tag */
            offset += extract_enc_string(lame_string,buf+offset,LAME_STRING_LENGTH);
        else
        {
            memcpy(p->lametag, buf+offset, LAMETAGSIZE);
            extract_enc_string(lame_string,buf+offset,9);
            offset += LAMETAGSIZE;  /* 'offset' is now at the very end of lame tag! */
            sum = crc_reflected_update(0, buffer_start, tag_start+offset-2);
            p->lametagVerified = (sum == (unsigned short)((reflect_byte(buffer_start[tag_start+offset-1])<<8)
                                                         | reflect_byte(buffer_start[tag_start+offset-2]) ) );

            if (p->lametagVerified)
                p->lameMusicCRC = ((reflect_byte(buffer_start[tag_start+offset-3])<<8)
                                  | reflect_byte(buffer_start[tag_start+offset-4]) );
        }
    }

    return offset;
}

char checkvbrinfotag(vbrtagdata_t *pTagData, unsigned char *buf, off_t pos, char *enc_string)
{
/*
 * Here we detect the info vbr/cbr tag (if any) -- the search is performed inside the buffer
 * Return values:
 *  0: no tag
 *  1: tag found (details into pTagData)
 */
    unsigned int temp;
    unsigned char mono, lsf;

    reset_tag_data(pTagData);

    pTagData->header=extract_bits(buf, NULL, 32); /* store the header */
    lsf  = 1-((pTagData->header&HEADER_FIELD_LSF)>>HEADER_FIELD_LSF_SHIFT);
    mono =((pTagData->header&HEADER_FIELD_CHANNELS)>>HEADER_FIELD_CHANNELS_SHIFT)/3;

    if (memcmp(buf+VBRI_TAG_START_OFFSET, VBRI_TAG_ID_STRING, VBR_TAG_ID_STRING_LEN) == 0)
    {
        parse_vbri_tag(pTagData,buf+VBRI_TAG_START_OFFSET);
    }
    else
    {
        temp = sizeof(unsigned int)+(int)sideinfo_tab[lsf][mono]; /* placement of xing vbr tag doesn't take into account optional crc16 */

        if (
            memcmp(&buf[temp], XING_TAG_ID_STRING, VBR_TAG_ID_STRING_LEN) == 0
            ||
            memcmp(&buf[temp], LAME_TAG_ID_STRING, VBR_TAG_ID_STRING_LEN) == 0
           )
        {
            parse_xing_tag(pTagData, buf, temp, enc_string);

            if ((pTagData->lame_buggy_vbrheader=(((pTagData->header&HEADER_FIELD_BITRATE)>>HEADER_FIELD_BITRATE_SHIFT)==BITRATE_INDEX_RESERVED)))
                /* Despite the bitrate field holds a buggy value, I know the header is valid
                   So, save this (in `lame_buggy_vbrheader' field) and set the right freeformat
                   bitrate index - I will need it later */
                pTagData->header &= ~HEADER_FIELD_BITRATE;
        }
    }

    if (pTagData->infoTag!=TAG_NOTAG) /* was any tag found? If so, I am sure I'm dealing with a layerIII stream */
        pTagData->tagStartsAt=pos;  /* offset of the mpeg frame containing this tag */

    return (pTagData->infoTag!=TAG_NOTAG);
}

void show_info_tag (vbrtagdata_t *p)
/*
 * Here we show the technical details found into the VBRI/XING tag
 * If a lame tag is found, then it's showed as well.
 * Any lame tag detail is based upon description at
 * http://gabriel.mp3-tech.org/mp3infotag.html
 * Offset are different than those reported by Gabriel
 * because here I saved the tag only into the lametag byte array,
 * so here the 'Encoder short VersionString' starts at zero.
 */
{
#define LAMETAG_TAGREVISION_VBRDETAIL            9
#define LAMETAG_LOWPASSFREQ                     10
#define LAMETAG_ENCODINGFLAGS                   19
#define LAMETAG_MINIMALBITRATE                  20
#define LAMETAG_ENCODERDELAYS                   21
#define LAMETAG_MISCINFOS                       24
#define LAMETAG_PRESETINFO                      26
#define LAMETAG_MUSICLENGTH                     28

    char lameTag=(p->lametag[0]!=0);

    printf("%s tag detected into the first frame (%d bytes long).\n",p->tagId,p->frameSize);
    printf("  Tag offset       : %lld (0x%08X)\n",(long long)p->tagStartsAt,(unsigned)p->tagStartsAt);
    if (p->infoTag==TAG_VBRITAG)
    {
        printf("  Tag version      : %d\n",p->version);
        printf("  Encoder delay    : %d samples\n",p->encDelay);
    }
    printf("  File size        : %u bytes\n",p->bytes);
    printf("  Number of frames : %u\n",p->reported_frames);
    if (p->vbr_scale!=-1) /* GOGO doesn't add a vbr quality value */
    {
        printf("  Quality          : %u",p->vbr_scale);
        if ((lameTag)&&(p->infoTag!=TAG_LAMECBRTAG))
        {   /* sure the file is vbr encoded by lame, I just need to know whether it's abr or vbr */
            unsigned char lamemode=p->lametag[LAMETAG_TAGREVISION_VBRDETAIL]&0x0f;
            if ((lamemode>2)&&(lamemode<7))
                /* it's vbr, so let's print encoder options */
                printf(" (-q %d -V %d)",(100-p->vbr_scale)%10,(100-p->vbr_scale)/10);
        }
        printf("\n");
    }
    printf("  TOC              : ");
    if (p->tocSize)
        printf("%d bytes (%d entries, %d byte%s each)\n",
               p->tocSize,p->tocEntries,p->sizePerTocEntry,(p->sizePerTocEntry==1)?"":"s");
    else
        printf("no\n");
    if (p->infoTag!=TAG_VBRITAG)
    {
        printf("  Lame tag         : ");
        if (lameTag)
        {
            unsigned char c,i;
            unsigned short j;
            unsigned int delay;
            printf("yes");
            if (p->lame_buggy_vbrheader)
                printf(" (buggy bitrate field)");
            /* print details from lame tag */
            printf("\nLame tag details...\n  Lame short string     : ");
            for(i=0;i<9;i++)
                if (isprint((int)p->lametag[i])) putchar(p->lametag[i]); else putchar(' ');
            c=p->lametag[LAMETAG_TAGREVISION_VBRDETAIL]>>4;
            printf("\n  Tag revision          : ");
            if (c!=15)
                printf("%d",c);
            else
                printf("invalid!");
            c=p->lametag[LAMETAG_TAGREVISION_VBRDETAIL]&0x0f;
            printf("\n  Bitrate strategy      : ");
            if ((c==2)||(c==9))
                printf("ABR, ");  /* it's abr */
            else
            {
                if ((c==1)||(c==8))
                    printf("CBR, ");  /* it's cbr */
                else
                    if ((c>2)&&(c<7)) /* it's vbr */
                        printf("VBR method %s, min ",(c==3)?"old/rh":(c==4)?"mtrh":/*if lame, c is 5*/"mt");
            }
            c=p->lametag[LAMETAG_MINIMALBITRATE];
            if (c)
            {
                printf("%d kbps",c);
                if (c==255) printf (" or higher");
            }
            else
                printf("unknown");
            c=p->lametag[LAMETAG_LOWPASSFREQ];
            printf("\n  Lowpass value         : ");
            if (c)
                printf("%d",c*100);
            else
                printf("unknown");
            c=p->lametag[LAMETAG_ENCODINGFLAGS];
            printf("\n  nspsytune             : %s\n",(c&0x10)?"yes":"no");
            printf("  nssafejoint           : %s\n",(c&0x20)?"yes":"no");
            printf("  nogap continued       : %s\n",(c&0x40)?"yes":"no");
            printf("  nogap continuation    : %s\n",(c&0x80)?"yes":"no");
            printf("  ATH type              : %d\n",c&0xf);
            delay=extract_bits((unsigned char *)p->lametag+LAMETAG_ENCODERDELAYS, NULL, 24);
            p->encDelay=(short)((delay>>12)&0xfff);
            p->encPadding=(short)(delay&0xfff);
            printf("  Encoder delay (start) : %d samples\n",p->encDelay);
            printf("  Encoder padding (end) : %d samples\n",p->encPadding);
            printf("  [ Length of original audio : %u samples ]\n",
                   /* length = frames * samples_per_granule * granules_per_frame - delay - padding */
                   (p->reported_frames * 576 * ((p->header&HEADER_FIELD_LSF)?2:1)) - p->encDelay - p->encPadding);
            c=p->lametag[LAMETAG_MISCINFOS];
            /* this single byte holds four noticeable fields - here three are shown
               fields are 2 + 1 + 3 + 2 bit long (msb to lsb) */
            printf("  Encoding mode         : ");
            switch (c&0x1c)
            {
                case 24:
                    printf("intensity stereo");
                    break;
                case 20:
                    printf("auto");
                    break;
                case 16:
                    printf("forced MS stereo");
                    break;
                case 12:
                    printf("joint stereo");
                    break;
                case 8:
                    printf("dual channel");
                    break;
                case 4:
                    printf("simple LR stereo");
                    break;
                case 0:
                    printf("mono");
                    break;
                default:
                    printf("other");
            }
            printf("\n  Unwise settings       : %sused\n",(c&32)?"":"not ");
            printf("  Source frequency      : ");
            switch (c&0xc0)
            {
                case 128:
                    printf("48 kHz");
                    break;
                case 64:
                    printf("44.1 kHz");
                    break;
                case 0:
                    printf("32 kHz or below");
                    break;
                default:
                    printf("higher than 48 kHz");
            }
            j=extract_bits((unsigned char *)p->lametag+LAMETAG_PRESETINFO, NULL, 16)&0x7ff;  /* 11 least significant bits are used for preset */
            printf("\n  Preset                : ");
            if (j==0)
                printf("No preset.");
            else
            {
                if (j<321)
                    printf("%d kbps",j);
                else
                {
                    /* using values from lame internal presets */
                    if (j>409 && j<501)
                        printf("V%d / VBR_%d",50-j/10,j-400);
                    else
                    {
                        if (j>999 && j<1008)
                        {
                            switch (j)
                            {
                                case 1000:
                                    printf("R3mix.");
                                    break;
                                case 1001:
                                    printf("Standard.");
                                    break;
                                case 1002:
                                    printf("Extreme.");
                                    break;
                                case 1003:
                                    printf("Insane.");
                                    break;
                                case 1004:
                                    printf("Standard fast.");
                                    break;
                                case 1005:
                                    printf("Extreme fast.");
                                    break;
                                case 1006:
                                    printf("Medium.");
                                    break;
                                case 1007:
                                    printf("Medium fast.");
                                    break;
                            }
                        }
                        else
                            printf("Unknown preset.");
                    }
                }
            }
            printf("\n  Originally encoded    : %u bytes\n",extract_bits((unsigned char *)p->lametag+LAMETAG_MUSICLENGTH, NULL, 32));
            printf("  Tag verification      : %s\n", (p->lametagVerified?"passed":"failed"));
        }
        else
            printf("no\n");
    }
    printf("\n");
}

///////////////////////////////////////////////////////

void show_id3v1(id3tag_t *id3)
{
    int n_genres;

    n_genres = sizeof(genre_list) / sizeof(char *);

    printf("  Title   : %s\n", id3->title);
    printf("  Artist  : %s\n", id3->artist);
    printf("  Album   : %s\n", id3->album);
    printf("  Year    : %s\n", id3->year);
    printf("  Comment : %s\n", id3->comment);
    if (id3->tag_name[3] != '0')
    {
        /* not version 1.0, we have a meaningful track number field */
        printf("  Track # : %u\n", id3->track_n);
    }

    printf("  Genre   : ");
    if (id3->tag_name[0] == 'E')
    {
        /* Enhanced tag */
        printf("%s (ID3v1: ", &id3->genre[1]);
        if (id3->genre[0] < n_genres)
            printf("%s)", genre_list[id3->genre[0]]);
        else
            printf("unknown)");
    }
    else
    {
        /* tag v1.0/v1.1/v1.2 */
        if (id3->genre[0] < n_genres)
            printf("%s", genre_list[id3->genre[0]]);
        else
            printf("unknown");

        if (id3->tag_name[3] =='2')
        {
            /* ID3v1.2 */
            printf(", Subgenre : %s", &id3->genre[1]);
        }
    }
    printf("\n");

    if (id3->tag_name[0] == 'E')
    {
        printf("  Speed   : ");
        switch (id3->speed)
        {
            case 1: printf("slow");
                    break;
            case 2: printf("medium");
                    break;
            case 3: printf("fast");
                    break;
            case 4: printf("hardcore");
                    break;
            default: printf("invalid");
                    break;
        }
        printf("\n  S-time  : %s\n", id3->start_time);
        printf("  E-time  : %s\n", id3->end_time);
    }

    printf("\n");
}

void metadata_cpy(id3tag_t *id3,  unsigned char *buffer)
{
    strcpy((char *)id3->tag_name, "v1.2");

    strncat((char *)id3->title,  (char *)&buffer[ 3], 30);
    id3->title[90] = 0; /* this terminating byte is already 0 - this just prevents complaints from gcc */
    strncat((char *)id3->artist, (char *)&buffer[33], 30);
    id3-> artist[90] = 0; /* this terminating byte is already 0 - this just prevents complaints from gcc */
    strncat((char *)id3->album,  (char *)&buffer[63], 30);
    id3->album[90] = 0;  /* this terminating byte is already 0 - this just prevents complaints from gcc */
    strncat((char *)id3->comment,(char *)&buffer[93], 15);
    id3->comment[43] = 0;  /* this terminating byte is already 0 - this just prevents complaints from gcc */
    memcpy(&id3->genre[1], &buffer[108], 20);
}

int checkid3v1(FILE *fi, off_t pos, id3tag_t *id3)
/*
 * Here the info tag id3v1.x is detected
 * This function returns the size of the tag
 * or zero if no tag is found.
 * Note: the file pointer of fi is moved, and then restored
 */
{
    off_t filepos;
    int id3size=0;
    unsigned char buffer[TAG_ID3TAG_ENH_LENGTH+TAG_ID3TAG_11_12_LENGTH];  /* will use for any tags */

    if (pos-(signed)TAG_ID3TAG_11_12_LENGTH >= 0)
    {
        filepos=ftello(fi);
        fseeko(fi,pos-TAG_ID3TAG_11_12_LENGTH,SEEK_SET);

        if (fread(buffer,TAG_ID3TAG_11_12_LENGTH,1,fi)==1)
        {
            if (
                memcmp(&buffer[0], "TAG", 3) == 0
                ||
                memcmp(&buffer[0], "tag", 3) == 0
               )
            {
                /* tag v1.0/v1.1 found */
                id3size = TAG_ID3TAG_11_12_LENGTH;
                memcpy(id3->title,   &buffer[ 3], 30);
                memcpy(id3->artist,  &buffer[33], 30);
                memcpy(id3->album,   &buffer[63], 30);
                memcpy(id3->year,    &buffer[93],  4);
                if (buffer[125]==0 && buffer[126]!=0)
                {
                    /* this is id3v1.1 */
                    strcpy((char *)id3->tag_name, "v1.1");
                    memcpy(&id3->comment, &buffer[97], 28);
                    id3->track_n = buffer[126];
                }
                else
                {
                    /* this is id3v1.0 */
                    strcpy((char *)id3->tag_name, "v1.0");
                    memcpy(&id3->comment, &buffer[97], 30);
                }
                id3->genre[0] = buffer[127];       /* genre is an array, the remaining will be used in case of modern id3v1 tags */

                /* now check for modern ID3v1 extensions */
                /* ID3 Enhanced? */
                if (pos-(signed)TAG_ID3TAG_11_12_LENGTH-(signed)TAG_ID3TAG_ENH_LENGTH >= 0)
                {
                    fseeko(fi,pos-TAG_ID3TAG_11_12_LENGTH-TAG_ID3TAG_ENH_LENGTH,SEEK_SET);
                    if (fread(buffer,TAG_ID3TAG_ENH_LENGTH+TAG_ID3TAG_11_12_LENGTH,1,fi)==1)
                    {
                        if (
                            memcmp(&buffer[0], "TAG+", 4) == 0
                            ||
                            memcmp(&buffer[0], "tag+", 4) == 0
                           )
                        {
                            /* ID3 Enhanced found! */
                            strcpy((char *)id3->tag_name, "Enhanced");
                            id3size += TAG_ID3TAG_ENH_LENGTH;
                            strncat((char *)id3->title,     (char *)&buffer[  4], 60);
                            id3->title[90] = 0; /* this terminating byte is already 0 - this just prevents complaints from gcc */
                            strncat((char *)id3->artist,    (char *)&buffer[ 64], 60);
                            id3-> artist[90] = 0; /* this terminating byte is already 0 - this just prevents complaints from gcc */
                            strncat((char *)id3->album,     (char *)&buffer[124], 60);
                            id3->album[90] = 0;  /* this terminating byte is already 0 - this just prevents complaints from gcc */
                            id3->speed = buffer[184];
                            strncpy((char *)&id3->genre[1], (char *)&buffer[185], 30);
                            id3->genre[31] = 0;  /* this terminating byte is already 0 - this just prevents complaints from gcc */
                            memcpy(id3->start_time, &buffer[215], 6);
                            memcpy(id3->end_time, &buffer[221], 6);
                        }
                        else
                        {
                            /* maybe there's an ID3v1.2 tag? */
                            if (
                                memcmp(&buffer[TAG_ID3TAG_ENH_LENGTH-TAG_ID3TAG_11_12_LENGTH], "EXT", 3) == 0
                                ||
                                memcmp(&buffer[TAG_ID3TAG_ENH_LENGTH-TAG_ID3TAG_11_12_LENGTH], "ext", 3) == 0
                               )
                            {
                                metadata_cpy(id3, &buffer[TAG_ID3TAG_ENH_LENGTH-TAG_ID3TAG_11_12_LENGTH]);
                                id3size += TAG_ID3TAG_11_12_LENGTH;
                            }
                        }

                    }
                }
                else
                {
                    /* ID3v1.2 ?*/
                    if (pos-(signed)TAG_ID3TAG_11_12_LENGTH-(signed)TAG_ID3TAG_11_12_LENGTH >= 0)
                    {
                        fseeko(fi,pos-TAG_ID3TAG_11_12_LENGTH-TAG_ID3TAG_11_12_LENGTH,SEEK_SET);
                        if (fread(buffer,TAG_ID3TAG_11_12_LENGTH,1,fi)==1)
                        {
                            if (
                                memcmp(buffer, "EXT", 3) == 0
                                ||
                                memcmp(buffer, "ext", 3) == 0
                               )
                            {
                                metadata_cpy(id3, buffer);
                                id3size += TAG_ID3TAG_11_12_LENGTH;
                            }
                        }
                    }
                }
            }
        }
        fseeko(fi,filepos,SEEK_SET);
    }
    return id3size;
}

/*
 * Since revision 4, ID3v2 may be found at the file tail also.
 * Here we'll check for its presence seeking its footer,
 * which is mandatory when the tag is placed at the bitstream end.
 *
 * If the tag is not found, return value is 0.
 * If errors are detected, return value is -1.
 * Note: this routine DOES read data from the input file
 * but it DOES restore the file pointer before exiting.
 */
int checkid3v2_footer(FILE *fi, off_t pos, unsigned char *idMaj, unsigned char *idmin)
{
    unsigned char footer[ID3V2_FOOTER_LENGTH];
    int id3v2size=0;
    off_t filepos=ftello(fi);

    if ((pos-ID3V2_FOOTER_LENGTH)>=0)
    {
        fseeko(fi,pos-ID3V2_FOOTER_LENGTH,SEEK_SET);

        if (fread(footer,ID3V2_FOOTER_LENGTH,1,fi)==1)
        {
            if (footer[0] == '3' && footer[1] == 'D' && footer[2] == 'I')
            {
                /* footer found! */
                *idMaj=footer[3];
                *idmin=footer[4];

                if (!(footer[6]&0x80) && !(footer[7]&0x80) && !(footer[8]&0x80) && !(footer[9]&0x80) && !(footer[5]&0x0f))
                {
                    id3v2size = footer[6]*2097152+
                                footer[7]*16384+
                                footer[8]*128+
                                footer[9] +ID3V2_HEADER_LENGTH +ID3V2_FOOTER_LENGTH;
                }
                else
                    id3v2size=-1;
            }
        }

        fseeko(fi,filepos,SEEK_SET);
    }
    return id3v2size;
}


unsigned int checkapetagx(apefooter_t *af)
{
    unsigned int ape_len = 0;

    if (!memcmp(af->preamble,APETAGEX_SIGNATURE,8))
    {
        /* ape tag found! */
        ape_len=get_little_endian_uint(af->tag_size);
        if ((get_little_endian_uint(af->tag_flags)&APETAGEX_FLAGS_HEADER_PRESENT)!=0)
        {
            /* tag contains a header */
            ape_len+=sizeof(apefooter_t);
        }
    }

    return ape_len;

}

/*
 * check for ape tag at the end of the file
 * when placed at the end of the file (between the very last frame and the id3v1 tag)
 * ape tag version can be both 1 and 2. Tag version is returned into `ape_vers', if found.
 * search is started at file position `pos' where the end of mpeg stream is expected to be.
 * return value is tag length if found, else zero.
 * file pointer doesn't get modified
 */
int checkapetagx_tail(FILE *fi, off_t pos, int *ape_vers, int *ape_items, char *header_present)
{
    int ape_len=0;
    apefooter_t apefooter;
    off_t filepos;

    if ((pos-(signed)sizeof(apefooter_t))>=0)
    {
        filepos=ftello(fi);
        fseeko(fi, pos-sizeof(apefooter_t), SEEK_SET);
        if (fread((void *)&apefooter,sizeof(apefooter_t),1,fi)==1)
        {
            if ((ape_len=checkapetagx(&apefooter)) != 0)
            {
                *ape_vers=get_little_endian_uint(apefooter.version);
                *ape_items=get_little_endian_uint(apefooter.item_count);
                *header_present=((get_little_endian_uint(apefooter.tag_flags)&APETAGEX_FLAGS_HEADER_PRESENT)!=0);
            }
        }
        fseeko(fi, filepos, SEEK_SET);
    }

    return ape_len;
}

/*
 * check for a LYRICS3 tag ending at file position 'pos'
 * the tag ends with the label 'LYRICSEND'
 */
int checklyrics3v1(FILE *fi, off_t pos)
{
    int lyr3_len=0;
    unsigned char lyrics3v1_tag[LYRICS3V1_TAG_MAXSIZE];
    off_t filepos=ftello(fi);
    unsigned char sign[LYRICS3_END_SIGNATURE_LEN],*p;

    fseeko(fi, pos-LYRICS3_END_SIGNATURE_LEN, SEEK_SET);
    if (fread(sign,LYRICS3_END_SIGNATURE_LEN,1,fi)==1)
    {
        if (!memcmp(sign,LYRICS3V1_END_SIGNATURE,LYRICS3_END_SIGNATURE_LEN))
        {
            /* label found */
            fseeko(fi,pos-LYRICS3V1_TAG_MAXSIZE-LYRICS3_END_SIGNATURE_LEN,SEEK_SET);
            if (fread(lyrics3v1_tag,1,LYRICS3V1_TAG_MAXSIZE,fi)==LYRICS3V1_TAG_MAXSIZE)
            {
                /* the tag has a limited max size, and this comes very handy
                   since there's no information about tag size anywhere */
                if ((p=memsrch(lyrics3v1_tag,LYRICS3V1_TAG_MAXSIZE,LYRICS3_BEGIN_SIGNATURE))!=NULL)
                {
                    /* begin label was found too - we can calculate the tag size */
                    lyr3_len = LYRICS3V1_TAG_MAXSIZE-(int)(p-lyrics3v1_tag)+LYRICS3_END_SIGNATURE_LEN;
                }
            }
        }
    }

    fseeko(fi, filepos, SEEK_SET);
    return lyr3_len;
}

/*
 * check for a Lyrics3 v2.00 tag ending at file position 'pos'
 * this new tag has a more complete footer including its size
 */
int checklyrics3v2(FILE *fi, off_t pos)
{
    int lyr3_len=0;
    off_t filepos=ftello(fi);
    unsigned char sign[LYRICS3V2_TAGSIZE_LEN+LYRICS3_END_SIGNATURE_LEN],*p;

    fseeko(fi, pos-LYRICS3_END_SIGNATURE_LEN-LYRICS3V2_TAGSIZE_LEN, SEEK_SET);
    if (fread(sign,LYRICS3_END_SIGNATURE_LEN+LYRICS3V2_TAGSIZE_LEN,1,fi)==1)
    {
        if (!memcmp(&sign[LYRICS3V2_TAGSIZE_LEN],LYRICS3V2_END_SIGNATURE,LYRICS3_END_SIGNATURE_LEN))
        {
            /* we've found the end signature - the 6 bytes at the very beginning
               are the tag size in string format */
            lyr3_len = strtol((char *)sign, NULL, 10) + LYRICS3_END_SIGNATURE_LEN + LYRICS3V2_TAGSIZE_LEN;
            /* seek back where the begin signature is expected to be */
            fseeko(fi, -lyr3_len, SEEK_CUR);
            if (fread(sign,LYRICS3_BEGIN_SIGNATURE_LEN,1,fi)==1)
            {
                if ((p=memsrch(sign,LYRICS3V2_TAGSIZE_LEN+LYRICS3_END_SIGNATURE_LEN,LYRICS3_BEGIN_SIGNATURE))!=sign)
                {
                    /* mismatch! - probably the tag is corrupted */
                    lyr3_len = 0;
                }
            }
            else
            {
                lyr3_len = 0;
            }
        }
    }

    fseeko(fi, filepos, SEEK_SET);
    return lyr3_len;
}

/*
 * check for the MusicMatch tag
 * this search is heavily based upon unofficial documentation at
 * https://github.com/dreamindustries/id3lib/blob/master/doc/musicmatch.txt
 */
void checkmmtag(FILE *fi, off_t pos, mmtag_t *mmtag)
{
    mmtag_tail_infos_t mm_tail_infos;
    unsigned char search[MMTAG_SAFE_SEARCH_SIZE], *p;
    off_t filepos=ftello(fi);
    unsigned int section_sizes[MMTAG_OFFSET_ENTRIES];
    int j;

    fseeko(fi, pos-MMTAG_FOOTER_SIZE-MMTAG_DATA_OFFSETS_SIZE, SEEK_SET);
    if (fread((unsigned char *)&mm_tail_infos,sizeof(mmtag_tail_infos_t),1,fi)==1)
    {
        /* in a single read we got both the 'data offsets' section and the 'footer' */
        if (!memcmp(&mm_tail_infos.footer[MMTAG_SIGNATURE_OFFSET], MMTAG_SIGNATURE, MMTAG_SIGNATURE_LEN))
        {
            /* we found the signature into the footer - store the version string */
            memcpy(mmtag->tag_ver, &mm_tail_infos.footer[MMTAG_VERSION_OFFSET], MMTAG_VERSION_LEN);

            fseeko(fi, pos-MMTAG_FOOTER_SIZE-MMTAG_DATA_OFFSETS_SIZE-MMTAG_META_DATA_MINIMUM_SIZE-MMTAG_SAFE_SEARCH_SIZE, SEEK_SET);
            if (fread(search,MMTAG_SAFE_SEARCH_SIZE,1,fi)==1)
            {
                /* we've read a whole bunch of bytes including the version block for sure BUT NOT the optional header */
                if ((p=memsrch(search, MMTAG_SAFE_SEARCH_SIZE, MMTAG_VERSION_BLOCK_SYNC_STRING))!=NULL)
                {
                    /* version block found! - store both the MusicMatch and the Xing encoder version strings */
                    memcpy(mmtag->mm_ver,  &p[MMTAG_VERSION_BLOCK_MM_VER_OFFSET],   MMTAG_VERSION_BLOCK_STRING_LEN);
                    memcpy(mmtag->enc_ver, &p[MMTAG_VERSION_BLOCK_XING_VER_OFFSET], MMTAG_VERSION_BLOCK_STRING_LEN);

                    /* calculate the sizes of the different blocks */
                    section_sizes[MMTAG_HEADER]          = MMTAG_HEADER_SIZE;
                    section_sizes[MMTAG_IMAGE_EXTENSION] = get_little_endian_uint(mm_tail_infos.image_binary)-
                                                           get_little_endian_uint(mm_tail_infos.image_extension);
                    section_sizes[MMTAG_IMAGE_BINARY]    = get_little_endian_uint(mm_tail_infos.unused)-
                                                           get_little_endian_uint(mm_tail_infos.image_binary);
                    section_sizes[MMTAG_UNUSED]          = get_little_endian_uint(mm_tail_infos.version_info)-
                                                           get_little_endian_uint(mm_tail_infos.unused);
                    section_sizes[MMTAG_VERSION_INFO]    = get_little_endian_uint(mm_tail_infos.audio_metadata)-
                                                           get_little_endian_uint(mm_tail_infos.version_info);
                    section_sizes[MMTAG_AUDIO_METADATA]  = MMTAG_SAFE_SEARCH_SIZE-(int)(p-search)-MMTAG_VERSION_INFO_SIZE+MMTAG_META_DATA_MINIMUM_SIZE;
                    mmtag->metadata_size = section_sizes[MMTAG_AUDIO_METADATA];

                    mmtag->tag_size = MMTAG_DATA_OFFSETS_SIZE + MMTAG_FOOTER_SIZE;
                    for (j=MMTAG_IMAGE_EXTENSION; j<MMTAG_OFFSET_ENTRIES; j++)
                        mmtag->tag_size += section_sizes[j];

                    /* now, seek back to collect further data and check whether the optional header is there */
                    fseeko(fi, pos-mmtag->tag_size-MMTAG_HEADER_SIZE, SEEK_SET);
                    if (fread(search,MMTAG_SAFE_SEARCH_SIZE,1,fi)==1)
                    {
                        memcpy(mmtag->image_ext, &search[MMTAG_HEADER_SIZE], MMTAG_IMAGE_EXTENSION_LEN);
                        mmtag->image_size = search[MMTAG_HEADER_SIZE+MMTAG_IMAGE_EXTENSION_LEN]+
                                search[MMTAG_HEADER_SIZE+MMTAG_IMAGE_EXTENSION_LEN+1]*256+
                                search[MMTAG_HEADER_SIZE+MMTAG_IMAGE_EXTENSION_LEN+2]*256*256+
                                search[MMTAG_HEADER_SIZE+MMTAG_IMAGE_EXTENSION_LEN+3]*256*256*256;
                        mmtag->image_offset = pos-mmtag->tag_size+MMTAG_IMAGE_EXTENSION_LEN+MMTAG_IMAGE_SIZE_LEN;
                        mmtag->header_present = (memsrch(search, MMTAG_SAFE_SEARCH_SIZE, MMTAG_VERSION_BLOCK_SYNC_STRING)==search);
                        if (mmtag->header_present)
                            mmtag->tag_size += MMTAG_HEADER_SIZE;
                    }
                }
            }
        }
    }

    fseeko(fi, filepos, SEEK_SET);
}

char checkmm_partial_tag(FILE *fi, off_t pos, mmtag_t *mmtag)
{
    mmtag_partial_infos_t mm_infos;
    char present=0;

    off_t filepos=ftello(fi);

    fseeko(fi, pos, SEEK_SET);
    if (fread((unsigned char *)&mm_infos,sizeof(mmtag_partial_infos_t),1,fi)==1)
    {
        if (!memcmp(&mm_infos.sync, MMTAG_VERSION_BLOCK_SYNC_STRING, MMTAG_VERSION_BLOCK_SYNC_LENGTH))
        {
            memcpy(mmtag->mm_ver,  mm_infos.musm, MMTAG_VERSION_BLOCK_STRING_LEN);
            memcpy(mmtag->enc_ver, mm_infos.xing, MMTAG_VERSION_BLOCK_STRING_LEN);
            present=1;
        }
    }

    fseeko(fi, filepos, SEEK_SET);
    return present;
}


/*
 * Here the info tag id3v2 is detected
 * This function returns the size of the tag (if detected)
 * so the main cycle can choose if either show it or skip it.
 * Also, idMaj and idmin provide id3V2 major and minor version number.
 * If the tag is not found, return value is 0.
 * If errors are detected, return value is -1.
 */
int checkid3v2(unsigned char *buff, int length, int *next_tag, unsigned char *idMaj, unsigned char *idmin)
{
    unsigned char *id3header;
    int id3v2size=0;

    if ((id3header=memsrch(buff, length, ID3V2_ID_STRING)) != NULL)
    {
        /* has ID3v2 */
        *idMaj=id3header[3];
        *idmin=id3header[4];
        if (!(id3header[6]&0x80) && !(id3header[7]&0x80) && !(id3header[8]&0x80) && !(id3header[9]&0x80) && !(id3header[5]&0x0f))
        {
            id3v2size = id3header[6]*2097152+id3header[7]*16384+id3header[8]*128+id3header[9] +ID3V2_HEADER_LENGTH;
            if (id3header[5]&ID3V2_FLAGS_FOOTER_PRESENT)
                id3v2size += ID3V2_FOOTER_LENGTH;
            *next_tag = (int)(id3header-buff);
        }
        else
            id3v2size=-1;
    }

    return id3v2size;
}

int checkapetagx_head(unsigned char *buff, int length, int *next_tag, int *ape_vers, int *ape_items, char *header_present)
{
    int ape_len=0;
    unsigned int hflags;
    apefooter_t apefooter;
    unsigned char *p_apefooter;

    if ((p_apefooter=memsrch(buff, length, APETAGEX_SIGNATURE)) != NULL)
    {
        memcpy((void *)&apefooter, p_apefooter, sizeof(apefooter_t));

        if ((ape_len=checkapetagx(&apefooter)) != 0)
        {
            hflags = get_little_endian_uint(apefooter.tag_flags);
            if ((hflags&APETAGEX_FLAGS_HEADER_PRESENT)!=0 && (hflags&APETAGEX_FLAGS_THIS_IS_HEADER)!=0)
            {
                *ape_vers=get_little_endian_uint(apefooter.version);
                *ape_items=get_little_endian_uint(apefooter.item_count);
                *header_present=1;
                *next_tag = (int)(p_apefooter - buff);
            }
            else
            {
                /* very strange tag - header MUST be present when put at the file head! */
                /* I will assume this as a data corruption */
                ape_len = 0;
            }
        }
    }

    return ape_len;
}

int checkwaveriff_datachunk(unsigned char *buff, int *next_tag, int *datachunk)
{
    int chunk_len = 0;

    if (
        isprint(buff[0])
        &&
        isprint(buff[1])
        &&
        isprint(buff[2])
        &&
        isprint(buff[3])
       )
    {
        *next_tag = 0;
        if (memcmp(buff, WAVE_RIFF_DATA_ID, WAVE_RIFF_IDS_LENGTH))
        {
            /* not the `data' chunk */
            chunk_len = get_little_endian_uint(&buff[4]) + WAVE_RIFF_IDS_LENGTH + sizeof(unsigned int);
            *datachunk = 0;
        }
        else
        {
            /* `data' chunk found */
            chunk_len =  WAVE_RIFF_IDS_LENGTH + sizeof(unsigned int);
            *datachunk = get_little_endian_uint(&buff[4]);
        }
    }

    return chunk_len;
}

int checkwaveriff(unsigned char *buff, int length, int *next_tag)
{
    int riff_len=0;
    unsigned char *waveriff;

    if ((waveriff=memsrch(buff, length, WAVE_RIFF_STRUCTURE_ID)) != NULL)
    {
        /* "RIFF" found */
        if (memcmp(&waveriff[8], WAVE_RIFF_WAVE_ID, WAVE_RIFF_IDS_LENGTH)==0)
        {
            /* "WAVE" found */
            riff_len = WAVE_RIFF_HEADER_LENGTH;
            *next_tag = (int)(waveriff - buff);
        }
    }

    return riff_len;
}
