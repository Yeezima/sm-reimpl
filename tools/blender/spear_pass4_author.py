"""Spear pass 4 (2026-11-12): re-authors the power spear's three light hews, the F snap cut and the F,F massive stab for weight and readability, over pass 2's pipeline
(tools/blender/spear_pass2_author.py, itself over the pilot's), and exports `Authored/spear_*_p4.json` (format sm-spear-authored-clip/1, read by AuthoredPoseClip).

Authored by us (GPL-3.0); no motion capture and no video-to-motion. What changes against pass 2 (owner, 2026-11-12: "the swing animations need to be reworked as well ... compact
wind-ups, explosive acceleration, a follow-through that carries the body, clean arcs"):

  * a more COMPACT wind-up: the apex of the coil is taken 0.03 s nearer the hit (`antic`), the coil's lead-in is shorter, and the coil itself is a little deeper (`apex_yaw`) so it still reads;
  * more EXPLOSIVE acceleration: the stroke's progress grows as a higher power of time (`p_in` 2.5 to 2.7 against 1.9 and 1.6), so the hips and hands start slowly out of the coil and arrive
    at their peak speed at the hit time (the hit stays the strike's own);
  * a follow-through that CARRIES THE BODY: after the hit the hips keep turning (`over_yaw`, degrees past pass 2's finish, and the stab's weight keeps travelling forward, `over_f`) and settle
    into the finish over 0.2 s, instead of stopping dead with the blade;
  * everything else (the palms' and the shaft's geometry through the stroke, the steps, the recovery order) is pass 2's and is named so in docs/claw-spear-animations.md section 3j.

Run (never with a window):
  blender.exe -b --factory-startup --python tools/blender/spear_pass4_author.py -- --rig <spear-authoring>/rig.json --ref-dir <spear-authoring> --out-dir src/SmContent/Extras/Musou/Authored
      [--only hew2,stab] [--check-dir <private dir>] [--renders dir]
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import spear_pass2_author as P   # noqa: E402

TUNE = {
    "hew1": dict(antic=0.17, coil=0.50, p_in=2.6, p_out=1.5, apex_yaw=-9.0, over_yaw=10.0, over_f=0.0),
    "hew2": dict(antic=0.15, coil=0.22, p_in=2.6, p_out=1.2, apex_yaw=0.0, over_yaw=8.0, over_f=0.0),
    "hew3": dict(antic=0.17, coil=0.26, p_in=2.7, p_out=1.5, apex_yaw=0.0, over_yaw=10.0, over_f=0.0),
    "stun": dict(antic=0.12, coil=0.32, p_in=2.4, p_out=1.5, apex_yaw=0.0, over_yaw=6.0, over_f=0.0),
    "stab": dict(antic=0.20, coil=0.34, p_in=2.5, p_out=1.5, apex_yaw=0.0, over_yaw=0.0, over_f=0.05),
}
for key, spec in list(P.SPECS.items()):
    new = dict(spec)
    new.update(TUNE[key])
    new["file"] = spec["file"].replace("_p2", "_p4")
    P.SPECS[key] = new

_build_p2 = P.build_rows


def build_rows(rig, spec, ref, carry):
    """Pass 2's rows with the carry-through added after the follow-through: the hips go on a little past the finish, then settle into it."""
    rows, landmarks = _build_p2(rig, spec, ref, carry)
    a, h, _dur = landmarks
    f_end = h + spec["follow"]
    hold = spec["hold"]
    hold_row = next(r for r in rows if abs(r[0] - hold) < 1e-6 and r[1] == "S" and "pelvis_yaw" in r[2])
    apex_row = min((r for r in rows if r[1] == "L"), key=lambda r: abs(r[0] - a))
    sign = 1.0 if hold_row[2]["pelvis_yaw"] >= apex_row[2]["pelvis_yaw"] else -1.0
    over, over_f = spec["over_yaw"] * sign, spec["over_f"]
    t1 = f_end + 0.10
    if (over != 0.0 or over_f != 0.0) and t1 < hold - 0.05:
        pushed = dict(hold_row[2])
        pushed["pelvis_yaw"] += over
        pushed["spine_yaw"] = max(-15.0, min(15.0, pushed.get("spine_yaw", 0.0) + 0.5 * over))   # the spine stays inside ClawSpearClips.SpineLimitYaw (16)
        pushed["move_f"] = pushed.get("move_f", 0.0) + over_f
        rows.append((t1, "S", pushed))
        settled = dict(hold_row[2])
        settled["pelvis_yaw"] += 0.4 * over
        settled["move_f"] = settled.get("move_f", 0.0) + 0.4 * over_f
        rows.remove(hold_row)
        rows.append((hold, "S", settled))
    return sorted(rows, key=lambda r: r[0]), landmarks


P.build_rows = build_rows
for key in P.TECHNIQUES:
    P.TECHNIQUES[key] += " Pass 4: a more compact coil, a higher-power acceleration into the hit and a carry-through of the hips after it."


def main():
    P.main()
    # relabel the exported files (the shared exporter writes pass 2's header)
    args = sys.argv[sys.argv.index("--") + 1:]
    opt = {args[i]: args[i + 1] for i in range(len(args) - 1) if args[i].startswith("--") and not args[i + 1].startswith("--")}
    for key in (opt["--only"].split(",") if "--only" in opt else list(P.SPECS)):
        path = os.path.join(opt["--out-dir"], P.SPECS[key]["file"])
        data = json.load(open(path, encoding="utf-8"))
        data["authoring"]["script"] = "tools/blender/spear_pass4_author.py"
        data["authoring"]["pass"] = 4
        with open(path, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(data, fh, indent=1, sort_keys=False)
            fh.write("\n")


if __name__ == "__main__":
    main()
