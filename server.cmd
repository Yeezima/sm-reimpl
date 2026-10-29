@echo off
rem Headless dedicated server (UDP, default port 27015; --map <name> or none). Reads the game install, never writes to it.
dotnet run --project "%~dp0src\SmServer" -c Release -- %*
