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

- This build does not play with earlier ones: everyone needs to update.

### Classic PvP
- Thunder hammer and daemon maul: you can swing at any point during a hop, the recovery after a heavy double swing is shorter, and the slam can be steered while it winds up.
- Headshots now play the original headshot sound, give +50 points and a medal.
- New black-and-white scoreboard showing each player's emblem, and a black LOADING screen.
- Bots hold the centre, spread over different routes, pick varied loadouts, never camp your spawn, only push along covered approaches, and back off to heal at low health. On Manufactorum one Devastator holds a balcony and one Tactical a ramp top per side.
- Manufactorum: the red warning-light props near the spawns are gone.
- Plasma pistol and plasma gun tracers now show in GPU safe mode.
- Bolter and relic bolter: the last round of the clip shows correctly on the HUD.
- Melta: the burn effect appears the moment a shot hits and covers the whole body.
- The crosshair scales with your field of view (the original field of view keeps the original size).
- The knife and your Versatility weapon now sit in sensible places during and after melee.

### Musou Mode
- New Orks: Bomb Squigs, Rokkit Boyz, Stormboy squads, the Big Shoota Nob with his own gun and sounds, and Scrap Meks who build shield domes, beacons, turrets and traps.
- The Librarian is playable: force sword, chain lightning, Warp Pull and Psychic Shockwave, all sharing a Psychic Stress gauge.
- Battles now flow: Ork waves march on held points, Guard garrisons of up to 200 defend them, armies retreat and rally, six AI Space Marines fight alongside you, and if you hold no point the Orks march on your base.
- If your local Musou server doesn't match your game's content, it now stops and you rejoin silently.
- Big performance pass: the long freezes during mass deaths are gone, distant fights cost far less, and far-away bodies are no longer drawn.
- New graphics options: Upscaling (FSR 2, off by default, Forward+ renderer only) and Ashfall grime (Exact, or Fast for weaker graphics cards).
