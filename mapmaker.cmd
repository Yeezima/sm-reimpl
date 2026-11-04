@echo off
rem Opens the Mapmaker (docs/mapmaker.md, Phase 1) in the Godot 4.7.2 editor on your desktop. Builds the plugin first. Agents and tests use godot.cmd instead (headless, or --windowed on the hidden desktop).
setlocal
set "ROOT=%~dp0"
set "GODOT=%LOCALAPPDATA%\sm-reimpl\tools\godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe"
if not exist "%GODOT%" (
  echo Godot 4.7.2 was not found. Run client.cmd once; it downloads it.
  exit /b 1
)
dotnet build "%ROOT%mapmaker\Mapmaker.csproj" -nologo -v quiet || exit /b 1
start "" "%GODOT%" --editor --path "%ROOT%mapmaker"
