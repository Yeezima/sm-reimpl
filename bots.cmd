@echo off
rem Starts headless test bots on a server and stops them again (M44a test aid): bots.cmd --connect 127.0.0.1:27141 --marines 6 --chaos 6 ; bots.cmd --stop ; bots.cmd --help
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\bots.ps1" %*
