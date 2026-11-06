# ashfall_bake.py - weathers the Ashfall kit's Meshy models (GPL-3.0, sm-reimpl; our code).
#
# Run headless, never with a window:
#   blender.exe --background --factory-startup --python tools/blender/ashfall_bake.py -- \
#       --src <kit source root> --out <processed root> [--only AC-001,AC-002] [--size 1024] [--samples 16] [--preview <dir>]
#
# For every glb under --src that has a base colour texture (the textureless ashfall-ground models are skipped) it bakes a new
# base colour image on the model's own UV map: the original colour made cooler, greyer and darker, with cavity/AO soot,
# soot at the wall base, vertical streaks, blotches, grit and lighter worn edges, then writes the glb to the same relative
# path under --out. Metallic-roughness and normal textures are kept (shrunk to --size at most). The source is never written.
import bpy, sys, os, time, zlib, math, traceback
import numpy as np

# ---- recipe constants (tuned by looking at previews; see tools/blender/README.md) ----
DESATURATE = 0.35                    # saturation is multiplied by (1 - this)
COOL_GREY = (0.58, 0.59, 0.61)  # multiplies the colour: darker and cooler (less cream)
SOOT_COLOR = (0.05, 0.048, 0.045)
WORN_COLOR = (0.56, 0.56, 0.57)
SOOT_CAVITY = 1.1 # soot mix weights
SOOT_BASE = 0.65
SOOT_STREAK = 1.2  
SOOT_BLOTCH = 0.45 
SOOT_MAX = 0.88 
WORN_EDGE = 0.7  
TONE_SHIFT = 0.06                    # per-asset +-6 % brightness
AO_DISTANCE_PER_SIZE = 0.03          # AO distance = this * the model's largest side, but at least 0.5 m
AO_DISTANCE_MIN = 0.5
AO_CLEAR = 0.92                      # AO value above which there is no cavity
AO_FULL = 0.5                        # AO value at which the cavity is full
BASE_BAND_FRACTION = 0.20            # soot band at the foot: this fraction of the height ...
BASE_BAND_MAX = 2.5                  # ... but at most this many metres
STREAK_SCALE = (2.5, 2.5, 0.35)      # noise frequency per metre along x, y, z (low z = long vertical streaks)
BLOTCH_SCALES = (0.4, 1.5)           # noise frequency per metre
GRIT_SCALE = 14.0
GRIT_AMOUNT = 0.35                   # +- brightness of the grit
BAKE_MARGIN = 8
# ---- v2 additions: contrast of the weathering (owner: concept walls show clear streaks, stains, crisp worn edges) ----
AO_UP_DISTANCE = 2.0                 # AO looking straight up: dark under ledges and sills; streaks start there and run down
STREAK_BASE = 0.5                    # share of the streaks that exist everywhere (the rest needs a ledge above)
STREAK_RANGE = (0.43, 0.53)          # noise ramp of the streaks (narrower = crisper)
STREAK_THIN = 5.0                    # streak frequency per metre across, x and y (thin streaks)
BLOCK_SCALE = 0.9                    # per-block tone cells per metre (voronoi)
BLOCK_TONE = 0.08                    # +-8 % per block
PATCH_SCALE = 0.12                   # large stain patches per metre
PATCH_RANGE = (0.50, 0.62)
SOOT_PATCH = 0.7  
EDGE_RANGE = (0.535, 0.60)           # pointiness ramp of the worn edges (narrow = crisp)
TARGET_LUMA = 0.28                   # per-model correction: mean luminance of a copy is pulled into [LUMA_LOW, LUMA_HIGH] x this
LUMA_LOW, LUMA_HIGH = 0.85, 1.08
TARGET_R_OVER_B = 0.95  # mean red / blue of the median model; copies warmer than MAX_R_OVER_B are cooled to it
MAX_R_OVER_B = 1.0  
MATTE_ROUGHNESS = 1.0                # glTF roughness factor of the processed copies; metallic 0, no specular sheen


def args_after_dashes():
    a = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    d = {'src': None, 'out': None, 'only': None, 'size': 1024, 'samples': 16, 'preview': None}
    i = 0
    while i < len(a):
        k = a[i].lstrip('-')
        if k in d and i + 1 < len(a):
            d[k] = a[i + 1]; i += 2
        else:
            i += 1
    d['size'] = int(d['size']); d['samples'] = int(d['samples'])
    return d


