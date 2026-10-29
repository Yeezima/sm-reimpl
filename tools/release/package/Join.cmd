@echo off
rem sm-reimpl: join a server directly. Asks for its address (host or host:port; the default port is 27015; just Enter = this PC).
setlocal
set "HERE=%~dp0"
if not exist "%HERE%game\sm-reimpl.exe" (
  echo The game was not found: game\sm-reimpl.exe is missing next to this file. Extract the whole zip again, keeping its folders.
  pause
  exit /b 1
)
if not defined SM_GAME_DIR for /f "usebackq delims=" %%G in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%game\Launcher.ps1" -FindGame`) do set "SM_GAME_DIR=%%G"
if not defined SM_GAME_DIR echo Space Marine ^(Steam app 55150^) was not found here; the game will look once more ^(run @PLAY@ once to choose the folder^).
set "ADDR="
for /f "usebackq delims=" %%A in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%game\Launcher.ps1" -AskAddress`) do set "ADDR=%%A"
if not defined ADDR (
  echo No server address given.
  pause
  exit /b 1
)
echo Joining %ADDR% ...
start "" /D "%HERE%game" "%HERE%game\sm-reimpl.exe" %SM_ENGINE_ARGS% -- --connect %ADDR% %*
rem fix/hosting-bots-logs: an optional hidden helper notes a game that closes within 10 seconds with an error, and where its log is.
start "" /min powershell -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "%HERE%game\Launcher.ps1" -Watch -Exe "%HERE%game\sm-reimpl.exe"
