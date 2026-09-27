"""MT_PARTS_1, rebuilt from scratch on repro_kit (docs/repro.md).

The second copy, 2026-09-27.  It stands 1.0 to the +X of the original in the
same scene, in collection `MT_PARTS_1.Repro`, every name suffixed `.Repro`.

Every number is in MT_PARTS_1's own frame, measured off its vertices:

- front -Y; ground z -0.4174 -- the lowest vertex of the *rebuilt* belts
  (`*.Caterpillar.Rebuilt`, which the MTP sprites were rendered from; the
  generator's `*.Caterpillar.Geometry` sits in the same place, 2 mm higher,
  and is not what the copy repeats);
- the left belt (`L.*`) on -X, belt centres x -0.2314 / +0.2331 -> 0.2322;
- the ring axis (0, 0.0065) as stamped (`ring_axis`); the original's turret
  was cut off its hull at z -0.0261 (`_ring_cut`), so its foot is not round
  (stamped roundness 0.81) and the hull keeps the turret's lower bowl as a
  platform of the same plan;
- the gun: a grey block in the turret face lays with it (`mantlet`), the
  face stays; the bore at z 0.086 (the original droops 1.0 deg, the copy
  rests level); muzzle y -0.4997;
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
    PAINT, GUN, RUBBER, TRACK, GLASS, DARK, PAINT_T, PAINTDK, RIVET, RIVET_G, RIV, GLASS_T,
    Group, new_bm, box, prism, lathe, rounded_rect_profile, cyl, sphere, rivets, line,
    bend_bar, hull_solid, bent_plate, clip_polygon, ring_prism_y, rr_loop, loft,
    belt_path, place_belt, game, segs,
)

NAME = "MT_PARTS_1"
SFX = ".Repro"
X_OFF = 1.0
PREFIX = "MTR"
GROUND = -0.4174
RING_C = (0.0, 0.0065)
TRACK_X = 0.2322
# the original's belts that the sprites show: the kit compares against these
ORIGINAL = {"l_cat": "L.Caterpillar.Rebuilt", "r_cat": "R.Caterpillar.Rebuilt"}

# the army's paint, not the original's: the generator painted MT_PARTS_1 14 deg
# bluer, greyer and 15 % darker than LT_PARTS, and one side's tanks should
# not look like two paint jobs (repro_kit.ARMY_PALETTE)
PALETTE = ARMY_PALETTE

# ------------------------------------------------------------------- layout

BELLY_Z = -0.352
CORE_X = 0.185            # the hull between the belts
DECK_Z = -0.059
FENDER_X = (0.183, 0.298)
SKIRT_X = (0.270, 0.300)  # a 30 mm plate, between the belt's runs, inside its arcs
SEAMS = (-0.104, 0.044, 0.222)


def glacis_y(z):
    """The glacis plane at x 0.12 (the raised plate stands 12 mm proud of it)."""
    return -0.217 + 0.95 * (z + 0.06)


# the rear plate over the engine, a 60 deg slope (outer face)
SLOPE = ((0.405, -0.050), (0.494, -0.207))


def slope_y(z):
    (y0, z0), (y1, z1) = SLOPE
    return y0 + (z0 - z) * (y1 - y0) / (z0 - z1)


# side profile on the centre line: belly, nose, glacis up to the fenders, the
# rear under and behind the engine plate.  Above z -0.135 the upper hull (a
# loft) takes over, so the prism can stay as wide as the belly.
CORE = [(-0.272, BELLY_Z), (0.330, BELLY_Z), (0.394, -0.347), (0.425, -0.330),
        (0.443, -0.310), (0.453, -0.290), (0.457, -0.252), (0.470, -0.234),
        (0.478, -0.214), (slope_y(-0.214) - 0.010, -0.214), (slope_y(-0.135) - 0.010, -0.135),
        (glacis_y(-0.135), -0.135), (-0.350, -0.200), (-0.362, -0.220), (-0.370, -0.240),
        (-0.368, -0.262), (-0.356, -0.290), (-0.336, -0.320), (-0.316, -0.340), (-0.297, -0.349)]

# the upper hull in plan: half width by y at z -0.11, bulging over the fenders
# round the turret, narrowing into the glacis, 0.175 behind; k(z) shrinks the
# bulge towards the deck (the walls lean in) and narrows the front
UPPER_W = [(-0.30, 0.140), (-0.26, 0.160), (-0.22, 0.176), (-0.18, 0.184), (-0.14, 0.192),
           (-0.10, 0.209), (-0.06, 0.222), (-0.02, 0.227), (0.02, 0.225), (0.05, 0.205),
           (0.08, 0.180), (0.11, 0.175), (0.60, 0.175)]
UPPER_Z = [(-0.145, 1.0), (-0.110, 1.0), (-0.090, 0.62), (-0.075, 0.42), (-0.065, 0.30),
           (DECK_Z, 0.22)]

# the turret's lower bowl, left on the hull by the original's cut: the same
# plan as the turret's foot, so the two line up at rest
FOOT = [(0.0, -0.157), (0.100, -0.157), (0.155, -0.140), (0.190, -0.110), (0.205, -0.070),
        (0.211, -0.030), (0.208, 0.010), (0.190, 0.045), (0.172, 0.080), (0.163, 0.120),
        (0.152, 0.160), (0.130, 0.190), (0.090, 0.212), (0.0, 0.214)]
PLATFORM_Z = (DECK_Z - 0.008, -0.028)

# the fender's top line: its front slopes down to y -0.35, the lamp stands
# ahead of it on a bumper box
FENDER = [(-0.334, -0.158), (-0.328, -0.144), (-0.312, -0.134), (-0.290, -0.128),
          (-0.240, -0.119), (-0.160, -0.127), (0.000, -0.130), (0.370, -0.126),
          (0.420, -0.134), (0.450, -0.148), (0.474, -0.170), (0.488, -0.200)]
SKIRT = [(-0.321, -0.355), (-0.323, -0.252), (-0.283, -0.192), (0.330, -0.196),
         (0.395, -0.210), (0.425, -0.232), (0.425, -0.262), (0.362, -0.330), (0.340, -0.355)]

REAR_BOX = dict(x=(0.170, 0.276), y=(0.262, 0.404), z=(-0.134, -0.046))
DRUM = dict(x=0.2387, z=-0.0857, r=0.043, y=(0.105, 0.245))
LAMP = dict(x=0.225, y=-0.346, z=-0.135)       # lens face at y -0.367
BUMPER = dict(x=(0.172, 0.290), y=(-0.378, -0.322), z=(-0.200, -0.168))

# running gear: the belt is a stadium, not a hull of the wheels (the idler
# stands 4 cm inside its front arc, as on the original)
BELT_C = ((-0.2451, -0.2872), (0.3511, -0.2872))
BELT_T = 0.0225
BELT_RIN = 0.1301 - BELT_T
BELT_Z = (-0.4173, -0.1571)            # outer bottom and top runs
# the ends' outer edge off the rebuilt belt: the extreme front and rear y in
# each 1 cm band starting at z -- not arcs of one circle, the upper front is
# flatter than the lower
BELT_ENDS = [
    (-0.415, -0.2700, 0.3745), (-0.405, -0.3005, 0.4013), (-0.395, -0.3151, 0.4225),
    (-0.385, -0.3298, 0.4338), (-0.375, -0.3404, 0.4451), (-0.365, -0.3491, 0.4562),
    (-0.355, -0.3574, 0.4614), (-0.345, -0.3636, 0.4674), (-0.335, -0.3672, 0.4738),
    (-0.325, -0.3708, 0.4758), (-0.315, -0.3748, 0.4776), (-0.305, -0.3752, 0.4792),
    (-0.295, -0.3748, 0.4812), (-0.285, -0.3746, 0.4806), (-0.275, -0.3750, 0.4786),
    (-0.265, -0.3736, 0.4769), (-0.255, -0.3693, 0.4756), (-0.245, -0.3653, 0.4706),
    (-0.235, -0.3621, 0.4648), (-0.225, -0.3538, 0.4584), (-0.215, -0.3454, 0.4513),
    (-0.205, -0.3377, 0.4401), (-0.195, -0.3231, 0.4293), (-0.185, -0.3085, 0.4121),
    (-0.175, -0.2868, 0.3914), (-0.165, -0.2583, 0.3569)]
BELT_W = 0.1175
PITCH = 0.0426
WHEEL_DX = 0.0093          # the wheels stand 9 mm outboard of the belt's centre
IDLER = (-0.263, -0.2995, 0.070)
SPROCKET = (0.349, -0.283, 0.078)
ROAD = [(-0.103, -0.338, 0.047), (0.050, -0.338, 0.047), (0.200, -0.338, 0.047)]
RETURN = [(-0.088, -0.233, 0.052), (0.100, -0.233, 0.052)]
TAIL = (0.328, -0.353, 0.034)            # the small wheel under the sprocket

# the game variant: where the sprung mass rocks (mid-belt, level with the
# belt tops) and where the exhaust leaves (the grille, as the stamp has it)
BODY_PIVOT = ((BELT_C[0][0] + BELT_C[1][0]) / 2, BELT_C[0][1] + BELT_RIN + BELT_T)
EXHAUST = ((0.0, 0.430, -0.072),)
GAME_TAG = "MTR"                        # Models/MTR/

# turret: a coffin plan -- flat front, widest just ahead of the ring, a narrow
# flat rear -- lofted from its foot on the platform up to a roof with a
# rounded edge
# z0, z1, r: inside the platform's plan, 15 mm of it clear under the body's
# foot -- turret_axis fits bands 3 % of the turret's height tall (12 mm with
# the antenna), and at 9 mm the best band took in the foot: axis 5.6 cm off
T_RING = (-0.040, -0.018, 0.150)
RING_Z0 = T_RING[0]
T_FOOT_Z = -0.025                      # 3 mm over the platform: the ring shows as a line
BODY = [(0.0, -0.226), (0.140, -0.226), (0.168, -0.222), (0.186, -0.206), (0.195, -0.180),
        (0.201, -0.150), (0.212, -0.080), (0.218, -0.030), (0.215, 0.020), (0.203, 0.080),
        (0.178, 0.160), (0.136, 0.240), (0.113, 0.276), (0.090, 0.285), (0.0, 0.285)]
T_SECTIONS = [  # z, front y, rear y, max half width
    (-0.013, -0.187, 0.228, 0.222),
    (0.000, -0.205, 0.245, 0.224),
    (0.012, -0.220, 0.274, 0.223),
    (0.028, -0.226, 0.285, 0.220),
    (0.100, -0.222, 0.273, 0.208),
    (0.140, -0.206, 0.266, 0.201),
    (0.165, -0.178, 0.258, 0.196),
    (0.184, -0.150, 0.238, 0.188),
    # the roof edge: a chamfer creased against the walls (the bevel inks it),
    # not a dome -- the original's roof is a flat plate on upright walls
    (0.205, -0.122, 0.213, 0.140),
]
ROOF_Z = 0.205
GUN_Z = 0.086
BLOCK = dict(hw=0.095, y_back=-0.170, y_front=-0.247)
# where the block's top and bottom arcs meet its face (y, z): both arcs turn
# into themselves about the trunnion, so the part standing proud of the
# turret face keeps its shape at any elevation; the back stays buried
BLOCK_TOP = (-0.247, 0.155)
BLOCK_LOW = (-0.247, 0.022)
TRUNNION = (-0.222, GUN_Z)
CUPOLA = (0.046, 0.034)
PERISCOPE = dict(x=(-0.135, -0.057), y=(0.008, 0.081))
ANTENNA = (0.112, 0.159, 0.364)


# ------------------------------------------------------------------ helpers

def smooth_loop(ctrl, n, iters=3):
    """A closed plan from its right half (x, y), front centre to rear centre,
    mirrored, corners rounded (Chaikin), resampled to n points by arc length
    from the front centre -- rings of one n loft into each other."""
    P = np.array(list(ctrl) + [(-x, y) for x, y in reversed(ctrl[1:-1])], float)
    for _ in range(iters):
        Q = np.empty((2 * len(P), 2))
        nxt = np.roll(P, -1, 0)
        Q[0::2] = 0.75 * P + 0.25 * nxt
        Q[1::2] = 0.25 * P + 0.75 * nxt
        P = Q
    k = int(np.argmin(np.where(P[:, 1] < P[:, 1].mean(), np.abs(P[:, 0]), 9.0)))
    P = np.roll(P, -k, 0)
    seg = np.linalg.norm(np.roll(P, -1, 0) - P, axis=1)
    cum = np.concatenate([[0.0], np.cumsum(seg)])
    s = np.arange(n) * cum[-1] / n
    i = np.searchsorted(cum, s, side="right") - 1
    t = (s - cum[i]) / seg[i]
    return P[i] + (np.roll(P, -1, 0)[i] - P[i]) * t[:, None]


def ring_at(xy, z):
    return [Vector((float(x), float(y), z)) for x, y in xy]


def reshape(ctrl, yf, yr, w, ym=-0.03):
    """`ctrl` stretched to a front at yf, a rear at yr, a half width w."""
    f0, r0 = ctrl[0][1], ctrl[-1][1]
    w0 = max(x for x, _ in ctrl)
    out = []
    for x, y in ctrl:
        yy = ym + (y - ym) * ((yf - ym) / (f0 - ym) if y < ym else (yr - ym) / (r0 - ym))
        out.append((x * w / w0, yy))
    return out


def upper_w(y, k):
    w0 = float(np.interp(y, [p[0] for p in UPPER_W], [p[1] for p in UPPER_W]))
    return 0.175 + (w0 - 0.175) * k if w0 >= 0.175 else w0 - 0.025 * (1.0 - k)


def upper_ring(z, k, n):
    yf, yr = glacis_y(z) + 0.001, slope_y(z) - 0.010
    ys = np.concatenate([np.linspace(yf, yf + 0.06, 5), np.linspace(yf + 0.08, yr, 18)])
    ctrl = [(0.0, yf)] + [(upper_w(y, k), y) for y in ys] + [(0.0, yr)]
    return ring_at(smooth_loop(ctrl, n, iters=2), z)


# ------------------------------------------------------------------- tracks

def link_mesh(pitch):
    """One link (u along the belt, v across, w out): a long shoe of three
    pads on a plate, the full thickness down to the ground, and a short
    connector standing 3 mm proud of it on the inside -- the original's belt
    alternates the two."""
    bm = new_bm()
    shoe, con = pitch * 0.62, pitch * 0.28
    us, uc = -pitch * 0.5 + 0.001 + shoe / 2, pitch * 0.5 - 0.001 - con / 2
    box(bm, (us, 0, 0.005), (shoe, BELT_W, 0.010), TRACK)
    for v in (-0.039, 0.0, 0.039):
        box(bm, (us, v, 0.016), (shoe * 0.92, 0.036, 0.013), TRACK)
    box(bm, (uc, 0, 0.007), (con, BELT_W * 0.96, 0.020), TRACK)
    return bm


def belt_outline():
    """The belt's outer edge as a CCW (y, z) polygon: the runs and BELT_ENDS
    (a band's extreme lies at its edge nearer the belt's middle height)."""
    zb, zt = BELT_Z
    zc = BELT_C[0][1]
    rear, front = [], []
    for z, yf, yr in BELT_ENDS:
        zz = z + 0.01 if z + 0.01 <= zc else (z if z >= zc else zc)
        rear.append((yr, zz))
        front.append((yf, zz))
    pts = ([(BELT_C[0][0], zb), (BELT_C[1][0], zb)] + rear +
           [(BELT_C[1][0], zt), (BELT_C[0][0], zt)] + front[::-1])
    P = np.array(pts, float)
    for _ in range(3):                     # soften the 1 cm steps
        nxt = np.roll(P, -1, 0)
        Q = np.empty((2 * len(P), 2))
        Q[0::2] = 0.75 * P + 0.25 * nxt
        Q[1::2] = 0.25 * P + 0.75 * nxt
        P = Q
    return P


def belt_spec():
    """The belt's inner surface as a CCW (y, z) path -- the outer edge moved
    in by the belt's thickness -- its pitch, its link."""
    P = belt_outline()
    t = np.roll(P, -1, 0) - np.roll(P, 1, 0)
    t /= np.linalg.norm(t, axis=1)[:, None]
    inward = np.c_[-t[:, 1], t[:, 0]]          # left of travel on a CCW loop
    return P + inward * BELT_T, PITCH, link_mesh


