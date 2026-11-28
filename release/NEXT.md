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
- The launcher has a new **PLAY TEST MAP** button next to MAPMAKER: pick a saved map, the mode, how many bots a side and your side, and play it with bots without the Mapmaker open (it uses much less memory than pressing Play test inside the Mapmaker). The game window follows the launcher's Old GPU / safe mode box, like PLAY. Close the game window to stop the server and the bots. Fewer bots use less memory.
- Developer tools (F6, on a match you host on this PC): a new "God mode (me only)" switch makes only you take no damage, your own grenades included, while bots and other players still get hurt; KILL ME still works.
- Developer tools: a new CUSTOM MAP row lists the maps you saved in the Mapmaker; pick one and press GO to end the match and continue on that map a few seconds later, also during a Mapmaker play test. A map without capture points plays Annihilation, and a map that cannot be loaded leaves the match running with the reason shown.
- This build has a new network version: everyone must update to play together.
