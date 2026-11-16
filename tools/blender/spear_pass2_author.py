"""Spear pass 2 (2026-11-12): authors the power spear's three light hews, the F snap cut and the F,F massive stab in Blender (headless), from the hip carry, and exports
each as original WeaponPoseClip keyframe data (format sm-spear-authored-clip/1, read by src/SmContent/Extras/Musou/AuthoredPoseClip.cs).

Authored by us (GPL-3.0); no motion capture and no video-to-motion. It builds on the pilot's pipeline (tools/blender/spear_hew_author.py: the marine's rig from the PRIVATE
`smtool spear-rig-dump`, FK in the runtime overlay's arithmetic, two-bone arm and leg solves, controls as Blender F-curves, 60 Hz export) and changes what is authored:

  * the carry is the owner's reference carry (2026-11-12): the hips squared to the facing (WeaponPoseClip.SquareYaw 71 degrees), the right arm hanging beside the hip, the shaft
    diagonal across the right side with the blade forward and up; every clip starts from it (or, chained, from the stroke before) and returns to it;
  * each stroke is built as a TECHNIQUE (docs/claw-spear-animations.md section 3h, which names the source technique of each): a held ready pose that yields at once to the next
    action, a short compact coil, a stroke whose speed grows to its peak at the existing hit time (the hips' turn and the passing step lead, the arms follow), a brief deliberate
    finish, and a quick recovery to the carry;
  * every gameplay timing is the strike's own (length, hit time, combo opening); the adapter refuses data that moves them.

What is carried over from the previous pass and named so: the palms' and the shaft's geometry through the stroke (measured from the previous clips' keys in the chest frame with
`--ref-dir`, private channel dumps of the previous strokes), because the clearance and hand-over tests had cleared it; it is re-timed (the acceleration law), not re-invented.

Run (never with a window):
  blender.exe -b --factory-startup --python tools/blender/spear_pass2_author.py -- --rig <spear-authoring>/rig.json --ref-dir <spear-authoring> --out-dir src/SmContent/Extras/Musou/Authored
      [--only hew2,stab] [--check-dir <private dir>] [--blend f.blend] [--renders dir]
"""
import json
import math
import os
import sys

import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import spear_hew_author as A   # noqa: E402

FPS = 60.0
DEG = A.DEG
END_SPINE_YAW = 2.0     # the stroke ends with the spine and head turned a little, as the carry holds them (4 and -4 on SpineD and the head)
CARRY_DROP = -0.025   # ClawSpearClips.CarryDrop: the carry lowers the hips 2.5 cm so the squared hips keep the planted feet in reach
SQUARE = 71.0   # WeaponPoseClip.SquareYaw: the carry's hips are squared to the facing, 71 degrees from the hammer idle's stance (ClawSpearClips.IdleHipsTurn)

# The carry (ClawSpearClips.P2*, SpearHold.Pass2Carry*; facing frame, degrees: elevation, azimuth). The in-game test compares this script's joints with the overlay's.
CARRY_AIMS = {"R": ((-74.0, 40.0), (-62.0, 32.0)), "L": ((-78.0, -18.0), (-68.0, -8.0))}
CARRY_EL, CARRY_AZ = 62.0, -22.0
CARRY_GRIP, HEW_GRIP, LOW_GRIP = 0.95, 0.3, 0.5
TEMPO = 0.85

