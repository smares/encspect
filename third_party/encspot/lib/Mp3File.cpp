#include <string>
#include <cstring>
#include <climits>
#include <vector>
#include <algorithm>
#include <encspot/Common.hpp>
#include <encspot/Mp3File.hpp>

static const long freqs[9] = { 44100, 48000, 32000,
       22050, 24000, 16000 ,
       11025 , 12000 , 8000 };
  
const int tabsel_123[2][3][16] = {
  { {128,32,64,96,128,160,192,224,256,288,320,352,384,416,448,},
    {128,32,48,56, 64, 80, 96,112,128,160,192,224,256,320,384,},
    {128,32,40,48, 56, 64, 80, 96,112,128,160,192,224,256,320,} },

  { {128,32,48,56,64,80,96,112,128,144,160,176,192,224,256,},
    {128,8,16,24,32,40,48,56,64,80,96,112,128,144,160,},
    {128,8,16,24,32,40,48,56,64,80,96,112,128,144,160,} }
};


//crc lookup table
static const unsigned int crc16tab[256] =
{
  0x0000, 0xC0C1, 0xC181, 0x0140, 0xC301, 0x03C0, 0x0280, 0xC241,
  0xC601, 0x06C0, 0x0780, 0xC741, 0x0500, 0xC5C1, 0xC481, 0x0440,
  0xCC01, 0x0CC0, 0x0D80, 0xCD41, 0x0F00, 0xCFC1, 0xCE81, 0x0E40, 
  0x0A00, 0xCAC1, 0xCB81, 0x0B40, 0xC901, 0x09C0, 0x0880, 0xC841,
  0xD801, 0x18C0, 0x1980, 0xD941, 0x1B00, 0xDBC1, 0xDA81, 0x1A40, 
  0x1E00, 0xDEC1, 0xDF81, 0x1F40, 0xDD01, 0x1DC0, 0x1C80, 0xDC41, 
  0x1400, 0xD4C1, 0xD581, 0x1540, 0xD701, 0x17C0, 0x1680, 0xD641,
  0xD201, 0x12C0, 0x1380, 0xD341, 0x1100, 0xD1C1, 0xD081, 0x1040,
  0xF001, 0x30C0, 0x3180, 0xF141, 0x3300, 0xF3C1, 0xF281, 0x3240,
  0x3600, 0xF6C1, 0xF781, 0x3740, 0xF501, 0x35C0, 0x3480, 0xF441, 
  0x3C00, 0xFCC1, 0xFD81, 0x3D40, 0xFF01, 0x3FC0, 0x3E80, 0xFE41, 
  0xFA01, 0x3AC0, 0x3B80, 0xFB41, 0x3900, 0xF9C1, 0xF881, 0x3840,
  0x2800, 0xE8C1, 0xE981, 0x2940, 0xEB01, 0x2BC0, 0x2A80, 0xEA41, 
  0xEE01, 0x2EC0, 0x2F80, 0xEF41, 0x2D00, 0xEDC1, 0xEC81, 0x2C40,
  0xE401, 0x24C0, 0x2580, 0xE541, 0x2700, 0xE7C1, 0xE681, 0x2640, 
  0x2200, 0xE2C1, 0xE381, 0x2340, 0xE101, 0x21C0, 0x2080, 0xE041,
  0xA001, 0x60C0, 0x6180, 0xA141, 0x6300, 0xA3C1, 0xA281, 0x6240, 
  0x6600, 0xA6C1, 0xA781, 0x6740, 0xA501, 0x65C0, 0x6480, 0xA441, 
  0x6C00, 0xACC1, 0xAD81, 0x6D40, 0xAF01, 0x6FC0, 0x6E80, 0xAE41,
  0xAA01, 0x6AC0, 0x6B80, 0xAB41, 0x6900, 0xA9C1, 0xA881, 0x6840, 
  0x7800, 0xB8C1, 0xB981, 0x7940, 0xBB01, 0x7BC0, 0x7A80, 0xBA41,
  0xBE01, 0x7EC0, 0x7F80, 0xBF41, 0x7D00, 0xBDC1, 0xBC81, 0x7C40,
  0xB401, 0x74C0, 0x7580, 0xB541, 0x7700, 0xB7C1, 0xB681, 0x7640, 
  0x7200, 0xB2C1, 0xB381, 0x7340, 0xB101, 0x71C0, 0x7080, 0xB041, 
  0x5000, 0x90C1, 0x9181, 0x5140, 0x9301, 0x53C0, 0x5280, 0x9241, 
  0x9601, 0x56C0, 0x5780, 0x9741, 0x5500, 0x95C1, 0x9481, 0x5440,
  0x9C01, 0x5CC0, 0x5D80, 0x9D41, 0x5F00, 0x9FC1, 0x9E81, 0x5E40, 
  0x5A00, 0x9AC1, 0x9B81, 0x5B40, 0x9901, 0x59C0, 0x5880, 0x9841,
  0x8801, 0x48C0, 0x4980, 0x8941, 0x4B00, 0x8BC1, 0x8A81, 0x4A40,
  0x4E00, 0x8EC1, 0x8F81, 0x4F40, 0x8D01, 0x4DC0, 0x4C80, 0x8C41,
  0x4400, 0x84C1, 0x8581, 0x4540, 0x8701, 0x47C0, 0x4680, 0x8641,
  0x8201, 0x42C0, 0x4380, 0x8341, 0x4100, 0x81C1, 0x8081, 0x4040
};


static inline int CRC_update_lookup(const int value, const int initial_crc)
{
  int crc = initial_crc;
  const int tmp=crc^value;
  crc=(crc>>8)^crc16tab[tmp & 0xff];
  return crc;
}

static int ExtractI4(const unsigned char *buf)
{
  // big endian extract
  int x = buf[0];
  x <<= 8;
  x |= buf[1];
  x <<= 8;
  x |= buf[2];
  x <<= 8;
  x |= buf[3];
  
  return x;
}


static int ExtractI2(unsigned char *buf)
{
  // big endian extract
  int x = buf[0];
  x <<= 8;
  x |= buf[1];
  
  return x;
}

// 4   Xing
// 4   flags
// 4   frames
// 4   bytes
// 100 toc


CMp3File::CMp3File() : m_nLastBits(0)
{
}

CMp3File::~CMp3File()
{
}

int CMp3File::ReadInt()
{
  int head = 0;
  head |= getc(m_pFile); head = head << 8;
  head |= getc(m_pFile); head = head << 8;
  head |= getc(m_pFile); head = head << 8;
  head |= getc(m_pFile);

  return head;
}

int CMp3File::ReadInt(const uint8_t *pBuffer)
{
  int head = 0;
  head |= *(pBuffer + 0); head = head << 8;
  head |= *(pBuffer + 1); head = head << 8;
  head |= *(pBuffer + 2); head = head << 8;
  head |= *(pBuffer + 3);

  return head;
}

