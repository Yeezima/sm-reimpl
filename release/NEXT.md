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
### Everyone
- This build does not play with earlier ones (new network version): the host and every player must update before joining. Servers on the old version cannot be joined, and this build cannot join them.
- Musou Mode is hidden from the menus for now. Warfront takes its place.

### Executions
- **Executions, Classic PvP Extended and Warfront:** tap Q (rebindable under Controls) to finish a target with the original paired finisher animations. A glowing red dot with a melting, dripping edge floats over the head of the one target the tap would take. The camera then orbits freely around the scene.
- **Classic PvP Extended:** the finisher opens for a moment after you kill an enemy in melee. The kill and the score count as usual and the victim's respawn is not delayed.
- **Warfront:** tap Q beside an Ork to finish it. You are safe during the finisher and come out of it with full health, full armour and a blue overshield on top of the armour bar; the shield glows over your armour and jump pack (not weapons or cloth). After a finisher there is a short cooldown before the next one.
- **Warfront against Chaos:** Traitor Guardsmen, Cultists, Chaos Marines (including Havocs, Aspiring Champions, grounded Raptors and the Lord's Chosen) and Daemons can be finished too, with their original finishers. In the final duel, finish the Chaos Lord once his health is below a tenth to win; the win waits for the finisher to play out.
- Every executor now puts his pistol away during a finisher, so the free hand grabs the victim. Combat-knife players use the chainsword's finishers on anything that is not an Ork, and the power sword uses the chainsword's finishers too.
- In the free melee camera, Fire and Reload do nothing while your finisher plays, so you come out of it still in the free camera; holding Aim when it ends returns to the normal camera.

### Warfront
- **Lightning Claw and Power Spear:** switched off for now. The Assault Marine no longer finds them in his melee choices, and a saved loadout or retainer that carried one now uses the class's default melee weapon. Everything else in it stays as you saved it.
- **Chaos as an opponent:** the Warfront page and the Host panel have a FACTION choice: Alternate (the default), Orks or Chaos. Alternate swaps between the two campaigns. The faction stays the same for the whole run, including after a wipe, a restart or a reconnect.
- **Chaos army:** Traitor Guardsmen and Cultists in the lanes, Raptors in melee, Chaos Tactical Marines and Havocs at range, and Daemons. The war map shows CHAOS-HELD, CHAOS STRONGHOLD and CHAOS LORD (SLAIN), and the HUD says CHAOS LORD, CHOSEN and CHAOS PREVAILS.
- **Warp breaches:** glowing tears in space that keep sending out Daemon squads. Hit one with a grenade, rocket or other explosive launcher to close it (bolt fire does not). Closing it stops new Daemons, but the ones already out stay.
- **Chaos Lord finale:** after the last bodyguard falls, the top-scoring living player duels the Chaos Lord alone while the others spectate, with no retainers or outside help. Win the duel and the Lord is slain. If you fall, the next player takes over. The Lord keeps the health he has left. If every player has fallen, the run is lost.
- **Retainers:** each player now has two retainers: marines that fight, move and sound like players. They do not capture points. Retainer cards no longer appear when you level up.
- **Drop pods:** the Marines a drop pod brings no longer use up player slots, so pods work with a full group of humans.
- **Walk-over pickups:** walk over powerups, ammo, equipment boxes and crates to take them, with no E press. Weapons that would replace one you carry still need E.
- **No Bomb Squigs** for now.
- **Armoury:** the mode buttons above the preview stay in their row and no longer run into the loadout column.

### Classic PvP
- **Power sword (Assault Marine, Raptor):** F, F is the foot stomp again, with its blast. The heavy stab now comes only from the Sword Stab key (T by default; change it under Controls). Light attack then F is the normal forward thrust strike, and no light or F combo ends in the stab any more.
- **Bots:** bots spread across the whole map. Each life takes a route from spawn to spawn, Assault and Tactical bots sometimes flank by a side route, and Devastators and Tacticals hold high ground or covered spots. Bots also drop off ledges up to 12 m on the maps other than Manufactorum (the bots' map data rebuilds once).
- **Thunder hammer and daemon maul:** hold the back direction (S on a keyboard) and attack to swing a 180 strike behind you, with follow-up strikes. The hammer marine's air dash and its landing play the hammer's own animations.
- **Heavy bolter:** while braced, you turn in place with the original turn animations.

### All modes
- **Melee camera:** after a knife or melee weapon strike, or the lunge of a dash attack, the camera orbits freely around your marine and you run toward where you steer, until you fire, aim, reload or die. Throwing and sprinting keep the camera free. Charges, ground pounds and kicks do not free it.
- **Power-weapon flash:** only the thunder hammer and the daemon maul still flash blue on a hit, at half the size and 40 percent strength, so you can see the hit. The power sword and power axe no longer flash.

### Effects
- **New blood spray, Classic PvP Extended and Warfront:** every hit on an enemy throws one streak of blood that spreads across the wound and breaks up into patches. It covers melee, gunfire, charges and explosions, but not flamers, plasma or melta.
- **Weather:** rain, thunderstorm, ash squall and fog use new textures that suit the maps, and raindrops no longer thin out when you move fast. Ember Wind is unchanged.

### Customizer
- **Armour pieces, all modes:** the DLC and campaign armour sets (Black Templars, Space Wolves, Blood Ravens, Ultramarines Champion, Chosen of Nemeroth and others) are now separate pieces you can mix and match slot by slot and paint like any other armour. This also covers extra pieces such as Raptor legs for every Chaos class and Librarian pieces. The full sets are still available as quick picks. An older build shows the default piece for these.

### Maps and Mapmaker
- **Mapmaker:** an Object / Edit mode (as in Blender) lets you select vertices, edges and faces, move, rotate and scale them, and see live sizes in metres. Edited capture points, spawns, doors and kill zones become real gameplay in the match, and objects take textures and simple shader materials. Maps saved from earlier versions play unchanged.

### Menus
- **Reset progression** moved from the Warfront page to Options > Interface, directly under Screen flashes. It asks you to confirm.
