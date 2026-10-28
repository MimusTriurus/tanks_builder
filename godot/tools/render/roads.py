"""Road looks on the relief board, rendered off-engine (draft, not the game).

One road network - a road up the board's diagonal that bends north, and a
branch off it that snakes east - laid six ways: a dirt track, a gravel
highway, cobbles, a corduroy causeway over a swamp, a forest track, asphalt.
The road goes where a tank can: centre to centre through the middle of a
side, bending inside a hex (`through`). Then a distance and an arc length to
it on the raster: the shader would read those two and draw ruts, setts, logs
and cracks per pixel.

Every look comes twice, as ground.py's: fine (1/30 R) and as the game's mesh
holds it (height, distance and arc length sampled at 1/12 R, read back
bilinear). Props - trees, posts, reeds - stand on either the same.

    python roads.py              -> <repo>/out/roads/<variant>.png and sheet.png
    python roads.py dirt cobble  -> only these (and their _game twins)
"""
import math
import os
import sys
import time

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rocks  # noqa: E402
import ground  # noqa: E402  (sets rocks.MAP; each board below sets its own)
from rocks import perlin, fbm, worley, ss, blur, GS, SUN, label, neighbours, rand01  # noqa: E402
from ground import mix, tone, render, draw_stalks, PX, ELEV  # noqa: E402

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "out", "roads")
os.makedirs(OUT, exist_ok=True)

TERRACE = {(0, 0): ("plain", 1), (1, 0): ("plain", 1), (0, 1): ("plain", 1)}


def board(swamp=False):
    rocks.COLS, rocks.ROWS = 9, 6
    m = dict(TERRACE)
    if swamp:
        for c in range(1, 8):
            for r in range(6):
                m.setdefault((c, r), ("sand", 0))
    rocks.MAP = m
    return rocks.Board()


# --------------------------------------------------------------- network ----

# A tank moves only across a side of the hex - north, south and four ways at
# 30 degrees off east and west - and turns in steps of 60. A road is laid the
# same way: from centre to centre through the middle of the side between, so
# a run "east" is a snake of north-east and south-east, and nothing turns 90.
MAIN = [(0, 5), (1, 4), (2, 4), (3, 3), (4, 3), (5, 2), (6, 2), (6, 1), (6, 0)]
BRANCH_AT = 3                      # MAIN's index the branch leaves from
BRANCH = [(4, 4), (5, 4), (6, 5), (7, 4), (8, 5)]


def centre(c, r):
    return np.array([1.5 * c, 1.7320508 * (r + 0.5 * (c & 1))])


def through(a, b, step=0.01):
    """The road's axis across one hex, from the middle of the side it comes
    in by (a) to the middle of the side it leaves by (b), meeting both
    square: straight across opposite sides, otherwise the circle arc that is
    perpendicular to both - its centre where the two sides' lines cross, so
    60 degrees a gentle bend and 120 a hairpin round the shared corner."""
    (pa, da), (pb, db) = a, b
    if abs(da[0] * db[1] - da[1] * db[0]) < 1e-6:
        n = max(2, int(np.linalg.norm(pb - pa) / step))
        return pa + (pb - pa) * np.linspace(0, 1, n, endpoint=False)[:, None]
    na = np.array([-da[1], da[0]]); nb = np.array([-db[1], db[0]])
    t, _ = np.linalg.solve(np.stack([na, -nb], 1), pb - pa)
    c = pa + na * t
    a0 = math.atan2(*(pa - c)[::-1]); a1 = math.atan2(*(pb - c)[::-1])
    turn = (a1 - a0 + math.pi) % (2 * math.pi) - math.pi
    n = max(3, int(abs(turn * t) / step))
    ang = a0 + turn * np.linspace(0, 1, n, endpoint=False)
    return c + abs(t) * np.stack([np.cos(ang), np.sin(ang)], 1)


