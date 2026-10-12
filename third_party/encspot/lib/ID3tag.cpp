#include <cstring>
#include <cstdio>
#include <string>
#include <vector>
#include <encspot/Common.hpp>
#include <encspot/ID3tag.hpp>

CID3Tag::CFrame::CFrame() : m_flags_high(0), m_flags_low(0)
{
  memset(id, 0, sizeof(id));
}

int CID3Tag::CFrame::size() const 
{ 
  return 10+buffer.size(); 
}

int CID3Tag::CFrame::Load(const uint8_t *pFrame, const uint32_t nMaxSize)
{
  memcpy(id, pFrame, 4);
  id[4] = 0;

  if (id[0]==0)
    return -1;

  pFrame+=4;
  const uint32_t size = ReadInt(pFrame);

  //limit to 100KB to help spot errors...
  if (size > nMaxSize)
    return -1;

  pFrame+=4;
  m_flags_high = pFrame[0];
  m_flags_low  = pFrame[1];
  buffer.assign(reinterpret_cast<const char *>(pFrame)+2, size);
  
  return (size + 10);    
}

void CID3Tag::CFrame::Save(uint8_t *pBuff) const
{
  memcpy(pBuff, id, 4);
  pBuff+=4;
  WriteInt(pBuff, buffer.size());
  pBuff+=4;
  pBuff[0] = m_flags_high;
  pBuff[1] = m_flags_low;
  pBuff+=2;
  memcpy(pBuff, buffer.data(), buffer.size());
}

CID3Tag::CID3Tag() :
                     m_nVersionMajor(3),
                     m_nVersionRevision(0),
                     m_nFlags(0),
                     m_nSize(0),
                     m_bExtendedHeader(false),
                     m_bExists(false)
{
}

CID3Tag::~CID3Tag()
{
}

//open and parse
bool CID3Tag::Load(const TCHAR *szFname)
{
  m_bExists = false;
  
  const int MAX_TAG_SIZE = 100000;
  m_sFname = szFname;

  m_frames.clear();
  m_nSize = 0;

  if (m_sFname.empty())
    return false;

  FILE *const pFile = _tfopen(m_sFname.c_str(), _T("rb"));
  if (pFile==NULL)
    return false;

  //assume id3 tag less than MAX_TAG_SIZE (excluding padding).
  std::vector<uint8_t> buff(MAX_TAG_SIZE);
  const std::size_t read = fread(&buff[0], 1, buff.size(), pFile);
  fclose(pFile);

  
  if (read >= 10 &&
      buff[0] == 'I' &&
      buff[1] == 'D' &&
      buff[2] == '3')
  {
    //id tag exists...
    m_bExists = true;
    m_nVersionMajor = buff[3];
    m_nVersionRevision = buff[4];
    m_nFlags = buff[5];
    m_bExtendedHeader = (m_nFlags >> 6) % 2;

    m_nSize = Read7Int(&buff[6]);

    // probably bad format if m_nSize too large...:)
    // in any case we're not gonna handle it.
    // Also bail out if extended header flag is set, but there is not 
    // enough header data.
    if (m_nSize > read || (m_bExtendedHeader && read < 16))
    {
      m_nSize = 0;
      return false;
    }

    const uint8_t *pFrame = &buff[10];
    uint32_t exheadersize = 0;
    if (m_bExtendedHeader)
    {
      exheadersize = ReadInt(&buff[10]);
      pFrame+=exheadersize;
    }

    if (exheadersize + 10 > m_nSize || exheadersize < 6)
      return false;

    const uint32_t frameslength = m_nSize - exheadersize;

    CFrame frame;
    uint32_t total = 0;
    for(;;)
    {
      const int offset = frame.Load(pFrame, frameslength);
      pFrame+=offset;
      total+=offset;

      if (offset==-1)
        break;

      m_frames.push_back(frame);

      if (total >= frameslength)
        break;
    }
    
    return true;
  }
  else
  {
    return false;
  }
}

