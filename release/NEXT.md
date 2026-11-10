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
- Musou Mode: enemies no longer drop weapons. Instead elite Orks sometimes, and bosses always, drop a powerup for you: a small black and white tile on the floor. Hold the use key for half a second next to it to take it (a bar under the prompt fills while you hold). Double Damage doubles all your damage for 30 seconds; Fear makes the Orks near you run away for 15 seconds (bosses stand their ground); Supercharged Melee doubles your melee damage and widens your swings for 20 seconds; Rapid Fire makes your guns fire twice as fast without using ammunition for 20 seconds. Your running powerups show as small icons with a timer beside your health bar. Taking the same one again starts its time over; dying or a new round ends them. No mix of powerups and stims goes past double damage or double fire rate.
- Musou Mode loads in a few seconds instead of half a minute: the battlefield's layout is built the first time after each update (that first join still takes about half a minute) and kept in the game's private cache, and the loading screen now says what it is doing (reading the map, building collision, drawing the map, preparing looks) instead of standing still while the map loads.
- Musou Mode: each melee swing now draws one glowing curved slash through the Orks it hits, as wide as the swing reaches (the power spear's sweep too), instead of a separate stroke on every Ork.
- The main menu has a moving background: a looping cinematic of the Ashfall Citadel battlefield, a Space Marine with a glowing power sword in the storm, behind the menu in Player versus Player and Musou Mode alike. It pauses while the customizer is open. If your PC struggles in the menu, the old still picture is back with --menu-still.
