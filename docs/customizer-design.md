# Custom-class creator and character customizer: UX design

Designer's specification (2026-10-28) for the owner's request: a custom-class creator with 10 slots and a character
customizer inside it, matching the original in everything but style. Data: `docs/customizer-and-perks-data.md`.
Architecture: `docs/customizer-architecture.md`. Nothing here is rendered or measured yet; sizes are specifications.

**Where this design and the architecture differ, the architecture wins** (the protocol owner implemented it): messaging is
idempotent resends plus receiver-pulled blobs (not a reliable `SetLoadout` channel); the profile lives in `user://`
(`%APPDATA%\sm-reimpl`), not `%LOCALAPPDATA%\...\profile`; emblems are imported up to 8 MB / 4096 px and stored as 128x128 BC3
(16,384 bytes) named by hash, not as PNG. Appearance profiles are per race (up to 10), each loadout slot points to one per race.

## 0. Facts, assumptions, decisions

Facts (repo): no front-end menu exists (`client.cmd --connect` joins at once; class/choices come from CLI args,
`LoadoutRequest`, `RequestedClass`, `RequestedSide`); Esc frees the mouse; the death screen shows "KILLED / RESPAWN IN n"
(`Hud.cs`); key C is unused. HUD style: near-black panels (0.03, 0.03, 0.03, 0.78), 2–3 px gold borders (1, 0.8, 0),
white emboldened default font with a dark outline, 1080-high virtual space; own side gold, enemy light red
(1, 0.38, 0.32). The scoreboard uses real Controls (`PanelContainer`, `StyleBoxFlat`, `RichTextLabel`).
`CharacterViewer` + `MarineAssets` build characters; `ModelViewer.AddCameraAndLights` is a preview rig;
`MarineAssets.PaintOverride` is static and must become per instance. The snapshot budget is full: appearance cannot ride
in snapshots. Data: perks per class Tactical 20, Devastator/Havoc 14, Assault/Raptor 14, 2 perk slots; 60 palette
colours in 8 groups; 47 chapter/warband presets (31 SM, 16 Chaos); badges 89 SM (one blank), 17 Chaos; armour pieces per
slot (SM head 7, torso 7, backpack 7, shoulders 7, arms 6, legs 8; Chaos 7, 9, 7, 9, 9, 7); Raptor head/legs and
Devastator backpack forced; decal UV degenerate on Havoc, Raptor and the Chaos right pad; `pattern_enabled` not applied
yet; eye-lens colour not in the data.

Assumptions: fixed Tactical/Devastator pistol and melee shown read-only; pistol-slot bolters need the True Grit perk;
pattern OFF renders secondary = primary; weapon perks without their weapon are allowed with a warning.

Decisions (ours): 1) a minimal Join screen is added (launch without `--connect`); `--connect` with args skips it so bots
and tests are unchanged. 2) In-match changes use a reliable `SetLoadout` message applied at the next spawn (server
validated) and a separate reliable appearance transfer (protocol bump). 3) The creator opens in a match only while dead,
spectating, in warm-up or after the match (C on the death screen, Esc menu entry). 4) Appearance profiles: see the
architecture (per-side appearance slots; each loadout picks one). 5) Shoulders added to the part list (pads carry the
badges); backpack read-only when forced. 6) Eye-lens colour only if the renderer can do it; otherwise the section is
hidden (no dead control). 7) Custom emblems: server flag `--no-custom-emblems`, client option "Show other players'
custom emblems" (default on); stored as a 128x128 RGBA image, never the source path. 8) Combat Stimulants shown disabled
("Not in this build yet"). 9) Every perk row says "NOT ACTIVE YET" until its effect exists.

## 1. User and goal
An alpha player who knows Space Marine multiplayer: set up a loadout and a look, then get into a match, in as few steps
as the original and with the same information.

## 2. Flows
**F1 Before joining (Join screen).** Host:port (last remembered), side Auto / Space Marines / Chaos, the 10-slot strip
with the ACTIVE slot marked; Customize opens the creator; Join connects with the active slot's class and choices plus
the appearance transfer.

