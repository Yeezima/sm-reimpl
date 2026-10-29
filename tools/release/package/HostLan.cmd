@echo off
rem sm-reimpl: host a match that other PCs can join (your network, or the internet with UDP port 27015 forwarded on your router).
rem The same server as "@HOST@", listening on every network interface. Options pass through, for example:  --mode seize
setlocal
set "SM_HOST_LAN=1"
call "%~dp0@HOST@" %*
