# Drop pod assault: the second support ability (Musou Mode, added content)

Owner, 2026-11-12: a new SUPPORT section in the Customizer, every faction and class gets it; option 1 is the existing artillery strike; option 2 is the
drop pod assault: B marks a point with the laser as the artillery strike does; a few seconds later a drop pod falls fast from the sky with a streak of fire and
smoke, the screen shakes hard on impact, the doors drop open and 4 tactical Marines come out, fight for 30 s, retreat to the Imperial home base (HQ) and disappear.
Musou Mode only (`--content musou`); Classic PvP has no support call, its gameplay and config hash do not change. Every number below is **ours / provisional**
unless marked, and goes into spec "Added content: Musou: drop pod assault" and the refine register.

## 1. How the support is chosen
* **Where**: a new customizer step SUPPORT, shown only in Musou Mode's layout (`MusouLayout`): the 5th step of the Standard Marines page (after PERKS) and the 6th of
  the Librarian's page. Two rows: ARTILLERY STRIKE (default) and DROP POD ASSAULT, with a card. "All factions and classes": Musou plays Space Marines only (the
  three classes' DEFAULTS and the ten custom slots, plus the Librarian); the Chaos and the Ork Boy have no Musou page, so nothing is added for them.
  A DEFAULTS loadout cannot be edited, so choosing on it behaves as a perk does ("edits a copy"): the choice is stored per DEFAULTS role instead (no copy is
  made; see open question 4).
* **Saved**: in the profile's Musou extras entry (`CustomizationProfile.Extras["musou"]`, the Librarian's loadout lives there too) as
  `"support": { "d:tactical": "droppod", "c:3": "droppod", "l": "droppod" }` (default artillery is never stored). Read and repaired by `MusouSlots`; a profile
  without it, and Classic's file, are byte for byte as before. Copying or clearing a slot carries or drops its entry.