int CMp3File::MakeHeader(unsigned long head, mp3header &mhHead)
{
  int srate;
  int framesize;

  if ((head & 0xffe00000) != 0xffe00000) return 0;
  if (!((head>>17)&3)) return 0;
  if (((head>>12)&0xf) == 0xf) return 0;
  if (((head>>10)&0x3) == 0x3 ) return 0;

  //mp3 type id
  
  mhHead.id      = (head >> 19) &  1;
  mhHead.idex      = (head >> 20) &  1;    //0 = Mpeg 2.5

  if(!mhHead.idex) 
    srate      = 6 + ((head>>10)&0x3);    
  else 
    srate      = ((head>>10)&0x3) + ((1-mhHead.id) * 3);

  mhHead.bitrate_idx  = ((head>>12)&0xf);
  mhHead.padding      = ((head>>9)&0x1);
  mhHead.mode         = ((head>>6)&0x3);
  mhHead.modeex       = (head >>  4) &  3; 

  mhHead.freq         = freqs[srate];
  mhHead.channels     = (mhHead.mode == MPG_MD_MONO) ? 1 : 2;
  mhHead.layer        = 4-((head>>17)&3);
  mhHead.protection   = (head >> 16) &  1;
  mhHead.copyright    = (head >> 3) & 1;
  mhHead.original     = (head >> 2) & 1;
  mhHead.emphasis     = head & 3;

  framesize = tabsel_123[1 - mhHead.id][mhHead.layer-1][mhHead.bitrate_idx]*144000/(mhHead.freq<<(1 - mhHead.id))+mhHead.padding;
  return framesize;
}

int CMp3File::SeekNextHeader(const int safety_max)
{
  MYASSERT(m_pFile);

  int pos = ftell(m_pFile);
  mp3header mhHead;

  std::vector<uint8_t> buffer(1000*1024);    //1MB buffer
  const int read = fread(&buffer[0], 1, buffer.size(), m_pFile);

  for(int i=0; i<read/2; ++i)
  {
    int framesize_guess = 1;
    int safety;
    int j = i;

    for (safety = 0; (safety < safety_max) && framesize_guess; ++safety)
    {
      const int head = ReadInt(&buffer[j]);      
      framesize_guess = MakeHeader(head, mhHead);

      j+=framesize_guess;

      if ((j + 4) >= read)
        return -1;
    }

    if (safety==safety_max)
    {
      fseek(m_pFile, pos + i, SEEK_SET);
      return i;
    }
  }

  fseek(m_pFile, pos, SEEK_SET);
  return -1;
}

int CMp3File::SeekLastHeader(uint8_t *const final10)
{
  MYASSERT(m_pFile);
  const int finalsize = 10; 

  fseek(m_pFile, 0, SEEK_END);
  const int fsize = ftell(m_pFile);

  //look at up to last 2MB
  int i;
  for (i = 0; i<5; ++i)
  {
    const int back_pos = std::min(fsize, 65536 * 5);
    fseek(m_pFile, -back_pos, SEEK_END);

    if (SeekNextHeader()!=-1)
      break;
  }

  if (i==5)
    return -1;

  //ok we've got a header... now move forward to the 
  //end of the file.
  for (;;)
  {
    const long currpos = ftell(m_pFile);
  
    const int head = ReadInt();
    mp3header mhHead;

    //if we had a a frame, but now dont, then perhaps id3 tag....
    const int framesize = MakeHeader(head, mhHead);
    if (!framesize)
    {
      if (final10)
      {
        //copy last 10 bytes into buffer
        fseek(m_pFile,currpos - finalsize - 1,SEEK_SET);
        const std::size_t read = fread(final10, 1, finalsize, m_pFile);

        if (read != finalsize)
          return -1;
      }

      return 1;
    }

    if ((currpos + framesize) == fsize)
    {
      //the final frame has the correct size...
      if (final10)
      {
        //copy last 10 bytes into buffer
        fseek(m_pFile,currpos + framesize - finalsize - 1,SEEK_SET);
        const std::size_t read = fread(final10, 1, finalsize, m_pFile);

        if (read != finalsize)
          return -1;
      }

      return 1;
    }
    else if ((currpos + framesize) > fsize)
    {
      return -1;
    }
    else
    {
      fseek(m_pFile,currpos + framesize,SEEK_SET);
    }
  }
}


//times in ms
//frame_length in s
//byte_locations: byte location of each frame.
bool CMp3File::Trim(const int64_t nStart, const int64_t nStop, const mp3data &data, 
  const std::vector<int> &byte_locations, const tstring& newpath, 
  const bool bNewXing, const bool bID3, CMp3File::sink_mini *const pSink)    
{
  MYASSERT(m_pFile);

  //calculate size of frame in s
  double frame_length = 576.0*(data.id?2:1)/data.base_freq;       

  tstring temp_name;
  if (newpath.empty())
    temp_name = m_tsFname + _T("__EncSpot.tmp");
  else
    temp_name = newpath;
  
  FILE *const pNewFile = _tfopen(temp_name.c_str(), _T("wb"));

  if (pNewFile==NULL)
    return false;

  const int start_pos  = nStart / (frame_length * 1000) + 1;
  const int stop_pos  = nStop  / (frame_length * 1000);

  int start_loc;
  int stop_loc;

  if (byte_locations.size() > stop_pos)
  {
    start_loc  = byte_locations[start_pos];
    stop_loc  = byte_locations[stop_pos];
  }
  else    //assume CBR
  {
    const int bitrate = data.bitrate;    //kbps = bpms
    start_loc  = (nStart * bitrate) / 8;
    stop_loc  = (nStart * bitrate) / 8;
  }

  int id3size = m_tag.size();
  fseek(m_pFile, id3size,SEEK_SET);
  SeekNextHeader();
  id3size = ftell(m_pFile);

  int nInitPos = 0;

  if (bID3)
    copy_file_data(pNewFile, m_pFile, 0, id3size);

  nInitPos+=id3size;
  
  if (data.xing_header.bValidLame || data.xing_present)
  {
    const int nPos = ftell(m_pFile);

    //read mp3 header
    const int nHeader = ReadInt();
    mp3header hHeader;
    const int nFramesize = MakeHeader(nHeader,hHeader);

    if (bNewXing)
    {
      //read frame
      std::vector<uint8_t> pb(nFramesize);
      fseek(m_pFile, nPos, SEEK_SET);
      fread(&pb[0],1,nFramesize,m_pFile);
      const std::string buffer(reinterpret_cast<char *>(&pb[0]), nFramesize);

      std::size_t startpos = buffer.find("Xing");
      if (startpos==std::string::npos)
        startpos = buffer.find("Info");

      if (startpos!=std::string::npos)
      {
        if (data.xing_header.flags & FRAMES_FLAG)
        {
          const int new_frame_count = data.frameCount - (stop_pos - start_pos);
          const int old_frame_count = ReadInt(&pb[startpos + 8]);
          CreateI4(&pb[startpos + 8], new_frame_count);
        }

        if (data.xing_header.flags & BYTES_FLAG)
        {
          const int old_bytes_count = ReadInt(&pb[startpos + 12]);
          const int new_bytes_count = old_bytes_count - (stop_loc - start_loc);
          CreateI4(&pb[startpos + 12],new_bytes_count);
        }

        if (data.xing_header.bValidLame)
        {
          //adjust music length
          const int nLenOffset = startpos + 0x0C + 7 * 0x10 + 0x10 + 0x0E - 2 - 4;
          const int nOldLength = ExtractI4(&pb[nLenOffset]);

          const int nNewLength = nOldLength - (stop_loc - start_loc);
          CreateI4(&pb[nLenOffset], nNewLength);        

          //adjust tag CRC
          const int nCRCLen = startpos + 0x0C + 7 * 0x10 + 0x10 + 0x0E;

          uint16_t crc = 0;
    
          for (int i = 0x00; i<nCRCLen; ++i)
            crc = CRC_update_lookup(pb[i],crc);

          const int nOldCRC = ExtractI2(&pb[nCRCLen]);
          CreateI2(&pb[nCRCLen], crc);  
        }
      }
      
      fwrite(&pb[0], 1, nFramesize, pNewFile);
    }
    
    nInitPos += nFramesize;
  }

  int nStopSize = m_nFilesize;
  if (!bID3 && data.bId3v1Tag)
    nStopSize-=128;

  if (!copy_file_data(pNewFile, m_pFile, nInitPos, start_loc - nInitPos,pSink) ||
    !copy_file_data(pNewFile, m_pFile, stop_loc, nStopSize - stop_loc,pSink)
    )
  {
    fclose(pNewFile);
    _tremove(temp_name.c_str());
    return false;
  }
  else
  {
    fclose(pNewFile);
    return true;
  }

/*  if (newpath.empty())
  {
    bool succ;
    succ = fclose(m_pFile);
    succ = remove(m_tsFname.c_str());
    succ = rename(temp_name.c_str(),m_tsFname.c_str());
    int test = errno;
  
    Open(m_tsFname);
  }
*/
}

