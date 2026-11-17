# Mapmaker dock layout proposal (2026-11-18, for the owner's review)

Request: after the current Mapmaker work, "an organization of the mapmaker godot scene editor plugin ... order the features logically and offer me a preview for review."
Standing UI rule applied: one text style per level in every menu; crisp, symmetric borders; never mix painted and live labels.
This is a **layout-only** change (placement, grouping, labels, text styles). No feature was removed and no behaviour changed. Branch `claude/mapmaker-organise`, not merged.

## What changed in one picture

| | Before | After |
|---|---|---|
| Tabs, left to right | Start, Build, Object, Library, Tools, Prefab | **Map, Library, Build, Object, Prefab, Test** |
| Mouse tool and Undo | Tools tab (two clicks away) | a bar above the tabs, always visible |
| Play test | big button in the Start tab (twice: quick button and full button) | one **Play test** + **Stop** row below the tabs, on every tab; its settings are on the Test tab |
| Help text | 8 long paragraphs, in 3 different styles | one short muted hint line per section; the 2-minute guide is the Map tab hint's tooltip |
| Widest tab (minimum width) | about 995 px (Tools), 944 px (Object) | about 470 px; tab titles are compact so all six fit a narrow dock |

Work order the tabs follow: **start or open a map (Map) -> place things (Library, Build) -> edit the selection (Object) -> reuse and refine (Prefab) -> check and play test (Test + footer).**

## Text styles (one per level)

| Level | Style | Where |
|---|---|---|
| Tab title | Godot's tab bar, 14 px, tight padding | the six tabs |
| Section heading | bold "HeaderSmall", a thin rule above every section after the first | every tab |
| Field label | plain label, fixed width so fields line up | Name, Layout, Position, Mode, ... |
| Hint | 12 px, muted blue-grey, one or two lines under the heading | every section that needed help |
| Status line | the dock's bottom line (unchanged: transient messages) | below the footer |
| Buttons | verb first, sentence case, "..." only when a dialog or file picker follows | everywhere |

Fixed along the way: a heading that held a whole sentence ("Pivot / origin (point in prefab coordinates ...)") became a heading plus a hint; the Build tab's and Library tab's paragraphs became hints; the Check facts, share info, SM1 info and prefab info lines now use the hint style (they were plain body text); two "PLAY TEST" buttons and two recent-map drop-downs were duplicates (see questions).

## Tab by tab

### Map (was Start, plus most of Tools)
Reasoning: everything about the file and the map as a whole, in the order a session starts: **New map** (name, layout, New map), **Open and save** (Open map..., Your maps, Save map, Save map as...), **Game modes**, then the settings you touch once per map, **Snap** and the **SM1 reference map**.
The 2-minute guide moved to the tooltip of the first hint line.

### Library (was Library)
Reasoning: placing is the most-used action, so the list comes first with its filter; import is rarer and sits below it; deletion is last.
Sections: **Library** (source, filter, list, "Placing: ..." line, item count), **Import** (Import asset..., Import folder..., Import prefab...), **Remove** (Delete selected prefab...). The file types moved from the button label into its tooltip. "Extract picked SM1 objects to prefab..." moved to the Prefab tab.

### Build (was Build)
Same pieces and sections (Ground and cover, Spawns, Objectives and hazards). The two paragraphs became one intro hint (top) and a per-section hint (Spawns). The "Placing: ..." line sits under the intro.

### Object (was Object)
Reasoning: top to bottom is what you do to a selection. **Selection** (what is selected, Name), **Transform** (Space, Position, Rotation, Scale), **Actions** (Duplicate, Toggle visibility, Group to prefab...), and **Delete** alone at the bottom behind a rule.

### Prefab (was Prefab, plus Extract)
Sections: **Create** (Extract picked SM1 objects...), **Prefab** (chooser, Name, Collision, info), **Parts** (list, transform, Hide part, Show part, Remove part last), **Pivot** (Shift, Apply pivot, Use bottom centre, Use first part), **Remove** (Delete this prefab...). Import prefab stays in Library > Import (it is where files come in).