def belt(mats, xc):
    path, pitch, link = belt_spec()
    bm, n, pitch, L = place_belt(mats, xc, path, pitch, link)
    return bm, {"links": n, "pitch": round(pitch, 5), "length": round(L, 4)}


def disc(bm, y, z, r, xc, w, seg=48, mi=TRACK):
    """A dished wheel centred at xc: tyre, dish, hub boss (lathe about X),
    the dish on both faces."""
    h = w / 2
    prof = [(-h - 0.006, 0.0), (-h - 0.006, r * 0.20), (-h, r * 0.26), (-h, r * 0.42),
            (-h + 0.008, r * 0.50), (-h + 0.008, r * 0.72), (-h, r * 0.80), (-h, r * 0.93),
            (-h + 0.004, r), (h - 0.004, r), (h, r * 0.93), (h, r * 0.80),
            (h - 0.008, r * 0.72), (h - 0.008, r * 0.50), (h, r * 0.42), (h, r * 0.26),
            (h + 0.006, r * 0.20), (h + 0.006, 0.0)]
    lathe(bm, prof, mi, seg=seg, axis="X", center=(xc, y, z))


def debris():
    """What flies off when the tank blows up, for the game variant: a name,
    the node it comes off, and a box in this frame.  Every piece of that
    node's mesh whose centre lies in the box goes with it.  Sides are the
    tank's own: L is +X."""
    out = []
    edges = [-0.5] + list(SEAMS) + [0.5]
    for s, S in ((1, "L"), (-1, "R")):
        def sx(a, b):
            return tuple(sorted((s * a, s * b)))
        for i, (a, b) in enumerate(zip(edges, edges[1:])):
            out.append({"name": "Skirt.%s.%d" % (S, i), "parent": "Hull",
                        "box": (sx(0.262, 0.330), (a + 0.001, b - 0.001), (-0.37, -0.185))})
        out.append({"name": "Drum.%s" % S, "parent": "Hull",
                    "box": (sx(0.190, 0.280), (DRUM["y"][0] - 0.02, DRUM["y"][1] + 0.02),
                            (-0.135, -0.035))})
        out.append({"name": "Lamp.%s" % S, "parent": "Hull",
                    "box": (sx(0.19, 0.27), (-0.42, -0.325), (-0.169, -0.09))})
    hx, hy = CUPOLA
    out.append({"name": "Hatch", "parent": "Turret",       # the cupola's slatted lid
                "box": ((hx - 0.082, hx + 0.082), (hy - 0.082, hy + 0.082),
                        (ROOF_Z + 0.014, ROOF_Z + 0.040))})
    return out