//times in ms
//frame_length in s
//byte_locations: byte location of each frame.
bool CMp3File::ExtractRegion(const int64_t nStart, const int64_t nStop, const mp3data &data, 
  const std::vector<int> &byte_locations, const tstring& newpath, const bool bNewXing, 
  const bool bID3, CMp3File::sink_mini *pSink)    
{
  MYASSERT(m_pFile);

  //calculate size of frame in s
  double frame_length = 576.0*(data.id?2:1)/data.base_freq;       

  tstring temp_name;

  if (newpath.empty())
    temp_name = m_tsFname + _T("__EncSpot.tmp");
  else
    temp_name = newpath;
  
  
  FILE *pNewFile = _tfopen(temp_name.c_str(), _T("wb"));
  if (pNewFile==NULL)
    return false;

  const int start_pos  = nStart / (frame_length * 1000) + 1;
  const int stop_pos  = nStop  / (frame_length * 1000);

  int64_t start_loc;
  int64_t stop_loc;

  if (byte_locations.size() > stop_pos)
  {
    start_loc  = byte_locations[start_pos];
    stop_loc  = byte_locations[stop_pos];
  }
  else    //assume CBR
  {
    const int bitrate = data.bitrate;    //kbps = bpms
    start_loc  = (nStart * bitrate) / 8;
    stop_loc  = (nStop * bitrate) / 8;
  }

  int id3size = m_tag.size();
  fseek(m_pFile, id3size,SEEK_SET);
  SeekNextHeader();
  id3size = ftell(m_pFile);

  int nInitPos = 0;

  if (bID3)
    copy_file_data(pNewFile, m_pFile, 0, id3size);

  nInitPos+=id3size;
  
  if (data.xing_header.bValidLame || data.xing_present)
  {
    const int nPos = ftell(m_pFile);

    //read mp3 header
    const int nHeader = ReadInt();
    mp3header hHeader;
    const int nFramesize = MakeHeader(nHeader,hHeader);

    if (bNewXing)
    {
      //read frame
      std::vector<uint8_t> pb(nFramesize);
      fseek(m_pFile, nPos, SEEK_SET);
      const std::size_t read = fread(&pb[0], 1, nFramesize, m_pFile);

      if (read != nFramesize)
        return false;

      const std::string buffer(reinterpret_cast<char*>(&pb[0]), nFramesize);
  
      std::size_t startpos = buffer.find("Xing");
      if (startpos==std::string::npos)
        startpos = buffer.find("Info");

      if (startpos!=std::string::npos)
      {
        if (data.xing_header.flags & FRAMES_FLAG)
        {
          const int new_frame_count = data.frameCount - (stop_pos - start_pos);
          const int old_frame_count = ReadInt(&pb[startpos + 8]);
          CreateI4(&pb[startpos + 8], new_frame_count);
        }

        if (data.xing_header.flags & BYTES_FLAG)
        {
          const int old_bytes_count = ReadInt(&pb[startpos + 12]);
          const int new_bytes_count = old_bytes_count - (stop_loc - start_loc);
          CreateI4(&pb[startpos + 12], new_bytes_count);
        }

        if (data.xing_header.bValidLame)
        {
          //adjust music length
          const int nLenOffset = startpos + 0x0C + 7 * 0x10 + 0x10 + 0x0E - 2 - 4;
          const int nOldLength = ExtractI4(&pb[nLenOffset]);
          const int nNewLength = nOldLength - (stop_loc - start_loc);
          CreateI4(&pb[nLenOffset] ,nNewLength);        

          //adjust tag CRC
          const int nCRCLen = startpos + 0x0C + 7 * 0x10 + 0x10 + 0x0E;
          uint16_t crc = 0;
    
          for (int i = 0x00; i<nCRCLen; ++i)
            crc = CRC_update_lookup(pb[i],crc);

          const int nOldCRC = ExtractI2(&pb[nCRCLen]);
          CreateI2(&pb[nCRCLen] ,crc);  
        }
      }

      fwrite(&pb[0], 1, nFramesize, pNewFile);
    }
    
    nInitPos += nFramesize;
  }


  if (!copy_file_data(pNewFile, m_pFile, start_loc, stop_loc - start_loc,pSink))
  {
    fclose(pNewFile);
    _tremove(temp_name.c_str());
    return false;
  }
  else
  {
    if (bID3 && data.bId3v1Tag)
    {
      CMp3File mp3new;
      mp3new.Open(temp_name.c_str(), _T("r+b"));
      mp3new.UpdateID3v1(data.id3v1tag, data.id3v1_track);
      mp3new.Close();
    }

    fclose(pNewFile);
    return true;
  }
}


//easy stuff

