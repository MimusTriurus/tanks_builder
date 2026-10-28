"""Ground looks on the relief board, rendered off-engine (draft, not the game).

The rocks' board and renderer (rocks.py, next to this one) with the ground made of
something: sand with dunes and ripples, a stony pavement, mud, a swamp. Every
look comes twice - fine (the height on a raster of 1/30 R) and as the game's
mesh would hold it (the height and the masks sampled at 1/12 R and read back
bilinear); the paint and the small bumps stay per pixel either way, as the
shader would compute them.

    python ground.py            -> <repo>/out/ground/<variant>.png and sheet.png
    python ground.py sand mud   -> only these (and their _game twins)
"""
import math
import os
import sys
import time

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rocks  # noqa: E402
from rocks import perlin, fbm, worley, ss, blur, GS, L, SUN, INK, label  # noqa: E402

# the pictures go to the repository's out/ (not in git); the script lives here
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "out", "ground")
os.makedirs(OUT, exist_ok=True)
PX = 110
ELEV = math.radians(35.0)

# A board of 9 x 6: the surface over the middle, grass at the two ends and a
# grass terrace one level up at the bottom left, so the edges show.
rocks.COLS, rocks.ROWS = 9, 6
MAP = {}
for c in range(1, 8):
    for r in range(6):
        MAP[(c, r)] = ("sand", 0)
for c, r in ((0, 3), (0, 4), (0, 5), (1, 4), (1, 5), (2, 5)):
    MAP[(c, r)] = ("plain", 1)
for c, r in ((4, 2),):
    MAP[(c, r)] = ("plain", 1)
rocks.MAP = MAP

WIND = math.radians(25.0)   # the way the wind blows to, as RockStyles.Wind
WX, WZ = math.cos(WIND), math.sin(WIND)


def along(x, z):
    return x * WX + z * WZ


def across(x, z):
    return -x * WZ + z * WX


def grad(f, x, z, e=0.004):
    """A per-pixel height function's slope, numerically: what the shader's
    bump would be from the same noise."""
    return ((f(x + e, z) - f(x - e, z)) / (2 * e), (f(x, z + e) - f(x, z - e)) / (2 * e))


def tone(c, g, lo=0.8, span=0.4):
    return np.asarray(c)[None, None, :] * (lo + span * g)[..., None]


def mix(a, b, t):
    t = np.asarray(t)[..., None] if np.ndim(t) else t
    return a + (b - a) * t


# ------------------------------------------------------------------ sand ----

SAND = np.array([0.86, 0.76, 0.55]); SAND_CREST = np.array([0.93, 0.85, 0.66])
SAND_TROUGH = np.array([0.78, 0.63, 0.43])


def dune(x, z, length=1.5, tall=0.15):
    """Transverse dunes across the wind: a long gentle stoss, a short steep
    slip face, crests broken into barchanoid arcs by a noise along them."""
    a = along(x, z); c = across(x, z)
    phase = a / length + 0.55 * perlin(c / 1.6, a / 3.0, 21) + 0.25 * perlin(c / 0.6, a / 0.9, 22)
    t = phase - np.floor(phase)
    crest = 0.8
    prof = np.where(t < crest, ss(0.0, 1.0, t / crest) ** 0.9, 1.0 - ss(0.0, 1.0, (t - crest) / (1 - crest)))
    amp = np.clip(0.55 + 0.6 * perlin(c / 1.3, a / 2.5, 23), 0.0, 1.0)
    return tall * amp * prof, t


def ripple(x, z):
    """Wind ripples: a few hundredths of R apart, asymmetric like the dunes."""
    a = along(x, z) + 0.05 * perlin(x / 0.35, z / 0.35, 24) + 0.02 * perlin(x / 0.11, z / 0.11, 25)
    t = a / 0.065; t = t - np.floor(t)
    return 0.0035 * np.where(t < 0.7, t / 0.7, (1 - t) / 0.3)


