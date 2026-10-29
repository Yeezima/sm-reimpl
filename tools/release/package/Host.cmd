@echo off
rem sm-reimpl: host a match on this PC with the bundled dedicated server (server\SmServer.exe; UDP port 27015; Annihilation on Manufactorum).
rem Only this PC can join (no firewall prompt). "@HOSTLAN@" starts the same server for other PCs too.
rem Options pass through to the server, for example:  --mode seize     --port 27016
setlocal enabledelayedexpansion
set "HERE=%~dp0"
title sm-reimpl server
if not exist "%HERE%server\SmServer.exe" (
  echo The server was not found: server\SmServer.exe is missing next to this file. Extract the whole zip again, keeping its folders.
  pause
  exit /b 1
)
if not defined SM_GAME_DIR for /f "usebackq delims=" %%G in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%game\Launcher.ps1" -FindGame`) do set "SM_GAME_DIR=%%G"
if not defined SM_GAME_DIR echo Space Marine ^(Steam app 55150^) was not found here; the server will look once more. If it fails, set SM_GAME_DIR to the game folder ^(the one holding "data"^).
set "BIND=--bind 127.0.0.1"
rem Only SM_HOST_LAN=1 (set by the LAN script) listens on every interface.
if "%SM_HOST_LAN%"=="1" set "BIND="
echo.
if "%SM_HOST_LAN%"=="1" (
  echo LAN mode: this server listens on all network interfaces, so other PCs can join. Windows Firewall may ask once: allow it on private networks.
  echo Friends on your network join this PC's address with port 27015, for example the first of these:
  powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%game\Launcher.ps1" -ShowAddresses
  echo Over the internet: forward UDP port 27015 on your router to this PC and give friends your public address.
) else (
  echo Only this PC can join this server.
)
echo To play on it yourself: run @JOIN@ and press Enter ^(127.0.0.1:27015^),
echo or start @PLAY@ and join 127.0.0.1:27015.
echo Leave this window open while you play; close it to stop the server.
rem fix/hosting-bots-logs (the owner's decision): this launcher starts no bots. For bots, use @PLAY@: choose BOTS (No bots by default), then HOST A MATCH.
rem The UDP port in use (27015 unless --port is given), for the port message.
set "PORT=27015"
set "PREV="
for %%A in (%*) do (
  if /i "!PREV!"=="--port" set "PORT=%%~A"
  set "PREV=%%~A"
)
echo No bots join this server. To play against bots, close this window, start @PLAY@, pick a number at BOTS and press HOST A MATCH.
echo.
cd /d "%HERE%server"
"%HERE%server\SmServer.exe" --map mp_tdm_09 %BIND% %*
set "CODE=!ERRORLEVEL!"
echo.
if "!CODE!"=="3" (
  echo Port !PORT! is already in use on this PC ^(another server is running^). Close it or choose another port, for example:  --port 27016
) else (
  echo The server has stopped.
)
pause
