"""More kinds of tree than the oak, from a seed: a look to choose by, and the
game model in the oak's contract.

`tree_gen` grows one kind: a broadleaf oak whose crown is puffs on an
ellipsoid shell and whose branches are routed to them. This module grows six
others (docs/tree-species-plan.md):

    pine     - a tall bare trunk, orange above, flat pads of needles at the top
    spruce   - a cone of drooping tiers from near the ground to a spike
    birch    - a thin white trunk with black dashes, a light open crown
    yucca    - the Joshua tree: a trunk forking into arms, a spiky rosette on each
    palm     - a ringed, bowed trunk under a head of combed fronds
    acacia   - a short trunk spreading into limbs under a flat umbrella crown

**A kind is a spec, built two ways.** Each kind lays out its tree once - wood
chains, crown cores, sheets of leaf it carries as geometry, twigs and whose
core each grows in - and two assemblers build that: `look` (the sprite's light
and line, for the sheet the kinds were chosen by) and `game`, which is the
oak's game model to the letter: the same `TreeGame.*` materials, a core per
crown mass carrying leaves by `tree_gen.clump_group`, the twigs hidden in the
cores and shown by fire, a root plate, and `tree_gen.bake` to one mesh. So the
oak's back half - `bake`, `export_glb`, `glb_burn`, `fall_spec`, the check
sheets - takes a kind as it takes an oak, and the board tells them apart only
by `tree.json`.

**Burn owners.** Cores and sheets share one numbering: owner `o`'s threshold
is `tree_gen._burn_at(seed, "core", o)`; a sheet's pieces burn between
`BURN["leaf_from"]` and it, a twig shows within `twig_about_core` of its
owner's (`tree_gen._twig_burn`). A frond or a rosette is a puff in all but
shape.

**The kind's colours ride on the oak's material names.** The board finds a
part's role by its material's name, so a kind is built with `TreeGame.*`
materials of its own colours: `game_materials` remakes them, and
`tree_gen.reset()` must run before an oak is built again in the same Blender.

    import importlib, tree_species as ts; importlib.reload(ts)
    ts.sheet(["pine", "spruce", "birch", "yucca", "palm", "acacia"], seeds=(1, 2))
    ts.check("pine", 1)       # look, game model, burn steps; tris and the .glb's burn ranges
"""

import math
import os
import random

import bmesh
import bpy
import numpy as np
from mathutils import Quaternion, Vector, noise

import tree_gen as tg

COL = "TreeSpecies"
OUT = os.path.join(tg.OUT, "species")
INK_WOOD = 0.04
INK_LEAF = 0.03

# Colours by kind, sRGB, (dark, mid, light): the look's three tones; the game
# takes `light` (Toon's lit tone is the albedo) and a palette dark -> light.
PALETTES = {
    "pine": {"bark": ((0.28, 0.19, 0.14), (0.44, 0.32, 0.24), (0.58, 0.44, 0.33)),
             "top": ((0.45, 0.20, 0.10), (0.70, 0.36, 0.17), (0.86, 0.50, 0.26)),
             "leaf": ((0.13, 0.25, 0.17), (0.24, 0.42, 0.24), (0.42, 0.60, 0.30))},
    "spruce": {"bark": ((0.18, 0.12, 0.09), (0.30, 0.21, 0.15), (0.40, 0.29, 0.21)),
               "leaf": ((0.08, 0.19, 0.15), (0.15, 0.32, 0.23), (0.27, 0.47, 0.30))},
    "birch": {"bark": ((0.55, 0.55, 0.52), (0.82, 0.81, 0.76), (0.95, 0.94, 0.90)),
              "leaf": ((0.27, 0.40, 0.12), (0.45, 0.60, 0.20), (0.66, 0.77, 0.32))},
    "yucca": {"bark": ((0.25, 0.20, 0.16), (0.40, 0.33, 0.26), (0.55, 0.47, 0.37)),
              "core": ((0.30, 0.24, 0.15), (0.48, 0.39, 0.24), (0.62, 0.52, 0.33)),
              "leaf": ((0.24, 0.32, 0.12), (0.40, 0.50, 0.20), (0.60, 0.66, 0.32))},
    "palm": {"bark": ((0.33, 0.27, 0.20), (0.52, 0.44, 0.33), (0.68, 0.60, 0.46)),
             "core": ((0.20, 0.15, 0.06), (0.36, 0.28, 0.10), (0.50, 0.40, 0.16)),
             "leaf": ((0.14, 0.32, 0.10), (0.27, 0.52, 0.15), (0.48, 0.70, 0.24))},
    "acacia": {"bark": ((0.24, 0.19, 0.16), (0.38, 0.31, 0.26), (0.52, 0.44, 0.37)),
               "leaf": ((0.27, 0.34, 0.14), (0.44, 0.52, 0.22), (0.64, 0.68, 0.36))},
}

# The game model's leaf by kind, for `tree_gen.leaf_mesh` and `clump_group`:
# (sides, width of its length, size m, density per m2, tilt range). Needles are
# long and narrow and stand out of their pad; a birch leaf is small and broad.
LEAVES = {
    "pine": (4, 0.16, 0.55, 14.0, (0.5, 1.1)),
    "spruce": (4, 0.14, 0.40, 9.0, (0.3, 0.8)),
    "birch": (8, 0.80, 0.30, 18.0, (0.15, 0.50)),
    "acacia": (4, 0.70, 0.22, 26.0, (0.10, 0.35)),
}

# Where the trunk ends and the crown begins, as a share of height
# (`tree_gen.fall_spec` lies the tree on the bark below it), and the collar's
# radius (the fall's hinge and the root plate's size).
FRAME = {
    "pine": {"crown_base": 0.60, "base_radius": 0.36},
    "spruce": {"crown_base": 0.90, "base_radius": 0.30},
    "birch": {"crown_base": 0.35, "base_radius": 0.22},
    "yucca": {"crown_base": 0.40, "base_radius": 0.50},
    "palm": {"crown_base": 0.92, "base_radius": 0.42},
    "acacia": {"crown_base": 0.25, "base_radius": 0.32},
}