def wheel_spec():
    """Every wheel that turns: name, axle (y, z), and the radius the belt turns
    it at (the engine spins each one distance / r)."""
    out = [("Idler", IDLER)]
    out += [("Road.%d" % i, w) for i, w in enumerate(ROAD)]
    out += [("Roller.%d" % i, w) for i, w in enumerate(RETURN)]
    out += [("Tail", TAIL), ("Sprocket", SPROCKET)]
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
            disc(bm, y, z, r - 0.010, xw, 0.070, seg=seg)
        elif w["name"].startswith("Roller"):
            disc(bm, y, z, r, xw, 0.060, seg=seg)
        else:
            disc(bm, y, z, r, xw, 0.078, seg=seg)
        g.add(bm, subsurf=1)
        if w["name"] == "Sprocket":
            bm = new_bm()          # teeth, two rings
            for k in range(12):
                t = math.tau * k / 12
                rot = Matrix.Rotation(t, 3, "X")
                for dx in (-0.026, 0.026):
                    c = Vector((xw + dx, y, z)) + rot @ Vector((0, 0, r - 0.008))
                    box(bm, c, (0.018, 0.018, 0.022), TRACK, rot=rot)
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
    """What does not turn: a long beam, struts up into the hull, arms down to
    the road wheels, axles."""
    g = Group(mats)
    bm = new_bm()
    xin = xc - s * 0.036
    zb = -0.283
    box(bm, (xin, 0.06, zb), (0.018, 0.54, 0.020), TRACK)
    for y in (-0.19, 0.02, 0.17, 0.31):
        box(bm, (xin, y, -0.235), (0.014, 0.018, 0.10), TRACK)
    for (y, z, r) in ROAD:
        a, b_ = Vector((xin, y + 0.06, zb)), Vector((xin, y, z))
        d = b_ - a
        rot = Vector((0, 1, 0)).rotation_difference(d.normalized()).to_matrix()
        box(bm, (a + b_) / 2, (0.014, d.length + 0.02, 0.018), TRACK, rot=rot)
        cyl(bm, (xin - s * 0.012, y + 0.06, zb), (xin + s * 0.012, y + 0.06, zb),
            0.013, TRACK, seg=16)
    for (y, z, r) in [IDLER, SPROCKET, TAIL] + ROAD + RETURN:
        cyl(bm, (xin, y, z), (xc + s * WHEEL_DX, y, z), 0.011, TRACK, seg=12)
    g.add(bm, bevel=(0.003, 2, 40), subsurf=1)
    return g


