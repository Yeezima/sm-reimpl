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

- Musou Mode: the Thunderhawk's troop bay now ends in an invisible wall at the top of the ramp, so you can no longer walk or jump down it and see the unfinished scene beyond. The planet still shows through.
- The menus have a new background picture, and the Thunderhawk hub shows a new planet picture.
- The launcher no longer has the "Host a match on this PC", "Join a server..." and "Host for friends (LAN)" buttons: hosting and joining are done in the game now. PLAY is the only game button left.
- Sounds: the chainsword, the combat knife and every charge (Tactical, Assault, Chaos Tactical, Raptor) are back to their original hit sounds; only the power sword and power axe, and the thunder hammer and daemon maul, keep the new ones, and those are 40% quieter.
- Gore: flying blood no longer hangs in the air; every drop, clot and stain now falls to a real surface and lands on it, and one that finds nothing under it simply disappears.
- Musou Mode: the status text over each point (HELD, SECURING and so on) is smaller and only shows while you aim at the point; the letter is always visible.
- Classic matches: the dark gore effects are removed from classic PvP completely (Extended and Faithful alike keep the original blood); they stay in Musou Mode only.
- Gore: a big mass kill (many Orks at once, an orbital strike) now costs far less: blast clouds in one spot merge into a few large banks, the number of flying drops per frame is capped, and the cloud shader is cheaper. A single hit still makes its big cloud.
- Gore: blood on the floor and the flying flecks are now matte and dark in sun and shadow; they no longer show as bright white-grey shiny splashes.
- Musou Mode: the final boss now starts in the open courtyard just outside his room, and he is always a big, tall Ork, never a small one. Play with everyone on the same new version.
- Musou Mode: Orks trickle out of every point still in enemy hands and walk to you, on top of the crowds, so about twice as many Orks are on the map. Capturing a point still pauses its trickle and a full capture stops it.
- Musou Mode: the reward cards after a won map are switched off for now, so a run flows straight on. The scores still show.
