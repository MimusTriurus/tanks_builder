"""Rock looks on the relief board, rendered off-engine (draft, not the game).

One hex map, the same terraces as Relief.cs (warp, tight blur, swell), and a
rock built several ways over it. Cel steps, sun and colours as Stage3D's
relief shader; an ortho camera at 35 degrees, painted front to back like a
voxel-space renderer, so a wall gets its own pixels.

    python rocks.py            -> <repo>/out/rocks/<variant>.png and sheet.png
    python rocks.py tor mesa   -> only these
"""
import math
import os
import sys
import time

import numpy as np
from PIL import Image, ImageDraw, ImageFont

# the pictures go to the repository's out/ (not in git); the script lives here
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "out", "rocks")
os.makedirs(OUT, exist_ok=True)

R = 1.0            # hex circumradius: every length below is in these
L = 0.35           # one level's lift
GS = 1.0 / 30.0    # height raster step (the game's is 1/12)
PX = 110           # image px per R
ELEV = math.radians(35.0)
SUN = np.array([-0.40, 0.82, -0.41]); SUN /= np.linalg.norm(SUN)

COLS, ROWS = 7, 5
# (col, row) -> (kind, level); the rest is plain at level 0
MAP = {
    (2, 1): ("rock", 2), (3, 1): ("rock", 2), (4, 1): ("rock", 2),
    (2, 2): ("rock", 2), (3, 2): ("rock", 2),
    (5, 3): ("rock", 1),
    (0, 3): ("plain", 1), (1, 3): ("plain", 1), (1, 4): ("plain", 1),
    (5, 0): ("plain", 1), (6, 0): ("plain", 1),
    (4, 4): ("sand", 0),
}

GRASS = np.array([0.34, 0.43, 0.17]); GRASS_TIP = np.array([0.55, 0.61, 0.29])
SOIL = np.array([0.52, 0.42, 0.30]); SAND = np.array([0.86, 0.76, 0.55])
ROCK_GREY = np.array([0.58, 0.56, 0.54]); INK = np.array([0.10, 0.09, 0.08])


# ---------------------------------------------------------------- noise ----

def _hash(ix, iz, seed):
    h = (ix.astype(np.int64).view(np.uint64) * np.uint64(0x9E3779B97F4A7C15)
         ^ iz.astype(np.int64).view(np.uint64) * np.uint64(0xC2B2AE3D27D4EB4F)
         ^ np.uint64((seed * 0x165667B19E3779F9) & 0xFFFFFFFFFFFFFFFF))
    h ^= h >> np.uint64(29); h *= np.uint64(0xBF58476D1CE4E5B9)
    h ^= h >> np.uint64(32); h *= np.uint64(0x94D049BB133111EB)
    h ^= h >> np.uint64(29)
    return h


def rand01(ix, iz, seed):
    return (_hash(np.asarray(ix), np.asarray(iz), seed) >> np.uint64(40)).astype(np.float64) / float(1 << 24)


def perlin(x, z, seed=0):
    x0 = np.floor(x); z0 = np.floor(z)
    fx = x - x0; fz = z - z0
    ix = x0.astype(np.int64); iz = z0.astype(np.int64)

    def g(dx, dz):
        a = rand01(ix + dx, iz + dz, seed) * 2.0 * np.pi
        return np.cos(a) * (fx - dx) + np.sin(a) * (fz - dz)

    u = fx * fx * fx * (fx * (fx * 6 - 15) + 10)
    v = fz * fz * fz * (fz * (fz * 6 - 15) + 10)
    a = g(0, 0) + (g(1, 0) - g(0, 0)) * u
    b = g(0, 1) + (g(1, 1) - g(0, 1)) * u
    return (a + (b - a) * v) * 1.41


def fbm(x, z, seed=0, octaves=3):
    s, amp, tot = 0.0, 1.0, 0.0
    for o in range(octaves):
        s = s + amp * perlin(x * 2 ** o, z * 2 ** o, seed + 17 * o)
        tot += amp; amp *= 0.5
    return s / tot


def worley(x, z, size, seed=0):
    """F1, F2 and the nearest feature's (x, z, id) - cells of `size`."""
    cx = np.floor(x / size).astype(np.int64); cz = np.floor(z / size).astype(np.int64)
    f1 = np.full(x.shape, 1e9); f2 = np.full(x.shape, 1e9)
    fx = np.zeros(x.shape); fz = np.zeros(x.shape); fid = np.zeros(x.shape)
    for di in (-1, 0, 1):
        for dj in (-1, 0, 1):
            ix = cx + di; iz = cz + dj
            px = (ix + rand01(ix, iz, seed)) * size
            pz = (iz + rand01(ix, iz, seed + 1)) * size
            d = np.hypot(x - px, z - pz)
            near = d < f1
            f2 = np.where(near, f1, np.minimum(f2, d))
            f1 = np.where(near, d, f1)
            fx = np.where(near, px, fx); fz = np.where(near, pz, fz)
            fid = np.where(near, rand01(ix, iz, seed + 2), fid)
    return f1, f2, fx, fz, fid


def ss(a, b, x):
    t = np.clip((x - a) / (b - a), 0.0, 1.0)
    return t * t * (3 - 2 * t)


def blur(a, sigma_r):
    s = sigma_r / GS
    p = int(3 * s) + 2
    b = np.pad(a, p, mode="edge")
    f = np.fft.rfft2(b)
    fy = np.fft.fftfreq(b.shape[0])[:, None]; fx = np.fft.rfftfreq(b.shape[1])[None, :]
    f *= np.exp(-2 * np.pi ** 2 * s * s * (fx ** 2 + fy ** 2))
    return np.fft.irfft2(f, s=b.shape)[p:-p, p:-p]


# ---------------------------------------------------------------- board ----

def hex_at(x, z):
    """(col, row, plan distance to the nearest side), flat-top odd-q - the shader's."""
    q = (2.0 / 3.0) * x
    r = -x / 3.0 + 0.5773503 * z
    s = -q - r
    rq, rr, rs = np.round(q), np.round(r), np.round(s)
    dq, dr, ds = abs(rq - q), abs(rr - r), abs(rs - s)
    a = (dq > dr) & (dq > ds)
    b = ~a & (dr > ds)
    rq = np.where(a, -rr - rs, rq)
    rr = np.where(b, -rq - rs, rr)
    vx = x - 1.5 * rq; vz = z - 1.7320508 * (rr + rq * 0.5)
    m = np.maximum(abs(vz), np.maximum(abs(0.8660254 * vx + 0.5 * vz), abs(0.8660254 * vx - 0.5 * vz)))
    col = rq.astype(np.int64)
    row = rr.astype(np.int64) + (col - (col & 1)) // 2
    return col, row, 0.8660254 - m


