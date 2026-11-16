@echo off
rem Opens the Mapmaker (docs/mapmaker.md, Phase 1) in the Godot 4.7.2 editor on your desktop. Builds the plugin first. Agents and tests use godot.cmd instead (headless, or --windowed on the hidden desktop).
setlocal
set "ROOT=%~dp0"
set "GODOT=%LOCALAPPDATA%\sm-reimpl\tools\godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe"
set "GODOTDONE=%LOCALAPPDATA%\sm-reimpl\tools\godot\Godot_v4.7.2-stable_mono_win64.complete"
set "NEEDGODOT="
if not exist "%GODOT%" set "NEEDGODOT=1"
rem The marker is written only after a full, verified extraction: without it a partial download is fetched again.
if not exist "%GODOTDONE%" set "NEEDGODOT=1"
if defined NEEDGODOT (
  echo Godot 4.7.2 is not installed yet: downloading it ^(about 117 MB, once^)...
  powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%tools\get-godot.ps1" || exit /b 1
)
if /i "%~1"=="--restart" (
  rem After a fix: build, then restart the open editor (it saves and reopens the map), or start one. tools\mapmaker-restart.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%tools\mapmaker-restart.ps1" %2 %3
  exit /b %ERRORLEVEL%
)
dotnet build "%ROOT%mapmaker\Mapmaker.csproj" -nologo -v quiet || exit /b 1
start "" "%GODOT%" --editor --path "%ROOT%mapmaker"