def glb_json(path):
    import json, struct
    with open(path, 'rb') as f:
        head = f.read(20)
        length = struct.unpack('<I', head[12:16])[0]
        f.seek(20)
        return json.loads(f.read(length))


def has_base_texture(path):
    try:
        j = glb_json(path)
    except Exception:
        return False
    return any('baseColorTexture' in m.get('pbrMetallicRoughness', {}) for m in j.get('materials', []))


def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def import_glb(path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path)
    return [o for o in bpy.data.objects if o not in before]


def join_meshes(objs):
    meshes = [o for o in objs if o.type == 'MESH']
    for o in objs:
        if o.type != 'MESH':
            continue
    bpy.ops.object.select_all(action='DESELECT')
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    bpy.ops.object.make_single_user(object=True, obdata=True)
    bpy.ops.object.parent_clear(type='CLEAR_KEEP_TRANSFORM')
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    if len(meshes) > 1:
        bpy.ops.object.join()
    ob = bpy.context.view_layer.objects.active
    for o in list(bpy.data.objects):
        if o is not ob:
            bpy.data.objects.remove(o, do_unlink=True)
    ob.name = 'kit'
    return ob


def world_bounds(ob):
    import mathutils
    pts = [ob.matrix_world @ mathutils.Vector(c) for c in ob.bound_box]
    mn = [min(p[i] for p in pts) for i in range(3)]
    mx = [max(p[i] for p in pts) for i in range(3)]
    return mn, mx


def find_base_tex_nodes(mat):
    out = []
    for n in mat.node_tree.nodes:
        if n.type == 'BSDF_PRINCIPLED':
            for l in n.inputs['Base Color'].links:
                if l.from_node.type == 'TEX_IMAGE':
                    out.append(l.from_node)
    return out


def mean_luma(img, n=256):
    if img.size[0] <= 1024 and img.size[1] <= 1024:
        arr = np.empty(img.size[0] * img.size[1] * 4, dtype=np.float32)
        img.pixels.foreach_get(arr)
        arr = arr.reshape(-1, 4)
        return float((0.2126 * arr[:, 0] + 0.7152 * arr[:, 1] + 0.0722 * arr[:, 2]).mean())
    c = img.copy()
    try:
        c.scale(n, n)
        arr = np.empty(n * n * 4, dtype=np.float32)
        c.pixels.foreach_get(arr)
    finally:
        bpy.data.images.remove(c)
    arr = arr.reshape(-1, 4)
    return float((0.2126 * arr[:, 0] + 0.7152 * arr[:, 1] + 0.0722 * arr[:, 2]).mean())


def file_seed(name):
    h = zlib.crc32(name.encode('utf-8')) & 0xffffffff
    return (h % 10000) / 100.0, ((h >> 8) % 1000) / 999.0 * 2 - 1     # noise W offset 0..100, tone -1..1


class Rig:
    """Small helper that builds shader nodes."""
    def __init__(self, mat):
        self.nt = mat.node_tree
        self.nodes = self.nt.nodes
        self.links = self.nt.links

    def put(self, inp, v):
        if isinstance(v, bpy.types.NodeSocket):
            self.links.new(v, inp)
        elif isinstance(v, (tuple, list)):
            inp.default_value = tuple(v) + ((1.0,) if len(v) == 3 and len(inp.default_value) == 4 else ())
        else:
            inp.default_value = v

    def math(self, op, a, b=None, clamp=False):
        n = self.nodes.new('ShaderNodeMath'); n.operation = op; n.use_clamp = clamp
        self.put(n.inputs[0], a)
        if b is not None:
            self.put(n.inputs[1], b)
        return n.outputs[0]

    def maprange(self, v, fmin, fmax, tmin=0.0, tmax=1.0):
        n = self.nodes.new('ShaderNodeMapRange'); n.clamp = True
        self.put(n.inputs['Value'], v); self.put(n.inputs['From Min'], fmin); self.put(n.inputs['From Max'], fmax)
        self.put(n.inputs['To Min'], tmin); self.put(n.inputs['To Max'], tmax)
        return n.outputs['Result']

    def mix(self, blend, fac, a, b):
        n = self.nodes.new('ShaderNodeMixRGB'); n.blend_type = blend
        self.put(n.inputs[0], fac); self.put(n.inputs[1], a); self.put(n.inputs[2], b)
        return n.outputs[0]

    def noise(self, vec, scale_xyz, w, detail=4.0, rough=0.55):
        m = self.nodes.new('ShaderNodeMapping'); m.vector_type = 'POINT'
        self.put(m.inputs['Vector'], vec); m.inputs['Scale'].default_value = scale_xyz
        n = self.nodes.new('ShaderNodeTexNoise'); n.noise_dimensions = '4D'
        n.inputs['Scale'].default_value = 1.0; n.inputs['Detail'].default_value = detail
        n.inputs['Roughness'].default_value = rough; n.inputs['W'].default_value = w
        self.links.new(m.outputs[0], n.inputs['Vector'])
        return n.outputs['Fac']


