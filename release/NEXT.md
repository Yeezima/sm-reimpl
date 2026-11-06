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
- Musou Mode: the Orks gather at their spawn points and come in loose blobs that surround you instead of running in lines, there are twice as many of them again, and the small red marks under attacking Orks are gone.
- Musou Mode for up to 4 players: MUSOU MODE on the main menu now has PLAY ON THIS PC (as before), HOST LOCAL LOBBY (This PC only or LAN / Internet, like a match) and JOIN LOCAL LOBBY (type a friend's address). Everyone fights the Orks together as Space Marines, a Deploy takes the whole team, and a friend who joins during a battle lands at the landing zone with a retainer. The zip also has two new files to host Musou Mode on this PC or for friends.
