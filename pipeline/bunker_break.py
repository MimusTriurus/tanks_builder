"""Breaking the hex capon: HM's concrete-piercing bomb comes in through the roof.

The rule is the stand's (godot docs/wall.md, "Капонир"; WallRig.Shatter): the
piercer goes off *inside*; every wall slab is thrown out along its own outer
normal at CpSpeed (3.2 at full strength) plus a fifth of the shot's direction
and a quarter of lift; the roof is let go and comes down on what was under it.
What is new here is the way in: the bomb arrives steeply from above, punches
the roof's middle slab and knocks the hatch off, and the fuse fires three
frames later.

Everything lives in its own Blender scene, `Bunker.Break`, so the intact
bunker and the reference tanks in the main scene are left as they are.

**Velocities are given exactly, not shoved.** A body that is kinematic over
its last frame and then let go keeps the velocity of that last frame's move
(measured: 0.2 per frame in, 0.2 per frame out). So every chunk is keyframed
at rest, then at rest + v/fps one frame later, and released - the push is
per chunk, which is also how the stand's Shatter does it.

**The chunks are the bunker from frame one.** Each concrete block is split by
a few planes into big convex chunks. The bevel goes only on the block's own
edges (bevel weight), the texture reads the rest position (attribute `rest`)
instead of object space, and the slits are cut into the chunks and applied -
so at rest the chunks look exactly like the intact model, the cracks are
invisible hairlines, and a broken face is a fresh light surface with a sharp
edge.
"""

import math
import random

import bmesh
import bpy
import numpy as np
from mathutils import Euler, Matrix, Vector

import bunker as sq
import bunker_hex as bh

SCENE = "Bunker.Break"
PREFIX = "Debris"

# Turret roofs without the aerials, and where each tank stands in the capon
# (Tank3D scale, measured against the walls: bunker_hex's fit test).
TURRET_TOP = {"LTR": 0.725, "MTR": 0.899, "HTR": 0.887, "TDR": 0.782, "HMR": 0.744}
PARK = {"LTR": -0.141, "MTR": -0.081, "HTR": -0.104, "TDR": 0.052, "HMR": 0.046}

CONFIG = {
    "seed": 7,
    # no chunk longer than this along its long axis, R: concrete breaks big.
    # 0.40 / 0.20 made 183 pieces, the hit slab alone 43 - gravel, not slabs
    "chunk": 0.55,
    "core_chunk": 0.32,   # the slab the bomb hits breaks finer
    "fps": 24,
    "impact": 21,         # the bomb meets the roof
    "blast": 24,          # the fuse: it goes off inside
    "frames": 300,
    # the stand's clean-up (WallRig.Linger / Crumble): a piece lies untouched
    # two seconds after it came to rest, then fades out over four; the clock
    # runs on time spent still and only adds
    "linger": 48,
    "crumble": 96,
    "still_lin": 0.004,   # R per frame
    "still_ang": 0.012,   # rad per frame
    "tank": "MTR",        # a tank parked inside (Models/ at board scale), or None
    "bomb_stop": 0.665,   # where the bomb's origin stops inside an empty box
    "burst_at": (0.0, 0.0, 0.45),
    "cp_speed": 3.2,      # WallRig.CpSpeed at strength 1
    "shot_share": 0.20,
    "lift_share": 0.25,
    "topple": 3.5,        # rad/s a wall turns outward about its foot
    "spread": 1.4,        # 1/s: sideways speed per unit off the wall's middle
    "jitter": 0.22,
    "style": "collapse",
    "fence": True,
    "fence_reach": 1.005,
    "friction": 0.82, "restitution": 0.05, "damping": (0.10, 0.35),
    "density": 2400.0,    # concrete, per cubic unit
    # where the bomb comes from: steep, from the front-right of the camera
    "shot_dir": (-0.30, 0.22, -1.0),
    "shot_speed": 0.36,   # R per frame in the air
}


# Two ways down. `burst` is the stand's Shatter as it is written: walls out
# along their normals, toppling about the outer foot - and a wall that stands
# on the cell edge and falls outward can only land on the next cell. `collapse`
# keeps the heap on its own hex: the roof heaves and drops in, the walls fold
# inward about their inner foot (they already lean in), the bedded course stays
# where it is as a kerb, and the chunks are bigger and stop sooner.
STYLES = {
    "burst": {},
    # Measured on seeds 7, 23, 42, 11, as mass whose centre lies on the cell:
    # burst 31 %; collapse as first tried (0.65 chunks, heave 0.5, fold 1.6)
    # 60-65 %; gentler and bigger 73-93 %; the same with the fence 100 %,
    # worst overhang 3 mm (the collision margin), 11-13 chunks resting on it.
    # A kinematic kerb kept a few chunks shivering on it to the last frame
    # and once threw one under the floor; the free one settles.
    "collapse": {
        "chunk": 0.85, "core_chunk": 0.42,   # ~41 chunks
        # with the fence in place, 0.9 / 0 / 0.2 and 1.4 / 0.3 / 0.3 held the
        # same (0 off the cell, 10-13 on the fence); the livelier one reads
        "wall_in": 0.3,           # R/s inward at the foot of the main course
        "topple_in": 1.4,         # rad/s the top folds in, about the inner foot
        "wall_delays": (2, 5),    # frames after the blast, alternate walls: two
                                  # neighbours folding in together meet at the mitre
        "ring_heave": 0.3,        # R/s: the roof starts, then drops in
        "hatch_up": 2.6,
        "plinth_static": False,
        "friction": 1.0, "restitution": 0.0, "damping": (0.35, 0.9),
    },
}


# ---------------------------------------------------------------- chunks

def _pca_axis(V):
    c = V.mean(0)
    w, U = np.linalg.eigh(np.cov((V - c).T))
    a = U[:, -1]
    t = (V - c) @ a
    return c, a, t.max() - t.min()


def shatter(P, rng, max_len, depth=0):
    """Split a convex block on its long axis, a bit off-centre and tilted,
    until no piece is longer than `max_len`."""
    V = bh.verts_of(P)
    c, a, length = _pca_axis(V)
    if length <= max_len or depth > 5:
        return [P]
    tilt = np.array([rng.uniform(-1, 1) for _ in range(3)]) * 0.35
    n = a + tilt
    n /= np.linalg.norm(n)
    point = c + a * length * rng.uniform(-0.14, 0.14)
    lo, hi = bh.split(P, bh.H(n, float(n @ point)))
    out = []
    for q in (lo, hi):
        if bh.solid(q, thin=0.02) is not None:
            out += shatter(q, rng, max_len, depth + 1)
    return out or [P]


def slit_boxes(cfg):
    z0, z1 = cfg["loop_z"]
    w = cfg["loop_w"]
    boxes = []
    for k, s in bh.SLITS:
        n, t = np.array(bh._n(k) + (0.0,)), np.array(bh._t(k) + (0.0,))
        boxes.append([bh.H(t, s + w), bh.H(-t, -(s - w)), bh.H((0, 0, 1), z1), bh.H((0, 0, -1), -z0),
                      bh.H(n, bh.r_out(cfg, z0) + 0.15), bh.H(-n, -(bh.r_in(cfg, z1) - 0.15))])
    return boxes


