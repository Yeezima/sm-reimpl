@echo off
rem Builds the tester release (the one playtest zip, plus the server packages and the source zip for server hosts, SHA256SUMS.txt, RELEASE-NOTES.md)
rem into %LOCALAPPDATA%\sm-reimpl\release\<version>\. Publishes nothing unless -Publish is passed.
rem First use downloads Godot's export templates (about 1.2 GB, SHA-512 checked). See tools\release\release.ps1 for the options.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\release\release.ps1" %*
