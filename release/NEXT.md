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
- The armour bar is drawn once again: the old gold bar that showed under the yellow one is gone. The blue and white armour bar with the star on the left only appears when you carry the Iron Halo perk; every other loadout has the plain yellow bar.
- Launcher: the "Old GPU / safe mode" tick box no longer shows a black strip behind its text or leftover pieces of the buttons above it; its row now repaints cleanly after every status change, tick, hover, resize and minimise.
- Losing your armour (or an enemy's) now shows a strong red glow around the character, which flashes again briefly with every hit taken afterwards; hits on a character with no armour throw out a spray of blood-red droplets.
- New network version: this build does not play with earlier builds or servers, so everyone must update.
- The scoreboard (Tab) shows each player's ping in milliseconds in a new PING column: green under 80, yellow under 150, red above.
- Launcher: on displays scaled to 125 % or more, the window and its buttons now grow with the text, so nothing overlaps or gets cut off; the window opens larger than before on those displays and fits itself back on screen when moved to another monitor.