def chunk_plan(cfg, brk):
    """[(name, planes, n_original_planes, kind, face)] for every chunk."""
    rng = random.Random(brk["seed"])
    plan = []
    for name, P in bh.all_pieces(cfg):
        if name == "Cap.Core":
            kind, lim = "core", brk["core_chunk"]
        elif name.startswith("Cap"):
            kind, lim = "ring", brk["chunk"]
        else:
            kind, lim = ("plinth" if ".P" in name else "wall"), brk["chunk"]
        face = int(name[1]) if name.startswith("W") else (int(name[5]) if kind == "ring" else None)
        for i, q in enumerate(shatter(P, rng, lim)):
            plan.append(("%s.%s.%d" % (PREFIX, name, i), q, len(P), kind, face))
    return plan


# ---------------------------------------------------------------- materials

def debris_material():
    """The bunker's concrete, read at the rest position so a chunk carries
    its texture away with it and two chunks at rest show no seam."""
    name = "Bunker.Debris"
    old = bpy.data.materials.get(name)
    if old is not None:
        bpy.data.materials.remove(old)
    mat = sq.concrete().copy()
    mat.name = name
    nt = mat.node_tree
    tc = [n for n in nt.nodes if n.type == "TEX_COORD"][0]
    at = nt.nodes.new("ShaderNodeAttribute")
    at.attribute_type = "GEOMETRY"
    at.attribute_name = "rest"
    for l in list(nt.links):
        if l.from_node is tc and l.from_socket.name == "Object":
            nt.links.new(at.outputs["Vector"], l.to_socket)
    nt.nodes.remove(tc)
    return _fadeable(mat)


def _fadeable(mat):
    """Alpha from the object's colour, so each chunk fades on its own keys."""
    nt = mat.node_tree
    bsdf = nt.nodes.get("Principled BSDF")
    oi = nt.nodes.new("ShaderNodeObjectInfo")
    nt.links.new(oi.outputs["Alpha"], bsdf.inputs["Alpha"])
    if hasattr(mat, "surface_render_method"):
        mat.surface_render_method = "DITHERED"
    if hasattr(mat, "use_transparent_shadow"):
        mat.use_transparent_shadow = True
    return mat


def _debris_copy(src, name):
    old = bpy.data.materials.get(name)
    if old is not None:
        bpy.data.materials.remove(old)
    mat = src.copy()
    mat.name = name
    return _fadeable(mat)


def fresh_material():
    """A broken face: light, dry, no weather on it."""
    name = "Bunker.Fresh"
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    for n in list(nt.nodes):
        if n.type not in ("BSDF_PRINCIPLED", "OUTPUT_MATERIAL"):
            nt.nodes.remove(n)
    b = nt.nodes["Principled BSDF"]
    at = nt.nodes.new("ShaderNodeAttribute")
    at.attribute_name = "rest"
    nz = nt.nodes.new("ShaderNodeTexNoise")
    nz.inputs["Scale"].default_value = 22.0
    nz.inputs["Detail"].default_value = 8.0
    nt.links.new(at.outputs["Vector"], nz.inputs["Vector"])
    rp = nt.nodes.new("ShaderNodeValToRGB")
    rp.color_ramp.elements[0].color = (0.30, 0.285, 0.255, 1)
    rp.color_ramp.elements[1].color = (0.50, 0.48, 0.44, 1)
    nt.links.new(nz.outputs["Fac"], rp.inputs["Fac"])
    nt.links.new(rp.outputs["Color"], b.inputs["Base Color"])
    bump = nt.nodes.new("ShaderNodeBump")
    bump.inputs["Strength"].default_value = 0.5
    bump.inputs["Distance"].default_value = 0.02
    nt.links.new(nz.outputs["Fac"], bump.inputs["Height"])
    nt.links.new(bump.outputs["Normal"], b.inputs["Normal"])
    b.inputs["Roughness"].default_value = 1.0
    if not any(n.type == "OBJECT_INFO" for n in nt.nodes):
        _fadeable(mat)
    return mat


# ---------------------------------------------------------------- meshes

def _chunk_mesh(name, P, n_orig, lug_geo=None):
    """Hull of the chunk, faces sorted into original (slot 0) and broken
    (slot 1), bevel weight on edges between two original faces, `rest`
    holding the world rest position. Vertices are about the centroid."""
    V = bh.verts_of(P)
    c = V.mean(0)
    bm = bmesh.new()
    bh._add_hull(bm, V)
    bmesh.ops.dissolve_limit(bm, angle_limit=math.radians(0.5), verts=bm.verts, edges=bm.edges)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    N = np.array([p[0] for p in P])
    D = np.array([p[1] for p in P])
    orig = {}
    for f in bm.faces:
        n = np.array(f.normal)
        d = float(n @ np.array(f.verts[0].co))
        hit = np.where((N @ n > 0.9999) & (np.abs(D - d) < 1e-4))[0]
        orig[f] = bool(len(hit)) and int(hit.min()) < n_orig
        f.material_index = 0 if orig[f] else 1
    bw = bm.edges.layers.float.get("bevel_weight_edge") or bm.edges.layers.float.new("bevel_weight_edge")
    for e in bm.edges:
        fs = e.link_faces
        e[bw] = 1.0 if len(fs) == 2 and orig[fs[0]] and orig[fs[1]] else 0.0
    if lug_geo is not None:
        lug_geo(bm)            # lugs ride on the chunk they stand on
    rest = bm.verts.layers.float_vector.new("rest")
    for v in bm.verts:
        v[rest] = v.co.copy()
        v.co = v.co - Vector(c)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    for p in me.polygons:
        p.use_smooth = False
    return me, Vector(c)


def _subtree(o):
    out = [o]
    for c in o.children:
        out += _subtree(c)
    return out


def _world_verts(o):
    me = o.data
    co = np.empty(len(me.vertices) * 3)
    me.vertices.foreach_get("co", co)
    M = np.array(o.matrix_world)
    return co.reshape(-1, 3) @ M[:3, :3].T + M[:3, 3]


def _apply_modifiers(ob):
    dg = bpy.context.evaluated_depsgraph_get()
    me = bpy.data.meshes.new_from_object(ob.evaluated_get(dg), preserve_all_data_layers=True, depsgraph=dg)
    old = ob.data
    ob.modifiers.clear()
    ob.data = me
    bpy.data.meshes.remove(old)


