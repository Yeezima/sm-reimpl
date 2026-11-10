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
- Musou Mode has loadouts of its own now, kept apart from your Classic loadouts: the first time you open Musou's customizer they start as a copy of your Classic ones. The Librarian gets his own loadout too: melee weapon, equipment, two perks and a look. Opening an older build of the game can reset your Musou loadouts.
- Musou Mode loads in a few seconds instead of half a minute: the battlefield's layout is built once per build and kept in the game's private cache, and the loading screen now says what it is doing (reading the map, building collision, drawing the map, preparing looks) instead of standing still while the map loads.
- Fewer stacked sounds in big fights: the game's own limits on how many copies of one sound play at once now apply to every sound (before, only to footsteps), so bolter fire, hits and blasts no longer pile up on themselves. Please report any hit, kill or voice sound that seems to go missing.
- Press F3 in a match for a performance readout in the top right corner (off by default; F3 again adds the network: your ping, snapshot loss, interpolation delay and input slack; a third press hides it). Please have it on when you report stutter or lag.
- In old GPU / safe mode Musou Mode's Ashfall always uses the FAST grime look (the Compatibility renderer paid most for the extra pass).