def build_recipe(rig, base_color, min_z, max_z, size, seed_w, tone, ao_scale_size=None):
    """Returns the colour socket of the weathered look. base_color: a colour socket (the original texture or a box-projected one)."""
    geo = rig.nodes.new('ShaderNodeNewGeometry')
    sep = rig.nodes.new('ShaderNodeSeparateXYZ'); rig.links.new(geo.outputs['Position'], sep.inputs[0])
    nsep = rig.nodes.new('ShaderNodeSeparateXYZ'); rig.links.new(geo.outputs['Normal'], nsep.inputs[0])
    height = max_z - min_z
    band = max(0.5, min(height * BASE_BAND_FRACTION, BASE_BAND_MAX))

    # masks, all 0..1
    ao = rig.nodes.new('ShaderNodeAmbientOcclusion'); ao.only_local = True; ao.samples = 16
    ao.inputs['Distance'].default_value = max(AO_DISTANCE_MIN, AO_DISTANCE_PER_SIZE * (ao_scale_size or size))
    cavity = rig.maprange(ao.outputs['AO'], AO_CLEAR, AO_FULL)
    aou = rig.nodes.new('ShaderNodeAmbientOcclusion'); aou.only_local = False; aou.samples = 16
    aou.inputs['Distance'].default_value = AO_UP_DISTANCE
    aou.inputs['Normal'].default_value = (0.0, 0.0, 1.0)                  # occlusion looking straight up
    under = rig.maprange(aou.outputs['AO'], 0.99, 0.7)                    # 1 under a ledge or sill, within AO_UP_DISTANCE
    convex = rig.maprange(geo.outputs['Pointiness'], EDGE_RANGE[0], EDGE_RANGE[1])
    concave = rig.maprange(geo.outputs['Pointiness'], 0.47, 0.33)
    cavity = rig.math('MAXIMUM', cavity, rig.math('MULTIPLY', concave, 0.9))
    base_mask = rig.maprange(sep.outputs['Z'], min_z + band, min_z)
    wall = rig.maprange(rig.math('ABSOLUTE', nsep.outputs['Z']), 0.5, 0.2)          # 1 on vertical faces
    high = rig.maprange(sep.outputs['Z'], max_z, min_z, 0.5, 1.0)                    # streaks stronger low down
    streak_n = rig.noise(geo.outputs['Position'], (STREAK_THIN, STREAK_THIN, STREAK_SCALE[2]), seed_w, detail=3.0)
    gate = rig.math('ADD', STREAK_BASE, rig.math('MULTIPLY', under, 1.0 - STREAK_BASE))
    streak = rig.math('MULTIPLY', rig.math('MULTIPLY', rig.maprange(streak_n, STREAK_RANGE[0], STREAK_RANGE[1]), wall), rig.math('MULTIPLY', high, gate))
    b1 = rig.noise(geo.outputs['Position'], (BLOTCH_SCALES[0],) * 3, seed_w + 7.0, detail=3.0)
    b2 = rig.noise(geo.outputs['Position'], (BLOTCH_SCALES[1],) * 3, seed_w + 13.0, detail=5.0)
    blotch = rig.maprange(rig.math('ADD', rig.math('MULTIPLY', b1, 0.6), rig.math('MULTIPLY', b2, 0.4)), 0.38, 0.6)
    patch_n = rig.noise(geo.outputs['Position'], (PATCH_SCALE,) * 3, seed_w + 41.0, detail=2.0)
    patch = rig.maprange(patch_n, PATCH_RANGE[0], PATCH_RANGE[1])
    grit_n = rig.noise(geo.outputs['Position'], (GRIT_SCALE,) * 3, seed_w + 29.0, detail=8.0, rough=0.7)
    # per-block tone: a random value per voronoi cell
    mp = rig.nodes.new('ShaderNodeMapping'); mp.vector_type = 'POINT'
    rig.links.new(geo.outputs['Position'], mp.inputs['Vector']); mp.inputs['Scale'].default_value = (BLOCK_SCALE,) * 3
    mp.inputs['Location'].default_value = (seed_w, seed_w * 0.37, 0.0)
    vor = rig.nodes.new('ShaderNodeTexVoronoi'); vor.feature = 'F1'; vor.voronoi_dimensions = '3D'
    rig.links.new(mp.outputs[0], vor.inputs['Vector'])
    bw = rig.nodes.new('ShaderNodeRGBToBW'); rig.links.new(vor.outputs['Color'], bw.inputs[0])
    block = rig.math('ADD', 1.0, rig.math('MULTIPLY', rig.math('SUBTRACT', bw.outputs[0], 0.5), 2.0 * BLOCK_TONE))
    edge_break = rig.maprange(rig.noise(geo.outputs['Position'], (3.0,) * 3, seed_w + 53.0, detail=4.0), 0.35, 0.6)

    soot = rig.math('ADD', rig.math('MULTIPLY', cavity, SOOT_CAVITY), rig.math('MULTIPLY', base_mask, SOOT_BASE))
    soot = rig.math('ADD', soot, rig.math('MULTIPLY', streak, SOOT_STREAK))
    soot = rig.math('ADD', soot, rig.math('MULTIPLY', blotch, SOOT_BLOTCH))
    soot = rig.math('ADD', soot, rig.math('MULTIPLY', patch, SOOT_PATCH))
    soot = rig.math('MINIMUM', soot, SOOT_MAX)

    # colour
    hs = rig.nodes.new('ShaderNodeHueSaturation'); hs.inputs['Saturation'].default_value = 1.0 - DESATURATE
    rig.put(hs.inputs['Color'], base_color)
    t = 1.0 + TONE_SHIFT * tone
    tint = (COOL_GREY[0] * t, COOL_GREY[1] * t, COOL_GREY[2] * t)
    col = rig.mix('MULTIPLY', 1.0, hs.outputs['Color'], tint)
    col = rig.mix('MULTIPLY', 1.0, col, block)
    grit = rig.math('ADD', 1.0, rig.math('MULTIPLY', rig.math('SUBTRACT', grit_n, 0.5), 2.0 * GRIT_AMOUNT))
    col = rig.mix('MULTIPLY', 1.0, col, grit)
    col = rig.mix('MIX', soot, col, SOOT_COLOR)
    col = rig.mix('MIX', rig.math('MULTIPLY', rig.math('MULTIPLY', convex, edge_break), WORN_EDGE), col, WORN_COLOR)
    return col