def neighbours(c, r):
    odd = c & 1
    if odd:
        d = [(0, -1), (0, 1), (-1, 0), (-1, 1), (1, 0), (1, 1)]
    else:
        d = [(0, -1), (0, 1), (-1, -1), (-1, 0), (1, -1), (1, 0)]
    return [(c + a, r + b) for a, b in d]


def cell(c, r):
    return MAP.get((c, r), ("plain", 0))


class Board:
    def __init__(self):
        self.x0, self.x1 = -1.2, 1.5 * (COLS - 1) + 1.2
        self.z0, self.z1 = -1.1, 1.7320508 * (ROWS - 0.5) + 1.1
        xs = np.arange(self.x0, self.x1 + GS, GS); zs = np.arange(self.z0, self.z1 + GS, GS)
        self.X, self.Z = np.meshgrid(xs, zs)
        X, Z = self.X, self.Z
        col, row, self.side = hex_at(X, Z)
        self.on = (col >= 0) & (col < COLS) & (row >= 0) & (row < ROWS)
        kinds = {"plain": 0, "sand": 1, "rock": 2}
        self.kind = np.zeros(X.shape, int); self.level = np.zeros(X.shape)
        under = {}   # the ground a rock stands on: its lowest non-rock neighbour
        for (c, r), (k, lv) in MAP.items():
            if k == "rock":
                around = [cell(*n)[1] for n in neighbours(c, r) if cell(*n)[0] != "rock"]
                under[(c, r)] = min(around) if around else 0
        self.under = np.zeros(X.shape)
        for (c, r), (k, lv) in MAP.items():
            m = (col == c) & (row == r)
            self.kind[m] = kinds[k]; self.level[m] = lv
            if k == "rock":
                self.under[m] = under[(c, r)]
        # Terraces as Relief.Fill: the level read a little off the point, never
        # a higher neighbour's, then a tight blur and the swell.
        wx = perlin(X / 0.7, Z / 0.7, 3) * 0.14; wz = perlin(X / 0.7, Z / 0.7, 4) * 0.14
        wc, wr, _ = hex_at(X + wx, Z + wz)
        wl = self._level_of(wc, wr)
        self.raw_game = np.where(wl > self.level, self.level, wl) * L
        flat_level = np.where(self.kind == 2, self.under, self.level)
        wfl = np.where(self._kind_of(wc, wr) == 2, self._under_of(wc, wr), wl)
        self.raw_flat = np.where(wfl > flat_level, flat_level, wfl) * L
        swell = L * 0.04 * (0.6 * perlin(X / 1.6, Z / 1.6, 11) + 0.4 * perlin(X / 0.35, Z / 0.35, 12))
        self.h_game = blur(self.raw_game, 0.09) + swell
        self.h_flat = blur(self.raw_flat, 0.09) + swell
        self.mask = (self.kind == 2).astype(float)
        self.sand = blur((self.kind == 1).astype(float), 0.35)
        rock_raw = np.where(self.kind == 2, self.level * L, 0.0)
        self.cliff0 = blur(self.mask, 0.16)
        self.rock_top = blur(rock_raw, 0.16) / np.maximum(self.cliff0, 1e-3)
        self.cliff_soft = blur(self.mask, 0.32)

    def _lookup(self, c, r, f):
        out = np.zeros(c.shape)
        for (cc, rr), v in MAP.items():
            out[(c == cc) & (r == rr)] = f(cc, rr, v)
        return out

    def _level_of(self, c, r):
        return self._lookup(c, r, lambda cc, rr, v: v[1])

    def _kind_of(self, c, r):
        return self._lookup(c, r, lambda cc, rr, v: {"plain": 0, "sand": 1, "rock": 2}[v[0]])

    def _under_of(self, c, r):
        out = np.zeros(c.shape)
        for (cc, rr), (k, lv) in MAP.items():
            if k == "rock":
                around = [cell(*n)[1] for n in neighbours(cc, rr) if cell(*n)[0] != "rock"]
                out[(c == cc) & (r == rr)] = min(around) if around else 0
        return out

    def sample(self, f, x, z):
        """Bilinear sample of a raster field at world points."""
        fx = np.clip((x - self.X[0, 0]) / GS, 0, f.shape[1] - 1.001)
        fz = np.clip((z - self.Z[0, 0]) / GS, 0, f.shape[0] - 1.001)
        i = fx.astype(int); j = fz.astype(int); u = fx - i; v = fz - j
        return ((f[j, i] * (1 - u) + f[j, i + 1] * u) * (1 - v)
                + (f[j + 1, i] * (1 - u) + f[j + 1, i + 1] * u) * v)

    def coarse(self, f, step=0.0833):
        """A field as a raster of `step` would hold it: sampled there, read back bilinear."""
        xs = np.arange(self.x0, self.x1 + step, step); zs = np.arange(self.z0, self.z1 + step, step)
        cx, cz = np.meshgrid(xs, zs)
        c = self.sample(f, cx, cz)
        fx = np.clip((self.X - xs[0]) / step, 0, len(xs) - 1.001)
        fz = np.clip((self.Z - zs[0]) / step, 0, len(zs) - 1.001)
        i = fx.astype(int); j = fz.astype(int); u = fx - i; v = fz - j
        return ((c[j, i] * (1 - u) + c[j, i + 1] * u) * (1 - v)
                + (c[j + 1, i] * (1 - u) + c[j + 1, i + 1] * u) * v)

    def rocks(self):
        """Each rock - a connected group of rock cells - as a mask of the
        raster points in its cells."""
        cells = {c for c, (k, _) in MAP.items() if k == "rock"}
        col, row, _ = hex_at(self.X, self.Z)
        out = []
        while cells:
            todo = [cells.pop()]; group = set(todo)
            while todo:
                for n in neighbours(*todo.pop()):
                    if n in cells:
                        cells.remove(n); group.add(n); todo.append(n)
            m = np.zeros(self.X.shape, bool)
            for (c, r) in group:
                m |= (col == c) & (row == r)
            out.append(m)
        return out

    def torn(self, amount=0.6, scale=0.6, seed=2):
        c = self.cliff0
        t = np.clip(c - amount * np.abs(perlin(self.X / scale, self.Z / scale, seed)), 0, 1)
        return np.where((c > 0.01) & (c < 0.99), t, c)


