"""Procedural copies of generator models: everything but the tank's own shape.

docs/repro.md has the order and the traps.  A copy is one module per tank in
`repro/` (see `repro/lt_parts.py`) that says where the copy stands, what its
paint looks like and how its parts are built; this module does the rest:

- primitives (box, prism, lathe, convex hulls, bent plates, rivets) and their
  evaluation through Bevel/Subdivision into one mesh per part;
- the canonical roots and names (docs/tank-scene.md) with a suffix, next to
  the original in the same scene;
- hand-painted source materials and the bake into one base colour and one
  roughness/metal image per root, the generator's layout;
- measuring the original off its vertices, rendering both side by side, and
  the checks every copy has to pass.

Coordinates are the original's own: a tank module writes every number in the
frame of the model it copies, so each one can be checked against its
vertices, and `build()` shifts the result by the tank's `X_OFF`.

A tank module provides:

    NAME, SFX, X_OFF, PREFIX      the original, name suffix, offset, datablock prefix
    GROUND, RING_C, RING_Z0       its ground z, ring axis (x, y), turret root z
    TRACK_X                       belt centre |x|; the left belt is on -X
    PALETTE                       {material index: dict(base, light, dark, ink, rough, metal)}
    TRUNNION                      (y, z) of the axis the gun lays about, across X
    hull(mats), engine(mats), turret(mats), barrel(mats)  -> Group
    mantlet(mats) -> Group        what lays with the gun and does not recoil
    belt(mats, xc) -> (bmesh, stats),  rolls(mats, xc, side) -> Group
    turret_ink(nb, pos, nrm) -> socket   optional: painted seams on the turret

The gun lays in elevation (the board has levels; `barrel_recoil` renders the
ladder of angles): `Mantlet` and `Barrel` get their origins on the trunnion,
so `lay(tank, deg)` is `rotation_euler.x` on both, and the barrel's breech
stub ends on the trunnion so `barrel_recoil.trunnion()` fits the same pivot.

    import repro_kit as K
    tank = K.load("lt_parts")
    K.build(tank); K.lay_sheet(tank)
    K.bake(tank, ("Hull", "Turret")); K.bake(tank, ("TrackL", "TrackR"))
    K.compare(tank, ["iso_fl", "side"], "cmp"); K.verify(tank); K.save(tank)
"""

import importlib.util
import math
import os
import time
import bpy
import bmesh
import numpy as np
from mathutils import Matrix, Vector

REPO = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(os.path.dirname(REPO), "assets")
TMP = "_repro_tmp"

# source materials: one per kind of surface, baked away at the end
KINDS = ["Paint", "Metal", "Rubber", "Track", "Glass", "Dark", "PaintTurret",
         "PaintDark", "Rivet", "RivetMetal"]
PAINT, GUN, RUBBER, TRACK, GLASS, DARK, PAINT_T, PAINTDK, RIVET, RIVET_G = range(10)
GREEN = (PAINT, PAINT_T, PAINTDK, RIVET)      # what the grey check samples under

NAMES = {
    "hull": "Hull.World", "hull_geo": "Hull.Geometry", "engine": "Engine.Geometry",
    "turret": "Turret.World", "turret_geo": "Turret.Geometry", "barrel": "Barrel.Geometry",
    "mantlet": "Mantlet.Geometry",
    "left": "Track.Left.World", "l_cat": "L.Caterpillar.Geometry", "l_rolls": "L.Rolls.Geometry",
    "right": "Track.Right.World", "r_cat": "R.Caterpillar.Geometry", "r_rolls": "R.Rolls.Geometry",
}
ROOTS = {   # bake key -> (root, children); a child the tank does not build is skipped
    "Hull": ("hull", ("hull_geo", "engine")),
    "Turret": ("turret", ("turret_geo", "mantlet", "barrel")),
    "TrackL": ("left", ("l_cat", "l_rolls")),
    "TrackR": ("right", ("r_cat", "r_rolls")),
}
FX = ("Burn", "Burst", "Dust", "Fire", "Flash", "Plume", "Scar", "Smoke")   # pipeline effects

TEX = 2048            # per root, like the canon
UV_MARGIN = 0.004     # of the UV square: ~8 px at 2048 between islands
BAKE_MARGIN = 16      # px, fills the gaps between islands with their own colour
RIV = 0.0062          # rivet radius


