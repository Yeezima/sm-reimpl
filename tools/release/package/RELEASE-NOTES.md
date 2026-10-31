@WHATSNEW@An unofficial fan project that recreates the multiplayer of **Warhammer 40,000: Space Marine (2011)** on a new engine with dedicated servers.
It is not affiliated with, endorsed by or supported by Relic Entertainment, SEGA or Games Workshop. Free software (GPL-3.0).

- **No game files are included.** You need your own legal copy of Space Marine (2011) on Steam (app 55150), installed. The game is read from your installation while it runs, **read only**: nothing in it is ever changed.
- This is **not** the Master Crafted Edition (2025) and **not** Space Marine 2. Those are different games and are not supported.

## What you need

- Windows 10 or 11, 64-bit
- A graphics card with Vulkan or Direct3D 12
- Space Marine (2011) installed through Steam

## Install and start

**Already using the launcher?** Just start **SM Reimpl Launcher**: it offers this build, downloads and checks it, and keeps your profile and settings.

1. Download **`@ZIP@`** (@ZIPSIZE@) from **Assets** below. It is the only download you need.
2. Right-click the zip > **Properties** > tick **Unblock** > **OK**. The programs are not code-signed; this stops Windows from blocking every file inside.
3. Right-click the zip > **Extract All...** to a folder of your choice. Keep the folders as they are.
4. Open the folder and run **`@LAUNCHER@`**. Run the launcher; it keeps you up to date: it installs the build into its own folder, looks for new builds each time it starts, downloads and checks them, and starts the game. From then on, start **SM Reimpl Launcher** from the desktop or the Start menu. (Or run **`@PLAY@`** directly, as before.)
5. If Windows shows "Windows protected your PC", click **More info** > **Run anyway**. The programs are not code-signed, so SmartScreen warns once.
6. The game finds your Steam install by itself; if it cannot, it asks for the game folder.

The folder holds these files, in the order you use them:

| File | What it does |
|---|---|
| `@LAUNCHER@` | **The launcher.** Installs the build, keeps it up to date (download, check, roll back) and starts Play, Host and Join. Start here. |
| `@README@` | These instructions, offline |
| `@PLAY@` | **The game.** Join screen, HOST A MATCH, CUSTOMIZE (class creator and armour). The launcher's PLAY starts it. |
| `@HOST@` | Starts a server only this PC can join (no bots) |
| `@JOIN@` | Asks for a server address and joins it directly (just Enter = this PC) |
| `@HOSTLAN@` | Starts a server other PCs can join (your network, or the internet) |

The folders `game`, `server`, `source` and `notes` hold the program, the bundled server, the source code and the notes. You never need to open them.

## Getting into a match

- **Join someone's server:** run `@PLAY@`, type the address you were given (for example `203.0.113.5:27015`) in **SERVER**, choose a side and press **JOIN**. Or run `@JOIN@`.
- **Play on your own:** in `@PLAY@` choose **This PC only** and press **HOST A MATCH** (it starts the server and joins it). Hosting starts no bots; to play against bots, set **BOTS** with **-** / **+** (1 to 15; Easy or Normal) before you press **HOST A MATCH**. Each bot uses about 0.27 GB of memory, so on a PC with 8 GB keep to about 4; to change the number, stop the server and host again. Or run `@HOST@` (no bots), then `@JOIN@` and press Enter.
- **Stop a server:** **STOP SERVER** in the HOST panel stops the server you hosted (and its bots), or one started by `@HOST@`. If the port is taken by another program, the menu says so and does not join.
- **Host for friends:** run `@HOSTLAN@`, or in `@PLAY@` choose **LAN / Internet** and press **HOST A MATCH**. Windows Firewall asks once: allow it. Friends on your network join your PC's address with port 27015 (the `@HOSTLAN@` window lists this PC's addresses). Over the internet, forward UDP port 27015 on your router to your PC; that does not work behind carrier-grade NAT (CGNAT), where a VPN such as Tailscale or ZeroTier does.

Everyone in a match must use this same build: different versions refuse each other on purpose.

## Controls

