#include <algorithm>
#include <cstdio>
#include <vector>
#include <string>
#include <utility>
#include <cstring>
#include <encspot/Common.hpp>
#include <encspot/Mp3Structs.hpp>


int mp3data_string::get_quality(const mp3data &data, const bool monoquality)
{
  MYASSERT(!encoder.empty());

  const tstring enc = encoder;
  const int br = data.bitrate;

  std::vector<tstring> good,bad;
  good.push_back(_T("FhG"));
  good.push_back(_T("Lame 3.8"));
  good.push_back(_T("Lame 3.9"));
  good.push_back(_T("Gogo (after 3.0)"));
  bad.push_back(_T("Blade"));
  bad.push_back(_T("dist"));
  bad.push_back(_T("QDesign (fast mode)"));
  bad.push_back(_T("Shine"));
  bad.push_back(_T("Xing"));

  //good, bad or neutral.
  int test1 = 0;

  if (test1==0)
  {
    for (unsigned i = 0; i<good.size(); ++i)
    {
      if (enc.find(good[i])!=tstring::npos)
        test1 = 1;
    }
  }

  if (test1==0)
  {
    for (unsigned i = 0; i<bad.size(); ++i)
    {
      if (enc.find(bad[i])!=tstring::npos)
        test1 = -1;
    }
  }

  if (test1==0)
  {
    if (enc.find(_T("RCA"))!=tstring::npos || enc.find(_T("Thomson"))!=tstring::npos)
      test1 = 3;
  }

  if (test1==0)
  {
    if (enc.find(_T("mppenc"))!=tstring::npos)
      test1 = 2;
  }

  int bits[16];
  if (encoder.find(_T("mppenc"))==tstring::npos)
    memcpy(bits,tabsel_123[1-data.id][data.layer-1],sizeof(bits));
  else
    memcpy(bits,tabsel_123[0][2],sizeof(bits));


  //128,32,40,48, 56, 64, 80, 96,112,128,160,192,224,256,320

  double effective_bits = br;
  if (mode == _T("mono") &&  monoquality )
    effective_bits*=1.85;  //mono could be considered as "half bitrate-equal quality" against plain stereo, but only around 1.85 against joint stereo
  if (mode == _T("stereo"))
    effective_bits*=0.9;

  if (data.vbr)
    effective_bits*=1.2;

  if (effective_bits > bits[10 - test1] ) 
    return 2;
  else if (effective_bits > bits[8 - test1] )
    return 1;
  else
    return 0;
}

tstring mp3data_string::get_complete(const mp3data &data)
{
  // returns bad_last_frame
  if (!data.complete2)
    return _T("yes");
  else
    return _T("no");
}

int mp3data_string::get_final_sum(const mp3data &data)
{
  int ret = 0;
  for (int i = 0; i<10; ++i)
    ret+=data.final10[i];
  return ret;
}

