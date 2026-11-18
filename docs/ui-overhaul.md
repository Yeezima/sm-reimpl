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

## Review round 1 (Opus, 2026-11-18): what changed

* **No flat boxes.** `opt-plate-blank` and its gold twin are alpha-cut to the plate's bevel (`tools/ui-measure/frame_cleanup.py`; no dark backing rectangle is left under plates); text fields are the kit's steel recess with a quiet selection colour;
  list rows, panels, dialogs and tables are steel.
* **Page chrome.** The frame's seam in the separator line and the lighter patch in the top band (the concept's art differs there) are painted over with the neighbouring stretch (same script). Every page has a heading line (Musou: PLAY ALONE OR TOGETHER,
  Pause: MATCH MENU). **One BACK and one main action everywhere:** BACK is the concept's BACK plate at the screen's bottom right (where Options puts it), the main action is the gold plate in the frame's bottom tray (where Options has APPLY); both are the
  page's own buttons made top-level (`ConceptPanelPage.Dock`, `DockBack`), so handlers and focus are unchanged. The page's plates leave a visible gap, hover and focus are both the gold plate with dark ink, a focused choice is the pill art gone
  gold (no rectangle), live titles carry the painted title's emboss (outline and shadow, `SteelBackdrop.EmbossTitle`), notes are capitals and centred in a narrow column.
* **Options grid.** One pill width (the widest the tab needs), one slider width, pills and switches start at the same x, rows 29 grid px so the RENDERER line fits without a scroll bar, whole rows only; the game-found note is hidden under 900 px of window
  height (it would draw under 10 px).
* **Keybinds** are in the panel frame (`PanelChrome`: veil, frame, KEYBINDS plate, tabs in the strip, table in the rows area, BACK on the BACK plate); labels and binds are capitals; the Appearance title is on a plate, its sections are steel tabs (the open one gold),
  list rows are capitals; the summary and count-up use the scoreboards' table, a painted title plate, capitals and the gold own row.
* **Scoreboard.** Score plates carry the side's name (SPACE MARINES, CHAOS) and emblem when its picture is here, the single board has TOTAL SCORE, plates are no wider than 600 px, the demo has icons and one own row. The doc comment of `ScoreboardView` records that the owner's
  request of 2026-11-18 supersedes the black-and-white rule of 2026-11-07 (and `docs/changes-from-original.md` has the row).
* **Main menu at 5:4.** The overlay's lower edge fades out, the Marine is framed further right on narrow windows so his backpack stays clear of the frame, the empty nameplate carries the builds-and-issues address (a link). **Documented exception:** the main menu's five
  labels keep the concept's own plate lettering (a glyph atlas cut from the reference, see `docs/ui-concept-textures.md` "Phase 5"); every other label of every screen is Oswald (`UiKit.ConceptFont`).
* **Armoury.** Whole cards only (three, as the concept), icon wells hold faint engraved type glyphs drawn in code (`EngravedGlyphs`: gun, pistol, blade, grenade, perk star, Ork head; the Ork Boy has a class icon), the preview viewport is transparent so the
  scene shows through. Wargear icons are read from the player's install at run time (`HudArt`, read-only, once on a worker thread, cached in memory; nothing is written) and drawn as faint white-on-steel engravings in the rail, slot list and tiles; until they are ready, or where the install has none (perks), the engraved glyph stands in. Later passes: faction tabs over the preview, no floating ACTIVE header, a looser dock, Keybinds table fades out at its foot instead of cutting a row, the Appearance look drop-down is in capitals, the failure and set-up screens wear a plate title, the Musou pages' steel board is cut to the page's content (`SteelBackdrop.FitContent`) and the war-map demo carries real figures.
* Self-test aids for the missing screens: `--menu-screen failure|dialog|tfw-range|tfw-war|tfw-score|tfw-reward|tfw-run` (the Musou ones through the `ExtrasMenuScreen` hook in `client/Extras/Musou/Tfw/FrontEndView.TfwScreens.cs`).
* The comparison tool takes `--alpha-min a --erode n`: a masked measurement that leaves out the frame's antialiased edge pixels.

## Measured fidelity (1920 x 1080, the existing UiCompare / UiComparePanel method against the owner's reference images)