def load(name):
    """The tank module `repro/<name>.py`, freshly executed (Blender caches
    imports, and a stale layout is the one thing that must never be built)."""
    path = os.path.join(REPO, "repro", name + ".py")
    spec = importlib.util.spec_from_file_location("repro_" + name, path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    mod.__file_path__ = path
    return mod


def out_dir(tank):
    d = os.path.join(REPO, "out", "repro", tank.NAME)
    os.makedirs(d, exist_ok=True)
    return d


def nm(tank, key):
    return NAMES[key] + tank.SFX


def coll_name(tank):
    return tank.NAME + tank.SFX


def src_name(tank, idx):
    return "%s_src_%s" % (tank.PREFIX, KINDS[idx])


def srgb(h):
    h = h.lstrip("#")
    c = [int(h[i:i + 2], 16) / 255.0 for i in (0, 2, 4)]
    return tuple(((x + 0.055) / 1.055) ** 2.4 if x > 0.04045 else x / 12.92
                 for x in c) + (1.0,)


# ---------------------------------------------------------------- materials

class NB:
    """Small node builder: sockets or constants everywhere."""

    def __init__(self, nt):
        self.nt, self.n, self.l = nt, nt.nodes, nt.links

    def put(self, sock, v):
        if isinstance(v, bpy.types.NodeSocket):
            self.l.new(v, sock)
        else:
            sock.default_value = v

    def math(self, op, a, b=None, c=None, clamp=False):
        m = self.n.new("ShaderNodeMath")
        m.operation = op
        m.use_clamp = clamp
        for i, v in enumerate((a, b, c)):
            if v is not None:
                self.put(m.inputs[i], v)
        return m.outputs[0]

    def rng(self, v, a, b, c=0.0, d=1.0, smooth=False):
        m = self.n.new("ShaderNodeMapRange")
        m.clamp = True
        if smooth:
            m.interpolation_type = "SMOOTHSTEP"
        self.put(m.inputs["Value"], v)
        m.inputs["From Min"].default_value = a
        m.inputs["From Max"].default_value = b
        m.inputs["To Min"].default_value = c
        m.inputs["To Max"].default_value = d
        return m.outputs["Result"]

    def mix(self, a, b, fac, blend="MIX"):
        m = self.n.new("ShaderNodeMix")
        m.data_type = "RGBA"
        m.blend_type = blend
        for s in m.inputs:
            if s.identifier == "A_Color":
                self.put(s, a)
            elif s.identifier == "B_Color":
                self.put(s, b)
            elif s.identifier == "Factor_Float":
                self.put(s, fac)
        return [s for s in m.outputs if s.identifier == "Result_Color"][0]

    def noise(self, vec, scale, detail=4.0, rough=0.55):
        t = self.n.new("ShaderNodeTexNoise")
        t.inputs["Scale"].default_value = scale
        t.inputs["Detail"].default_value = detail
        t.inputs["Roughness"].default_value = rough
        self.put(t.inputs["Vector"], vec)
        return t

    def vmath(self, op, a, b=None, scale=None):
        m = self.n.new("ShaderNodeVectorMath")
        m.operation = op
        self.put(m.inputs[0], a)
        if b is not None:
            self.put(m.inputs[1], b)
        if scale is not None:
            m.inputs["Scale"].default_value = scale
        return m.outputs["Value"] if op in ("DOT_PRODUCT", "LENGTH", "DISTANCE") \
            else m.outputs["Vector"]

    def line(self, v, centre, width, soft=0.0007):
        """1 within width/2 of `centre`, a soft edge `soft` wide: a painted line."""
        return self.rng(self.math("ABSOLUTE", self.math("SUBTRACT", v, centre)),
                        width * 0.5 + soft, width * 0.5, smooth=True)


def make_source(tank, idx):
    """A procedural hand-painted material, only ever baked (Cycles: AO, Bevel).

    Flat paint in soft patches and brush strokes, lit from above (by the
    normal's z, so it holds when the turret turns), shaded and inked in
    creases (AO of this object only) and inked on hard edges (Bevel node --
    modelled bevels are two 45 deg segments, sharper than the 38 deg the mesh
    splits normals at, so every chamfer edge is hard).
    Rivets: a lit dome in a dark ring from the vertex layer `Ink`.
    """
    name = src_name(tank, idx)
    m = bpy.data.materials.get(name)
    if m:
        bpy.data.materials.remove(m)
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    for nd in list(nt.nodes):
        nt.nodes.remove(nd)
    b = NB(nt)
    pal = tank.PALETTE[idx]
    C = {k: srgb(v) for k, v in pal.items() if k not in ("rough", "metal")}
    out = b.n.new("ShaderNodeOutputMaterial")
    out.name = "OUT"
    tc = b.n.new("ShaderNodeTexCoord")
    geo = b.n.new("ShaderNodeNewGeometry")
    obj = tc.outputs["Object"]

    n1 = b.noise(obj, 3.2, 3.0, 0.5)
    col = b.mix(C["base"], C["light"], b.rng(n1.outputs["Fac"], 0.52, 0.78, 0.0, 0.45))
    col = b.mix(col, C["dark"], b.rng(n1.outputs["Fac"], 0.46, 0.22, 0.0, 0.45))
    mp = b.n.new("ShaderNodeMapping")
    mp.inputs["Scale"].default_value = (26.0, 26.0, 5.0)
    b.put(mp.inputs["Vector"], obj)
    n2 = b.noise(mp.outputs["Vector"], 1.0, 2.0, 0.5)
    col = b.mix(col, C["light"], b.rng(n2.outputs["Fac"], 0.58, 0.72, 0.0, 0.22))
    col = b.mix(col, C["dark"], b.rng(n2.outputs["Fac"], 0.42, 0.28, 0.0, 0.22))

    rim = b.n.new("ShaderNodeAttribute")
    rim.attribute_name = "Ink"
    if idx in (RIVET, RIVET_G):
        col = b.mix(col, C["light"], 0.35)
        col = b.mix(col, C["ink"], b.rng(rim.outputs["Fac"], 0.3, 0.8, smooth=True))
        return _finish(b, m, col, pal, out)

    nz = b.n.new("ShaderNodeSeparateXYZ")
    b.put(nz.inputs[0], geo.outputs["Normal"])
    col = b.mix(col, C["light"], b.rng(nz.outputs["Z"], 0.35, 0.95, 0.0, 0.4))
    col = b.mix(col, C["dark"], b.rng(nz.outputs["Z"], -0.15, -0.9, 0.0, 0.5))

    # the belt is small parts packed tight: everything is near a crease or an
    # edge there, so it gets a lighter hand or it goes black
    small = idx == TRACK
    ao = b.n.new("ShaderNodeAmbientOcclusion")
    ao.samples = 12
    ao.only_local = True
    ao.inputs["Distance"].default_value = 0.010 if small else 0.022
    col = b.mix(col, C["dark"], b.rng(ao.outputs["AO"], 0.97, 0.72, 0.0, 0.45 if small else 0.7))
    ink = b.rng(ao.outputs["AO"], 0.8 if small else 0.87, 0.6 if small else 0.64, smooth=True)
    bev = b.n.new("ShaderNodeBevel")
    bev.samples = 8
    bev.inputs["Radius"].default_value = 0.0012 if small else 0.0025
    e = b.math("SUBTRACT", 1.0, b.vmath("DOT_PRODUCT", bev.outputs["Normal"], geo.outputs["Normal"]))
    ink = b.math("MAXIMUM", ink, b.rng(e, 0.03, 0.08, smooth=True))
    if small:
        ink = b.math("MULTIPLY", ink, 0.7)
    if idx == PAINT_T and hasattr(tank, "turret_ink"):
        ink = b.math("MAXIMUM", ink, tank.turret_ink(b, obj, geo.outputs["Normal"]))
    # no painted dents: a small inked ring on a riveted plate reads as a
    # missing rivet, not as a dent
    col = b.mix(col, C["ink"], ink)
    return _finish(b, m, col, pal, out)


def _finish(b, m, col, pal, out):
    """Two emission outputs (albedo, roughness/metal) and the bake target."""
    em_c = b.n.new("ShaderNodeEmission")
    em_c.name = "EM_ALBEDO"
    b.put(em_c.inputs["Color"], col)
    em_d = b.n.new("ShaderNodeEmission")
    em_d.name = "EM_ORM"
    em_d.inputs["Color"].default_value = (0.0, pal["rough"], pal["metal"], 1.0)
    b.l.new(em_c.outputs[0], out.inputs["Surface"])
    tgt = b.n.new("ShaderNodeTexImage")
    tgt.name = "BAKE_TARGET"
    b.n.active = tgt
    return m


# --------------------------------------------------------------- primitives

def new_bm():
    return bmesh.new()


def setmat(faces, mi):
    for f in faces:
        f.material_index = mi


def box(bm, c, s, mi, rot=None):
    m = Matrix.Diagonal((s[0], s[1], s[2], 1.0))
    if rot is not None:
        m = rot.to_4x4() @ m
    m = Matrix.Translation(Vector(c)) @ m
    r = bmesh.ops.create_cube(bm, size=1.0, matrix=m)
    setmat({f for v in r["verts"] for f in v.link_faces}, mi)
    return r["verts"]


def prism(bm, poly_yz, x0, x1, mi):
    """Extrude a closed (y, z) polygon along X from x0 to x1."""
    a = [bm.verts.new((x0, y, z)) for y, z in poly_yz]
    b = [bm.verts.new((x1, y, z)) for y, z in poly_yz]
    n = len(poly_yz)
    fs = [bm.faces.new(a[::-1]), bm.faces.new(b)]
    for i in range(n):
        j = (i + 1) % n
        fs.append(bm.faces.new((a[i], a[j], b[j], b[i])))
    setmat(fs, mi)
    bmesh.ops.recalc_face_normals(bm, faces=fs)
    return fs


def lathe(bm, prof, mi_list, seg=48, axis="X", center=(0, 0, 0), closed=True, phase=0.0):
    """Revolve [(a, r)] about an axis through `center`; r ~ 0 makes a pole.
    Stepped profiles: no subdivision afterwards, it melts the steps."""
    cx, cy, cz = center
    rings = []
    for a, r in prof:
        if r < 1e-6:
            p = {"X": (cx + a, cy, cz), "Y": (cx, cy + a, cz), "Z": (cx, cy, cz + a)}[axis]
            rings.append([bm.verts.new(p)] * seg)
            continue
        ring = []
        for j in range(seg):
            t = phase + math.tau * j / seg
            u, w = r * math.cos(t), r * math.sin(t)
            p = {"X": (cx + a, cy + u, cz + w), "Y": (cx + w, cy + a, cz + u),
                 "Z": (cx + u, cy + w, cz + a)}[axis]
            ring.append(bm.verts.new(p))
        rings.append(ring)
    npf = len(prof) if closed else len(prof) - 1
    fs = []
    for i in range(npf):
        i2 = (i + 1) % len(prof)
        mi = mi_list if isinstance(mi_list, int) else mi_list[i]
        for j in range(seg):
            j2 = (j + 1) % seg
            q = []
            for v in (rings[i][j], rings[i][j2], rings[i2][j2], rings[i2][j]):
                if v not in q:
                    q.append(v)
            if len(q) < 3:
                continue
            f = bm.faces.new(q)
            f.material_index = mi
            fs.append(f)
    bmesh.ops.recalc_face_normals(bm, faces=fs)
    return fs


def rounded_rect_profile(a0, a1, r0, r1, rad, n=4):
    pts = []
    for ca, cr, start in ((a1 - rad, r0 + rad, -90), (a1 - rad, r1 - rad, 0),
                          (a0 + rad, r1 - rad, 90), (a0 + rad, r0 + rad, 180)):
        for k in range(n + 1):
            t = math.radians(start + 90.0 * k / n)
            pts.append((ca + rad * math.cos(t), cr + rad * math.sin(t)))
    return pts


def sphere(bm, c, r, mi, scale=(1, 1, 1), rot=None, seg=12, rings=6):
    m = Matrix.Diagonal((r * scale[0], r * scale[1], r * scale[2], 1.0))
    if rot is not None:
        m = rot.to_4x4() @ m
    m = Matrix.Translation(Vector(c)) @ m
    res = bmesh.ops.create_uvsphere(bm, u_segments=seg, v_segments=rings, radius=1.0, matrix=m)
    setmat({f for v in res["verts"] for f in v.link_faces}, mi)
    return res["verts"]


def cyl(bm, p0, p1, r, mi, seg=24, r1=None, caps=True):
    p0, p1 = Vector(p0), Vector(p1)
    d = p1 - p0
    rot = Vector((0, 0, 1)).rotation_difference(d.normalized()).to_matrix()
    m = Matrix.Translation((p0 + p1) / 2) @ rot.to_4x4()
    res = bmesh.ops.create_cone(bm, cap_ends=caps, cap_tris=False, segments=seg, radius1=r,
                                radius2=r if r1 is None else r1, depth=d.length, matrix=m)
    setmat({f for v in res["verts"] for f in v.link_faces}, mi)
    return res["verts"]


def basis_from_normal(nrm):
    return Vector((0, 0, 1)).rotation_difference(Vector(nrm).normalized()).to_matrix()


def rivets(bm, pts, nrm, r=RIV, mi=RIVET):
    """Squashed open domes, their rim sunk 0.4 mm into the plate (callers pass
    a point 0.4 mm proud of it).  The vertex layer 'Ink' marks the rim, which
    the rivet material paints as a dark ring.  Only the dome is built: it is a
    height field over its base, so `rivet_uvs` gives it one flat island."""
    lay = bm.verts.layers.float.get("Ink") or bm.verts.layers.float.new("Ink")
    axis = Vector(nrm).normalized()
    rot = basis_from_normal(nrm)
    for p in pts:
        c = Vector(p) - axis * 0.0008
        vs = sphere(bm, c, r, mi, scale=(1, 1, 0.6), rot=rot, seg=12, rings=6)
        below = [v for v in vs if (v.co - c).dot(axis) < -1e-6]
        bmesh.ops.delete(bm, geom=below, context="VERTS")
        for v in vs:
            if v.is_valid:
                h = (v.co - c).dot(axis) / (r * 0.6)
                v[lay] = max(0.0, min(1.0, (0.78 - h) / 0.26))


def line(p0, p1, n):
    """n points evenly inside the segment p0..p1 (not on its ends)."""
    p0, p1 = Vector(p0), Vector(p1)
    return [p0.lerp(p1, (i + 0.5) / n) for i in range(n)]


def bend_bar(bm, pts, r, mi, seg=10):
    for a, b in zip(pts, pts[1:]):
        cyl(bm, a, b, r, mi, seg=seg, caps=False)
    for p in pts:
        sphere(bm, p, r, mi, seg=seg, rings=6)


def hull_solid(bm, pts, mi):
    """Convex polyhedron through `pts`, coplanar triangles merged back to
    n-gons (so an angle-limited Bevel leaves the flat faces alone).  Use a
    fresh bmesh: it hulls every vertex it is given."""
    vs = [bm.verts.new(Vector(p)) for p in pts]
    res = bmesh.ops.convex_hull(bm, input=vs, use_existing_faces=False)
    kill = [g for g in res["geom_interior"] + res["geom_unused"] if isinstance(g, bmesh.types.BMVert)]
    if kill:
        bmesh.ops.delete(bm, geom=kill, context="VERTS")
    bmesh.ops.dissolve_limit(bm, angle_limit=math.radians(0.5), use_dissolve_boundaries=False,
                             verts=list(bm.verts), edges=list(bm.edges))
    setmat(bm.faces, mi)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))


