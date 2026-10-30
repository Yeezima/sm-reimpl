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

- The launcher has a dark look.
- The Devastator and Havoc hop now goes the way your movement keys point, like the Tactical roll: W+D hops forward-right, S+A back-left, and so on, in all eight directions (no key held: forward). This changes the game rules, so everyone must update to the same build.
- There is a Field of view slider on the Join screen and in the customizer / Esc menu: from the original look up to 120 degrees (measured across the screen), live while you play. Scopes keep their own zoom.
