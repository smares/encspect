# Third-party sources

Tools the fixture scripts build to cross-check the analyzer. They are committed rather than downloaded or installed, so the checks keep working when the original downloads disappear. The analyzer does not use them.

| Directory | What | Origin | Licence |
|---|---|---|---|
| `minimp3` | `minimp3.h`, a single-header MP3 decoder by lieff | [lieff/minimp3](https://github.com/lieff/minimp3) at commit `ea99364f61c14656440e8d77e9c233ccf3124633`, SHA-256 `57e437c5c1f0e8b243885d3929c8973b5e6c778451e0100ab4251d19915cb3ad` | CC0 1.0 (`minimp3/LICENSE`) |
| `getid3` | getID3 by James Heinrich: `getid3.php`, `getid3.lib.php`, `module.audio.mp3.php` and the ID3v1, ID3v2, APE and Lyrics3 tag modules, unchanged | [JamesHeinrich/getID3](https://github.com/JamesHeinrich/getID3) at commit `9ee5678f1cfb68f6ccc9efc08511c61cbdb78b5a` | Used under the LGPL 3.0, one of the licences it offers (`getid3/license.txt`, `getid3/licenses/`) |
| `encspot` | EncSpot Console 2.01 by GuerillaSoft, as ported to Linux by Francis Russell (CMake build, buffer overflow fixes, the scalefactor regression fix of 2011-03-07), unchanged | `encspot-2.01+mtn20110729.tar.bz2` from [unchartedbackwaters.co.uk](https://www.unchartedbackwaters.co.uk/pyblosxom/static/encspot) (`https://www.unchartedbackwaters.co.uk/files/encspot/encspot-2.01+mtn20110729.tar.bz2`), SHA-256 `6b1be223393f94b5646f26c9da34c8b6addc2b0c64244b7e976a22b43a252014`. `debian/` is the port's own packaging, with its copyright file. | BSD 3-clause ("New BSD", `encspot/LICENSE`) |
| `mp3guessenc` | mp3guessenc 0.27.6 beta by Elio Blanca and others | The Ubuntu and Debian source package `0.27.6~beta+dfsg-1`: `mp3guessenc_0.27.6~beta+dfsg.orig.tar.gz` (SHA-256 `e6da8a7b629dcacdf9770592bacf3017e6aece68a3cca60d887bcde3d7b786a4`) with the patches of `mp3guessenc_0.27.6~beta+dfsg-1.debian.tar.xz` (SHA-256 `3056e3b7bdb7bd2dea934d6f5229a36b514b644b2a8dbcab3ac2c4ad141cf43a`) applied, which only change the Makefile and drop the build date from `-V`. `debian/` keeps those patches and Debian's copyright file. | LGPL 2.1 or later (`mp3guessenc/COPYING`) |

`scripts/fixtures/minimp3_tool.py`, `scripts/fixtures/guessenc_tool.py` and `scripts/fixtures/encspot_tool.py` build them; `scripts/fixtures/getid3_tool.py` runs getID3 with PHP. Change nothing in these directories except to update a tool, and then update this table.
