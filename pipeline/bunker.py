"""The bunker's kit: its materials, its hardware primitives and the preview rig.

What the hex capon is built from (`bunker_hex`, the bunker on one cell) and
broken with (`bunker_break`). The square truncated pyramid this module first
built - `build()` and its block layout - is kept as the first version: the
shape the reference shows, before the board's hex asked for its own.

Materials are rebuilt in place (`_fresh`), never removed: removing one empties
the slot of every mesh that uses it, in every scene.
"""

import math

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector

INF = 1e9

CONFIG = {
    "name": "Bunker",
    "base": 1.30,        # outer half-width at the ground
    "slope": 0.42,       # run per rise, every face (67 deg from the ground)
    "wall": 0.20,        # horizontal wall thickness
    "plinth": 0.20,      # bottom course
    "wall_top": 1.08,    # roof underside; the tallest turret (MT) is 1.03
    "cap_top": 1.36,
    "lip": 0.03,         # the cap overhangs the walls: the dark line under it
    "grid": 0.27,        # roof seams at +-grid
    "slit_x": 0.35,      # embrasure half-width
    "slit_z": (0.40, 0.82),
    "gate_x": 0.55,      # rear entrance half-width (TD hull is 0.495)
    "loop_z": (0.70, 0.92),
    "loop_w": 0.042,     # vision slit half-width
    "bevel": 0.016,
}

# per face, the course splits along the face (s) - front frame: s is +X seen
# from outside the front; INF ends are mitred corners
FACES = {
    0: {"plinth": [-INF, -0.62, 0.18, INF]},                    # front, -Y
    1: {"plinth": [-INF, 0.10, INF], "main": [-INF, -0.30, 0.42, INF]},  # right, +X
    2: {"plinth": [-INF, -0.55, 0.55, INF], "main": [-INF, -0.55, 0.55, INF]},  # back
    3: {"plinth": [-INF, -0.10, INF], "main": [-INF, -0.42, 0.30, INF]},  # left, -X
}
SLITS = [(1, -0.62), (1, 0.66), (3, -0.66), (3, 0.62)]   # (face, s)


def a_out(cfg, z):
    return cfg["base"] - cfg["slope"] * z


def a_in(cfg, z):
    return a_out(cfg, z) - cfg["wall"]


def _rot(k):
    return Matrix.Rotation(k * math.pi / 2.0, 3, "Z")


def wall_block(cfg, k, s0, s1, z0, z1):
    """Eight corners of a block of face k between s0..s1 and z0..z1."""
    R = _rot(k)
    pts = []
    for z in (z0, z1):
        ao, ai = a_out(cfg, z), a_in(cfg, z)
        ring = [(max(s0, -ao), -ao), (min(s1, ao), -ao),
                (min(s1, ai), -ai), (max(s0, -ai), -ai)]
        pts.append([R @ Vector((x, y, z)) for x, y in ring])
    return pts


def cap_block(cfg, x0, x1, y0, y1):
    pts = []
    for z in (cfg["wall_top"], cfg["cap_top"]):
        c = a_out(cfg, z) + cfg["lip"]
        xa, xb = max(x0, -c), min(x1, c)
        ya, yb = max(y0, -c), min(y1, c)
        pts.append([Vector((xa, ya, z)), Vector((xb, ya, z)),
                    Vector((xb, yb, z)), Vector((xa, yb, z))])
    return pts