### Test (new; the check and the play test settings, from Start)
Reasoning: "check and play test" is the last step. **Check** (headline, issues with Fix it / Select, facts), **Play test settings** (Mode, Bots a side, I start as), **Share** (Show file, Copy path, file info).

### Above the tabs and below them
- Tool bar: **Select**, **Pick SM1**, **Place**, **Undo**.
- Footer: **Play test** (large), **Stop**, play test progress.

## Old to new: every control

| Old place | Old label | New place | New label |
|---|---|---|---|
| Start | Quick-start paragraph (5 steps) | Map, hint tooltip | (same text, updated for the new tabs) |
| Start | Name (field) | Map > New map | Name |
| Start | Layout (drop-down) + description | Map > New map | Layout + description (hint style) |
| Start | New map | Map > New map | New map |
| Start | Open map... | Map > Open and save | Open map... |
| Start | Your maps... (drop-down) | Map > Open and save | Your maps... |
| Start | PLAY TEST (quick button) | footer | Play test (one button, replaces both) |
| Start | status under quick button | footer | play status (one line, replaces both) |
| Start | Game modes this map is for: Annihilation, Seize Ground | Map > Game modes | Annihilation, Seize Ground |
| Start | Check: headline, issue rows (Fix it, Select), facts | Test > Check | same |
| Start | Play test: Mode, Bots a side, I start as | Test > Play test settings | Mode, Bots a side, I start as |
| Start | PLAY TEST (full button) | footer | Play test |
| Start | Stop | footer | Stop |
| Start | Share: Save | Map > Open and save | Save map |
| Start | Share: Save As... | Map > Open and save | Save map as... |
| Start | Share: Show file | Test > Share | Show file |
| Start | Share: Copy path | Test > Share | Copy path |
| Start | file / send info | Test > Share | same, hint style, shorter |
| Build | intro paragraph | Build, intro hint | shortened; mentions Map tab for snap |
| Build | Floor, Wall, Cover block, Ramp, Column, Invisible wall | Build > Ground and cover | same |
| Build | Marine spawn, Chaos spawn, Marine door, Chaos door | Build > Spawns | same |
| Build | Capture point, Kill zone | Build > Objectives and hazards | same |
| Build | "Placing: ..." line | Build, under the intro | same |
| Build | closing paragraph (Object tab, arrow) | Build > Spawns hint (arrow) and intro (Object tab) | merged |
| Object | selection info | Object > Selection | same |
| Object | Name | Object > Selection | Name |
| Object | Local / World drop-down | Object > Transform | Space |
| Object | Position, Rotation, Scale | Object > Transform | same |
| Object | Duplicate | Object > Actions | Duplicate |
| Object | Toggle visible | Object > Actions | Toggle visibility |
| Object | Group to prefab... | Object > Actions | Group to prefab... |
| Object | Delete | Object, bottom | Delete |
| Object | rotation paragraph | Object > Transform hint | shortened |
| Library | source drop-down, Filter | Library > Library | same |
| Library | list, "Placing: ..." line, item count | Library > Library | same (count in hint style) |
| Library | Import asset (.zip/.fbx/.glb/.gltf)... | Library > Import | Import asset... (types in the tooltip) |
| Library | Import folder... | Library > Import | Import folder... |
| Library | Import prefab... | Library > Import | Import prefab... |
| Library | Extract picked SM1 objects to prefab... | Prefab > Create | Extract picked SM1 objects... |
| Library | Delete selected prefab... | Library > Remove | Delete selected prefab... |
| Library | usage paragraph | Library > Library hint | shortened |
| Tools | map title line | Map, top | same (clips long paths, full text in tooltip) |
| Tools | New, Open..., Save, Save As... | Map | New empty map (next to New map), Open map..., Save map, Save map as... |
| Tools | Recent maps... (drop-down) | Map > Open and save | merged with Your maps... (see questions) |
| Tools | SM1 map name, Open SM1 map | Map > SM1 reference map | same |
| Tools | Show SM1 map | Map > SM1 reference map | Show SM1 map |
| Tools | SM1 info line | Map > SM1 reference map | same, hint style |
| Tools | Select, Pick SM1, Place | tool bar above the tabs | same |
| Tools | Snap: Grid (m), Angle (deg) | Map > Snap | same |
| Tools | snap paragraph | Map > Snap hint | shortened |
| Tools | Undo | tool bar above the tabs | Undo |
| Tools | Frame SM1 map | Map > SM1 reference map | Frame SM1 map |
| Prefab | Prefab, Name, Collision | Prefab > Prefab | same |
| Prefab | Meshes (parts) list | Prefab > Parts | Parts |
| Prefab | part Position, Rotation, Scale | Prefab > Parts | same |
| Prefab | Remove part | Prefab > Parts, last | Remove part |
| Prefab | Hide part on selected instance | Prefab > Parts | Hide part |
| Prefab | Show | Prefab > Parts | Show part |
| Prefab | Pivot / origin: Shift | Prefab > Pivot | Shift |
| Prefab | Apply pivot | Prefab > Pivot | Apply pivot |
| Prefab | Bottom centre, First part | Prefab > Pivot | Use bottom centre, Use first part |
| Prefab | Delete this prefab... | Prefab > Remove | Delete this prefab... |
| Prefab | prefab info line | Prefab > Prefab | same, hint style |

