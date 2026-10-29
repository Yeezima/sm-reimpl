# sm-reimpl playtest: start here

Thank you for testing. This page tells you what the project is, what you need, how to install and play, and where to look when something goes wrong.

Pages in this kit:

- [CONTROLS.md](CONTROLS.md): every key and mouse button.
- [WHAT-TO-TEST.md](WHAT-TO-TEST.md): the checklist, and the deliberate changes that are not bugs.
- [KNOWN-ISSUES.md](KNOWN-ISSUES.md): what is missing, what is a placeholder. Read it before you report something.
- Downloads and reports live here: https://github.com/Yeezima/sm-reimpl-releases (Releases for builds, Issues for reports). See "Report what you find" below.

## What this is

sm-reimpl is an unofficial, free fan project. It is a new game engine that plays the **multiplayer** of
*Warhammer 40,000: Space Marine* (2011, PC) using the game files from your own copy of the game.

- It is **not** affiliated with, endorsed by or connected to Relic Entertainment, SEGA or Games Workshop.
- It is **not** Space Marine 2 (2024) and not the 2025 Master Crafted Edition. It targets the 2011 game, Steam app **55150**.
- You need your **own legal copy** of Space Marine (2011) on Steam. The project ships no game files, no textures, no sounds, no models.
- It reads your install **read-only**. It never writes to, renames or patches anything in the game folder, and it does not touch Steam, your saves or any online service.
- The code is open source under the GPL-3.0 licence. The game's content stays with its owners.

The goal is to recreate the original multiplayer as faithfully as the evidence allows. The one deliberate change is the networking:
matches run on **dedicated servers** that decide everything (the original was peer-hosted).
Your job as a tester is to tell us where this build does not feel or look like Space Marine 1.

What you can play right now: **Annihilation** and **Seize Ground**, Space Marines against Chaos, up to 8 against 8, on one map
(Manufactorum). See [KNOWN-ISSUES.md](KNOWN-ISSUES.md).

## What you need

- Windows 10 or 11, 64-bit.
- Space Marine (2011) installed through Steam (app 55150). The Master Crafted Edition (app 3169520) is detected too, but the 2011 game is what we test against.
- A graphics card that supports Vulkan (the client is built on Godot 4). If the client does not start or shows a black window, tell us your GPU and driver version.
- About 8 GB of RAM or more. The map and its textures are loaded from your install when you join, so the first join takes longer than later ones.
- A broadband connection. Playing needs UDP traffic to the server.
- Optional: a headset and speakers. All sound is the original game's sound, decoded from your install on your machine.

We have not tested low-end machines. Report your hardware with every bug, good or bad.

## Install

1. Download the latest playtest zip (`SM-Reimpl-Playtest-...-Windows.zip`, the only download) from the Releases page: https://github.com/Yeezima/sm-reimpl-releases/releases .
2. Right-click the zip > Properties > tick **Unblock** > OK, then unzip it (Extract All) to a normal folder that you can write to, for example `Documents\sm-reimpl`.
   Do not unzip it into the game's folder.
3. The version is in the zip's name and in `notes\VERSION.txt`. Write it down: you will need it for every report.
4. Double-click **`2 - Play.cmd`** (the files at the top of the folder are numbered in the order you use them; `1 - READ ME FIRST.txt` explains them).
5. On the first run it looks for your Space Marine install through Steam. If it cannot find it, it asks you for the game folder
   (the folder that contains `SpaceMarine.exe`). Pick it once; it remembers.
6. The first run may also download a few helper files (the game engine runtime and an audio codebook file). They are checked against known hashes
   and stored in `%LOCALAPPDATA%\sm-reimpl`. If a firewall or antivirus asks about them, allow it.
7. When the Join screen opens you are ready.

If Windows SmartScreen warns about an unknown publisher, that is expected for a small free project. Only run packages from the link the project gave you.

## Join a match

1. On the Join screen, type the server into the **SERVER** box as `host:port`, for example `203.0.113.7:27015`. If you leave out the port, 27015 is used.
2. Pick your **SIDE**: Auto (the server balances the teams), Space Marines or Chaos.
3. Pick a class from the loadout strip. Everything is unlocked; there is no levelling yet. **CUSTOMIZE** opens the class creator and the armour customizer.
4. Press **JOIN**.