# --------------------------------------------------------------------- hull

def glacis_frame():
    """Point on the glacis at (x, z) and its outward normal."""
    d = Vector((0.0, 0.95, 1.0)).normalized()          # up the slope
    n = Vector((0.0, -d.z, d.y))

    def at(x, z, out=0.0):
        return Vector((x, glacis_y(z), z)) + n * out
    return at, n


def hull(mats):
    g = Group(mats)

    # --- body between the belts: belly, nose, lower glacis, rear
    bm = new_bm()
    prism(bm, CORE, -CORE_X, CORE_X, PAINT)
    g.add(bm, bevel=(0.018, 2, 30))

    # --- upper hull: a loft of plan rings from under the fender tops to the
    # deck; its front is the glacis (1 mm behind the core's, which covers
    # it up to the fenders), its back the engine plate's slope
    n = segs(96, 48)
    bm = new_bm()
    loft(bm, [upper_ring(z, k, n) for z, k in UPPER_Z], PAINT)
    g.add(bm, bevel=(0.006, 2, 30))

    # --- the turret platform: the turret's lower bowl, on the deck
    bm = new_bm()
    foot = smooth_loop(FOOT, segs(96, 48))
    loft(bm, [ring_at(foot * [1.0, 1.0], PLATFORM_Z[0]), ring_at(foot, PLATFORM_Z[1])], PAINT)
    g.add(bm, bevel=(0.004, 2, 30))

    # --- the raised rear deck the grille stands on
    bm = new_bm()
    y1 = slope_y(-0.054) - 0.004
    # 1 cm into the rear boxes (inner faces x 0.170): flush, the two faces baked grey
    box(bm, (0, (0.245 + y1) / 2, -0.061), (0.36, y1 - 0.245, 0.014), PAINT)
    g.add(bm, bevel=(0.004, 2, 30))

    # --- fenders: one bent plate a side, the mudguard down in front, the end
    # rounded down behind the sprocket
    bm = new_bm()
    for s in (-1, 1):
        x0, x1 = sorted((s * FENDER_X[0], s * FENDER_X[1]))
        bent_plate(bm, FENDER, 0.022, x0, x1, PAINT)
    g.add(bm, bevel=(0.008, 2, 30))

    # --- skirts: four panels a side, split at the seams
    bm = new_bm()
    edges = [-0.5] + list(SEAMS) + [0.5]
    for s in (-1, 1):
        x0, x1 = sorted((s * SKIRT_X[0], s * SKIRT_X[1]))
        for a, b in zip(edges, edges[1:]):
            prism(bm, clip_polygon(SKIRT, a + 0.002, b - 0.002), x0, x1, PAINT)
    g.add(bm, bevel=(0.005, 2, 30))
    # a louvred slot low in front, a square plate high at the back
    bm = new_bm()
    bmd = new_bm()
    for s in (-1, 1):
        xf = s * SKIRT_X[1]
        box(bm, (xf + s * 0.002, -0.235, -0.320), (0.006, 0.080, 0.026), PAINT)
        box(bmd, (xf + s * 0.0035, -0.235, -0.320), (0.004, 0.068, 0.016), DARK)
        box(bm, (xf + s * 0.002, 0.357, -0.275), (0.006, 0.050, 0.062), PAINT)
    g.add(bm, bevel=(0.0025, 2, 30))
    bm = new_bm()
    for s in (-1, 1):
        xf = s * (SKIRT_X[1] + 0.0055)
        for zz in (-0.3145, -0.3255):
            box(bm, (xf, -0.235, zz), (0.004, 0.064, 0.0035), PAINT)
    g.add(bmd)
    g.add(bm)

    # --- rear boxes either side of the grille, a diamond vent on each
    bm = new_bm()
    B = REAR_BOX
    for s in (-1, 1):
        cx = s * (B["x"][0] + B["x"][1]) / 2
        box(bm, (cx, sum(B["y"]) / 2, sum(B["z"]) / 2),
            (B["x"][1] - B["x"][0], B["y"][1] - B["y"][0], B["z"][1] - B["z"][0]), PAINT)
    g.add(bm, bevel=(0.007, 2, 30))
    for s in (-1, 1):
        cx, cy = s * (B["x"][0] + B["x"][1]) / 2, 0.365
        bm = new_bm()       # a square frame lying on the lid (built along Y, laid flat)
        ring_prism_y(bm, rr_loop(0, 0, 0.034, 0.034, 0.004, n=2),
                     rr_loop(0, 0, 0.027, 0.027, 0.003, n=2), 0.0, 0.006, PAINT)
        for v in bm.verts:
            x, yy, zz = v.co
            v.co = Vector((cx + x, cy + zz, B["z"][1] - 0.001 + yy))
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
        box(bm, (cx, cy, B["z"][1] + 0.002), (0.030, 0.030, 0.006), PAINT,
            rot=Matrix.Rotation(math.radians(45), 3, "Z"))
        g.add(bm, bevel=(0.0015, 2, 30))

    # --- fuel drums on the fenders, their hoops, their straps
    bm = new_bm()
    D = DRUM
    for s in (-1, 1):
        x = s * D["x"]
        y0, y1 = D["y"]
        lathe(bm, [(y0, 0.0), (y0, D["r"] - 0.010), (y0 + 0.004, D["r"]), (y1 - 0.004, D["r"]),
                   (y1, D["r"] - 0.010), (y1, 0.0)], GUN, seg=48, axis="Y", center=(x, 0, D["z"]))
        for t in (0.30, 0.70):
            yh = y0 + (y1 - y0) * t
            lathe(bm, rounded_rect_profile(yh - 0.006, yh + 0.006, D["r"] - 0.004, D["r"] + 0.004,
                                           0.002, n=1), GUN, seg=48, axis="Y",
                  center=(x, 0, D["z"]))
    g.add(bm, subsurf=1)
    bm = new_bm()
    for s in (-1, 1):
        x = s * D["x"]
        for yy in (D["y"][0] + 0.022, D["y"][1] - 0.022):
            pts = []
            for k in range(11):
                t = math.radians(215 - 250 * k / 10)
                pts.append(Vector((x + s * (D["r"] + 0.003) * math.cos(t),
                                   yy, D["z"] + (D["r"] + 0.003) * math.sin(t))))
            bend_bar(bm, pts, 0.0028, PAINTDK, seg=8)
    g.add(bm)

    # --- glacis: a raised plate, two domed bosses at its top
    at, nrm = glacis_frame()
    bm = new_bm()
    rot = Vector((0, 0, 1)).rotation_difference(nrm).to_matrix()
    zc = -0.162
    box(bm, at(0.0, zc, 0.004), (0.160, 0.072 / 0.72, 0.016), PAINT, rot=rot)
    g.add(bm, bevel=(0.004, 2, 30))
    bm = new_bm()
    for x in (-0.115, 0.115):
        sphere(bm, at(x, -0.088, 0.002), 0.024, PAINT, scale=(1, 1, 0.8), rot=rot, seg=24, rings=12)
    g.add(bm, subsurf=1)
    # a small hatch plate on the glacis above the raised plate
    bm = new_bm()
    box(bm, at(0.0, -0.118, 0.001), (0.12, 0.030 / 0.72, 0.006), PAINT, rot=rot)
    g.add(bm, bevel=(0.002, 2, 30))

    # --- headlights on the mudguards, their brackets
    bm = new_bm()
    bmg = new_bm()
    L = LAMP
    for s in (-1, 1):
        c = (s * L["x"], L["y"], L["z"])
        # 4 mm off the axis: a pole's fan of sliver faces gets no texel of its own
        # and baked the glass's grey into the housing (26 samples)
        lathe(bm, rounded_rect_profile(-0.016, 0.018, 0.004, 0.034, 0.008, n=3), PAINT,
              seg=48, axis="Y", center=c)
        lathe(bm, rounded_rect_profile(-0.022, -0.011, 0.026, 0.037, 0.004, n=2), PAINT,
              seg=48, axis="Y", center=c)
        lathe(bmg, [(-0.020, 0.0), (-0.0195, 0.012), (-0.017, 0.022), (-0.013, 0.028)],
              GLASS, seg=48, axis="Y", center=c, closed=False)
    g.add(bm, subsurf=1)
    g.add(bmg, subsurf=1)
    bm = new_bm()                  # the bumper box under each lamp, the lamp's foot
    U = BUMPER
    for s in (-1, 1):
        box(bm, (s * sum(U["x"]) / 2, sum(U["y"]) / 2, sum(U["z"]) / 2),
            (U["x"][1] - U["x"][0], U["y"][1] - U["y"][0], U["z"][1] - U["z"][0]), PAINT)
        box(bm, (s * L["x"], L["y"] + 0.012, -0.163), (0.040, 0.026, 0.020), PAINT)
    g.add(bm, bevel=(0.008, 2, 30))

    # --- tow shackles, front and rear
    bm = new_bm()
    for x in (-0.085, 0.085):
        # rear: a U hanging off its bracket
        yb = 0.470
        box(bm, (x, yb, -0.212), (0.036, 0.020, 0.022), PAINT)
        cyl(bm, (x - 0.02, yb + 0.010, -0.218), (x + 0.02, yb + 0.010, -0.218), 0.005, GUN, seg=12)
        u = [Vector((x - 0.011, yb + 0.016, -0.216))]
        for k in range(9):
            t = math.pi * k / 8
            u.append(Vector((x - 0.011 * math.cos(t), yb + 0.022, -0.251 - 0.011 * math.sin(t))))
        u.append(Vector((x + 0.011, yb + 0.016, -0.216)))
        bend_bar(bm, u, 0.0055, GUN, seg=10)
        # front: a ring hanging deep off the nose, turned to show from both ways
        box(bm, (x, -0.372, -0.218), (0.030, 0.026, 0.022), PAINT)
        cyl(bm, (x - 0.017, -0.384, -0.226), (x + 0.017, -0.384, -0.226), 0.0055, GUN, seg=12)
        rot = Matrix.Rotation(math.radians(40 if x > 0 else -40), 3, "Z")
        c = Vector((x, -0.392, -0.268))
        ring = [c + rot @ Vector((0.0, 0.020 * math.sin(t), 0.022 * math.cos(t)))
                for t in np.linspace(0, math.tau, 25)]
        bend_bar(bm, ring, 0.0055, GUN, seg=10)
    g.add(bm, bevel=(0.003, 2, 30), subsurf=1)

    # --- rivets
    bm = new_bm()
    for s in (-1, 1):
        xf = s * (SKIRT_X[1] + 0.0004)
        for a, b in zip(edges, edges[1:]):
            a, b = max(a, -0.30), min(b, 0.40)
            ya, yb = a + 0.016, b - 0.016
            nn = max(2, int(round((yb - ya) / 0.05)) + 1)
            rivets(bm, [(xf, y, -0.208) for y in np.linspace(ya, min(yb, 0.33), nn)], (s, 0, 0))
            lo_b = min(yb, 0.33)
            nn = max(2, int(round((lo_b - ya) / 0.05)) + 1)
            rivets(bm, [(xf, y, -0.341) for y in np.linspace(ya, lo_b, nn)], (s, 0, 0))
        # along the fender's outer edge
        fy, fz = [p[0] for p in FENDER[2:-3]], [p[1] for p in FENDER[2:-3]]
        rivets(bm, [(s * 0.286, y, float(np.interp(y, fy, fz)) + 0.0004)
                    for y in np.linspace(-0.305, 0.36, 13)], (0, 0, 1), r=RIV * 0.9)
        # rear box outer face
        rivets(bm, line((s * (REAR_BOX["x"][1] + 0.0004), 0.27, -0.060),
                        (s * (REAR_BOX["x"][1] + 0.0004), 0.40, -0.060), 3), (s, 0, 0))
    for x in (-0.07, 0.07):
        for z in (-0.180, -0.144):
            rivets(bm, [at(x, z, 0.0124)], nrm)
    # round the platform's foot
    foot = smooth_loop(FOOT, 28)
    for k, (x, y) in enumerate(foot):
        p = Vector((x, y, 0.0))
        d = Vector((x, y - RING_C[1], 0.0)).normalized()
        rivets(bm, [Vector((x, y, -0.036)) + d * 0.0004], d, r=RIV * 0.85)
    g.add(bm, subsurf=1)

    if game():
        # the game variant loses its turret: what shows then is the ring's
        # opening, dark, not a painted platform -- and a knocked-out turret
        # tipped in its ring shows it under the raised side.  1 mm over the
        # platform, inside the ring (r 0.150)
        bm = new_bm()
        lathe(bm, [(PLATFORM_Z[1] + 0.001, 0.0), (PLATFORM_Z[1] + 0.001, 0.148)], DARK, seg=96,
              axis="Z", center=(RING_C[0], RING_C[1], 0), closed=False)
        g.add(bm)
    return g