bool CMp3File::ProcessFrames(const bool bFull, mp3data &data_out, const int nFrameLimit, 
  info_lists *pInfoLists, const bool bDisableCache, CMp3File::sink *pHelp)
{
  MYASSERT(m_pFile);

  //Prelims...
  lstrcpyn(data_out.path, m_tsFname.c_str(), sizeof(data_out.path)/sizeof(data_out.path[0]));

  fseek(m_pFile, 0, SEEK_SET);

  const int last_frame_pos = SeekLastHeader(data_out.final10);
  data_out.complete2   = (last_frame_pos!=-1);

  fseek(m_pFile, 0, SEEK_SET);

//  int framelength_debug = GuessFrameLength();
  fseek(m_pFile, 0, SEEK_SET);

  data_out.all_read  = false;
  data_out.fsize    = m_nFilesize;

  m_lastBits = 0;

  if (m_nFilesize==0)
    return false;

  const int    id3v1pos  = GetID3v1(data_out);
  const bool  bHasID3v2  = GetID3v2(data_out);      //changes file pointer....
  const int num = m_tag.GetCueFrameNumber();
  data_out.cue    = (num!=-1);

  int nTestPos = ftell(m_pFile);
  SeekNextHeader();
  nTestPos = ftell(m_pFile);
  GetVBRTags(data_out);

  int nCurrentPos = ftell(m_pFile);

  const tstring label = GetLabels(data_out.xing_header);
  if (!label.empty())
    lstrcpyn(data_out.label,label.c_str(), sizeof(data_out.label)/sizeof(data_out.label[0]));
  else
    data_out.label[0] = 0;

  fseek(m_pFile, nCurrentPos, SEEK_SET);

  //locate first header
  if (SeekNextHeader()==-1)
    throw tstring(_T("Cannot find valid mp3 header."));

  const int pos = ftell(m_pFile);

  if (!data_out.xing_present)
    data_out.first_frame_pos = pos;

  //read first header
  const int nFirstHeader    = ReadInt();
  mp3header hFirstHeader;
  const int nFirstFramesize    = MakeHeader(nFirstHeader,hFirstHeader);

  const int id = hFirstHeader.id;
  const int layer = hFirstHeader.layer;
  const int bitrate = tabsel_123[1-id][layer-1][hFirstHeader.bitrate_idx];
  data_out.bitrate = bitrate;

  const int64_t bits = m_nFilesize * 8;
  const int64_t kbits = bits / 1000;
  data_out.length = kbits / bitrate;

  if (!nFirstFramesize)
    throw tstring(_T("Hmm - I thought I'd found a header... ")
                  _T("If this is a valid mp3 file, please report this bug."));

  SetBaseInfo(data_out, hFirstHeader);
  data_out.frameCount = 0;

  if (bFull)
    return FullProcessFrames(data_out,nFrameLimit,pInfoLists,bDisableCache,id3v1pos, pos + nFirstFramesize, pHelp);
  else 
    return true;
}


bool CMp3File::FullProcessFrames(mp3data &data_out, const int nFrameLimit,info_lists *pInfoLists,
  const bool bDisableCache, const int id3v1pos, const int initpos, CMp3File::sink *pHelp)
{

  MYASSERT(m_pFile);

  //prepare for the main loop
  memset(data_out.bitrateCount,0,sizeof(data_out.bitrateCount));
  memset(data_out.modeCount,0,sizeof(data_out.modeCount));
  memset(data_out.blockCount,0,sizeof(data_out.blockCount));
  
  bool  bResync    = false;
  bool  bLastResync = false;

  int pos = initpos;
  /*pos+=nFirstFramesize;*/
  for (;;)
  {
    if (bResync) 
    {
      LOG(_T("Sync Error: ") + m_tsFname);
      bLastResync = true;
      
      if (SeekNextHeader()==-1)
        break;
      bResync = false;
    }
    else
    {
      if (bLastResync)
      {
        ++data_out.sync_errors;

        if (data_out.sync_errors < MAX_SYNC_ERRORS)
        {
          int64_t temp = (data_out.frameCount) * 576 *(data_out.id?2:1);
          temp*= 1000;
          temp /= data_out.base_freq; 

          data_out.sync_errors_pos[data_out.sync_errors-1] = temp; 
        }
      }

      bLastResync = false;

      fseek(m_pFile,pos, SEEK_SET);
      if (pInfoLists)
        pInfoLists->byte_location.push_back(pos);
    }

    if (pos==id3v1pos)
      break;

    if (CheckDone(data_out, pos))
      break;

    const int newpos = ProcessNextFrame(data_out, pInfoLists);
    if (newpos==-1)
      bResync = true;
    else
      pos = newpos;

    ++data_out.frameCount;

    if (pHelp && (data_out.frameCount % 2000) == 0)
    {
      if (!ReportProgress(data_out, pHelp))
        return true;
    }
    
    if (nFrameLimit!=-1 && data_out.frameCount >= nFrameLimit)
      break;
  }

  //loop finished. 
  if (nFrameLimit == -1 || data_out.frameCount < nFrameLimit)
    data_out.all_read = true;

  if (!ReportProgress(data_out, pHelp))
    return true;

  return true;
}



bool CMp3File::ReportProgress(mp3data &data, const CMp3File::sink *pHelp)
{
  if (data.all_read)
    data.length      = data.frameCount*576.0*(data.id?2:1)/data.base_freq;       
  else  //estimate
  {
    const float average_bitrate = GetAverageBitrate(data);
    float fLength = static_cast<float>(data.fsize);
    fLength = (fLength * 8.0)/(1000 * average_bitrate);
    data.length = fLength;
  }

  data.bitrate = GetAverageBitrate(data);

  //is the file a vbr file?
  DetectVBR(data);

  if (pHelp)
    return pHelp->Fire(data);

  return true;
}

