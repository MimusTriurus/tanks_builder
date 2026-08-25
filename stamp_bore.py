"""Stamp the gun's bore into the shot layers' JSON, in world units.

`stamp_ports` with the other end of the tank in it, and the argument is the same
one: the bench has to build the muzzle flash and its smoke itself, every number
either is made of is quoted against the *bore radius*, and where a shot starts
is a point on the end of the tube. Neither is anywhere in the atlas - the frames
carry pixels and the view, and nothing about the gun the pixels are of.

World units, not pixels, for `stamp_ports`' reason exactly: the muzzle is a fact
about the model and does not move when the camera does, while the bench already
holds the two numbers that turn world into pixels. Stamping pixels here would
freeze a projection the bench can do exactly, and freeze it per heading.

Pure numpy and PIL, no Blender. Nothing is re-rendered to gain the block.

**Two of the three numbers are measured here rather than read, and they are soft
in different degrees.** `muzzle_point.set_muzzle` stamps all three on `Barrel` -
`muzzle_point`, `muzzle_dir`, `muzzle_radius` - but only the point reaches the
run report; the other two live on the object inside the .blend. So:

* `point` is read straight out of the run report. Hard.
* `dir` is *derived* and it is still a measurement: the bore axis runs through
  the ring axis and the muzzle, both of which are in the report, so the bearing
  is `normalize(muzzle.xy - axis.xy)` and the z term is zero because the gun is
  level - which is the assumption the whole shooting model already rests on
  (`TankTick.Track` crosses a line of fire with one comparison for exactly this
  reason). The report's own `bearing_disagreement` is the check on it, and it
  comes to 0.11 / 0.73 / 0.79 degrees on the three tanks.
* `radius` is *fitted* off the barrel atlas: the tube's drawn width across the
  bore, taken in a band just short of the muzzle so the taper and the
  antialiased tip do not set it, over the six headings where the bore is least
  foreshortened. This is `stamp_ports.hull_length`'s trick and it carries that
  one's caveat with it.

The block says which of the three it is carrying for each, because a fitted
constant standing in for a measured one is exactly the sort of thing that goes
unnoticed. When Blender is next up, `tank_pipeline` writing the real
`muzzle_dir` and `muzzle_radius` into the run report replaces both without
re-rendering anything.
"""

import json
import math
import os

import numpy as np
from PIL import Image

CONFIG = {
    "root": "Sprites",
    "tags": ("LTP", "MTP", "HTP"),
    # The layers a shot is drawn out of. Both get the same block: they are one
    # event at one point, and `muzzle_flash.py` holds CONFIG and SMOKE side by
    # side for that reason.
    "layers": ("flash", "smoke"),
    "source": "barrel",
    # Where across the tube to measure. Not at the very tip: a rod tapers into
    # its own antialiasing there, and the muzzle brake on some models flares.
    "band": (0.18, 0.02),
    # How many headings to take. The bore is level, so a heading foreshortens it
    # by |ground direction|, and the least foreshortened few are the ones where
    # the drawn width is the bore and not a slice of it.
    "headings": 6,
    "alpha": 32,
}


def _ground(heading_deg, elevation_deg):
    """Screen direction of a heading lying in the ground plane, unnormalised.

    Its length is the share of a horizontal length that survives the
    projection - the same quantity `AtlasSet.GroundDirection` carries and for
    the same reason.
    """
    h = math.radians(heading_deg)
    squash = math.sin(math.radians(elevation_deg))
    return math.cos(h), -math.sin(h) * squash