def blocks(cfg):
    out = []
    P, T = cfg["plinth"], cfg["wall_top"]
    sx, (sz0, sz1) = cfg["slit_x"], cfg["slit_z"]
    for k, f in FACES.items():
        s = f["plinth"]
        for i in range(len(s) - 1):
            if k == 2 and s[i] == -cfg["gate_x"]:
                continue                              # the gate runs to the floor
            out.append(("W%d.P%d" % (k, i), wall_block(cfg, k, s[i], s[i + 1], 0.0, P)))
        if k == 0:
            out.append(("W0.L", wall_block(cfg, 0, -INF, -sx, P, T)))
            out.append(("W0.R", wall_block(cfg, 0, sx, INF, P, T)))
            out.append(("W0.Sill", wall_block(cfg, 0, -sx, sx, P, sz0)))
            out.append(("W0.Lintel", wall_block(cfg, 0, -sx, sx, sz1, T)))
            continue
        s = f["main"]
        for i in range(len(s) - 1):
            if k == 2 and s[i] == -cfg["gate_x"]:
                continue
            out.append(("W%d.M%d" % (k, i), wall_block(cfg, k, s[i], s[i + 1], P, T)))
    g = cfg["grid"]
    cuts = [-INF, -g, g, INF]
    for i in range(3):
        for j in range(3):
            out.append(("Cap%d%d" % (i, j), cap_block(cfg, cuts[i], cuts[i + 1], cuts[j], cuts[j + 1])))
    return out


def _hexa(bm, lo, hi):
    a = [bm.verts.new(p) for p in lo]
    b = [bm.verts.new(p) for p in hi]
    bm.faces.new(list(reversed(a)))
    bm.faces.new(b)
    for i in range(4):
        j = (i + 1) % 4
        bm.faces.new((a[i], a[j], b[j], b[i]))


def _hexa_n(bm, lo, hi):
    """Prism between two n-gons, any n, counter-clockwise from above."""
    a = [bm.verts.new(p) for p in lo]
    b = [bm.verts.new(p) for p in hi]
    bm.faces.new(list(reversed(a)))
    bm.faces.new(b)
    n = len(a)
    for i in range(n):
        j = (i + 1) % n
        bm.faces.new((a[i], a[j], b[j], b[i]))


def _mesh(name, builder):
    me = bpy.data.meshes.get(name) or bpy.data.meshes.new(name)
    bm = bmesh.new()
    builder(bm)
    # no remove_doubles: blocks that touch must stay separate islands, or the
    # bevel has no edge between them to draw the seam on
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(me)
    bm.free()
    me.update()
    return me


def _obj(name, me, parent, col, mats=()):
    ob = bpy.data.objects.get(name)
    if ob is None:
        ob = bpy.data.objects.new(name, me)
        col.objects.link(ob)
    ob.data = me
    me.materials.clear()
    for m in mats:
        me.materials.append(m)
    ob.parent = parent
    return ob


def _bevel(ob, width, segments=2, angle=30.0):
    for m in list(ob.modifiers):
        if m.type == "BEVEL":
            ob.modifiers.remove(m)
    bv = ob.modifiers.new("Bevel", "BEVEL")
    bv.width = width
    bv.segments = segments
    bv.limit_method = "ANGLE"
    bv.angle_limit = math.radians(angle)
    bv.harden_normals = False
    return bv


# ---------------------------------------------------------------- materials

def _fresh(name):
    """The material by that name with an empty Principled -> Output graph.
    Rebuilt in place, never removed: removing a material empties the slot of
    every mesh that uses it, in every scene."""
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.name = "Principled BSDF"
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    return mat


