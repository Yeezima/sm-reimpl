#!/bin/sh
# Headless dedicated server (UDP, default port 27015; --map <name> or none). Reads the game install, never writes to it.
# POSIX twin of server.cmd. Game folder: SM_GAME_DIR (see README, "Linux and containers"); --bind 127.0.0.1 for a local-only test server.
dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd) || exit 1
command -v dotnet >/dev/null 2>&1 || { echo ".NET SDK not found. Install the .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0" >&2; exit 1; }
exec dotnet run --project "$dir/src/SmServer" -c Release -- "$@"