# The fused wood: a chain thinner than this is lost by the voxel remesh, so
# the spec clamps the wood's radii to it and leaves finer wood to the twigs.
MIN_WOOD = 0.045


def _lin(c):
    """sRGB 0-1 to linear."""
    return tuple(((x + 0.055) / 1.055) ** 2.4 if x > 0.04045 else x / 12.92 for x in c)


# ------------------------------------------------------------------ spec ----

class Spec:
    """One tree's layout, with nothing built: wood chains `(pts, radii, tone)`,
    cores, sheets and twigs `(pts, radii, owner)`. Cores and sheets share the
    owner numbering (`owner`)."""

    def __init__(self, kind, seed):
        self.kind, self.seed = kind, seed
        self.rng = random.Random(seed)
        self.wood, self.cores, self.sheets, self.twigs = [], [], [], []
        self.owners = 0

    def owner(self):
        self.owners += 1
        return self.owners - 1

    def chain(self, pts, radii, tone="bark"):
        self.wood.append((pts, [max(r, MIN_WOOD) for r in radii], tone))

    def core(self, c, make, leaf=True, shade=None):
        """A crown mass at c, built by make(bm) round the origin."""
        o = self.owner()
        self.cores.append({"c": c, "make": make, "leaf": leaf, "owner": o})
        return o

    def sheet(self, c, make):
        """Leaf the tree carries as geometry, round c: make(bm, piece) builds
        its pieces, each calling piece() once per leaf it makes."""
        o = self.owner()
        self.sheets.append({"c": c, "make": make, "owner": o})
        return o

    def twig(self, pts, radii, owner):
        self.twigs.append((pts, radii, owner))


def _frames(pts):
    out, u = [], None
    for i, p in enumerate(pts):
        t = (pts[min(i + 1, len(pts) - 1)] - pts[max(i - 1, 0)]).normalized()
        if u is None:
            u = t.orthogonal().normalized()
        u = (u - t * u.dot(t)).normalized()
        out.append((t, u, t.cross(u)))
    return out


def tube(bm, pts, radii, sides=10):
    """A closed tube along pts into bm, a cone at each end; returns its faces."""
    fr = _frames(pts)
    rings, faces = [], []
    for p, r, (t, u, v) in zip(pts, radii, fr):
        rings.append([bm.verts.new(p + (u * math.cos(a) + v * math.sin(a)) * r)
                      for a in (2 * math.pi * k / sides for k in range(sides))])
    for a, b in zip(rings, rings[1:]):
        for k in range(sides):
            faces.append(bm.faces.new((a[k], a[(k + 1) % sides], b[(k + 1) % sides], b[k])))
    tip = bm.verts.new(pts[-1] + fr[-1][0] * radii[-1] * 0.6)
    for k in range(sides):
        faces.append(bm.faces.new((rings[-1][k], rings[-1][(k + 1) % sides], tip)))
    foot = bm.verts.new(pts[0] - fr[0][0] * radii[0] * 0.3)
    for k in range(sides):
        faces.append(bm.faces.new((rings[0][(k + 1) % sides], rings[0][k], foot)))
    return faces


def bent(p0, p1, rng, bow=0.12, droop=0.0, n=8):
    v = p1 - p0
    side = v.cross(Vector((0, 0, 1)))
    if side.length < 1e-3:
        side = Vector((1, 0, 0))
    side = side.normalized() * rng.uniform(-1, 1) * bow * v.length
    return [p0.lerp(p1, k / n) + side * math.sin(math.pi * k / n)
            - Vector((0, 0, droop * v.length * math.sin(math.pi * k / n))) for k in range(n + 1)]


def puff_maker(axes, rng, lumps=0.22, subdiv=2):
    """An icosphere stretched to `axes`, pushed out by noise, round the origin."""
    sv = Vector((rng.uniform(0, 99), rng.uniform(0, 99), rng.uniform(0, 99)))

    def make(bm):
        geo = bmesh.ops.create_icosphere(bm, subdivisions=subdiv, radius=1.0)
        for v in geo["verts"]:
            d = v.co.normalized()
            k = 1.0 + lumps * noise.noise(d * 2.3 + sv) + 0.5 * lumps * noise.noise(d * 5.1 + sv)
            v.co = Vector((d.x * axes[0], d.y * axes[1], d.z * axes[2])) * k
    return make


def inside(axes, rng, share=0.7):
    """A point inside the ellipsoid `axes`, within `share` of it."""
    while True:
        d = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-1, 1)))
        if d.length <= 1.0:
            return Vector((d.x * axes[0], d.y * axes[1], d.z * axes[2])) * share


def twigs_in(S, base, c, axes, o, rng, n=3):
    """The twigs of a crown mass at c: the branch going on from `base` to the
    middle, and `n` short ones from there, all inside the mass so the live
    tree hides them."""
    S.twig([base, base.lerp(c, 0.5), c], [0.04, 0.03, 0.022], o)
    for _ in range(n):
        S.twig([c, c + inside(axes, rng, 0.55)], [0.02, 0.012], o)


# ----------------------------------------------------------------- kinds ----