def bore_radius(tag, cfg=None):
    """Fit the bore radius, in world units, off the barrel atlas."""
    cfg = cfg or CONFIG
    base = os.path.join(cfg["root"], tag)
    source = cfg["source"]
    with open(os.path.join(base, source + "_atlas.json")) as fh:
        meta = json.load(fh)
    img = np.asarray(
        Image.open(os.path.join(base, source + "_atlas.png")).convert("RGBA"))
    upp = meta["units_per_pixel"]
    ax, ay = meta["anchor_px"]
    elev = meta["view"]["elevation"]
    lo, hi = cfg["band"]

    found = []
    for frame in meta["frames"]:
        # Phase 0 is the gun at rest; a recoiled tube is the same tube moved,
        # but taking one phase keeps the sample from being a mixture.
        if frame.get("phase", 0) != 0:
            continue
        x, y, w, h = frame["rect"]
        ox, oy = frame["off"]
        mask = img[y:y + h, x:x + w, 3] > cfg["alpha"]
        if not mask.any():
            continue
        ys, xs = np.nonzero(mask)
        px = xs + ox - ax
        py = ys + oy - ay

        dx, dy = _ground(frame["angle"] + 270.0, elev)
        span = math.hypot(dx, dy)
        if span < 1e-6:
            continue
        dx, dy = dx / span, dy / span
        along = px * dx + py * dy
        across = -px * dy + py * dx

        tip, heel = along.max(), along.min()
        reach = tip - heel
        band = (along > tip - lo * reach) & (along < tip - hi * reach)
        if band.sum() < 8:
            continue
        found.append((span, across[band].max() - across[band].min()))

    if not found:
        raise RuntimeError(tag + ": no barrel frame gave a bore to measure")
    found.sort(key=lambda r: -r[0])
    taken = [w for _, w in found[:cfg["headings"]]]
    width = float(np.median(taken))
    return {
        "radius": round(width / 2.0 * upp, 6),
        "radius_px": round(width / 2.0, 3),
        "radius_from": source + " atlas width across the bore",
        "radius_spread_px": round((max(taken) - min(taken)) / 2.0, 3),
    }


def bore_axis(tag, cfg=None):
    """Point and direction of the bore, in world units, off the run report."""
    cfg = cfg or CONFIG
    with open(os.path.join(cfg["root"], tag, "_run_report.json")) as fh:
        report = json.load(fh)
    point = report["muzzle_point"]
    axis = report["axis"]["ring_axis"]
    dx, dy = point[0] - axis[0], point[1] - axis[1]
    span = math.hypot(dx, dy)
    if span < 1e-9:
        raise RuntimeError(tag + ": the muzzle sits on the ring axis")
    dx, dy = dx / span, dy / span
    return {
        "point": [round(v, 6) for v in point],
        # z is zero: the gun is level, and every other part of the stand
        # already takes that as given.
        "dir": [round(dx, 6), round(dy, 6), 0.0],
        "dir_from": "muzzle against the ring axis, level",
        "bearing": round(math.degrees(math.atan2(dy, dx)) % 360.0, 3),
        "bearing_disagreement": report.get("bearing_disagreement"),
        "shape": report.get("muzzle_shape"),
    }


def block_for(tag, cfg=None):
    cfg = cfg or CONFIG
    out = bore_axis(tag, cfg)
    out.update(bore_radius(tag, cfg))
    # Carried alongside because a shot is drawn on a tank and the bench sizes
    # what it draws against both: the flash against the bore, its place against
    # the hull. Lifted off the ports block rather than fitted a second time -
    # two fits of one number are two numbers.
    with open(os.path.join(cfg["root"], tag, "fire_atlas.json")) as fh:
        ports = json.load(fh).get("ports", {})
    if "hull_length" in ports:
        out["hull_length"] = ports["hull_length"]
        out["hull_length_from"] = ports.get("hull_length_from")
    return out


def run(cfg=None):
    cfg = cfg or CONFIG
    report = {}
    for tag in cfg["tags"]:
        block = block_for(tag, cfg)
        for layer in cfg["layers"]:
            path = os.path.join(cfg["root"], tag, layer + "_atlas.json")
            if not os.path.exists(path):
                print(tag + ": no " + layer + " layer, skipped")
                continue
            with open(path) as fh:
                meta = json.load(fh)
            meta["bore"] = block
            with open(path, "w", encoding="utf-8") as fh:
                json.dump(meta, fh, indent=2)
        report[tag] = block
        print("{}: bore at {} bearing {:.2f} deg (ring disagrees by {}), "
              "radius {:.5f} world = {:.1f}px (+-{:.1f}px over {} headings)"
              .format(tag, block["point"], block["bearing"],
                      block["bearing_disagreement"], block["radius"],
                      block["radius_px"], block["radius_spread_px"],
                      cfg["headings"]))
    return report


if __name__ == "__main__":
    run()
