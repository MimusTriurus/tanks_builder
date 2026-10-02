"""The concrete capon as a truncated hexagonal pyramid on one board cell.

Flat-top hex of circumradius R = 1, the base on the cell boundary itself
(as hex_capon: neighbours touch). Side k has its outward normal at
30 + 60k deg: k = 4 faces -Y (the front, toward the camera) and carries the
embrasure, k = 1 faces +Y and is the gate. Tanks are at Tank3D's scale: the
medium's hull is 0.60 of the hex width (Tank3DBench.HullOfHex), every other
class times its MovementProfile.Size.

Every block is a convex polytope held as half-spaces (n.p <= d). A face is
the wedge between the outer and the inner slope planes and its two mitres;
courses and seams split it by planes, and an opening is subtracted by
splitting a block on the opening's planes one at a time, keeping what falls
outside each. So the jambs, the sill and the lintels are not modelled - they
are what is left. The bevel on every block draws the seams.
"""

import math
from itertools import combinations

import bmesh
import bpy
import numpy as np
from mathutils import Vector

import bunker as sq      # materials, hardware primitives and the preview rig

CONFIG = {
    "name": "Bunker",
    "r0": math.sqrt(3.0) / 2.0,   # base inradius: the walls stand on the cell edge
    "slope": 0.30,       # run per rise of every face (73 deg from the ground)
    "wall": 0.12,        # horizontal wall thickness, as hex_capon
    "plinth": 0.16,
    # roof underside, close over the tallest turret: MT's cupola is 0.899.
    # Aerials stand higher (MT 1.03, HT 1.00) and end inside the cap's concrete.
    "wall_top": 0.925,
    "cap_top": 1.165,
    "lip": 0.025,        # the cap overhangs the walls: the dark line under it
    "core": 0.215,       # inradius of the roof's middle slab, the one with the hatch
    "slit_x": 0.19,      # embrasure half-width
    # guns sit 0.48..0.76; CaponKit's 0.90 would leave a 2 cm lintel under
    # this roof
    "slit_z": (0.40, 0.80),
    # The gate is the back side alone; the two back obliques are whole along
    # their hex sides. Its clear width is the inner hexagon's side - 0.86 at
    # the ground, 0.72 at track top (z 0.4) - and the tracks are 0.80 (MT)
    # to 0.98 (TD) across, so every tank rubs the gate corners going in,
    # 0.02 R (MT) to 0.06 R (HT, TD). Asked for: the walls stay whole.
    "loop_z": (0.62, 0.80),
    "loop_w": 0.035,
    "bevel": 0.012,
    "base_top": 0.004,   # the pad left after the break: 4 mm proud of the ground
}

FRONT, GATE = 4, 1
OBLIQUE_SPLIT = (0, 2, 3, 5)     # faces whose main course is two blocks
LUG_FACES = (0, 2, 3, 5)
SLITS = [(5, -0.17), (3, 0.17)]  # (face, s along the face), mirror pair


def _n(k):
    a = math.radians(30.0 + 60.0 * k)
    return math.cos(a), math.sin(a)


def _t(k):
    """Tangent of face k, counter-clockwise."""
    c, s = _n(k)
    return -s, c


def r_out(cfg, z):
    return cfg["r0"] - cfg["slope"] * z


def r_in(cfg, z):
    return r_out(cfg, z) - cfg["wall"]


# ---------------------------------------------------------------- polytopes

def H(n, d):
    n = np.asarray(n, dtype=float)
    L = np.linalg.norm(n)
    return (n / L, d / L)


def flip(h):
    return (-h[0], -h[1])


def verts_of(planes, eps=1e-7):
    N = np.array([p[0] for p in planes])
    D = np.array([p[1] for p in planes])
    idx = np.array(list(combinations(range(len(planes)), 3)))
    A = N[idx]
    b = D[idx]
    det = np.linalg.det(A)
    ok = np.abs(det) > 1e-9
    P = np.linalg.solve(A[ok], b[ok][..., None])[..., 0]
    inside = (P @ N.T - D <= eps).all(axis=1)
    P = P[inside]
    if len(P) == 0:
        return P
    _, u = np.unique(np.round(P, 6), axis=0, return_index=True)
    return P[np.sort(u)]