def axis(cells, start=0):
    """A path of neighbouring cells as one polyline: each hex crossed side
    to side, the ends carried on straight off the board."""
    pts = [centre(*c) for c in cells]
    pts = [2 * pts[0] - pts[1]] + pts + [2 * pts[-1] - pts[-2]]
    out = []
    for k in range(max(1, start + 1), len(pts) - 1):
        come = (pts[k - 1] + pts[k]) / 2; go = (pts[k] + pts[k + 1]) / 2
        out.append(through((come, (pts[k] - pts[k - 1]) / np.linalg.norm(pts[k] - pts[k - 1])),
                           (go, (pts[k + 1] - pts[k]) / np.linalg.norm(pts[k + 1] - pts[k]))))
    line = np.concatenate(out + [((pts[-2] + pts[-1]) / 2)[None, :]])
    if start == 0:
        line = np.concatenate([line[:1] - (line[1] - line[0]) / np.linalg.norm(line[1] - line[0]) * np.linspace(1.0, 0.01, 60)[:, None], line])
    tail = (line[-1] - line[-2]) / np.linalg.norm(line[-1] - line[-2])
    return np.concatenate([line, line[-1] + tail * np.linspace(0.01, 1.0, 60)[:, None]])


def network():
    """The road's lines: each a dense polyline with its arc length. The
    branch leaves its fork hex by its own bend, from the side the main road
    came in by - the two share that half of the hex."""
    for path in (MAIN, BRANCH):
        for a, b in zip(path, path[1:]):
            assert b in neighbours(*a), (a, b)
    assert BRANCH[0] in neighbours(*MAIN[BRANCH_AT])
    main = axis(MAIN)
    branch = axis(MAIN[:BRANCH_AT + 1] + BRANCH, start=BRANCH_AT)
    lines = []
    s0 = 0.0
    for line in (main, branch):
        seg = np.linalg.norm(np.diff(line, axis=0), axis=1)
        s = s0 + np.concatenate([[0.0], np.cumsum(seg)])
        lines.append((line, s))
        s0 = s[-1] + 3.0
    return lines


def fields_of(b, lines):
    """Signed distance to the nearest road line (left of the way it runs is
    minus) and the arc length there, on the raster. Signed, because read
    back bilinear from 1/12 R a distance's V at the axis rounds off to a
    hundredth or more and a centre line goes; a signed one is a straight
    ramp there and comes back exact."""
    P = np.concatenate([l for l, _ in lines]).astype(np.float32)
    S = np.concatenate([s for _, s in lines]).astype(np.float32)
    T = np.concatenate([np.gradient(l, axis=0) for l, _ in lines]).astype(np.float32)
    pts = np.stack([b.X.ravel(), b.Z.ravel()], 1).astype(np.float32)
    d = np.empty(len(pts), np.float32); s = np.empty(len(pts), np.float32)
    for i in range(0, len(pts), 1500):
        q = pts[i:i + 1500]
        dd = ((q[:, None, :] - P[None, :, :]) ** 2).sum(-1)
        k = dd.argmin(1)
        off = q - P[k]
        side = np.sign(T[k, 0] * off[:, 1] - T[k, 1] * off[:, 0])
        d[i:i + 1500] = np.sqrt(dd[np.arange(len(q)), k]) * np.where(side == 0, 1, side); s[i:i + 1500] = S[k]
    return d.reshape(b.X.shape).astype(float), s.reshape(b.X.shape).astype(float)


def bump(px, pattern):
    """A pattern's slope in world units from its pixels: x is PX px an R, z
    is PX * sin(ELEV) (heights neglected - the road is level)."""
    gy, gx = np.gradient(pattern)
    return gx * PX, gy * PX * math.sin(ELEV)


def grass_floor(px):
    g = 0.5 + 0.5 * fbm(px["x"] / 0.12, px["z"] / 0.12, 95, 2)
    turf = rocks.GRASS + (rocks.GRASS_TIP - rocks.GRASS) * ss(-0.2, 0.5, fbm(px["x"] / 0.5, px["z"] / 0.5, 97, 2))[..., None]
    return turf * (0.8 + 0.4 * g)[..., None], g


# ----------------------------------------------------------------- types ----

