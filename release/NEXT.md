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
- Tapping Space as an Assault Marine or a Raptor now plays a single jump-pack hop sound. The extra roll-style whoosh and the doubled jump-pack burst that came with it are gone, for you and for the players you hear, in every mode.
- The launcher has a new MAPMAKER button next to PLAY. It opens the map editor (the Mapmaker, in the free Godot editor) on your own Space Marine install, so you can build and test your own maps. The first press asks before it downloads the Godot editor once (about 111 MB; plus a small .NET download only if your PC has none) and checks it, then prepares the editor for a minute with a progress note. Press MAPMAKER again any time to open it; maps you save stay on your PC.