def concrete():
    mat = _fresh("Bunker.Concrete")
    nt = mat.node_tree
    N, L = nt.nodes, nt.links
    bsdf = N["Principled BSDF"]
    co = N.new("ShaderNodeTexCoord")

    # broad tone
    tone = N.new("ShaderNodeTexNoise")
    tone.inputs["Scale"].default_value = 2.2
    tone.inputs["Detail"].default_value = 6.0
    L.new(co.outputs["Object"], tone.inputs["Vector"])
    tramp = N.new("ShaderNodeValToRGB")
    tramp.color_ramp.elements[0].position = 0.35
    tramp.color_ramp.elements[0].color = (0.20, 0.178, 0.145, 1.0)
    tramp.color_ramp.elements[1].position = 0.70
    tramp.color_ramp.elements[1].color = (0.33, 0.30, 0.25, 1.0)
    L.new(tone.outputs["Fac"], tramp.inputs["Fac"])

    # damp stains, stretched down the slope (vertical streaks)
    smap = N.new("ShaderNodeMapping")
    smap.inputs["Scale"].default_value = (3.0, 3.0, 0.8)
    L.new(co.outputs["Object"], smap.inputs["Vector"])
    stain = N.new("ShaderNodeTexNoise")
    stain.inputs["Scale"].default_value = 2.5
    stain.inputs["Detail"].default_value = 4.0
    stain.inputs["Roughness"].default_value = 0.65
    L.new(smap.outputs["Vector"], stain.inputs["Vector"])
    sramp = N.new("ShaderNodeValToRGB")
    sramp.color_ramp.elements[0].position = 0.52
    sramp.color_ramp.elements[0].color = (0, 0, 0, 1)
    sramp.color_ramp.elements[1].position = 0.72
    sramp.color_ramp.elements[1].color = (0.45, 0.45, 0.45, 1)
    L.new(stain.outputs["Fac"], sramp.inputs["Fac"])
    mix = N.new("ShaderNodeMix")
    mix.data_type = "RGBA"
    mix.inputs["B"].default_value = (0.10, 0.095, 0.055, 1.0)
    L.new(sramp.outputs["Color"], mix.inputs["Factor"])
    L.new(tramp.outputs["Color"], mix.inputs["A"])

    # pits: small dark dots
    pits = N.new("ShaderNodeTexVoronoi")
    pits.inputs["Scale"].default_value = 14.0
    L.new(co.outputs["Object"], pits.inputs["Vector"])
    pramp = N.new("ShaderNodeValToRGB")
    pramp.color_ramp.elements[0].position = 0.035
    pramp.color_ramp.elements[0].color = (0.45, 0.45, 0.45, 1)
    pramp.color_ramp.elements[1].position = 0.06
    pramp.color_ramp.elements[1].color = (1, 1, 1, 1)
    L.new(pits.outputs["Distance"], pramp.inputs["Fac"])
    mul = N.new("ShaderNodeMix")
    mul.data_type = "RGBA"
    mul.blend_type = "MULTIPLY"
    mul.inputs["Factor"].default_value = 1.0
    L.new(mix.outputs["Result"], mul.inputs["A"])
    L.new(pramp.outputs["Color"], mul.inputs["B"])
    L.new(mul.outputs["Result"], bsdf.inputs["Base Color"])

    # grain for the bump
    grain = N.new("ShaderNodeTexNoise")
    grain.inputs["Scale"].default_value = 60.0
    grain.inputs["Detail"].default_value = 3.0
    L.new(co.outputs["Object"], grain.inputs["Vector"])
    hsum = N.new("ShaderNodeMath")
    hsum.operation = "MULTIPLY"
    L.new(grain.outputs["Fac"], hsum.inputs[0])
    L.new(pramp.outputs["Color"], hsum.inputs[1])
    bump = N.new("ShaderNodeBump")
    bump.inputs["Strength"].default_value = 0.22
    bump.inputs["Distance"].default_value = 0.02
    L.new(hsum.outputs["Value"], bump.inputs["Height"])
    L.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
    bsdf.inputs["Roughness"].default_value = 0.92
    return mat


def shade():
    """Inside of a slit: what little light gets in does not come back out."""
    name = "Bunker.Shade"
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    b = mat.node_tree.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = (0.018, 0.017, 0.015, 1)
    b.inputs["Roughness"].default_value = 1.0
    return mat


def metal():
    mat = _fresh("Bunker.Metal")
    nt = mat.node_tree
    bsdf = nt.nodes["Principled BSDF"]
    co = nt.nodes.new("ShaderNodeTexCoord")
    nz = nt.nodes.new("ShaderNodeTexNoise")
    nz.inputs["Scale"].default_value = 12.0
    nt.links.new(co.outputs["Object"], nz.inputs["Vector"])
    rp = nt.nodes.new("ShaderNodeValToRGB")
    rp.color_ramp.elements[0].color = (0.055, 0.06, 0.035, 1)
    rp.color_ramp.elements[1].color = (0.13, 0.135, 0.08, 1)
    nt.links.new(nz.outputs["Fac"], rp.inputs["Fac"])
    nt.links.new(rp.outputs["Color"], bsdf.inputs["Base Color"])
    bsdf.inputs["Metallic"].default_value = 0.35
    bsdf.inputs["Roughness"].default_value = 0.55
    return mat


