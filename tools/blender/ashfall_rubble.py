# ashfall_rubble.py - four small rubble piles as separate props (GPL-3.0, sm-reimpl; our code).
#
#   blender.exe --background --factory-startup --python tools/blender/ashfall_rubble.py -- \
#       --src <kit source root> --out <processed root> [--wall completed_meshy_assets/models/AC-001.glb] [--size 1024] [--samples 16]
#
# Writes <out>/rubble/AR-001..AR-004.glb: low piles of broken, bevelled stone blocks (1.5 to 4 m across, 0.4 to 1.2 m high,
# at most about 3k triangles), UV-unwrapped, origin at the base centre, textured by baking the kit wall's stone colour (box
# projected) through the same grime recipe as ashfall_bake.py. Prints each pile's bounding size.
import bpy, bmesh, sys, os, math, random, time
import mathutils

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ashfall_bake as ab

# name, radius (m), max height (m), block count, seed
PILES = [('AR-001', 1.0, 0.45, 16, 11), ('AR-002', 1.4, 0.8, 24, 23), ('AR-003', 1.6, 0.95, 28, 37), ('AR-004', 0.95, 0.5, 14, 51)]
TRI_LIMIT = 3000


def make_block(rng, size):
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    sx, sy, sz = (size * rng.uniform(0.6, 1.5), size * rng.uniform(0.5, 1.3), size * rng.uniform(0.3, 0.8))
    for v in bm.verts:
        v.co.x *= sx; v.co.y *= sy; v.co.z *= sz
    for _ in range(rng.randint(1, 3)):                     # chip corners off with random planes
        n = mathutils.Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-1, 1))).normalized()
        corner = mathutils.Vector((rng.choice((-1, 1)) * sx / 2, rng.choice((-1, 1)) * sy / 2, rng.choice((-1, 1)) * sz / 2))
        if n.dot(corner) < 0:
            n = -n
        d = rng.uniform(0.35, 0.8) * n.dot(corner)
        geom = bm.verts[:] + bm.edges[:] + bm.faces[:]
        bmesh.ops.bisect_plane(bm, geom=geom, dist=0.0001, plane_co=n * d, plane_no=n, clear_outer=True, use_snap_center=False)
        bmesh.ops.holes_fill(bm, edges=[e for e in bm.edges if e.is_boundary])
    for v in bm.verts:                                      # roughen
        v.co += mathutils.Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-1, 1))) * size * 0.03
    bmesh.ops.triangulate(bm, faces=bm.faces[:])
    me = bpy.data.meshes.new('blk'); bm.to_mesh(me); bm.free()
    ob = bpy.data.objects.new('blk', me); bpy.context.scene.collection.objects.link(ob)
    return ob


def build_pile(name, radius, hmax, count, seed):
    rng = random.Random(seed)
    blocks = []
    for i in range(count):
        a = rng.uniform(0, math.tau)
        d = radius * math.sqrt(rng.random()) * 0.9
        profile = hmax * max(0.0, 1.0 - (d / radius) ** 2)
        size = rng.uniform(0.35, 0.8) * (0.6 + 0.6 * (1 - d / radius)) * (hmax / 0.8) ** 0.5
        b = make_block(rng, size)
        b.rotation_euler = (rng.uniform(-0.5, 0.5), rng.uniform(-0.5, 0.5), rng.uniform(0, math.tau))
        bpy.context.view_layer.update()
        mn, mx = ab.world_bounds(b)
        half = (mx[2] - mn[2]) / 2
        zc = max(half * 0.85, profile * rng.uniform(0.55, 0.9))
        b.location = (math.cos(a) * d * 1.25, math.sin(a) * d, zc)         # a slightly oval footprint
        blocks.append(b)
    bpy.ops.object.select_all(action='DESELECT')
    for b in blocks:
        b.select_set(True)
    bpy.context.view_layer.objects.active = blocks[0]
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    bpy.ops.object.join()
    ob = bpy.context.view_layer.objects.active
    # bevel, keep the triangle count in check
    mod = ob.modifiers.new('bev', 'BEVEL'); mod.width = 0.025; mod.segments = 1; mod.limit_method = 'ANGLE'
    bpy.ops.object.modifier_apply(modifier='bev')
    tris = sum(len(p.vertices) - 2 for p in ob.data.polygons)
    if tris > TRI_LIMIT:
        dec = ob.modifiers.new('dec', 'DECIMATE'); dec.ratio = TRI_LIMIT * 0.95 / tris
        bpy.ops.object.modifier_apply(modifier='dec')
    bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=0.004)
    bpy.ops.object.mode_set(mode='OBJECT')
    # origin at the base centre, resting on z = 0
    mn, mx = ab.world_bounds(ob)
    off = mathutils.Vector(((mn[0] + mx[0]) / 2, (mn[1] + mx[1]) / 2, mn[2]))
    for v in ob.data.vertices:
        v.co -= off
    ob.name = name
    for p in ob.data.polygons:
        p.use_smooth = False
    return ob


