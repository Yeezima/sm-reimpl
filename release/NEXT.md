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