# One row per stroke. Times are the hammer's (the strike's timing contract) divided by the spear's tempo; `antic` and `follow` are the existing apex and follow-through offsets.
#   start  : "carry" (the stroke starts from the one-handed carry) or "chain" (it starts from the pose of the stroke before, taken from the previous clip's first key).
#   coil   : seconds before the apex at which the coil begins (a chained stroke holds its ready pose until then; a stroke from the carry coils from its start).
#   hold   : the finish pose is held until this time; rec: the carry is reached at this time (the clip's remaining time rests in the carry).
#   p_in, p_out: the acceleration laws (see warp()).  apex: authored changes at the coil's apex (degrees added to the pelvis yaw, decaying to nothing at the hit).
SPECS = {
    "hew1": dict(file="spear_hew1_p2.json", name="spear_hew1", length=2.667, hit=0.604, antic=0.19, follow=0.29, start="carry", coil=0.52, hold=1.15, rec=2.10, grip_slide=0.22, hands_up=True,
                 p_in=1.9, p_out=1.4, apex_yaw=-8.0, ref="hew1", next=0.933),
    "hew2": dict(file="spear_hew2_p2.json", name="spear_hew2", length=2.333, hit=0.567, antic=0.18, follow=0.28, start="chain", start_nudge=(0.06, 0.0, 0.08), coil=0.24, hold=1.15, rec=1.95, grip_slide=0.0,
                 p_in=1.9, p_out=1.0, apex_yaw=0.0, ref="hew2", next=0.967),
    "hew3": dict(file="spear_hew3_p2.json", name="spear_hew3", length=2.333, hit=0.700, antic=0.20, follow=0.18, start="chain", coil=0.28, hold=1.25, rec=2.05, grip_slide=0.0,
                 p_in=1.9, p_out=1.4, apex_yaw=0.0, ref="hew3", next=None),
    "stun": dict(file="spear_stun_p2.json", name="spear_stun", length=1.667, hit=0.433, antic=0.14, follow=0.12, start="carry", coil=0.36, hold=0.72, rec=1.50, grip_slide=0.16,
                 p_in=1.6, p_out=1.4, apex_yaw=0.0, ref="stun", next=0.533),
    "stab": dict(file="spear_stab_p2.json", name="spear_stab", length=3.0, hit=1.1, antic=0.20, follow=0.20, start="chain", coil=0.34, hold=1.80, rec=3.00, grip_slide=0.0,
                 p_in=1.6, p_out=1.4, apex_yaw=0.0, ref="stab", next=None),
}

CONTROLS = ["pelvis_yaw", "pelvis_pitch", "pelvis_roll", "move_f", "move_r", "move_u", "spine_yaw", "spine_pitch", "spine_roll", "head_yaw", "head_pitch",
            "clav_yaw", "clav_pitch", "clav_roll", "lfoot_f", "lfoot_r", "lfoot_u", "rfoot_f", "rfoot_r", "rfoot_u",
            "spa", "ik_blend", "grip_l", "grip_pt", "rf", "rr", "ru", "sh_el", "sh_az", "spacing"]


def carry_state2(rig):
    """The carry's controls with the hips squared: the pelvis yaw SQUARE, the right palm's place in the chest frame, the shaft's direction in it, and the four carry directions."""
    rig.reset()
    pel, spa = rig.index["RIG_Pelvis"], rig.index["RIG_SpineA"]
    pivot = rig.gpos[pel].copy()
    q = A.turn_quat(rig, SQUARE, 0, 0)
    rig.move_bone(pel, q, Vector((0, 0, 0)), pivot)
    rig.move_bone(spa, q, Vector((0, 0, 0)), pivot)
    dirs = {}
    for side in ("R", "L"):
        (ue, ua), (fe, fa) = CARRY_AIMS[side]
        dirs[side] = (rig.aim_to_dir(ue, ua - A.IDLE_HIPS_TURN), rig.aim_to_dir(fe, fa - A.IDLE_HIPS_TURN))
    for side in ("R", "L"):
        rig.aim(f"RIG_{side}_UpperArm", f"RIG_{side}_Forearm", dirs[side][0])
        rig.aim(f"RIG_{side}_Forearm", f"RIG_{side}_Hand", dirs[side][1])
    sd = rig.index["RIG_SpineD"]
    chest = (rig.grot[sd] @ rig.stance_g[sd][1].inverted()).normalized()
    cf, cr, cu = chest @ rig.front, chest @ rig.right, chest @ rig.up
    mid = (rig.origin("RIG_R_UpperArm") + rig.origin("RIG_L_UpperArm")) * 0.5
    d = rig.origin(A.R_SOCKET) - mid
    sh = rig.aim_to_dir(CARRY_EL, CARRY_AZ - A.IDLE_HIPS_TURN)
    carry = dict(rf=d.dot(cf), rr=d.dot(cr), ru=d.dot(cu), sh_el=math.asin(max(-1, min(1, sh.dot(cu)))) / DEG, sh_az=math.atan2(sh.dot(cr), sh.dot(cf)) / DEG)
    rig.reset()
    return carry, dirs


def carry_controls(carry):
    c = {k: 0.0 for k in CONTROLS}
    c.update(carry)
    c.update(pelvis_yaw=SQUARE, move_u=CARRY_DROP, spa=1.0, spine_yaw=END_SPINE_YAW, head_yaw=-END_SPINE_YAW, ik_blend=0.0, grip_l=0.0, grip_pt=CARRY_GRIP, spacing=0.5)
    return c