const char *const genre_names[] =
{
    /*
     * NOTE: The spelling of these genre names is identical to those found in
     * Winamp and mp3info.
     */
    "Blues", "Classic Rock", "Country", "Dance", "Disco", "Funk", "Grunge",
    "Hip-Hop", "Jazz", "Metal", "New Age", "Oldies", "Other", "Pop", "R&B",
    "Rap", "Reggae", "Rock", "Techno", "Industrial", "Alternative", "Ska",
    "Death Metal", "Pranks", "Soundtrack", "Euro-Techno", "Ambient", "Trip-Hop",
    "Vocal", "Jazz+Funk", "Fusion", "Trance", "Classical", "Instrumental",
    "Acid", "House", "Game", "Sound Clip", "Gospel", "Noise", "Alt. Rock",
    "Bass", "Soul", "Punk", "Space", "Meditative", "Instrumental Pop",
    "Instrumental Rock", "Ethnic", "Gothic", "Darkwave", "Techno-Industrial",
    "Electronic", "Pop-Folk", "Eurodance", "Dream", "Southern Rock", "Comedy",
    "Cult", "Gangsta Rap", "Top 40", "Christian Rap", "Pop/Funk", "Jungle",
    "Native American", "Cabaret", "New Wave", "Psychedelic", "Rave",
    "Showtunes", "Trailer", "Lo-Fi", "Tribal", "Acid Punk", "Acid Jazz",
    "Polka", "Retro", "Musical", "Rock & Roll", "Hard Rock", "Folk",
    "Folk/Rock", "National Folk", "Swing", "Fast-Fusion", "Bebob", "Latin",
    "Revival", "Celtic", "Bluegrass", "Avantgarde", "Gothic Rock",
    "Progressive Rock", "Psychedelic Rock", "Symphonic Rock", "Slow Rock",
    "Big Band", "Chorus", "Easy Listening", "Acoustic", "Humour", "Speech",
    "Chanson", "Opera", "Chamber Music", "Sonata", "Symphony", "Booty Bass",
    "Primus", "Porn Groove", "Satire", "Slow Jam", "Club", "Tango", "Samba",
    "Folklore", "Ballad", "Power Ballad", "Rhythmic Soul", "Freestyle", "Duet",
    "Punk Rock", "Drum Solo", "A Cappella", "Euro-House", "Dance Hall",
    "Goa", "Drum & Bass", "Club-House", "Hardcore", "Terror", "Indie",
    "BritPop", "Negerpunk", "Polsk Punk", "Beat", "Christian Gangsta Rap",
    "Heavy Metal", "Black Metal", "Crossover", "Contemporary Christian",
    "Christian Rock", "Merengue", "Salsa", "Thrash Metal", "Anime", "JPop",
    "Synthpop"
};


const int genre_count = sizeof(genre_names);

void mp3data_string::update_id3v1(const mp3data &data)
{
  id3v1_album    = id3trim(&data.id3v1tag.album[0],30);
  id3v1_artist  = id3trim(&data.id3v1tag.artist[0],30);
  id3v1_comment  = id3trim(&data.id3v1tag.comment[0],30);
  id3v1_year    = id3trim(&data.id3v1tag.year[0],4);
  id3v1_title    = id3trim(&data.id3v1tag.title[0],30);

  if (data.bId3v1Tag)
  {
    USES_CONVERSION;
    id3v1_track = itoa10(data.id3v1_track);

    const unsigned nGen = data.id3v1tag.genre;
    if (nGen < (sizeof(genre_names)/sizeof(char *)))
      id3v1_genre = A2T(genre_names[nGen]);
    else
      id3v1_genre = _T("Unknown");
  }
}

tstring mp3data_string::id3trim(const char *szStr, const int nLen)
{
  int pos = nLen-1;
  while (pos >= 0 && ((szStr[pos]==' ') || szStr[pos]==0))
    pos--;

  if (pos==-1)
    return _T("");
  else
  {
    USES_CONVERSION;
    const tstring first = tstring(A2T(szStr), pos+1);  
    //return inital substring...
    return first;
  }
}

int mp3data_string::update_mpp(const mp3data &data, const bool monoquality)
{
  const tstring profs[] = {_T("Thumb"), _T("Radio"), _T("Standard"), _T("Xtreme"), _T("Insane") };

  mpp_stream_version = _T("SV");
  mpp_stream_version+=itoa10(data.mpp_stream_version);

  if (data.mpp_stream_profile <= 4)
    mpp_stream_profile = profs[data.mpp_stream_profile];
  else
    mpp_stream_profile = _T("Unknown");

  encoder = _T("mppenc");
  type  = _T("mpeg plus");

  const int quality  = get_quality(data, monoquality);
  if (quality==2)
    quality_str = _T("good");
  else if (quality==1)
    quality_str = _T("ok");
  else quality_str= _T("bad");

  vbr = data.vbr?_T("yes"):_T("no");

  cue = av_reservoir = bad_last_frame 
    = max_reservoir = scalefac = vbr_lame = sync_errors = _T("--");

  return quality;
}