def solid(planes, thin=0.008):
    """The vertices if the polytope is a real block, else None."""
    V = verts_of(planes)
    if len(V) < 4:
        return None
    ext = V.max(0) - V.min(0)
    if ext.min() < thin:
        return None
    # a wedge can be long in all three axes and still flat: check against
    # every plane how far the farthest vertex sits from it
    for n, d in planes:
        if (d - V @ n).max() < thin:
            return None
    return V


def split(planes, h):
    return planes + [h], planes + [flip(h)]


def subtract(pieces, box):
    """Pieces minus a box (a list of half-spaces), by peeling: for each plane
    in turn, what lies outside it is final, what lies inside goes on."""
    out = []
    for name, P in pieces:
        if solid(P + box) is None:      # the opening misses this block
            out.append((name, P))
            continue
        cur = P
        for i, h in enumerate(box):
            keep_in, keep_out = split(cur, h)
            if solid(keep_out) is not None:
                out.append(("%s.%d" % (name, i), keep_out))
            cur = keep_in
            if solid(cur) is None:
                break
    return out


# ---------------------------------------------------------------- layout

def wedge(cfg, k, z0, z1):
    cx, cy = _n(k)
    a0, a1 = math.radians(60.0 * k), math.radians(60.0 * (k + 1))
    c0 = (math.cos(a0), math.sin(a0))
    c1 = (math.cos(a1), math.sin(a1))
    s, r0 = cfg["slope"], cfg["r0"]
    return [
        H((cx, cy, s), r0),                        # outer slope
        H((-cx, -cy, -s), -(r0 - cfg["wall"])),    # inner slope (the cavity)
        H((c0[1], -c0[0], 0.0), 0.0),              # mitre, corner 60k
        H((-c1[1], c1[0], 0.0), 0.0),              # mitre, corner 60k + 60
        H((0, 0, -1), -z0), H((0, 0, 1), z1),
    ]


def lateral(k, s):
    """Vertical plane across face k at s along it; n.p <= d is the s- side."""
    tx, ty = _t(k)
    return H((tx, ty, 0.0), s)


def wall_pieces(cfg):
    P, T = cfg["plinth"], cfg["wall_top"]
    pieces = []
    for k in range(6):
        if k == GATE:
            continue
        pieces.append(("W%d.P" % k, wedge(cfg, k, 0.0, P)))
        main = wedge(cfg, k, P, T)
        if k in OBLIQUE_SPLIT:
            a, b = split(main, lateral(k, 0.0))
            pieces += [("W%d.Ma" % k, a), ("W%d.Mb" % k, b)]
        else:
            pieces.append(("W%d.M" % k, main))

    sx, (z0, z1) = cfg["slit_x"], cfg["slit_z"]
    # lintel first (it spans the face), then the jambs, then the sill
    embrasure = [H((0, 0, 1), z1), H((1, 0, 0), sx), H((-1, 0, 0), sx),
                 H((0, 0, -1), -z0), H((0, 1, 0), 0.0)]
    return subtract(pieces, embrasure)


def cap_pieces(cfg):
    T, C, s = cfg["wall_top"], cfg["cap_top"], cfg["slope"]
    zb = [H((0, 0, -1), -T), H((0, 0, 1), C)]
    core = [H(_n(k) + (0.0,), cfg["core"]) for k in range(6)] + zb
    out = [("Cap.Core", core)]
    for k in range(6):
        cx, cy = _n(k)
        w = wedge(cfg, k, T, C)
        ring = [H((cx, cy, s), cfg["r0"] + cfg["lip"]), w[2], w[3],
                H((-cx, -cy, 0.0), -cfg["core"])] + zb
        out.append(("Cap.R%d" % k, ring))
    return out


def all_pieces(cfg):
    return [(n, P) for n, P in wall_pieces(cfg) + cap_pieces(cfg) if solid(P) is not None]


# ---------------------------------------------------------------- meshes

def _add_hull(bm, V):
    vs = [bm.verts.new(tuple(p)) for p in V]
    res = bmesh.ops.convex_hull(bm, input=vs)
    # a vertex can be both interior and unused: delete each once
    junk = list({g for g in res["geom_interior"] + res["geom_unused"] if isinstance(g, bmesh.types.BMVert)})
    if junk:
        bmesh.ops.delete(bm, geom=junk, context="VERTS")


def _mesh(name, pieces):
    me = bpy.data.meshes.get(name) or bpy.data.meshes.new(name)
    bm = bmesh.new()
    for _, P in pieces:
        _add_hull(bm, verts_of(P))
    # the hull is triangles: merge the coplanar ones back into the block's faces
    bmesh.ops.dissolve_limit(bm, angle_limit=math.radians(0.5), verts=bm.verts, edges=bm.edges)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(me)
    bm.free()
    me.update()
    return me


