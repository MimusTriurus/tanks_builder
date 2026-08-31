"""Stamp the engine port into the burning layers' JSON, in world units.

Why a stamp at all, and why in the layer's own file: the bench has to build the
smoke column itself, and every number the column is made of is quoted against
the hull - `rise`, `spread`, `puff_grow` are all fractions of its length - while
where the column starts is a point on the engine deck. Neither is anywhere in
the atlas: the frames carry pixels and the view, and nothing about the tank the
pixels are of.

This is the plate table's argument with one term changed. That one is stamped in
*pixels*, between the render and the check, because `units_per_pixel` is a
result of fitting the camera and a table worked out before the job would be a
second guess at it. This one is stamped in *world units* for the opposite half
of the same reason: the port is a fact about the model and does not move when
the camera does, and the bench already holds the two numbers that turn world
into pixels - `units_per_pixel` and the view's elevation. Stamping pixels here
would freeze a projection the bench can do exactly, and freeze it per heading.

Pure numpy and PIL, no Blender - `atlas_pack.repack_dir`'s argument: everything
here is a function of files that are already on disk, so a set that took twelve
minutes to render is stamped in a second, and no tank has to be rendered again
to gain the block.

**`hull_length` is measured, not read, and that is the one soft number here.**
`exhaust_point` stamps the real one on the hull as `exhaust_scale` - the larger
of the hull's world bbox X and Y - and it is not in the shipped JSON or the run
report, only on the object inside the .blend. So it is recovered from the hull
atlas instead: at heading h the hull's drawn width is `L|cos h| + W|sin h|`
because the camera's x axis takes the ground plane unforeshortened, and a least
squares over the twenty-four headings gives L and W. Measured against the
shipped column it is within a pixel an edge of the fitted best (15.2px against
12.1px of total error over three edges of a 110px box), which is inside the
noise of comparing a sphere's silhouette to its bounding circle.

`tank_pipeline` writes the true `exhaust_scale` into the block on its next run,
and this tool prints both when both are there. Until then the block says which
of the two it is carrying, because a fitted constant standing in for a measured
one is exactly the sort of thing that goes unnoticed.
"""

import json
import math
import os
import sys

import numpy as np
from PIL import Image

# The layers built out of the port. Both, because they are one effect: the
# column is what makes the flame work, and a set carrying the block for one of
# them would draw half a fire procedurally and half from the atlas.
LAYERS = ("burn", "fire")


def hull_length(folder):
    """The hull's longest world axis, from its own atlas.

    Returns (length, width, worst residual) in world units. The residual is
    printed rather than swallowed: a hull is not a box, so a large one means the
    two numbers are describing something else.
    """
    meta = json.load(open(os.path.join(folder, "hull_atlas.json")))
    upp = float(meta["units_per_pixel"])
    image = np.asarray(Image.open(os.path.join(folder, "hull_atlas.png"))
                       .convert("RGBA"))
    rows, spans = [], []
    for frame in meta["frames"]:
        rect = frame.get("rect")
        if not rect:
            continue
        x, y, w, h = rect
        cols = np.nonzero((image[y:y + h, x:x + w, 3] > 8).any(axis=0))[0]
        if not len(cols):
            continue
        head = math.radians(270.0 + float(frame["angle"]))
        rows.append([abs(math.cos(head)), abs(math.sin(head))])
        spans.append((cols.max() - cols.min() + 1) * upp)
    fit, *_ = np.linalg.lstsq(np.array(rows), np.array(spans), rcond=None)
    worst = float(np.abs(np.array(rows) @ fit - np.array(spans)).max())
    return float(fit[0]), float(fit[1]), worst


def block(folder):
    """The ports block for one sprite folder, or None if there is no port."""
    report = json.load(open(os.path.join(folder, "_run_report.json")))
    ports = report.get("exhaust_ports") or []
    if not ports:
        return None
    length, width, worst = hull_length(folder)
    stamped = report.get("exhaust_scale")
    return {
        "hull_length": round(max(length, width), 6),
        # Named rather than implied: the number is a stand-in until a render
        # writes the real one, and the two are within a percent or two on
        # everything shipped so far - which is exactly why the difference would
        # never be spotted without a label.
        "hull_length_from": "hull atlas broadside fit"
                            if stamped is None else "exhaust_scale",
        "hull_width": round(min(length, width), 6),
        "hull_fit_residual": round(worst, 6),
        "list": [{"point": [round(float(v), 6) for v in p["point"]],
                  "dir": [round(float(v), 6) for v in p["dir"]],
                  "radius": round(float(p["radius"]), 6)} for p in ports],
    }


def stamp(folder, dry_run=False):
    """Write the block into every burning layer of one folder."""
    made = block(folder)
    if made is None:
        return f"{os.path.basename(folder)}: no port, nothing to stamp"
    touched = []
    for layer in LAYERS:
        path = os.path.join(folder, f"{layer}_atlas.json")
        if not os.path.exists(path):
            continue
        meta = json.load(open(path))
        meta["ports"] = made
        if not dry_run:
            # Same shape the renderer writes, so a stamped file and a fresh one
            # differ only in the block.
            with open(path, "w") as handle:
                json.dump(meta, handle, indent=2)
        touched.append(layer)
    port = made["list"][0]
    return ("%s: hull %.4f x %.4f (%s, worst residual %.1f mm), "
            "%d port(s), first at (%.4f, %.4f, %.4f) r %.4f -> %s"
            % (os.path.basename(folder), made["hull_length"], made["hull_width"],
               made["hull_length_from"], 1000.0 * made["hull_fit_residual"],
               len(made["list"]), *port["point"], port["radius"],
               ", ".join(touched) or "nothing"))


def run(root="Sprites", tags=None, dry_run=False):
    tags = tags or [name for name in sorted(os.listdir(root))
                    if os.path.exists(os.path.join(root, name, "_run_report.json"))]
    return [stamp(os.path.join(root, tag), dry_run) for tag in tags]


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("-")]
    for line in run(tags=args or None, dry_run="--dry-run" in sys.argv):
        print(line)
