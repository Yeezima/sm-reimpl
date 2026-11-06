@echo off
rem sm-reimpl: host Musou Mode on this PC (co-op: up to 4 players together against the Orks; the bundled server, UDP port 27015, this PC only).
rem The same server as "@HOST@" with --content musou. "@MUSOULAN@" lets other PCs join. Options pass through, for example:  --port 27016
setlocal
set "SM_HOST_MUSOU=1"
call "%~dp0@HOST@" %*
