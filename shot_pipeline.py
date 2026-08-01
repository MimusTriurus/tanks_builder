"""
The whole shot, end to end.

Measures the muzzle, builds the ground tile and the flash, renders the four
layers as one job, and composites a check image with the numbers that decide
whether it worked.

    import shot_pipeline
    shot_pipeline.run()                       # everything, into out/
    shot_pipeline.run({"output_dir": r"...\\Sprites\\MT"})

Everything here is automatic. The one step that is not, and cannot sensibly be,
is separating the gun tip into a `Barrel` object parented to the turret - see
MUZZLE_FLASH.md. `prepare` fails with an explanation if that has not been done.

Why one entry point rather than four calls
------------------------------------------
The same reason `render_set` exists. The four stages share values that must
agree - the muzzle stamp, the ring axis, the tile plane, the camera - and each
one read from a different place is a way for them to drift apart. They are
resolved here once. `check` then measures the result rather than trusting it,
because every number below was wrong at least once.
"""

import importlib.util
import json
import math
import os

import bpy
import numpy as np
from mathutils import Matrix, Vector

REPO = r"D:\Projects\AgentCoding\BlenderMCP"

CONFIG = {
    "repo": REPO,
    "output_dir": os.path.join(REPO, "out"),

    # scene objects. Read the scene before trusting these: the three scenes in
    # Scenes/ are hand-split and have differed before.
    "root": "world",
    "hull": "Hull",
    "turret": "Turret",
    "barrel": "Barrel",
    "flash": "Flash",
    "smoke": "Smoke",
    "hex": "Hex",

    # --- the render ---------------------------------------------------------
    "steps": 12,
    "tile": 256,
    # the effect layers get a wider frame at the same units_per_pixel: the
    # muzzle already sits 68.8px out from the spin axis, so a 256 tile leaves
    # the flash 59px and a flash worth looking at is longer than that
    "effect_tile_scale": 1.5,
    "phases": 8,
    "azimuth": 0.0,
    "elevation": 30.0,
    "front_dir": 270.0,

    # --- the check ----------------------------------------------------------
    # frame indices to composite: at camera, across right, away, across left
    "check_headings": [0, 3, 6, 9],
    "check_phases": [0, 1, 2, 4],
    # how far the flash may sit off the tank's symmetry line, in pixels, on the
    # two headings where that line is vertical on screen. This is the assertion
    # that would have caught the muzzle sitting on the rim of the bore instead
    # of on its axis - see MUZZLE_FLASH.md.
    "centre_tolerance_px": 2.0,
}

LAYER_ORDER = ("hex", "hull", "turret", "smoke", "flash")


def _load(cfg, name):
    spec = importlib.util.spec_from_file_location(
        name, os.path.join(cfg["repo"], "%s.py" % name))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


# ---------------------------------------------------------------------------
# stages
# ---------------------------------------------------------------------------

def prepare(cfg=None):
    """Measure the muzzle, build the tile and the flash. Idempotent."""
    cfg = dict(CONFIG, **(cfg or {}))
    scene = bpy.context.scene

    barrel = scene.objects.get(cfg["barrel"])
    if barrel is None:
        barrel = next((o for o in scene.objects
                       if o.name.strip() == cfg["barrel"]), None)
    if barrel is None:
        raise RuntimeError(
            "no %r in the scene. Separate the gun tip by hand first: box-select "
            "the barrel in a side ortho view, Ctrl+L to complete the panels it "
            "cuts through, P > Selection, rename it %r, then select it, "
            "Ctrl-click %r and Ctrl+P > Object (Keep Transform). See "
            "MUZZLE_FLASH.md." % (cfg["barrel"], cfg["barrel"], cfg["turret"]))

    # the transient objects are rebuilt every run, so a re-run never leaves
    # Hex.001 behind to be rendered instead of Hex
    for name in (cfg["hex"], cfg["flash"], cfg["smoke"]):
        ob = scene.objects.get(name)
        if ob is not None:
            data = ob.data
            bpy.data.objects.remove(ob)
            bpy.data.meshes.remove(data)

    muzzle = _load(cfg, "muzzle_point").set_muzzle(
        {"turret": cfg["turret"], "barrel": cfg["barrel"],
         "stamp_on": [cfg["turret"], cfg["barrel"]]})

    hex_mod = _load(cfg, "hex_base")
    tile = hex_mod.make_hex(dict(
        hex_mod.CONFIG,
        target=cfg["root"], name=cfg["hex"],
        # the tank in these scenes is already placed and must not be moved;
        # ground_z None then stands the tile under its tracks
        move_target=False, center_on_origin=False, ground_z=None))

    flash_mod = _load(cfg, "muzzle_flash")
    flash_mod.build({"turret": cfg["turret"], "name": cfg["flash"]})
    flash_mod.build_smoke({"turret": cfg["turret"], "name": cfg["smoke"]})
    return {"muzzle": muzzle, "hex": tile}


