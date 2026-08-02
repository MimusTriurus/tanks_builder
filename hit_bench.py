"""
A bench for tuning the impact burst, with the tank in it.

    import hit_bench
    hit_bench.sheet()        # writes out/_hit_bench.png and returns numbers

Unlike `flash_bench`, this one keeps the tank. The muzzle flash's open question
is its shape against a bore, which a stub barrel answers faster than a million
vertices of tank; the burst's open question is whether it reads *on armour* and
*on the field*, and neither exists without them. So this is a small
`render_set` job rather than a rig of its own - the same job the pipeline will
run, minus the layers that are not being looked at.

It composites twice, and the second row is the point
-----------------------------------------------------
Top row: field, tank, dust, burst - what the player sees. Bottom row: the same
without the tank, so the burst is added straight onto the pale field. That is
the case that has broken every additive effect in this project so far: a
low-alpha additive pixel over 0.65 grey is that grey very slightly tinted, i.e.
grey, and it only looks convincing over something dark. Judging on the tank
alone hides it, because armour is darker than the ground.

It also proves the offset table
-------------------------------
The burst is not drawn at the anchor - it is drawn where `hit_point`'s
projection says the plate is. Placing it that way here means the numbers that
the harness will read are exercised now rather than believed now: if the table
is transposed or the sign of screen y is wrong, the burst lands off the tank
and the picture says so immediately.
"""

import json
import math
import os

import bpy
import numpy as np

REPO = r"D:\Projects\AgentCoding\BlenderMCP"

CONFIG = {
    "repo": REPO,
    "output_dir": os.path.join(REPO, "out", "burst"),
    "sheet": "_hit_bench.png",
    "hull": "Hull",
    "turret": "Turret",
    "root": "world",
    "hex": "Hex",
    "tile": 256,
    # The burst needs far less frame than the tank; it is placed by offset, so
    # its frame only has to hold the burst itself.
    "burst_tile_scale": 0.75,
    "phases": 10,
    "steps": 12,
    "samples": 32,
    # which plate and which heading the sheet puts the burst on
    "face": "front",
    "heading": 0,
    # the field, if there is no Hex in the scene to measure
    "ground": (0.651, 0.675, 0.627),
    # a burst body is anything above this; a burst *core* anything above the
    # second, which is what the colour is judged on
    "body_alpha": 0.05,
    "core_alpha": 0.25,
}


def _sibling(name):
    import importlib.util
    here = (os.path.dirname(os.path.abspath(__file__))
            if "__file__" in globals() else REPO)
    spec = importlib.util.spec_from_file_location(
        name, os.path.join(here, "%s.py" % name))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def _tile_of(pix, meta, index):
    size = meta["tile"][0]
    cols, rows = meta["grid"]["columns"], meta["grid"]["rows"]
    col, row = index % cols, index // cols
    y0 = (rows - row - 1) * size          # image data is bottom-up
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


def _span(pix, meta, floor):
    """Bounding box of everything above `floor`, over every frame, in px."""
    w = h = 0
    for frame in meta["frames"]:
        a = _tile_of(pix, meta, frame["index"])[:, :, 3]
        ys, xs = np.nonzero(a > floor)
        if not len(xs):
            continue
        w = max(w, int(xs.max() - xs.min() + 1))
        h = max(h, int(ys.max() - ys.min() + 1))
    return [w, h]


def _edge_alpha(pix, meta):
    worst = 0.0
    for frame in meta["frames"]:
        t = _tile_of(pix, meta, frame["index"])[:, :, 3]
        worst = max(worst, float(max(t[0].max(), t[-1].max(),
                                     t[:, 0].max(), t[:, -1].max())))
    return worst


