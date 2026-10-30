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
- New network version: everyone must update together.
- Rapid Deployment now works: you come back to life after 3 seconds instead of 5.
- Weapon Versatility now works for Tactical Marines: press 3 for a second primary weapon. Pick it in the class creator (Weapons step) after choosing the perk; with none picked, 3 does nothing. The mouse wheel and X now cycle pistol, primary and the second primary.
- Teleport Homer now works: while you live, your allies who die come back next to you.
- Tactical Readout and Signum Link now show a health and armour bar on the enemy under your crosshair. Without one of them, enemies no longer show a name or a bar.
- The Assault and Raptor jump pack now takes the colour you pick for the Backpack in the armour colours (before, it followed the torso colour).
- Tactical Marines can no longer fire their weapon while charging: a shot started just before the charge is dropped, and pressing fire during the charge no longer interrupts it.
- Fixed: in Seize Ground, after capturing a point a humming noise no longer starts and grows louder; each capture now gives its one cue and then it is quiet again.
- New network version again: everyone must update together.
- Charging is now purely W + F: with W held, F always charges and never gives the stun strike. If you press it again while the charge is still recovering, nothing happens.
- Reloads now carry on while the weapon is put away: start a reload, switch weapons, and it finishes on its own; switching back finds the weapon ready.
- Melee now goes the way you hold: hold S when you strike and the Marine turns round and hits backwards; A and D strike to the sides, and the diagonals work too. With no key held it strikes forward.
- Charging off a ledge: the falling animation now starts about one second into the charge.
- Headshots were checked against the game's numbers: the bolt pistol's headshot kills a Marine whose armour is gone in one shot.
