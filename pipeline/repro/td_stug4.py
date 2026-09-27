"""TD_StuG4, rebuilt from scratch on repro_kit (docs/repro.md).

The fifth copy and the second casemate, 2026-09-27.  It stands 1.0 to the +X
of the original in the same scene, in collection `TD_StuG4.Repro`, every
name suffixed `.Repro`.  No turret: the welded casemate is `Casemate.Geometry`
under `Hull.World`, and so are the collar and the gun (docs/tank-scene.md,
"Машина без башни") -- the module has `casemate` where a turret has `turret`.

Every number is in TD_StuG4's own frame, measured off its vertices:

- front -Y; ground z -0.2663 -- the lowest vertex of the *rebuilt* belts
  (`*.Caterpillar.Rebuilt`, which the TDP sprites were rendered from);
- the left belt (`L.*`) on -X; belt centres x -0.2062 / +0.2053 -> 0.2057,
  0.098 wide, symmetric about z -0.1745 to a millimetre; the ends are not
  arcs of a circle but flattened (the track roots are scaled 0.72 x 0.37 x
  0.71): a superellipse, 0.18 long and the belt's half height tall, exponent
  2.2, fits both within 3 mm and meets the runs level (`belt_outline`);
- the belts run under a fender deck the full width of the hull (0.262), and
  five skirt plates a side hang off its edge down to z -0.243 -- a side view
  shows only the bottom run and the ends.  The road wheels are ellipses
  (0.15 x 0.09 the big ones): the copy's are round, on the same axles,
  sized to the belt (`fit_r`), and hidden but for the front and rear views;
- no ring: the casemate is a box of planes -- front 43.5 deg from upright,
  sides 21 deg, the rear 15 deg -- on a hull that is itself a box; a
  steeper housing stands out of the front plate round the gun, the square
  mount (the gun's grey) on its face.  The pipeline turns the carousel about
  `parts_render.hull_axis()` and never runs `turret_axis`;
- the gun: a long tube with a sleeve, a neck and a muzzle brake, level but
  for 0.9 deg up (the original's bore: muzzle
  (0.000, -0.500, 0.046), heading 270.0, elevation 1.2 by its principal
  axis; the tube's centres rise 0.9 deg).  The square mount is the
  casemate's; the collar in front of it lays with the gun (`mantlet`); the
  tube recoils.  The trunnion is on the bore 1 cm behind the housing's face;
- one material on everything under `Hull.World`, images 4096; belts and
  wheels 2048 (the copy bakes the canon's 2048 everywhere);
- stamps (`muzzle_*`, `hit_*`, `exhaust_*`) and the effect meshes are tool
  output: not repeated.
"""

import math
import bmesh
import numpy as np
from mathutils import Matrix, Vector

from repro_kit import (
    ARMY_PALETTE,
    PAINT, GUN, TRACK, GLASS, GLASS_T, DARK, PAINTDK, RIVET, RIVET_G, RIV,
    Group, new_bm, box, prism, lathe, rounded_rect_profile, cyl, rivets,
    bend_bar, hull_solid, loft, place_belt, tilt, game, segs,
)

NAME = "TD_StuG4"
SFX = ".Repro"
X_OFF = 1.0
PREFIX = "TDR"
GROUND = -0.2663
TRACK_X = 0.2057
# the original's belts that the sprites show: the kit compares against these
ORIGINAL = {"l_cat": "L.Caterpillar.Rebuilt", "r_cat": "R.Caterpillar.Rebuilt"}

# the army's paint, not the original's (repro_kit.ARMY_PALETTE)
PALETTE = ARMY_PALETTE

# ------------------------------------------------------------------- layout

BELLY_Z = -0.2226
# between the belts: 4 mm inside their inner edge (0.1562) -- the nose and
# the tail stand in front of the ends
CORE_X = 0.152
# the hull between the belts, side profile: the nose plate leaning forward
# up to the glacis' lip, a top well under the deck (8+ mm: overlapping solids
# never share a face), the rear plate, its lower chamfer
CORE = [(-0.229, BELLY_Z), (0.434, BELLY_Z), (0.466, -0.152), (0.477, -0.152),
        (0.477, -0.040), (-0.110, -0.040), (-0.150, -0.055), (-0.180, -0.067),
        (-0.262, -0.118)]
# the deck over everything, fenders included: the glacis (43 deg) from its lip
# to a front deck rising gently to the casemate's foot, level under the
# casemate and the engine deck; its underside clears the belts' top run and
# ends
UPPER_X = 0.262
UPPER = [(-0.268, -0.110), (-0.200, -0.047), (-0.110, -0.030), (0.470, -0.030),
         (0.470, -0.112), (0.430, -0.076), (-0.220, -0.076)]
GL0, GL1 = (-0.268, -0.110), (-0.200, -0.047)     # the glacis, (y, z)
# the skirt: five plates a side off the deck's edge, the front one's top cut
# down along the glacis, the rear one's bottom corner cut (inner face 2 mm
# outside the belt's outer edge 0.2543)
SKIRT_X = (0.2565, 0.2661)
SKIRT_Z = (-0.243, -0.028)
SKIRT_SEAMS = (-0.253, -0.114, 0.026, 0.174, 0.314, 0.482)
SKIRT_FRONT = [(-0.114, -0.028), (-0.184, -0.048), (-0.253, -0.110), (-0.253, -0.243),
               (-0.114, -0.243)]
SKIRT_REAR = [(0.314, -0.028), (0.314, -0.243), (0.446, -0.243), (0.482, -0.194),
              (0.482, -0.028)]
