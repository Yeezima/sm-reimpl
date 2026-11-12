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

### Musou Mode
- Melee healing is capped: one melee swing now heals you at most 3 times, however many Orks it hits (the first three struck heal, the rest give nothing); your AI Marine allies are capped the same way. The air slash's landing heal and healing from other sources are unchanged. Musou Mode servers and players must update together.

- Orks now commit to a swing. They turn toward you only for the first moment of the wind-up (never a sudden half-turn), then the direction is fixed: run in a circle round an Ork as it raises its weapon and the blow misses (a walk is not quick enough; a run is). Stepping back out of reach still works. Not compatible with earlier builds: everyone must update. Try it against Boyz, Nobs and the Boss Orks, and tell us if the swings feel too easy or too hard to dodge.
- Your own bolter now plays the original shot sound only; the extra deep layers under it are gone.
