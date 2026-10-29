@echo off
rem Runs every automatable M1 check (docs/roadmap.md). Read-only against the game; writes under %LOCALAPPDATA%\sm-reimpl.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\m1.ps1" %*
pause
