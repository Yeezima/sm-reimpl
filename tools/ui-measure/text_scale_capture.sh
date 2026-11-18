#!/usr/bin/env bash
# ui/text-scale capture driver (Git Bash): every front-end screen at the given resolutions, with the text audit beside each screenshot.
# usage: text_scale_capture.sh <out dir> <profile file> <WxH>[,<WxH>...] [screen ...]
# Starts Godot only through godot.cmd (windowed = the hidden desktop); nothing here binds a socket.
set -u
out="$1"; prof="$2"; ress="$3"; shift 3
root="$(cd "$(dirname "$0")/../.." && pwd)"
declare -A ARGS=(
  [main]="--menu"
  [lobby]="--menu --menu-screen lobby"
  [host]="--menu --menu-screen host"
  [joinlocal]="--menu --menu-screen joinlocal"
  [musou]="--menu --menu-screen musou"
  [options-graphics]="--menu --menu-screen graphics"
  [options-sound]="--menu --menu-screen sound"
  [options-controls]="--menu --menu-screen controls"
  [options-interface]="--menu --menu-screen interface"
  [keybinds]="--controls-screen keyboard"
  [armoury]="--customizer --demo-profile"
  [appearance]="--appearance --demo-profile --demo-look"
  [pause]="--menu --menu-screen pause"
  [scoreboard]="--menu --menu-screen scoreboard"
  [scoreboard-single]="--menu --menu-screen scoreboard-single"
  [summary]="--menu --menu-screen summary"
  [countup]="--menu --menu-screen countup"
  [failure]="--menu --menu-screen failure"
  [dialog]="--menu --menu-screen dialog"
  [setup]="--menu --show-setup"
  [tfw-range]="--menu --menu-screen tfw-range --content musou"
  [tfw-war]="--menu --menu-screen tfw-war --content musou"
  [tfw-score]="--menu --menu-screen tfw-score --content musou"
  [tfw-reward]="--menu --menu-screen tfw-reward --content musou"
  [tfw-run]="--menu --menu-screen tfw-run --content musou"
)
screens=("$@")
if [ ${#screens[@]} -eq 0 ]; then screens=(main lobby host joinlocal musou options-graphics options-sound options-controls options-interface keybinds armoury appearance pause scoreboard scoreboard-single summary countup failure dialog setup tfw-range tfw-war tfw-score tfw-reward tfw-run); fi
mkdir -p "$out"
IFS=',' read -ra R <<< "$ress"
for res in "${R[@]}"; do
  for s in "${screens[@]}"; do
    png="$out/$s-$res.png"
    "$root/godot.cmd" --windowed --resolution "$res" -- ${ARGS[$s]} --screenshot "$png" --text-audit "$out/$s-$res.txt" --server-exe "$root/src/SmServer/bin/Release/net10.0/SmServer.exe" --profile "$prof" --no-sound --no-screen-blink --quit-after 7 > "$out/$s-$res.log" 2>&1
    [ -f "$png" ] || echo "MISSING $s $res"
  done
done
echo done
