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
- Musou Mode: Orks thrown by a grenade or any other blast no longer corkscrew through the air. Each body now flies in one clean arc straight away from the explosion, tips over as it falls and stays down where it lands.
- Musou Mode: one grenade throw sends at most 20 Orks flying, the ones nearest the blast. Any other Ork it kills explodes on the spot, and small Orks that survive it are knocked to the ground. Everyone must update: this build does not play with earlier ones.