# ------------------------------------------------------------- palettes ----

def strata(h, x, z, palette, thick, wobble=0.025, seed=40):
    """Layer colour by height, the layers waving a little across the rock."""
    t = (h + wobble * perlin(x / 0.9, z / 0.9, seed)) / thick
    k = np.floor(t).astype(np.int64)
    pick = (rand01(k, np.zeros_like(k), seed) * len(palette)).astype(int)
    pal = np.array(palette)
    return pal[np.clip(pick, 0, len(palette) - 1)]


def varnish(px, depth=0.38):
    """Desert varnish: dark streaks down the steep faces, from the top."""
    face = ss(0.55, 0.8, px["hl"])
    streak = perlin(px["x"] * 6.0 + px["z"] * 3.7, px["h"] * 0.9, 77) * 0.6 \
        + perlin(px["x"] * 13.0 - px["z"] * 9.1, px["h"] * 1.6, 78) * 0.4
    return 1.0 - depth * face * ss(0.05, 0.45, streak)


def grain(px, scale=0.12, seed=90):
    return 0.5 + 0.5 * fbm(px["x"] / scale, (px["z"] + px["h"]) / scale, seed, 2)


# ------------------------------------------------------------- variants ----

def v_current(b):
    """The game today: tight mask, edge torn by noise, ridged crags."""
    cliff = b.torn()
    ridge = 1.0 - np.abs(perlin(b.X * 1.7 / 0.35, b.Z * 1.7 / 0.35, 12))
    top = b.rock_top + L * 0.6 * (ridge * ridge - 0.3)
    wall = ss(0.5, 0.72, cliff)
    h = b.h_game + (np.maximum(b.h_game, top) - b.h_game) * wall
    # on the game's own raster (Relief.Step, 1/12 R), so the crags are as soft
    # as the mesh makes them
    near = ss(0.05, 0.3, b.cliff_soft)
    h = h + (b.coarse(h) - h) * near; cliff = cliff + (b.coarse(cliff) - cliff) * near
    fields = {"rock": ss(0.5, 0.62, cliff), "scree": np.zeros_like(h)}

    def paint(px):
        g = grain(px)
        return ROCK_GREY * (0.55 + 0.7 * g)[..., None]
    return h, fields, paint


def v_tor(b, seed=5):
    """Joshua Tree: piles of rounded granite, stacked two or three high."""
    rng = np.random.default_rng(seed)
    h = b.h_flat.copy()
    boulders = []
    sp = 0.40
    for gz in np.arange(b.z0, b.z1, sp):
        for gx in np.arange(b.x0, b.x1, sp):
            cx = gx + rng.uniform(-0.45, 0.45) * sp; cz = gz + rng.uniform(-0.45, 0.45) * sp
            c0 = b.sample(b.cliff0, np.array(cx), np.array(cz))
            if c0 < 0.6:
                continue
            top = float(b.sample(b.rock_top, np.array(cx), np.array(cz)))
            ground = float(b.sample(b.h_flat, np.array(cx), np.array(cz)))
            want = ground + top * rng.uniform(0.5, 1.15) * float(ss(0.6, 0.95, c0))
            z = ground - 0.08; k = 0; x, zz = cx, cz
            while True:
                r = rng.uniform(0.20, 0.40) * 0.8 ** k
                rz = r * rng.uniform(0.55, 0.8)
                cy = z + rz * 0.55
                boulders.append((x, zz, cy, r, rz, rng.uniform(0.65, 1.0), rng.uniform(0, np.pi)))
                z = cy + rz * 0.7; k += 1
                if cy + rz >= want or k > 3:
                    break
                x += rng.uniform(-0.5, 0.5) * r; zz += rng.uniform(-0.5, 0.5) * r
    for _ in range(40):   # loose stones on the rock's own cells
        cx = rng.uniform(b.x0, b.x1); cz = rng.uniform(b.z0, b.z1)
        if b.sample(b.cliff0, np.array(cx), np.array(cz)) < 0.55:
            continue
        g = float(b.sample(b.h_flat, np.array(cx), np.array(cz)))
        r = rng.uniform(0.06, 0.13)
        boulders.append((cx, cz, g, r, r * 0.8, rng.uniform(0.7, 1.0), rng.uniform(0, np.pi)))
    pexp = 2.2
    for (cx, cz, cy, r, rz, asp, th) in boulders:
        i0 = max(0, int((cx - r - b.x0) / GS) - 1); i1 = int((cx + r - b.x0) / GS) + 2
        j0 = max(0, int((cz - r - b.z0) / GS) - 1); j1 = int((cz + r - b.z0) / GS) + 2
        X = b.X[j0:j1, i0:i1] - cx; Z = b.Z[j0:j1, i0:i1] - cz
        u = (X * np.cos(th) + Z * np.sin(th)) / r
        v = (-X * np.sin(th) + Z * np.cos(th)) / (r * asp)
        q = np.abs(u) ** pexp + np.abs(v) ** pexp
        hh = np.where(q < 1, cy + rz * np.clip(1 - q, 0, 1) ** (1 / pexp), -1e9)
        h[j0:j1, i0:i1] = np.maximum(h[j0:j1, i0:i1], hh)
    up = ss(0.015, 0.06, h - b.h_flat)
    fields = {"rock": np.maximum(up, 0.85 * ss(0.5, 0.62, b.cliff0)), "scree": ss(0.5, 0.62, b.cliff0) * (1 - up),
              "lift": h - b.h_flat}
    tan = np.array([0.80, 0.67, 0.50]); stain = np.array([0.47, 0.34, 0.25])

    def paint(px):
        g = grain(px, 0.09)
        c = tan * (0.8 + 0.35 * g)[..., None]
        # tops bleached, the foot of each boulder stained by the soil
        foot = 1.0 - ss(0.02, 0.18, px["lift"])
        c = c * (1 - 0.35 * foot)[..., None] + stain * (0.35 * foot)[..., None]
        c = c * varnish(px, 0.32)[..., None]
        grit = SAND * 0.82 * (0.85 + 0.3 * g)[..., None]
        return c * (1 - px["scree"])[..., None] + grit * px["scree"][..., None]
    return h, fields, paint


def _talus(b, top, reach=0.62, share=0.38, seed=31):
    n = 0.85 + 0.3 * (0.5 + 0.5 * fbm(b.X / 0.25, b.Z / 0.25, seed, 2))
    return (top - b.h_flat).clip(0) * share * ss(0.10, reach, b.cliff_soft) * n


