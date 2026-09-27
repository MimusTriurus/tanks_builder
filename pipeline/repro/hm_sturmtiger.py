"""HM_SturmTiger, rebuilt from scratch on repro_kit (docs/repro.md).

The fourth copy and the first casemate, 2026-09-27.  It stands 1.0 to the +X
of the original in the same scene, in collection `HM_SturmTiger.Repro`, every
name suffixed `.Repro`.  No turret: the welded casemate is `Casemate.Geometry`
under `Hull.World`, and so are the mount and the gun (docs/tank-scene.md,
"Машина без башни") -- the module has `casemate` where a turret has `turret`,
and no ring.

Every number is in HM_SturmTiger's own frame, measured off its vertices:

- front -Y; ground z -0.3694 -- the lowest vertex of the *rebuilt* belts
  (`*.Caterpillar.Rebuilt`, which the HMP sprites were rendered from);
- the left belt (`L.*`) on -X; belt centres x -0.2592 / +0.2633 (the
  original is not quite symmetric) -> 0.2612, 0.14 wide: a stadium of two
  r 0.104 arcs at y -0.4123 / +0.4206;
- the belts run inside the hull's box, under a fender shelf (z -0.078) with a
  panelled band round its edge and five skirt plates a side below it (x
  0.353), so a side view shows only the bottom run and the arcs.  The road
  wheels (Tiger-like, interleaved, a toothed sprocket at the *front*) are
  hidden behind the skirts but for a strip of their bottoms;
- no ring: the casemate is a box with every face sloped, the mortar comes
  out of its front plate through a square flange; the pipeline turns the
  carousel about `parts_render.hull_axis()` and never runs `turret_axis`;
- the gun: a stubby stepped mortar at rest 26.5 deg *up* (the original's
  bore: muzzle (0.003, -0.458, 0.187), heading 270.6, elevation 26.46).  The
  flange and its inner plate are the casemate's; the short tapered boot on
  the inner plate lays with the gun (`mantlet`); the jacket, neck and the
  three-drum tube recoil.  The trunnion is on the bore where it crosses the
  front plate;
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
    PAINT, GUN, TRACK, GLASS, DARK, PAINTDK, RIVET, RIVET_G, RIV,
    Group, new_bm, box, prism, lathe, rounded_rect_profile, cyl, rivets,
    bend_bar, hull_solid, bent_plate, rr_loop, loft, belt_path, place_belt, tilt, game, segs,
)

NAME = "HM_SturmTiger"
SFX = ".Repro"
X_OFF = 1.0
PREFIX = "HMR"
GROUND = -0.3694
TRACK_X = 0.2612
# the original's belts that the sprites show: the kit compares against these
ORIGINAL = {"l_cat": "L.Caterpillar.Rebuilt", "r_cat": "R.Caterpillar.Rebuilt"}

# the army's paint, not the original's (repro_kit.ARMY_PALETTE)
PALETTE = ARMY_PALETTE

# ------------------------------------------------------------------- layout

BELLY_Z = -0.334
# between the belts: 2 mm inside their inner edge (TRACK_X - BELT_W/2 =
# 0.1912) -- the nose and the tail stand in front of the arcs
CORE_X = 0.189
# the hull between the belts, side profile: the lower nose, the vertical
# front face, the glacis curving up to the casemate's front plate, a deck
# hidden under the upper hull, the rear plate and its chamfer
CORE = [(-0.4265, BELLY_Z), (0.401, BELLY_Z), (0.430, -0.320), (0.4733, -0.292),
        (0.4733, -0.046), (0.300, -0.034), (-0.270, -0.034), (-0.270, -0.020), (-0.2836, -0.020),
        (-0.300, -0.028), (-0.3765, -0.060), (-0.497, -0.190), (-0.497, -0.268),
        (-0.472, -0.276), (-0.468, -0.290)]
# the upper hull over the belts' inner halves, under the casemate: wider in
# front of y 0.06; its top rises from -0.018 to the engine deck (+0.006)
UPPER_X = (0.280, 0.294)
UPPER = [(-0.270, -0.085), (0.4725, -0.085), (0.4725, 0.000), (0.4665, 0.006), (0.160, 0.006),
         (0.040, -0.018), (-0.270, -0.018)]
UPPER_W = [(-0.270, -0.085), (0.055, -0.085), (0.066, -0.074), (0.066, -0.021), (-0.270, -0.021)]
# the fender over the belts: one plate 12 mm thick, bent down over both arcs
# (a shelf and two separate end plates met end to end in slivers, which baked
# a neighbour's grey); the panelled band is its outer edge, the skirt plates
# hang under it (inner face 8 mm outside the belt's outer edge), 7 mm proud
# of the band -- a ledge that narrow was all bevel
FENDER = [(-0.490, -0.195), (-0.490, -0.160), (-0.425, -0.078), (0.466, -0.078),
          (0.4965, -0.108), (0.4965, -0.185)]
FENDER_X = (0.190, 0.338)
BAND = dict(x=(0.3375, 0.346), y=(-0.412, 0.470), z=(-0.127, -0.0785))
# its ends run down over the skirt's cut corners to the belt's arcs
BAND_NOSE = [(-0.4135, -0.0785), (-0.440, -0.0785), (-0.494, -0.160), (-0.494, -0.192),
             (-0.470, -0.192), (-0.470, -0.177), (-0.420, -0.127), (-0.4135, -0.127)]
BAND_TAIL = [(0.4015, -0.0785), (0.4685, -0.0785), (0.4935, -0.102), (0.4935, -0.127),
             (0.4015, -0.127)]
BAND_SEAMS = (-0.070, 0.025, 0.195, 0.290, 0.400)
SKIRT_X = (0.339, 0.3532)
SKIRT_Z = (BELLY_Z, -0.125)
SKIRT_SEAMS = (-0.472, -0.260, -0.080, 0.045, 0.235, 0.472)

# on the shelf: the lamp's housing at the front corner, a box with a round lid
# behind it, a box along the casemate, a small one at the rear
LAMP_BOX = dict(x=(0.192, 0.300), y=(-0.452, -0.382), z=(-0.118, -0.028))
LAMP = dict(x=0.246, y=-0.452, z=-0.062, r=0.019)
LID_BOX = dict(x=(0.192, 0.308), y=(-0.374, -0.276), z=(-0.085, -0.014))
LID = (0.2525, -0.323, 0.040)
SIDE_BOX = dict(x=(0.290, 0.318), y=(-0.262, -0.152), z=(-0.080, -0.030))
REAR_BOX = dict(x=(0.276, 0.310), y=(0.160, 0.200), z=(-0.080, -0.052))
SHACKLE_X = 0.130
REAR_SHACKLE_X = 0.125
GRILLE = dict(x=(0.052, 0.240), y=(0.300, 0.405))

# running gear: a stadium belt, the toothed sprocket on its front arc, the
# idler on the rear one, three big road wheels, two upper ones interleaved
BELT_C = ((-0.4123, -0.2654), (0.4206, -0.2654))
BELT_T = 0.018
BELT_ROUT = 0.104
BELT_RIN = BELT_ROUT - BELT_T
BELT_W = 0.140
PITCH = 0.027
SPROCKET = (BELT_C[0][0], BELT_C[0][1], 0.076)       # teeth reach the belt's inner surface
IDLER = (BELT_C[1][0], BELT_C[1][1], 0.072)
_ROAD_Z = GROUND + BELT_T + 0.068 + 0.001
# (the original's wheels are ellipses, squashed in z by their root's scale;
# the copy's turn, so they are round, on the same axles, touching the belt)
ROAD = [(-0.175, _ROAD_Z, 0.068), (-0.005, _ROAD_Z, 0.068), (0.170, _ROAD_Z, 0.068)]
_UP_Z = BELT_C[0][1] + BELT_RIN - 0.050 - 0.001
UPPER_WHEELS = [(-0.090, _UP_Z, 0.050), (0.080, _UP_Z, 0.050)]
TAIL = (0.305, GROUND + BELT_T + 0.038 + 0.001, 0.038)

# the game variant: where the sprung mass rocks (mid-belt, level with the
# belt tops), where the exhaust leaves (the two grilles), where the blast is
# (the fighting compartment, under the casemate's roof hatch)
BODY_PIVOT = ((BELT_C[0][0] + BELT_C[1][0]) / 2, BELT_C[0][1] + BELT_ROUT)
EXHAUST = ((-0.146, 0.352, 0.012), (0.146, 0.352, 0.012))
BLAST = (0.0, 0.0, 0.020)
GAME_TAG = "HMR"                        # Models/HMR/

# casemate: every face a plane -- front y = -0.266 + 0.643 z (57 deg), sides
# x = 0.2977 - 0.395 z (68 deg, straight to a small round at the roof), rear
# y = 0.2455 - 0.142 z -- from its foot
# (z -0.034, over the upper hull's sides; from y 0.04 it rises onto the
# engine deck -- the long slant a side view shows) to the roof at 0.1873
CM_Z0 = -0.034
CM_RISE_Y = 0.040
ROOF_Z = 0.1873


def front_y(z):
    return -0.266 + 0.643 * z


def side_x(z):
    return 0.2977 - 0.395 * z


def rear_y(z):
    return 0.2455 - 0.142 * z


# the front plate as a frame: V up the slope, n out of it
PLATE_V = Vector((0.0, 0.643, 1.0)).normalized()
PLATE_N = Vector((0.0, -1.0, 0.643)).normalized()
# the gun: the bore at rest 26.46 deg up, the trunnion where it crosses the
# front plate (the mount's flange stands 2 cm proud of it, its inner plate
# another 1.4 cm, so the pivot is well inside what does not move)
GUN_EL = 26.46
TRUNNION = (-0.2203, 0.0693)
BORE = Vector((0.0, -math.cos(math.radians(GUN_EL)), math.sin(math.radians(GUN_EL))))
MOUNT_O = Vector((0.0, -0.2211, 0.0697))           # the bore on the plate
# (the inner plate centred on the bore and wider than the boot by more than
# the boot swings at the ends of the ladder)
FLANGE = dict(hw=0.122, hh=0.112, r=0.022, v=0.008, d=(-0.010, 0.020))
INNER = dict(hw=0.097, hh=0.097, r=0.018, v=0.0, d=(0.012, 0.034))
# the lay sheet's cameras: back far enough for the whole mortar
LAY_DIST, LAY_AIM = 1.7, (-0.110, 0.055)
HATCH = (0.057, -0.015, 0.086)                     # the round roof hatch with its cross
ANTENNA = (0.190, 0.175, 0.345)


# ------------------------------------------------------------------- tracks

def link_mesh(pitch):
    """One link (u along the belt, v across, w out): a plate the full width,
    three pads across it -- a wide one in the middle, two narrow outboard."""
    bm = new_bm()
    box(bm, (0, 0, 0.0045), (pitch * 0.92, BELT_W, 0.009), TRACK)
    box(bm, (0, 0, 0.0135), (pitch * 0.80, 0.050, 0.009), TRACK)
    for v in (-0.047, 0.047):
        box(bm, (0, v, 0.0135), (pitch * 0.80, 0.036, 0.009), TRACK)
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


def wheel_spec():
    """Every wheel that turns: name, axle (y, z), and the radius the belt turns
    it at (the engine spins each one distance / r)."""
    out = [("Sprocket", (SPROCKET[0], SPROCKET[1], BELT_RIN))]
    out += [("Road.%d" % i, w) for i, w in enumerate(ROAD)]
    out += [("Upper.%d" % i, w) for i, w in enumerate(UPPER_WHEELS)]
    out += [("Tail", TAIL), ("Idler", IDLER)]
    return [{"name": n, "axle": (y, z), "r": r} for n, (y, z, r) in out]


def wheels(mats, xc, s):
    """wheel_spec, each with its own Group -- one object per wheel in the game
    variant, so each can turn about its axle.  The upper row stands inboard of
    the road wheels (they interleave)."""
    out = []
    for w in wheel_spec():
        (y, z), r = w["axle"], w["r"]
        g = Group(mats)
        bm = new_bm()
        seg = 64 if w["name"] in ("Idler", "Sprocket") else 48
        if game():
            seg = int(seg * 0.8)
        if w["name"] == "Sprocket":
            disc(bm, y, z, SPROCKET[2] - 0.004, xc, 0.070, seg=seg)
        elif w["name"] == "Idler":
            disc(bm, y, z, IDLER[2], xc, 0.070, seg=seg)
        elif w["name"].startswith("Upper"):
            disc(bm, y, z, r, xc - s * 0.034, 0.040, seg=seg)
        elif w["name"] == "Tail":
            disc(bm, y, z, r, xc, 0.050, seg=seg)
        else:
            disc(bm, y, z, r, xc + s * 0.022, 0.056, seg=seg)
        g.add(bm, subsurf=1)
        if w["name"] == "Sprocket":
            bm = new_bm()          # teeth, two rings, their tips on the belt's inner surface
            for k in range(16):
                t = math.tau * k / 16
                rot = Matrix.Rotation(t, 3, "X")
                for dx in (-0.024, 0.024):
                    c = Vector((xc + dx, y, z)) + rot @ Vector((0, 0, BELT_RIN - 0.010))
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
    xin = xc - s * 0.064           # inboard of the upper wheels
    zb = -0.255
    box(bm, (xin, 0.0, zb), (0.012, 0.62, 0.018), TRACK)
    for (y, z, r) in ROAD:
        a, b_ = Vector((xin, y + 0.06, zb)), Vector((xin, y, z))
        d = b_ - a
        rot = Vector((0, 1, 0)).rotation_difference(d.normalized()).to_matrix()
        box(bm, (a + b_) / 2, (0.012, d.length + 0.016, 0.016), TRACK, rot=rot)
    for (y, z, r) in [SPROCKET, IDLER, TAIL] + ROAD + UPPER_WHEELS:
        cyl(bm, (xin, y, z), (xc, y, z), 0.011, TRACK, seg=12)
    g.add(bm, bevel=(0.003, 2, 40), subsurf=1)
    return g


def debris():
    """What flies off when the tank blows up, for the game variant: a name,
    the node it comes off, and a box in this frame.  Every piece of that
    node's mesh whose centre lies in the box goes with it.  Sides are the
    tank's own: L is +X.  The casemate is the hull's (welded), so its hatch
    comes off `Hull` too."""
    out = []
    for s, S in ((1, "L"), (-1, "R")):
        def sx(a, b):
            return tuple(sorted((s * a, s * b)))
        for i, (a, b) in enumerate(zip(SKIRT_SEAMS, SKIRT_SEAMS[1:])):
            out.append({"name": "Skirt.%s.%d" % (S, i), "parent": "Hull",
                        "box": (sx(0.336, 0.362), (a + 0.001, b - 0.001), (-0.34, -0.12))})
        out.append({"name": "Lamp.%s" % S, "parent": "Hull",
                    "box": (sx(0.185, 0.305), (-0.475, -0.378), (-0.12, -0.012))})
        out.append({"name": "Box.%s" % S, "parent": "Hull",
                    "box": (sx(0.185, 0.315), (-0.378, -0.270), (-0.09, 0.0))})
    hx, hy, hr = HATCH
    out.append({"name": "Hatch", "parent": "Hull",
                "box": ((hx - hr - 0.01, hx + hr + 0.01), (hy - hr - 0.01, hy + hr + 0.01),
                        (ROOF_Z + 0.001, 0.26))})
    return out


# --------------------------------------------------------------------- hull

def SMALL_BEVEL(w):
    """A small part's chamfer: two segments on the copy, none on the game
    variant -- the kit makes it one segment there, and on the band's panels
    and the roof boxes that single 2-3 mm face baked a neighbour's grey (the
    engine's outline draws the edge anyway)."""
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