void mp3data_string::update_lame(const mp3data &data)
{
  XHEADDATA xing = data.xing_header;
  const tstring vbr_methods[6] = {_T("Unknown"), _T("cbr"), _T("abr"), _T("vbr-old / vbr-rh"), _T("vbr-mtrh"), _T("vbr-new / vbr-mt") };
  const tstring modes[] = {_T("Mono"), _T("Stereo"), _T("Dual Stereo"), _T("Joint Stereo"), _T("Auto"), _T("Intensity"), _T("Unknown") };
  const tstring infreqs[]={_T("32kHz or less"), _T("44.1kHz"), _T("48kHz"), _T("more than 48kHz") };

  lame_vbr_scale      = itoa10(xing.vbr_scale);
  lame_tag_revision    = itoa10(xing.tag_revision);
  lame_vbr_method      = vbr_methods[xing.vbr_method];
  if (xing.lowpass)
    lame_lowpass    = itoa10(xing.lowpass);
  else
    lame_lowpass    = _T("Unknown");

  lame_nspsytune      = (xing.encoding_flags & 1)?_T("nspsytune"):_T("gpsycho");
  lame_nssafejoint    = (xing.encoding_flags & 2)?_T("yes"):_T("no");
  lame_nogapcontinued    = (xing.encoding_flags & 4)?_T("yes"):_T("no");
  lame_nogapcontinuation  = (xing.encoding_flags & 8)?_T("yes"):_T("no");

  lame_athtype      = itoa10(xing.ath_type);
  
  if (xing.abr_bitrate == 0xFF)
    lame_abr_bitrate  =  _T("255 or more");
  else if (xing.abr_bitrate)
    lame_abr_bitrate  = itoa10(xing.abr_bitrate);
  else
    lame_abr_bitrate  = _T("Unknown");

  lame_noise_shaping  = itoa10(xing.noise_shaping);
  lame_unwise         = xing.unwise?_T("yes"):_T("no");
  lame_input_freq     = infreqs[xing.input_freq];
  lame_stereo_mode    = modes[xing.stereo_mode];
}

int mp3data_string::update(const mp3data &data, bool monoquality)
{
  update_lame(data);
  length    = convert_time(data.length);

  //frames
  frames = itoa10(data.frameCount);
  if (!data.all_read)
    frames+=_T("+");

  //filesize
  fsize = itoa10(data.fsize);
  if (data.fsize > 999)
  {
    fsize.insert(fsize.size() - 3,_T(","));
    if (data.fsize > 999999)
      fsize.insert(fsize.size() - 7,_T(","));
  }

  mode    = get_mode(data.mode);
  bitrate = itoa10(data.bitrate);
  freq    = itoa10(data.base_freq);


  //mp+
  if (data.mpp_stream_version)
    return update_mpp(data,monoquality);
  
  mpp_stream_version = _T("--");
  mpp_stream_profile = _T("--");

  //id3v1
  update_id3v1(data);

  vbr = data.vbr?_T("yes"):_T("no");
  cue = data.cue?_T("yes"):_T("no");
  vbr_lame = data.xing_header.bValidLame?_T("yes"):_T("no");
  
  //type
  type = _T("mpeg ");
  type += data.id?_T("1"):_T("2");
  if (!data.idex)
    type+=_T(".5");
  type += _T(" ") + tstring(_T("layer ")) + tstring(data.layer,_T('I'));


  //scalefac
  
  if (data.scalefac == 0 || !data.frameCount)
  {
    scalefac = _T("not used");
  }
  else
  {
    const int grancount= data.id?2:1;
    const int nch = data.nch?data.nch:2;
    double scale = 0;

    scale = data.scalefac * 100.0;
    scale/= (grancount * nch * data.frameCount);
    TCHAR b[256];
    _sntprintf(b,256,_T("%.1f%%"), scale);
    scalefac = b;
  }
    
  emphasis = (data.emphasis == 0 ? _T("none") : data.emphasis == 1 ? _T("50/15ms") : _T("CCITT")); 
  encoder  = guessenc(data);

  //maybe mp3pro
  if (encoder.find(_T("RCA"))!=tstring::npos || encoder.find(_T("Thomson"))!=tstring::npos)
    type += _T(" (mp3pro)");

  //cut
  bad_last_frame = get_complete(data);

  max_reservoir  = itoa10(data.max_reservoir);
  av_reservoir  = itoa10(data.av_reservoir);
  sync_errors  = data.sync_errors > 0 ? itoa10(data.sync_errors) : _T("none");

  //uses data already calculated
  const int quality = get_quality(data, monoquality);

  if (quality==2)
    quality_str = _T("good");
  else if (quality==1)
    quality_str = _T("ok");
  else quality_str= _T("bad");

  return quality;
}