def pine(S):
    """Scots pine: a trunk bare to two thirds, grey below and orange above,
    a few limbs out of the top third, each ending in flat pads of needles."""
    rng = S.rng
    H = rng.uniform(11.0, 13.5)
    lean = Vector((rng.uniform(-0.6, 0.6), rng.uniform(-0.3, 0.3), 0))
    trunk = [lean * (k / 12) ** 2 + Vector((0, 0, H * 0.92 * k / 12)) for k in range(13)]
    rad = [0.36 * (1 - 0.75 * k / 12) for k in range(13)]
    S.chain(trunk[:6], rad[:6], "bark")
    S.chain(trunk[5:], rad[5:], "top")
    for i in range(rng.randint(4, 6)):
        z = H * rng.uniform(0.62, 0.86)
        k = min(11, int(z / (H * 0.92) * 12))
        p0 = trunk[k]
        ang = i * 2.4 + rng.uniform(-0.4, 0.4)
        L = rng.uniform(1.4, 2.6) * (1.1 - 0.4 * (z / H - 0.62) / 0.24)
        p1 = p0 + Vector((math.cos(ang) * L, math.sin(ang) * L * 0.8, rng.uniform(0.3, 0.9)))
        S.chain(bent(p0, p1, rng, 0.1, 0.0, 6), [0.13 - 0.08 * j / 6 for j in range(7)], "top")
        for _ in range(rng.randint(2, 3)):
            c = p1 + Vector((rng.uniform(-0.5, 0.5), rng.uniform(-0.5, 0.5), rng.uniform(-0.1, 0.25)))
            axes = (rng.uniform(0.9, 1.3), rng.uniform(0.8, 1.1), rng.uniform(0.38, 0.5))
            o = S.core(c, puff_maker(axes, rng))
            twigs_in(S, p1, c, axes, o, rng)
    for _ in range(3):
        c = trunk[-1] + Vector((rng.uniform(-0.6, 0.6), rng.uniform(-0.4, 0.4), rng.uniform(-0.2, 0.4)))
        axes = (rng.uniform(0.9, 1.2), rng.uniform(0.8, 1.0), rng.uniform(0.45, 0.6))
        o = S.core(c, puff_maker(axes, rng))
        twigs_in(S, trunk[-1], c, axes, o, rng)
    return H


def tier_maker(r, h, k, spin, rng):
    """A spruce tier round the origin: a cone from its apex at h to a rim of
    2k points - long tips drooping, short ones between - closed underneath by
    a shallower cone up to 0.35 h, so the tier is a solid for the ink."""
    jit = [(rng.uniform(-0.05, 0.05), rng.uniform(0.95, 1.08), rng.uniform(0.66, 0.76)) for _ in range(2 * k)]

    def make(bm):
        apex = bm.verts.new(Vector((0, 0, h)))
        under = bm.verts.new(Vector((0, 0, 0.35 * h)))
        ring = []
        for j in range(2 * k):
            a = spin + math.pi * j / k + jit[j][0]
            tip = j % 2 == 0
            rr = r * (jit[j][1] if tip else jit[j][2])
            ring.append(bm.verts.new(Vector((math.cos(a) * rr, math.sin(a) * rr, -0.28 * h if tip else 0.02 * h))))
        for j in range(2 * k):
            bm.faces.new((ring[j], ring[(j + 1) % (2 * k)], apex))
            bm.faces.new((ring[(j + 1) % (2 * k)], ring[j], under))
    return make


def spruce(S):
    """Spruce: a cone of drooping tiers, widest near the ground, a spike on
    top. The trunk runs up the middle to the top, hidden by the tiers, and
    each tier hides a whorl of bare branches - the burnt spruce's outline."""
    rng = S.rng
    H = rng.uniform(9.5, 11.5)
    R = rng.uniform(2.4, 2.9)
    S.chain([Vector((0, 0, 0)), Vector((0, 0, H * 0.5)), Vector((0, 0, H + 0.2))], [0.3, 0.18, 0.05])
    n = rng.randint(7, 9)
    z0 = rng.uniform(0.7, 1.1)
    for i in range(n):
        f = i / (n - 1)
        z = z0 + (H - 1.6 - z0) * f
        r = R * (1 - 0.88 * f) * rng.uniform(0.92, 1.06)
        h = 1.25 + 0.9 * (1 - f)
        c = Vector((rng.uniform(-0.05, 0.05), 0, z))
        o = S.core(c, tier_maker(r, h, rng.choice((9, 10, 11)), rng.uniform(0, math.pi), rng))
        for j in range(rng.randint(5, 6)):
            a = 2 * math.pi * j / 6 + rng.uniform(-0.3, 0.3)
            d = Vector((math.cos(a), math.sin(a), 0))
            S.twig([c + Vector((0, 0, 0.45 * h)), c + d * 0.4 * r + Vector((0, 0, 0.2 * h)),
                    c + d * 0.75 * r + Vector((0, 0, -0.05 * h))], [0.035, 0.025, 0.012], o)
    return H


def birch(S):
    """Birch: one or two thin white trunks, leaning, branches arching up and
    out, the crown hanging in small lumps off their ends."""
    rng = S.rng
    H = rng.uniform(9.0, 11.0)
    stems = 2 if rng.random() < 0.5 else 1
    for s in range(stems):
        lean = Vector((rng.uniform(-1.0, 1.0) + (0.9 if s else 0), rng.uniform(-0.4, 0.4), 0))
        h = H * (1.0 if s == 0 else 0.82)
        trunk = [lean * (k / 10) ** 1.5 + Vector((0, 0, h * k / 10)) for k in range(11)]
        S.chain(trunk, [0.2 * (1 - 0.75 * k / 10) for k in range(11)])
        for i in range(rng.randint(8, 10)):
            k = rng.randint(3, 9)
            p0 = trunk[k]
            ang = i * 2.4 + rng.uniform(-0.3, 0.3)
            L = rng.uniform(1.6, 2.6) * (1.2 - k / 12)
            p1 = p0 + Vector((math.cos(ang) * L, math.sin(ang) * L * 0.7, rng.uniform(0.6, 1.3)))
            S.chain(bent(p0, p1, rng, 0.1, -0.08, 5), [0.07 - 0.03 * j / 5 for j in range(6)])
            for q in range(rng.randint(3, 4)):
                c = p0.lerp(p1, 0.55 + 0.15 * q) + Vector((rng.uniform(-0.4, 0.4), rng.uniform(-0.3, 0.3),
                                                            rng.uniform(-0.9, -0.1)))
                axes = (rng.uniform(0.45, 0.65), rng.uniform(0.4, 0.55), rng.uniform(0.55, 0.8))
                o = S.core(c, puff_maker(axes, rng, 0.3))
                twigs_in(S, p0.lerp(p1, 0.5 + 0.15 * q), c, axes, o, rng, 2)
        for _ in range(3):
            c = trunk[-1] + Vector((rng.uniform(-0.6, 0.6), rng.uniform(-0.4, 0.4), rng.uniform(-0.8, 0.0)))
            o = S.core(c, puff_maker((0.7, 0.6, 0.7), rng, 0.3))
            twigs_in(S, trunk[-2], c, (0.7, 0.6, 0.7), o, rng, 2)
    return H


