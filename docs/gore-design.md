# Gore: design (2026-10-28)

The owner's request: limbs shot or cut off become independent ragdolls; bolts are miniature explosive rockets; targets explode and blood
flies (moderately) on death. Popular gore mods are a behaviour reference only (never their files or code). **A deliberate change from the
original**: its MP never dismembered marines. Survey notes and extracts: `%LOCALAPPDATA%\sm-reimpl\cache\gore-investigation\` (private).

## What the original has
- `sm_space_marine_player` / `csm_chaos_marine_player` have no `dismemberment_ext`; marine armour has no wound or stump geometry. Marine
  deaths only play effects (`ragdoll_death`, `plasma_limb_vanish` via `actn_plasma_death`, `meltagun_dmg_vaporize_limb`,
  `jumppack_dmg_burn_limb`).
- Every weapon names a `dismemberment_type` (bolter, heavy_bolter, stalker_pattern, lascannon, melta_gun, plasma_light/heavy,
  explosive_light/heavy, chainsword(_heavy), power_axe(_heavy), thunder_hammer(_heavy), bullrush, fury_*).
- The PvE units (Ork boy, nob, gretchin, Guardsmen) carry the rule table `dismemberment_by_type.<type>.hit_zone.<zone>` (`chance` 0–100,
  `dismemberment` point; `nothing_left`) and 33 `dismemberment_point`s that reduce to four outcomes: head, left side (arm), right side
  (arm), whole body. Legs map to the arm points (the original never removes legs). `entities_to_spawn` is empty: no limb ragdolls or gib
  meshes; each point runs `apply_gore` (a blood "gore map" on nearby characters, 15 s + 15 s fade).
- Bolter row: head 75 (`explode_head`), body 50, arms 50, legs 40; `nothing_left` `explode_body`. Heavy bolter: head 100, body 75, arms 75,
  legs 50. Stalker: head 100, arms 90, legs 75, body 50. Explosive light 25–40; explosive heavy 100. Melta 75–100; lascannon and
  plasma heavy 100; plasma light 0–25. Chainsword 0; heavy chainsword, power axe, thunder hammer 100.
- Effects in `master_fx` (no mesh elements): `body_dismemberment`, `body_dismemberment_head`, `body_explode`, `body_dismemberment_limb`,
  `body_dismemberment_drip`, `blood_dismember_arm`, `plasma_dismemberment`, `plasma_limb_vanish`, `meltagun_dmg_vaporize_limb`,
  `blood_death`, `body_death`, blood decals `decal_stain_blood_*`. Stump material `dismember.bmat` (`global_dismemberment_*`).
- Sounds: `play_or_dismemberment`, `play_blood_hit`, `play_col_flesh_medium/large`, `play_ch_bodyfall`, melee flesh hits.

## Design (numbers ours unless named data)
- **Death-only and cosmetic first.** Living players keep their limbs (no gameplay change). The server decides; the client draws.
- **Chances:** the original rows through the weapon's `dismemberment_type`, applied to marines (ours) with legs added (ours), times a
  gore level: off 0, low 0.25, medium 0.6 (default), high 1.0. Melta, lascannon and plasma sever with their vaporize/plasma effect;
  heavy melee severs (100 % × level); light melee never.
- **Bolts are explosive rounds:** the bolter family uses its own rows (head, arms, legs) — limbs and heads blow off; bolter kills do not
  gib the whole body on overkill alone.
- **Explode on death:** an explosive-class kill (explosive_*, melta_gun, lascannon, plasma_heavy, heavy melee) that overshoots the remaining
  shield + health by 75 or more, or the table's `explode_body` with an explosive-class type: all ragdoll joints released, a strong kick,
  `body_explode`, blood.
- **Server:** a `BodyPart` per hit volume (head, torso, each arm, each leg; from the ragdoll body names, matching the blueprint's
  `hit_zones`), `HitEvent.Part/Overkill/Heavy`, `SmContent/GoreTuning.cs` (table plus weapon types), `SmSim/Gore.cs` (roll seeded by a hash
  of the kill sequence, deterministic). One `Gore` byte in `KillView` (severed mask: head, 4 limbs; explode bit) → protocol bump; pay
  for it by lowering the guaranteed shot records from 4 to 3 (worst case 1195).
- **Client:** `Ragdoll.Start(severed, explode)`: skip the shoulder/hip/neck joints (and collision exceptions) of severed parts so they fly
  free; hide the helmet's neck ring when the head goes; stump caps on both cut ends (a small disc mesh with the `dismember.bmat` textures,
  ours); per-part kicks. Effects `body_dismemberment*`/`body_explode`/vaporize, death blood, blood droppers with collision so they leave
  decals; sounds above.
- **Limits:** severed parts live inside their corpse (the 16-corpse cap stays); at most 8 concurrent death sprays; 64 blood decals in a
  ring, 30 s fade; corpses as today.
- **Option:** `--gore off|low|medium|high` and a front-end toggle; the client can only show less than the server decided.
- Later: elbow and knee cuts (bone-weight triangle split per armour piece), the original gore map blood on nearby characters.

## Work breakdown
| # | Task | Size | Files |
|---|---|---|---|
| G1 | Body parts, gore roll, kill gore byte, protocol bump, tests | M | `SmSim/HitVolumes.cs`, `Combat.cs`, `Simulation.cs`, `Teams.cs`, new `SmSim/Gore.cs`; `SmContent/HitVolumeTuning.cs`, new `GoreTuning.cs`; `SmNet/Protocol.cs`; tests |
| G2 | Ragdoll severing, explode, stump caps | M | `client/Ragdoll.cs`, `NetworkView.Death.cs`, `CharacterViewer.cs` |
| G3 | Effects and blood decals | M | `client/FxPlayer*.cs`, `NetworkView.Fx.cs` |
| G4 | Sounds | S | `NetworkView.Sound.cs`, `SoundTriggers.cs` |
| G5 | `--gore` option and front-end toggle | S | `client/FrontEnd/JoinOptions.cs`, `NetworkView.cs` |
| G6 | Docs | S | spec, changes-from-original, CLAUDE.md |

Interpretations to confirm in play: the meaning of `chance` and `nothing_left`; which event plays the plasma/melta death effects.