# the lamps on the glacis' corners: a housing, a lens in a rim facing ahead
# (its face 1 cm behind the glacis' lip: a side view shows nothing ahead of
# the skirt)
LAMP_BOX = dict(x=(0.150, 0.250), y=(-0.258, -0.205), z=(-0.100, -0.046))
LAMP = dict(x=0.200, y=-0.258, z=-0.073, r=0.013)
HOOK_X = 0.110
# the engine deck behind the casemate, its rear lip, the grille on the right
DECK = dict(x=0.235, y=(0.296, 0.470), z=(-0.036, -0.006))
LIP = dict(x=0.240, y=(0.462, 0.484), z=(-0.034, -0.002))
GRILLE = dict(x=(0.004, 0.182), y=(0.352, 0.434))

# running gear: two level runs and two superelliptic ends, a sprocket in the
# front end, an idler in the rear, three road wheels, two return rollers, two
# small ones.  The ends off the rebuilt belt's half height per 2 cm of y (the
# belt is symmetric about BELT_ZC): 0.0387 / 0.0567 / 0.0672 / 0.0767 /
# 0.0816 / 0.0865 at 1.4 ... 11.4 cm from the front tip, the rear alike -- an
# ellipse is too lean near the tip, and the bands' extremes (the first
# outline) stepped 1 cm where the bottom run met the front end: a kinked link
BELT_H = 0.0918                        # half height, outer
BELT_ZC = GROUND + BELT_H
BELT_Z = (GROUND, GROUND + 2 * BELT_H)  # outer bottom and top runs
TIPS = (-0.2943, 0.5258)
END_A, END_P = 0.180, 2.2
BELT_C = ((TIPS[0] + END_A, BELT_ZC), (TIPS[1] - END_A, BELT_ZC))    # where the runs end
BELT_T = 0.016
BELT_W = 0.098
PITCH = 0.0205
SPROCKET_Y, IDLER_Y = -0.200, 0.432           # sized to the flattened ends by fit_r
_ROAD_R = 0.040
_ROAD_Z = GROUND + BELT_T + _ROAD_R + 0.001
ROAD = [(-0.032, _ROAD_Z, _ROAD_R), (0.113, _ROAD_Z, _ROAD_R), (0.255, _ROAD_Z, _ROAD_R)]
_UP_R = 0.022
UPPER_WHEELS = [(-0.023, BELT_Z[1] - BELT_T - _UP_R - 0.001, _UP_R),
                (0.163, BELT_Z[1] - BELT_T - _UP_R - 0.001, _UP_R)]
_SMALL_R = 0.022
SMALL = [(-0.112, GROUND + BELT_T + _SMALL_R + 0.001, _SMALL_R),
         (0.378, GROUND + BELT_T + _SMALL_R + 0.001, _SMALL_R)]

# the game variant: where the sprung mass rocks (mid-belt, level with the
# belt tops), where the exhaust leaves (the grille), where the blast is (the
# fighting compartment, under the roof hatch)
BODY_PIVOT = ((BELT_C[0][0] + BELT_C[1][0]) / 2, BELT_Z[1])
EXHAUST = ((0.093, 0.393, -0.004),)
BLAST = (0.0, 0.110, 0.030)
GAME_TAG = "TDR"                        # Models/TDR/

# casemate: every face a plane -- front y = -0.102 + 0.95 z, sides
# x = 0.227 - 0.38 z, rear y = 0.3125 - 0.26 z -- from its foot (buried 8 mm
# in the deck, the rear in the engine deck) to the roof at 0.113
CM_Z0 = -0.045
ROOF_Z = 0.113


def front_y(z):
    return -0.102 + 0.95 * z


def side_x(z):
    return 0.227 - 0.38 * z


def rear_y(z):
    return 0.3125 - 0.26 * z


PLATE_V = Vector((0.0, 0.95, 1.0)).normalized()     # up the front plate
PLATE_N = Vector((0.0, -1.0, 0.95)).normalized()    # out of it
# the housing round the gun: its face nearly upright (y = -0.110 + 0.1 z),
# its top sloping back into the front plate
HOUSING_X = 0.060
HOUSING = [(-0.114, -0.040), (-0.1006, 0.094), (-0.062, 0.102), (-0.005, 0.102), (0.0, -0.040)]
FACE_N = Vector((0.0, -1.0, 0.1)).normalized()      # out of the housing's face


def face_y(z):
    return -0.110 + 0.1 * z


# the gun: level but for 0.9 deg up; the trunnion on the bore 1 cm behind the
# housing's face (the mount and the collar's buried end are in front of it)
GUN_EL = 0.9
TRUNNION = (-0.095, 0.043)
BRAKE_R = 0.041
BRAKE_BAR = 55.0                        # half the angle of each bar: windows +-35 deg
MOUNT = dict(hw=0.050, z=(0.000, 0.087), d=(-0.004, 0.010))
LAY_DIST, LAY_AIM = 1.5, (-0.160, 0.0)
RIB_X = (0.075, 0.123)                  # the two ribs up the front plate
VISOR = dict(x=(0.145, 0.195), y=-0.122, z=(-0.016, 0.009))
HATCH = (0.049, 0.120, 0.068)            # the round roof hatch with its handle
ANTENNA = (0.135, 0.292, 0.255)


# ------------------------------------------------------------------- tracks

def link_mesh(pitch):
    """One link (u along the belt, v across, w out), w = 0 at mid thickness
    (`belt_spec`): a plate the full width, three pads across it, the outer
    two nearly the pitch long -- seen from the side the belt is a chain of
    blocks, as on the original, not a row of loose pads over a thin plate."""
    h = BELT_T / 2
    bm = new_bm()
    box(bm, (0, 0, 0.0045 - h), (pitch * 0.94, BELT_W, 0.009), TRACK)
    box(bm, (0, 0, 0.0125 - h), (pitch * 0.80, 0.026, 0.007), TRACK)
    for v in (-0.034, 0.034):
        box(bm, (0, v, 0.0125 - h), (pitch * 0.96, 0.030, 0.007), TRACK)
    return bm


