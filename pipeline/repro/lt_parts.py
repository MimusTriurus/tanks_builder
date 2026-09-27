"""LT_PARTS, rebuilt from scratch on repro_kit (docs/repro.md).

The first copy, 2026-09-27.  It stands 1.0 to the +X of the original in the
same scene, in collection `LT_PARTS.Repro`, every name suffixed `.Repro`.

Every number is in LT_PARTS's own frame -- front -Y, its ground at z -0.3844,
its left belt at -X, ring axis (0, -0.0212) -- measured off its vertices, so
each one can be checked against the original directly.
"""

import math
import bmesh
import numpy as np
from mathutils import Matrix, Vector

from repro_kit import (
    PAINT, GUN, RUBBER, TRACK, GLASS, DARK, PAINT_T, PAINTDK, RIVET, RIVET_G, RIV,
    Group, new_bm, box, prism, lathe, rounded_rect_profile, cyl, rivets, line,
    bend_bar, hull_solid, offset_path, bent_plate, clip_path_y, clip_polygon,
    ring_prism_y, tilt, rr_loop, d_section, loft, belt_path, place_belt,
)

NAME = "LT_PARTS"
SFX = ".Repro"
X_OFF = 1.0
PREFIX = "LTR"
GROUND = -0.3844
RING_C = (0.0, -0.0212)
TRACK_X = 0.2646

# What the original's *visible* surfaces carry (sRGB as stored): its texture
# median (#1f3c19) is dragged down by shade the generator baked into hidden
# shells, and under one light it rendered ~1.5x brighter than that median.
PALETTE = {
    PAINT:   dict(base="#2e591f", light="#447726", dark="#1b3a13", ink="#08120a", rough=0.96, metal=0.0),
    PAINT_T: dict(base="#2e591f", light="#447726", dark="#1b3a13", ink="#08120a", rough=0.96, metal=0.0),
    PAINTDK: dict(base="#1d3a15", light="#2c5220", dark="#11240d", ink="#07100a", rough=0.96, metal=0.0),
    RIVET:   dict(base="#3a6a23", light="#4f8a2e", dark="#1b3a13", ink="#08120a", rough=0.96, metal=0.0),
    GUN:     dict(base="#4a4a4d", light="#737275", dark="#28282a", ink="#0d0d0f", rough=0.58, metal=0.65),
    RIVET_G: dict(base="#555558", light="#7c7b7e", dark="#28282a", ink="#0d0d0f", rough=0.58, metal=0.65),
    TRACK:   dict(base="#40435a", light="#646881", dark="#25262f", ink="#0b0b0f", rough=0.95, metal=0.0),
    RUBBER:  dict(base="#1d1d1f", light="#2e2e31", dark="#121213", ink="#0a0a0b", rough=0.9, metal=0.0),
    GLASS:   dict(base="#d6d6d2", light="#f4f4ee", dark="#9a9a96", ink="#3a3a3a", rough=0.2, metal=0.0),
    DARK:    dict(base="#101211", light="#1c1e1d", dark="#0a0b0a", ink="#060606", rough=0.9, metal=0.0),
}

# ------------------------------------------------------------------- layout

DECK_Z = -0.013
SHELF_Z = -0.064
BELLY_Z = -0.339
CORE_X = 0.172            # hull between the tracks
SUPER_X = 0.225           # superstructure above the shelves
FENDER_X = (0.172, 0.335)
RAIL_X = (0.335, 0.357)
# The skirt reads thick from the front, as the original's does (outer face
# 0.383, inner 0.347 above z -0.135): a 27 mm plate full height -- its inner
# face clears the belt, whose outer edge is x 0.354 -- and a beam along the
# top out to 0.347, above the belt's top run (-0.128).
SKIRT_X = (0.357, 0.384)          # the plate
SKIRT_BEAM = (0.347, -0.125)      # inner x, bottom z of the top beam
REAR_Y = 0.497

# nose: lower plate, near-vertical front plate, a 25 deg lower glacis, a 58 deg
# upper glacis, deck; rear: rounded top edge, plate, chamfer to the belly.
# The core stops inside the superstructure (top at -0.090, rear 8 mm inside the
# rear plate): two solids sharing a face z-fight and bake black in AO.
CORE = [(-0.392, BELLY_Z), (0.300, BELLY_Z), (0.450, -0.280), (0.474, -0.226),
        (REAR_Y - 0.008, -0.160), (REAR_Y - 0.008, -0.090), (-0.300, -0.090),
        (-0.305, -0.095), (-0.452, -0.150), (-0.466, -0.160), (-0.459, -0.245)]