SOIL = np.array([0.56, 0.46, 0.33]); SOIL_WET = np.array([0.40, 0.31, 0.22])
GRAVEL = np.array([0.72, 0.68, 0.60]); STONE = np.array([0.55, 0.53, 0.50])
WOOD = np.array([0.52, 0.38, 0.24]); TAR = np.array([0.29, 0.29, 0.30])
NEEDLES = np.array([0.36, 0.33, 0.20])


def edge_noise(px, amp):
    return amp * perlin(px["s"] / 0.35, px["x"] * 0.0 + 1.3, 71) + 0.4 * amp * perlin(px["s"] / 0.09, px["z"] * 0.0 + 2.1, 72)


def profile(d, hw, crown=0.0, ditch=0.0, bank=0.0, sink=0.0):
    """The road's cross-section on the raster: crown, an embankment, ditches."""
    inside = 1 - ss(hw * 0.85, hw + 0.03, d)
    h = crown * np.clip(1 - (d / hw) ** 2, 0, 1) * inside + sink * inside
    if bank:
        h += bank * (1 - ss(hw + 0.02, hw + 0.12, d))
    if ditch:
        h -= ditch * ss(hw + 0.03, hw + 0.08, d) * (1 - ss(hw + 0.12, hw + 0.18, d))
    return h


class Road:
    def __init__(self, title, hw, swamp=False):
        self.title, self.hw, self.swamp = title, hw, swamp

    def height(self, b, d):
        return np.zeros_like(d)

    def look(self, px, d, s):
        raise NotImplementedError

    def props(self, b, h, df, lines):
        return []


class Dirt(Road):
    """Two ruts, grass between them, a ragged edge into the field."""
    def height(self, b, d):
        rut = 1 - ss(0.03, 0.07, np.abs(d - 0.24))
        return profile(d, self.hw, sink=-0.006) - 0.006 * rut

    def look(self, px, d, s):
        x, z = px["x"], px["z"]
        g = 0.5 + 0.5 * fbm(x / 0.06, z / 0.06, 73, 2)
        edge = self.hw + edge_noise(px, 0.035)
        c = tone(SOIL, g, 0.8, 0.4)
        rut = 1 - ss(0.035, 0.07, np.abs(d - 0.24))
        c = mix(c, tone(SOIL_WET, g), rut * 0.8)
        mid, _ = grass_floor(px)
        strip = (1 - ss(0.06, 0.1, d)) * ss(0.35, 0.6, 0.5 + 0.5 * perlin(s / 0.4, x * 0 + 5.0, 74))
        c = mix(c, mid * 0.85, strip * 0.7)
        cover = 1 - ss(edge - 0.03, edge + 0.02, d)
        pr = -0.004 * rut
        bx, bz = bump(px, pr)
        return c, cover, bx, bz


class Gravel(Road):
    """A crowned bed of crushed stone, ditches both sides, white posts."""
    def height(self, b, d):
        return profile(d, self.hw, crown=0.012, ditch=0.035)

    def look(self, px, d, s):
        x, z = px["x"], px["z"]
        g = 0.5 + 0.5 * fbm(x / 0.08, z / 0.08, 75, 2)
        f1, f2, _, _, fid = worley(x, z, 0.012, 76)
        c = GRAVEL[None, None, :] * (0.82 + 0.3 * fid)[..., None] * (0.9 + 0.2 * g)[..., None]
        c = mix(c, GRAVEL * 0.6, (1 - ss(0.0, 0.003, f2 - f1)) * 0.6)
        # wheel tracks a shade darker, the crown paler
        c = c * (1 - 0.12 * (1 - ss(0.02, 0.05, np.abs(d - 0.24))))[..., None]
        verge, _ = grass_floor(px)
        shoulder = ss(self.hw - 0.02, self.hw + 0.02, d)
        c = mix(c, mix(verge, SOIL * 0.9, 0.35), shoulder)
        # the ditch: damp at its bottom
        dd = ss(self.hw + 0.07, self.hw + 0.1, d) * (1 - ss(self.hw + 0.11, self.hw + 0.15, d))
        c = mix(c, verge * 0.6, dd * 0.6)
        cover = 1 - ss(self.hw + 0.16, self.hw + 0.22, d)
        z0 = np.zeros_like(x)
        return c, cover, z0, z0

    def props(self, b, h, df, lines):
        out = []
        for line, s in lines:
            tang = np.gradient(line, axis=0)
            tang /= np.linalg.norm(tang, axis=1, keepdims=True)
            for at in np.arange(s[0] + 0.3, s[-1], 0.75):
                i = int(np.searchsorted(s, at))
                if i >= len(line):
                    break
                n = np.array([-tang[i, 1], tang[i, 0]])
                for side in (-1, 1):
                    p = line[i] + side * n * (self.hw + 0.05)
                    if not (b.x0 < p[0] < b.x1 and b.z0 < p[1] < b.z1):
                        continue
                    # a post on a road the other line made: skip it
                    if b.sample(df, np.array(p[0]), np.array(p[1])) < self.hw:
                        continue
                    y = float(b.sample(h, np.array(p[0]), np.array(p[1])))
                    out.append(("post", p[0], p[1], y))
        return out


