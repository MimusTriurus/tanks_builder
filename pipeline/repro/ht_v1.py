"""HT_V1, rebuilt from scratch on repro_kit (docs/repro.md).

The third copy, 2026-09-27.  It stands 1.0 to the +X of the original in the
same scene, in collection `HT_V1.Repro`, every name suffixed `.Repro`.

Every number is in HT_V1's own frame, measured off its vertices:

- front -Y; ground z -0.3205 -- the lowest vertex of the *rebuilt* belts
  (`*.Caterpillar.Rebuilt`, which the HTP_v1 sprites were rendered from; the
  generator's `*.Caterpillar.Geometry` sits 0.2 mm higher);
- the left belt (`L.*`) on -X, belt centres x -0.1999 / +0.1996 -> 0.1997,
  0.0922 wide: a stadium of two r 0.0925 arcs, unlike MT_PARTS_1's;
- the belts run *inside* the hull's box: over them a sponson (a panelled
  band out to x 0.275, 0.288 in the middle), below it a plain skirt at x 0.249
  down to z -0.309, so a side view shows only the bottom run and the arcs;
- the ring axis (0, 0.040): the centre of the hull's round collar.  The stamp
  says (0, 0.0571), but it was fitted to a turret foot that is not round
  (roundness 0.719, "treat the result as approximate"); the collar is round
  to a millimetre at every height;
- the gun: the turret's front is a fixed prow; a square box stands out of
  its point and lays with the gun (`mantlet`, in the original's Barrel mesh,
  painted like the turret); the grey octagonal sleeve, the tube and the
  octagonal muzzle brake recoil.  Bore at z 0.071 (the original droops 0.2
  deg, the copy rests level); muzzle y -0.4997;
- hull and turret images 4096, belts and wheels 2048 (the copy bakes the
  canon's 2048 everywhere);
- stamps (`ring_*`, `muzzle_*`, `hit_*`, `exhaust_*`) and the effect meshes
  are tool output: not repeated.
"""

import math
import bmesh
import numpy as np
from mathutils import Matrix, Vector

from repro_kit import (
    ARMY_PALETTE,
    PAINT, GUN, TRACK, GLASS, DARK, PAINT_T, PAINTDK, RIVET, RIVET_G, RIV,
    Group, new_bm, box, prism, lathe, rounded_rect_profile, cyl, rivets, line,
    bend_bar, hull_solid, bent_plate, rr_loop, loft, belt_path, place_belt, game, segs,
)

NAME = "HT_V1"
SFX = ".Repro"
X_OFF = 1.0
PREFIX = "HTR"
GROUND = -0.3205
RING_C = (0.0, 0.040)
TRACK_X = 0.1997
# the original's belts that the sprites show: the kit compares against these
ORIGINAL = {"l_cat": "L.Caterpillar.Rebuilt", "r_cat": "R.Caterpillar.Rebuilt"}

# the army's paint, not the original's (repro_kit.ARMY_PALETTE)
PALETTE = ARMY_PALETTE

# ------------------------------------------------------------------- layout

BELLY_Z = -0.281
DECK_Z = -0.068            # the hull between the belts; the sponsons are 7 mm lower
SPONSON_Z = -0.075
# between the belts: 1.6 mm inside their inner edge (TRACK_X - BELT_W/2 =
# 0.1536) -- the nose and the tail stand in front of the arcs and would cut
# the inner links off in the front and rear views
CORE_X = 0.152
SKIN_X = (0.249, 0.266)    # the sponson's outer wall, 3 mm outside the belt, closing down to the skirt
BAND_X = 0.2745            # the raised panels on it
BULGE_X = 0.288            # the three middle panels of the lower row stand out further
SKIRT_X = (0.2468, 0.251)  # the plain lower skirt: its inner face 1 mm off the belt

# the hull between the belts, side profile: belly, the rear under the engine
# deck, the deck forward to the glacis' top edge (the nose is a separate prow)
CORE = [(-0.186, BELLY_Z), (0.360, BELLY_Z), (0.400, -0.273), (0.430, -0.262), (0.452, -0.250),
        (0.479, -0.180), (0.494, -0.170), (0.500, -0.140), (0.500, -0.085), (0.490, -0.071),
        (0.475, DECK_Z), (-0.186, DECK_Z)]


def _on_plane(a, b, c, x, z):
    """The point at (x, z) of the plane through a, b, c."""
    n = (b - a).cross(c - a)
    return Vector((x, a.y - (n.x * (x - a.x) + n.z * (z - a.z)) / n.y, z))


# the glacis: one plane a side, meeting on the centre line in a mild prow (1
# cm), from 3 mm under the deck (y -0.19, the original's reaches z -0.07 at
# every x but the driver's) down to the nose line, which rises from the point
# (-0.318, -0.328 with the beak) to the corners (-0.283, -0.29 with it)
TOP_C, TOP_S = Vector((0.0, -0.1985, DECK_Z - 0.003)), Vector((0.152, -0.1885, DECK_Z - 0.003))
NOSE_C = Vector((0.0, -0.3180, -0.185))
NOSE_S = _on_plane(TOP_C, TOP_S, NOSE_C, 0.152, -0.160)
NOSE_LOW_Y = -0.262        # the lower nose at the belly

# the sponson's outer wall, side profile: a vertical front with its top
# chamfered back, the rear corner rounded up
# (its top 1 mm over the roof plate, which runs into it: never one face);
# it ends at y 0.386, the fairing behind it is narrower
SKIN = [(-0.255, -0.225), (-0.255, -0.130), (-0.157, SPONSON_Z + 0.001),
        (0.350, SPONSON_Z + 0.001), (0.372, -0.082), (0.384, -0.100), (0.386, -0.200),
        (0.380, -0.225)]
