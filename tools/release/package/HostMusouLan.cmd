@echo off
rem sm-reimpl: host Musou Mode for friends (co-op: up to 4 players; your network, or the internet with UDP port 27015 forwarded on your router).
rem The same server as "@HOST@" with --content musou, listening on every network interface. Options pass through, for example:  --port 27016
setlocal
set "SM_HOST_LAN=1"
set "SM_HOST_MUSOU=1"
call "%~dp0@HOST@" %*