def v_sand(b):
    inner = ss(0.45, 0.85, b.sand)
    d, _ = dune(b.X, b.Z)
    h = b.h_game + blur(d, 0.03) * inner
    gx = np.gradient(h, GS, axis=1); gz = np.gradient(h, GS, axis=0)
    steep = np.hypot(gx, gz)
    fields = {"inner": inner, "steep": steep}

    def look(px):
        x, z = px["x"], px["z"]
        g = 0.5 + 0.5 * fbm(x / 0.1, z / 0.1, 26, 2)
        _, t = dune(x, z)
        c = mix(SAND, SAND_CREST, ss(0.55, 0.8, t) * ss(0.0, 0.03, px["h"] - px["base"]))
        c = mix(c, SAND_TROUGH, (1 - ss(0.0, 0.25, t)) * 0.6)
        c = c * (0.9 + 0.2 * g)[..., None]
        # ripples fade out on the slip faces, where the sand avalanches
        calm = 1 - ss(0.35, 0.6, px["steep"])
        bx, bz = grad(ripple, x, z)
        return {"colour": c, "cover": ss(0.3, 0.6, px["sand"]), "bx": bx * calm, "bz": bz * calm}
    return h, None, fields, look


# ----------------------------------------------------------------- stony ----

PAVE = np.array([0.56, 0.49, 0.40])
PEBBLES = np.array([(0.42, 0.38, 0.34), (0.62, 0.57, 0.50), (0.52, 0.41, 0.32),
                    (0.70, 0.64, 0.56), (0.36, 0.32, 0.29), (0.58, 0.48, 0.40)])
BOULDER = np.array([0.60, 0.57, 0.53])


def boulders(x, z):
    """Scattered boulders a tenth of R across, give or take: a dome on a
    third of the cells."""
    f1, _, _, _, fid = worley(x, z, 0.4, 31)
    rad = 0.05 + 0.05 * (fid / 0.3)
    keep = fid < 0.3
    return np.where(keep, 0.6 * rad * np.sqrt(np.clip(1 - (f1 / rad) ** 2, 0, 1)), 0.0)


def cobbles(x, z):
    f1, _, _, _, fid = worley(x, z, 0.075, 32)
    rad = 0.022 + 0.014 * fid
    keep = fid < 0.3
    return np.where(keep, 0.45 * rad * np.sqrt(np.clip(1 - (f1 / rad) ** 2, 0, 1)), 0.0)


def v_stony(b):
    inner = ss(0.45, 0.85, b.sand)
    h = b.h_game + boulders(b.X, b.Z) * inner
    fields = {"inner": inner}

    def look(px):
        x, z = px["x"], px["z"]
        g = 0.5 + 0.5 * fbm(x / 0.1, z / 0.1, 33, 2)
        # the pavement: fine gravel, each pebble a colour of its own
        f1, f2, _, _, fid = worley(x, z, 0.028, 34)
        pick = PEBBLES[(fid * len(PEBBLES)).astype(int) % len(PEBBLES)]
        gap = 1 - ss(0.0, 0.006, f2 - f1)
        c = mix(mix(pick, PAVE, 0.45), PAVE * 0.75, gap * 0.6)
        # bare patches of sandy soil between the gravel
        bare = ss(0.1, 0.35, fbm(x / 0.6, z / 0.6, 35, 2))
        c = mix(c, PAVE * (0.85 + 0.3 * g)[..., None], bare * 0.8)
        # cobbles: lighter, with a bump, and the boulders' grey
        cb = cobbles(x, z)
        c = mix(c, tone(PEBBLES[3], g, 0.85, 0.3), ss(0.0, 0.003, cb) * (1 - bare * 0.7))
        bd = boulders(x, z)
        c = mix(c, tone(BOULDER, g, 0.75, 0.45), ss(0.0, 0.006, bd))
        bx, bz = grad(cobbles, x, z)
        k = (1 - bare * 0.7)
        if px["game"]:
            # the boulders the mesh lost, kept as a bump the shader computes
            gx, gz = grad(boulders, x, z)
            bx = bx * k + gx * px["inner"]; bz = bz * k + gz * px["inner"]
        else:
            bx = bx * k; bz = bz * k
        return {"colour": c, "cover": ss(0.3, 0.6, px["sand"]), "bx": bx, "bz": bz}
    return h, None, fields, look


# ------------------------------------------------------------------- mud ----

MUD_WET = np.array([0.33, 0.25, 0.18]); MUD_DRY = np.array([0.52, 0.42, 0.31])
PUDDLE = np.array([0.31, 0.27, 0.21]); SKY = np.array([0.80, 0.84, 0.86])


def puddle_n(x, z):
    return perlin(x / 0.55, z / 0.55, 41) + 0.35 * perlin(x / 0.15, z / 0.15, 42)


