#ifndef ENCSPOT_ID3TAG_H
#define ENCSPOT_ID3TAG_H

#include <cstdlib>
#include <string>
#include <vector>
#include "Callbacks.hpp"
#include "Common.hpp"

class CID3Tag  
{
private:
  static uint32_t Read7Int(const uint8_t *pBuff);
  static uint32_t ReadInt(const uint8_t *pBuff);

  static void WriteInt(uint8_t *pBuff, uint32_t nVal);
  static void Write7Int(uint8_t *pBuff, uint32_t nVal);

  struct CFrame
  {
    std::string buffer;
    char id[5];

    uint8_t m_flags_high;
    uint8_t m_flags_low;

    CFrame();
    int size() const;
    int Load(const uint8_t *pFrame, const uint32_t nMaxSize);
    void Save(uint8_t *pBuff) const;
  };

  friend struct CFrame;

  tstring m_sFname;
  uint8_t    m_nVersionMajor;
  uint8_t    m_nVersionRevision;
  uint8_t    m_nFlags;
  uint32_t   m_nSize;

  bool m_bExtendedHeader;

  std::vector<CFrame> m_frames;

public:
  int size() const;

  bool    m_bExists;

  bool    CreateNewSpace(int nSpace, iProgCallback *pCallback = NULL);
  bool    GetTextFrame(const std::string &szID, std::string &data) const;
  int     GetFrameNumber(const std::string &id) const;
  void    MakeTextFrame(const std::string& szID, const std::string &data);
  bool    RemoveTagFromFile();
  void    MakeCueSheetFrame(const char *szCuesheet, const char *szVersion, const char *szInfo);
  int     GetCueFrameNumber(tstring *pString = NULL) const;
  bool    GetCueFrame(tstring &data) const;
  
  bool    Load(const TCHAR *szFname);
  bool    Save();
  CID3Tag();
  virtual ~CID3Tag();
};

#endif