| Key | Action |
|---|---|
| W A S D | Move |
| Mouse | Look and aim |
| Shift (hold) | Sprint |
| Left mouse | Fire. Plasma weapons and lascannon: tap = normal shot, hold = charged shot |
| Left Ctrl (hold) | Aim mode |
| R | Reload. Heavy bolter: brace / unbrace. Plasma weapons: vent heat. Grenade launcher: detonate its grenades |
| Right mouse (or V) | Melee; press again for the combo. In the air with a jump pack: slash or ground pound |
| F | Stun strike (heavy melee) in the direction of the keys held (forward with none). Shift + W + F: the charge (Shift + W + A or W + D with F: a bit to that side) |
| Space | Evade: Tactical roll, Devastator and Havoc hop. Jump pack (Assault, Raptor): tap = hop-dash, medium hold = small jump, long hold = full jump |
| Right mouse during the hop-dash | Dash attack (Assault, Raptor); W A S D pick one of 8 directions |
| G | Throw your grenade |
| 1 / 2 | Pistol / primary weapon |
| X or mouse wheel | Switch weapon |
| E | Pick up a dropped ammo or grenade box |
| T | Shout "FOR THE EMPEROR" (added content: Extended profile, loyalist Space Marines) |
| Tab (hold) | Scoreboard |
| F1 | Info overlay |
| Esc | Menu: change loadout (alive or dead, for your next life), class creator, armour editor, leave match |
| Left / Right arrows | Turn with the keyboard |

A controller works too: plug in an Xbox-style pad and it takes over (the original's layout; see What's new).

Every key and controller binding can be changed in CONTROLS (the Join screen, or the Esc menu in a match). If nothing fires, click in the game window once: the mouse must be captured.

## Good to know

- **Modes:** Annihilation (team deathmatch) and Seize Ground, on Manufactorum. Space Marines against Chaos, up to 8 against 8.
- **Everything is unlocked:** every weapon, perk and piece of armour from the start. No levels yet.
- **Customize** with CUSTOMIZE on the Join screen (10 class slots, armour and paint), or with Esc in a match. Changes apply at your next respawn.
- **Content profile:** CONTENT on the Join screen picks **Extended** (the default: SM1 plus the added content, the Ork test class and the FOR THE EMPEROR key) or **Faithful** (SM1 alone). Everyone in a match must use the same one.
- **The first join is slow:** the map, sounds and textures are read from your install once and cached.
- **Your files:** the profile (custom classes, armour, settings) and the log are in `%APPDATA%\sm-reimpl`, the caches in `%LOCALAPPDATA%\sm-reimpl`. Deleting them is safe; the game rebuilds what it needs (deleting the profile also deletes your custom classes). The game talks only to the server you join.

## Known issues

- One map (Manufactorum) and two modes. Exterminatus (co-op) is not playable yet.
- No matchmaking, server list, lobby, voice or text chat: you type an address.
- HUD art and fonts are placeholders; lighting and some effects are approximations.
- Some perks do nothing yet, and many values are estimates to check against the original. The zip's `notes` folder lists them (`refine-register.md`) and the deliberate differences (`changes-from-original.md`).
- The game runs on Windows only (the dedicated server also runs on Linux).
- This is an early test build: expect bugs, crashes and freezes.

## Reporting problems

Open an issue in the [**Issues** tab](https://github.com/Yeezima/sm-reimpl-releases/issues). Say the version (`@VERSION@`), what you did and what happened, and attach a screenshot or the log `%APPDATA%\sm-reimpl\logs\godot.log` if you can. Differences from the original game are very welcome. For a launcher problem, its log is `logs\launcher.log` in the launcher's install folder: look it over before you attach it, since it holds folder paths of your PC (with your Windows user name).

## Source code

GPL-3.0. The complete source code of this build is inside the zip: `source\@SOURCEZIP@`. Licences: `notes\LICENSE.txt` and `notes\THIRD_PARTY_NOTICES.md`.
@SERVERASSETS@
## Download check

SHA-256 of `@ZIP@`:
```
@SHA256@
```
In PowerShell: `Get-FileHash .\@ZIP@`

Version **@VERSION@** · protocol @PROTOCOL@ · built @DATE@