def hull(mats):
    g = Group(mats)

    # --- between the belts: the nose, the glacis, the rear
    bm = new_bm()
    prism(bm, CORE, -CORE_X, CORE_X, PAINT)
    g.add(bm, bevel=(0.008, 2, 30))
    # the plate on the glacis, 12 mm proud, 3 mm into it
    gl0, gl1 = Vector((-0.497, -0.190)), Vector((-0.3765, -0.060))
    up = (gl1 - gl0).normalized()
    nn = Vector((-up.y, up.x))                  # out of the glacis (forward, up)
    pts = []
    for z in (-0.145, -0.068):
        q = gl0 + up * ((z - gl0.y) / up.y)
        for d in (-0.003, 0.012):
            p = q + nn * d
            for x in (-0.086, 0.086):
                pts.append((x, p.x, p.y))
    bm = solids([pts], PAINT)
    g.add(bm, bevel=(0.003, 2, 30))
    bm = new_bm()
    for z in (-0.137, -0.076):
        q = gl0 + up * ((z - gl0.y) / up.y) + nn * 0.0124
        rivets(bm, [(x, q.x, q.y) for x in (-0.074, 0.0, 0.074)], (0.0, nn.x, nn.y), r=RIV * 0.8)
    g.add(bm, subsurf=1)

    # --- the upper hull between the fenders, under the casemate
    bm = new_bm()
    prism(bm, UPPER, -UPPER_X[0], UPPER_X[0], PAINT)
    g.add(bm, bevel=(0.004, 2, 30))
    bm = new_bm()
    prism(bm, UPPER_W, -UPPER_X[1], UPPER_X[1], PAINT)
    g.add(bm, bevel=(0.004, 2, 30))

    # --- fenders: one bent plate over the belt, the band round its edge
    bm = new_bm()
    for s in (-1, 1):
        x0, x1 = sorted((s * FENDER_X[0], s * FENDER_X[1]))
        bent_plate(bm, FENDER, 0.012, x0, x1, PAINT)
    g.add(bm, bevel=(0.004, 2, 30))
    bm = new_bm()
    B = BAND
    edges = [B["y"][0]] + list(BAND_SEAMS)
    for s in (-1, 1):
        for a, b in zip(edges, edges[1:]):
            sbox(bm, s, B["x"], (a + 0.0015, b - 0.0015), B["z"], PAINT)
        x0, x1 = sorted((s * B["x"][0], s * B["x"][1]))
        prism(bm, BAND_NOSE, x0, x1, PAINT)
        prism(bm, BAND_TAIL, x0, x1, PAINT)
    g.add(bm, bevel=SMALL_BEVEL(0.002))

    # --- the skirt: five plates a side, the front one's top corner cut back
    # along the fender's slope, the rear one's bottom corner cut
    bm = new_bm()
    z0, z1 = SKIRT_Z
    for s in (-1, 1):
        x0, x1 = sorted((s * SKIRT_X[0], s * SKIRT_X[1]))
        for i, (a, b) in enumerate(zip(SKIRT_SEAMS, SKIRT_SEAMS[1:])):
            a, b = a + 0.0015, b - 0.0015
            poly = [(a, z1), (a, z0), (b, z0), (b, z1)]
            if i == 0:
                # the outer face's end, the inner face's end
                out_ = [(-0.422, z1), (a, -0.175), (a, -0.312), (-0.450, z0), (b, z0), (b, z1)]
                in_ = [(-0.452, z1), (-0.491, -0.170), (-0.491, -0.305), (-0.462, z0), (b, z0), (b, z1)]
            elif i == len(SKIRT_SEAMS) - 2:
                out_ = [(a, z1), (a, z0), (0.402, z0), (b, -0.286), (b, z1 - 0.005), (b - 0.005, z1)]
                in_ = [(a, z1), (a, z0), (0.412, z0), (0.4905, -0.280), (0.4905, z1 - 0.005),
                       (0.4855, z1)]
            else:
                prism(bm, poly, x0, x1, PAINT)
                continue
            xo, xi = s * SKIRT_X[1], s * SKIRT_X[0]
            sub = new_bm()
            hull_solid(sub, [(xo, y, z) for y, z in out_] + [(xi, y, z) for y, z in in_], PAINT)
            me = bpy_mesh(sub)
            bm.from_mesh(me)
            free_mesh(me)
    g.add(bm, bevel=(0.0018, 2, 30))
    # vents low on the front and the rear plates: four dark slots each
    bm = new_bm()
    for s in (-1, 1):
        for y0, y1 in ((-0.445, -0.395), (0.330, 0.378)):
            for z in np.linspace(-0.309, -0.288, 4):
                sbox(bm, s, (SKIRT_X[1] - 0.001, SKIRT_X[1] + 0.0012), (y0, y1),
                     (z - 0.0022, z + 0.0022), DARK)
    g.add(bm)

    # --- on the shelf: the lamp's housing, the lidded box, the side boxes
    bm = new_bm()
    for s in (-1, 1):
        for Bx in (LAMP_BOX, LID_BOX):
            sbox(bm, s, Bx["x"], Bx["y"], Bx["z"], PAINT)
    g.add(bm, bevel=(0.006, 2, 30))
    bm = new_bm()
    for s in (-1, 1):
        for Bx in (SIDE_BOX, REAR_BOX):
            sbox(bm, s, Bx["x"], Bx["y"], Bx["z"], PAINT)
    g.add(bm, bevel=(0.004, 2, 30))
    bm = new_bm()
    lx, ly, lr = LID
    for s in (-1, 1):        # (a capped cylinder: a lathe's pole fan on top bakes slivers)
        cyl(bm, (s * lx, ly, LID_BOX["z"][1] - 0.004), (s * lx, ly, LID_BOX["z"][1] + 0.006), lr,
            PAINT, seg=64)
    g.add(bm, bevel=(0.002, 2, 30))
    bm = new_bm()
    bmg = new_bm()
    L = LAMP
    for s in (-1, 1):
        c = (s * L["x"], L["y"], L["z"])
        # a rim round the lens, open inside it (a tube to the axis bakes the
        # glass' grey: docs/repro.md, "Щепки запекаются чужим цветом")
        lathe(bm, rounded_rect_profile(-0.010, 0.004, 0.015, L["r"] + 0.006, 0.003, n=2), PAINT,
              seg=48, axis="Y", center=c)
        lathe(bmg, [(-0.012, 0.0), (-0.0115, L["r"] * 0.55), (-0.009, L["r"] * 0.9),
                    (-0.006, L["r"])], GLASS, seg=48, axis="Y", center=c, closed=False)
    g.add(bm, subsurf=1)
    g.add(bmg, subsurf=1)

    # --- tow shackles on the nose: a lug on the glacis' foot, a D on its pin
    # (the D's top bar runs through the knob; its plane clears the face)
    bm = new_bm()
    for s in (-1, 1):
        cyl(bm, (s * SHACKLE_X, -0.468, -0.163), (s * SHACKLE_X, -0.512, -0.163), 0.017, PAINT, seg=24)
    g.add(bm, bevel=(0.005, 2, 30))
    bm = new_bm()
    for s in (-1, 1):
        x = s * SHACKLE_X
        u = [Vector((x - 0.026, -0.507, -0.172))]
        for k in range(9):
            t = math.pi * k / 8
            u.append(Vector((x - 0.026 * math.cos(t), -0.507, -0.222 - 0.022 * math.sin(t))))
        u.append(Vector((x + 0.026, -0.507, -0.172)))
        u.append(u[0])
        bend_bar(bm, u, 0.0065, GUN, seg=10)
    g.add(bm)

    # --- the rear: a bar along the plate's foot, a lip under the deck, two
    # ribs, the shackles on brackets
    bm = new_bm()
    box(bm, (0, 0.4815, -0.2725), (2 * CORE_X - 0.004, 0.029, 0.055), PAINT)
    # (its ends 2.5 mm past the upper hull's sides: never in their plane)
    box(bm, (0, 0.4735, -0.040), (2 * UPPER_X[0] + 0.005, 0.015, 0.012), PAINT)
    g.add(bm, bevel=(0.003, 2, 30))
    bm = solids([[(s * 0.045 + dx, y, z) for dx in (-0.006, 0.006) for y in (0.470, 0.4785)
                  for z in (-0.212, -0.098)]
                 + [(s * 0.045, 0.470, -0.222), (s * 0.045, 0.470, -0.088),
                    (s * 0.045, 0.476, -0.220), (s * 0.045, 0.476, -0.090)] for s in (-1, 1)], PAINT)
    g.add(bm, bevel=(0.002, 2, 30))
    bm = new_bm()
    for s in (-1, 1):
        box(bm, (s * REAR_SHACKLE_X, 0.484, -0.228), (0.034, 0.024, 0.024), GUN)
    g.add(bm, bevel=(0.003, 2, 30))
    bm = new_bm()
    for s in (-1, 1):
        x = s * REAR_SHACKLE_X
        u = [Vector((x - 0.017, 0.504, -0.236))]
        for k in range(9):
            t = math.pi * k / 8
            u.append(Vector((x - 0.017 * math.cos(t), 0.504, -0.284 - 0.017 * math.sin(t))))
        u.append(Vector((x + 0.017, 0.504, -0.236)))
        bend_bar(bm, u, 0.0065, GUN, seg=10)
    g.add(bm)

    # --- rivets: the skirt plates' corners and ends, the band, the boxes
    bm = new_bm()
    for s in (-1, 1):
        xf = s * (SKIRT_X[1] + 0.0004)
        for i, (a, b) in enumerate(zip(SKIRT_SEAMS, SKIRT_SEAMS[1:])):
            ya, yb = a + 0.020, b - 0.020
            if i == 0:
                ya = -0.430
            pts = [(xf, y, z) for y in (ya, yb) for z in (-0.148, -0.308)]
            if i == 0:
                pts = [p for p in pts if not (p[1] < -0.42 and p[2] > -0.2)]
                pts += [(xf, -0.455, -0.205), (xf, -0.418, -0.148)]
            if i == len(SKIRT_SEAMS) - 2:
                pts += [(xf, 0.452, -0.205)]
            rivets(bm, pts, (s, 0, 0))
        xb = s * (BAND["x"][1] + 0.0004)
        rivets(bm, [(xb, y, -0.106) for y in (-0.385, -0.090, 0.005, 0.175, 0.310, 0.450)],
               (s, 0, 0), r=RIV * 0.8)
        rivets(bm, [(s * x, LAMP_BOX["y"][0] - 0.0004, -0.030) for x in (0.205, 0.288)],
               (0, -1, 0), r=RIV * 0.7)
    for x in np.linspace(-0.15, 0.15, 5):
        rivets(bm, [(x, 0.4739, -0.075)], (0, 1, 0), r=RIV * 0.8)
    for x in (-0.07, 0.07):
        rivets(bm, [(x, -0.4974, -0.215)], (0, -1, 0), r=RIV * 0.8)
    g.add(bm, subsurf=1)
    return g