def v_mud(b):
    inner = ss(0.45, 0.85, b.sand)
    # the hollows the water stands in, deep enough that their banks show
    dip = -0.08 * ss(0.3, 0.62, puddle_n(b.X, b.Z))
    h = b.h_game + blur(dip, 0.03) * inner
    fields = {"inner": inner}

    def look(px):
        x, z = px["x"], px["z"]
        g = 0.5 + 0.5 * fbm(x / 0.08, z / 0.08, 43, 2)
        pn = puddle_n(x, z)
        damp = ss(-0.4, 0.3, pn)
        c = mix(MUD_DRY, MUD_WET, damp) * (0.85 + 0.3 * g)[..., None]
        # cracks where it has dried
        f1, f2, _, _, _ = worley(x, z, 0.07, 44)
        crack = (1 - ss(0.0, 0.005, f2 - f1)) * (1 - damp)
        c = mix(c, MUD_WET * 0.6, crack * 0.8)
        # the wet bank under the waterline's edge, then the water, darker
        # where the hollow is deeper; judged per pixel
        c = mix(c, MUD_WET * 0.7, ss(0.36, 0.44, pn) * px["inner"])
        wet = ss(0.44, 0.47, pn) * px["inner"]
        sheen = ss(0.2, 0.7, perlin(x / 0.3 + z / 0.9, z / 0.12, 45))
        water = mix(PUDDLE, SKY, 0.12 + 0.3 * sheen)
        water = mix(water, PUDDLE * 0.55, ss(0.5, 0.7, pn))
        c = mix(c, water, wet)
        zero = np.zeros_like(x)
        return {"colour": c, "cover": ss(0.3, 0.6, px["sand"]), "bx": zero, "bz": zero, "flat": wet}
    return h, None, fields, look


# ----------------------------------------------------------------- swamp ----

PEAT = np.array([0.24, 0.19, 0.13]); SEDGE = np.array([0.42, 0.46, 0.20])
SEDGE_TIP = np.array([0.66, 0.63, 0.34]); BOG = np.array([0.15, 0.19, 0.16])
WEED = np.array([0.46, 0.58, 0.17]); REED = np.array([0.30, 0.36, 0.14])


def tussocks(x, z):
    """Hummocks a tenth of R across, and islands of sedge they crowd on."""
    f1, _, _, _, fid = worley(x, z, 0.2, 51)
    rad = 0.05 + 0.04 * fid
    lone = fid < 0.4 * ss(-0.3, 0.3, perlin(x / 0.8, z / 0.8, 50))
    one = 0.045 * np.clip(1 - (f1 / rad) ** 2, 0, 1) ** 0.6 * lone
    isle = 0.035 * ss(0.05, 0.4, fbm(x / 1.1, z / 1.1, 52, 2))
    return one + isle - 0.022


def reed_clump(x, z, t):
    """Where reeds grow: in clumps, at the water's edge."""
    return ss(0.2, 0.45, perlin(x / 0.5, z / 0.5, 55)) * (1 - ss(0.006, 0.025, np.abs(t)))


REED_HEAD = np.array([0.40, 0.26, 0.15])


def reeds(b, h, inner, step=0.016):
    """Reed stalks as props: a root on the ground or the water, a height of
    a tenth of R and more, a lean; a quarter carry a cattail's head."""
    xs = np.arange(b.x0, b.x1, step); zs = np.arange(b.z0, b.z1, step)
    X, Z = np.meshgrid(xs, zs)
    ix = np.round(X / step).astype(np.int64); iz = np.round(Z / step).astype(np.int64)
    r = [rocks.rand01(ix, iz, 61 + k) for k in range(6)]
    X = X + (r[0] - 0.5) * step; Z = Z + (r[1] - 0.5) * step
    t = tussocks(X, Z)
    keep = r[2] < 0.85 * reed_clump(X, Z, t) * b.sample(inner, X, Z)
    X, Z, t = X[keep], Z[keep], t[keep]
    r = [k[keep] for k in r]
    root = np.maximum(b.sample(h, X, Z), b.sample(b.h_game, X, Z))
    tall = 0.09 + 0.09 * r[3]
    lean = (r[4] - 0.5) * 0.5
    order = np.argsort(Z)
    return [(X[i], Z[i], root[i], tall[i], lean[i] * tall[i], r[5][i] < 0.25) for i in order]