def engine(mats):
    """The rear plate on its slope and the grille of round bars over the
    rear deck, draped down the plate -- the exhaust source."""
    g = Group(mats)
    (y0, z0), (y1, z1) = SLOPE
    d = Vector((0.0, y1 - y0, z1 - z0)).normalized()
    nrm = Vector((0.0, -d.z, d.y))
    if nrm.y < 0:
        nrm = -nrm
    bm = new_bm()
    p0, p1 = Vector((0, y0, z0)), Vector((0, y1, z1))
    q0, q1 = p0 - nrm * 0.012, p1 - nrm * 0.012
    # as wide as the hull behind it and 1 cm into the rear boxes, never flush
    prism(bm, [(p0.y, p0.z), (p1.y, p1.z), (q1.y, q1.z), (q0.y, q0.z)], -0.180, 0.180, PAINT)
    g.add(bm, bevel=(0.005, 2, 30))
    bm = new_bm()
    rot = Vector((0, 0, 1)).rotation_difference(nrm).to_matrix()
    c = p0.lerp(p1, 0.60) + nrm * 0.002
    box(bm, c, (0.130, 0.105, 0.008), PAINT, rot=rot)
    g.add(bm, bevel=(0.003, 2, 30))
    # the grille: a dark bed, eleven round bars along the deck and down
    bm = new_bm()
    box(bm, (0, 0.335, -0.0535), (0.270, 0.150, 0.004), DARK)
    g.add(bm)
    bm = new_bm()                  # the frame: sides and front, the rear is the plate's edge
    for s in (-1, 1):
        box(bm, (s * 0.141, 0.334, -0.050), (0.012, 0.156, 0.016), PAINT)
    box(bm, (0, 0.256, -0.051), (0.294, 0.012, 0.014), PAINT)   # 2 mm under the sides' tops
    g.add(bm, bevel=(0.003, 2, 30))
    bm = new_bm()
    zc = -0.0545               # half sunk in the deck: the tops at -0.046, as the original's

    def on_plate(x, z):
        return Vector((x, slope_y(z), z)) + nrm * 0.004
    for x in np.linspace(-0.121, 0.121, 12):
        pts = [Vector((x, 0.262, zc)), Vector((x, 0.392, zc)), Vector((x, 0.403, zc - 0.003)),
               on_plate(x, -0.064), on_plate(x, -0.110)]
        bend_bar(bm, pts, 0.0088, PAINTDK, seg=10)
    g.add(bm)
    return g


