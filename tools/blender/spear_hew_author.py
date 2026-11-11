"""Authors the power spear's light hew 1 in Blender (headless) and exports it as original WeaponPoseClip keyframe data.

Authored by us (GPL-3.0); no motion capture and no video-to-motion: every pose below is a hand-keyed Blender F-curve (controls on an empty),
solved onto the marine's own proportions by Python (FK in the runtime overlay's arithmetic, two-bone arm and leg solves).
The marine's skeleton comes from the owner's install through `smtool spear-rig-dump` into a PRIVATE folder (never the repository); this
script reads it, builds a temporary armature plus proxy meshes in memory, keys the controls, samples them at 60 Hz and writes:

  --export <file.json>   the committed, original authored data (explicit units, axes, rest pose, bone map, sample rate and timing; no install bytes)
  --check  <file.json>   (private) Blender's solved joint positions at 20 Hz, for the in-game comparison test (they carry the install's proportions, so they are not committed)
  --blend  <file.blend>  (private) the authoring scene: the controls' F-curves, editable in Blender
  --renders <dir>        (private) Workbench stills of the key poses from side / front / top, and a data dump

Run (never with a window):
  blender.exe -b --factory-startup --python tools/blender/spear_hew_author.py -- --rig <spear-authoring>\\rig.json --export tools/blender/spear/hew1_authored.json [--blend f.blend] [--renders dir]

Conventions (the runtime's, docs/claw-spear-animations.md section 1): skeleton space, Z up, metres; the stance frame is read from the thighs of the base stance (the
hammer's idle: its right from the left thigh to the right one, flat; its front square to that); a Turn key is yaw (positive toward the character's left, about up), pitch
(about the stance's right, positive tips a standing segment back), roll (about its front, positive tips it to the right), degrees, composed yaw then pitch then roll about the
bone's own origin in that frame; an Aim key is where a segment points: elevation above the horizontal, azimuth from the front toward the right (the stance's), absolute; a Move
or Foot key is metres forward, right, up in the stance frame (a foot's is an offset from where the stance plants it). The pelvis carries RIG_SpineA with it (they are siblings
under the root). The simulation owns translation: nothing here moves the root.
"""
import json
import math
import os
import sys

import bpy
from mathutils import Matrix, Quaternion, Vector

FPS = 60.0
DEG = math.pi / 180.0
UP = Vector((0.0, 0.0, 1.0))

# ---- timing (the design's: the hammer's X1 at the spear's tempo 0.85; the clip's length and hit are the simulation's contract) ----
TEMPO = 0.85
DURATION = 2.667 / TEMPO
HIT = 0.604 / TEMPO
CLIP_NAME = "spear_hew1"

# ---- the carry the clip starts from and returns to (ClawSpearClips: aims absolute in the facing frame; the stance frame is IDLE_HIPS_TURN degrees to its right) ----
IDLE_HIPS_TURN = 71.0
CARRY_AIMS = {"R": ((-35.0, 45.0), (0.0, 40.0)), "L": ((-70.0, 51.0), (-50.0, 46.0))}   # upper arm, forearm: (elevation, azimuth in the facing frame)
CARRY_GRIP = 0.95
HEW_GRIP = 0.3
BLADE_FROM_BUTT = 2.85

R_SOCKET, L_SOCKET = "RIG_Prop_RHand", "RIG_Prop_LHand"


# The swing's arm geometry, 90 Hz from 0.50 s to 0.80 s: (t, rf, rr, ru, sh_el, sh_az, spacing), the palms and the shaft in the chest frame as the previous pass's cleared sweep had them
# (measured with --reference from its keys), kept so the blade passes clear of the body through the fast part of the cut. The hips, coil, timing, acceleration and hold around it are this pass's.
SWING_ARMS = [
    (0.5000, 0.440, 0.176, -0.168, 32.6, 93.4, 0.300),
    (0.5111, 0.442, 0.176, -0.159, 33.3, 94.0, 0.305),
    (0.5222, 0.453, 0.176, -0.108, 31.2, 100.7, 0.303),
    (0.5333, 0.452, 0.168, -0.099, 29.3, 99.9, 0.303),
    (0.5444, 0.450, 0.161, -0.089, 27.8, 99.4, 0.304),
    (0.5556, 0.449, 0.154, -0.084, 26.5, 98.6, 0.304),
    (0.5667, 0.450, 0.146, -0.100, 23.8, 95.8, 0.303),
    (0.5778, 0.449, 0.138, -0.116, 21.6, 92.9, 0.302),
    (0.5889, 0.444, 0.123, -0.132, 18.3, 88.9, 0.304),
    (0.6000, 0.428, 0.073, -0.151, 16.7, 83.8, 0.301),
    (0.6111, 0.506, 0.018, 0.062, 11.5, 79.4, 0.305),
    (0.6222, 0.498, -0.017, 0.187, 10.8, 69.8, 0.272),
    (0.6333, 0.488, -0.033, 0.233, 9.7, 61.7, 0.254),
    (0.6444, 0.491, 0.122, -0.003, 14.3, 57.2, 0.205),
    (0.6556, 0.472, 0.195, -0.040, 10.6, 40.2, 0.170),
    (0.6667, 0.503, 0.225, 0.055, 10.1, 28.8, 0.117),
    (0.6778, 0.532, 0.255, 0.096, 5.7, 7.3, 0.072),
    (0.6889, 0.507, 0.339, 0.007, 12.6, -29.7, 0.101),
    (0.7000, 0.400, 0.431, -0.150, 11.5, -32.7, 0.200),
    (0.7111, 0.260, 0.478, -0.281, 8.0, -28.6, 0.303),
    (0.7222, 0.330, 0.433, -0.269, 5.7, -28.8, 0.283),
    (0.7333, 0.386, 0.376, -0.253, 2.0, -26.6, 0.274),
    (0.7444, 0.424, 0.311, -0.235, -2.5, -22.9, 0.279),
    (0.7556, 0.443, 0.307, -0.229, -4.8, -26.2, 0.282),
    (0.7667, 0.461, 0.298, -0.222, -7.0, -28.8, 0.289),
    (0.7778, 0.477, 0.284, -0.215, -9.2, -30.8, 0.298),
    (0.7889, 0.473, 0.281, -0.221, -10.6, -33.0, 0.317),
    (0.8000, 0.470, 0.275, -0.227, -12.1, -35.7, 0.332),
]


