@echo off
rem Starts the Godot client for tests and agents with no window on the owner's desktop and no focus change: godot.cmd -- --connect 127.0.0.1:27181 --bot ; godot.cmd --windowed -- ... --screenshot x.png ; godot.cmd --help
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\godot.ps1" %*
