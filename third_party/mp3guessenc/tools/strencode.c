
/* this will enable 64 bit integers as file offset on 32 bit posix machines */
#define _FILE_OFFSET_BITS 64

#include <ctype.h>
#include <stdio.h>
#include <string.h>
#include <sys/stat.h>
#include "../decode.h"

#define MAX_BUFFER_LEN 1024 // maximum size of the input file

int main(int argc,char **argv)
{
    char data[MAX_BUFFER_LEN],s[MAX_BUFFER_LEN],
         stat_len;  /* this is the length of a single generated statement */
    unsigned char f2c,verb=0;
    int i,errors=0,/*punta=0,*/len;
    FILE *input_file;
#ifdef _WIN32
    struct _stati64 buf;
    #define stat _stati64
#else
    struct stat buf;
#endif /* _WIN32 */

    if ((argc!=2)&&(argc!=3))
    {
        printf("strencode encodes a file in order to embed it within mp3guessenc.\nUsage:\tstrencode [-v] filename\nMax input file size: %d bytes.\n",MAX_BUFFER_LEN);
        return 0;
    }

/*
 * Now I'm sure argc is 2 (verbose option was not set) or 3 (verbose option was set)
 */

    if (argc==3)
    {
        if ((argv[1][0]=='-')&&(argv[1][1]=='v'))
            verb=1; /* verbose mode was asked */
        else
        {
            printf("The only valid option is `-v' (verbose).\n");
            return 1;
        }
    }

    /* set position of filename to check */
/*    if (argc==2)
        f2c=1;
    else
        f2c=2;
*/
    f2c = argc - 1;

    if (stat(argv[f2c],&buf)==-1)
    {
        sprintf(data,"Unable to retrieve informations about `%s'.\nError",argv[f2c]);
        perror(data);
        return 2;
    }

    if (!S_ISREG(buf.st_mode))
    {
        printf("`%s' not a regular file, exiting.\n",argv[f2c]);
        return 3;
    }

    if (!buf.st_size)
    {
        printf("File `%s' is empty, nothing to do.\n",argv[f2c]);
        return 4;
    }

    input_file=fopen(argv[f2c],"rb");
    if (input_file==NULL)
    {
        printf("Error opening `%s' in read-only mode, exiting.\n",argv[f2c]);
        return 5;
    }

    if ((long long int)buf.st_size>(long long int)MAX_BUFFER_LEN)
        printf("WARNING: file is too big, only %d bytes will be read.\n",MAX_BUFFER_LEN);

    if (verb) printf("Reading the file.\n");

/*    i=fgetc(input_file);

    while (!feof(input_file)&&(punta!=MAX_BUFFER_LEN-1))
    {
        data[punta++]=i;
        i=fgetc(input_file);
    }
    data[punta]=0;
*/
    len=fread(data,1,MAX_BUFFER_LEN,input_file);

    fclose(input_file);

    if (verb)
        printf("Finished reading `%s' (read %d b), ", argv[f2c],len);

/*    if (punta==MAX_BUFFER_LEN-1)
        printf("WARNING: buffer is full!\n");
    else
        if (verb) printf("all went OK.\n");
*/

//    len=strlen(data);

//    for (i=0; i<len+1; i++) s[i]=data[i];/* s[len]=0;*/
    memcpy(s,data,len);
//    if (verb) printf("string:\n%s\nlength=%d\n",s,len);
//    len++; /* I need this in order to include the string terminator `\0' in the scramble process */
    if (verb)
    {
        printf("input data:\n\n");
        for (i=0; i<len; i++)
//            if (isprint(s[i])||(s[i]=='\n'))
                putc(s[i],stdout);
//            else
//                putc('.',stdout);
//        printf("\ndata length is %d b.\n",len);
        if (s[len-1]!='\n') putc('\n',stdout);
    }

/*
 * NOW I'VE GOT MY DATA INTO `data' AND INTO `s', I WILL NOW DO 4 MAIN STEPS:
 * 1. SCRAMBLE DATA IN `s' VIA `decode'
 * 2. PRINT SCRAMBLED STRING
 * 3. SCRAMBLE DATA `s' AGAIN
 * 4. COMPARE DATA WITH THE UN-MODIFIED COPY INTO `data'
 */

    decode (s,len);
    if (verb)
    {
        printf("\nscramble done!\nthe new string is:\n");
/*    for (i=0; i<len; i++)
        if (verb)
            {if (isprint(s[i])) printf("%c",s[i]); else printf(".");}
*/
        for (i=0; i<len; i++)
            if (isprint((int)s[i]))
                putc(s[i],stdout);
            else
                putc('.',stdout);
        printf("\n");
    }

#define MAX_STATEMENT 76
    printf("\n#define DATA_LEN %u\n",len);
    stat_len=printf("unsigned char scrambled_data[DATA_LEN]={");
    for (i=0; i<len-1; i++)
    {
      stat_len+=printf("%u,",(unsigned char)s[i]);
      if (stat_len>MAX_STATEMENT)
      {
        printf("\n");
        stat_len=0;
      }
    }
    printf("%u};\n\n",(unsigned char)s[i]);

    decode (s,len);
    if (verb)
    {
        printf("scramble done again! string:\n\n");
        for (i=0; i<len; i++)
//            if (isprint(s[i])||(s[i]=='\n'))
                putc(s[i],stdout);
//            else
//                putc('.',stdout);
//        printf("\nlength=%d\n",len);
        if (s[len-1]!='\n') putc('\n',stdout);
        printf("\nComparison...\n");
    }
    for (i=0; i<len; i++) if (s[i]!=data[i]) {if (verb) printf("ERROR AT CHAR %u!!! (%d!=%d)\n",i,(unsigned char)s[i],(unsigned char)data[i]); errors++;}
    if (!errors)
    {
        if (verb) printf("No errors detected.\n");
        return 0;
    }
    else
    {
        if (verb) printf ("%u errors detected.\n",errors);
        return 6;
    }
}