def rosette_maker(rng):
    """A ball of stiff spikes round the origin, mostly up and out."""
    spikes = [(Vector((rng.gauss(0, 1), rng.gauss(0, 1), abs(rng.gauss(0, 1)) * 1.2 + 0.05)).normalized(),
               rng.uniform(0.6, 0.85), rng.uniform(0, math.pi)) for _ in range(60)]

    def make(bm, piece):
        for d, L, spin in spikes:
            s = d.orthogonal().normalized()
            s.rotate(Quaternion(d, spin))
            n = d.cross(s)
            p = d * 0.08
            w = 0.05
            base = [bm.verts.new(p + s * w), bm.verts.new(p + n * w * 0.3),
                    bm.verts.new(p - s * w), bm.verts.new(p - n * w * 0.3)]
            tip = bm.verts.new(p + d * L)
            fs = [bm.faces.new((base[k], base[(k + 1) % 4], tip)) for k in range(4)]
            fs.append(bm.faces.new(list(reversed(base))))
            piece(fs, 0.5 + 0.5 * d.z)
    return make


def yucca(S):
    """Joshua tree: a shaggy trunk forking into arms that turn up at the
    elbow, one or two forks deep; on each arm's end a ball of stiff spikes
    over a skirt of dead ones."""
    rng = S.rng
    h0 = rng.uniform(2.6, 3.4)
    S.chain([Vector((0, 0, 0)), Vector((0, 0, h0 * 0.5)), Vector((rng.uniform(-0.1, 0.1), 0, h0))],
            [0.5, 0.4, 0.36])
    top = [0.0]

    def arm(p, d, r, depth):
        L = rng.uniform(1.3, 2.0)
        side = Vector((d.x, d.y, 0))
        elbow = p + side.normalized() * L * 0.7 + Vector((0, 0, L * 0.35)) if side.length > 1e-3 \
            else p + Vector((0, 0, L))
        end = elbow + Vector((side.x * 0.15, side.y * 0.15, L * 0.55))
        S.chain([p, p.lerp(elbow, 0.5), elbow, elbow.lerp(end, 0.5), end],
                [r, r * 0.92, r * 0.85, r * 0.8, r * 0.76])
        if depth == 0 or rng.random() < 0.15 * depth:
            S.core(end - Vector((0, 0, 0.2)), puff_maker((r * 1.6, r * 1.6, 0.4), rng, 0.15, 1), leaf=False)
            S.sheet(end + Vector((0, 0, 0.12)), rosette_maker(rng))
            top[0] = max(top[0], end.z + 0.9)
            return
        n = 2 if rng.random() < 0.7 else 3
        a0 = rng.uniform(0, 2 * math.pi)
        for k in range(n):
            a = a0 + 2 * math.pi * k / n + rng.uniform(-0.4, 0.4)
            arm(end, Vector((math.cos(a), math.sin(a) * 0.8, 0.6)), r * 0.78, depth - 1)

    n = 2 if rng.random() < 0.6 else 3
    a0 = rng.uniform(0, 2 * math.pi)
    for k in range(n):
        a = a0 + 2 * math.pi * k / n
        arm(Vector((0, 0, h0)), Vector((math.cos(a), math.sin(a) * 0.8, 0.5)), 0.3, rng.choice((1, 2)))
    return top[0]


def frond_maker(spine, rng):
    """One frond along `spine`, relative to the sheet's centre: leaflets hang
    off both sides in a comb - a tip, then a notch, down its length - widest
    at the middle, folded down from the rib so the frond reads as a roof."""
    def make(bm, piece):
        fr = _frames(spine)
        m = len(spine) - 1
        rib, left, right = [], [], []
        for k, (p, (t, _, _)) in enumerate(zip(spine, fr)):
            side = t.cross(Vector((0, 0, 1)))
            side = side.normalized() if side.length > 1e-3 else Vector((1, 0, 0))
            wmax = 0.75 * math.sin(math.pi * min(1.0, (k + 0.5) / m)) ** 0.6
            for sgn, edge in ((1, left), (-1, right)):
                for sub in (0, 1):
                    f = 1.0 if sub == 0 else 0.45
                    edge.append(bm.verts.new(p + t * (0.08 * sub) + side * sgn * wmax * f
                                             - Vector((0, 0, 0.45 * wmax * f))))
            rib.append(bm.verts.new(p + Vector((0, 0, 0.04))))
        for edge, flip in ((left, False), (right, True)):
            for k in range(m):
                a, b = rib[k], rib[k + 1]
                e0, e1, e2 = edge[2 * k], edge[2 * k + 1], edge[2 * k + 2]
                tris = [(a, e0, e1), (a, e1, b), (b, e1, e2)]
                fs = [bm.faces.new(tuple(reversed(t)) if flip else t) for t in tris]
                piece(fs, 0.35 + 0.4 * (1 - k / m))
    return make