# ---------------------------------------------------------------- hardware

def _cyl(bm, r, z0, z1, x=0.0, y=0.0, seg=32):
    res = bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=seg,
                                radius1=r, radius2=r, depth=z1 - z0)
    bmesh.ops.translate(bm, verts=res["verts"], vec=(x, y, (z0 + z1) / 2.0))


def _box(bm, lo, hi):
    (x0, y0, z0), (x1, y1, z1) = lo, hi
    b = [Vector((x0, y0, 0)), Vector((x1, y0, 0)), Vector((x1, y1, 0)), Vector((x0, y1, 0))]
    _hexa(bm, [Vector((p.x, p.y, z0)) for p in b], [Vector((p.x, p.y, z1)) for p in b])


def hatch(cfg, bm):
    z = cfg["cap_top"]
    _cyl(bm, 0.235, z, z + 0.030, seg=48)          # collar
    _cyl(bm, 0.200, z + 0.030, z + 0.070, seg=48)  # lid
    _cyl(bm, 0.150, z + 0.070, z + 0.082, seg=40)  # lid boss
    for i in range(10):                            # collar bolts
        a = 2 * math.pi * (i + 0.5) / 10
        _cyl(bm, 0.011, z + 0.030, z + 0.042, 0.218 * math.cos(a), 0.218 * math.sin(a), seg=8)
    # hinge on -X, handle across the lid
    _box(bm, (-0.265, -0.07, z + 0.01), (-0.185, 0.07, z + 0.075))
    _box(bm, (-0.20, -0.035, z + 0.07), (-0.02, 0.035, z + 0.095))
    _box(bm, (0.02, -0.105, z + 0.082), (0.05, -0.075, z + 0.125))
    _box(bm, (0.02, 0.075, z + 0.082), (0.05, 0.105, z + 0.125))
    _box(bm, (0.02, -0.105, z + 0.125), (0.05, 0.105, z + 0.150))


def lugs(cfg, bm):
    z = cfg["cap_top"]
    for sx in (-1, 1):
        for sy in (-1, 1):
            cx, cy = 0.53 * sx, 0.53 * sy
            w, d, h, t = 0.055, 0.020, 0.075, 0.020   # half-length, half-depth, height, bar
            _box(bm, (cx - w - 0.012, cy - d - 0.012, z), (cx + w + 0.012, cy + d + 0.012, z + 0.012))
            _box(bm, (cx - w, cy - d, z), (cx - w + t, cy + d, z + h))
            _box(bm, (cx + w - t, cy - d, z), (cx + w, cy + d, z + h))
            _box(bm, (cx - w, cy - d, z + h - t), (cx + w, cy + d, z + h))


def slit_cutters(cfg, bm):
    z0, z1 = cfg["loop_z"]
    w = cfg["loop_w"]
    for k, s in SLITS:
        R = _rot(k)
        y0 = -a_out(cfg, z0) - 0.2
        y1 = -a_in(cfg, z1) + 0.2
        b = [(s - w, y0), (s + w, y0), (s + w, y1), (s - w, y1)]
        _hexa(bm, [R @ Vector((x, y, z0)) for x, y in b], [R @ Vector((x, y, z1)) for x, y in b])


# ---------------------------------------------------------------- build