bool CID3Tag::Save()
{
  const int total_size = m_nSize+10;
  std::vector<uint8_t> pBuff(total_size, 0);
  
  uint8_t *pBuffPtr = &pBuff[0];
  memcpy(pBuffPtr, "ID3", 3);
  pBuffPtr+=3;

  pBuffPtr[0] = m_nVersionMajor;
  pBuffPtr[1] = m_nVersionRevision;

  pBuffPtr[2] = 0;  //no flags! :)

  pBuffPtr+=3;
  Write7Int(pBuffPtr, total_size - 10);
  pBuffPtr+=4;

  for (unsigned i=0; i<m_frames.size(); ++i)
  {
    m_frames[i].Save(pBuffPtr);
    pBuffPtr += m_frames[i].size();
  }

  FILE* const pFile = _tfopen(m_sFname.c_str(), _T("r+b"));

  if (pFile==NULL)
    return false;  

  fseek(pFile, 0, SEEK_SET);
  fwrite(&pBuff[0], 1, pBuff.size(), pFile);
  fclose(pFile);

  return true;
}


uint32_t CID3Tag::Read7Int(const uint8_t *pBuff)
{
  uint8_t size_a= *(pBuff + 0);
  uint8_t size_b= *(pBuff + 1);
  uint8_t size_c= *(pBuff + 2);
  uint8_t size_d= *(pBuff + 3);

  uint32_t ret = size_d + (size_c << 7) + (size_b << 14) + (size_a << 21);

  return ret;
}


uint32_t CID3Tag::ReadInt(const uint8_t *pBuff)
{
  uint8_t size_a= *(pBuff + 0);
  uint8_t size_b= *(pBuff + 1);
  uint8_t size_c= *(pBuff + 2);
  uint8_t size_d= *(pBuff + 3);

  uint32_t ret = size_d + (size_c << 8) + (size_b << 16) + (size_a << 24);

  return ret;
}


void CID3Tag::Write7Int(uint8_t *pBuff, const uint32_t nVal)
{
  *(pBuff + 3) = nVal % 128;
  *(pBuff + 2) = (nVal >> 7)  % 128;
  *(pBuff + 1) = (nVal >> 14) % 128;
  *(pBuff + 0) = (nVal >> 21) % 128;
}

void CID3Tag::WriteInt(uint8_t *pBuff, const uint32_t nVal)
{
  *(pBuff + 3) = nVal % 256;
  *(pBuff + 2) = (nVal >> 8)  % 256;
  *(pBuff + 1) = (nVal >> 16) % 256;
  *(pBuff + 0) = (nVal >> 24) % 256;
}

int CID3Tag::GetCueFrameNumber(tstring *const pString) const
{
  unsigned i;
  for (i = 0;i<m_frames.size();i++)
  {
    const std::string id = m_frames[i].id;
    if (id == "GEOB")
      break;
  }

  if (i==m_frames.size())
  {
    return -1;
  }
  else
  {
    if (pString)
      *pString = _T("GEOB");
  }
    
  const std::string contents = m_frames[i].buffer;
  if (contents.find("CUESHEET")!=std::string::npos)
    return i;
  else
    return -1;    
}

void CID3Tag::MakeCueSheetFrame(const char *szCuesheet, const char *szVersion, const char *szInfo)
{
  int frame_num = GetCueFrameNumber();
  
  if (frame_num==-1)
  {
    CFrame frame;
    strncpy(frame.id, "GEOB", sizeof(frame.id));
    m_frames.push_back(frame);
    frame_num = m_frames.size()-1;
  }

  CFrame &frame = m_frames[frame_num];

  std::string text;
  text+="CUESHEET\r\n"  + std::string(szCuesheet)+ "ENDCUESHEET";
  text+="CUEVERSION\r\n"  + std::string(szVersion)  + "ENDCUEVERSION";
  text+="CUEINFO\r\n"    + std::string(szInfo)  + "ENDCUEINFO";

  
  frame.buffer+='\0';  //encoding as ascii
  frame.buffer+="text"; //mime
  frame.buffer+='\0';
  frame.buffer+="mp3cue CueSheet";//filename
  frame.buffer+='\0';
  frame.buffer+='Q';  //descriptor
  frame.buffer += text;
}

bool CID3Tag::RemoveTagFromFile()
{
  if (m_nSize==0)
    return false;

  FILE *const pFile = _tfopen(m_sFname.c_str(), _T("r+b"));
  if (pFile==NULL)
    return false;  

  std::vector<uint8_t> pBuff(m_nSize+10);

  fseek(pFile, 0, SEEK_SET);
  fwrite(&pBuff[0], 1, pBuff.size(), pFile);
  fclose(pFile);

  return true;
}