# The approved recovery's arms (aims: (t, azimuth, elevation) in the stance frame, 30 Hz from 0.95 s to the end): from the finish on the arms are the previous pass's, whose hand-overs
# to the next swings and to the carry were cleared and approved ("the approved recovery"); this pass authors the strike, the finish pose and the brief hold, and hands over to it at 0.95-1.0 s.
RECOVERY_AIMS = {
    "R_upper": [(0.9500, -96.4, -41.4), (0.9833, -97.0, -42.0), (1.0167, -96.1, -41.4), (1.0500, -93.0, -41.7), (1.0833, -90.7, -41.7), (1.1167, -84.1, -41.6), (1.1500, -79.8, -42.0), (1.1833, -72.5, -42.0), (1.2167, -63.1, -40.7), (1.2500, -58.5, -40.9), (1.2833, -50.1, -43.0), (1.3167, -37.6, -46.2), (1.3500, -29.4, -42.8), (1.3833, -24.5, -40.0), (1.4167, -11.9, -41.3), (1.4500, 1.8, -42.9), (1.4833, 22.0, -40.2), (1.5167, 30.3, -31.9), (1.5500, 29.5, -20.6), (1.5833, 37.0, -22.3), (1.6167, 70.3, -46.5), (1.6500, 71.2, -45.2), (1.6833, 73.4, -41.9), (1.7167, 77.6, -35.4), (1.7500, 80.1, -31.6), (1.7833, 84.2, -25.3), (1.8167, 89.8, -16.7), (1.8500, 93.9, -10.4), (1.8833, 96.4, -6.6), (1.9167, 100.6, -0.1), (1.9500, 102.2, 2.3), (1.9833, 103.7, 4.5), (2.0167, 103.9, 5.0), (2.0500, 103.4, 4.8), (2.0833, 102.3, 4.5), (2.1167, 99.8, 3.7), (2.1500, 98.2, 3.2), (2.1833, 95.5, 2.4), (2.2167, 91.1, 1.0), (2.2500, 88.6, 0.3), (2.2833, 84.5, -1.0), (2.3167, 78.6, -2.8), (2.3500, 73.8, -4.3), (2.3833, 70.5, -5.3), (2.4167, 63.6, -7.4), (2.4500, 58.2, -9.1), (2.4833, 54.5, -10.2), (2.5167, 47.0, -12.5), (2.5500, 43.2, -13.7), (2.5833, 37.5, -15.5), (2.6167, 29.9, -17.8), (2.6500, 26.1, -19.0), (2.6833, 20.6, -20.7), (2.7167, 13.3, -22.9), (2.7500, 9.9, -24.0), (2.7833, 4.8, -25.5), (2.8167, -1.6, -27.5), (2.8500, -6.0, -28.8), (2.8833, -8.7, -29.7), (2.9167, -13.8, -31.2), (2.9500, -17.1, -32.3), (2.9833, -19.0, -32.9), (3.0167, -22.3, -33.8), (3.0500, -23.5, -34.2), (3.0833, -25.0, -34.7), (3.1167, -25.9, -35.0), (3.1376, -26.0, -35.0)],
    "R_fore": [(0.9500, -140.0, -34.0), (0.9833, -140.0, -34.0), (1.0167, -140.0, -34.0), (1.0500, -138.7, -33.3), (1.0833, -137.0, -33.0), (1.1167, -133.1, -33.0), (1.1500, -130.7, -33.0), (1.1833, -126.4, -33.0), (1.2167, -119.8, -34.3), (1.2500, -115.5, -34.7), (1.2833, -108.4, -34.6), (1.3167, -97.9, -33.4), (1.3500, -94.0, -36.6), (1.3833, -91.8, -39.0), (1.4167, -83.1, -38.4), (1.4500, -77.8, -37.1), (1.4833, -72.9, -40.1), (1.5167, -71.9, -47.1), (1.5500, -72.3, -50.9), (1.5833, -67.2, -48.5), (1.6167, -46.2, -32.1), (1.6500, -44.1, -32.3), (1.6833, -38.6, -32.8), (1.7167, -28.0, -33.8), (1.7500, -21.8, -34.4), (1.7833, -11.6, -35.3), (1.8167, 2.6, -36.7), (1.8500, 12.8, -37.6), (1.8833, 19.0, -38.2), (1.9167, 29.6, -39.2), (1.9500, 33.5, -39.6), (1.9833, 37.2, -39.9), (2.0167, 37.9, -40.0), (2.0500, 37.7, -39.8), (2.0833, 37.1, -39.5), (2.1167, 35.8, -38.7), (2.1500, 34.9, -38.2), (2.1833, 33.5, -37.4), (2.2167, 31.1, -36.0), (2.2500, 29.8, -35.3), (2.2833, 27.7, -34.0), (2.3167, 24.5, -32.2), (2.3500, 22.0, -30.7), (2.3833, 20.2, -29.7), (2.4167, 16.5, -27.6), (2.4500, 13.7, -25.9), (2.4833, 11.7, -24.8), (2.5167, 7.7, -22.5), (2.5500, 5.7, -21.3), (2.5833, 2.7, -19.5), (2.6167, -1.3, -17.2), (2.6500, -3.3, -16.0), (2.6833, -6.3, -14.3), (2.7167, -10.1, -12.1), (2.7500, -12.0, -11.0), (2.7833, -14.7, -9.5), (2.8167, -18.0, -7.5), (2.8500, -20.4, -6.2), (2.8833, -21.8, -5.3), (2.9167, -24.5, -3.8), (2.9500, -26.3, -2.7), (2.9833, -27.3, -2.1), (3.0167, -29.0, -1.2), (3.0500, -29.7, -0.8), (3.0833, -30.4, -0.3), (3.1167, -31.0, -0.0), (3.1376, -31.0, -0.0)],
    "L_upper": [(0.9500, -126.4, -30.0), (0.9833, -127.0, -30.0), (1.0167, -126.4, -30.0), (1.0500, -124.0, -29.3), (1.0833, -121.7, -29.0), (1.1167, -115.1, -28.4), (1.1500, -110.8, -28.0), (1.1833, -103.1, -28.0), (1.2167, -89.9, -27.4), (1.2500, -82.8, -26.7), (1.2833, -72.8, -26.0), (1.3167, -55.7, -25.4), (1.3500, -35.0, -32.8), (1.3833, -27.0, -32.2), (1.4167, -18.0, -22.0), (1.4500, -10.2, -21.4), (1.4833, -1.5, -20.3), (1.5167, 7.2, -20.4), (1.5500, 11.6, -16.7), (1.5833, 13.5, -16.0), (1.6167, 15.2, -19.3), (1.6500, 15.7, -20.3), (1.6833, 16.9, -22.8), (1.7167, 19.2, -27.5), (1.7500, 20.6, -30.3), (1.7833, 22.9, -34.8), (1.8167, 26.1, -41.2), (1.8500, 28.4, -45.7), (1.8833, 29.8, -48.5), (1.9167, 32.1, -53.2), (1.9500, 33.0, -55.0), (1.9833, 33.8, -56.7), (2.0167, 33.9, -57.0), (2.0500, 33.8, -57.1), (2.0833, 33.3, -57.2), (2.1167, 32.3, -57.4), (2.1500, 31.6, -57.6), (2.1833, 30.5, -57.9), (2.2167, 28.6, -58.3), (2.2500, 27.6, -58.5), (2.2833, 25.9, -58.9), (2.3167, 23.5, -59.5), (2.3500, 21.5, -60.0), (2.3833, 20.1, -60.3), (2.4167, 17.2, -61.0), (2.4500, 15.0, -61.6), (2.4833, 13.4, -62.0), (2.5167, 10.3, -62.7), (2.5500, 8.7, -63.1), (2.5833, 6.4, -63.7), (2.6167, 3.2, -64.4), (2.6500, 1.6, -64.8), (2.6833, -0.7, -65.3), (2.7167, -3.7, -66.1), (2.7500, -5.1, -66.4), (2.7833, -7.2, -66.9), (2.8167, -9.8, -67.6), (2.8500, -11.7, -68.0), (2.8833, -12.8, -68.3), (2.9167, -14.9, -68.8), (2.9500, -16.3, -69.1), (2.9833, -17.1, -69.3), (3.0167, -18.4, -69.6), (3.0500, -19.0, -69.8), (3.0833, -19.6, -69.9), (3.1167, -20.0, -70.0), (3.1376, -20.0, -70.0)],
    "L_fore": [(0.9500, -55.0, -18.4), (0.9833, -55.0, -19.0), (1.0167, -54.4, -18.4), (1.0500, -52.7, -18.0), (1.0833, -50.7, -18.0), (1.1167, -46.1, -18.0), (1.1500, -43.7, -17.7), (1.1833, -39.4, -16.7), (1.2167, -35.4, -17.3), (1.2500, -31.2, -17.4), (1.2833, -22.0, -16.3), (1.3167, -20.0, -16.4), (1.3500, -27.2, -8.8), (1.3833, -25.3, -8.2), (1.4167, -12.4, -13.7), (1.4500, -7.3, -13.3), (1.4833, 5.2, -14.3), (1.5167, 13.9, -13.1), (1.5500, 17.3, -9.4), (1.5833, 18.8, -8.8), (1.6167, 20.9, -10.7), (1.6500, 20.8, -9.9), (1.6833, 20.4, -7.9), (1.7167, 19.7, -4.1), (1.7500, 19.2, -1.8), (1.7833, 18.5, 1.9), (1.8167, 17.5, 7.1), (1.8500, 16.8, 10.8), (1.8833, 16.3, 13.1), (1.9167, 15.6, 16.9), (1.9500, 15.3, 18.4), (1.9833, 15.1, 19.7), (2.0167, 15.0, 19.9), (2.0500, 14.8, 19.7), (2.0833, 14.5, 19.1), (2.1167, 13.7, 17.7), (2.1500, 13.2, 16.9), (2.1833, 12.4, 15.4), (2.2167, 11.0, 13.0), (2.2500, 10.3, 11.7), (2.2833, 9.0, 9.5), (2.3167, 7.2, 6.3), (2.3500, 5.7, 3.8), (2.3833, 4.7, 2.0), (2.4167, 2.6, -1.8), (2.4500, 0.9, -4.7), (2.4833, -0.2, -6.7), (2.5167, -2.5, -10.7), (2.5500, -3.7, -12.8), (2.5833, -5.5, -15.8), (2.6167, -7.8, -19.9), (2.6500, -9.0, -21.9), (2.6833, -10.7, -24.9), (2.7167, -12.9, -28.8), (2.7500, -14.0, -30.7), (2.7833, -15.5, -33.4), (2.8167, -17.5, -36.8), (2.8500, -18.8, -39.2), (2.8833, -19.7, -40.7), (2.9167, -21.2, -43.4), (2.9500, -22.3, -45.2), (2.9833, -22.9, -46.2), (3.0167, -23.8, -48.0), (3.0500, -24.2, -48.7), (3.0833, -24.7, -49.4), (3.1167, -25.0, -50.0), (3.1376, -25.0, -50.0)],
}
RECOVERY_FROM, RECOVERY_BLEND = 0.95, 0.05


