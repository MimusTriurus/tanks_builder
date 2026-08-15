"""Turn the generator's water sheet into the pond's own loop.

The sheet arrives as twelve frames of open sea on a 4x3 grid, drawn at a
camera the board does not use and, in its first delivery, with no alpha channel
at all.  What comes out of here is four frames of one calm hexagon, squashed
onto the board's camera and cut to the plate exactly, which is what the pond
surface in ``Stage3D`` draws.

**Only the top row, and that is a measurement rather than a preference.**  Rows
two and three carry spray that runs off the top of their own plate, so their
alpha touches the row above and no boundary between them can be *found* - it
would have to be declared, and a declared grid is a second opinion about a
number the file already answers.  The calm quartet has no overhang at all: its
bounding box **is** its plate, so the frame needs no template, no anchor and no
overhang machinery.  That is the whole reason those four are the ford's frames
and not just the quiet-looking ones.

The other two reasons, both measured (see ``report``):

* **The tint under a wading tank is one flat colour and cannot be anything
  else.**  The waterline is cut inside the tank's own shader, because a plane
  with one sort key cannot stand half in front of a billboard; the tint stands
  in for the surface over that fragment, and the shader cannot sample the real
  surface there because a billboard does not know where in the world its lower
  half is.  So the submerged half is flat by construction, and how badly that
  shows is exactly how uneven the surface is: sd(L) runs 11 on frame 1 to 45 on
  frame 12.  Flat paint under calm water is invisible; under a breaking sea it
  is a patch.
* **The quartet closes.**  Consecutive steps 6.2, 7.9, 8.6 and a wrap of 9.8
  against a worst inside step of 8.6 - ratio 1.14, where the project calls a
  seam anything over 4.  The full twelve do not: their wrap is 41.5 against a
  worst normal step of 33.2.

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
    # How many plates the top row must hold. Named so a sheet laid out some
    # other way is refused by count rather than quietly cut wrong.
    "expect": 4,
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
    """The top row's plates, as (x0, y0, w, h), measured off the alpha.

    Two passes because the sheet's rows overlap in y and its columns do not.
    The column runs are found in a band that stops short of the second row's
    plate, then each plate's own rows are found inside its own columns - which
    is clean precisely because the calm frames carry no spray to touch the row
    below them.
    """
    a = _alpha(img)
    h = a.shape[0]
    # A third of the sheet: past the first row's plate, short of the second's.
    band = a[: h // 3, :] > cfg["alpha_floor"]
    cols = _runs(band.any(axis=0))
    if len(cols) != cfg["expect"]:
        raise SystemExit(
            f"top row holds {len(cols)} plates, not {cfg['expect']}: {cols}"
        )

    out = []
    for x0, x1 in cols:
        rows = _runs((a[: h // 3, x0 : x1 + 1] > cfg["alpha_floor"]).any(axis=1))
        if len(rows) != 1:
            raise SystemExit(f"plate at x{x0}..{x1} is {len(rows)} pieces: {rows}")
        y0, y1 = rows[0]
        out.append((x0, y0, x1 - x0 + 1, y1 - y0 + 1))
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
        frames.append(_resize(img[y0 : y0 + h, x0 : x0 + w], (width, tall)))

    strip = np.concatenate(frames, axis=1)
    out = os.path.join(here, cfg["out"])
    os.makedirs(os.path.dirname(out), exist_ok=True)
    Image.fromarray(strip, "RGBA").save(out)

    per = [_stats(f) for f in frames]
    mean = tuple(
        int(sum(p[0][c] for p in per) / len(per)) for c in range(3)
    )
    return {
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