GLACIS_LO = (Vector((0.0, -0.452, -0.150)), Vector((0.0, -0.305, -0.095)))
GLACIS_HI = (Vector((0.0, -0.305, -0.095)), Vector((0.0, -0.255, DECK_Z)))

# fender: a bent plate over the track, a box-like mudguard over the idler (the
# lamp stands on it) and a rounded end behind the sprocket
FENDER = [(-0.472, -0.172), (-0.472, -0.118), (-0.463, -0.100), (-0.442, -0.089),
          (-0.360, -0.083), (-0.240, SHELF_Z), (0.385, SHELF_Z), (0.428, -0.075),
          (0.452, -0.098), (0.464, -0.128), (0.464, -0.160)]
SEAMS = (-0.216, -0.005, 0.205)       # skirt panels, fender plates

SKIRT = [(-0.337, -0.340), (-0.457, -0.287), (-0.457, -0.137), (-0.385, -0.066),
         (0.375, -0.066), (0.445, -0.137), (0.445, -0.287), (0.300, -0.340)]

# running gear: the belt is a stadium, not a hull of the wheels
BELT_C = ((-0.339, -0.256), (0.351, -0.256))
BELT_RIN = 0.107
BELT_T = 0.021
BELT_W = 0.178
PITCH = 0.0236
IDLER = (-0.345, -0.268, 0.085)
SPROCKET = (0.355, -0.232, 0.072)      # teeth stay inside the belt
ROAD = [(-0.160, -0.300, 0.062), (0.000, -0.300, 0.062), (0.170, -0.300, 0.062),
        (0.305, -0.322, 0.040)]
RETURN = [(-0.150, -0.196, 0.045), (0.060, -0.196, 0.045)]

# turret: a D-plan frustum, flat front, round back
T_RING = (0.014, 0.035, 0.176)          # z0, z1, r
RING_Z0 = T_RING[0]                     # the turret root sits at the ring's foot
T_SECTIONS = [  # z, half width, front y, rear y
    (0.029, 0.238, -0.228, 0.256),     # foot 9 mm over the collar; the ring owns 0.014-0.029
    (0.130, 0.214, -0.221, 0.242),
    (0.240, 0.185, -0.192, 0.225),
    (0.268, 0.173, -0.172, 0.215),
    (0.284, 0.156, -0.152, 0.200),
]
GUN_Z = 0.153
FRAME = dict(cz=0.155, ohw=0.125, ohh=0.083, ihw=0.083, ihh=0.066, r_o=0.028, r_i=0.014,
             y_back=-0.180, y_front=-0.249)
BLOCK = dict(hw=0.074, z0=0.094, z1=0.222, y_back=-0.185, y_front=-0.247)
CUPOLA = (-0.050, 0.095)
ROOF_Z = 0.284
ROOF_SEAMS = (RING_C[1], -0.106)     # plate seams across the roof (world y), as the original draws


def turret_ink(b, pos, nrm):
    """Painted panel lines on the turret, in the mesh's object space -- the
    ring's frame: 12 vertical seams by plan angle (none across the front, the
    mantlet lives there), one line round under the roof band, and the roof
    plates' seams straight across, over the rounded edge to meet the walls'."""
    N, w = 12, 0.0026
    p = b.n.new("ShaderNodeSeparateXYZ")
    b.put(p.inputs[0], pos)
    ang = b.math("ARCTAN2", p.outputs["Y"], p.outputs["X"])
    f = b.math("MULTIPLY", ang, N / math.tau)
    d = b.math("ABSOLUTE", b.math("SUBTRACT", b.math("FRACT", b.math("ADD", f, 0.5)), 0.5))
    rxy = b.math("SQRT", b.math("ADD", b.math("MULTIPLY", p.outputs["X"], p.outputs["X"]),
                                b.math("MULTIPLY", p.outputs["Y"], p.outputs["Y"])))
    dist = b.math("MULTIPLY", d, b.math("MULTIPLY", rxy, math.tau / N))
    stroke = b.rng(dist, w * 0.5 + 0.0007, w * 0.5, smooth=True)
    q = b.n.new("ShaderNodeSeparateXYZ")
    b.put(q.inputs[0], nrm)
    wall = b.rng(b.math("ABSOLUTE", q.outputs["Z"]), 0.75, 0.55)
    front = b.rng(b.math("ABSOLUTE", b.math("SUBTRACT", ang, -math.pi / 2)), 0.25, 0.32)
    above = b.rng(p.outputs["Z"], 0.024, 0.030)
    vert = b.math("MULTIPLY", b.math("MULTIPLY", stroke, wall), b.math("MULTIPLY", front, above))
    hz = b.line(p.outputs["Z"], 0.252 - T_RING[0], w)
    out = b.math("MAXIMUM", vert, b.math("MULTIPLY", hz, wall))
    up = b.math("MULTIPLY", b.rng(q.outputs["Z"], 0.55, 0.7),
                b.rng(p.outputs["Z"], ROOF_Z - T_RING[0] - 0.03, ROOF_Z - T_RING[0] - 0.02))
    for y in ROOF_SEAMS:
        out = b.math("MAXIMUM", out, b.math("MULTIPLY", b.line(p.outputs["Y"], y - RING_C[1], w), up))
    return out