ROOF = dict(x=(0.142, 0.2565), y=(-0.216, 0.360))        # the sponson's roof plate, 12 mm
# the fairing over the rear arc, out to the skirt's x: 0.5 mm under the roof
# plate where it starts inside it, 4 mm off the belt at its closest
FAIRING = [(0.352, SPONSON_Z - 0.0005), (0.360, SPONSON_Z - 0.0005), (0.400, -0.090),
           (0.437, -0.100), (0.469, -0.120), (0.488, -0.140), (0.495, -0.163)]
# the panels of the band, as the original lays them: two rows, seams at world
# y, a stepped block in the middle of the lower row standing out to BULGE_X
BAND_UP = (-0.153, -0.084)
BAND_LO = (-0.219, -0.159)
SEAMS_UP = [-0.160, -0.121, -0.082, -0.054, -0.016, 0.026, 0.131, 0.179, 0.215, 0.239,
            0.278, 0.320, 0.372]
SEAMS_LO = [-0.160, -0.121, -0.082, -0.046, 0.023, 0.131, 0.194, 0.215, 0.239, 0.278, 0.320, 0.372]
STEP = (0.023, 0.131, -0.119)          # y0, y1, top z of the stepped panel
SKIRT_SEAMS = (-0.082, 0.077, 0.236)
SKIRT_Y = (-0.290, 0.476)
SKIRT_Z = (-0.309, -0.190)

FRONT_BOX = dict(x=(0.146, 0.245), y=(-0.210, -0.131), z=(-0.085, -0.045))
# the lamp's housing, side profile: its sloped belly clears the belt's front
# arc by 5 mm
LAMP_BOX = [(-0.300, -0.146), (-0.265, -0.132), (-0.200, -0.128), (-0.200, -0.078),
            (-0.262, -0.078), (-0.300, -0.097)]
LAMP_X = (0.155, 0.243)
LAMP = dict(x=0.200, y=-0.300, z=-0.112, r=0.024)
SHACKLE_X = 0.097
GRILLE = dict(x=(0.022, 0.128), y=(0.272, 0.458))

# running gear: a stadium belt, the idler and the sprocket on its arcs
BELT_C = ((-0.243, -0.228), (0.416, -0.228))
BELT_T = 0.016
BELT_RIN = 0.0925 - BELT_T
BELT_W = 0.0922
PITCH = 0.022
WHEEL_DX = 0.007          # the wheels stand 7 mm outboard of the belt's centre
IDLER = (-0.243, -0.228, 0.074)
SPROCKET = (0.416, -0.228, 0.068)        # teeth reach the belt's inner surface
ROAD = [(-0.065, -0.2555, 0.048), (0.087, -0.2555, 0.048), (0.232, -0.2555, 0.048)]
TAIL = (0.357, -0.2705, 0.033)           # the small wheel before the sprocket
RETURN = [(-0.050, -0.1965, 0.044), (0.137, -0.1965, 0.044)]

# the game variant: where the sprung mass rocks (mid-belt, level with the
# belt tops) and where the exhaust leaves (the two grilles)
BODY_PIVOT = ((BELT_C[0][0] + BELT_C[1][0]) / 2, BELT_C[0][1] + BELT_RIN + BELT_T)
EXHAUST = ((-0.075, 0.366, -0.058), (0.075, 0.366, -0.058))
GAME_TAG = "HTR"                        # Models/HTR/

# turret: a box -- front face leaning back 13 deg, its top edge chamfered
# deep into the roof, sides stepped in 6 mm behind y 0.085, rear corners cut
# -- on a ring inside the hull's collar
COLLAR_TOP = -0.0255
# z0, z1, r: the lower 12 mm in the collar, 3 mm showing, the rest in the
# body -- 15 mm of it clear under the body's foot (turret_axis fits bands 3 %
# of the turret's height, 10 mm with the antenna)
T_RING = (-0.0375, -0.012, 0.130)
RING_Z0 = T_RING[0]
T_FOOT_Z = -0.0225
T_SECTIONS = [(T_FOOT_Z, -0.118), (-0.010, -0.121), (0.130, -0.089), (0.165, -0.040)]   # z, front y
ROOF_Z = 0.165
GUN_Z = 0.071
# the prow the gun comes out of: fixed, the turret's; its point on the bore
PROW = dict(hw=0.079, point=(-0.176, GUN_Z), top=(-0.118, 0.180), low=(-0.121, -0.0135))
HOUSING = dict(hw=0.089, y=(-0.117, -0.045), z=(0.110, 0.182))
# the square box that lays with the gun (the original's is in its Barrel
# mesh, painted like the turret): deep in the prow, so at either end of the
# ladder its back never leaves it
BLOCK = dict(hw=0.052, y=(-0.236, -0.100), z=(0.0228, 0.1268))
TRUNNION = (-0.180, GUN_Z)
CUPOLA = (0.085, 0.036)
ANTENNA = (0.134, 0.064, 0.309)


# ------------------------------------------------------------------- tracks

def link_mesh(pitch):
    """One link (u along the belt, v across, w out): a plate the full width,
    two pads either side of a groove down the middle, the full thickness down
    to the ground."""
    bm = new_bm()
    box(bm, (0, 0, 0.004), (pitch * 0.94, BELT_W, 0.008), TRACK)
    for v in (-0.0235, 0.0235):
        box(bm, (0, v, 0.0115), (pitch * 0.78, 0.040, 0.009), TRACK)
    return bm


def belt_spec():
    """The belt's inner surface as a CCW (y, z) path, its pitch, its link."""
    path = belt_path([(BELT_C[0][0], BELT_C[0][1], BELT_RIN), (BELT_C[1][0], BELT_C[1][1], BELT_RIN)])
    return path, PITCH, link_mesh


def belt(mats, xc):
    path, pitch, link = belt_spec()
    bm, n, pitch, L = place_belt(mats, xc, path, pitch, link)
    return bm, {"links": n, "pitch": round(pitch, 5), "length": round(L, 4)}