# ======================================================================================================================================
# The authored motion: Blender F-curves on one empty. Each row is (time s, {control: value}); the mode names the segment that STARTS at the row:
#   S smooth (Bezier), L linear, I ease in (accelerates into the next key: the swing), O ease out (decelerates: the follow-through), H hold.
# Angles in degrees, lengths in metres. Pelvis/spine/head/clavicle values are the runtime's Turn keys (stance frame); move_* the pelvis's Move key; l/rfoot_* the Foot
# offsets; the shaft controls are in the CHEST frame (the stance frame turned by the torso): r{f,r,u} the right palm's place from the middle of the shoulders (forward, right,
# up), sh_el / sh_az the blade's direction from the hands (elevation, azimuth from the chest's front toward its right), spacing the hands' distance along the shaft.
# ======================================================================================================================================
def rows():
    # Time landmarks: 0.20 the left hand has taken the shaft; the wind-up's apex (compact: about 0.33 s of gathering, then 0.19 s of acceleration into the hit); HIT the blade's
    # contact; 0.95 the finish pose, held to 1.18 (brief), then the approved recovery to the carry. NEXT_AT 1.098 is where the next light swing may start.
    # The hands' geometry (rf rr ru, the shaft's chest-frame direction, the spacing) is taken from the previous pass's cleared poses (measured with --reference): the hands low in front of the
    # chest, the shaft doing the sweeping; what is authored here is the timing, the hips' drive, the coil, the acceleration and the hold.
    apex = HIT - 0.19
    out = [
        (0.00, "S", dict(pelvis_yaw=0, pelvis_pitch=0, pelvis_roll=0, move_f=0, move_r=0, move_u=0, spine_yaw=0, spine_pitch=0, spine_roll=0, head_yaw=0, head_pitch=0,
                         clav_yaw=0, clav_pitch=0, clav_roll=0, ik_blend=0, grip_l=0, grip_pt=CARRY_GRIP, rf="carry", rr="carry", ru="carry", sh_el="carry", sh_az="carry", spacing=0.5)),
        (0.10, "S", dict(pelvis_yaw=-1, pelvis_pitch=0.3, pelvis_roll=1, move_f=0.01, move_r=0.015, move_u=-0.05, spine_yaw=-1.5, spine_pitch=0.5, spine_roll=0, head_yaw=4, head_pitch=1,
                         clav_yaw=-2, clav_pitch=3, clav_roll=0, ik_blend=0.9, grip_l=0.1, grip_pt=0.44, rf=0.50, rr=0.27, ru=-0.24, sh_el=-12, sh_az=-5, spacing=0.35)),
        (0.20, "S", dict(pelvis_yaw=-2, pelvis_pitch=0.7, pelvis_roll=2, move_f=0.02, move_r=0.03, move_u=-0.10, spine_yaw=-3, spine_pitch=1, spine_roll=0, head_yaw=8, head_pitch=2,
                         clav_yaw=-4, clav_pitch=6, clav_roll=0, ik_blend=1, grip_l=1, grip_pt=HEW_GRIP, rf=0.46, rr=0.36, ru=-0.28, sh_el=48, sh_az=70, spacing=0.30)),
        (0.36, "S", dict(pelvis_yaw=-5, pelvis_pitch=1.0, pelvis_roll=2.8, move_f=-0.02, move_r=0.06, move_u=-0.14, spine_yaw=-6, spine_pitch=2, spine_roll=0, head_yaw=11, head_pitch=3,
                         clav_yaw=-6, clav_pitch=8, clav_roll=0, ik_blend=1, grip_l=1, grip_pt=HEW_GRIP, rf=0.44, rr=0.20, ru=-0.22, sh_el=46, sh_az=92, spacing=0.30)),
        (apex, "I", dict(pelvis_yaw=-8, pelvis_pitch=1.3, pelvis_roll=3.8, move_f=-0.06, move_r=0.10, move_u=-0.20, spine_yaw=-9, spine_pitch=2.5, spine_roll=0, head_yaw=14, head_pitch=3,
                         clav_yaw=-6, clav_pitch=10, clav_roll=0, ik_blend=1, grip_l=1, grip_pt=HEW_GRIP, rf=0.44, rr=0.18, ru=-0.17, sh_el=40, sh_az=108, spacing=0.30)),
        # the swing's arm geometry: where the previous pass's cleared sweep had the palms and the shaft each beat through the cut (measured with --reference), so the blade keeps clear of the
        # body through the fast part; the hips, coil, timing and the acceleration around it are what this pass authors
        (0.567, "L", dict(rf=0.450, rr=0.146, ru=-0.100, sh_el=23.8, sh_az=95.8, spacing=0.303)),
        (0.600, "L", dict(rf=0.428, rr=0.073, ru=-0.151, sh_el=16.7, sh_az=83.8, spacing=0.301)),
        (0.633, "L", dict(rf=0.488, rr=-0.033, ru=0.233, sh_el=9.7, sh_az=61.7, spacing=0.254)),
        (0.667, "L", dict(rf=0.503, rr=0.225, ru=0.055, sh_el=10.1, sh_az=28.8, spacing=0.20)),        # the weight shifts and drops a beat ahead of the turn (the hips travel to the lead leg first, then turn through the target), so the planting foot is in reach when it lands
        (0.64, "S", dict(move_f=0.03, move_r=-0.21, move_u=-0.29)),
        (HIT, "O", dict(pelvis_yaw=62, pelvis_pitch=-3.9, pelvis_roll=-11.3, move_f=0.05, move_r=-0.26, move_u=-0.30, spine_yaw=8, spine_pitch=-3, spine_roll=0, head_yaw=-45, head_pitch=4,
                        clav_yaw=10, clav_pitch=2, clav_roll=0, ik_blend=1, grip_l=1, grip_pt=HEW_GRIP, rf=0.40, rr=0.431, ru=-0.150, sh_el=11.5, sh_az=-32.7, spacing=0.20)),
        (0.733, "L", dict(rf=0.386, rr=0.376, ru=-0.253, sh_el=2.0, sh_az=-26.6, spacing=0.274)),
        (0.767, "L", dict(rf=0.461, rr=0.298, ru=-0.222, sh_el=-7.0, sh_az=-28.8, spacing=0.289)),
        (0.80, "S", dict(ik_blend=1, grip_l=1, grip_pt=HEW_GRIP, rf=0.47, rr=0.275, ru=-0.227, sh_el=-12, sh_az=-36, spacing=0.33)),
        (0.95, "S", dict(pelvis_yaw=95, pelvis_pitch=-3.3, pelvis_roll=-9.5, move_f=0.016, move_r=-0.29, move_u=-0.28, spine_yaw=12, spine_pitch=-4, spine_roll=0, head_yaw=-85, head_pitch=6,
                         clav_yaw=14, clav_pitch=2, clav_roll=0, ik_blend=1, grip_l=1, grip_pt=HEW_GRIP, rf=0.43, rr=0.23, ru=-0.30, sh_el=-22, sh_az=-51, spacing=0.36)),
        (1.18, "S", dict(pelvis_yaw=91.5, pelvis_pitch=-3.3, pelvis_roll=-9.5, move_f=0.016, move_r=-0.29, move_u=-0.285, spine_yaw=12, spine_pitch=-4, spine_roll=0, head_yaw=-85, head_pitch=6,
                         clav_yaw=14, clav_pitch=2, clav_roll=0, ik_blend=1, grip_l=1, grip_pt=HEW_GRIP, rf=0.43, rr=0.24, ru=-0.30, sh_el=-22, sh_az=-52, spacing=0.36)),
        (1.40, "S", dict(pelvis_yaw=60)),
        (1.60, "S", dict(pelvis_yaw=40, pelvis_pitch=-2.6, pelvis_roll=-7.6, move_f=0.034, move_r=-0.221, move_u=-0.26, spine_yaw=6, spine_pitch=-4, spine_roll=0, head_yaw=-40, head_pitch=6,
                         clav_yaw=8, clav_pitch=1, clav_roll=0, ik_blend=1, grip_l=1, grip_pt=HEW_GRIP, rf=0.15, rr=0.46, ru=-0.31, sh_el=-6, sh_az=-21, spacing=0.30)),
        (2.00, "S", dict(pelvis_yaw=3, pelvis_pitch=-3, pelvis_roll=0, move_f=0.065, move_r=-0.189, move_u=-0.20, spine_yaw=2, spine_pitch=-1, spine_roll=0, head_yaw=-4, head_pitch=2,
                         clav_yaw=4, clav_pitch=1, clav_roll=0, ik_blend=0.6, grip_l=1, grip_pt=HEW_GRIP, rf=0.0, rr=0.62, ru=-0.2, sh_el=-5, sh_az=-48, spacing=0.50)),
        (2.40, "S", dict(ik_blend=0, grip_l=0, grip_pt=CARRY_GRIP)),
        (DURATION, "S", dict(pelvis_yaw=0, pelvis_pitch=0, pelvis_roll=0, move_f=0, move_r=0, move_u=0, spine_yaw=0, spine_pitch=0, spine_roll=0, head_yaw=0, head_pitch=0,
                             clav_yaw=0, clav_pitch=0, clav_roll=0, ik_blend=0, grip_l=0, grip_pt=CARRY_GRIP)),
    ]
    BRR, BRU = 0.12, 0.0
    bump = lambda t: max(0.0, 1.0 - abs(t - 0.65) / 0.045)   # the right palm held 0.10 m further out and 0.08 m lower around 0.65 s, so the left arm has room
    arm_keys = ("rf", "rr", "ru", "sh_el", "sh_az", "spacing")
    out = [(t, m, {k: v for k, v in d.items() if not (0.5 <= t <= 0.801 and k in arm_keys)}) for t, m, d in out]   # the swing's arms come from the table above
    # the previous pass's right palm crossed to the left of the chest's middle at 0.58-0.65 s (its shaft's butt end then passed the left upper arm); held to the right of it here, 0.12 m at the least
    out += [(t, "L", dict(rf=a, rr=(max(b, 0.12) if 0.58 <= t <= 0.66 else b) + BRR * bump(t), ru=c + BRU * bump(t), sh_el=d, sh_az=e, spacing=g)) for t, a, b, c, d, e, g in SWING_ARMS]
    return sorted(out, key=lambda r: r[0])