def palm(S):
    """Coconut palm: a ringed trunk bowed to one side, and a head of a dozen
    fronds - each an arching rib with its leaflets combed down either side.
    The rib is a twig: the burnt palm stands with bare ribs."""
    rng = S.rng
    H = rng.uniform(8.0, 9.5)
    bow = Vector((rng.uniform(1.0, 2.0) * rng.choice((-1, 1)), rng.uniform(-0.4, 0.4), 0))
    trunk = [bow * (k / 12) ** 1.8 + Vector((0, 0, H * k / 12)) for k in range(13)]
    S.chain(trunk, [0.3 * (1 - 0.35 * k / 12) + (0.12 if k == 0 else 0) for k in range(13)])
    head = trunk[-1]
    up = (trunk[-1] - trunk[-2]).normalized()
    for k in range(rng.randint(3, 5)):
        a = rng.uniform(0, 2 * math.pi)
        S.core(head + Vector((math.cos(a) * 0.3, math.sin(a) * 0.3, -0.25)),
               puff_maker((0.17, 0.17, 0.2), rng, 0.05, 1), leaf=False)
    n = rng.randint(11, 14)
    for i in range(n):
        az = 2 * math.pi * i / n + rng.uniform(-0.2, 0.2)
        rise = rng.uniform(0.2, 0.9)
        L = rng.uniform(3.0, 3.8)
        out = Vector((math.cos(az), math.sin(az), 0))
        spine = []
        for k in range(15):
            t = k / 14
            ang = rise * (1 - t) - 1.1 * t * t
            spine.append(up * 0.15 + out * (L * t * math.cos(ang * 0.6))
                         + Vector((0, 0, L * 0.45 * (math.sin(rise) * t - 1.0 * t * t))))
        o = S.sheet(head, frond_maker(spine, rng))
        S.twig([head + p for p in spine], [0.06 - 0.045 * k / 14 for k in range(15)], o)
    return H + 0.5


def acacia(S):
    """Umbrella thorn: a short, often crooked trunk splitting into three or
    four limbs that spread out wide and level off, under a flat crown of
    broad, thin layers - the savanna's table."""
    rng = S.rng
    H = rng.uniform(5.5, 6.8)
    Rc = rng.uniform(3.0, 3.8)
    fork = Vector((rng.uniform(-0.3, 0.3), 0, rng.uniform(1.3, 1.8)))
    S.chain(bent(Vector((0, 0, 0)), fork, rng, 0.15, 0, 5), [0.32, 0.3, 0.28, 0.26, 0.25, 0.24])
    n = rng.randint(3, 4)
    a0 = rng.uniform(0, 2 * math.pi)
    ends = []
    for k in range(n):
        a = a0 + 2 * math.pi * k / n + rng.uniform(-0.3, 0.3)
        r = Rc * rng.uniform(0.45, 0.75)
        mid = fork + Vector((math.cos(a) * r * 0.55, math.sin(a) * r * 0.55 * 0.8, (H - 1.0 - fork.z) * 0.6))
        end = fork + Vector((math.cos(a) * r, math.sin(a) * r * 0.8, H - 1.0 - fork.z))
        S.chain(bent(fork, mid, rng, 0.15, 0, 4) + bent(mid, end, rng, 0.1, 0, 4)[1:],
                [0.2 - 0.12 * j / 8 for j in range(9)])
        ends.append(end)
    for i in range(rng.randint(11, 14)):
        if i < len(ends):
            c = ends[i] + Vector((0, 0, 0.35))
            base = ends[i]
        else:
            a = rng.uniform(0, 2 * math.pi)
            r = Rc * math.sqrt(rng.uniform(0.0, 0.85))
            c = Vector((math.cos(a) * r, math.sin(a) * r * 0.8, H - 0.6 + rng.uniform(-0.25, 0.3)))
            base = min(ends, key=lambda e: (e - c).length)
        axes = (rng.uniform(1.1, 1.6), rng.uniform(1.0, 1.4), rng.uniform(0.28, 0.4))
        o = S.core(c, puff_maker(axes, rng, 0.18))
        # flat: the table's twigs fan out level inside its layer
        S.twig([base, c], [0.04, 0.025], o)
        for _ in range(3):
            p = inside(axes, rng, 0.6)
            S.twig([c, c + Vector((p.x, p.y, 0.0))], [0.022, 0.012], o)
    return H


KINDS = {"pine": pine, "spruce": spruce, "birch": birch, "yucca": yucca, "palm": palm, "acacia": acacia}


def spec(kind, seed):
    S = Spec(kind, seed)
    S.height = KINDS[kind](S)
    return S


# ------------------------------------------------------------------ look ----

def toon_mat(name, tones, bands=None):
    """Emitted colour from the sun's three tones (tree_gen's `_toon`): its
    0.45 / 0.72 / 1.0 become the kind's dark, mid and light. `bands` puts a
    birch's dashes or a palm's rings over it."""
    dark, mid, light = tones
    m, N, L = tg._new_mat(name)
    t = tg._toon(N, L, (-1200, 0))
    bw = N.new("ShaderNodeRGBToBW")
    bw.location = (-700, 0)
    L.new(t.outputs["Color"], bw.inputs[0])
    pal = tg._ramp(N, [(0.0, _lin(dark)), (0.6, _lin(mid)), (0.86, _lin(light))], "CONSTANT", (-500, 0))
    L.new(bw.outputs[0], pal.inputs[0])
    out = pal.outputs["Color"]
    if bands == "birch":
        b = tg._ramp(N, [(0.0, (1, 1, 1)), (0.62, (0.06, 0.06, 0.06))], "CONSTANT", (-500, -600))
        L.new(tg._grain(N, L, (2.2, 2.2, 22.0), 1.6, 2, (-800, -600)), b.inputs[0])
        out = tg._multiply(N, L, out, b.outputs["Color"], (0, -200))
    elif bands == "palm":
        w = N.new("ShaderNodeTexWave")
        w.location = (-800, -600)
        w.wave_type = "BANDS"
        w.bands_direction = "Z"
        w.inputs["Scale"].default_value = 3.2
        w.inputs["Distortion"].default_value = 1.5
        tc = N.new("ShaderNodeTexCoord")
        tc.location = (-1000, -600)
        L.new(tc.outputs["Object"], w.inputs["Vector"])
        b = tg._ramp(N, [(0.0, (0.62, 0.62, 0.62)), (0.25, (1, 1, 1))], "CONSTANT", (-500, -600))
        L.new(w.outputs["Fac"], b.inputs[0])
        out = tg._multiply(N, L, out, b.outputs["Color"], (0, -200))
    tg._emit(N, L, out, (200, 0))
    return m


