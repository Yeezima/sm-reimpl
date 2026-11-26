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

- This build does not play with earlier ones in the extended content and Warfront: everyone must update.

### Executions
- Executions now play the original game's own sounds, voices and blood, timed to each finisher: the strain of the struggle, the victim's reactions, blade scrapes, the chainsword's bite, the power sword's sizzle and the hammer's impacts.
- New finishers: knife players get the Ork arm break, and chainsword and power sword players get the Ork skull stab.

### Warfront
- Allied Guardsmen now hold their line. When a push breaks them they fall back together to a friendly point, then rally and re-form once the push is broken, and they turn to answer flanks.
- The Guardsmen call it out on the callout line, with two Guardsman barks.

### Mapmaker
- Edits on big maps no longer hang: on an imported Space Marine map an edit went from about a minute to a fraction of a second.
- Import images has moved into the material editor.
- A multi-selection stays selected after you move, rotate or scale it.
- Snap now starts off.
