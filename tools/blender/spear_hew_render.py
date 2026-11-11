"""Workbench stills of the authored hew's key poses (proxy cylinders on the solved skeleton, the spear as a line with its blade, a stand-in target), from the game's
third-person camera position, the side, the front and the top. Private previews only (they show the marine's proportions): written to a folder outside the repository."""
import math
import os

import bpy
from mathutils import Vector

DEG = math.pi / 180.0

SEGMENTS = [
    ("RIG_Pelvis", "RIG_SpineA", 0.16), ("RIG_SpineA", "RIG_SpineD", 0.19), ("RIG_SpineD", "RIG_NeckA", 0.2), ("RIG_NeckA", "RIG_Head", 0.07),
    ("RIG_Pelvis", "RIG_L_Thigh", 0.1), ("RIG_Pelvis", "RIG_R_Thigh", 0.1),
    ("RIG_L_Thigh", "RIG_L_Calf", 0.085), ("RIG_L_Calf", "RIG_L_Foot", 0.07), ("RIG_L_Foot", "RIG_L_Toe", 0.05),
    ("RIG_R_Thigh", "RIG_R_Calf", 0.085), ("RIG_R_Calf", "RIG_R_Foot", 0.07), ("RIG_R_Foot", "RIG_R_Toe", 0.05),
    ("RIG_SpineD", "RIG_L_UpperArm", 0.07), ("RIG_SpineD", "RIG_R_UpperArm", 0.07),
    ("RIG_L_UpperArm", "RIG_L_Forearm", 0.06), ("RIG_L_Forearm", "RIG_L_Hand", 0.05), ("RIG_L_Hand", "RIG_Prop_LHand", 0.04),
    ("RIG_R_UpperArm", "RIG_R_Forearm", 0.06), ("RIG_R_Forearm", "RIG_R_Hand", 0.05), ("RIG_R_Hand", "RIG_Prop_RHand", 0.04),
    ("RIG_Head", "RIG_ForeHead", 0.11),
]
COLOURS = {"L": (0.25, 0.45, 0.85, 1), "R": (0.85, 0.35, 0.25, 1), "C": (0.6, 0.6, 0.62, 1)}


def _cyl(name, colour):
    bpy.ops.mesh.primitive_cylinder_add(vertices=14, radius=1.0, depth=1.0)
    o = bpy.context.active_object
    o.name = name
    o.color = colour
    return o


def _place(o, a, b, radius):
    d = b - a
    length = max(d.length, 1e-4)
    o.location = (a + b) * 0.5
    o.rotation_mode = "QUATERNION"
    o.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(d.normalized())
    o.scale = (radius, radius, length)


def render(rig, curves, carry_dirs, outdir, samples, pose_at, times=None, size=640):
    os.makedirs(outdir, exist_ok=True)
    scene = bpy.context.scene
    for o in list(bpy.data.objects):
        if o.type in {"MESH", "LIGHT", "CAMERA"}:
            bpy.data.objects.remove(o, do_unlink=True)
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.render.resolution_x = scene.render.resolution_y = size
    scene.render.film_transparent = False
    sh = scene.display.shading
    sh.light, sh.color_type, sh.show_shadows, sh.show_cavity = "FLAT", "OBJECT", False, False
    world = bpy.data.worlds.new("w") if not bpy.data.worlds else bpy.data.worlds[0]
    scene.world = world
    world.color = (0.93, 0.93, 0.95)
    proxies = []
    for a, b, r in SEGMENTS:
        side = "L" if "_L_" in a + b or "LHand" in b else "R" if "_R_" in a + b or "RHand" in b else "C"
        proxies.append((a, b, r, _cyl(f"p_{a}_{b}", COLOURS[side])))
    shaft = _cyl("shaft", (0.1, 0.1, 0.1, 1))
    blade = _cyl("blade", (0.2, 0.55, 0.95, 1))
    floor = _cyl("floor", (0.8, 0.8, 0.78, 1))
    floor.scale, floor.location = (4.0, 4.0, 0.02), (0, 0.8, -0.01)
    target = _cyl("target", (0.35, 0.55, 0.3, 1))
    target.scale, target.location = (0.35, 0.35, 1.9), (0.0, 2.2, 0.95)   # a stand-in Ork's column 2.2 m ahead of the facing (+Y)

    cam_data = bpy.data.cameras.new("cam")
    cam = bpy.data.objects.new("cam", cam_data)
    scene.collection.objects.link(cam)
    scene.camera = cam

    def look(from_, at, ortho=None, lens=35):
        cam.location = from_
        cam.rotation_mode = "QUATERNION"
        cam.rotation_quaternion = (Vector(at) - Vector(from_)).to_track_quat("-Z", "Y")
        if ortho:
            cam_data.type, cam_data.ortho_scale = "ORTHO", ortho
        else:
            cam_data.type, cam_data.lens = "PERSP", lens

    pel = rig.origin("RIG_Pelvis")
    views = {
        "tp": lambda: look(Vector((pel.x + 0.9, pel.y - 3.2, 2.3)), Vector((pel.x, pel.y + 0.2, 1.35)), lens=38),   # behind and above the right shoulder, as the game's camera
        "side": lambda: look(Vector((pel.x + 6.0, pel.y + 0.6, 1.2)), Vector((pel.x, pel.y + 0.6, 1.2)), ortho=3.4),   # the marine's right, facing +Y to the right of the picture
        "front": lambda: look(Vector((pel.x, pel.y + 6.0, 1.2)), Vector((pel.x, pel.y, 1.2)), ortho=3.4),
        "top": lambda: look(Vector((pel.x, pel.y + 0.4, 6.0)), Vector((pel.x, pel.y + 0.4, 0.0)), ortho=3.6),
    }
    times = times or [0.0, 0.2, 0.36, 0.53, 0.64, 0.711, 0.8, 0.95, 1.18, 1.6, 2.0, 2.6]
    for t in times:
        pose_at(rig, curves, t, carry_dirs)
        for a, b, r, o in proxies:
            _place(o, rig.origin(a), rig.origin(b), r)
        rs, ls = rig.origin("RIG_Prop_RHand"), rig.origin("RIG_Prop_LHand")
        grip_l = min(max(curves["grip_l"].evaluate(t * 60.0), 0.0), 1.0)
        grip_pt = curves["grip_pt"].evaluate(t * 60.0)
        carry_dir = rig.aim_to_dir(65.0, -50.0 - 71.0)
        two = (ls - rs).normalized()
        d = (carry_dir * (1 - grip_l) + two * grip_l).normalized()
        butt = rs - d * grip_pt
        _place(shaft, butt, butt + d * 2.85, 0.021)
        _place(blade, butt + d * 1.98, butt + d * 2.85, 0.07)
        for vname, setter in views.items():
            setter()
            scene.render.filepath = os.path.join(outdir, f"{vname}_{int(round(t * 1000)):04d}.png")
            bpy.ops.render.render(write_still=True)