tstring mp3data_string::convert_time(const double secs)
{
  TCHAR b[16];
  int s = static_cast<int>(secs);

  int wSecond = s % 60;
  int wMinute = (s / 60) % 60;
  int wHour   = s / (60 * 60);
  _sntprintf(b,16,_T("%02i:%02i:%02i"),wHour,wMinute,wSecond);
  tstring ret(b);
  int milli = ((int)((secs - s) * 1000))/100; 
  if (s < 10)
    ret+=_T(".")+itoa10(milli);
  return ret;
}

tstring mp3data_string::get_mode(const int mode)
{
  switch (mode)
  {
  case MPG_MD_STEREO: 
    return _T("stereo");
    break;
  case MPG_MD_JOINT_STEREO:
    return _T("joint stereo");
    break;
  case MPG_MD_DUAL_CHANNEL:
    return _T("dual channel");
    break;
  case MPG_MD_MONO:
    return _T("mono");
    break;
  }

  return _T("");
}

tstring mp3data_string::guessenc(const mp3data &data)
{
  const tstring label = data.label;
  tstring guess = _T("Unknown");

  if (!label.empty())
    return label;

  //no identification string found
  if (data.blockCount[1] == 0)      //no short blocks 
  {
    if (data.mode == MPG_MD_DUAL_CHANNEL) // (note by GB: dual channel and no short: probably a very bad encoder ;-)
    {
      if (data.scfsi)  //scfsi used
      { }
      else
      {    //no scfsi
        if (data.scalefac > 0) 
        { }
        else //scalefactors not used
          return guess = _T("Shine");
      }
    }
    else
    {  //no dual channel
      if ( (data.modeCount[1]) || (data.modeCount[3]) )        //intensity stereo!
        return guess = _T("Xing (very old)");
      else
      {                //no intensity stereo
        if (data.scfsi)        //scfsi => new Xing
          return guess = _T("Xing (new)");
        else
        {              //no scfsi
          if (data.mode == MPG_MD_JOINT_STEREO)    //joint stereo: can't be Xing: prob FhG
          {
            if (data.modeCount[0])        //also includes some stereo frames
            {
              if (data.padding)
              {
                if (data.original)
                  return guess = _T("FhG (l3enc)");
                else
                  return guess = _T("FhG (fastenc or mp3enc)");
              }
              else  //no padding
                return guess = _T("FhG (ACM or producer pro)");
            }
            else  //no stereo frames: forced joint
            {
              if ((data.padding) && !(data.original) && !(data.copyright))
                return guess = _T("QDesign (fast mode)");
            }

          }
          else if (data.mode == MPG_MD_STEREO &&  data.scalefac==0 && !data.original)
            return guess = _T("Plugger");
          else
            return guess = _T("Xing (old)");
        }
      }
    }
  } 
  else  
  {           //short blocks
    if (data.scfsi)  //scfsi used
    {
      if (data.scalefac > 0) 
        return guess = _T("Gogo (after 3.0)");  //could be new Lame... but then there should be a label. So likely GOGO.
      else 
        return guess = _T("Lame (old) or m3e");
    } 
    else      //no scfsi
    {
      if (data.scalefac > 0) 
      {
        if (data.padding)
        {
          if (data.original)
          {
            //10 last bytes
            int sum = get_final_sum(data);
            if (sum==0)
              return guess = _T("FhG (fastenc, low quality mode)");
            else if (sum==10 * 0xFF)
              return guess = _T("FhG (l3enc)");
            else if (sum==5 * 0x20)
              return guess = _T("FhG (fastenc, medium or high quality mode)");
            else
              return guess = _T("FhG (l3enc or fastenc)");
          }
          else
          {
            if (data.mode == MPG_MD_JOINT_STEREO && data.modeCount[1] > 0)    //some intensity stereo!
              return guess = _T("Thomson mp3PRO Encoder");
            else
              return guess = _T("FhG (fastenc or mp3enc)");
          }
        }
        else  //no padding
        {
          if (data.vbr)
            return guess = _T("FhG (fastenc)");
          else
            return guess = _T("FhG (ACM or producer pro)");
        }
      } 
      else
      {  //scalefactors not used

        if (data.mode == MPG_MD_JOINT_STEREO)
        {
          if ((data.padding) && !(data.original) && !(data.copyright))
            return guess = _T("QDesign");
        }

        if (data.vbr)
          return guess = _T("Lame (old)");   //old Lame with no scfsi


        if (data.mode == MPG_MD_DUAL_CHANNEL)
        {
          if (data.padding)
            return guess = _T("Blade");
          else
            return guess = _T("dist10 encoder or other encoder");
        }
        //either mono or plain stereo
        
        
        //ISO based encoders are unable to properly use bit reservoir... average reservoir usage is about 10 
        if (data.av_reservoir < 40 && !data.vbr) 
        {
          if (data.padding)
              return guess = _T("Blade");
            else
              return guess = _T("dist10 encoder or other encoder");
        }
        else
          return guess = _T("Gogo (before 3.0)");         
      }
    }
  }

  return guess;
}