| Screen (reference) | Result today | Note |
|---|---|---|
| Main menu (panel 01 v2) | **FULL 0.974**; wall 0.997, wordmark 0.940, crest 0.971, frame 0.974, recess 0.945, gold plate 0.981, plates 0.967 to 0.996, labels 0.928 to 0.964; mean-colour dE at most 2.5 | `--ui-compare`, capture of the real client; every region meets SSIM 0.90 |
| Options (panel 04 v2, INTERFACE tab, concept height via `--options-short`) | unmasked FULL 0.832 (frame steel 0.797, title and crest 0.852, tabs 0.971 to 0.985, APPLY 0.984, bands 0.948); **masked** (`--alpha-min 0.999 --erode 4`, antialiased edge pixels left out): frame steel 0.787, title and crest 0.868, stones 0.855 / 0.816, tabs 0.976 to 0.992, APPLY 0.995, interior bands 0.971, REVERT 0.792 | Masking does not raise the frame: the edge pixels are not the cause. Why the old 0.96 does not reproduce: that figure was measured against the old four-panel target; against the v2 reference the old art's best was 0.860. The frame was since made mirror-symmetric at the owner's request, which costs 1:1 match against an asymmetric reference; the result is resolution-independent and registration is already optimal at shift 0,0. Reaching 0.95 would mean breaking the symmetry, and the owner's standing rule ("borders and outlines uniform and clean, no lack of symmetry") outranks a pixel score against an asymmetric reference: the frame stays mirror-symmetric (lead's decision, 2026-11-18). The symmetric regions score 0.97 to 0.995 (tabs 0.976 to 0.992, APPLY 0.995, interior bands 0.971); the frame steel and the title and crest (0.79 and 0.87 masked) differ from the reference only where the reference is asymmetric. The live parts (tabs, APPLY, bands) are 0.97 to 0.995. Below 0.95 on the frame: open |
| Armoury (panel 03) | title plate and wordmark 0.985, wings and skull 0.947, row DEVASTATOR 0.903, weapon name plate 0.815; FULL 0.653 (unchanged by the blur: the pixels it changes are the scene behind the frame, which the reference's rail region is measured over) on a like-for-like Librarian capture (`--customizer --content musou --musou-entry librarian`; title plate 0.986, rows 0.93 to 0.99 except where the reference's neighbouring rows differ; rail frame 0.624 because the veil lets the scene through where the concept shows a blurred one; weapon plate 0.778) | **Below 0.95.** The concept's tiles and cards carry artwork (weapon and power icons) that the game has no pictures for, except the Librarian's powers; they are neutral steel tiles here (the navy backing of the concept's empty tile was removed). The rail frame region also lost points to the veil that now lets the scene show behind the screen (the concept shows a blurred scene there). Needs an owner decision, see below |
| Every other screen | no reference exists; consistency is by construction (same plate, frame, row art, lettering and colours as the three above) and by review of the sheets | scoreboards, pages, keybinds, appearance, pause, summary |

Resolutions captured (1280 x 720, 1920 x 1080, 2560 x 1440, 2560 x 1080 = 21:9, 1280 x 1024 = 5:4) for the main menu, Options GRAPHICS, Host, the Armoury and the scoreboard; the front end's own geometry check (`UiSelfTest.Menus` "fit", eight window sizes up to 3840 x 2160 and 1280 x 1024: every page's rows end in the window, all text fits, frame and actions in the window) passes. Evidence: the folder `sm-reimpl\tmp-build\ui-overhaul` under `%LOCALAPPDATA%` (`cap`, `before`, `before-res`, `res`, `cmp`, `sheets`).

## Checks run