def _collection():
    col = bpy.data.collections.get(COL)
    if col is None:
        col = bpy.data.collections.new(COL)
        bpy.context.scene.collection.children.link(col)
    return col


def _root(name, offset):
    for o in [o for o in bpy.data.objects if o.name.startswith(name + ".")]:
        bpy.data.objects.remove(o, do_unlink=True)
    w = bpy.data.objects.new(name + ".World", None)
    _collection().objects.link(w)
    w.location = offset
    return w


def _object(name, bm, mats, world, ink=None, smooth=True):
    me = bpy.data.meshes.new(name)
    bm.normal_update()
    bm.to_mesh(me)
    bm.free()
    for p in me.polygons:
        p.use_smooth = smooth
    ob = bpy.data.objects.new(name, me)
    _collection().objects.link(ob)
    ob.parent = world
    for m in mats:
        me.materials.append(m)
    if ink is not None:
        tg._add_ink(ob, ink[0], ink[1])
    return ob


def _shift(bm, n0, by):
    """Move the verts made since there were n0 by `by`. By index: a set of
    BMVerts taken before an op does not hold across it, and offsets landed on
    the earlier puffs again - the crown went 140 m up."""
    bm.verts.ensure_lookup_table()
    for i in range(n0, len(bm.verts)):
        bm.verts[i].co += by


def look(S, name, offset):
    """The kind as the sheet shows it: the sprite's light and line."""
    pal = PALETTES[S.kind]
    ink = tg._ink_mat("Sp.Ink")
    bands = {"birch": "birch", "palm": "palm"}.get(S.kind)
    mat = {t: toon_mat(f"Sp.{S.kind}.{t}", pal[t], bands if t == "bark" else None)
           for t in ("bark", "top") if t in pal}
    leaf = toon_mat(f"Sp.{S.kind}.leaf", pal["leaf"])
    core = toon_mat(f"Sp.{S.kind}.core", pal.get("core", pal["leaf"]))
    w = _root(name, offset)
    for tone in sorted({t for _, _, t in S.wood}):
        bm = bmesh.new()
        for pts, rs, t in S.wood:
            if t == tone:
                tube(bm, pts, rs)
        _object(f"{name}.Wood.{tone}", bm, [mat[tone]], w, (ink, INK_WOOD))
    for key, cores in (("leaf", [c for c in S.cores if c["leaf"]]), ("core", [c for c in S.cores if not c["leaf"]])):
        if not cores:
            continue
        bm = bmesh.new()
        for c in cores:
            n0 = len(bm.verts)
            c["make"](bm)
            _shift(bm, n0, c["c"])
        _object(f"{name}.Cores.{key}", bm, [leaf if key == "leaf" else core], w, (ink, INK_LEAF),
                smooth=S.kind != "spruce")
    if S.sheets:
        bm = bmesh.new()
        for sh in S.sheets:
            n0 = len(bm.verts)
            sh["make"](bm, lambda fs, tone: None)
            _shift(bm, n0, sh["c"])
        _object(f"{name}.Sheets", bm, [leaf], w, (ink, 0.012), smooth=False)
    return w


def sheet(kinds, seeds=(1, 2), spacing=20.0, px_per_m=48, size=(720, 720)):
    """Each kind and seed rendered alone, through one camera at the board's
    30 degrees and one scale, so heights compare across the pictures."""
    os.makedirs(OUT, exist_ok=True)
    cam = tg.rig()
    sc = bpy.context.scene
    sc.render.resolution_x, sc.render.resolution_y = size
    sc.render.resolution_percentage = 100
    cam.data.ortho_scale = max(size) / px_per_m
    rot = cam.rotation_euler.to_matrix()
    up, back = rot.col[1], rot.col[2]
    built = []
    for i, kind in enumerate(kinds):
        for j, seed in enumerate(seeds):
            name = f"Sp.{kind}.{seed}"
            built.append((name, look(spec(kind, seed), name, Vector((i * spacing, j * spacing, 0)))))
    files = []
    for name, w in built:
        keep = {w.name} | {o.name for o in w.children_recursive} | {"TreeGen.Sun", "TreeGen.Camera"}
        saved = {o.name: o.hide_render for o in bpy.data.objects}
        for o in bpy.data.objects:
            o.hide_render = o.name not in keep
        cam.location = w.location + up * (cam.data.ortho_scale * 0.5 - 1.6) + back * 60.0
        sc.render.filepath = os.path.join(OUT, name.replace("Sp.", "") + ".png")
        bpy.ops.render.render(write_still=True)
        for n, h in saved.items():
            bpy.data.objects[n].hide_render = h
        files.append(sc.render.filepath)
    return files


# ------------------------------------------------------------------ game ----

def _image(name, px):
    """An sRGB image from linear pixels (h, w, 3), packed so the .glb carries it."""
    h, w = px.shape[:2]
    img = bpy.data.images.get(name)
    if img is not None and tuple(img.size) != (w, h):
        bpy.data.images.remove(img)
        img = None
    if img is None:
        img = bpy.data.images.new(name, w, h, alpha=False)
    out = np.ones((h, w, 4))
    out[..., :3] = tg._srgb(px)
    img.pixels.foreach_set(out.astype(np.float32).ravel())
    os.makedirs(OUT, exist_ok=True)
    img.filepath_raw = os.path.join(OUT, name + ".png")
    img.file_format = "PNG"
    img.save()
    img.pack()
    return img


def leaf_palette(kind, h=64):
    """V = a leaf's tone, dark .. light, as `tree_gen.palette_image` lays it;
    no rim columns - the look is "C", one tone to the leaf's edge."""
    dark, mid, light = (np.array(_lin(c)) for c in PALETTES[kind]["leaf"])
    px = np.zeros((h, 8, 3))
    for y in range(h):
        t = (y + 0.5) / h
        c = dark + (mid - dark) * min(1.0, t / 0.5) if t < 0.5 else mid + (light - mid) * (t - 0.5) / 0.5
        px[y, :, :] = tg._board(c)
    return _image(f"Sp.{kind}.LeafPalette", px)


