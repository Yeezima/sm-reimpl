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
- Deaths look more like the original: the death animation now depends on how you were killed. Shot from the left or right, from behind, in the head, by a lascannon, melta or plasma weapon, while sprinting or in the middle of a charge, each plays its own fall; a grenade or a ground pound throws you with the original's blast reaction.
- The fall flows into the ragdoll instead of freezing for a moment: the body keeps the motion of its last frame.
- Getting shot jolts the body according to the weapon that hit you and where it hit (head, chest, legs, front or back).
- Light melee hits, the combat knife's included, now show a small flinch on the victim for everyone watching, not only for the attacker. They still never stagger or interrupt anything.