int CMp3File::ProcessNextFrame(mp3data &data_out, info_lists *pInfoLists)
{
  const int nStartPos = ftell(m_pFile);
  int nBlockType = 0;
  int nBigValues;
  bool bScaleFac = false;

  //read next header
  const int nHeader = ReadInt();
  mp3header hHeader;
  const int nFramesize = MakeHeader(nHeader,hHeader);

  if (!nFramesize)
    return -1;  

  const tstring err = ReportOutOfSyncError(data_out, hHeader);
  if (!err.empty())
  {
   // printf("%s  at %d(%d%%), frame number %d\n",err.c_str(), pos,pos*100/out_data.fsize,framenum);
    return -1;
  }

  const int current_reservoir = UpdateInfoFromHeader(data_out, hHeader);
  
  for(int gr=0; gr<((1 - hHeader.id)?1:2); ++gr)
  {
    for(int ch=0; ch<hHeader.channels; ++ch)
    {
      readBits(12);          // part2_3_length
      nBigValues = readBits(9);    // big_values
      readBits(8);          // global_gain
      if (hHeader.id == 1) 
        readBits(4);        // scalefac_compress
      else 
        readBits(9);        // scalefac_compress
      int window_switching_flag = readBits(1);
      if (window_switching_flag == 1) 
      {
        int block_type = readBits(2);          // block_type
        int mixed_block_flag = readBits(1);        // mixed_block_flag
        for(int region=0;region<2;region++) readBits(5);  // table_select
        for(int window=0;window<3;window++) readBits(3);  // subblock_gain
        if (block_type == 2) 
        {
          if (mixed_block_flag) 
          {
            nBlockType = 2;      //mixed
            data_out.blockCount[2]++;
          }
          else
          {
            nBlockType = 1;      //short
            data_out.blockCount[1]++;
          }
        } 
        else 
        {
          nBlockType = 0;        //long
          data_out.blockCount[0]++;
        }
      } //window switching
      else 
      {
        for(int region=0;region<3;region++) readBits(5); // table_select
        readBits(4); // region0_count
        readBits(3); // region1_count
        data_out.blockCount[0]++;
      }
      if (hHeader.id == 1) readBits(1); // preflag
      if (readBits(1))
      {
        bScaleFac = true;
        data_out.scalefac++;
      }
      readBits(1); // count1table_select
    }//channel
  }//granules

  ++data_out.bitrateCount[hHeader.bitrate_idx];
  if (hHeader.mode == 1) 
    ++data_out.modeCount[hHeader.modeex];
  else 
    ++data_out.modeCount[0];

  // other_data.crcCount+=hHeader.protection;

  if (pInfoLists)
  {
    pInfoLists->scalefac.push_back(bScaleFac);
    if (nBlockType == 1)
      pInfoLists->bigvalues.push_back(nBigValues);
    else
      pInfoLists->bigvalues.push_back(0);
    
    pInfoLists->bitlist.push_back(hHeader.bitrate_idx);
    pInfoLists->modelist.push_back(hHeader.modeex);
    pInfoLists->blocklist.push_back(nBlockType);
    pInfoLists->reslist.push_back(current_reservoir);
  }
  
  const int framesize = tabsel_123[1 - hHeader.id][hHeader.layer-1][hHeader.bitrate_idx]*144000/(hHeader.freq<<(1 - hHeader.id)) + 
    hHeader.padding;
  
  return (nStartPos + framesize);
}


//occurs once per frame
int CMp3File::UpdateInfoFromHeader(mp3data &out_data, const mp3header &hHeader)
{
  m_nLastBits = 0;
  if (!hHeader.protection) readBits(16);    //!protection =>16 bit CRC follows header
  if (hHeader.padding) out_data.padding = 1;

  int main_data_begin = 0;
  if (hHeader.id) 
  {
    main_data_begin = readBits(9);
    if (main_data_begin > out_data.max_reservoir) 
      out_data.max_reservoir = main_data_begin;
  } 
  else 
  {
    main_data_begin = readBits(8);
    if (main_data_begin > out_data.max_reservoir) 
      out_data.max_reservoir = main_data_begin;
  }

  out_data.total_reservoir+=main_data_begin;
  if (out_data.frameCount >= 0 )
    out_data.av_reservoir = out_data.total_reservoir / (out_data.frameCount+1);

  if (hHeader.id) 
  {
    int test;
    if (hHeader.mode==3) 
      test = readBits(5); // private_bits
    else 
      test = readBits(3); // private_bits
  } 
  else 
  {
    if (hHeader.mode==3) readBits(1); // private_bits
    else readBits(2); // private_bits
  }

  if (hHeader.id == 1) 
  {
    int ch,scfsi_band;
    for(ch=0;ch<hHeader.channels;ch++)
      for(scfsi_band=0;scfsi_band<4;scfsi_band++)
      out_data.scfsi = readBits(1) || out_data.scfsi;
  }

  return main_data_begin;
}

void CMp3File::SetBaseInfo(mp3data &data, const mp3header &header)
{
  data.mode    = header.mode;
  data.nch    = header.channels;
//  data.base_lsf  = 1 - header.id;  //change this...
  data.id      = header.id;
  data.idex    = header.idex;
  data.layer    = header.layer;
  data.base_freq  = header.freq;

  if (header.bitrate_idx == 0)
    throw tstring(_T("This file appears to be a free format bitstream. ")
                  _T("Sorry, free format bitstreams are not supported."));

  data.copyright  = header.copyright > 0;
  data.original  = header.original > 0;
  data.emphasis  = header.emphasis;
}

//returns position of id3v1 tag if present, otherwise -1.
//also fills out id3tag structure
int CMp3File::GetID3v1(mp3data &data)
{
  data.id3v1tag.genre = 0xFF;    //unknown at present.

  int id3v1pos = -1;
  unsigned char c1,c2,c3;

  fseek(m_pFile,-128,SEEK_END);
  c1 = getc(m_pFile);
  c2 = getc(m_pFile);
  c3 = getc(m_pFile);
  if (c1 == 'T' && c2 == 'A' && c3 == 'G') 
  {
    
    fseek(m_pFile,-128,SEEK_END);
    const int read = fread(&data.id3v1tag,1,sizeof(id3v1),m_pFile);
    if (read==128)
    {
      data.bId3v1Tag = true;
      id3v1pos = m_nFilesize - 128;

      //track
      if (data.id3v1tag.comment[29]!=0 && data.id3v1tag.comment[29]!=32)
        data.id3v1_track = data.id3v1tag.comment[29];
    }
  }
  
  fseek(m_pFile,0,SEEK_SET);
  return id3v1pos;
}

bool CMp3File::GetID3v2(mp3data &data)
{
  fseek(m_pFile,0,SEEK_SET);
  unsigned char c1,c2,c3,c4;

  c1 = getc(m_pFile);
  c2 = getc(m_pFile);
  c3 = getc(m_pFile);


  if (c1 == 'I' && c2 == 'D' && c3 == '3') 
  {
    fseek(m_pFile,6,SEEK_SET);
    c1 = getc(m_pFile);
    c2 = getc(m_pFile);
    c3 = getc(m_pFile);
    c4 = getc(m_pFile);
    fseek(m_pFile,c1*2097152+c2*16384+c3*128+c4,SEEK_CUR);
    data.bId3v2Tag = true;
    return true;
  }
  else
  {
    data.bId3v2Tag = false;
    fseek(m_pFile,0,SEEK_SET);
    return false;
  }
}


bool CMp3File::GetVBRTags(mp3data &data)
{
  const long posn = ftell(m_pFile);

  std::vector<uint8_t> buff(2000);
  const std::size_t read = fread(&buff[0],1,buff.size(),m_pFile);

  //xing.
  std::size_t pos = 0;
  while (  (pos + 4 <= read) && memcmp(&buff[pos], "Xing", 4) && memcmp(&buff[pos], "Info",4) )
    ++pos;

  if (pos + sizeof(data.xing_header) <= read)
  {
    fseek(m_pFile, posn, SEEK_SET);
    data.xing_present = GetXingHeader(data, &buff[0]) > 0;
    fseek(m_pFile, posn, SEEK_SET);
  }
  else
  {
    data.xing_present = false;
  }

  //vbri
  pos = 0;
  while (  (pos + 4 <= read) && memcmp(&buff[pos], "VBRI", 4) )
    ++pos;

  if (pos + sizeof(data.vbri_header) <= read)
  {
    fseek(m_pFile, posn, SEEK_SET);
    data.vbri_present = GetVBRIHeader(data, &buff[0]);
    fseek(m_pFile, posn, SEEK_SET);
  }
  else
  {
    data.vbri_present = false;
  }

  return true;
}

