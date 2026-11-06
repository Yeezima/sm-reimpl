<!--
What's new in the NEXT playtest build. ship.cmd puts everything below this comment at the top of the release description, then moves it to
release/history/<version>.md and leaves only this comment here.

Rules (the text is public; ship.cmd refuses lines that break them):
- One "- " bullet per change a tester can notice, in plain words: what to press, what looks or sounds different, what to try.
- Content only: no PR or issue numbers, file or folder names of the repository, branch names, commit hashes or developer jargon.
- Say when a build does not play with earlier ones (a new network version): testers must all update.
- Leave out changes a tester cannot notice (tooling, docs, refactors). Optional "### " headings group long lists.
- Add your bullets in the same pull request as the change, when it merges.
-->
- Charges no longer pass through enemies: a charge that runs into an enemy's body stops there and hits, including a glancing shoulder-to-shoulder clip at the side, and the charger stops where the bodies meet instead of sliding past. Try charging enemies who sidestep into your path at the last moment.
- Players no longer walk or run through enemy players, even with lag; when your view of an enemy was out of date you may be nudged back a few centimetres to where the bodies meet.
- This build uses a new network version: everyone must update to play together.
- Musou Mode: the Orks gather at their spawn points and come in loose blobs that surround you instead of running in lines, there are twice as many of them again, and the small red marks under attacking Orks are gone.
- Musou Mode for up to 4 players: MUSOU MODE on the main menu now has PLAY ON THIS PC (as before), HOST LOCAL LOBBY (This PC only or LAN / Internet, like a match) and JOIN LOCAL LOBBY (type a friend's address). Everyone fights the Orks together as Space Marines, a Deploy takes the whole team, and a friend who joins during a battle lands at the landing zone with a retainer. The zip also has two new files to host Musou Mode on this PC or for friends.
- Charges that meet head-on now clash: both Marines lock together instead of passing through each other.
- Classic matches: the Deploy, the choice of where to respawn and the heavy bolter's ammo count now work as in the original.
- Melee: the step forward of a swing starts the moment the swing starts and every weapon steps 20% further; the lock-on lunge and the air jump were tuned per mode.
- Strafing and turning while running blend smoothly instead of snapping between animations.
- The plasma gun can vent while aiming down sights or sprinting; plasma weapons have the original heat gauges, crosshairs and shot effects, and the plasma explosion looks like the original.
- The melta's recharge no longer resets when you charge, throw a grenade or kick, its blast looks like the original, and its glow and side vents stay on the gun.
- Grenades: a quick throw and a quickdraw weapon switch, and grenades now have their own models.
- Shots on players show the original's hit glow and sparks.
- Assault Marines and Raptors: a launch flash at each jump, ready jets on the pack and proper legs during the air strike. On a gamepad the aerial tackle is on Y.
- OPTIONS, GRAPHICS has ten lighting presets, and menu buttons glow softly when selected.
- Musou Mode: a new look for Ashfall with storm clouds, haze, ash and debris, grime on the stone, fires, and raised objective letters with a capture tracker; buildings no longer stand in the roads and you can shoot through their windows.
- Musou Mode: Imperial Guardsmen hold the points you capture, Ork patrols and hunter columns roam the map, Boss Orks appear with an elite retinue, Orks carry and fire visible guns, and they shout and taunt.
- Musou Mode: Orks are no longer stunned by your shots, far-away Orks move smoothly, and each enemy you hit in melee heals you.
