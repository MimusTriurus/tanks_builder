"""Turn the generator's water sheet into the pond's own loop.

The sheet arrives as twelve frames of open sea on a 4x3 grid, drawn at a
camera the board does not use and, in its first delivery, with no alpha channel
at all.  What comes out of here is four frames of one calm hexagon, squashed
onto the board's camera and cut to the plate exactly, which is what the pond
surface in ``Stage3D`` draws.

**All twelve, as three bands of four**, because that is what the sheet is: it
escalates monotonically from a flat calm to a breaking sea, and the pond reads
the ladder as a sea *state* rather than as one long animation.  Calm under a
still cell, chop under a cell a tank is driving through, break where one has just
crossed a shoreline.

That split is what makes the sheet usable at all.  **The twelve do not close** -
their wrap is 41.5 against a worst normal step of 33.2, which is a seam by any
reading - but **each quartet does**: 1..4 wraps at 1.14 of its own worst inside
step, 5..8 at 1.11, 9..12 at 1.21, where the project calls a seam anything over
4.  So the loop is four frames long and the band is a state, not a phase.

**The first cut took only the top row, on a measurement that was wrong**, and it
is worth saying which: read without an alpha channel, the lower rows' spray
seemed to reach the row above, so their boundary looked undiscoverable.  With the
alpha the sheet parts into three clean runs - 141..325, 404..587, 670..852 - and
what looked like a merge is detached droplets standing over frames 10 to 12.

Two things a frame is not allowed to keep, and both are cropped:

* **Attached spray.**  Frames 10 to 12 run 190, 209 and 215 px against a plate of
  185, all of it upward.  A frame has to *be* the plate, because that is what puts
  a corner of the mesh on a corner of the picture; airborne water is a different
  effect standing above the surface and does not belong to the plane.
* **Droplets.**  Same argument, arriving as separate runs.

So the plate is measured, not declared: the bottom edge comes off the alpha per
frame - spray goes up, never down - and the height is carried from the top row,
whose four plates are unambiguous at 185 and agree to the pixel.

And one thing the frames arrive with that has to be taken off them: **the keyed
rim**.  The sheet came shaped by its alpha, which is right for a picture standing
on its own and wrong here, because the surface mesh is already that hexagon.  Two
cells meeting edge to edge laid two fades on top of each other and the pond drew
as separate pieces - see ``_harden``.

Plain Python, no Blender: this is a pure function of pixels, the same argument
``atlas_pack.repack_dir`` makes.

    py water_sheet.py
"""

from __future__ import annotations

import math
import os

import numpy as np
from PIL import Image

CONFIG = {
    # Beside its output rather than in Terrains, and that is not tidiness:
    # TerrainSet reads that folder as kinds of ground and refuses by name
    # anything that is not the template's frame - so the sheet sat there
    # reporting "refused water" on every run, which reads as the water art
    # having failed rather than as the source of it not being a kind.
    "source": "Images/Water/water_sheet.png",
    "out": "Images/Water/pond.png",
    # The board's camera. The plate's flat-to-flat over vertex-to-vertex is
    # (sqrt(3)/2)*sin(elevation), so this number *is* the aspect the art has to
    # be squashed onto - see TerrainSet's second guard.
    "elevation": 30.0,
    # Anything below this is surround, not surface.
    "alpha_floor": 8,
    # The grid the sheet is laid out on. Named so a sheet laid out some other
    # way is refused by count rather than quietly cut wrong, and because the
    # rows are what the pond reads as sea states - see the module docstring.
    "columns": 4,
    "rows": 3,
    # What counts as a plate rather than a droplet: a hexagon reaches nearly the
    # full width of its column somewhere, and a fleck of spray does not. Frames
    # 10 to 12 carry two, one and one detached runs above their own plate.
    "plate_share": 0.9,
}


def _alpha(img: np.ndarray) -> np.ndarray:
    return img[:, :, 3]


def _runs(mask: np.ndarray) -> list[tuple[int, int]]:
    """Inclusive [start, end] runs of True."""
    out: list[tuple[int, int]] = []
    start = -1
    for i, on in enumerate(mask):
        if on and start < 0:
            start = i
        elif not on and start >= 0:
            out.append((start, i - 1))
            start = -1
    if start >= 0:
        out.append((start, len(mask) - 1))
    return out


