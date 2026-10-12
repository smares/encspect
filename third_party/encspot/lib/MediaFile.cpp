#include <cstdio>
#include <encspot/Common.hpp>
#include <encspot/MediaFile.hpp>

CMediaFile::CMediaFile() : m_pFile(NULL), m_nFilesize(0)
{
}

CMediaFile::~CMediaFile()
{
  if (m_pFile)
    fclose(m_pFile);
}

bool CMediaFile::Open(const tstring &fname, const tstring &mode)
{
  m_tsFname = fname;

  //open for reading only
  m_pFile = _tfopen(fname.c_str(),mode.c_str());

  if (m_pFile)
  {
    fseek(m_pFile,0,SEEK_END);
    m_nFilesize = ftell(m_pFile);
    fseek(m_pFile,0,SEEK_SET);
  }
  
  return (m_pFile!=NULL);
}

void CMediaFile::Close()
{
  fclose(m_pFile);
  m_pFile = NULL;
}
