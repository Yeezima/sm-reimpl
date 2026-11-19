@echo off
rem sm-reimpl: host the Warfront on this PC (co-op campaign: up to 4 players together; the bundled server, UDP port 27015, this PC only).
rem The same server as "@HOST@" with --content warfront. "@WARFRONTLAN@" lets other PCs join. Options pass through, for example:  --port 27016
setlocal
set "SM_HOST_WARFRONT=1"
call "%~dp0@HOST@" %*