class Cobble(Road):
    """Setts in staggered rows across the road, a kerb of long stones."""
    def height(self, b, d):
        return profile(d, self.hw, crown=0.006) + 0.006 * (1 - ss(0.0, 0.02, np.abs(d - self.hw + 0.012)))

    def look(self, px, d, s):
        x, z = px["x"], px["z"]
        g = 0.5 + 0.5 * fbm(x / 0.1, z / 0.1, 77, 2)
        u = s / 0.032
        row = np.floor(u)
        v = d / 0.03 + 0.5 * (row % 2)
        col = np.floor(v)
        fu, fv = u - row, v - col
        pick = rand01(row.astype(np.int64), col.astype(np.int64), 78)
        c = STONE[None, None, :] * (0.78 + 0.35 * pick)[..., None] * (0.9 + 0.2 * g)[..., None]
        c = mix(c, np.array([0.68, 0.55, 0.45]), (pick > 0.85) * 0.5)
        m = np.maximum(np.abs(fu - 0.5), np.abs(fv - 0.5)) * 2
        gap = ss(0.78, 0.92, m)
        c = mix(c, STONE * 0.45, gap)
        dome = 0.0025 * np.clip(1 - m ** 3, 0, 1)
        kerb = (1 - ss(0.012, 0.016, np.abs(d - self.hw + 0.012)))
        kc = tone(np.array([0.66, 0.65, 0.62]), g, 0.85, 0.3)
        c = mix(c, kc, kerb)
        # joints between the kerb's long stones
        c = mix(c, STONE * 0.4, kerb * ss(0.9, 0.97, np.abs((s / 0.12) % 1.0 - 0.5) * 2))
        verge, _ = grass_floor(px)
        c = mix(c, verge, ss(self.hw, self.hw + 0.006, d))
        cover = 1 - ss(self.hw + 0.005, self.hw + 0.02, d)
        bx, bz = bump(px, dome * (1 - kerb))
        return c, cover, bx, bz


class Corduroy(Road):
    """Logs across the road on a low bank of brushwood and earth, over a swamp."""
    def __init__(self, title, hw):
        super().__init__(title, hw, swamp=True)

    def height(self, b, d):
        return profile(d, self.hw, bank=0.035)

    def look(self, px, d, s):
        x, z = px["x"], px["z"]
        u = s / 0.042
        k = np.floor(u); fu = u - k
        ki = k.astype(np.int64)
        r1 = rand01(ki, ki * 0 + 1, 81); r2 = rand01(ki, ki * 0 + 2, 82)
        reach = self.hw + 0.035 * (r1 - 0.3)
        log = (d < reach) & (fu > 0.06) & (fu < 0.94)
        cyl = np.sqrt(np.clip(1 - ((fu - 0.5) / 0.44) ** 2, 0, 1))
        bark = 0.5 + 0.5 * perlin(d / 0.08 + 7 * r2, k, 83)
        c = tone(WOOD, bark, 0.75, 0.4) * (0.75 + 0.4 * r2)[..., None]
        c = c * (0.65 + 0.35 * cyl)[..., None]
        # a rotten or missing log now and then, mud showing
        gone = (r1 > 0.93)
        mud = tone(SOIL_WET * 0.8, bark, 0.8, 0.3)
        c = mix(mud, c, (log & ~gone).astype(float))
        # log ends: the pale cut
        end = log & (np.abs(d - reach) < 0.012)
        c = mix(c, np.array([0.70, 0.58, 0.40]), end.astype(float) * 0.8)
        # past the logs' ends: the bank's trodden earth and brushwood
        bank = 1 - ss(self.hw + 0.02, self.hw + 0.12, d)
        earth = tone(mix(SOIL_WET, rocks.GRASS * 0.8, 0.3), bark, 0.8, 0.3)
        c = mix(earth, c, (d < reach + 0.004).astype(float))
        cover = bank
        bx, bz = bump(px, 0.006 * cyl * (log & ~gone))
        return c, cover, bx, bz