# ------------------------------------------------------------------- tracks

def link_mesh(pitch):
    """One shoe (u along the belt, v across, w out): plate, three pads, horn, pin."""
    bm = new_bm()
    hw = BELT_W / 2
    box(bm, (0, 0, 0.004), (pitch * 0.9, BELT_W, 0.008), TRACK)
    for v in (-0.058, 0.0, 0.058):
        box(bm, (0, v, 0.0145), (pitch * 0.76, 0.049, 0.013), TRACK)
    box(bm, (0, 0, -0.006), (pitch * 0.5, 0.016, 0.012), TRACK)
    cyl(bm, (pitch * 0.46, -hw + 0.004, 0.004), (pitch * 0.46, hw - 0.004, 0.004), 0.0034,
        TRACK, seg=10)
    return bm


def belt(mats, xc):
    path = belt_path([(BELT_C[0][0], BELT_C[0][1], BELT_RIN), (BELT_C[1][0], BELT_C[1][1], BELT_RIN)])
    bm, n, pitch, L = place_belt(mats, xc, path, PITCH, link_mesh)
    return bm, {"links": n, "pitch": round(pitch, 5), "length": round(L, 4)}


def disc(bm, y, z, r, xc, w=0.040, seg=48, mi=TRACK):
    """Dished wheel disc centred at xc: rim, dish, hub boss (lathe about X)."""
    h = w / 2
    prof = [(-h, 0.0), (-h, r * 0.93), (-h + 0.004, r), (h - 0.004, r), (h, r * 0.93),
            (h, r * 0.78), (h - 0.006, r * 0.72), (h - 0.006, r * 0.42), (h + 0.002, r * 0.36),
            (h + 0.002, r * 0.2), (h + 0.008, r * 0.16), (h + 0.008, 0.0)]
    lathe(bm, prof, mi, seg=seg, axis="X", center=(xc, y, z))


def twin_wheel(bm, y, z, r, xc, s, seg=48):
    for dx in (-0.045, 0.045):
        disc(bm, y, z, r, xc + dx * s, seg=seg)
    cyl(bm, (xc - 0.03, y, z), (xc + 0.03, y, z), r * 0.3, TRACK, seg=24)


def rolls(mats, xc, s):
    g = Group(mats)
    bm = new_bm()
    y, z, r = IDLER
    twin_wheel(bm, y, z, r, xc, s, seg=64)
    for (y, z, r) in ROAD + RETURN:
        twin_wheel(bm, y, z, r, xc, s)
    y, z, r = SPROCKET
    twin_wheel(bm, y, z, r - 0.008, xc, s, seg=64)
    g.add(bm, subsurf=1)
    # sprocket teeth, two rings
    bm = new_bm()
    y, z, r = SPROCKET
    for k in range(12):
        t = math.tau * k / 12
        rot = Matrix.Rotation(t, 3, "X")
        for dx in (-0.045, 0.045):
            c = Vector((xc + dx * s, y, z)) + rot @ Vector((0, 0, r - 0.006))
            box(bm, c, (0.03, 0.016, 0.02), TRACK, rot=rot)
    g.add(bm, bevel=(0.002, 2, 40))
    # suspension: a long beam, bogie arms down to the road wheels, axles
    bm = new_bm()
    xin = xc - s * 0.075
    box(bm, (xin, 0.02, -0.235), (0.018, 0.52, 0.022), TRACK)
    for (y, z, r) in ROAD[:3]:
        a, b_ = Vector((xin, y + 0.05, -0.235)), Vector((xin, y, z))
        d = b_ - a
        rot = Vector((0, 1, 0)).rotation_difference(d.normalized()).to_matrix()
        box(bm, (a + b_) / 2, (0.014, d.length + 0.02, 0.018), TRACK, rot=rot)
        cyl(bm, (xin - s * 0.012, y + 0.05, -0.235), (xin + s * 0.012, y + 0.05, -0.235),
            0.013, TRACK, seg=16)
    for (y, z, r) in [IDLER, SPROCKET] + ROAD + RETURN:
        cyl(bm, (xin, y, z), (xc, y, z), 0.011, TRACK, seg=12)
    g.add(bm, bevel=(0.003, 2, 40), subsurf=1)
    return g


