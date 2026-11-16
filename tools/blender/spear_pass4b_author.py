"""Spear pass 4b (2026-11-12): the second iteration on the five strokes (hews 1-3, the F snap cut, the F,F massive stab) over pass 4's re-authoring
(tools/blender/spear_pass4_author.py, itself over pass 2's pipeline) and exports `Authored/spear_*_p4b.json` (format sm-spear-authored-clip/1, read by AuthoredPoseClip).

Authored by us (GPL-3.0); no motion capture and no video-to-motion. What changes against pass 4 (owner, 2026-11-12: "do a full pass ... no jank"; compact wind-up, explosive
acceleration, a body-driven follow-through, clean arcs):

  * the coil is a touch more COMPACT again (`antic` 0.01 s nearer the hit) and a bit deeper (`apex_yaw`), so the wind-up still reads but takes less time;
  * the stroke's progress grows as a still higher power of time (`p_in` +0.3), so the hips and hands leave the coil later and arrive at their peak speed AT the hit;
  * the follow-through carries the body further (`over_yaw` +3 to +4 degrees; the stab's `over_f` +0.02 m), settling over the same 0.2 s.
  The palms' and the shaft's geometry, the steps, the hand-overs and every gameplay timing are pass 4's (the adapter refuses a changed hit, length or base clip).

Run (never with a window):
  blender.exe -b --factory-startup --python tools/blender/spear_pass4b_author.py -- --rig <spear-authoring>/rig.json --ref-dir <spear-authoring> --out-dir src/SmContent/Extras/Musou/Authored
      [--only hew2,stab] [--check-dir <private dir>]
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import spear_pass4_author as P4   # noqa: E402  (this also sets up pass 2's pipeline with pass 4's tuning)

P = P4.P

TUNE4B = {
    "hew1": dict(antic=0.16, coil=0.50, p_in=2.9, p_out=1.5, apex_yaw=-10.0, over_yaw=13.0, over_f=0.0),
    "hew2": dict(antic=0.14, coil=0.22, p_in=2.9, p_out=1.2, apex_yaw=0.0, over_yaw=11.0, over_f=0.0),
    "hew3": dict(antic=0.16, coil=0.26, p_in=3.0, p_out=1.5, apex_yaw=0.0, over_yaw=13.0, over_f=0.0),
    "stun": dict(antic=0.11, coil=0.32, p_in=2.7, p_out=1.5, apex_yaw=0.0, over_yaw=8.0, over_f=0.0),
    "stab": dict(antic=0.19, coil=0.34, p_in=2.8, p_out=1.5, apex_yaw=0.0, over_yaw=0.0, over_f=0.07),
}
for key, spec in list(P.SPECS.items()):
    new = dict(spec)
    new.update(TUNE4B[key])
    new["file"] = spec["file"].replace("_p4.json", "_p4b.json")
    P.SPECS[key] = new
for key in P.TECHNIQUES:
    P.TECHNIQUES[key] += " Pass 4b: a still more compact coil, a higher-power acceleration and a deeper carry-through."


def main():
    P4.main()
    args = sys.argv[sys.argv.index("--") + 1:]
    opt = {args[i]: args[i + 1] for i in range(len(args) - 1) if args[i].startswith("--") and not args[i + 1].startswith("--")}
    for key in (opt["--only"].split(",") if "--only" in opt else list(P.SPECS)):
        path = os.path.join(opt["--out-dir"], P.SPECS[key]["file"])
        data = json.load(open(path, encoding="utf-8"))
        data["authoring"]["script"] = "tools/blender/spear_pass4b_author.py"
        data["authoring"]["pass"] = "4b"
        with open(path, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(data, fh, indent=1, sort_keys=False)
            fh.write("\n")


if __name__ == "__main__":
    main()