def choose_device(scene, log):
    scene.cycles.device = 'CPU'
    try:
        prefs = bpy.context.preferences.addons['cycles'].preferences
        for kind in ('OPTIX', 'CUDA'):
            try:
                prefs.compute_device_type = kind
                prefs.get_devices()
            except Exception:
                continue
            gpus = [d for d in prefs.devices if d.type != 'CPU']
            if gpus:
                for d in prefs.devices:
                    d.use = d.type != 'CPU'
                scene.cycles.device = 'GPU'
                log(f'bake device: GPU ({kind}: {", ".join(d.name for d in gpus)})')
                return
    except Exception as e:
        log(f'GPU setup failed ({e})')
    log('bake device: CPU')


def shrink(img, size):
    if img is None:
        return
    w, h = img.size
    m = max(w, h)
    if m > size:
        k = size / m
        img.scale(max(1, int(w * k)), max(1, int(h * k)))


def process_one(src, dst, size, samples, log):
    t0 = time.time()
    reset_scene()
    objs = import_glb(src)
    ob = join_meshes(objs)
    if not ob.data.uv_layers:
        raise RuntimeError('no UV map')
    mats = [s.material for s in ob.material_slots]
    if not mats or mats[0] is None:
        raise RuntimeError('no material')
    tex_nodes = []
    for m in mats:
        if m:
            tex_nodes += find_base_tex_nodes(m)
    if not tex_nodes:
        raise RuntimeError('no base colour texture')
    orig_img = tex_nodes[0].image
    luma_before = mean_luma(orig_img)
    mn, mx = world_bounds(ob)
    sizes = [mx[i] - mn[i] for i in range(3)]
    stem = os.path.splitext(os.path.basename(src))[0]
    seed_w, tone = file_seed(stem)

    scene = bpy.context.scene
    scene.render.engine = 'CYCLES'
    scene.cycles.samples = samples
    scene.cycles.use_denoising = False
    scene.cycles.bake_type = 'EMIT'
    scene.render.bake.margin = BAKE_MARGIN
    first = not getattr(process_one, 'device_set', False)
    choose_device(scene, log if first else (lambda s: None))
    process_one.device_set = True

    # the bake material
    bake_mat = bpy.data.materials.new('bake'); bake_mat.use_nodes = True
    bake_mat.node_tree.nodes.clear()
    rig = Rig(bake_mat)
    tex = rig.nodes.new('ShaderNodeTexImage'); tex.image = orig_img; tex.interpolation = 'Cubic'
    col = build_recipe(rig, tex.outputs['Color'], mn[2], mx[2], max(sizes), seed_w, tone)
    em = rig.nodes.new('ShaderNodeEmission'); rig.links.new(col, em.inputs['Color'])
    out = rig.nodes.new('ShaderNodeOutputMaterial'); rig.links.new(em.outputs[0], out.inputs['Surface'])
    baked = bpy.data.images.new(stem + '_base', size, size, alpha=False)
    baked.colorspace_settings.name = 'sRGB'
    target = rig.nodes.new('ShaderNodeTexImage'); target.image = baked
    rig.nodes.active = target; target.select = True

    for s in ob.material_slots:
        s.material = bake_mat
    bpy.ops.object.select_all(action='DESELECT'); ob.select_set(True); bpy.context.view_layer.objects.active = ob
    bpy.ops.object.bake(type='EMIT', margin=BAKE_MARGIN, use_clear=True)
    luma_after = correct_tone(baked)

    # back to the original materials, base colour now the baked image
    for s, m in zip(ob.material_slots, mats):
        s.material = m
    for n in tex_nodes:
        n.image = baked
    for m in mats:
        if m is None:
            continue
        for n in m.node_tree.nodes:
            if n.type == 'TEX_IMAGE' and n.image is not None and n.image is not baked:
                shrink(n.image, size)
    bpy.data.materials.remove(bake_mat)
    for m in mats:                       # matte, no sheen (owner 2026-11-06): roughness 1, metallic 0, the metallic-roughness texture dropped
        if m is None:
            continue
        for n in list(m.node_tree.nodes):
            if n.type == 'BSDF_PRINCIPLED':
                for name, val in (('Roughness', MATTE_ROUGHNESS), ('Metallic', 0.0)):
                    for l in list(n.inputs[name].links):
                        m.node_tree.links.remove(l)
                    n.inputs[name].default_value = val
                for name in ('Specular IOR Level', 'Coat Weight', 'Sheen Weight'):
                    if name in n.inputs:
                        n.inputs[name].default_value = 0.0
        for n in list(m.node_tree.nodes):    # image nodes left without a user (the metallic-roughness one and its separator)
            if n.type in ('SEPARATE_COLOR', 'SEPARATE_RGB') or (n.type == 'TEX_IMAGE' and not any(o.links for o in n.outputs)):
                m.node_tree.nodes.remove(n)

    os.makedirs(os.path.dirname(dst), exist_ok=True)
    bpy.ops.object.select_all(action='DESELECT'); ob.select_set(True); bpy.context.view_layer.objects.active = ob
    bpy.ops.export_scene.gltf(filepath=dst, export_format='GLB', use_selection=True, export_image_format='JPEG',
                              export_jpeg_quality=90, export_yup=True)
    dt = time.time() - t0
    log(f'{stem}: {dt:.1f}s luma {luma_before:.3f} -> {luma_after:.3f} ({luma_after / max(luma_before, 1e-6) * 100:.0f}%) size {sizes[0]:.1f}x{sizes[1]:.1f}x{sizes[2]:.1f}')
    return luma_before, luma_after