class Ref:
    """A previous stroke's channels sampled at 90 Hz (private dump). controls(t) lays them with the overlay's arithmetic and reads the controls back, the palms and the shaft in the chest frame."""

    def __init__(self, rig, path):
        self.rig = rig
        self.d = json.load(open(path, encoding="utf-8"))
        self.times = self.d["times"]

    def index(self, t):
        return min(max(int(round(t * 90.0)), 0), len(self.times) - 1)

    def controls(self, t):
        rig, ref = self.rig, self.d
        i = self.index(t)
        g = lambda key, dflt=(0, 0, 0, 0): ref[key][i] if key in ref else dflt
        rig.reset()
        py, pp, pr_ = g("RIG_Pelvis|Turn|")[0:3]
        mf, mr, mu = g("RIG_Pelvis|Move|")[0:3]
        pel = rig.index["RIG_Pelvis"]
        q = A.turn_quat(rig, py, pp, pr_)
        sh = rig.front * mf + rig.right * mr + rig.up * mu
        pivot = rig.gpos[pel].copy()
        rig.move_bone(pel, q, sh, pivot)
        rig.move_bone(rig.index["RIG_SpineA"], q, sh, pivot)
        for bone in ("RIG_SpineA", "RIG_SpineD", "RIG_Head", "RIG_L_Clavicle", "RIG_R_Clavicle"):
            y, p, r = g(f"{bone}|Turn|")[0:3]
            rig.move_bone(rig.index[bone], A.turn_quat(rig, y, p, r), Vector((0, 0, 0)), rig.gpos[rig.index[bone]].copy())
        for side in ("R", "L"):
            for bone, child in ((f"RIG_{side}_UpperArm", f"RIG_{side}_Forearm"), (f"RIG_{side}_Forearm", f"RIG_{side}_Hand")):
                az, el = g(f"{bone}|Aim|{child}")[0:2]
                rig.aim(bone, child, rig.aim_to_dir(el, az))
        sd = rig.index["RIG_SpineD"]
        chest = (rig.grot[sd] @ rig.stance_g[sd][1].inverted()).normalized()
        cf, cr, cu = chest @ rig.front, chest @ rig.right, chest @ rig.up
        mid = (rig.origin("RIG_R_UpperArm") + rig.origin("RIG_L_UpperArm")) * 0.5
        rs, ls = rig.origin(A.R_SOCKET), rig.origin(A.L_SOCKET)
        d = rs - mid
        axis = ls - rs
        spacing = axis.length
        axis = axis.normalized()
        c = {k: 0.0 for k in CONTROLS}
        c.update(pelvis_yaw=py, pelvis_pitch=pp, pelvis_roll=pr_, move_f=mf, move_r=mr, move_u=mu)
        y, p, r = g("RIG_SpineD|Turn|")[0:3]
        c.update(spine_yaw=y, spine_pitch=p, spine_roll=r)
        y, p, _ = g("RIG_Head|Turn|")[0:3]
        c.update(head_yaw=y, head_pitch=p)
        y, p, r = g("RIG_R_Clavicle|Turn|")[0:3]
        c.update(clav_yaw=y, clav_pitch=p, clav_roll=r)
        for side in ("l", "r"):
            f, r_, u = g(f"RIG_{side.upper()}_Foot|Foot|")[0:3]
            c.update({f"{side}foot_f": f, f"{side}foot_r": r_, f"{side}foot_u": u})
        c.update(spa=1.0, ik_blend=1.0, grip_l=ref["grip"][i], grip_pt=ref["grip_point"][i], rf=d.dot(cf), rr=d.dot(cr), ru=d.dot(cu),
                 sh_el=math.asin(max(-1, min(1, axis.dot(cu)))) / DEG, sh_az=math.atan2(axis.dot(cr), axis.dot(cf)) / DEG, spacing=spacing)
        rig.reset()
        return c


