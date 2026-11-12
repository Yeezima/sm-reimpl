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
### Classic PvP
- Bots no longer blow themselves up with their own grenades: they throw frag grenades only at enemies well outside the blast, aim them where the enemy will be, and no longer jump, charge or run into their own grenade before it goes off. They also stop firing plasma cannon and charged plasma shots at enemies (or walls) so close that the blast would catch them, and a heavy that is too close for its plasma cannon now draws its pistol instead of waiting.
- Shooting another player no longer draws hit effects on him: no fiery burst and smoke cloud (the Stalker Bolter's was the biggest), no sparks, flashes or burning. You still see his armour flash and glow, hear the hit and get the hit marker; shots that miss still mark the walls and floor.
- A bull rush (the charge) is no longer cut short by pressing the grenade key, with or without grenades left (a grenade pressed just before the charge ends is thrown as soon as it ends). To cut a charge short, tap or hold S (backwards, also S with A or D) once it has run about half a second.
- Charging into a teammate no longer stops you or plays the recoil: your charge slides past him and carries on, and nothing happens to him. Charging into an enemy works as before.
- This build does not play with earlier builds: everyone, and the server, needs this update.
- New heavy weapon in the Extended content setting: the Devastator and the Havoc can take the autocannon (last in the heavy weapon list and in the custom class creator). It fires slow full-auto rounds that hit as hard as a Stalker Bolter shot, with the same headshot bonus, and no explosion; it has its own original autocannon sound, 400 rounds, and overheats after 15 rounds. The Faithful setting does not have it.
- New heavy weapon in the Extended content setting: the Devastator and the Havoc can take the autocannon (last in the heavy weapon list and in the custom class creator). It fires slow full-auto rounds that hit as hard as a Stalker Bolter shot, with the same headshot bonus, and no explosion; it has its own original autocannon sound, a 120-round magazine that ammo drops refill like the heavy bolter's, and overheats after 15 rounds. The Faithful setting does not have it.
- A Devastator carrying the autocannon wears the heavy bolter's backpack.
- The Havoc's autocannon shows the Chaos look all the way through: purple muzzle flash, purple tracers and purple impacts. The Devastator's keeps its warm loyalist flash, tracers and impacts.
- Classic PvP with the Extended setting in this build does not play with earlier builds: everyone in an Extended match, and the server, needs this update. Faithful matches are not affected.

### Musou Mode
- The power spear's animations have had another pass: standing still he now carries it as in your reference (hips square to you, the right arm hanging beside the hip, the blade pointing forward and up), the three light hews, the F snap cut and the F, F stab have a short compact wind-up, speed up into the blow, hold the finish only a moment and recover quickly, and the hews flow from the carry and into each other without popping; running and sprinting keep the older chest-height carry for now, because a hip-height hand put the shaft through his legs.
- The power spear is now carried the way you asked: one-handed in the right hand at the hip, the shaft running along his spine (it leans with him as he runs and sprints, blade up, butt low, tipped a little inward so the blade stays out of the crosshair), the right arm swinging slightly and the left freely, the hand on the shaft with the arm connected to it; the hews, the snap cut and the stab are as you liked them. A spear or armour passing through the body for a moment no longer counts as a fault.
- The power spear's hands got another pass: standing, his left hand is now the bolt pistol idle's own; running, both arms pump against the stride (the left one bent at the elbow, rising as it comes forward, the spear hand carrying the shaft with a small swing) and the right hand closes its fingers round the shaft; the three light hews, the F snap cut and the F, F stab have a more compact wind-up, accelerate harder into the blow and carry the hips through after it. The slight tilt that keeps the blade off the crosshair is unchanged.
- The power spear's hands got a second pass: the hand now grips the shaft finger by finger (no finger through it, the wrist turned so the shaft crosses the palm), the left hand reaches the shaft when it takes it, the running left arm no longer flickers at the elbow, the carry leans a little forward, and the three hews, the snap cut and the stab have a tighter wind-up and more weight in the follow-through.
- The Librarian's chain lightning now leaps twice as far from Ork to Ork (up to 16 m) and no longer weakens down the chain: every Ork it strikes, the last as the first, takes the full damage.
- Orbital strikes you call now do 20 % more damage to the Orks too, which bosses feel most (ordinary Orks die in the blast either way).
- In Musou Mode too, the grenade key no longer cuts a charge short; tap or hold S instead (charging into a teammate slides past him there too).
- The autocannon is much stronger: each round's explosion now kills any ordinary Ork (Boyz, Shoota Boyz, Gretchin, Rokkit Boyz, Stormboyz, Squigs) inside its blast that it reaches (not behind cover or a Mek's shield), and the round itself hits even harder (30 more than the blast). Its ammunition never runs out (an infinity sign in the ammo box); it still overheats after 15 rounds.
- The autocannon is much stronger: each round's explosion now kills any ordinary Ork (Boyz, Shoota Boyz, Gretchin, Rokkit Boyz, Squigs) inside its blast that it reaches (not behind cover or a Mek's shield), and the round itself hits even harder (30 more than the blast). Stormboyz are tougher than that: one explosion leaves them hurt but standing.
- The autocannon has no magazine: it never reloads and keeps firing as long as you hold the trigger (an infinity sign in the ammo box). It still overheats after 15 rounds, so watch the heat bar.
- No gun loses damage with distance any more: every shot from every gun (bolters, Stalker Bolter, lascannon, plasma, the autocannon and the rest) hits as hard at long range as up close, and the melta burns at full strength to the end of its reach. Grenades and other explosions still hurt less towards the edge of the blast.
- The Devastator now wears the heavy bolter's backpack when carrying the autocannon.
- Musou Mode in this build does not play with earlier builds: everyone in a Musou match, and the server, needs this update.

### Deaths and hits (both modes)
- An Assault Marine or Raptor shot down in the middle of a jump-pack jump now plays the original's mid-air death: the body jerks from the hit as the pack bursts into flame, tumbles down head first, slams into the ground with a crater and smoke, and only then goes limp. Shoot one out of the air to see it.
