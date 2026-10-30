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
- The camera no longer trails the mouse: it follows your aim exactly again.
- The stalker bolter and the lascannon now zoom in further when you scope (stalker bolter about 1.5 times tighter, lascannon about 2.6 times tighter than before).
- The C key no longer opens the loadout menu; press Esc to change your loadout, alive or dead (the death screen shows "[ESC] CHANGE LOADOUT").
- Switching between the bolt pistol and your primary weapon now eases the camera over a quarter of a second instead of jumping.
- The stalker bolter's reload now plays its sounds once each: the doubled click is gone and the bolt release remains.
- New: Spectate. Press SPECTATE on the Join screen before joining (it is not remembered) to watch a match with a free camera instead of playing; you take no player slot, side or score and cannot be hit, and the scoreboard lists you under SPECTATORS. WASD flies where you look, Space or E up, Ctrl or Q down, Shift faster, Alt slower, the mouse wheel changes the speed, [ and ] change the field of view (Backspace resets it), G switches smooth cinematic movement, F follows the next player over the shoulder (Shift+F the previous one, after the last back to free flight), H hides the whole screen display for clean footage, F10 returns to the menu. A server admits up to 4 spectators.