# Feet (offsets from the stance's spots, metres forward / right / up in the stance frame): the left foot lifts with the coil, steps toward the target and PLANTS before contact
# (0.64 s), stays through the finish and the hold, then steps back; the right (rear) foot stays planted.
def foot_rows():
    left = [
        (0.00, "S", (0.0, 0.0, 0.0)), (0.30, "S", (0.02, -0.02, 0.0)), (HIT - 0.19, "S", (0.04, -0.11, 0.14)), (0.64, "O", (0.16, -0.527, 0.0)),
        (2.00, "S", (0.16, -0.527, 0.0)), (2.30, "S", (0.08, -0.263, 0.08)), (DURATION, "S", (0.0, 0.0, 0.0)),
    ]
    right = [(0.0, "S", (0.0, 0.0, 0.0)), (DURATION, "S", (0.0, 0.0, 0.0))]
    return left, right


# ======================================================================================================================================
# The rig
# ======================================================================================================================================
def quat_xyzw(a):
    return Quaternion((a[3], a[0], a[1], a[2])).normalized()


class Rig:
    """The marine skeleton in the overlay's arithmetic (CharacterViewer.WeaponPoseOverlay / WeaponPoseMirror): local transforms, globals parents first."""

    def __init__(self, dump):
        self.names = [b["name"] for b in dump["bones"]]
        self.parent = [b["parent"] for b in dump["bones"]]
        self.index = {n: i for i, n in enumerate(self.names)}
        self.n = len(self.names)
        self.stance_loc = [Vector(b["stance"][0:3]) for b in dump["bones"]]
        self.stance_rot = [quat_xyzw(b["stance"][3:7]) for b in dump["bones"]]
        self.loc = [v.copy() for v in self.stance_loc]
        self.rot = [q.copy() for q in self.stance_rot]
        depth = []
        for b in range(self.n):
            d, p = 0, self.parent[b]
            while p >= 0:
                d, p = d + 1, self.parent[p]
            depth.append(d)
        order = sorted(range(self.n), key=lambda b: (depth[b], b))
        pel, spA = self.index["RIG_Pelvis"], self.index["RIG_SpineA"]
        self.carried = {pel: spA}   # the torso is carried with the pelvis (the rig hangs them side by side under the root)
        order.remove(spA)
        order.insert(order.index(pel) + 1, spA)
        self.order = order
        self.update()
        self.stance_g = [(self.gpos[b].copy(), self.grot[b].copy()) for b in range(self.n)]
        lt, rt = self.index["RIG_L_Thigh"], self.index["RIG_R_Thigh"]
        across = self.gpos[rt] - self.gpos[lt]
        across.z = 0
        self.right = across.normalized()
        self.front = UP.cross(self.right).normalized()
        self.up = UP.copy()
        self.planted = {}
        for side in ("L", "R"):
            f = self.index[f"RIG_{side}_Foot"]
            self.planted[side] = (self.gpos[f].copy(), self.grot[f].copy())

    def reset(self):
        for b in range(self.n):
            self.loc[b] = self.stance_loc[b].copy()
            self.rot[b] = self.stance_rot[b].copy()
        self.update()

    def snapshot(self):
        return [v.copy() for v in self.loc], [q.copy() for q in self.rot]

    def restore(self, snap):
        self.loc = [v.copy() for v in snap[0]]
        self.rot = [q.copy() for q in snap[1]]
        self.update()

    def update(self):
        self.gpos, self.grot = [None] * self.n, [None] * self.n
        for b in range(self.n):
            p = self.parent[b]
            if p < 0:
                self.gpos[b], self.grot[b] = self.loc[b].copy(), self.rot[b].copy()
            else:
                self.gpos[b] = self.gpos[p] + self.grot[p] @ self.loc[b]
                self.grot[b] = (self.grot[p] @ self.rot[b]).normalized()

    def origin(self, name):
        return self.gpos[self.index[name]]

    def move_bone(self, b, turn, shift, pivot):
        """The overlay's MoveBone: turn by the skeleton-space rotation about pivot, shift, at full weight."""
        p = self.parent[b]
        prot = self.grot[p] if p >= 0 else Quaternion()
        origin = self.gpos[b].copy()
        turns = abs(turn.w) <= 0.9999995
        if turns:
            local = self.rot[b]
            target = (prot.inverted() @ turn @ prot @ local).normalized()
            if local.dot(target) < 0:
                target = Quaternion((-target.w, -target.x, -target.y, -target.z))
            self.rot[b] = target
        moved = (pivot + turn @ (origin - pivot) if turns else origin.copy()) + shift
        delta = moved - origin
        if delta.length_squared > 1e-12:
            self.loc[b] = self.loc[b] + prot.inverted() @ delta
        self.update()

    def aim(self, bone, toward, want):
        """Aim channel: the segment bone -> toward points along `want` (skeleton space), by the shortest turn."""
        b, c = self.index[bone], self.index[toward]
        cur = (self.gpos[c] - self.gpos[b])
        if cur.length_squared < 1e-8:
            return
        arc = cur.normalized().rotation_difference(want.normalized())
        self.move_bone(b, arc, Vector((0, 0, 0)), self.gpos[b].copy())

    def dir_to_aim(self, d):
        """A skeleton-space direction as (elevation, azimuth) in the stance frame (the key's numbers)."""
        d = d.normalized()
        el = math.asin(max(-1.0, min(1.0, d.dot(self.up)))) / DEG
        az = math.atan2(d.dot(self.right), d.dot(self.front)) / DEG
        return el, az

    def aim_to_dir(self, el, az):
        e, a = el * DEG, az * DEG
        return (self.front * (math.cos(e) * math.cos(a)) + self.right * (math.cos(e) * math.sin(a)) + self.up * math.sin(e)).normalized()