RED = [(0.80, 0.50, 0.34), (0.87, 0.66, 0.48), (0.72, 0.41, 0.29),
       (0.90, 0.75, 0.59), (0.66, 0.36, 0.26), (0.84, 0.58, 0.40)]


OCHRE = [(0.86, 0.70, 0.45), (0.78, 0.60, 0.38), (0.90, 0.78, 0.56), (0.70, 0.52, 0.33)]
PAINTED = [(0.86, 0.52, 0.40), (0.92, 0.86, 0.76), (0.70, 0.45, 0.42), (0.80, 0.70, 0.62), (0.62, 0.40, 0.38)]

# The knobs a mesa turns: palette, layer thickness, how deep the bays bite
# (amount, scale), the apron, the cap's rise, and a ledge part-way up.
MESAS = {
    1: dict(pal=RED, thick=0.075, tear=0.55, scale=0.9, talus=0.38, cap=0.08, bench=0.0, seed=0),
    2: dict(pal=OCHRE, thick=0.10, tear=0.40, scale=0.6, talus=0.30, cap=0.10, bench=0.55, seed=7),
    3: dict(pal=PAINTED, thick=0.05, tear=0.80, scale=1.3, talus=0.45, cap=0.04, bench=0.0, seed=13),
}


def v_mesa(b, k=1):
    """Monument Valley: a flat cap, sheer banded walls, a scree apron."""
    m = MESAS[k]; s = m["seed"]
    cliff = np.clip(b.cliff0 - m["tear"] * np.abs(perlin(b.X / m["scale"], b.Z / m["scale"], 21 + s))
                    - 0.2 * np.abs(perlin(b.X / 0.3, b.Z / 0.3, 22 + s)), 0, 1)
    cliff = np.where(b.cliff0 > 0.99, b.cliff0, cliff)
    top = np.maximum(b.h_flat, b.rock_top) + m["cap"] + 0.015 * perlin(b.X / 0.4, b.Z / 0.4, 23 + s)
    talus = _talus(b, top, share=m["talus"], seed=31 + s)
    base = b.h_flat + talus
    wall = ss(0.46, 0.54, cliff)
    if m["bench"] > 0:
        # a stepped mesa: the lower wall stops at a ledge, the upper one
        # stands back from the rim, inside the rock's softer share
        inner = np.clip(blur(b.mask, 0.3) - 0.25 * np.abs(perlin(b.X / 0.7, b.Z / 0.7, 26 + s)), 0, 1)
        upper = ss(0.70, 0.76, inner)
        ledge = base + (top - base) * m["bench"]
        h = base + (np.maximum(base, ledge) - base) * wall
        h = h + (np.maximum(h, top) - h) * upper
        lid_at = np.where(upper > 0.5, top, ledge)
    else:
        h = base + (np.maximum(base, top) - base) * wall
        lid_at = top
    # scrub on the cap: dark tufts, thinning toward the rim
    f1, *_ = worley(b.X, b.Z, 0.16, 24 + s)
    scrub = (1 - ss(0.025, 0.045, f1)) * ss(0.7, 0.95, b.cliff0) * (perlin(b.X / 0.5, b.Z / 0.5, 25 + s) > -0.1)
    rim = ss(0.4, 0.5, cliff) * (1 - ss(0.5, 0.75, cliff))
    fields = {"rock": ss(0.4, 0.48, cliff), "scree": ss(0.02, 0.06, talus) * (1 - ss(0.4, 0.48, cliff)),
              "cap": ss(0.0, 0.04, h - (lid_at - 0.07)), "scrub": scrub, "rim": rim}
    pal = m["pal"]
    cap = np.array(pal[1]) * 0.9; capstone = np.array(pal[-1]) * 0.75
    scree = np.array(pal[0]); bush = np.array([0.36, 0.38, 0.22])

    def paint(px):
        g = grain(px)
        c = strata(px["h"], px["x"], px["z"], pal, m["thick"], seed=40 + s) * (0.85 + 0.25 * g)[..., None]
        # the caprock: a darker hard band at the lip, paler sand-swept top
        lid = cap + (capstone - cap) * px["rim"][..., None]
        lid = lid + (bush - lid) * px["scrub"][..., None]
        c = c * (1 - px["cap"])[..., None] + lid * (0.85 + 0.3 * g)[..., None] * px["cap"][..., None]
        c = c * varnish(px, 0.30)[..., None]
        sc = scree * (0.8 + 0.4 * grain(px, 0.05, 91))[..., None]
        w = px["scree"] / np.maximum(px["scree"] + px["rock"], 1e-3)
        return c * (1 - w)[..., None] + sc * w[..., None]
    return h, fields, paint


def v_spires(b):
    """Buttes and hoodoos: the rock broken into banded stacks over a rubble floor."""
    size = 0.62
    f1, f2, fx, fz, fid = worley(b.X, b.Z, size, 51)
    inside = b.sample(b.cliff0, fx, fz) > 0.6
    # a third are wide buttes, the rest spires, a few cells left empty
    butte = fid < 0.33
    jitter = rand01((fx * 97).astype(np.int64), (fz * 89).astype(np.int64), 52)
    rr = size * np.where(butte, 0.44 + 0.08 * jitter, 0.18 + 0.12 * jitter)
    col = ss(rr, rr * 0.75, f1) * inside * (fid < 0.88)
    height = b.rock_top * np.where(butte, 0.85 + 0.25 * jitter, 0.6 + 0.75 * jitter) + 0.05
    floor_ = b.h_flat + (0.10 + 0.06 * fbm(b.X / 0.2, b.Z / 0.2, 53, 2)) * ss(0.45, 0.7, b.cliff0)
    top = b.h_flat + height
    talus = _talus(b, b.h_flat + b.rock_top * 0.6, reach=0.55, share=0.3)
    base = np.maximum(floor_, b.h_flat + talus)
    h = base + (np.maximum(base, top) - base) * col
    capband = ss(0.0, 0.05, h - (top - 0.06)) * col
    fields = {"rock": np.maximum(col, ss(0.45, 0.6, b.cliff0)), "col": col, "cap": capband,
              "scree": ss(0.02, 0.06, talus) * (1 - ss(0.45, 0.6, b.cliff0))}
    cap = np.array([0.55, 0.40, 0.33]); rubble = np.array([0.78, 0.56, 0.41])

    def paint(px):
        g = grain(px)
        c = strata(px["h"], px["x"], px["z"], RED, 0.06) * (0.85 + 0.25 * g)[..., None]
        c = c * (1 - px["cap"])[..., None] + cap * px["cap"][..., None]
        c = c * varnish(px, 0.25)[..., None]
        s = rubble * (0.75 + 0.45 * grain(px, 0.04, 92))[..., None]
        w = np.clip(1 - px["col"], 0, 1)
        return c * (1 - w)[..., None] + s * w[..., None]
    return h, fields, paint