def disc(bm, y, z, r, xc, w, seg=48, mi=TRACK):
    """A dished wheel centred at xc: tyre, dish, hub boss (lathe about X)."""
    h = w / 2
    prof = [(-h - 0.005, 0.0), (-h - 0.005, r * 0.22), (-h, r * 0.28), (-h, r * 0.44),
            (-h + 0.007, r * 0.52), (-h + 0.007, r * 0.74), (-h, r * 0.82), (-h, r * 0.93),
            (-h + 0.004, r), (h - 0.004, r), (h, r * 0.93), (h, r * 0.82),
            (h - 0.007, r * 0.74), (h - 0.007, r * 0.52), (h, r * 0.44), (h, r * 0.28),
            (h + 0.005, r * 0.22), (h + 0.005, 0.0)]
    lathe(bm, prof, mi, seg=seg, axis="X", center=(xc, y, z))


def debris():
    """What flies off when the tank blows up, for the game variant: a name,
    the node it comes off, and a box in this frame.  Every piece of that
    node's mesh whose centre lies in the box goes with it.  Sides are the
    tank's own: L is +X."""
    out = []
    edges = [SKIRT_Y[0]] + list(SKIRT_SEAMS) + [SKIRT_Y[1]]
    for s, S in ((1, "L"), (-1, "R")):
        def sx(a, b):
            return tuple(sorted((s * a, s * b)))
        for i, (a, b) in enumerate(zip(edges, edges[1:])):
            out.append({"name": "Skirt.%s.%d" % (S, i), "parent": "Hull",
                        "box": (sx(0.244, 0.256), (a + 0.001, b - 0.001), (-0.32, -0.18))})
        out.append({"name": "Lamp.%s" % S, "parent": "Hull",
                    "box": (sx(0.150, 0.250), (-0.33, -0.214), (-0.16, -0.07))})
        out.append({"name": "Box.%s" % S, "parent": "Hull",
                    "box": (sx(0.150, 0.270), (-0.214, -0.125), (-0.09, -0.03))})
    cx, cy = CUPOLA
    out.append({"name": "Hatch", "parent": "Turret",       # the cupola's top
                "box": ((cx - 0.05, cx + 0.05), (cy - 0.045, cy + 0.05), (0.200, 0.24))})
    return out


def wheel_spec():
    """Every wheel that turns: name, axle (y, z), and the radius the belt turns
    it at (the engine spins each one distance / r)."""
    out = [("Idler", IDLER)]
    out += [("Road.%d" % i, w) for i, w in enumerate(ROAD)]
    out += [("Roller.%d" % i, w) for i, w in enumerate(RETURN)]
    out += [("Tail", TAIL), ("Sprocket", (SPROCKET[0], SPROCKET[1], BELT_RIN))]
    return [{"name": n, "axle": (y, z), "r": r} for n, (y, z, r) in out]


def wheels(mats, xc, s):
    """wheel_spec, each with its own Group -- one object per wheel in the game
    variant, so each can turn about its axle."""
    out = []
    xw = xc + s * WHEEL_DX
    for w in wheel_spec():
        (y, z), r = w["axle"], w["r"]
        g = Group(mats)
        bm = new_bm()
        seg = 64 if w["name"] in ("Idler", "Sprocket") else 48
        if game():
            seg = int(seg * 0.8)
        if w["name"] == "Sprocket":
            disc(bm, y, z, SPROCKET[2] - 0.006, xw, 0.060, seg=seg)
        elif w["name"] == "Idler":
            disc(bm, y, z, IDLER[2], xw, 0.064, seg=seg)
        elif w["name"].startswith("Roller"):
            disc(bm, y, z, r, xw, 0.050, seg=seg)
        else:
            disc(bm, y, z, r, xw, 0.064, seg=seg)
        g.add(bm, subsurf=1)
        if w["name"] == "Sprocket":
            bm = new_bm()          # teeth, two rings, their tips on the belt's inner surface
            for k in range(14):
                t = math.tau * k / 14
                rot = Matrix.Rotation(t, 3, "X")
                for dx in (-0.022, 0.022):
                    c = Vector((xw + dx, y, z)) + rot @ Vector((0, 0, BELT_RIN - 0.010))
                    box(bm, c, (0.014, 0.014, 0.018), TRACK, rot=rot)
            g.add(bm, bevel=(0.002, 2, 40))
        out.append(dict(w, group=g))
    return out


def rolls(mats, xc, s):
    g = Group(mats)
    for w in wheels(mats, xc, s):
        g.meshes += w["group"].meshes
    g.meshes += running_gear(mats, xc, s).meshes
    return g


def running_gear(mats, xc, s):
    """What does not turn: a long beam inside the belt, arms down to the road
    wheels, axles."""
    g = Group(mats)
    bm = new_bm()
    xin = xc - s * 0.032
    zb = -0.222
    box(bm, (xin, 0.11, zb), (0.016, 0.40, 0.018), TRACK)
    for (y, z, r) in ROAD:
        a, b_ = Vector((xin, y + 0.05, zb)), Vector((xin, y, z))
        d = b_ - a
        rot = Vector((0, 1, 0)).rotation_difference(d.normalized()).to_matrix()
        box(bm, (a + b_) / 2, (0.012, d.length + 0.016, 0.016), TRACK, rot=rot)
    for (y, z, r) in [IDLER, SPROCKET, TAIL] + ROAD + RETURN:
        cyl(bm, (xin, y, z), (xc + s * WHEEL_DX, y, z), 0.010, TRACK, seg=12)
    g.add(bm, bevel=(0.003, 2, 40), subsurf=1)
    return g


# --------------------------------------------------------------------- hull

def glacis_frame():
    """The right half of the glacis as a frame: origin on the top edge at the
    centre line, U along the top edge outwards, V down the slope, n out."""
    n = (NOSE_S - NOSE_C).cross(TOP_C - NOSE_C).normalized()
    if n.y > 0:
        n = -n
    U = (TOP_S - TOP_C).normalized()
    V = n.cross(U)
    if V.z > 0:
        V = -V
    return TOP_C.copy(), U, V, n