def bomb_mesh(name="Bunker.Bomb"):
    """Sturmtiger's 380 mm rocket mortar, stylised: a fat cylinder, a blunt
    rounded nose, the ring of nozzles at the base. Points along -Z."""
    me = bpy.data.meshes.get(name) or bpy.data.meshes.new(name)
    r, L = 0.072, 0.30
    bm = bmesh.new()
    body = bmesh.ops.create_cone(bm, cap_ends=True, segments=24, radius1=r, radius2=r, depth=L * 0.62)
    bmesh.ops.translate(bm, verts=body["verts"], vec=(0, 0, L * 0.12))
    nose = bmesh.ops.create_cone(bm, cap_ends=True, segments=24, radius1=r, radius2=r * 0.45, depth=L * 0.16)
    bmesh.ops.translate(bm, verts=nose["verts"], vec=(0, 0, -L * 0.27))
    tip = bmesh.ops.create_cone(bm, cap_ends=True, segments=24, radius1=r * 0.45, radius2=r * 0.12, depth=L * 0.06)
    bmesh.ops.translate(bm, verts=tip["verts"], vec=(0, 0, -L * 0.38))
    band = bmesh.ops.create_cone(bm, cap_ends=True, segments=24, radius1=r * 1.06, radius2=r * 1.06, depth=L * 0.05)
    bmesh.ops.translate(bm, verts=band["verts"], vec=(0, 0, L * 0.40))
    bm.to_mesh(me)
    bm.free()
    return me


# ---------------------------------------------------------------- scene

def _scene():
    sc = bpy.data.scenes.get(SCENE)
    if sc is None:
        sc = bpy.data.scenes.new(SCENE)
    main = bpy.data.scenes["Scene"]
    sc.world = main.world
    sc.render.fps = CONFIG["fps"]
    sc.render.engine = "BLENDER_EEVEE"
    sc.view_settings.view_transform = "Standard"
    return sc


def _wipe(sc):
    """Everything this scene made goes; what it borrows from the main scene
    (the ground, the cell) is only unlinked."""
    for ob in list(sc.collection.all_objects):
        if any(s is not sc for s in ob.users_scene):
            for c in list(ob.users_collection):
                if c is sc.collection or c.name in sc.collection.children:
                    c.objects.unlink(ob)
        else:
            bpy.data.objects.remove(ob, do_unlink=True)
    for c in list(sc.collection.children):
        bpy.data.collections.remove(c)
    for me in list(bpy.data.meshes):
        if me.users == 0 and me.name.startswith((PREFIX, "_break", "Bunker.Bomb", "Bunker.Flash", "_lugtmp")):
            bpy.data.meshes.remove(me)
    # every rebuild left one action per chunk behind: 4009 of them after a day
    for act in list(bpy.data.actions):
        if act.users == 0 and act.name.startswith((PREFIX, "Bunker.Bomb", "Bunker.Flash")):
            bpy.data.actions.remove(act)


def _key_release(ob, frame, v, w, fps):
    """At rest through `frame - 1`, moved by one frame's worth of (v, w) at
    `frame`, let go at `frame + 1`: Bullet carries the last frame's motion on."""
    rb = ob.rigid_body
    rest_loc, rest_rot = ob.location.copy(), ob.rotation_euler.copy()
    ob.keyframe_insert("location", frame=1)
    ob.keyframe_insert("rotation_euler", frame=1)
    ob.keyframe_insert("location", frame=frame - 1)
    ob.keyframe_insert("rotation_euler", frame=frame - 1)
    ob.location = rest_loc + Vector(v) / fps
    rot = (Matrix.Rotation(np.linalg.norm(w) / fps, 3, Vector(w).normalized()) if np.linalg.norm(w) > 1e-6
           else Matrix.Identity(3)) @ rest_rot.to_matrix()
    ob.rotation_euler = rot.to_euler("XYZ", rest_rot)
    ob.keyframe_insert("location", frame=frame)
    ob.keyframe_insert("rotation_euler", frame=frame)
    rb.kinematic = True
    rb.keyframe_insert("kinematic", frame=1)
    rb.keyframe_insert("kinematic", frame=frame)
    rb.kinematic = False
    rb.keyframe_insert("kinematic", frame=frame + 1)
    ob.location, ob.rotation_euler = rest_loc, rest_rot


def resolve(brk=None):
    brk = dict(brk or {})
    style = brk.get("style", CONFIG["style"])
    out = dict(CONFIG)
    out.update(STYLES[style])
    out.update(brk)
    return out


