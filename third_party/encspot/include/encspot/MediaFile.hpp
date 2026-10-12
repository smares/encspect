#ifndef ENCSPOT_MEDIAFILE_H
#define ENCSPOT_MEDIAFILE_H

#include <cstdio>
#include "Common.hpp"

class CMediaFile  
{
protected:
  FILE *  m_pFile;
  tstring m_tsFname; 
  int     m_nFilesize;

public:
  bool    Open(const tstring &fname, const tstring &mode = _T("rb"));    
  void    Close();
  CMediaFile();
  virtual ~CMediaFile();
};

#endif
