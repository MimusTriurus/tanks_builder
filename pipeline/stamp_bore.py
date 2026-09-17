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
    # Every live set. Written out rather than discovered, unlike
    # `stamp_ports.run`, because this one needs a `barrel` layer and a run
    # report to read the muzzle out of, and a set missing either would fail
    # halfway rather than be skipped. TDP and HMP joined when they went on the
    # Godot bench: without the block `FlashSource.Built` falls back to the
    # painted sheet, silently, which is the one failure this stamp exists to
    # prevent.
    "tags": ("LTP", "MTP", "HTP", "TDP", "HMP"),
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


def bore_axis(tag, cfg=None, report=None):
    """Point and direction of the bore, in world units, off the run report.

    `report` is the run report already in hand. `tank_pipeline.run` stamps
    before it writes the report, so that the report is still the last thing
    written in the directory - the finish signal the polling step relies on -
    and so that the outcome of the stamp can go into it.
    """
    cfg = cfg or CONFIG
    if report is None:
        with open(os.path.join(cfg["root"], tag, "_run_report.json")) as fh:
            report = json.load(fh)
    point = report["muzzle_point"]
    measured = report.get("muzzle_dir")
    if measured:
        # Read, not derived. `muzzle_point.set_muzzle` fits the bore in three
        # dimensions and now writes it into the report, so the derivation below
        # is the fallback for sets rendered before it did - and it has to be a
        # fallback rather than the answer, because it pins z to zero. A howitzer
        # carries its elevation in exactly that term.
        d = np.array([float(v) for v in measured], float)
        n = float(np.linalg.norm(d))
        if n < 1e-9:
            raise RuntimeError(tag + ": muzzle_dir has no length")
        d /= n
        return {
            "point": [round(v, 6) for v in point],
            "dir": [round(float(v), 6) for v in d],
            "dir_from": "measured on the tube by muzzle_point.set_muzzle",
            "elevation": round(math.degrees(math.asin(
                max(-1.0, min(1.0, float(d[2]))))), 3),
            "bearing": round(math.degrees(math.atan2(d[1], d[0])) % 360.0, 3),
            "bearing_disagreement": report.get("bearing_disagreement"),
            "shape": report.get("muzzle_shape"),
        }
    axis = report["axis"]["ring_axis"]
    if axis is None:
        # A casemate, and this branch cannot serve one: it derives the bore's
        # bearing from the muzzle's offset off the ring, and there is no ring.
        # It is also the branch that pins z to zero, which on the two vehicles
        # in this project that have no ring is exactly wrong - both lay their
        # guns high. A re-render writes `muzzle_dir` and the branch above takes
        # it; nothing else can.
        raise RuntimeError(
            tag + ": no muzzle_dir in the report and no ring to derive one "
            "from - this vehicle is a casemate, so re-render it. Deriving the "
            "bearing here would need an axis the vehicle does not have, and "
            "would flatten a gun that is laid.")
    dx, dy = point[0] - axis[0], point[1] - axis[1]
    span = math.hypot(dx, dy)
    if span < 1e-9:
        raise RuntimeError(tag + ": the muzzle sits on the ring axis")
    dx, dy = dx / span, dy / span
    return {
        "point": [round(v, 6) for v in point],
        # z is zero: this branch assumes a level gun, which is the assumption the
        # flat-firing classes already rest on. A laid or lobbing gun must not
        # come through here - see the branch above.
        "dir": [round(dx, 6), round(dy, 6), 0.0],
        "dir_from": "muzzle against the ring axis, level (no measured dir in "
                    "the report - re-render, or the elevation is lost)",
        "elevation": 0.0,
        "bearing": round(math.degrees(math.atan2(dy, dx)) % 360.0, 3),
        "bearing_disagreement": report.get("bearing_disagreement"),
        "shape": report.get("muzzle_shape"),
    }


def block_for(tag, cfg=None, report=None):
    cfg = cfg or CONFIG
    out = bore_axis(tag, cfg, report)
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


def stamp(folder, report=None, cfg=None):
    """Write the bore block into one folder's shot layers.

    `stamp_ports.stamp`'s twin, and the entry `tank_pipeline.run` calls so a
    render cannot leave a set un-stamped. Folder rather than tag because a
    render knows where it wrote and need not be in `CONFIG["tags"]` to have a
    gun - `Sprites/HT_V1` had one before it was anybody's class.

    Returns a line saying what happened, and it says "nothing to stamp" only
    when there is genuinely no tube. A set that has a barrel layer and comes
    back un-stamped is the failure this whole module exists to prevent, so the
    caller is expected to read the answer rather than assume it.
    """
    cfg = dict(cfg or CONFIG)
    root, tag = os.path.split(os.path.normpath(folder))
    cfg["root"] = root
    name = os.path.basename(tag)
    if not os.path.exists(os.path.join(folder, cfg["source"] + "_atlas.json")):
        return name + ": no " + cfg["source"] + " layer, no bore to stamp"
    block = block_for(name, cfg, report)
    touched = []
    for layer in cfg["layers"]:
        path = os.path.join(folder, layer + "_atlas.json")
        if not os.path.exists(path):
            continue
        with open(path) as fh:
            meta = json.load(fh)
        meta["bore"] = block
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(meta, fh, indent=2)
        touched.append(layer)
    return ("%s: bore at %s bearing %.2f deg (ring disagrees by %s), radius "
            "%.5f world = %.1fpx -> %s"
            % (name, block["point"], block["bearing"],
               block["bearing_disagreement"], block["radius"],
               block["radius_px"], ", ".join(touched) or "nothing"))


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