* **Wire**: nothing is sent at deploy. The client knows its active Musou loadout and, on B, sends `TfwCommand.Orbital` (artillery) or the new
  `TfwCommand.DropPod` (11 is FireTest; the next free value), both with the crosshair point. The server validates and runs either, so a player's choice is
  server-checked and server-authoritative. Core `Protocol.Version` stays; `TfwRules.Version` 71 (after main's 70; lead renumbers; `MusouSim.Version` stays: a war-only change) because the command, a fire
  record kind (`TfwFireKind.Pod` 8, tag 11) and the marines' sim change.

## 2. The existing artillery strike (reused)
`B` (D-pad left on the pad) in `TfwOrders.TfwOrdersFrame` (battle in, alive, or at the Firing Range): the client draws the red designator beam
(`TfwDesignatorBeam`), sends `Orbital` with `TfwCrosshairPoint`; `World.TfwOrbital` validates (battle, in the field, alive, point on the battlefield) and refuses while
`TfwFire.OrbitalTicksLeft` runs. **Cooldown 60 s per player, none at the Firing Range** (`TfwFireRules.OrbitalCooldownSeconds`, owner 2026-11-06; the 2026-11-01
removal was reversed). The barrage starts 1 s later. The drop pod reuses: the B key press, the designator beam, the point clamp and ground snap
(`TfwGroundBelow`), the validation, the **same per-player cooldown** (60 s, shared: one support call a minute whichever kind; none at the range) and the HUD line,
which reads "B DROP POD READY" / "IN n S". The own block's cooldown byte (tag 7) is unchanged.

## 3. The pod
* **Model (original asset from the install, loaded at runtime, never committed)**: `DropPod.dcm` with its `dropPod_2` material and `droppod_dif`/`_nrm` textures in the
  campaign chunk `part_01__s_004_r350_airlock.oppc` (also `part_02_04__s_013_r200_arena_1and2.oppc`). **Singleplayer-content dependency** (as the Thunderhawk
  is): the client loads it as `TfwFireView` loads `thunderhawk`, and draws a plain capsule pod (placeholder) when the chunk is missing. The mesh has 7 blocks: block 5
  the hull (7.2 m wide, 10.7 m tall), block 6 an interior, blocks 0 to 4 five door panels (block 0 modelled apart, blocks 1 to 4 in one place: their poses come
  from the mesh's own object data or our placement; the door drop is a **provisional approximation** until judged). Ork drop pods (`ork_droppod_a`) exist in co-op maps; not used.
* **Effects (original, master_fx)**: `drop_pod_trail` (the streak of fire and smoke) on the falling pod, `droppod_land_close` and `drop_pod_landing_destruction` at the
  impact, `droppod_engine_cooling_down` on the landed pod; sound `play_droppod_incoming` (already the artillery whistle's fallback) while it falls.
* **Timeline (ticks at the server rate; ours)**: call at t0; the beam shows at once; the pod lands at t0 + 3.0 s (the owner's "a few seconds later"); the client sees
  the fall from 90 m up over the last 1.5 s (rapid, ~60 m/s), tag 11 `Pod` record sent at call time so the client can start it exactly; doors open over 0.6 s at
  landing; the marines appear at landing + 1.0 s and fight for 30 s (the owner), then retreat; they vanish at the HQ (within 6 m) or after 40 s of retreat; the pod
  node stays on the ground until the squad is gone (+ 2 s) and then goes with it.
* **Impact**: screen shake `TfwShake` 0.12 rad at 0 m falling to 0 at 90 m (the artillery's is 0.05, cap 0.07: "hard" is ~2.5x; cap raised to 0.14 for the pod);
  crater decal as a shell's. **Does the pod hurt Orks on landing? The owner did not say.** Provisional: it kills every ordinary enemy within 6 m of the touchdown
  (like a shell, `MusouDeathKind.Barrage`, x1.2 for a human caller; a boss takes `BossDamage`) and never hurts players, AI Marines or Guardsmen (so a call at your own feet is safe).
  A point within 10 m of the caller is refused (provisional) so a pod does not land on him.

## 4. The four Tactical Marines
Reuse the AI Marine system (`MarineBrain`, `World.TfwPuppets.cs`): a new `MarineRole.Pod` beside Retainer and Ally. Puppets are server-side players without a peer
(ids 65516 to 65527, **12 shared with retainers and the allied pair**), the Tactical class's default loadout (the owner's "tactical marines": `TfwMarineClass("tactical")`,
primary 0), side Space Marines, wearing the caller's look as a retainer does. Fight phase: hold the landing point (`PlanRetainer` Hold, engaging within
`MarineRules.HoldRadius`) for 30 s; then retreat phase: `MarineBrain.Plan` with the objective `Layout.Hq` (the Imperial HQ; the run's `Field.MarineStart` at the range), sprinting
by the walking graph, no stopping to fight; at the HQ or after the timeout `TfwDismissMarine`. A Marine killed is not replaced and is removed 3 s after.
They take the AI Marines' damage scale (spec: a tenth of every hit) and burn a pod's lifetime only. **They do not follow the caller** (provisional; open question 3).

## 5. Limits
One pod squad per player at a time (a call while that player's pod is falling or its squad lives is refused and costs no cooldown); a call that needs more free puppet ids than
exist (4) is refused. With the 12 ids: the allied pair (2) and a retainer for each human (up to 4) leave room for one squad with 4 humans and two with up to 2; the others' calls are refused until a squad ends. The cooldown starts only on an
accepted call. The call is not accepted in the Stormbird, between battles, dead or off the field. A battle ending removes the pod and its marines (`TfwDismissMarines`).

## 6. Open questions for the owner
1. Does the landing kill Orks (we guessed: within 6 m, never friends)? Radius?  2. Cooldown 60 s shared with the artillery (our default), or its own?
3. Should the marines follow the caller or stand at the landing point (we: stand and fight, 30 s)?  4. DEFAULTS loadouts: store the choice per DEFAULTS role (ours) or
force a copy to a custom slot?  5. One pod at a time per player, one support call per cooldown: right?  6. The retreat point is the Imperial HQ (`Layout.Hq`); a marine
that cannot reach it in 40 s vanishes where it stands.  7. May the caller mark a point near himself (we: not within 10 m)?