def offset_path(path, d):
    """Offset an open (y, z) polyline by d to the right of travel (mitred)."""
    P = [Vector(p) for p in path]
    segn = []
    for a, b in zip(P, P[1:]):
        t = (b - a).normalized()
        segn.append(Vector((t.y, -t.x)))
    out = []
    for i, p in enumerate(P):
        if i == 0:
            n = segn[0]
        elif i == len(P) - 1:
            n = segn[-1]
        else:
            n = (segn[i - 1] + segn[i]).normalized()
            n = n / max(0.3, n.dot(segn[i]))
        out.append(tuple(p + n * d))
    return out


def bent_plate(bm, path, th, x0, x1, mi):
    """A plate `th` thick bent along an open (y, z) path, extruded along X."""
    inner = offset_path(path, th)
    prism(bm, list(path) + inner[::-1], x0, x1, mi)


def clip_path_y(path, y0=None, y1=None):
    """Part of a path whose y never decreases, between y0 and y1."""
    def at(y):
        for a, b in zip(path, path[1:]):
            if a[0] <= y <= b[0] and b[0] > a[0]:
                t = (y - a[0]) / (b[0] - a[0])
                return (y, a[1] + (b[1] - a[1]) * t)
        raise ValueError(y)
    out = [] if y0 is None else [at(y0)]
    for p in path:
        if (y0 is None or p[0] > y0 + 1e-9) and (y1 is None or p[0] < y1 - 1e-9):
            out.append(p)
    if y1 is not None:
        out.append(at(y1))
    return out


def clip_polygon(poly, ya=None, yb=None, za=None, zb=None):
    """A convex (y, z) polygon clipped to y in ya..yb and z in za..zb (any
    bound may be None)."""
    bm = bmesh.new()
    bm.faces.new([bm.verts.new((y, z, 0.0)) for y, z in poly])
    cuts = []
    if ya is not None:
        cuts.append(((ya, 0, 0), (1.0, 0.0, 0.0)))
    if yb is not None:
        cuts.append(((yb, 0, 0), (-1.0, 0.0, 0.0)))
    if za is not None:
        cuts.append(((0, za, 0), (0.0, 1.0, 0.0)))
    if zb is not None:
        cuts.append(((0, zb, 0), (0.0, -1.0, 0.0)))
    for co, n in cuts:
        geom = list(bm.verts) + list(bm.edges) + list(bm.faces)
        bmesh.ops.bisect_plane(bm, geom=geom, plane_co=co, plane_no=n, clear_inner=True)
    f = max(bm.faces, key=lambda ff: ff.calc_area())
    out = [(v.co.x, v.co.y) for v in f.verts]
    bm.free()
    return out


def ring_prism_y(bm, outer, inner, y0, y1, mi):
    """Frame between two equal-length (x, z) loops, from y0 (back) to y1 (front)."""
    n = len(outer)
    o0 = [bm.verts.new((x, y0, z)) for x, z in outer]
    o1 = [bm.verts.new((x, y1, z)) for x, z in outer]
    i0 = [bm.verts.new((x, y0, z)) for x, z in inner]
    i1 = [bm.verts.new((x, y1, z)) for x, z in inner]
    fs = []
    for k in range(n):
        j = (k + 1) % n
        fs += [bm.faces.new((o1[k], o1[j], i1[j], i1[k])),
               bm.faces.new((o0[k], i0[k], i0[j], o0[j])),
               bm.faces.new((o0[k], o0[j], o1[j], o1[k])),
               bm.faces.new((i0[k], i1[k], i1[j], i0[j]))]
    setmat(fs, mi)
    bmesh.ops.recalc_face_normals(bm, faces=fs)


def tilt(bm, deg, y, z):
    """Turn everything in bm about the X axis through (y, z); negative leans
    the top back (+Y)."""
    m = (Matrix.Translation((0, y, z)) @ Matrix.Rotation(math.radians(deg), 4, "X")
         @ Matrix.Translation((0, -y, -z)))
    bmesh.ops.transform(bm, matrix=m, verts=bm.verts)


def rr_loop(cx, cz, hw, hh, r, n=6):
    """Rounded rectangle in (x, z), counter-clockwise, 4 * (n + 1) points."""
    pts = []
    for ccx, ccz, start in ((cx + hw - r, cz - hh + r, -90), (cx + hw - r, cz + hh - r, 0),
                            (cx - hw + r, cz + hh - r, 90), (cx - hw + r, cz - hh + r, 180)):
        for k in range(n + 1):
            t = math.radians(start + 90.0 * k / n)
            pts.append((ccx + r * math.cos(t), ccz + r * math.sin(t)))
    return pts


def d_section(z, a, yf, yr, pf, pr, n):
    """A turret plan at height z: flat-ish front (exponent pf), round rear (pr)."""
    yc = (yf + yr) / 2
    bf, br = yc - yf, yr - yc
    out = []
    for j in range(n):
        t = math.tau * j / n
        c, s = math.cos(t), math.sin(t)
        p = pf if s < 0 else pr
        e = 2.0 / p
        x = a * math.copysign(abs(c) ** e, c)
        y = yc + (bf if s < 0 else br) * math.copysign(abs(s) ** e, s)
        out.append(Vector((x, y, z)))
    return out


def loft(bm, rings, mi, cap_bottom=True, cap_top=True):
    """Quads between equal-length rings of Vectors; the top capped with a fan."""
    n = len(rings[0])
    vs = [[bm.verts.new(v) for v in r] for r in rings]
    fs = [bm.faces.new(vs[0][::-1])] if cap_bottom else []
    for lo, hi in zip(vs, vs[1:]):
        for i in range(n):
            j = (i + 1) % n
            fs.append(bm.faces.new((lo[i], lo[j], hi[j], hi[i])))
    if cap_top:
        c = bm.verts.new(sum(rings[-1], Vector()) / n)
        for i in range(n):
            fs.append(bm.faces.new((vs[-1][i], vs[-1][(i + 1) % n], c)))
    setmat(fs, mi)
    bmesh.ops.recalc_face_normals(bm, faces=fs)
    return fs