def turn_quat(rig, yaw, pitch, roll):
    return (Quaternion(rig.up, yaw * DEG) @ Quaternion(rig.right, pitch * DEG) @ Quaternion(rig.front, roll * DEG)).normalized()


def lerp_dir(a, b, t):
    d = a * (1 - t) + b * t
    return d.normalized() if d.length_squared > 1e-8 else b


def solve_arm_dirs(rig, side, target_socket, hint, iterations=12, swivel=0.0):
    """Two-bone arm solve: the upper arm's and forearm's directions (skeleton space) that put the palm socket on target_socket (the elbow toward `hint`).
    The runtime lays the aims by shortest-arc turns, which fixes the hand's twist, so the socket's offset from the wrist is measured by laying them (scratch copy) and iterated."""
    up_n, fo_n, ha_n, so_n = f"RIG_{side}_UpperArm", f"RIG_{side}_Forearm", f"RIG_{side}_Hand", R_SOCKET if side == "R" else L_SOCKET
    snap = rig.snapshot()
    S, E, H = rig.origin(up_n).copy(), rig.origin(fo_n).copy(), rig.origin(ha_n).copy()
    a, b = (E - S).length, (H - E).length
    offset = rig.origin(so_n) - H
    result = None
    for _ in range(iterations):
        rig.restore(snap)
        ht = target_socket - offset
        to_t = ht - S
        dist = to_t.length
        direction = to_t / max(dist, 1e-6)
        d = min(max(dist, abs(a - b) + 1e-3), (a + b) * 0.999)
        pole = hint - direction * hint.dot(direction)
        if pole.length_squared < 1e-6:
            pole = direction.cross(UP)
        pole = pole.normalized()
        if swivel:
            pole = (Quaternion(direction, swivel * DEG) @ pole).normalized()   # the elbow swung round the shoulder-to-hand line
        x = (a * a - b * b + d * d) / (2 * d)
        h = math.sqrt(max(0.0, a * a - x * x))
        elbow = S + direction * x + pole * h
        upper_dir = (elbow - S).normalized()
        fore_dir = (S + direction * d - elbow).normalized()
        rig.aim(up_n, fo_n, upper_dir)
        rig.aim(fo_n, ha_n, fore_dir)
        socket = rig.origin(so_n)
        err = target_socket - socket
        offset = socket - rig.origin(ha_n)
        result = (upper_dir, fore_dir, err.length, dist > (a + b) * 0.999)
        if err.length < 1e-4:
            break
    rig.restore(snap)
    return result + (a,)


def aim_at(table, t):
    """Linear lookup in an (t, azimuth, elevation) table, the azimuth the short way round."""
    if t <= table[0][0]:
        return table[0][1], table[0][2]
    for (t0, a0, e0), (t1, a1, e1) in zip(table, table[1:]):
        if t <= t1:
            u = (t - t0) / max(t1 - t0, 1e-9)
            da = (a1 - a0 + 180.0) % 360.0 - 180.0
            return a0 + da * u, e0 + (e1 - e0) * u
    return table[-1][1], table[-1][2]


