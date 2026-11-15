# UI concept redesign: inventory and mapping (branch `claude/ui-concept-redesign`)

The owner's request of 2026-11-15: implement the four-panel concept (main menu, combat HUD, Armoury, Options) in the existing Godot UI, matching it
closely at 16:9, then adapt the layout so every existing option fits. Layout and spacing change before art direction; no working feature or major visual
element is removed. This file is the original-to-new inventory the owner asked for. Phase 1 (this commit) builds the steel kit and applies it to the
main menu and the densest Options tab (GRAPHICS); every other row below says where it goes in phases 2 and 3.

Status column: **done** (in this branch, tested), **p2** (phase 2: Armoury, HUD of both modes, the other Options tabs), **p3** (resolution and UI-scale
sweep, review), **keep** (stays as it is, restyled later through the theme only).

All UI is real Controls in a 1920 x 1080 UI space that the window scales (`UiKit.EnterVirtualSpace`, content scale mode canvas items, aspect Expand), so a
1280 x 720 window shows the same layout at two thirds of the size, and an ultrawide or taller window adds room at the sides or the bottom. The HUD has
its own 1080-high virtual space scaled by the window height, with panels anchored to the corners (`Hud.cs`).

## 1. Routes and screens

| # | Route (code) | How you get there | Classic PvP | Musou | New UI | Status |
|---|---|---|---|---|---|---|
| 1 | Install search ("Reading your Space Marine install...", `FrontEndView.ShowLoading`) | start | yes | yes | keeps the video background; text in the steel palette | p2 |
| 2 | Install read failed (`ShowFailure`: RETRY, QUIT) | start, no install | yes | yes | a steel frame with two steel buttons | p2 |
| 3 | Game folder setup (`GameFolderSetup`: Browse, Retry, QUIT) | first run, no install found | yes | yes | steel frame, steel buttons, same fields | p2 |
| 4 | **Main menu** (`ConceptMainPage`): CLASSIC PVP (default focus), MUSOU MODE (added content), ARMOURY, OPTIONS, EXIT, and STOP MUSOU SERVER while a Musou server of this client runs | start | yes | yes | the concept's panel 01 | **done** |
| 5 | Lobby, "Join or host lobby" (`LobbyMenu`): HOST LOCAL LOBBY, JOIN SERVER, JOIN LOCAL LOBBY, SIDE, CONTENT, SPECTATE, active loadout summary | CLASSIC PVP | yes | no | the main menu's frame layout with the same rows as steel buttons and option rows | p2 |
| 6 | Host local lobby (`HostPanel`: mode, reach, port, bots, difficulty, HOST, STOP, JOIN YOUR SERVER) | lobby, Musou page | yes | yes (HOST MUSOU) | steel option rows in the Options frame's two columns | p2 |
| 7 | Join local lobby (address field, CONNECT, BACK) | lobby, Musou page | yes | yes | steel frame, steel field, steel buttons | p2 |
| 8 | Musou page (`MusouMenu`: PLAY ON THIS PC, HOST LOCAL LOBBY, JOIN LOCAL LOBBY, CUSTOMIZER, co-op note) | MUSOU MODE | no | yes | the main menu's frame layout | p2 |
| 9 | **Options** (`OptionsView`): GRAPHICS, SOUND, CONTROLS, INTERFACE | OPTIONS; Esc menu in a match | yes | yes | the concept's panel 04, 1:1 (painted overlay and plates, live rows scrolling inside; phase 4a) | **done** (measured; see section 2 and docs/ui-concept-textures.md) |
| 10 | Keybinds (`ControlsView`: keyboard tab, controller tab, rebind capture, conflict prompt) | CONTROLS, KEYBINDS | yes | yes | steel frame and tabs | p2 |
| 11 | Armoury = the class creator (`CreatorView`): DEFAULTS and CUSTOM 1 to 10, class tabs, steps CLASS, WEAPONS, EQUIPMENT, PERKS, (LOOK), (SUPPORT ABILITIES), preview, item card, action bar | ARMOURY (the old CUSTOMIZER); Esc menu, LOADOUT in a match | yes | yes (Musou layout: STANDARD MARINES, LIBRARIAN, HERETIC (not yet)) | the concept's panel 03 |**done (phase 4d)** |
| 12 | Appearance editor (`AppearanceView`: MY APPEARANCES, CHAPTER, ARMOUR, COLOURS, BADGES, EYES) | APPEARANCE... in the Armoury | yes | yes | steel frame and rows; the preview stays |**done (phase 4d)** |
| 13 | Emblem importer (custom image) | BADGES | yes | yes | steel dialog | p2 |
| 14 | Loading screen (`NetworkView.Loading`, `MenuVideo`: the still `loading-background.jpg`) | a join | yes | yes | unchanged (the request does not cover it) | keep |
| 15 | In-match Esc menu (`MatchMenu`): RESUME, LOADOUT, OPTIONS, LEAVE MATCH, extra rows (Musou: BACK TO STORMBIRD), respawn banner | Esc | yes | yes | the main menu's steel buttons over the match's dim | p2 |
| 16 | In-match loadout picker (the Armoury opened with DEPLOY / SET ACTIVE) | death, LOADOUT | yes | yes (PLAY AS LIBRARIAN) | the Armoury of panel 03 |**done (phase 4d)** |
| 17 | Match flow and summary (`MatchFlowView`, `MatchSummaryView`: warm-up, end splash, scoreboard) | match | yes | yes | steel plates, same data | p2 |
| 18 | Tab scoreboard, kill feed, banners | match | yes | yes (Musou rows) | steel plates | p2 |
| 19 | Musou hub screens (`Extras/Musou/Tfw`: war map, equipment station prompt, reward panels, range confirm, battle panel) | Musou | no | yes | steel plates, inside the added-content boundary | p2 |
| 20 | Perf probe, readout (`HudReadout`, F3) | F3 | yes | yes | keep | keep |