# --------------------------------------------------------------- evaluation

def tmp_collection():
    c = bpy.data.collections.get(TMP)
    if c is None:
        c = bpy.data.collections.new(TMP)
        bpy.context.scene.collection.children.link(c)
    return c


def evaluate(bm, mats, bevel=None, subsurf=0, smooth=True):
    """bm through an angle-limited Bevel (width, segments, angle) and a
    Subdivision, back as a new bmesh."""
    me = bpy.data.meshes.new("_tmp")
    bm.to_mesh(me)
    for m in mats:
        me.materials.append(m)
    ob = bpy.data.objects.new("_tmp", me)
    tmp_collection().objects.link(ob)
    if bevel:
        w, segs, ang = bevel
        md = ob.modifiers.new("bevel", "BEVEL")
        md.width = w
        md.segments = segs
        md.limit_method = "ANGLE"
        md.angle_limit = math.radians(ang)
        md.use_clamp_overlap = True
    if subsurf:
        md = ob.modifiers.new("sub", "SUBSURF")
        md.levels = md.render_levels = subsurf
        md.quality = 3
    dg = bpy.context.evaluated_depsgraph_get()
    out = bmesh.new()
    out.from_object(ob, dg)
    for f in out.faces:
        f.smooth = smooth
    bpy.data.objects.remove(ob)
    bpy.data.meshes.remove(me)
    return out


class Group:
    """Evaluated pieces of one part, joined into one mesh by `build`."""

    def __init__(self, mats):
        self.mats = mats
        self.meshes = []

    def add(self, bm, bevel=None, subsurf=0, smooth=True):
        ev = evaluate(bm, self.mats, bevel, subsurf, smooth)
        bm.free()
        me = bpy.data.meshes.new("_piece")
        ev.to_mesh(me)
        ev.free()
        self.meshes.append(me)

    def add_raw(self, bm, smooth=True):
        for f in bm.faces:
            f.smooth = smooth
        me = bpy.data.meshes.new("_piece")
        bm.to_mesh(me)
        bm.free()
        self.meshes.append(me)

    def build(self, name, parent, coll, x_off=0.0, origin=None):
        """Join the pieces; made in the original's frame, they are shifted by
        x_off and then taken into the root's frame (loc 0, scale 1 under it).
        `origin` (original's frame) puts the object's origin there instead --
        the pivot of a part that moves, like the gun on its trunnion."""
        bm = bmesh.new()
        for me in self.meshes:
            bm.from_mesh(me)
            bpy.data.meshes.remove(me)
        m = parent.matrix_world.inverted() @ Matrix.Translation((x_off, 0.0, 0.0))
        loc = Vector((0.0, 0.0, 0.0))
        if origin is not None:
            loc = m @ Vector(origin)
            m = Matrix.Translation(-loc) @ m
        bmesh.ops.transform(bm, matrix=m, verts=bm.verts)
        me = bpy.data.meshes.new(name)
        bm.to_mesh(me)
        bm.free()
        for mt in self.mats:
            me.materials.append(mt)
        ob = bpy.data.objects.new(name, me)
        coll.objects.link(ob)
        ob.parent = parent
        ob.location = loc
        compact_materials(me)
        me.set_sharp_from_angle(angle=math.radians(38))
        return ob


def compact_materials(me):
    mi = np.empty(len(me.polygons), np.int32)
    me.polygons.foreach_get("material_index", mi)
    used = sorted(set(mi.tolist()))
    remap = np.zeros(max(used) + 1, np.int32)
    keep = [me.materials[i] for i in used]
    for new, old in enumerate(used):
        remap[old] = new
    me.materials.clear()
    for m in keep:
        me.materials.append(m)
    me.polygons.foreach_set("material_index", remap[mi])


# ------------------------------------------------------------------- tracks

def belt_path(circles):
    """Inner belt surface as a CCW (y, z) polyline: the hull of the circles."""
    pts = []
    for y, z, r in circles:
        for k in range(720):
            t = math.tau * k / 720
            pts.append((round(y + r * math.cos(t), 7), round(z + r * math.sin(t), 7)))
    pts = sorted(set(pts))

    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])
    lower, upper = [], []
    for p in pts:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], p) <= 0:
            lower.pop()
        lower.append(p)
    for p in reversed(pts):
        while len(upper) >= 2 and cross(upper[-2], upper[-1], p) <= 0:
            upper.pop()
        upper.append(p)
    return np.array(lower[:-1] + upper[:-1])


def resample(poly, n):
    seg = np.roll(poly, -1, 0) - poly
    sl = np.linalg.norm(seg, axis=1)
    cum = np.concatenate([[0], np.cumsum(sl)])
    s = np.arange(n) * cum[-1] / n
    idx = np.searchsorted(cum, s, side="right") - 1
    t = (s - cum[idx]) / sl[idx]
    return poly[idx] + seg[idx] * t[:, None]


def place_belt(mats, xc, path, pitch, link_bm, bevel=(0.0018, 2, 35), subsurf=1):
    """Links round a closed path at a whole number of links near `pitch`.
    `link_bm(pitch)` builds one link in (u along, v across, w out) with w = 0
    on the inner surface.  Returns (bmesh, n_links, pitch, length)."""
    L = float(np.linalg.norm(np.roll(path, -1, 0) - path, axis=1).sum())
    n_links = int(round(L / pitch))
    pitch = L / n_links
    nodes = resample(path, n_links)
    link = evaluate(link_bm(pitch), mats, bevel=bevel, subsurf=subsurf)
    lme = bpy.data.meshes.new("_link")
    link.to_mesh(lme)
    link.free()
    co = np.empty(len(lme.vertices) * 3, np.float32)
    lme.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3).astype(np.float64)
    out = bmesh.new()
    for k in range(n_links):
        p0, p1 = nodes[k], nodes[(k + 1) % n_links]
        mid = (p0 + p1) / 2
        t = (p1 - p0) / np.linalg.norm(p1 - p0)
        U = np.array([0, t[0], t[1]])
        V = np.array([1.0, 0, 0])
        W = np.array([0, t[1], -t[0]])          # outward for a CCW loop; U x V = W
        P = co[:, :1] * U + co[:, 1:2] * V + co[:, 2:3] * W
        P += np.array([xc, mid[0], mid[1]])
        me = lme.copy()
        me.vertices.foreach_set("co", P.astype(np.float32).ravel())
        out.from_mesh(me)
        bpy.data.meshes.remove(me)
    bpy.data.meshes.remove(lme)
    return out, n_links, pitch, L


# -------------------------------------------------------------------- build

def clear(tank):
    coll = bpy.data.collections.get(coll_name(tank))
    if coll:
        for ob in list(coll.objects):
            data = ob.data
            bpy.data.objects.remove(ob)
            if isinstance(data, bpy.types.Mesh) and data.users == 0:
                bpy.data.meshes.remove(data)
    for me in list(bpy.data.meshes):
        if me.users == 0 and (me.name.startswith("_") or me.name.endswith(tank.SFX)):
            bpy.data.meshes.remove(me)
    c = bpy.data.collections.get(TMP)
    if c:
        for ob in list(c.objects):
            bpy.data.objects.remove(ob)
        bpy.data.collections.remove(c)
    return coll


def build(tank):
    """Every part of the copy, with source materials; `bake` textures it."""
    t0 = time.time()
    coll = clear(tank)
    if coll is None:
        coll = bpy.data.collections.new(coll_name(tank))
        bpy.context.scene.collection.children.link(coll)
    mats = [make_source(tank, i) for i in range(len(KINDS))]

    def root(key, loc):
        e = bpy.data.objects.new(nm(tank, key), None)
        e.empty_display_type = "PLAIN_AXES"
        e.empty_display_size = 0.2
        coll.objects.link(e)
        e.matrix_world = Matrix.Translation(Vector(loc) + Vector((tank.X_OFF, 0, 0)))
        return e

    hull_w = root("hull", (0, 0, 0))
    tur_w = root("turret", (tank.RING_C[0], tank.RING_C[1], tank.RING_Z0))
    left_w = root("left", (-tank.TRACK_X, 0, tank.GROUND))
    right_w = root("right", (tank.TRACK_X, 0, tank.GROUND))
    x = tank.X_OFF
    pivot = trunnion(tank)
    obs = [tank.hull(mats).build(nm(tank, "hull_geo"), hull_w, coll, x),
           tank.engine(mats).build(nm(tank, "engine"), hull_w, coll, x),
           tank.turret(mats).build(nm(tank, "turret_geo"), tur_w, coll, x)]
    if hasattr(tank, "mantlet"):
        obs.append(tank.mantlet(mats).build(nm(tank, "mantlet"), tur_w, coll, x, origin=pivot))
    obs.append(tank.barrel(mats).build(nm(tank, "barrel"), tur_w, coll, x, origin=pivot))
    stats = {}
    for side, rw, cat, rol in ((-1, left_w, "l_cat", "l_rolls"), (1, right_w, "r_cat", "r_rolls")):
        xc = side * tank.TRACK_X
        belt, belt_stats = tank.belt(mats, xc)
        g = Group(mats)
        g.add_raw(belt)
        obs.append(g.build(nm(tank, cat), rw, coll, x))
        obs.append(tank.rolls(mats, xc, side).build(nm(tank, rol), rw, coll, x))
        stats["belt"] = belt_stats
    c = bpy.data.collections.get(TMP)
    if c:
        bpy.data.collections.remove(c)
    stats["verts"] = {o.name: len(o.data.vertices) for o in obs}
    stats["total_verts"] = sum(stats["verts"].values())
    stats["build_s"] = round(time.time() - t0, 1)
    return stats