# --------------------------------------------------------------------- hull

def glacis_pt(gl, t, x):
    p = gl[0].lerp(gl[1], t)
    return Vector((x, p.y, p.z))


def glacis_n(gl):
    d = gl[1] - gl[0]
    return Vector((0.0, -d.z, d.y)).normalized()


def super_points():
    """Down to the glacis joint, so its front face *is* the upper glacis; the
    rear face 8 mm inside the rear plate's."""
    zs, zd, yr = -0.095, DECK_Z, REAR_Y - 0.003
    pts = []
    for s in (-1, 1):
        pts += [(s * 0.170, -0.305, zs), (s * SUPER_X, -0.228, zs), (s * SUPER_X, yr, zs),
                (s * 0.170, -0.255, zd), (s * SUPER_X, -0.195, zd), (s * SUPER_X, 0.470, zd),
                (s * SUPER_X, 0.487, zd - 0.006), (s * SUPER_X, yr, zd - 0.022)]
    return pts


def hull(mats):
    g = Group(mats)

    # --- body between the tracks
    bm = new_bm()
    prism(bm, CORE, -CORE_X, CORE_X, PAINT)
    g.add(bm, bevel=(0.008, 2, 20))

    # --- superstructure: chamfered front corners, flat deck
    bm = new_bm()
    hull_solid(bm, super_points(), PAINT)
    g.add(bm, bevel=(0.008, 2, 20))

    # --- rear plate, as wide as the superstructure, down behind the sprockets
    bm = new_bm()
    box(bm, (0, REAR_Y - 0.002, -0.105), (2 * SUPER_X, 0.014, 0.12), PAINT)   # face y 0.502
    g.add(bm, bevel=(0.006, 2, 30))
    bm = new_bm()      # its raised panel
    box(bm, (0, REAR_Y + 0.006, -0.1025), (0.36, 0.007, 0.105), PAINT)       # face y 0.5065
    g.add(bm, bevel=(0.0025, 2, 30))

    # --- fenders: four plates a side (seams over the skirt seams), rails
    cuts = [None] + [c for c in SEAMS] + [None]
    bm = new_bm()
    for s in (-1, 1):
        x0, x1 = sorted((s * FENDER_X[0], s * FENDER_X[1]))
        for a, b in zip(cuts, cuts[1:]):
            pa = clip_path_y(FENDER, None if a is None else a + 0.0015,
                             None if b is None else b - 0.0015)
            bent_plate(bm, pa, 0.010, x0, x1, PAINT)
    g.add(bm, bevel=(0.004, 2, 30))
    bm = new_bm()
    rail = [p for p in offset_path(FENDER, -0.018)]
    rail = [p for p in rail if p[1] > -0.135]
    for s in (-1, 1):
        x0, x1 = sorted((s * RAIL_X[0], s * RAIL_X[1]))
        bent_plate(bm, rail, 0.026, x0, x1, PAINT)
    g.add(bm, bevel=(0.004, 2, 30))

    # --- skirts: four panels a side, each a thick plate with a beam behind its
    # top edge (per panel, so the seams cut both)
    bm = new_bm()
    edges = [-0.5] + list(SEAMS) + [0.5]
    for s in (-1, 1):
        for a, b in zip(edges, edges[1:]):
            ya, yb = a + 0.002, b - 0.002
            x0, x1 = sorted((s * SKIRT_X[0], s * SKIRT_X[1]))
            prism(bm, clip_polygon(SKIRT, ya, yb), x0, x1, PAINT)
            x0, x1 = sorted((s * SKIRT_BEAM[0], s * SKIRT_X[0]))
            prism(bm, clip_polygon(SKIRT, ya, yb, za=SKIRT_BEAM[1]), x0, x1, PAINT)
    g.add(bm, bevel=(0.004, 2, 30))
    # hinges across the seams, handles front and rear
    bm = new_bm()
    for s in (-1, 1):
        xf = s * SKIRT_X[1]
        for ys in SEAMS:
            for zz in (-0.125, -0.255):
                box(bm, (xf + s * 0.002, ys, zz), (0.004, 0.034, 0.024), PAINT)
                cyl(bm, (xf + s * 0.005, ys, zz - 0.012), (xf + s * 0.005, ys, zz + 0.012),
                    0.0042, PAINT, seg=12)
    g.add(bm, bevel=(0.0015, 2, 30), subsurf=1)
    bm = new_bm()
    for s in (-1, 1):
        xf = s * SKIRT_X[1]
        for yc in (-0.338, 0.322):
            x1 = xf + s * 0.026
            bend_bar(bm, [Vector((xf, yc - 0.036, -0.174)), Vector((x1, yc - 0.036, -0.174)),
                          Vector((x1, yc + 0.036, -0.174)), Vector((xf, yc + 0.036, -0.174))],
                     0.0062, GUN, seg=10)
            for dy in (-0.036, 0.036):
                cyl(bm, (xf - s * 0.001, yc + dy, -0.174), (xf + s * 0.004, yc + dy, -0.174),
                    0.009, GUN, seg=16)
    g.add(bm)

    # --- rivets
    bm = new_bm()
    for s in (-1, 1):
        xf = s * (SKIRT_X[1] + 0.0004)
        for a, b in zip(edges, edges[1:]):
            a, b = max(a, -0.44), min(b, 0.43)
            ya, yb = a + 0.016, b - 0.016
            ta, tb = max(ya, -0.372), min(yb, 0.360)      # clear of the top chamfers
            n_top = max(2, int(round((tb - ta) / 0.055)) + 1)
            rivets(bm, [(xf, y, -0.081) for y in np.linspace(ta, tb, n_top)], (s, 0, 0))
            lo_a, lo_b = max(ya, -0.33), min(yb, 0.29)
            n_lo = max(2, int(round((lo_b - lo_a) / 0.055)) + 1)
            rivets(bm, [(xf, y, -0.325) for y in np.linspace(lo_a, lo_b, n_lo)], (s, 0, 0))
        # superstructure wall
        rivets(bm, line((s * (SUPER_X + 0.0004), -0.17, -0.04), (s * (SUPER_X + 0.0004), 0.47, -0.04), 12),
               (s, 0, 0))
        # mudguard fronts
        for xx in (0.20, 0.235, 0.30):
            rivets(bm, [(s * xx, -0.4724, -0.158)], (0, -1, 0))
    n = glacis_n(GLACIS_LO)
    rivets(bm, [glacis_pt(GLACIS_LO, 0.12, x) + n * 0.0004 for x in np.linspace(-0.15, 0.15, 7)], n)
    rivets(bm, [glacis_pt(GLACIS_LO, 0.88, x) + n * 0.0004 for x in (-0.15, -0.1, 0.1, 0.15)], n)
    n = glacis_n(GLACIS_HI)
    rivets(bm, [glacis_pt(GLACIS_HI, 0.8, x) + n * 0.0004 for x in (-0.15, -0.1, 0.1, 0.15)], n)
    rivets(bm, [(x, -0.4605, -0.232) for x in np.linspace(-0.15, 0.15, 6)], (0, -1, 0))
    rivets(bm, line((-0.17, REAR_Y + 0.0099, -0.059), (0.17, REAR_Y + 0.0099, -0.059), 8), (0, 1, 0))
    rivets(bm, line((-0.17, REAR_Y + 0.0099, -0.146), (0.17, REAR_Y + 0.0099, -0.146), 8), (0, 1, 0))
    for k in range(20):     # on the collar's slope
        t = math.tau * (k + 0.5) / 20
        c, s_ = math.cos(t), math.sin(t)
        nrm = Vector((c * 0.76, s_ * 0.76, 0.65))
        rivets(bm, [Vector((RING_C[0] + 0.2035 * c, RING_C[1] + 0.2035 * s_, 0.0015)) + nrm * 0.0004],
               nrm, r=0.0048)
    g.add(bm, subsurf=1)

    # --- driver's visor where the glaces meet: its foot sinks into the lower
    # glacis, its face leans back like the original's, a brow over the slot
    bm = new_bm()
    hull_solid(bm, [(sx * 0.056, -0.343, -0.112) for sx in (-1, 1)] +
               [(sx * 0.052, -0.324, -0.036) for sx in (-1, 1)] +
               [(sx * 0.046, -0.316, -0.028) for sx in (-1, 1)] +
               [(sx * 0.046, -0.262, -0.028) for sx in (-1, 1)] +
               [(sx * 0.056, -0.262, -0.100) for sx in (-1, 1)], PAINT)
    g.add(bm, bevel=(0.006, 2, 30))
    bm = new_bm()
    box(bm, (0, -0.330, -0.040), (0.090, 0.012, 0.008), PAINT)
    g.add(bm, bevel=(0.003, 2, 30), subsurf=1)
    bm = new_bm()
    box(bm, (0, -0.3305, -0.058), (0.060, 0.004, 0.010), DARK,
        rot=Matrix.Rotation(math.atan2(0.019, 0.074), 3, "X"))
    g.add(bm, bevel=(0.0015, 1, 30))

    # --- tow shackles on the front plate
    bm = new_bm()
    for x in (-0.12, 0.12):
        box(bm, (x, -0.474, -0.163), (0.032, 0.022, 0.030), PAINT)
        cyl(bm, (x - 0.02, -0.482, -0.170), (x + 0.02, -0.482, -0.170), 0.0055, GUN, seg=12)
        ring = [Vector((x + 0.024 * math.sin(t), -0.488, -0.194 + 0.024 * math.cos(t)))
                for t in np.linspace(0, math.tau, 25)]
        bend_bar(bm, ring, 0.0068, GUN, seg=10)
    g.add(bm, bevel=(0.003, 2, 30), subsurf=1)

    # --- headlights standing on the mudguards
    bm = new_bm()
    bmg = new_bm()
    for s in (-1, 1):
        c = (s * 0.262, -0.402, -0.047)
        box(bm, (c[0], -0.395, -0.080), (0.03, 0.04, 0.02), PAINT)
        lathe(bm, rounded_rect_profile(-0.030, 0.030, 0.0, 0.036, 0.008, n=3), PAINT,
              seg=48, axis="Y", center=c)
        lathe(bm, rounded_rect_profile(-0.036, -0.024, 0.026, 0.037, 0.004, n=2), PAINT,
              seg=48, axis="Y", center=c)
        lathe(bmg, [(-0.033, 0.0), (-0.032, 0.012), (-0.029, 0.021), (-0.026, 0.027)],
              GLASS, seg=48, axis="Y", center=c, closed=False)
    g.add(bm, subsurf=1)
    g.add(bmg, subsurf=1)

    # --- rear lamps on the fender ends, tow blocks under the rear plate
    bm = new_bm()
    for s in (-1, 1):
        c = (s * 0.285, 0.405, -0.046)
        lathe(bm, rounded_rect_profile(-0.02, 0.02, 0.0, 0.022, 0.009, n=3), PAINT,
              seg=32, axis="Z", center=c)
        box(bm, (s * 0.12, 0.470, -0.262), (0.04, 0.03, 0.035), PAINT)
    g.add(bm, bevel=(0.004, 2, 30), subsurf=1)

    # --- turret ring collar: a raised race round the ring, up to z 0.020 like
    # the original's (0.017), sloping down to the deck, so the turret sits on
    # it instead of floating a finger's width above the deck
    bm = new_bm()
    lathe(bm, [(DECK_Z - 0.006, 0.180), (DECK_Z - 0.006, 0.215), (DECK_Z + 0.001, 0.215),
               (0.008, 0.198), (0.020, 0.190), (0.020, 0.180)],
          PAINTDK, seg=160, axis="Z", center=(RING_C[0], RING_C[1], 0))
    g.add(bm, bevel=(0.0025, 2, 30))
    return g