def seg_dist(p0, p1, q0, q1):
    """Least distance between segments p0-p1 and q0-q1."""
    u, v, w0 = p1 - p0, q1 - q0, p0 - q0
    a, b, c, d, e = u.dot(u), u.dot(v), v.dot(v), u.dot(w0), v.dot(w0)
    den = a * c - b * b
    sc = min(max((b * e - c * d) / den, 0.0), 1.0) if den > 1e-9 else 0.0
    tc = (b * sc + e) / c if c > 1e-9 else 0.0
    if tc < 0.0:
        tc, sc = 0.0, min(max(-d / a, 0.0), 1.0) if a > 1e-9 else 0.0
    elif tc > 1.0:
        tc, sc = 1.0, min(max((b - d) / a, 0.0), 1.0) if a > 1e-9 else 0.0
    return ((p0 + u * sc) - (q0 + v * tc)).length


def solve_leg(rig, side, target, planted_rot):
    """The overlay's SolveWeaponLeg: the thigh aims at the new knee, the calf at the foot, the knee bending the way it bends now."""
    th, ca, fo = rig.index[f"RIG_{side}_Thigh"], rig.index[f"RIG_{side}_Calf"], rig.index[f"RIG_{side}_Foot"]
    H, K, A = rig.gpos[th].copy(), rig.gpos[ca].copy(), rig.gpos[fo].copy()
    a, b = (K - H).length, (A - K).length
    if (A - target).length_squared < 1e-8:
        return 0.0
    to_t = target - H
    dist = to_t.length
    direction = to_t / dist
    d = min(max(dist, abs(a - b) + 1e-3), (a + b) * 0.995)
    along = A - H
    pole = (K - H) - along.normalized() * (K - H).dot(along.normalized()) if along.length_squared > 1e-8 else Vector()
    if pole.length_squared < 1e-6:
        pole = rig.front.copy()
    pole = pole - direction * pole.dot(direction)
    if pole.length_squared < 1e-8:
        pole = UP.cross(direction)
    pole = pole.normalized()
    x = (a * a - b * b + d * d) / (2 * d)
    h = math.sqrt(max(0.0, a * a - x * x))
    knee = H + direction * x + pole * h
    foot = H + direction * d
    parent_rot = rig.grot[rig.parent[th]]
    thigh_rot, knee_rot, foot_rot = rig.grot[th].copy(), rig.grot[ca].copy(), rig.grot[fo].copy()
    thigh_new = ((K - H).normalized().rotation_difference((knee - H).normalized()) @ thigh_rot).normalized()
    rig.rot[th] = (parent_rot.inverted() @ thigh_new).normalized()
    calf_rel = (thigh_rot.inverted() @ knee_rot).normalized()
    calf_rot = (thigh_new @ calf_rel).normalized()
    foot_now = knee + calf_rot @ (knee_rot.inverted() @ (A - K))
    calf_new = ((foot_now - knee).normalized().rotation_difference((foot - knee).normalized()) @ calf_rot).normalized()
    rig.rot[ca] = (thigh_new.inverted() @ calf_new).normalized()
    rig.rot[fo] = (calf_new.inverted() @ planted_rot).normalized()
    rig.update()
    return max(0.0, dist - (a + b) * 0.995)


# ======================================================================================================================================
# Controls as Blender F-curves
# ======================================================================================================================================
def build_controls(carry):
    scene = bpy.context.scene
    scene.render.fps = int(FPS)
    ctrl = bpy.data.objects.new("HEW_CTRL", None)
    scene.collection.objects.link(ctrl)
    series = {}
    for t, mode, values in rows():
        for name, v in values.items():
            if v == "carry":
                v = carry[name]
            if mode == "I" and name.split("_")[0] in ("spine", "clav", "head", "pelvis") and name not in ("pelvis_yaw", "pelvis_pitch", "pelvis_roll"):
                mode = "L"   # the torso's small turns, the shoulders and the head run at a steady rate through the swing; the hips' yaw is what accelerates into the hit
            series.setdefault(name, []).append((t, float(v), mode))
    for side, rows_ in zip(("l", "r"), foot_rows()):
        for k, axis in enumerate(("f", "r", "u")):
            series[f"{side}foot_{axis}"] = [(t, v[k], m) for t, m, v in rows_]
    for name, keys in series.items():
        ctrl[name] = keys[0][1]
        for t, v, mode in keys:
            ctrl[name] = v
            ctrl.keyframe_insert(data_path=f'["{name}"]', frame=t * FPS)
        fc = ctrl.animation_data.action.fcurves.find(f'["{name}"]')
        for kp, (t, v, mode) in zip(fc.keyframe_points, keys):
            kp.handle_left_type = kp.handle_right_type = "AUTO_CLAMPED"
            if mode == "S":
                kp.interpolation = "BEZIER"
            elif mode == "L":
                kp.interpolation = "LINEAR"
            elif mode == "H":
                kp.interpolation = "CONSTANT"
            elif mode == "I":
                kp.interpolation, kp.easing = "QUAD", "EASE_IN"
            elif mode == "O":
                kp.interpolation, kp.easing = "CUBIC", "EASE_OUT"
    curves = {name: ctrl.animation_data.action.fcurves.find(f'["{name}"]') for name in series}
    return ctrl, curves


def value(curves, name, t):
    return curves[name].evaluate(t * FPS)


