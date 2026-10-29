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
- Perk descriptions in the class creator now show the real numbers under each perk's text, worked out from the same values the game plays with: for example "Bolt Pistol: clip 10 → 16 (+60%)", "Armour 100 → 150 (+50%)", "Bolter: hip spread 0.25° → 0.125° (−50%)" and "heals 30 health per hit while hurt".
- Perks that do not work yet (Rapid Deployment, Teleport Homer, Tactical Readout, Signum Link, Weapon Versatility) say "Not active yet." under their description.