GREY = [(0.72, 0.71, 0.68), (0.64, 0.63, 0.61), (0.78, 0.77, 0.73), (0.58, 0.57, 0.55)]
BUFF = [(0.80, 0.72, 0.58), (0.74, 0.66, 0.53), (0.84, 0.77, 0.63), (0.70, 0.62, 0.50)]
PINK = [(0.86, 0.66, 0.52), (0.80, 0.58, 0.46), (0.90, 0.74, 0.60), (0.74, 0.52, 0.42)]

# The knobs a yardang field turns: the wind (one per map), how long and how
# far apart the ridges run, how tall, the palette.
YARDANGS = {
    1: dict(wind=25, length=2.4, gap=0.42, tall=0.95, pal=BUFF, seed=0),
    2: dict(wind=70, length=3.4, gap=0.30, tall=0.85, pal=GREY, seed=5),
    3: dict(wind=-20, length=1.3, gap=0.62, tall=1.25, pal=PINK, seed=11),
}


def v_yardang(b, k=1):
    """Wind-carved ridges, all one way: long and streamlined, sand in the lee."""
    y = YARDANGS[k]; s = y["seed"]
    th = math.radians(y["wind"])
    u = b.X * math.cos(th) + b.Z * math.sin(th); v = -b.X * math.sin(th) + b.Z * math.cos(th)
    ridge = 1.0 - np.abs(perlin(u / y["length"], v / y["gap"], 61 + s))
    ridge2 = 1.0 - np.abs(perlin(u / (y["length"] / 2), v / (y["gap"] / 2), 62 + s))
    # rounded crests: the ridges smoothed, so the wind's work reads, not the noise's
    shape = blur(np.clip(0.8 * ridge ** 2.2 + 0.2 * ridge2 ** 2, 0, 1), 0.05)
    cliff = np.clip(b.cliff0 - 0.5 * np.abs(perlin(u / (y["length"] * 0.9), v / y["gap"], 63 + s)), 0, 1)
    cliff = np.where(b.cliff0 > 0.99, b.cliff0, cliff)
    top = b.h_flat + b.rock_top * (0.35 + y["tall"] * shape)
    wall = ss(0.42, 0.6, cliff) * ss(0.25, 0.45, shape + 0.15)
    sand = ss(0.1, 0.5, b.cliff_soft) * (0.5 + 0.5 * fbm(u / 0.8, v / 0.3, 64 + s, 2)) * 0.12
    base = b.h_flat + sand
    h = base + (np.maximum(base, top) - base) * wall
    fields = {"rock": ss(0.3, 0.5, wall), "scree": ss(0.01, 0.05, sand) * (1 - ss(0.3, 0.5, wall))}

    def paint(px):
        g = grain(px)
        c = strata(px["h"], px["x"], px["z"], y["pal"], 0.04, 0.05, seed=40 + s) * (0.85 + 0.25 * g)[..., None]
        c = c * varnish(px, 0.22)[..., None]
        sd = SAND * (0.9 + 0.2 * g)[..., None]
        return c * (1 - px["scree"])[..., None] + sd * px["scree"][..., None]
    return h, fields, paint


def v_dome(b):
    """Inselberg: one bare granite dome, sheets peeling off it in steps."""
    inner = blur(b.mask, 0.8)
    d = np.clip((inner - 0.15) / max(inner.max() - 0.15, 1e-3), 0, 1)
    # steep at the foot, round on top: a bald dome
    dome = b.rock_top * 1.75 * d ** 0.85
    q = 0.075
    t = dome / q
    sheets = q * (np.floor(t) + ss(0.55, 1.0, t - np.floor(t)))
    dome = 0.5 * dome + 0.5 * sheets
    cliff = b.torn(0.35, 0.5, 71)
    wall = ss(0.5, 0.62, cliff)
    top = b.h_flat + dome
    talus = _talus(b, b.h_flat + b.rock_top * 0.5, reach=0.6, share=0.25)
    base = b.h_flat + talus
    h = base + (np.maximum(base, top) - base) * wall
    # the sheets' edges: dark lines along the dome's contours
    frac = (dome / q) % 1.0
    crack = (1 - ss(0.0, 0.12, frac)) * ss(0.04, 0.1, dome) * ss(0.6, 0.9, cliff)
    fields = {"rock": ss(0.45, 0.55, cliff), "crack": crack,
              "scree": ss(0.02, 0.06, talus) * (1 - ss(0.45, 0.55, cliff))}
    pink = np.array([0.82, 0.69, 0.60]); grit = np.array([0.78, 0.67, 0.55])

    def paint(px):
        g = grain(px, 0.15)
        c = pink * (0.82 + 0.3 * g)[..., None]
        c = c * varnish(px, 0.40)[..., None]
        c = c * (1 - 0.45 * px["crack"])[..., None]
        s = grit * (0.8 + 0.4 * grain(px, 0.05, 93))[..., None]
        return c * (1 - px["scree"])[..., None] + s * px["scree"][..., None]
    return h, fields, paint


