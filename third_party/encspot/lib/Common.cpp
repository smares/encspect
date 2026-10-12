#include <sstream>
#include <encspot/Common.hpp>

tstring itoa10(const int value)
{
  std::basic_ostringstream<TCHAR> stream;
  stream << value;
  return stream.str();
}
