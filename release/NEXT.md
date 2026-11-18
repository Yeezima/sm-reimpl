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
- Classic PvP servers are updated with this build; older builds can no longer join Classic matches, so everyone must update. Musou Mode is unaffected.
### Combat (Classic PvP)
- New animation cancel defaults for everyone: a light melee swing can be cut short by another light melee press or by a charge from half a second into the swing; a charge (both kinds) can be cut short by a light melee press from one second in; an aerial tackle can always be cut short by a light melee press.
### Classic PvP maps
- Hab Center: the floors of the two big halls are back, so the vents, ramps, catwalks and pipes there no longer float over an empty background.
- Maps load with less of a stall: the ragdoll world is built behind the loading screen.
### Menus
- All menu text is one consistent size per level and stays sharp at every window size; buttons and sliders scale with it.
- The Armoury shows the original game's own pictures for each side, with steady slot tiles and option lists that fit.
### Hosting
- Hosting a Classic PvP match with "Who can join" set to "This PC only" always turns on the developer tools.
### Mapmaker
- PLAY TEST first asks which saved map to test and runs exactly that file (fixes "could not be loaded or differs" when testing a map locally); save unsaved edits first.
- Import an original Space Marine multiplayer map for editing: it opens as a new unsaved map with its objects listed in the Scene panel.
- Right-click a custom asset or prefab in the library to rename or delete it (undo works); the delete dialog names other saved maps that use it.
- Grouping objects into a prefab keeps every part exactly where it was.