def belt_outline(n=64):
    """The belt's outer edge as a CCW (y, z) polygon: the bottom run, the
    rear end, the top run, the front end.  Each end is a superellipse
    |dy/END_A|^p + |dz/BELT_H|^p = 1 about the run's end, so it meets the run
    level -- no corner for a link to break over."""
    (yf, zc), (yr, _) = BELT_C
    a, b, e = END_A, BELT_H, 2.0 / END_P

    def end(y0, t0, t1):
        return [(y0 + a * math.copysign(abs(math.cos(t)) ** e, math.cos(t)),
                 zc + b * math.copysign(abs(math.sin(t)) ** e, math.sin(t)))
                for t in np.linspace(t0, t1, n + 1)][:-1]
    m = int((yr - yf) / 0.01)
    pts = [(y, zc - b) for y in np.linspace(yf, yr, m, endpoint=False)]
    pts += end(yr, -math.pi / 2, math.pi / 2)
    pts += [(y, zc + b) for y in np.linspace(yr, yf, m, endpoint=False)]
    pts += end(yf, math.pi / 2, 1.5 * math.pi)
    return np.array(pts, float)


def inset(d):
    """The outline moved in by d (left of travel on a CCW loop)."""
    P = belt_outline()
    t = np.roll(P, -1, 0) - np.roll(P, 1, 0)
    t /= np.linalg.norm(t, axis=1)[:, None]
    return P + np.c_[-t[:, 1], t[:, 0]] * d


def belt_spec():
    """The belt's mid-thickness as a CCW (y, z) path, its pitch, its link.
    Not the inner surface, as the other copies lay theirs: the ends are
    tight (4 cm inside), and rigid links laid on the inner surface opened
    40 % of a pitch between their outer faces there; about the middle the
    gap halves and the overlap stays inside the loop."""
    return inset(BELT_T / 2), PITCH, link_mesh


def belt(mats, xc):
    path, pitch, link = belt_spec()
    # crisp blocks, chamfered, not subdivided: subdivision rounded each pad
    # into a pill, and the side view read as loose pads, not a chain
    bm, n, pitch, L = place_belt(mats, xc, path, pitch, link, bevel=(0.0015, 2, 35), subsurf=0)
    return bm, {"links": n, "pitch": round(pitch, 5), "length": round(L, 4)}


def fit_r(y, z, clear=0.002):
    """The biggest round wheel at (y, z) inside the belt's inner surface, less
    `clear`: the flattened ends take no circle of the runs' half height."""
    path = inset(BELT_T)
    a, b = path, np.roll(path, -1, 0)
    p = np.array([y, z])
    ab = b - a
    t = np.clip(((p - a) * ab).sum(1) / (ab * ab).sum(1), 0.0, 1.0)
    d = np.linalg.norm(a + ab * t[:, None] - p, axis=1)
    return float(d.min()) - clear


def disc(bm, y, z, r, xc, w, seg=48, mi=TRACK):
    """A dished wheel centred at xc: tyre, dish, hub boss (lathe about X)."""
    h = w / 2
    prof = [(-h - 0.005, 0.0), (-h - 0.005, r * 0.22), (-h, r * 0.28), (-h, r * 0.44),
            (-h + 0.006, r * 0.52), (-h + 0.006, r * 0.74), (-h, r * 0.82), (-h, r * 0.93),
            (-h + 0.004, r), (h - 0.004, r), (h, r * 0.93), (h, r * 0.82),
            (h - 0.006, r * 0.74), (h - 0.006, r * 0.52), (h, r * 0.44), (h, r * 0.28),
            (h + 0.005, r * 0.22), (h + 0.005, 0.0)]
    lathe(bm, prof, mi, seg=seg, axis="X", center=(xc, y, z))


def wheel_spec():
    """Every wheel that turns: name, axle (y, z), and the radius the belt turns
    it at (the engine spins each one distance / r)."""
    zc = BELT_C[0][1]
    out = [("Sprocket", (SPROCKET_Y, zc, fit_r(SPROCKET_Y, zc, 0.001)))]
    out += [("Road.%d" % i, w) for i, w in enumerate(ROAD)]
    out += [("Upper.%d" % i, w) for i, w in enumerate(UPPER_WHEELS)]
    out += [("Small.%d" % i, w) for i, w in enumerate(SMALL)]
    out += [("Idler", (IDLER_Y, zc, fit_r(IDLER_Y, zc, 0.001)))]
    return [{"name": n, "axle": (y, z), "r": r} for n, (y, z, r) in out]


def wheels(mats, xc, s):
    """wheel_spec, each with its own Group -- one object per wheel in the game
    variant, so each can turn about its axle.  The return rollers stand
    inboard of the road wheels."""
    out = []
    for w in wheel_spec():
        (y, z), r = w["axle"], w["r"]
        g = Group(mats)
        bm = new_bm()
        seg = 64 if w["name"] in ("Idler", "Sprocket") else 48
        if game():
            seg = int(seg * 0.8)
        if w["name"] == "Sprocket":
            disc(bm, y, z, r - 0.012, xc, 0.060, seg=seg)
        elif w["name"] == "Idler":
            disc(bm, y, z, r - 0.002, xc, 0.060, seg=seg)
        elif w["name"].startswith("Upper"):
            disc(bm, y, z, r, xc - s * 0.020, 0.034, seg=seg)
        elif w["name"].startswith("Small"):
            disc(bm, y, z, r, xc, 0.040, seg=seg)
        else:
            disc(bm, y, z, r, xc + s * 0.012, 0.052, seg=seg)
        g.add(bm, subsurf=1)
        if w["name"] == "Sprocket":
            bm = new_bm()          # teeth, two rings, their tips on the belt's inner surface
            for k in range(14):
                t = math.tau * k / 14
                rot = Matrix.Rotation(t, 3, "X")
                for dx in (-0.021, 0.021):
                    c = Vector((xc + dx, y, z)) + rot @ Vector((0, 0, r - 0.009))
                    box(bm, c, (0.012, 0.012, 0.016), TRACK, rot=rot)
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
    xin = xc - s * 0.044           # inboard of the return rollers
    zb = -0.165
    box(bm, (xin, 0.11, zb), (0.010, 0.52, 0.016), TRACK)
    for (y, z, r) in ROAD:
        a, b_ = Vector((xin, y + 0.05, zb)), Vector((xin, y, z))
        d = b_ - a
        rot = Vector((0, 1, 0)).rotation_difference(d.normalized()).to_matrix()
        box(bm, (a + b_) / 2, (0.010, d.length + 0.014, 0.014), TRACK, rot=rot)
    zc = BELT_C[0][1]
    for (y, z) in [(SPROCKET_Y, zc), (IDLER_Y, zc)] + [(w[0], w[1]) for w in ROAD + UPPER_WHEELS + SMALL]:
        cyl(bm, (xin, y, z), (xc, y, z), 0.009, TRACK, seg=12)
    g.add(bm, bevel=(0.003, 2, 40), subsurf=1)
    return g


