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
- Every gun's shots now leave the barrel: the plasma ball, the plasma cannon's shot and the launcher's grenades start at the muzzle (and fly onto the aimed path within a few metres) instead of near your eye, a charged plasma shot is drawn at once with one flash instead of three, and a streak shows the first stretch of a fast shot.

- The Assault and Raptor air slash now lands in a blue-white energy burst at the impact, with a blue light that fades in under half a second (the earlier dust cone is gone).
- New network version (49): everyone must update, older builds do not play with this one.

- The Devastator's and the Havoc's stomp (F) no longer steps or slides you forward: you stay exactly where you stand, and pressing F and left click quickly cannot slide you along. The stomp's rings, shake and sound now land when the foot hits the ground.

- The launcher window is readable again: no more overwritten or garbled text, nothing overlaps at 125%, 150% or 200% display scaling, and long status lines wrap inside their own space.
- The score box at the bottom left of the screen now holds its contents centred: the team scores, the two bars, the time left and the score limit sit in the middle of the black box with even room on every side, and nothing touches its edges, even with three-digit scores or a timer past ten minutes.
- The charge now needs the sprint as well: hold Shift and W, then press F. F without Shift is the stun strike in the direction you are holding (W, A, S, D or a mix, forward when none), so W + F alone is now a forward stun strike. On a controller, hold the sprint (left stick click) with the stick forward and press Y to charge; Y otherwise is a stun strike in the stick's direction.
- Charged plasma shots (plasma gun, plasma pistol, plasma cannon) now leave the instant you let go of the fire button, with no short delay. The lascannon fires the instant you press the fire button; it no longer charges while held.
- You can now change your keys and controller buttons: press CONTROLS on the Join screen (next to the field-of-view slider) or in the Esc menu during a match. There is a tab for keyboard and mouse and one for controller; click an entry and press the new key or button, and reset a tab to the defaults if you want them back.
- The medium Space jump (hold a little, not a tap and not all the way) now hangs in the air a bit longer at its peak, easing into the hover and out of it, before you drop. You can still steer, ground pound or air slash from the hover. The tap and the long jump are unchanged.