# ------------------------------------------------------------------- turret

def body_rings(n):
    rings = [ring_at(smooth_loop(FOOT, n), T_FOOT_Z)]
    for z, yf, yr, w in T_SECTIONS:
        rings.append(ring_at(smooth_loop(reshape(BODY, yf, yr, w), n, iters=2), z))
    return rings


def body_point(phi, z, rings):
    """Point and outward normal on the turret body at height z, plan angle
    phi about the ring axis (for rivets); `rings` = body_rings(n)."""
    n = len(rings[0])
    zs = [r[0].z for r in rings]
    k = max(0, min(len(zs) - 2, int(np.searchsorted(zs, z)) - 1))
    u = (z - zs[k]) / (zs[k + 1] - zs[k])
    ra, rb = rings[k], rings[k + 1]
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
    # the roundest 3 % slice in the lowest 22 %); its lower 12 mm sit in the
    # platform, 3 mm show between it and the body's foot
    bm = new_bm()
    z0, z1, r = T_RING
    wall = [(z, r) for z in np.linspace(z0, z1, 2 if game() else 16)]
    lathe(bm, wall + [(z1, 0.10), (z0, 0.10)], PAINTDK, seg=192, axis="Z", center=(cx, cy, 0))
    g.add(bm)

    # body: foot on the platform, flaring out, walls leaning in, roof edge
    # rounded into a flat roof
    bm = new_bm()
    loft(bm, body_rings(segs(128, 48)), PAINT_T)
    g.add(bm, bevel=(0.006, 2, 40))

    # rivets: a row round the lower wall, one under the roof edge, pairs
    # either side of the side seams
    bm = new_bm()
    dense = body_rings(720)

    def clear_of_front(t):
        return abs(math.atan2(math.sin(t + math.pi / 2), math.cos(t + math.pi / 2))) > 0.62

    for k in range(30):
        t = math.tau * (k + 0.5) / 30
        if clear_of_front(t):
            p, nn = body_point(t, 0.022, dense)
            rivets(bm, [p + nn * 0.0004], nn)
    for k in range(34):
        t = math.tau * (k + 0.5) / 34
        if clear_of_front(t):
            p, nn = body_point(t, 0.160, dense)
            rivets(bm, [p + nn * 0.0004], nn, r=RIV * 0.9)
    for ys in SIDE_SEAMS:
        for s in (-1, 1):
            for dy in (-0.012, 0.012):
                t = math.atan2(ys + dy - cy, s * 0.2)
                for z in (0.050, 0.090, 0.130):
                    p, nn = body_point(t, z, dense)
                    rivets(bm, [p + nn * 0.0004], nn, r=RIV * 0.9)
    g.add(bm, subsurf=1)

    # the block that lays with the gun, proud of the face: `mantlet`

    # cupola: a thick ring, a slatted lid, a handle arched over its right side
    hx, hy = CUPOLA
    zr = ROOF_Z
    bm = new_bm()
    lathe(bm, rounded_rect_profile(zr - 0.010, zr + 0.020, 0.074, 0.091, 0.006, n=3),
          GUN, seg=96, axis="Z", center=(hx, hy, 0))
    lathe(bm, [(zr + 0.026, 0.0), (zr + 0.026, 0.074), (zr + 0.022, 0.079), (zr + 0.012, 0.079)],
          GUN, seg=96, axis="Z", center=(hx, hy, 0), closed=False)
    g.add(bm, subsurf=1)
    bm = new_bm()
    rot = Matrix.Rotation(math.radians(-12), 3, "Z")
    for dy in np.linspace(-0.052, 0.052, 6):
        ln = 2 * math.sqrt(max(0.070 ** 2 - dy ** 2, 1e-6)) * 0.86
        box(bm, Vector((hx, hy, 0)) + rot @ Vector((0, dy, zr + 0.029)), (ln, 0.010, 0.006), GUN, rot=rot)
    arc = []
    for k in range(11):
        t = math.radians(-80 + 160 * k / 10)
        arc.append(Vector((hx + 0.083 * math.cos(t), hy + 0.083 * math.sin(t), zr + 0.052)))
    bend_bar(bm, arc, 0.0045, GUN, seg=10)
    g.add(bm, bevel=(0.002, 2, 30), subsurf=1)
    for a0, a1 in ((-80, -28), (28, 80)):
        band = []
        for k in range(7):
            t = math.radians(a0 + (a1 - a0) * k / 6)
            c, sn = math.cos(t), math.sin(t)
            for rr in (0.080, 0.086):
                for z in (zr + 0.020, zr + 0.052):
                    band.append((hx + rr * c, hy + rr * sn, z))
        for k in range(6):                   # hull the band piecewise: it curves
            bm = new_bm()
            hull_solid(bm, band[8 * k:8 * k + 8], GUN)
            g.add(bm, bevel=(0.0015, 2, 30))

    # periscope: an octagonal base, a head with a hood, teal glass to the front
    P = PERISCOPE
    px, py = sum(P["x"]) / 2, sum(P["y"]) / 2
    bm = new_bm()
    hull_solid(bm, [(px + sx * a, py + sy * b, z)
                    for sx in (-1, 1) for sy in (-1, 1) for (a, b) in ((0.039, 0.026), (0.026, 0.037))
                    for z in (zr - 0.004, zr + 0.012)], PAINT_T)
    g.add(bm, bevel=(0.003, 2, 30))
    bm = new_bm()
    box(bm, (px, py + 0.002, zr + 0.054), (0.068, 0.062, 0.086), PAINT_T)
    box(bm, (px, py - 0.033, zr + 0.090), (0.074, 0.014, 0.010), PAINT_T)
    g.add(bm, bevel=(0.006, 2, 30))
    bm = new_bm()
    box(bm, (px, py - 0.0295, zr + 0.058), (0.052, 0.004, 0.050), GLASS_T)
    g.add(bm, bevel=(0.002, 2, 30))

    # hatch plate, handle rail over the front of the roof, a box at the edge
    bm = new_bm()
    box(bm, (-0.0925, -0.0345, zr + 0.002), (0.059, 0.051, 0.008), PAINT_T)
    box(bm, (0.169, 0.034, zr - 0.004), (0.028, 0.024, 0.030), PAINT_T)
    g.add(bm, bevel=(0.002, 2, 30))
    bm = new_bm()
    rail = [Vector((-0.110, -0.110, zr - 0.004)), Vector((-0.110, -0.110, zr + 0.010)),
            Vector((-0.100, -0.110, zr + 0.017)), Vector((0.100, -0.110, zr + 0.017)),
            Vector((0.110, -0.110, zr + 0.010)), Vector((0.110, -0.110, zr - 0.004))]
    bend_bar(bm, rail, 0.0065, GUN, seg=12)
    g.add(bm)

    # antenna: a socket and a whip
    ax, ay, az = ANTENNA
    bm = new_bm()
    lathe(bm, [(zr - 0.004, 0.0), (zr - 0.004, 0.013), (zr + 0.012, 0.013), (zr + 0.016, 0.008),
               (zr + 0.030, 0.008), (zr + 0.030, 0.0)], GUN, seg=24, axis="Z", center=(ax, ay, 0))
    cyl(bm, (ax, ay, zr + 0.028), (ax, ay, az), 0.0025, GUN, seg=8)
    g.add(bm)
    return g