def debris():
    """What flies off when the tank blows up, for the game variant: a name,
    the node it comes off, and a box in this frame.  Every piece of that
    node's mesh whose centre lies in the box goes with it.  Sides are the
    tank's own: L is +X.  The casemate is the hull's (welded), so what flies
    off it comes off `Hull` too."""
    out = []
    for s, S in ((1, "L"), (-1, "R")):
        def sx(a, b):
            return tuple(sorted((s * a, s * b)))
        for i, (a, b) in enumerate(zip(SKIRT_SEAMS, SKIRT_SEAMS[1:])):
            out.append({"name": "Skirt.%s.%d" % (S, i), "parent": "Hull",
                        "box": (sx(0.2585, 0.275), (a + 0.001, b - 0.001), (-0.25, -0.02))})
        out.append({"name": "Lamp.%s" % S, "parent": "Hull",
                    "box": (sx(0.140, 0.259), (-0.290, -0.200), (-0.110, -0.040))})
        out.append({"name": "Hook.%s" % S, "parent": "Hull",
                    "box": (sx(0.070, 0.150), (-0.290, -0.235), (-0.195, -0.100))})
        out.append({"name": "Links.%s" % S, "parent": "Hull",
                    "box": (sx(0.160, 0.250), (0.050, 0.235), (0.015, 0.110))})
    hx, hy, hr = HATCH
    out.append({"name": "Hatch", "parent": "Hull",
                "box": ((hx - hr - 0.03, hx + hr + 0.03), (hy - hr - 0.03, hy + hr + 0.03),
                        (ROOF_Z + 0.001, 0.20))})
    ax, ay, az = ANTENNA
    out.append({"name": "Antenna", "parent": "Hull",
                "box": ((ax - 0.025, ax + 0.025), (ay - 0.025, ay + 0.025), (0.080, az + 0.01))})
    return out


# --------------------------------------------------------------------- hull

def SMALL_BEVEL(w):
    """A small part's chamfer: two segments on the copy, none on the game
    variant -- a single 2-3 mm face bakes a neighbour's grey there
    (hm_sturmtiger.py), and the engine's outline draws the edge anyway."""
    return None if game() else (w, 2, 30)


def bpy_mesh(bm):
    import bpy
    me = bpy.data.meshes.new("_tile")
    bm.to_mesh(me)
    bm.free()
    return me


def free_mesh(me):
    import bpy
    bpy.data.meshes.remove(me)


def solids(pieces, mi):
    """Several convex solids into one bmesh (hull_solid wants a fresh bmesh
    per solid: it hulls every vertex it is given)."""
    bm = new_bm()
    for pts in pieces:
        sub = new_bm()
        hull_solid(sub, pts, mi)
        me = bpy_mesh(sub)
        bm.from_mesh(me)
        free_mesh(me)
    return bm


def sbox(bm, s, xr, yr, zr, mi, rot=None):
    """A box on side s (x range given for +X, mirrored for s = -1)."""
    x0, x1 = sorted((s * xr[0], s * xr[1]))
    box(bm, (0.5 * (x0 + x1), 0.5 * (yr[0] + yr[1]), 0.5 * (zr[0] + zr[1])),
        (x1 - x0, yr[1] - yr[0], zr[1] - zr[0]), mi, rot=rot)


def nose_y(z):
    """The nose plate between the belly's front and the glacis' lip."""
    (y0, z0), (y1, z1) = CORE[0], CORE[-1]
    return y0 + (y1 - y0) * (z - z0) / (z1 - z0)


def glacis(z):
    """(y, z) on the glacis at height z, and its outward normal (y, z)."""
    a, b = Vector(GL0), Vector(GL1)
    up = (b - a).normalized()
    return a + up * ((z - a.y) / up.y), Vector((-up.y, up.x))


