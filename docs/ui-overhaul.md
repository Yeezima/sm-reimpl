# UI overhaul (branch `claude/ui-overhaul`, resumed 2026-11-18)

The owner's request of 2026-11-18: continue the paused concept redesign of 2026-11-15 (`docs/ui-redesign-inventory.md`, `docs/ui-concept-textures.md`), bring the buttons and every
menu up to today's release, give the smaller elements (scoreboards, Musou Mode menus) the same look, and **never touch the in-game HUD**. Menus and the scoreboards of all modes only.
Nothing here changes behaviour: every row, button, option, handler, save and focus path is the one on `main`; only drawing, lettering and layout of the screens changed.

## What was brought together

`claude/ui-concept-redesign` (Options panel 04 phase 4b-2), `claude/ui-menu-v2` (main menu from the panel 01 v2 reference, measured 0.974) and `claude/ui-armoury` (Armoury panel 03) were
merged onto `main` of 2026-11-18 (three merge commits; the conflicts were the notices file, the dev-tool arguments of `ModelViewer` and the texture builder's panel list, resolved by keeping both
sides). `claude/ui-concept-hud` (the in-game HUD) was **not** used and no HUD file was changed (`client/Hud*`, `client/Hud/*`, `MatchFlowView`, the kill feed, markers, banners, Musou combat
readouts and the Musou M-overlay in a battle are as on `main`).

## The style kit (one look, one lettering)