SIDE_SEAMS = (-0.014, 0.164)      # vertical plate seams on the turret's sides, world y


def turret_ink(b, pos, nrm):
    """Painted plate seams on the turret, in the mesh's object space (the
    ring's frame): vertical lines at two y on the side walls, one round the
    walls under the roof edge."""
    w = 0.0026
    p = b.n.new("ShaderNodeSeparateXYZ")
    b.put(p.inputs[0], pos)
    q = b.n.new("ShaderNodeSeparateXYZ")
    b.put(q.inputs[0], nrm)
    side = b.rng(b.math("ABSOLUTE", q.outputs["X"]), 0.55, 0.75)
    wall = b.rng(b.math("ABSOLUTE", q.outputs["Z"]), 0.75, 0.55)
    above = b.rng(p.outputs["Z"], 0.030, 0.036)
    below = b.rng(p.outputs["Z"], ROOF_Z - T_RING[0] - 0.035, ROOF_Z - T_RING[0] - 0.045)
    out = None
    for ys in SIDE_SEAMS:
        ln = b.math("MULTIPLY", b.line(p.outputs["Y"], ys - RING_C[1], w),
                    b.math("MULTIPLY", side, b.math("MULTIPLY", above, below)))
        out = ln if out is None else b.math("MAXIMUM", out, ln)
    hz = b.math("MULTIPLY", b.line(p.outputs["Z"], 0.172 - T_RING[0], w), wall)
    return b.math("MAXIMUM", out, hz)


