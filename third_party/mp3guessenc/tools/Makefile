#
# Makefile
#

# common preferences
CC = gcc
CFLAGS += -Wall -Wextra -O2
LDFLAGS += -s

# no need to change anything below here
#--------------------------------------

SRC0 = strencode.c
OBJ0 = strencode.o

all: $(OBJ0) ../decode.o strencode
make: all

strencode: $(OBJ0) ../decode.o
strencode.o: strencode.c

clean:
	rm -f $(OBJ0) strencode
	
distclean: clean	