| Piece | File | Used by |
|---|---|---|
| Painted frames, plates, tabs, pills, switches, sliders (cut from the owner's concept panels, Real-ESRGAN x4 back-projected, `docs/ui-concept-textures.md`) | `client/assets/ui/*.png`, `ConceptArt`, `PaintedButtons` | main menu, Options, Armoury, every page below |
| **Panel frame** (new): the Options frame with its title lettering replaced by a clean plate (`overlay-panel.png`, `UiTextureBuilder.OptionsV2.cs`) and made taller by mirroring a slice of its plain interior (`PanelFrame`, 0 or 2 copies so stone and steel stay continuous) | `Customizer/PanelFrame.cs` | Options (taller: 12 rows without scrolling), `ConceptPanelPage` |
| **`ConceptPanelPage`** (new): the shared page of every menu that is not the main menu, Options or the Armoury: frame, live title on the plate, heading strip, the old page's rows restyled as the concept's rows (pill, switch, plate, field, note), one column centred or two columns from the top, text-fit and scroll-free for the longest page | `FrontEnd/ConceptPanelPage.cs` | Classic PvP lobby, Host lobby, Join lobby, Musou Mode page, in-match Esc menu (PAUSED) |
| **`BoardStyle`** (new): the steel look of boards drawn with `_Draw`: painted plate for the big numbers, recessed steel table, dark header strip, warm-black rows, gold wash and bar for the own row, Oswald | `Customizer/BoardStyle.cs` | Tab scoreboard of every mode, Warfront boards (follow-up) |
| **`SteelBackdrop`** (new): veil, recessed steel board and rivetted title plate behind a page that lays its own content out by hand in the 1920 x 1080 space; `StyleTitle`, `StyleLine`, `StylePlate` | `Customizer/SteelBackdrop.cs` | Musou score, reward and run pages, range confirmation, interactive war map; Warfront pages (follow-up) |
| Defaults of the theme: Oswald everywhere (`UiKit.Theme.DefaultFont`), `UiKit.Button`/`Toggle` are steel plates (upper case), `UiKit.Panel` and dialogs the steel recess, drop-downs steel, scroll bars a dark track with a steel grabber that turns gold, list rows (`UiKit.Row`) warm black with a steel hairline and a gold rim under the pointer | `Customizer/UiKit.cs` | CONTROLS (keybinds), Appearance, emblem importer, dialogs, failure and set-up pages, every legacy list |

Text rule kept (owner, 2026-11-15, "one text style per level"): titles on the plate, tabs, plates and buttons, row labels, row values, notes and table cells each have one face (`UiKit.ConceptFont()`),
one size per level and one colour set on every screen. Notes are sentence text of the page's own words (some are mixed case because the page writes them at run time and the tests compare them).

## Screen inventory (old to new, every control)

Each row: screen, how it is reached, old look on `main` (2026-11-18), new look, controls (all kept, same handlers).

| # | Screen | Old | New | Controls |
|---|---|---|---|---|
| 1 | Main menu | v1 steel (`main`) | the panel 01 v2 overlay (wall, wordmark, crest, framed recess) with painted plates | CLASSIC PVP (default focus), MUSOU MODE (added content), STOP MUSOU SERVER (while a Musou server of this client runs), ARMOURY, OPTIONS, EXIT; footer with where the game was found and the builds' address |
| 2 | Classic PvP (lobby) | text rows | `ConceptPanelPage` "CLASSIC PVP" | HOST LOCAL LOBBY, JOIN SERVER, JOIN LOCAL LOBBY, SIDE (AUTO / SPACE MARINES / CHAOS), CONTENT, SPECTATE, active loadout line, content note, BACK |
| 3 | Host lobby | text rows | `ConceptPanelPage` "HOST LOBBY", heading HOST LOCAL LOBBY, two columns | GAME MODE, MAP (Manufactorum first, ROTATION last), WHO CAN JOIN, PORT field, PLAYERS' EMBLEMS, BOT DIFFICULTY, ADD LOYALIST BOT, ADD CHAOS BOT (counters, left and right click remove), CLEAR BOTS, HOST A MATCH / HOST MUSOU, STOP SERVER, JOIN YOUR SERVER and running-server rows, reach note, BACK |
| 4 | Join lobby | text rows | `ConceptPanelPage` "JOIN LOBBY" one centred column | ADDRESS field, hint, CONNECT, BACK |
| 5 | Musou Mode page | text rows | `ConceptPanelPage` "MUSOU MODE" | PLAY ON THIS PC, HOST LOCAL LOBBY, JOIN LOCAL LOBBY, CUSTOMIZER, co-op note, BACK |
| 6 | Options (GRAPHICS, SOUND, CONTROLS, INTERFACE) | v1 steel | panel 04 frame, taller (12 rows visible), whole rows only, wider FOV value | every row of `docs/ui-redesign-inventory.md` section 2, APPLY, REVERT, BACK, toast, KEYBINDS |
| 7 | Keybinds (CONTROLS screen) | flat grey tabs, DejaVu | steel tabs, steel bind plates, steel recess table, steel BACK and RESET | KEYBOARD & MOUSE / CONTROLLER tabs, every bind slot, RESET TO DEFAULTS, BACK, conflict dialog (Swap, Replace, Cancel) |
| 8 | Armoury (class creator) | v1 | panel 03 pieces, neutral icon tiles (navy backing removed), card captions clear of the bezel, veil over the scene, steel toolbar plates | DEFAULTS and CUSTOM 1 to 10 (SLOTS tab), CLASS list, steps CLASS / WEAPONS / EQUIPMENT / PERKS / LOOK / SUPPORT ABILITIES, preview, item card, RENAME, COPY TO, RESET, CLEAR, REVERT, SAVE, ACTIVE, APPEARANCE, CONFIRM LOADOUT, BACK, faction toggles |
| 9 | Appearance editor | text list, grey boxes | steel list rows, steel plates, steel recess, veil over the scene | MY APPEARANCES, CHAPTER, ARMOUR, COLOURS, BADGES, EYES; RESET SECTION, UNDO, SAVE, DONE; look drop-down, side toggle, custom image importer |
| 10 | In-match Esc menu | text rows | `ConceptPanelPage` "PAUSED", the match dim kept | RESUME, LOADOUT, OPTIONS, LEAVE MATCH, added-content rows, respawn banner |
| 11 | Tab scoreboard (Annihilation, Seize Ground, Musou one column) | black and white boxes | `BoardStyle`: painted score plates, steel tables, gold own row, Oswald | per-side score, VS, KILLS, DEATHS, RATIO, ASSISTS, SCORE, PING, own row accent, icons |
| 12 | Match summary (count-up and battle record) | plain panel, gold edge | steel panel (recess lip, thin gold line), Oswald; the movies' own text colours kept | MATCH RESULTS, team score, KILLS, list of the side's best, SCOREBOARD / BATTLE RECORD rows |
| 13 | Musou score, reward and run pages, range confirmation, war map (station) | text rows on black | `SteelBackdrop` board and plate, plate rows | CONTINUE, the three reward cards, COPY RESULT, CLOSE, TELEPORT, CANCEL, DEPLOY per front |
| 14 | Install failure, game folder set-up, dialogs, emblem importer | flat boxes | steel recess and plates | RETRY, QUIT, BROWSE, retry, SAVE, DISCARD, CANCEL, FIT, FILL |
| 15 | Loading screen | its own picture | unchanged (owner, 2026-11-15) | - |
| - | **Not touched (HUD or in-play)**: weapon box, vitals, crosshair, kill feed, markers, banners and warm-up/victory splash (`MatchFlowView`), Musou combat readouts, the M overlay in a battle, the F6 developer tools panel, F3 readout | | | |

Screens that live only on `claude/warfront` (not on `main`) are listed under "Warfront follow-up".

## Test aids added

* `client.cmd --menu --menu-screen scoreboard|scoreboard-single|pause` (screenshot aids: the Tab scoreboard with made-up rows, two sides or Musou's column; the in-match Esc menu).
* `--panel-debug` prints each panel page's layout decision (one column or two, row height against the area).
* `--options-short`: Options at the concept's own height (the comparison tool needs it).
* Self-tests follow the upper-case buttons (`UiSelfTest*` compare button text without case) and the new default font; `UiSelfTest.Menus` still checks that every page's rows end in the window.

## Warfront follow-up (claude/warfront is not merged here)

Everything below is a hand-over to the lead; the shared components exist on this branch.

* **Scoreboard** (`client/Extras/Warfront/Progression/WarfrontXpBoard.cs`, branch `claude/warfront-xp-scoreboard`): it draws with the old black-and-white palette and the system face. Replace its fills with `BoardStyle`:
  `new BoardStyle()` as a field; the plate (`Plate`) under its title or big number, `Table(this, rect, scale)` for the panel, `HeaderStrip` for the header, `Row(this, rect, alternate, isMe, scale)` for each row, `BoardStyle.Font` for text,
  `BoardStyle.Header` for header text, `BoardStyle.Ink(isMe)` for cells; the XP bar can keep `BoardStyle.Gold` fill on `Steel.SteelLo`. (`ScoreboardView` is the model: it is 40 lines shorter than before.)
* **Pages that use `MenuText` / `MenuButton` on a hand-laid layout** (`WarfrontResultsPanel`, `WarfrontTravelPanel`, `WarfrontHubPanel`'s texts, `WarfrontJoinPanel`, `WarfrontPickerPanel`, `WarfrontMenuPage` pages): do what `TfwPagePanel` does in
  `client/Extras/Musou/Tfw/TfwRewardPanels.cs`: add `new SteelBackdrop(name + "Dim", titleLabel)` instead of the black `ColorRect`, `SteelBackdrop.StyleTitle(titleLabel)`, `StyleLine(label)` for text and `StylePlate(button)` for rows.
  Move the title to (`UiKit.MenuLeft + 8`, 62) so it sits on the plate.
* **Pages built from `UiKit.Button` / `Text` / `Panel`** (`WarfrontSquadPanel`, `WarfrontSquadHud` tabs): nothing to do, they now come out steel and Oswald from the theme.
* **Warfront entries in the menus** (`FrontEndView.WarfrontMenu.cs`): its page is built from `UiKit.MenuPage`; make it `new ConceptPanelPage(name, heading, title)` and the rows restyle themselves (choices, buttons, notes, fields).
* **Custom-drawn lane map and boxes** (`WarfrontHubPanel.MapDraw`, `WarfrontJoinPanel.Frames`): swap the 2 px outline rectangles for `BoardStyle.Table` panels and `Steel.Gold` highlights; keep the in-play parts (`WarfrontXpHud`, `WarfrontSquadHud`, battle HUD, spectate HUD) as they are (HUD).

## Open points and deviations

See the end of this file (filled in after the measured passes).