def engine(mats):
    """The two grilles on the rear deck -- frames, dark beds, round slats
    across -- and the raised plate between them: the exhaust source."""
    g = Group(mats)
    G = GRILLE
    bm = new_bm()
    bmd = new_bm()
    bms = new_bm()
    top = 0.013
    for s in (-1, 1):
        x0, x1 = sorted((s * G["x"][0], s * G["x"][1]))
        y0, y1 = G["y"]
        cx = 0.5 * (x0 + x1)
        for yy in (y0 + 0.006, y1 - 0.006):
            box(bm, (cx, yy, 0.0035), (x1 - x0, 0.012, 2 * (top - 0.0035)), PAINT)
        for xx in (x0 + 0.006, x1 - 0.006):
            box(bm, (xx, 0.5 * (y0 + y1), 0.0035), (0.012, y1 - y0 - 0.020, 2 * (top - 0.0035)), PAINT)
        box(bmd, (cx, 0.5 * (y0 + y1), 0.003), (x1 - x0 - 0.020, y1 - y0 - 0.020, 0.008), DARK)
        for yy in np.linspace(y0 + 0.020, y1 - 0.020, 6):
            cyl(bms, (x0 + 0.011, yy, 0.0085), (x1 - 0.011, yy, 0.0085), 0.0055, PAINTDK, seg=12)
    g.add(bm, bevel=(0.002, 2, 30))
    g.add(bmd)
    g.add(bms)
    bm = new_bm()
    box(bm, (0, 0.360, 0.0065), (0.066, 0.120, 0.013), PAINT)
    g.add(bm, bevel=(0.003, 2, 30))
    return g