def engine(mats):
    """Two radiator grilles on the rear deck -- the exhaust source."""
    g = Group(mats)
    y0, y1 = 0.196, 0.440      # clear of the ring collar (r 0.215)
    for s in (-1, 1):
        cx = s * 0.1095
        bm = new_bm()
        outer = rr_loop(cx, 0.0, 0.0875, (y1 - y0) / 2, 0.022)
        inner = rr_loop(cx, 0.0, 0.0755, (y1 - y0) / 2 - 0.012, 0.012)
        # frame lies flat: loops are (x, y) here, lifted to z below
        ring_prism_y(bm, [(x, z) for x, z in outer], [(x, z) for x, z in inner], 0.0, 1.0, PAINT)
        for v in bm.verts:
            x, yy, zz = v.co
            v.co = Vector((x, (y0 + y1) / 2 + zz, DECK_Z + (0.012 if yy > 0.5 else -0.004)))
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
        g.add(bm, bevel=(0.002, 2, 30))
        bm = new_bm()
        box(bm, (cx, (y0 + y1) / 2, DECK_Z + 0.0005), (0.155, y1 - y0 - 0.02, 0.002), DARK)
        slat = Matrix.Rotation(math.radians(-28), 3, "X")
        for yy in np.linspace(y0 + 0.026, y1 - 0.026, 8):
            box(bm, (cx, yy, DECK_Z + 0.006), (0.150, 0.016, 0.004), PAINT, rot=slat)
        g.add(bm, bevel=(0.0012, 2, 30))
    return g