# ======================================================================================================================================
# One sample: controls at time t -> the runtime's channel values and the resulting pose
# ======================================================================================================================================
def pose_at(rig, curves, t, carry_dirs):
    rig.reset()
    v = lambda n: value(curves, n, t)
    right, front, up = rig.right, rig.front, rig.up
    out = {}
    q_pelvis = turn_quat(rig, v("pelvis_yaw"), v("pelvis_pitch"), v("pelvis_roll"))
    shift = front * v("move_f") + right * v("move_r") + up * v("move_u")
    spine_q = turn_quat(rig, v("spine_yaw"), v("spine_pitch"), v("spine_roll"))
    head_q = turn_quat(rig, v("head_yaw"), v("head_pitch"), 0.0)
    clav_q = turn_quat(rig, v("clav_yaw"), v("clav_pitch"), v("clav_roll"))
    turns = {"RIG_Pelvis": q_pelvis, "RIG_SpineA": spine_q, "RIG_SpineD": spine_q, "RIG_Head": head_q, "RIG_L_Clavicle": clav_q, "RIG_R_Clavicle": clav_q}
    arms_done = False
    infos = {}
    for b in rig.order:
        name = rig.names[b]
        if name in ("RIG_Pelvis",):
            pivot = rig.gpos[b].copy()
            rig.move_bone(b, q_pelvis, shift, pivot)
            c = rig.carried.get(b)
            if c is not None:
                rig.move_bone(c, q_pelvis, shift, pivot)
        elif name in turns:
            rig.move_bone(b, turns[name], Vector((0, 0, 0)), rig.gpos[b].copy())
        elif name == "RIG_R_UpperArm" and not arms_done:
            arms_done = True
            # the chest frame: the stance frame turned by what the torso has done to the spine's top
            sd = rig.index["RIG_SpineD"]
            chest = (rig.grot[sd] @ rig.stance_g[sd][1].inverted()).normalized()
            cf, cr, cu = chest @ front, chest @ right, chest @ up
            mid = (rig.origin("RIG_R_UpperArm") + rig.origin("RIG_L_UpperArm")) * 0.5
            el, az = v("sh_el") * DEG, v("sh_az") * DEG
            shaft = (cf * (math.cos(el) * math.cos(az)) + cr * (math.cos(el) * math.sin(az)) + cu * math.sin(el)).normalized()
            pr = mid + cf * v("rf") + cr * v("rr") + cu * v("ru")
            # the hands close up (never under 0.10 m apart) when the leading arm cannot reach the authored spacing, so the left palm stays on the shaft
            spacing = v("spacing")
            for _ in range(4):
                e = solve_arm_dirs(rig, "L", pr + shaft * spacing, -cr * 0.8 - cf * 0.1 - cu * 0.4)[2]
                if e < 0.012 or spacing <= 0.10:
                    break
                spacing = max(0.10, spacing - e * 1.2)
            pl = pr + shaft * spacing
            blend = min(max(v("ik_blend"), 0.0), 1.0)
            grip_l = min(max(v("grip_l"), 0.0), 1.0)
            dirs = {}
            rec = min(max((t - RECOVERY_FROM) / RECOVERY_BLEND, 0.0), 1.0)
            rec = rec * rec * (3 - 2 * rec)
            for side, target, hint in (("R", pr, cr * 0.8 - cf * 0.1 - cu * 0.4), ("L", pl, -cr * 0.8 - cf * 0.1 - cu * 0.4)):
                # the elbow's swivel: the natural one (down and out) unless the shaft would pass through the upper arm; then the nearest swing that keeps the upper arm clear of it
                butt = pr - shaft * v("grip_pt")
                tip = butt + shaft * BLADE_FROM_BUTT
                best = None
                for sw in range(-150, 151, 15):
                    ud, fd, err, clamped, ulen = solve_arm_dirs(rig, side, target, hint, swivel=sw)
                    elbow = rig.origin(f"RIG_{side}_UpperArm") + ud * ulen
                    clear = seg_dist(rig.origin(f"RIG_{side}_UpperArm"), elbow, butt, tip)
                    score = min(clear, 0.30) - 0.0002 * abs(sw)
                    if best is None or score > best[0]:
                        best = (score, ud, fd, err, clamped)
                    if clear >= 0.30 and sw == 0:
                        break
                _, ud, fd, err, clamped = best
                cu_d, cf_d = carry_dirs[side]
                dirs[side] = (lerp_dir(cu_d, ud, blend), lerp_dir(cf_d, fd, blend))
                if rec > 0.0:
                    ro = [aim_at(RECOVERY_AIMS[f"{side}_{k}"], t) for k in ("upper", "fore")]
                    dirs[side] = tuple(lerp_dir(dirs[side][j], rig.aim_to_dir(ro[j][1], ro[j][0]), rec) for j in (0, 1))
                infos[side] = (err, clamped)
            for side in ("R", "L"):
                rig.aim(f"RIG_{side}_UpperArm", f"RIG_{side}_Forearm", dirs[side][0])
                rig.aim(f"RIG_{side}_Forearm", f"RIG_{side}_Hand", dirs[side][1])
            for side in ("R", "L"):
                out[f"{side}_upper"] = rig.dir_to_aim(dirs[side][0])
                out[f"{side}_fore"] = rig.dir_to_aim(dirs[side][1])
            out["_shaft"] = (pr, pl, shaft, grip_l)
    # the legs: solved onto the planted feet plus the authored offsets (the stance frame)
    foot_err = 0.0
    for side in ("L", "R"):
        off = front * v(f"{side.lower()}foot_f") + right * v(f"{side.lower()}foot_r") + up * v(f"{side.lower()}foot_u")
        pos, rot = rig.planted[side]
        foot_err = max(foot_err, solve_leg(rig, side, pos + off, rot))
    out["_arm_err"] = infos
    out["_leg_over"] = foot_err
    out["controls"] = {n: v(n) for n in ("pelvis_yaw", "pelvis_pitch", "pelvis_roll", "move_f", "move_r", "move_u", "spine_yaw", "spine_pitch", "spine_roll", "head_yaw", "head_pitch",
                                          "clav_yaw", "clav_pitch", "clav_roll", "lfoot_f", "lfoot_r", "lfoot_u", "rfoot_f", "rfoot_r", "rfoot_u", "grip_l", "grip_pt")}
    return out


def measure_reference(rig, ref, picks):
    """Reference mode (--reference <json> of the previous pass's channels sampled at 30 Hz, written by the test suite, private): lays those channels with the same arithmetic and prints where
    the palms and the shaft are in the chest frame, so the authored key poses start from a known-clear arm geometry (rf rr ru sh_el sh_az spacing)."""
    times = ref["times"]
    for t in picks:
        i = min(range(len(times)), key=lambda k: abs(times[k] - t))
        rig.reset()
        g = lambda key: ref[key][i]
        py, pp, pr_ = g("RIG_Pelvis|Turn|")[0:3]
        mf, mr, mu = g("RIG_Pelvis|Move|")[0:3]
        pel = rig.index["RIG_Pelvis"]
        q = turn_quat(rig, py, pp, pr_)
        sh = rig.front * mf + rig.right * mr + rig.up * mu
        pivot = rig.gpos[pel].copy()
        rig.move_bone(pel, q, sh, pivot)
        rig.move_bone(rig.index["RIG_SpineA"], q, sh, pivot)
        for bone in ("RIG_SpineA", "RIG_SpineD", "RIG_Head", "RIG_L_Clavicle", "RIG_R_Clavicle"):
            y, p, r = g(f"{bone}|Turn|")[0:3]
            rig.move_bone(rig.index[bone], turn_quat(rig, y, p, r), Vector((0, 0, 0)), rig.gpos[rig.index[bone]].copy())
        for side in ("R", "L"):
            for bone, child in ((f"RIG_{side}_UpperArm", f"RIG_{side}_Forearm"), (f"RIG_{side}_Forearm", f"RIG_{side}_Hand")):
                az, el = g(f"{bone}|Aim|{child}")[0:2]
                rig.aim(bone, child, rig.aim_to_dir(el, az))
        sd = rig.index["RIG_SpineD"]
        chest = (rig.grot[sd] @ rig.stance_g[sd][1].inverted()).normalized()
        cf, cr, cu = chest @ rig.front, chest @ rig.right, chest @ rig.up
        mid = (rig.origin("RIG_R_UpperArm") + rig.origin("RIG_L_UpperArm")) * 0.5
        rs, ls = rig.origin(R_SOCKET), rig.origin(L_SOCKET)
        d = rs - mid
        axis = (ls - rs)
        spacing = axis.length
        axis = axis.normalized()
        el = math.asin(max(-1, min(1, axis.dot(cu)))) / DEG
        az = math.atan2(axis.dot(cr), axis.dot(cf)) / DEG
        print(f"SWING ({times[i]:.4f}, {d.dot(cf):.3f}, {d.dot(cr):.3f}, {d.dot(cu):.3f}, {el:.1f}, {az:.1f}, {spacing:.3f}),")
        print(f"  ref t {times[i]:.3f}: rf {d.dot(cf):.3f} rr {d.dot(cr):.3f} ru {d.dot(cu):.3f} sh_el {el:.1f} sh_az {az:.1f} spacing {spacing:.3f} grip {ref['grip'][i]:.2f} pt {ref['grip_point'][i]:.2f}")
    rig.reset()