The server may refuse you with a message. The usual reason is a version mismatch: the server and your client must be the same build (same `notes\VERSION.txt`) and the same CONTENT profile (Faithful or Extended),
and both must use the same Space Marine install version. Update your package and try again.

While you are dead you can open the loadout menu with **C**; changes apply at your next respawn. **Esc** opens the customizer over the match. Details: [CONTROLS.md](CONTROLS.md).

## Host a match

Use **Host a match** on the Join screen to start a dedicated server on your PC and join it. This is optional; the project also runs test servers.

- The server uses **UDP port 27015** by default.
- **Windows Firewall** will ask if the server may talk on the network the first time. Allow it on **private** networks. If you only want to test alone, hosting on `127.0.0.1` needs no firewall prompt, but nobody else can reach it.
- To let people on the internet join, forward **UDP 27015** on your router to your PC, then give them your public IP address and the port. Search for your router's model and "port forwarding" if you have not done this before. Do not forward TCP; it is not used.
- Everyone who joins needs their own Space Marine install and the same build as the server. The host's PC also needs its own install.
- The server reads the game files read-only, like the client.
- **Bots:** hosting starts none. To play against bots, set **BOTS** with **-** / **+** (1 to 15) before you press **HOST A MATCH**; they split over the two sides. Each bot is a game process of about 0.27 GB of memory and about 5% of one processor core (no graphics, effects or sounds; 30 frames a second), so on a PC with 8 GB keep to a few. To change the number, stop the server and host again.
- Hosting on a PC that also plays means the host's own connection and PC performance affect everyone. If a match feels bad, say whether the server was a dedicated machine or someone's gaming PC.

Without the Join screen: `3 - Host a match on this PC.cmd` starts the bundled server for this PC only (no bots), `5 - Host a match for friends (LAN).cmd` for other PCs too
(it shows this PC's network addresses); both pass options on to the server, for example `--mode seize` or `--port 27016`. `4 - Join a server by address.cmd`
asks for an address and joins it.

## Where files, logs and settings live

Everything the project writes goes under `%LOCALAPPDATA%\sm-reimpl` (paste that into the Explorer address bar). Common contents:

- `cache\` : private cache of things made from your install, such as decoded audio. Safe to delete; it is rebuilt. Never share it: it holds data derived from the game.
- `tools\` : the downloaded engine runtime.
- `screenshots\` and `demos\` : output of screenshot and recording options, if you use them. Demos hold other players' looks, so keep them private.

Your class slots and armour looks are saved by the client in its own user-data folder (`%APPDATA%\sm-reimpl`, file `customization.json`), not in the game folder.
If a profile file is damaged, the client renames it to `customization.json.bad-<time>` and starts with the default.

Logs: the game writes its log to `%APPDATA%\sm-reimpl\logs\godot.log` (older runs next to it, with their time in the name; the 10 newest are kept). Bots you host write their own logs in `logs\bots\`, so they never push
yours out. If the game closes within about 10 seconds of starting, a window says so and where the log is. A server started by
the Host scripts writes to its window; copy the text from it before closing it. Attach the log to your report.

## Uninstall

1. Delete the folder you unzipped.
2. Delete `%LOCALAPPDATA%\sm-reimpl` (cache, runtime, screenshots, demos).
3. Optional: delete `%APPDATA%\sm-reimpl` (it holds `customization.json` and the logs) to remove your saved classes and looks.
4. Your Space Marine install and your Steam account are untouched, because the project never wrote to them. There is nothing to repair.

## Report what you find

Use the issue forms at https://github.com/Yeezima/sm-reimpl-releases/issues (New issue):

- **Playtest bug**: crashes, freezes, wrong behaviour, visual or sound glitches that are clearly broken.
- **Fidelity report**: "this is not how Space Marine 1 did it". Name the checklist ID (for example `R-9F8281FF`) if there is one, say how SM1 did it, and give evidence.
  A video of SM1 with a timestamp is the best evidence. A link is welcome.

Always include the build version (the zip's name or `notes\VERSION.txt`), your Windows version and your GPU.

Some things are deliberate changes or known gaps, not bugs. Check [WHAT-TO-TEST.md](WHAT-TO-TEST.md) (the "Deliberate changes" section) and
[KNOWN-ISSUES.md](KNOWN-ISSUES.md) first.

Please be kind and specific. Memory is a weak witness: if you are not sure how SM1 did something, say so, and mark your report "from memory".
