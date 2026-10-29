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
- Stomps and slams now hit the ground hard: cracks, a shockwave ring, a flash and chunks of floor thrown up, sized to how far the stomp reaches (no dust or smoke).
- The power sword glows blue while it is in hand and flares bright with sparks when it hits someone (Chaos swords too).
- Jump-pack thrusters: the flames start right at the nozzles and the trails are much shorter, closer to the original game.
- New default lighting: the "remaster" look (cleaner air, higher sun, more contrast). The old look is still there with --look current.
- Manufactorum has no more god rays (light shafts).
- Crosshairs are now the original game's own reticles for every weapon: the thin ring with four ticks for bolters and bolt pistols, the plasma, melta, heavy bolter and grenade launcher shapes. The ticks open while you move or keep firing and close when you aim, and the reticle turns orange over an enemy.
- The stalker bolter and the lascannon show no crosshair until you look through the scope, as in the original.
- The hit marker is the original's four gold ticks.
- The kill feed, the "+50 Kill" reward line, the team score panel, the weapon box, the Seize Ground point markers and the "Pick up Ammo" prompt now use the original game's HUD pictures and layout. The weapon box's heat bar now empties as the weapon heats up, like the original.
- Trial: the camera trails your aim very slightly (about 50 ms), so turning feels less stiff. Your shots still go exactly where the crosshair is, and the crosshair may drift a little off the centre during fast turns. Tell us whether you like it.
- The class creator's tabs now show the whole class name (for example "Tactical Marine / Chaos Space Marine") instead of cutting it off.
- Assault and Raptor with the thunder hammer or the daemon maul: the marine now stands, runs and sprints with the hammer in both hands, holsters the pistol while doing so, and swaps back from the pistol with its own animation. The hammer's charge uses its own hammer swing.
- Melee strikes no longer slide: a foot the swing plants stays where it was put down while the body lunges, stops at its target or turns to it. Chainsword, power sword, axe and hammer swings blend in, chain and settle back to running with the timings of the original's animation files.
- A small camera shake when your stun strike (F) lands, a bigger one for stomps and hammer slams, and one when your charge or dash attack connects.
- Charging off a ledge: when you land, the marine goes straight into running instead of starting the bash again. Side charges and lunges turn the body over a few frames instead of snapping.
- The jump-pack air slash no longer jerks its sword forward when you land earlier than the swing expected.
- Switching from your primary weapon to the bolt pistol (and back) now puts the gun away in your right hand, round the right side onto your back, instead of the gun flying round your left side on its own; the pistol goes back to its thigh holster the same way.
- Bots now have names that fit their side instead of "P1", "P2": Space Marine bots are Brothers and Sergeants, Chaos bots carry Chaos names and epithets, and Ork bots get Ork names. You see them in the kill feed, on the scoreboard (Tab), over the crosshair, above allies and in the end-of-match summary. Players without a name still show as P and a number.
- New network version: this build does not play with earlier ones. Everyone, server hosts included, must update.