# ------------------------------------------------------------------- turret

def body_point(phi, z):
    """Point and outward normal on the turret loft at height z, at plan angle
    phi about the ring axis -- the frame the painted seams are drawn in."""
    zs = [s[0] for s in T_SECTIONS]
    k = max(0, min(len(zs) - 2, int(np.searchsorted(zs, z)) - 1))
    za, zb = zs[k], zs[k + 1]
    u = (z - za) / (zb - za)
    n = 1440
    ra = d_section(za, *T_SECTIONS[k][1:], 4.2, 2.3, n)
    rb = d_section(zb, *T_SECTIONS[k + 1][1:], 4.2, 2.3, n)
    ang = np.array([math.atan2(v.y - RING_C[1], v.x - RING_C[0]) for v in ra])
    j = int(np.argmin(np.abs(np.angle(np.exp(1j * (ang - phi))))))
    p = ra[j].lerp(rb[j], u)
    tang = (ra[(j + 1) % n] - ra[j - 1]).lerp(rb[(j + 1) % n] - rb[j - 1], u)
    nn = tang.cross(rb[j] - ra[j]).normalized()
    if nn.dot(p - Vector((RING_C[0], RING_C[1], p.z))) < 0:
        nn = -nn
    return p, nn


def turret(mats):
    g = Group(mats)
    cx, cy = RING_C
    # the rotating ring: a dense wall, alone in its slices (turret_axis fits
    # the roundest 3 % slice in the lowest 22 %)
    bm = new_bm()
    z0, z1, r = T_RING
    wall = [(z, r) for z in np.linspace(z0, z1, 16)]
    lathe(bm, wall + [(z1, 0.12), (z0, 0.12)], PAINTDK, seg=192, axis="Z", center=(cx, cy, 0))
    g.add(bm)

    # body: loft of D sections, flat roof
    bm = new_bm()
    loft(bm, [d_section(z, a, yf, yr, 4.2, 2.3, 96) for z, a, yf, yr in T_SECTIONS], PAINT_T)
    g.add(bm, bevel=(0.006, 2, 40))

    # rivets on the walls: both sides of every painted seam, a row along the
    # foot, a row just over the line under the roof band
    bm = new_bm()

    def off_front(t, half):
        return abs(math.atan2(math.sin(t + math.pi / 2), math.cos(t + math.pi / 2))) >= half

    def under_frame(p):
        """Inside the mantlet frame's outline (+8 mm): a dome there is buried."""
        F = FRAME
        dx = max(abs(p.x) - (F["ohw"] - F["r_o"]), 0.0)
        dz = max(abs(p.z - F["cz"]) - (F["ohh"] - F["r_o"]), 0.0)
        return p.y < RING_C[1] and math.hypot(dx, dz) - F["r_o"] < 0.008

    for k in range(12):
        t = math.tau * k / 12
        if not off_front(t, 0.33):
            continue
        for z in (0.078, 0.150, 0.222):
            for side in (-1, 1):
                p, nn = body_point(t + side * 0.065, z)
                if not under_frame(p):
                    rivets(bm, [p + nn * 0.0004], nn)
    for k in range(28):
        t = math.tau * (k + 0.5) / 28
        if off_front(t, 0.55):
            p, nn = body_point(t, 0.046)
            rivets(bm, [p + nn * 0.0004], nn)
    for k in range(36):
        t = math.tau * (k + 0.5) / 36
        p, nn = body_point(t, 0.261)
        rivets(bm, [p + nn * 0.0004], nn, r=RIV * 0.9)

    # and on the roof: a row each side of both plate seams, a row round the
    # edge -- clear of the cupola and its hinge
    roof = d_section(ROOF_Z, *T_SECTIONS[-1][1:], 4.2, 2.3, 720)
    rx = np.array([v.x for v in roof])
    ry = np.array([v.y for v in roof])

    def half_width(y):
        near = np.abs(ry - y) < 0.004
        return float(np.abs(rx[near]).max()) if near.any() else 0.0

    def clear_of_cupola(x, y):
        return (math.hypot(x - CUPOLA[0], y - CUPOLA[1]) > 0.085 and
                not (abs(x - CUPOLA[0]) < 0.045 and y > CUPOLA[1] + 0.05))

    top = []
    for ys in ROOF_SEAMS:
        for y in (ys - 0.012, ys + 0.012):
            hw = half_width(y) - 0.022
            n = max(2, int(round(2 * hw / 0.042)) + 1)
            top += [(x, y) for x in np.linspace(-hw, hw, n)]
    inset = d_section(ROOF_Z, T_SECTIONS[-1][1] - 0.016, T_SECTIONS[-1][2] + 0.016,
                      T_SECTIONS[-1][3] - 0.016, 4.2, 2.3, 28)
    for v in inset:
        if all(abs(v.y - (ys + d)) > 0.02 for ys in ROOF_SEAMS for d in (-0.012, 0.012)):
            top.append((v.x, v.y))
    rivets(bm, [(x, y, ROOF_Z + 0.0004) for x, y in top if clear_of_cupola(x, y)], (0, 0, 1),
           r=RIV * 0.9)
    g.add(bm, subsurf=1)

    # mantlet: a raised frame in the front face, a grey block in it
    F = FRAME
    bm = new_bm()
    ring_prism_y(bm, rr_loop(0.0, F["cz"], F["ohw"], F["ohh"], F["r_o"]),
                 rr_loop(0.0, F["cz"], F["ihw"], F["ihh"], F["r_i"]),
                 F["y_back"], F["y_front"], PAINT_T)
    # its face leans back with the turret's (~6 deg), so it stands out evenly
    tilt(bm, -6.0, F["y_front"], F["cz"])
    g.add(bm, bevel=(0.005, 2, 30))
    B = BLOCK
    bm = new_bm()
    box(bm, (0, (B["y_back"] + B["y_front"]) / 2, (B["z0"] + B["z1"]) / 2),
        (2 * B["hw"], B["y_back"] - B["y_front"], B["z1"] - B["z0"]), GUN)
    tilt(bm, -4.0, B["y_front"], GUN_Z)
    g.add(bm, bevel=(0.011, 2, 30))
    bm = new_bm()
    lathe(bm, rounded_rect_profile(-0.012, 0.0, 0.0, 0.058, 0.004, n=2), GUN, seg=64,
          axis="Y", center=(0, B["y_front"] + 0.002, GUN_Z))
    g.add(bm, subsurf=1)
    # the block's rivets lean with its face, or the lower pair sinks 3 mm
    # into it and the Bevel node inks a ring over the buried dome
    bm = new_bm()
    for x in (-0.056, 0.056):
        for z in (B["z0"] + 0.016, B["z1"] - 0.016):
            rivets(bm, [(x, B["y_front"] - 0.0004, z)], (0, -1, 0), r=0.0058, mi=RIVET_G)
    tilt(bm, -4.0, B["y_front"], GUN_Z)
    g.add(bm, subsurf=1)

    # cupola: thick ring, grated lid, hinge behind, a latch handle
    hx, hy = CUPOLA
    zr = ROOF_Z
    bm = new_bm()
    lathe(bm, rounded_rect_profile(zr - 0.01, zr + 0.022, 0.048, 0.069, 0.006, n=3),
          GUN, seg=96, axis="Z", center=(hx, hy, 0))
    lathe(bm, [(zr + 0.026, 0.0), (zr + 0.026, 0.047), (zr + 0.021, 0.052), (zr + 0.012, 0.052)],
          GUN, seg=96, axis="Z", center=(hx, hy, 0), closed=False)
    g.add(bm, subsurf=1)
    bm = new_bm()
    for dy in np.linspace(-0.034, 0.034, 6):
        ln = 2 * math.sqrt(max(0.046 ** 2 - dy ** 2, 1e-6)) * 0.9
        box(bm, (hx, hy + dy, zr + 0.029), (ln, 0.0075, 0.006), GUN)
    box(bm, (hx, hy + 0.074, zr + 0.016), (0.052, 0.026, 0.030), GUN)
    cyl(bm, (hx - 0.03, hy + 0.070, zr + 0.036), (hx + 0.03, hy + 0.070, zr + 0.036), 0.010, GUN, seg=16)
    bend_bar(bm, [Vector((hx - 0.016, hy + 0.086, zr + 0.036)),
                  Vector((hx - 0.016, hy + 0.086, zr + 0.052)),
                  Vector((hx + 0.016, hy + 0.086, zr + 0.052)),
                  Vector((hx + 0.016, hy + 0.086, zr + 0.036))], 0.0042, GUN, seg=8)
    g.add(bm, bevel=(0.002, 2, 30), subsurf=1)
    return g


def barrel(mats):
    """Collar, sleeve, tube; bored.  The breech end deep in the block, so the
    recoil layer (0.13 of the length back) never shows it."""
    g = Group(mats)
    bm = new_bm()
    y0 = -0.236
    prof = [(0.0, 0.0), (0.0, 0.050), (-0.052, 0.050), (-0.056, 0.047), (-0.060, 0.0448),
            (-0.112, 0.0448), (-0.116, 0.041), (-0.121, 0.0295), (-0.2227, 0.0295),
            (-0.2227, 0.0145), (-0.2027, 0.0145), (-0.2027, 0.0)]
    lathe(bm, prof, GUN, seg=64, axis="Y", center=(0, y0, GUN_Z), closed=False)
    g.add(bm, bevel=(0.0015, 2, 30))
    return g