def draw_stalks(img, b, stalks, top_pad):
    from PIL import ImageDraw
    d = ImageDraw.Draw(img)
    cs, sn = math.cos(ELEV), math.sin(ELEV)

    def at(x, z, y):
        return ((x - b.x0) * PX, (z - b.z0) * sn * PX + top_pad - y * cs * PX)

    def rgb(c):
        return tuple(int(255 * min(1.0, max(0.0, v))) for v in c)
    ink = rgb(REED * 0.45)
    for x, z, y, tall, lean, head in stalks:
        x0, y0 = at(x, z, y)
        x1, y1 = at(x + lean, z, y + tall)
        xm, ym = at(x + lean * 0.3, z, y + tall * 0.45)
        d.line([(x0, y0), (xm, ym), (x1, y1)], fill=ink, width=3)
        d.line([(x0, y0), (xm, ym)], fill=rgb(REED * 0.85), width=2)
        d.line([(xm, ym), (x1, y1)], fill=rgb((REED + SEDGE_TIP) * 0.55), width=2)
        if head:
            ha = at(x + lean * 0.72, z, y + tall * 0.72)
            hb = at(x + lean * 0.9, z, y + tall * 0.9)
            d.line([ha, hb], fill=ink, width=6)
            d.line([ha, hb], fill=rgb(REED_HEAD), width=4)


def v_swamp(b):
    inner = ss(0.45, 0.85, b.sand)
    h = b.h_game + tussocks(b.X, b.Z) * inner
    fields = {"inner": inner}
    props = lambda hh: reeds(b, hh, inner)  # noqa: E731

    def look(px):
        x, z = px["x"], px["z"]
        g = 0.5 + 0.5 * fbm(x / 0.06, z / 0.06, 53, 2)
        t = tussocks(x, z)
        # the water stands at the board's level; what pokes over it is land,
        # decided per pixel from the same hummocks the fine height has
        land = ss(-0.002, 0.004, t) + (1 - px["inner"])
        land = np.clip(land, 0, 1)
        top = ss(0.004, 0.03, t)
        blades = 0.5 + 0.5 * perlin(x / 0.012, z / 0.05, 54)
        c = mix(tone(SEDGE, g), tone(SEDGE_TIP, g), top * ss(0.4, 0.8, blades))
        c = mix(PEAT * (0.8 + 0.4 * g)[..., None], c, ss(0.0, 0.006, t))
        # the reeds' feet: a dark ring of rot under each clump
        c = mix(c, PEAT * 0.8, 0.6 * reed_clump(x, z, t) * px["inner"])
        # open water: dark, duckweed rafts, the sky's sheen
        weed = ss(0.15, 0.3, fbm(x / 0.6, z / 0.6, 57, 3)) * (0.75 + 0.25 * ss(0.3, 0.7, 0.5 + 0.5 * perlin(x / 0.03, z / 0.03, 58)))
        sheen = ss(0.3, 0.8, perlin(x / 0.4 + z / 1.1, z / 0.15, 59))
        water = mix(mix(BOG, SKY * 0.7, 0.18 * sheen), tone(WEED, g, 0.8, 0.4), weed)
        c = mix(water, c, land)
        bx, bz = grad(tussocks, x, z, 0.003)
        k = px["inner"] * land * (0.35 if not px["game"] else 0.8)
        return {"colour": c, "cover": ss(0.3, 0.6, px["sand"]), "bx": bx * k, "bz": bz * k,
                "flat": 1 - land}
    return h, props, fields, look


VARIANTS = {
    "sand": ("A. песок: дюны поперёк ветра, рябь", v_sand),
    "stony": ("B. каменистая: мостовая из гальки, булыжник, валуны", v_stony),
    "mud": ("C. грязь: глубокие лужи, трещины", v_mud),
    "swamp": ("D. болото: кочки, вода, ряска, камыш", v_swamp),
}


# -------------------------------------------------------------- renderer ----

GRASS = rocks.GRASS; GRASS_TIP = rocks.GRASS_TIP; SOIL = rocks.SOIL