def render(cfg=None):
    """Render hull, turret, tile and flash as one framed set."""
    cfg = dict(CONFIG, **(cfg or {}))
    atlas = _load(cfg, "sprite_atlas")
    flash_mod = _load(cfg, "muzzle_flash")

    return atlas.render_set({
        "shared": {
            "output_dir": cfg["output_dir"],
            "steps": cfg["steps"], "tile": cfg["tile"],
            "azimuth": cfg["azimuth"], "elevation": cfg["elevation"],
            "hex_labels": {"front_dir": cfg["front_dir"], "orientation": "flat"},
        },
        "layers": [
            # the turret hint excludes descendants, which is what keeps the
            # barrel out of the hull layer
            {"name": "hull", "target": cfg["root"], "exclude": [cfg["turret"]]},
            {"name": "turret", "target": cfg["turret"]},
            {"name": "hex", "target": cfg["hex"], "static": True},
            {"name": "flash", "target": cfg["flash"],
             # never in the fit, or a transient effect resizes every layer
             "fit": False,
             "tile_scale": cfg["effect_tile_scale"],
             "phases": cfg["phases"],
             "phase_hook": flash_mod.phase_hook({"turret": cfg["turret"],
                                                 "name": cfg["flash"]}),
             # the tank blocks without being drawn: fired away from the camera
             # the flash is behind the turret, and a flat layer has no depth
             "holdout": [cfg["hull"], cfg["turret"]]},
            # smoke is its own layer because it occludes where the flash emits.
            # One layer cannot be both: fire adds light to what is behind it,
            # smoke takes it away, and the game blends them differently.
            {"name": "smoke", "target": cfg["smoke"], "fit": False,
             "tile_scale": cfg["effect_tile_scale"],
             "phases": cfg["phases"],
             "phase_hook": flash_mod.smoke_phase_hook(
                 {"turret": cfg["turret"], "name": cfg["smoke"]}),
             "holdout": [cfg["hull"], cfg["turret"]]},
        ],
    })


# ---------------------------------------------------------------------------
# check
# ---------------------------------------------------------------------------

def _atlases(cfg):
    atlas = _load(cfg, "sprite_atlas")
    out = {}
    for name in LAYER_ORDER:
        base = os.path.join(cfg["output_dir"], "%s_atlas" % name)
        with open(base + ".json", encoding="utf-8") as fh:
            meta = json.load(fh)
        out[name] = (atlas.read_rgba(base + ".png"), meta)
    return out


def _tile_of(layers, name, index):
    pix, meta = layers[name]
    size = meta["tile"][0]
    cols, rows = meta["grid"]["columns"], meta["grid"]["rows"]
    col, row = index % cols, index // cols
    # image data is bottom-up; grid row 0 is the top one
    y0 = (rows - row - 1) * size
    return pix[y0:y0 + size, col * size:(col + 1) * size]


def _over(dst, src):
    a = src[:, :, 3:4]
    out = np.empty_like(dst)
    out[:, :, :3] = src[:, :, :3] * a + dst[:, :, :3] * (1.0 - a)
    out[:, :, 3:4] = a + dst[:, :, 3:4] * (1.0 - a)
    return out


def _add(dst, src):
    out = dst.copy()
    out[:, :, :3] = np.clip(dst[:, :, :3] + src[:, :, :3] * src[:, :, 3:4],
                            0.0, 1.0)
    return out


def _centroid_x(tile_rgba, floor=0.05):
    a = tile_rgba[:, :, 3]
    mask = a > floor
    if not mask.any():
        return None
    xs = np.nonzero(mask)[1]
    weights = a[mask]
    return float((xs * weights).sum() / weights.sum())