def export_pile(ob, wall_img, dst, size, samples, log):
    mn, mx = ab.world_bounds(ob)
    sz = [mx[i] - mn[i] for i in range(3)]
    scene = bpy.context.scene
    scene.render.engine = 'CYCLES'; scene.cycles.samples = samples; scene.cycles.use_denoising = False
    scene.render.bake.margin = ab.BAKE_MARGIN
    ab.choose_device(scene, lambda s: None)
    mat = bpy.data.materials.new('rubble'); mat.use_nodes = True
    mat.node_tree.nodes.clear()
    rig = ab.Rig(mat)
    tc = rig.nodes.new('ShaderNodeTexCoord')
    mp = rig.nodes.new('ShaderNodeMapping'); mp.inputs['Scale'].default_value = (0.33, 0.33, 0.33)
    rig.links.new(tc.outputs['Object'], mp.inputs['Vector'])
    tex = rig.nodes.new('ShaderNodeTexImage'); tex.image = wall_img; tex.projection = 'BOX'; tex.projection_blend = 0.25
    tex.interpolation = 'Cubic'
    rig.links.new(mp.outputs[0], tex.inputs['Vector'])
    seed_w, tone = ab.file_seed(ob.name)
    col = ab.build_recipe(rig, tex.outputs['Color'], 0.0, sz[2], max(sz), seed_w, tone)
    em = rig.nodes.new('ShaderNodeEmission'); rig.links.new(col, em.inputs['Color'])
    out = rig.nodes.new('ShaderNodeOutputMaterial'); rig.links.new(em.outputs[0], out.inputs['Surface'])
    baked = bpy.data.images.new(ob.name + '_base', size, size, alpha=False)
    target = rig.nodes.new('ShaderNodeTexImage'); target.image = baked
    rig.nodes.active = target; target.select = True
    ob.data.materials.clear(); ob.data.materials.append(mat)
    bpy.ops.object.select_all(action='DESELECT'); ob.select_set(True); bpy.context.view_layer.objects.active = ob
    scene.cycles.bake_type = 'EMIT'
    bpy.ops.object.bake(type='EMIT', margin=ab.BAKE_MARGIN, use_clear=True)
    # final material: baked base colour, rough matte stone
    nt = mat.node_tree; nt.nodes.clear()
    pr = nt.nodes.new('ShaderNodeBsdfPrincipled'); pr.inputs['Roughness'].default_value = 0.92; pr.inputs['Metallic'].default_value = 0.0
    im = nt.nodes.new('ShaderNodeTexImage'); im.image = baked
    nt.links.new(im.outputs['Color'], pr.inputs['Base Color'])
    o = nt.nodes.new('ShaderNodeOutputMaterial'); nt.links.new(pr.outputs[0], o.inputs['Surface'])
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    bpy.ops.export_scene.gltf(filepath=dst, export_format='GLB', use_selection=True, export_image_format='JPEG', export_jpeg_quality=90, export_yup=True)
    tris = sum(len(p.vertices) - 2 for p in ob.data.polygons)
    log(f'{ob.name}: bounds {sz[0]:.2f} x {sz[1]:.2f} x {sz[2]:.2f} m (x, y, height), {tris} triangles')


def main():
    a = ab.args_after_dashes()
    wall_rel = 'completed_meshy_assets/models/AC-001.glb'
    if '--wall' in sys.argv:
        wall_rel = sys.argv[sys.argv.index('--wall') + 1]
    src = os.path.abspath(a['src']); out = os.path.abspath(a['out'])
    # the owner's originals are read only: refuse an output folder that is the source or inside it (review)
    if os.path.commonpath([os.path.normcase(src), os.path.normcase(out)]) == os.path.normcase(src):
        sys.exit('--out must not be the source folder or inside it: ' + out)
    log = lambda s: print('[rubble] ' + s, flush=True)
    for name, radius, hmax, count, seed in PILES:
        ab.reset_scene()
        # the wall's stone colour (source, ungrimed: the recipe adds the grime once)
        objs = ab.import_glb(os.path.join(src, wall_rel))
        wall_img = None
        for o in objs:
            if o.type == 'MESH' and o.material_slots and o.material_slots[0].material:
                nodes = ab.find_base_tex_nodes(o.material_slots[0].material)
                if nodes:
                    wall_img = nodes[0].image; break
        if wall_img is None:
            raise RuntimeError('wall has no base colour texture')
        for o in objs:
            bpy.data.objects.remove(o, do_unlink=True)
        ob = build_pile(name, radius, hmax, count, seed)
        export_pile(ob, wall_img, os.path.join(out, 'rubble', name + '.glb'), a['size'], a['samples'], log)


if __name__ == '__main__':
    main()
