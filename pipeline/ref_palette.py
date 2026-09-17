"""Bring the reference art to one palette - green paint and grey steel.

`report()` measures every reference. `normalize()` writes the corrected copies.
Pure numpy over PIL, no Blender: recolouring is a function of pixels, the same
argument `atlas_pack.repack_dir` and `water_sheet.py` are built on, so it runs
in seconds and needs nothing open.

**This is the upstream half of the job, and the two halves are not
interchangeable.** `paint_norm` corrects a material on a scene that already
exists; it cannot help `TD` and `SPG`, which have no scene yet - and by the time
they do, the colour is baked into a generated texture and the model has been
split by hand, so fixing it there is the expensive end. The reference is the
cheap end: correct it *before* generation and the model comes out on target.

**That this works at all is a measurement, not a hope.** The generator carries
the reference's hue through with a constant offset:

    reference    glb albedo
    LT  101.5 -> 110.0   +8.5
    MT   97.8 -> 107.1   +9.3
    HT  104.7 -> 109.5   +4.8      mean +7.5, sd 2.0

So references driven to one hue give models within a few degrees of each other,
against the 24 degrees the references span today. The residual is what
`paint_norm` is for; between them the two cover a tank at either stage of its
life. What the offset is *made of* is not known from here - it is the generator
and the glTF import together - so it is quoted as measured and not modelled.

**The references already shipped are deliberately not the target's source of
truth for regeneration.** `LT`, `MT` and `HT` have models that were split by
hand; regenerating them to fix colour would throw that work away for a fault
`paint_norm` closes in the scene. Their references are still worth correcting,
so the folder is consistent and the next generation from them starts right, but
nothing downstream of them should be rebuilt on account of it.

**Worked in display sRGB, and here that is correct rather than sloppy.**
`paint_norm` has to work in linear because the node it drives does. This is flat
art with no lighting model in it: the image *is* the display values, an eye is
the only thing that will look at it, and a linear detour would only add two
conversions that cancel.

**The ink is a cluster, not a thing to step around, and that was a mistake worth
recording.** It is kept out of the *paint's* medians - between 47 and 70 per
cent of the opaque pixels of these files are outline, so a green measured over
it measures the line (`brick_wall` paid three passes for this exact lesson on
this exact kind of reference, and the threshold here is the one it settled on).
The first pass took the next step too and left the ink alone entirely, on the
grounds that the line is the drawing rather than the paint. That is wrong by its
consequence: the line is not black, it is a *coloured* line at saturation 0.45
to 0.63, and it sat twenty to thirty degrees apart between these references -
`TD` and `SPG` at hue 86 to 90 against 107 to 116 for the rest. Correcting the
paint and not the ink leaves the old palette exactly where there is most of it
to see. So it gets a ladder of its own, all three channels.

**Membership is soft, and a hard mask is the thing it is avoiding.** Classifying
each pixel as green or steel and applying two different transforms leaves a seam
wherever the two meet, because the antialiased pixels along that border get one
answer or the other and nothing in between. A weight that falls off smoothly
gives those pixels a blend of both corrections, which is what they are.

**Whole ladders are matched, not medians, and that distinction is the
difference between "the same average green" and "the same green".** Landing
each picture's median on one number leaves five greens that agree at one point
and nowhere else, and the eye reads the rest. Measured after a median-only
pass, the highlights still spanned 0.561 to 0.761 of value and the hue fans
ran from 5 degrees wide to 17 - `SPG`'s green ramp was half again as long as
`LT`'s. So every channel of a cluster goes on a shared tone ladder: the
quantiles are mapped rung to rung, which is monotone, so a shadow stays darker
than the tone beside it and nothing posterises - what changes is the spacing.
Afterwards the five agree at *every* percentile, highlight spread 0.004 instead
of 0.200 and hue fan 0.9 degrees instead of 9.6.

**The ladder comes from `CONFIG["anchors"]`, and one name there means one tank
is the base.** It ships as `["MT"]`: every other reference is brought to the
medium's greens, its steel and its line. The medium's own green and ink come
back untouched - matching a ladder to itself is the identity, and it measures
as one - and so does every pixel neither cluster claims. Its *steel* does move,
by up to 32 levels, and that is the one deliberate exception: steel hue is set
rather than matched, so even the anchor's grey is collapsed onto a single angle.
Worth knowing before using a diff against the anchor as a test. Name several
anchors instead and the
ladder is their average, taken rung by rung: the quantile function is what gets
averaged, so the result is a ramp somebody could have painted rather than a
blur of three.

**Hue targets are `None` by default, meaning "wherever the anchors already
sit".** That is what makes anchoring on one tank mean *its* colours rather than
its ladder slid onto a number from somewhere else. Put a number in `CONFIG` and
every reference goes there, the anchors included.

**Steel has its hue set while green and ink have theirs matched, and the
difference is the saturation.** The green sits at 0.47 to 0.62 and the ink at
0.45 to 0.63 - real colours whose fans are worth carrying across. The steel sits
at 0.04 to 0.14, where hue is mostly noise: `HT`'s grey reads 252 degrees at
saturation 0.041, which is not a colour, it is a rounding error with an angle
attached. A ladder built from that spreads the noise evenly over every picture;
setting it replaces noise with the target. Its saturation and value still go on
the ladder like everything else.

**What is left over is line density, and it is not a colour at all.** With the
paint and the ink both matched, the silhouettes still read at different
lightness - 0.259 for `SPG` against 0.312 for `LT` - and the whole of that is
composition: `SPG` spends 70 per cent of its drawn area on line where `LT`
spends 47, and mixing the two matched tones in those proportions predicts both
numbers to within 0.005. Closing it would mean thinning somebody's linework,
which is redrawing rather than recolouring, and is deliberately not done here.

**What this does not do.** It repaints nothing the artist drew as a third
colour: shell bands, headlights and stowage keep their own hues by
construction, because the windows come from the measured clusters rather than
being declared. And it is a recolour, not a redraw - brush texture, panel
lines and every shape stay exactly where they were.

The authority is the picture: `sheet()` writes a before-and-after contact sheet,
and that is what the change should be judged on.
"""