class Forest(Dirt):
    """A narrower track through pines: needles on the floor, trees as props."""
    def look(self, px, d, s):
        c, cover, bx, bz = super().look(px, d, s)
        x, z = px["x"], px["z"]
        g = 0.5 + 0.5 * fbm(x / 0.07, z / 0.07, 84, 2)
        floor = mix(tone(NEEDLES, g), tone(rocks.GRASS * 0.8, g), ss(0.0, 0.4, perlin(x / 0.5, z / 0.5, 85)) * 0.6)
        c = mix(floor, c, cover)
        return c, np.ones_like(cover), bx * cover, bz * cover

    def props(self, b, h, df, lines):
        step = 0.24
        xs = np.arange(b.x0 - 0.3, b.x1 + 0.3, step); zs = np.arange(b.z0 - 0.3, b.z1 + 0.3, step)
        X, Z = np.meshgrid(xs, zs)
        ix = np.round(X / step).astype(np.int64); iz = np.round(Z / step).astype(np.int64)
        r = [rand01(ix, iz, 90 + k) for k in range(5)]
        X = X + (r[0] - 0.5) * step; Z = Z + (r[1] - 0.5) * step
        dd = b.sample(df, X, Z)
        keep = (dd > self.hw + 0.18 + 0.15 * r[2]) & (r[3] < 0.42) & (Z > b.z0 + 0.25)
        out = []
        for x, z, a, k in zip(X[keep], Z[keep], r[4][keep], r[2][keep]):
            y = float(b.sample(h, np.array(x), np.array(z)))
            out.append(("pine", x, z, y, 0.26 + 0.16 * a, 0.075 + 0.035 * k))
        return out


class Asphalt(Road):
    """Tarmac: a dark crowned top, patches, cracks, a dashed centre line and
    gravel shoulders."""
    def height(self, b, d):
        return profile(d, self.hw, crown=0.006, ditch=0.02)

    def look(self, px, d, s):
        x, z = px["x"], px["z"]
        g = 0.5 + 0.5 * fbm(x / 0.05, z / 0.05, 86, 2)
        c = tone(TAR, g, 0.88, 0.25)
        patch = ss(0.45, 0.5, 0.5 + 0.5 * perlin(x / 0.25, z / 0.25, 87))
        c = mix(c, TAR * 0.8, patch * 0.7)
        f1, f2, _, _, fid = worley(x, z, 0.09, 88)
        crack = (1 - ss(0.0, 0.0025, f2 - f1)) * ss(0.1, 0.3, perlin(x / 0.4, z / 0.4, 89))
        c = mix(c, TAR * 0.45, crack)
        dash = (1 - ss(0.008, 0.012, px["axis"])) * (((s / 0.3) % 1.0) < 0.5)
        c = mix(c, np.array([0.86, 0.84, 0.76]), dash * 0.85)
        wear = 1 - ss(0.03, 0.06, np.abs(d - 0.25))
        c = c * (1 + 0.08 * wear)[..., None]
        shoulder = ss(self.hw - 0.005, self.hw + 0.005, d)
        sg = tone(GRAVEL * 0.92, g, 0.85, 0.3)
        c = mix(c, sg, shoulder)
        verge, _ = grass_floor(px)
        c = mix(c, verge, ss(self.hw + 0.05, self.hw + 0.07, d))
        cover = 1 - ss(self.hw + 0.14, self.hw + 0.2, d)
        z0 = np.zeros_like(x)
        return c, cover, z0, z0