def slit_cutters(cfg, bm):
    z0, z1 = cfg["loop_z"]
    w = cfg["loop_w"]
    for k, s in SLITS:
        n, t = Vector(_n(k) + (0.0,)), Vector(_t(k) + (0.0,))
        lo, hi = r_in(cfg, z1) - 0.15, r_out(cfg, z0) + 0.15
        quad = [t * (s - w) + n * lo, t * (s + w) + n * lo, t * (s + w) + n * hi, t * (s - w) + n * hi]
        sq._hexa(bm, [q + Vector((0, 0, z0)) for q in quad], [q + Vector((0, 0, z1)) for q in quad])


def _inside(P, p):
    return all(np.dot(n, p) <= d + 1e-9 for n, d in P)


def hatch(cfg, bm):
    z = cfg["cap_top"]
    sq._cyl(bm, 0.190, z, z + 0.026, seg=48)          # collar
    sq._cyl(bm, 0.160, z + 0.026, z + 0.060, seg=48)  # lid
    sq._cyl(bm, 0.118, z + 0.060, z + 0.070, seg=40)  # boss
    for i in range(8):
        a = 2 * math.pi * (i + 0.5) / 8
        sq._cyl(bm, 0.010, z + 0.026, z + 0.036, 0.176 * math.cos(a), 0.176 * math.sin(a), seg=8)
    sq._box(bm, (-0.215, -0.055, z + 0.008), (-0.150, 0.055, z + 0.064))   # hinge, -X
    sq._box(bm, (-0.165, -0.028, z + 0.058), (-0.020, 0.028, z + 0.080))
    sq._box(bm, (0.015, -0.085, z + 0.070), (0.040, -0.060, z + 0.105))    # handle
    sq._box(bm, (0.015, 0.060, z + 0.070), (0.040, 0.085, z + 0.105))
    sq._box(bm, (0.015, -0.085, z + 0.105), (0.040, 0.085, z + 0.126))


def lugs(cfg, bm):
    z = cfg["cap_top"]
    rad = (cfg["core"] + r_out(cfg, z) + cfg["lip"]) / 2.0 + 0.01
    for k in LUG_FACES:
        n, t = Vector(_n(k) + (0.0,)), Vector(_t(k) + (0.0,))
        c = n * rad
        w, d, h, b = 0.048, 0.018, 0.064, 0.018   # half-length, half-depth, height, bar

        def box(s0, s1, n0, n1, z0, z1):
            quad = [c + t * s0 + n * n0, c + t * s1 + n * n0, c + t * s1 + n * n1, c + t * s0 + n * n1]
            sq._hexa(bm, [q + Vector((0, 0, z0)) for q in quad], [q + Vector((0, 0, z1)) for q in quad])

        box(-w - 0.012, w + 0.012, -d - 0.012, d + 0.012, z, z + 0.010)
        box(-w, -w + b, -d, d, z, z + h)
        box(w - b, w, -d, d, z, z + h)
        box(-w, w, -d, d, z + h - b, z + h)


def foundation(cfg, bm):
    """What is left when the debris has gone: a concrete pad on the cell,
    standing proud of the ground by `base_top`, low enough that it stops
    neither a tank nor a shell. Under the intact bunker it is the floor."""
    top, bot = cfg["base_top"], -0.06
    ring = [Vector((math.cos(math.radians(60 * i)), math.sin(math.radians(60 * i)), 0.0))
            for i in range(6)]
    R = cfg["r0"] / (math.sqrt(3.0) / 2.0)
    sq._hexa_n(bm, [p * R + Vector((0, 0, bot)) for p in ring], [p * R + Vector((0, 0, top)) for p in ring])


