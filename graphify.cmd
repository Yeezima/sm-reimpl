@echo off
rem Graphify code graph (dev tool): setup, index, serve (MCP over stdio), explain|affected|path|query. See docs/commands.md.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\graphify.ps1" %*
exit /b %ERRORLEVEL%
