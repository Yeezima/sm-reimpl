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
- Melee healing is capped: one melee swing now heals you at most 3 times, however many Orks it hits (the first three struck heal, the rest give nothing); your AI Marine allies are capped the same way. The air slash's landing heal and healing from other sources are unchanged. Musou Mode servers and players must update together.

- Orks now commit to a swing. They turn toward you only for the first moment of the wind-up (never a sudden half-turn), then the direction is fixed: run in a circle round an Ork as it raises its weapon and the blow misses (a walk is not quick enough; a run is). Stepping back out of reach still works. Not compatible with earlier builds: everyone must update. Try it against Boyz, Nobs and the Boss Orks, and tell us if the swings feel too easy or too hard to dodge.
- Your own bolter now plays the original shot sound only; the extra deep layers under it are gone.
- Loud fights no longer clip at the peaks. Nothing else got quieter: only the loudest moments are caught before they leave the game.
- Close danger is easier to hear: a boss winding up, slamming or charging, a Nob's heavy swing, a rokkit or Big Shoota aimed at you, a lit Bomb Squig next to you and an Ork raising his weapon behind you play a little louder and the crowd can no longer cut them off.
- Big explosions muffle your hearing less: only blasts close to you do it, it is milder and over sooner.
- The far battle and the distant war dip a few decibels while Orks are fighting you, then come back.
- The armies' Waaagh! war cries are back (the far crowd's yells stay off).
- A quiet wind and the crackle of fires sit under the battle.
- Classic PvP sound: other players are easier to hear. Their footsteps, rolls, charges, jump-pack jumps and hops, landings, grunts and shouts now carry to about 50 metres with a steady fade, and their footsteps are louder than before (yours are as they were). Another player's evade roll now has a sound (cloth and armour), and their reloads are heard within about 20 metres, quieter than steps.
- Classic PvP sound: gunfire, explosions, jump-pack take-offs and ground pounds stay faintly audible across the whole map, and other players' footsteps and moves are no longer drowned out or cut off by gunfire.
- Classic PvP sound: direction is clearer. Sounds pan harder to left and right and sound duller from behind. A launch option gives the softer, natural positioning back, and another restores the whole old mix. Musou Mode is unchanged.
- The autocannon's explosion no longer kicks up dust (its flash stays), its hit kicks up half as much, and each explosion throws chunks of rubble out of the ground, like a smaller ground pound. Fire it at a crowd and look at the floor around the blast.

- Orks now flinch when you hit them: they recoil away from the shot or blow (torso well back, hips down, knees bending, feet staying put) while they keep running or swinging, harder for heavy weapons, hammers, charges and close explosions, and only a little for Nobs and the big Orks. Far crowds jolt and lean the same way.