bool CID3Tag::GetCueFrame(tstring &data) const
{
  const int i = GetCueFrameNumber();
  if (i==-1)
    return false;

  const CFrame &frame = m_frames[i];
  if (frame.buffer.size() < 20)
    data = _T("");
  else 
  {
    const char *szBuff = frame.buffer.c_str();
    if (*szBuff == 0 && *(szBuff + 1)==0)
    {
      USES_CONVERSION;
      data = A2T(szBuff+4);
    }
    else if (*szBuff==0x01)
    {
      MessageBox(GetActiveWindow(), _T("UNICODE not supported in this version."),0,0);
      return false;
    }
    else if (*szBuff==0x00)//not unicode
    {
      USES_CONVERSION;
      std::string mime = szBuff + 1;
      std::string filename = szBuff + 1 + mime.size()+1;
      data = A2T(szBuff + 1 + mime.size() + 1 + filename.size() + 1 + 1);
    }
    else //non conforming early version.
    {
      USES_CONVERSION;
      data = A2T(szBuff);
    }
  }  

  return true;
}

bool CID3Tag::GetTextFrame(const std::string &szID, std::string &data) const
{
  const int frame_num = GetFrameNumber(szID);

  if (frame_num==-1)
    return false;

  const CFrame &frame = m_frames[frame_num];

  data = frame.buffer.empty() ? "" : frame.buffer.c_str()+1;
  return true;
}


void CID3Tag::MakeTextFrame(const std::string& szID, const std::string &data)
{
  int frame_num = GetFrameNumber(szID);

  if (frame_num==-1)
  {
    CFrame frame;
    strncpy(frame.id, szID.c_str(), sizeof(frame.id));
    m_frames.push_back(frame);
    frame_num = m_frames.size()-1;
  }


  CFrame &frame = m_frames[frame_num];
  
  frame.buffer = "";
  frame.buffer+='\0';
  frame.buffer+=data;
}

int CID3Tag::GetFrameNumber(const std::string &id) const
{
  for (unsigned i = 0;i<m_frames.size();i++)
  {
    if (m_frames[i].id==id)
      return i;
  }

  return -1;
}

bool CID3Tag::CreateNewSpace(const int nSpace, iProgCallback *const pCallback)
{
  tstring tpath(m_sFname);
  tpath += _T("_tmp");
  FILE *const pTfile = _tfopen(tpath.c_str(), _T("wb"));

  if (pTfile == NULL) 
  {
    MessageBox(GetActiveWindow(), _T("Unable to open temp file for writing"),NULL,NULL);
    return false;
  }

  std::vector<uint8_t> pZero(nSpace, 0);
  fwrite(&pZero[0], 1, nSpace, pTfile);

  //1MB at a time
  const int nBlockSize = 1000000;
  std::vector<uint8_t> data(nBlockSize);
  FILE *const pOld = _tfopen(m_sFname.c_str(), _T("rb"));

  if (pOld == NULL)
  {
    MessageBox(GetActiveWindow(), _T("Unable to open file for reading"), NULL, NULL);
    return false;
  }

  fseek(pOld, 0, SEEK_END);
  const int nOldSize = ftell(pOld);
  fseek(pOld, 0, SEEK_SET);

  int nCount = 0;
  while (!feof(pOld))
  {
    const int read = fread(&data[0], 1, data.size(), pOld);
    fwrite(&data[0], 1, read, pTfile);

    ++nCount; 
    if (pCallback && nOldSize)
    {
      
      if (!pCallback->Report((100 * static_cast<float>(nBlockSize) * nCount)/nOldSize))
      {
        //aborted.
        fclose(pOld);
        fclose(pTfile);

        //delete temp file
        _tremove(tpath.c_str());
        return false;
      }
    }
  }

  fclose(pOld);
  fclose(pTfile);

  if (_tremove(m_sFname.c_str())!=0 || _trename(tpath.c_str(),m_sFname.c_str())!=0)
    MessageBox(GetActiveWindow(), _T("Failed to add new space to file for some reason..."),_T("Error"), 0);
  //FileOp(tpath, m_sFname.c_str(),FO_RENAME);

  //by adding this space we've increased the size of the 
  //tag.
  if (m_nSize==0)
    m_nSize = nSpace - 10;
  else
    m_nSize+=nSpace;

  return true;
}

int CID3Tag::size() const
{
  return m_nSize;
}