# Every road is as wide as a hex's side (R): the road a tank drives along
# fills the side it crosses, whatever its type. Half of that is the half-width
# every type takes; its verge, ditches and kerbs lie past it.
SIDE = 0.5

TYPES = {
    "dirt": Dirt("A. грунтовка: две колеи, трава посередине", SIDE),
    "gravel": Gravel("B. гравийное шоссе: щебень, кюветы, столбики", SIDE),
    "cobble": Cobble("C. брусчатка: булыжник рядами, бордюр", SIDE),
    "corduroy": Corduroy("D. гать: брёвна поперёк, насыпь через болото", SIDE),
    "forest": Forest("E. лесная дорога: колея под соснами", SIDE),
    "asphalt": Asphalt("F. асфальт: заплаты, трещины, осевая", SIDE),
}


# ----------------------------------------------------------------- props ----

def draw_props(img, b, props, top_pad):
    from PIL import Image, ImageDraw
    cs, sn = math.cos(ELEV), math.sin(ELEV)

    def at(x, z, y):
        return ((x - b.x0) * PX, (z - b.z0) * sn * PX + top_pad - y * cs * PX)

    def rgb(c, a=255):
        return tuple(int(255 * min(1.0, max(0.0, v))) for v in c) + (a,)
    base = img.convert("RGBA")
    shade = Image.new("RGBA", base.size, (0, 0, 0, 0))
    ds = ImageDraw.Draw(shade)
    off = np.array([-SUN[0] / SUN[1], -SUN[2] / SUN[1]])
    for p in props:
        if p[0] == "pine":
            _, x, z, y, tall, rad = p
            sx, sz = x + off[0] * tall * 0.7, z + off[1] * tall * 0.7
            cx, cy = at(sx, sz, y)
            rx, ry = rad * 1.1 * PX, rad * 1.1 * PX * sn
            ds.ellipse([cx - rx, cy - ry, cx + rx, cy + ry], fill=(20, 25, 10, 110))
    base = Image.alpha_composite(base, shade)
    d = ImageDraw.Draw(base)
    ink = rgb(rocks.INK)
    for p in sorted(props, key=lambda p: p[2]):
        if p[0] == "post":
            _, x, z, y = p
            x0, y0 = at(x, z, y); x1, y1 = at(x, z, y + 0.06)
            d.line([(x0, y0), (x1, y1)], fill=ink, width=5)
            d.line([(x0, y0), (x1, y1)], fill=rgb((0.92, 0.91, 0.86)), width=3)
            xm, ym = at(x, z, y + 0.045)
            d.line([(xm, ym), (x1, y1)], fill=rgb((0.15, 0.15, 0.15)), width=3)
        elif p[0] == "pine":
            _, x, z, y, tall, rad = p
            x0, y0 = at(x, z, y); x1, y1 = at(x, z, y + tall * 0.45)
            d.line([(x0, y0), (x1, y1)], fill=ink, width=5)
            d.line([(x0, y0), (x1, y1)], fill=rgb((0.45, 0.30, 0.20)), width=3)
            # a pine: three stacked tiers, each a triangle, lit from the left
            for k, (lo, hi, w) in enumerate(((0.3, 0.7, 1.0), (0.5, 0.88, 0.75), (0.68, 1.0, 0.5))):
                bxl = at(x - rad * w, z, y + tall * lo); bxr = at(x + rad * w, z, y + tall * lo)
                tip = at(x, z, y + tall * hi)
                mid = at(x, z, y + tall * lo)
                dark = np.array([0.17, 0.27, 0.15]) * (0.9 + 0.1 * k)
                lit = np.array([0.30, 0.42, 0.22]) * (0.9 + 0.1 * k)
                d.polygon([bxl, tip, bxr], fill=rgb(dark), outline=ink)
                d.polygon([bxl, tip, mid], fill=rgb(lit))
                d.line([bxl, tip, bxr], fill=ink, width=2)
    return base.convert("RGB")