tstring mp3data_string::get_report(const mp3data &data, const tstring &version)
{
  const tstring tag = _T("--[ ") + version + _T(" ]--[ http://www.guerillasoft.com ]--");
  const int taglength = tag.size();

  tstring name = path; 
  const unsigned pos = path.rfind(_T('\\'));
  if (pos!=std::string::npos)
    name = path.substr(pos+1,std::string::npos);
  
  tstring out;
  out+=name + _T("\n");
  out+=tstring(name.size(),_T('-'))+_T("\n\n");
  
  out+= _T("Bitrates:\n");
  out+=tstring(taglength,_T('-'))+_T("\n");

  int max = 0;
  for (int i = 0;i<15;i++)
  {
    if (data.bitrateCount[i]>max)
      max = data.bitrateCount[i];
  }

  if (max == 0)
    max = 1;

  for (int i = 0; i<15; ++i)
  {
    const int brate_value = tabsel_123[1-data.id][data.layer-1][i];
    const int count    = data.bitrateCount[i];

    if (count>0)
    {
      tstring value = itoa10(brate_value);
      value = tstring(std::max<int>(3 - value.size(),0),_T(' ')) + value + _T("     ");   
      out+=value;

      const int num_bars = (40 * count)/ max;
      out+=tstring(num_bars,'|');
      out+=tstring(45 - num_bars,' ');

      TCHAR buff[256];
      _sntprintf(buff,256,_T("%.1f%%"),float(count * 100) / data.frameCount);
      tstring percent = buff;
      percent = tstring(std::max<int>(7 - percent.size(),0),_T(' ')) + percent;
      out+=percent;
      out+=_T("\n");
    }
  }

  out+=tstring(taglength,_T('-'))+_T("\n");
  out+=_T("\n");

  std::vector< std::pair<tstring,tstring> > data_list; 
  data_list.push_back(std::make_pair(_T("Type"),    type));
  data_list.push_back(std::make_pair(_T("Bitrate"),    bitrate));
  data_list.push_back(std::make_pair(_T("Mode"),      mode));
  data_list.push_back(std::make_pair(_T("Frequency"),    freq + _T(" Hz")));
  data_list.push_back(std::make_pair(_T("Frames"),      frames));
  data_list.push_back(std::make_pair(_T("Length"),      length));
  data_list.push_back(std::make_pair(_T("Av. Reservoir"),  av_reservoir));
  data_list.push_back(std::make_pair(_T("Max. Reservoir"),  max_reservoir));
  data_list.push_back(std::make_pair(_T("Emphasis"),    emphasis));
  data_list.push_back(std::make_pair(_T("Scalefac"),    scalefac));
  data_list.push_back(std::make_pair(_T("Sync Errors"),    sync_errors));
  data_list.push_back(std::make_pair(_T("Bad Last Frame"),  bad_last_frame));
  data_list.push_back(std::make_pair(_T("Encoder"),    encoder));

  for (unsigned i = 0; i<data_list.size(); ++i)
  {
    const tstring& name = data_list[i].first;
    const tstring& data = data_list[i].second;

    out+=name+tstring(std::max<int>(20-name.size(),0),_T(' ')) + _T(": ")+data + _T("\n"); 
  }

  out+= get_header_report(data);
  out+=_T("\n");  
  out+=tag+_T("\n");

  return out;
}

