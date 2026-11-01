# Controls

These are the bindings in this build. The game's data does not contain the PC default keys, so they come from the project owner's memory of the original
(marked "from memory" below) or are our own choice (marked "ours"). If a binding differs from what you remember in SM1, that is a useful report.

The bindings below are the defaults. Every one can be changed: CONTROLS on the Join screen (next to the field-of-view setting) and in the Esc menu opens the rebinding screen, with one tab for the keyboard and mouse (two bindings per action) and one for the controller. The change is saved and applies at once.

## In a match

### Move and look

| Input | Action | Note |
|---|---|---|
| W A S D | Move forward, left, back, right | |
| Mouse | Look and aim | The mouse is captured while you play. |
| Left / Right arrows | Turn left / right with the keyboard | Ours. Handy without a mouse. **Changed:** this was Q / E until the ammunition drops; E now picks things up. |
| E | Pick up a dropped ammo or grenade box | Ours (SM1's "Use / Execute" key is not in the game's data). Every killed player drops a box (ammo 3 times in 4, grenades otherwise); stand within 4 m and press E once when "[E]  Pick up Ammo" or "Pick up Equipment" shows. Anyone can take it, from either side; it disappears after 25 s. |
| Shift (hold) | Sprint | Sprint ends while you hold fire with a ranged weapon, and is ignored in aim mode. |
| Space | Evade or jump, depending on class | Tactical: roll in the stick direction (8 directions). Devastator and Havoc: a short hop. Assault and Raptor (jump pack): jump; hold Space for more height. The key is ours. |
| Left Ctrl (hold) | Aim mode | From memory. Narrower field of view, slower look, slower movement. Ends on melee, throw, jump or death. |

### Weapons

| Input | Action | Note |
|---|---|---|
| Left mouse | Fire | Full-auto weapons fire while held. Semi-auto weapons fire once per press. |
| Left mouse on charge weapons | Tap = normal shot, hold = charged shot | Plasma gun, plasma cannon, plasma pistol, lascannon. From memory (owner's rule). |
| R | Reload | With launcher grenades in the air, R detonates them instead. Heavy bolter and plasma weapons use heat instead of reloads; the melta recharges by itself. |
| 1 | Pistol | From memory. |
| 2 | Primary weapon | From memory. |
| X or mouse wheel | Switch weapon | Ours. Either wheel direction switches. |
| G | Throw your equipment grenade | Ours. Frag or blind grenade, chosen in your loadout. The files call the blind grenade "krak"; it only blinds. |

The first shot from a lowered weapon waits for the weapon to come up (about 0.2 s for rifles, 0.167 s for pistols). Heavy bolter, grenade launcher, melta, lascannon and plasma cannon are always ready.

### Melee

| Input | Action | Note |
|---|---|---|
| Right mouse, or V | Melee attack | From memory (right mouse). V is an extra bind of ours. Press repeatedly for the combo. In the air with a jump pack: ground pound. |
| F | Stun strike (heavy melee) | From memory. |
| Shift + W + F | The charge | Hold Shift (sprint) and W when you press F (owner's rule, 2026-10-31; protocol 49 replaced the W-only rule of 2026-10-29). Hold A or D with W to charge a bit (25 degrees) to that side. Assault and Raptor charge from the ground only. F without Shift is the stun strike in the direction of the held W, A, S, D keys (forward with none). Controller: sprint (left stick click) held + stick forward + Y. |
| Space tap, then right mouse | The dash attack (Assault, Raptor) | During the short dash hop. Hold W, A, S, D or two of them at the right-mouse press to lunge that way (eight directions); no key: along the view. |

### Menus and information

| Input | Action | Note |
|---|---|---|
| Tab (hold) | Scoreboard | It also shows by itself when the match has ended. Layout is a placeholder. |
| Esc | Opens the customizer (class creator and armour editor) over the match, with the mouse free | The match keeps running and your character stands still while the menu is open. Changes apply at your next respawn. Esc again, or RESUME, closes it. LEAVE MATCH returns to the Join screen. |
| C (while dead) | Open the loadout picker from the death screen | Ours. Not available while you are alive. |
| F1 | Show or hide the info overlay | Status, controls and notes about what is a placeholder. |
| F11 | Fullscreen on or off | Works everywhere: menus, Options, in a match, the customizer. It changes OPTIONS, GRAPHICS, WINDOW MODE and is remembered. Not rebindable. |
| T | Shout FOR THE EMPEROR | Added content (loyalist barks; on in the extended profile, off with `--content faithful`). Loyalist Space Marines only, at any time, at most every 2 s; everyone near you hears it. It never interrupts what you are doing and plays no animation. |
| Click in the window | Recapture the mouse after Esc | |

The mouse must be captured for fire, melee, stun strike, grenade and weapon switch to work. If nothing fires, click in the window once.

## Join screen and customizer

| Input | Action |
|---|---|
| Enter in the SERVER box | Join |
| 1 to 9, 0 | Pick loadout slot 1 to 10 on the slot strip |
| Esc | Back, or cancel a connection that is going nowhere |
| Ctrl + S | Save (class creator and appearance editor) |
| Ctrl + Z | Undo (appearance editor) |
| F2 | Rename the selected class slot (class creator) |
| Page Down / Page Up | Next / previous step or section |
| Mouse wheel on the preview | Zoom |
| Arrow keys on the preview | Left and right rotate, up and down zoom |
| Home on the preview | Reset the preview view |

## What we want to know

- Does any default key differ from your SM1 key? Say which, and what SM1 used.
- Do the 1 and 2 keys do the same as SM1 on your keyboard layout? They are physical keys, not letters.
- Is the aim mode, the stun strike or the charge started differently in SM1?
- Which key picked up ammo ("Use / Execute") in SM1? Did one press do it, and could you pick up with full ammunition?
