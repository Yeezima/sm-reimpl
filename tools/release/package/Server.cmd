@echo off
rem sm-reimpl dedicated server (UDP, default port 27015). Reads your Space Marine install, never writes to it.
rem Options:  --port 27015  --map mp_tdm_09  --mode annihilation|seize  --bind 127.0.0.1 (this PC only)  --game-dir <folder>
setlocal
cd /d "%~dp0"
if not defined SM_GAME_DIR for /f "usebackq delims=" %%G in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Launcher.ps1" -FindGame`) do set "SM_GAME_DIR=%%G"
"%~dp0SmServer.exe" %*
