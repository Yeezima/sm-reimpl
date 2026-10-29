#!/bin/sh
# sm-reimpl dedicated server (UDP, default port 27015). Reads your Space Marine install (read only), never writes to it.
# Set SM_GAME_DIR to the folder that holds "data" (or pass --game-dir <folder>). Options: --port --map --mode --bind 127.0.0.1 ...
# SM_DATA_DIR moves the private cache (default ~/.local/share/sm-reimpl); it must lie outside the game folder.
dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd) || exit 1
[ -x "$dir/SmServer" ] || chmod +x "$dir/SmServer" 2>/dev/null
exec "$dir/SmServer" "$@"
