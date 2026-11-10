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
- Melee swings reach 40 % less far for every weapon, the power spear's sweep too; the swing line you see shows the new reach.
- Every ranged attack does 20 % more damage to the Orks: guns, launchers, explosions, grenades and the Remote Detonators.
- The Librarian's chain lightning now damages exactly the Orks you see it strike, reaches 25 % farther and deals 25 % more damage.
- Musou Mode in this build does not play with earlier builds: everyone in a Musou match, and the server, needs this update.
- Deaths look more like the original: the death animation now depends on how you were killed. Shot from the left or right, from behind, in the head, by a lascannon, melta or plasma weapon, while sprinting or in the middle of a charge, each plays its own fall; a grenade or a ground pound throws you with the original's blast reaction.
- The fall flows into the ragdoll instead of freezing for a moment: the body keeps the motion of its last frame.
- Getting shot jolts the body according to the weapon that hit you and where it hit (head, chest, legs, front or back).
- Light melee hits, the combat knife's included, now show a small flinch on the victim for everyone watching, not only for the attacker. They still never stagger or interrupt anything.
### Loading
- Old-GPU (safe mode) players: joining a Musou match often closed the game during the loading screen. That is fixed for nearly every join; if the game still closes while LOADING, start it again and tell us.
- The stutter in the first moment after you join a match is shorter, most of all with sound on, and in Musou Mode as you appear aboard the ship.
- Old-GPU (safe mode) players: one long stutter right after joining a match is gone; loading takes a fraction of a second longer instead.
- Musou Mode, power spear: the jump-pack dive now ends in the point-down impale only when it actually lands on an Ork; a dive that hits nothing lands, dips the spear and brings it straight back up. The carried spear sits closer in at the chest with the blade clear of the crosshair, both hands now hold the shaft in their palms, and the feet stay planted through the steps of the swings.
- Musou Mode, power spear: the light attacks now swing the spear out in wide sweeps around the marine, his hips turning into each cut and his feet stepping toward the target. While running, the spear and arm stay close in at the chest with the blade clear of the crosshair, and the feet no longer slide along the ground during the lunging swings.