def plates(img: np.ndarray, cfg=CONFIG) -> list[tuple[int, int, int, int]]:
    """Every plate, as (x0, y0, w, h), in reading order, measured off the alpha.

    Three passes, and each one asks the pixels a question they can answer:

    1. the column bands, from the top third of the sheet - where the four calm
       frames stand alone and nothing overhangs anything;
    2. within a column, the runs that are *plates*, told from droplets by
       reaching nearly the full column width;
    3. each plate's bottom edge, which is the one boundary spray cannot move,
       because spray goes up.

    The height is the only number carried rather than measured, and it is
    carried from the top row - four plates, no spray at all, agreeing on 185 to
    the pixel.  Cutting to each frame's own run instead would scale frames 10 to
    12 by 190, 209 and 215 against everybody else's 185, and a hexagon that
    changes size between frames is a pond that breathes.
    """
    a = _alpha(img)
    h = a.shape[0]
    band = a[: h // cfg["rows"], :] > cfg["alpha_floor"]
    cols = _runs(band.any(axis=0))
    if len(cols) != cfg["columns"]:
        raise SystemExit(
            f"top row holds {len(cols)} plates, not {cfg['columns']}: {cols}"
        )
    width = max(x1 - x0 + 1 for x0, x1 in cols)

    # The top row, on its own, settles the plate's height for the whole sheet.
    tall = 0
    for x0, x1 in cols:
        rows = _runs((a[: h // cfg["rows"], x0 : x1 + 1] > cfg["alpha_floor"])
                     .any(axis=1))
        if len(rows) != 1:
            raise SystemExit(f"plate at x{x0}..{x1} is {len(rows)} pieces: {rows}")
        tall = max(tall, rows[0][1] - rows[0][0] + 1)

    out: list[tuple[int, int, int, int]] = []
    found: list[list[tuple[int, int]]] = []
    for x0, x1 in cols:
        keep = []
        for y0, y1 in _runs((a[:, x0 : x1 + 1] > cfg["alpha_floor"]).any(axis=1)):
            wide = int((a[y0 : y1 + 1, x0 : x1 + 1] > cfg["alpha_floor"])
                       .sum(axis=1).max())
            if wide >= cfg["plate_share"] * width:
                keep.append((y0, y1))
        if len(keep) != cfg["rows"]:
            raise SystemExit(
                f"column x{x0}..{x1} holds {len(keep)} plates, not {cfg['rows']}:"
                f" {keep}"
            )
        found.append(keep)

    for r in range(cfg["rows"]):
        for c, (x0, x1) in enumerate(cols):
            bottom = found[c][r][1]
            out.append((x0, bottom - tall + 1, x1 - x0 + 1, tall))
    return out


def _resize(plate: np.ndarray, size: tuple[int, int]) -> np.ndarray:
    """Squash, with the colour premultiplied so the soft rim does not drag the
    surround's colour into the hexagon's edge."""
    rgba = plate.astype(np.float64)
    a = rgba[:, :, 3:4] / 255.0
    pre = np.concatenate([rgba[:, :, :3] * a, rgba[:, :, 3:4]], axis=2)
    small = np.asarray(
        Image.fromarray(np.clip(pre, 0, 255).astype(np.uint8), "RGBA").resize(
            size, Image.LANCZOS
        )
    ).astype(np.float64)
    a2 = small[:, :, 3:4] / 255.0
    rgb = np.where(a2 > 1e-6, small[:, :, :3] / np.maximum(a2, 1e-6), 0.0)
    return np.clip(
        np.concatenate([rgb, small[:, :, 3:4]], axis=2), 0, 255
    ).astype(np.uint8)


def _linear(c: int) -> float:
    """sRGB to linear, the real curve rather than a 2.2 power: the tint is a
    Godot Color, which is linear, and the art is sampled as ``source_color``,
    which converts. A gamma approximation puts them 10% apart in the darks -
    and the darks are most of this water."""
    v = c / 255.0
    return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4


def _harden(frame: np.ndarray) -> np.ndarray:
    """Give the frame a hard hexagon and colour that reaches it.

    **The mesh is already a hexagon, so the texture must not be one too.**  The
    sheet arrived keyed, which means its hexagon fades out over three to seven
    pixels of rim - and a frame is cut to the plate exactly, so that rim sits on
    the frame's own boundary.  Two cells meeting edge to edge then lay two of
    those fades on top of each other, and the pond draws as separate pieces with
    dry ground along every shared edge.  Measured on the first cut: alpha 15 of
    255 at the outermost column, five cells rendering as three.

    So the alpha becomes the plate's own hexagon, hard: opaque inside, nothing
    outside, no rim to double up.  The shape is not lost - the surface mesh is
    that same hexagon, and it is what cuts the silhouette, exactly as it did when
    the pond was a flat colour with no texture at all.

    Colour is carried outward first.  Where alpha was low the stored colour is
    whatever survived unpremultiplying near zero, and turning that opaque would
    hang a dark fringe on the water; each pass hands an unresolved pixel the mean
    of its solid neighbours, which is the same trick that fills a keyed sheet's
    edge everywhere else in this project.
    """
    out = frame.astype(np.float64).copy()
    solid = out[:, :, 3] > 250
    for _ in range(_FILL_PASSES):
        if solid.all():
            break
        near = np.zeros(out.shape[:2] + (4,), dtype=np.float64)
        count = np.zeros(out.shape[:2], dtype=np.float64)
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            rolled = np.roll(np.roll(out[:, :, :3] * solid[:, :, None], dy, 0), dx, 1)
            near[:, :, :3] += rolled
            count += np.roll(np.roll(solid, dy, 0), dx, 1)
        grow = (~solid) & (count > 0)
        out[:, :, :3] = np.where(
            grow[:, :, None], near[:, :, :3] / np.maximum(count, 1)[:, :, None],
            out[:, :, :3])
        solid = solid | grow

    h, w = out.shape[:2]
    y, x = np.mgrid[0:h, 0:w]
    # A flat-top hexagon inscribed in the plate: the left boundary runs from 0 at
    # mid height to w/4 at either end, and the right one mirrors it. Straight off
    # the frame's size, with no trigonometry - the plate already is the camera.
    left = (w / 2.0) * np.abs(y + 0.5 - h / 2.0) / h
    inside = (x + 0.5 >= left) & (x + 0.5 <= w - left)
    out[:, :, 3] = np.where(inside, 255.0, 0.0)
    return np.clip(out, 0, 255).astype(np.uint8)


#: How far colour is carried out past the keyed rim. Seven, one past the widest
#: rim measured on the shipped sheet.
_FILL_PASSES = 7


def _stats(frame: np.ndarray) -> tuple[tuple[int, int, int], float, float]:
    """Mean colour, spread of luminance and the near-white share, over the
    frame's solid interior. The spread is the number that decides whether a
    flat tint can stand in for this water; the mean is what that tint is."""
    solid = frame[:, :, 3] > 250
    if not solid.any():
        return (0, 0, 0), 0.0, 0.0
    rgb = frame[:, :, :3][solid].astype(np.float64)
    lum = rgb @ np.array([0.299, 0.587, 0.114])
    white = np.all(rgb > 200, axis=1).mean() * 100.0
    return tuple(int(v) for v in rgb.mean(axis=0)), float(lum.std()), float(white)


def run(cfg=CONFIG) -> dict:
    here = os.path.dirname(os.path.abspath(__file__))
    src = os.path.join(here, cfg["source"])
    img = np.asarray(Image.open(src).convert("RGBA"))
    if _alpha(img).min() == 255:
        raise SystemExit(
            f"{cfg['source']} has no transparency at all - it is still the "
            "opaque delivery, and keying it is not this script's job"
        )

    boxes = plates(img, cfg)
    sizes = {(w, h) for _, _, w, h in boxes}
    width = max(w for _, _, w, _ in boxes)
    height = max(h for _, _, _, h in boxes)
    if max(width - min(w for _, _, w, _ in boxes),
           height - min(h for _, _, _, h in boxes)) > 2:
        raise SystemExit(f"the four plates are not one size: {sorted(sizes)}")

    want = math.sqrt(3.0) / 2.0 * math.sin(math.radians(cfg["elevation"]))
    tall = int(round(width * want))
    was = height / width

    frames = []
    for x0, y0, w, h in boxes:
        frames.append(
            _harden(_resize(img[y0 : y0 + h, x0 : x0 + w], (width, tall))))

    strip = np.concatenate(frames, axis=1)
    out = os.path.join(here, cfg["out"])
    os.makedirs(os.path.dirname(out), exist_ok=True)
    Image.fromarray(strip, "RGBA").save(out)

    per = [_stats(f) for f in frames]
    band = len(frames) // cfg["rows"]
    mean = tuple(int(sum(p[0][c] for p in per[:band]) / band) for c in range(3))
    return {
        "bands": [
            [round(p[1], 1) for p in per[i * band : (i + 1) * band]]
            for i in range(cfg["rows"])
        ],
        "source": cfg["source"],
        "out": cfg["out"],
        "plates": boxes,
        "plate": (width, height),
        "frame": (width, tall),
        "aspect_was": round(was, 4),
        "aspect_now": round(tall / width, 4),
        "aspect_want": round(want, 4),
        "squash": round(tall / height, 4),
        "strip": (strip.shape[1], strip.shape[0]),
        "spread": [round(p[1], 1) for p in per],
        "white_pct": [round(p[2], 2) for p in per],
        "mean_rgb": [p[0] for p in per],
        "tint_rgb": mean,
        # What Stage3D.Pond wants: the mean as a linear colour, because the
        # tint is composited in the shader and the art is stored encoded.
        # Only the fallback needs it - WaterArt measures the same mean off the
        # loaded strip, so the shipped tint is not a copy of this number.
        "tint_linear": [round(_linear(c), 4) for c in mean],
    }


def report(cfg=CONFIG) -> dict:
    out = run(cfg)
    for k, v in out.items():
        print(f"{k:14} {v}")
    return out


if __name__ == "__main__":
    report()