# ------------------------------------------------------------------ main ----

def build(b, kind, lines):
    sd, sf = fields_of(b, lines)
    df = np.abs(sd)
    road = TYPES[kind]
    near = 1 - ss(road.hw + 0.1, road.hw + 0.35, df)
    flat = b.h_game + (blur(b.h_game, 0.3) - b.h_game) * near
    h = flat + road.height(b, df)
    sw = None
    if road.swamp:
        sh, sprops, sfields, slook = ground.v_swamp(b)
        # the swamp's own tussocks, kept off the causeway
        keep = ss(road.hw + 0.06, road.hw + 0.2, df)
        h = h + (sh - b.h_game) * keep
        sw = (slook, sprops, keep)
    return h, {"d": df, "sd": sd, "s": sf, "inner": ss(0.45, 0.85, b.sand)}, sw


def main(names):
    from PIL import Image
    lines = network()
    plain = board(); swampy = board(swamp=True)
    shots = []
    for name in names:
        road = TYPES[name]
        b = swampy if road.swamp else plain
        t0 = time.time()
        h, fields, sw = build(b, name, lines)
        for game in (False, True):
            hh, ff = (b.coarse(h), {k: b.coarse(f) for k, f in fields.items()}) if game else (h, fields)

            def look(px):
                # the unsigned distance for everything; the signed one only
                # near the axis - at the fork the sign flips from one line to
                # the other, and read back bilinear that flip is a false zero,
                # a seam of road across the wedge between them, where the
                # unsigned distance says the road is far
                d, s = px["d"], px["s"]
                px["axis"] = np.where(d < 0.06, np.abs(px["sd"]), d)
                c, cover, bx, bz = road.look(px, d, s)
                out = {"colour": c, "cover": cover, "bx": bx * cover, "bz": bz * cover, "flat": 0.0}
                if sw is not None:
                    slook, _, _ = sw
                    px2 = dict(px)
                    px2["inner"] = px["inner"] * ss(road.hw + 0.06, road.hw + 0.2, d)
                    so = slook(px2)
                    k = so["cover"]
                    field, _ = grass_floor(px)
                    out["colour"] = mix(mix(field, so["colour"], k), c, cover)
                    out["cover"] = np.ones_like(cover)
                    out["bx"] = so["bx"] * k * (1 - cover) + bx * cover
                    out["bz"] = so["bz"] * k * (1 - cover) + bz * cover
                    out["flat"] = so["flat"] * k * (1 - cover)
                return out
            img, top_pad = render(b, hh, ff, look, game)
            props = road.props(b, hh, fields["d"], lines)
            if sw is not None:
                stalks = [p for p in sw[1](hh) if b.sample(fields["d"], np.array(p[0]), np.array(p[1])) > road.hw + 0.1]
                draw_stalks(img, b, stalks, top_pad)
            if props:
                img = draw_props(img, b, props, top_pad)
            img = label(img, road.title + (" — растр 1/12" if game else " — тонко"))
            img.save(os.path.join(OUT, f"{name}{'_game' if game else ''}.png"))
            shots.append(img)
        print(f"{name}: {img.size[0]}x{img.size[1]} in {time.time() - t0:.1f}s", flush=True)
    w = max(s.size[0] for s in shots) // 2; hh = max(s.size[1] for s in shots) // 2
    sheet = Image.new("RGB", (w * 2, hh * ((len(shots) + 1) // 2)), (250, 248, 242))
    for i, s in enumerate(shots):
        sheet.paste(s.resize((s.size[0] // 2, s.size[1] // 2), Image.LANCZOS), ((i % 2) * w, (i // 2) * hh))
    sheet.save(os.path.join(OUT, "sheet.png"))


if __name__ == "__main__":
    main(sys.argv[1:] or list(TYPES))