def correct_tone(img):
    """Pulls a copy that came out paler, darker or warmer than the median model back toward the median grey; returns the mean luminance."""
    n = img.size[0] * img.size[1]
    arr = np.empty(n * 4, dtype=np.float32); img.pixels.foreach_get(arr); px = arr.reshape(-1, 4)
    changed = False
    ro_b = float(px[:, 0].mean() / max(px[:, 2].mean(), 1e-6))
    if ro_b > MAX_R_OVER_B:                       # warm: cool it (red down, green half way)
        k = TARGET_R_OVER_B / ro_b
        px[:, 0] *= k; px[:, 1] *= 0.5 + 0.5 * k
        changed = True
    luma = float((0.2126 * px[:, 0] + 0.7152 * px[:, 1] + 0.0722 * px[:, 2]).mean())
    gain = 1.0
    if luma > TARGET_LUMA * LUMA_HIGH:
        gain = TARGET_LUMA * LUMA_HIGH / luma
    elif luma < TARGET_LUMA * LUMA_LOW:
        gain = TARGET_LUMA * LUMA_LOW / luma
    if abs(gain - 1.0) > 1e-3:
        px[:, :3] *= gain
        changed = True
    if changed:
        np.clip(px, 0.0, 1.0, out=px)
        img.pixels.foreach_set(px.ravel()); img.update()
        luma = float((0.2126 * px[:, 0] + 0.7152 * px[:, 1] + 0.0722 * px[:, 2]).mean())
    return luma