def warp(spec, ref, a_new, h, f_new, a_old, h_old, f_old):
    """New stroke time -> the previous clip's time. The same poses in the same order, but the stroke's PROGRESS (the hips' turn, or their shift for the stab) grows as a power of the time
    to its peak speed at the hit (p_in), so the hips and the arms start slowly out of the coil and arrive fast; the follow-through decelerates (p_out). The previous clips snapped early
    (their hips had turned most of the way well before the hit); progress is measured on the hips' own travel, not on the clock, so the blow arrives at full speed at the hit."""
    key, k = ("RIG_Pelvis|Turn|", 0) if abs(ref.d["RIG_Pelvis|Turn|"][ref.index(h_old)][0] - ref.d["RIG_Pelvis|Turn|"][ref.index(a_old)][0]) >= 15.0 else ("RIG_Pelvis|Move|", 0)
    i0, i1 = ref.index(a_old), ref.index(h_old)
    vals = [ref.d[key][i][k] for i in range(i0, i1 + 1)]
    lo, hi = vals[0], vals[-1]
    sign = 1.0 if hi >= lo else -1.0
    prog, cur = [], 0.0
    for v in vals:                       # monotone progress 0..1 along the hips' travel (a dip in the old keys never runs the stroke backwards)
        cur = max(cur, (v - lo) / (hi - lo) if hi != lo else 0.0)
        prog.append(min(cur, 1.0))
    def old_time_for(target):
        for j in range(1, len(prog)):
            if prog[j] >= target:
                span = max(prog[j] - prog[j - 1], 1e-9)
                return ref.times[i0 + j - 1] + (ref.times[i0 + j] - ref.times[i0 + j - 1]) * (target - prog[j - 1]) / span
        return h_old
    def f(t):
        if t <= h:
            u = min(max((t - a_new) / max(h - a_new, 1e-6), 0.0), 1.0)
            return min(max(old_time_for(u ** spec["p_in"]), a_old), h_old)
        u = min(max((t - h) / max(f_new - h, 1e-6), 0.0), 1.0)
        return h_old + (f_old - h_old) * (1.0 - (1.0 - u) ** spec["p_out"])
    return f


