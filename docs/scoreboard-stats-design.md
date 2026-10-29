# Scoreboard statistics the wire does not carry (design note for a later protocol change)

Written with fidelity gap 4's end-of-match screens (branch `fidelity/match-summary`, client only, no protocol change). The original's scoreboard has the
columns level, name, score, kills, deaths, assists, ratio, captures, uncaptures and defends (`SummaryScreen.gfx`; docs/discovery.md "End-of-match screens");
ours shows kills, deaths and the computed ratio, and a dash for the rest. This note says what the server would have to count and send to fill them. Nothing
here is implemented; the protocol numbers 28 to 30 are held by other branches (main is at 27), so the change would be **protocol 31 or later**.

## What the client already has

Every death reaches every client (`KillView`: killer, victim, cause, index), so a client can build per player the kills, deaths, streaks, weapons, classes and
who killed whom (`SmNet/MatchSummary.cs`, used by the battle record). A client that joins mid-match misses earlier deaths; the scoreboard's kills and deaths
(authoritative, from the server) are the totals shown. The wire says who **holds** a control point (`ControlPointView.Owner`), not who captured it.

## What only the server can say

| Column | Why it is not derivable | Server rule needed | Wire |
|---|---|---|---|
| Assists (106099 / `userAssists`) | damage dealt per victim is server state | `xp_settings_pvp` pays assists at 25 / 50 / 75 % of a victim's health + armour dealt: track damage per (victim, attacker) since the victim's spawn; at the death give an assist to every other attacker at 25 % or more | `u8` per scoreboard row |
| Captures, uncaptures (`userCaptures`, `userUncaptures`) | who stood in the point when it flipped | credit the players of the capturing side inside the point's cylinder when its owner changes (capture 200 XP, uncapture 50 XP in the data); the exact rule (all inside, or the one with the most time) is not in the data | `u8` + `u8` |
| Defends (`defended_txt`) | needs the original's definition | a kill within the radius of a point the killer's side holds, or of the victim's capture attempt (guess; the data names the column and no rule) | `u8` |
| Score (`userScore`, `stat1_txt`) | the original's per-player score is the experience-style tally of its reward table | sum the 32 rewards of `xp_settings_pvp/rewards` that exist here (kill, assist, capture, uncapture, round win, headshot 35, ...) | `u16` |
| Level / rank (`userLevel`, `userRank`) | progression | out of the alpha (everyone has everything); constant 1 until it exists | `u8` (or none) |
| Headshots (106092) | the kill record has no hit-zone | one bit in the kill's cause byte (three bits used, up to cause 5) | none on the scoreboard; a flag in `KillView` |

## Size

A scoreboard row is 12 bytes today (id 4, side 1, class 1, kills 2, deaths 2, appearance version 2); the message is sent about once a second, unreliably, on its own
datagram. Adding score (2), assists, captures, uncaptures, defends and level (1 each) is **7 bytes a row**, 112 bytes for 16 players. The message grows from about 330 bytes
(header 13, Seize Ground block about 126 with three points, rows 192) to about 440, well inside the datagram. The **snapshot is not touched**: it is exactly at its 1200-byte
budget (protocol 23), so per-kill assist ids or a headshot bit in the kill entries are the expensive options; totals in the scoreboard avoid them.

## Slot

Extend `ScoreEntry` (`SmNet/Protocol.cs`; read and write in the `Scoreboard` case) with the new fields and add a counter set next to `PlayerScore(Kills, Deaths)` in
`SmSim/Teams.cs` (`ResolveDeaths` updates kills and deaths there; assists need a per-victim damage ledger in `World`, captures a hook where `SeizeGround` flips an owner).
On the client `NetworkView.ScoreboardText` and `BuildCountUp` replace the dashes and the count-up's second stage can count the player's score as the original's
`playerscore` stage does. The match restart already resets the scores each round.