def hull(mats):
    g = Group(mats)

    # --- between the belts: the nose, the belly, the rear plate
    bm = new_bm()
    prism(bm, CORE, -CORE_X, CORE_X, PAINT)
    g.add(bm, bevel=(0.006, 2, 30))
    # --- the deck the full width: glacis, front deck, fenders
    bm = new_bm()
    prism(bm, UPPER, -UPPER_X, UPPER_X, PAINT)
    g.add(bm, bevel=(0.004, 2, 30))
    # the plate on the glacis, 10 mm proud, 3 mm into it
    pts = []
    for z in (-0.092, -0.054):
        q, nn = glacis(z)
        for d in (-0.003, 0.010):
            p = q + nn * d
            for x in (-0.075, 0.075):
                pts.append((x, p.x, p.y))
    g.add(solids([pts], PAINT), bevel=(0.003, 2, 30))
    bm = new_bm()
    for z in (-0.086, -0.060):
        q, nn = glacis(z)
        q = q + nn * 0.0104
        rivets(bm, [(x, q.x, q.y) for x in (-0.062, 0.062)], (0.0, nn.x, nn.y), r=RIV * 0.8)
    g.add(bm, subsurf=1)

    # --- the skirt: five plates a side
    bm = new_bm()
    z0, z1 = SKIRT_Z
    for s in (-1, 1):
        x0, x1 = sorted((s * SKIRT_X[0], s * SKIRT_X[1]))
        n = len(SKIRT_SEAMS) - 1
        for i, (a, b) in enumerate(zip(SKIRT_SEAMS, SKIRT_SEAMS[1:])):
            if i == 0:
                poly = [(min(y, b - 0.0015), z) for y, z in SKIRT_FRONT]
            elif i == n - 1:
                poly = [(max(y, a + 0.0015), z) for y, z in SKIRT_REAR]
            else:
                poly = [(a + 0.0015, z1), (a + 0.0015, z0), (b - 0.0015, z0), (b - 0.0015, z1)]
            prism(bm, poly, x0, x1, PAINT)
    g.add(bm, bevel=(0.0018, 2, 30))
    # the vent low on the rear plate: three dark slots
    bm = new_bm()
    for s in (-1, 1):
        for z in (-0.187, -0.194, -0.201):
            sbox(bm, s, (SKIRT_X[1] - 0.001, SKIRT_X[1] + 0.0012), (0.417, 0.455),
                 (z - 0.0022, z + 0.0022), DARK)
    g.add(bm)

    # --- the lamps: a housing on each glacis corner, the lens in a rim
    pts_all = []
    L = LAMP_BOX
    for s in (-1, 1):
        x0, x1 = sorted((s * L["x"][0], s * L["x"][1]))
        yf, yb = L["y"]
        zl, zt = L["z"]
        yb_low = glacis(zl)[0].x + 0.012         # the bottom's back, into the glacis
        pts_all.append([(x, y, z) for x in (x0, x1)
                        for y, z in ((yf, zl), (yf, zt), (yb, zt), (yb_low, zl))])
    g.add(solids(pts_all, PAINT), bevel=(0.004, 2, 30))
    bm = new_bm()
    bmg = new_bm()
    for s in (-1, 1):
        c = (s * LAMP["x"], LAMP["y"], LAMP["z"])
        # a rim round the lens, open inside it (a tube to the axis bakes the
        # glass' grey: docs/repro.md, "Щепки запекаются чужим цветом")
        lathe(bm, rounded_rect_profile(-0.008, 0.003, LAMP["r"] - 0.002, LAMP["r"] + 0.007, 0.003, n=2),
              PAINT, seg=48, axis="Y", center=c)
        lathe(bmg, [(-0.004, 0.0), (-0.0035, LAMP["r"] * 0.55), (-0.0015, LAMP["r"] * 0.9),
                    (0.001, LAMP["r"])], GLASS, seg=48, axis="Y", center=c, closed=False)
    g.add(bm, subsurf=1)
    g.add(bmg, subsurf=1)

    # --- tow hooks on the nose: a lug, a U hanging off it along the plate
    bm = new_bm()
    for s in (-1, 1):
        box(bm, (s * HOOK_X, nose_y(-0.124) - 0.004, -0.124), (0.016, 0.016, 0.026), PAINT)
    g.add(bm, bevel=(0.003, 2, 30))
    bm = new_bm()
    for s in (-1, 1):
        x = s * HOOK_X
        u = []
        for z, dx in ((-0.128, -0.020), (-0.166, -0.020)):
            u.append(Vector((x + dx, nose_y(z) - 0.0075, z)))
        for k in range(1, 8):
            t = math.pi * k / 8
            z = -0.166 - 0.020 * math.sin(t)
            u.append(Vector((x - 0.020 * math.cos(t), nose_y(z) - 0.0075, z)))
        for z in (-0.166, -0.128):
            u.append(Vector((x + 0.020, nose_y(z) - 0.0075, z)))
        bend_bar(bm, u, 0.0055, GUN, seg=10)
    g.add(bm)

    # --- the engine deck, its rear lip, plates on its left half
    bm = new_bm()
    D = DECK
    box(bm, (0, sum(D["y"]) / 2, sum(D["z"]) / 2), (2 * D["x"], D["y"][1] - D["y"][0],
                                                     D["z"][1] - D["z"][0]), PAINT)
    g.add(bm, bevel=(0.004, 2, 30))
    bm = new_bm()
    P = LIP
    box(bm, (0, sum(P["y"]) / 2, sum(P["z"]) / 2), (2 * P["x"], P["y"][1] - P["y"][0],
                                                     P["z"][1] - P["z"][0]), PAINT)
    g.add(bm, bevel=(0.003, 2, 30))
    bm = new_bm()
    for (x0, x1), (y0, y1), h in (((-0.152, -0.084), (0.393, 0.450), 0.006),
                                  ((-0.186, -0.058), (0.318, 0.358), 0.004)):
        box(bm, ((x0 + x1) / 2, (y0 + y1) / 2, D["z"][1] + h / 2 - 0.002), (x1 - x0, y1 - y0, h + 0.002),
            PAINT)
    g.add(bm, bevel=SMALL_BEVEL(0.002))

    # --- the rear: two doors on the plate between the belts, a dark gap
    # between them, the fenders' rear plates
    bm = new_bm()
    for s in (-1, 1):
        sbox(bm, s, (0.020, 0.136), (0.469, 0.483), (-0.148, -0.028), PAINT)
        sbox(bm, s, (0.156, 0.254), (0.466, 0.478), (-0.110, -0.026), PAINT)
    g.add(bm, bevel=(0.003, 2, 30))
    bm = new_bm()
    for s in (-1, 1):                 # the doors' raised frames: bars round the edge
        for (xa, xb), (za, zb) in (((0.026, 0.130), (-0.040, -0.034)), ((0.026, 0.130), (-0.142, -0.136)),
                                   ((0.026, 0.032), (-0.136, -0.040)), ((0.124, 0.130), (-0.136, -0.040))):
            sbox(bm, s, (xa, xb), (0.480, 0.486), (za, zb), PAINT)
    g.add(bm, bevel=SMALL_BEVEL(0.0015))
    bm = new_bm()                     # (a shadowed recess, dark paint, not a hole)
    box(bm, (0, 0.4775, -0.088), (0.024, 0.003, 0.118), PAINTDK)
    g.add(bm)

    # --- rivets: the skirt plates' corners, the rear lip, the nose
    bm = new_bm()
    for s in (-1, 1):
        xf = s * (SKIRT_X[1] + 0.0004)
        for i, (a, b) in enumerate(zip(SKIRT_SEAMS, SKIRT_SEAMS[1:])):
            ya, yb = a + 0.012, b - 0.012
            pts = [(xf, y, z) for y in (ya, yb) for z in (-0.040, -0.228)]
            if i == 0:
                pts = [(xf, yb, -0.040), (xf, yb, -0.228), (xf, -0.241, -0.228), (xf, -0.241, -0.124),
                       (xf, -0.212, -0.090)]
            elif i == len(SKIRT_SEAMS) - 2:          # (above the cut corner)
                pts = [(xf, ya, -0.040), (xf, ya, -0.228), (xf, yb, -0.040), (xf, yb, -0.182)]
            rivets(bm, pts, (s, 0, 0))
    for x in np.linspace(-0.20, 0.20, 7):
        rivets(bm, [(x, LIP["y"][1] + 0.0004, -0.018)], (0, 1, 0), r=RIV * 0.8)
    (y0, z0), (y1, z1) = CORE[0], CORE[-1]
    nn = Vector((0.0, -(z1 - z0), (y1 - y0))).normalized()     # out of the nose plate
    for x in (-0.12, -0.06, 0.06, 0.12):
        z = -0.205
        rivets(bm, [tuple(Vector((x, nose_y(z), z)) + nn * 0.0004)], tuple(nn), r=RIV * 0.8)
    g.add(bm, subsurf=1)
    return g


