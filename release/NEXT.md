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
- Musou Mode: your personal retainer is back, beside the two roaming allied Space Marines. Z tells it to follow you, X to hold the spot under your crosshair and V to attack what you aim at (in Musou X and V give orders again: the mouse wheel still changes weapon and the right mouse button still strikes); it revives you when you fall and is back about three war-minutes after it dies.
- Musou Mode: Orks no longer drop ammunition or equipment boxes when they die (you already have endless ammunition).
- Musou Mode: grenades work as in the normal game again. One press throws one frag with the normal blast size and damage (no more three big frags), and it can hurt you if you stand in it. A grenade you have used comes back every 15 seconds. This build does not play with earlier ones.
- Musou Mode: Orks thrown by a grenade or any other blast no longer corkscrew through the air. Each body now flies in one clean arc straight away from the explosion, tips over as it falls and stays down where it lands.
- Musou Mode: a glowing slash appears across every enemy your melee hits - red for the chain weapons and the combat knife, blue for the power weapons - and your own melee hits give the camera a small shake.
- Musou Mode: every melee weapon now cleaves a wider area around you (1.5 times the old reach). This build does not play with earlier ones: everyone must update.

- Musou Mode: Orks turn to face you more. Gunners no longer shoot while looking away or running off: they turn to you first (a Shoota Boy, Rokkit Boy or Big Shoota Nob fires only when facing within about 30 degrees of you), and Orks near you back off, circle and close in facing you. The build does not play with earlier ones in Musou Mode.
- Musou Mode: one grenade throw sends at most 20 Orks flying, the ones nearest the blast. Any other Ork it kills explodes on the spot, and small Orks that survive it are knocked to the ground. Everyone must update: this build does not play with earlier ones.
- Musou Mode: every plasma weapon (plasma gun, plasma pistol, plasma cannon, light and charged shots) now pierces every enemy its shot passes through, and each enemy it hits burns with blue-white flames: about half the hit's damage again over 3 seconds. Bosses burn slower. Hit a crowd in a line and watch them all light up.
- Musou Mode: the Warboss, the Nobs, the giant Boss Orks, the Big Shoota Nob, the Weirdboy and the Scrap Mek are now solid. You can no longer walk, sprint, charge or fly through them; the ordinary Boyz and Gretchin still let you pass. Your melee attacks and charges now lock onto these big Orks the same way they lock onto enemy players, so you line up and step in on them.

### All modes
- Every game sound is louder and carries much farther. Distance now turns a sound down to a fifth of its full level at most, so far-off fights stay audible instead of cutting out.
- Melee weapons hit twice as loud as before, and their hit sounds carry over distance like gunfire.
- Shots no longer leave hit effects stuck to players. Bullet and bolt marks only stay on walls, floors and other map surfaces.
- The plasma gun and plasma pistol light shot now looks like a smaller charged shot: a glowing ball with the charged shot's trail, softened at both ends. The ball flies at twice the charged shot's speed (the hit itself still lands at once), and the plasma pistol gets a tracer.
- Two new rows in Options: Camera shake and Screen flashes. They start at the old strength; turn them down if shake or flashes bother you.

### Classic PvP
- Annihilation bots play as they did in the previous build; the newer bot behaviour (safer pushes, cover, holding posts) stays in Seize Ground only.
- The headshot medal no longer shows a description line under it.

### Musou Mode
- Guardsmen spread out in open order on the way to a point and fan out round it once it is taken, and no longer snag on corners or hug walls.
- Air jumps with the jump pack (Assault Marine, Raptor) now make the same launch sound as the first jump.
- A far-off battle now sounds all around you: Ork war cries, bolter and lasgun fire, artillery and explosions, with the odd artillery hit shaking the screen a little. You and the allied Marines also shout battle lines from the campaign.
- Stormboyz fly smooth arcs when they hop, and Orks and Guardsmen glide instead of stepping, so crowds no longer look choppy.
- Ashfall: the boundary wall and the map's models no longer flicker, with FSR on or off and in GPU safe mode.
- Power sword strikes and bolt pistol shots react at the moment they connect on your screen: the slash, flash, camera shake and hit sound no longer wait for the server. Bolt pistol shots now stop at the Ork they hit. Kills, blood and hit markers still come from the server. Musou screen shake no longer piles up, and ordinary Orks are pushed back from your hits without spinning round.
