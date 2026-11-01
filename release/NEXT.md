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
- Fixed a disconnect: dying during a directional melee attack could end your connection with 'Disconnected (no snapshot from the server)'.
- The camera now always stays over the right shoulder; it no longer swaps to the left shoulder near walls.
- At a higher field of view the camera sits slightly further right, so your character covers less of the crosshair. Shots still land on the crosshair, and the default field of view looks the same as before.
- Musou Mode: the information panels left the top of the screen and now show on the war map, while you hold M.
- Musou Mode: point markers are now just the small A, B and C letters, and enemies no longer show health bars over their heads.
- Melee swings step forward sooner.
- Thunder hammer and daemon maul strikes follow their target and land more reliably.
- The blue clouds from power weapon hits are half the size.
- The stalker bolter leaves a wall crater, larger than the bolter's.
- Musou Mode: Orks, Gretchin, Nobs, Chaos Marines and the Warboss now have their original sounds: guns, melee swings, footsteps, grunts, shouts and deaths.
- Musou Mode: the commander's status text is much smaller and shows only while you aim close to his position.
- Musou Mode: the small Orks are gone; normal Ork Boyz take their place, so the enemy count is the same.
- Musou Mode: explosions you cannot see no longer light up the room you are in through its walls.
- Musou Mode: Orks now react reliably to being shot, including the head, chest and shoulders, and while they run.
- Melee attacks can no longer be chained faster than the combo allows by shooting between swings: after a swing starts you must wait until its combo window before the next one starts. This build does not play with earlier ones: everyone must update.
- Musou Mode: you can start a new melee swing whenever you like by pressing melee again once the previous swing has hit, without shooting or waiting for it to finish.
- Charges are no longer cancelled by movement keys: only hitting an enemy or a wall (the recoil), jumping, attacking or throwing ends one.
- Musou Mode: calling an artillery strike with B now shows a thin red laser beam from your weapon to the target.
