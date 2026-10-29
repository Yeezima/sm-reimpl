@echo off
rem sm-reimpl: start the game. The front end: the Join screen (server address, side), "Host a match", CUSTOMIZE (class creator and armour).
rem The game is in game\, the bundled server in server\ (the Host a match button starts it).
setlocal
set "HERE=%~dp0"
if not exist "%HERE%game\sm-reimpl.exe" (
  echo The game was not found: game\sm-reimpl.exe is missing next to this file. Extract the whole zip again, keeping its folders.
  pause
  exit /b 1
)
if not defined SM_GAME_DIR for /f "usebackq delims=" %%G in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%game\Launcher.ps1" -FindGame`) do set "SM_GAME_DIR=%%G"
rem Not found here: the game looks once more and, if it still cannot find Space Marine, asks for the folder (Browse).
rem SM_ENGINE_ARGS (empty unless set; test aid of the release smoke test): engine options before "--", such as --headless.
start "" /D "%HERE%game" "%HERE%game\sm-reimpl.exe" %SM_ENGINE_ARGS% -- --server-exe "%HERE%server\SmServer.exe" %*
rem fix/hosting-bots-logs: an optional hidden helper notes a game that closes within 10 seconds with an error, and where its log is (blocked PowerShell: no note, the game still runs).
start "" /min powershell -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "%HERE%game\Launcher.ps1" -Watch -Exe "%HERE%game\sm-reimpl.exe"