def render(cfg=None):
    """Build the pair and render it with the tank, in one job."""
    cfg = dict(CONFIG, **(cfg or {}))
    sa = _sibling("sprite_atlas")
    burst = _sibling("hit_burst")
    scene = bpy.context.scene

    burst.build()
    burst.build_dust()
    os.makedirs(cfg["output_dir"], exist_ok=True)

    effects = ["Flash", "Smoke", "Plume", "Fire", "Burn",
               burst.CONFIG["name"], burst.DUST["name"]]
    layers = [{"name": "tank", "target": cfg["root"], "exclude": effects}]
    if cfg["hex"] in scene.objects:
        layers.append({"name": "hex", "target": cfg["hex"], "static": True})
    for name, hook in (("burst", burst.phase_hook()),
                       ("dust", burst.dust_phase_hook())):
        layers.append({"name": name, "target": burst.CONFIG["name"]
                       if name == "burst" else burst.DUST["name"],
                       "static": True, "fit": False, "phases": cfg["phases"],
                       "tile_scale": cfg["burst_tile_scale"],
                       "phase_hook": hook})
    job = {
        "shared": {"output_dir": cfg["output_dir"], "tile": cfg["tile"],
                   "steps": cfg["steps"], "samples": cfg["samples"],
                   "keep_frames": False, "spin_pivot_from": [cfg["turret"]]},
        "layers": layers,
    }
    return sa.render_set(job)


