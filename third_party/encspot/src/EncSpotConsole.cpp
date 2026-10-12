#include <vector>
#include <string>
#include <cstdio>
#include <cstring>
#include <encspot/Common.hpp>
#include <encspot/Mp3File.hpp>

int _tmain(int argc, TCHAR* argv[])
{
  std::vector<tstring> files;
  for (int i=1; i<argc; ++i)
    files.push_back(argv[i]);

  for (unsigned i=0; i<files.size(); ++i)
  {
    const tstring& item = files[i];
    CMp3File mp3;

    if (mp3.Open(item))
    {
      try
      {
        mp3data data;
        memset(&data,0,sizeof(data));
        mp3data_string data_string;
        data_string.path = item;

        mp3.ProcessFrames(true, data, -1, NULL, true /*DisableCache*/, NULL);
        data.quality = data_string.update(data, false);
        const tstring out_string = data_string.get_report(data, _T("EncSpot Console 2.0"));
        _tprintf((out_string + _T("\n\n\n\n")).c_str());
      }
      catch (const tstring& s)
      {
        _ftprintf(stderr, _T("An error occured during scanning...\n"));
        _ftprintf(stderr, _T("File: %s\n"), item.c_str());
        _ftprintf(stderr, _T("Description: %s\n\n"), s.c_str());
      }
    }
    else
    {
      _ftprintf(stderr, _T("File not found: %s\n\n"), item.c_str());
    }
  }

  return EXIT_SUCCESS;
}