def check(cfg=None):
    """Composite the layers with no offsets, and measure what the picture shows.

    The composite is the check. A wrong axis, a tile at the wrong height or a
    flash off the bore are obvious here and invisible in a return value - all
    three of those happened.
    """
    cfg = dict(CONFIG, **(cfg or {}))
    layers = _atlases(cfg)
    meta = layers["hull"][1]

    # The effect layers are rendered at a wider tile, so the cell is sized to
    # the largest and every layer is placed by its *anchor* rather than by its
    # frame corner. That is the composite the game performs, and it is the only
    # one that stays right when the tiles differ.
    size = max(m["tile"][0] for _, m in layers.values())
    at = np.array([max(m["anchor_px"][i] for _, m in layers.values())
                   for i in range(2)])

    headings, phases = cfg["check_headings"], cfg["check_phases"]
    cols, rows = len(phases), len(headings)
    sheet = np.zeros((rows * size, cols * size, 4), dtype=np.float32)
    sheet[:, :, :3] = 0.16
    sheet[:, :, 3] = 1.0

    def place(buf, name, index, blend):
        src = _tile_of(layers, name, index)
        anchor = np.array(layers[name][1]["anchor_px"])
        # tile pixels are top-down, the arrays are bottom-up: x shifts as it
        # reads, y shifts the other way
        dx = int(round(at[0] - anchor[0]))
        dy = int(round((size - at[1]) - (src.shape[0] - anchor[1])))
        x0, y0 = max(dx, 0), max(dy, 0)
        sx, sy = max(-dx, 0), max(-dy, 0)
        w = min(src.shape[1] - sx, size - x0)
        h = min(src.shape[0] - sy, size - y0)
        if w <= 0 or h <= 0:
            return buf
        patch = buf[y0:y0 + h, x0:x0 + w]
        buf[y0:y0 + h, x0:x0 + w] = blend(patch, src[sy:sy + h, sx:sx + w])
        return buf

    for ri, angle_index in enumerate(headings):
        for ci, phase in enumerate(phases):
            cell = sheet[(rows - ri - 1) * size:(rows - ri) * size,
                         ci * size:(ci + 1) * size]
            buf = cell.copy()
            frame = phase * meta["count"] + angle_index
            for name, index in (("hex", 0), ("hull", angle_index),
                                ("turret", angle_index), ("smoke", frame)):
                buf = place(buf, name, index, _over)
            # additive, because that is how the game draws it. Composited with
            # plain alpha the flash comes out visibly paler and the picture
            # understates it. The smoke above is the opposite - it occludes,
            # so it goes on with normal alpha and *under* the fire.
            buf = place(buf, "flash", frame, _add)
            cell[:] = buf

    path = os.path.join(cfg["output_dir"], "_check_shot.png")
    img = bpy.data.images.new("_check_shot", cols * size, rows * size,
                              alpha=True, float_buffer=False)
    try:
        img.colorspace_settings.name = "Non-Color"
        img.alpha_mode = "CHANNEL_PACKED"
        img.pixels.foreach_set(sheet.reshape(-1))
        img.file_format = "PNG"
        img.filepath_raw = path
        img.save()
    finally:
        bpy.data.images.remove(img)

    report = {"composite": path, "problems": []}

    # nothing may touch the tile edge, on any frame of any tank layer
    edges = {}
    for name in ("hull", "turret", "hex"):
        pix, m = layers[name]
        worst = 0.0
        for frame in m["frames"]:
            t = _tile_of(layers, name, frame["index"])[:, :, 3]
            worst = max(worst, float(max(t[0].max(), t[-1].max(),
                                         t[:, 0].max(), t[:, -1].max())))
        edges[name] = round(worst, 4)
        if worst > 0.0:
            report["problems"].append(
                "%s touches the tile edge (alpha %.3f) - the frame is too "
                "tight and the sprite is being clipped" % (name, worst))
    report["max_edge_alpha"] = edges

    # The flash gets the same test, but it means something different and so is
    # reported separately. The tank layers touching the edge is a framing bug.
    # The flash touching it is the tile budget running out: the muzzle sits a
    # fixed distance from the spin axis, the frame has half a tile, and what is
    # left over is all the flash may have. That distance differs per tank, so
    # a flash sized in bore radii can fit on one gun and be cut on another -
    # this is the one thing tuning on the bench cannot tell you.
    flash_meta = layers["flash"][1]
    worst = 0.0
    for frame in flash_meta["frames"]:
        t = _tile_of(layers, "flash", frame["index"])[:, :, 3]
        worst = max(worst, float(max(t[0].max(), t[-1].max(),
                                     t[:, 0].max(), t[:, -1].max())))
    report["flash_edge_alpha"] = round(worst, 4)
    if worst > 0.02:
        report["problems"].append(
            "the flash reaches the tile edge (alpha %.3f) and is being cut off "
            "- shorten it in muzzle_flash.CONFIG, or give the layer its own "
            "tile size at the same units_per_pixel" % worst)

    # the tank stands on the tile, rather than the tile cutting through it
    hull_a = _tile_of(layers, "hull", 0)[:, :, 3] > 0.5
    hex_a = _tile_of(layers, "hex", 0)[:, :, 3] > 0.5
    hull_rows = np.nonzero(hull_a)[0]
    hex_rows = np.nonzero(hex_a)[0]
    stands = bool(hex_rows.min() <= hull_rows.min() <= hex_rows.max())
    report["hull_rows"] = [int(hull_rows.min()), int(hull_rows.max())]
    report["hex_rows"] = [int(hex_rows.min()), int(hex_rows.max())]
    report["stands_on_tile"] = stands
    if not stands:
        report["problems"].append(
            "the hull's lowest pixel is outside the tile's span, so the tank "
            "is not standing on the tile - check hex_base ground_z")

    # The flash sits on the tank's symmetry line, at the two headings where
    # that line is vertical on screen. This is the cheap independent test for
    # a muzzle point that is off the bore axis: it needs no camera maths, only
    # the fact that a tank pointing at or away from the camera is symmetric.
    step = 360.0 / meta["count"]
    offsets = {}
    for frame in meta["frames"]:
        facing = None
        for part in (frame.get("label") or "").split(" "):
            if part.isdigit():
                facing = int(part)
                break
        if facing is None or facing % 180 != int(cfg["front_dir"]) % 180:
            continue
        flash = _tile_of(layers, "flash", 1 * meta["count"] + frame["index"])
        cx = _centroid_x(flash)
        if cx is None:
            continue
        # against the flash layer's own anchor - it may be a wider tile
        anchor = layers["flash"][1]["anchor_px"]
        offsets[facing] = round(cx - anchor[0], 2)
        # Only the heading that fires at the camera is asserted on. The
        # opposite one is measured too, but the flash there is behind the
        # turret and clipped by the holdout, and the tank is not symmetric
        # about that cut - the aerial stands off to one side and shaves the
        # flash unevenly. It read 1.85px off on MT with the muzzle dead on the
        # bore, which is noise, not error.
        if facing != int(cfg["front_dir"]) % 360:
            continue
        if abs(cx - anchor[0]) > cfg["centre_tolerance_px"]:
            report["problems"].append(
                "at facing %d the flash sits %.1f px off the tank's centre "
                "line - the muzzle point is not on the bore axis"
                % (facing, cx - anchor[0]))
    report["flash_offcentre_px"] = offsets
    report["step_deg"] = step
    report["anchor_px"] = meta["anchor_px"]
    report["tiles"] = {name: m["tile"][0] for name, (_, m) in layers.items()}
    report["units_per_pixel"] = meta["units_per_pixel"]
    return report