def build(cfg=None):
    cfg = dict(CONFIG, **(cfg or {}))
    n = cfg["name"]
    scene = bpy.context.scene
    col = bpy.data.collections.get(n)
    if col is None:
        col = bpy.data.collections.new(n)
        scene.collection.children.link(col)

    root = bpy.data.objects.get("%s.World" % n)
    if root is None:
        root = bpy.data.objects.new("%s.World" % n, None)
        col.objects.link(root)
        root.empty_display_size = 0.3

    conc, met = concrete(), metal()
    bl = blocks(cfg)
    # The exact boolean reads touching islands as one self-intersecting
    # volume and eats them, so only the blocks a slit goes through are cut,
    # on an object of their own where no two blocks touch.
    slit = set()
    for k, s in SLITS:
        sp = FACES[k]["main"]
        slit.update("W%d.M%d" % (k, i) for i in range(len(sp) - 1) if sp[i] < s < sp[i + 1])
    walls = [b for b in bl if not b[0].startswith("Cap") and b[0] not in slit]
    pierced = [b for b in bl if b[0] in slit]
    caps = [b for b in bl if b[0].startswith("Cap")]

    def build_set(items):
        def f(bm):
            for _, (lo, hi) in items:
                _hexa(bm, lo, hi)
        return f

    w_ob = _obj("%s.Walls" % n, _mesh("%s.Walls" % n, build_set(walls)), root, col, [conc])
    s_ob = _obj("%s.Slits" % n, _mesh("%s.Slits" % n, build_set(pierced)), root, col, [conc])
    c_ob = _obj("%s.Cap" % n, _mesh("%s.Cap" % n, build_set(caps)), root, col, [conc])
    cut = _obj("%s.Cut" % n, _mesh("%s.Cut" % n, lambda bm: slit_cutters(cfg, bm)), root, col,
               [shade()])
    cut.display_type = "WIRE"
    cut.hide_render = True
    cut.hide_set(True)

    for ob in (w_ob, s_ob, c_ob):
        for m in list(ob.modifiers):
            ob.modifiers.remove(m)
        _bevel(ob, cfg["bevel"] * (1.6 if ob is c_ob else 1.0))
    bo = s_ob.modifiers.new("Slits", "BOOLEAN")
    bo.operation = "DIFFERENCE"
    bo.solver = "EXACT"
    bo.object = cut
    bo.material_mode = "TRANSFER"     # the slit's walls take the cutter's dark

    h_ob = _obj("%s.Hatch" % n, _mesh("%s.Hatch" % n, lambda bm: hatch(cfg, bm)), root, col, [met])
    _bevel(h_ob, 0.004, segments=1)
    l_ob = _obj("%s.Lugs" % n, _mesh("%s.Lugs" % n, lambda bm: lugs(cfg, bm)), root, col, [met])
    _bevel(l_ob, 0.003, segments=1)
    for ob in (w_ob, s_ob, c_ob, h_ob, l_ob):
        for p in ob.data.polygons:
            p.use_smooth = False
    bpy.context.view_layer.update()
    return {"blocks": len(bl), "walls": len(walls), "pierced": sorted(slit), "caps": len(caps),
            "footprint": round(2 * cfg["base"], 3), "top": round(2 * (a_out(cfg, cfg["cap_top"]) + cfg["lip"]), 3),
            "height": cfg["cap_top"]}


# ---------------------------------------------------------------- fit

def planes(cfg):
    """Half-spaces of every block, (n, d) with n.p <= d inside."""
    out = []
    for name, (lo, hi) in blocks(cfg):
        bm = bmesh.new()
        _hexa(bm, lo, hi)
        bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-7)
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
        bm.normal_update()
        P = []
        for f in bm.faces:
            if f.calc_area() < 1e-10:
                continue
            nrm = f.normal.copy()
            P.append((tuple(nrm), nrm.dot(f.verts[0].co)))
        bm.free()
        out.append((name, np.array([p[0] for p in P]), np.array([p[1] for p in P])))
    return out


def clearance(pts, pl):
    """Smallest outside distance of any point to any block (negative: inside)."""
    best, who = INF, None
    for name, Nn, D in pl:
        s = (pts @ Nn.T - D).max(axis=1)
        m = s.min()
        if m < best:
            best, who = m, name
    return best, who


