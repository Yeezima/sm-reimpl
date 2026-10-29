# Running the dedicated server

Operator notes for a public server (for example a tester demo). Gameplay rules are in `spec/behavior.md`; the values below are
ours and listed there under "Dedicated server hardening".

## Options

`server.cmd --help` (or `server.sh --help`) lists every option. Unknown options, options without their value and invalid values
stop the server with a one-line error and exit code 2.

| Option | Default | Meaning |
|---|---|---|
| `--port <1-65535>` | 27015 | UDP port |
| `--bind <ip>` | every interface | `127.0.0.1` for test runs (no firewall prompt) |
| `--game-dir <dir>` | located install | the game install, read-only |
| `--map <name\|none>` | `mp_tdm_09` | map, or the flat placeholder ground |
| `--mode annihilation\|seize\|ffa` | annihilation | match rules |
| `--score-limit`, `--time-limit`, `--friendly-fire`, `--countdown`, `--unlocks all`, `--no-custom-emblems` | | match and customizer options |
| `--pickup-test <s>` | off | test aid: test drops in front of every player |
| `--honor-side-requests` | off | test aid (M44a, ours): a joining client's requested side is kept up to 8 per side and the balance applies only to joins that name no side; bot fills with `bots.cmd` in uneven or 6v6 teams |
| `--max-per-ip <n>` | 4 | players from one IP address; 0 = no cap |
| `--join-interval <s>` | 5 | one new join per IP address this often; 0 = no limit |
| `--idle-kick <s>` | 300 | drop a player without movement, buttons or customizer use for this long while the match is played; 0 = never |
| `--ban-file <path>` | none | bans, re-read when the file changes |
| `--limit-loopback` | off | switch: hold loopback clients to the three limits above too |

### Exit codes

| Code | Meaning | Supervisor |
|---|---|---|
| 0 | `--help`, or stopped with Ctrl+C | stop |
| 1 | game install not found (the message names every way to point at it: `--game-dir`, `SM_GAME_DIR`, `config/local.json`), or a content problem (unknown map, Seize Ground on a map without points) | stop: fix the configuration |
| 2 | command-line error | stop: fix the command line |
| 3 | the UDP port is already in use, or `--bind` names an address this machine does not have (on Windows the socket is bound for exclusive use, so a second server on the same port fails even when one listens on `0.0.0.0` and the other on `127.0.0.1`) | stop: another server runs, or fix `--bind` |
| 4 | the simulation failed 60 ticks in a row (2 s); logged as `FATAL` | restart |
| other | a crash of the process (.NET unhandled exception, out of memory) | restart |

**Local bot playtests** (up to 12 clients on this machine) need nothing: loopback addresses (127.0.0.0/8, `::1`, and
`::ffff:127.0.0.1` on a dual-mode socket) are exempt from `--max-per-ip`, `--join-interval` and `--idle-kick`. `--limit-loopback`
(a switch) applies them to loopback too, for tests of the limits. Bans apply to loopback either way. A client that reaches the
server through the machine's LAN or public address is not loopback and is limited.

**Shared addresses (deployment).** Behind Docker Desktop's port forwarding, other userland UDP proxies, VPN or tunnel services (for
example a relay in front of a home server), the server may see every player at the same address (the proxy's, often a loopback or a
private one). Then the per-address cap and the join rate apply to everyone together: start the server with
`--max-per-ip 0 --join-interval 0`, or, when that shared address is loopback, it is already exempt (leave out `--limit-loopback`).
Bans by address do not work through such a proxy. Docker on Linux with the default bridge and `-p ...:.../udp` keeps the players'
real addresses.

**Idle kick.** Movement, any button, or using the class creator or appearance editor (each Deploy sends a Setup; an emblem upload)
counts as activity; turning the camera alone does not. Warm-up and round ends do not count. The protocol has no kick message: a
kicked player's client shows a lost connection, with no reason, after its server timeout (the operator log names the reason).

## Bans

One IP address or CIDR range per line; `#` starts a comment; blank lines are skipped. An address needs a dot (IPv4) or a colon
(IPv6): a bare number such as `12345` is not taken as an address. A line that is not an address or range is ignored and named in
the log.

```text
# repeated griefing, 2026-10-28
203.0.113.7
198.51.100.0/24
2001:db8::/32
```

The server checks the file every 2 s and reads it again when its time or size changes. Banned sources get no reply at all (not even
the handshake challenge); players already connected from a banned address are dropped. A missing file bans no one and is picked up
when it appears.

## Logs

Lines in brackets (UTC time) are the operator log: joins and leaves with `ip:port` and the reason (left, timed out, idle, banned,
replaced, dropped after a fault), refusals with what differed (the client's protocol, content fingerprint, config or map hash),
deferred joins, ban-file reloads, caught faults (the first 20 are logged, the first 5 of them with stack traces; then one line per
10 s with a count of those not logged), skipped ticks, and `FATAL` before exit code 4. The 10-second status line adds the fault,
skip, idle-kick, deferral and banned-packet counters once any is non-zero.

## Keep it running (restart supervisor)

Packet handlers, snapshots and ticks are isolated inside the server: one bad packet, one recipient's failing snapshot or a bug in one
tick is logged and the server goes on. A simulation that keeps failing (exit code 4) and a crash of the process itself are left to a
supervisor that restarts it. Codes 0 to 3 mean "stop": restarting would not help.

Docker (the `Dockerfile` at the repository root):

```sh
docker run -d --restart unless-stopped -p 27015:27015/udp -v /path/to/the/game/install:/game:ro smserver --map mp_tdm_09 --ban-file /data/bans.txt
```

(`--ban-file` under `/data` needs the `-v smdata:/data` volume; the image never holds game files.) Docker cannot skip exit codes:
`unless-stopped` also restarts after a configuration error (1, 2, 3), with growing delays; check `docker logs` when the container
keeps restarting.

A plain shell loop on Linux:

```sh
while :; do
  ./server.sh --map mp_tdm_09 "$@"; code=$?
  case "$code" in
    0|1|2|3) exit "$code" ;;   # stopped, or a configuration error: restarting would not help
  esac
  echo "smserver exited with $code; restarting in 5 s" >&2; sleep 5
done
```

systemd: `Restart=on-failure`, `RestartSec=5`, `RestartPreventExitStatus=1 2 3`.

On Windows, the same loop in a `.cmd` file:

```bat
:again
call server.cmd --map mp_tdm_09 %*
set code=%errorlevel%
if %code% GEQ 0 if %code% LEQ 3 exit /b %code%
echo smserver exited with %code%; restarting in 5 s
timeout /t 5 /nobreak >nul
goto again
```

## Not done

- No encryption or authentication of players beyond the handshake cookie and session token (hardening review: later).
- No interest management: every player gets every other player in each snapshot.
- Lag compensation trusts the client's acknowledged snapshot within the 500 ms rewind cap (review S2, open; docs/changes-from-original.md).
- Emblem upload chunks carry no session (spec "Dedicated server hardening", open item).
- An idle kick or ban sends no message: the client shows a lost connection after its server timeout.
