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

- New network version: this build does not play with earlier ones, so everyone must update together.
- No shooting while sprinting, with any weapon: while you sprint (Shift and W) the trigger does nothing, the lascannon included. A charge you were holding is lost when you start to sprint, and letting go of the trigger mid-sprint fires nothing. Let go of Shift to shoot: the gun comes up as it always did after a sprint (the heavy weapons, melta and grenade launcher at once); single-shot and charge weapons need a fresh click. Holding the trigger no longer slows your sprint.
- Devastator and Havoc: the stomp can no longer be cut short by shooting, aiming, melee or any other button; only a hop (Space) cancels it, from the same moment as before.
- Devastator and Havoc: running or sprinting off right after a stomp no longer leaves the arms stuck in the stomp pose over running legs; the arms now go into the run or sprint as soon as you can move. The same goes for the knife strikes and the heavy classes' kick.
- Spent bolt casings drop to the ground much faster instead of floating.
- Controller: the B button now closes the open menu or goes back one level, like Esc (the in-match menu, loadouts, armour and colours, controls, dialogs, and the connecting screen). While a controls entry waits for a new button, B is bound as usual. In a match B still uses and picks up, and the B that closes a menu no longer also picks something up.