# ----------------------------------------------------------- laying the gun

def trunnion(tank):
    """The trunnion as a point on the centre line, original's frame."""
    ty, tz = tank.TRUNNION
    return (0.0, ty, tz)


def lay(tank, deg):
    """Lay the copy's gun `deg` up (negative: down) about its trunnion; 0 is
    rest.  Mantlet and barrel carry their origins on the trunnion and their
    roots are unrotated, so this is rotation_euler.x on both -- the same one
    number an engine would drive.  Returns the objects it turned."""
    turned = []
    for key in ("mantlet", "barrel"):
        ob = bpy.data.objects.get(nm(tank, key))
        if ob:
            ob.rotation_euler = (-math.radians(deg), 0.0, 0.0)
            turned.append(ob.name)
    bpy.context.view_layer.update()
    return turned


def lay_angles():
    """The angles the pipeline renders (`barrel_recoil.ladder()`: facts about
    the board, not taste), the steepest up and down plus level."""
    import sys
    if REPO not in sys.path:
        sys.path.insert(0, REPO)
    import barrel_recoil
    importlib.reload(barrel_recoil)
    table = barrel_recoil.ladder()[0]
    top = max(table)
    return [top, 0.0, -top], table


def lay_sheet(tank, degs=None, name="lay", samples=24):
    """The gun close up, side and three-quarter front, one row per angle
    (steepest up, level, steepest down by default).  What to look for: the
    block turns inside its frame without opening a slot or poking through,
    and the tube stays centred in its collar."""
    if degs is None:
        degs = lay_angles()[0]
    ty, tz = tank.TRUNNION
    X = tank.X_OFF
    aim = Vector((X, ty - 0.035, tz))
    extra = {"gun_side": (aim + Vector((0.75, 0.0, 0.02)), aim, 75, False),
             "gun_front": (aim + Vector((0.45, -0.70, 0.40)), aim, 75, False)}
    d = out_dir(tank)
    rows = []
    try:
        for deg in degs:
            lay(tank, deg)
            p = os.path.join(d, "_lay_%+.1f.png" % deg)
            shoot(["gun_side", "gun_front"], p, (X, 0, 0),
                  only=lambda o: o.name.endswith(tank.SFX), w=560, h=420,
                  samples=samples, extra=extra)
            rows.append(_read_png(p))
    finally:
        lay(tank, 0.0)
    return _tile_png(rows, 1, os.path.join(d, name + ".png"))


def check_lay(tank):
    """Numbers behind the picture: both origins on the trunnion, the gun at
    rest, and the tube's breech end on the trunnion -- `barrel_recoil`
    pivots about the breech end of the tube on the bore, so anything else
    lays the rendered gun about a different axis than the model's."""
    p = Vector(trunnion(tank)) + Vector((tank.X_OFF, 0, 0))
    out = {"trunnion": [round(v, 4) for v in p]}
    for key in ("mantlet", "barrel"):
        ob = bpy.data.objects.get(nm(tank, key))
        if ob is None:
            out[key] = None
            continue
        out[key] = {"origin_off": round((ob.matrix_world.translation - p).length, 6),
                    "rotation": [round(v, 6) for v in ob.rotation_euler]}
    bar = bpy.data.objects.get(nm(tank, "barrel"))
    if bar:
        co = world_co(bar)
        lo, hi = co.min(0), co.max(0)
        breech = Vector(((lo[0] + hi[0]) / 2, hi[1], (lo[2] + hi[2]) / 2))
        out["breech_off"] = round((breech - p).length, 6)
    return out


# --------------------------------------------------------------------- bake

def _select_only(objs):
    vl = bpy.context.view_layer
    for o in vl.objects:
        if o.select_get():
            o.select_set(False)
    for o in objs:
        o.select_set(True)
    vl.objects.active = objs[0]


def unwrap(tank, objs):
    """Smart UV project, then one flat island per rivet, then pack everything
    with a wide margin.  Smart project cuts a rivet into dozens of 1-3 px
    islands spread over the atlas, and with 3 px between islands texture
    filtering pulls the neighbours in: grey smears on green rivets."""
    _select_only(objs)
    ov = dict(active_object=objs[0], object=objs[0], selected_objects=objs,
              selected_editable_objects=objs)
    with bpy.context.temp_override(**ov):
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_all(action="SELECT")
        bpy.ops.uv.smart_project(angle_limit=math.radians(60), island_margin=0.0,
                                 area_weight=0.0, correct_aspect=True, scale_to_bounds=False)
        bpy.ops.object.mode_set(mode="OBJECT")
    riv = (src_name(tank, RIVET), src_name(tank, RIVET_G))
    for o in objs:
        uv = o.data.uv_layers
        if uv and uv[0].name != "UVMap":
            uv[0].name = "UVMap"
        rivet_uvs(o, riv)
    ts = bpy.context.scene.tool_settings
    keep = ts.use_uv_select_sync
    ts.use_uv_select_sync = True          # face selection is the UV selection
    try:
        with bpy.context.temp_override(**ov):
            bpy.ops.object.mode_set(mode="EDIT")
            bpy.ops.mesh.select_all(action="SELECT")
            bpy.ops.uv.pack_islands(rotate=True, margin_method="FRACTION", margin=UV_MARGIN)
            bpy.ops.object.mode_set(mode="OBJECT")
    finally:
        ts.use_uv_select_sync = keep