Not in the dock (unchanged): Library row menu (Rename...), F2, drag and drop of files, the dialogs (Extract to prefab, Delete prefab, Not a Mapmaker map).

## Code and tests

- `DockTab` is now `Map, Library, Build, Object, Prefab, Test`. The in-editor drivers use the enum and the dock's test aids, not button texts, so only `DockTab.Start` became `DockTab.Map`. Selftest, FlowTest (63 checks) and LibraryTest pass.
- New agent aid in FlowTest: `--mm-docktabs --mm-tabsuffix <word>` saves a cropped picture of every dock tab (used for the screenshots below).
- Screenshots: `%LOCALAPPDATA%\sm-reimpl\tmp-build\mm-organise\` (`NN-<tab>-before.png`, `-after.png`, `-compare.png`; the six original tabs are in `old-tabs\`). The Map tab compares against the old Start and Tools tabs, the Test tab against the old Start tab. Before and after were taken in the same run setup (the dock is as wide as its content needs, so the widths differ).

## Questions for the owner

1. **Tool bar above the tabs.** I moved the mouse tool (Select, Pick SM1, Place) and Undo out of the Tools tab into a permanent bar. Pro: always one click, and the Tools tab is gone (7 tabs did not fit a narrow dock). Con: four always-visible buttons use one row of height. Keep, or put them back on a tab?
2. **Snap and SM1 reference on the Map tab.** They are per-map settings I touch rarely, so I put them at the bottom of Map. Would you rather have Snap on Build and Library (where it applies)?
3. **Two duplicates merged.** There were two "recent maps" drop-downs (Start and Tools) and two Play test buttons; I kept one of each. Say if you want the second Play test button back on the Map tab as well.
4. **Save map / Save map as...** now live on Map only (they were on Start Share and Tools). Test > Share keeps Show file and Copy path. Fine?
5. **Extract picked SM1 objects...** moved from Library to Prefab > Create (it makes a prefab). Library keeps Import prefab... and Delete selected prefab... because they act on the list row you clicked. Right split?
6. **Parent and layer.** You listed "parent" and "layer" as object properties. The Object tab has neither today (the document has a parent, but the dock does not show it, and there are no layers), so there was nothing to place. Do you want controls for them? That would be a feature, not a layout change.
7. **Label "Play test"** replaces "PLAY TEST" (sentence case like every other button). The button is still the largest control. OK?
8. **Status line** (the dock's bottom message) kept its large body style because it carries errors and results. Should it match the hint style instead?