def v_basalt(b, seed=3):
    """Columnar basalt (Giant's Causeway): hexagonal columns, tops stepping
    down toward the rim, joints dark between them."""
    s = 0.13                      # a column's circumradius
    qc, qr, side = hex_at(b.X / s, b.Z / s)
    cx = 1.5 * qc * s; cz = 1.7320508 * s * (qr + 0.5 * (qc & 1))
    c0 = b.sample(b.cliff0, cx, cz)
    r1 = rand01(qc, qr, seed); r2 = rand01(qc, qr, seed + 1)
    inside = c0 > 0.5
    # the tops: high in the middle, a staircase down at the rim, a few broken
    stair = ss(0.5, 0.9, c0)
    rise = b.sample(b.rock_top, cx, cz) * (0.45 + 0.65 * stair) + 0.06 * (r1 - 0.5)         - 0.12 * (r2 < 0.08)
    ground = b.sample(b.h_flat, cx, cz)
    h = np.where(inside, np.maximum(b.h_flat, ground + rise), b.h_flat)
    joint = (1 - ss(0.03, 0.09, side)) * inside
    fields = {"rock": inside.astype(float), "joint": joint.astype(float),
              "tone": np.where(inside, r1, 0.0), "scree": np.zeros_like(h)}
    dark = np.array([0.33, 0.33, 0.35]); light = np.array([0.46, 0.45, 0.45])
    lichen = np.array([0.62, 0.60, 0.42])

    def paint(px):
        g = grain(px, 0.06)
        c = dark + (light - dark) * px["tone"][..., None]
        c = c * (0.85 + 0.3 * g)[..., None]
        # tops a step paler, lichen in patches on them
        flat = 1 - ss(0.3, 0.6, px["hl"])
        c = c * (1 + 0.18 * flat)[..., None]
        moss = flat * ss(0.2, 0.45, fbm(px["x"] / 0.4, px["z"] / 0.4, 81, 2))
        c = c + (lichen - c) * (0.6 * moss)[..., None]
        return c * (1 - 0.55 * px["joint"])[..., None]
    return h, fields, paint


# The knobs a roche moutonnee turns: where the ice ran, how tall the back
# rises, how big the plucked blocks of the lee are, the stone, the moss.
MOUTONNEES = {
    1: dict(flow=-30, tall=1.1, block=0.20, stone=(0.64, 0.63, 0.60), moss=0.35, band=0.0, seed=0),
    2: dict(flow=150, tall=1.3, block=0.30, stone=(0.52, 0.52, 0.54), moss=0.15, band=0.0, seed=4),
    3: dict(flow=10, tall=0.8, block=0.14, stone=(0.70, 0.60, 0.56), moss=0.5, band=0.35, seed=9),
}


def v_moutonnee(b, k=1):
    """Roche moutonnee (Karelia): the ice ran over it - smooth and gentle on
    the side it came from, plucked into a ragged step on the side it left."""
    m = MOUTONNEES[k]; s = m["seed"]
    th = math.radians(m["flow"])
    du, dv = math.cos(th), math.sin(th)
    env = np.clip(blur(b.mask, 0.35) / max(blur(b.mask, 0.35).max(), 1e-3), 0, 1)
    # how far along the flow a point is within its own rock: 0 at the edge
    # the ice met first, 1 at the edge it left - one back, never a bowl
    u = b.X * du + b.Z * dv
    ramp = np.zeros_like(b.X)
    for comp in b.rocks():
        inside = comp & (b.cliff0 > 0.5)
        lo, hi = u[inside].min(), u[inside].max()
        ramp = np.where(comp, np.clip((u - lo) / max(hi - lo, 1e-3), 0, 1), ramp)
    ramp = np.clip(blur(ramp, 0.1), 0, 1)
    # the lee: a point a little downstream already off the rock
    lee = b.sample(b.cliff0, b.X + du * 0.12, b.Z + dv * 0.12)
    cut = ss(0.3, 0.7, lee)
    # plucked blocks in the lee face: steps by Worley cells
    f1, f2, fx, fz, fid = worley(b.X, b.Z, m["block"], 83 + s)
    blocks = 0.5 * m["block"] * (fid - 0.5) * (1 - cut)
    # never sagging under its own middle: the back's floor follows the envelope
    lift = np.maximum(0.3 + m["tall"] * ramp ** 1.2, 0.7 * m["tall"] * env)
    dome = b.rock_top * lift * (0.75 + 0.25 * env) + blocks
    cliff = b.torn(0.3, 0.5, 84 + s)
    wall = ss(0.5, 0.6, cliff)
    top = b.h_flat + dome
    h = b.h_flat + (np.maximum(b.h_flat, top) - b.h_flat) * wall
    v = -b.X * dv + b.Z * du
    stria = (np.abs(perlin(u / 1.5, v / 0.025, 85 + s)) < 0.08) * cut
    fields = {"rock": ss(0.45, 0.55, cliff), "stria": stria.astype(float),
              "crack": (1 - ss(0.0, 0.02, f2 - f1)) * (1 - cut), "scree": np.zeros_like(h)}
    stone = np.array(m["stone"]); moss = np.array([0.33, 0.42, 0.22])
    lichen = np.array([0.80, 0.78, 0.66]); dark = stone * 0.7

    def paint(px):
        g = grain(px, 0.1)
        c = stone * (0.85 + 0.3 * g)[..., None]
        if m["band"] > 0:
            # gneiss: the stone's own folded bands, across the flow
            uu = px["x"] * dv - px["z"] * du
            fold = np.sin(uu / 0.06 + 2.5 * perlin(px["x"] / 0.6, px["z"] / 0.6, 88 + s))
            c = c + (dark - c) * (m["band"] * ss(0.4, 0.9, fold))[..., None]
        c = c * (1 - 0.25 * px["stria"])[..., None] * (1 - 0.5 * px["crack"])[..., None]
        flat = 1 - ss(0.35, 0.7, px["hl"])
        mo = flat * ss(0.45 - m["moss"], 0.7 - m["moss"], fbm(px["x"] / 0.35, px["z"] / 0.35, 86 + s, 3))
        c = c + (moss * (0.8 + 0.4 * g)[..., None] - c) * mo[..., None]
        spot = (1 - ss(0.012, 0.022, worley(px["x"], px["z"], 0.14, 87 + s)[0])) * flat
        return c + (lichen - c) * (0.5 * spot)[..., None]
    return h, fields, paint


def v_karst(b, seed=91):
    """Tower karst (Guilin): steep limestone haystacks, jungle on whatever is
    not a sheer face."""
    size = 0.7
    f1, f2, fx, fz, fid = worley(b.X, b.Z, size, seed)
    inside = b.sample(b.cliff0, fx, fz) > 0.55
    jit = rand01((fx * 97).astype(np.int64), (fz * 89).astype(np.int64), seed + 1)
    rr = size * (0.42 + 0.14 * jit)
    d = np.clip(f1 / rr, 0, 1)
    tall = b.rock_top * (1.3 + 0.7 * fid)
    # haystack: sheer sides, a round crown
    tower = tall * np.clip(1 - d ** 3.0, 0, 1) ** 0.45 * inside
    floor_ = 0.08 * ss(0.45, 0.7, b.cliff0)
    canopy = 0.035 * (0.5 + 0.5 * fbm(b.X / 0.07, b.Z / 0.07, seed + 2, 2))
    h = b.h_flat + np.maximum(floor_, tower)
    rock = ss(0.45, 0.6, b.cliff0)
    h = h + canopy * rock
    fields = {"rock": np.maximum(rock, ss(0.0, 0.05, tower)), "scree": np.zeros_like(h)}
    lime = np.array([0.80, 0.79, 0.74]); jungle = np.array([0.20, 0.36, 0.15])
    jungle2 = np.array([0.32, 0.48, 0.20])

    def paint(px):
        g = grain(px, 0.08)
        bare = ss(0.72, 0.85, px["hl"])
        c = lime * (0.85 + 0.25 * g)[..., None] * varnish(px, 0.45)[..., None]
        leaf = jungle + (jungle2 - jungle) * ss(-0.2, 0.4, fbm(px["x"] / 0.12, px["z"] / 0.12, 92, 2))[..., None]
        return leaf * (1 - bare)[..., None] + c * bare[..., None]
    return h, fields, paint