## 2. Settings (the Options screen and every other saved choice)

All of these live in `frontend.json` beside the profile (`FrontEndSettings`, `GameOptions`) and are applied live by `OptionsHost` and the services as
they change. That behaviour is unchanged: every row still applies and saves at once. The new APPLY and REVERT buttons sit on top of it (section 2.4).

### 2.1 GRAPHICS (the densest tab: 12 controls and one information line = 13 rows since the INTERFACE tab was added; it was 17)

| # | Row (label) | Control | Default | Saves / applies | New UI |
|---|---|---|---|---|---|
| 1 | RESOLUTION | choice (1280x720 .. 3840x2160 up to the screen; rests unless WINDOWED) | the window's size | `Options.WindowWidth/Height`; `OptionsHost.ApplyWindow` | steel choice pill, left column |
| 2 | WINDOW MODE | choice WINDOWED / BORDERLESS / FULLSCREEN (F11 follows live) | WINDOWED | `Options.Window`; `ApplyWindow` | steel choice pill |
| 3 | VSYNC | OFF / ON | ON | `Options.Vsync`; `ApplyWindow` | steel switch (the concept's ON/OFF toggle) |
| 4 | FPS CAP | choice UNLIMITED, 30 .. 240 | UNLIMITED | `Options.FpsCap`; `Engine.MaxFps` | steel choice pill |
| 5 | UPSCALING | choice OFF / FSR 2 presets (rests without Forward+) | OFF | `Options.Upscale`; `RenderQuality` | steel choice pill |
| 6 | RENDER SCALE | slider 50 to 100 % (rests while FSR 2 is on) | 100 % | `Options.RenderScale`; `RenderQuality.ApplyScaling` | steel slider, amber fill |
| 7 | SHADOW QUALITY | choice DEFAULT / LOW / MEDIUM / HIGH | DEFAULT | `Options.Shadows`; `MapLightingView` | steel choice pill |
| 8 | ANTI-ALIASING | choice | DEFAULT | `Options.Aa`; `RenderQuality` | steel choice pill |
| 9 | ASHFALL GRIME | choice EXACT (OVERLAY) / FAST (FOLDED, WEAK GPUS) (Musou's Ashfall; forced in safe mode) | EXACT | `Options.GrimeFast`; `AshfallGrime.SetFolded` | steel choice pill |
| 10 | FIELD OF VIEW | slider, horizontal degrees at the window's shape; lowest = the original's | 0 (the data's look, "66 deg (ORIGINAL)") | `FovDegrees`; `FovMath` | steel slider (wider value column) |
| 11 | BRIGHTNESS | slider 50 to 150 % | 100 % | `Options.Brightness`; the brightness pass | steel slider |
| 12 | LIGHTING PRESET | choice, ten looks | FAITHFUL REMASTER | `Options.Lighting`; `LightingGradeView` | steel choice pill |
| 13 | REDUCE MOTION | OFF / ON | OFF | `ReduceMotion`; `UiKit.ReduceMotion` | **moved to INTERFACE** (2.7) |
| 14 | CAMERA SHAKE | slider 0 to 100 % | 100 % | `Options.CameraShake` | **moved to INTERFACE** (2.7) |
| 15 | SCREEN FLASHES | slider 0 to 100 % | 100 % | `Options.ScreenFlashes` | **moved to INTERFACE** (2.7) |
| 16 | SHOW OTHERS' EMBLEMS | OFF / ON | ON | `ShowOthersEmblems` | **moved to INTERFACE** (2.7) |
| 17 | RENDERER | information line (not a control) | FORWARD+ or COMPATIBILITY | read-only (`SafeMode`) | dim line under the two columns |

### 2.2 SOUND (5 rows)

| # | Row | Control | Default | Saves / applies | New UI |
|---|---|---|---|---|---|
| 1 | MASTER VOLUME | slider 0 to 100 % | 100 % | `Options.MasterVolume`; `SoundSystem.ApplyMasterVolume` | steel slider |
| 2 | MUSIC VOLUME | slider | 100 % | `Options.MusicVolume`; `LiveOptions.GainDb` | steel slider |
| 3 | EFFECTS VOLUME | slider | 100 % | `Options.EffectsVolume` | steel slider |
| 4 | VOICE VOLUME | slider | 100 % | `Options.VoiceVolume` | steel slider |
| 5 | MUTE WHEN UNFOCUSED | OFF / ON | OFF | `Options.MuteUnfocused`; `OptionsHost.ApplyMute` | steel switch |

### 2.3 CONTROLS (7 controls)

| # | Row | Control | Default | Saves / applies | New UI |
|---|---|---|---|---|---|
| 1 | MOUSE SENSITIVITY | slider 0.10x to 3.00x | 1.00x | `MouseSensitivity`; `MouseSensitivity.Selected` | steel slider |
| 2 | AIM SENSITIVITY | slider 0.25x to 3.00x | 1.00x | `Options.AimSensitivity` | steel slider |
| 3 | INVERT Y | OFF / ON | OFF | `Options.InvertY` | steel switch |
| 4 | GAMEPAD LOOK SENSITIVITY | slider | 1.00x | `Options.PadLookSensitivity` | steel slider |
| 5 | GAMEPAD AIM SENSITIVITY | slider | 1.00x | `Options.PadAimSensitivity` | steel slider |
| 6 | INVERT GAMEPAD Y | OFF / ON | OFF | `Options.InvertPadY` | steel switch |
| 7 | KEYBINDS | opens the keybind screen (row 10 of section 1) | - | `controls.json` | wide steel button under the rows |

### 2.7 INTERFACE (new tab, owner 2026-11-15; 4 rows, all moved from GRAPHICS unchanged)

| # | Row | Control | Default | Saves / applies |
|---|---|---|---|---|
| 1 | REDUCE MOTION | OFF / ON | OFF | `ReduceMotion`; `UiKit.ReduceMotion` |
| 2 | CAMERA SHAKE | slider 0 to 100 % | 100 % | `Options.CameraShake` |
| 3 | SCREEN FLASHES | slider 0 to 100 % | 100 % | `Options.ScreenFlashes` |
| 4 | SHOW OTHERS' EMBLEMS | OFF / ON | ON | `ShowOthersEmblems` |

No setting was added and no default changed; the concept's example INTERFACE rows (HUD scale, HUD visibility, objective markers, subtitles, colour blind mode, armour glint, UI brightness) do not exist in the game.

### 2.4 Behaviour of the new buttons (the only behavioural addition)

The concept has APPLY, REVERT and BACK. The game's options already apply and save on every change, and that stays (the owner: "keep the existing settings and
their current save/apply behavior"). So: **APPLY** keeps the options as they are now as the point REVERT returns to and says "SETTINGS APPLIED" (nothing is
written that was not written already); **REVERT** puts every option this page edits (all of `GameOptions`, field of view, mouse sensitivity, reduce motion, others' emblems) back to how they
were when the page was opened or last applied, applied and saved like any change; **BACK** leaves, keeping what is set (Esc and the pad's B do the same). No setting, default or file format changed.

### 2.5 Saved choices that are not on the Options screen (unchanged, listed so nothing is lost)

| Choice | Where it lives | Default |
|---|---|---|
| Performance readout (F3) | `Options.Readout`, a hotkey | OFF |
| F11 window toggle | `Options.Window`, the WINDOW MODE row follows | - |
| Side (AUTO / SPACE MARINES / CHAOS) | lobby page, `Side` | AUTO |
| Content (FAITHFUL / EXTENDED) | lobby page, `Content` | per build |
| Spectate | lobby page, not remembered | OFF |
| Server address | JOIN LOCAL LOBBY field, `Host` | the last one |
| Host panel: mode, reach, port, bots, difficulty | `HostPanel`; difficulty `BotDifficulty` | see `docs/commands.md` |
| Active loadout, ten custom classes, looks, emblems | `profile.json` (`ProfileSession`) | DEFAULTS |

### 2.6 Absent from the concept art (and where it goes)

* The concept's INTERFACE tab example rows (HUD scale, HUD visibility, objective markers, subtitles, colour blind mode, armour glint, UI brightness): **no such settings exist**. None is added. The tab exists (owner, 2026-11-15) and holds the four existing rows listed in 2.7.
* AUDIO tab name: ours is SOUND. Kept.
* The concept's "Apply" semantics are described in 2.4.

## 3. HUD: elements and states (to be mapped in phase 2)

The HUD (`client/Hud.cs`, `client/Hud/*`) draws from one `Hud.Frame` filled every frame by `NetworkView`. Its art today is the original's, loaded from the player's own install
at run time (`HudArt`); the concept needs our own steel drawing instead, so phase 2 replaces the frames and keeps every data field. Nothing below changes meaning.

| Element | State it shows (`Hud.Frame`) | Shown in | Concept element it becomes (phase 2) |
|---|---|---|---|
| Armour and health bar | `Health/HealthMax`, `Shield/ShieldMax`, `IronHalo` (the starburst medallion), `VitalsCorner` (Musou's place) | all | the slim segmented top-centre bar (armour cyan over health red, the medallion at its left end) |
| Capture progress | `Points[].Progress`, `Contested`, which point the player stands on | Seize Ground | the thin bar under the top bar ("CAPTURING B 62 %") |
| Objective chips | `Points[]` (letter, `Owner`, `Side`) | Seize Ground | the A / B / C chips top right |
| Team score panel | `OwnScore`, `EnemyScore`, `ScoreLimit`, `Clock`, `Teams`; absent in Musou (`NoScorePanel`) | Annihilation, Seize Ground | **absent from the art**: becomes score chips (own, enemy, limit) and the clock beside the objective chips top right, so Annihilation has its chips too |
| Kill feed | `Kill` lines with weapon silhouettes | PvP (Musou's rows differ) | **absent**: under the chips, top right |
| Reward lines | `EventLine` (`RewardTracker`) | all | **absent**: under the top bar |
| Weapon box | `WeaponName/Wargear`, `Clip/ClipMax`, `Reserve`, heat bar (`HasHeat`, `Heat`, `Overheated`, `Venting`), charge (`Charge`, `ChargeReady`), `WeaponBarFull` (melta), `Braced`, the other and third weapon (`HasOther`, `HasThird`) | all | the framed ammo and weapon cluster bottom right (clip over reserve, the weapon silhouette, the heat or charge bar as its own strip, the holstered weapons as small chips) |
| Grenade box | `HasGrenade`, `Grenades`, `GrenadeCooldown`, `Throwing`, `GrenadesOutText` | all | an ability icon with a count bottom left |
| Jump pack meter | `HasJump`, `JumpPower`, `JumpLow`, `JumpPhase` | classes with a pack (Assault, Raptor, Librarian, Musou's Dreadnought has none) | an ability icon with a fill bottom left (only where applicable) |
| Stims | `StimLeft` | the Inert perk | an ability icon with a timer (only while it applies) |
| Status emblem | the side emblem the score panel shows today | all | the round emblem bottom left (side crest; ring = the class's resource where it has one) |
| Crosshair, spread, aim, scope | `SpreadRadius`, `SpreadFraction`, `Aiming`, `Scoped`, `ReticleScale` (`ScopeView`) | all | kept as is (the concept keeps the combat view open; a thin steel-coloured reticle only if the owner asks) |
| Hit markers, headshot medal | `_hitAt`, `_hitKill`, `_hitHead` | all | kept |
| Aimed enemy | `AimedAtEnemy`, `AimedName`, `AimedBar` (Tactical Readout, Signum Link perks) | PvP | kept (a name and bar over the enemy) |
| Interaction prompt | `Prompt`, `PromptKey` | all | a steel plate above the weapon cluster |
| Banner, respawn, spectate | `Banner`, `RespawnLeft`, `Spectating` | all | steel plate, centre |
| Objective markers | `Points[].Screen`, `Behind`, `Distance` (clamped to the screen's edges) | Seize Ground | kept, drawn with the chips' letters |
| Status line, hint | `Status`, `StatusIsError`, `ShowHint` | all | steel plate, bottom |
| World decorators | `Decorators` (name tags, rings over other players) | PvP | kept |
| Musou run panel | waves, enemies left, lives, own KOs and points (`MusouHud`; not in Musou Mode's campaign phase) | Musou | top-left steel plate |
| Musou boss column | commander and Boss Orks' bars, ENRAGED, thresholds | Musou | the bars bottom left, steel frames (their place is the owner's of 2026-11-10) |
| Musou KO chain | `Chain`, `Multiplier`, `ChainLeft` | Musou | bottom right over the weapon cluster |
| Musou ability cells | `Cells[3]` (the Librarian's Shockwave and Pull, the support call B), `LibrarianBox` | Musou | the ability icons bottom left, the cooldown as a ring (this is where the concept's two ability icons map) |
| Musou powerup buffs | `Buffs` (kind, left, total) | Musou | small round icons beside the emblem |
| Musou call-outs and break countdown | WAAAGH!, ENRAGED, the break's timer | Musou | the strip along the top edge, steel |
| Musou hub (Thunderhawk) | station prompts, war map, battle panel, reward panels | Musou Mode | steel plates |
| Librarian weapon HUD | Force Sword, Chain Lightning and Psychic Stress presentation (`Hud.Arsenal`, `ForceBlade`) | Musou | the ability icons and the cluster, as in panel 03's cards |

The Musou parts stay in `client/Extras/Musou/` and are reached through the existing Extras hooks (`VitalsCorner`, `WeaponBoxCorner`, `NoScorePanel`); `ExtrasBoundaryTests` and
`MusouIsolationTests` hold that line. The Classic HUD gets no Musou information, and the Musou HUD no Classic-only parts (the score panel).

## 4. Loadout controls (the Armoury, phase 2)

| Control | Classic PvP | Musou | Source of the data | New UI |
|---|---|---|---|---|
| Loadout choice | DEFAULTS and CUSTOM 1 to 10 (a list of ten slots) | Musou's own ten slots (`MusouSlots`) | `ProfileSession`, `LoadoutEdits` | the concept's left column of class and loadout rows, gold on the selected one |
| Class (role) | Tactical Marine / Chaos Space Marine, Assault Marine / Raptor, Devastator Marine / Havoc, plus the Ork Boy (Extended content only) | STANDARD MARINES (the three marine classes), LIBRARIAN, HERETIC (marked not yet available) | `ProfileStore.Roles`, `RoleLabel`, `MusouCustomizer` | the class rows (LIBRARIAN, TACTICAL, ASSAULT, DEVASTATOR in the art; DEFAULTS and the ten slots above them as a compact picker) |
| Step: CLASS | the class card (description, the class's armour, speed, health) | the Librarian's page: CLASS, WEAPONS, POWERS, PERKS, LOOK | `LoadoutRows` | the left column, with the class card on the right |
| Step: WEAPONS | primary, pistol, melee (by class rules) | melee weapon (Librarian), Musou's arsenal (Autocannon, claw, spear, charge) | `CustomizationCatalog.Perks.ForRole` | the right-hand equipment cards (the art's FORCE SWORD card for the Librarian, the class's actual weapons for the others) |
| Step: EQUIPMENT | grenade type, other gear | equipment (Librarian) | same | an equipment card |
| Step: PERKS | 48 perks, slots by class, Weapon Versatility's third weapon | two perks (Librarian) | `docs/customizer-and-perks-data.md` | perk cards on the right (the art's ability cards CHAIN LIGHTNING / PSYCHIC STRESS for the Librarian; his Chain Lightning, Warp Pull and Shockwave as the three ability buttons) |
| Step: LOOK | the Ork Boy's look choice (Extended only) | Librarian's look | `OrkLoadout`, `AppearanceEdits` | a card row |
| Step: SUPPORT ABILITIES | not shown | Musou Mode's support call (B) | `MusouSupport` | the art's ability buttons (CHAIN LIGHTNING, WARP PULL, SHOCKWAVE) |
| Preview | the 3D character on the preview stage (Space Marine or Chaos) | the Librarian dressed (`PreviewStage.MusouLibrarian`) | `PreviewStage` | the central preview (kept as is) |
| Item card | the focused item's numbers | same | `CreatorView.ShowItemCard` | the right-hand cards |
| Action bar | Rename (F2), Copy to..., Reset, Clear, Revert, APPEARANCE..., SAVE (Ctrl+S), SET ACTIVE, DEPLOY (in a match), BACK (Esc) / RESUME, LEAVE MATCH | same; PLAY AS LIBRARIAN in a match | `CreatorView` | CONFIRM LOADOUT (the primary, gold) = SAVE and SET ACTIVE for the open loadout (or DEPLOY in a match); BACK secondary; Rename, Copy, Reset, Clear, Revert and APPEARANCE... as the small steel buttons the art omits |
| Mode badge | - | - | the profile's content (`Content`) | the MUSOU / CLASSIC badge top right (new, display only) |
| Appearance (armour and paint) | MY APPEARANCES (ten saved looks per side), CHAPTER / WARBAND, ARMOUR (per part), COLOURS (primary, secondary, per part), BADGES, EYES (lens), custom emblem import | same, the Librarian's look | `AppearanceView`, `AppearanceEdits` | its own page in the same frame (APPEARANCE... opens it, as today) |
| Locks | the lock visuals behind `--lock-demo` (nothing is locked; no progression) | same | `CreatorView` | kept |

"CONFIRM LOADOUT" is a label: it does what SAVE plus SET ACTIVE do today (or DEPLOY in a match). No new mechanic and no change to what saves.

## 5. Counts

* Routes and screens: 20 rows (section 1), of which 9 are in the main-menu tree (main, lobby, host, join local, Musou page, Options, Keybinds, Armoury, Appearance).
* Options controls: GRAPHICS 12 + 1 information line, SOUND 5, CONTROLS 7 (6 + KEYBINDS), INTERFACE 4 = 28 controls and one line. Not on the screen: 8 saved choices (2.5).
* HUD elements: 27 rows (section 3), of which 9 are Musou only.
* Loadout controls: 14 rows (section 4).
* In the art but not in the game: the INTERFACE tab and its seven example settings (2.6); a "UI DESIGN STUDY" legend; the MUSOU badge (display only, added).
* In the game but not in the art: GRAPHICS' 17 rows beyond the art's four, SOUND, the lobby, host, join, Musou page, keybinds, appearance editor, the Esc menu, the scoreboard, loading, the hub; each has its place in the tables above.


## Phase 4b note (Options rows; Armoury not yet done)

Options rows (rows 9, 40+): every choice is the concept's arrow pill, every OFF / ON option the concept's switch, every slider the concept's amber slider; two columns when the labels fit (INTERFACE, SOUND), one otherwise (GRAPHICS, CONTROLS). Every setting, default and handler is unchanged. Armoury (row 11, 12, 16) and the HUD are not part of this commit.


## Phase 4d: the Armoury's controls (owner, 2026-11-15; answers: one card per loadout slot, scrolling; the extras in a tab strip above the class list)

| Original control | Where it is now |
| --- | --- |
| Class tabs (both sides' names; Ork Boy with Extended) | the four class plate rows (gold = the open class; one side's name drawn, both sides in the button's text and tooltip); Musou: LIBRARIAN first, then the marines' classes |
| DEFAULTS and CUSTOM 1 to 10 chips | the SLOTS tab (header strip): the chips wrap in the rail |
| Steps CLASS, WEAPONS, EQUIPMENT, PERKS, LOOK, SUPPORT | the cards: one card per loadout slot (each weapon section, equipment, perk 1, perk 2; the Librarian: melee, powers, perks, look, support); a card opens that slot's choices as cards; the weapon row and the three tiles open the same steps; PageUp / PageDown and the pad shoulders still move through the steps |
| Perk slot chooser (Perk 1 / Perk 2) | the PERK 1 and PERK 2 cards and tiles; the toggles stay in the choices list |
| Faction row (Space Marines, Chaos, Orks; Musou entries) | pills above the preview |
| Item card | under the preview, in the one face |
| APPEARANCE... (armour, paint, badges, eyes; Ork look) | the ARMOUR & PAINT tab (opens the appearance editor) and the dock's APPEARANCE... |
| Rename (F2), Copy to..., Reset, Clear, Revert, SAVE (Ctrl+S), SET ACTIVE, DEPLOY, LEAVE MATCH | the dock of plates under the preview (keys and pad buttons unchanged) |
| CONFIRM LOADOUT (new label) | SET ACTIVE and SAVE (in a match: SAVE and DEPLOY; on the Librarian's page: SAVE and PLAY AS LIBRARIAN) |
| BACK (Esc, B) / RESUME | the BACK plate |
| Mode badge (new) | CLASSIC PVP, or MUSOU in Musou Mode |