import os

import numpy as np
from PIL import Image

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "../assets/Images")

CONFIG = {
    # The references, in the order the contact sheet lays them out. `None`
    # means "every image under `root`" - walked recursively, tagged by its
    # filename stem, already-normalized copies (ending in `suffix`) and the
    # contact sheet itself skipped. Give an explicit list of (tag, rel_path)
    # here to pin it down instead.
    "refs": None,
    # Where each cluster's hue is driven. `None` means "wherever the anchors
    # already sit", which is what makes anchoring on a single tank mean its own
    # colours rather than its ladder slid onto a number from somewhere else.
    # Give a number here to drive every reference, anchors included, onto it.
    "green": {"hue": None},
    "steel": {"hue": None},
    "ink": {"hue": None},

    # Ink is anything darker than this in every channel. `brick_wall` settled on
    # the same number against the same kind of art.
    "ink_below": 90 / 255.0,
    # Paper: unsaturated and bright. Kept off both transforms.
    "paper_above": 0.90,

    # Which references define the shared ladder. One name means "everything
    # comes to this tank"; several means the ladder is their average, rung by
    # rung. `MT` is the base because it is the middle class and the one whose
    # atlas the board already borrows when a layer is judged. A tag here is
    # matched against the filename stem `refs` produced, so with auto-discovery
    # the anchor file's name (without extension) must be exactly "MT".
    "anchors": ["LT_0"],

    "suffix": "_norm",

    # Where `refs` paths are resolved from. `None` means the default
    # `assets/Images` next to this file.
    "root": None,
    # Where `normalize()` writes corrected copies and `sheet()` writes the
    # contact sheet. `None` means next to the source file, as before.
    "out_dir": None,
}

