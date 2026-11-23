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
- Warfront: your level, XP, cards and extra retainers now stay with you across campaigns (after a win, a defeat, a new campaign or an update), saved on your own PC. Max level is 50.
- Warfront: RESET PROGRESSION on the Warfront menu sends you back to level 1 (it asks first; one safety copy is kept).
- Warfront: new players start at level 1 instead of catching up to the others. The Orks still scale with the highest level in the group, so a new campaign with a high-level player starts harder.
- Warfront: a damaged campaign save is set aside (not overwritten) and you are told; a save from a newer build or one that cannot be read is left alone with a message; a failed save no longer wipes your campaign.
- Warfront needs this build: earlier builds cannot join a Warfront game. Classic PvP is unchanged.
- Classic: after a map change on a rotating server you now spawn with the loadout you last saved or deployed in the match, not the one you first joined with. Your side choice is still asked again (Automatic stays Automatic).
- Warfront: rockets, plasma and launcher shots now hit where the Orks are, not where they just were: slightly less forgiving against side-stepping Orks. Instant shots (bolters, lascannon) are unchanged.
- Classic: the spawn point you pick on the death screen can no longer be overridden by an older choice that arrives late over a bad connection; your last pick is the one you respawn at. New network version: everyone must update, and earlier builds cannot join a Classic game on this build.
- Classic: a light melee press during a light swing queues your next combo hit again at any point of the swing (as in the original), instead of being dropped before half a second. The other melee timings from the last build stay.
- Warfront: dense fights with piercing weapons (plasma, lascannon, piercing bolters) cost the server far less, so big hordes should hitch less. Hits and damage are unchanged.
- Options: if your settings cannot be saved (a read-only or full drive), the page now says SETTINGS NOT SAVED instead of SETTINGS APPLIED. Dragging a slider no longer writes the settings file on every step; a change is still saved when you leave the page, start a match or quit.