# explosive reactive armour on the glacis, as on a T-72: bricks of one size in
# a grid on each half, rows along the top edge, the lowest row cut along the
# nose line.  Each lies like a roof tile, its lower edge standing out further
# (the original's section steps 2-3 mm at every joint).  Sizes on the plane.
ERA = dict(w=0.0372, h=0.040, gap=0.0042, cols=4, rows=4, t=(0.005, 0.010))
# the driver's visor, in the glacis' top at the centre line: the top row has
# no bricks under it
VISOR = dict(hw=0.046, y=(-0.215, -0.176), z=(-0.100, -0.058))


def era_blocks():
    """Footprints of the right half's ERA bricks: [(u0, u1, v0, v1a, v1b)],
    the lower edge at u0 and at u1 (the nose line cuts the last row)."""
    O, U, V, n = glacis_frame()

    def uv(p):
        return (p - O).dot(U), (p - O).dot(V)
    (uc, vc), (us, vs) = uv(NOSE_C), uv(NOSE_S)

    def v_nose(u):
        return vc + (u - uc) * (vs - vc) / (us - uc)
    E, g = ERA, ERA["gap"] / 2
    out = []
    for j in range(E["rows"]):
        for k in range(E["cols"]):
            u0, u1 = 0.002 + k * E["w"] + g, 0.002 + (k + 1) * E["w"] - g
            v0, v1 = j * E["h"] + g, (j + 1) * E["h"] - g
            va = min(v1, v_nose(u0) - 2 * g)
            vb = min(v1, v_nose(u1) - 2 * g)
            if max(va, vb) < v0 + 0.009:
                continue
            if j == 0:                  # the top row starts where the visor ends
                if u1 < VISOR["hw"] + 0.015:
                    continue
                u0 = max(u0, VISOR["hw"] + 0.004)
            out.append((u0, u1, v0, max(va, v0 + 0.004), max(vb, v0 + 0.004)))
    return out