def bark_strip(kind, h=256):
    """The bark up the trunk, V = height over the tree's: the game model's
    bark is one material, and what differs up it - the pine's orange top, the
    birch's dashes, the palm's rings - is this strip, read by `UVMap` V
    (`_bark_uv`). The light tone; Toon brings the shade."""
    p = PALETTES[kind]
    base = np.array(_lin(p["bark"][2]))
    v = (np.arange(h) + 0.5) / h
    px = np.tile(base, (h, 1))
    rng = np.random.default_rng(7)
    if kind == "pine":
        top = np.array(_lin(p["top"][2]))
        t = np.clip((v - 0.40) / 0.12, 0, 1)[:, None]
        px = base * (1 - t) + top * t
    elif kind == "birch":
        # sparse black dashes, a few rows each, denser near the foot
        for y in range(h):
            if rng.random() < 0.10 + 0.15 * (1 - v[y]):
                px[y] = base * 0.12
    elif kind == "palm":
        ring = (np.sin(v * 2 * math.pi * 40) > 0.55)[:, None]
        px = np.where(ring, base * 0.62, base)
    px = px * (0.92 + 0.16 * rng.random((h, 1)))
    return _image(f"Sp.{kind}.Bark", np.repeat(np.array([tg._board(c) for c in px])[:, None, :], 4, axis=1))


def game_materials(kind):
    """`TreeGame.*` in this kind's colours. Wipes tree_gen's cache first:
    its materials, scatter group and leaf go, and are made again here."""
    tg.reset()
    names = tg.MAT_NAMES["game"]
    p = PALETTES[kind]
    core = p.get("core", p["leaf"])[0]
    mats = {"leaf": tg._flat_mat(names["leaf"], (1, 1, 1), leaf_palette(kind)),
            "core": tg._flat_mat(names["core"], tg._board(_lin(core))),
            "bark": tg._flat_mat(names["bark"], (1, 1, 1), bark_strip(kind)),
            "char": tg._flat_mat(names["char"], tg._board(tg.GAME_CHAR)),
            "twig": tg._flat_mat(names["twig"], tg._board(_lin(p["bark"][1]))),
            "soil": tg._flat_mat(names["soil"], tg._board(tg.GAME_SOIL)),
            "root": tg._flat_mat(names["root"], tg._board(np.array(_lin(p["bark"][0])) * 0.8))}
    return mats


def _uvs(me, tone, burn):
    """`UVMap` (U 0.35, V the tone) and `Burn` (X the threshold) per face,
    `Bend` zero - tree_gen's three layers, in its order."""
    tg._uv_layers(me)
    lt = np.empty(len(me.polygons), dtype=np.int32)
    me.polygons.foreach_get("loop_total", lt)
    t = np.repeat(np.asarray(tone, dtype=np.float32), lt)
    b = np.repeat(np.asarray(burn, dtype=np.float32), lt)
    me.uv_layers["UVMap"].data.foreach_set("uv", np.column_stack([np.full_like(t, 0.35), t]).ravel())
    me.uv_layers["Burn"].data.foreach_set("uv", np.column_stack([b, np.zeros_like(b)]).ravel())