* `client.cmd --ui-selftest`: 1244 passed, 1 failed. The one failure, "CONNECT joins that address on the Musou profile", fails the same way on `main` of 2026-11-18 (1071 passed, 1 failed there); it is the join test's data (no game plan in the test profile), not this work.
* `dotnet test tests/SmNet.Tests --filter ExtrasBoundaryTests|LeanBot`: 45 passed (the front end's new aids are classified in `LeanBot.PresentationAids`).
* `dotnet test tests/SmFormats.Tests --filter PublicTextGuard`: 32 passed.
* `smtool.cmd refine-register` run after the `changes-from-original.md` rows were added.
* Not run: the whole `dotnet test` (the owner approves that run).

## Open points and deviations

* **Armoury fidelity (owner decision).** Below the 0.95 bar for the reasons above. Options: accept neutral tiles; or fill the tiles with the class's own wargear silhouettes read from the install at run time (a small client feature, the install is the player's); no image service was used and none is proposed.
* The Armoury's 3D preview keeps its flat dark backdrop box (the concept stands the marine in the scene); making it transparent changes the preview renderer, left for a decision.
* Page notes are sentence case (they are written at run time and the self-tests compare them); all labels, buttons and tabs are capitals.
* `MatchFlowView` (warm-up count, victory splash), the F6 developer panel, F3 readout, kill feed, markers, banners and the Musou M overlay are HUD and untouched, so they keep their old look.
* The Musou hub screens that exist only on `claude/warfront` and the Warfront scoreboard are the lead's follow-up (above).
* Controller focus: every page keeps its controls and focus chain (the self-test's pad group passes); the main menu and Options keep their painted gold focus plate, the pages use the same plates.
* Evidence screenshots show the real client on the owner's install, including the game-found note under the Options frame; nothing of the install is committed.

## Review rounds 2 and 3 (Opus, 2026-11-18): what changed

* **Armoury.** The whole scene is blurred behind the screen (a screen-texture pane at a high mip under the painted pieces: the concept's out-of-focus battlefield, and the second marine of the video no longer ghosts behind the preview). Install silhouettes are drawn lighter with a one-pixel dark copy behind them so a thin shape reads at tile size (they stay one flat colour: the install gives a mask, not a picture of the part). One row of tools in the dock (smaller lettering and 3 px gaps; the texts are the buttons' own), tile captions clear of the bezels, the summary line in capitals, the description panel's recess gained the concept's thin warm line inside the lip (shared by every recess).
* **Scoreboards.** The big number is 72 and sits inside the plate's lower bevel on both boards; the made-up emblems of the test aid are thinner and grey (the live game passes the player's real emblem pictures, unchanged).
* **Pages.** The tray's main action is steel until it has the focus, and gold with dark ink then (one gold cue per page; pause now has RESUME there); the Keybinds page puts RESET TO DEFAULTS in the same tray and its tabs wear the Options tab art; no focus rectangle on a tray plate (the focused plate is the gold art). Fields are the gold-rimmed recess at rest too (HOST port as the join address), the join address starts right after its label, the HOST bot buttons stand together in the second column (`MenuColumn.BreakMeta`) and the gap before STOP SERVER is gone. Running capital notes use open tracking (`UiKit.NoteFont`). The main menu's address is centred on the nameplate.
* **Options.** One control x per row: sliders start where pills and switches do; one value width and one track width for every slider of a tab; the second column starts at the same x on every tab; no scroll bar when the page fits (GRAPHICS). Not done: one label column width across all tabs (the controls start after the tab's own longest label; a fixed width would leave gaps on GRAPHICS).
* **Appearance.** Button lettering is clear of the rivets (the plate theme's content margin is now 44 px, tabs 30 px), the card, status and notes are in capitals, no grey outline round the navigation and the list.
* **Summary and count-up.** Banded rows, a header strip and a well for the big number, in the scoreboards' own pieces.
* **Musou pages (score, reward, run, war map).** The steel board is cut to the content and centred with it (`SteelBackdrop.FitContent`); the war-map demo carries real figures and its lane bars wear steel pips and rims.
* Captures: all 1080 screens, and every screen at 1280 x 720, 2560 x 1440, 2560 x 1080 and 1280 x 1024 (`res`), sheets regenerated.

### Housekeeping the review asked for

* `client/assets/menu/menu-concept-loop.ogv` is the owner's own uploaded video (decisions/2026-11-15: "make the referenced video image the background of the main menu"; an 8 s clip he made, not an original game file), converted by `tools/menu-video.ps1`; it came in with the merge of `claude/ui-concept-redesign`.
* `tools/release/release.ps1` (a +4 line change from that merge): the release copies the new menu video beside the exe like the still pictures, otherwise the shipped main menu would fall back to the still. `spec/behavior.md` (14 lines, also from that merge): the "UI concept redesign" parameter rows (video, layout, Options semantics, steel kit) and the one changed background row. `src/SmNet/LeanBot.cs` (+1 line, this branch): the menu screenshot aids (`menu-stop-row-demo`, `panel-debug`, `options-short`, `dump-rows`, `options-toast`) are classified as presentation aids, as `LeanBotTests` require for every client test flag.
* The strings scan (every string literal of added or removed code lines) now covers `client/Extras/Musou/Tfw/**`, `client/Controls/**` and the scoreboard and summary views too: nothing there was lost; only demo figures were added (`strings-diff.txt`).

## Review round 4 (Opus review 3, 2026-11-18): what changed

* **Painted frame for the dialog family and the Musou boards.** `Steel.Kind.PaintedRecess` draws the Options frame's art (`frame-options`, scaled) round a dark floor; `UiKit.Panel` (the confirm and prompt dialogs, the install failure, the set-up page, the emblem importer, the Armoury and Appearance cards) and `SteelBackdrop` (the Musou score, reward, run, range and war-map pages) wear it instead of the flat recess. Titles are the rivetted plate (`UiKit.PlateTitle`), body and notes in capitals with open tracking (`UiKit.NoteFont`), buttons carry no focus halo (the plate's own gold is the cue). The emblem zoom slider is the Options slider's art (`UiKit.ConceptSliderStyle`).
* **Musou pages.** Page-size lettering (labels 24, notes 26), the war map's bars are 860 wide with 22 px text and 420 x 56 DEPLOY plates, the run page's rows have more room; the fitted board stays centred.
* **Armoury.** The summary line no longer carries the "(choose anything to edit a copy)" tail in the Armoury (it is in the tooltip) so it never truncates; the dock's lettering is never below 11 px; the sidearm and grenade slots use the engraved glyphs (lifted in brightness) instead of the soft install pictures; the description box wears the painted ring.
* **Focus.** The Keybinds tabs' focus is the gold plate (no rectangle); the HOST port has the steel rim until it is focused.
* **Frame art** (`tools/ui-measure/frame_cleanup2.py`, run once on `overlay-panel.png`): the separator under the tab strip is one plain profile (the seam at x 1985 is gone) and the frame's lower part is mirrored from its right half onto its left (no copper/grey step, no tick at the join). The heading band's right end and the lighter patch there were left as the art has them (the bevel starts at that edge).
* **Appearance.** The look drop-down's arrow stands clear of the rivet; the detail panel is the painted ring.
* **Count-up and battle record.** The selected tab is the gold plate with dark ink; the count-up block is centred vertically, the big number sits in a steel recess clear of its border.
* **Evidence.** `--show-setup` shows the real game-folder page (a screenshot aid, classified in `LeanBot.PresentationAids`); every screen was recaptured at 1920 x 1080 and the Musou pages, the dialogs, the set-up page, the importer, the count-up and the Armoury also at 1280 x 720 and 1280 x 1024.

### Options: the label column differs per tab (documented, not changed)

The label column of each Options tab is as wide as that tab's longest label plus a margin, so the controls start at a different x on GRAPHICS, SOUND, CONTROLS and INTERFACE (within a tab every control of a column starts at one x). A single width across tabs would have to be the widest label of any tab (GAMEPAD LOOK SENSITIVITY) and would leave a wide gap on GRAPHICS. The concept's own labels are short enough that its column is a fixed 104 px; ours are longer words, so we keep the per-tab width.

## Review round 5 and the owner's main-menu request (2026-11-18)

* **Main menu 1:1 with the owner's reference** (`owner-main-ref-2026-11-18.webp`, the same concept as `owner-ref-main-menu-crop.png`): the SM-REIMPL wordmark was re-cut from the reference (`tools/ui-measure/wordmark_recut.py`: the reference is registered onto the overlay with the v2 similarity, 698 SIFT inliers; the letters, bevel and drop shadow are laid into `overlay-main.png` and `wordmark.png`; the old ragged letters and their shadow beyond the right wing are gone), and the rows are in the reference's order, MUSOU MODE first and focused, then CLASSIC PVP, ARMOURY, OPTIONS, EXIT (a faithful build starts with CLASSIC PVP). Kept as decided: no tagline (the nameplate shows the releases address), no "UI DESIGN STUDY" badge, our own background.
* Masked SSIM of the left region (our overlay's own opaque pixels, the nameplate strip left out): 0.578 overall; wordmark 0.739, rock wall 0.671, crest 0.542, frame and plates 0.503. What still differs: the background behind the art (ours is the video, the reference's sky is grey and its stone is lit differently), the plate labels (live Oswald against the reference's heavier face), the nameplate words, and the frame glow. The wordmark's letter edges were viewed at 1920x1080, 1280x720, 2560x1440, 2560x1080 and 1280x1024 and are complete. Sheet: `sheets\compare-main-2026-11-18.png`.
* Firing range text wraps inside the ring; the run and score pages' plates are 58 px with a gap; the title plates are inset from the ring's corner; a disabled plate takes no focus (a focused disabled plate drew the gold focus plate under grey ink on HOST LOCAL LOBBY); the pistol and grenade slots use solid pistol and grenade shapes in the bolter's ink; the set-up page has one title (the plate) and capitals throughout; the Armoury description body is in capitals; the count-up block is re-centred.

## Text scale (branch `claude/ui-text-scale`, 2026-11-18)

The owner's request: "all text consistent in size and scale, no blurry text, buttons and sliders scaled proportionally". Client only; no HUD file, no behaviour, no protocol change.

**What the audit found** (`--text-audit <file>` beside a screenshot, `tools/ui-measure/text_scale_table.py`; evidence in the media folder `ui-text-scale`):

* One level of text had many sizes: plates, tabs and buttons drew at 15 different sizes (20 to 47 UI px: Options tabs 33, APPLY 37, panel plates 39, BACK 37 or 32), row labels at 36 and 37, values at 31 and 33, and every hand-laid page its own number.
* At 1280 x 720 and 2560 x 1440 about two thirds of all texts (258 of 391 and 262 of 395) were drawn at a fractional window size (the UI px times the window scale: 14.67, 21.33, ...), so Godot rasterised them between pixels.
* Sources of blur: (1) text and art placed between window pixels (the 2D snap settings were off); (2) fractional sizes as above; (3) painted pieces resampled twice: the pill, slider channel, fill and knob were first Lanczos-copied to the virtual size and then scaled again by the window (soft at 1440p and 720p, blurry when magnified at 2160p); (4) painted art drawn with `Linear` (no mipmaps) from 4x art; (5) the Options title was a painted word in another face (DejaVu bold) than every other title (live Oswald); (6) the main menu's five labels are painted into the plates (kept, see the open question).

**What it does now**

| Piece | Where | What |
|---|---|---|
| One UI scale | `UiType.ApplyWindow` (from `UiKit.EnterVirtualSpace`, on every window resize) | The menus' content scale is the window's scale snapped to whole sixths (never leaving the pages under about 94 % of 1920 x 1080) and 2D drawing snaps to whole window pixels (`Snap2DTransformsToPixel`, `Snap2DVerticesToPixel`, on the window while the menus have it, released with `LeaveVirtualSpace`; the HUD's own state is untouched). |
| One size table | `UiType` (title 54, primary action 48, plate 36, row label 36, row value 30, caption 24, small 18, score 72 UI px) | Every size is a multiple of 6, so it is a whole number of window pixels at every sixth of scale. A page asks `UiType.Px(level)`; hand-laid pages map their own number with `UiType.Level`; `UiType.Snap` moves any other size to the nearest whole-pixel one. In a match (no virtual space) the table is multiplied by the window's height over 1080 in sixths. Caches of styled sizes include `UiType.Key`, so a resize restyles. |
| Levels | Options (tabs, APPLY, REVERT, BACK, rows), `ConceptPanelPage` (plates, rows, headings, fields, notes, BACK, tray), Keybinds (tabs, table cells, headers), Armoury (`ArmPx`: names and BACK = label/plate level, captions, chips), scoreboards and summary tables (cells = value level, headers = caption level, big number = score), dialogs and Musou pages (`UiKit.Text`, `TfwPagePanel`, war map), theme defaults (`UiKit.BodySize` = caption, `Steel.ButtonSize` = value, ...) | Same table everywhere; plates are one size on every page; titles are one size. |
| Title | `ConceptPanelPage.StyleTitle` / `FitTitle` | Options now uses the blank-plate frame (`overlay-panel`) and live Oswald with the same emboss as every page title. |
| Painted pieces | `CrispArt.Patch` (pill, slider channel, fill, knob), `CrispArt.Fitted` (main menu plates), `FrontEndView.TextureFilter` | Pills and slider parts are drawn from the 4x art as a nine-patch whose caps keep the art's proportion and are shrunk once by the GPU (mipmaps); the slider's own boxes are empty and its knob a blank of the same size, so range, keys, mouse and focus are the real `HSlider`'s. The main menu's plates carry words, so they are resampled once (Lanczos) to the exact window pixels they cover and drawn 1:1 (a mipmap blurred them, a point sample jagged them). |
| Aids and tests | `--text-audit`, `UiTextAudit`, `UiSelfTest.TextLevels.cs`, `tools/ui-measure/text_scale_*.py/sh` | The self-test opens the lobby, host, join and four Options tabs at the eight window sizes and checks: every text is a size of the table, every text is a whole number of window pixels, every plate is one size, every BACK is the plate size, all titles are one size. |

**Open points and deviations**

* **Owner decision (2026-11-18, "Keep painted words"):** the main menu's five plate labels stay the painted reference lettering, drawn 1:1 (resampled once to their window pixels, `CrispArt.Fitted`). This is the one deliberate exception to live text; every other label of every screen is live Oswald.
* The emblem importer's zoom slider (`ConceptSliderStyle`) keeps its prescaled boxes (one rarely used control); the Armoury's and Appearance's dense captions use the caption and small levels (24 and 18), not the 30 and 36 of the concept pages, because their art is laid out at that density.
* The slider's hit area is the knob's travel (as before); the channel's caps (7 UI px each side) are art only.
* `MatchSummaryView` (count-up, battle record) snaps its computed sizes to whole window pixels but keeps its stage-scaled sizes (it composes its own 1280 x 720 stage).
