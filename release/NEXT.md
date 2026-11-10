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
- Musou Mode loads in a few seconds instead of half a minute: the battlefield's layout is built the first time after each update (that first join still takes about half a minute) and kept in the game's private cache, and the loading screen now says what it is doing (reading the map, building collision, drawing the map, preparing looks) instead of standing still while the map loads.
- Musou Mode: each melee swing now draws one glowing curved slash through the Orks it hits, as wide as the swing reaches (the power spear's sweep too), instead of a separate stroke on every Ork.