def game(S, name, offset):
    """The kind as the oak's game model is built (`tree_gen.build`, detail
    "game"): one fused wood, twigs with their thresholds, a core per crown
    mass carrying its leaves, sheets of leaf, the root plate."""
    seed = S.seed
    rng = random.Random(seed * 100_003 + 11)
    mats = game_materials(S.kind)
    cfg = {**tg.CONFIG, **tg.GAME_TIERS[tg.GAME_TIER], **tg.LOOKS[tg.GAME_LOOK], **FRAME[S.kind], "seed": seed}
    col = _collection()
    w = _root(name, offset)
    t0 = lambda o: tg._burn_at(seed, "core", o)  # noqa: E731

    # the crown's envelope, for the leaves' and cores' normals (bake)
    cs = [c["c"] for c in S.cores if c["leaf"]] + [sh["c"] for sh in S.sheets]
    if cs:
        lo = Vector((min(p.x for p in cs), min(p.y for p in cs), min(p.z for p in cs)))
        hi = Vector((max(p.x for p in cs), max(p.y for p in cs), max(p.z for p in cs)))
        w["crown_c"] = tuple((lo + hi) / 2)
        w["crown_axes"] = tuple(max(0.8, (hi[i] - lo[i]) / 2 + 0.8) for i in range(3))
        w["crown_mix"] = cfg["crown_mix"]

    # wood: one surface, as tree_gen's (remesh, smooth, collapse to its budget)
    bm = bmesh.new()
    for pts, rs, _ in S.wood:
        tube(bm, pts, rs, 12)
    trunk = _object(f"{name}.Trunk", bm, [mats["bark"]], w)
    rm = trunk.modifiers.new("Fuse", "REMESH")
    rm.mode = "VOXEL"
    rm.voxel_size = cfg["voxel"]
    rm.adaptivity = 0.0
    rm.use_smooth_shade = True
    sm = trunk.modifiers.new("Smooth", "SMOOTH")
    sm.factor = 0.5
    sm.iterations = 6
    tg._fit_tris(trunk, cfg["wood_tris"])

    # twigs: hidden in their owner's core, shown by fire
    if S.twigs:
        bm = bmesh.new()
        burn = []
        for ci, (pts, rs, o) in enumerate(S.twigs):
            fs = tube(bm, pts, rs, 4)
            burn += [tg._twig_burn(seed, o, ci)] * len(fs)
        tw = _object(f"{name}.Twigs", bm, [mats["twig"]], w)
        _uvs(tw.data, [0.0] * len(burn), burn)

    # cores, each with its leaves scattered by tree_gen's group
    ng = tg.clump_group("game", mats)
    ids = {it.name: it.identifier for it in ng.interface.items_tree if getattr(it, "in_out", None) == "INPUT"}
    leaf_ob = None
    if S.kind in LEAVES:
        sides, width, size, density, tilt = LEAVES[S.kind]
        leaf_ob = tg.leaf_mesh(f"Sp.{S.kind}.LeafGame", sides=sides, width=width, uv=True, rim=False)
        if leaf_ob.name not in col.objects:
            col.objects.link(leaf_ob)
        leaf_ob.hide_viewport = leaf_ob.hide_render = True
    crown_c = Vector(w.get("crown_c", (0, 0, S.height * 0.7)))
    for c in S.cores:
        bm = bmesh.new()
        c["make"](bm)
        ob = _object(f"{name}.Clump.{c['owner']:02d}", bm, [mats["core"]], w, smooth=S.kind != "spruce")
        ob.location = c["c"]
        g = ob.modifiers.new("Leaves", "NODES")
        g.node_group = ng
        inp = g.properties.inputs
        d = (c["c"] - crown_c).normalized() if (c["c"] - crown_c).length > 1e-3 else Vector((0, 0, 1))
        leafy = c["leaf"] and leaf_ob is not None
        getattr(inp, ids["Leaf"]).value = leaf_ob
        getattr(inp, ids["Density"]).value = density if leafy else 0.0
        getattr(inp, ids["Leaf Size"]).value = size if leafy else 0.1
        getattr(inp, ids["Seed"]).value = seed * 101 + c["owner"]
        getattr(inp, ids["Shade"]).value = max(-0.12, min(0.08, 0.07 * -d.y + 0.03 * d.z - 0.02))
        getattr(inp, ids["Min Up"]).value = cfg["min_up"]
        getattr(inp, ids["Core Burn"]).value = t0(c["owner"])
        base = cfg["tone_mean"] - 0.5 * cfg["tone_up"] - 0.5 * cfg["tone_rand"]
        lo_t, hi_t = tilt if leafy else cfg["tilt"]
        for k, v in (("Tone Up", cfg["tone_up"]), ("Tone Rand", cfg["tone_rand"]), ("Tone Base", base),
                     ("Tilt Min", lo_t), ("Tilt Max", hi_t)):
            getattr(inp, ids[k]).value = v

    # sheets: leaf as geometry, each piece burning between leaf_from and its owner
    for sh in S.sheets:
        bm = bmesh.new()
        tone, burn = [], []
        t = t0(sh["owner"])

        def piece(fs, up, t=t):
            b = rng.uniform(tg.BURN["leaf_from"], t)
            v = min(0.95, max(0.05, 0.25 + 0.3 * up + rng.uniform(-0.06, 0.06)))
            tone.extend([v] * len(fs))
            burn.extend([b] * len(fs))
        sh["make"](bm, piece)
        ob = _object(f"{name}.Clump.S{sh['owner']:02d}", bm, [mats["leaf"]], w, smooth=False)
        ob.location = sh["c"]
        _uvs(ob.data, tone, burn)

    tg._plate_object(name, cfg, mats, col, w)
    bpy.context.view_layer.update()
    return cfg


def _bark_uv(ob):
    """`UVMap` V on the bark = height over the model's: the bark strip reads it
    (the fused wood came out of the remesh with no UVs, `bake` zeroed them)."""
    me = ob.data
    names = [m.name if m else "" for m in me.materials]
    if tg.MAT_NAMES["game"]["bark"] not in names:
        return
    k = names.index(tg.MAT_NAMES["game"]["bark"])
    co = np.empty(len(me.vertices) * 3)
    me.vertices.foreach_get("co", co)
    z = co.reshape(-1, 3)[:, 2]
    H = float(z.max())
    mi = np.empty(len(me.polygons), dtype=np.int32)
    me.polygons.foreach_get("material_index", mi)
    lt = np.empty(len(me.polygons), dtype=np.int32)
    me.polygons.foreach_get("loop_total", lt)
    vi = np.empty(len(me.loops), dtype=np.int32)
    me.loops.foreach_get("vertex_index", vi)
    on = np.repeat(mi == k, lt)
    uv = np.empty(len(me.loops) * 2, dtype=np.float32)
    me.uv_layers["UVMap"].data.foreach_get("uv", uv)
    uv = uv.reshape(-1, 2)
    uv[on, 0] = 0.5
    uv[on, 1] = np.clip(z[vi[on]] / H, 0.002, 0.998)
    me.uv_layers["UVMap"].data.foreach_set("uv", uv.ravel())


def model_name(kind, seed):
    return f"{kind.capitalize()}_{seed:03d}"


def check(kind, seed, burns=(0.0, 0.25, 0.5, 0.75, 1.0)):
    """The kind's game model beside its look: built, baked to one mesh, written
    to `<OUT>/<Name>/tree.glb` (not published), and its sheet - the look, the
    model, the model at each burn. Returns tris, the .glb's burn ranges and
    the sheet's path."""
    name = model_name(kind, seed)
    folder = os.path.join(OUT, name)
    os.makedirs(folder, exist_ok=True)
    S = spec(kind, seed)
    lname = f"Sp.{kind}.{seed}"
    look(S, lname, Vector((0, -12, 0)))
    S = spec(kind, seed)            # the same stream again: the game's own copy
    gname = f"{name}_game"
    cfg = game(S, gname, Vector((0, 12, 0)))
    ob, tris = tg.bake(gname)
    _bark_uv(ob)
    glb = os.path.join(folder, "tree.glb")
    exported = tg.export_glb(ob, glb)
    live = os.path.join(folder, f"{name}.png")
    tg.sprites([lname], [live])
    shots = tg._mesh_shots(gname, [None] + list(burns), folder)
    sheet_png = tg.check_sheet([live] + shots, os.path.join(folder, "_check_game.png"))
    return {"name": name, "tris": tris, "export": exported, "burn": tg.glb_burn(glb),
            "sheet": sheet_png, "height": round(S.height, 2), "cfg": {k: cfg[k] for k in FRAME[kind]}}