# How membership falls off. Green is gated on being saturated and not ink;
# steel on being desaturated and in the mid tones, which is what excludes the
# paper above it and the outline below it without naming either.
_GREEN_SAT = (0.20, 0.32)      # below the first, no weight; above the second, full
_GREEN_VAL = (0.12, 0.22)      # keeps the darkest shading out of the median
_GREEN_HUE_PAD = 14.0          # degrees of falloff beyond the window
_GREEN_HUE_MIN = 16.0          # half-window floor, for art whose green is flat
_STEEL_SAT = (0.16, 0.28)      # above the second, no weight
_STEEL_VAL = (0.28, 0.85)      # between the ink and the paper
_INK_PAD = 0.08                # how far above the ink threshold it fades out

# Rungs in a tone ladder. 64 resolves the shape of a ramp far finer than an
# eye reads it, and the mapping between two ladders is interpolated anyway.
_LADDER_BINS = 64


def _rgb_to_hsv(rgb):
    """Hue in degrees, saturation, value. On 0..1 rgb."""
    mx = rgb.max(-1)
    mn = rgb.min(-1)
    d = mx - mn
    s = np.where(mx > 1e-6, d / np.maximum(mx, 1e-6), 0.0)
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    h = np.zeros_like(mx)
    nz = d > 1e-6
    h = np.where(nz & (mx == r), ((g - b) / np.maximum(d, 1e-6)) % 6, h)
    h = np.where(nz & (mx == g), (b - r) / np.maximum(d, 1e-6) + 2, h)
    h = np.where(nz & (mx == b), (r - g) / np.maximum(d, 1e-6) + 4, h)
    return h * 60.0, s, mx


def _hsv_to_rgb(h, s, v):
    h = np.mod(h, 360.0) / 60.0
    i = np.floor(h)
    f = h - i
    p = v * (1.0 - s)
    q = v * (1.0 - s * f)
    t = v * (1.0 - s * (1.0 - f))
    i = np.mod(i, 6).astype(np.int32)
    r = np.choose(i, [v, q, p, p, t, v])
    g = np.choose(i, [t, v, v, q, p, p])
    b = np.choose(i, [p, p, t, v, v, q])
    return np.clip(np.stack([r, g, b], axis=-1), 0.0, 1.0)


