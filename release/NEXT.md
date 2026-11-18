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
- New network version: everyone must update to play together, and the servers are updated with this build.
### Menus
- Every menu has the new look: Classic PvP, Host, Join, Musou, Options, Keybinds, Appearance, the Armoury, the pause menu, dialogs and the Musou score, reward, war map and range pages, all in the same steel-and-gold style.
- The main menu matches the concept art: the full SM-REIMPL title, the stone wall and the gold frame, with MUSOU MODE first.
- The scoreboards of every mode and the match results use the same style.
### Classic PvP maps
- The ten newer maps look closer to the original: painted decals on walls and floors (Manufactorum too), window light falling on walls, Dome Mechanicus' warmer interior colour, Basilica's and Hab Center's ceiling fans turn and Basilica's lava flows.
- Banners and wall-mounted Mechanicus pieces that were missing on Hab Center and Station Tertius are back.
- Map edges and roofs follow the original's invisible walls, loose cover (crates, sandbags, rubble) stands where the original has it, and you slide off surfaces the original does not let you stand on.
- Desolation's big concrete walls stand in the right places.
- The autocannon has a new firing sound for both sides; tell us how it sounds while moving.
### Musou Mode
- The Dreadnought's guns fire a torrent of tracers, and its screen shake (shooting, swings and punches) is much gentler.
### Mapmaker
- Every library asset shows a preview, box select works when you start dragging over objects, and placing an asset shows a ghost you can adjust (left click places, right click or Esc stops).
- Drag a glTF/GLB file or a zip with FBX and textures into the Mapmaker to import it; custom assets and prefabs can be renamed.
- PLAY TEST works in the tester install, the Mapmaker works in every scene tab, and an extracted prefab no longer pulls in neighbouring objects.
- The dock is reorganised into a clearer order, with a New empty map button.
- Typing a rotation or position now changes only what you selected: a selected child turns on its own.
