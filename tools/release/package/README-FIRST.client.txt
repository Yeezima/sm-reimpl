SPACE MARINE MULTIPLAYER REIMPLEMENTATION - PLAYTEST BUILD @VERSION@
==================================================================

WHAT THIS IS
  An unofficial fan project that recreates the multiplayer of Warhammer 40,000: Space Marine (2011) on a new
  engine with dedicated servers. It is NOT affiliated with, endorsed by or supported by Relic Entertainment,
  SEGA or Games Workshop. It is free software (GPL-3.0).
  This download contains NO game files: no models, textures, sounds, maps or data.

WHAT YOU NEED
  - Windows 10 or 11, 64-bit, and a graphics card with Vulkan or Direct3D 12.
  - Your own legal copy of Warhammer 40,000: Space Marine (2011) on Steam (app 55150), installed.
    The game is read from your installation while it runs. It is READ ONLY: nothing in it is ever written,
    renamed or patched.
  - The Master Crafted Edition (2025) and Space Marine 2 are different games and are not supported.

INSTALL AND START
  1. Before extracting: right-click the zip > Properties > tick "Unblock" > OK.
     (The programs are not code-signed. Unblocking stops Windows from blocking every file inside.)
  2. Right-click the zip > "Extract All..." to a folder of your choice. Keep the folders as they are.
  3. Run "@LAUNCHER@". The launcher keeps you up to date: it installs this build into its own
     folder (%LOCALAPPDATA%\SM Reimpl unless you choose another), looks for a new build each time it
     starts, downloads and checks it for you, and starts the game (PLAY), a server or Join.
     From then on, start "SM Reimpl Launcher" from the desktop or the Start menu; you can delete
     this folder and the zip.
  4. If Windows shows "Windows protected your PC": click "More info" > "Run anyway". The programs are not
     code-signed, so Windows SmartScreen warns about them once.
  5. The game finds your Steam install by itself; if it cannot, it asks for the folder (the one that
     holds "data").
  Without the launcher: run "@PLAY@" in this folder, as before, and download new builds yourself.

THE FILES, IN ORDER
  @LAUNCHER@
      the launcher: installs the build, keeps it up to date, starts PLAY, Host and Join. Start here.
  @README@
      this text
  @PLAY@
      the game: the Join screen, HOST A MATCH, CUSTOMIZE (class creator and armour). The launcher's
      PLAY button starts it; you can also start it here directly.
      To join a server: type its address in SERVER, choose a side, press JOIN.
  @HOST@
      starts a server that only this PC can join (no bots; for bots use HOST A MATCH in @PLAY@)
  @JOIN@
      asks for a server address and joins it directly (just Enter = this PC)
  @HOSTLAN@
      starts a server that other PCs can join (your network, or the internet)
  Folders: game\ (the program), server\ (the bundled dedicated server), source\ (the source code),
  notes\ (version, licences, tester notes). You never need to open them.

