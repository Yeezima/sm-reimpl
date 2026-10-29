#!/bin/sh
# Archive inspection tool. Requires the .NET 10 SDK. POSIX twin of smtool.cmd.
dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd) || exit 1
command -v dotnet >/dev/null 2>&1 || { echo ".NET SDK not found. Install the .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0" >&2; exit 1; }
exec dotnet run --project "$dir/src/SmTool" -c Release -- "$@"