bool CMp3File::CheckDone(mp3data &data, const int pos)
{
  if (pos > data.fsize)    //pos is supposed to be the end of the last frame
  {                //so the last frame was truncated...
    return true;
  }
  else
  {
    // all done
    return pos  == data.fsize;
  }
}

tstring CMp3File::ReportOutOfSyncError(mp3data &main_data, const mp3header &hHeader)
{
  if (hHeader.layer != main_data.layer) 
  {
    return _T("Layer error");
  }

  if (hHeader.channels != main_data.nch) 
  {
    main_data.nch = hHeader.channels;
    return _T("Channel mode error"); 
  }

  if (hHeader.freq != main_data.base_freq) 
  {
    main_data.base_freq = hHeader.freq;
    return _T("Frequency error");
  }

  if (hHeader.bitrate_idx == 0) 
  {
    return _T("Freeformat frame");
  }

  return _T("");
}

bool CMp3File::DetectVBR(mp3data &data_out)
{
  bool vbr(false);
  int v1(0), v2(0);
  int i=1;
  while ((v1==0) && (i<16)){
    if (data_out.bitrateCount[i] != 0)
      v1=i;
    ++i;
  }

  while ((v2==0) && (i<16)){
    if (data_out.bitrateCount[i] != 0)
      v2=i;
    ++i;
  }

  if (v2!=0)
    vbr=true;
  data_out.vbr=vbr;

  return vbr;
}

tstring CMp3File::GetLabels(const XHEADDATA &xing)
{
  if (xing.encoder[0]!=0)
  {
    USES_CONVERSION;
    return TranslateLabel(A2T(xing.encoder));
  }

  fseek(m_pFile, 0, SEEK_SET);
  mp3data dummy;
  GetID3v2(dummy);

  std::vector<char> buff(1024);
  std::size_t count = fread(&buff[0], 1, buff.size(), m_pFile);
  const tstring ret = GetLabelsFromBuffer(&buff[0], count);

  if (!ret.empty())
    return ret;

  fseek(m_pFile,-1024,SEEK_END);
  count = fread(&buff[0], 1, buff.size(), m_pFile);
  fseek(m_pFile, 0, SEEK_SET);
  
  return GetLabelsFromBuffer(&buff[0], count);
}


tstring CMp3File::GetLabelsFromBuffer(const char *buff, const std::size_t len)
{
  USES_CONVERSION;

  const std::string b(buff,len);
  const std::size_t pos = b.find("LAME");

  const std::string protag = "RCA mp3PRO Encoder";
  const std::string protag2 = "THOMSON mp3PRO Encoder ";
  const std::size_t pos_pro = b.find(protag);
  const std::size_t pos_pro2 = b.find(protag2);

  tstring label;

  if ( (pos!=std::string::npos) && (b.size() - pos >= 11))
  {
    const std::string version(b.substr(pos+4, 4));
    const std::string tst(b.substr(pos+8, 3));
    TCHAR tag = _T(' ');
    if ((tst.size()>=3) && (tst[0]==' ') && (tst[1]=='('))
      tag = tst[2];

    label = _T("LAME")+ tstring(A2T(version.c_str()))+tag;
  }
  else if ( (pos_pro!=std::string::npos) && (b.size() - pos_pro >= 23))
  {
    const std::string version(b.substr(pos_pro+protag.size(), 5));
    label = _T("RCA ")+ tstring(A2T(version.c_str()));
  }
  else if ( (pos_pro2!=std::string::npos) && (b.size() - pos_pro2 >= 29))
  {
    const std::string version(b.substr(pos_pro2+protag2.size(), 6));
    label = _T("Thomson ")+ tstring(A2T(version.c_str()));
  }
  else if (b.find("VBRI")!=std::string::npos)
    label =  _T("VBRI");
  else if (b.find("MPGE")!=std::string::npos)
    label =  _T("MPGE");
  else if (b.find("GOGO")!=std::string::npos)
    label =  _T("GOGO");
  
  return TranslateLabel(label);
}

unsigned long int CMp3File::readBits(unsigned n)
{
  unsigned long int ret = 0;

  while (n>0)
  {
    if (n > m_nLastBits)
    {
      while(m_nLastBits+CHAR_BIT <= sizeof(m_lastBits)*CHAR_BIT)
      {
        const int tmp = getc(m_pFile);
        const unsigned char c = (tmp == EOF) ? 0 : static_cast<char>(tmp);
        m_lastBits = (m_lastBits << CHAR_BIT) | c;
        m_nLastBits += CHAR_BIT;
      }
    }

    const unsigned shift = (n < m_nLastBits) ? n : m_nLastBits;
    m_nLastBits -= shift;
    ret = (ret << shift) | ((m_lastBits >> m_nLastBits) & ((1 << shift) - 1));
    n -= shift;
  }

  return ret;
}

float CMp3File::GetAverageBitrate(const mp3data &data)
{
  if (data.xing_present)
  {
    const int frames  = data.xing_header.frames + 1;      //include the header frame: will be included in amount of data
    
    if (data.frameCount < frames && data.all_read)
    {
      //Corrupt Xing Header: reporting too many frames. Probably file is truncated.
    }
    else if (data.frameCount <= frames)
    {
      float fBitrate;
      if (frames==0)
        fBitrate = 0;
      else
      {
        const float length = static_cast<float>(frames)*576.0*(data.id?2:1)/data.base_freq;   
        const int music_data = data.fsize;
         // if (data.bId3v1Tag)
         //   music_data-=128;
        
        fBitrate = music_data * 8/(1000 * length);
      }

      if (fBitrate > 7)
        return fBitrate;
      
      //otherwise: file is probably truncated: there are _not_ correct number of frames in file.  
    }
    else
    {
      //Somehow header is reporting too _few_ frames. Maybe appended mp3s....
    }
  }

  float average = 0;
  int total = 0;
  for (int i = 1; i<16; ++i)
  {
    average += data.bitrateCount[i] * tabsel_123[1-data.id][data.layer-1][i];
    total   += data.bitrateCount[i];
  }

  if (total == 0)
    return 0;

  average/=total;
  return average;

}

int CMp3File::GuessFrameLength()
{
  SeekNextHeader();
  const int pos0 = ftell(m_pFile);
  fseek(m_pFile, pos0+1, SEEK_SET);
  SeekNextHeader();
  const int pos1 = ftell(m_pFile);

  return (pos1 - pos0);
}

bool CMp3File::IsMp3File(tstring fname)
{
  MYASSERT(m_pFile==NULL);

  m_pFile = _tfopen(fname.c_str(),_T("rb"));

  if (!m_pFile)
    return false;

  fseek(m_pFile,0,SEEK_END);
  m_nFilesize = ftell(m_pFile);
  fseek(m_pFile, m_nFilesize/2,SEEK_SET);

  const bool bResult = (SeekNextHeader()!=-1);  

  fclose(m_pFile);
  return bResult;
}