def render_glb(path, png, res=512):
    """Small headless preview: the model, a neutral grey world and one sun, same camera for every model."""
    import mathutils
    reset_scene()
    objs = import_glb(path)
    ob = join_meshes(objs)
    mn, mx = world_bounds(ob)
    c = mathutils.Vector([(mn[i] + mx[i]) / 2 for i in range(3)])
    r = max(mx[i] - mn[i] for i in range(3))
    scene = bpy.context.scene
    cam_d = bpy.data.cameras.new('cam'); cam_d.lens = 40
    cam = bpy.data.objects.new('cam', cam_d); scene.collection.objects.link(cam)
    d = mathutils.Vector((1.0, -1.35, 0.55)).normalized()
    cam.location = c + d * r * 1.2
    cam.rotation_euler = (c - cam.location).to_track_quat('-Z', 'Y').to_euler()
    scene.camera = cam
    sun_d = bpy.data.lights.new('sun', 'SUN'); sun_d.energy = 3.0
    sun = bpy.data.objects.new('sun', sun_d); scene.collection.objects.link(sun)
    sun.rotation_euler = (math.radians(50), 0, math.radians(35))
    world = bpy.data.worlds.new('w'); world.use_nodes = True
    world.node_tree.nodes['Background'].inputs[0].default_value = (0.35, 0.36, 0.38, 1)
    world.node_tree.nodes['Background'].inputs[1].default_value = 0.8
    scene.world = world
    scene.render.resolution_x = scene.render.resolution_y = res
    scene.render.filepath = png
    scene.view_settings.view_transform = 'Standard'
    for eng in ('BLENDER_EEVEE_NEXT', 'BLENDER_WORKBENCH'):
        try:
            scene.render.engine = eng
            break
        except TypeError:
            continue
    if scene.render.engine == 'BLENDER_WORKBENCH':
        scene.display.shading.light = 'STUDIO'; scene.display.shading.color_type = 'TEXTURE'
    os.makedirs(os.path.dirname(png), exist_ok=True)
    bpy.ops.render.render(write_still=True)


def main():
    a = args_after_dashes()
    if not a['src'] or not a['out']:
        print('usage: --src <dir> --out <dir> [--only A,B] [--size N] [--samples N] [--preview dir]'); return
    src_root = os.path.abspath(a['src']); out_root = os.path.abspath(a['out'])
    only = [s.strip().lower() for s in a['only'].split(',')] if a['only'] else None
    files = []
    for root, _, names in os.walk(src_root):
        for n in sorted(names):
            if not n.lower().endswith('.glb'):
                continue
            rel = os.path.relpath(os.path.join(root, n), src_root).replace('\\', '/')
            if only and not any(os.path.splitext(n)[0].lower().startswith(o) or o in rel.lower() for o in only):
                continue
            files.append(rel)
    files.sort()
    def log(s):
        print('[bake] ' + s, flush=True)
    log(f'{len(files)} candidate files')
    sums = []
    for rel in files:
        srcp = os.path.join(src_root, rel)
        if not has_base_texture(srcp):
            log(f'{rel}: no base colour texture, skipped'); continue
        dstp = os.path.join(out_root, rel)
        try:
            sums.append(process_one(srcp, dstp, a['size'], a['samples'], log))
            if a['preview']:
                stem = os.path.splitext(os.path.basename(rel))[0]
                render_glb(srcp, os.path.join(a['preview'], stem + '_before.png'))
                render_glb(dstp, os.path.join(a['preview'], stem + '_after.png'))
        except Exception:
            log(f'{rel}: FAILED'); traceback.print_exc()
    if sums:
        b = sum(s[0] for s in sums) / len(sums); af = sum(s[1] for s in sums) / len(sums)
        log(f'BATCH mean luminance {b:.3f} -> {af:.3f} ({af / b * 100:.0f}%) over {len(sums)} files')


if __name__ == '__main__':
    main()