def hull(mats):
    g = Group(mats)

    # --- between the belts: belly, the rear, the deck to the step face
    bm = new_bm()
    prism(bm, CORE, -CORE_X, CORE_X, PAINT)
    g.add(bm, bevel=(0.010, 2, 30))
    # the lip under the lower nose
    bm = new_bm()
    box(bm, (0, -0.230, -0.289), (0.222, 0.044, 0.022), PAINT)
    g.add(bm, bevel=(0.004, 2, 30))

    # --- the prow: glacis, nose line, lower nose; its back 15 mm in the core
    bm = new_bm()
    pts = []
    for s in (-1, 1):
        for p in (TOP_S, NOSE_S):
            pts.append((s * p.x, p.y, p.z))
        pts += [(s * CORE_X, NOSE_LOW_Y, BELLY_Z), (s * CORE_X, -0.176, BELLY_Z),
                (s * CORE_X, -0.176, TOP_S.z)]
    pts += [tuple(TOP_C), tuple(NOSE_C)]      # (no point between two others: hull_solid chokes)
    hull_solid(bm, pts, PAINT)
    g.add(bm, bevel=(0.006, 2, 30))
    # the beak along the nose line, 10 mm proud
    for s in (-1, 1):
        a = Vector((0.0, NOSE_C.y, NOSE_C.z))
        b = a.lerp(Vector((s * NOSE_S.x, NOSE_S.y, NOSE_S.z)), 0.150 / 0.152)
        bm = new_bm()              # hull_solid hulls every vertex it is given: one each
        hull_solid(bm, [p + Vector((0, dy, dz)) for p in (a, b) for dy in (-0.010, 0.006)
                        for dz in (-0.008, 0.008)], PAINT)
        g.add(bm, bevel=(0.003, 2, 30))
    # ERA bricks on the glacis: shingles, 2 mm into the plate
    bm = new_bm()
    O, U, V, n = glacis_frame()
    t0, t1 = ERA["t"]
    for s in (-1, 1):
        for u0, u1, v0, va, vb in era_blocks():
            pts = []
            for u, v in ((u0, v0), (u1, v0), (u1, vb), (u0, va)):
                p = O + U * u + V * v
                t = t0 + (t1 - t0) * (v - v0) / (ERA["h"] - ERA["gap"])
                for d in (-0.002, t):
                    q = p + n * d
                    pts.append((s * q.x, q.y, q.z))
            sub = new_bm()
            hull_solid(sub, pts, PAINT)
            me = bpy_mesh(sub)
            bm.from_mesh(me)
            free_mesh(me)
    g.add(bm, bevel=(0.0012, 2, 30))

    # --- the deck: collar, driver's hatch, his visor on the step face
    bm = new_bm()
    lathe(bm, [(DECK_Z - 0.004, 0.0), (DECK_Z - 0.004, 0.153), (-0.045, 0.151), (-0.035, 0.147),
               (-0.0285, 0.139), (COLLAR_TOP, 0.132), (COLLAR_TOP, 0.0)], PAINT, seg=128,
          axis="Z", center=(RING_C[0], RING_C[1], 0))
    g.add(bm, bevel=(0.003, 2, 30))
    bm = new_bm()
    box(bm, (0, -0.149, -0.0645), (0.100, 0.062, 0.015), PAINT)       # into the visor, never flush
    g.add(bm, bevel=(0.003, 2, 30))
    bm = new_bm()
    Vs = VISOR
    box(bm, (0, sum(Vs["y"]) / 2, sum(Vs["z"]) / 2),
        (2 * Vs["hw"], Vs["y"][1] - Vs["y"][0], Vs["z"][1] - Vs["z"][0]), PAINT)
    box(bm, (0, Vs["y"][0] + 0.004, Vs["z"][1] + 0.003), (2 * Vs["hw"] + 0.008, 0.016, 0.008), PAINT)
    g.add(bm, bevel=(0.003, 2, 30))
    bm = new_bm()
    box(bm, (0, Vs["y"][0] - 0.0005, -0.075), (0.060, 0.003, 0.010), DARK)
    g.add(bm)

    # --- sponsons: the roof bent into the rear fairing, the outer wall
    bm = new_bm()
    R = ROOF
    for s in (-1, 1):
        x0, x1 = sorted((s * R["x"][0], s * R["x"][1]))
        box(bm, (0.5 * (x0 + x1), sum(R["y"]) / 2, SPONSON_Z - 0.006),
            (x1 - x0, R["y"][1] - R["y"][0], 0.012), PAINT)
    g.add(bm, bevel=(0.004, 2, 30))
    bm = new_bm()
    for s in (-1, 1):
        x0, x1 = sorted((s * R["x"][0], s * SKIRT_X[0] + s * 0.002))
        bent_plate(bm, FAIRING, 0.006, x0, x1, PAINT)
    g.add(bm, bevel=(0.0025, 2, 30))
    bm = new_bm()
    for s in (-1, 1):
        x0, x1 = sorted((s * SKIN_X[0], s * SKIN_X[1]))
        prism(bm, SKIN, x0, x1, PAINT)
    g.add(bm, bevel=(0.004, 2, 30))

    # --- the band's panels
    bm = new_bm()
    for s in (-1, 1):
        def panel(y0, y1, z0, z1, xo=BAND_X):
            x0, x1 = sorted((s * (SKIN_X[1] - 0.004), s * xo))
            box(bm, (0.5 * (x0 + x1), 0.5 * (y0 + y1), 0.5 * (z0 + z1)),
                (x1 - x0, y1 - y0 - 0.004, z1 - z0), PAINT)
        # the front plate under the chamfer
        x0, x1 = sorted((s * (SKIN_X[1] - 0.004), s * (BAND_X - 0.001)))
        prism(bm, [(-0.250, BAND_LO[0]), (-0.250, -0.133), (-0.163, BAND_UP[1] + 0.002),
                   (-0.163, BAND_LO[0])], x0, x1, PAINT)
        panel(-0.240, -0.198, -0.203, -0.168, BAND_X + 0.003)
        for y0, y1 in zip(SEAMS_UP, SEAMS_UP[1:]):
            if y0 == 0.026:                       # over the step: a short one, split
                for a, b in ((0.026, 0.077), (0.077, 0.131)):
                    panel(a, b, STEP[2] + 0.005, BAND_UP[1])
                continue
            panel(y0, y1, BAND_UP[0], BAND_UP[1])
        for y0, y1 in zip(SEAMS_LO, SEAMS_LO[1:]):
            if y0 == 0.023:
                panel(y0, y1, BAND_LO[0], STEP[2], BULGE_X)
            elif -0.05 < y0 < 0.19:
                panel(y0, y1, BAND_LO[0], BAND_LO[1], BULGE_X)
            else:
                panel(y0, y1, BAND_LO[0], BAND_LO[1])
    g.add(bm, bevel=(0.002, 2, 30))

    # --- the lower skirt: four plates a side, the end corners rounded
    bm = new_bm()
    edges = [SKIRT_Y[0]] + list(SKIRT_SEAMS) + [SKIRT_Y[1]]
    z0, z1 = SKIRT_Z
    for s in (-1, 1):
        x0, x1 = sorted((s * SKIRT_X[0], s * SKIRT_X[1]))
        for i, (a, b) in enumerate(zip(edges, edges[1:])):
            a, b = a + 0.001, b - 0.001
            poly = [(a, z1), (a, z0), (b, z0), (b, z1)]
            if i == 0:
                poly = [(a, z1), (a, z0 + 0.018), (a + 0.006, z0 + 0.006), (a + 0.018, z0),
                        (b, z0), (b, z1)]
            if i == len(edges) - 2:
                poly = [(a, z1), (a, z0), (b - 0.022, z0), (b - 0.007, z0 + 0.007),
                        (b, z0 + 0.022), (b, z1)]
            prism(bm, poly, x0, x1, PAINT)
    g.add(bm, bevel=(0.0015, 2, 30))

    # --- front corners: a box with a handle, the lamp's housing in front of it
    bm = new_bm()
    F = FRONT_BOX
    for s in (-1, 1):
        x0, x1 = sorted((s * F["x"][0], s * F["x"][1]))
        box(bm, (0.5 * (x0 + x1), sum(F["y"]) / 2, sum(F["z"]) / 2),
            (x1 - x0, F["y"][1] - F["y"][0], F["z"][1] - F["z"][0]), PAINT)
    g.add(bm, bevel=(0.005, 2, 30))
    bm = new_bm()
    for s in (-1, 1):
        x0, x1 = sorted((s * LAMP_X[0], s * LAMP_X[1]))
        prism(bm, LAMP_BOX, x0, x1, PAINT)
    g.add(bm, bevel=(0.006, 2, 30))
    bm = new_bm()
    bmg = new_bm()
    L = LAMP
    for s in (-1, 1):
        c = (s * L["x"], L["y"], L["z"])
        # a rim round the lens, open inside it: a tube down to the axis behind
        # the glass was all sliver faces and baked the glass' grey (mip 3)
        lathe(bm, rounded_rect_profile(-0.013, 0.004, 0.016, L["r"] + 0.006, 0.003, n=2), PAINT,
              seg=48, axis="Y", center=c)
        lathe(bmg, [(-0.015, 0.0), (-0.0145, L["r"] * 0.55), (-0.012, L["r"] * 0.9),
                    (-0.009, L["r"])], GLASS, seg=48, axis="Y", center=c, closed=False)
    g.add(bm, subsurf=1)
    g.add(bmg, subsurf=1)
    bm = new_bm()
    for s in (-1, 1):
        x = s * (F["x"][1] + 0.004)
        bend_bar(bm, [Vector((x, -0.184, -0.070)), Vector((x, -0.184, -0.048)),
                      Vector((x, -0.151, -0.048)), Vector((x, -0.151, -0.070))], 0.004, GUN, seg=8)
    g.add(bm)

    # --- tow shackles on the lower nose: a bracket, a U hanging from it
    bm = new_bm()
    for s in (-1, 1):
        x = s * SHACKLE_X
        for dx in (-0.023, 0.023):              # a clevis: two lugs, a pin through them
            box(bm, (x + dx, -0.288, -0.197), (0.010, 0.028, 0.024), GUN)
    g.add(bm, bevel=(0.003, 2, 30))
    bm = new_bm()
    for s in (-1, 1):
        x = s * SHACKLE_X
        cyl(bm, (x - 0.030, -0.2965, -0.197), (x + 0.030, -0.2965, -0.197), 0.0055, GUN, seg=12)
    g.add(bm)
    bm = new_bm()
    for s in (-1, 1):
        x = s * SHACKLE_X
        u = [Vector((x - 0.016, -0.2965, -0.197))]        # a D ring on the pin
        for k in range(9):
            t = math.pi * k / 8
            u.append(Vector((x - 0.016 * math.cos(t), -0.2965, -0.231 - 0.016 * math.sin(t))))
        u.append(Vector((x + 0.016, -0.2965, -0.197)))
        bend_bar(bm, u, 0.0075, GUN, seg=10)
    g.add(bm)

    # --- the rear: a bumper, raised plates on the upper and lower rear plates
    bm = new_bm()
    box(bm, (0, 0.499, -0.1535), (0.296, 0.014, 0.023), PAINT)
    g.add(bm, bevel=(0.004, 2, 30))
    bm = new_bm()
    xs = np.linspace(-0.145, 0.145, 6)
    for a, b in zip(xs, xs[1:]):
        box(bm, ((a + b) / 2, 0.5005, -0.110), (b - a - 0.004, 0.005, 0.050), PAINT)
    lean = Matrix.Rotation(-math.atan2(0.479 - 0.452, 0.070), 3, "X")   # the lower plate's slope
    xs = np.linspace(-0.130, 0.130, 5)
    for a, b in zip(xs, xs[1:]):
        box(bm, ((a + b) / 2, 0.4655, -0.215), (b - a - 0.004, 0.005, 0.050), PAINT, rot=lean)
    g.add(bm, bevel=(0.0015, 2, 30))

    # --- rivets
    bm = new_bm()
    for s in (-1, 1):
        xf = s * (SKIRT_X[1] + 0.0004)
        for a, b in zip(edges, edges[1:]):
            ya, yb = a + 0.018, b - 0.018
            nn = max(2, int(round((yb - ya) / 0.05)) + 1)
            for z in (-0.240, -0.299):
                rivets(bm, [(xf, y, z) for y in np.linspace(ya, yb, nn)], (s, 0, 0))
        xb = s * (BAND_X - 0.001 + 0.0004)
        rivets(bm, [(xb, -0.240, -0.140), (xb, -0.180, -0.110), (xb, -0.240, -0.212)], (s, 0, 0),
               r=RIV * 0.9)
        xb = s * (BAND_X + 0.0004)
        rivets(bm, [(xb, 0.345, -0.090), (xb, 0.198, -0.146), (xb, 0.345, -0.146)], (s, 0, 0),
               r=RIV * 0.9)
        # the lamp's housing, the front box
        rivets(bm, [(s * x, -0.3004, -0.140) for x in (0.165, 0.233)], (0, -1, 0), r=RIV * 0.8)
        rivets(bm, [(s * x, -0.2164, -0.055) for x in (0.160, 0.230)], (0, -1, 0), r=RIV * 0.8)
    for x in np.linspace(-0.12, 0.12, 5):
        rivets(bm, [(x, 0.5034, -0.126)], (0, 1, 0), r=RIV * 0.8)
    g.add(bm, subsurf=1)

    if game():
        # the game variant loses its turret: what shows then is the ring's
        # opening, dark, not a painted collar -- and a knocked-out turret
        # tipped in its ring shows it under the raised side.  1 mm over the
        # collar, inside the ring (r 0.130)
        bm = new_bm()
        lathe(bm, [(COLLAR_TOP + 0.001, 0.0), (COLLAR_TOP + 0.001, 0.128)], DARK, seg=96,
              axis="Z", center=(RING_C[0], RING_C[1], 0), closed=False)
        g.add(bm)
    return g


