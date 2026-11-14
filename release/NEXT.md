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
- This build has a new network version: everyone must update to play together, and the online servers are updated with it.
- Combat stims now start healing at once: using one skips the wait before health regenerates (normal rate, or the faster rate with Larraman's Organ). Armour still waits its usual time after damage. Both modes.
- Devastator and Havoc: the kick (right mouse button) knocks enemies back 60 % less far. The stomp is unchanged.
- Classic autocannon: firing no longer plays a Dreadnought walking sound; only the gun's own shot is heard, your own shots included.
- Assault Marines and Raptors: jump-pack thrusters go out and vanish the moment the player dies instead of hanging in the air. Both modes.
- Plasma weapons: the stray blue light that floated above plasma cannon carriers is gone, and plasma bolts and trails (gun, pistol, cannon, both sides) now fade out softly at their edges instead of ending in hard lines.
- The download is smaller: the main menu's background video is no longer included (the menu shows its still image).
