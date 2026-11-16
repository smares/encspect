/* Measures how fast libspeex 1.2.1's fixed-point decoder (the build speex_tool.py makes) forgets its state: the first Speex stream of an Ogg file is decoded whole, and again
 * from each packet K on with a decoder that starts as at the beginning of a stream (what the analyzer's decoder does after a gap). For each K a line "K n" says in how many
 * frames of the stream's size the two outputs last differ by more than THRESHOLD least significant bits (n = 0 where they never do).
 *
 *     speex_convergence FILE THRESHOLD
 *
 * docs/design/lossy-decoders.md, "As built: Speex", gives the quantiles over the fixtures; scripts/fixtures/speex_convergence.py prints them.
 * Copyright (c) 2002-2008 Jean-Marc Valin and Xiph.org Foundation, whose speexdec.c this follows in its use of libspeex (BSD 3-clause license, in THIRD-PARTY-NOTICES.md).
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ogg/ogg.h>
#include <speex/speex.h>
#include <speex/speex_header.h>
#include <speex/speex_stereo.h>
#include <speex/speex_callbacks.h>
typedef struct { unsigned char *d; int n; } Pk;
static Pk pk[100000]; static int np=0;
static void *mk(SpeexHeader *h, int *fs, SpeexStereoState *st, int *ch){
  const SpeexMode *m=speex_lib_get_mode(h->mode); void *s=speex_decoder_init(m); spx_int32_t enh=1, rate=h->rate;
  speex_decoder_ctl(s,SPEEX_SET_ENH,&enh); speex_decoder_ctl(s,SPEEX_GET_FRAME_SIZE,fs); speex_decoder_ctl(s,SPEEX_SET_SAMPLING_RATE,&rate);
  SpeexStereoState init=SPEEX_STEREO_STATE_INIT; *st=init; *ch=h->nb_channels==1?1:2;
  if(*ch==2){SpeexCallback cb; cb.callback_id=SPEEX_INBAND_STEREO; cb.func=speex_std_stereo_request_handler; cb.data=st; speex_decoder_ctl(s,SPEEX_SET_HANDLER,&cb);}
  return s; }
static int run(SpeexHeader *h,int from,short *out){ int fs,ch; SpeexStereoState st; void *s=mk(h,&fs,&st,&ch); SpeexBits bits; speex_bits_init(&bits); short o[1280]; memset(o,0,sizeof o); int n=0;
  for(int p=from;p<np;p++){ speex_bits_read_from(&bits,(char*)pk[p].d,pk[p].n);
    for(int j=0;j<h->frames_per_packet;j++){ int r=speex_decode_int(s,&bits,o); if(r) break; if(ch==2) speex_decode_stereo_int(o,fs,&st); memcpy(out+n,o,fs*ch*2); n+=fs*ch; } }
  return n; }
int main(int argc,char**argv){ FILE*in=fopen(argv[1],"rb"); ogg_sync_state oy; ogg_page og; ogg_stream_state os; int init=0; ogg_sync_init(&oy); SpeexHeader *h=NULL; int count=0;
  for(;;){ while(ogg_sync_pageout(&oy,&og)==1){ if(!init){ogg_stream_init(&os,ogg_page_serialno(&og));init=1;} ogg_stream_pagein(&os,&og); ogg_packet op;
      while(ogg_stream_packetout(&os,&op)==1){ if(count==0) h=speex_packet_to_header((char*)op.packet,op.bytes); else if(count>=2){ pk[np].d=malloc(op.bytes); memcpy(pk[np].d,op.packet,op.bytes); pk[np].n=op.bytes; np++; } count++; } }
    char*b=ogg_sync_buffer(&oy,4096); int n=fread(b,1,4096,in); if(n<=0)break; ogg_sync_wrote(&oy,n);} 
  int fs,ch; SpeexStereoState st; void *s=mk(h,&fs,&st,&ch); (void)s;
  static short a[4000000], b[4000000]; int na=run(h,0,a); int per=h->frames_per_packet*fs*ch;
  int th=atoi(argv[2]); int worst=0; double sum=0; int cnt=0; int maxdiff=0;
  for(int k=3;k<np-5;k+=1){ int nb=run(h,k,b); int off=k*per; int last=-1; for(int i=0;i<nb;i++){ int d=a[off+i]-b[i]; if(abs(d)>th){ last=i; if(abs(d)>maxdiff)maxdiff=abs(d);} } int smp=(last+1)/ch; if(smp>worst)worst=smp; sum+=smp; cnt++; printf("K %d\n",(smp+fs-1)/fs); }
  printf("%s: packets %d frame %d channels %d: samples until exact: max %d (%.2f frames) mean %.1f max diff %d\n",argv[1],np,fs,ch,worst,(double)worst/fs,sum/cnt,maxdiff);
  return 0; }