def bpy_mesh(bm):
    import bpy
    me = bpy.data.meshes.new("_tile")
    bm.to_mesh(me)
    bm.free()
    return me


def free_mesh(me):
    import bpy
    bpy.data.meshes.remove(me)


def engine(mats):
    """The two grilles on the rear deck -- frames, dark beds, slats across --
    the exhaust source."""
    g = Group(mats)
    G = GRILLE
    bm = new_bm()
    bmd = new_bm()
    bms = new_bm()
    for s in (-1, 1):
        x0, x1 = sorted((s * G["x"][0], s * G["x"][1]))
        y0, y1 = G["y"]
        cx = 0.5 * (x0 + x1)
        zc = -0.065
        for yy in (y0 + 0.005, y1 - 0.005):
            box(bm, (cx, yy, zc), (x1 - x0, 0.010, 0.014), PAINT)
        for xx in (x0 + 0.005, x1 - 0.005):
            box(bm, (xx, 0.5 * (y0 + y1), zc), (0.010, y1 - y0 - 0.018, 0.014), PAINT)
        box(bmd, (cx, 0.5 * (y0 + y1), -0.0665), (x1 - x0 - 0.018, y1 - y0 - 0.018, 0.007), DARK)
        for yy in np.linspace(y0 + 0.016, y1 - 0.016, 14):
            box(bms, (cx, yy, -0.0615), (x1 - x0 - 0.020, 0.0065, 0.004), GUN)
    g.add(bm, bevel=(0.002, 2, 30))
    g.add(bmd)
    g.add(bms, bevel=(0.0012, 2, 30))
    return g