def sheet(cfg=None, rendered=None):
    """Composite the phases twice and measure them."""
    cfg = dict(CONFIG, **(cfg or {}))
    sa = _sibling("sprite_atlas")
    hp = _sibling("hit_point")
    burst = _sibling("hit_burst")
    out = rendered if rendered is not None else render(cfg)

    layers = {}
    for name in ("tank", "hex", "burst", "dust"):
        base = os.path.join(cfg["output_dir"], "%s_atlas" % name)
        if not os.path.exists(base + ".json"):
            continue
        with open(base + ".json", encoding="utf-8") as fh:
            layers[name] = (sa.read_rgba(base + ".png"), json.load(fh))

    tank_pix, tank_meta = layers["tank"]
    upp = tank_meta["units_per_pixel"]
    angles = [f["angle"] for f in tank_meta["frames"][:tank_meta["count"]]]
    origin = burst.origin_of(dict(burst.CONFIG))
    table = hp.project_plates(origin, upp, angles)

    if "hex" in layers:
        hx, hm = layers["hex"]
        tile = _tile_of(hx, hm, 0)
        solid = tile[:, :, 3] > 0.9
        ground = (np.median(tile[solid][:, :3], axis=0).astype(np.float32)
                  if solid.any() else np.float32(cfg["ground"]))
    else:
        ground = np.asarray(cfg["ground"], dtype=np.float32)

    size = tank_meta["tile"][0]
    at = np.array(tank_meta["anchor_px"], dtype=np.float64)
    row = table[cfg["face"]][cfg["heading"]]
    shift = np.array(row["offset"], dtype=np.float64)

    def place(buf, name, index, blend, offset=(0.0, 0.0)):
        pix, meta = layers[name]
        src = _tile_of(pix, meta, index)
        anchor = np.array(meta["anchor_px"], dtype=np.float64)
        dx = int(round(at[0] - anchor[0] + offset[0]))
        # tile pixels are top-down and the arrays bottom-up, so screen y and
        # array y move opposite ways - the same flip `tank_pipeline._sheet` makes
        dy = int(round((size - at[1]) - (src.shape[0] - anchor[1]) - offset[1]))
        x0, y0 = max(dx, 0), max(dy, 0)
        sx, sy = max(-dx, 0), max(-dy, 0)
        w = min(src.shape[1] - sx, size - x0)
        h = min(src.shape[0] - sy, size - y0)
        if w <= 0 or h <= 0:
            return buf
        patch = buf[y0:y0 + h, x0:x0 + w]
        buf[y0:y0 + h, x0:x0 + w] = blend(patch, src[sy:sy + h, sx:sx + w])
        return buf

    phases = layers["burst"][1]["phases"]
    sheet_px = np.zeros((2 * size, phases * size, 4), dtype=np.float32)
    sheet_px[:, :, :3] = ground
    sheet_px[:, :, 3] = 1.0
    plain = []
    for p in range(phases):
        for r in (0, 1):
            cell = sheet_px[(1 - r) * size:(2 - r) * size, p * size:(p + 1) * size]
            buf = cell.copy()
            if r == 0:
                buf = place(buf, "tank", cfg["heading"], _over)
            buf = place(buf, "dust", p, _over, shift)
            buf = place(buf, "burst", p, _add, shift)
            cell[:] = buf
            if r == 1:
                plain.append(buf)

    path = os.path.join(cfg["output_dir"], cfg["sheet"])
    img = bpy.data.images.new("hit_bench", phases * size, 2 * size,
                              alpha=True, float_buffer=False)
    try:
        img.colorspace_settings.name = "Non-Color"
        img.alpha_mode = "CHANNEL_PACKED"
        img.pixels.foreach_set(sheet_px.reshape(-1))
        img.file_format = "PNG"
        img.filepath_raw = path
        img.save()
    finally:
        bpy.data.images.remove(img)

    # --- measurements ------------------------------------------------------
    bp, bm = layers["burst"]
    dp, dm = layers["dust"]
    ground_red = float(ground[0] - 0.5 * (ground[1] + ground[2]))

    white, redness, contrast = 0.0, [], 0.0
    report_rgb = core_rgb = None
    for p in range(phases):
        b = _tile_of(bp, bm, p)
        d = _tile_of(dp, dm, p)
        # the burst alone on the field, at its own tile size
        field = np.zeros_like(b)
        field[:, :, :3] = ground
        field[:, :, 3] = 1.0
        lit = _add(_over(field, d), b)
        body = b[:, :, 3] > cfg["core_alpha"]
        if body.any():
            rgb = lit[:, :, :3][body]
            score = float(np.median(rgb[:, 0] - 0.5 * (rgb[:, 1] + rgb[:, 2]))
                          - ground_red)
            if not redness or score > max(redness):
                # the colour itself, at the phase that scores best - a redness
                # number alone does not say whether the failure is pale or dim.
                # Body and core separately: the body's median mixes the halo
                # with the hot part, and a tan body over a red core is a hole in
                # the dust rather than a wrong ramp.
                report_rgb = [round(float(v), 3) for v in np.median(rgb, axis=0)]
                hot = b[:, :, 3] > max(cfg["core_alpha"],
                                       float(np.percentile(b[:, :, 3][body], 90)))
                if hot.any():
                    core_rgb = [round(float(v), 3)
                                for v in np.median(lit[:, :, :3][hot], axis=0)]
            redness.append(score)
            white = max(white, float(np.mean(rgb.min(axis=1) > 0.97)))
        # what the dust alone does to the field, in levels of 255
        smudge = _over(field, d)
        contrast = max(contrast, float(np.abs(smudge[:, :, :3] - ground).max() * 255.0))

    report = {
        "sheet": path,
        "framing_identical": out["framing_identical"],
        "units_per_pixel": upp,
        "burst_tile": bm["tile"][0],
        "burst_span_px": _span(bp, bm, cfg["body_alpha"]),
        "dust_span_px": _span(dp, dm, cfg["body_alpha"]),
        "burst_edge_alpha": round(_edge_alpha(bp, bm), 4),
        "dust_edge_alpha": round(_edge_alpha(dp, dm), 4),
        "white_fraction": round(white, 5),
        "redness": round(max(redness) if redness else 0.0, 4),
        "redness_levels": round(255.0 * (max(redness) if redness else 0.0), 1),
        "burst_rgb": report_rgb,
        "core_rgb": core_rgb,
        "dust_contrast": round(contrast, 1),
        "ground": [round(float(v), 3) for v in ground],
        "offset_px": row["offset"],
        "facing": row["facing"],
        "warnings": out["warnings"],
    }
    print("[bench] burst %s px, edge %.3f, white %.4f, red %.1f levels, "
          "dust %.1f levels" % (report["burst_span_px"],
                                report["burst_edge_alpha"],
                                report["white_fraction"],
                                report["redness_levels"],
                                report["dust_contrast"]))
    return report


if __name__ == "__main__":
    from pprint import pprint

    pprint(sheet())