def build_rows(rig, spec, ref, carry):
    h = spec["hit"] / TEMPO
    dur = spec["length"] / TEMPO
    a = h - spec["antic"]                      # the apex (the previous clips' own: the clips' windows)
    f_end = h + spec["follow"]
    # the window the new stroke takes from the previous clip: its apex a_old (= a), its hit and its follow-through end are the previous clip's timing
    a_old, h_old, f_old = h - spec["antic"], h, h + spec["follow"]
    w = warp(spec, ref, a, h, f_end, a_old, h_old, f_old)
    cc = carry_controls(carry)
    rows = []

    apex = ref.controls(a_old)
    apex["pelvis_yaw"] += spec["apex_yaw"]
    start = cc if spec["start"] == "carry" else ref.controls(0.0)
    if spec["start"] == "chain":
        # a chained stroke starts with the left hand ON the shaft at the melee grip (the stroke before leaves it there); a stroke begun from the carry (Musou picks any hew) reaches that grip in the 0.14 s hand-over
        start.update(ik_blend=1.0, grip_l=1.0, grip_pt=apex["grip_pt"])
        n = spec.get("start_nudge", (0.0, 0.0, 0.0))   # the hands a little higher and further ahead at the start of a chained stroke, so the shaft stays clear of the hips through a hand-over from the carry
        start["rf"] += n[0]; start["rr"] += n[1]; start["ru"] += n[2]
        if "start_yaw" in spec:   # the previous strokes leave the hips at very different turns (hew 1 past 90, hew 2 near 40): hew 3 starts between them so either hand-over stays clear
            start["pelvis_yaw"] = spec["start_yaw"]
    coil_t = max(a - spec["coil"], 0.0)
    gs = spec["grip_slide"]
    rows.append((0.0, "S", dict(start)))
    if spec["start"] == "carry":
        # the shaft slides to the melee grip and the left hand takes it while the hips begin to coil
        gs = spec["grip_slide"]
        # the shaft slides through the right hand, which stays at the hip, while the left hand reaches across and takes it; the hands come up for the coil only after that
        keep = dict(rf=carry["rf"] + 0.04, rr=carry["rr"] + 0.06, ru=carry["ru"] + 0.04, sh_el=carry["sh_el"], sh_az=carry["sh_az"])
        rows.append((gs * 0.5, "S", dict(ik_blend=1.0, grip_l=0.1, grip_pt=0.5 * (CARRY_GRIP + apex["grip_pt"]), spacing=0.25, **keep)))
        hold_body = {k: cc[k] for k in ("pelvis_yaw", "pelvis_pitch", "pelvis_roll", "move_f", "move_r", "move_u", "spine_yaw", "spine_pitch", "spine_roll", "head_yaw", "head_pitch", "clav_yaw", "clav_pitch", "clav_roll",
                                         "lfoot_f", "lfoot_r", "lfoot_u", "rfoot_f", "rfoot_r", "rfoot_u", "spa")}
        rows.append((gs, "S", dict(ik_blend=1.0, grip_l=1.0, grip_pt=apex["grip_pt"], spacing=0.30, **keep, **hold_body)))   # the coil of the hips begins once the hands are on the shaft
    else:
        rows.append((coil_t, "S", dict(start)))
        if spec.get("start_grip", 1.0) < 1.0:   # the left hand is back on the shaft within 0.2 s
            rows.append((min(0.2, coil_t - 0.01), "S", dict(grip_l=1.0)))
    # the hands come up in front of the chest as the hips begin to coil, so the shaft's butt end sweeps clear of the hip and thigh (the pilot's coil poses: right palm at the chest, the blade out to the right)
    if spec.get("hands_up"):
        t_up = gs + 0.45 * (a - gs)
        rows.append((t_up, "S", dict(rf=0.46, rr=0.36, ru=-0.28, sh_el=48.0, sh_az=70.0, spacing=0.30)))
    rows.append((a, "L", apex))
    # the stroke: sampled every 1/90 s of the new time from the previous clip's poses, with the hips' extra coil decaying to nothing at the hit
    n = int(round((f_end - a) * 90.0))
    for k in range(1, n + 1):
        t = a + (f_end - a) * k / n
        c = ref.controls(w(t))
        if t < h:
            c["pelvis_yaw"] += spec["apex_yaw"] * (1.0 - (t - a) / max(h - a, 1e-6))
        rows.append((t, "L", c))
    # a single odd sample in the measured palm geometry (the previous clips' fastest frames) is a glitch, not a pose: a 3-tap median over the arms' controls through the window
    win = [i for i, r in enumerate(rows) if r[0] > a + 1e-9 and r[1] == "L"]
    for key in ("rf", "rr", "ru", "sh_el", "sh_az", "spacing"):
        vals = [rows[i][2][key] for i in win]
        for j in range(1, len(win) - 1):
            lo, mid, hi = sorted(vals[j - 1:j + 2])
            rows[win[j]][2][key] = mid
    # make the hit an exact key
    hit_row = ref.controls(h_old)
    rows = [r for r in rows if abs(r[0] - h) > 0.004] + [(h, "L", hit_row)]
    finish = ref.controls(f_old)
    rows.append((spec["hold"], "S", dict(finish)))
    # recovery: a quick return to the carry (the feet step back with a small lift in between, the grip slides back, the left hand lets go and the arms come to the carry)
    rec = spec["rec"]
    mid = spec["hold"] + (rec - spec["hold"]) * 0.5
    # the feet come back first (a quick step: half way with a small lift, then planted on the stance's spots) so the carried shaft's butt is clear of the legs before the hips and hands settle
    foot = lambda f, lift_l, lift_r: dict(lfoot_f=finish["lfoot_f"] * f, lfoot_r=finish["lfoot_r"] * f, lfoot_u=lift_l, rfoot_f=finish["rfoot_f"] * f, rfoot_r=finish["rfoot_r"] * f, rfoot_u=lift_r)
    moved_r = abs(finish["rfoot_f"]) + abs(finish["rfoot_r"]) > 0.05
    lift_r = 0.07 if moved_r else 0.0
    rows.append((spec["hold"] + 0.12, "S", foot(0.5, 0.08, lift_r)))
    rows.append((spec["hold"] + 0.30, "S", foot(0.0, 0.08, lift_r)))      # arrives while still lifted (a planted foot never travels: FootPin)
    settle = foot(0.0, 0.0, 0.0)
    settle.update({k: cc[k] for k in ("move_f", "move_r", "move_u", "pelvis_pitch", "pelvis_roll")})   # the hips' shift and lean come back with the feet (the legs reach no further than they can); the turn follows
    rows.append((spec["hold"] + 0.42, "S", settle))
    back = {k: v for k, v in finish.items() if "foot" not in k}
    for k in ("pelvis_yaw", "spine_yaw", "spine_pitch", "spine_roll", "head_yaw", "head_pitch", "clav_yaw", "clav_pitch", "clav_roll"):
        back[k] = (finish[k] + cc[k]) * 0.5
    for k in ("pelvis_pitch", "pelvis_roll", "move_f", "move_r", "move_u"):
        back.pop(k, None)
    rows.append((mid, "S", back))
    end = dict(cc)
    end.update(spine_yaw=END_SPINE_YAW, head_yaw=-END_SPINE_YAW)
    rows.append((rec - 0.50, "S", dict(ik_blend=1.0, grip_l=1.0, grip_pt=finish["grip_pt"])))
    rows.append((rec - 0.30, "S", dict(ik_blend=1.0, grip_l=0.0, grip_pt=CARRY_GRIP)))
    # the right hand comes down outside the body (out to the right and low) with the shaft already leaning as in the carry, and only then to the hip
    rows.append((rec - 0.15, "S", dict(ik_blend=1.0, grip_l=0.0, grip_pt=CARRY_GRIP, rf=0.30, rr=0.55, ru=-0.45, sh_el=carry["sh_el"], sh_az=carry["sh_az"], spacing=0.5)))
    rows.append((rec, "S", end))
    rows.append((dur, "S", dict(end)))
    return sorted(rows, key=lambda r: r[0]), (a, h, dur)