tstring CMp3File::TranslateLabel(const tstring &label)
{
  if (label.find(_T("LAME"))!=std::string::npos)
  {
    if (label.size() < 8)
      return _T("Lame");

    const tstring version = label.substr(4, 4);
    tstring ret = _T("Lame ") + version;
    TCHAR tag = label[8];
    if (tag==0)
      return ret;
    
    if (tag==_T('a'))
      ret+=_T(" (alpha)");
    else if (tag==_T('b'))
      ret+=_T(" (beta)");

    if (label.size() >= 11)
    {
      if (label[8]==_T(' ') && label[9]==_T('('))
      {
        tag = label[10];
        if (tag==_T('a'))
          ret+=_T(" (alpha)");
        else if (tag==_T('b'))
          ret+=_T(" (beta)");
      }
    }

    return ret;
  }

  if (label.find(_T("VBRI"))!=std::string::npos)
    return _T("FhG (fastenc)");

  if (label.find(_T("MPGE"))!=std::string::npos)
    return _T("Gogo (before 3.0)");

  if (label.find(_T("GOGO"))!=std::string::npos)
    return _T("Gogo (after 3.0)");

  if (label.find(_T("RCA"))!=std::string::npos)
    return label;

  if (label.find(_T("Thomson"))!=std::string::npos)
    return label;

  return _T("");
}

//returns a 16 bit int
int CMp3File::GetMusicCRC(mp3data &data, const CMp3File::sink_mini *pHelp)
{
  MYASSERT(m_pFile);
  
  XHEADDATA *X = &data.xing_header;
  const int nInitPos = data.first_frame_pos;
  uint16_t music_crc = 0;

  //compute music crc.
  //first: how long is this frame??
  fseek(m_pFile, data.first_frame_pos,SEEK_SET);
  uint8_t b[4];
  std::size_t read = fread(b,1,4,m_pFile);
  if (read != 4) 
    return 0;

  int nHead = ReadInt(b);

  mp3header header;
  X->nFirstFrameSize = MakeHeader(nHead,header);
  if (!X->nFirstFrameSize)
    return 0;

  fseek(m_pFile, X->nFirstFrameSize + nInitPos,SEEK_SET);
  read = fread(b,1,4,m_pFile);              //check we're really at another header
  if (read != 4) 
    return 0;

  nHead = ReadInt(b);

  if (!MakeHeader(nHead,header))
    return 0;

  fseek(m_pFile, X->nFirstFrameSize + nInitPos,SEEK_SET);

  int64_t nToGo = m_nFilesize;
  
  nToGo -= (X->nFirstFrameSize + nInitPos);
  if (data.bId3v1Tag)
    nToGo-=128;

  const int64_t nTotal = nToGo;
  std::vector<char> buffer(1000000);
  while (nToGo > 0)
  {
    read = fread(&buffer[0], 1, buffer.size(), m_pFile);
    if (read == 0)
      return 0;

    for (std::size_t count = 0; count < read; ++count)
    {
      music_crc = CRC_update_lookup(buffer[count],music_crc);
      nToGo--;

      if (nToGo==0)
        break;
    }

    if (pHelp)
      pHelp->Fire(nTotal - nToGo, nTotal);
  }

  if (music_crc == X->music_crc)
    X->bValidMusic = true;

  data.xing_header.music_crc_calculated = true;
  return music_crc;
}

int CMp3File::GetXingHeader(mp3data &data, unsigned char *buf)
{
  data.first_frame_pos = ftell(m_pFile);
  
  XHEADDATA *X = &data.xing_header;

  int i, head_flags;
  int h_id, h_mode, h_sr_index;
  static int sr_table[4] = { 44100, 48000, 32000, 99999 };

  unsigned char *buf_start = buf;

  // get Xing header data
  X->flags = 0;     // clear to null incase fail

  // get selected MPEG header data
  h_id       = (buf[1] >> 3) & 1;
  h_sr_index = (buf[2] >> 2) & 3;
  h_mode     = (buf[3] >> 6) & 3;

  // determine offset of header
  if( h_id ) {        // mpeg1
    if( h_mode != 3 ) buf+=(32+4);
    else              buf+=(17+4);
  }
  else 
  {      
    // mpeg2
    if( h_mode != 3 ) buf+=(17+4);
    else              buf+=(9+4);
  }

  char label[5];
  memcpy(label, buf, 4);
  label[4] = 0;

  if (strcmp(label,"Xing") && strcmp(label, "Info"))
    return 0;

  buf+=4;

  X->h_id = h_id;
  X->samprate = sr_table[h_sr_index];
  if( h_id == 0 ) X->samprate >>= 1;

  head_flags = X->flags = ExtractI4(buf); buf+=4;      // get flags

  if( head_flags & FRAMES_FLAG ) {X->frames   = ExtractI4(buf); buf+=4;}
  if( head_flags & BYTES_FLAG )  {X->bytes = ExtractI4(buf); buf+=4;}

  if( head_flags & TOC_FLAG ) {
    if( X->toc != NULL ) {
      for(i=0;i<100;i++) X->toc[i] = buf[i];
    }
    buf+=100;
  }

  X->vbr_scale = -1;
  if( head_flags & VBR_SCALE_FLAG )  {X->vbr_scale = ExtractI4(buf); buf+=4;}

  memcpy(X->encoder, buf, 4); 
  X->encoder[4] = 0;
  if (strcmp(X->encoder, "LAME")==0)
  {
    memcpy(X->encoder, buf, 11); 
    X->encoder[11] = 0;

    //lame specific stuff
    buf+= 9;      //e.g. LAME3.90a
    X->tag_revision = *buf >> 4;
    X->vbr_method   = *buf % 0x10;
    buf+=1;

    if (X->vbr_method > 5)
      X->vbr_method = 0;      //corrupt...but allow it for debug purposes.

    X->lowpass = *buf * 100;
    buf+=1;

    memcpy(X->replay_gain, buf, 8);
    buf+=8;

    X->encoding_flags = *buf >> 4;
    X->ath_type       = *buf % 0x10;
    buf+=1;

    X->abr_bitrate = *buf;
    buf+=1;

    memcpy(X->encoder_delays, buf, 3);
    buf+=3;

    const int nMisc = *buf;
    X->noise_shaping = nMisc % 4;
    X->stereo_mode   = (nMisc >> 2) % 8;
    X->unwise     = ((nMisc >> 5) % 2) != 0;
    X->input_freq   = (nMisc >> 6) % 4;
    buf+=1;

    memcpy(X->unused, buf, 3);
    buf+=3;

    X->music_length = ExtractI4(buf);
    buf+=4;

    X->music_crc = ExtractI2(&buf[0]);
    buf+=2;

    uint16_t crc = 0;
    int nFrameLength = buf - buf_start;

    for (int i = 0x00; i<nFrameLength; ++i)
      crc = CRC_update_lookup(buf_start[i],crc);

    X->tag_crc = ExtractI2(&buf[0]);
    buf+=2;

    if (X->tag_crc==crc)
    {
      X->encoder[9]=0;
      X->bValidLame = true;
    }
  }
  else
  {
    buf+=4;
  }

  return 1;       // success
}

