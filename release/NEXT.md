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
- Charges no longer pass through enemies: a charge that runs into an enemy's body stops there and hits, including a glancing shoulder-to-shoulder clip at the side, and the charger stops where the bodies meet instead of sliding past. Try charging enemies who sidestep into your path at the last moment.
- Players no longer walk or run through enemy players, even with lag; when your view of an enemy was out of date you may be nudged back a few centimetres to where the bodies meet.
- This build uses a new network version: everyone must update to play together.
