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

- New network version: this build does not play with earlier ones, so everyone must update together.
- No shooting while sprinting, with any weapon: while you sprint (Shift and W) the trigger does nothing, the lascannon included. A charge you were holding is lost when you start to sprint, and letting go of the trigger mid-sprint fires nothing. Let go of Shift to shoot: the gun comes up as it always did after a sprint (the heavy weapons, melta and grenade launcher at once); single-shot and charge weapons need a fresh click. Holding the trigger no longer slows your sprint.
- Devastator and Havoc: the stomp can no longer be cut short by shooting, aiming, melee or any other button; only a hop (Space) cancels it, from the same moment as before.
- Devastator and Havoc: running or sprinting off right after a stomp no longer leaves the arms stuck in the stomp pose over running legs; the arms now go into the run or sprint as soon as you can move. The same goes for the knife strikes and the heavy classes' kick.
- Spent bolt casings drop to the ground much faster instead of floating.
- Storm Bolter (Tactical) and the Chaos Twin Bolter: every shot now uses its rounds as in the original, so a full magazine lasts 5 seconds of full-auto fire instead of 10 and you reload twice as often. The ammo counter drops by two with each shot (one round per barrel).
- Controller: the B button now closes the open menu or goes back one level, like Esc (the in-match menu, loadouts, armour and colours, controls, dialogs, and the connecting screen). While a controls entry waits for a new button, B is bound as usual. In a match B still uses and picks up, and the B that closes a menu no longer also picks something up.
- Tactical and Chaos Tactical: no shooting during the roll, with any weapon. Holding or pressing the trigger no longer cuts the roll short with a shot; the roll plays out, and you can shoot once it ends (or once you break out of it by moving, aiming or throwing a grenade, from the same moment as before). A charge you were holding is lost when you roll; single-shot and charge weapons need a fresh click after the roll. The Devastator and Havoc hop and the jump-pack dash are unchanged.
- Every class can now look and shoot straight up into the sky, in hip fire and while aiming or zoomed; before, the view stopped well short of vertical. Looking down is unchanged.
- The stalker bolter's and lascannon's scope now darkens the whole screen on a wide window too: the strips left and right of the picture (any window wider than 16:9, such as 2000 x 1070 or ultrawide) were lighter than the scope's tint and are now the same tint, edge to edge. The reticle stays centred and round.
- Devastator and Havoc: the hop now goes down stairs and off ledges while it keeps going forward, instead of floating at the height you started from and then dropping straight down.
- Assault Marine and Raptor: tapping Space while falling (off a ledge, at the top of a jump or on the way down) now does the short hop in the air. Holding Space while falling still jumps again as soon as you land.
- Assault Marine, Raptor and Tactical: a charge that runs off a ledge can now be steered a little left and right with A and D while it flies, at the same speed.
- True Grit: the Bolter and Relic Bolter in the pistol slot now hang down at the side at rest, like the bolt pistol, instead of being held out level, and walk, run, sprint, raise, shoot and reload one-handed like the bolt pistol (seen by you and by other players). Their first shot from the lowered gun now comes as quickly as the bolt pistol's.
- Mouse sensitivity slider: a new "Mouse" slider (0.10x to 3.00x, 1.00x is the old feel) sits next to the FOV slider on the Join screen and in the in-match menu (Esc, then the loadout menu) in every mode. It changes how far the view turns per mouse movement, in hip fire, aimed and scoped alike, takes effect at once and is remembered. The controller is not affected.
- Camera: a roll, hop or jump pack dash while looking down keeps your character in view, instead of the camera dropping behind you and losing him.
- Camera at walls: backing into a wall keeps the camera over your shoulder instead of inside the head, it no longer pumps in and out at pillars and door frames, and it eases back out more gently. The crosshair stays on the same spot while the camera is pulled in. Check how it feels at walls, corners and pillars.