GETTING INTO A MATCH
  Join someone's server
    Run "@PLAY@", type the address you were given (for example 203.0.113.5:27015) in SERVER,
    choose a side and press JOIN. Or run "@JOIN@" and type the address.
  Play on your own
    In "@PLAY@" choose "This PC only" and press HOST A MATCH (it starts the server and joins it).
    Hosting starts no bots. To play against bots, set BOTS with - / + (1 to 15; Easy or Normal)
    before you press HOST A MATCH; they split over the two sides. Each bot uses about 0.27 GB of
    memory, so on a PC with 8 GB keep to a few. To change the number, STOP SERVER and host again.
    Or run "@HOST@" (no bots), then "@JOIN@" and press Enter.
  Stop a server
    STOP SERVER in the HOST panel stops the server you hosted (and its bots), or one started by
    "@HOST@". If the port is taken by another program, the menu says so and does not join.
  Host for friends
    Run "@HOSTLAN@", or in "@PLAY@" choose "LAN / Internet" and press HOST A MATCH.
    Windows Firewall asks once: allow it. Friends on your network join your PC's address with port 27015
    (the server window lists this PC's addresses). Over the internet, forward UDP port 27015 on your router
    to your PC; that does not work behind carrier-grade NAT (CGNAT), where a VPN such as Tailscale or
    ZeroTier does.
  Everyone in a match must use this same build. Different versions refuse each other on purpose.

CONTROLS (F1 in a match shows them too; no key rebinding yet)
  W A S D              move
  Mouse                look and aim
  Shift (hold)         sprint
  Left mouse           fire. Plasma weapons and lascannon: tap = normal shot, hold = charged shot
  Left Ctrl (hold)     aim mode
  R                    reload. Heavy bolter: brace / unbrace. Plasma weapons: vent heat.
                       Grenade launcher: detonate the grenades in the air
  Right mouse (or V)   melee; press again for the combo. In the air with a jump pack: slash or ground pound
  F                    stun strike (heavy melee). W + F: the charge (W + A / W + D + F: a bit to the side)
  Space                evade: Tactical roll, Devastator and Havoc hop.
                       Jump pack (Assault, Raptor): tap = hop-dash, medium hold = small jump, long hold = full jump.
                       Right mouse during the hop-dash: the dash attack (W A S D pick one of 8 directions)
  G                    throw your grenade
  1 / 2                pistol / primary weapon
  X or mouse wheel     switch weapon
  E                    pick up a dropped ammo or grenade box
  T                    shout "FOR THE EMPEROR" (added content: Extended profile, loyalist Space Marines)
  Tab (hold)           scoreboard
  F1                   info overlay
  Esc                  menu: change loadout (alive or dead), class creator, armour editor, leave match
  Controller           plug in an Xbox-style pad and it takes over (the original's layout)
  Left / Right arrows  turn with the keyboard
  If nothing fires, click in the window once: the mouse must be captured.

GOOD TO KNOW
  - Modes: Annihilation (team deathmatch) and Seize Ground, on Manufactorum. Space Marines against Chaos,
    up to 8 against 8.
  - Everything is unlocked: every weapon, perk and piece of armour from the start. No levels yet.
  - CUSTOMIZE on the Join screen: 10 class slots, armour and paint. In a match: Esc. Changes apply at your
    next respawn.
  - CONTENT on the Join screen: Extended (the default: SM1 plus the added content, the Ork test class and the
    FOR THE EMPEROR key) or Faithful (SM1 alone). Everyone in a match must use the same one.
  - The first join is slow: the map, sounds and textures are read from your install once and cached.

KNOWN ISSUES
  - One map (Manufactorum) and two modes. Exterminatus (co-op) is not playable yet.
  - No matchmaking, server list, lobby, voice or text chat: you type an address.
  - HUD art and fonts are placeholders; lighting and some effects are approximations.
  - Some perks do nothing yet, and many values are estimates (notes\refine-register.md lists them;
    notes\changes-from-original.md lists the deliberate differences).
  - The game runs on Windows only (the dedicated server also runs on Linux).
  - This is an early test build: expect bugs, crashes and freezes.

WHERE IT KEEPS ITS FILES
  Profile (custom classes, armour, settings) and logs: %APPDATA%\sm-reimpl  (log: logs\godot.log;
  bots you host write their own logs in logs\bots, so they never replace yours)
  Caches built from your install (converted sounds, navigation data): %LOCALAPPDATA%\sm-reimpl
  Deleting them is safe; the game rebuilds what it needs. Deleting the profile also deletes your custom classes.
  The launcher's install folder (%LOCALAPPDATA%\SM Reimpl by default): the current build and the one before it
  (for "Roll back"), and the launcher itself. Updates never touch your profile, settings or caches.
  The game talks only to the server you join. The launcher talks only to GitHub (the releases page), over HTTPS.

REPORTING PROBLEMS
  Open an issue on https://github.com/Yeezima/sm-reimpl-releases/issues (the Issues tab).
  Say the version (@VERSION@), what you did and what happened, and attach a screenshot or
  %APPDATA%\sm-reimpl\logs\godot.log if you can. If the game closes right after it starts, a window
  says so and where that log is. Differences from the original game are very welcome.
  Problems with the launcher (updating, installing): its log is logs\launcher.log in its install folder
  (%LOCALAPPDATA%\SM Reimpl unless you chose another; "Open the install folder" in the launcher).
  Look it over before you attach it: it holds folder paths of your PC, which include your Windows user name.

SOURCE CODE AND LICENCES
  GPL-3.0. The complete source of this build is in source\@SOURCEZIP@.
  Licences: notes\LICENSE.txt (GPL-3.0), notes\THIRD_PARTY_NOTICES.md (Godot Engine, .NET and others).

Version @VERSION@, protocol @PROTOCOL@, built @DATE@. The SHA-256 of the zip is shown on the release page.