tstring mp3data_string::get_header_report(const mp3data &data)
{
  tstring out;
  if (!data.xing_present || !data.xing_header.bValidLame)
  {
    const tstring name = _T("Lame Header");
    const tstring data = _T("No");
    out+=name+tstring(std::max<int>(20-name.size(),0),_T(' ')) + _T(": ")+data + _T("\n"); 
    return out;
  }

  out+=_T("\nLame Header:\n\n");

  std::vector< std::pair<tstring, tstring> > data_list;
  data_list.push_back(std::make_pair(_T("Quality"),      lame_vbr_scale));
  data_list.push_back(std::make_pair(_T("Version String"),    encoder));
  data_list.push_back(std::make_pair(_T("Tag Revision"),    lame_tag_revision));
  data_list.push_back(std::make_pair(_T("VBR Method"),      lame_vbr_method));
  data_list.push_back(std::make_pair(_T("Lowpass Filter"),    lame_lowpass));
  data_list.push_back(std::make_pair(_T("Psycho-acoustic Model"),      lame_nspsytune));
  data_list.push_back(std::make_pair(_T("Safe Joint Stereo"),    lame_nssafejoint));
  data_list.push_back(std::make_pair(_T("nogap (continued)"),  lame_nogapcontinued));
  data_list.push_back(std::make_pair(_T("nogap (continuation)"),lame_nogapcontinuation));
  data_list.push_back(std::make_pair(_T("ATH Type"),      lame_athtype));
  data_list.push_back(std::make_pair(_T("ABR Bitrate"),    lame_abr_bitrate));
  data_list.push_back(std::make_pair(_T("Noise Shaping"),    lame_noise_shaping));
  data_list.push_back(std::make_pair(_T("Stereo Mode"),    lame_stereo_mode));
  data_list.push_back(std::make_pair(_T("Unwise Settings Used"),  lame_unwise));
  data_list.push_back(std::make_pair(_T("Input Frequency"),  lame_input_freq));
  
  for (unsigned i=0; i<data_list.size(); ++i)
  {
    const tstring& name = data_list[i].first;
    const tstring& data = data_list[i].second;
    out+=name+tstring(std::max<int>(30-name.size(),0),_T(' ')) + _T(": ")+data + _T("\n"); 
  }

  return out;
}
