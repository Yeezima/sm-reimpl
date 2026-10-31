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

- The Assault and Raptor air slash now lands in a blue-white energy burst at the impact, with a blue light that fades in under half a second (the earlier dust cone is gone).
- New network version (49): everyone must update, older builds do not play with this one.

- The launcher window is readable again: no more overwritten or garbled text, nothing overlaps at 125%, 150% or 200% display scaling, and long status lines wrap inside their own space.
- The score box at the bottom left of the screen now holds its contents centred: the team scores, the two bars, the time left and the score limit sit in the middle of the black box with even room on every side, and nothing touches its edges, even with three-digit scores or a timer past ten minutes.
- The charge now needs the sprint as well: hold Shift and W, then press F. F without Shift is the stun strike in the direction you are holding (W, A, S, D or a mix, forward when none), so W + F alone is now a forward stun strike. On a controller, hold the sprint (left stick click) with the stick forward and press Y to charge; Y otherwise is a stun strike in the stick's direction.