# ----------------------------------------------------------------- casemate

def flat_loft(bm, rings, mi):
    """`loft` with its top one flat face, not a fan from the centre: a boxy
    plan's fan is slivers wherever two ring points sit close (a corner), and
    slivers bake streaks and a neighbour's grey (ht_v1.py)."""
    loft(bm, rings, mi, cap_top=False)
    res = bmesh.ops.holes_fill(bm, edges=[e for e in bm.edges if e.is_boundary], sides=0)
    for f in res["faces"]:
        f.material_index = mi
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))


def on_plate(u, v, d):
    """A point on the front plate's mount frame: u across (X), v up the slope
    from where the bore crosses it, d out of it."""
    return MOUNT_O + Vector((u, 0.0, 0.0)) + PLATE_V * v + PLATE_N * d


def on_rear(x, z, d):
    """A point d out of the rear plate at (x, z)."""
    n = Vector((0.0, 1.0, 0.142)).normalized()
    return Vector((x, rear_y(z), z)) + n * d


def casemate(mats):
    g = Group(mats)

    # the body: one convex solid, its foot in the upper hull; the rear of the
    # foot rises onto the engine deck
    pts = []
    for s in (-1, 1):
        pts += [(s * side_x(CM_Z0), front_y(CM_Z0), CM_Z0), (s * side_x(CM_Z0), CM_RISE_Y, CM_Z0),
                (s * side_x(-0.004), rear_y(-0.004), -0.004),
                (s * side_x(ROOF_Z), front_y(ROOF_Z), ROOF_Z), (s * side_x(ROOF_Z), rear_y(ROOF_Z), ROOF_Z)]
    bm = solids([pts], PAINT)
    g.add(bm, bevel=(0.008, 2, 30))

    # the mortar's mount on the front plate: a flange and a raised inner plate
    # with a bolt at each corner (the gun's, dark metal) -- fixed, the boot on
    # it lays with the gun
    for P, bev in ((FLANGE, 0.004), (INNER, 0.006)):
        bm = new_bm()
        ring = rr_loop(0.0, P["v"], P["hw"], P["hh"], P["r"], n=4)
        flat_loft(bm, [[on_plate(u, v, d) for u, v in ring] for d in P["d"]], GUN)
        g.add(bm, bevel=(bev, 2, 30))
    bm = new_bm()
    for u in (-0.076, 0.076):
        for v in (INNER["v"] - 0.068, INNER["v"] + 0.068):
            cyl(bm, on_plate(u, v, INNER["d"][1] - 0.002), on_plate(u, v, INNER["d"][1] + 0.006),
                0.0085, RIVET_G, seg=6)
    g.add(bm, bevel=(0.0012, 1, 30))

    # --- the roof: the round hatch with a cross over it, boxes and scopes,
    # a plate along the rear edge, the antenna
    hx, hy, hr = HATCH
    bm = new_bm()             # (a ring and a capped disc: no lathe pole's fan in the open)
    lathe(bm, [(ROOF_Z - 0.004, hr - 0.016), (ROOF_Z - 0.004, hr), (0.200, hr), (0.204, hr - 0.004),
               (0.204, hr - 0.014), (0.199, hr - 0.016)], GUN, seg=96, axis="Z", center=(hx, hy, 0))
    cyl(bm, (hx, hy, ROOF_Z - 0.004), (hx, hy, 0.1985), hr - 0.017, GUN, seg=96)
    g.add(bm, bevel=(0.0015, 2, 30))
    if game():
        # the game variant blows the hatch off: what shows then is the dark
        # opening, not painted roof -- inside the hatch's ring until then
        bm = new_bm()
        lathe(bm, [(ROOF_Z + 0.0006, 0.0), (ROOF_Z + 0.0006, hr - 0.003)], DARK, seg=48,
              axis="Z", center=(hx, hy, 0), closed=False)
        g.add(bm)
    # a cross pattee: four arms widening to the rim, and a handle across it
    # rising to the middle (the original's is its highest point, 0.239)
    arms = []
    R = hr - 0.019
    for k, ang in enumerate((0.0, 90.0, 180.0, 270.0)):
        t = math.radians(ang)
        c, sn = math.cos(t), math.sin(t)
        z0 = 0.197 - 0.0004 * k            # (no two bottoms in one plane)
        arms.append([(hx + c * r - sn * w, hy + sn * r + c * w, z)
                     for r, hw in ((0.010, 0.009), (R, 0.026)) for w in (-hw, hw)
                     for z in (z0, 0.209)])
    bm = solids(arms, GUN)
    g.add(bm, bevel=(0.0015, 2, 30))
    bm = new_bm()
    # (the ridge's middle has no bottom point: hull_solid chokes on a point
    # between two others)
    bm = solids([[(hx + w, hy + r, z) for w in (-0.008, 0.008)
                  for r, z in ((-R, 0.205), (-R, 0.214), (0.0, 0.226), (R, 0.205), (R, 0.214))]],
                GUN)
    g.add(bm, bevel=(0.0015, 2, 30))
    bm = new_bm()
    for (x0, x1), (y0, y1), z1 in (((-0.190, -0.085), (0.025, 0.105), 0.218),
                                   ((-0.145, -0.020), (-0.140, -0.085), 0.228),
                                   ((-0.205, -0.150), (-0.050, -0.010), 0.2226),
                                   ((-0.215, -0.060), (0.135, 0.180), 0.197)):
        box(bm, ((x0 + x1) / 2, (y0 + y1) / 2, (ROOF_Z - 0.004 + z1) / 2),
            (x1 - x0, y1 - y0, z1 - ROOF_Z + 0.004), PAINT)
    g.add(bm, bevel=SMALL_BEVEL(0.003))
    bm = new_bm()                    # two scopes, painted all over, their tops rounded
    for x in (-0.185, -0.160):
        box(bm, (x, -0.110, 0.199), (0.018, 0.046, 0.028), PAINT)
    g.add(bm, bevel=(0.004, 2, 30))
    # the boxes' lids: plates standing 4 mm proud, painted like the box (the
    # original has no glass or black on the roof)
    bm = new_bm()
    box(bm, (-0.1375, 0.065, 0.2195), (0.084, 0.058, 0.005), PAINT)
    box(bm, (-0.1775, -0.030, 0.2241), (0.043, 0.028, 0.005), PAINT)
    g.add(bm, bevel=SMALL_BEVEL(0.0015))
    bm = new_bm()                    # a vent cap at the front right, two bolts on the rear plate
    cyl(bm, (0.165, -0.115, ROOF_Z - 0.003), (0.165, -0.115, 0.1955), 0.015, PAINT, seg=32)
    g.add(bm, bevel=(0.003, 2, 30))
    bm = new_bm()
    rivets(bm, [(x, 0.158, 0.1974) for x in (-0.185, -0.115, -0.080)], (0, 0, 1), r=RIV)
    g.add(bm, subsurf=1)
    ax, ay, az = ANTENNA
    bm = new_bm()
    lathe(bm, [(ROOF_Z - 0.004, 0.0), (ROOF_Z - 0.004, 0.012), (ROOF_Z + 0.004, 0.012),
               (ROOF_Z + 0.007, 0.008), (0.222, 0.007), (0.226, 0.005), (0.226, 0.0)], GUN,
          seg=24, axis="Z", center=(ax, ay, 0))
    g.add(bm)
    bm = new_bm()
    bend_bar(bm, [Vector((ax, ay, 0.224)), Vector((ax, ay, az))], 0.0022, GUN, seg=8)
    g.add(bm)

    # --- the rear plate: a door, a grille, a small box, brackets at the foot
    lean = Matrix.Rotation(math.atan(0.142), 3, "X")
    bm = new_bm()
    for (x0, x1), (z0, z1), d in (((-0.190, -0.035), (0.010, 0.165), 0.010),
                                  ((0.120, 0.145), (0.050, 0.095), 0.011),
                                  ((0.205, 0.255), (0.008, 0.030), 0.018),
                                  ((-0.255, -0.205), (0.008, 0.030), 0.018)):
        c = on_rear((x0 + x1) / 2, (z0 + z1) / 2, d / 2 - 0.003)
        box(bm, c, (x1 - x0, d + 0.006, (z1 - z0) / math.cos(math.atan(0.142))), PAINT, rot=lean)
    g.add(bm, bevel=(0.003, 2, 30))
    bm = new_bm()
    c = on_rear(0.024, 0.045, -0.0015)
    box(bm, c, (0.088, 0.006, 0.078), DARK, rot=lean)
    g.add(bm)
    bm = new_bm()
    for z in np.linspace(0.014, 0.076, 5):
        c = on_rear(0.024, z, 0.0012)
        box(bm, c, (0.084, 0.006, 0.006), PAINT, rot=lean)
    g.add(bm, bevel=(0.0012, 2, 30))
    bm = new_bm()                    # the door's handle
    p0, p1 = on_rear(-0.060, 0.070, 0.010), on_rear(-0.060, 0.110, 0.010)
    q0, q1 = on_rear(-0.060, 0.070, 0.019), on_rear(-0.060, 0.110, 0.019)
    bend_bar(bm, [p0, q0, q1, p1], 0.003, GUN, seg=8)
    g.add(bm)

    # --- clamps along the sides' top edge
    bm = new_bm()
    for s in (-1, 1):
        n = Vector((s * 1.0, 0.0, 0.395)).normalized()
        rot = Matrix.Rotation(-s * math.atan(0.395), 3, "Y")
        for y in (-0.045, 0.025, 0.075):
            c = Vector((s * side_x(0.160), y, 0.160)) + n * 0.002
            box(bm, c, (0.010, 0.022, 0.034), PAINT, rot=rot)
    g.add(bm, bevel=(0.002, 2, 30))

    # --- rivets on the plates
    bm = new_bm()
    nf = PLATE_N
    for s in (-1, 1):
        for x, z in ((0.232, 0.030), (0.218, 0.088), (0.203, 0.146), (0.165, 0.160)):
            p = Vector((s * x, front_y(z), z)) + nf * 0.0004
            rivets(bm, [tuple(p)], tuple(nf))
        ns = Vector((s * 1.0, 0.0, 0.395)).normalized()
        for y, z in ((-0.110, 0.030), (-0.110, 0.120), (0.060, 0.050), (0.180, 0.030), (0.180, 0.120)):
            p = Vector((s * side_x(z), y, z)) + ns * 0.0004
            rivets(bm, [tuple(p)], tuple(ns))
        nr = Vector((0.0, 1.0, 0.142)).normalized()
        for x, z in ((0.215, 0.050), (0.215, 0.100), (0.215, 0.150)):
            p = Vector((s * x, rear_y(z), z)) + nr * 0.0004
            rivets(bm, [tuple(p)], tuple(nr))
    g.add(bm, subsurf=1)
    return g


