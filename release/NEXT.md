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
- In the loadout and armour screens every class now stands in the bolt pistol's relaxed idle pose, and the figure no longer turns by itself while you pick colours; drag it with the mouse (or use Left / Right) to turn it.
- With Content set to Extended you can now choose the Ork Boy in the menus: an Orks button and an Ork Boy tab in the loadout screen, an Ork Boy default loadout on the Join screen, and custom Ork loadouts with the Tactical Marine's perks.
- The Ork Boy has no armour or colours: his LOOK step lets you pick one of five Ork models (or Automatic), and your Ork wears that look in the match. In Faithful the Ork is hidden and a saved Ork loadout joins as the side's default class.
- Charging off a ledge no longer replays the shoulder bash when you land: the charge ends on landing and you run on if you hold a movement key, or slow down to a stand if you don't.
- Tactical and Assault marines (and their Chaos counterparts): press F twice for the full heavy combo. The second F during the backhand now goes into the stomp.
- Melee swings step further forward (about 30 % more). This is an estimate to be tuned in playtests.
- Weapon switching is a bit faster (0.48 s instead of 0.6 s). This is an estimate and still needs to be compared with the original game.
- Melee and stun lock-ons turn your marine smoothly toward the target instead of snapping, keep the same target more reliably, and the camera follows more gently.
- The power sword's hits now use the power sword's own impact sound from the original game.
- This build uses a new network version: everyone has to update.
- Players now collide with each other: you can no longer walk, sprint, roll, charge or get knocked through another marine, friend or foe. Walking into someone stops you and you slide around him.
- Two players who end up inside each other (a crowded brawl, a busy spawn) are pushed apart within a moment instead of staying stuck.
- Landing on someone's head with the jump pack (or falling onto him) slides you off his shoulders onto the floor beside him.
- Melee lunges and the charge stop at arm's length from the target instead of ending up inside him.
