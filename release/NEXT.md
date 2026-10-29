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
- Other players' melee hits now make a sound (and hits on you): the weapon's hit sound where the blow lands, only when the victim is really hurt.
- Charges and dash attacks that hit play the game's own heavy body-slam impact instead of the weapon's hit sound.
- Melee weapons hum while held: the thunder hammer, the daemon maul and the power axe with their own hum, the power swords quietly with the axe's hum. The chain axe stays silent.
- The thunder hammer and the power axe now glow while held, like the power sword.
