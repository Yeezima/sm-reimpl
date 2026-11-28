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

- Blood sprays now vary from hit to hit: every spray still flies away from the hit, but its ribbons scatter a little differently each time and some reach a little shorter or further.
- Mapmaker: Edit mode works on the first click. Select a piece, press Tab (or pick Edit in the mode menu) and click a vertex, edge or face straight away to move it.
- Mapmaker: in Edit mode your pieces stay selected. Clicking other objects, empty space or rows in the Scene list no longer changes what you are editing; press Tab (or pick Object in the mode menu) to go back with the same pieces still selected.
- Mapmaker: a picked edge now shows as a bright line, Godot's move arrows hide while you edit and come back as soon as you press Tab, and Shift+F freelook moves the camera without starting an edit.
- Mapmaker: placing from the library waits until you are back in Object mode.
