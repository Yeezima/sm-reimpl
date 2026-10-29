@echo off
rem Runs dotnet test and smtool refine-register --check; when both pass on a clean tree, records it for the HEAD commit in
rem %LOCALAPPDATA%\sm-reimpl\tmp-build\tested\<sha>, so ship.cmd of that commit within 24 hours skips its own test run.
rem   test-and-mark.cmd -Check    only says whether HEAD has a valid marker. See docs\releasing.md.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\release\test-and-mark.ps1" %*
