# Warfront: playtest notes (local campaign, first draft)

What this is: a 30 to 60 minute campaign on this PC, built from the owner's brief ("hard generally; a solo player must play well and be determined to win").
Everything is a first draft and every number is an estimate (`docs/refine-register.md`). Details of the rules: `docs/warfront.md`. What was proven and what
was not: `docs/warfront-test-report.md`.

## Start (two steps, PowerShell)

```powershell
dotnet run -c Debug --project src/SmTool -- warfront-maps --prebuild   # once, about 5 minutes warm, up to 30 cold; the Godot client is a Debug build
.\client.cmd                                                          # main menu: WARFRONT, NEW CAMPAIGN (CONTINUE CAMPAIGN after the first run)
```

(`.\smtool.cmd warfront-maps --prebuild` builds the *Release* set, which only the dedicated server `server.cmd` reads; the client does not read it.)
The campaign's server is started by the client for this PC only (127.0.0.1, one human) and stopped when the client goes back to the menu; quitting
mid-run and choosing CONTINUE CAMPAIGN resumes it (a battle resumes on the ship before it, nothing rolled or granted for it). The first start after a
change of build re-generates layouts (20 to 50 s a map, behind the loading screen).

## Keys Warfront adds

- **1 to 4** (or the D-pad): the card picker; you cannot be hit and cannot act while it is open, the others play on.
- **F4** aboard the ship: the squad panel (names, records, the Librarian's squad class). **J** in the field with a jump pack: mark a jump-and-pound.
- **M held**: the war map (read only in a battle). Revive a downed Marine or player: hold Use next to them.
- Deploying is the leader's (you, alone): the war map at the Destination station.

## Where the save is, how to start over

`%LOCALAPPDATA%\sm-reimpl\warfront\run.json` (and `records\`, `exports\`, `client.json`, the key kept on this PC). Start over: NEW CAMPAIGN on the
Warfront page, or the results panel's NEW RUN; a finished run is archived under `records\` and never read by the next. The results panel's copy for
Discord is written to `exports\`.

## Known, not bugs

- A short freeze on the travel screen while the next map's view is built (measured in the test report: seconds; the window stays up behind the loading screen).
- First draft look: the fortress and the HQ are dressed with plates, not the game's pieces; the HQ's interior is not dressed.
- Solo you get two allied Marines in lane battles only (none in the HQ defence, none in the fortress): the owner has asked whether they should rather be extra
  retainers; until answered the solo HQ defence is much harder than it will be.
- The fortress Warboss: with you waiting outside the plaza he walks to an opening, pins on the wall for 2 to 3 s and is put back on his spot (about every 10 s); he
  is at full pace once anyone is inside. His model stands about 5.9 m while his hit body is 13 m (Musou's own row).
- At the HQ the Orks queue at the keep's doors during the collapse (about 8 % of them); a failsafe lifts them out.
- (Fixed 2026-11-19, claude/warfront-replaces-musou: the customizer's Back returns to the Warfront page, and the host panel, the armoury badge, the sound tab and the customizer caption say Warfront.)

## What to check (at most fifteen)

1. Difficulty: "hard generally"? A solo run: where did you die, to what? (R-8E33AFE7 and the damage card R-B6869371 feel; the Ork scaling table in the report.)
2. Length: how long did a complete run take (30 to 60 minutes wanted)? Which node took longest?
3. Do the three bands read differently (wasteland, outskirts, urban core), and is D (north centre) easy to find? (R-855582BE)
4. The card picker: does it interrupt fights, is 20 s long enough, is the retainer card worth taking?
5. Squad strength: do nine Marines feel like about two players? Do they follow you, jump, revive? (R-D446885C)
6. Revive: the 25 s window, the 60 s spectating, the return at the landing zone. (R-5FDBEE7A)
7. The rolls after a deployment: is the war map (M, the ship) easy to read, and is "held at the floor" understood?
8. HQ defence: 8 minutes; do W, K and V read as three stages; is the wave size fair?
9. Fortress: reaching the plaza, the Warboss fight, any Orks stuck at the walls.
10. Framerate in the urban core and the plaza (stutters, the 36-retainer case needs four players, not tested here).
11. Quit mid-run and CONTINUE CAMPAIGN: nothing lost, nothing doubled. (R-5660ED7E)
12. The results panel and the Discord copy: readable, the right numbers, a sensible top retainer.
13. First start: how long was the first deploy (the layout cache), did the loading screen stay up?
14. Anything that looks wrong at the edges of a map (neighbour scenery, the spire, the ground outside).
15. Anything that felt like Space Marine 1 and anything that did not.