def foot_rows_from(rows):
    left = [(t, m, (c.get("lfoot_f", None), c.get("lfoot_r", None), c.get("lfoot_u", None))) for t, m, c in rows]
    return left


def build_controls(rows, carry):
    """The pilot's build_controls over these rows (feet from the rows too)."""
    scene = bpy.context.scene
    scene.render.fps = int(FPS)
    ctrl = bpy.data.objects.new("HEW_CTRL", None)
    scene.collection.objects.link(ctrl)
    series = {}
    for t, mode, values in rows:
        for name, v in values.items():
            series.setdefault(name, []).append((t, float(v), mode))
    for name in CONTROLS:
        if name not in series:
            raise SystemExit(f"control {name} has no keys")
    for name, keys in series.items():
        keys.sort(key=lambda k: k[0])
        if name == "sh_az":   # an azimuth that wraps at 180 degrees between two keys must not be interpolated through zero: unwrap it
            fixed, prev = [], None
            for t, v, mode in keys:
                if prev is not None:
                    while v - prev > 180.0:
                        v -= 360.0
                    while v - prev < -180.0:
                        v += 360.0
                fixed.append((t, v, mode))
                prev = v
            keys[:] = fixed
        ctrl[name] = keys[0][1]
        for t, v, mode in keys:
            ctrl[name] = v
            ctrl.keyframe_insert(data_path=f'["{name}"]', frame=t * FPS)
        fc = ctrl.animation_data.action.fcurves.find(f'["{name}"]')
        for kp, (t, v, mode) in zip(fc.keyframe_points, keys):
            kp.handle_left_type = kp.handle_right_type = "AUTO_CLAMPED"
            kp.interpolation = {"S": "BEZIER", "L": "LINEAR", "H": "CONSTANT", "I": "QUAD", "O": "CUBIC"}[mode]
            if mode == "I":
                kp.easing = "EASE_IN"
            if mode == "O":
                kp.easing = "EASE_OUT"
    return ctrl, {name: ctrl.animation_data.action.fcurves.find(f'["{name}"]') for name in series}


