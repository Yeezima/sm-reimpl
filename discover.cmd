@echo off
rem Read-only inventory of the original game installation. Writes the report under %LOCALAPPDATA%\sm-reimpl.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\discover.ps1" %*
pause