def _ramp(x, lo, hi):
    """Smoothstep from 0 at `lo` to 1 at `hi`. Handles hi < lo as a fall."""
    if hi == lo:
        return (x >= hi).astype(np.float64)
    t = np.clip((x - lo) / (hi - lo), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def _arc(a, b):
    """Signed shortest angle from `a` to `b`, degrees."""
    return (b - a + 180.0) % 360.0 - 180.0


def _weights(rgb, hue, sat, val, green_centre, green_half, cfg):
    """How much of each correction every pixel gets. All three fall off smoothly.

    The ink is claimed first and the other two get what is left, because the
    line is drawn *over* the paint: a pixel dark enough to be line is line, and
    the gradient along its edge should read as the line thinning rather than as
    two corrections arguing.
    """
    ink = 1.0 - _ramp(rgb.max(-1), cfg["ink_below"], cfg["ink_below"] + _INK_PAD)

    near = np.abs(_arc(green_centre, hue))
    green = (_ramp(sat, *_GREEN_SAT)
             * _ramp(val, *_GREEN_VAL)
             * (1.0 - _ramp(near, green_half, green_half + _GREEN_HUE_PAD)))

    steel = ((1.0 - _ramp(sat, *_STEEL_SAT))
             * _ramp(val, _STEEL_VAL[0], _STEEL_VAL[0] + 0.10)
             * (1.0 - _ramp(val, _STEEL_VAL[1] - 0.10, _STEEL_VAL[1])))

    # Nothing is two things at once; where the gates overlap, the stronger claim
    # wins and the other gets what is left, so the three never sum past one and
    # no pixel receives a correction and a half.
    green = np.minimum(green, 1.0 - ink)
    steel = np.minimum(steel, 1.0 - ink - green)
    return green, steel, ink


def _load(path):
    image = Image.open(path).convert("RGBA")
    a = np.asarray(image).astype(np.float64) / 255.0
    return a[..., :3], a[..., 3]


def _clusters(rgb, alpha, cfg):
    """Where the green and the steel are in this picture, and how wide."""
    hue, sat, val = _rgb_to_hsv(rgb)
    solid = alpha > 0.9
    ink = rgb.max(-1) < cfg["ink_below"]
    paper = (sat < 0.10) & (val > cfg["paper_above"])
    body = solid & ~ink & ~paper

    # The green is the saturated part of the body; its own spread sets the
    # window, so art whose green is flat gets a tight one and art whose green
    # is mottled gets a loose one, and neither is declared.
    green = body & (sat > 0.25)
    if green.sum() < 200:
        return None
    gh = hue[green]
    centre = float(np.median(gh))
    spread = float(np.percentile(gh, 75) - np.percentile(gh, 25))
    half = max(_GREEN_HUE_MIN, 3.0 * spread)

    steel = body & (sat < 0.20) & (val > _STEEL_VAL[0]) & (val < _STEEL_VAL[1])
    # The ink is a cluster in its own right, not a thing to step around. It is
    # a *coloured* line - saturation 0.45 to 0.63 - and it differs between these
    # references by twenty to thirty degrees of hue while covering half to two
    # thirds of the picture. Left alone, it keeps the old palette exactly where
    # there is most of it to see.
    return {"hue": hue, "sat": sat, "val": val, "body": body, "solid": solid,
            "green": green, "steel": steel, "ink": solid & ink,
            "centre": centre, "half": half,
            "green_share": float(green.sum()) / max(int(solid.sum()), 1),
            "steel_share": float(steel.sum()) / max(int(solid.sum()), 1),
            "ink_share": float((solid & ink).sum()) / max(int(solid.sum()), 1)}


def _median(values, mask):
    return float(np.median(values[mask])) if mask.sum() > 200 else float("nan")


def stats(path, cfg=None):
    """What one reference measures. `None` if there is no paint in it."""
    cfg = cfg or CONFIG
    rgb, alpha = _load(path)
    c = _clusters(rgb, alpha, cfg)
    if c is None:
        return None
    out = {"path": path}
    for name in ("green", "steel"):
        mask = c[name]
        out[name] = {
            "hue": _median(c["hue"], mask),
            "sat": _median(c["sat"], mask),
            "value": _median(c["val"], mask),
            "share": c[name + "_share"],
        }
    out["ink_share"] = c["ink_share"]
    out["window"] = (c["centre"], c["half"])
    return out


def _ladder(values, mask, bins=_LADDER_BINS):
    """The tone ladder of a cluster: its quantiles, darkest to lightest.

    This is the whole shape of the paint, not a number standing in for it -
    where the deepest shadow sits, where the base tone sits, how far the
    highlight reaches. Two pictures can share a median and still be different
    greens, which is exactly what matching medians left behind.
    """
    if mask.sum() < 200:
        return None
    return np.percentile(values[mask], np.linspace(0.0, 100.0, bins))


def _rising(q):
    """A ladder made strictly rising, so it can be interpolated through.

    Flat runs are real - a picture can spend a lot of its area on one tone -
    but `np.interp` needs a rising x, so ties get the smallest nudge that
    separates them. It moves no tone by anything an eye could find.
    """
    out = np.array(q, dtype=np.float64)
    step = np.arange(len(out)) * 1e-9
    return np.maximum.accumulate(out + step)


def _remap(values, source, target):
    """Put each value where it stands in `source`, read it off `target`.

    Monotone by construction, so the drawing survives: a shadow stays darker
    than the tone beside it and nothing inverts or posterises. What changes is
    the spacing - a ladder that ran short gets stretched, one that ran long
    gets packed - and that is the whole of making two greens the same green.
    """
    steps = np.linspace(0.0, 1.0, len(source))
    stand = np.interp(values, _rising(source), steps)
    return np.interp(stand, steps, target)


def ladders(cfg=None):
    """The one ladder every reference is brought to, per cluster and channel.

    Built by averaging the anchors' ladders rung by rung - the quantile
    function is what gets averaged, not the pixels, so the result is a real
    ladder somebody could have painted rather than a blur of five. Anchored on
    the references that already have models, because those are the shipped look
    and the two new classes should come to them rather than drag them.

    Cached: five pictures get decoded to build it and then it does not change.
    """
    cfg = cfg or CONFIG
    key = (_root(cfg), tuple(cfg["anchors"]))
    cache = ladders.__dict__.setdefault("_cache", {})
    if key in cache:
        return cache[key]

    wanted = (("green", "hue"), ("green", "sat"), ("green", "value"),
              ("steel", "sat"), ("steel", "value"),
              ("ink", "hue"), ("ink", "sat"), ("ink", "value"))
    stack = {k: [] for k in wanted}
    steel_hue = []
    for tag, rel in _refs(cfg):
        if tag not in cfg["anchors"]:
            continue
        rgb, alpha = _load(os.path.join(_root(cfg), rel))
        c = _clusters(rgb, alpha, cfg)
        if c is None:
            continue
        pick = {"hue": c["hue"], "sat": c["sat"], "value": c["val"]}
        for cluster, ch in wanted:
            rung = _ladder(pick[ch], c[cluster])
            if rung is not None:
                stack[(cluster, ch)].append(rung)
        steel_hue.append(_median(c["hue"], c["steel"]))

    out = {k: (np.mean(v, axis=0) if v else None) for k, v in stack.items()}

    # A hue ladder is averaged and then slid bodily onto the named target, so
    # the fan the anchors share is kept while the colour it fans around is the
    # one `CONFIG` asks for. `None` there means "leave it where the anchors
    # are", which is what makes a single anchor mean that tank's own colours.
    for cluster in ("green", "ink"):
        rung = out[(cluster, "hue")]
        want = cfg.get(cluster, {}).get("hue")
        if rung is not None and want is not None:
            out[(cluster, "hue")] = rung + (want - float(np.median(rung)))

    want = cfg.get("steel", {}).get("hue")
    good = [h for h in steel_hue if np.isfinite(h)]
    out["steel_hue"] = want if want is not None else (
        float(np.median(good)) if good else None)
    cache[key] = out
    return out


def convert(path, cfg=None):
    """The corrected image, plus what it measured before and after."""
    cfg = cfg or CONFIG
    rgb, alpha = _load(path)
    c = _clusters(rgb, alpha, cfg)
    if c is None:
        return None
    target = ladders(cfg)

    hue, sat, val = c["hue"], c["sat"], c["val"]
    w_green, w_steel, w_ink = _weights(rgb, hue, sat, val,
                                       c["centre"], c["half"], cfg)
    # The paper never moves - it is not part of the drawing at all, and a
    # reference with a tinted background teaches the generator a background.
    keep = c["solid"]
    w_green = np.where(keep, w_green, 0.0)
    w_steel = np.where(keep, w_steel, 0.0)
    w_ink = np.where(keep, w_ink, 0.0)

    def seen(h, s, v):
        return {name: {"hue": _median(h, c[name]), "sat": _median(s, c[name]),
                       "value": _median(v, c[name]),
                       "ladder": _ladder(v, c[name])}
                for name in ("green", "steel", "ink")}

    before = seen(hue, sat, val)

    channel = {"hue": hue, "sat": sat, "value": val}

    def full(cluster, names, set_hue=None):
        """This cluster's correction applied *whole*, ignoring its weight.

        Each cluster is transformed from the original pixel rather than from
        what the previous one left, so the three are genuinely independent and
        the order they are written in cannot matter.
        """
        moved = dict(channel)
        for ch in names:
            rung = target[(cluster, ch)]
            mine = _ladder(channel[ch], c[cluster])
            if rung is None or mine is None:
                continue
            moved[ch] = _remap(channel[ch], mine, rung)
        if set_hue is not None:
            moved["hue"] = moved["hue"] + _arc(moved["hue"], set_hue)
        return _hsv_to_rgb(moved["hue"], np.clip(moved["sat"], 0.0, 1.0),
                           np.clip(moved["value"], 0.0, 1.0))

    # Green: every channel goes on the shared ladder, so what matches is the
    # whole ramp - deepest shadow, base tone, reach of the highlight - and not
    # one number standing in for it.
    # Ink: a ladder of its own, all three. It is a coloured line and most of the
    # picture, and left out it keeps the old palette where there is most to see.
    # Steel: hue *set* rather than matched, because at saturation 0.04 the angle
    # it carries is noise and a ladder built from noise spreads it evenly.
    parts = ((full("green", ("hue", "sat", "value")), w_green),
             (full("ink", ("hue", "sat", "value")), w_ink),
             (full("steel", ("sat", "value"), target.get("steel_hue")), w_steel))

    # Mixed in *rgb*, weights summing to one with the original taking the rest.
    # Blending inside hsv instead would send every pixel through a round trip
    # whether it was being corrected or not, and 8-bit rounding on the way back
    # moved untouched paint by up to six levels - which quietly broke the one
    # thing this was supposed to guarantee, that a colour nobody asked about
    # comes out exactly as it went in.
    rest = 1.0
    out = np.zeros_like(rgb)
    for moved, weight in parts:
        out += moved * weight[..., None]
        rest = rest - weight
    out = np.clip(out + rgb * np.clip(rest, 0.0, 1.0)[..., None], 0.0, 1.0)
    after = seen(*_rgb_to_hsv(out))

    rgba = np.concatenate([out, alpha[..., None]], axis=-1)
    image = Image.fromarray((np.clip(rgba, 0, 1) * 255.0 + 0.5).astype(np.uint8),
                            mode="RGBA")
    return {"image": image, "before": before, "after": after,
            "window": (c["centre"], c["half"]),
            "shares": (c["green_share"], c["steel_share"], c["ink_share"])}


def _root(cfg):
    return cfg.get("root") or ROOT


def _out_dir(cfg):
    return cfg.get("out_dir")


_IMAGE_EXTS = (".png", ".jpg", ".jpeg")


def _discover_refs(cfg):
    """Every image under `root`, tagged by its filename stem.

    Walked recursively so a prepared folder can group files in subfolders or
    not, as the user likes. A file already written by `normalize()` (its stem
    ends in `suffix`) is skipped, so re-running against the same folder never
    treats yesterday's output as today's input.
    """
    root = _root(cfg)
    suffix = cfg.get("suffix", "_norm")
    found = []
    for dirpath, _dirnames, filenames in os.walk(root):
        for name in filenames:
            stem, ext = os.path.splitext(name)
            if (ext.lower() not in _IMAGE_EXTS or stem.endswith(suffix)
                    or stem == "_palette_sheet"):
                continue
            rel = os.path.relpath(os.path.join(dirpath, name), root)
            found.append((stem, rel.replace(os.sep, "/")))
    found.sort(key=lambda row: row[1])
    return found


def _refs(cfg):
    return cfg["refs"] if cfg.get("refs") is not None else _discover_refs(cfg)


def normalize(cfg=None, write=True):
    """Correct every reference. Returns a row per file."""
    cfg = cfg or CONFIG
    out_dir = _out_dir(cfg)
    if write and out_dir:
        os.makedirs(out_dir, exist_ok=True)
    rows = []
    for tag, rel in _refs(cfg):
        path = os.path.join(_root(cfg), rel)
        if not os.path.exists(path):
            rows.append({"tag": tag, "note": "missing"})
            continue
        made = convert(path, cfg)
        if made is None:
            rows.append({"tag": tag, "note": "no paint found"})
            continue
        stem, ext = os.path.splitext(path)
        stem = os.path.join(out_dir, os.path.basename(stem)) if out_dir else stem
        out = stem + cfg["suffix"] + ext
        if write:
            made["image"].save(out)
        rows.append({"tag": tag, "path": path, "out": out, **made})
    return rows


def sheet(cfg=None, path=None):
    """Before over after, every reference side by side. The thing to judge on."""
    cfg = cfg or CONFIG
    rows = normalize(cfg, write=False)
    live = [r for r in rows if "image" in r]
    if not live:
        return None
    cell = 320
    pad = 8
    width = len(live) * (cell + pad) + pad
    sheet_img = Image.new("RGB", (width, 2 * (cell + pad) + pad), (240, 240, 240))
    for i, row in enumerate(live):
        src = Image.open(row["path"]).convert("RGBA")
        for j, im in enumerate((src, row["image"])):
            flat = Image.new("RGB", im.size, (255, 255, 255))
            flat.paste(im, mask=im.split()[3])
            flat = flat.resize((cell, cell), Image.LANCZOS)
            sheet_img.paste(flat, (pad + i * (cell + pad), pad + j * (cell + pad)))
    if path is None:
        out_dir = _out_dir(cfg) or _root(cfg)
        os.makedirs(out_dir, exist_ok=True)
        path = os.path.join(out_dir, "_palette_sheet.png")
    sheet_img.save(path)
    return path


def report(cfg=None, write=False):
    """Print what every reference measures, and what the correction would do."""
    cfg = cfg or CONFIG
    rows = normalize(cfg, write=write)
    head = (f"{'ref':5s} {'green hue':>18s} {'sat':>15s} {'val':>15s}"
            f" {'steel hue':>18s} {'ink hue':>18s} {'ink val':>15s}")
    print(head)
    for row in rows:
        if "note" in row:
            print(f"{row['tag']:5s} - {row['note']}")
            continue
        b, a = row["before"], row["after"]
        print(f"{row['tag']:5s}"
              f" {b['green']['hue']:7.1f} ->{a['green']['hue']:7.1f}"
              f" {b['green']['sat']:6.3f} ->{a['green']['sat']:6.3f}"
              f" {b['green']['value']:6.3f} ->{a['green']['value']:6.3f}"
              f" {b['steel']['hue']:7.1f} ->{a['steel']['hue']:7.1f}"
              f" {b['ink']['hue']:7.1f} ->{a['ink']['hue']:7.1f}"
              f" {b['ink']['value']:6.3f} ->{a['ink']['value']:6.3f}")
    for row in rows:
        if "note" in row:
            continue
        g, s, ink = row["shares"]
        centre, half = row["window"]
        print(f"  {row['tag']:4s} green {g*100:5.1f}%  steel {s*100:5.1f}%"
              f"  ink {ink*100:5.1f}%  hue window {centre:6.1f} +-{half:4.1f}")
    return rows


def _parse_args():
    import argparse
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--root", help="folder the refs are read from "
                                  "(default: assets/Images next to this file)")
    p.add_argument("--out", help="folder normalized copies and the contact "
                                 "sheet are written to (default: next to "
                                 "each source file)")
    return p.parse_args()


if __name__ == "__main__":
    args = _parse_args()
    cfg = dict(CONFIG, root=args.root, out_dir=args.out)
    report(cfg=cfg, write=True)
    print("sheet:", sheet(cfg=cfg))