# ---------------------------------------------------------------- preview

def preview(path, azimuth=-40.0, elevation=35.0, size=900, scale=3.6, target=(0.0, 0.0, 0.55),
            samples=32, ground=True):
    """Ortho shot from `azimuth` degrees off the front (-Y), negative toward -X."""
    scene = bpy.context.scene
    for name in ("_bunker_cam", "_bunker_sun"):
        ob = bpy.data.objects.get(name)
        if ob:
            bpy.data.objects.remove(ob, do_unlink=True)
    cd = bpy.data.cameras.new("_bunker_cam")
    cd.type = "ORTHO"
    cd.ortho_scale = scale
    cd.clip_start, cd.clip_end = 0.01, 100.0
    cam = bpy.data.objects.new("_bunker_cam", cd)
    scene.collection.objects.link(cam)
    az, el = math.radians(azimuth), math.radians(elevation)
    t = Vector(target)
    d = Vector((math.sin(az) * math.cos(el), -math.cos(az) * math.cos(el), math.sin(el)))
    cam.location = t + d * 20.0
    cam.rotation_euler = (-d).to_track_quat("-Z", "Y").to_euler()
    sd = bpy.data.lights.new("_bunker_sun", "SUN")
    sd.energy = 2.6
    sd.angle = math.radians(3.0)
    sun = bpy.data.objects.new("_bunker_sun", sd)
    scene.collection.objects.link(sun)
    sun.rotation_euler = (math.radians(42.0), 0.0, math.radians(-35.0))
    gr = bpy.data.objects.get("_bunker_ground")
    if gr is None:
        gm = bpy.data.meshes.new("_bunker_ground")
        bm = bmesh.new()
        bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=12.0)
        bm.to_mesh(gm)
        bm.free()
        gmat = bpy.data.materials.get("_bunker_ground") or bpy.data.materials.new("_bunker_ground")
        gmat.use_nodes = True
        gmat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.80, 0.80, 0.80, 1)
        gmat.node_tree.nodes["Principled BSDF"].inputs["Roughness"].default_value = 1.0
        gm.materials.append(gmat)
        gr = bpy.data.objects.new("_bunker_ground", gm)
        scene.collection.objects.link(gr)
    gr.hide_render = not ground
    scene.camera = cam
    scene.render.engine = "BLENDER_EEVEE"
    scene.render.resolution_x = scene.render.resolution_y = size
    scene.render.resolution_percentage = 100
    scene.render.film_transparent = False
    scene.render.image_settings.file_format = "PNG"
    scene.view_settings.view_transform = "Standard"
    try:
        scene.eevee.taa_render_samples = samples
        scene.eevee.use_shadows = True
    except AttributeError:
        pass
    world = scene.world or bpy.data.worlds.new("World")
    scene.world = world
    world.use_nodes = True
    # white behind the model, a grey sky on it: the camera ray picks
    wn, wl = world.node_tree.nodes, world.node_tree.links
    for nd in list(wn):
        if nd.type != "OUTPUT_WORLD":
            wn.remove(nd)
    out = [nd for nd in wn if nd.type == "OUTPUT_WORLD"][0]
    sky = wn.new("ShaderNodeBackground")
    sky.inputs["Color"].default_value = (0.62, 0.66, 0.72, 1.0)
    sky.inputs["Strength"].default_value = 0.55
    white = wn.new("ShaderNodeBackground")
    white.inputs["Color"].default_value = (1.0, 1.0, 1.0, 1.0)
    white.inputs["Strength"].default_value = 1.0
    lp = wn.new("ShaderNodeLightPath")
    mx = wn.new("ShaderNodeMixShader")
    wl.new(lp.outputs["Is Camera Ray"], mx.inputs["Fac"])
    wl.new(sky.outputs["Background"], mx.inputs[1])
    wl.new(white.outputs["Background"], mx.inputs[2])
    wl.new(mx.outputs["Shader"], out.inputs["Surface"])
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    return path
