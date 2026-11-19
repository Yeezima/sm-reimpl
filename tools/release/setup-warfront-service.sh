#!/bin/sh
# Installs or updates the shared, always-on Warfront server as the systemd service `sm-warfront` on UDP 27016 (docs/dedicated-server.md,
# "The Warfront server"). Run as root on the Linux VM; safe to run again (it rewrites the unit and restarts the service).
#
#   sh setup-warfront-service.sh [options] [server-dir]
#
#   server-dir        a server package folder holding SmServer (as vm-build-server.sh + setup-smserver.sh lay it out). Default: the folder
#                     /opt/smserver/current points to, resolved NOW and written into the unit as that versioned path, so a later Classic
#                     update (which moves the symlink) never restarts Warfront onto another build by surprise. Update Warfront on purpose by
#                     running this script again.
#   --follow-current  write the symlink itself into the unit (Warfront then runs whatever Classic runs after its next restart; not advised)
#   --port N          UDP port (default 27016)          --game-dir D  the game install (default /srv/sm1)
#   --dry-run         print what would be done, change nothing
#   --rollback        stop and disable sm-warfront and start the old sm-musou service again (its unit file and data were kept)
#
# Never touches the `smserver` service or the `smbot-*` units. Re-running while players are in the campaign restarts the stage: the run is saved
# (SIGTERM saves) and continues from the last save, but the players are dropped; do it when nobody is in it.
set -eu

port=27016
game=/srv/sm1
dry=0
rollback=0
follow=0
dir=
while [ $# -gt 0 ]; do
  case "$1" in
    --dry-run) dry=1 ;;
    --rollback) rollback=1 ;;
    --follow-current) follow=1 ;;
    --port) shift; port="${1:?--port needs a number}" ;;
    --game-dir) shift; game="${1:?--game-dir needs a folder}" ;;
    -h|--help) sed -n '2,17p' "$0"; exit 0 ;;
    -*) echo "unknown option $1 (see --help)" >&2; exit 2 ;;
    *) dir="$1" ;;
  esac
  shift
done

unit=/etc/systemd/system/sm-warfront.service
state=/var/lib/sm-warfront
user=smwarfront

run() {
  if [ "$dry" = 1 ]; then echo "[dry-run] $*"; else "$@"; fi
}
have_unit() { systemctl cat "$1" >/dev/null 2>&1; }

if [ "$dry" = 0 ] && [ "$(id -u)" != 0 ]; then echo "run as root" >&2; exit 2; fi
if [ "$dry" = 0 ]; then command -v systemctl >/dev/null || { echo "systemd (systemctl) is missing" >&2; exit 2; }; fi

if [ "$rollback" = 1 ]; then
  echo "rollback: sm-warfront off, sm-musou on"
  if have_unit sm-warfront; then run systemctl disable --now sm-warfront; fi
  if have_unit sm-musou; then run systemctl enable --now sm-musou; else echo "sm-musou has no unit on this machine; nothing to start" >&2; fi
  echo "the Warfront run stays in $state (nothing deleted)"
  exit 0
fi

if [ -z "$dir" ]; then
  if [ "$follow" = 1 ]; then dir=/opt/smserver/current; else dir=$(readlink -f /opt/smserver/current 2>/dev/null || true); fi
  [ -n "$dir" ] || { echo "no server-dir given and /opt/smserver/current does not exist" >&2; exit 2; }
fi
if [ "$follow" = 0 ]; then dir=$(readlink -f "$dir" 2>/dev/null || echo "$dir"); fi
if [ "$dry" = 0 ]; then
  [ -x "$dir/SmServer" ] || { echo "$dir/SmServer is missing or not executable" >&2; exit 2; }
  [ -d "$game" ] || { echo "the game folder $game does not exist" >&2; exit 2; }
fi
echo "server: $dir/SmServer   game: $game   port: UDP $port   run folder: $state"

if ! id "$user" >/dev/null 2>&1; then
  nologin=/usr/sbin/nologin; [ -x "$nologin" ] || nologin=/sbin/nologin; [ -x "$nologin" ] || nologin=/bin/false
  run useradd --system --home-dir "$state" --no-create-home --shell "$nologin" "$user"
fi
run mkdir -p "$state" "$state/data"
run chown -R "$user:$user" "$state"
run chmod 0750 "$state"

content="[Unit]
Description=Space Marine reimplementation: shared Warfront campaign server (UDP $port)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$user
WorkingDirectory=$state
Environment=SM_WF_DIR=$state
Environment=SM_DATA_DIR=$state/data
ExecStart=$dir/SmServer --port $port --game-dir $game --warfront-dedicated --ban-file $state/bans.txt
Restart=on-failure
RestartSec=5
# exit codes a restart cannot fix (1 run or install unreadable, 2 command line, 3 port taken, 5 another supervisor holds the run folder)
RestartPreventExitStatus=1 2 3 5
KillMode=mixed
TimeoutStopSec=40

[Install]
WantedBy=multi-user.target"

if [ "$dry" = 1 ]; then
  echo "[dry-run] write $unit:"; echo "$content"
else
  printf '%s\n' "$content" > "$unit"
fi

# The old Musou service owns the same job on the same port; keep its unit and data for a rollback.
if [ "$dry" = 0 ] && have_unit sm-musou && { systemctl is-enabled --quiet sm-musou 2>/dev/null || systemctl is-active --quiet sm-musou 2>/dev/null; }; then
  echo "sm-musou found: disabling it (unit file and data are kept; roll back with: sh $0 --rollback)"
  run systemctl disable --now sm-musou
elif [ "$dry" = 1 ]; then
  echo "[dry-run] if sm-musou exists and is enabled or active: systemctl disable --now sm-musou (kept for --rollback)"
fi

run systemctl daemon-reload
run systemctl enable sm-warfront
if [ "$dry" = 0 ] && systemctl is-active --quiet sm-warfront 2>/dev/null; then run systemctl restart sm-warfront; else run systemctl start sm-warfront; fi

if [ "$dry" = 1 ]; then echo "[dry-run] would wait up to 60 s for 'smserver on UDP $port; protocol' in the journal"; exit 0; fi
since=$(date '+%Y-%m-%d %H:%M:%S')
i=0
while [ "$i" -lt 60 ]; do
  line=$(journalctl -u sm-warfront --since "$since" --no-pager -o cat 2>/dev/null | grep -m 1 "smserver on UDP $port; protocol" || true)
  if [ -n "$line" ]; then echo "ready: $line"; echo "logs: journalctl -u sm-warfront -f   status: systemctl status sm-warfront"; exit 0; fi
  # between restarts systemd reports activating; only a failed unit ends the wait early
  if systemctl is-failed --quiet sm-warfront 2>/dev/null; then break; fi
  sleep 1; i=$((i + 1))
done
echo "no ready line within 60 s: check 'journalctl -u sm-warfront -n 80 --no-pager'; roll back with: sh $0 --rollback" >&2
exit 1
