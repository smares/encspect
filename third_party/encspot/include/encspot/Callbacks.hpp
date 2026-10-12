#ifndef ENCSPOT_CALLBACKS_H
#define ENCSPOT_CALLBACKS_H

struct iProgCallback
{
  virtual bool Report(int nProg) = 0;
};

#endif