def render(b, h, fields, look, game):
    W = int((b.x1 - b.x0) * PX)
    cs, sn = math.cos(ELEV), math.sin(ELEV)
    top_pad = (h.max() + 0.1) * cs * PX
    H = int((b.z1 - b.z0) * sn * PX + top_pad - (min(h.min(), 0) * cs * PX)) + 4
    xs = b.x0 + (np.arange(W) + 0.5) / PX
    zrows = b.Z[:, 0]
    nz = len(zrows)

    def cols(f):
        fx = (xs - b.X[0, 0]) / GS
        i = np.clip(fx.astype(int), 0, f.shape[1] - 2); u = fx - i
        return f[:, i] * (1 - u) + f[:, i + 1] * u

    hc = cols(h)
    ys = (zrows[:, None] - b.z0) * sn * PX + top_pad - hc * cs * PX
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
    px = {"x": np.broadcast_to(xs[None, :], (H, W)).copy(), "z": at(b.Z), "h": at(h),
          "base": at(b.h_game), "sand": at(b.sand), "game": game}
    for k, f in fields.items():
        px[k] = at(f)
    lk = look(px)
    cover = lk["cover"]
    nx = -(at(gx) + lk["bx"] * cover); nzv = -(at(gz) + lk["bz"] * cover)
    flat = lk.get("flat", 0.0) * cover
    nx = nx * (1 - flat); nzv = nzv * (1 - flat)
    n = np.stack([nx, np.ones((H, W)), nzv], -1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    hl = np.hypot(n[..., 0], n[..., 2])
    shade = at(rocks.shadow(b, h))
    side = at(b.side); on = at(b.on.astype(float))

    # the ground off the surface: the relief's grass and earth
    g = 0.5 + 0.5 * fbm(px["x"] / 0.12, px["z"] / 0.12, 95, 2)
    patch = fbm(px["x"] / 1.2, px["z"] / 1.2, 96, 2)
    turf = GRASS + (GRASS_TIP - GRASS) * ss(-0.2, 0.5, fbm(px["x"] / 0.5, px["z"] / 0.5, 97, 2))[..., None]
    flat_r = 1 - ss(0.2, 0.32, hl)
    lawn = np.maximum(flat_r * ss(-0.35, -0.15, patch), 0.0)
    ground = SOIL * (0.75 + 0.5 * g)[..., None]
    ground = ground + (turf * (0.8 + 0.4 * g)[..., None] - ground) * lawn[..., None]
    colour = mix(ground, lk["colour"], cover)

    d = n @ SUN; fd = SUN[1]
    step = np.where(d > fd - 0.08, 1.0, np.where(d > fd - 0.3, 0.86, 0.72))
    step = np.where(hl > 0.8, np.minimum(step, 0.56), step)
    step = np.where(shade > 0.5, np.minimum(step * 0.82, 0.72), step)
    colour = colour * step[..., None]

    line = (1 - ss(0.018, 0.03, side)) * on * (1 - ss(0.45, 0.7, hl))
    colour = colour + (np.array([0.12, 0.14, 0.07]) - colour) * (0.3 * line)[..., None]

    zz = px["z"]
    edge = np.zeros((H, W), bool)
    edge[:-1] |= np.abs(np.diff(zz, axis=0)) > 0.10
    dzh = np.abs(np.diff(zz, axis=1)) > 0.10
    edge[:, :-1] |= dzh & (zz[:, :-1] < zz[:, 1:])
    edge[:, 1:] |= dzh & (zz[:, 1:] < zz[:, :-1])
    colour = np.where(edge[..., None], INK, colour)
    colour = np.where(valid[..., None], colour, np.array([0.93, 0.92, 0.89]))
    from PIL import Image
    return Image.fromarray((np.clip(colour, 0, 1) * 255).astype(np.uint8)), top_pad


def on_game_raster(b, h, fields):
    """The height and the masks as the game's raster (1/12 R) holds them."""
    return b.coarse(h), {k: b.coarse(f) for k, f in fields.items()}


def main(names):
    from PIL import Image
    b = rocks.Board()
    shots = []
    for name in names:
        title, fn = VARIANTS[name]
        t0 = time.time()
        h, props, fields, look = fn(b)
        for game in (False, True):
            hh, ff = on_game_raster(b, h, fields) if game else (h, fields)
            img, top_pad = render(b, hh, ff, look, game)
            if props is not None:
                draw_stalks(img, b, props(hh), top_pad)
            img = label(img, title + (" — растр 1/12" if game else " — тонко"))
            img.save(os.path.join(OUT, f"{name}{'_game' if game else ''}.png"))
            shots.append(img)
        print(f"{name}: {img.size[0]}x{img.size[1]} in {time.time() - t0:.1f}s", flush=True)
    w = max(s.size[0] for s in shots) // 2; hh = max(s.size[1] for s in shots) // 2
    sheet = Image.new("RGB", (w * 2, hh * ((len(shots) + 1) // 2)), (250, 248, 242))
    for i, s in enumerate(shots):
        sheet.paste(s.resize((s.size[0] // 2, s.size[1] // 2), Image.LANCZOS), ((i % 2) * w, (i // 2) * hh))
    sheet.save(os.path.join(OUT, "sheet.png"))


if __name__ == "__main__":
    main(sys.argv[1:] or list(VARIANTS))
