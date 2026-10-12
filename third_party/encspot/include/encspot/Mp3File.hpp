#ifndef ENCSPOT_MP3FILE_H
#define ENCSPOT_MP3FILE_H

#include <vector>
#include "ID3tag.hpp"
#include "Mp3Structs.hpp"
#include "Common.hpp"

#define MPG_MD_STEREO       0
#define MPG_MD_JOINT_STEREO 1
#define MPG_MD_DUAL_CHANNEL 2
#define MPG_MD_MONO         3

#define MPEGID_1            0x0      //MPEG Version 1 (ISO/IEC 11172-3)     
#define MPEGID_2            0x1      //MPEG Version 2 (ISO/IEC 13818-3)

#include "MediaFile.hpp"

struct mp3header
{
  int id;                            //e.g. MPEG version 1, unless idex = 1
  int idex;                          //1 = MPEG 2.5
  int bitrate_idx;                   //between 0 and 15
  int mode;                          //e.g. joint stereo
  int modeex;                        //e.g. joint stereo usage
  int padding;                       //0 or 1
  int freq;                          //e.g. 44100
  int channels;                      //e.g. 1 = mono, 2 = stereo
  int layer;                         //1,2 or 3    
  int protection;                    //crc protection bit

  int copyright;
  int emphasis;
  int original;
};
 
struct mp3data;
struct xing;
struct thread_helper;

class CMp3File : public CMediaFile
{
private:    
  unsigned m_nLastBits;
  unsigned long m_lastBits;

public:    
  static bool IsThisType(const tstring &fname)
  {
    CMp3File afile;
    return(afile.IsMp3File(fname));
  }

  bool UpdateID3v1(id3v1 tag, int track);
    
  struct sink
  {
    virtual bool Fire(const mp3data &data) const = 0;
  };

  struct sink_mini
  {
    virtual bool Fire(int64_t nCurr, int64_t nTotal) const = 0;
  };

  bool Open(const tstring &fname, const tstring &mode = _T("rb"))
  {
    m_tag.Load(fname.c_str());
    return CMediaFile::Open(fname, mode);    
  }

  CID3Tag    m_tag;
  
  int     GetFilesize() { return m_nFilesize; }
  float   GetAverageBitrate(const mp3data &data);
  bool    ProcessFrames(bool bFull, mp3data &data_out, int nFrameLimit,info_lists *pInfoLists,bool bDisableCache,CMp3File::sink *pHelp = NULL);
  bool    Trim(int64_t nStart, int64_t nStop, const mp3data &data, const std::vector<int> &byte_locations, 
            const tstring &newpath, bool bNewXing,bool bID3, CMp3File::sink_mini *pSink = NULL);
  bool    ExtractRegion(int64_t nStart, int64_t nStop, const mp3data &data, const std::vector<int> &byte_locations,
            const tstring& newpath, bool bNewXing, bool bID3, CMp3File::sink_mini *pSink = NULL);
  bool    AppendMP3(mp3data datain1, const tstring &path2, const tstring &pathout, bool bID1, bool bID2, CMp3File::sink_mini *pHelp);
  CMp3File();
  virtual ~CMp3File();
    
  tstring TranslateLabel(const tstring &label);
  int     GetMusicCRC(mp3data &data, const CMp3File::sink_mini *pHelp=NULL);

private:
  bool    IsMp3File(tstring fname);

  bool    FullProcessFrames(mp3data &data_out, int nFrameLimit,info_lists *pInfoLists,bool bDisableCache, int id3v1pos, int initpos,CMp3File::sink *pHelp = NULL);
    
  void    CreateI2(uint8_t *buf, int nValue) const;
  void    CreateI4(uint8_t *buf, int nValue) const;
  bool    copy_file_data(FILE *new_file, FILE *old_file, int nStart, int nSize = -1, CMp3File::sink_mini *pSink = NULL);
  bool    DetectVBR(mp3data &data_out);
  bool    ReportProgress(mp3data &data, const sink *pHelp);
  int     UpdateInfoFromHeader(mp3data &out_data, const mp3header &hHeader);

  int     SeekNextHeader(int safety_max = 5);
  int     SeekLastHeader(uint8_t *final10 = NULL);
    
  bool    GetVBRIHeader(mp3data &data, unsigned char *buf);
  int     GetXingHeader(mp3data &data, unsigned char *buf);
    
    
  int     GuessFrameLength();
  int     ProcessNextFrame(mp3data &data_out,info_lists *pInfoLists);
  tstring GetLabelsFromBuffer(const char *buff, std::size_t len);
  tstring ReportOutOfSyncError(mp3data &main_data, const mp3header &hHeader);
  bool    CheckDone(mp3data &data, int pos);
  void    SetBaseInfo(mp3data &data, const mp3header &header);

    
  tstring GetLabels(const XHEADDATA &xing);

  int ReadInt();
  static int ReadInt(const uint8_t *pBuffer);

  //used to be head_check2
  int MakeHeader(unsigned long head, mp3header &mhHead);

  bool        GetVBRTags(mp3data &data);
  bool        GetID3v2(mp3data &data);
  int            GetID3v1(mp3data &data);

  unsigned long int readBits(unsigned n);
};

#endif
