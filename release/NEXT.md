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

- New network version (48): everyone must update to this build; older builds cannot join its servers.
- The launcher has a dark look.
- The Devastator and Havoc hop now goes the way your movement keys point, like the Tactical roll: W+D hops forward-right, S+A back-left, and so on, in all eight directions (no key held: forward). This changes the game rules, so everyone must update to the same build.
- Vengeance launcher grenades no longer bounce: they stick at once to the wall, floor, ceiling or player they hit, and fall if the player they were stuck to dies. This changes the game rules, so everyone must update to the same build.
- There is a Field of view slider on the Join screen and in the customizer / Esc menu: from the original look up to 120 degrees (measured across the screen), live while you play. Scopes keep their own zoom.
- Switching weapons now works while a switch is still playing: press another weapon key (or the same one to go back) and the switch is cancelled at once and the new weapon is raised. You still cannot fire until the new weapon is up.
- Melee attacks now step forward further (about a quarter more) and, when locked onto an enemy, dash at them in a quick burst: the farther the enemy, the bigger and faster the step, stopping right in front of them.
- The melta's blast is a much longer, softer cone: fuzzy cloudy edges with no hard outline, still purple-blue and still from the barrel to where you aim.
- The sound of your armour regenerating is half as loud as before.
- No weapon can be fired while a charge plays, for every class: a trigger pressed during the charge does nothing (and is not saved for when it ends), and aim / zoom presses during a charge no longer end it or flip the camera.