def build(cfg=None, brk=None):
    cfg = dict(bh.CONFIG, **(cfg or {}))
    brk = resolve(brk)
    rng = random.Random(brk["seed"] + 1)
    fps = brk["fps"]
    main_window_scene = bpy.context.window.scene
    sc = _scene()
    bpy.context.window.scene = sc
    try:
        # Free the old bake before anything else. Any frame change while it is
        # there writes its transforms back onto the bodies by index, and the
        # new floor, fence and tank proxies took the old debris' places: the
        # rubble came to rest on a floor at z = 1 with the fence at the origin.
        _free(sc)
        _wipe(sc)
        col = bpy.data.collections.new("Bunker.Debris")
        sc.collection.children.link(col)
        if sc.rigidbody_world is None:
            bpy.ops.rigidbody.world_add()
        rbw = sc.rigidbody_world
        rbw.collection = bpy.data.collections.get("Bunker.Break.RBW") or bpy.data.collections.new("Bunker.Break.RBW")
        rbw.substeps_per_frame = 10
        rbw.solver_iterations = 20
        # Bullet pushes interpenetrating bodies apart by adding velocity;
        # split impulse does it without. Without it the same seed came out
        # clean on one run and threw a plinth chunk under the floor on another
        rbw.use_split_impulse = True
        sc.frame_start, sc.frame_end = 1, brk["frames"]
        rbw.point_cache.frame_start, rbw.point_cache.frame_end = 1, brk["frames"]

        # the debris has its own copies: the intact bunker's materials are
        # shared with the main scene and stay opaque
        mats = [debris_material(), fresh_material(),
                _debris_copy(sq.metal(), "Bunker.Debris.Metal"), _debris_copy(sq.shade(), "Bunker.Debris.Shade")]

        # lugs: which ring chunk each stands on
        plan = chunk_plan(cfg, brk)
        lug_at = {}
        z = cfg["cap_top"]
        rad = (cfg["core"] + bh.r_out(cfg, z) + cfg["lip"]) / 2.0 + 0.01
        for k in bh.LUG_FACES:
            p = np.array(bh._n(k) + (0.0,)) * rad + np.array((0, 0, z - 0.01))
            for name, P, n0, kind, face in plan:
                if kind == "ring" and bh._inside(P, p):
                    lug_at.setdefault(name, []).append(k)
                    break

        def lug_builder(ks):
            def f(bm):
                tmp = bmesh.new()
                bh.lugs(cfg, tmp)
                # keep only the lugs of faces ks: lugs() makes 4 boxes per lug, in LUG_FACES order
                keep = []
                for i, k in enumerate(bh.LUG_FACES):
                    if k in ks:
                        keep.append(i)
                verts = list(tmp.verts)
                per = len(verts) // len(bh.LUG_FACES)      # lugs() builds them in LUG_FACES order
                me_tmp = bpy.data.meshes.new("_lugtmp")
                bmesh.ops.delete(tmp, geom=[v for i, v in enumerate(verts) if i // per not in keep],
                                 context="VERTS")
                tmp.to_mesh(me_tmp)
                tmp.free()
                # from_mesh does not append at the end of bm.faces: "the last N"
                # painted the chunk's own roof face metal and left the lugs
                # concrete. The lugs are the faces that were not there before.
                before = set(bm.faces)
                bm.from_mesh(me_tmp)
                for fc in bm.faces:
                    if fc not in before:
                        fc.material_index = 2
                bpy.data.meshes.remove(me_tmp)
            return f

        boxes = slit_boxes(cfg)
        cut_me = bpy.data.meshes.new("_break_cut")
        cbm = bmesh.new()
        bh.slit_cutters(cfg, cbm)
        bmesh.ops.recalc_face_normals(cbm, faces=cbm.faces)
        cbm.to_mesh(cut_me)
        cbm.free()
        cut_me.materials.append(mats[3])
        cut = bpy.data.objects.new("_break_cut", cut_me)
        sc.collection.objects.link(cut)

        shot = Vector(brk["shot_dir"]).normalized()
        B = Vector(brk["burst_at"])
        made = []
        for name, P, n0, kind, face in plan:
            me, c = _chunk_mesh(name, P, n0, lug_builder(lug_at[name]) if name in lug_at else None)
            for m in mats[:3]:
                me.materials.append(m)
            ob = bpy.data.objects.new(name, me)
            col.objects.link(ob)
            ob.location = c
            if any(bh.solid(P + bx, thin=0.002) is not None for bx in boxes):
                ob.location = (0, 0, 0)            # cut in rest pose: mesh offset by c
                bpy.context.view_layer.update()
                mo = ob.modifiers.new("Slit", "BOOLEAN")
                mo.operation, mo.solver, mo.object = "DIFFERENCE", "EXACT", cut
                mo.material_mode = "TRANSFER"
                # the mesh is about its centroid; move the cutter into that frame
                cut.location = -c
                bpy.context.view_layer.update()
                _apply_modifiers(ob)
                cut.location = (0, 0, 0)
                ob.location = c
            bv = ob.modifiers.new("Bevel", "BEVEL")
            bv.width = cfg["bevel"] * (1.6 if kind in ("ring", "core") else 1.0)
            bv.segments = 2
            bv.limit_method = "WEIGHT"
            made.append((ob, P, kind, face, c))

        hme = bpy.data.meshes.new("Debris.Hatch")
        hbm = bmesh.new()
        bh.hatch(cfg, hbm)
        hc = Vector(np.array([v.co[:] for v in hbm.verts]).mean(0))
        for v in hbm.verts:
            v.co -= hc
        hbm.to_mesh(hme)
        hbm.free()
        hme.materials.append(mats[2])
        hatch = bpy.data.objects.new("Debris.Hatch", hme)
        col.objects.link(hatch)
        hatch.location = hc
        hb = hatch.modifiers.new("Bevel", "BEVEL")
        hb.width, hb.segments = 0.004, 1
        for p in hme.polygons:
            p.use_smooth = False
        bpy.data.objects.remove(cut, do_unlink=True)

        # floor: a box whose top is z = 0 (a BOX shape is centred on the origin)
        gme = bpy.data.meshes.new("_break_floor")
        gbm = bmesh.new()
        bmesh.ops.create_cube(gbm, size=1.0)
        for v in gbm.verts:
            v.co.x *= 40.0
            v.co.y *= 40.0
            v.co.z *= 2.0              # thick: a fast chunk cannot step through it
        gbm.to_mesh(gme)
        gbm.free()
        floor = bpy.data.objects.new("_break_floor", gme)
        sc.collection.objects.link(floor)
        floor.location = (0, 0, -1.0)
        floor.hide_render = True
        floor.display_type = "WIRE"

        # The cell's own edges, made physical (brick_wall.hex_fence): the prop
        # belongs to one cell and a chunk on the neighbour is in somebody
        # else's hex. Invisible, tall enough that nothing goes over (the hatch
        # tops out at 1.55), a hair outside the walls' feet so nothing starts
        # in contact with it.
        fence = []
        if brk["fence"]:
            inner = math.sqrt(3.0) * 0.5 * brk["fence_reach"]
            th, hgt = 0.25, 2.4
            for k in range(6):
                fme = bpy.data.meshes.new("_break_fence.%d" % k)
                fbm = bmesh.new()
                bmesh.ops.create_cube(fbm, size=1.0)
                for v in fbm.verts:
                    v.co.x *= 1.12           # run past the corner so nothing squeezes through
                    v.co.y *= th
                    v.co.z *= hgt
                fbm.to_mesh(fme)
                fbm.free()
                fo = bpy.data.objects.new("_break_fence.%d" % k, fme)
                sc.collection.objects.link(fo)
                ang = math.radians(30.0 + 60.0 * k)
                n = Vector((math.cos(ang), math.sin(ang), 0.0))
                fo.location = n * (inner + th * 0.5) + Vector((0, 0, hgt * 0.5 - 0.05))
                fo.rotation_euler = Euler((0.0, 0.0, ang - math.pi * 0.5))
                fo.hide_render = True
                fo.display_type = "WIRE"
                fence.append(fo)
        # the tank: the model itself for the eye, three convex proxies for the
        # solver - hull with tracks, turret, gun. Its aerial stays out of them,
        # or it would stand in the roof as an invisible spike.
        tank_cols = []
        tag = brk["tank"]
        if tag:
            troot = bpy.data.objects["Ref.%s" % tag]
            troot.location = (0.0, PARK[tag], 0.0)
            tcol = bpy.data.collections.new("Bunker.Break.Tank")
            sc.collection.children.link(tcol)
            objs = _subtree(troot)
            for o in objs:
                tcol.objects.link(o)
            bpy.context.view_layer.update()
            gun = set()
            for o in objs:
                if o.name.split(".")[0] in ("Barrel", "Muzzle"):
                    gun.update(_subtree(o))
            turret = set()
            for o in objs:
                if o.name.split(".")[0] == "Turret":
                    turret.update(x for x in _subtree(o) if x not in gun)
            groups = {"hull": [], "turret": [], "gun": []}
            for o in objs:
                if o.type != "MESH":
                    continue
                key = "gun" if o in gun else ("turret" if o in turret else "hull")
                groups[key].append(_world_verts(o))
            for key, parts in groups.items():
                if not parts:
                    continue
                V = np.vstack(parts)
                V = V[V[:, 2] <= TURRET_TOP[tag] + 0.006]
                V = V[np.unique(np.round(V / 0.004).astype(np.int64), axis=0, return_index=True)[1]]
                cme = bpy.data.meshes.new("_break_tank.%s" % key)
                cbm = bmesh.new()
                bh._add_hull(cbm, V)
                cbm.to_mesh(cme)
                cbm.free()
                co = bpy.data.objects.new("_break_tank.%s" % key, cme)
                sc.collection.objects.link(co)
                co.hide_render = True
                co.display_type = "WIRE"
                tank_cols.append(co)
        bpy.context.view_layer.update()
        for ob in [o for o, *_ in made] + [hatch, floor] + fence + tank_cols:
            rbw.collection.objects.link(ob)
            bpy.context.view_layer.objects.active = ob
            bpy.ops.rigidbody.object_add()
            rb = ob.rigid_body
            rb.collision_shape = "BOX" if (ob is floor or ob in fence) else "CONVEX_HULL"
            if ob in tank_cols:
                rb.type = "PASSIVE"
                continue
            rb.mesh_source = "BASE"
            rb.collision_margin = 0.002
            rb.use_margin = True
            rb.friction = brk["friction"]
            rb.restitution = brk["restitution"]
            if ob is floor or ob in fence:
                rb.type = "PASSIVE"
                continue
            rb.type = "ACTIVE"
            d = ob.dimensions
            rb.mass = max(0.5, d.x * d.y * d.z * 0.6 * brk["density"])
            rb.use_deactivation = True
            rb.deactivate_linear_velocity = 0.15
            rb.deactivate_angular_velocity = 0.30
            rb.linear_damping, rb.angular_damping = brk["damping"]

        # ---- the pushes
        # One rigid motion per wall, per roof segment and for the hit slab -
        # v(p) = V + w x (p - pivot), the same w for every chunk of it. Chunks
        # each spun about their own centre ran into their neighbours on the
        # first step (the top of the one below goes out, the foot of the one
        # above comes in) and Bullet threw one of them clear of the 16-unit
        # floor at 6 R/s. Neighbouring walls separate on their own: their
        # normals diverge.
        J = brk["jitter"]

        def jit():
            return Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-1, 1)))

        up = Vector((0, 0, 1))
        motion = {}
        for k in range(6):
            n0 = Vector(bh._n(k) + (0.0,))
            # each wall a little off its normal, or the heap lies in a
            # six-pointed star, one ray per wall
            n = Matrix.Rotation(math.radians(rng.uniform(-12.0, 12.0)), 3, "Z") @ n0
            f = rng.uniform(1 - J, 1 + J)
            # base speed at the foot, and a topple about the outer foot edge
            # that sends the top out faster (+0.9 w at the top of the wall)
            V = (n + shot * brk["shot_share"] + up * brk["lift_share"]) * brk["cp_speed"] * 0.40 * f
            # the axis stays on the foot edge (n0, not n): turned with the
            # throw, one end of the foot went down into the floor at 0.36 R/s
            # and a plinth chunk came out under it at -257
            w = up.cross(n0) * brk["topple"] * f
            motion["wall%d" % k] = (V, w, n0 * cfg["r0"], Vector(bh._t(k) + (0.0,)))
            V = (n * 0.45 + up * 0.9) * rng.uniform(0.7, 1.3)
            motion["ring%d" % k] = (V, jit() * 1.2, None, None)
        motion["core"] = (shot * 2.4, jit() * 2.0, None, None)
        release = {}
        if brk["style"] == "collapse":
            Pz = cfg["plinth"]
            for i, k in enumerate(kk for kk in range(6) if kk != bh.GATE):
                n0 = Vector(bh._n(k) + (0.0,))
                f = rng.uniform(1 - J, 1 + J)
                V = -n0 * brk["wall_in"] * f + up * 0.05
                # about the inner foot of the main course: the outer foot lifts
                # off the plinth instead of going into it, the top comes in
                w = -up.cross(n0) * brk["topple_in"] * f
                pivot = n0 * bh.r_in(cfg, Pz) + up * Pz
                motion["wall%d" % k] = (V, w, pivot, None)
                release["wall%d" % k] = brk["blast"] + brk["wall_delays"][i % 2]
                motion["ring%d" % k] = (up * brk["ring_heave"] * rng.uniform(0.7, 1.3) - n0 * 0.1,
                                        jit() * 0.8, None, None)
            motion["ring%d" % bh.GATE] = (up * brk["ring_heave"], jit() * 0.8, None, None)
        for ob, P, kind, face, c in made:
            if brk["style"] == "collapse" and kind == "plinth":
                if brk["plinth_static"]:
                    # the bedded course never moves: a kerb the heap lies in
                    ob.rigid_body.kinematic = True
                else:
                    _key_release(ob, brk["blast"] + max(brk["wall_delays"]), jit() * 0.02, Vector(), fps)
                continue
            key = "core" if kind == "core" else ("ring%d" % face if kind == "ring" else "wall%d" % face)
            V, w, pivot, t = motion[key]
            v = V + (w.cross(c - pivot) if pivot is not None else Vector()) + jit() * 0.06
            if t is not None:
                # spread along the wall from its middle: neighbours only part
                v += t * (t.dot(c - pivot) * brk["spread"])
            when = brk["impact"] if kind == "core" else release.get(key, brk["blast"])
            _key_release(ob, when, v, w, fps)
        if brk["style"] == "collapse":
            hv = Vector((-shot.x, -shot.y, 0)).normalized() * 0.25 + up * brk["hatch_up"] + jit() * 0.1
        else:
            hv = Vector((-shot.x, -shot.y, 0)).normalized() * 1.2 + up * 2.2 + jit() * 0.3
        _key_release(hatch, brk["impact"], hv, jit() * 9.0, fps)

        # ---- the bomb: in the air to the roof, on through it, gone at the blast
        bomb = bpy.data.objects.new("Bunker.Bomb", bomb_mesh())
        sc.collection.objects.link(bomb)
        bomb.data.materials.clear()
        bomb.data.materials.append(mats[2])
        for p in bomb.data.polygons:
            p.use_smooth = True
        roof = Vector((0, 0, cfg["cap_top"] + 0.38 * 0.30))
        # with a tank under it the bomb goes off in the roof, nose just over
        # the turret, not on through it
        stop_z = TURRET_TOP[tag] + 0.145 if tag else brk["bomb_stop"]
        if tag:
            B = Vector((0.0, 0.0, stop_z - 0.15))
        bomb.rotation_euler = (-shot).to_track_quat("Z", "Y").to_euler()
        for f in range(1, brk["blast"] + 1):
            if f <= brk["impact"]:
                p = roof - shot * brk["shot_speed"] * (brk["impact"] - f)
            else:
                t = (f - brk["impact"]) / float(brk["blast"] - brk["impact"])
                p = roof + shot * (roof.z - stop_z) / abs(shot.z) * (1 - (1 - t) ** 2)
            bomb.location = p
            bomb.keyframe_insert("location", frame=f)
        bomb.hide_render = False
        bomb.keyframe_insert("hide_render", frame=brk["blast"] - 1)
        bomb.hide_render = True
        bomb.keyframe_insert("hide_render", frame=brk["blast"])
        bomb.hide_viewport = False
        bomb.keyframe_insert("hide_viewport", frame=brk["blast"] - 1)
        bomb.hide_viewport = True
        bomb.keyframe_insert("hide_viewport", frame=brk["blast"])

        # ---- the flash: a short orange ball where the fuse fires
        fme = bpy.data.meshes.new("Bunker.Flash")
        fbm = bmesh.new()
        bmesh.ops.create_icosphere(fbm, subdivisions=3, radius=1.0)
        fbm.to_mesh(fme)
        fbm.free()
        fm = bpy.data.materials.get("Bunker.Flash") or bpy.data.materials.new("Bunker.Flash")
        fm.use_nodes = True
        nt = fm.node_tree
        for nd in list(nt.nodes):
            if nd.type != "OUTPUT_MATERIAL":
                nt.nodes.remove(nd)
        em = nt.nodes.new("ShaderNodeEmission")
        em.inputs["Color"].default_value = (1.0, 0.45, 0.10, 1)
        em.inputs["Strength"].default_value = 2.5     # 6 clipped to a white ball
        nt.links.new(em.outputs["Emission"], [nd for nd in nt.nodes if nd.type == "OUTPUT_MATERIAL"][0].inputs["Surface"])
        fme.materials.append(fm)
        flash = bpy.data.objects.new("Bunker.Flash", fme)
        sc.collection.objects.link(flash)
        flash.location = B
        # inside the box, not round it: at 0.95 it swallowed the bunker whole;
        # under the roof over a turret even 0.52 came out of the hole as a
        # yellow disc, so with a tank it is two thirds of that
        fs = 0.65 if tag else 1.0
        for f, s, vis in ((1, 0.01, False), (brk["blast"] - 1, 0.01, False), (brk["blast"], 0.25 * fs, True),
                          (brk["blast"] + 1, 0.42 * fs, True), (brk["blast"] + 2, 0.52 * fs, True),
                          (brk["blast"] + 3, 0.48 * fs, True), (brk["blast"] + 4, 0.01, False)):
            flash.scale = (s, s, s)
            flash.keyframe_insert("scale", frame=f)
            flash.hide_render = not vis
            flash.keyframe_insert("hide_render", frame=f)
            flash.hide_viewport = not vis
            flash.keyframe_insert("hide_viewport", frame=f)

        # ---- the ground you see, and the cell
        g = bpy.data.objects.get("_bunker_ground")
        if g is not None:
            sc.collection.objects.link(g)
        hx = bpy.data.objects.get("_bunker_hex")
        if hx is not None:
            sc.collection.objects.link(hx)
        base = bpy.data.objects.get("Bunker.Foundation")
        if base is not None:
            sc.collection.objects.link(base)      # what stays: the pad
        sc.frame_set(1)
        return {"chunks": len(made), "by_kind": {k: sum(1 for m in made if m[2] == k)
                                                 for k in ("wall", "plinth", "ring", "core")},
                "lugs_on": lug_at}
    finally:
        bpy.context.window.scene = main_window_scene