def v_tsingy(b, seed=101):
    """Tsingy (Madagascar): a forest of limestone blades, cut by two sets of
    joints into slots and needles."""
    th = math.radians(20.0)
    u = b.X * math.cos(th) + b.Z * math.sin(th); v = -b.X * math.sin(th) + b.Z * math.cos(th)
    a = (1 - np.abs(perlin(u / 0.9, v / 0.11, seed))) ** 6
    c = (1 - np.abs(perlin(u / 0.13, v / 0.7, seed + 1))) ** 6
    blades = np.maximum(a, 0.8 * c)
    needles = (1 - np.abs(perlin(b.X / 0.07, b.Z / 0.07, seed + 2))) ** 8
    sharp = np.clip(0.75 * blades + 0.35 * needles, 0, 1)
    cliff = b.torn(0.4, 0.5, seed + 3)
    wall = ss(0.5, 0.6, cliff)
    top = b.h_flat + b.rock_top * (0.35 + 1.0 * sharp)
    h = b.h_flat + (np.maximum(b.h_flat, top) - b.h_flat) * wall
    fields = {"rock": ss(0.45, 0.55, cliff), "lift": (h - b.h_flat) / np.maximum(b.rock_top, 1e-3),
              "scree": np.zeros_like(h)}
    lime = np.array([0.80, 0.79, 0.75]); slot = np.array([0.34, 0.33, 0.31])

    def paint(px):
        g = grain(px, 0.05)
        c = lime * (0.85 + 0.3 * g)[..., None] * varnish(px, 0.2)[..., None]
        # the slots deep and dark, the blades' edges bleached
        deep = 1 - ss(0.3, 0.65, px["lift"])
        return c + (slot - c) * (0.55 * deep)[..., None]
    return h, fields, paint


def on_game_raster(fn):
    """A variant as the game's raster (1/12 R) would hold it: the height and
    the fields the shader reads are sampled at the mesh's vertices and read
    back bilinear; the paint stays per pixel, as the shader's does."""
    def made(b):
        h, fields, paint = fn(b)
        near = ss(0.05, 0.3, b.cliff_soft)
        h = h + (b.coarse(h) - h) * near
        fields = {k: f + (b.coarse(f) - f) * near for k, f in fields.items()}
        return h, fields, paint
    return made


def v_tor_game(b):
    """The tor on the game's raster (1/12 R): what Relief's mesh would keep of it."""
    h, fields, paint = v_tor(b)
    near = ss(0.05, 0.3, b.cliff_soft)
    h = h + (b.coarse(h) - h) * near
    fields = {k: f + (b.coarse(f) - f) * near for k, f in fields.items()}
    return h, fields, paint


VARIANTS = {
    "current": ("A. как сейчас: стена + ridged-гребни", v_current),
    "tor": ("B. тор (Джошуа-Три): груды валунов", v_tor),
    "tor_game": ("B'. тот же тор на растре игры 1/12", v_tor_game),
    "mesa": ("C. меса: плоский верх, слои, осыпь", v_mesa),
    "mesa2": ("C2. меса ступенью: уступ, охра, толстые слои", lambda b: v_mesa(b, 2)),
    "mesa3": ("C3. меса размытая: глубокие бухты, пёстрые слои", lambda b: v_mesa(b, 3)),
    "spires": ("D. бьютты и худу: столбы на щебне", v_spires),
    "yardang": ("E. ярданги: гряды по ветру", v_yardang),
    "yardang2": ("E2. ярданги: ветер вдоль взгляда, длинные, серые", lambda b: v_yardang(b, 2)),
    "yardang3": ("E3. ярданги: короткие, высокие, розовые", lambda b: v_yardang(b, 3)),
    "dome": ("F. инзельберг: купол, шелушение", v_dome),
    "basalt": ("G. базальтовые столбы", v_basalt),
    "moutonnee": ("H. бараньи лбы: лёд слева снизу, гранит, мох", v_moutonnee),
    "moutonnee2": ("H2. бараньи лбы: лёд справа сверху, выше, тёмный", lambda b: v_moutonnee(b, 2)),
    "moutonnee3": ("H3. бараньи лбы: лёд слева, низкие, гнейс в полоску", lambda b: v_moutonnee(b, 3)),
    "karst": ("I. карстовые башни: известняк и джунгли", v_karst),
    "tsingy": ("J. цинги: лес известняковых лезвий", v_tsingy),
}
for _name in ("mesa", "mesa2", "yardang", "yardang3", "moutonnee", "moutonnee2"):
    _title, _fn = VARIANTS[_name]
    VARIANTS[_name + "_game"] = (_title.split(":")[0] + ": растр игры 1/12", on_game_raster(_fn))


# ------------------------------------------------------------- renderer ----

def shadow(b, h):
    """Cast shadow from the sun: march toward it over the raster."""
    sxz = np.hypot(SUN[0], SUN[2]); dx, dz = SUN[0] / sxz, SUN[2] / sxz; rise = SUN[1] / sxz
    lit = np.ones(h.shape, bool)
    for k in range(1, int(2.5 / GS)):
        t = k * GS
        hs = b.sample(h, b.X + dx * t, b.Z + dz * t)
        lit &= hs <= h + rise * t + 0.004
    return (~lit).astype(float)


