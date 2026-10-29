# Known issues and limits

Read this before you report something. If it is listed here, we already know. If you think the priority is wrong
(for example "I noticed this in the first minute"), say so in a fidelity report: that is useful.

Sources: the "Not yet reproduced" list of changes from the original and the refine register (the full list of estimates and guesses). Both are in
the playtest zip's `notes` folder (`changes-from-original.md`, `refine-register.md`).

## Scope of this build

- **One map:** Manufactorum (`mp_tdm_09`). The other multiplayer maps are not developed or checked.
- **Two modes:** Annihilation (team deathmatch) and Seize Ground (three points). No other PvP modes.
- **Exterminatus (co-op waves) is not playable.** It needs Ork and Chaos AI that does not exist yet.
- **Space Marines against Chaos, up to 8 against 8.**
- **Content profile:** the Join screen's CONTENT row picks **Extended** (the default: SM1 plus the added content, the Ork Boy test class and the
  "FOR THE EMPEROR" key) or **Faithful** (SM1 alone). A server and its players must use the same one; a mismatch is refused when you join.
- **The Ork Boy (added content, not in SM1) is a test class.** It cannot be picked in the menus yet. Its clips are stretched to the marine timings the
  simulation keeps, so some motions look hurried, and it has no turn-in-place, run-start or side-hit clips. Its hit area is the Ork's own, smaller one.
- **No progression.** No levels, XP, unlocks or challenges. Every weapon, perk and piece of armour is available from the start.
- **Windows game.** The dedicated server also runs on Linux; the game does not yet.
- **You need the Steam version of the 2011 game** (app 55150). Other versions are not supported.
- **No accounts, matchmaking or lobby.** You type a server address (or host one with HOST A MATCH). Teams are balanced by the server. No ready-up, team swap or map vote.
- **No voice or text chat.**
- **Singleplayer is out of scope.**

## Things SM1 multiplayer had that are missing

Do not report these as bugs.

1. **Medals and XP popups.** SM1 shows rewards (headshot, avenger, revenge, multi-kill and others). Here you see only a plain "+ Kill" line.
2. **Death screen.** SM1 shows who killed you with their weapon, health, level and perks, a "copy loadout" button and a spawn list (including
   capture-point spawns). Here you see the ragdoll, a vignette, the respawn timer and the C loadout picker. You spawn only in your spawn room.
3. **End of the match.** The countdown, the victory or defeat splash, the score count-up and a battle record are in; the next-map vote is not, and
   the match restarts on the same map.
4. **Scoreboard columns.** Kills, deaths and the ratio are filled in; level, score, assists, captures, defends and similar columns show a dash.
5. **Some perks do nothing yet** (about 12, for example Rapid Deployment, Teleport Homer, Tactical Readout, Signum Link, Weapon Versatility); a few
   others work only in part. Selecting an inactive one has no effect.
6. **Combat Stimulants and the melta bomb** are not offered.
7. **Players do not collide with each other.** SM1 is believed to block movement between players.
8. **HUD details:** no weapon icons in the kill feed, no race icons, names or capture bars floating over players and points, no icons over dropped
   ammo boxes, the original font and reticules are not used.
9. **Host options.** Only a few server settings (mode, map, score and time limit, friendly fire). SM1's others (armour regeneration, max armour,
   spawn cadence, perks on or off) are not exposed.
10. **Music** is not played except the match-end stingers.

Also not reproduced:

- Fury and executions. (Revive, fall damage and weapon pickups are believed not to exist in SM1 multiplayer.)
- Aim assist.
- Crouch. Speeds are recorded but there is no crouch state.
- Character turn-rate limits and the flip-around turn.
- Flinches, 180-degree attacks and some follow-up attacks; the thunder hammer's own dash-attack clips (it uses the sword's).
- The death animations before the ragdoll.
- Class and rank badges on the shoulder pads (no mapping found).
- Distortion materials, mesh effects and the original's shaders. Materials use a close approximation of the original.

## Placeholders and approximations

The following exist and work, but are not the original's look or number. Each is tagged in the register.

- **HUD drawing.** Layout is arranged after the original, but the art and fonts are placeholders.
- **Scoreboard and kill feed layout.**
- **Camera shake** from shots and blasts, and the **white blindness overlay** of the blind grenade.
- **Some effects.** Effect timing, curves and sprite orientation are our reading of the data. The ragdoll uses Godot joints that approximate the original limits.
- **Lighting, fog, exposure and shadows** are a modern approximation, not the original's renderer.
- **Sound mixing.** The sounds are the original ones; the mixer rules (levels, reverb, voice limits, low-pass) are our approximation.
- **Armour paint shading and decal placement.**
- **Many numbers** are estimates: the owner's recollections (the brace and vent timings, the 10 s weapon-ready hold), and our own choices where the
  data says nothing (gravity, body size, step height, weapon switch time). They are listed in [WHAT-TO-TEST.md](WHAT-TO-TEST.md) and in the refine register.

## Technical limits

- **Server and client must be the same build** (and the same content profile). Mismatched builds refuse each other at the handshake. Update both.
- **Network.** The server simulates at 30 ticks per second and sends 20 snapshots per second. Shots are checked with lag compensation up to 500 ms. A very high ping will feel worse than SM1 peer-hosted play did. Report your ping with your report.
- **Other players' ammo, health, shield and heat are hidden** from your client, by design. Other players' reloads are guessed from the shots you see.
- **First join is slow.** The map, textures and sounds are read from your install and converted on first use, and cached on your PC.
- **Customized looks and emblems of other players arrive after you join.** Until then you see the default look. Changing a look takes effect at the next respawn.
- **Real-time recordings and demos** hold other players' looks. Keep them private.
- **Performance is not tuned.** Frame rate varies with your GPU and the lighting preset. Please report your hardware with any performance complaint.
- **Early builds may crash or freeze.** Please attach the log (`%APPDATA%\sm-reimpl\logs\godot.log`).

## Not planned

- Replacing or reviving the original's online services, accounts or matchmaking.
- Changing the original's balance without a reason. If you think SM1 balanced something differently, that is a fidelity report, not a feature request.