def _free(sc):
    if sc.rigidbody_world is not None:
        with bpy.context.temp_override(scene=sc):
            bpy.ops.ptcache.free_bake_all()


def bake():
    sc = bpy.data.scenes[SCENE]
    main = bpy.context.window.scene
    bpy.context.window.scene = sc
    try:
        _free(sc)                 # first, then the frame: see build()
        sc.frame_set(1)
        with bpy.context.temp_override(scene=sc):
            bpy.ops.ptcache.bake_all(bake=True)
        return sc.rigidbody_world.point_cache.is_baked
    finally:
        bpy.context.window.scene = main


def report(frame=None, tile_radius=1.0):
    """Where things came to rest: the pile, the spread, what stands."""
    sc = bpy.data.scenes[SCENE]
    main = bpy.context.window.scene
    bpy.context.window.scene = sc
    try:
        sc.frame_set(1)
        pcs = [o for o in sc.collection.all_objects if o.name.startswith(PREFIX)]
        rest = {o.name: o.matrix_world.translation.copy() for o in pcs}
        sc.frame_set(frame or sc.frame_end)
        now = {o.name: o.matrix_world.translation.copy() for o in pcs}
        moved = sorted((now[n] - rest[n]).length for n in rest)
        r_cell = [Vector((p.x, p.y, 0)).length for p in now.values()]
        standing = [n for n in rest if (now[n] - rest[n]).length < 0.05 and rest[n].z > 0.3]
        roof = [n for n in rest if ".Cap" in n]
        out = {
            "pieces": len(pcs),
            "moved_over_5cm": sum(1 for d in moved if d > 0.05),
            "median_travel": round(moved[len(moved) // 2], 3),
            "max_travel": round(moved[-1], 3),
            "still_standing": standing,
            "pile_top": round(max(p.z for n, p in now.items() if n != "Debris.Hatch"), 3),
            "lowest_z": round(min(p.z for p in now.values()), 3),
            "inside_cell": sum(1 for r in r_cell if r < 0.866 * tile_radius),
            "roof_inside_cell": sum(1 for n in roof if Vector((now[n].x, now[n].y, 0)).length < 0.866),
            "roof_pieces": len(roof),
            "farthest": round(max(r_cell), 3),
        }
        lin = []
        sc.frame_set((frame or sc.frame_end) - 1)
        prev = {o.name: o.matrix_world.translation.copy() for o in pcs}
        sc.frame_set(frame or sc.frame_end)
        out["still_moving"] = sum(1 for o in pcs if (o.matrix_world.translation - prev[o.name]).length > 0.002)
        sc.frame_set(1)
        return out
    finally:
        bpy.context.window.scene = main


def camera(azimuth=-40.0, elevation=35.0, scale=4.2, target=(0.0, 0.0, 0.35), size=560):
    sc = bpy.data.scenes[SCENE]
    for name in ("_break_cam", "_break_sun"):
        ob = bpy.data.objects.get(name)
        if ob:
            bpy.data.objects.remove(ob, do_unlink=True)
    cd = bpy.data.cameras.new("_break_cam")
    cd.type = "ORTHO"
    cd.ortho_scale = scale
    cd.clip_start, cd.clip_end = 0.01, 100.0
    cam = bpy.data.objects.new("_break_cam", cd)
    sc.collection.objects.link(cam)
    az, el = math.radians(azimuth), math.radians(elevation)
    d = Vector((math.sin(az) * math.cos(el), -math.cos(az) * math.cos(el), math.sin(el)))
    cam.location = Vector(target) + d * 20.0
    cam.rotation_euler = (-d).to_track_quat("-Z", "Y").to_euler()
    sd = bpy.data.lights.new("_break_sun", "SUN")
    sd.energy = 2.6
    sd.angle = math.radians(3.0)
    sun = bpy.data.objects.new("_break_sun", sd)
    sc.collection.objects.link(sun)
    sun.rotation_euler = (math.radians(42.0), 0.0, math.radians(-35.0))
    sc.camera = cam
    sc.render.resolution_x = sc.render.resolution_y = size
    sc.render.resolution_percentage = 100
    sc.render.film_transparent = False
    sc.render.image_settings.file_format = "PNG"
    try:
        sc.eevee.taa_render_samples = 16
    except AttributeError:
        pass
    return cam


def record(folder, frames, prefix="b"):
    import os
    sc = bpy.data.scenes[SCENE]
    main = bpy.context.window.scene
    bpy.context.window.scene = sc
    try:
        os.makedirs(folder, exist_ok=True)
        out = []
        for f in frames:
            sc.frame_set(f)
            path = os.path.join(folder, "%s%04d.png" % (prefix, f))
            sc.render.filepath = path
            bpy.ops.render.render(write_still=True)
            out.append(path)
        return out
    finally:
        bpy.context.window.scene = main


def containment(frame=None, tol=0.03):
    """How much of the heap is on the bunker's own hex. A piece is out by
    how far its farthest vertex lies past the cell edge (flat-top, R = 1):
    max over the six sides of n.p - inradius."""
    sc = bpy.data.scenes[SCENE]
    main = bpy.context.window.scene
    bpy.context.window.scene = sc
    try:
        sc.frame_set(frame or sc.frame_end)
        N = np.array([bh._n(k) for k in range(6)])
        r0 = math.sqrt(3.0) / 2.0
        rows = []
        for o in sc.collection.all_objects:
            if not o.name.startswith(PREFIX):
                continue
            M = np.array(o.matrix_world)
            co = np.empty(len(o.data.vertices) * 3)
            o.data.vertices.foreach_get("co", co)
            W = co.reshape(-1, 3) @ M[:3, :3].T + M[:3, 3]
            out_v = (W[:, :2] @ N.T).max(axis=1) - r0
            c = M[:3, 3]
            rows.append((o.name, float((N @ c[:2]).max() - r0), float(out_v.max()),
                         o.rigid_body.mass if o.rigid_body else 0.0))
        mass = sum(r[3] for r in rows)
        out = {
            "pieces": len(rows),
            "centre_off_cell": sum(1 for r in rows if r[1] > 0),
            "overhang_over_tol": sum(1 for r in rows if r[2] > tol),
            "worst_overhang": round(max(r[2] for r in rows), 3),
            "mass_on_cell": round(sum(r[3] for r in rows if r[1] <= 0) / mass, 3),
            # the plinth stands on the edge by design; the rest got there
            "against_fence": sum(1 for r in rows if r[2] > -0.02 and ".P." not in r[0]),
            "worst": sorted(((round(r[2], 3), r[0]) for r in rows), reverse=True)[:4],
        }
        sc.frame_set(1)
        return out
    finally:
        bpy.context.window.scene = main


def crumble(brk=None):
    """Key each chunk's fade from the baked simulation, by the stand's rule:
    `linger` frames of being still (counted after the blast, never reset),
    then alpha to nothing over `crumble` frames, then hidden. The bodies
    stay where they are; only the drawing goes."""
    brk = resolve(brk)
    sc = bpy.data.scenes[SCENE]
    main = bpy.context.window.scene
    bpy.context.window.scene = sc
    try:
        pcs = [o for o in sc.collection.all_objects if o.name.startswith(PREFIX)]
        end = sc.frame_end
        sc.frame_set(brk["blast"])
        prev = {o.name: (o.matrix_world.translation.copy(), o.matrix_world.to_quaternion()) for o in pcs}
        still = {o.name: 0 for o in pcs}
        start = {}
        for f in range(brk["blast"] + 1, end + 1):
            sc.frame_set(f)
            for o in pcs:
                loc, q = o.matrix_world.translation.copy(), o.matrix_world.to_quaternion()
                pl, pq = prev[o.name]
                if (loc - pl).length < brk["still_lin"] and pq.rotation_difference(q).angle < brk["still_ang"]:
                    still[o.name] += 1
                    if still[o.name] >= brk["linger"] and o.name not in start:
                        start[o.name] = f
                prev[o.name] = (loc, q)
        late = end - brk["crumble"] - 2
        for o in pcs:
            f0 = min(start.get(o.name, late), late)
            o.color = (1.0, 1.0, 1.0, 1.0)
            o.keyframe_insert("color", index=3, frame=1)
            o.keyframe_insert("color", index=3, frame=f0)
            o.color = (1.0, 1.0, 1.0, 0.0)
            o.keyframe_insert("color", index=3, frame=f0 + brk["crumble"])
            o.color = (1.0, 1.0, 1.0, 1.0)
            o.hide_render = False
            o.keyframe_insert("hide_render", frame=1)
            o.hide_render = True
            o.keyframe_insert("hide_render", frame=f0 + brk["crumble"] + 1)
            o.hide_render = False
        sc.frame_set(1)
        starts = sorted(min(start.get(o.name, late), late) for o in pcs)
        return {"pieces": len(pcs), "never_still": sum(1 for o in pcs if o.name not in start),
                "first_fade": starts[0], "last_fade": starts[-1],
                "all_gone_by": starts[-1] + brk["crumble"] + 1}
    finally:
        bpy.context.window.scene = main


# ---------------------------------------------------------------- the game's copy

GAME_COLOURS = {
    # linear base colours for Toon: the procedural concrete averaged, since the
    # cel pass paints flat tones and the glTF cannot carry a node tree anyway
    "BunkerGame.Concrete": (0.27, 0.25, 0.21),
    "BunkerGame.Fresh": (0.40, 0.38, 0.35),
    "BunkerGame.Metal": (0.09, 0.095, 0.055),
    "BunkerGame.Shade": (0.02, 0.02, 0.018),
}
GAME_OF = {
    "Bunker.Concrete": "BunkerGame.Concrete", "Bunker.Debris": "BunkerGame.Concrete",
    "Bunker.Fresh": "BunkerGame.Fresh",
    "Bunker.Metal": "BunkerGame.Metal", "Bunker.Debris.Metal": "BunkerGame.Metal",
    "Bunker.Shade": "BunkerGame.Shade", "Bunker.Debris.Shade": "BunkerGame.Shade",
}


TEX_SCALE = 1.6        # repeats per R: WallStack's tex_scale
TEX_WEATHERED = 0.8    # the outside's share of the picture's brightness


def _tex(path, name, gain):
    """The picture, scaled in brightness, packed so the glb carries it."""
    img = bpy.data.images.get(name)
    if img is not None:
        bpy.data.images.remove(img)
    base = bpy.data.images.load(path, check_existing=False)
    w, h = base.size
    px = np.empty(w * h * 4, dtype=np.float32)
    base.pixels.foreach_get(px)
    px = px.reshape(-1, 4)
    px[:, :3] *= gain
    img = bpy.data.images.new(name, w, h, alpha=False)
    img.pixels.foreach_set(px.ravel())
    img.pack()
    bpy.data.images.remove(base)
    return img


def _box_uv(me, matrix):
    """UVs by box projection in the world at rest, R units times TEX_SCALE:
    each face along its own normal's axis - the board's triplanar concrete,
    and a chunk's pattern the one it had in the wall."""
    uv = me.uv_layers.get("UVMap") or me.uv_layers.new(name="UVMap")
    M = np.array(matrix)
    co = np.array([v.co[:] for v in me.vertices])
    W = co @ M[:3, :3].T + M[:3, 3]
    for poly in me.polygons:
        n = np.abs(np.array(matrix.to_3x3() @ poly.normal))
        ax = int(np.argmax(n))
        a, b = [(1, 2), (0, 2), (0, 1)][ax]
        for li in poly.loop_indices:
            p = W[me.loops[li].vertex_index]
            uv.data[li].uv = (p[a] * TEX_SCALE, p[b] * TEX_SCALE)


def _gl(v):
    """Blender (x, y, z) -> glTF / Godot (x, z, -y): what the exporter does."""
    return [round(float(v[0]), 6), round(float(v[2]), 6), round(float(-v[1]), 6)]


def _hull_volume(V):
    bm = bmesh.new()
    bh._add_hull(bm, V)
    vol = bm.calc_volume()
    bm.free()
    return float(abs(vol))


def export_game(out_dir=None, cfg=None, brk=None):
    """`assets/Models/Bunker/{bunker.glb, bunker.json}` - what Tank3D stands.

    The glb: `Intact_*` (the bunker as built, modifiers applied), `Foundation`
    (what stays), and one `Debris_*` node per chunk at rest, its mesh about
    its own centroid. Flat game materials, `BunkerGame.*`, for `Toon`.

    The json: per chunk its node, kind, face, rest centroid and convex hull
    (model frame, glTF axes, units of R) and hull volume; per wall face its
    outward normal and the inner foot the collapse folds it about; the fence;
    and the collapse's numbers. Times and speeds are Blender's (24 fps, R and
    seconds at g = 9.81 R/s^2); the stand rescales them to its own metres.
    Nothing here is simulated: the stand runs the break itself, against
    whatever tank is in the box."""
    import json
    import os
    cfg = dict(bh.CONFIG, **(cfg or {}))
    brk = resolve(brk)
    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out_dir = out_dir or os.path.join(repo, "assets", "Models", "Bunker")
    os.makedirs(out_dir, exist_ok=True)

    main = bpy.data.scenes["Scene"]
    sc = bpy.data.scenes[SCENE]
    win = bpy.context.window
    was = win.scene

    # The board's concrete (WallStack's concrete_tex, the capon's): the same
    # picture at the same size, weathered on the outside and as it is on a
    # fresh break. Flat, the albedo came out near white under the board's sun.
    src = os.path.join(repo, "assets", "Images", "Environment", "Solid", "concrete.png")
    texes = {"BunkerGame.Concrete": _tex(src, "BunkerGame.ConcreteTex", TEX_WEATHERED),
             "BunkerGame.Fresh": _tex(src, "BunkerGame.FreshTex", 1.0)}
    mats = {}
    for name, col in GAME_COLOURS.items():
        m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
        m.use_nodes = True
        nt = m.node_tree
        b = nt.nodes.get("Principled BSDF")
        for nd in list(nt.nodes):
            if nd.type == "TEX_IMAGE":
                nt.nodes.remove(nd)
        if name in texes:
            tx = nt.nodes.new("ShaderNodeTexImage")
            tx.image = texes[name]
            nt.links.new(tx.outputs["Color"], b.inputs["Base Color"])
        else:
            b.inputs["Base Color"].default_value = (*col, 1.0)
        b.inputs["Roughness"].default_value = 0.95
        b.inputs["Metallic"].default_value = 0.0
        mats[name] = m

    ex = bpy.data.scenes.get("_bunker_export") or bpy.data.scenes.new("_bunker_export")
    for ob in list(ex.collection.all_objects):
        bpy.data.objects.remove(ob, do_unlink=True)

    def take(src, name, scene):
        win.scene = scene
        scene.frame_set(1)
        dg = bpy.context.evaluated_depsgraph_get()
        me = bpy.data.meshes.new_from_object(src.evaluated_get(dg), depsgraph=dg)
        me.name = name
        for i, m in enumerate(me.materials):
            me.materials[i] = mats[GAME_OF.get(m.name if m else "", "BunkerGame.Concrete")]
        _box_uv(me, src.matrix_world)
        ob = bpy.data.objects.new(name, me)
        ob.matrix_world = src.matrix_world.copy()
        ex.collection.objects.link(ob)
        return ob

    try:
        for part in ("Walls", "Slits", "Cap", "Hatch", "Lugs"):
            take(bpy.data.objects["Bunker.%s" % part], "Intact_%s" % part, main)
        take(bpy.data.objects["Bunker.Foundation"], "Foundation", main)

        plan = {name: (P, kind, face) for name, P, n0, kind, face in chunk_plan(cfg, brk)}
        chunks = []
        debris = sorted((o for o in sc.collection.all_objects if o.name.startswith(PREFIX + ".")),
                        key=lambda o: o.name)
        for o in debris:
            node = o.name.replace(".", "_")
            take(o, node, sc)
            c = o.matrix_world.translation.copy()
            if o.name in plan:
                P, kind, face = plan[o.name]
                V = bh.verts_of(P) - np.array(c)
            else:                          # the hatch: its own mesh
                kind, face = "hatch", None
                V = np.array([v.co[:] for v in o.data.vertices])
            chunks.append({"node": node, "kind": kind, "face": face, "at": _gl(c),
                           "hull": [_gl(p) for p in V], "volume": round(_hull_volume(V), 6)})

        Pz = cfg["plinth"]
        faces = []
        for k in range(6):
            n = np.array(bh._n(k) + (0.0,))
            faces.append({"k": k, "gate": k == bh.GATE, "slit": k == bh.FRONT, "normal": _gl(n),
                          "inner_foot": _gl(n * bh.r_in(cfg, Pz) + np.array((0.0, 0.0, Pz)))})

        win.scene = ex
        glb = os.path.join(out_dir, "bunker.glb")
        # the export scene alone: without it every scene in the file went in
        # (56 MB - the reference tanks, the break, the main scene)
        bpy.ops.export_scene.gltf(filepath=glb, export_format="GLB", use_selection=False, use_active_scene=True,
                                  export_apply=False, export_yup=True, export_materials="EXPORT",
                                  export_animations=False, export_cameras=False, export_lights=False)
        side = {
            "source": "pipeline/bunker_break.py export_game; geometry bunker_hex.py, break chunk_plan",
            "frame": "glTF / Godot: +Y up, +Z the slit (the bunker's front), units of R, the hex circumradius;"
                     " the cell's middle on the ground at the origin",
            "units": {"R": 1.0},
            "size": {"height": cfg["cap_top"], "inradius": cfg["r0"], "roof_under": cfg["wall_top"],
                     "slit": list(cfg["slit_z"]), "plinth": Pz, "foundation_top": cfg["base_top"]},
            "faces": faces,
            "fence": {"reach": brk["fence_reach"], "height": 2.4, "thickness": 0.25, "overlap": 1.12},
            "collapse": {
                "fps": brk["fps"], "fuse_frames": brk["blast"] - brk["impact"],
                "wall_delays_frames": list(brk["wall_delays"]),
                "wall_in": brk["wall_in"], "topple_in": brk["topple_in"], "jitter": brk["jitter"],
                "ring_heave": brk["ring_heave"], "ring_in": 0.1, "ring_spin": 0.8,
                "core_speed": 2.4, "hatch_up": brk["hatch_up"], "hatch_side": 0.25,
                "friction": brk["friction"], "restitution": brk["restitution"],
                "damping": list(brk["damping"]), "density": brk["density"],
                "gravity": 9.81,
            },
            "chunks": chunks,
        }
        with open(os.path.join(out_dir, "bunker.json"), "w", encoding="utf-8") as f:
            json.dump(side, f, indent=1)
        return {"glb": glb, "bytes": os.path.getsize(glb), "chunks": len(chunks),
                "nodes": len(ex.collection.objects)}
    finally:
        win.scene = was
        for ob in list(ex.collection.all_objects):
            me = ob.data
            bpy.data.objects.remove(ob, do_unlink=True)
            if me.users == 0:
                bpy.data.meshes.remove(me)
        bpy.data.scenes.remove(ex)
