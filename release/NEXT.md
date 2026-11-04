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

- Reloads are no longer cancelled by a melee attack, a grenade throw, a ground pound, being staggered or stunned, or a charge: the reload keeps going and finishes on time. Only switching weapons takes it out of your hands (that weapon keeps reloading while holstered, as before), and dying clears it.
- New network version: this build does not play with earlier ones, so everyone must update.
- Melee hits now sound like the original game: every weapon plays its own original hit sound on the frame the blow lands, and heavy strikes, charges and extra enemies caught in one swing each get the sound the original gives them. The generated hit sounds of the power sword, power axe, thunder hammer and daemon maul are gone, and the combat knife now makes a hit sound.
- The hit confirm you hear when your shots land is the original game's again (it is sharper and louder than the muffled one of the last builds).
- Charges play only the original body smash on impact, and the thunder hammer and daemon maul no longer add an extra crackle on hits.
- Musou Mode: unarmoured Orks (Boys, Shoota Boys, Gretchin) always sound like flesh when hit; armoured Orks ('Ard Boys, Nobs, the Warboss) play the armour and flesh sounds together.
- Manufactorum: the olive wall boards and salmon-coloured fittings around the map are gone.