def _frame_edge(top):
    """(y, z) where the mantlet's top or bottom arc meets its face."""
    return BLOCK_TOP if top else BLOCK_LOW


def mantlet(mats):
    """The grey block and its bolts: everything that lays with the gun and
    does not recoil.  Top and bottom are arcs about the trunnion, so at any
    elevation the block turns into itself: the part proud of the face keeps
    its shape and no slot opens where it goes in."""
    g = Group(mats)
    B = BLOCK
    ty, tz = TRUNNION
    pts = []
    for top in (True, False):
        ey, ez = _frame_edge(top)
        R = math.hypot(ey - ty, ez - tz)
        s = 1.0 if top else -1.0
        for yy in np.linspace(B["y_front"], B["y_back"], 24):
            zz = tz + s * math.sqrt(max(R * R - (yy - ty) ** 2, 0.0))
            for x in (-B["hw"], B["hw"]):
                pts.append((x, yy, zz))
    bm = new_bm()
    hull_solid(bm, pts, GUN)
    g.add(bm, bevel=(0.028, 3, 30))     # a pill: the original's block is round, not chamfered
    # bolts down both sides of the face
    bm = new_bm()
    for x in (-0.080, 0.080):
        for z in np.linspace(GUN_Z - 0.050, GUN_Z + 0.050, 4):
            rivets(bm, [(x, B["y_front"] - 0.0004, z)], (0, -1, 0), r=0.0055, mi=RIVET_G)
    g.add(bm, subsurf=1)
    return g


def barrel(mats):
    """Breech stub, sleeve, tube, muzzle; bored.  The stub ends exactly on the
    trunnion, which is where `barrel_recoil.trunnion()` puts the pivot (the
    breech end of the tube on the bore), so the pipeline lays the gun about
    the same axis the model was built for."""
    g = Group(mats)
    bm = new_bm()
    y0 = BLOCK["y_front"]       # the sleeve starts on the block's face
    tb = TRUNNION[0] - y0
    prof = [(tb, 0.0), (tb, 0.040), (0.0, 0.040),
            (0.0, 0.049), (-0.062, 0.049), (-0.068, 0.041), (-0.174, 0.041),
            (-0.178, 0.0455), (-0.2487, 0.0455), (-0.2527, 0.0425), (-0.2527, 0.0300),
            (-0.2467, 0.0270), (-0.2467, 0.0155), (-0.1527, 0.0155), (-0.1527, 0.0)]
    lathe(bm, prof, GUN, seg=64, axis="Y", center=(0, y0, GUN_Z), closed=False)
    g.add(bm, bevel=(0.0015, 2, 30))
    return g
