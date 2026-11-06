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

- This build uses a new network version: everyone must update to play together.
- Musou Mode: Ashfall now has its full detailed look in the tester build (walls, towers, bridges, platforms, paving and rubble), the same as in the developers' playtests, instead of the plain grey blocks. The download is about 400 MB larger because of it.
- Musou Mode: the Imperial Guardsmen holding your points are much tougher, your reserve ammunition never runs out (you still reload and vent), a grenade comes back every 30 seconds, and the AI Space Marines fighting beside you take far less damage.
- Musou Mode: Boss Orks face where they run, no longer shuffle against wall corners, and patrol columns no longer stall waiting for stragglers.
- Musou Mode: you can walk and shoot through the gaps in broken walls, rubble and cover on Ashfall instead of bumping into invisible walls.
- Classic matches: bots aim and react more like players (less accurate at range, a short reaction delay), and the jump pack has the original's fuel.
- Plasma: your own plasma ball appears without delay, the gun no longer kicks the body, and the muzzle flash stays on the gun.
- Hit effects on players stay on the player who was hit, hit confirmations fade with distance, and regenerating armour shows a yellow glow.
