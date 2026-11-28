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