def engine(mats):
    """The grille on the right of the engine deck -- a frame, a dark bed, bars
    along it: the exhaust source."""
    g = Group(mats)
    G = GRILLE
    top = DECK["z"][1] + 0.005
    x0, x1 = G["x"]
    y0, y1 = G["y"]
    cx, cy = 0.5 * (x0 + x1), 0.5 * (y0 + y1)
    bm = new_bm()
    bmd = new_bm()
    bms = new_bm()
    for yy in (y0 + 0.005, y1 - 0.005):
        box(bm, (cx, yy, top - 0.006), (x1 - x0, 0.010, 0.012), PAINT)
    for xx in (x0 + 0.005, x1 - 0.005):
        box(bm, (xx, cy, top - 0.006), (0.010, y1 - y0 - 0.016, 0.012), PAINT)
    box(bmd, (cx, cy, DECK["z"][1] + 0.0008), (x1 - x0 - 0.016, y1 - y0 - 0.016, 0.004), DARK)
    # flat slats along it, dark slits between (the original: green bars)
    n = 7
    w = (x1 - x0 - 0.020) / n
    for i in range(n):
        xx = x0 + 0.010 + w * (i + 0.5)
        box(bms, (xx, cy, top - 0.005), (w - 0.008, y1 - y0 - 0.018, 0.008), PAINT)
    g.add(bm, bevel=(0.002, 2, 30))
    g.add(bmd)
    g.add(bms, bevel=SMALL_BEVEL(0.0015))
    return g


# ----------------------------------------------------------------- casemate

def on_plate(x, z, d):
    """A point d out of the front plate at (x, z)."""
    return Vector((x, front_y(z), z)) + PLATE_N * d


def on_face(x, z, d):
    """A point d out of the housing's face at (x, z)."""
    return Vector((x, face_y(z), z)) + FACE_N * d


def on_side(s, y, z, d):
    n = Vector((s * 1.0, 0.0, 0.38)).normalized()
    return Vector((s * side_x(z), y, z)) + n * d, n


def on_rear(x, z, d):
    """A point d out of the rear plate at (x, z)."""
    n = Vector((0.0, 1.0, 0.26)).normalized()
    return Vector((x, rear_y(z), z)) + n * d