bool CMp3File::GetVBRIHeader(mp3data &data, unsigned char *buf)
{
  VBRIDATA *X = &data.vbri_header;

//  int i, head_flags;
  int h_id, h_mode, h_sr_index;

  // get Xing header data

  // get selected MPEG header data
  h_id       = (buf[1] >> 3) & 1;
  h_sr_index = (buf[2] >> 2) & 3;
  h_mode     = (buf[3] >> 6) & 3;

  // determine offset of header
  if( h_id ) {        // mpeg1
    if( h_mode != 3 ) buf+=(32+4);
    else              buf+=(17+4);
  }
  else {      // mpeg2
    if( h_mode != 3 ) buf+=(17+4);
    else              buf+=(9+4);
  }

  if( buf[0] != 'V' ) return 0;    // fail
  if( buf[1] != 'B' ) return 0;    // header not found
  if( buf[2] != 'R' ) return 0;
  if( buf[3] != 'I' ) return 0;
  buf+=4;

  X->unknown1 = ExtractI2(&buf[0]);
  buf+=2;

  X->unknown2 = ExtractI2(&buf[0]);
  buf+=2;

  X->vbr_scale = ExtractI2(&buf[0]);
  buf+=2;

  X->bitstream_bytes = ExtractI4(&buf[0]);
  buf+=4;

  X->frames   = ExtractI4(&buf[0]);
  buf+=4;

  X->toc_size = ExtractI2(&buf[0]);
  buf+=2;

  X->unknown3 = ExtractI2(&buf[0]);
  buf+=2;

  X->unknown4 = ExtractI2(&buf[0]);
  buf+=2;

  X->toc_mult = ExtractI2(&buf[0]);
  buf+=2;

  if (X->toc_size < MAX_TOC_SIZE)
  {
    for (int i = 0;i<X->toc_size;i++)
    {
      X->toc[i] = ExtractI2(&buf[0]);
      buf+=2;
    }
  }

  return true;
}

bool CMp3File::copy_file_data(FILE *new_file, FILE *old_file, const int nStart, const int nSize, CMp3File::sink_mini *pSink)
{
  const int nInit = ftell(old_file);
  fseek(old_file, nStart, SEEK_SET);
  const int BUFFSIZE = 500 * 1024;
  std::vector<uint8_t> buff(BUFFSIZE);

  int nRead = BUFFSIZE;
  int nTotal= 0;
  int nTotalWritten =0;
  while (nRead == BUFFSIZE)
  {
    int nToDo = BUFFSIZE;
    if (nSize!=-1)        nToDo = (nSize - nTotal);
    if (nToDo > BUFFSIZE) nToDo = BUFFSIZE;
    
    nRead = fread(&buff[0], 1, nToDo, old_file);
    nTotalWritten+=fwrite(&buff[0], 1, nRead, new_file);

    nTotal+=nRead;
    if (nSize!=-1 && nTotal >= nSize)
      break;

    if (pSink && pSink->Fire(nTotal, nSize)==false)
      return false;
  }

  fseek(old_file, nInit, SEEK_SET);
  return true;
}

void CMp3File::CreateI4(uint8_t *buf, const int nValue) const
{
  /* big endian create */
  buf[0]=(nValue>>24)&0xff;
  buf[1]=(nValue>>16)&0xff;
  buf[2]=(nValue>> 8)&0xff;
  buf[3]=(nValue    )&0xff;
}

void CMp3File::CreateI2(uint8_t *buf, const int nValue) const
{
  /* big endian create */
  buf[0]=(nValue>> 8)&0xff;
  buf[1]=(nValue    )&0xff;
}

//must be open for reading and writing
bool CMp3File::UpdateID3v1(id3v1 tag, const int track)
{
  MYASSERT(m_pFile);
  
  mp3data data;
  const int pos = GetID3v1(data);
  fseek(m_pFile, 0, SEEK_SET);    //flush

  if (pos==-1)
  {
    /*m_pFile = fopen(m_tsFname.c_str(), "wb");*/
    const int succ = fseek(m_pFile, 0, SEEK_END);
  }
  else
  {
    fseek(m_pFile, pos, SEEK_SET);
  }

  if (track!=0) tag.comment[29] = track;
  const int written = fwrite(&tag, 1, sizeof(id3v1), m_pFile);

  return true;
}

bool CMp3File::AppendMP3(mp3data datain1, const tstring &path2, const tstring &pathout, 
  const bool bID1, const bool bID2, CMp3File::sink_mini *pHelp)
{
  MYASSERT(m_pFile);
  if (pathout.empty() || path2.empty()) return false;

  id3v1 tag;
  int track = 0;
  if (bID1)
  {
    track = datain1.id3v1_track;
    tag = datain1.id3v1tag;
  }

  //first we must read some data from the second file....
  //e.g. must find first frame, etc.
  //NB at the moment will use the Lame Tag header / Xing header. This is bad....

  CMp3File mp3Second;
  if (!mp3Second.Open(path2))
    return false;

  mp3data data2;
  memset(&data2, 0, sizeof(mp3data));
  mp3Second.ProcessFrames(true, data2, 100, NULL, false, NULL);
  mp3Second.Close();

  if (bID2)
  {
    track = data2.id3v1_track;
    tag = data2.id3v1tag;
  }

  const int bit1 = datain1.bitrate;
  const int bit2 = data2.bitrate;

  if (bit1!=bit2)
  {
    const TCHAR* mesg = _T("These two files have different bitrates. The resulting file will therefore\n")
                        _T("be a VBR file. It should play correctly, but it's length may be displayed\n ")
                        _T("incorrectly by some players.");
    MessageBox(GetActiveWindow(), mesg, _T("FYI"), MB_ICONINFORMATION);
  }

  FILE *pNewFile = _tfopen(pathout.c_str(), _T("wb"));
  int total1 = m_nFilesize;
  if (datain1.bId3v1Tag)
    total1-=128;

  //new Frankenstein file should not have a xing or lame header....

  int start1 = 0;
  if (datain1.xing_present)
  {
    start1 = datain1.first_frame_pos;
    fseek(m_pFile, start1 + 4, SEEK_SET);
    SeekNextHeader();
    start1 = ftell(m_pFile);
    total1-=start1;
  }

  //copy all the first data into the new file
  
  copy_file_data(pNewFile, m_pFile, start1, total1, pHelp);

  //second file

  FILE *pSecond = _tfopen(path2.c_str(), _T("rb"));

  int todo = mp3Second.m_nFilesize;
  todo-=data2.first_frame_pos;
  todo-=128;

  copy_file_data(pNewFile, pSecond, data2.first_frame_pos, todo, pHelp);

  fclose(pSecond);
  fclose(pNewFile);

  //deal with id3 tag.
  if (bID1 || bID2)
  {
  
    CMp3File mp3new;
    if (!mp3new.Open(pathout, _T("r+b")))
      return false;

    mp3new.UpdateID3v1(tag,track);
  }

  return true;
}