def rivet_uvs(ob, rivet_materials):
    """Every rivet dome (a connected patch of rivet-material faces) gets a
    planar projection along its own axis, at the texel density of the rest."""
    me = ob.data
    riv = {i for i, m in enumerate(me.materials) if m and m.name in rivet_materials}
    if not riv:
        return 0
    bm = bmesh.new()
    bm.from_mesh(me)
    uvl = bm.loops.layers.uv["UVMap"]
    a3 = auv = 0.0
    for f in bm.faces:
        if f.material_index in riv:
            continue
        a3 += f.calc_area()
        uvs = [l[uvl].uv for l in f.loops]
        auv += abs(sum(uvs[i].x * uvs[i - 1].y - uvs[i - 1].x * uvs[i].y
                       for i in range(len(uvs)))) / 2
    k = math.sqrt(auv / a3) if a3 > 0 else 1.0
    todo = {f for f in bm.faces if f.material_index in riv}
    count = 0
    while todo:
        seed = todo.pop()
        comp, stack = [seed], [seed]
        while stack:
            f = stack.pop()
            for e in f.edges:
                for g in e.link_faces:
                    if g in todo:
                        todo.remove(g)
                        comp.append(g)
                        stack.append(g)
        n = sum((f.normal * f.calc_area() for f in comp), Vector()).normalized()
        verts = {v for f in comp for v in f.verts}
        c = sum((v.co for v in verts), Vector()) / len(verts)
        u = n.orthogonal().normalized()
        w = n.cross(u)
        ox, oy = (count % 60) * 0.02, (count // 60) * 0.02     # spread out; packing moves them
        for f in comp:
            for l in f.loops:
                d = l.vert.co - c
                l[uvl].uv = (ox + d.dot(u) * k, oy + d.dot(w) * k)
        count += 1
    bm.to_mesh(me)
    bm.free()
    return count


def _image(name, size, colorspace):
    im = bpy.data.images.get(name)
    if im:
        bpy.data.images.remove(im)
    im = bpy.data.images.new(name, size, size, alpha=False)
    im.colorspace_settings.name = colorspace
    return im


def _set_pass(mat, which):
    nt = mat.node_tree
    out = nt.nodes["OUT"]
    for l in list(out.inputs["Surface"].links):
        nt.links.remove(l)
    nt.links.new(nt.nodes["EM_ALBEDO" if which == "albedo" else "EM_ORM"].outputs[0],
                 out.inputs["Surface"])


def final_material(name, img_c, img_d):
    """The generator's layout: base colour, and G -> roughness, B -> metallic."""
    m = bpy.data.materials.get(name)
    if m:
        bpy.data.materials.remove(m)
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    n = nt.nodes
    bsdf = n["Principled BSDF"]
    tc = n.new("ShaderNodeTexImage")
    tc.image = img_c
    tc.location = (-600, 300)
    td = n.new("ShaderNodeTexImage")
    td.image = img_d
    td.location = (-600, -50)
    sep = n.new("ShaderNodeSeparateColor")
    sep.location = (-300, -50)
    nt.links.new(tc.outputs["Color"], bsdf.inputs["Base Color"])
    nt.links.new(td.outputs["Color"], sep.inputs[0])
    nt.links.new(sep.outputs["Green"], bsdf.inputs["Roughness"])
    nt.links.new(sep.outputs["Blue"], bsdf.inputs["Metallic"])
    return m


def bake_root(tank, key, size=TEX, samples=12):
    """Unwrap and bake one root; its objects keep their source materials."""
    _, kids = ROOTS[key]
    objs = [o for o in (bpy.data.objects.get(nm(tank, k)) for k in kids) if o]
    lay(tank, 0.0)                  # the texture belongs to the rest pose
    t0 = time.time()
    unwrap(tank, objs)
    t_uv = time.time() - t0
    img_c = _image("%s_%s_BaseColor" % (tank.PREFIX, key), size, "sRGB")
    img_d = _image("%s_%s_RoughMetal" % (tank.PREFIX, key), size, "Non-Color")
    pre = tank.PREFIX + "_src_"
    mats = {m for o in objs for m in o.data.materials if m and m.name.startswith(pre)}
    sc = bpy.context.scene
    keep = (sc.render.engine, sc.cycles.samples, sc.render.bake.margin,
            sc.render.bake.use_clear, sc.cycles.device)
    sc.render.engine = "CYCLES"
    sc.cycles.samples = samples
    sc.render.bake.margin = BAKE_MARGIN
    sc.render.bake.use_clear = True
    _select_only(objs)
    try:
        for img, which in ((img_c, "albedo"), (img_d, "orm")):
            for m in mats:
                t = m.node_tree.nodes["BAKE_TARGET"]
                t.image = img
                m.node_tree.nodes.active = t
                _set_pass(m, which)
            with bpy.context.temp_override(active_object=objs[0], object=objs[0],
                                           selected_objects=objs, selected_editable_objects=objs):
                bpy.ops.object.bake(type="EMIT", margin=BAKE_MARGIN, use_clear=True)
    finally:
        (sc.render.engine, sc.cycles.samples, sc.render.bake.margin,
         sc.render.bake.use_clear, sc.cycles.device) = keep
        for m in mats:
            _set_pass(m, "albedo")
    img_c.pack()
    img_d.pack()
    return objs, img_c, img_d, round(t_uv, 1), round(time.time() - t0, 1)


def apply_final(tank, key, objs, img_c, img_d):
    m = final_material("%s_%s" % (tank.PREFIX, key), img_c, img_d)
    for o in objs:
        me = o.data
        me.materials.clear()
        me.materials.append(m)
        me.polygons.foreach_set("material_index", np.zeros(len(me.polygons), np.int32))
        if "Ink" in me.attributes:          # only the bake needed it
            me.attributes.remove(me.attributes["Ink"])
    return m


def bake(tank, keys=("Hull", "Turret", "TrackL", "TrackR"), size=TEX, samples=12):
    """Bake, check for grey under the green, finalise.  One or two roots per
    call: four in one MCP call ran past the bridge's timeout."""
    out = {}
    green = {src_name(tank, i) for i in GREEN}
    for key in keys:
        objs, img_c, img_d, t_uv, t = bake_root(tank, key, size, samples)
        out[key] = {"uv_s": t_uv, "total_s": t, "grey": check_grey(objs, img_c.name, green)}
        apply_final(tank, key, objs, img_c, img_d)
    for i in range(len(KINDS)):
        m = bpy.data.materials.get(src_name(tank, i))
        if m and m.users == 0:
            bpy.data.materials.remove(m)
    return out


def check_grey(objs, img_name, green_names):
    """Sample the baked base colour under every green face (centroid and
    corners pulled in 25 %) at mip 0, 2 and 3, count grey samples.  Any is a
    neighbour island bleeding in.  Needs the source materials still on."""
    im = bpy.data.images[img_name]
    W = im.size[0]
    px = np.empty(W * W * 4, np.float32)
    im.pixels.foreach_get(px)
    px = px.reshape(W, W, 4)[..., :3]
    mips = {0: px}
    for lv in (2, 3):
        f = 2 ** lv
        mips[lv] = px.reshape(W // f, f, W // f, f, 3).mean((1, 3))
    out = {}
    for ob in objs:
        me = ob.data
        gm = [i for i, m in enumerate(me.materials) if m and m.name in green_names]
        mi = np.empty(len(me.polygons), np.int32)
        me.polygons.foreach_get("material_index", mi)
        faces = np.where(np.isin(mi, gm))[0]
        if len(faces) == 0:
            continue
        pl = np.empty(len(me.polygons), np.int32)
        me.polygons.foreach_get("loop_start", pl)
        pt = np.empty(len(me.polygons), np.int32)
        me.polygons.foreach_get("loop_total", pt)
        uv = np.empty(len(me.loops) * 2, np.float32)
        me.uv_layers["UVMap"].data.foreach_get("uv", uv)
        uv = uv.reshape(-1, 2)
        pts = []
        for f in faces:
            l = uv[pl[f]:pl[f] + pt[f]]
            c = l.mean(0)
            pts.append(c[None])
            pts.append(l * 0.75 + c * 0.25)
        P = np.concatenate(pts)
        res = {"samples": len(P)}
        for lv, img in mips.items():
            n = img.shape[0]
            ij = np.clip((P * n).astype(int), 0, n - 1)
            c = img[ij[:, 1], ij[:, 0]]
            grey = (c.mean(1) > 0.12) & (c[:, 1] < 1.15 * np.maximum(c[:, 0], c[:, 2]))
            res["mip%d" % lv] = int(grey.sum())
        out[ob.name] = res
    return out


# ---------------------------------------------------------------- measuring

def world_co(ob):
    me = ob.data
    co = np.empty(len(me.vertices) * 3, np.float64)
    me.vertices.foreach_get("co", co)
    m = np.array(ob.matrix_world, dtype=np.float64)
    return co.reshape(-1, 3) @ m[:3, :3].T + m[:3, 3]


def inspect(names=None):
    """Transforms, world bbox, sizes, materials, images and custom
    properties of every object (or of `names`) -- step 1."""
    out = {}
    for o in bpy.data.objects:
        if names and o.name not in names:
            continue
        d = {"type": o.type, "parent": o.parent.name if o.parent else None,
             "loc": [round(v, 4) for v in o.location], "rot": [round(v, 4) for v in o.rotation_euler],
             "scale": [round(v, 4) for v in o.scale], "props": list(o.keys())}
        if o.type == "MESH":
            w = world_co(o)
            d.update(bbox=[w.min(0).round(4).tolist(), w.max(0).round(4).tolist()],
                     verts=len(o.data.vertices),
                     mats=[m.name if m else None for m in o.data.materials],
                     images=sorted({(n.image.name, tuple(n.image.size)) for m in o.data.materials if m
                                    and m.node_tree for n in m.node_tree.nodes
                                    if n.bl_idname == "ShaderNodeTexImage" and n.image}))
        out[o.name] = d
    return out


def sections(parts, cuts, path, scale=0.002, tol=0.003):
    """Vertex clouds cut by planes, drawn into one PNG -- the fastest way to
    read a soup of shells.  parts: {object name: rgb}; cuts: [(cut axis, value,
    u axis, v axis, (u0, u1), (v0, v1))], axes 0/1/2 = x/y/z.  Grid 5 cm, the
    zero lines darker.  Big flat faces have no vertices inside: read walls off
    envelopes, not off one thin cut."""
    clouds = {n: world_co(bpy.data.objects[n]) for n in parts}
    tiles = []
    for ax, val, au, av, ur, vr in cuts:
        W = int((ur[1] - ur[0]) / scale)
        H = int((vr[1] - vr[0]) / scale)
        img = np.ones((H, W, 3), np.float32)
        for g in np.arange(math.ceil(ur[0] / 0.05) * 0.05, ur[1], 0.05):
            img[:, min(int(round((g - ur[0]) / scale)), W - 1)] = 0.85 if abs(g) > 1e-6 else 0.6
        for g in np.arange(math.ceil(vr[0] / 0.05) * 0.05, vr[1], 0.05):
            img[min(int(round((g - vr[0]) / scale)), H - 1), :] = 0.85 if abs(g) > 1e-6 else 0.6
        for n, col in parts.items():
            a = clouds[n]
            m = np.abs(a[:, ax] - val) < tol
            u = ((a[m, au] - ur[0]) / scale).astype(int)
            v = ((a[m, av] - vr[0]) / scale).astype(int)
            ok = (u >= 0) & (u < W) & (v >= 0) & (v < H)
            img[v[ok], u[ok]] = col
        tiles.append(img)
    return _tile_png(tiles, min(4, len(tiles)), path)


def envelope(points, cut_axis, cut, u_axis, bins, v_axis=2, fn="max", tol=0.004):
    """Along u, the max (or min) of v over the points near the plane
    cut_axis = cut: a profile that big flat faces cannot hide."""
    s = points[np.abs(points[:, cut_axis] - cut) < tol]
    f = np.max if fn == "max" else np.min
    out = []
    for u0, u1 in zip(bins[:-1], bins[1:]):
        t = s[(s[:, u_axis] >= u0) & (s[:, u_axis] < u1)]
        out.append((round(float(u0), 4), None if len(t) == 0 else round(float(f(t[:, v_axis])), 4)))
    return out


def radial_profile(points, centre, z0, z1, step_deg=2):
    """Outer radius per plan angle about `centre` in the band z0..z1."""
    s = points[(points[:, 2] >= z0) & (points[:, 2] < z1)]
    d = s[:, :2] - np.asarray(centre)
    r = np.hypot(d[:, 0], d[:, 1])
    ang = np.degrees(np.arctan2(d[:, 1], d[:, 0])) % 360
    out = []
    for a in range(0, 360, step_deg):
        m = (ang >= a) & (ang < a + step_deg)
        out.append((a, round(float(r[m].max()), 4) if m.any() else None))
    return out


def belt_profile(points, tol=0.004):
    """A belt's stadium (arc centres, outer radius, thickness, width, ground)
    and link pitch candidates, the strongest periods of the lower run's vertex
    density (FFT).  The strongest need not be the link: on LT_PARTS it was
    0.046, two alternating links of 0.0236 -- count links on a picture and
    pick.  Assumes front and rear arcs of one radius, as generators draw."""
    z0, z1 = points[:, 2].min(), points[:, 2].max()
    y0, y1 = points[:, 1].min(), points[:, 1].max()
    r_out = (z1 - z0) / 2
    zc = (z0 + z1) / 2
    low = points[points[:, 2] < z0 + 0.004]
    mid = points[(np.abs(points[:, 1] - (y0 + y1) / 2) < 0.15) & (points[:, 2] < zc)]
    thick = float(np.percentile(mid[:, 2], 99) - z0) if len(mid) else None
    ys = low[(low[:, 1] > y0 + r_out) & (low[:, 1] < y1 - r_out)][:, 1]
    span = (y0 + r_out, y1 - r_out)
    h, _ = np.histogram(ys, bins=int((span[1] - span[0]) / 0.0005), range=span)
    f = np.abs(np.fft.rfft(h - h.mean()))
    L = span[1] - span[0]
    k = np.arange(len(f))
    ok = (k > 0) & (L / np.maximum(k, 1) > 0.012) & (L / np.maximum(k, 1) < 0.08)
    best = k[ok][np.argsort(-f[ok])][:4] if ok.any() else []
    return {"centres_y": [round(float(y0 + r_out), 4), round(float(y1 - r_out), 4)],
            "centre_z": round(float(zc), 4), "r_out": round(float(r_out), 4),
            "thickness": None if thick is None else round(thick, 4),
            "x": [round(float(points[:, 0].min()), 4), round(float(points[:, 0].max()), 4)],
            "ground": round(float(z0), 4),
            "pitch_candidates": [round(float(L / kk), 4) for kk in best]}


def texture_stats(img_name, size=512):
    """Percentiles of green, grey and near-black texels of a baked texture.
    The generator bakes shade into hidden shells, so the median is darker
    than the visible paint: calibrate on `window_means` of a render."""
    src = bpy.data.images[img_name]
    c = src.copy()
    c.scale(size, size)
    px = np.empty(size * size * 4, np.float32)
    c.pixels.foreach_get(px)
    bpy.data.images.remove(c)
    px = px.reshape(-1, 4)[:, :3]
    out = {}
    g = px[(px[:, 1] > px[:, 0] * 1.2) & (px[:, 1] > px[:, 2] * 1.5)]
    grey = px[(np.abs(px[:, 0] - px[:, 1]) < 0.03) & (np.abs(px[:, 1] - px[:, 2]) < 0.04)]
    for name, a in (("green", g), ("grey", grey), ("all", px)):
        if len(a):
            o = np.argsort(a.mean(1))
            out[name] = {p: a[o[int((len(a) - 1) * p / 100)]].round(3).tolist() for p in (10, 50, 75, 90)}
    out["dark_frac"] = round(float((px.max(1) < 0.1).mean()), 3)
    return out


def window_means(png, windows, r=10):
    """Mean display colour of small windows {name: (x, y)} (top-left pixels)
    of a rendered PNG: how bright two tanks come out under one light."""
    im = bpy.data.images.load(png, check_existing=False)
    W, H = im.size
    px = np.empty(W * H * 4, np.float32)
    im.pixels.foreach_get(px)
    bpy.data.images.remove(im)
    px = px.reshape(H, W, 4)[::-1]
    return {k: px[y - r:y + r, x - r:x + r, :3].reshape(-1, 3).mean(0).round(3).tolist()
            for k, (x, y) in windows.items()}


# ---------------------------------------------------------------- rendering

def views(c):
    """Cameras relative to a tank's centre c: (location, target or
    {"euler": ...}, lens or ortho scale, ortho)."""
    cx, cy, cz = c

    def P(x, y, z):
        return (cx + x, cy + y, cz + z)
    return {
        "iso_fl": (P(1.2, -1.55, 1.05), P(0, -0.03, 0.0), 42, False),
        "iso_fr": (P(-1.2, -1.55, 1.05), P(0, -0.03, 0.0), 42, False),
        "iso_rl": (P(1.3, 1.6, 1.0), P(0, 0.02, 0.0), 42, False),
        "iso_rr": (P(-1.3, 1.6, 1.0), P(0, 0.02, 0.0), 42, False),
        "side": (P(3.0, 0.0, -0.02), P(0.0, 0.0, -0.02), 1.08, True),
        "side_l": (P(-3.0, 0.0, -0.02), P(0.0, 0.0, -0.02), 1.08, True),
        "top": (P(0.0, 0.0, 3.0), {"euler": (0.0, 0.0, 0.0)}, 1.12, True),
        "front": (P(0.0, -3.0, 0.0), P(0.0, 0.0, 0.0), 0.95, True),
        "rear": (P(0.0, 3.0, 0.0), P(0.0, 0.0, 0.0), 0.95, True),
        "sprite": (P(0, -2.6, 1.5), P(0, 0, 0.0), 1.45, True),
        "front_close": (P(0.55, -1.25, 0.25), P(0.05, -0.35, -0.12), 50, False),
        "mantlet": (P(0.25, -1.0, 0.35), P(0.0, -0.2, 0.15), 60, False),
        "turret_close": (P(0.6, -0.9, 0.72), P(0, -0.1, 0.19), 50, False),
        "roof": (P(0.0, 0.01, 2.0), {"euler": (0.0, 0.0, 0.0)}, 0.58, True),
        "track_close": (P(1.0, -0.5, 0.02), P(0.3, -0.2, -0.18), 45, False),
        "rear_close": (P(-0.55, 1.0, 0.52), P(0, 0.35, 0.02), 45, False),
        "low_fl": (P(0.9, -1.3, -0.13), P(0, -0.1, -0.08), 35, False),
        "base_low": (P(0.55, -0.85, 0.06), P(0.0, -0.02, 0.02), 55, False),
    }


def _aim(cam, loc, target, lens, ortho):
    cam.location = loc
    if isinstance(target, dict):
        cam.rotation_euler = target["euler"]
    else:
        cam.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat("-Z", "Y").to_euler()
    cam.data.type = "ORTHO" if ortho else "PERSP"
    if ortho:
        cam.data.ortho_scale = lens
    else:
        cam.data.lens = lens
    cam.data.clip_start = 0.01


def shoot(names, path, centre, only=None, w=760, h=560, cols=2, samples=24, extra=None):
    """Render views about `centre` with a preview light of its own (scene
    lights and pipeline effects hidden, world colour put back), tile into
    one PNG.  `only(ob)` limits the meshes rendered."""
    sc = bpy.context.scene
    cam = sc.camera
    V = views(centre)
    V.update(extra or {})
    keep = (cam.location.copy(), cam.rotation_euler.copy(), cam.data.type, cam.data.lens,
            cam.data.ortho_scale, cam.data.clip_start, sc.render.resolution_x,
            sc.render.resolution_y, sc.render.filepath, sc.eevee.taa_render_samples,
            sc.render.film_transparent)
    hidden = {}
    for o in sc.objects:
        if o.name in FX or o.type == "LIGHT" or (only is not None and o.type == "MESH" and not only(o)):
            hidden[o.name] = o.hide_render
            o.hide_render = True
    bg = sc.world.node_tree.nodes["Background"]
    wkeep = (tuple(bg.inputs[0].default_value), bg.inputs[1].default_value)
    bg.inputs[0].default_value = (0.42, 0.45, 0.5, 1)
    bg.inputs[1].default_value = 0.6
    tmp = []
    for n, rot, en in (("_pv_key", (math.radians(50), 0, math.radians(35)), 3.2),
                       ("_pv_fill", (math.radians(60), 0, math.radians(-140)), 1.0)):
        ld = bpy.data.lights.new(n, "SUN")
        ld.energy = en
        ld.angle = math.radians(8)
        lo = bpy.data.objects.new(n, ld)
        lo.rotation_euler = rot
        sc.collection.objects.link(lo)
        tmp.append(lo)
    sc.render.resolution_x, sc.render.resolution_y = w, h
    sc.eevee.taa_render_samples = samples
    sc.render.film_transparent = False
    tiles = []
    d = os.path.dirname(path)
    try:
        for v in names:
            _aim(cam, *V[v])
            p = os.path.join(d, "_v_%s.png" % v)
            sc.render.filepath = p
            bpy.ops.render.render(write_still=True)
            im = bpy.data.images.load(p, check_existing=False)
            px = np.empty(w * h * 4, np.float32)
            im.pixels.foreach_get(px)
            tiles.append(px.reshape(h, w, 4)[..., :3])
            bpy.data.images.remove(im)
    finally:
        for lo in tmp:
            ld = lo.data
            bpy.data.objects.remove(lo)
            bpy.data.lights.remove(ld)
        for n, v in hidden.items():
            sc.objects[n].hide_render = v
        bg.inputs[0].default_value = wkeep[0]
        bg.inputs[1].default_value = wkeep[1]
        (cam.location, cam.rotation_euler, cam.data.type, cam.data.lens, cam.data.ortho_scale,
         cam.data.clip_start, sc.render.resolution_x, sc.render.resolution_y, sc.render.filepath,
         sc.eevee.taa_render_samples, sc.render.film_transparent) = keep
    return _tile_png(tiles, cols, path, top_down=True)


def compare(tank, names, name, w=760, h=560, samples=24, extra=None):
    """Each view twice, the original left and the copy right, same camera
    relative to each tank's centre.  Returns the PNG path."""
    d = out_dir(tank)
    orig = set(NAMES[k] for k in ("hull_geo", "engine", "turret_geo", "barrel",
                                  "l_cat", "l_rolls", "r_cat", "r_rolls"))
    a = shoot(names, os.path.join(d, "_cmp_a.png"), (0, 0, 0),
              only=lambda o: o.name in orig, w=w, h=h, cols=1, samples=samples, extra=extra)
    b = shoot(names, os.path.join(d, "_cmp_b.png"), (tank.X_OFF, 0, 0),
              only=lambda o: o.name.endswith(tank.SFX), w=w, h=h, cols=1, samples=samples,
              extra=extra)
    A, B = _read_png(a), _read_png(b)
    C = np.concatenate([A, np.ones((A.shape[0], 6, 3), np.float32), B], axis=1)
    return _write_png(C, os.path.join(d, name + ".png"))


def _read_png(p):
    im = bpy.data.images.load(p, check_existing=False)
    W, H = im.size
    px = np.empty(W * H * 4, np.float32)
    im.pixels.foreach_get(px)
    bpy.data.images.remove(im)
    return px.reshape(H, W, 4)[..., :3]


def _write_png(rgb, path):
    H, W = rgb.shape[:2]
    a = np.ones((H, W, 4), np.float32)
    a[..., :3] = rgb
    im = bpy.data.images.new("_png", W, H)
    im.pixels.foreach_set(a.ravel())
    im.filepath_raw = path
    im.file_format = "PNG"
    im.save()
    bpy.data.images.remove(im)
    return path


def _tile_png(tiles, cols, path, top_down=True):
    """Tiles in reading order (first top-left) into one PNG; Blender image
    rows run bottom-up."""
    h = max(t.shape[0] for t in tiles)
    w = max(t.shape[1] for t in tiles)
    rows = math.ceil(len(tiles) / cols)
    can = np.ones((rows * (h + 6), cols * (w + 6), 3), np.float32) * 0.1
    for i, t in enumerate(tiles):
        r, c = divmod(i, cols)
        y0 = (rows - 1 - r) * (h + 6) if top_down else r * (h + 6)
        can[y0:y0 + t.shape[0], c * (w + 6):c * (w + 6) + t.shape[1]] = t
    return _write_png(can, path)


# ------------------------------------------------------------------- checks

def verify(tank):
    """What every copy has to pass before anyone looks at pictures: the ring
    axis measured on the copy (read-only), belts on the original's ground,
    one baked material per root, UVMap, no custom properties, no source
    materials left, the scene's engine back on EEVEE, the gun on its
    trunnion and at rest (`check_lay`)."""
    import sys
    if REPO not in sys.path:
        sys.path.insert(0, REPO)
    import turret_axis
    importlib.reload(turret_axis)
    cfg = dict(turret_axis.CONFIG)
    cfg.update(turret=nm(tank, "turret_geo"), hull=nm(tank, "hull_geo"),
               symmetry_x=tank.X_OFF, apply=False, stamp=False)
    _, _, axis, seam_z, rep = turret_axis.measure(cfg)
    tw = bpy.data.objects[nm(tank, "turret")].matrix_world.translation
    out = {"axis": [round(v, 5) for v in axis],
           "axis_minus_root": [round(axis[0] - tw[0], 5), round(axis[1] - tw[1], 5)],
           "roundness": rep["roundness"], "band": rep["band"], "warnings": rep["warnings"],
           "rotatable": rep["rotatable"].get("free")}
    ground = min(world_co(bpy.data.objects[NAMES[k]])[:, 2].min() for k in ("l_cat", "r_cat")
                 if bpy.data.objects.get(NAMES[k]))
    belts = min(world_co(bpy.data.objects[nm(tank, k)])[:, 2].min() for k in ("l_cat", "r_cat"))
    out["ground_original"] = round(float(ground), 4)
    out["ground_copy"] = round(float(belts), 4)
    coll = bpy.data.collections[coll_name(tank)]
    out["materials"] = {o.name: [m.name for m in o.data.materials]
                        for o in coll.objects if o.type == "MESH"}
    out["no_uvmap"] = [o.name for o in coll.objects if o.type == "MESH"
                       and "UVMap" not in o.data.uv_layers]
    out["custom_props"] = {o.name: list(o.keys()) for o in coll.objects if o.keys()}
    out["source_left"] = [src_name(tank, i) for i in range(len(KINDS))
                          if bpy.data.materials.get(src_name(tank, i))]
    out["engine"] = bpy.context.scene.render.engine
    out["gun"] = check_lay(tank)
    return out


def save(tank, path=None):
    """Kit and tank module into the scene as texts, then a *copy* of the
    scene to a new file: the open file stays the user's to save (saving it
    would also overwrite their .blend1)."""
    for src in (os.path.abspath(__file__), tank.__file_path__):
        t = bpy.data.texts.get(os.path.basename(src)) or bpy.data.texts.new(os.path.basename(src))
        t.clear()
        t.write(open(src, encoding="utf-8").read())
    path = path or os.path.join(ASSETS, "Scenes", tank.NAME + "_Repro.blend")
    bpy.ops.wm.save_as_mainfile(filepath=path, copy=True)
    return {"saved": path, "size_mb": round(os.path.getsize(path) / 1e6, 1),
            "open_file": bpy.data.filepath}