def build(cfg=None):
    cfg = dict(CONFIG, **(cfg or {}))
    n = cfg["name"]
    scene = bpy.context.scene
    col = bpy.data.collections.get(n)
    if col is None:
        col = bpy.data.collections.new(n)
        scene.collection.children.link(col)
    # the square bunker goes: same names, new shape
    for ob in list(col.objects):
        if ob.name != "%s.World" % n:
            bpy.data.objects.remove(ob, do_unlink=True)
    root = bpy.data.objects.get("%s.World" % n)
    if root is None:
        root = bpy.data.objects.new("%s.World" % n, None)
        col.objects.link(root)
        root.empty_display_size = 0.3

    conc, met, dark = sq.concrete(), sq.metal(), sq.shade()
    walls = [(nm, P) for nm, P in wall_pieces(cfg) if solid(P) is not None]
    caps = [(nm, P) for nm, P in cap_pieces(cfg) if solid(P) is not None]
    slit_pts = []
    for k, s in SLITS:
        z = sum(cfg["loop_z"]) / 2.0
        r = (r_in(cfg, z) + r_out(cfg, z)) / 2.0
        slit_pts.append(np.array(_n(k) + (0.0,)) * r + np.array(_t(k) + (0.0,)) * s + np.array((0, 0, z)))
    pierced = [w for w in walls if any(_inside(w[1], p) for p in slit_pts)]
    plain = [w for w in walls if w not in pierced]

    w_ob = sq._obj("%s.Walls" % n, _mesh("%s.Walls" % n, plain), root, col, [conc])
    s_ob = sq._obj("%s.Slits" % n, _mesh("%s.Slits" % n, pierced), root, col, [conc])
    c_ob = sq._obj("%s.Cap" % n, _mesh("%s.Cap" % n, caps), root, col, [conc])
    cme = bpy.data.meshes.get("%s.Cut" % n) or bpy.data.meshes.new("%s.Cut" % n)
    bm = bmesh.new()
    slit_cutters(cfg, bm)
    # the quads run clockwise in (n, t): a cutter turned inside out cuts nothing
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(cme)
    bm.free()
    cut = sq._obj("%s.Cut" % n, cme, root, col, [dark])
    cut.display_type = "WIRE"
    cut.hide_render = True
    cut.hide_set(True)
    for ob in (w_ob, s_ob, c_ob):
        sq._bevel(ob, cfg["bevel"] * (1.6 if ob is c_ob else 1.0))
    bo = s_ob.modifiers.new("Slits", "BOOLEAN")
    bo.operation = "DIFFERENCE"
    bo.solver = "EXACT"
    bo.object = cut
    bo.material_mode = "TRANSFER"

    def mk(name, fn, mat, bev):
        me = bpy.data.meshes.get(name) or bpy.data.meshes.new(name)
        b = bmesh.new()
        fn(cfg, b)
        b.to_mesh(me)
        b.free()
        ob = sq._obj(name, me, root, col, [mat])
        sq._bevel(ob, bev, segments=1)
        return ob

    h_ob = mk("%s.Hatch" % n, hatch, met, 0.004)
    l_ob = mk("%s.Lugs" % n, lugs, met, 0.003)
    f_ob = mk("%s.Foundation" % n, foundation, conc, 0.006)
    for ob in (w_ob, s_ob, c_ob, h_ob, l_ob, f_ob):
        for p in ob.data.polygons:
            p.use_smooth = False
    bpy.context.view_layer.update()
    return {"walls": len(walls), "pierced": [p[0] for p in pierced], "caps": len(caps),
            "top_inradius": round(r_out(cfg, cfg["cap_top"]) + cfg["lip"], 3),
            "names": [p[0] for p in walls]}


# ---------------------------------------------------------------- fit

def clearance(pts, pieces):
    """Smallest outside distance of any point to any block (negative: inside)."""
    best, who = 1e9, None
    for name, P in pieces:
        N = np.array([p[0] for p in P])
        D = np.array([p[1] for p in P])
        m = (pts @ N.T - D).max(axis=1).min()
        if m < best:
            best, who = m, name
    return best, who


def hex_tile(name="_bunker_hex", z=0.002, radius=1.0, color=(0.36, 0.40, 0.30)):
    """Preview only: the cell the capon stands on, flat on the ground."""
    ob = bpy.data.objects.get(name)
    if ob is None:
        me = bpy.data.meshes.new(name)
        bm = bmesh.new()
        vs = [bm.verts.new((radius * math.cos(math.radians(60 * i)),
                            radius * math.sin(math.radians(60 * i)), z)) for i in range(6)]
        bm.faces.new(vs)
        bm.to_mesh(me)
        bm.free()
        mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
        mat.use_nodes = True
        mat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (*color, 1)
        mat.node_tree.nodes["Principled BSDF"].inputs["Roughness"].default_value = 1.0
        me.materials.append(mat)
        ob = bpy.data.objects.new(name, me)
        bpy.context.scene.collection.objects.link(ob)
    return ob
