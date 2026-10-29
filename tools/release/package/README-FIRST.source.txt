sm-reimpl source @VERSION@
==========================

This is the complete source code of sm-reimpl tester build @VERSION@ (`git archive` of the commit named in VERSION.txt).
sm-reimpl is an unofficial, independent fan reimplementation of the multiplayer of Warhammer 40,000: Space Marine (2011),
GPL-3.0 (see LICENSE). It is NOT affiliated with Relic Entertainment, SEGA or Games Workshop. It contains no game files.

To run it you need a legal copy of Space Marine (2011) on Steam (app 55150); the engine reads your own installation at run
time, read only. Build with the .NET 10 SDK and Godot 4.7.2 .NET: the server with `dotnet publish src/SmServer -c Release`
(or `server.cmd` / `server.sh`), the client with `client.cmd`, which downloads Godot on first use and runs the project in `client/`.
This archive holds what builds and plays the game; the project's notes, tests and developer tools are left out of the source
zip to keep it small.
Binaries: https://github.com/Yeezima/sm-reimpl-releases (Releases).