# ------------------------------------------------------------------- turret

def t_plan(yf):
    """The turret's plan at a section whose front is at yf: right half from
    the front centre to the rear centre, mirrored into a closed loop."""
    # (the sides step in 6 mm at y 0.085 over a 1 cm chamfer: a square riser
    # that narrow was all bevel and baked a neighbour's grey)
    half = [(0.0, yf), (0.140, yf), (0.151, yf + 0.004), (0.156, yf + 0.014), (0.157, 0.080),
            (0.151, 0.090), (0.151, 0.215), (0.083, 0.264), (0.0, 0.264)]
    return half + [(-x, y) for x, y in reversed(half[1:-1])]


def flat_loft(bm, rings, mi):
    """`loft` with its top one flat face, not the kit's fan from the centre: a
    boxy plan's fan is slivers wherever two ring points sit close (a corner),
    and slivers bake streaks and a neighbour's grey."""
    loft(bm, rings, mi, cap_top=False)
    res = bmesh.ops.holes_fill(bm, edges=[e for e in bm.edges if e.is_boundary], sides=0)
    for f in res["faces"]:
        f.material_index = mi
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))


def wall_x(y):
    """The side wall's x at y (between the front corner and the rear chamfer)."""
    return 0.156 + 0.001 * (y + 0.082) / 0.162 if y < 0.080 else 0.151


def turret(mats):
    g = Group(mats)
    cx, cy = RING_C
    # the rotating ring: a dense wall, alone in its slices (turret_axis fits
    # the roundest 3 % slice in the lowest 22 %)
    bm = new_bm()
    z0, z1, r = T_RING
    wall = [(z, r) for z in np.linspace(z0, z1, 2 if game() else 16)]
    lathe(bm, wall + [(z1, 0.10), (z0, 0.10)], PAINTDK, seg=192, axis="Z", center=(cx, cy, 0))
    g.add(bm)

    # body: a loft of plan sections, the front leaning back, the roof edge
    # chamfered deep at the front
    bm = new_bm()
    flat_loft(bm, [[Vector((x, y, z)) for x, y in t_plan(yf)] for z, yf in T_SECTIONS], PAINT_T)
    g.add(bm, bevel=(0.005, 2, 40))

    # the prow the gun comes out of: a wedge, its point on the bore
    P = PROW
    bm = new_bm()
    pts = []
    for x in (-P["hw"], P["hw"]):
        for y, z in (P["point"], P["top"], P["low"], (-0.095, P["top"][1]), (-0.095, P["low"][1])):
            pts.append((x, y, z))
    hull_solid(bm, pts, PAINT_T)
    g.add(bm, bevel=(0.006, 2, 30))
    # the brow over it on the roof, the raised plate at the back of the roof
    bm = new_bm()
    Hs = HOUSING
    box(bm, (0, sum(Hs["y"]) / 2, sum(Hs["z"]) / 2),
        (2 * Hs["hw"], Hs["y"][1] - Hs["y"][0], Hs["z"][1] - Hs["z"][0]), PAINT_T)
    g.add(bm, bevel=(0.005, 2, 30))
    bm = new_bm()
    ring = rr_loop(0.0, 0.1775, 0.118, 0.0725, 0.028, n=4)
    flat_loft(bm, [[Vector((x, y, z)) for x, y in ring] for z in (ROOF_Z - 0.005, 0.186)], PAINT_T)
    g.add(bm, bevel=(0.004, 2, 30))
    # plates on the face either side of the prow, leaning with it
    bm = new_bm()
    lean = math.atan2(T_SECTIONS[2][1] - T_SECTIONS[1][1], T_SECTIONS[2][0] - T_SECTIONS[1][0])
    rot = Matrix.Rotation(-lean, 3, "X")
    for s in (-1, 1):
        for z0, z1 in ((0.000, 0.060), (0.068, 0.124)):
            zc = 0.5 * (z0 + z1)
            yf = T_SECTIONS[1][1] + (zc - T_SECTIONS[1][0]) * math.tan(lean)
            box(bm, (s * 0.118, yf - 0.0025, zc), (0.050, 0.009, (z1 - z0) / math.cos(lean)),
                PAINT_T, rot=rot)
    g.add(bm, bevel=(0.0015, 2, 30))       # (at 2 mm on 6 mm plates the ends baked grey)
    # a hatch plate on the left, the rib down the rear face
    bm = new_bm()
    box(bm, (-0.080, 0.005, ROOF_Z + 0.0015), (0.090, 0.110, 0.006), PAINT_T)
    box(bm, (0, 0.276, 0.0765), (0.048, 0.026, 0.163), PAINT_T)
    g.add(bm, bevel=(0.002, 2, 30))

    # boxes on the sides, low; a handle on the long one
    bm = new_bm()
    for s in (-1, 1):
        for (x0, x1), (y0, y1), (za, zb) in (((0.151, 0.173), (-0.030, 0.025), (-0.022, 0.058)),
                                              ((0.148, 0.178), (0.058, 0.088), (-0.022, 0.058)),
                                              ((0.151, 0.176), (0.050, 0.084), (0.075, 0.140)),
                                              ((0.145, 0.186), (0.088, 0.185), (-0.022, 0.042))):
            a, b = sorted((s * x0, s * x1))
            box(bm, (0.5 * (a + b), 0.5 * (y0 + y1), 0.5 * (za + zb)), (b - a, y1 - y0, zb - za),
                PAINT_T)
    g.add(bm, bevel=(0.003, 2, 30))
    bm = new_bm()
    for s in (-1, 1):
        bend_bar(bm, [Vector((s * 0.181, 0.162, 0.038)), Vector((s * 0.181, 0.162, 0.058)),
                      Vector((s * 0.181, 0.184, 0.058)), Vector((s * 0.181, 0.184, 0.038))],
                 0.0035, GUN, seg=8)
    g.add(bm)

    # rivets: two rows along each side above the boxes, the rear face
    bm = new_bm()
    for s in (-1, 1):
        for z in (0.100, 0.148):
            for y in (-0.045, 0.0, 0.030, 0.110, 0.155, 0.195):
                rivets(bm, [(s * (wall_x(y) + 0.0004), y, z)], (s, 0, 0))
        for z in (0.030, 0.140):
            rivets(bm, [(s * 0.060, 0.2644, z)], (0, 1, 0))
    g.add(bm, subsurf=1)

    # cupola: a thick ring with vision slots, a box on top with its window
    hx, hy = CUPOLA
    bm = new_bm()
    # (the top a ring of quads round the box, its pole's fan of slivers
    # inside the box: out in the open they baked grey)
    lathe(bm, [(ROOF_Z - 0.007, 0.0), (ROOF_Z - 0.007, 0.054), (0.196, 0.054), (0.200, 0.050),
               (0.200, 0.030), (0.204, 0.030), (0.204, 0.0)], PAINT_T, seg=96,
          axis="Z", center=(hx, hy, 0))
    g.add(bm, bevel=(0.002, 2, 30))
    bm = new_bm()
    for a in (-90, -40, -140, 10, 170):
        t = math.radians(a)
        rot = Matrix.Rotation(t + math.pi / 2, 3, "Z")
        box(bm, (hx + 0.0535 * math.cos(t), hy + 0.0535 * math.sin(t), 0.184),
            (0.020, 0.004, 0.009), DARK, rot=rot)
    box(bm, (hx - 0.002, hy + 0.005 - 0.039, 0.2195), (0.050, 0.003, 0.010), DARK)
    g.add(bm)
    bm = new_bm()              # the hatch box, overhanging the ring a little at its corners
    box(bm, (hx - 0.002, hy + 0.005, 0.218), (0.088, 0.078, 0.034), PAINT_T)
    g.add(bm, bevel=(0.009, 2, 30))

    # antenna: a socket and a straight whip (the original's bends back; the
    # user wants it straight)
    ax, ay, az = ANTENNA
    bm = new_bm()
    lathe(bm, [(ROOF_Z - 0.004, 0.0), (ROOF_Z - 0.004, 0.007), (ROOF_Z + 0.012, 0.007),
               (ROOF_Z + 0.016, 0.005), (ROOF_Z + 0.022, 0.005), (ROOF_Z + 0.022, 0.0)], GUN,
          seg=24, axis="Z", center=(ax, ay, 0))
    g.add(bm)
    bm = new_bm()
    bend_bar(bm, [Vector((ax, ay, ROOF_Z + 0.020)), Vector((ax, ay, az))], 0.0022, GUN, seg=8)
    g.add(bm)
    return g