def casemate(mats):
    g = Group(mats)

    # the body: one convex solid, its foot in the deck
    pts = []
    for s in (-1, 1):
        for z in (CM_Z0, ROOF_Z):
            pts += [(s * side_x(z), front_y(z), z), (s * side_x(z), rear_y(z), z)]
    g.add(solids([pts], PAINT), bevel=(0.006, 2, 30))
    # the housing round the gun
    g.add(solids([[(x, y, z) for x in (-HOUSING_X, HOUSING_X) for y, z in HOUSING]], PAINT),
          bevel=(0.004, 2, 30))

    # the square mount on the housing's face (the gun's grey) and its bolts --
    # fixed, the collar in front of it lays with the gun
    M = MOUNT
    ring = [(x, z) for x in (-M["hw"], M["hw"]) for z in M["z"]]
    g.add(solids([[tuple(on_face(x, z, d)) for x, z in ring for d in M["d"]]], GUN),
          bevel=(0.003, 2, 30))
    bm = new_bm()
    for x in (-0.038, 0.038):
        for z in (M["z"][0] + 0.012, M["z"][1] - 0.012):
            cyl(bm, on_face(x, z, M["d"][1] - 0.002), on_face(x, z, M["d"][1] + 0.005), 0.0070,
                RIVET_G, seg=6)
    g.add(bm, bevel=(0.0012, 1, 30))

    # --- the front plate: two ribs up it with feet, the visor boxes at its foot
    pts = []
    for s in (-1, 1):
        pts.append([tuple(on_plate(s * x, z, d)) for x in RIB_X for z in (CM_Z0 + 0.012, 0.100)
                    for d in (-0.010, 0.012)])
    g.add(solids(pts, PAINT), bevel=(0.003, 2, 30))
    pts = []
    for s in (-1, 1):
        pts.append([(s * x, y, z) for x in (RIB_X[0] - 0.003, RIB_X[1] + 0.003)
                    for y, z in ((-0.146, -0.040), (-0.146, -0.006), (front_y(-0.006) - 0.004, -0.006),
                                 (front_y(-0.036) - 0.004, -0.036))])
    g.add(solids(pts, PAINT), bevel=SMALL_BEVEL(0.002))
    V = VISOR
    pts = []
    for s in (-1, 1):
        zl, zt = V["z"]
        pts.append([(s * x, y, z) for x in V["x"]
                    for y, z in ((V["y"], zl), (V["y"], zt), (front_y(zt) + 0.004, zt),
                                 (front_y(zl) + 0.004, zl))])
    g.add(solids(pts, PAINT), bevel=(0.003, 2, 30))
    bm = new_bm()
    for s in (-1, 1):
        sbox(bm, s, (V["x"][0] + 0.009, V["x"][1] - 0.012), (V["y"] - 0.0015, V["y"] + 0.002),
             (-0.007, 0.004), GLASS_T)
    g.add(bm)

    # --- the roof: the round hatch with a handle across it, two boxes, the
    # antenna on the rear corner
    hx, hy, hr = HATCH
    bm = new_bm()             # (a ring and a capped disc: no lathe pole's fan in the open)
    # (a thick lid: the original's hump stands 2.5 cm over the roof)
    lathe(bm, [(ROOF_Z - 0.004, hr - 0.012), (ROOF_Z - 0.004, hr), (ROOF_Z + 0.014, hr),
               (ROOF_Z + 0.020, hr - 0.005), (ROOF_Z + 0.020, hr - 0.010), (ROOF_Z + 0.015, hr - 0.012)],
          GUN, seg=96, axis="Z", center=(hx, hy, 0))
    cyl(bm, (hx, hy, ROOF_Z - 0.004), (hx, hy, ROOF_Z + 0.017), hr - 0.0125, GUN, seg=96)
    g.add(bm, bevel=(0.0015, 2, 30))
    if game():
        # the game variant blows the hatch off: what shows then is the dark
        # opening, not painted roof -- inside the hatch's ring until then
        bm = new_bm()
        lathe(bm, [(ROOF_Z + 0.0006, 0.0), (ROOF_Z + 0.0006, hr - 0.003)], DARK, seg=48,
              axis="Z", center=(hx, hy, 0), closed=False)
        g.add(bm)
    # the handle: a bar across the lid at 38 deg, raised on two posts, its
    # outer end past the rim
    t = math.radians(38.0)
    ax_ = Vector((math.cos(t), math.sin(t), 0.0))
    c = Vector((hx, hy, 0.0))
    p0, p1 = c - ax_ * 0.068, c + ax_ * 0.088
    # (the inner post stands on the lid, the outer one past the rim on the roof)
    zb0, zb1, zt = ROOF_Z + 0.016, ROOF_Z - 0.002, ROOF_Z + 0.038
    bm = new_bm()
    bend_bar(bm, [Vector((p0.x, p0.y, zb0)), Vector((p0.x, p0.y, zt)) + ax_ * 0.014,
                  Vector((p1.x, p1.y, zt)) - ax_ * 0.014, Vector((p1.x, p1.y, zb1))], 0.0055, GUN, seg=10)
    g.add(bm)
    bm = new_bm()
    for (x0, x1), (y0, y1), z1 in (((-0.155, -0.095), (0.183, 0.268), 0.137),
                                   ((-0.0725, -0.0275), (0.203, 0.263), 0.121)):
        box(bm, ((x0 + x1) / 2, (y0 + y1) / 2, (ROOF_Z - 0.004 + z1) / 2),
            (x1 - x0, y1 - y0, z1 - ROOF_Z + 0.004), PAINT)
    g.add(bm, bevel=(0.003, 2, 30))
    bm = new_bm()                     # the tall box's lid, 4 mm proud
    box(bm, (-0.125, 0.2255, 0.1385), (0.052, 0.075, 0.005), PAINT)
    g.add(bm, bevel=SMALL_BEVEL(0.0015))
    ax, ay, az = ANTENNA
    bm = new_bm()
    lathe(bm, [(0.088, 0.0), (0.088, 0.015), (0.140, 0.015), (0.146, 0.011), (0.152, 0.011),
               (0.156, 0.007), (0.156, 0.0)], GUN, seg=24, axis="Z", center=(ax, ay, 0))
    g.add(bm)
    bm = new_bm()
    bend_bar(bm, [Vector((ax, ay, 0.154)), Vector((ax, ay, az))], 0.0045, GUN, seg=8)
    g.add(bm)

    # --- the rear plate: a louvre, rivets along the top
    bm = new_bm()
    lean = Matrix.Rotation(math.atan(0.26), 3, "X")
    box(bm, on_rear(0.0, 0.0475, -0.001), (0.062, 0.006, 0.058), DARK, rot=lean)
    g.add(bm)
    bm = new_bm()
    for z in np.linspace(0.025, 0.070, 5):
        box(bm, on_rear(0.0, z, 0.0015), (0.058, 0.006, 0.0055), PAINT, rot=lean)
    g.add(bm, bevel=(0.0012, 2, 30))

    # --- spare links on the sides: two rows of four, lying on the plate
    bm = new_bm()
    for s in (-1, 1):
        rot = Matrix.Rotation(-s * math.atan(0.38), 3, "Y")
        up = Vector((-s * 0.38, 0.0, 1.0)).normalized()          # up the side plate
        for zc in (0.044, 0.083):
            for y in np.linspace(0.085, 0.198, 4):
                c, n = on_side(s, y, zc, 0.004)
                box(bm, c, (0.010, 0.032, 0.034), TRACK, rot=rot)
                for dz in (-0.0105, 0.0105):          # two pads on each
                    box(bm, c + n * 0.006 + up * dz, (0.006, 0.026, 0.009), TRACK, rot=rot)
    g.add(bm, bevel=(0.0015, 2, 30))

    # --- rivets on the plates
    bm = new_bm()
    for s in (-1, 1):
        for x, z in ((0.140, 0.095), (0.165, 0.062), (0.195, 0.030), (0.205, 0.000)):
            p = on_plate(s * x, z, 0.0004)
            rivets(bm, [tuple(p)], tuple(PLATE_N))
        for z in (0.005, 0.040, 0.075):
            p = on_plate(s * sum(RIB_X) / 2, z, 0.0124)
            rivets(bm, [tuple(p)], tuple(PLATE_N), r=RIV * 0.8)
        for y, z in ((-0.050, 0.005), (0.030, 0.005), (0.245, 0.005), (0.272, 0.060), (0.015, 0.095)):
            p, n = on_side(s, y, z, 0.0004)
            rivets(bm, [tuple(p)], tuple(n))
    for x in (-0.180, -0.120, -0.060, 0.0, 0.060, 0.195):
        rivets(bm, [tuple(on_rear(x, 0.100, 0.0004))], tuple(on_rear(0, 0, 1) - on_rear(0, 0, 0)))
    g.add(bm, subsurf=1)
    return g


