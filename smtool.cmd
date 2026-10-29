@echo off
rem Archive inspection tool. Requires the .NET 10 SDK.
where dotnet >nul 2>nul || (echo .NET SDK not found. Install with: winget install Microsoft.DotNet.SDK.10 & exit /b 1)
dotnet run --project "%~dp0src\SmTool" -c Release -- %*
