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

- Classic PvP sound: other players are easier to hear. Their footsteps, rolls, charges, jump-pack jumps and hops, landings, grunts and shouts now carry to about 50 metres with a steady fade, and their footsteps are louder than before (yours are as they were). Another player's evade roll now has a sound (cloth and armour). Reloads of other players are heard within about 20 metres, quieter than steps. Gunfire, explosions, jump-pack take-offs and ground pounds stay faintly audible across the whole map. Other players' footsteps and moves are no longer drowned out or cut off by gunfire (up to 20 at once). Direction is clearer: sounds pan harder to left and right and sound duller from behind. Launch option `--sound-natural-position` gives the softer, natural positioning back; `--old-classic-mix` restores the whole old mix. Musou Mode is unchanged.