def run(cfg=None):
    """prepare, render, check - and say plainly whether it is good."""
    cfg = dict(CONFIG, **(cfg or {}))
    prepared = prepare(cfg)
    rendered = render(cfg)
    checked = check(cfg)

    problems = list(rendered["warnings"]) + list(checked["problems"])
    if not rendered["framing_identical"]:
        problems.append("layers do not share a framing")
    if prepared["muzzle"]["warnings"]:
        problems.extend(prepared["muzzle"]["warnings"])

    report = {
        "muzzle_point": prepared["muzzle"]["muzzle_point"],
        "muzzle_shape": prepared["muzzle"]["muzzle_shape"],
        "bearing_disagreement": prepared["muzzle"]["bearing_disagreement"],
        "ground_z": prepared["hex"]["ground_z"],
        "stands_on_tile": checked["stands_on_tile"],
        "framing_identical": rendered["framing_identical"],
        "spin_pivot": rendered["spin_pivot"],
        "spin_pivot_from": rendered["spin_pivot_from"],
        "anchor_px": checked["anchor_px"],
        "units_per_pixel": checked["units_per_pixel"],
        "max_edge_alpha": checked["max_edge_alpha"],
        "flash_edge_alpha": checked["flash_edge_alpha"],
        "flash_offcentre_px": checked["flash_offcentre_px"],
        "composite": checked["composite"],
        "problems": problems,
    }
    print("[shot] %s  %d problems" % (cfg["output_dir"], len(problems)))
    for line in problems:
        print("[shot]   ! %s" % line)
    print("[shot] now open %s and look at it - the numbers above have all been "
          "green while the picture was wrong" % checked["composite"])
    return report


if __name__ == "__main__":
    from pprint import pprint

    pprint(run(CONFIG))
