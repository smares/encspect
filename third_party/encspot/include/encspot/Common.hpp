#ifndef ENCSPOT_COMMON_H
#define ENCSPOT_COMMON_H

#include <string>

#ifdef _WIN32

#define NOMINMAX
#define UNICODE
#define _UNICODE
#include <windows.h>
#include <tchar.h>
#include <atlconv.h>

typedef signed char   int8_t;
typedef SHORT         int16_t;
typedef INT32         int32_t;
typedef INT64         int64_t;

typedef unsigned char uint8_t;
typedef USHORT        uint16_t;
typedef UINT32        uint32_t;
typedef UINT64        uint64_t;

#else

#include <stdint.h>

typedef char TCHAR;

#define MAX_PATH          260
#define MessageBox(a,b,c,d) printf(b)

#define _T(x) x
#define USES_CONVERSION
#define A2T(x) x

#define lstrcpyn        strncpy
#define _tremove        remove
#define _trename        rename
#define _tfopen         fopen
#define _sntprintf      snprintf
#define _tprintf        printf
#define _ftprintf       fprintf
#define _tmain          main

#endif

#define MYASSERT(expr) 
#define LOG(expr) 

typedef std::basic_string<TCHAR> tstring;
tstring itoa10(int value);

#endif
