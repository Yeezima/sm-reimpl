sm-reimpl tester build @VERSION@  (dedicated server, @PLATFORM@)
================================================================

WHAT THIS IS
  An unofficial fan project: an independent reimplementation of the multiplayer of Warhammer 40,000: Space Marine (2011).
  It is NOT affiliated with, endorsed by or supported by Relic Entertainment, SEGA or Games Workshop.
  It is free software (GPL-3.0). This package contains NO game files: no models, textures, sounds, maps or data.

WHAT YOU NEED
  - A legal copy of Warhammer 40,000: Space Marine (2011) on Steam (app 55150), installed or copied to the server machine.
    The server reads your own installation at run time (maps, collision, weapon and class data). The installation is READ ONLY:
    nothing is ever written to it. Point SM_GAME_DIR (or --game-dir) at the folder that holds "data".
  - @REQUIREMENTS@

WINDOWS WARNINGS (Windows package only; the programs are not code-signed)
  Windows may show SmartScreen or block the downloaded files: before extracting, right-click the zip > Properties > "Unblock".

HOW TO RUN
  @RUN@
  Options: --port 27015  --map mp_tdm_09  --mode annihilation|seize  --bind 127.0.0.1 (this machine only)  --game-dir <folder>
  UDP port 27015 must be reachable by the players (open it in your firewall or router). Players need the sm-reimpl client of the
  SAME protocol version (see VERSION.txt); other versions are refused on purpose.
  The server keeps a private cache (navigation data, converted files) in SM_DATA_DIR (default @DATADIR@); it must lie outside the game folder.

THIS IS A TEST BUILD
  Expect bugs. Please report what you find at https://github.com/Yeezima/sm-reimpl-releases (Issues).

SOURCE CODE (GPL-3.0)
  The complete corresponding source of this build is sm-reimpl-source-@VERSION@.zip, built with this package; it is also
  inside the playtest zip of the same build (source\ folder) at https://github.com/Yeezima/sm-reimpl-releases (Releases).
  Licences: LICENSE (GPL-3.0) and THIRD_PARTY_NOTICES.md (.NET and others).

Verify the download: compare the SHA-256 of the archive with the one shown on the release page.