# ---------------------------------------------------------------------- gun

def _bore_level(bm):
    """Everything below is built level along -Y from the trunnion; this turns
    it up to the rest elevation about the trunnion."""
    tilt(bm, -GUN_EL, TRUNNION[0], TRUNNION[1])


def mantlet(mats):
    """The boot on the mount's inner plate: a rounded square tapering to the
    jacket -- what lays with the gun and does not recoil.  Its foot runs
    2.4 cm back into the inner plate, so at either end of the ladder the
    plate still swallows it: the part in the open changes shape, nothing
    opens."""
    g = Group(mats)
    ty, tz = TRUNNION
    bm = new_bm()
    rings = []
    for s, hw, r in ((0.010, 0.086, 0.026), (0.035, 0.082, 0.028), (0.050, 0.077, 0.032)):
        rings.append([Vector((x, ty - s, tz + z)) for x, z in rr_loop(0.0, 0.0, hw, hw, r, n=4)])
    flat_loft(bm, rings, GUN)
    _bore_level(bm)
    g.add(bm, bevel=(0.003, 2, 30))
    return g


def barrel(mats):
    """Breech stub, jacket, neck, the tube in three drums, bored.  The stub
    ends exactly on the trunnion, which is where `barrel_recoil.trunnion()`
    puts the pivot (the breech end of the tube on the bore), so the pipeline
    lays the gun about the same axis the model was built for."""
    g = Group(mats)
    ty, tz = TRUNNION
    prof = [(0.0, 0.0), (0.0, 0.045), (-0.040, 0.045), (-0.040, 0.070), (-0.044, 0.0747),
            (-0.111, 0.0747), (-0.115, 0.0705), (-0.119, 0.066), (-0.139, 0.066),
            (-0.142, 0.0700), (-0.181, 0.0700), (-0.183, 0.0672), (-0.187, 0.0672),
            (-0.189, 0.0700), (-0.222, 0.0700), (-0.224, 0.0672), (-0.228, 0.0672),
            (-0.230, 0.0700), (-0.262, 0.0700), (-0.265, 0.0670), (-0.265, 0.0520),
            (-0.260, 0.0500), (-0.232, 0.0500), (-0.232, 0.0)]
    mi = [GUN] * 20 + [DARK] * 4
    bm = new_bm()
    lathe(bm, prof, mi, seg=64, axis="Y", center=(0.0, ty, tz))
    _bore_level(bm)
    g.add(bm, bevel=(0.0015, 2, 30))
    return g
