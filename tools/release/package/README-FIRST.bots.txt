sm-reimpl tester build @VERSION@  (headless bot client for a Linux dedicated server)
================================================================================

WHAT THIS IS
  An unofficial fan project: an independent reimplementation of the multiplayer of Warhammer 40,000: Space Marine (2011).
  It is NOT affiliated with, endorsed by or supported by Relic Entertainment, SEGA or Games Workshop.
  It is free software (GPL-3.0). This package contains NO game files: no models, textures, sounds, maps or data.
  It holds the Godot game client built for headless use (no window, no sound) and the "smbots" script that starts it as bots.

WHAT YOU NEED
  - A Linux dedicated server of the SAME build (sm-reimpl-server-linux-x64-@VERSION@.tar.gz, protocol @PROTOCOL@) on the same machine,
    started with --honor-side-requests so the sides stay as the bots ask, and the game install it reads (for example /srv/sm1,
    read only): the bots read it too, at run time.
  - A 64-bit Linux (glibc) with systemd, root access, and about 300 MB of memory per bot (smbots refuses to start more bots than fit).
  - The bots and the server must be of the same build: other versions are refused on purpose.

INSTALL (as root; setup-smserver.sh --bots does all of this)
  mkdir -p /opt/smbots && tar xzf sm-reimpl-bots-linux-x64-@VERSION@.tar.gz -C /opt/smbots
  ln -sfn /opt/smbots/sm-reimpl-bots-linux-x64-@VERSION@ /opt/smbots/current
  install -m 0755 /opt/smbots/current/smbots /usr/local/bin/smbots
  useradd --system --home /opt/smserver --shell /usr/sbin/nologin smserver   (once; the server's user)
  chown -R smserver:smserver /opt/smbots

USE
  smbots add 2 2                      2 Space Marine bots and 2 Chaos bots join 127.0.0.1:27015
  smbots add 3 3 --classes tactical,assault --accuracy 0.1
  smbots list                         running bots with their uptime and memory
  smbots remove 2 | smbots remove all
  smbots stop                         stops every bot smbots started (and nothing else)
  Bots run as transient systemd units (smbot-<id>), as the user smserver, at a lower priority than the server; they survive the SSH
  logout. Logs: /opt/smbots/log/bot-<id>.log. Run "smbots help" for every option.

THIS IS A TEST BUILD
  Expect bugs. Please report what you find at https://github.com/Yeezima/sm-reimpl-releases (Issues).

SOURCE CODE (GPL-3.0)
  The complete corresponding source of this build is sm-reimpl-source-@VERSION@.zip, built with this package; it is also
  inside the playtest zip of the same build (source\ folder) at https://github.com/Yeezima/sm-reimpl-releases (Releases).
  Licences: LICENSE (GPL-3.0), THIRD_PARTY_NOTICES.md (.NET and others) and licenses/godot (the engine).
