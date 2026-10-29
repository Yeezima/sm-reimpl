@echo off
rem Faithful build (docs/added-content.md): the solution and the Godot client built with -p:SmExtras=false (no Extras/ folder is compiled,
rem SM_EXTRAS is undefined, only --content faithful runs), then the tests of that build (extra arguments go to dotnet test, e.g. --filter).
rem Outputs go to the git-ignored .faithful-build\ folder of the repository (the tests find the repository from their output), so the usual
rem (extended) bin\ and obj\ builds stay as they are.
setlocal
where dotnet >nul 2>nul || (echo .NET SDK not found. Install with: winget install Microsoft.DotNet.SDK.10 & exit /b 1)
set "OUT=%~dp0.faithful-build"
echo faithful build: solution
dotnet build "%~dp0SmReimpl.slnx" -nologo -v quiet -p:SmExtras=false --artifacts-path "%OUT%" || (echo faithful build: the solution failed to build & exit /b 1)
echo faithful build: client
dotnet build "%~dp0client\SmClient.csproj" -nologo -v quiet -p:SmExtras=false --artifacts-path "%OUT%" || (echo faithful build: the client failed to build & exit /b 1)
echo faithful build: tests
dotnet test "%~dp0SmReimpl.slnx" -nologo -p:SmExtras=false --artifacts-path "%OUT%" %* || (echo faithful build: tests failed & exit /b 1)
echo faithful build: the solution and the client build without added content, and the tests pass.
