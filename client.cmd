@echo off
rem Builds and runs the Godot client (downloads Godot 4.7.2 .NET on first use). Read-only against the game.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\client.ps1" %*
