# What to test

This list is cut from the refine register (in the playtest zip's `notes` folder as `refine-register.md`), the project's full list of everything that was estimated, guessed or interpreted
(about 490 gameplay rows). Here are about 60 rows that you will notice most while playing.
Each line has the register ID, what this build does, and what to compare with Space Marine 1.

How to use it:

1. Play the same situation in SM1 and in this build: same class, same weapon, same spot if you can.
2. Compare. Use a stopwatch for timers, a video for animation timing, your ears for sound.
3. Report through the **Fidelity report** issue form. Put the ID in the form. Say whether this build is `same`, `close`, or `differs`, and give SM1's value or behaviour.
4. A video of SM1 with a timestamp beats a memory. If you only remember, say "from memory".

"Ours" in the text means we chose the value because the game data does not say. "Data" means we read it from the game files and trust it, but you can still say it feels wrong.
Items in the [Deliberate changes](#deliberate-changes-do-not-report-these-as-bugs) section at the end are on purpose.

## Movement

- `R-90435565` **Walk speed.** 3.6 units per second, from the data. Compare with SM1: time a walk over a known distance. The unit's meaning is unknown, so compare feel and relative speed (walk against run against sprint).
- `R-93ECE75B` **Aim-mode speeds.** Run 3, strafe 3, back 2, walk 3.6 (interpretation of the data). Compare with SM1: is aim-walking this slow and does it feel the same?
- `R-CBD42EBE` **When the sprint animation shows.** Above 7.375 m/s moving forward (ours). Compare with SM1: does the sprint pose start at the same moment you reach full speed?
- `R-B8C4C46A` **Gravity.** 20 m/s², a placeholder (the data does not have it). Compare with SM1: jump off a ledge you know and watch how long the fall takes.
- `R-753C7041` **Body size.** Capsule radius 0.45 m, height 1.9 m (placeholder). Compare with SM1: can you squeeze through the same gaps? Do doorways and railings catch you the same way?
- `R-581F8682` **Step height.** 0.4 m (placeholder). Compare with SM1: which low obstacles can you walk over without jumping?
- `R-320F45AE` **Players pass through each other.** Not implemented; SM1 is believed to block. Compare with SM1: do players block each other in doorways and on points?
- `R-0D62E699` **Evade spacing.** At least 0.8 s from one roll or hop to the next (data, interpreted). Compare with SM1: how quickly can you chain rolls?
- `R-FE9539C0` **Diagonal rolls.** The Tactical roll goes in 8 directions (from memory). Compare with SM1: does W+D roll diagonally?
- `R-D3B64075` **Evade protects against stuns.** Hyper armour for the first part of a roll (from memory of the original). Compare with SM1: can you roll out of a stun strike? How long is the protected window?

## Weapons and damage

- `R-9F8281FF` **Time to kill with the bolter.** About 14 body hits, about 1.7 s of continuous fire, from full shield and health (derived, unverified). Compare with SM1: time how long a bolter takes to kill a Tactical at medium range.
- `R-210FBF3B` **How shield and health combine.** A hit wears the shield first, then spills into health (interpretation). Compare with SM1: when does the shield break, and does the overflow feel right?
- `R-14D25338` **Damage fall-off with range.** Full damage, then linear down to the last point (inferred). Compare with SM1: how much weaker is a bolter at long range?
- `R-8813BF01` **Weapon switch time.** 0.6 s (placeholder). Compare with SM1: time a switch from pistol to primary.
- `R-04EFEAEF` **Raise delay before the first shot.** Rifle 0.2 s, pistol 0.167 s (data, owner's rule). Compare with SM1: after sprinting, how long until the first shot comes out?
- `R-C64B6FB4` **How long the weapon stays up after a shot.** 10 s, the owner's rough estimate, unverified. This one needs a stopwatch in SM1: fire once, stop, and see when the weapon lowers.
- `R-C0955EA4` **Heavy bolter heat.** +3 per shot, overheat at 100, cools 25 per second after 0.3 s. Compare with SM1: how long can you fire before overheating, and how long is the lockout?
- `R-BE6E6375` **Plasma gun heat.** +6.5 per shot, overheat at 100, cools 4 per second, locked until nearly cold. Compare with SM1: how many shots before overheat, and how long does cooling take?
- `R-86255356` **Charge weapons: tap and hold.** A tap fires the normal shot, a hold fires the charged shot (owner's rule). Compare with SM1: how long must you hold, and is there a visible charge state?
- `R-711E2FC9` **Lascannon charged shot.** Full charge after 1.5 s; cooldown 3 s; pierces up to 10 players. Compare with SM1: charge time and how many targets it passes through.
- `R-51217E2B` **Frag grenade.** 2 carried, you start with 1; fuse starts at the first ground contact (rigid bounces, ours). Compare with SM1: fuse time, bounce, and how many you carry at spawn.
- `R-B1D145CD` **Blind grenade.** Blinds everyone who can see it: 3.5 s within 8 m, 2 s up to 30 m; allies and you too; no damage (owner's rule). Compare with SM1: duration, radius and the look of the blindness. We draw a white overlay, which is a placeholder.
- `R-EF22A94C` **Melta.** Short cone, 200 damage plus 25 on the shield, one round that recharges every 2.5 s. Compare with SM1: effective range and recharge time.
- `R-76B0410A` **Where shots leave from.** The eye, 1.6 m above the feet (placeholder; the head is really about 2.1 m). Compare with SM1: can you shoot over low cover the same way? Do shots hit where the crosshair is at point-blank?
- `R-2E155649` **Hit areas.** Taken from the marine's physics shapes (data, interpreted). Compare with SM1: headshots, and whether shots at legs and arms count as you expect.
- `R-1662D062` **Bullet spread (new).** Weapons now scatter as the game data says: the bolter rests at 0.25 degrees from the hip and 0.075 in aim mode, and widens with each shot up to 1.5 degrees (our reading of the numbers). Compare with SM1: hold the trigger at a wall from 15 m, hip and aimed; how wide does the pattern get, and how fast does it close again?
- `R-C280A2D1` **Crosshair opens with the spread.** The four ticks move out as the spread grows (our drawing). Compare with SM1: does the reticule grow while firing, and by how much?
- `R-F05E2B5F` **Weapon kick (climb and sway).** Each shot lifts and nudges your view by the data's amounts; the lascannon kicks hard, Lascannon Stabilizers less (our reading). Compare with SM1: how far does the view climb per shot, and does it come back down by itself?

## Melee and the charge

- `R-90766D75` **Melee reach.** 4 m (data). Compare with SM1: from how far away does a knife or chainsword swing connect?
- `R-E2D0F575` **Melee cone and height.** ±60 degrees and 1.5 m up or down (placeholder). Compare with SM1: can you hit someone beside you, or above you on a step?
- `R-9A32D372` **No lunge.** Attacks do not step you toward the enemy (not applied). Compare with SM1: does your marine move forward when you swing?
- `R-0B7E5FFB` **Stun strike (F) chaining.** Heavy presses buffer and chain like light ones (interpretation). Compare with SM1: can you chain a stun strike after a light attack, and when?
- `R-5E05702F` **Stun damage does nothing on its own.** The stun meter is not reachable; the strikes stun directly. Compare with SM1: do repeated knife hits stun an enemy over time?
- `R-5CE08395` **Charge start.** Shift + W + F (owner's rule, 2026-10-31; replaces the W-only rule of 2026-10-29); F without Shift is the stun strike in the held direction. Compare with SM1: does the charge need the sprint held, and can you start it from a standstill?
- `R-758EB91D` **Side charge.** W + A or W + D with F turns the charge 25 degrees to that side (our angle for "a bit"). Compare with SM1: how far to the side does it go?
- `R-8A127BAB` **Dash attack directions.** During the Assault / Raptor dash, right mouse with W, A, S, D or a diagonal lunges that way (the body turns and plays the forward clip). Compare with SM1: do the eight directions and the turned body match?
- `R-23BBFED5` **Charger's hyper armour.** First 0.333 s of the charge (owner: "wears off roughly halfway"). Compare with SM1: how far into the charge can you be stunned?
- `R-AD303A5D` **Charge hit.** 25 shield and 75 health damage and a stun on contact. Compare with SM1: damage, stun length, and whether it knocks back.

## Jump pack (Assault and Raptor)

- `R-6A8897B5` **Jump height by hold.** Tap 4 m, up to 18 m with a full hold (data, interpreted). Compare with SM1: how high is a tap, and how high a full hold?
- `R-07E9DDEB` **Jump pack power.** 100 max, a jump costs 35 to 55, recharge 10 per second. Compare with SM1: how many jumps in a row, and how long to refill?
- `R-DA5CC76F` **Gravity in flight.** Rise 20, hang 12 for 0.5 s at the top, fall 30 (our reading of the pack's values). Compare with SM1: does the hang at the top exist, and does the fall feel too fast or slow?
- `R-7F3A0CDF` **Ground pound blast.** Radius 7 m, 25 damage plus 50 on the shield, falling off to almost nothing at 7 m (data). Compare with SM1: radius and damage.
- `R-27F1C543` **Ground pound recovery.** 0.356 s of helplessness after impact (ours). Compare with SM1: how long before you can act again?
- `R-71FF1CE8` **No fall damage.** None found in the data. Compare with SM1: can you die from a normal fall?

## Camera, aim and HUD

- `R-B82BCDC8` **Aim key and melee keys.** Left Ctrl aim, right mouse melee, F stun strike (from memory). Compare with SM1: are these the PC defaults?
- `R-6F296FAA` **What ends aim mode.** Melee, throw, jump or death (ours). Compare with SM1: does reloading or being hit end it?
- `R-C6C8A576` **Damage flash.** Screen edges flash yellow for shield damage, red for health damage, for 0.45 s, toward the shooter (owner's description; the original's own effect is not drawn). Compare with SM1: colour, strength, duration, direction.
- `R-649D3A4F` **Camera kick per shot.** Small shake, bigger for the lascannon and plasma cannon (placeholder). Compare with SM1: does your view move when you fire?
- `R-500B8E15` **Blast shake.** Shake within 25 m of a blast (placeholder). Compare with SM1: shake strength and range.

The HUD drawing itself (health, shield, ammo, kill feed, scoreboard) is a placeholder; see [KNOWN-ISSUES.md](KNOWN-ISSUES.md). Layout feedback is welcome, but file it as a fidelity report, not a bug.

## Sound

- `R-E444E3F5` **Overall mix.** Bus levels, reverb, limiter. Event sounds are the original ones; the mixing is our approximation. Compare with SM1: is anything too loud, too quiet or missing? Name the weapon or action.
- `R-E9995E29` **Your own sounds.** You hear your own weapons in the "local player" version of each sound (evidence in the data, interpreted). Compare with SM1: do your shots and footsteps sound like SM1's, and different from other players'?
- `R-FEBA8CB6` **Voice barks.** Effort grunts on melee swings, the charge and tagged animations; one line at a time per character (ours). Compare with SM1: when do marines speak, and how often?
- `R-FAF282B6` **No armour rattle while moving.** Deliberately silent (owner's rule). Compare with SM1: do you hear gear while running? Report if you do.

## Effects

- `R-F1A1F99A` **Bullet tracers.** A bright streak that reaches the target in at most 0.1 s (owner's rule; the 0.1 s cap is ours). Compare with SM1: speed and length of the streak for the bolter, heavy bolter and plasma gun.
- `R-36E268F3` **Ground pound ring (ours, added).** A thin dark ring at the blast radius, 10 s. Compare with SM1: is there any ring on the ground? If not, tell us and we remove it.

## Match rules

- `R-34102653` **Players per side.** At most 8 (public information, unverified). Compare with SM1: is 8 against 8 right?
- `R-4746038B` **Annihilation score.** Every death scores 1 for the other side, whatever the cause (interpretation). Compare with SM1: do suicides and falls give the other team a point?
- `R-44FC1E5E` **Annihilation score limit.** 41 points (ours). Compare with SM1: what limit did the default match use?
- `R-22568AED` **Time limit.** 10 minutes, higher score wins (ours). Compare with SM1: default match length.
- `R-BED2640F` **Respawn time.** 5 s (a placeholder in the register; a later audit found a matching 5 s `death_time` in the data). Compare with SM1: time a respawn.
- `R-956A1C89` **No spawn protection.** None found in the data. Compare with SM1: can you be shot straight after spawning?
- `R-050BB140` **Friendly fire.** Off by default (ours). Compare with SM1: did the default match hurt teammates?
- `R-B2AE5E76` and `R-B4AAA4D4` **Seize Ground capture.** 20 s to capture a neutral point, 10 s to neutralise an enemy point (data). Compare with SM1: time a capture and a neutralise with one player, and with more players.
- `R-00249424` and `R-F9CC76E0` **Seize Ground scoring.** 2 points per owned point, every 1 s (interpretation); limit 1000 (`R-B769A458`, ours). Compare with SM1: how fast does the score move with one, two and three points?
- `R-EF33FE2C` **Match flow.** Warm-up until both sides have a player; 15 s after the end the scores reset and everyone respawns (ours). Compare with SM1: what happens at the end of a match?

## The rest of the register

This is a selection. Everything else (several hundred rows: projectile physics, hit reactions, camera collision, effect timing, sound details, perks, customizer, lighting)
is in the full refine register (`notes\refine-register.md` in the playtest zip), grouped by area. Pick any row, play it in SM1 and in this build, and report it.
If you have a favourite area (for example heavy bolter feel, or jump-pack movement), go through that whole section.

## Deliberate changes (do not report these as bugs)

These differ from SM1 on purpose. The project owner decided each one. You are welcome to say you dislike one, but it is not a defect. The full list with reasons is in
`changes-from-original.md` (in the playtest zip's `notes` folder), and the refine register's Part C asks after the playtest whether to keep or revert each.

- **Dedicated, server-authoritative servers.** SM1 matches were hosted by a player. Here the server decides everything, and your client predicts and corrects. This is the one intended change in the project. You never see other players' health, shield, ammo or heat. Expect a bit of rubber-banding on a bad connection.
- **Spawn-room walls reach 25 m high.** In SM1 jump packs could fly over the barriers at the spawn rooms. Here the other side cannot get over them. (Owner's decision.)
- **Spawn-room barriers apply in Annihilation too, and everyone spawns in their own spawn room.** The data points to shared spawn markers; we spawn each side in its room and face the exit.
- **The map is silent.** The map's own ambience and machine sounds are off. Only players make sound. The original map sounds had a constant ringing for the owner, probably the spawn-room shield hum. A start option can turn them back on.
- **Impact sounds on a player hit always combine the armour sound and the flesh sound.** In SM1 the flesh sound plays only once armour is broken. (Owner's direction.)
- **A modern lighting pass.** Soft shadows, volumetric smog, soft god rays and a colour grade, on the original assets. Presets `off`, `low`, `medium` (default) and `high`. If something looks too bright, dark or foggy, tell us your preset.
- **No level progression.** All weapons, perks and armour are unlocked from the start. There are no levels, XP or challenges.
- **10 custom class slots**, instead of the original's 6 unlocked by level.
- **Extras in the customizer.** You can pick the colour of your helmet lenses, and import your own 128 x 128 image as a shoulder-pad emblem that other players see. SM1 has neither.
- **Players can change class and look between lives** (C on the death screen, Esc in a match). The change applies at the next respawn.
- **Chaos Havoc and Raptor default armour follow the game data.** (Owner's decision.)
- **Zone markers hidden.** Seize Ground point columns, ground-pound rings and spawn-room shield planes are not drawn by default, because the owner says SM1 did not show them. (The ground-pound ring marked above as ours is a separate small addition, shown by default.)
- **Orks (a sanctioned addition).** The owner is adding an Ork faction as a playable third look. SM1 multiplayer had only Space Marines and Chaos. It is not in every build; if you see Orks, they are on purpose.
- **Test bots** (`--bot`) are a test aid, not a feature.