def render(b, h, fields, paint):
    W = int((b.x1 - b.x0) * PX)
    cs, sn = math.cos(ELEV), math.sin(ELEV)
    top_pad = (h.max() + 0.1) * cs * PX
    H = int((b.z1 - b.z0) * sn * PX + top_pad - (min(h.min(), 0) * cs * PX)) + 4
    xs = b.x0 + (np.arange(W) + 0.5) / PX
    zrows = b.Z[:, 0]
    nz = len(zrows)

    def cols(f):
        """A raster field resampled to the image's columns, rows kept."""
        fx = (xs - b.X[0, 0]) / GS
        i = np.clip(fx.astype(int), 0, f.shape[1] - 2); u = fx - i
        return f[:, i] * (1 - u) + f[:, i + 1] * u

    hc = cols(h)
    ys = (zrows[:, None] - b.z0) * sn * PX + top_pad - hc * cs * PX
    # front to back: each row paints what is under its top and not yet painted
    jpix = np.full((H, W), -1, int); tpix = np.zeros((H, W))
    hz = np.full(W, float(H))
    yy = (np.arange(H) + 0.5)[:, None]
    for j in range(nz - 1, -1, -1):
        top = ys[j]
        lo = max(0, int(np.floor(top.min()))); hi = min(H, int(np.ceil(hz.max())))
        if hi > lo:
            seg = yy[lo:hi]
            m = (seg >= top[None, :]) & (seg < hz[None, :])
            nxt = ys[j + 1] if j + 1 < nz else top + 1.0
            span = np.maximum(nxt - top, 1e-3)
            t = np.clip((seg - top[None, :]) / span[None, :], 0, 1)
            jpix[lo:hi][m] = j
            tpix[lo:hi][m] = np.broadcast_to(t, m.shape)[m]
        hz = np.minimum(hz, top)
    valid = jpix >= 0
    j0 = np.clip(jpix, 0, nz - 1); j1 = np.clip(jpix + 1, 0, nz - 1)
    cidx = np.broadcast_to(np.arange(W)[None, :], (H, W))

    def at(f):
        fc = cols(f)
        return fc[j0, cidx] * (1 - tpix) + fc[j1, cidx] * tpix

    gx = np.gradient(h, GS, axis=1); gz = np.gradient(h, GS, axis=0)
    px = {"x": np.broadcast_to(xs[None, :], (H, W)), "z": at(b.Z), "h": at(h)}
    n = np.stack([-at(gx), np.ones((H, W)), -at(gz)], -1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    px["hl"] = np.hypot(n[..., 0], n[..., 2])
    for k, f in fields.items():
        px[k] = at(f)
    px["shadow"] = at(shadow(b, h))
    side = at(b.side); sand = at(b.sand); on = at(b.on.astype(float))

    # the ground: grass on the flats, earth on the banks, the sand cell
    g = 0.5 + 0.5 * fbm(px["x"] / 0.12, px["z"] / 0.12, 95, 2)
    patch = fbm(px["x"] / 1.2, px["z"] / 1.2, 96, 2)
    turf = GRASS + (GRASS_TIP - GRASS) * ss(-0.2, 0.5, fbm(px["x"] / 0.5, px["z"] / 0.5, 97, 2))[..., None]
    flat = 1 - ss(0.2, 0.32, px["hl"])
    lawn = flat * ss(-0.35, -0.15, patch)
    ground = SOIL * (0.75 + 0.5 * g)[..., None]
    ground = ground + (turf * (0.8 + 0.4 * g)[..., None] - ground) * lawn[..., None]
    ground = ground + (SAND * (0.75 + 0.5 * g)[..., None] - ground) * ss(0.3, 0.6, sand)[..., None]
    rockw = np.clip(px["rock"] + px.get("scree", 0), 0, 1)
    colour = ground * (1 - rockw)[..., None] + paint(px) * rockw[..., None]

    # cel steps by the turn to the sun, a wall a step darker (the shader's)
    d = n @ SUN; fd = SUN[1]
    step = np.where(d > fd - 0.08, 1.0, np.where(d > fd - 0.3, 0.86, 0.72))
    step = np.where(px["hl"] > 0.8, np.minimum(step, 0.56), step)
    step = np.where(px["shadow"] > 0.5, np.minimum(step * 0.82, 0.72), step)
    colour = colour * step[..., None]

    # the hex grid on the ground, not on the rock, not on the banks
    line = (1 - ss(0.018, 0.03, side)) * on * (1 - ss(0.45, 0.7, px["hl"])) * (1 - ss(0.3, 0.5, rockw))
    colour = colour + (np.array([0.12, 0.14, 0.07]) - colour) * (0.35 * line)[..., None]

    # outline where the surface jumps in depth
    zz = px["z"]
    edge = np.zeros((H, W), bool)
    dzv = np.abs(np.diff(zz, axis=0)) > 0.10
    edge[:-1] |= dzv
    dzh = np.abs(np.diff(zz, axis=1)) > 0.10
    edge[:, :-1] |= dzh & (zz[:, :-1] < zz[:, 1:])
    edge[:, 1:] |= dzh & (zz[:, 1:] < zz[:, :-1])
    colour = np.where(edge[..., None], INK, colour)
    sky = np.array([0.93, 0.92, 0.89])
    colour = np.where(valid[..., None], colour, sky)
    return Image.fromarray((np.clip(colour, 0, 1) * 255).astype(np.uint8))


def label(img, text):
    font = ImageFont.truetype("C:/Windows/Fonts/arialbd.ttf", 26)
    d = ImageDraw.Draw(img)
    d.rectangle([0, 0, d.textlength(text, font=font) + 24, 40], fill=(250, 248, 242))
    d.text((12, 6), text, fill=(30, 28, 25), font=font)
    return img


def main(names):
    b = Board()
    shots = []
    for name in names:
        title, fn = VARIANTS[name]
        t0 = time.time()
        h, fields, paint = fn(b)
        img = label(render(b, h, fields, paint), title)
        img.save(os.path.join(OUT, f"{name}.png"))
        shots.append(img)
        print(f"{name}: {img.size[0]}x{img.size[1]} in {time.time() - t0:.1f}s", flush=True)
    if len(shots) > 1:
        w = max(s.size[0] for s in shots) // 2; hh = max(s.size[1] for s in shots) // 2
        sheet = Image.new("RGB", (w * 2, hh * ((len(shots) + 1) // 2)), (250, 248, 242))
        for i, s in enumerate(shots):
            sheet.paste(s.resize((s.size[0] // 2, s.size[1] // 2), Image.LANCZOS), ((i % 2) * w, (i // 2) * hh))
        sheet.save(os.path.join(OUT, "sheet.png"))


if __name__ == "__main__":
    main(sys.argv[1:] or list(VARIANTS))