**F2 In a match.** The death screen adds "[C] CHANGE LOADOUT": opens the creator with the mouse free and no game input
sent; banner "Applies when you respawn" with the respawn countdown; DEPLOY WITH SLOT n sends `SetLoadout`; if the timer
ends first the creator closes and the pending change stays (or falls back with a status line if rejected). While alive C
does nothing and the hint reads "Available when you are down".

**F3 Choosing a slot.** Chips show "ACTIVE" with a star; Enter on a chip (Join screen / deploy) or SET ACTIVE sets it.
An empty/invalid active slot falls back to slot 1, then the server default, with a status line.

**F4 Slot management.** Slots 1–10 always available (no level gate for now). Slot 1 pre-filled with Tactical defaults;
2–10 "Empty" (a card with three class buttons). Rename (F2): max 20 printable characters, empty reverts to "Loadout n".
Copy to… (confirm when replacing; name + " (copy)"). Reset (class defaults, keeps class and name; confirm). Clear
(confirm). Revert (drop unsaved edits). Save (Ctrl+S): writes the whole profile atomically (temp then rename) to
`%LOCALAPPDATA%\sm-reimpl\profile\`. Leaving or switching with unsaved edits asks Save / Discard / Cancel.

**F5 Editing a loadout (one screen, four steps).** 1 CLASS (Tactical, Devastator/Havoc, Assault/Raptor; changing class
resets weapons, equipment and perks, confirm if edited; names follow the preview side). 2 WEAPONS: Tactical primary (7)
with pistol "Bolt Pistol" and melee "Combat Knife" shown "Fixed"; Devastator/Havoc heavy weapon (3), pistol fixed;
Assault/Raptor pistol (4) and melee (5), primary "None (jump pack class)"; pistol-slot bolters "Requires perk: True Grit".
3 EQUIPMENT: Frag Grenades, Blind Grenades (files: krak), Combat Stimulants (disabled). 4 PERKS: Perk 1 and Perk 2,
grouped CLASS PERKS / WEAPON PERKS; the class default perk pre-filled; no duplicates; weapon perk without its weapon
warns "Needs <weapon>; no effect without it"; removing True Grit resets a pistol-slot bolter with a notice.

**F6 Customizer (APPEARANCE).** Sections: CHAPTER (presets filtered by side: three swatches + name; focus previews,
Enter commits); ARMOUR (rows Helmet, Left shoulder, Right shoulder, Torso, Left arm, Right arm, Left leg, Right leg,
Backpack; piece names from the install's locale, read in memory; forced pieces disabled "Set by class: Raptor");
COLOURS (part: All parts or one; channel Primary / Pattern colour = secondary / Detail colour = tertiary; 8 labelled
palette groups; focused swatch name printed below the grid; PATTERN ON/OFF and "Apply to all parts"); BADGES (left pad,
right pad: None, Custom image…, My emblems, chapter badges with variants; a pad that cannot show a decal is disabled with
"This pad cannot show a badge"); EYES (palette picker with "Default" first; see decision 6).

**F7 Emblem import.** Native Godot file dialog (PNG, JPG, WebP, BMP, TGA); limits 10 MB and 8192 px per side; no
animated images. Crop modal (1:1): square frame, drag pans, wheel/slider zooms, Fit / Fill, a 128x128 actual-size preview
and a live pad preview; Apply to LEFT / RIGHT / BOTH, Cancel. Apply resamples to 128x128 RGBA, stores a PNG named by its
hash in `profile\emblems\`, adds it to "My emblems" (max 8). Delete on a row removes it (confirm); pads using it revert to
None. Errors shown in the modal: "Can't read this file", "Unsupported format (use PNG, JPG, WebP, BMP or TGA)", "File too large: X MB
(at most 8 MB)", "Image too large: W x H pixels (at most 4096 x 4096)", "Animated images are not supported"; the limits are also
stated under "Custom image..." (a tester's request, 2026-10-29).

**F8 Invalid or missing data.** Missing profile: defaults plus a one-time note. Corrupt profile: renamed to `.bad`,
fresh start, banner. Unknown weapon/perk/piece in a slot: repaired to defaults, chip tag "REPAIRED" and a notice on first
open. Missing emblem file: None with the same notice. Install/content failure: full-screen error with Retry and Quit.

## 3. Screens (1920x1080 virtual, 84 px side margins; 1600x900 = same layout x 0.833)
Join screen:
```
| SPACE MARINE  MULTIPLAYER                                  ver / status line |
|  Server [127.0.0.1:27015]   Side ( Auto | Space Marines | Chaos )          |
|  [1][2][3][4][5][6][7][8][9][10]  (loadout slot strip)                      |
|  ACTIVE: 3 "Name"  Tactical Marine - Bolter - Frag - Perk A, Perk B          |
|  [ CUSTOMIZE ]                                              [ JOIN ]        |
```
Creator:
```
| LOADOUTS   Preview side: [Space Marines | Chaos]   ACTIVE: 3 "Name"   [Back Esc] |
| (in match: Applies when you respawn - RESPAWN IN 4)                              |
| [1 Tactical][2 Empty][3* ACTIVE Devastator][4] ... [10]   (chips 168x88)          |
| STEPS    | LIST (step content)      | 3D PREVIEW (turntable) + item card         |
| 1 CLASS  | [ ] Bolter    SELECTED   |                                            |
| 2 WEAPONS| [ ] Stalker Bolter       |  card: name, description, effect/stat line, |
| 3 EQUIP  | ...                      |        Required Level / lock / status       |
| 4 PERKS  |                          |                                            |
| LOOK...  |                          |                                            |
| Rename Copy Reset Clear Revert   * UNSAVED   [Save Ctrl+S] [Set active] [Deploy]   |
```
Columns: steps x 84–304, list 320–900, preview 916–1836; card y 780–950; action bar y 984–1040. Chips show number, name
(or Empty) and class; state tags are words (ACTIVE, UNSAVED, REPAIRED). A summary line lists the open slot's choices. The
preview is a SubViewport with `CharacterViewer` in an idle stance and the slot's weapon in hand, lit like
`AddCameraAndLights`, model right of centre.

Customizer:
```
| APPEARANCE [Appearance 1 v]  Side: [Space Marines | Chaos]  Previewing class: Tactical |
| CHAPTER | option panel (section)          | 3D PREVIEW, slot-focused camera           |
| ARMOUR  | Part: [All parts v]             |                                            |
| COLOURS | Channel: Primary|Pattern|Detail |                                            |
| BADGES  | Pattern [ON|OFF] [x] Apply to all|                                           |
| EYES    | Blue (9): [..][..] ...          | focused swatch name: "Regal Blue"         |
| Reset section  Undo                  * UNSAVED   [Save] [Done - back to loadouts]     |
```
Emblem crop modal: centred 900x640; source + frame left (560 px), 128x128 preview, pad preview, zoom slider with value
text and the buttons right; traps focus; Esc cancels.

Preview camera: class/weapons/equipment/perks/chapter 3/4 full body (turntable); helmet and eyes head close, front; torso
upper body front; arms the chosen arm; legs lower body front; shoulder/badge the pad close from its side; backpack upper
back. Moves 0.35 s ease-out (cuts with Reduce motion). Turntable 18 deg/s only in full-body framings, paused 4 s after a
user rotation. Heights from `CharacterViewer.Bounds` or bones, tuned by eye.

## 4. Component states (colour never the only signal)
Focus: 3 px white outline. Selected/equipped: 6 px gold left bar, check icon and the word. Hover: lighter fill. Disabled
/ fixed: grey text with the reason. Locked (later): padlock, "LOCKED", dim, still focusable; the card reads "Required
Level: N", "Challenge: … (12/50)" or "Requires perk: …". Unavailable: reason in the card, never selectable. Empty slot:
dashed chip "Empty". Unsaved: "*" and "UNSAVED", Save enabled only then. Repaired: tag until opened. Loading: in-place text,
dimmed preview "Loading <name>". Error: red-bordered banner with "ERROR" and Retry/Quit (light red text). Success: a 2.5 s
status line. Lock model: `Unlock = Unlocked | Level(n) | Challenge(text, progress, target) | NeedsPerk(name) |
Unavailable(reason)`; everything Unlocked for now; `--lock-demo` forces a sample of each state.

## 5. Input
Keyboard: Tab / Shift+Tab across regions (slot strip, steps, list, card/preview, action bar), arrows inside; Left/Right
on steps or PageUp/PageDown or Ctrl+Tab switch steps; digits 1–9, 0 jump to slots when the strip has focus (never in a
text field); Enter selects, Space toggles, Esc goes back one level (asks about unsaved edits); Ctrl+S save; F2 rename;
Delete removes an emblem; Home resets the preview camera; preview focus: Left/Right rotate, Up/Down zoom (±30%).
Mouse: click selects, double-click = Enter, drag rotates the preview, wheel zooms. The mouse is visible in the creator
(restored to captured on close); game bindings do not fire while it is open. Controller (Godot `ui_*`): D-pad/left stick
navigate, A accept, B cancel, LB/RB steps, X copy, Y rename, Start save, right stick rotates the preview.

## 6. Accessibility
Text (virtual 1080): body 24, captions ≥ 18, headers 32; hit targets ≥ 44. Contrast: white and gold on the panel well
above 4.5:1; grey (0.62) about 6:1; use the light red (1, 0.38, 0.32) for red text, not the HUD's dark red. Selection,
focus, lock, unsaved, error and disabled always carry an icon and a word; swatch names printed; side toggle uses text.
Every icon has a text label. "Reduce motion" toggle. No text baked into textures.

## 7. Visual direction
Our own look matching the placeholder HUD: dark panels (0.03, 0.03, 0.03, 0.86), 2 px gold borders, white text, the same
font and outline; real Controls with one shared `Theme`; gold for own/active/primary actions, light red for warnings
only; class glyphs may reuse the HUD's procedural Aquila / Chaos star shapes, always with text; preview background a flat
dark gradient. No original UI art.

## 8. Acceptance criteria
Join: J1 no `--connect` shows the Join screen, `--connect` skips it unchanged; J2 Join uses the active slot's class and
choices (server log / scoreboard); J3 empty/invalid active slot falls back with a status line; J4 the side selector sends
the requested side.
Slots: S1 ten chips, slot 1 filled, 2–10 Empty; S2 rename rules; S3 copy confirm/cancel/name; S4 reset and clear
behave and confirm; S5 UNSAVED and save survive a restart; S6 Save/Discard/Cancel work; S7 exactly one ACTIVE chip.
Steps: C1 per-class rows (Tactical 7 primaries + fixed pistol/melee; Devastator/Havoc 3 heavy + fixed pistol;
Assault/Raptor 4 pistols + 5 melee, no primary); C2 fixed rows say "Fixed"; C3 pistol-slot bolters need True Grit; C4
Frag, Blind, Stimulants (disabled); C5 two perk slots, default pre-filled, grouped, no duplicates, weapon warning, "NOT
ACTIVE YET"; C6 names and descriptions from the locale with readable fallbacks; C7 Chaos names on the Chaos side.
In match: M1 C on the death screen opens the creator, no input sent, hint while alive; M2 banner and countdown, DEPLOY
applies at the next spawn, rejection keeps the old loadout; M3 closing restores mouse capture and input.
Customizer: Z1 sections and focus order; Z2 focus previews, Enter commits, Esc reverts; Z3 colours on the right parts
and channels, "Apply to all"; Z4 pattern OFF = secondary equals primary (or the toggle is hidden); Z5 forced pieces
disabled and labelled; Z6 pads without decal UVs disabled with the text; Z7 camera framings and Reduce motion; Z8 Save /
Done update the slot's look and survive a restart.
Emblem: E1 formats and the four error messages; E2 pan/zoom with live previews; E3 apply to left/right/both, "My
emblems" max 8, removal reverts pads; E4 stored 128x128 without the source path, seen by other players unless disabled.
Data errors: D1–D4 as in F8. Accessibility: A1 no overlap at 1600x900 and 1920x1080; A2 full keyboard order with focus
ring; A3 readable in greyscale; A4 controller reaches everything; A5 `--lock-demo` shows each lock state.
