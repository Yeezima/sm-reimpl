@echo off
rem Records the Godot client with Movie Maker at a chosen size (--movie-size WxH --write-movie file.avi --fixed-fps 30); see tools\movie.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\movie.ps1" %*