def carry_state(rig):
    """The carry (the clip's first pose): the right palm's place relative to the shoulders and the four carry directions, in the stance frame."""
    rig.reset()
    dirs = {}
    for side in ("R", "L"):
        (ue, ua), (fe, fa) = CARRY_AIMS[side]
        dirs[side] = (rig.aim_to_dir(ue, ua - IDLE_HIPS_TURN), rig.aim_to_dir(fe, fa - IDLE_HIPS_TURN))
    for side in ("R", "L"):
        rig.aim(f"RIG_{side}_UpperArm", f"RIG_{side}_Forearm", dirs[side][0])
        rig.aim(f"RIG_{side}_Forearm", f"RIG_{side}_Hand", dirs[side][1])
    mid = (rig.origin("RIG_R_UpperArm") + rig.origin("RIG_L_UpperArm")) * 0.5
    d = rig.origin(R_SOCKET) - mid
    # the carry's shaft: SpearHold.CarryElevation 65, CarryAzimuth -50 in the facing frame
    shaft = rig.aim_to_dir(65.0, -50.0 - IDLE_HIPS_TURN)
    carry = dict(rf=d.dot(rig.front), rr=d.dot(rig.right), ru=d.dot(rig.up), sh_el=65.0, sh_az=-50.0 - IDLE_HIPS_TURN)
    rig.reset()
    return carry, dirs


# ======================================================================================================================================
def main():
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    opt = {args[i]: args[i + 1] for i in range(len(args) - 1) if args[i].startswith("--") and not args[i + 1].startswith("--")}
    dump = json.load(open(opt["--rig"], encoding="utf-8"))
    rig = Rig(dump)
    carry, carry_dirs = carry_state(rig)
    print(f"stance frame: front {tuple(round(x, 3) for x in rig.front)} right {tuple(round(x, 3) for x in rig.right)}; carry fist {carry}")
    if "--reference" in opt:
        measure_reference(rig, json.load(open(opt["--reference"], encoding="utf-8")), [k / 90.0 for k in range(45, 73)])
    ctrl, curves = build_controls(carry)

    n = int(math.ceil(DURATION * FPS))
    # the 60 Hz grid plus the clip's landmarks (the wind-up's apex and the hit are keys exactly, as every strike's are)
    grid = [min(i / FPS, DURATION) for i in range(n + 1)]
    landmarks = [round(HIT - 0.19, 6), round(HIT, 6)]
    times = sorted(set(round(t, 6) for t in grid + landmarks))
    times = [t for t in times if not any(0 < abs(t - m) < 0.004 and t not in landmarks for m in landmarks)]   # a grid sample within 4 ms of a landmark gives way to it
    samples = []
    worst_arm, worst_leg = 0.0, 0.0
    CHECK = ["RIG_Pelvis", "RIG_SpineD", "RIG_Head", R_SOCKET, L_SOCKET, "RIG_R_Forearm", "RIG_L_Forearm", "RIG_R_Hand", "RIG_L_Hand", "RIG_L_Calf", "RIG_R_Calf", "RIG_L_Foot", "RIG_R_Foot"]
    for t in times:
        s = pose_at(rig, curves, t, carry_dirs)
        s["check"] = {k: tuple(rig.origin(k)) for k in CHECK}
        s["t"] = t
        samples.append(s)
        for side, (err, clamped) in s["_arm_err"].items():
            if side == "R" or s["controls"]["grip_l"] > 0.99:
                worst_arm = max(worst_arm, err)
                if err > 0.03 and "--verbose" in args:
                    print(f"  t {t:.3f} {side} socket off by {err:.3f} m{' (out of reach)' if clamped else ''}")
        worst_leg = max(worst_leg, s["_leg_over"])
    print(f"{len(samples)} samples; worst palm-socket error {worst_arm:.4f} m; legs out of reach by up to {worst_leg:.4f} m")

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
    # Aim keys are stored (azimuth, elevation) like PoseKey's Yaw, Pitch: key_list above wrote [t, azimuth, elevation, 0, weight].
    data = {
        "format": "sm-spear-authored-clip/1",
        "name": CLIP_NAME,
        "authoring": {"tool": f"Blender {bpy.app.version_string}", "script": "tools/blender/spear_hew_author.py", "method": "hand-keyed F-curves on control properties, solved by Python (FK in the runtime overlay's arithmetic, two-bone arm and leg solves); no motion capture, no video-to-motion"},
        "units": {"length": "metres", "angle": "degrees", "time": "seconds"},
        "axes": "skeleton space, Z up. Stance frame: right = left thigh to right thigh (flat), front = up x right. Turn: yaw positive toward the character's left (about up), pitch about the stance right (positive tips a standing segment back), roll about the stance front (positive tips it to the right); composed yaw, pitch, roll about the bone's own origin. Aim: azimuth from the stance front toward its right, elevation above the horizontal (absolute). Move/Foot: forward, right, up metres in the stance frame.",
        "rest_pose": {"clip": dump["stance_clip"], "time": dump["stance_time"], "note": "the base stance the keys lie over (hips 71 degrees right of the facing); a bone with no track at the skeleton's reference pose; keys are valid over any loop phase of the idle"},
        "sample_rate_hz": FPS,
        "timing": {"duration": round(DURATION, 5), "hit_time": round(HIT, 5), "source": "the hammer's X1 SM_MEL_TH_XATK_Combo2 2.667 s, hit 0.604 s at the spear's tempo 0.85", "wind_up_apex": round(HIT - 0.19, 5), "finish_pose": 0.95, "finish_hold_end": 1.18, "recovery_begins": 1.18},
        "interpolation": "linear between samples (PoseEase.Linear)",
        "bone_map": {"pelvis": "RIG_Pelvis", "torso_carried_with_pelvis": "RIG_SpineA", "spine_top": "RIG_SpineD", "head": "RIG_Head", "clavicles": ["RIG_L_Clavicle", "RIG_R_Clavicle"],
                     "arms": {"R": ["RIG_R_UpperArm", "RIG_R_Forearm", "RIG_R_Hand"], "L": ["RIG_L_UpperArm", "RIG_L_Forearm", "RIG_L_Hand"]}, "grip_sockets": [R_SOCKET, L_SOCKET],
                     "legs": {"L": ["RIG_L_Thigh", "RIG_L_Calf", "RIG_L_Foot"], "R": ["RIG_R_Thigh", "RIG_R_Calf", "RIG_R_Foot"]}},
        "channels": ch,
        "grip": [[round(s["t"], 5), round(s["controls"]["grip_l"], 4)] for s in samples],
        "grip_point": [[round(s["t"], 5), round(s["controls"]["grip_pt"], 4)] for s in samples],
    }
    check = {"rate_hz": 20, "note": "joint origins in skeleton space (metres) as Blender solved them over the base stance at its time 0: the install's proportions, so PRIVATE (never committed); the in-game comparison test reads it",
             "joints": {k: [[round(x, 4) for x in s["check"][k]] for i, s in enumerate(samples) if i % 3 == 0] for k in CHECK},
             "times": [round(s["t"], 5) for i, s in enumerate(samples) if i % 3 == 0]}
    if "--check" in opt:
        with open(opt["--check"], "w", encoding="utf-8", newline="\n") as f:
            json.dump(check, f)
    if "--export" in opt:
        os.makedirs(os.path.dirname(os.path.abspath(opt["--export"])), exist_ok=True)
        with open(opt["--export"], "w", encoding="utf-8", newline="\n") as f:
            json.dump(data, f, indent=1, sort_keys=False)
            f.write("\n")
        print("exported", opt["--export"])
    if "--blend" in opt:
        bpy.ops.wm.save_as_mainfile(filepath=opt["--blend"])
    if "--renders" in opt:
        sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
        import spear_hew_render
        spear_hew_render.render(rig, curves, carry_dirs, opt["--renders"], samples, pose_at)


if __name__ == "__main__":
    main()