def export_clip(rig, spec, rows, landmarks, carry, carry_dirs, dump, out_path, check_path, extras):
    ctrl, curves = build_controls(rows, carry)
    a, h, dur = landmarks
    n = int(math.ceil(dur * FPS))
    grid = [min(i / FPS, dur) for i in range(n + 1)]
    marks = [round(a, 6), round(h, 6)]
    times = sorted(set(round(t, 6) for t in grid + marks))
    times = [t for t in times if not any(0 < abs(t - m) < 0.004 and t not in marks for m in marks)]
    samples = []
    worst_arm = worst_leg = 0.0
    CHECK = ["RIG_Pelvis", "RIG_SpineD", "RIG_Head", A.R_SOCKET, A.L_SOCKET, "RIG_R_Forearm", "RIG_L_Forearm", "RIG_R_Hand", "RIG_L_Hand", "RIG_L_Calf", "RIG_R_Calf", "RIG_L_Foot", "RIG_R_Foot"]
    for t in times:
        s = A.pose_at(rig, curves, t, carry_dirs)
        s["check"] = {k: tuple(rig.origin(k)) for k in CHECK}
        s["t"] = t
        samples.append(s)
        for side, (err, clamped) in s["_arm_err"].items():
            if side == "R" or s["controls"]["grip_l"] > 0.99:
                worst_arm = max(worst_arm, err)
        worst_leg = max(worst_leg, s["_leg_over"])
        if "--verbose" in sys.argv:
            for side, (err, clamped) in s["_arm_err"].items():
                if err > 0.012 and (side == "R" or s["controls"]["grip_l"] > 0.99):
                    print(f"  t {t:.3f} {side} palm socket off by {err:.3f} m{' (out of reach)' if clamped else ''}")
    print(f"{spec['name']}: {len(samples)} samples; worst palm-socket error {worst_arm:.4f} m; legs out of reach by up to {worst_leg:.4f} m")

    def key_list(fn):
        return [[round(s["t"], 5)] + [round(x, 4) for x in fn(s)] for s in samples]

    ch = []

    def turn(bone, prefix):
        ch.append({"bone": bone, "kind": "Turn", "keys": key_list(lambda s: (s["controls"][f"{prefix}_yaw"], s["controls"][f"{prefix}_pitch"], s["controls"].get(f"{prefix}_roll", 0.0)) + (1.0,))})

    turn("RIG_Pelvis", "pelvis")
    ch.append({"bone": "RIG_Pelvis", "kind": "Move", "keys": key_list(lambda s: (s["controls"]["move_f"], s["controls"]["move_r"], s["controls"]["move_u"], 1.0))})
    for side in ("L", "R"):
        ch.append({"bone": f"RIG_{side}_Foot", "kind": "Foot", "keys": key_list(lambda s, p=side.lower(): (s["controls"][f"{p}foot_f"], s["controls"][f"{p}foot_r"], s["controls"][f"{p}foot_u"], 1.0))})
    turn("RIG_SpineA", "spine")
    turn("RIG_SpineD", "spine")
    ch.append({"bone": "RIG_Head", "kind": "Turn", "keys": key_list(lambda s: (s["controls"]["head_yaw"], s["controls"]["head_pitch"], 0.0, 1.0))})
    turn("RIG_R_Clavicle", "clav")
    turn("RIG_L_Clavicle", "clav")
    for side in ("R", "L"):
        ch.append({"bone": f"RIG_{side}_UpperArm", "kind": "Aim", "toward": f"RIG_{side}_Forearm", "keys": key_list(lambda s, k=f"{side}_upper": (s[k][1], s[k][0], 0.0, 1.0))})
        ch.append({"bone": f"RIG_{side}_Forearm", "kind": "Aim", "toward": f"RIG_{side}_Hand", "keys": key_list(lambda s, k=f"{side}_fore": (s[k][1], s[k][0], 0.0, 1.0))})
    data = {
        "format": "sm-spear-authored-clip/1",
        "name": spec["name"],
        "authoring": {"tool": f"Blender {bpy.app.version_string}", "script": "tools/blender/spear_pass2_author.py", "pass": 2,
                      "method": "hand-keyed technique rows over the pilot's pipeline (F-curves on control properties, FK in the runtime overlay's arithmetic, two-bone arm and leg solves); the palms' and the shaft's geometry through the stroke is re-timed from the previous clips (named in the docs); no motion capture, no video-to-motion"},
        "units": {"length": "metres", "angle": "degrees", "time": "seconds"},
        "axes": "skeleton space, Z up. Stance frame: right = left thigh to right thigh (flat), front = up x right. Turn: yaw positive toward the character's left (about up), pitch about the stance right (positive tips a standing segment back), roll about the stance front (positive tips it to the right); composed yaw, pitch, roll about the bone's own origin. Aim: azimuth from the stance front toward its right, elevation above the horizontal (absolute). Move/Foot: forward, right, up metres in the stance frame.",
        "rest_pose": {"clip": dump["stance_clip"], "time": dump["stance_time"], "note": "the base stance the keys lie over (hips 71 degrees right of the facing); the carry squares the hips with a pelvis yaw of 71 degrees (WeaponPoseClip.SquareYaw) so the clips start and end on that; a bone with no track at the skeleton's reference pose"},
        "sample_rate_hz": FPS,
        "timing": {"duration": round(dur, 5), "hit_time": round(h, 5), "wind_up_apex": round(a, 5), "finish_hold_end": spec["hold"], "carry_reached": spec["rec"],
                   "source": "the hammer's strike at the spear's tempo 0.85 (docs/claw-spear-animations.md section 2); length, hit and combo opening are the strike's"},
        "technique": extras.get("technique", ""),
        "interpolation": "linear between samples (PoseEase.Linear)",
        "bone_map": {"pelvis": "RIG_Pelvis", "torso_carried_with_pelvis": "RIG_SpineA", "spine_top": "RIG_SpineD", "head": "RIG_Head", "clavicles": ["RIG_L_Clavicle", "RIG_R_Clavicle"],
                     "arms": {"R": ["RIG_R_UpperArm", "RIG_R_Forearm", "RIG_R_Hand"], "L": ["RIG_L_UpperArm", "RIG_L_Forearm", "RIG_L_Hand"]}, "grip_sockets": [A.R_SOCKET, A.L_SOCKET],
                     "legs": {"L": ["RIG_L_Thigh", "RIG_L_Calf", "RIG_L_Foot"], "R": ["RIG_R_Thigh", "RIG_R_Calf", "RIG_R_Foot"]}},
        "channels": ch,
        "grip": [[round(s["t"], 5), round(s["controls"]["grip_l"], 4)] for s in samples],
        "grip_point": [[round(s["t"], 5), round(s["controls"]["grip_pt"], 4)] for s in samples],
    }
    check = {"rate_hz": 60, "note": "joint origins in skeleton space as Blender solved them; PRIVATE (the install's proportions)",
             "joints": {k: [[round(x, 4) for x in s["check"][k]] for i, s in enumerate(samples)] for k in CHECK},
             "times": [round(s["t"], 5) for i, s in enumerate(samples)]}
    if check_path:
        os.makedirs(os.path.dirname(os.path.abspath(check_path)), exist_ok=True)
        with open(check_path, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(check, fh)
    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
    with open(out_path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(data, fh, indent=1, sort_keys=False)
        fh.write("\n")
    print("exported", out_path)
    return curves, samples


TECHNIQUES = {
    "hew1": "Light hew 1: the descending diagonal cut from the right shoulder through the target to low left, struck with the hips' turn and a passing step (the left foot steps to the target as the cut lands). Source technique: the shoulder-height diagonal cut of Joachim Meyer's halberd and staff teaching (Fechtbuch 1570), the polearm counterpart of his Zornhau, delivered with the hips as in Meyer's and Fiore's pollaxe plays.",
    "hew2": "Light hew 2: the rising cut from low left up across to high right, the right foot passing through as the hips uncoil. Source technique: the rising cut from below that follows a downward cut in Meyer's halberd plays (the Nachreisen / Aufsteigen of his longsword and staff), driven by the passing step.",
    "hew3": "Light hew 3: the flat horizontal cut at waist height from behind the right side round to far left, a pivoting step turning the whole body through it. Source technique: the horizontal cut of Meyer's Zwerchhau family as it is applied to the halberd, struck from a step and a pivot.",
    "stun": "F snap cut: a short fast flat cut at head height, right to left, from a half step, finished short. Source technique: the quick snapping cut that Meyer teaches as the Schnappen (the blade thrown on at the opening and snapped back), compact because the hands do little more than flick the shaft round while the hips turn.",
    "stab": "F,F massive stab: the spear drawn back along the right hip, both hands at the butt, then a long lunging step and one straight thrust driven by the hips and the rear hand. Source technique: the thrust from the long point (Meyer's Langort, the straight stab of his staff and halberd plays and the point thrust of Le Jeu de la Hache's pollaxe) with a lunging step.",
}


def main():
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    opt = {args[i]: args[i + 1] for i in range(len(args) - 1) if args[i].startswith("--") and not args[i + 1].startswith("--")}
    dump = json.load(open(opt["--rig"], encoding="utf-8"))
    rig = A.Rig(dump)
    carry, carry_dirs = carry_state2(rig)
    print("stance frame:", tuple(round(x, 3) for x in rig.front), tuple(round(x, 3) for x in rig.right), "carry controls", {k: round(v, 3) for k, v in carry.items()})
    # the pilot's recovery tables belong to the old carry: this pass has none (the arms come to the carry by the IK blend)
    A.RECOVERY_FROM = 1e9
    only = opt["--only"].split(",") if "--only" in opt else list(SPECS)
    for key in only:
        for o in list(bpy.data.objects):
            bpy.data.objects.remove(o, do_unlink=True)
        spec = SPECS[key]
        ref = Ref(rig, os.path.join(opt["--ref-dir"], f"orig_{spec['ref']}.json"))
        rows, landmarks = build_rows(rig, spec, ref, carry)
        out = os.path.join(opt["--out-dir"], spec["file"])
        chk = os.path.join(opt["--check-dir"], spec["file"].replace(".json", ".check.json")) if "--check-dir" in opt else None
        curves, samples = export_clip(rig, spec, rows, landmarks, carry, carry_dirs, dump, out, chk, {"technique": TECHNIQUES[key]})
        if "--blend" in opt:
            bpy.ops.wm.save_as_mainfile(filepath=opt["--blend"].replace(".blend", f"_{key}.blend"))
        if "--renders" in opt:
            import spear_hew_render
            spear_hew_render.CARRY = (CARRY_EL, CARRY_AZ)
            spear_hew_render.render(rig, curves, carry_dirs, os.path.join(opt["--renders"], key), samples, A.pose_at,
                                    times=[float(x) for x in opt["--times"].split(",")] if "--times" in opt else [0.0, landmarks[0], landmarks[1] - 0.05, landmarks[1], landmarks[1] + 0.15, spec["hold"], spec["rec"] - 0.3, spec["rec"]])


if __name__ == "__main__":
    main()