# ---------------------------------------------------------------------- gun

def _bore_level(bm):
    """Everything below is built level along -Y from the trunnion; this turns
    it up to the rest elevation about the trunnion."""
    tilt(bm, -GUN_EL, TRUNNION[0], TRUNNION[1])


def mantlet(mats):
    """The collar round the tube in front of the mount -- what lays with the
    gun and does not recoil.  Its back runs 2.5 cm into the mount and the
    housing, so at either end of the ladder they still swallow it."""
    g = Group(mats)
    ty, tz = TRUNNION
    bm = new_bm()
    lathe(bm, [(0.004, 0.028), (0.004, 0.041), (-0.029, 0.041), (-0.034, 0.036), (-0.034, 0.028)],
          GUN, seg=64, axis="Y", center=(0.0, ty, tz))
    _bore_level(bm)
    g.add(bm, bevel=(0.002, 2, 30))
    return g


def barrel(mats):
    """Breech stub, sleeve, a ring, the tube, the neck, the muzzle brake with
    three holes a side, bored.  The stub ends exactly on the trunnion, which
    is where `barrel_recoil.trunnion()` puts the pivot (the breech end of the
    tube on the bore), so the pipeline lays the gun about the same axis the
    model was built for."""
    g = Group(mats)
    ty, tz = TRUNNION
    # the brake: two rings round a dark core, bars over and under it -- a
    # rectangular window a side where the original has three round holes
    # (asked for, 2026-09-27)
    prof = [(0.0, 0.0), (0.0, 0.026), (-0.020, 0.026), (-0.020, 0.0373), (-0.093, 0.0373),
            (-0.095, 0.0385), (-0.107, 0.0385), (-0.109, 0.0345), (-0.270, 0.0345),
            (-0.272, 0.0362), (-0.312, 0.0362), (-0.314, 0.0345), (-0.318, 0.0345),
            (-0.321, BRAKE_R), (-0.335, BRAKE_R), (-0.335, 0.0300), (-0.391, 0.0300),
            (-0.391, BRAKE_R), (-0.402, BRAKE_R), (-0.405, 0.0385), (-0.405, 0.0230),
            (-0.400, 0.0220), (-0.389, 0.0220), (-0.389, 0.0)]
    mi = [GUN] * 15 + [DARK] + [GUN] * 4 + [DARK] * 4
    bm = new_bm()
    lathe(bm, prof, mi, seg=64, axis="Y", center=(0.0, ty, tz))
    # (3 mm into both rings and the core: overlapping solids share no face)
    for c in (90.0, 270.0):
        arc_band(bm, -0.332, -0.394, 0.027, BRAKE_R, c - BRAKE_BAR, c + BRAKE_BAR, GUN, 24)
    _bore_level(bm)
    g.add(bm, bevel=(0.0015, 2, 30))
    return g


def arc_band(bm, a0, a1, r0, r1, t0, t1, mi, seg):
    """A thick arc about the bore (the lathe's Y axis through the trunnion):
    from a0 to a1 along it, radii r0..r1, angles t0..t1 deg in the x-z plane
    from +X."""
    ty, tz = TRUNNION
    ts = np.radians(np.linspace(t0, t1, max(2, segs(seg)) + 1))
    V = {(i, j): [bm.verts.new((r * math.cos(t), ty + a, tz + r * math.sin(t))) for t in ts]
         for i, a in enumerate((a0, a1)) for j, r in enumerate((r0, r1))}
    fs = []
    for k in range(len(ts) - 1):
        for j in (0, 1):                      # inner and outer skins
            fs.append(bm.faces.new((V[0, j][k], V[0, j][k + 1], V[1, j][k + 1], V[1, j][k])))
        for i in (0, 1):                      # the two ends
            fs.append(bm.faces.new((V[i, 0][k], V[i, 0][k + 1], V[i, 1][k + 1], V[i, 1][k])))
    for k in (0, len(ts) - 1):                # the two sides
        fs.append(bm.faces.new((V[0, 0][k], V[0, 1][k], V[1, 1][k], V[1, 0][k])))
    for f in fs:
        f.material_index = mi
    bmesh.ops.recalc_face_normals(bm, faces=fs)