def mantlet(mats):
    """The square box the gun comes out of, and its sights: everything that
    lays with the gun and does not recoil.  It stands out of the prow's
    point and runs back into the turret body, so at either end of the ladder
    the prow still swallows its back: the part in the open changes length,
    nothing opens."""
    g = Group(mats)
    B = BLOCK
    bm = new_bm()
    box(bm, (0, sum(B["y"]) / 2, sum(B["z"]) / 2),
        (2 * B["hw"], B["y"][1] - B["y"][0], B["z"][1] - B["z"][0]), PAINT_T)
    g.add(bm, bevel=(0.005, 2, 30))
    bm = new_bm()
    for s in (-1, 1):
        box(bm, (s * (B["hw"] + 0.001), -0.212, 0.082), (0.004, 0.016, 0.011), GUN)
    g.add(bm, bevel=(0.001, 2, 30))
    return g


def oct_xz(hx, hz, c):
    """A square section (half widths hx, hz) with its corners cut by c."""
    return [(hx, hz - c), (hx - c, hz), (-hx + c, hz), (-hx, hz - c), (-hx, -hz + c),
            (-hx + c, -hz), (hx - c, -hz), (hx, -hz + c)]


def barrel(mats):
    """Breech stub, sleeve, tube, muzzle brake with a slot each side; bored.
    Sleeve and brake are squares with their corners cut, as the original's
    (0.068 and 0.065 across the flats, 0.081 across the corners).  The stub
    ends exactly on the trunnion, which is where `barrel_recoil.trunnion()`
    puts the pivot (the breech end of the tube on the bore), so the pipeline
    lays the gun about the same axis the model was built for."""
    g = Group(mats)
    y0 = BLOCK["y"][0]          # the sleeve starts on the box's face
    bm = new_bm()
    lathe(bm, [(TRUNNION[0] - y0, 0.0), (TRUNNION[0] - y0, 0.028), (-0.004, 0.028), (-0.004, 0.0)],
          GUN, seg=16, axis="Y", center=(0, y0, GUN_Z))
    g.add(bm)
    for (ya, yb), (hx, hz, c) in (((y0 + 0.004, -0.295), (0.034, 0.033, 0.010)),
                                  ((-0.415, -0.500), (0.0325, 0.035, 0.011))):
        bm = new_bm()
        hull_solid(bm, [(x, y, GUN_Z + z) for x, z in oct_xz(hx, hz, c) for y in (ya, yb)], GUN)
        g.add(bm, bevel=(0.0015, 2, 30))
    bm = new_bm()
    lathe(bm, [(-0.291, 0.0), (-0.291, 0.031), (-0.418, 0.031), (-0.418, 0.0)], GUN, seg=48,
          axis="Y", center=(0, 0, GUN_Z))
    g.add(bm, bevel=(0.0015, 2, 30))
    bm = new_bm()
    lathe(bm, [(-0.5003, 0.0), (-0.5003, 0.018)], DARK, seg=24, axis="Y", center=(0, 0, GUN_Z),
          closed=False)
    for s in (-1, 1):
        box(bm, (s * (0.0325 + 0.0002), -0.452, GUN_Z), (0.002, 0.034, 0.014), DARK)
    g.add(bm)
    return g
